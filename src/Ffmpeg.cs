using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SimpleShot
{
    /// <summary>目标容器格式：扩展名 + 重编码时的默认编码参数。</summary>
    internal sealed class VideoFormat
    {
        public string Ext;        // 不含点，如 "mp4"
        public string Label;      // 下拉框显示名
        public string VCodec;     // 重编码用视频编码器
        public string VExtra;     // 视频附加参数
        public string ACodec;     // 重编码用音频编码器
        public string AExtra;     // 音频附加参数
        public bool NoAudio;      // 目标不含音频（gif）
        public bool NoRemux;      // 无法流拷贝，必须重编码
        public string Extra;      // 全局附加参数

        public override string ToString() { return Label; }

        public static readonly VideoFormat[] All = new[]
        {
            new VideoFormat { Ext="mp4",  Label="MP4  (*.mp4)  — 最通用",
                VCodec="libx264", VExtra="-preset veryfast -crf 23 -pix_fmt yuv420p",
                ACodec="aac", AExtra="-b:a 160k" },
            new VideoFormat { Ext="mkv",  Label="MKV  (*.mkv)  — 万能容器",
                VCodec="libx264", VExtra="-preset veryfast -crf 23 -pix_fmt yuv420p",
                ACodec="aac", AExtra="-b:a 160k" },
            new VideoFormat { Ext="mov",  Label="MOV  (*.mov)  — QuickTime",
                VCodec="libx264", VExtra="-preset veryfast -crf 23 -pix_fmt yuv420p",
                ACodec="aac", AExtra="-b:a 160k" },
            new VideoFormat { Ext="m4v",  Label="M4V  (*.m4v)  — iTunes",
                VCodec="libx264", VExtra="-preset veryfast -crf 23 -pix_fmt yuv420p",
                ACodec="aac", AExtra="-b:a 160k" },
            new VideoFormat { Ext="avi",  Label="AVI  (*.avi)  — 老设备兼容",
                VCodec="mpeg4", VExtra="-q:v 4", ACodec="libmp3lame", AExtra="-b:a 192k" },
            new VideoFormat { Ext="webm", Label="WebM (*.webm) — 网页用",
                VCodec="libvpx-vp9", VExtra="-crf 32 -b:v 0 -pix_fmt yuv420p",
                ACodec="libopus", AExtra="-b:a 128k" },
            new VideoFormat { Ext="flv",  Label="FLV  (*.flv)",
                VCodec="libx264", VExtra="-preset veryfast -crf 25 -pix_fmt yuv420p",
                ACodec="aac", AExtra="-b:a 128k" },
            new VideoFormat { Ext="wmv",  Label="WMV  (*.wmv)",
                VCodec="wmv2", VExtra="-q:v 4", ACodec="wmav2", AExtra="-b:a 160k" },
            new VideoFormat { Ext="mpg",  Label="MPG  (*.mpg)  — MPEG-2",
                VCodec="mpeg2video", VExtra="-q:v 3", ACodec="mp2", AExtra="-b:a 192k" },
            new VideoFormat { Ext="ts",   Label="TS   (*.ts)   — 流媒体切片",
                VCodec="libx264", VExtra="-preset veryfast -crf 23 -pix_fmt yuv420p",
                ACodec="aac", AExtra="-b:a 160k" },
            new VideoFormat { Ext="3gp",  Label="3GP  (*.3gp)  — 老式手机",
                VCodec="libx264", VExtra="-preset veryfast -crf 26 -pix_fmt yuv420p",
                ACodec="aac", AExtra="-b:a 64k -ar 32000" },
            new VideoFormat { Ext="gif",  Label="GIF  (*.gif)  — 动图（无声音）",
                VCodec="gif", VExtra="", ACodec=null, AExtra="",
                NoAudio=true, NoRemux=true,
                Extra="-loop 0 -vf \"fps=12,scale=640:-1:flags=lanczos\"" }
        };

        public static VideoFormat ByExt(string ext)
        {
            string e = (ext ?? "").TrimStart('.').ToLowerInvariant();
            for (int i = 0; i < All.Length; i++)
                if (All[i].Ext == e) return All[i];
            return All[0];
        }
    }

    internal sealed class FfmpegResult
    {
        public bool Ok;
        public bool Remuxed;   // 是否走的是无损流拷贝
        public string Error;
    }

    /// <summary>
    /// ffmpeg 封装：定位可执行文件、探测时长、无损重封装 / 重编码转换。
    /// ffmpeg 是外部依赖（不内置），查找顺序：设置里指定的路径 → plugins\ffmpeg\
    /// → 程序目录 → PATH。
    /// </summary>
    internal static class Ffmpeg
    {
        public static string Find()
        {
            string p = Settings.Current.FfmpegPath;
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;

            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string cand = Path.Combine(dir, "plugins", "ffmpeg", "ffmpeg.exe");
                if (File.Exists(cand)) return cand;
                cand = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(cand)) return cand;
            }
            catch { }

            // 内置资源：单文件分发时 ffmpeg 嵌在 exe 里，首次使用时释放到本地
            string embedded = ExtractEmbedded();
            if (embedded != null) return embedded;

            string ev = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string d in ev.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(d)) continue;
                try
                {
                    string cand = Path.Combine(d.Trim('"'), "ffmpeg.exe");
                    if (File.Exists(cand)) return cand;
                }
                catch { }
            }
            return null;
        }

        public static bool Available { get { return Find() != null; } }

        private const string ResName = "SimpleShot.ffmpeg.exe";

        /// <summary>
        /// 把内置的 ffmpeg 释放到 %LocalAppData%\SnapCut\ffmpeg.exe。
        /// 仅首次或大小变化时写盘，之后每次直接复用，避免启动开销。
        /// 没有内置资源（未嵌入的构建）时返回 null。
        /// </summary>
        private static string ExtractEmbedded()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream(ResName))
                {
                    if (s == null) return null;
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SnapCut");
                    Directory.CreateDirectory(dir);
                    string dst = Path.Combine(dir, "ffmpeg.exe");
                    if (!File.Exists(dst) || new FileInfo(dst).Length != s.Length)
                    {
                        using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write))
                            s.CopyTo(fs);
                    }
                    return dst;
                }
            }
            catch { return null; }
        }

        /// <summary>校验某个 exe 确实是 ffmpeg（用户手动指定时用）。</summary>
        public static bool IsFfmpeg(string path)
        {
            try
            {
                var psi = new ProcessStartInfo(path, "-version");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    string s = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return s.IndexOf("ffmpeg version", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        /// <summary>探测视频时长（秒），失败返回 -1。用于换算进度百分比。</summary>
        public static double ProbeDuration(string file)
        {
            string exe = Find();
            if (exe == null || string.IsNullOrEmpty(file) || !File.Exists(file)) return -1;
            try
            {
                var psi = new ProcessStartInfo(exe);
                // 只给 -i 不给输出文件：ffmpeg 打印完信息就退出（退出码非 0，属正常）
                psi.Arguments = "-hide_banner -i \"" + file + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;

                var sb = new StringBuilder();
                using (var p = new Process { StartInfo = psi })
                {
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } }
                }

                var m = Regex.Match(sb.ToString(), @"Duration:\s*(\d+):(\d+):(\d+(?:[.,]\d+)?)");
                if (!m.Success) return -1;
                double hh = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double mm = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                double ss = double.Parse(m.Groups[3].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                return hh * 3600 + mm * 60 + ss;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 转换单个文件。allowRemux 时先尝试 -c copy 无损重封装（秒级、画质零损失），
        /// 失败（目标容器装不下原编码，或退出码非 0）再回退到重编码。
        /// onProgress 收到 0–100 的百分比。
        /// </summary>
        public static FfmpegResult Convert(string inFile, string outFile, VideoFormat fmt,
            bool allowRemux, Action<double> onProgress, Func<bool> isCancelled)
        {
            var res = new FfmpegResult();
            string exe = Find();
            if (exe == null) { res.Error = "未找到 ffmpeg.exe"; return res; }
            if (fmt == null) fmt = VideoFormat.All[0];

            double dur = ProbeDuration(inFile);
            Delete(outFile);

            // 1) 无损优先：流拷贝重封装
            if (allowRemux && !fmt.NoRemux)
            {
                string args = "-y -hide_banner -progress pipe:1 -nostats -i \"" + inFile
                            + "\" -map 0 -c copy \"" + outFile + "\"";
                string err;
                int code = Run(exe, args, dur, onProgress, isCancelled, out err);
                if (code == 0 && NonEmpty(outFile))
                {
                    res.Ok = true;
                    res.Remuxed = true;
                    return res;
                }
                Delete(outFile);   // 失败残留删掉，避免留下半个坏文件
            }

            // 2) 重编码兜底
            string a = "-y -hide_banner -progress pipe:1 -nostats -i \"" + inFile + "\"";
            if (fmt.NoAudio) a += " -an";
            a += " -c:v " + fmt.VCodec;
            if (!string.IsNullOrEmpty(fmt.VExtra)) a += " " + fmt.VExtra;
            if (!fmt.NoAudio && !string.IsNullOrEmpty(fmt.ACodec))
            {
                a += " -c:a " + fmt.ACodec;
                if (!string.IsNullOrEmpty(fmt.AExtra)) a += " " + fmt.AExtra;
            }
            if (!string.IsNullOrEmpty(fmt.Extra)) a += " " + fmt.Extra;
            a += " \"" + outFile + "\"";

            string err2;
            int code2 = Run(exe, a, dur, onProgress, isCancelled, out err2);
            if (code2 == 0 && NonEmpty(outFile))
            {
                res.Ok = true;
                res.Remuxed = false;
                return res;
            }
            res.Error = Tail(err2);
            return res;
        }

        private static int Run(string exe, string args, double durationSec,
            Action<double> onProgress, Func<bool> isCancelled, out string error)
        {
            error = "";
            var err = new StringBuilder();
            try
            {
                var psi = new ProcessStartInfo(exe);
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null || onProgress == null) return;
                        // -progress pipe:1 输出形如：out_time_ms=12345678
                        if (e.Data.StartsWith("out_time_ms=", StringComparison.Ordinal))
                        {
                            long ms;
                            if (long.TryParse(e.Data.Substring(12), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out ms) && durationSec > 0)
                            {
                                double pct = ms / 1000.0 / durationSec * 100.0;
                                if (pct < 0) pct = 0;
                                if (pct > 100) pct = 100;
                                onProgress(pct);
                            }
                        }
                    };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) err.AppendLine(e.Data); };

                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    while (!p.WaitForExit(500))
                    {
                        if (isCancelled != null && isCancelled())
                        {
                            try { p.Kill(); } catch { }
                            error = "已取消";
                            return -1;
                        }
                    }
                    error = err.ToString();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { error = ex.Message; return -1; }
        }

        private static bool NonEmpty(string p)
        {
            try { return File.Exists(p) && new FileInfo(p).Length > 0; }
            catch { return false; }
        }

        private static void Delete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        /// <summary>取错误输出最后几行（ffmpeg 的真实原因通常在末尾）。</summary>
        private static string Tail(string err)
        {
            if (string.IsNullOrEmpty(err)) return "ffmpeg 执行失败（无错误输出）";
            var lines = err.Split('\n');
            var sb = new StringBuilder();
            int start = Math.Max(0, lines.Length - 6);
            for (int i = start; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length > 0) sb.AppendLine(t);
            }
            return sb.ToString().Trim();
        }
    }
}
