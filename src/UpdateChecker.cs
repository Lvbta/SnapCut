using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>云端发布的新版本信息（来自 update/manifest.txt）。</summary>
    internal sealed class UpdateInfo
    {
        public string Version = "";
        public string Url = "";
        public string Notes = "";
        public long SizeBytes;

        public string SizeText
        {
            get
            {
                if (SizeBytes <= 0) return "";
                return Math.Round(SizeBytes / 1048576.0, 1) + " MB";
            }
        }
    }

    /// <summary>
    /// 在线更新：从云端清单（纯文本 key=value）读取最新版本号，与本地版本比对，
    /// 有新版本时**自动开始下载（带进度条）**，下载完成后自动静默安装并退出以完成升级。
    /// 清单格式见仓库 update/manifest.txt。
    /// </summary>
    internal static class UpdateChecker
    {
        /// <summary>
        /// 后台检查更新。回调参数：info != null 表示有新版本；否则 error 为失败原因（null 表示无更新）。
        /// 回调已在调用方线程上执行，调用方自行决定是否需要回到 UI 线程。
        /// </summary>
        public static void CheckAsync(Action<UpdateInfo, string> onDone)
        {
            var t = new Thread(delegate()
            {
                UpdateInfo info = null;
                string err = null;
                try
                {
                    // 候选清单地址：用户配置优先，再叠加多镜像兜底，任一可达即可，
                    // 单点域名（如 raw.githubusercontent.com 国内常超时）不可达不再导致"永远收不到更新"。
                    var candidates = new List<string>();
                    string cfg = Settings.Current.UpdateUrl;
                    if (!string.IsNullOrEmpty(cfg)) candidates.Add(cfg);
                    candidates.Add("https://cdn.jsdelivr.net/gh/Lvbta/SnapCut@master/update/manifest.txt");
                    candidates.Add("https://raw.githubusercontent.com/Lvbta/SnapCut/master/update/manifest.txt");

                    string text = null;
                    using (var wc = new WebClient())
                    {
                        wc.Encoding = Encoding.UTF8;
                        foreach (var baseUrl in candidates)
                        {
                            try
                            {
                                // 加时间戳绕过缓存
                                text = wc.DownloadString(baseUrl + (baseUrl.IndexOf('?') >= 0 ? "&" : "?")
                                                                 + "t=" + DateTime.Now.Ticks);
                                info = Parse(text);
                                if (info != null) break;   // 解析成功即采用
                            }
                            catch { }
                        }
                    }
                    if (info == null)
                    {
                        err = "无法获取更新清单（所有镜像均不可达）";
                        Log(err);
                    }
                    else if (!IsNewer(info.Version, AppMeta.Version)) info = null;   // 已是最新
                    else if (info.Version == Settings.Current.IgnoredVersion) info = null;  // 用户忽略过
                }
                catch (Exception ex) { info = null; err = ex.Message; Log(err); }

                if (onDone != null) onDone(info, err);
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>把更新检查失败原因写入 %LocalAppData%\SnapCut\update.log，便于事后排查"为什么没更新"。</summary>
        private static void Log(string msg)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapCut");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "update.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
            }
            catch { }
        }

        /// <summary>解析清单文本（version / url / notes / size）。</summary>
        internal static UpdateInfo Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            var info = new UpdateInfo();
            string v;
            if (map.TryGetValue("version", out v)) info.Version = v;
            if (map.TryGetValue("url", out v)) info.Url = v;
            if (map.TryGetValue("notes", out v)) info.Notes = v;
            if (map.TryGetValue("size", out v)) long.TryParse(v, out info.SizeBytes);
            if (string.IsNullOrEmpty(info.Version) || string.IsNullOrEmpty(info.Url)) return null;
            return info;
        }

        /// <summary>a 是否比 b 新（按三段版本号逐段比较）。</summary>
        public static bool IsNewer(string a, string b)
        {
            var va = Split(a);
            var vb = Split(b);
            for (int i = 0; i < 3; i++)
                if (va[i] != vb[i]) return va[i] > vb[i];
            return false;
        }

        private static int[] Split(string v)
        {
            var r = new int[3];
            var p = (v ?? "").TrimStart('v', 'V').Split('.');
            for (int i = 0; i < 3 && i < p.Length; i++)
            {
                int n;
                if (int.TryParse(p[i], out n)) r[i] = n;
            }
            return r;
        }

        /// <summary>每次启动是否检查更新（仅当设置开启时）；不再按天限流，启动即查。</summary>
        public static bool ShouldAutoCheck()
        {
            return Settings.Current.CheckUpdateOnStartup;
        }

        public static void MarkChecked()
        {
            Settings.Current.LastUpdateCheck = DateTime.Now.ToString("yyyy-MM-dd");
            Settings.Current.Save();
        }

        public static void Ignore(string version)
        {
            Settings.Current.IgnoredVersion = version;
            Settings.Current.Save();
        }
    }

    /// <summary>发现新版本时的更新窗：展示更新说明，打开即自动开始下载（带进度条）；用户可稍后或忽略。</summary>
    internal sealed class UpdateForm : Form
    {
        private readonly UpdateInfo _info;
        private readonly PillButton _go = new PillButton();
        private readonly PillButton _later = new PillButton();
        private readonly PillButton _ignore = new PillButton();
        private readonly ModernProgress _bar = new ModernProgress();
        private readonly Label _state = new Label();
        private WebClient _wc;
        private string _file;
        private bool _busy;
        private bool _allowClose;

        public UpdateForm(UpdateInfo info)
        {
            _info = info;
            Text = "发现新版本";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(460, 300);
            BackColor = Ui.Bg;
            Font = Ui.Body;
            Icon = AppIcon.Create(16);

            var title = new Label
            {
                Text = "新版本 " + info.Version,
                Location = new Point(20, 18),
                Font = Ui.Title,
                ForeColor = Ui.Text,
                AutoSize = true
            };
            var sub = new Label
            {
                Text = "当前版本 " + AppMeta.VersionText
                     + (info.SizeText.Length > 0 ? "　·　安装包 " + info.SizeText : ""),
                Location = new Point(22, 48),
                Font = Ui.Sub,
                ForeColor = Ui.TextSub,
                AutoSize = true
            };

            var notes = new TextBox
            {
                Location = new Point(20, 78),
                Size = new Size(ClientSize.Width - 40, 118),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Ui.CardBg,
                Text = string.IsNullOrEmpty(info.Notes) ? "（本次更新暂无说明）" : info.Notes
            };

            _bar.Location = new Point(20, 208);
            _bar.Size = new Size(ClientSize.Width - 40, 20);
            _bar.Set(0);
            _bar.Visible = false;

            _state.Location = new Point(20, 232);
            _state.Size = new Size(ClientSize.Width - 40, 18);
            _state.ForeColor = Ui.TextSub;
            _state.AutoEllipsis = true;
            _state.Text = "更新包会下载到临时目录，安装程序会自动完成升级。";

            _go.Text = "立即更新";
            _go.Primary = true;
            _go.Location = new Point(ClientSize.Width - 20 - 104, ClientSize.Height - 46);
            _go.Size = new Size(104, 32);
            _go.Click += delegate { StartDownload(); };

            _later.Text = "稍后";
            _later.Location = new Point(ClientSize.Width - 20 - 104 - 8 - 84, ClientSize.Height - 46);
            _later.Size = new Size(84, 32);
            _later.Click += delegate {
                if (_busy && _wc != null) { try { _wc.CancelAsync(); } catch { } }
                _allowClose = true;
                Close();
            };

            _ignore.Text = "忽略此版本";
            _ignore.Location = new Point(20, ClientSize.Height - 46);
            _ignore.Size = new Size(104, 32);
            _ignore.Click += delegate {
                if (_wc != null) { try { _wc.CancelAsync(); } catch { } }
                _allowClose = true;
                UpdateChecker.Ignore(_info.Version);
                Close();
            };

            Controls.AddRange(new Control[] { title, sub, notes, _bar, _state, _go, _later, _ignore });

            // 发现新版本后自动开始下载（带进度条），无需用户点击
            StartDownload();
        }

        private void StartDownload()
        {
            if (_busy) return;
            _busy = true;
            _go.Enabled = false;
            _later.Enabled = false;
            _ignore.Enabled = false;
            _bar.Visible = true;
            _state.Text = "正在下载更新包…";

            _file = Path.Combine(Path.GetTempPath(),
                "SnapCut-Setup-" + _info.Version + ".exe");
            try { if (File.Exists(_file)) File.Delete(_file); } catch { }

            _wc = new WebClient();
            _wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
            {
                if (e.TotalBytesToReceive > 0)
                {
                    _bar.Set(e.BytesReceived * 100.0 / e.TotalBytesToReceive);
                    _state.Text = "正在下载… " + Math.Round(e.BytesReceived / 1048576.0, 1) + " / "
                                  + Math.Round(e.TotalBytesToReceive / 1048576.0, 1) + " MB";
                }
            };
            _wc.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e)
            {
                if (e.Cancelled) { Fail("已取消"); return; }
                if (e.Error != null) { Fail("下载失败：" + e.Error.Message); return; }
                RunInstaller();
            };
            try { _wc.DownloadFileAsync(new Uri(_info.Url), _file); }
            catch (Exception ex) { Fail(ex.Message); }
        }

        private void Fail(string msg)
        {
            _busy = false;
            _go.Enabled = true;
            _later.Enabled = true;
            _ignore.Enabled = true;
            _state.ForeColor = Ui.Danger;
            _state.Text = msg;
        }

        private void RunInstaller()
        {
            try
            {
                _busy = false;
                _allowClose = true;
                _bar.Set(100);
                _state.ForeColor = Ui.TextSub;
                _state.Text = "下载完成，正在启动安装程序…";
                // /SILENT：静默安装（本安装包为免管理员的用户级安装，不会弹 UAC）
                Process.Start(new ProcessStartInfo(_file, "/SILENT /NORESTART"));
                Application.Exit();   // 退出自身，让安装程序替换文件
            }
            catch (Exception ex)
            {
                Fail("启动安装程序失败：" + ex.Message);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 下载进行中且用户未主动取消/忽略时，不允许关闭（WebClient 还在写文件）
            if (_busy && !_allowClose)
            {
                e.Cancel = true;
                return;
            }
            if (_wc != null) { _wc.Dispose(); _wc = null; }
            base.OnFormClosing(e);
        }
    }
}
