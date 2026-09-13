using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SimpleShot
{
    /// <summary>
    /// Captures the system's audio output (WASAPI shared-mode loopback) and exposes it as a
    /// stream of little-endian 16-bit PCM. Silence gaps are padded against a wall clock so the
    /// audio length tracks the video length. Pure COM interop - no external libraries.
    /// If anything fails to initialise, <see cref="Start"/> returns false and the caller records
    /// video only.
    /// </summary>
    internal sealed class AudioCapture : IDisposable
    {
        // ---- output (16-bit PCM) format, filled after a successful Start() ----
        public int Channels { get; private set; }
        public int SampleRate { get; private set; }
        public int BitsPerSample { get { return 16; } }
        public int BlockAlign { get { return Channels * 2; } }
        public int AvgBytesPerSec { get { return SampleRate * BlockAlign; } }

        private IAudioClient _client;
        private IAudioCaptureClient _capture;
        private Thread _thread;
        private volatile bool _running;

        private readonly List<byte[]> _pending = new List<byte[]>();
        private readonly object _lock = new object();

        private int _srcBits;        // source mix-format bits per sample (float when 32)
        private bool _srcFloat;

        public bool Start()
        {
            try
            {
                var enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                var devEnum = (IMMDeviceEnumerator)Activator.CreateInstance(enumType);

                IMMDevice dev;
                if (devEnum.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out dev) < 0 || dev == null)
                    return false;

                Guid iidClient = IID_IAudioClient;
                object clientObj;
                if (dev.Activate(ref iidClient, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out clientObj) < 0)
                    return false;
                _client = (IAudioClient)clientObj;

                IntPtr pFmt;
                if (_client.GetMixFormat(out pFmt) < 0 || pFmt == IntPtr.Zero)
                    return false;

                short fmtTag = Marshal.ReadInt16(pFmt, 0);
                Channels = Marshal.ReadInt16(pFmt, 2);
                SampleRate = Marshal.ReadInt32(pFmt, 4);
                _srcBits = Marshal.ReadInt16(pFmt, 14);
                // 共享模式混音格式几乎都是 32 位 IEEE float（或 EXTENSIBLE 包装的 float）。
                // 注意：不能仅凭 _srcBits == 32 判定为 float，否则 32 位整型 PCM 会被当作
                // 浮点解析而产生爆音；整型 32 位走下面 Convert 的“取高 16 位”分支。
                _srcFloat = fmtTag == 3 || (fmtTag == unchecked((short)0xFFFE) && _srcBits == 32);
                if (Channels <= 0 || Channels > 8 || SampleRate <= 0) { Marshal.FreeCoTaskMem(pFmt); return false; }

                const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
                Guid session = Guid.Empty;
                int hr = _client.Initialize(0 /*shared*/, AUDCLNT_STREAMFLAGS_LOOPBACK,
                    10000000 /*1s buffer, 100ns units*/, 0, pFmt, ref session);
                Marshal.FreeCoTaskMem(pFmt);
                if (hr < 0) return false;

                Guid iidCap = IID_IAudioCaptureClient;
                object capObj;
                if (_client.GetService(ref iidCap, out capObj) < 0) return false;
                _capture = (IAudioCaptureClient)capObj;

                if (_client.Start() < 0) return false;

                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
                _thread.Start();
                return true;
            }
            catch { return false; }
        }

        private void Loop()
        {
            int srcBytesPerFrame = Channels * (_srcBits / 8);
            long startTicks = Stopwatch.GetTimestamp();
            long emittedFrames = 0;

            while (_running)
            {
                try
                {
                    uint packet;
                    if (_capture.GetNextPacketSize(out packet) >= 0)
                    {
                        while (packet > 0 && _running)
                        {
                            IntPtr pData; uint frames, flags; long p1, p2;
                            if (_capture.GetBuffer(out pData, out frames, out flags, out p1, out p2) < 0) break;

                            byte[] pcm = new byte[frames * BlockAlign];
                            if ((flags & 0x2) == 0 && pData != IntPtr.Zero) // not AUDCLNT_BUFFERFLAGS_SILENT
                                Convert(pData, frames, pcm);
                            Enqueue(pcm);
                            emittedFrames += frames;

                            _capture.ReleaseBuffer(frames);
                            if (_capture.GetNextPacketSize(out packet) < 0) break;
                        }
                    }

                    // pad silence so the audio timeline keeps pace with real time
                    long elapsed = Stopwatch.GetTimestamp() - startTicks;
                    long shouldFrames = elapsed * SampleRate / Stopwatch.Frequency;
                    long deficit = shouldFrames - emittedFrames;
                    if (deficit > SampleRate / 25) // >40ms behind
                    {
                        if (deficit > SampleRate) deficit = SampleRate; // cap a single pad to 1s
                        Enqueue(new byte[deficit * BlockAlign]);
                        emittedFrames += deficit;
                    }
                }
                catch { }
                Thread.Sleep(8);
            }
        }

        private void Convert(IntPtr src, uint frames, byte[] dst)
        {
            int samples = (int)frames * Channels;
            int di = 0;
            if (_srcFloat)
            {
                for (int i = 0; i < samples; i++)
                {
                    float f = ReadFloat(src, i * 4);
                    if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                    short s = (short)(f * 32767f);
                    dst[di++] = (byte)(s & 0xFF);
                    dst[di++] = (byte)((s >> 8) & 0xFF);
                }
            }
            else if (_srcBits == 16)
            {
                Marshal.Copy(src, dst, 0, samples * 2);
            }
            else // 32-bit integer PCM -> take high 16 bits
            {
                for (int i = 0; i < samples; i++)
                {
                    int v = Marshal.ReadInt32(src, i * 4);
                    short s = (short)(v >> 16);
                    dst[di++] = (byte)(s & 0xFF);
                    dst[di++] = (byte)((s >> 8) & 0xFF);
                }
            }
        }

        private static unsafe float ReadFloat(IntPtr p, int off)
        {
            byte* b = (byte*)p + off;
            int bits = b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
            return *(float*)&bits;
        }

        private void Enqueue(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            lock (_lock) _pending.Add(data);
        }

        /// <summary>Returns all PCM bytes accumulated since the last call (or null if none).</summary>
        public byte[] Drain()
        {
            List<byte[]> take;
            lock (_lock)
            {
                if (_pending.Count == 0) return null;
                take = new List<byte[]>(_pending);
                _pending.Clear();
            }
            int total = 0;
            foreach (var b in take) total += b.Length;
            byte[] outBuf = new byte[total];
            int off = 0;
            foreach (var b in take) { Buffer.BlockCopy(b, 0, outBuf, off, b.Length); off += b.Length; }
            return outBuf;
        }

        public void Dispose()
        {
            _running = false;
            try { if (_thread != null) _thread.Join(1500); } catch { }
            try { if (_client != null) _client.Stop(); } catch { }
            if (_capture != null) { try { Marshal.ReleaseComObject(_capture); } catch { } _capture = null; }
            if (_client != null) { try { Marshal.ReleaseComObject(_client); } catch { } _client = null; }
        }

        // ---------- COM interop ----------
        private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate([In] ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr props);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration,
                long periodicity, IntPtr format, ref Guid audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint bufferFrames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService([In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out uint flags,
                out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint numFrames);
            [PreserveSig] int GetNextPacketSize(out uint numFrames);
        }
    }
}
