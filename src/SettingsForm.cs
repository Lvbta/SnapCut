using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>
    /// 设置对话框（侧边导航式，参考现代应用的窗口布局）：
    /// 左侧为应用名与分组导航（圆角胶囊高亮当前项），右侧为对应分组的设置内容，
    /// 底部右侧为“取消 / 确定”胶囊按钮；Win11 上自动启用系统圆角窗口。
    /// 由调用方在返回 OK 后执行 Settings.Current.Save() 持久化到磁盘。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        // ---- 配色（浅色现代风格）----
        private static readonly Color SidebarBg = Color.FromArgb(247, 247, 248);  // 侧栏浅灰
        private static readonly Color ContentBg = Color.White;                    // 内容区白底
        private static readonly Color PillActive = Color.FromArgb(233, 233, 236); // 导航选中胶囊
        private static readonly Color PillHover = Color.FromArgb(242, 242, 244);  // 导航悬停胶囊
        private static readonly Color TextMain = Color.FromArgb(30, 30, 30);
        private static readonly Color TextSub = Color.FromArgb(140, 140, 140);
        private static readonly Color BorderColor = Color.FromArgb(232, 233, 235);
        private static readonly Color DarkPill = Color.FromArgb(30, 30, 30);      // “确定”黑胶囊
        private static readonly Color Accent = Color.FromArgb(7, 193, 96);

        private readonly Panel _content = new Panel();
        private readonly List<Panel> _pages = new List<Panel>();
        private readonly List<NavItem> _navs = new List<NavItem>();

        private TextBox _saveFolder, _videoFolder;
        private CheckBox _runAtStartup, _showBar, _copyAfterSave, _captureCursor, _showBorder,
                         _recordAudio, _highlightMouse, _openFolder;
        private NumericUpDown _fps, _jpeg, _scrollDelay, _scrollStep;
        private ComboBox _scrollMethod;
        private HotkeyBox _shotKey, _recKey;

        private bool _centered;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_centered) { _centered = true; Ui.CenterOnScreen(this); }
        }

        public SettingsForm()
        {
            var s = Settings.Current;

            Icon = MainContext.CreateTrayIcon();
            AutoScaleMode = AutoScaleMode.None;
            Text = "设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = ContentBg;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ForeColor = TextMain;
            ClientSize = new Size(680, 520);

            BuildSidebar();
            BuildContent(s);
            BuildBottomBar();
            ShowPage(0);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Win11 圆角窗口（Win10 上调用失败则静默忽略，退回方角）
            try
            {
                int pref = 2; // DWMCP_ROUND
                NativeMethods.DwmSetWindowAttribute(Handle,
                    NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
            }
            catch { }
        }

        // ---------------- 侧边导航 ----------------

        private void BuildSidebar()
        {
            var sb = new Panel
            {
                Location = Point.Empty,
                Size = new Size(180, ClientSize.Height),
                BackColor = SidebarBg
            };
            sb.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor))
                    e.Graphics.DrawLine(p, sb.Width - 1, 0, sb.Width - 1, sb.Height);
            };
            Controls.Add(sb);

            var logo = new Label
            {
                Text = "快截 SnapCut",
                Location = new Point(18, 16),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
                ForeColor = TextMain
            };
            sb.Controls.Add(logo);

            var ver = new Label
            {
                Text = Ui.AppVersion,
                Location = new Point(19, 44),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                ForeColor = TextSub
            };
            sb.Controls.Add(ver);

            string[] items = { "通用", "截图", "录屏", "长截图", "快捷键" };
            int y = 84;
            for (int i = 0; i < items.Length; i++)
            {
                var nav = new NavItem(items[i]) { Location = new Point(12, y) };
                int idx = i;
                nav.Click += delegate { ShowPage(idx); };
                sb.Controls.Add(nav);
                _navs.Add(nav);
                y += 44;
            }

            // 侧栏底部：更新日志（链接样式）
            var log = new Label
            {
                Text = "更新日志",
                Location = new Point(20, ClientSize.Height - 36),
                AutoSize = true,
                ForeColor = Accent,
                Cursor = Cursors.Hand
            };
            log.Click += delegate { OpenChangelog(); };
            sb.Controls.Add(log);
        }

        /// <summary>切换到第 i 个分组：内容页与导航胶囊高亮联动。</summary>
        private void ShowPage(int i)
        {
            for (int k = 0; k < _pages.Count; k++) _pages[k].Visible = k == i;
            for (int k = 0; k < _navs.Count; k++) { _navs[k].Active = k == i; _navs[k].Invalidate(); }
        }

        // ---------------- 内容页 ----------------

        private void BuildContent(Settings s)
        {
            _content.Location = new Point(180, 0);
            _content.Size = new Size(ClientSize.Width - 180, ClientSize.Height - 62);
            _content.BackColor = ContentBg;
            Controls.Add(_content);

            // ---- 通用 ----
            var p0 = NewPage();
            PageTitle(p0, "通用", "基础行为");
            SectionHeader(p0, "启动与常驻", 84);
            _runAtStartup = AddCheck(p0, "开机自动启动（登录 Windows 后驻留托盘）", s.RunAtStartup, 112);
            _showBar = AddCheck(p0, "显示桌面悬浮窗（贴边窄条，悬停展开截图 / 录屏等功能）", s.ShowFloatingBar, 142);
            AddHint(p0, "关闭后可随时双击托盘图标重新打开悬浮窗。", 172);

            SectionHeader(p0, "关于", 214);
            p0.Controls.Add(new Label
            {
                Text = AppMeta.AppName + " " + AppMeta.AppNameEn + "  " + AppMeta.VersionText,
                Location = new Point(26, 240), AutoSize = true, ForeColor = TextMain
            });
            var aboutHint = AddHint(p0,
                "免费、绿色的截图 / 长截图 / 录屏 / 视频转换工具。\r\n" +
                "截图默认保存到系统的\"图片\"文件夹，可在\"截图\"页修改；\r\n" +
                "视频保存位置与画质在\"录屏\"页调整。\r\n\r\n" +
                AppMeta.ContactText, 264);
            int linkY = aboutHint.Bottom + 16;

            var changelog = new Label
            {
                Text = "查看更新日志",
                Location = new Point(26, linkY), AutoSize = true,
                ForeColor = Accent, Cursor = Cursors.Hand
            };
            changelog.Click += delegate { OpenChangelog(); };
            p0.Controls.Add(changelog);

            var checkUpd = new Label
            {
                Text = "检查更新",
                Location = new Point(140, linkY), AutoSize = true,
                ForeColor = Accent, Cursor = Cursors.Hand
            };
            checkUpd.Click += delegate { CheckUpdateNow(); };
            p0.Controls.Add(checkUpd);

            // ---- 截图 ----
            var p1 = NewPage();
            PageTitle(p1, "截图", "保存位置与剪贴板");
            _saveFolder = AddFolderRow(p1, "图片保存位置", s.SaveFolder, 76);
            _copyAfterSave = AddCheck(p1, "保存后同时复制到剪贴板", s.CopyAfterSave, 116);

            // ---- 录屏 ----
            var p2 = NewPage();
            PageTitle(p2, "录屏", "画质与录制内容");
            SectionHeader(p2, "保存位置", 84);
            _videoFolder = AddFolderRow(p2, "视频保存位置", s.VideoFolder, 108);

            SectionHeader(p2, "画质与声音", 148);
            _fps = AddNumber(p2, "帧率 FPS", s.Fps, 5, 60, 26, 174);
            _jpeg = AddNumber(p2, "画质 (1-100)", s.JpegQuality, 20, 100, 256, 174);
            _captureCursor = AddCheck(p2, "录制鼠标指针", s.CaptureCursor, 204);
            _recordAudio = AddCheck(p2, "录制电脑声音（系统声音）", s.RecordAudio, 234);

            SectionHeader(p2, "标注与提示", 274);
            _showBorder = AddCheck(p2, "显示录制边框（不会被录入视频）", s.ShowRecordBorder, 300);
            _highlightMouse = AddCheck(p2, "标注鼠标点击 / 滚动", s.HighlightMouse, 330);

            SectionHeader(p2, "完成后", 370);
            _openFolder = AddCheck(p2, "在文件夹中定位视频", s.OpenFolderAfterRecord, 396);

            // ---- 长截图 ----
            var p3 = NewPage();
            PageTitle(p3, "长截图", "滚动拼接");
            SectionHeader(p3, "自动滚动", 84);
            _scrollDelay = AddNumber(p3, "滚动间隔 (ms)", s.ScrollDelayMs, 100, 2000, 26, 110);
            _scrollStep = AddNumber(p3, "滚动幅度 (px)", s.ScrollStep, 100, 1200, 256, 110);
            _scrollMethod = AddCombo(p3, "滚动方式",
                new[] { "自动（失败逐级回退）", "滚动条消息", "真实滚轮", "PageDown", "方向键" },
                s.ScrollMethod, 26, 146);
            AddHint(p3, "自动模式下按此间隔向页面发送滚动；幅度越大拼得越快，但过快\n" +
                        "容易跳过内容。若自动方式无效，可手动指定滚动方式。", 178);

            SectionHeader(p3, "使用技巧", 232);
            AddHint(p3,
                "入口：悬浮窗或托盘菜单进入长截图，框选区域后滚动鼠标滚轮\n" +
                "拼接，点“完成”生成长图；滚多了可直接往回滚。\n\n" +
                "粘性页头 / 页脚会自动识别剔除；建议匀速滚动、滚一段停一下。\n" +
                "也可点控制条上的“自动滚动”由程序代滚，到底会自动收图。", 258);

            // ---- 快捷键 ----
            var p4 = NewPage();
            PageTitle(p4, "快捷键", "全局热键");
            _shotKey = AddHotkey(p4, "截图", s.ShotMods, s.ShotKey,
                NativeMethods.MOD_ALT, 0x41, 26, 76);
            _recKey = AddHotkey(p4, "录屏", s.RecordMods, s.RecordKey,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x52, 26, 120);
            AddHint(p4, "点击录入框后按下新组合键；按 Delete 恢复默认", 164);
        }

        /// <summary>新建一个内容页（初始隐藏，由 ShowPage 控制显隐）。</summary>
        private Panel NewPage()
        {
            var p = new Panel
            {
                Location = Point.Empty,
                Size = _content.Size,
                BackColor = ContentBg,
                Visible = false
            };
            _content.Controls.Add(p);
            _pages.Add(p);
            return p;
        }

        /// <summary>内容页左上角的分组大标题 + 灰色副标题。</summary>
        private static void PageTitle(Panel p, string title, string sub)
        {
            p.Controls.Add(new Label
            {
                Text = title,
                Location = new Point(26, 22),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
                ForeColor = TextMain
            });
            p.Controls.Add(new Label
            {
                Text = sub,
                Location = new Point(27, 52),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                ForeColor = TextSub
            });
        }

        /// <summary>内容页内的分组小标题（比 PageTitle 小一级，用于页内分区）。</summary>
        private static void SectionHeader(Panel p, string text, int top)
        {
            p.Controls.Add(new Label
            {
                Text = text,
                Location = new Point(26, top),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                ForeColor = TextMain
            });
        }

        // ---------------- 底部按钮 ----------------

        private void BuildBottomBar()
        {
            int by = ClientSize.Height - 62;
            var sep = new Panel
            {
                Location = new Point(180, by),
                Size = new Size(ClientSize.Width - 180, 1),
                BackColor = BorderColor
            };
            Controls.Add(sep);

            var ok = Pill("确定", DarkPill, Color.White, Color.Empty);
            ok.Location = new Point(ClientSize.Width - 16 - 88, by + 15);
            ok.Click += delegate { if (Apply()) DialogResult = DialogResult.OK; };
            Controls.Add(ok);

            var cancel = Pill("取消", Color.White, TextMain, BorderColor);
            cancel.Location = new Point(ClientSize.Width - 16 - 88 - 8 - 80, by + 15);
            cancel.Width = 80;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>胶囊按钮（圆角矩形，参考截图的黑底白字主按钮样式）。</summary>
        private static Button Pill(string text, Color back, Color fore, Color border)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(88, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = fore,
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };
            b.FlatAppearance.BorderSize = border == Color.Empty ? 0 : 1;
            b.FlatAppearance.BorderColor = border;
            b.FlatAppearance.MouseOverBackColor =
                back == Color.White ? PillHover : ControlPaint.Light(back);
            b.HandleCreated += delegate
            {
                using (var gp = Rounded(new Rectangle(0, 0, b.Width, b.Height), 10))
                    b.Region = new Region(gp);
            };
            return b;
        }

        private static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var gp = new GraphicsPath();
            gp.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            gp.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            gp.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            gp.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        private void OpenChangelog()
        {
            using (var f = new ChangelogForm())
                f.ShowDialog(this);
        }

        /// <summary>手动检查更新：有新版则弹更新窗，否则提示已是最新 / 失败原因。</summary>
        private void CheckUpdateNow()
        {
            UpdateChecker.MarkChecked();
            UpdateChecker.CheckAsync(delegate(UpdateInfo info, string err)
            {
                BeginInvoke(new Action(delegate
                {
                    if (info != null)
                    {
                        using (var f = new UpdateForm(info))
                            f.ShowDialog(this);
                        return;
                    }
                    MessageBox.Show(this,
                        string.IsNullOrEmpty(err)
                            ? "已是最新版本（" + AppMeta.VersionText + "）。"
                            : "检查更新失败：\r\n" + err,
                        "快截", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
            });
        }

        /// <summary>校验并写回设置。校验失败（热键冲突）时提示并返回 false，窗体保持打开。</summary>
        private bool Apply()
        {
            if (_shotKey.Mods == _recKey.Mods && _shotKey.Key == _recKey.Key)
            {
                MessageBox.Show(this, "截图热键与录屏热键相同，请设置为不同的组合。",
                    "快截", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            var s = Settings.Current;
            s.RunAtStartup = _runAtStartup.Checked;
            s.ShowFloatingBar = _showBar.Checked;
            s.SaveFolder = _saveFolder.Text.Trim();
            s.VideoFolder = _videoFolder.Text.Trim();
            s.CopyAfterSave = _copyAfterSave.Checked;
            s.CaptureCursor = _captureCursor.Checked;
            s.ShowRecordBorder = _showBorder.Checked;
            s.RecordAudio = _recordAudio.Checked;
            s.HighlightMouse = _highlightMouse.Checked;
            s.OpenFolderAfterRecord = _openFolder.Checked;
            s.Fps = (int)_fps.Value;
            s.JpegQuality = (int)_jpeg.Value;
            s.ScrollDelayMs = (int)_scrollDelay.Value;
            s.ScrollStep = (int)_scrollStep.Value;
            s.ScrollMethod = _scrollMethod != null ? _scrollMethod.SelectedIndex : 0;
            s.ShotMods = _shotKey.Mods; s.ShotKey = _shotKey.Key;
            s.RecordMods = _recKey.Mods; s.RecordKey = _recKey.Key;
            return true;
        }

        // ---------------- 控件辅助 ----------------

        /// <summary>添加一行“文件夹选择”：标签 + 文本框 + 浏览按钮。</summary>
        private TextBox AddFolderRow(Control parent, string caption, string value, int top)
        {
            var l = new Label { Text = caption, Location = new Point(26, top + 4), AutoSize = true };
            parent.Controls.Add(l);
            var box = new TextBox
            {
                Text = value,
                Location = new Point(150, top),
                Width = 240
            };
            parent.Controls.Add(box);
            var browse = new Button
            {
                Text = "浏览…",
                Location = new Point(398, top - 1),
                Width = 62,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White
            };
            browse.FlatAppearance.BorderColor = BorderColor;
            browse.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.SelectedPath = box.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
                }
            };
            parent.Controls.Add(browse);
            return box;
        }

        /// <summary>添加一个数值项：标签 + 数字框（left 为列起点）。</summary>
        private NumericUpDown AddNumber(Control parent, string caption,
            int value, int min, int max, int left, int top)
        {
            var l = new Label { Text = caption, Location = new Point(left, top + 3), AutoSize = true };
            parent.Controls.Add(l);
            var num = new NumericUpDown
            {
                Location = new Point(left + 120, top),
                Width = 88,
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, value))
            };
            parent.Controls.Add(num);
            return num;
        }

        /// <summary>添加一个下拉选择：标签 + 只读下拉框。</summary>
        private static ComboBox AddCombo(Control parent, string caption,
            string[] items, int selected, int left, int top)
        {
            var l = new Label { Text = caption, Location = new Point(left, top + 3), AutoSize = true };
            parent.Controls.Add(l);
            var cb = new ComboBox
            {
                Location = new Point(left + 120, top),
                Width = 168,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            cb.Items.AddRange(items);
            cb.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, selected));
            parent.Controls.Add(cb);
            return cb;
        }

        /// <summary>添加一个复选框。</summary>
        private CheckBox AddCheck(Control parent, string caption, bool value, int top)
        {
            var cb = new CheckBox
            {
                Text = caption,
                Location = new Point(26, top),
                AutoSize = true,
                Checked = value
            };
            parent.Controls.Add(cb);
            return cb;
        }

        /// <summary>添加一行热键录入：标签 + 录入框。</summary>
        private HotkeyBox AddHotkey(Control parent, string caption, uint mods, uint key,
            uint defMods, uint defKey, int left, int top)
        {
            var l = new Label { Text = caption, Location = new Point(left, top + 4), AutoSize = true };
            parent.Controls.Add(l);
            var box = new HotkeyBox
            {
                Location = new Point(left + 50, top),
                Width = 180,
                DefMods = defMods,
                DefKey = defKey
            };
            box.SetHotkey(mods, key);
            parent.Controls.Add(box);
            return box;
        }

        /// <summary>添加灰色小字提示（支持多行）。</summary>
        private Label AddHint(Control parent, string text, int top)
        {
            var l = new Label
            {
                Text = text,
                Location = new Point(26, top),
                AutoSize = true,
                MaximumSize = new Size(parent.ClientSize.Width - 52, 0),
                ForeColor = TextSub,
                Font = new Font("Microsoft YaHei UI", 8.5f)
            };
            parent.Controls.Add(l);
            return l;
        }

        // ---------------- 导航胶囊项 ----------------

        /// <summary>侧栏导航项：圆角胶囊高亮当前/悬停状态，类似现代应用的菜单样式。</summary>
        private sealed class NavItem : Control
        {
            private bool _hover;
            public bool Active;

            public NavItem(string text)
            {
                Text = text;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);   // 不抢焦点，点击只切换页面
                Size = new Size(156, 36);
                Cursor = Cursors.Hand;
                Font = new Font("Microsoft YaHei UI", 9.5f);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hover = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hover = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(SidebarBg);

                if (Active || _hover)
                {
                    var pill = new Rectangle(4, 3, Width - 8, Height - 6);
                    using (var gp = Rounded(pill, 10))
                    using (var br = new SolidBrush(Active ? PillActive : PillHover))
                        g.FillPath(br, gp);
                }

                var f = Active ? new Font(Font, FontStyle.Bold) : Font;
                TextRenderer.DrawText(g, Text, f,
                    new Rectangle(18, 0, Width - 22, Height),
                    TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                if (Active) f.Dispose();
            }
        }

        // ---------------- 热键录入框 ----------------

        /// <summary>
        /// 热键录入框：只读文本框，获得焦点后按下的第一个“修饰键+普通键”组合
        /// 即被捕获为新的全局热键；Delete/Backspace 恢复默认。
        /// 不带修饰键的单键会被拒绝（否则全局热键会拦截正常键盘输入）。
        /// </summary>
        private sealed class HotkeyBox : TextBox
        {
            public uint Mods { get; private set; }
            public uint Key { get; private set; }
            public uint DefMods, DefKey;

            public HotkeyBox()
            {
                ReadOnly = true;
                TextAlign = HorizontalAlignment.Center;
                Cursor = Cursors.Hand;
                BackColor = Color.White;
            }

            public void SetHotkey(uint mods, uint key)
            {
                Mods = mods;
                Key = key;
                Text = Settings.HotkeyText(mods, key);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                e.SuppressKeyPress = true;

                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
                {
                    SetHotkey(DefMods, DefKey);
                    return;
                }
                // 单独的修饰键不构成热键，忽略
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey ||
                    e.KeyCode == Keys.Menu || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
                    return;

                uint mods = 0;
                if (e.Control) mods |= NativeMethods.MOD_CONTROL;
                if (e.Alt) mods |= NativeMethods.MOD_ALT;
                if (e.Shift) mods |= NativeMethods.MOD_SHIFT;
                if (mods == 0) return; // 拒绝无修饰键的热键，避免拦截正常输入

                SetHotkey(mods, (uint)(e.KeyCode & Keys.KeyCode));
            }
        }
    }
}
