using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>
    /// 视频格式转换：批量选文件 → 选目标容器 → 优先"无损"（流拷贝 -c copy，
    /// 画质零损失、秒级完成）；目标容器装不下原编码时自动回退为重新编码。
    /// ffmpeg 已内置于 exe 资源中（单文件分发），缺失时可在此界面手动指定。
    /// 界面为浅灰底 + 白卡片 + 统一设计令牌（见 UiKit.cs），窗口可自由缩放。
    /// </summary>
    internal sealed class VideoConvertForm : Form
    {
        private readonly ListBox _list = new ListBox();
        private readonly Label _lblFiles = new Label();
        private readonly Panel _logo = new Panel();
        private readonly Panel _drop = new Panel();
        private readonly Label _lblLog = new Label();
        private readonly ComboBox _format = new ComboBox();
        private readonly ComboBox _mode = new ComboBox();
        private readonly CheckBox _sameDir = new CheckBox();
        private readonly TextBox _outDir = new TextBox();
        private readonly Label _ffDot = new Label();
        private readonly Label _ff = new Label();
        private readonly ModernProgress _bar = new ModernProgress();
        private readonly Label _status = new Label();
        private readonly TextBox _log = new TextBox();
        private readonly PillButton _start = new PillButton();
        private readonly PillButton _stop = new PillButton();

        private Thread _worker;
        private volatile bool _cancelling;
        private volatile bool _running;

        private bool _centered;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_centered) { _centered = true; Ui.CenterOnScreen(this); }
        }

        public VideoConvertForm()
        {
            Text = "视频格式转换";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            ClientSize = new Size(800, 664);
            MinimumSize = new Size(740, 600);
            BackColor = Ui.Bg;
            Font = Ui.Body;
            DoubleBuffered = true;
            Icon = MainContext.CreateTrayIcon();   // 左上角应用图标

            // ---- 标题区 ----
            _logo.Location = new Point(16, 14);
            _logo.Size = new Size(32, 32);
            _logo.BackColor = Ui.Bg;
            _logo.Paint += delegate(object s, PaintEventArgs e) { DrawLogo(e.Graphics, _logo.ClientRectangle); };

            var lblTitle = new Label
            {
                Text = "视频格式转换",
                Location = new Point(58, 14),
                Font = Ui.Title,
                ForeColor = Ui.Text,
                AutoSize = true
            };
            var lblSub = new Label
            {
                Text = "无损优先：编码兼容时直接流拷贝，画质零损失；不兼容时自动回退重编码",
                Location = new Point(58, 46),
                Font = Ui.Sub,
                ForeColor = Ui.TextSub,
                AutoSize = true
            };

            // ---- 卡片一：文件 ----
            _lblFiles.Text = "待转换文件";
            _lblFiles.Location = new Point(16, 76);
            _lblFiles.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _lblFiles.ForeColor = Ui.Text;
            _lblFiles.AutoSize = true;

            var card1 = new Card
            {
                Location = new Point(16, 96),
                Size = new Size(ClientSize.Width - 32, 160),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _list.Location = new Point(12, 12);
            _list.Size = new Size(card1.Width - 12 * 2 - 116 - 12, card1.Height - 24);
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _list.IntegralHeight = false;
            _list.BorderStyle = BorderStyle.None;
            _list.HorizontalScrollbar = true;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = 28;
            _list.AllowDrop = true;
            _list.DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            _list.DragDrop += delegate(object s, DragEventArgs e)
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files == null) return;
                foreach (string f in files)
                    if (File.Exists(f) && _list.Items.IndexOf(f) < 0) _list.Items.Add(f);
                UpdateFilesMeta();
            };
            _list.DrawItem += DrawFileItem;
            _list.SelectedIndexChanged += delegate { _list.Invalidate(); };

            _drop.BackColor = Ui.CardBg;
            _drop.Location = _list.Location;
            _drop.Size = _list.Size;
            _drop.Anchor = _list.Anchor;
            _drop.AllowDrop = true;
            _drop.Paint += DrawDropZone;
            _drop.DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            _drop.DragDrop += delegate(object s, DragEventArgs e)
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files == null) return;
                foreach (string f in files)
                    if (File.Exists(f) && _list.Items.IndexOf(f) < 0) _list.Items.Add(f);
                UpdateFilesMeta();
            };

            var bAdd = new PillButton { Text = "添加文件…", Primary = true, Location = new Point(card1.Width - 128, 12), Size = new Size(116, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var bDel = new PillButton { Text = "移除选中", Location = new Point(card1.Width - 128, 50), Size = new Size(116, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var bClr = new PillButton { Text = "清空列表", Location = new Point(card1.Width - 128, 88), Size = new Size(116, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            bAdd.Click += delegate { AddFiles(); };
            bDel.Click += delegate { if (_list.SelectedIndex >= 0) { _list.Items.RemoveAt(_list.SelectedIndex); UpdateFilesMeta(); } };
            bClr.Click += delegate { _list.Items.Clear(); UpdateFilesMeta(); };
            card1.Controls.AddRange(new Control[] { _list, _drop, bAdd, bDel, bClr });

            // ---- 卡片二：设置 ----
            var lblSet = new Label
            {
                Text = "转换设置",
                Location = new Point(16, 268),
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                ForeColor = Ui.Text,
                AutoSize = true
            };

            var card2 = new Card
            {
                Location = new Point(16, 288),
                Size = new Size(ClientSize.Width - 32, 104),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var lFmt = new Label { Text = "目标格式", Location = new Point(14, 17), ForeColor = Ui.TextSub, AutoSize = true };
            _format.Location = new Point(82, 13);
            _format.Size = new Size(224, 26);
            _format.DropDownStyle = ComboBoxStyle.DropDownList;
            _format.FlatStyle = FlatStyle.Flat;
            _format.Items.AddRange(VideoFormat.All);
            _format.SelectedItem = VideoFormat.ByExt(Settings.Current.ConvertFormat);

            var lMode = new Label { Text = "转换方式", Location = new Point(334, 17), ForeColor = Ui.TextSub, AutoSize = true };
            _mode.Location = new Point(402, 13);
            _mode.Size = new Size(236, 26);
            _mode.DropDownStyle = ComboBoxStyle.DropDownList;
            _mode.FlatStyle = FlatStyle.Flat;
            _mode.Items.AddRange(new object[] { "无损优先（流拷贝，推荐）", "始终重新编码" });
            _mode.SelectedIndex = Settings.Current.ConvertMode == 1 ? 1 : 0;

            _sameDir.Text = "输出到源文件所在目录";
            _sameDir.Location = new Point(14, 56);
            _sameDir.AutoSize = true;
            _sameDir.Checked = true;
            _sameDir.ForeColor = Ui.Text;
            _sameDir.CheckedChanged += delegate { _outDir.Enabled = !_sameDir.Checked; };

            _outDir.Location = new Point(196, 55);
            _outDir.Size = new Size(card2.Width - 196 - 14 - 72 - 8, 24);
            _outDir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _outDir.BorderStyle = BorderStyle.FixedSingle;
            _outDir.Text = Settings.Current.VideoFolder;
            _outDir.Enabled = false;

            var bDir = new PillButton { Text = "浏览…", Location = new Point(card2.Width - 14 - 72, 53), Size = new Size(72, 27), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            bDir.Click += delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    if (_outDir.Text.Length > 0 && Directory.Exists(_outDir.Text)) d.SelectedPath = _outDir.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) _outDir.Text = d.SelectedPath;
                }
            };
            card2.Controls.AddRange(new Control[] { lFmt, _format, lMode, _mode, _sameDir, _outDir, bDir });

            // ---- 进度与状态 ----
            _bar.Location = new Point(16, 406);
            _bar.Size = new Size(ClientSize.Width - 32, 20);
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _bar.Set(0);

            _status.Location = new Point(16, 432);
            _status.Size = new Size(ClientSize.Width - 32, 18);
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _status.ForeColor = Ui.TextSub;
            _status.AutoEllipsis = true;
            _status.Text = "同格式互转（如 MKV→MP4）且编码兼容时，走流拷贝可做到完全无损。";

            _ffDot.Text = "●";
            _ffDot.Location = new Point(16, 458);
            _ffDot.Size = new Size(18, 20);
            _ffDot.Font = new Font("Segoe UI", 10f);

            _ff.Location = new Point(38, 460);
            _ff.Size = new Size(ClientSize.Width - 38 - 150 - 12, 18);
            _ff.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _ff.AutoEllipsis = true;

            var bFf = new PillButton { Text = "指定 ffmpeg.exe…", Location = new Point(ClientSize.Width - 16 - 150, 454), Size = new Size(150, 30), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            bFf.Click += delegate { PickFfmpeg(); };

            // ---- 日志 ----
            _lblLog.Text = "日志";
            _lblLog.Location = new Point(16, 486);
            _lblLog.Size = new Size(200, 16);
            _lblLog.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
            _lblLog.ForeColor = Ui.Text;

            _log.Location = new Point(16, 504);
            _log.Size = new Size(ClientSize.Width - 32, 100);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.BackColor = Color.FromArgb(250, 251, 252);
            _log.Font = Ui.Mono;

            // ---- 底部操作 ----
            _start.Text = "开始转换";
            _start.Primary = true;
            _start.Location = new Point(ClientSize.Width - 16 - 124, ClientSize.Height - 48);
            _start.Size = new Size(124, 36);
            _start.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _start.Click += delegate { StartConvert(); };

            _stop.Text = "取消转换";
            _stop.Location = new Point(ClientSize.Width - 16 - 124 - 8 - 96, ClientSize.Height - 44);
            _stop.Size = new Size(96, 32);
            _stop.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _stop.Enabled = false;
            _stop.Click += delegate { _cancelling = true; _status.Text = "正在取消…"; };

            Controls.AddRange(new Control[] { _logo, lblTitle, lblSub, _lblFiles, card1, lblSet, card2,
                _bar, _status, _ffDot, _ff, bFf, _lblLog, _log, _start, _stop });

            RefreshFfmpeg();
            UpdateFilesMeta();

            // 无感 ffmpeg：就绪时不显示任何 ffmpeg 元素，日志区上移补位；
            // 只有缺失时才显示红色提示行和"指定 ffmpeg.exe…"按钮。
            if (Ffmpeg.Find() != null)
            {
                _ffDot.Visible = false;
                _ff.Visible = false;
                bFf.Visible = false;
                _lblLog.Location = new Point(16, 458);
                _log.Location = new Point(16, 476);
                _log.Height = 128;
            }
        }

        // ---------------- 展示层辅助 ----------------

        private void DrawFileItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool sel = (e.State & DrawItemState.Selected) != 0;

            using (var br = new SolidBrush(sel ? Ui.AccentSoft : Ui.CardBg))
                g.FillRectangle(br, e.Bounds);
            if (sel)
                using (var br = new SolidBrush(Ui.Accent))
                    g.FillRectangle(br, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);

            string path = (string)_list.Items[e.Index];
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(name);
            if (ext.Length > 0) ext = ext.Substring(1).ToUpperInvariant();

            var textR = new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 14 - 60, e.Bounds.Height);
            TextRenderer.DrawText(g, name, Font, textR, sel ? Ui.AccentDark : Ui.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.EndEllipsis);

            if (ext.Length > 0 && ext.Length <= 5)
            {
                int w = TextRenderer.MeasureText(ext, Ui.Sub).Width + 14;
                var badge = new Rectangle(e.Bounds.Right - 12 - w,
                    e.Bounds.Y + (e.Bounds.Height - 18) / 2, w, 18);
                using (var br = new SolidBrush(sel ? Color.White : Ui.AccentSoft))
                using (var gp = Ui.Round(badge, 9))
                    g.FillPath(br, gp);
                TextRenderer.DrawText(g, ext, Ui.Sub, badge, Ui.AccentDark,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        /// <summary>标题旁的绿色圆角图标（双向箭头 = 格式转换）。</summary>
        private static void DrawLogo(Graphics g, Rectangle r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var br = new SolidBrush(Ui.Accent))
            using (var gp = Ui.Round(new Rectangle(0, 0, r.Width, r.Height), 9))
                g.FillPath(br, gp);
            using (var p = new Pen(Color.White, 2f))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                int cy = r.Height / 2;
                g.DrawLine(p, 9, cy - 5, r.Width - 10, cy - 5);
                g.DrawLine(p, r.Width - 14, cy - 9, r.Width - 10, cy - 5);
                g.DrawLine(p, r.Width - 14, cy - 1, r.Width - 10, cy - 5);
                g.DrawLine(p, r.Width - 9, cy + 5, 10, cy + 5);
                g.DrawLine(p, 14, cy + 1, 10, cy + 5);
                g.DrawLine(p, 14, cy + 9, 10, cy + 5);
            }
        }

        /// <summary>空列表时的拖放引导区：虚线圆角框 + 下箭头 + 两行提示。</summary>
        private void DrawDropZone(object sender, PaintEventArgs e)
        {
            var c = (Control)sender;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, c.Width - 1, c.Height - 1);
            using (var pen = new Pen(Ui.BorderHi) { DashStyle = DashStyle.Dash })
            using (var gp = Ui.Round(r, 10))
                g.DrawPath(pen, gp);

            int cx = r.Width / 2, cy = r.Height / 2;
            using (var p = new Pen(Ui.Accent, 2.2f))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawLine(p, cx, cy - 42, cx, cy - 26);
                g.DrawLine(p, cx - 7, cy - 33, cx, cy - 26);
                g.DrawLine(p, cx + 7, cy - 33, cx, cy - 26);
            }
            TextRenderer.DrawText(g, "把视频文件拖到这里", Font,
                new Rectangle(0, cy - 8, r.Width, 22), Ui.Text, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "或点击右侧「添加文件…」，支持一次选多个", Ui.Sub,
                new Rectangle(0, cy + 14, r.Width, 20), Ui.TextSub, TextFormatFlags.HorizontalCenter);
        }

        private void UpdateFilesMeta()
        {
            int n = _list.Items.Count;
            _lblFiles.Text = n > 0 ? "待转换文件（" + n + "）" : "待转换文件";
            _drop.Visible = n == 0;
        }

        private void RefreshFfmpeg()
        {
            string exe = Ffmpeg.Find();
            if (exe != null)
            {
                _ffDot.ForeColor = Ui.Accent;
                _ff.Text = "ffmpeg 已就绪：" + exe;
                _ff.ForeColor = Ui.TextSub;
            }
            else
            {
                _ffDot.ForeColor = Ui.Danger;
                _ff.Text = "未找到 ffmpeg —— 请点右侧按钮指定，或重新安装完整版程序";
                _ff.ForeColor = Ui.Danger;
            }
        }

        private void PickFfmpeg()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "选择 ffmpeg.exe";
                d.Filter = "ffmpeg.exe|ffmpeg.exe|所有文件|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!Ffmpeg.IsFfmpeg(d.FileName))
                {
                    MessageBox.Show(this, "这个文件不是可用的 ffmpeg（未能读取到版本信息）。",
                        "快截", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Settings.Current.FfmpegPath = d.FileName;
                Settings.Current.Save();
                RefreshFfmpeg();
            }
        }

        private void AddFiles()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "选择要转换的视频";
                d.Filter = "视频|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.mpg;*.mpeg;*.ts;*.3gp;*.gif|所有文件|*.*";
                d.Multiselect = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                foreach (string f in d.FileNames)
                    if (_list.Items.IndexOf(f) < 0) _list.Items.Add(f);
            }
            UpdateFilesMeta();
        }

        private void Log(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), s); return; }
            _log.AppendText(s + Environment.NewLine);
        }

        private void SetProgress(double pct, string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action<double, string>(SetProgress), pct, text); return; }
            _bar.Set(pct);
            if (text != null) _status.Text = text;
        }

        private string OutputPath(string input, VideoFormat fmt)
        {
            string dir = _sameDir.Checked ? Path.GetDirectoryName(input) : _outDir.Text;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Path.GetDirectoryName(input);
            string name = Path.GetFileNameWithoutExtension(input);
            string p = Path.Combine(dir, name + "." + fmt.Ext);
            if (string.Equals(p, input, StringComparison.OrdinalIgnoreCase))
                p = Path.Combine(dir, name + "_converted." + fmt.Ext);
            int i = 2;
            while (File.Exists(p))
            {
                p = Path.Combine(dir, name + "_" + i + "." + fmt.Ext);
                i++;
            }
            return p;
        }

        // ---------------- 转换流程（逻辑与之前完全一致） ----------------

        private void StartConvert()
        {
            if (_running) return;
            if (_list.Items.Count == 0)
            {
                MessageBox.Show(this, "请先添加要转换的视频文件。", "快截",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Ffmpeg.Find() == null)
            {
                MessageBox.Show(this,
                    "没有找到 ffmpeg.exe。\r\n\r\n请点「指定 ffmpeg.exe…」选择它。",
                    "快截", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var fmt = _format.SelectedItem as VideoFormat ?? VideoFormat.All[0];
            bool remux = _mode.SelectedIndex == 0;
            var files = new List<string>();
            foreach (object o in _list.Items) files.Add((string)o);

            Settings.Current.ConvertFormat = fmt.Ext;
            Settings.Current.ConvertMode = remux ? 0 : 1;
            Settings.Current.Save();

            _cancelling = false;
            _running = true;
            _start.Enabled = false;
            _stop.Enabled = true;
            _log.Clear();
            _bar.Set(0);

            _worker = new Thread(delegate()
            {
                int ok = 0, fail = 0, remuxed = 0;
                try
                {
                    for (int i = 0; i < files.Count; i++)
                    {
                        if (_cancelling) break;
                        string src = files[i];
                        string dst = OutputPath(src, fmt);
                        string name = Path.GetFileName(src);
                        int idx = i;

                        SetProgress(idx * 100.0 / files.Count,
                            "正在处理 " + (idx + 1) + "/" + files.Count + "：" + name);

                        var r = Ffmpeg.Convert(src, dst, fmt, remux,
                            delegate(double pct)
                            {
                                SetProgress((idx + pct / 100.0) * 100.0 / files.Count,
                                    "正在处理 " + (idx + 1) + "/" + files.Count + "：" + name
                                    + "  " + ((int)pct) + "%");
                            },
                            delegate() { return _cancelling; });

                        if (r.Ok)
                        {
                            ok++;
                            if (r.Remuxed) remuxed++;
                            Log("[完成] " + name + "  →  " + Path.GetFileName(dst)
                                + (r.Remuxed ? "  （无损流拷贝）" : "  （重新编码）"));
                        }
                        else
                        {
                            fail++;
                            Log("[失败] " + name + "  " + r.Error);
                        }
                    }
                }
                catch (Exception ex) { Log("[异常] " + ex.Message); }

                BeginInvoke(new Action(delegate
                {
                    _running = false;
                    _start.Enabled = true;
                    _stop.Enabled = false;
                    _bar.Set(100);
                    string msg = "完成 " + ok + " 个"
                        + (remuxed > 0 ? "（其中 " + remuxed + " 个为无损流拷贝）" : "")
                        + (fail > 0 ? "，失败 " + fail + " 个" : "")
                        + (_cancelling ? "，已取消" : "");
                    _status.Text = msg + "。详情见下方日志。";
                    if (!_cancelling && ok > 0)
                    {
                        string dir = _sameDir.Checked
                            ? Path.GetDirectoryName(files[0]) : _outDir.Text;
                        if (MessageBox.Show(this, msg + "。\r\n\r\n是否打开输出文件夹？", "快截",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            try { System.Diagnostics.Process.Start(dir); } catch { }
                        }
                    }
                }));
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 转换进行中不允许直接关闭（ffmpeg 子进程还在跑）
            if (_running)
            {
                e.Cancel = true;
                MessageBox.Show(this, "转换正在进行，请先点「取消转换」。", "快截",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            base.OnFormClosing(e);
        }
    }
}
