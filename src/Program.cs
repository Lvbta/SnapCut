using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>What the capture overlay should do when the user confirms a region.</summary>
    internal enum CaptureMode { Screenshot, Record, LongShot }

    /// <summary>
    /// 常驻控制器（隐形窗口）+ 侧边悬浮窗 + 托盘图标。
    ///   - 本窗体永不显示，仅作为热键消息窗口；常驻 UI 是贴屏缘的悬浮窗：
    ///     平时收缩为窄条，鼠标悬浮展开为功能按钮列（截图/长截图/录屏/编辑/视频转换/设置/关闭）。
    ///   - 悬浮窗可按住拖到任意位置，松手自动吸附最近的屏缘（上下左右；位置与方向持久化）；
    ///     "关闭"隐藏后可双击托盘图标恢复。
    ///   - Alt+A（截图）/ Ctrl+Alt+R（录屏）热键可用；托盘菜单提供全部入口。
    /// </summary>
    internal sealed class MainContext : Form
    {
        private const int HK_SHOT = 1;
        private const int HK_RECORD = 2;

        private NotifyIcon _tray;
        private FloatingBar _bar;
        private bool _busy;

        // 供其他模块在后台弹出非阻断性系统提示
        private static NotifyIcon _sharedTray;

        /// <summary>弹出托盘气泡提示（音频初始化失败等非阻断警告使用）。</summary>
        internal static void Balloon(string title, string message, ToolTipIcon icon)
        {
            try { if (_sharedTray != null) _sharedTray.ShowBalloonTip(4000, title, message, icon); }
            catch { }
        }

        public MainContext()
        {
            Text = "SnapCut";
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;

            SetupBar();
            SetupTray();
            RegisterHotkeys();
            ScheduleUpdateCheck();
            try { Settings.ApplyStartup(Settings.Current.RunAtStartup); } catch { }
        }

        protected override void SetVisibleCore(bool value)
        {
            // 控制器窗口永不显示（Application.Run 需要一个消息窗体）
            base.SetVisibleCore(false);
        }

        // ---- 悬浮窗 ----

        private void SetupBar()
        {
            _bar = new FloatingBar(this);
            _bar.Show();
            if (!Settings.Current.ShowFloatingBar) _bar.Hide();
        }

        /// <summary>托盘双击：显示 / 隐藏悬浮窗。</summary>
        private void ToggleBar()
        {
            if (_bar == null || _bar.IsDisposed) return;
            if (_bar.Visible) _bar.Hide();
            else _bar.Show();
        }

        /// <summary>悬浮窗"关闭"按钮：隐藏悬浮窗并提示恢复方式（不改设置，托盘双击即可恢复）。</summary>
        private void CloseBar()
        {
            if (_bar != null) _bar.Hide();
            Balloon("快截", "悬浮窗已关闭，双击托盘图标可重新打开。", ToolTipIcon.Info);
        }

        // ---- 抠图 ----

        /// <summary>
        /// 独立抠图：选择图片 → AI 去背景（plugins\sam）→ 保存透明 PNG 并复制到剪贴板。
        /// </summary>
        private void MatteImage()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要抠图的图片";
                dlg.Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*";
                string dir = Settings.Current.SaveFolder;
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                if (dlg.ShowDialog() != DialogResult.OK) return;

                var engine = OnnxMatting.Get();
                if (engine == null)
                {
                    MessageBox.Show("抠图功能未就绪：plugins\\sam\\ 下需要 encoder.onnx 和 decoder.onnx。\r\n" +
                                    "详见 plugins\\MODEL_GUIDE.md。", "快截",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Bitmap src;
                try { src = new Bitmap(dlg.FileName); }
                catch (Exception ex)
                {
                    MessageBox.Show("无法打开图片：" + ex.Message, "快截",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // 打开交互式抠图编辑窗：标记前景 / 背景 → 自动重抠 → 画笔精修 → 保存 + 复制
                Bitmap edited;
                using (var f = new MattingForm(src))
                {
                    f.ShowDialog();
                    edited = f.Result;
                }

                string file = null;
                try
                {
                    Directory.CreateDirectory(dir);
                    file = Path.Combine(dir, "抠图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                    edited.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
                catch { file = null; }
                try { Clipboard.SetImage(edited); } catch { }
                edited.Dispose();
                src.Dispose();
                Balloon("快截 抠图完成",
                    file != null ? "已保存并复制到剪贴板：\r\n" + file : "已复制到剪贴板。",
                    ToolTipIcon.Info);
            }
        }

        // ---- 在线更新 ----

        private UpdateInfo _pendingUpdate;
        private UpdateForm _updateForm;

        /// <summary>
        /// 每次启动后台检查一次更新。发现新版本**自动弹出更新窗并开始下载（带进度条）**，
        /// 无需用户点击；下载完成后自动静默安装并退出以完成升级。
        /// </summary>
        private void ScheduleUpdateCheck()
        {
            try
            {
                if (!UpdateChecker.ShouldAutoCheck()) return;
                UpdateChecker.MarkChecked();
                UpdateChecker.CheckAsync(delegate(UpdateInfo info, string err)
                {
                    if (info == null) return;
                    BeginInvoke(new Action(delegate { OpenUpdateForm(info); }));
                });
            }
            catch { }
        }

        /// <summary>打开更新窗（非模态，避免阻塞软件使用）；已打开则置顶。</summary>
        private void OpenUpdateForm(UpdateInfo info)
        {
            if (info == null) return;
            if (_updateForm != null && !_updateForm.IsDisposed)
            {
                _updateForm.BringToFront();
                return;
            }
            _pendingUpdate = info;
            _updateForm = new UpdateForm(info);
            _updateForm.FormClosed += delegate { _updateForm = null; };
            _updateForm.Show();
        }

        // ---- 视频转换 ----

        /// <summary>
        /// 视频格式转换：打开转换窗，批量选文件 → 选目标容器 → 优先无损流拷贝。
        /// </summary>
        private void ConvertVideo()
        {
            using (var f = new VideoConvertForm())
                f.ShowDialog();
        }

        // ---- 托盘 ----

        private void SetupTray()
        {
            var menu = new ContextMenuStrip();
            var shotItem = menu.Items.Add("", null, delegate { StartCapture(CaptureMode.Screenshot); });
            menu.Items.Add("长截图  Scrolling shot", null, delegate { StartCapture(CaptureMode.LongShot); });
            menu.Items.Add("录屏  Record screen", null, delegate { StartCapture(CaptureMode.Record); });
            menu.Items.Add("视频转换  Video convert…", null, delegate { ConvertVideo(); });
            menu.Items.Add("编辑图片  Edit image…", null, delegate { OpenImageEditor(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("显示/隐藏悬浮窗  Floating bar", null, delegate { ToggleBar(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("设置  Settings...", null, delegate { OpenSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于  About…", null, delegate { OpenChangelog(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出  Exit", null, delegate { Application.Exit(); });

            menu.Opened += delegate
            {
                var s = Settings.Current;
                shotItem.Text = "截图  (" + Settings.HotkeyText(s.ShotMods, s.ShotKey) + ")";
            };

            _tray = new NotifyIcon();
            _tray.Icon = CreateTrayIcon();
            _tray.Text = "快截 截图 / 录屏工具  (双击显示/隐藏悬浮窗)";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ToggleBar(); };
            _tray.BalloonTipClicked += delegate { if (_pendingUpdate != null) OpenUpdateForm(_pendingUpdate); };
            _sharedTray = _tray;
        }

        private void OpenSettings()
        {
            using (var f = new SettingsForm())
            {
                if (f.ShowDialog() == DialogResult.OK)
                {
                    Settings.Current.Save();
                    RegisterHotkeys();
                    try { Settings.ApplyStartup(Settings.Current.RunAtStartup); } catch { }
                    if (_bar != null && !_bar.IsDisposed)
                    {
                        if (Settings.Current.ShowFloatingBar) _bar.Show();
                        else _bar.Hide();
                    }
                }
            }
        }

        private void OpenChangelog()
        {
            using (var f = new ChangelogForm())
                f.ShowDialog();
        }

        private void OpenImageEditor()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要编辑的图片";
                dlg.Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*";
                string dir = Settings.Current.SaveFolder;
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    using (var src = new Bitmap(dlg.FileName))
                        new ImageEditorForm((Bitmap)src.Clone()).Show();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("无法打开图片：" + ex.Message, "快截",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>运行时绘制托盘/标题栏图标（绿色裁剪框），供各对话框复用。</summary>
        internal static Icon CreateTrayIcon()
        {
            return CreateAppIcon(16);
        }

        /// <summary>
        /// 绘制应用图标（绿色裁剪框），可指定尺寸：16 用于托盘 / 标题栏，
        /// 256 用于导出 .ico（供 exe、安装包、快捷方式使用），保证各处同一套图案。
        /// </summary>
        internal static Icon CreateAppIcon(int size)
        {
            return AppIcon.Create(size);   // 统一实现在 UiKit，避免多处各画一份
        }

        internal static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var gp = new GraphicsPath();
            gp.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            gp.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            gp.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            gp.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        /// <summary>左侧圆角、右侧直角（贴屏边）的窗体轮廓。</summary>
        internal static GraphicsPath LeftRounded(Rectangle r, int rad)
        {
            return EdgeRounded(r, rad, 1);
        }

        /// <summary>右侧圆角、左侧直角（贴左屏缘）的窗体轮廓。</summary>
        internal static GraphicsPath RightRounded(Rectangle r, int rad)
        {
            return EdgeRounded(r, rad, 0);
        }

        /// <summary>
        /// 贴屏缘的窗体轮廓：side 指定贴边（直角）的一侧，0=左 1=右 2=上 3=下，
        /// 其对侧两角圆角。注意 GDI+ 的角度以 y 轴向下为正方向顺时针度量，
        /// 且路径会自动用直线连接不相邻的点 —— 各段必须严格首尾相接地构造，
        /// 否则会出现对角切割导致窗体显示不全（实测复现）。
        /// </summary>
        internal static GraphicsPath EdgeRounded(Rectangle r, int rad, int side)
        {
            var gp = new GraphicsPath();
            switch (side)
            {
                case 0:   // 贴左缘：左缘直角，右两角圆角
                    gp.AddLine(r.X, r.Y, r.Right - rad, r.Y);                       // 顶边
                    gp.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);   // 右上圆角 → (r.Right, r.Y+rad)
                    gp.AddLine(r.Right, r.Y + rad, r.Right, r.Bottom - rad);        // 右缘向下
                    gp.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);  // 右下圆角 → (r.Right-rad, r.Bottom)
                    gp.AddLine(r.Right - rad, r.Bottom, r.X, r.Bottom);             // 底边
                    break;
                case 1:   // 贴右缘：右缘直角，左两角圆角
                    gp.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);                 // 左上圆角 → (r.X+rad, r.Y)
                    gp.AddLine(r.Right, r.Y, r.Right, r.Bottom);                    // 顶边 + 右缘向下
                    gp.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);   // 底边 + 左下圆角 → (r.X, r.Bottom-rad)
                    break;
                case 2:   // 贴上缘：上缘直角，下两角圆角
                    gp.AddLine(r.X, r.Y, r.Right, r.Y);                             // 顶边
                    gp.AddLine(r.Right, r.Y, r.Right, r.Bottom - rad);              // 右缘向下
                    gp.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);  // 右下圆角 → (r.Right-rad, r.Bottom)
                    gp.AddLine(r.Right - rad, r.Bottom, r.X + rad, r.Bottom);       // 底边
                    gp.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);   // 左下圆角 → (r.X, r.Bottom-rad)
                    break;
                default:  // 贴下缘：下缘直角，上两角圆角
                    gp.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);                 // 左上圆角 → (r.X+rad, r.Y)
                    gp.AddLine(r.X + rad, r.Y, r.Right - rad, r.Y);                 // 顶边
                    gp.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);   // 右上圆角 → (r.Right, r.Y+rad)
                    gp.AddLine(r.Right, r.Y + rad, r.Right, r.Bottom);              // 右缘向下
                    gp.AddLine(r.Right, r.Bottom, r.X, r.Bottom);                   // 底边
                    break;
            }
            gp.CloseFigure();
            return gp;
        }

        private void RegisterHotkeys()
        {
            var s = Settings.Current;
            NativeMethods.UnregisterHotKey(Handle, HK_SHOT);
            NativeMethods.UnregisterHotKey(Handle, HK_RECORD);
            bool shotOk = NativeMethods.RegisterHotKey(Handle, HK_SHOT, s.ShotMods, s.ShotKey);
            bool recOk = NativeMethods.RegisterHotKey(Handle, HK_RECORD, s.RecordMods, s.RecordKey);
            if (!shotOk || !recOk)
            {
                var sb = new System.Text.StringBuilder();
                if (!shotOk) sb.AppendLine("截图热键 " + Settings.HotkeyText(s.ShotMods, s.ShotKey) + " 已被其他程序占用。");
                if (!recOk) sb.AppendLine("录屏热键 " + Settings.HotkeyText(s.RecordMods, s.RecordKey) + " 已被其他程序占用。");
                sb.Append("可在\"设置\"中修改热键，或直接使用托盘菜单。");
                _tray.ShowBalloonTip(4000, "快截 热键冲突", sb.ToString(), ToolTipIcon.Warning);
            }
        }

        private void StartCapture(CaptureMode mode)
        {
            if (_busy)
            {
                Balloon("快截", "当前有进行中的截图 / 录屏任务，请先完成或取消。", ToolTipIcon.Info);
                return;
            }
            _busy = true;
            try
            {
                using (var overlay = new OverlayForm(mode))
                {
                    overlay.ShowDialog();
                    var region = overlay.RecordRegion;
                    if (region.HasValue)
                    {
                        var rec = new RecorderBar(region.Value);
                        rec.FormClosed += delegate { _busy = false; };
                        rec.Show();
                        return;
                    }
                    var longRegion = overlay.LongShotRegion;
                    if (longRegion.HasValue)
                    {
                        RunLongShot(longRegion.Value);
                    }
                    else
                    {
                        _busy = false;
                    }
                }
            }
            finally
            {
                if (!(_busy && Application.OpenForms.Count > 1))
                {
                    _busy = false;
                }
            }
        }

        private void RunLongShot(Rectangle region)
        {
            var session = new LongShotSession(region);
            session.Completed += (Bitmap result) =>
            {
                if (IsDisposed) { if (result != null) result.Dispose(); return; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _busy = false;
                        if (result != null)
                            using (var preview = new LongShotPreview(result))
                                preview.ShowDialog();
                    });
                }
                catch (InvalidOperationException) { if (result != null) result.Dispose(); }
            };
            session.Show();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HK_SHOT) StartCapture(CaptureMode.Screenshot);
                else if (id == HK_RECORD) StartCapture(CaptureMode.Record);
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                NativeMethods.UnregisterHotKey(Handle, HK_SHOT);
                NativeMethods.UnregisterHotKey(Handle, HK_RECORD);
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                _sharedTray = null;
                if (_bar != null) _bar.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------------- 侧边悬浮窗 ----------------

          /// <summary>
          /// 贴屏缘的悬浮窗：收缩态为窄条，鼠标悬浮展开为功能按钮列，移开鼠标约半秒后自动收回。
          /// 按住可拖到任意位置，松手自动吸附最近的屏缘（上下左右四选一；上下吸附时为横向条）。
          /// </summary>
        internal sealed class FloatingBar : Form
        {
            private sealed class BarBtn
            {
                public string Text;
                public IconKind Glyph;
                public Color Accent;
                public EventHandler Action;
                public BarBtn(string text, FloatingBar.IconKind glyph, Color accent, EventHandler action)
                { Text = text; Glyph = glyph; Accent = accent; Action = action; }
            }

            internal enum IconKind { Camera, Scroller, Video, Photo, Convert, Scissors, Gear, Close }

            private static readonly Font LabelFont = new Font("Microsoft YaHei UI", 8f);

            private const int BarW = 64, BtnH = 56, BarPad = 8, TabW = 14, TabH = 110;

            private readonly MainContext _owner;
            private readonly BarBtn[] _btns;
            private readonly System.Windows.Forms.Timer _collapseTimer;
            private bool _expanded;
            private bool _dragging;
            private bool _pressing;      // 左键按下中（尚未超过拖动阈值）
            private Point _dragCursor;   // 按下时光标屏幕坐标
            private Point _dragForm;     // 按下时窗体位置
            private int _dockSide;       // 吸附的屏缘：0=左 1=右 2=上 3=下
            private int _anchorY;        // 左右吸附时中心的纵坐标
            private int _anchorX;        // 上下吸附时中心的横坐标
            private int _hoverBtn = -1;
            private bool _layered;          // 是否启用分层窗口（每像素 Alpha，圆角真正抗锯齿）
            private Bitmap _layer;          // 最近一次渲染的位图，用于命中测试时判断透明区

            public FloatingBar(MainContext owner)
            {
                _owner = owner;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.White;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

                var s = Settings.Current;
                _dockSide = Math.Max(0, Math.Min(3, s.BallSide));
                var wa = Screen.PrimaryScreen.WorkingArea;
                _anchorY = s.BallY > 0 ? s.BallY : wa.Bottom - 260;
                _anchorX = s.BallX > 0 ? s.BallX : wa.Right - 260;

                var btns = new List<BarBtn>
                {
                    new BarBtn("截图", IconKind.Camera, Color.FromArgb(7, 193, 96),
                        delegate { owner.StartCapture(CaptureMode.Screenshot); }),
                    new BarBtn("长截图", IconKind.Scroller, Color.FromArgb(0, 120, 215),
                        delegate { owner.StartCapture(CaptureMode.LongShot); }),
                    new BarBtn("录屏", IconKind.Video, Color.FromArgb(220, 80, 80),
                        delegate { owner.StartCapture(CaptureMode.Record); }),
                    new BarBtn("编辑图片", IconKind.Photo, Color.FromArgb(240, 150, 40),
                        delegate { owner.OpenImageEditor(); }),
                    new BarBtn("视频转换", IconKind.Convert, Color.FromArgb(150, 90, 220),
                        delegate { owner.ConvertVideo(); }),
                    new BarBtn("设置", IconKind.Gear, Color.FromArgb(130, 132, 138),
                        delegate { owner.OpenSettings(); }),
                    new BarBtn("关闭", IconKind.Close, Color.FromArgb(160, 162, 168),
                        delegate { owner.CloseBar(); })
                };
                // 智能抠图：入口默认隐藏（功能暂停推进，代码保留待后续开发）。
                // 在 config.ini 里把 MattingEnabled 设为 True 即可恢复该按钮。
                if (Settings.Current.MattingEnabled)
                    btns.Insert(btns.Count - 2, new BarBtn("抠图", IconKind.Scissors,
                        Color.FromArgb(120, 100, 200), delegate { owner.MatteImage(); }));
                _btns = btns.ToArray();

                _collapseTimer = new System.Windows.Forms.Timer { Interval = 450 };
                _collapseTimer.Tick += delegate { _collapseTimer.Stop(); Collapse(); };

                ApplyState();
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ApplyCaptureAffinity();
                // 启用分层窗口（每像素 Alpha）→ 圆角边缘真正抗锯齿。失败则退回 Region 方案。
                try
                {
                    int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
                    NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE,
                        ex | NativeMethods.WS_EX_LAYERED);
                    _layered = true;
                    Region = null;
                    UpdateLayered();
                }
                catch
                {
                    _layered = false;
                    Region = null;
                    UpdateShape();
                }
            }

            /// <summary>
            /// 是否把自己从屏幕捕获中排除。排除后悬浮窗不会污染用户的截图 / 录屏，
            /// 但代价是：它在截图、录屏、远程桌面中会渲染成黑色块（系统行为）。
            /// 需要把悬浮窗展示给别人时，可在设置里关掉（Settings.BarExcludeFromCapture）。
            /// </summary>
            internal void ApplyCaptureAffinity()
            {
                if (!IsHandleCreated) return;
                try
                {
                    uint aff = Settings.Current.BarExcludeFromCapture
                        ? NativeMethods.WDA_EXCLUDEFROMCAPTURE
                        : NativeMethods.WDA_NONE;
                    NativeMethods.SetWindowDisplayAffinity(Handle, aff);
                }
                catch { }
            }

            private int ExpandedH { get { return BarPad * 2 + BtnH * _btns.Length; } }   // 纵向展开高
            private int ExpandedW { get { return BarPad * 2 + BtnH * _btns.Length; } }   // 横向展开宽
            private bool Horizontal { get { return _dockSide >= 2; } }

            /// <summary>悬浮窗中心当前所在的屏幕（多显示器下吸附到副屏后不会被拉回主屏）。</summary>
            private Screen DockScreen()
            {
                var center = new Point(Location.X + Width / 2,
                    Math.Max(Math.Min(Location.Y + Height / 2, 50000), -50000));
                return Screen.FromPoint(center);
            }

            /// <summary>按收缩/展开状态设置尺寸、贴边位置与窗体轮廓（贴屏缘一侧直角）。</summary>
            private void ApplyState()
            {
                var wa = DockScreen().WorkingArea;
                if (Horizontal)
                {
                    _anchorX = Math.Max(wa.Left + 80, Math.Min(_anchorX, wa.Right - 80));
                    // 上下吸附：横向条（展开时按钮排成一行）
                    Size = _expanded ? new Size(ExpandedW, BarW) : new Size(TabH, TabW);
                    int x = Math.Max(wa.Left, Math.Min(_anchorX - Width / 2, wa.Right - Width));
                    Location = new Point(x, _dockSide == 2 ? wa.Top : wa.Bottom - Height);
                    UpdateShape();
                }
                else
                {
                    _anchorY = Math.Max(wa.Top + 80, Math.Min(_anchorY, wa.Bottom - 80));
                    // 左右吸附：纵向条（展开时按钮排成一列）
                    Size = _expanded ? new Size(BarW, ExpandedH) : new Size(TabW, TabH);
                    Location = new Point(_dockSide == 0 ? wa.Left : wa.Right - Width, _anchorY - Height / 2);
                    UpdateShape();
                }
            }

            /// <summary>窗体轮廓：贴屏缘一侧直角，另一侧圆角。</summary>
            private GraphicsPath EdgePath(Rectangle r, int rad)
            {
                return MainContext.EdgeRounded(r, rad, _dockSide);
            }

            private void Expand()
            {
                if (_expanded) return;
                _expanded = true;
                _collapseTimer.Stop();
                ApplyState();
                Invalidate();
            }

            /// <summary>强制展开。悬浮窗平时被 WDA_EXCLUDEFROMCAPTURE 排除在截屏之外，
            /// 出图只能靠 DrawToBitmap 直接渲染，这里保证渲染出来的是展开态。</summary>
            internal void ExpandForShot()
            {
                _expanded = false;
                Expand();
            }

            private void Collapse()
            {
                if (!_expanded) return;
                _expanded = false;
                _hoverBtn = -1;
                ApplyState();
                Invalidate();
            }

            private void ScheduleCollapse()
            {
                _collapseTimer.Stop();
                _collapseTimer.Start();
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                // 关键：无论当前是否已展开，回到窗内都要取消"待收回"计时。
                // 否则：移出→启动 450ms 收回计时→450ms 内移回（此时仍是展开态，
                // Expand() 因已展开而直接 return、不会停表）→计时到点会在光标就停在
                // 悬浮条上时把面板收掉，交互体验是"自己缩回去了"。
                _collapseTimer.Stop();
                if (_pressing) return;   // 按住拖动经过时不展开
                Expand();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                // 移开鼠标半秒后收回；期间回到窗内会由 MouseEnter 取消
                if (_expanded && !_pressing) ScheduleCollapse();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_pressing && e.Button == MouseButtons.Left && !_dragging &&
                    (Math.Abs(Cursor.Position.X - _dragCursor.X) > 6 ||
                     Math.Abs(Cursor.Position.Y - _dragCursor.Y) > 6))
                {
                    // 按住后移动超过阈值 = 拖动（悬停展开的面板先收回，跟随光标的是收缩条）
                    _dragging = true;
                    _hoverBtn = -1;
                    if (_expanded) Collapse();
                    // 收回后重新取基准：拖的是收缩条当前位置，光标与窗体无偏移突变
                    _dragCursor = Cursor.Position;
                    _dragForm = Location;
                    Cursor = Cursors.SizeAll;
                }
                if (_dragging && e.Button == MouseButtons.Left)
                {
                    // 拖动：窗体跟随光标在虚拟屏幕内自由移动（两轴），松手后吸附最近屏缘
                    var vs = SystemInformation.VirtualScreen;
                    int x = Math.Max(vs.Left, Math.Min(_dragForm.X + Cursor.Position.X - _dragCursor.X, vs.Right - Width));
                    int y = Math.Max(vs.Top, Math.Min(_dragForm.Y + Cursor.Position.Y - _dragCursor.Y, vs.Bottom - Height));
                    Location = new Point(x, y);
                    return;
                }
                if (_expanded)
                {
                    int r = BtnAt(e.Location);
                    if (r != _hoverBtn) { _hoverBtn = r; Invalidate(); }
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left)
                {
                    _pressing = true;
                    _dragCursor = Cursor.Position;
                    _dragForm = Location;
                    // 捕获鼠标：拖到窗体范围外仍能收到 Move / Up。
                    // 否则：① 快速拖动时光标跑出窗体就收不到 MouseMove，窗体跟不上；
                    //       ② 在窗外松手收不到 MouseUp，_pressing 永远为真，
                    //          之后再也展不开（悬浮窗卡死）。
                    Capture = true;
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left) return;
                Capture = false;
                if (_dragging)
                {
                    _dragging = false;
                    _pressing = false;
                    Cursor = Cursors.Default;
                    SnapToNearestEdge();
                    return;
                }
                _pressing = false;
                if (_expanded)
                {
                    int r = BtnAt(e.Location);
                    if (r < 0) return;
                    Collapse();
                    _btns[r].Action(this, EventArgs.Empty);
                }
            }

            /// <summary>松手后把悬浮窗吸附到最近的屏缘（上下左右四选一，按窗体中心到各缘的距离）。</summary>
            private void SnapToNearestEdge()
            {
                var center = new Point(Location.X + Width / 2, Location.Y + Height / 2);
                var wa = Screen.FromPoint(center).WorkingArea;
                int dL = center.X - wa.Left, dR = wa.Right - center.X;
                int dT = center.Y - wa.Top, dB = wa.Bottom - center.Y;
                int min = Math.Min(Math.Min(dL, dR), Math.Min(dT, dB));
                if (min == dL) _dockSide = 0;
                else if (min == dR) _dockSide = 1;
                else if (min == dT) _dockSide = 2;
                else _dockSide = 3;
                if (_dockSide <= 1)
                    _anchorY = Math.Max(wa.Top + 80, Math.Min(center.Y, wa.Bottom - 80));
                else
                    _anchorX = Math.Max(wa.Left + 80, Math.Min(center.X, wa.Right - 80));
                ApplyState();
                var s = Settings.Current;
                s.BallY = _anchorY;
                s.BallX = _anchorX;
                s.BallSide = _dockSide;
                s.Save();
            }

            /// <summary>展开态命中测试：返回按钮下标（纵向按 y，横向按 x）。</summary>
            private int BtnAt(Point p)
            {
                int i = Horizontal ? (p.X - BarPad) / BtnH : (p.Y - BarPad) / BtnH;
                return (i >= 0 && i < _btns.Length) ? i : -1;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (_layered) return;   // 分层窗口由 UpdateLayered 直接渲染位图，不走常规 Paint
                var g = e.Graphics;
                g.Clear(BackColor);
                DrawBar(g);
            }

            /// <summary>绘制悬浮窗内容（不含清底，调用方负责底色）。同时供常规绘制与分层位图复用。</summary>
            private void DrawBar(Graphics g)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                if (!_expanded)
                {
                    // 收缩态：浅灰半条 + 三个握把圆点（纵条竖排、横条横排）
                    using (var path = EdgePath(new Rectangle(0, 0, Width - 1, Height - 1), 7))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(224, 226, 230)))
                            g.FillPath(b, path);
                        using (var p = new Pen(Color.FromArgb(210, 212, 218)))
                            g.DrawPath(p, path);
                    }
                    using (var b = new SolidBrush(Color.FromArgb(150, 152, 158)))
                        for (int i = 0; i < 3; i++)
                        {
                            if (Horizontal)
                                g.FillEllipse(b, Width / 2f - 9 + i * 8, (TabW - 4) / 2f, 4, 4);
                            else
                                g.FillEllipse(b, (TabW - 4) / 2f, Height / 2f - 9 + i * 8, 4, 4);
                        }
                    return;
                }

                // 展开态：白底面板，贴屏缘
                using (var path = EdgePath(new Rectangle(0, 0, Width - 1, Height - 1), 14))
                {
                    using (var b = new SolidBrush(Color.White))
                        g.FillPath(b, path);
                    using (var p = new Pen(Color.FromArgb(226, 228, 232)))
                        g.DrawPath(p, path);
                }

                for (int i = 0; i < _btns.Length; i++)
                {
                    var btn = _btns[i];
                    bool hover = i == _hoverBtn;
                    Rectangle tile, label;
                    if (Horizontal)
                    {
                        int x = BarPad + i * BtnH;
                        tile = new Rectangle(x + (BtnH - 32) / 2, (Height - 50) / 2, 32, 32);
                        label = new Rectangle(x, Height - 21, BtnH, 16);
                    }
                    else
                    {
                        int y = BarPad + i * BtnH;
                        tile = new Rectangle((Width - 32) / 2, y + 3, 32, 32);
                        label = new Rectangle(0, y + 37, Width, 16);
                    }
                    using (var path = MainContext.Rounded(tile, 9))
                    using (var b = new SolidBrush(Color.FromArgb(hover ? 40 : 24, btn.Accent)))
                        g.FillPath(b, path);
                    DrawGlyph(g, btn.Glyph, btn.Accent, tile);

                    TextRenderer.DrawText(g, btn.Text, LabelFont, label,
                        hover ? Color.FromArgb(30, 30, 30) : Color.FromArgb(110, 113, 120),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }

            // 图标按 38px 基准坐标绘制，随图标底座尺寸等比缩放
            private void DrawGlyph(Graphics g, IconKind kind, Color c, Rectangle box)
            {
                float k = box.Width / 38f;
                var state = g.Save();
                g.TranslateTransform(box.Left + box.Width / 2f, box.Top + box.Height / 2f);
                g.ScaleTransform(k, k);
                using (var p = new Pen(c, 1.9f))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    switch (kind)
                    {
                        case IconKind.Camera:
                        {
                            using (var path = MainContext.Rounded(new Rectangle(-10, -6, 20, 14), 3))
                                g.DrawPath(p, path);
                            g.DrawLine(p, -4, -6, -2, -9);
                            g.DrawLine(p, -2, -9, 2, -9);
                            g.DrawLine(p, 2, -9, 4, -6);
                            g.DrawEllipse(p, -3.5f, -3.5f, 7, 7);
                            break;
                        }
                        case IconKind.Scroller: // 窗口 + 下箭头 = 滚动长截图
                        {
                            using (var path = MainContext.Rounded(new Rectangle(-10, -8, 20, 16), 2))
                                g.DrawPath(p, path);
                            g.DrawLine(p, -10, -4, 10, -4);
                            g.DrawLine(p, 0, -1, 0, 4);
                            g.DrawLine(p, -3, 1, 0, 4);
                            g.DrawLine(p, 3, 1, 0, 4);
                            break;
                        }
                        case IconKind.Video:
                        {
                            using (var path = MainContext.Rounded(new Rectangle(-11, -6, 15, 12), 3))
                                g.DrawPath(p, path);
                            g.DrawPolygon(p, new[]
                            {
                                new Point(5, -3), new Point(10, -6),
                                new Point(10, 6), new Point(5, 3)
                            });
                            break;
                        }
                        case IconKind.Photo: // 相框 + 山峰 + 太阳 = 图片
                        {
                            using (var path = MainContext.Rounded(new Rectangle(-10, -8, 20, 16), 2))
                                g.DrawPath(p, path);
                            g.DrawEllipse(p, -6.5f, -4.5f, 3, 3);
                            g.DrawLines(p, new[]
                            {
                                new Point(-9, 6), new Point(-4, 0),
                                new Point(-1, 3), new Point(3, -1), new Point(9, 5)
                            });
                            break;
                        }
                        case IconKind.Convert: // 两个反向箭头 = 格式转换
                        {
                            g.DrawLine(p, -9f, -4f, 7f, -4f);
                            g.DrawLines(p, new[]
                            {
                                new Point(3, -7), new Point(7, -4), new Point(3, -1)
                            });
                            g.DrawLine(p, 9f, 4f, -7f, 4f);
                            g.DrawLines(p, new[]
                            {
                                new Point(-3, 1), new Point(-7, 4), new Point(-3, 7)
                            });
                            break;
                        }
                        case IconKind.Scissors: // 剪刀 = 抠图
                        {
                            g.DrawEllipse(p, -7f, 3f, 6, 6);
                            g.DrawEllipse(p, 1f, 3f, 6, 6);
                            g.DrawLine(p, -4.5f, 4f, 7f, -8f);
                            g.DrawLine(p, 4.5f, 4f, -7f, -8f);
                            break;
                        }
                        case IconKind.Gear:
                        {
                            // 外圈 + 8 个短齿 + 中心孔
                            for (int i = 0; i < 8; i++)
                            {
                                double a = i * Math.PI / 4;
                                g.DrawLine(p,
                                    (float)Math.Cos(a) * 8f, (float)Math.Sin(a) * 8f,
                                    (float)Math.Cos(a) * 10.5f, (float)Math.Sin(a) * 10.5f);
                            }
                            g.DrawEllipse(p, -8, -8, 16, 16);
                            g.DrawEllipse(p, -3, -3, 6, 6);
                            break;
                        }
                        case IconKind.Close:
                        {
                            using (var xp = new Pen(c, 2.4f))
                            {
                                xp.StartCap = xp.EndCap = LineCap.Round;
                                g.DrawLine(xp, -6, -6, 6, 6);
                                g.DrawLine(xp, 6, -6, -6, 6);
                            }
                            break;
                        }
                    }
                }
                g.Restore(state);
            }

            // ---- 分层窗口渲染（每像素 Alpha → 圆角边缘真正抗锯齿）----

            /// <summary>根据当前状态更新窗体形状：分层模式重绘位图，否则回退 Region 裁切。</summary>
            private void UpdateShape()
            {
                if (IsHandleCreated && _layered)
                    UpdateLayered();
                else
                {
                    using (var gp = MainContext.EdgeRounded(new Rectangle(0, 0, Width - 1, Height - 1),
                        _expanded ? 14 : 7, _dockSide))
                        Region = new Region(gp);
                }
            }

            /// <summary>把悬浮窗渲染到 32 位 ARGB 位图并通过 UpdateLayeredWindow 输出（圆角带平滑 Alpha 边缘）。</summary>
            private void UpdateLayered()
            {
                if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
                IntPtr hDib = IntPtr.Zero;
                try
                {
                    using (var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.Transparent);
                            DrawBar(g);
                        }

                        var info = new NativeMethods.BITMAPINFO();
                        info.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
                        info.bmiHeader.biWidth = bmp.Width;
                        info.bmiHeader.biHeight = -bmp.Height;   // top-down
                        info.bmiHeader.biPlanes = 1;
                        info.bmiHeader.biBitCount = 32;

                        var screen = NativeMethods.GetDC(IntPtr.Zero);
                        IntPtr ppvBits;
                        hDib = NativeMethods.CreateDIBSection(screen, ref info, NativeMethods.DIB_RGB_COLORS,
                            out ppvBits, IntPtr.Zero, 0);
                        if (hDib == IntPtr.Zero) return;
                        var sd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        // GDI+ 32bppARGB 与 DIB BI_RGB 同为 BGRA 内存布局，可直接逐行拷贝并保留 Alpha
                        NativeMethods.MoveMemory(ppvBits, sd.Scan0, (IntPtr)(sd.Stride * bmp.Height));
                        bmp.UnlockBits(sd);

                        var mem = NativeMethods.CreateCompatibleDC(screen);
                        var old = NativeMethods.SelectObject(mem, hDib);
                        var size = new NativeMethods.SIZE { cx = bmp.Width, cy = bmp.Height };
                        var psrc = new NativeMethods.POINT { X = 0, Y = 0 };
                        var pdst = new NativeMethods.POINT { X = Location.X, Y = Location.Y };
                        var blend = new NativeMethods.BLENDFUNCTION
                        {
                            BlendOp = 0,          // AC_SRC_OVER
                            BlendFlags = 0,
                            SourceConstantAlpha = 255,
                            AlphaFormat = 1       // AC_SRC_ALPHA
                        };
                        NativeMethods.UpdateLayeredWindow(Handle, screen, ref pdst, ref size, mem,
                            ref psrc, 0, ref blend, NativeMethods.ULW_ALPHA);
                        NativeMethods.SelectObject(mem, old);
                        NativeMethods.DeleteDC(mem);
                        NativeMethods.ReleaseDC(IntPtr.Zero, screen);

                        var prev = _layer;
                        _layer = (Bitmap)bmp.Clone();
                        if (prev != null) prev.Dispose();
                    }
                }
                catch
                {
                    // 分层渲染失败（极少数系统）：退回 Region 方案，至少保证可用
                    _layered = false;
                    Region = null;
                    UpdateShape();
                }
                finally
                {
                    if (hDib != IntPtr.Zero) NativeMethods.DeleteObject(hDib);
                }
            }

            /// <summary>透明像素处放行鼠标（HTTRANSPARENT），让圆角外区域点击穿透到下方窗口。</summary>
            protected override void WndProc(ref Message m)
            {
                const int WM_NCHITTEST = 0x84;
                if (m.Msg == WM_NCHITTEST && _layered && _layer != null)
                {
                    var p = PointToClient(Cursor.Position);
                    if (p.X >= 0 && p.Y >= 0 && p.X < Width && p.Y < Height && AlphaAt(p.X, p.Y) < 24)
                    {
                        m.Result = (IntPtr)(-1);   // HTTRANSPARENT
                        return;
                    }
                }
                base.WndProc(ref m);
            }

            /// <summary>读取分层位图在 (x,y) 处的 Alpha 值（0=全透明）。</summary>
            private int AlphaAt(int x, int y)
            {
                if (_layer == null) return 255;
                var bd = _layer.LockBits(new Rectangle(0, 0, _layer.Width, _layer.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    IntPtr row = IntPtr.Add(bd.Scan0, y * bd.Stride);
                    return Marshal.ReadByte(row, x * 4 + 3);  // BGRA：Alpha 位于第 4 字节
                }
                finally { _layer.UnlockBits(bd); }
            }

            /// <summary>导出当前悬浮窗渲染位图（供 --capture-bar 预览用，不依赖屏幕捕获）。</summary>
            internal Bitmap RenderSnapshot()
            {
                var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); DrawBar(g); }
                return bmp;
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 构建时导出图标：SnapCut.exe --write-icon <path>
            // exe / 安装包 / 快捷方式共用这套运行时绘制的图标，无需额外图片资源。
            if (args != null && args.Length >= 2 && args[0] == "--write-icon")
            {
                try
                {
                    using (var ico = MainContext.CreateAppIcon(256))
                    using (var fs = new FileStream(args[1], FileMode.Create, FileAccess.Write))
                        ico.Save(fs);
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("write-icon failed: " + ex.Message);
                    return;
                }
            }

            // 导出悬浮窗预览图：SnapCut.exe --capture-bar <out.png>
            // 悬浮窗被 WDA_EXCLUDEFROMCAPTURE 排除在屏幕捕获之外（避免污染用户截图），
            // 所以出图不能靠截屏，只能用 DrawToBitmap 直接渲染控件。
            if (args != null && args.Length >= 2 && args[0] == "--capture-bar")
            {
                try
                {
                    using (var bar = new MainContext.FloatingBar(null))
                    {
                        bar.StartPosition = FormStartPosition.Manual;
                        bar.Location = new Point(-30000, -30000);   // 放到屏幕外，避免闪现
                        bar.Show();
                        bar.ExpandForShot();
                        Application.DoEvents();
                        int pad = 28;
                        using (var snap = bar.RenderSnapshot())
                        using (var bmp = new Bitmap(bar.Width + pad * 2, bar.Height + pad * 2))
                        {
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.Clear(Color.FromArgb(246, 247, 249));
                                g.DrawImage(snap, pad, pad);
                            }
                            bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                        }
                        bar.Hide();
                    }
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("capture-bar failed: " + ex.Message);
                    return;
                }
            }

            bool created;
            using (new Mutex(true, "SimpleShot_SingleInstance", out created))
            {
                if (!created) return;

                try { NativeMethods.SetProcessDpiAwareness(2); }
                catch { try { NativeMethods.SetProcessDPIAware(); } catch { } }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainContext());
            }
        }
    }
}
