using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>
    /// 手动 / 自动滚动长截图。
    ///
    /// 关键设计（针对"长截图始终失败"的根因重写）：
    /// · 【抓帧 / 拼接跑在后台线程】不再挂在 WinForms Timer 上——Timer 消息优先级最低，
    ///   抓帧一忙节拍就被拉长到 150ms+，等稳定与节拍节流全部失真。
    /// · 【自动模式先滚到顶】对齐 ShareX：否则从半页开始且无法知道总长。
    /// · 【滚动通道可回退 / 可指定】滚轮消息 → WM_VSCROLL → 真实滚轮 → PageDown →
    ///   方向键；且用 SendMessageTimeout 同步发送以便判断是否真的被处理，
    ///   不再静默丢弃 PostMessage 的失败（UIPI 会静默丢弃）。
    /// · 【绝不因失配提前收图】早期版本连续 5 拍无进展就 Finish()，2~5 秒就交出一张
    ///   只有一两屏的图。现在只有"用户点完成 / Esc / 页面确实到底"才收图。
    /// · 【到底判定用多信号】连续多帧完全一致（ShareX 式）+ GetScrollInfo 到底。
    /// </summary>
    internal sealed class LongShotSession : Form
    {
        // ---- 滚动通道 ----
        private const int ChWheelMsg = 0;
        private const int ChScrollMsg = 1;
        private const int ChRealWheel = 2;
        private const int ChPageDown = 3;
        private const int ChArrowDown = 4;
        private const int ChCount = 5;

        private static readonly string[] ChannelNames =
            { "滚轮消息", "滚动条消息", "真实滚轮", "PageDown", "方向键" };

        private readonly Rectangle _region;
        private FrameOverlay _frame;
        /// <summary>保护 _engine / _frameBuf：后台线程与 UI 线程（Dispose）都会触碰它们。</summary>
        private readonly object _sync = new object();

        private StitchEngine _engine;
        private Bitmap _frameBuf;
        private Bitmap _frameBuf2;
        private IntPtr _targetWnd;
        private IntPtr _rootWnd;
        private IntPtr _scrollWnd;

        // ---- 后台采集线程 ----
        private Thread _worker;
        private volatile bool _stopping;
        private volatile bool _cancelled;
        private volatile bool _delivered;
        private volatile bool _auto;

        // ---- UI 只读快照 ----
        private volatile int _uiHeight;
        private volatile int _uiStitches;
        private volatile string _uiState = "准备中";

        // ---- 以下字段只由后台线程访问 ----
        private bool _settling;
        private double[][] _settleSig;
        private double[][] _prevSig;
        private int _settleTicks;
        private int _fastBeats;
        private int _lastSendMs;
        private int _heightAtSend;
        private int _noProgressBeats;
        private int _stallBeats;
        private int _staticBeats;
        private int _bottomConfirm;
        private int _channel;
        private bool _channelFixed;
        private volatile bool _autoResetPending;
        private int _autoPhase;      // 0 = 回到顶部, 1 = 节拍滚动
        private int _topWait;
        private int _idleBeats;
        private int _noStitchBeats;  // 连续多少拍没拼上（用于"放慢一点"引导）
        private int _warnBeats;      // "有缝/失配"警告的剩余展示拍数
        private bool _lastWarn;
        private int _seenResync;     // 已见到的重同步次数（用于提示）
        private int _resyncHintBeats;

        private readonly System.Windows.Forms.Timer _ui;

        private static readonly Font StatusFont = new Font("Microsoft YaHei UI", 9f);
        private static readonly Font BtnFont = new Font("Microsoft YaHei UI", 9f);

        private Rectangle _okR, _cancelR, _autoR;
        private int _hover;             // 0 无, 1 完成, 2 取消, 3 自动

        /// <summary>触发一次：完成时带位图（调用方负责释放），取消时为 null。</summary>
        public event Action<Bitmap> Completed;

        public LongShotSession(Rectangle region)
        {
            region.Width &= ~1; region.Height &= ~1;
            if (region.Width < 16) region.Width = 16;
            if (region.Height < 16) region.Height = 16;
            _region = region;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(32, 32, 32);
            Size = new Size(540, 40);
            _cancelR = new Rectangle(Width - 60, 7, 52, 26);
            _okR = new Rectangle(Width - 124, 7, 56, 26);
            _autoR = new Rectangle(Width - 226, 7, 94, 26);
            PlaceBar();

            _ui = new System.Windows.Forms.Timer { Interval = 100 };
            _ui.Tick += delegate { Invalidate(); };

            MouseMove += delegate (object s, MouseEventArgs e)
            {
                int h = _okR.Contains(e.Location) ? 1 : _cancelR.Contains(e.Location) ? 2
                      : _autoR.Contains(e.Location) ? 3 : 0;
                if (h != _hover) { _hover = h; Invalidate(); }
                Cursor = h != 0 ? Cursors.Hand : Cursors.Default;
            };
            MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (_okR.Contains(e.Location)) RequestFinish();
                else if (_cancelR.Contains(e.Location)) CancelSession();
                else if (_autoR.Contains(e.Location)) ToggleAuto();
            };

            Load += delegate { Start(); };
            FormClosed += delegate
            {
                if (_frame != null) { _frame.Dispose(); _frame = null; }
                if (_ui != null) { _ui.Stop(); _ui.Dispose(); }
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            try { NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE); } catch { }
            using (var gp = Toolbar.Rounded(new Rectangle(0, 0, Width, Height), 8))
                Region = new Region(gp);
        }

        private void PlaceBar()
        {
            var wa = SystemInformation.VirtualScreen;
            int x = Math.Min(Math.Max(_region.Right - Width, wa.Left + 4), wa.Right - Width - 4);
            int y = _region.Bottom + 12;
            if (y + Height > wa.Bottom - 4) y = _region.Top - Height - 12;
            if (y < wa.Top + 4) y = _region.Bottom - Height - 12;
            Location = new Point(x, y);
        }

        // ---------------- 启动 ----------------

        private void Start()
        {
            _frame = new FrameOverlay(_region, 3, Color.FromArgb(7, 193, 96));
            _frame.Show();

            try
            {
                var center = new NativeMethods.POINT
                {
                    X = _region.Left + _region.Width / 2,
                    Y = _region.Top + _region.Height / 2
                };
                _targetWnd = NativeMethods.WindowFromPoint(center);
                _rootWnd = NativeMethods.GetAncestor(_targetWnd, NativeMethods.GA_ROOT);
                // 真正的"可滚动窗口"：从叶子沿祖先链上溯找带垂直滚动条的那一层，
                // 找不到（浏览器自绘滚动条）就退回顶层窗口
                _scrollWnd = FindScrollable(_targetWnd, _rootWnd);
                if (_rootWnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(_rootWnd);
            }
            catch { }

            _frameBuf = ScrollingCapture.Grab(_region);
            _frameBuf2 = new Bitmap(_region.Width, _region.Height,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            _engine = new StitchEngine(_frameBuf);
            _uiHeight = _engine.Height;

            int method = Settings.Current.ScrollMethod;
            _channelFixed = method > 0 && method < ChCount;
            _channel = _channelFixed ? method : ChWheelMsg;

            _ui.Start();
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "LongShot";
            _worker.Start();
        }

        // ---------------- 后台采集循环 ----------------

        private void WorkerLoop()
        {
            Bitmap result = null;
            try
            {
                while (!_stopping)
                {
                    Step();
                    int sleep = _auto ? 25 : (_idleBeats > 8 ? 90 : 30);
                    Thread.Sleep(sleep);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("longshot worker error: " + ex);
            }
            // 收尾（导出 + 释放）与 UI 线程的 Dispose 互斥，避免"导出巨图时被 Dispose"
            lock (_sync)
            {
                try
                {
                    if (!_cancelled && _engine != null)
                    {
                        if (_engine.Skipped) _engine.DumpDiag(_frameBuf);
                        result = _engine.Export();
                    }
                }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine("longshot export error: " + ex2);
                }
                if (_engine != null) { _engine.Dispose(); _engine = null; }
                if (_frameBuf != null) { _frameBuf.Dispose(); _frameBuf = null; }
                if (_frameBuf2 != null) { _frameBuf2.Dispose(); _frameBuf2 = null; }
            }
            Bitmap final = result;
            try { BeginInvoke((MethodInvoker)delegate { DeliverOnUi(final); }); }
            catch { if (final != null) final.Dispose(); }
        }

        private void Step()
        {
            if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_ESCAPE) & 0x8000) != 0)
            {
                _cancelled = true;
                _stopping = true;
                return;
            }

            lock (_sync)
            {
                if (_engine == null || _frameBuf == null) { _stopping = true; return; }

                ScrollingCapture.GrabInto(_frameBuf, _region);
                var curSig = ScrollingCapture.RowSignatures(_frameBuf);

                if (_auto)
                {
                    if (_autoResetPending) { ResetAutoState(); _autoResetPending = false; }
                    AutoStep(curSig);
                }
                else { ManualStep(curSig); }

                // "有缝 / 跳过"警告只短暂显示（~2.5s），不常驻——否则一次抖动
                // 会让用户以为整张图失败了
                bool warn = _engine.Skipped;
                if (warn && !_lastWarn) _warnBeats = 60;
                _lastWarn = warn;
                if (_warnBeats > 0) _warnBeats--;

                _prevSig = curSig;
                _uiHeight = _engine.Height;
                _uiStitches = _engine.StitchCount;
                var ld = _lastDecision;
                _engine.Trace("h=" + _engine.Height
                    + (ld.Stitch ? " +" + ld.Shift : ld.UpShift > 0 ? " ^" + ld.UpShift
                       : _engine.ActiveMismatch ? " x" : " .")
                    + (_auto ? " auto" : " man"));
            }
        }

        private StitchEngine.Decision _lastDecision;

        private void ManualStep(double[][] curSig)
        {
            // 注意：这里**不做**"等画面停稳"的闸门。影视站这类页面图片懒加载，
            // 滚动后画面要持续变化好几秒，按"静止"闸门会永远放不下帧（线上实测
            // stitchCount=0 / ambiguous=18）。正确做法是每帧都交给引擎：运动中的
            // 帧精确匹配自然失败、且引擎已不再做 bestGuess 强拼，所以不会产生接缝；
            // 等页面真静止下来那一拍，精确匹配一次就锁上。
            var d = RunEngine(curSig);
            // 只有"确实没动静"才降频；画面滚动中必须保持高频率抓帧，
            // 否则滚快了会漏掉中间位置，静止后一次跳变过大就拼不上
            if (d.Stitch || d.UpShift > 0 || _engine.ActiveMismatch) _idleBeats = 0;
            else _idleBeats++;

            // 长时间只在滚动、一次都没拼上：多半是滚得太快（浏览器平滑滚动时画面
            // 是亚像素重采样的，字节对不上），给一句操作引导而不是报错
            _noStitchBeats = (d.Stitch || d.UpShift > 0) ? 0 : _noStitchBeats + 1;

            // 卡死重同步：一次滚过一屏多时画布会彻底够不着当前画面，此时只能整屏接上
            if (_engine.ResyncCount > _seenResync)
            {
                _seenResync = _engine.ResyncCount;
                _resyncHintBeats = 40;
            }
            if (_resyncHintBeats > 0) _resyncHintBeats--;

            if (_engine.Height >= ScrollingCapture.MaxHeight)
                _uiState = "已达最大长度，请点完成";
            else if (_resyncHintBeats > 0)
                _uiState = "跳过一段（滚太快），已接上继续";
            else if (_warnBeats > 0)
                _uiState = "这段没拼上，可往回滚再滚一次";
            else if (_noStitchBeats > 60)
                _uiState = "放慢一点，滚一段停一下";
            else if (_engine.ActiveMismatch)
                _uiState = "画面滚动中…";
            else
                _uiState = "滚动鼠标滚轮拼接";
        }

        /// <summary>处理一帧；失配时立即重抓一次（过滤撕裂 / 合成中途的坏帧）。</summary>
        private StitchEngine.Decision RunEngine(double[][] curSig)
        {
            var d = _engine.Process(_frameBuf, curSig);
            if (!d.Stitch && d.UpShift == 0 && _engine.ActiveMismatch)
            {
                // 失配时立即重抓一次再判 —— 过滤撕裂帧 / 合成中途的坏帧
                Thread.Sleep(15);
                ScrollingCapture.GrabInto(_frameBuf2, _region);
                var sig2 = ScrollingCapture.RowSignatures(_frameBuf2);
                var d2 = _engine.Process(_frameBuf2, sig2);
                if (d2.Stitch || d2.UpShift > 0)
                {
                    // 用重抓的帧作为本拍结果
                    var t = _frameBuf; _frameBuf = _frameBuf2; _frameBuf2 = t;
                    curSig = sig2;
                    d = d2;
                }
            }
            _lastDecision = d;
            return d;
        }

        // ---------------- 自动模式 ----------------

        private void AutoStep(double[][] curSig)
        {
            if (_autoPhase == 0) { ScrollToTopPhase(curSig); return; }

            // 1. 等画面稳定（滚动动画 / 懒加载弹图都在这里被吸收）
            if (_settling && _settleSig != null && _settleTicks < 40 &&
                !ScrollingCapture.IsStatic(_settleSig, curSig))
            {
                _settleSig = curSig;
                _settleTicks++;
                _uiState = "自动滚动中 · 等待画面稳定";
                return;
            }
            int waited = _settleTicks;
            _settling = false;
            _settleTicks = 0;
            _settleSig = curSig;
            if (waited == 1) _fastBeats++;
            else if (waited > 1) _fastBeats = 0;

            // 2. 稳定帧：校验拼接
            var d = RunEngine(curSig);

            bool sameAsPrev = _prevSig != null && ScrollingCapture.IsStatic(_prevSig, curSig);
            _staticBeats = (sameAsPrev && !d.Stitch && d.UpShift == 0) ? _staticBeats + 1 : 0;

            // 3. 到底 / 收图判定
            if (_engine.Height >= ScrollingCapture.MaxHeight)
            {
                _uiState = "已达最大长度，自动收图";
                _stopping = true;
                return;
            }
            if (IsAtBottom())
            {
                if (++_bottomConfirm >= 3)
                {
                    _uiState = "已到页面底部，自动收图";
                    _stopping = true;
                    return;
                }
            }
            else _bottomConfirm = 0;

            // 页面彻底不动（已到底或滚动无效）且确实拼过内容 → 收图。
            // 阈值必须大于一个节拍间隔内的拍数，否则 ScrollDelayMs 调大时会在
            // "刚发完滚动、正在等待"的正常间隙里被误判成到底。
            int staticLimit = Math.Max(30, Settings.Current.ScrollDelayMs / 25 + 20);
            if (_staticBeats >= staticLimit && _engine.StitchCount > 0)
            {
                _uiState = "页面已停止滚动，自动收图";
                _stopping = true;
                return;
            }

            int now = Environment.TickCount;
            int delay = Settings.Current.ScrollDelayMs;
            if (_fastBeats >= 2) delay = Math.Min(delay, 140);
            if (now - _lastSendMs < delay)
            {
                _uiState = AutoState();
                return;
            }

            bool grew = _engine.Height > _heightAtSend;
            if (grew)
            {
                _noProgressBeats = 0;
                _stallBeats = 0;
            }
            else
            {
                _noProgressBeats++;
                _stallBeats++;
                if (!_channelFixed && _noProgressBeats >= 3)
                {
                    if (_channel < ChCount - 1) { _channel++; _noProgressBeats = 0; _bottomConfirm = 0; }
                    else if (_stallBeats >= 90)
                    {
                        // 所有通道都试过且长时间毫无进展：保留已拼内容收图
                        _uiState = "页面无法自动滚动，已保留已拼部分";
                        _stopping = true;
                        return;
                    }
                }
            }

            _heightAtSend = _engine.Height;
            _lastSendMs = now;
            _staticBeats = 0;      // 刚发出滚动：静止计数从这一拍重新起算
            SendScrollStep();
            _uiState = AutoState();
        }

        private string AutoState()
        {
            string s = _settling ? "自动滚动中 · 等待画面稳定" : "自动滚动中";
            s += " · " + ChannelNames[_channel];
            if (_warnBeats > 0) s += " · 部分内容可能有缝";
            return s;
        }

        /// <summary>自动模式第一阶段：先把页面滚到顶部，稳定后以当前画面重建画布。</summary>
        private void ScrollToTopPhase(double[][] curSig)
        {
            if (_topWait == 0)
            {
                SendScrollTop();
                _topWait = 1;
                _settleSig = curSig;
                _uiState = "正在回到页面顶部…";
                return;
            }
            bool still = _settleSig != null && ScrollingCapture.IsStatic(_settleSig, curSig);
            _settleSig = curSig;
            _topWait++;
            _uiState = "正在回到页面顶部…";
            if (still || _topWait > 50)
            {
                if (_engine != null) _engine.Dispose();
                _engine = new StitchEngine(_frameBuf);
                _heightAtSend = _engine.Height;
                _settleSig = null;
                _prevSig = null;
                _settleTicks = 0;
                _staticBeats = 0;
                _noProgressBeats = 0;
                _stallBeats = 0;
                _bottomConfirm = 0;
                _lastSendMs = 0;
                _autoPhase = 1;
            }
        }

        // ---------------- 滚动注入 ----------------

        private void SendScrollTop()
        {
            if (_scrollWnd != IntPtr.Zero)
            {
                IntPtr res;
                NativeMethods.SendMessageTimeout(_scrollWnd, (uint)NativeMethods.WM_VSCROLL,
                    (IntPtr)NativeMethods.SB_TOP, IntPtr.Zero,
                    NativeMethods.SMTO_ABORTIFHUNG, 200, out res);
            }
            try
            {
                NativeMethods.keybd_event(NativeMethods.VK_HOME, 0, NativeMethods.KEYEVENTF_KEYDOWN, IntPtr.Zero);
                NativeMethods.keybd_event(NativeMethods.VK_HOME, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
            }
            catch { }
        }

        private void SendScrollStep()
        {
            switch (_channel)
            {
                case ChWheelMsg:
                    SendWheel(_scrollWnd != IntPtr.Zero ? _scrollWnd : _targetWnd);
                    break;
                case ChScrollMsg:
                    if (_scrollWnd != IntPtr.Zero)
                    {
                        IntPtr res;
                        NativeMethods.SendMessageTimeout(_scrollWnd, (uint)NativeMethods.WM_VSCROLL,
                            (IntPtr)NativeMethods.SB_PAGEDOWN, IntPtr.Zero,
                            NativeMethods.SMTO_ABORTIFHUNG, 300, out res);
                    }
                    break;
                case ChRealWheel:
                    RealWheel();
                    break;
                case ChPageDown:
                    KeyTap(NativeMethods.VK_NEXT);
                    break;
                case ChArrowDown:
                    int n = Math.Max(1, Settings.Current.ScrollStep / 40);
                    for (int i = 0; i < n; i++) KeyTap(NativeMethods.VK_DOWN);
                    break;
            }
            _settling = true;
        }

        /// <summary>同步投递 WM_MOUSEWHEEL。用 SendMessageTimeout 而不是 PostMessage：
        /// PostMessage 的失败（UIPI / 句柄失效）是静默的，永远查不出"为什么没滚"。</summary>
        private void SendWheel(IntPtr wnd)
        {
            if (wnd == IntPtr.Zero) return;
            int step = -Settings.Current.ScrollStep;
            IntPtr wParam = (IntPtr)((uint)(step & 0xFFFF) << 16);
            IntPtr lParam = (IntPtr)((((uint)(_region.Top + _region.Height / 2)) << 16) |
                                     (uint)(_region.Left + _region.Width / 2 & 0xFFFF));
            IntPtr res;
            NativeMethods.SendMessageTimeout(wnd, (uint)NativeMethods.WM_MOUSEWHEEL, wParam, lParam,
                NativeMethods.SMTO_ABORTIFHUNG, 300, out res);
        }

        /// <summary>真实滚轮注入（光标必须位于选区内）。</summary>
        private void RealWheel()
        {
            try
            {
                NativeMethods.SetCursorPos(_region.Left + _region.Width / 2,
                                           _region.Top + _region.Height / 2);
                NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_WHEEL, 0, 0,
                    -Settings.Current.ScrollStep, IntPtr.Zero);
            }
            catch { }
        }

        private static void KeyTap(int vk)
        {
            try
            {
                NativeMethods.keybd_event((byte)vk, 0, NativeMethods.KEYEVENTF_KEYDOWN, IntPtr.Zero);
                NativeMethods.keybd_event((byte)vk, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
            }
            catch { }
        }

        // ---- 滚动条探测 ----

        private static IntPtr FindScrollable(IntPtr leaf, IntPtr root)
        {
            IntPtr h = leaf;
            for (int i = 0; i < 16 && h != IntPtr.Zero; i++)
            {
                if (HasVScroll(h)) return h;
                if (h == root) break;
                h = NativeMethods.GetParent(h);
            }
            return root != IntPtr.Zero ? root : leaf;
        }

        private static bool HasVScroll(IntPtr h)
        {
            var si = new NativeMethods.SCROLLINFO();
            si.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.SCROLLINFO));
            si.fMask = NativeMethods.SIF_RANGE | NativeMethods.SIF_PAGE;
            if (!NativeMethods.GetScrollInfo(h, NativeMethods.SB_VERT, ref si)) return false;
            return si.nMax > 0 && si.nMax > (int)si.nPage;
        }

        private bool IsAtBottom()
        {
            IntPtr h = _scrollWnd;
            if (h == IntPtr.Zero) return false;
            var si = new NativeMethods.SCROLLINFO();
            si.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.SCROLLINFO));
            si.fMask = NativeMethods.SIF_RANGE | NativeMethods.SIF_PAGE | NativeMethods.SIF_POS;
            if (!NativeMethods.GetScrollInfo(h, NativeMethods.SB_VERT, ref si)) return false;
            if (si.nMax <= 0) return false;
            return si.nPos >= si.nMax - (int)si.nPage + 1;
        }

        // ---------------- 交互 / 收尾 ----------------

        private void ToggleAuto()
        {
            if (_delivered || _stopping) return;
            _auto = !_auto;
            if (_auto)
            {
                // 真正的状态重置在后台线程里做（需要读 _engine，不能在 UI 线程碰它）
                _autoResetPending = true;
                // 光标移到控制条上：链接预览气泡立即消失，且滚轮消息 / 键盘通道
                // 都不需要光标留在页面上
                try
                {
                    NativeMethods.SetCursorPos(Location.X + Width / 2, Location.Y + Height / 2);
                }
                catch { }
            }
            Invalidate();
        }

        /// <summary>进入自动滚动时重置节拍状态（后台线程调用，已在 _sync 内）。</summary>
        private void ResetAutoState()
        {
            _autoPhase = 0;      // 先回到页面顶部
            _topWait = 0;
            _settling = false;
            _settleSig = null;
            _settleTicks = 0;
            _fastBeats = 0;
            _lastSendMs = 0;
            _staticBeats = 0;
            _noProgressBeats = 0;
            _stallBeats = 0;
            _bottomConfirm = 0;
            _heightAtSend = _engine != null ? _engine.Height : 0;
            if (!_channelFixed) _channel = ChWheelMsg;
        }

        private void RequestFinish() { _stopping = true; }

        private void CancelSession()
        {
            _cancelled = true;
            _stopping = true;
        }

        private void DeliverOnUi(Bitmap img)
        {
            if (_delivered)
            {
                if (img != null) img.Dispose();
                return;
            }
            _delivered = true;
            _ui.Stop();
            var h = Completed;
            if (h != null)
            {
                try { h(img); }
                catch { if (img != null) img.Dispose(); }
            }
            else if (img != null) img.Dispose();
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            string state = _uiState;
            if (_auto && _uiState == "准备中") state = "自动滚动中";
            string text = string.Format("{0} · 已拼接 {1} × {2}px",
                state, _uiStitches, _uiHeight);
            using (var br = new SolidBrush(Color.White))
                g.DrawString(text, StatusFont, br, 10, 11);

            DrawButton(g, _autoR, _auto ? "自动滚动 ✓" : "自动滚动",
                _auto ? Color.FromArgb(0, 120, 215) : Color.FromArgb(70, 70, 70), _hover == 3 || _auto);
            DrawButton(g, _okR, "完成", Color.FromArgb(7, 193, 96), _hover == 1);
            DrawButton(g, _cancelR, "取消", Color.FromArgb(70, 70, 70), _hover == 2);
        }

        private static void DrawButton(Graphics g, Rectangle r, string text, Color back, bool hover)
        {
            using (var b = new SolidBrush(hover ? ControlPaint.Light(back) : back))
            using (var gp = Toolbar.Rounded(r, 5))
                g.FillPath(b, gp);
            TextRenderer.DrawText(g, text, BtnFont, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stopping = true;
                if (_worker != null && _worker.IsAlive)
                {
                    try { _worker.Join(300); } catch { }
                    _worker = null;
                }
                // 与后台线程的收尾互斥：线程还活着时不要动引擎，交给它自己释放
                lock (_sync)
                {
                    if (_engine != null) { _engine.Dispose(); _engine = null; }
                    if (_frameBuf != null) { _frameBuf.Dispose(); _frameBuf = null; }
                    if (_frameBuf2 != null) { _frameBuf2.Dispose(); _frameBuf2 = null; }
                }
            }
            base.Dispose(disposing);
        }
    }
}
