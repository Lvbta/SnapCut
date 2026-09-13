using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace SimpleShot
{
    /// <summary>
    /// App configuration persisted as a tiny key=value file in
    /// %AppData%\SimpleShot\config.ini. No JSON library needed.
    /// </summary>
    internal sealed class Settings
    {
        private static Settings _current;
        public static Settings Current { get { return _current ?? (_current = Load()); } }

        // ---- 通用 ----
        public bool RunAtStartup = false;      // 开机自动启动（写 HKCU Run 注册表项）

        // ---- screenshot ----
        public string SaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        public bool CopyAfterSave = true;

        // ---- recorder ----
        public string VideoFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        public int Fps = 15;
        public int JpegQuality = 75;
        public bool CaptureCursor = true;
        public bool OpenFolderAfterRecord = true;
        public bool ShowRecordBorder = true;   // thin frame around the recorded region
        public bool RecordAudio = true;        // 录制系统声音（环回采集），可在设置中关闭
        public bool HighlightMouse = false;    // draw click / scroll annotations into the video

        // ---- long screenshot ----
        public int ScrollDelayMs = 350;   // wait after each scroll before capturing
        public int ScrollStep = 400;      // wheel delta magnitude per step
        /// <summary>自动滚动通道：0=自动（失败逐级回退）1=滚动条消息(WM_VSCROLL)
        /// 2=真实滚轮 3=PageDown 4=方向键。对齐 ShareX 的"滚动方式可选"。</summary>
        public int ScrollMethod = 0;

        // ---- 视频转换 ----
        public string FfmpegPath = "";       // ffmpeg.exe 路径（留空 = 自动探测）
        public string ConvertFormat = "mp4"; // 默认目标容器
        public int ConvertMode = 0;          // 0=无损优先（先流拷贝，失败才重编码） 1=始终重编码

        // ---- 抠图 ----
        public bool MattingEnabled = false;  // 默认隐藏入口（功能暂停推进，代码保留待后续开发）

        // ---- 悬浮窗 ----
        /// <summary>true=把悬浮窗从屏幕捕获中排除（截图/录屏里不会出现它）。
        /// 注意：该标记会让悬浮窗在截图、录屏、远程桌面中显示为黑色块；
        /// 若需要把悬浮窗截给别人看，把它设为 false。</summary>
        public bool BarExcludeFromCapture = true;

        // ---- 在线更新 ----
        public bool CheckUpdateOnStartup = true;
        /// <summary>更新清单地址（纯文本 key=value，见 update/manifest.txt）。</summary>
        public string UpdateUrl = "https://raw.githubusercontent.com/Lvbta/SnapCut/master/update/manifest.txt";
        public string LastUpdateCheck = "";   // 上次检查日期 yyyy-MM-dd
        public string IgnoredVersion = "";    // 用户选择"忽略此版本"的版本号

        // ---- hotkeys (virtual-key + modifier flags) ----
        public uint ShotMods = NativeMethods.MOD_ALT;
        public uint ShotKey = 0x41;        // 'A'
        public uint RecordMods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        public uint RecordKey = 0x52;      // 'R'

        // ---- floating ball ----
        public bool ShowFloatingBar = true;   // 显示桌面悬浮窗（贴边窄条，悬停展开功能按钮）
        public int BallX = -1;   // 悬浮窗位置（-1 = 默认屏幕右下）
        public int BallY = -1;
        public bool BallDockLeft = false;     // 旧版设置：悬浮窗吸附左屏缘（已由 BallSide 取代，读取时兼容）
        public int BallSide = 1;              // 悬浮窗吸附的屏缘：0=左 1=右 2=上 3=下

        private static string ConfigDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleShot");
            }
        }
        private static string ConfigPath { get { return Path.Combine(ConfigDir, "config.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(ConfigPath)) return s;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(ConfigPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                s.RunAtStartup = GetBool(map, "RunAtStartup", s.RunAtStartup);
                s.SaveFolder = Get(map, "SaveFolder", s.SaveFolder);
                s.CopyAfterSave = GetBool(map, "CopyAfterSave", s.CopyAfterSave);
                s.VideoFolder = Get(map, "VideoFolder", s.VideoFolder);
                s.Fps = Clamp(GetInt(map, "Fps", s.Fps), 5, 60);
                s.JpegQuality = Clamp(GetInt(map, "JpegQuality", s.JpegQuality), 20, 100);
                s.CaptureCursor = GetBool(map, "CaptureCursor", s.CaptureCursor);
                s.OpenFolderAfterRecord = GetBool(map, "OpenFolderAfterRecord", s.OpenFolderAfterRecord);
                s.ShowRecordBorder = GetBool(map, "ShowRecordBorder", s.ShowRecordBorder);
                s.RecordAudio = GetBool(map, "RecordAudio", s.RecordAudio);
                s.HighlightMouse = GetBool(map, "HighlightMouse", s.HighlightMouse);
                s.ScrollDelayMs = Clamp(GetInt(map, "ScrollDelayMs", s.ScrollDelayMs), 100, 2000);
                s.ScrollStep = Clamp(GetInt(map, "ScrollStep", s.ScrollStep), 100, 1200);
                s.ScrollMethod = Clamp(GetInt(map, "ScrollMethod", s.ScrollMethod), 0, 4);
                s.FfmpegPath = Get(map, "FfmpegPath", s.FfmpegPath);
                s.ConvertFormat = Get(map, "ConvertFormat", s.ConvertFormat);
                s.ConvertMode = Clamp(GetInt(map, "ConvertMode", s.ConvertMode), 0, 1);
                s.MattingEnabled = GetBool(map, "MattingEnabled", s.MattingEnabled);
                s.BarExcludeFromCapture = GetBool(map, "BarExcludeFromCapture", s.BarExcludeFromCapture);
                s.CheckUpdateOnStartup = GetBool(map, "CheckUpdateOnStartup", s.CheckUpdateOnStartup);
                s.UpdateUrl = Get(map, "UpdateUrl", s.UpdateUrl);
                s.LastUpdateCheck = Get(map, "LastUpdateCheck", s.LastUpdateCheck);
                s.IgnoredVersion = Get(map, "IgnoredVersion", s.IgnoredVersion);
                s.ShotMods = (uint)GetInt(map, "ShotMods", (int)s.ShotMods);
                s.ShotKey = (uint)GetInt(map, "ShotKey", (int)s.ShotKey);
                s.RecordMods = (uint)GetInt(map, "RecordMods", (int)s.RecordMods);
                s.RecordKey = (uint)GetInt(map, "RecordKey", (int)s.RecordKey);
                s.ShowFloatingBar = GetBool(map, "ShowFloatingBar", s.ShowFloatingBar);
                s.BallX = GetInt(map, "BallX", -1);
                s.BallY = GetInt(map, "BallY", -1);
                s.BallDockLeft = GetBool(map, "BallDockLeft", s.BallDockLeft);
                s.BallSide = Clamp(GetInt(map, "BallSide", s.BallDockLeft ? 0 : 1), 0, 3);
            }
            catch { /* fall back to defaults */ }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# SnapCut configuration");
                sb.AppendLine("RunAtStartup=" + RunAtStartup);
                sb.AppendLine("SaveFolder=" + SaveFolder);
                sb.AppendLine("CopyAfterSave=" + CopyAfterSave);
                sb.AppendLine("VideoFolder=" + VideoFolder);
                sb.AppendLine("Fps=" + Fps);
                sb.AppendLine("JpegQuality=" + JpegQuality);
                sb.AppendLine("CaptureCursor=" + CaptureCursor);
                sb.AppendLine("OpenFolderAfterRecord=" + OpenFolderAfterRecord);
                sb.AppendLine("ShowRecordBorder=" + ShowRecordBorder);
                sb.AppendLine("RecordAudio=" + RecordAudio);
                sb.AppendLine("HighlightMouse=" + HighlightMouse);
                sb.AppendLine("ScrollDelayMs=" + ScrollDelayMs);
                sb.AppendLine("ScrollStep=" + ScrollStep);
                sb.AppendLine("ScrollMethod=" + ScrollMethod);
                sb.AppendLine("FfmpegPath=" + FfmpegPath);
                sb.AppendLine("ConvertFormat=" + ConvertFormat);
                sb.AppendLine("ConvertMode=" + ConvertMode);
                sb.AppendLine("MattingEnabled=" + MattingEnabled);
                sb.AppendLine("BarExcludeFromCapture=" + BarExcludeFromCapture);
                sb.AppendLine("CheckUpdateOnStartup=" + CheckUpdateOnStartup);
                sb.AppendLine("UpdateUrl=" + UpdateUrl);
                sb.AppendLine("LastUpdateCheck=" + LastUpdateCheck);
                sb.AppendLine("IgnoredVersion=" + IgnoredVersion);
                sb.AppendLine("ShotMods=" + ShotMods);
                sb.AppendLine("ShotKey=" + ShotKey);
                sb.AppendLine("RecordMods=" + RecordMods);
                sb.AppendLine("RecordKey=" + RecordKey);
                sb.AppendLine("ShowFloatingBar=" + ShowFloatingBar);
                sb.AppendLine("BallX=" + BallX);
                sb.AppendLine("BallY=" + BallY);
                sb.AppendLine("BallDockLeft=" + BallDockLeft);
                sb.AppendLine("BallSide=" + BallSide);
                File.WriteAllText(ConfigPath, sb.ToString());
            }
            catch { /* non-fatal */ }
        }

        /// <summary>把“开机自动启动”写入/移除 HKCU 的 Run 注册表项（无需管理员权限）。
        /// 状态未变化时不触碰注册表 —— 每次启动都写一次会触发安全软件的“修改启动项”提示。</summary>
        public static void ApplyStartup(bool enable)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    string path = "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"";
                    object cur = key.GetValue("SnapCut");
                    object legacy = key.GetValue("SimpleShot");   // 旧名称残留
                    if (enable)
                    {
                        if (cur != null && (string)cur == path)
                        {
                            if (legacy != null) key.DeleteValue("SimpleShot", false);
                            return;
                        }
                        key.SetValue("SnapCut", path);
                        key.DeleteValue("SimpleShot", false);
                    }
                    else
                    {
                        if (cur == null && legacy == null) return;
                        key.DeleteValue("SnapCut", false);
                        key.DeleteValue("SimpleShot", false);
                    }
                }
            }
            catch { /* 无写权限等情况：静默忽略 */ }
        }

        private static string Get(Dictionary<string, string> m, string k, string d)
        {
            string v; return m.TryGetValue(k, out v) && v.Length > 0 ? v : d;
        }
        private static int GetInt(Dictionary<string, string> m, string k, int d)
        {
            string v; int r;
            return m.TryGetValue(k, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : d;
        }
        private static bool GetBool(Dictionary<string, string> m, string k, bool d)
        {
            string v; bool r;
            return m.TryGetValue(k, out v) && bool.TryParse(v, out r) ? r : d;
        }
        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        /// <summary>把“修饰键 + 虚拟键”格式化为 "Ctrl + Alt + R" 这样的显示文本。</summary>
        public static string HotkeyText(uint mods, uint key)
        {
            var sb = new System.Text.StringBuilder();
            if ((mods & NativeMethods.MOD_CONTROL) != 0) sb.Append("Ctrl + ");
            if ((mods & NativeMethods.MOD_ALT) != 0) sb.Append("Alt + ");
            if ((mods & NativeMethods.MOD_SHIFT) != 0) sb.Append("Shift + ");
            sb.Append(KeyName(key));
            return sb.ToString();
        }

        /// <summary>虚拟键的友好名称（字母/数字/F 键直接显示，其余用 Keys 枚举名）。</summary>
        public static string KeyName(uint key)
        {
            if (key >= 0x41 && key <= 0x5A) return ((char)key).ToString();      // A-Z
            if (key >= 0x30 && key <= 0x39) return ((char)key).ToString();      // 0-9
            if (key >= 0x70 && key <= 0x7B) return "F" + (key - 0x70 + 1);      // F1-F12
            try { return ((System.Windows.Forms.Keys)key).ToString(); }
            catch { return "0x" + key.ToString("X2"); }
        }
    }
}
