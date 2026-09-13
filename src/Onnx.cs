using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SimpleShot
{
    /// <summary>ONNX 推理异常。</summary>
    internal sealed class OnnxException : Exception
    {
        public OnnxException(string m) : base(m) { }
    }

    /// <summary>推理结果张量（展平为一维浮点数组 + 形状）。</summary>
    internal sealed class OnnxTensor
    {
        public float[] Data;
        public long[] Shape;
    }

    /// <summary>
    /// ONNX 推理封装（零 NuGet 依赖）。
    /// onnxruntime.dll 的 C-API 只按名导出 OrtGetApiBase，其余函数统一经它返回的
    /// OrtApi 结构体（一串函数指针）按索引调用。索引对应 ORT_API_VERSION=18（onnxruntime 1.18.x），
    /// 由 onnxruntime_c_api.h 中 struct OrtApi 的成员顺序推导并核对。
    /// 原生运行时放在 plugins\onnxruntime.dll 按需加载；模型（ocr / sam）作为插件放在 plugins 下。
    /// </summary>
    internal static class Onnx
    {
        public static readonly string PluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
        public static readonly string DllPath = Path.Combine(PluginsDir, "onnxruntime.dll");

        // ---- OrtApi 函数索引（1.18.x / ORT_API_VERSION=18）----
        private const int F_GetErrorMessage = 2;          // const char* GetErrorMessage(OrtStatus*)
        private const int F_CreateEnv = 3;                // CreateEnv(log_level, logid, &env)
        private const int F_CreateSession = 7;            // CreateSession(env, wchar_path, options, &sess)
        private const int F_Run = 9;
        private const int F_CreateSessionOptions = 10;
        private const int F_SetSessionGraphOptimizationLevel = 23;
        private const int F_SetIntraOpNumThreads = 24;
        private const int F_SessionGetInputCount = 30;
        private const int F_SessionGetOutputCount = 31;
        private const int F_SessionGetInputName = 36;
        private const int F_SessionGetOutputName = 37;
        private const int F_CreateTensorWithDataAsOrtValue = 49;
        private const int F_GetTensorMutableData = 51;
        private const int F_GetDimensionsCount = 61;
        private const int F_GetDimensions = 62;
        private const int F_GetTensorTypeAndShape = 65;
        private const int F_CreateCpuMemoryInfo = 69;
        private const int F_AllocatorFree = 76;           // void AllocatorFree(allocator, ptr)
        private const int F_GetAllocatorWithDefaultOptions = 78; // OrtAllocator* ()
        private const int F_ReleaseEnv = 92;
        private const int F_ReleaseStatus = 93;
        private const int F_ReleaseMemoryInfo = 94;
        private const int F_ReleaseSession = 95;
        private const int F_ReleaseValue = 96;
        private const int F_ReleaseTensorTypeAndShapeInfo = 99;
        private const int F_ReleaseSessionOptions = 100;

        private const int ORT_API_VERSION = 18;

        private static IntPtr _ortApi;
        private static IntPtr _env;
        private static IntPtr _allocator;
        private static IntPtr _memInfo;
        private static bool _init;
        private static bool _initFailed;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("onnxruntime.dll")]
        private static extern IntPtr OrtGetApiBase();

        // ---- 通用委托（ORT_API_CALL = __stdcall / Winapi）----
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnApiBaseGetApi(uint version);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnCreateEnv(int level, [MarshalAs(UnmanagedType.LPStr)] string logid, out IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnCreateSession(IntPtr env, [MarshalAs(UnmanagedType.LPWStr)] string modelPath, IntPtr options, out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnCreateSessionOptions(out IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnSetOptLevel(IntPtr options, int level);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnSetThreads(IntPtr options, int threads);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnGetCount(IntPtr session, out IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnGetName(IntPtr session, IntPtr index, IntPtr allocator, out IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnCreateCpuMemInfo(int allocType, int memType, out IntPtr memInfo);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnGetAllocator(out IntPtr allocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnCreateTensor(IntPtr memInfo, IntPtr data, IntPtr len, long[] shape, IntPtr shapeLen, int elemType, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnGetMutableData(IntPtr value, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnGetTypeShape(IntPtr value, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnGetDimsCount(IntPtr info, out IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnGetDims(IntPtr info, long[] dims, IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnRun(IntPtr session, IntPtr runOptions, IntPtr[] inNames, IntPtr[] inVals, IntPtr inCount, IntPtr[] outNames, IntPtr outCount, IntPtr[] outputs);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DFnRelease(IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DFnAllocFree(IntPtr allocator, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DFnGetErrMsg(IntPtr status);

        // ---- 基础初始化 ----

        /// <summary>原生运行时是否存在。</summary>
        public static bool Available
        {
            get { try { return File.Exists(DllPath); } catch { return false; } }
        }

        /// <summary>插件模型路径：plugins/{folder}/{file}。</summary>
        public static string ModelPath(string folder, string file)
        {
            return Path.Combine(PluginsDir, folder, file);
        }

        private static IntPtr Fn(int index)
        {
            if (_ortApi == IntPtr.Zero) throw new OnnxException("onnxruntime 未初始化");
            IntPtr p = Marshal.ReadIntPtr(_ortApi, index * IntPtr.Size);
            if (p == IntPtr.Zero) throw new OnnxException("onnxruntime API 函数不可用：" + index);
            return p;
        }

        private static void Check(IntPtr st)
        {
            if (st == IntPtr.Zero) return;
            IntPtr msg = ((DFnGetErrMsg)Marshal.GetDelegateForFunctionPointer(Fn(F_GetErrorMessage), typeof(DFnGetErrMsg)))(st);
            string s = Marshal.PtrToStringAnsi(msg);
            ((DFnRelease)Marshal.GetDelegateForFunctionPointer(Fn(F_ReleaseStatus), typeof(DFnRelease)))(st);
            throw new OnnxException("onnxruntime: " + (s == null ? "未知错误" : s));
        }

        private static void Ensure()
        {
            if (_init) return;
            if (_initFailed) throw new OnnxException("onnxruntime.dll 初始化失败");
            try
            {
                SetDllDirectory(PluginsDir);
                if (!File.Exists(DllPath)) throw new OnnxException("缺少运行时：plugins\\onnxruntime.dll");

                IntPtr apiBase = OrtGetApiBase();
                if (apiBase == IntPtr.Zero) throw new OnnxException("OrtGetApiBase 返回空");
                IntPtr getApiPtr = Marshal.ReadIntPtr(apiBase, 0);
                var getApi = (DFnApiBaseGetApi)Marshal.GetDelegateForFunctionPointer(getApiPtr, typeof(DFnApiBaseGetApi));
                _ortApi = getApi(ORT_API_VERSION);
                if (_ortApi == IntPtr.Zero) throw new OnnxException("GetApi(" + ORT_API_VERSION + ") 返回空");

                // 创建共享环境
                var createEnv = (DFnCreateEnv)Marshal.GetDelegateForFunctionPointer(Fn(F_CreateEnv), typeof(DFnCreateEnv));
                Check(createEnv(3, "SimpleShot", out _env));

                var getAl = (DFnGetAllocator)Marshal.GetDelegateForFunctionPointer(Fn(F_GetAllocatorWithDefaultOptions), typeof(DFnGetAllocator));
                Check(getAl(out _allocator));
                var createMem = (DFnCreateCpuMemInfo)Marshal.GetDelegateForFunctionPointer(Fn(F_CreateCpuMemoryInfo), typeof(DFnCreateCpuMemInfo));
                Check(createMem(1 /*arena*/, 0 /*default*/, out _memInfo));
            }
            catch (OnnxException) { _initFailed = true; throw; }
            catch (Exception ex) { _initFailed = true; throw new OnnxException("onnxruntime 初始化失败：" + ex.Message); }
            _init = true;
        }

        // ---- 会话 ----

        public sealed class Session : IDisposable
        {
            private IntPtr _sess;
            private string[] _inNames;
            private string[] _outNames;

            public Session(string path, int inputCount)
            {
                Ensure();
                IntPtr opts;
                Check(((DFnCreateSessionOptions)Marshal.GetDelegateForFunctionPointer(Fn(F_CreateSessionOptions), typeof(DFnCreateSessionOptions)))(out opts));
                try
                {
                    Check(((DFnSetOptLevel)Marshal.GetDelegateForFunctionPointer(Fn(F_SetSessionGraphOptimizationLevel), typeof(DFnSetOptLevel)))(opts, 99));
                    Check(((DFnSetThreads)Marshal.GetDelegateForFunctionPointer(Fn(F_SetIntraOpNumThreads), typeof(DFnSetThreads)))(opts, Math.Max(1, Environment.ProcessorCount - 1)));
                    Check(((DFnCreateSession)Marshal.GetDelegateForFunctionPointer(Fn(F_CreateSession), typeof(DFnCreateSession)))(_env, path, opts, out _sess));
                }
                finally
                {
                    ((DFnRelease)Marshal.GetDelegateForFunctionPointer(Fn(F_ReleaseSessionOptions), typeof(DFnRelease)))(opts);
                }
                _inNames = ReadNames(_sess, true);
                _outNames = ReadNames(_sess, false);
            }

            /// <summary>模型输入名称（顺序与模型定义一致）。</summary>
            public string[] InputNames { get { return _inNames; } }
            /// <summary>模型输出名称。</summary>
            public string[] OutputNames { get { return _outNames; } }

            /// <summary>在一段展平数据上求 argmax（取最大元素索引）。</summary>
            public static int ArgMax(OnnxTensor t, int offset, int count)
            {
                float best = t.Data[offset];
                int bi = 0;
                for (int i = 1; i < count; i++)
                {
                    float v = t.Data[offset + i];
                    if (v > best) { best = v; bi = i; }
                }
                return bi;
            }

            private static string[] ReadNames(IntPtr sess, bool input)
            {
                int idxCount = input ? F_SessionGetInputCount : F_SessionGetOutputCount;
                int idxName = input ? F_SessionGetInputName : F_SessionGetOutputName;
                IntPtr n;
                Check(((DFnGetCount)Marshal.GetDelegateForFunctionPointer(Fn(idxCount), typeof(DFnGetCount)))(sess, out n));
                int count = n.ToInt32();
                string[] names = new string[count];
                var getName = (DFnGetName)Marshal.GetDelegateForFunctionPointer(Fn(idxName), typeof(DFnGetName));
                var free = (DFnAllocFree)Marshal.GetDelegateForFunctionPointer(Fn(F_AllocatorFree), typeof(DFnAllocFree));
                for (int i = 0; i < count; i++)
                {
                    IntPtr p;
                    Check(getName(sess, (IntPtr)i, _allocator, out p));
                    names[i] = Marshal.PtrToStringAnsi(p);
                    free(_allocator, p);
                }
                return names;
            }

            /// <summary>单输入单输出推理（默认取第 0 个输入/输出）。</summary>
            public OnnxTensor Run(float[] input, long[] shape)
            {
                if (_inNames.Length < 1) throw new OnnxException("模型无输入");
                string[] outNames = _outNames;
                string[] outSel = outNames.Length == 0 ? new string[] { null } : outNames;
                OnnxTensor[] res = RunMulti(new string[] { _inNames[0] },
                    new Array[] { input }, new long[][] { shape }, outSel);
                return res[0];
            }

            /// <summary>多输入推理（float / int64 混合），返回每个输出名对应的张量。</summary>
            public OnnxTensor[] RunMulti(string[] names, Array[] data, long[][] shapes, string[] outNames)
            {
                Ensure();
                int nIn = names.Length;
                int nOut = outNames == null || outNames.Length == 0 ? 1 : outNames.Length;
                if (outNames == null || outNames.Length == 0) outNames = new string[] { null };

                IntPtr[] inVals = new IntPtr[nIn];
                GCHandle[] pins = new GCHandle[nIn];
                IntPtr[] inNameP = new IntPtr[nIn];
                IntPtr[] outNameP = new IntPtr[nOut];
                IntPtr[] outputs = new IntPtr[nOut];
                try
                {
                    for (int i = 0; i < nIn; i++)
                    {
                        int elem = data[i] is long[] ? 7 : 1;
                        long total = 1;
                        foreach (long d in shapes[i]) total *= d;
                        long bytes = total * (elem == 7 ? 8L : 4L);
                        pins[i] = GCHandle.Alloc(data[i], GCHandleType.Pinned);
                        Check(((DFnCreateTensor)Marshal.GetDelegateForFunctionPointer(Fn(F_CreateTensorWithDataAsOrtValue), typeof(DFnCreateTensor)))(
                            _memInfo, pins[i].AddrOfPinnedObject(), (IntPtr)bytes, shapes[i], (IntPtr)shapes[i].Length, elem, out inVals[i]));
                        inNameP[i] = Marshal.StringToHGlobalAnsi(names[i] ?? "");
                    }
                    for (int i = 0; i < nOut; i++) outNameP[i] = Marshal.StringToHGlobalAnsi(outNames[i] == null ? "" : outNames[i]);

                    Check(((DFnRun)Marshal.GetDelegateForFunctionPointer(Fn(F_Run), typeof(DFnRun)))(
                        _sess, IntPtr.Zero, inNameP, inVals, (IntPtr)nIn, outNameP, (IntPtr)nOut, outputs));
                }
                finally
                {
                    for (int i = 0; i < nIn; i++)
                    {
                        if (pins[i].IsAllocated) pins[i].Free();
                        if (inVals[i] != IntPtr.Zero)
                            ((DFnRelease)Marshal.GetDelegateForFunctionPointer(Fn(F_ReleaseValue), typeof(DFnRelease)))(inVals[i]);
                    }
                    for (int i = 0; i < nIn; i++) if (inNameP[i] != IntPtr.Zero) Marshal.FreeHGlobal(inNameP[i]);
                    for (int i = 0; i < nOut; i++) if (outNameP[i] != IntPtr.Zero) Marshal.FreeHGlobal(outNameP[i]);
                }

                OnnxTensor[] res = new OnnxTensor[nOut];
                try
                {
                    for (int i = 0; i < nOut; i++)
                        if (outputs[i] != IntPtr.Zero) res[i] = ReadTensor(outputs[i]);
                }
                finally
                {
                    for (int i = 0; i < nOut; i++)
                        if (outputs[i] != IntPtr.Zero)
                            ((DFnRelease)Marshal.GetDelegateForFunctionPointer(Fn(F_ReleaseValue), typeof(DFnRelease)))(outputs[i]);
                }
                return res;
            }

            private static OnnxTensor ReadTensor(IntPtr ortValue)
            {
                IntPtr info;
                Check(((DFnGetTypeShape)Marshal.GetDelegateForFunctionPointer(Fn(F_GetTensorTypeAndShape), typeof(DFnGetTypeShape)))(ortValue, out info));
                try
                {
                    IntPtr nd;
                    Check(((DFnGetDimsCount)Marshal.GetDelegateForFunctionPointer(Fn(F_GetDimensionsCount), typeof(DFnGetDimsCount)))(info, out nd));
                    int rank = nd.ToInt32();
                    long[] dims = new long[rank];
                    if (rank > 0)
                        Check(((DFnGetDims)Marshal.GetDelegateForFunctionPointer(Fn(F_GetDimensions), typeof(DFnGetDims)))(info, dims, (IntPtr)rank));
                    long total = 1;
                    for (int i = 0; i < rank; i++) total *= dims[i];
                    IntPtr dataPtr;
                    Check(((DFnGetMutableData)Marshal.GetDelegateForFunctionPointer(Fn(F_GetTensorMutableData), typeof(DFnGetMutableData)))(ortValue, out dataPtr));
                    float[] data = new float[total];
                    if (total > 0) Marshal.Copy(dataPtr, data, 0, (int)total);
                    return new OnnxTensor { Data = data, Shape = dims };
                }
                finally
                {
                    ((DFnRelease)Marshal.GetDelegateForFunctionPointer(Fn(F_ReleaseTensorTypeAndShapeInfo), typeof(DFnRelease)))(info);
                }
            }

            public void Dispose()
            {
                if (_sess != IntPtr.Zero)
                {
                    ((DFnRelease)Marshal.GetDelegateForFunctionPointer(Fn(F_ReleaseSession), typeof(DFnRelease)))(_sess);
                    _sess = IntPtr.Zero;
                }
            }
        }

        /// <summary>诊断：初始化运行时（不创建模型会话），成功返回 null，失败返回错误信息。</summary>
        public static string TestInit()
        {
            try
            {
                Ensure();
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
