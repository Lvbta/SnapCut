using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;

namespace SimpleShot
{
    /// <summary>
    /// 文字识别入口。两级策略，全部免费：
    ///   1. 优先调用本机 Umi-OCR 的 HTTP 服务（免费开源，PaddleOCR 引擎，
    ///      中文识别效果第一梯队）——检测到服务在运行即自动使用；
    ///   2. 回退到系统内置 Windows.Media.Ocr（离线、零依赖，需安装语言包）。
    /// 编译期定义 OCR 符号（build.ps1 在 Win10/11 自动检测）时才包含第 2 级。
    /// </summary>
    internal static class Ocr
    {
        public static string Recognize(Bitmap bmp)
        {
            string engine;
            return Recognize(bmp, out engine);
        }

        /// <summary>识别文字并返回所用的引擎名称（用于结果界面展示）。</summary>
        public static string Recognize(Bitmap bmp, out string engine)
        {
            // 一级：内置 ONNX 引擎（models 放在 plugins\ocr，用户无需安装任何东西）
            try
            {
                if (OnnxOcr.Available)
                {
                    var eng = OnnxOcr.Get();
                    if (eng != null)
                    {
                        string t = eng.Recognize(bmp);
                        if (!string.IsNullOrEmpty(t))
                        {
                            engine = "内置 PP-OCR 引擎";
                            return t;
                        }
                    }
                }
            }
            catch { }   // 模型/输入异常时静默降级到下一级

            // 二级：本机 Umi-OCR 服务（可选增强）
            string umi = TryUmiOcr(bmp);
            if (umi != null)
            {
                engine = "Umi-OCR 本地服务";
                return umi;
            }
#if OCR
            engine = "Windows 系统 OCR";
            return WinRtRecognize(bmp);
#else
            engine = "无可用引擎";
            return "未找到可用的 OCR 引擎。\r\n\r\n" +
                   "推荐：把 PP-OCR 模型放到 plugins\\ocr\\ 目录即可使用内置识别引擎，\r\n" +
                   "详见 plugins\\MODEL_GUIDE.md。\r\n" +
                   "或改用 build.ps1 重新构建以启用系统内置离线 OCR。\r\n";
#endif
        }

        // ---------- 一级：Umi-OCR 本地 HTTP 服务 ----------

        /// <summary>调用 Umi-OCR（http://127.0.0.1:1224/api/ocr）；服务未运行或失败时返回 null。</summary>
        private static string TryUmiOcr(Bitmap bmp)
        {
            try
            {
                string b64;
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    b64 = Convert.ToBase64String(ms.ToArray());
                }
                byte[] body = Encoding.UTF8.GetBytes("{\"base64\":\"" + b64 + "\"}");

                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                    "http://127.0.0.1:1224/api/ocr");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                using (var rs = req.GetRequestStream())
                    rs.Write(body, 0, body.Length);
                using (var resp = req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var sr = new StreamReader(rs, Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    return ExtractTexts(json);
                }
            }
            catch { return null; }   // 服务不存在 / 超时：静默回退
        }

        /// <summary>
        /// 无 JSON 库的轻量解析：扫描响应中所有 "text":"..." 字段并按行拼接。
        /// Umi-OCR 成功响应固定包含 "code":100；解析结果为空视为失败（回退二级）。
        /// </summary>
        private static string ExtractTexts(string json)
        {
            if (string.IsNullOrEmpty(json) || !json.Contains("\"code\":100")) return null;
            var sb = new StringBuilder();
            int i = 0;
            while ((i = json.IndexOf("\"text\":\"", i, StringComparison.Ordinal)) >= 0)
            {
                i += 8;
                var line = new StringBuilder();
                while (i < json.Length)
                {
                    char c = json[i];
                    if (c == '\\' && i + 1 < json.Length)
                    {
                        char n = json[i + 1];
                        if (n == '"' || n == '\\') { line.Append(n); i += 2; continue; }
                        if (n == 'n') { line.Append('\n'); i += 2; continue; }
                        if (n == 'u' && i + 5 < json.Length)
                        {
                            line.Append((char)Convert.ToInt32(json.Substring(i + 2, 4), 16));
                            i += 6;
                            continue;
                        }
                        line.Append(n);
                        i += 2;
                        continue;
                    }
                    if (c == '"') break;
                    line.Append(c);
                    i++;
                }
                sb.AppendLine(line.ToString());
                i++;
            }
            string r = sb.ToString().TrimEnd();
            return r.Length > 0 ? r : null;
        }

#if OCR
        // ---------- 二级：系统内置 Windows.Media.Ocr（离线） ----------

        public static string WinRtRecognize(Bitmap bmp)
        {
            try
            {
                // System.Drawing.Bitmap -> PNG 字节 -> WinRT 流 -> SoftwareBitmap
                byte[] png;
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }

                var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0));
                writer.WriteBytes(png);
                Await(writer.StoreAsync());
                Await(writer.FlushAsync());
                writer.DetachStream();
                stream.Seek(0);

                var decoder = Await(Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream));
                var software = Await(decoder.GetSoftwareBitmapAsync());
                var bgra = Windows.Graphics.Imaging.SoftwareBitmap.Convert(
                    software, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

                var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                             ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                                 new Windows.Globalization.Language("zh-CN"))
                             ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                                 new Windows.Globalization.Language("en"));
                if (engine == null)
                    return "OCR 引擎不可用：未安装任何语言包。\r\n" +
                           "OCR engine unavailable: no language pack installed.\r\n" +
                           "请在 Windows 设置 > 时间和语言 中添加语言的可选功能“基本键入”，" +
                           "或安装 Umi-OCR 以获得更好的中文识别效果。";

                var result = Await(engine.RecognizeAsync(bgra));
                var sb = new System.Text.StringBuilder();
                foreach (var line in result.Lines)
                    sb.AppendLine(line.Text);
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "OCR 失败 / failed: " + ex.Message;
            }
        }

        /// <summary>阻塞等待 WinRT 异步操作完成，避免依赖 AsTask() 外观库。</summary>
        private static T Await<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            using (var done = new ManualResetEventSlim(false))
            {
                op.Completed = delegate { done.Set(); };
                done.Wait();
            }
            return op.GetResults();
        }

        private static void Await(Windows.Foundation.IAsyncOperationWithProgress<uint, uint> op)
        {
            using (var done = new ManualResetEventSlim(false))
            {
                op.Completed = delegate { done.Set(); };
                done.Wait();
            }
            op.GetResults();
        }

        private static void Await(Windows.Foundation.IAsyncOperation<bool> op)
        {
            using (var done = new ManualResetEventSlim(false))
            {
                op.Completed = delegate { done.Set(); };
                done.Wait();
            }
            op.GetResults();
        }
#endif
    }
}
