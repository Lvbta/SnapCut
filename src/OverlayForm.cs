using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>
    /// 全屏截图覆盖层，复刻微信截图体验：
    /// 屏幕冻结、窗口自动高亮、放大镜取色、绿框选区（8 个手柄）、
    /// 尺寸角标、浮动工具条与标注。方向键可微调选区，右键 / Esc 分级取消。
    /// </summary>
    internal sealed class OverlayForm : Form
    {
        private enum State { Idle, Dragging, Selected, Drawing, Moving, Resizing }

        private static readonly Color WeGreen = Color.FromArgb(7, 193, 96);
        private const int HANDLE = 6, MIN_SEL = 10;

        private readonly Bitmap _screen;          // frozen desktop (virtual screen)
        private Bitmap _pixelated;                // lazy, for mosaic tool
        private readonly Rectangle _vs;           // virtual screen bounds (screen coords)
        private readonly WindowDetector _detector;
        private readonly CaptureMode _mode;

        private State _state = State.Idle;
        private Rectangle _sel;                   // selection in client/bitmap coords
        private Rectangle _hoverWin;              // window under cursor (client coords)
        private Point _dragStart, _moveOffset;
        private int _resizeHandle = -1;
        private Point _mouse;

        private readonly List<Annotation> _annos = new List<Annotation>();
        private int _selAnno = -1;      // 选中的标注下标（-1 = 无；滑块 / 颜色仅作用于它）
        private bool _movingAnno;       // 按住选中标注拖动中
        private Point _moveAnnoLast;    // 拖动时上一次光标位置
        private Annotation _current;
        private Bitmap _matted;                  // 智能抠图结果（RGBA，背景透明）
        private readonly Toolbar _toolbar;
        private readonly StylePanel _style;
        private TextBox _textBox;
        private Point _textPos;

        /// <summary>Set (screen coords) when the user chose "record"; MainContext starts the recorder.</summary>
        public Rectangle? RecordRegion;
        /// <summary>Set (screen coords) when the user chose "long screenshot"; MainContext runs scrolling capture.</summary>
        public Rectangle? LongShotRegion;

        public OverlayForm(CaptureMode mode)
        {
            _mode = mode;
            _vs = SystemInformation.VirtualScreen;

            // 1. freeze the screen BEFORE the overlay appears
            _screen = new Bitmap(_vs.Width, _vs.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(_screen))
                g.CopyFromScreen(_vs.Left, _vs.Top, 0, 0, _vs.Size);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = _vs;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            _detector = new WindowDetector(Handle, _vs);
            _hoverWin = ToClient(_detector.HitTest(Cursor.Position));

            _toolbar = new Toolbar(mode) { Visible = false };
            _toolbar.Clicked += OnToolbarClick;
            Controls.Add(_toolbar);

            _style = new StylePanel { Visible = false };
            _style.Changed += delegate
            {
                // 滑块 / 颜色只影响下一次绘制；选中了已有标注时，实时修改那一条
                if (_selAnno >= 0 && _selAnno < _annos.Count)
                {
                    var a = _annos[_selAnno];
                    a.Width = _style.SelWidth;
                    a.Color = _style.SelColor;
                    if (a.Kind == ToolKind.Text)
                        a.Font = new Font("Microsoft YaHei UI", 10 + _style.SelWidth * 2,
                                          FontStyle.Regular, GraphicsUnit.Pixel);
                }
                if (_textBox != null) ApplyTextBoxStyle();
                Invalidate();
            };
            Controls.Add(_style);

            MouseDown += OnDown; MouseMove += OnMove; MouseUp += OnUp;
            MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && _state == State.Selected && _sel.Contains(e.Location))
                    Confirm();
            };
            KeyDown += OnKey;

            // 覆盖层显示后强制激活：全局热键弹出时前台窗口可能属于其他进程，
            // 不激活则键盘消息全丢（鼠标不需要焦点所以能画），表现为
            // “Esc / Enter 失灵”。这是“Esc 无法退出截屏”的修复。
            Shown += delegate
            {
                Activate();
                try { NativeMethods.SetForegroundWindow(Handle); } catch { }
            };
        }

        // ---------- coordinate helpers ----------
        private Rectangle ToClient(Rectangle screenRect)
        {
            screenRect.Offset(-_vs.Left, -_vs.Top);
            return screenRect;
        }
        private Rectangle ToScreen(Rectangle clientRect)
        {
            clientRect.Offset(_vs.Left, _vs.Top);
            return clientRect;
        }

        // ---------- 鼠标 ----------
        private void OnDown(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                // 分级取消（微信行为），避免一键毁掉已有工作：
                //   正在绘制 -> 仅取消这一笔；有文字框 -> 仅提交文字；
                //   有标注   -> 清除标注但保留选区；选区态 -> 回到重新框选；空闲 -> 退出
                if (_state == State.Drawing)
                {
                    _current = null;
                    _state = State.Selected;
                    Invalidate();
                    return;
                }
                if (_textBox != null) { CommitTextBox(); return; }
                if (_state == State.Selected && _annos.Count > 0)
                {
                    _annos.Clear();
                    _selAnno = -1;
                    Invalidate();
                    return;
                }
                if (_state != State.Idle)
                {
                    // 回到重新框选：同时丢弃抠图结果
                    if (_matted != null) { _matted.Dispose(); _matted = null; }
                    _current = null;
                    _toolbar.ActiveTool = ""; _toolbar.Visible = false; _style.Visible = false;
                    _state = State.Idle;
                    _hoverWin = ToClient(_detector.HitTest(Cursor.Position));
                    Cursor = Cursors.Cross;
                    Invalidate();
                    return;
                }
                Close();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            switch (_state)
            {
                case State.Idle:
                    _dragStart = e.Location;
                    _state = State.Dragging;
                    break;

                case State.Selected:
                    CommitTextBox();
                    string tool = _toolbar.ActiveTool;
                    bool inside = _sel.Contains(e.Location);
                    // 点击标注 = 选中（QQ / 微信方式），按住即可拖动；松手仍在原地 = 仅选中
                    int hit = HitAnno(e.Location);
                    if (hit >= 0)
                    {
                        SelectAnno(hit);
                        _movingAnno = true;
                        _moveAnnoLast = e.Location;
                        return;
                    }
                    _selAnno = -1;
                    if (tool != "" && inside)
                    {
                        if (tool == "text") { PlaceTextBox(e.Location); return; }
                        _current = NewAnnotation(tool, e.Location);
                        _state = State.Drawing;
                        return;
                    }
                    int h = HitHandle(e.Location);
                    if (h >= 0 && _annos.Count == 0 && tool == "")
                    {
                        _resizeHandle = h; _state = State.Resizing;
                        _toolbar.Visible = false; _style.Visible = false;
                    }
                    else if (inside && tool == "")
                    {
                        _moveOffset = new Point(e.X - _sel.X, e.Y - _sel.Y);
                        _state = State.Moving;
                        _toolbar.Visible = false; _style.Visible = false;
                    }
                    break;
            }
        }

        /// <summary>选中一条标注：滑块同步到它的粗细，颜色同步为它的颜色。</summary>
        private void SelectAnno(int idx)
        {
            _selAnno = idx;
            var a = _annos[idx];
            _style.SelWidth = a.Width;
            _style.SelColor = a.Color;
            _style.Invalidate();
            Invalidate();
        }

        /// <summary>
        /// 命中测试：点击处的标注下标（顶层优先），未命中返回 -1。
        /// 宽松命中（QQ / 微信方式）：矩形 / 椭圆内部也算，其余按折线距离。
        /// </summary>
        private int HitAnno(Point p)
        {
            for (int i = _annos.Count - 1; i >= 0; i--)
            {
                var a = _annos[i];
                if (a.Kind == ToolKind.Text)
                {
                    if (a.Bounds().Contains(p)) return i;
                    continue;
                }
                if (a.Kind == ToolKind.Rect || a.Kind == ToolKind.Ellipse)
                {
                    var n = a.Norm();
                    bool near = Rectangle.Inflate(n, (int)(a.Width / 2) + 6, (int)(a.Width / 2) + 6).Contains(p);
                    if (near) return i;   // 边框附近或内部均可选中
                    continue;
                }
                float tol = a.Kind == ToolKind.Mosaic
                    ? (12 + a.Width * 8) / 2f          // 马赛克按笔刷半径命中
                    : Math.Max(10, a.Width * 0.5f + 6);
                var pts = new List<Point>();
                if (a.Kind == ToolKind.Arrow) { pts.Add(a.A); pts.Add(a.B); }
                else if (a.Path != null) pts.AddRange(a.Path);
                for (int k = 1; k < pts.Count; k++)
                    if (DistToSeg(p, pts[k - 1], pts[k]) <= tol) return i;
                if (pts.Count == 1 && Dist(p, pts[0]) <= tol) return i;   // 单点笔迹
            }
            return -1;
        }

        private static float Dist(Point p, Point q)
        {
            float dx = p.X - q.X, dy = p.Y - q.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static float DistToSeg(Point p, Point a, Point b)
        {
            float vx = b.X - a.X, vy = b.Y - a.Y;
            float wx = p.X - a.X, wy = p.Y - a.Y;
            float len2 = vx * vx + vy * vy;
            float t = len2 <= 0 ? 0 : Math.Max(0f, Math.Min(1f, (wx * vx + wy * vy) / len2));
            float dx = p.X - (a.X + t * vx), dy = p.Y - (a.Y + t * vy);
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private void OnMove(object s, MouseEventArgs e)
        {
            _mouse = e.Location;

            // 拖动选中的标注
            if (_movingAnno && _selAnno >= 0 && _selAnno < _annos.Count)
            {
                int dx = e.X - _moveAnnoLast.X, dy = e.Y - _moveAnnoLast.Y;
                if (dx != 0 || dy != 0)
                {
                    _annos[_selAnno].Offset(dx, dy);
                    _moveAnnoLast = e.Location;
                    Invalidate();
                }
                return;
            }

            switch (_state)
            {
                case State.Idle:
                    _hoverWin = ToClient(_detector.HitTest(ToScreen(new Rectangle(e.Location, Size.Empty)).Location));
                    Invalidate();
                    break;

                case State.Dragging:
                    _sel = RectFrom(_dragStart, e.Location);
                    Invalidate();
                    break;

                case State.Moving:
                    {
                        // 移动选区时同步平移已有标注，修复标注与画面错位的 Bug
                        int nx = Clamp(e.X - _moveOffset.X, 0, ClientSize.Width - _sel.Width);
                        int ny = Clamp(e.Y - _moveOffset.Y, 0, ClientSize.Height - _sel.Height);
                        TranslateAnnotations(nx - _sel.X, ny - _sel.Y);
                        _sel.X = nx; _sel.Y = ny;
                        Invalidate();
                        break;
                    }

                case State.Resizing:
                    ResizeSel(e.Location);
                    Invalidate();
                    break;

                case State.Drawing:
                    if (_current != null)
                    {
                        var p = ClampPoint(e.Location, _sel);
                        if (_current.Path != null) _current.Path.Add(p); else _current.B = p;
                        Invalidate();
                    }
                    break;

                case State.Selected:
                    // 悬停到标注上时给出可拖动提示
                    Cursor = HitAnno(e.Location) >= 0 ? Cursors.SizeAll : Cursors.Default;
                    break;
            }
        }

        private void OnUp(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_movingAnno) { _movingAnno = false; return; }
            switch (_state)
            {
                case State.Dragging:
                    // tiny drag = click -> take the highlighted window (WeChat behavior)
                    if (Math.Abs(e.X - _dragStart.X) < 4 && Math.Abs(e.Y - _dragStart.Y) < 4)
                        _sel = _hoverWin;
                    else
                        _sel = RectFrom(_dragStart, e.Location);
                    if (_sel.Width < MIN_SEL || _sel.Height < MIN_SEL)
                        _sel = new Rectangle(_sel.X, _sel.Y, Math.Max(_sel.Width, MIN_SEL), Math.Max(_sel.Height, MIN_SEL));
                    EnterSelected();
                    break;

                case State.Moving:
                case State.Resizing:
                    _resizeHandle = -1;
                    EnterSelected();
                    break;

                case State.Drawing:
                    if (_current != null)
                    {
                        bool keep = _current.Path != null
                            ? _current.Path.Count > 1
                            : (Math.Abs(_current.A.X - _current.B.X) > 2 || Math.Abs(_current.A.Y - _current.B.Y) > 2);
                        if (keep) _annos.Add(_current);
                        _current = null;
                    }
                    _state = State.Selected;
                    Invalidate();
                    break;
            }
        }

        private void EnterSelected()
        {
            _state = State.Selected;
            PlaceToolbar();
            _toolbar.Visible = true;
            _style.Visible = _toolbar.ActiveTool != "";
            Invalidate();
        }

        // ---------- 键盘 ----------
        private void OnKey(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                // Esc 同样分级：先收文字框 -> 取消正在画的一笔 -> 取消选中 -> 退出当前工具 -> 关闭
                if (_textBox != null) { CommitTextBox(); return; }
                if (_state == State.Drawing) { _current = null; _state = State.Selected; Invalidate(); return; }
                if (_selAnno >= 0) { _selAnno = -1; Invalidate(); return; }
                if (_toolbar.ActiveTool != "") { _toolbar.ActiveTool = ""; _style.Visible = false; _toolbar.Invalidate(); UpdateCursor(_mouse); return; }
                Close();
                return;
            }
            if (e.KeyCode == Keys.Delete && _selAnno >= 0 && _selAnno < _annos.Count && _state == State.Selected)
            {
                _annos.RemoveAt(_selAnno);   // Delete 删除选中的标注
                _selAnno = -1;
                Invalidate();
                e.Handled = true;
                return;
            }
            // 方向键微调选区（Shift = 每次 10px），标注随选区同步平移
            if (_state == State.Selected && _textBox == null &&
                (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right ||
                 e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
            {
                int step = e.Shift ? 10 : 1;
                int dx = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0;
                int dy = e.KeyCode == Keys.Up ? -step : e.KeyCode == Keys.Down ? step : 0;
                NudgeSelection(dx, dy);
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Enter && _state == State.Selected) Confirm();
            else if (e.Control && e.KeyCode == Keys.Z) Undo();
            else if (e.Control && e.KeyCode == Keys.S && _state == State.Selected) SaveAs();
            else if (e.Control && e.KeyCode == Keys.C && _state == State.Selected) Confirm();
        }

        /// <summary>按像素平移选区（方向键微调），工具条位置与标注一并跟随。</summary>
        private void NudgeSelection(int dx, int dy)
        {
            int nx = Clamp(_sel.X + dx, 0, ClientSize.Width - _sel.Width);
            int ny = Clamp(_sel.Y + dy, 0, ClientSize.Height - _sel.Height);
            int ax = nx - _sel.X, ay = ny - _sel.Y;
            if (ax == 0 && ay == 0) return;
            _sel.X = nx; _sel.Y = ny;
            TranslateAnnotations(ax, ay);
            PlaceToolbar();
            Invalidate();
        }

        /// <summary>把所有标注（含正在绘制的一笔）整体平移 (dx, dy)。</summary>
        private void TranslateAnnotations(int dx, int dy)
        {
            if (dx == 0 && dy == 0) return;
            foreach (var a in _annos) a.Offset(dx, dy);
            if (_current != null) _current.Offset(dx, dy);
        }

        // ---------- toolbar ----------
        private void OnToolbarClick(string id)
        {
            switch (id)
            {
                case "rect": case "ellipse": case "arrow":
                case "pen": case "mosaic": case "text":
                    CommitTextBox();
                    _toolbar.ActiveTool = _toolbar.ActiveTool == id ? "" : id;
                    _style.Visible = _toolbar.ActiveTool != "";
                    _toolbar.Invalidate();
                    Cursor = _toolbar.ActiveTool != "" ? Cursors.Cross : Cursors.Default;
                    break;
                case "undo": Undo(); break;
                case "save": SaveAs(); break;
                case "ocr": RunOcr(); break;
                case "matte": RunMatting(); break;
                case "longshot":
                    CommitTextBox();
                    LongShotRegion = ToScreen(_sel);
                    Close();
                    break;
                case "record":
                    CommitTextBox();
                    RecordRegion = ToScreen(_sel);
                    Close();
                    break;
                case "cancel": Close(); break;
                case "ok": Confirm(); break;
            }
        }

        private void Undo()
        {
            CommitTextBox();
            if (_annos.Count > 0)
            {
                _annos.RemoveAt(_annos.Count - 1);
                _selAnno = -1;
                Invalidate();
            }
        }

        // ---------- text tool ----------
        private void PlaceTextBox(Point p)
        {
            CommitTextBox();
            _textPos = p;
            _textBox = new TextBox
            {
                Location = p,
                BorderStyle = BorderStyle.FixedSingle,
                MinimumSize = new Size(60, 0),
                Width = 120
            };
            ApplyTextBoxStyle();
            _textBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
                { e.SuppressKeyPress = true; CommitTextBox(); }
            };
            _textBox.LostFocus += delegate { CommitTextBox(); };
            Controls.Add(_textBox);
            _textBox.Focus();
        }

        private void ApplyTextBoxStyle()
        {
            if (_textBox == null) return;
            float fs = 10 + _style.SelWidth * 2;   // 字号随粗细无级变化
            _textBox.Font = new Font("Microsoft YaHei UI", fs, FontStyle.Regular, GraphicsUnit.Pixel);
            _textBox.ForeColor = _style.SelColor;
        }

        private void CommitTextBox()
        {
            if (_textBox == null) return;
            var tb = _textBox; _textBox = null; // guard against LostFocus reentry
            if (tb.Text.Trim().Length > 0)
            {
                float fs = 10 + _style.SelWidth * 2;
                _annos.Add(new Annotation
                {
                    Kind = ToolKind.Text,
                    Text = tb.Text,
                    A = new Point(_textPos.X + 2, _textPos.Y + 2),
                    Color = _style.SelColor,
                    Font = new Font("Microsoft YaHei UI", fs, FontStyle.Regular, GraphicsUnit.Pixel)
                });
            }
            Controls.Remove(tb);
            tb.Dispose();
            Invalidate();
        }

        private Annotation NewAnnotation(string tool, Point p)
        {
            var a = new Annotation { Color = _style.SelColor, Width = _style.SelWidth, A = p, B = p };
            switch (tool)
            {
                case "rect": a.Kind = ToolKind.Rect; break;
                case "ellipse": a.Kind = ToolKind.Ellipse; break;
                case "arrow": a.Kind = ToolKind.Arrow; break;
                case "pen": a.Kind = ToolKind.Pen; a.Path = new List<Point> { p }; break;
                case "mosaic": a.Kind = ToolKind.Mosaic; a.Path = new List<Point> { p }; break;
            }
            return a;
        }

        // ---------- selection geometry ----------
        private static Rectangle RectFrom(Point a, Point b)
        {
            return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                                 Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }
        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static Point ClampPoint(Point p, Rectangle r)
        {
            return new Point(Clamp(p.X, r.Left, r.Right), Clamp(p.Y, r.Top, r.Bottom));
        }

        private Point[] HandlePoints()
        {
            int mx = _sel.X + _sel.Width / 2, my = _sel.Y + _sel.Height / 2;
            return new[]
            {
                new Point(_sel.Left, _sel.Top), new Point(mx, _sel.Top), new Point(_sel.Right, _sel.Top),
                new Point(_sel.Right, my), new Point(_sel.Right, _sel.Bottom), new Point(mx, _sel.Bottom),
                new Point(_sel.Left, _sel.Bottom), new Point(_sel.Left, my)
            };
        }

        private int HitHandle(Point p)
        {
            var hp = HandlePoints();
            for (int i = 0; i < hp.Length; i++)
            {
                var r = new Rectangle(hp[i].X - HANDLE, hp[i].Y - HANDLE, HANDLE * 2, HANDLE * 2);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        private void ResizeSel(Point p)
        {
            int l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom;
            switch (_resizeHandle)
            {
                case 0: l = p.X; t = p.Y; break;
                case 1: t = p.Y; break;
                case 2: r = p.X; t = p.Y; break;
                case 3: r = p.X; break;
                case 4: r = p.X; b = p.Y; break;
                case 5: b = p.Y; break;
                case 6: l = p.X; b = p.Y; break;
                case 7: l = p.X; break;
            }
            _sel = Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
            _sel.Intersect(new Rectangle(Point.Empty, ClientSize));
        }

        private void UpdateCursor(Point p)
        {
            if (_toolbar.ActiveTool != "") { Cursor = Cursors.Cross; return; }
            int h = _annos.Count == 0 ? HitHandle(p) : -1;
            switch (h)
            {
                case 0: case 4: Cursor = Cursors.SizeNWSE; break;
                case 2: case 6: Cursor = Cursors.SizeNESW; break;
                case 1: case 5: Cursor = Cursors.SizeNS; break;
                case 3: case 7: Cursor = Cursors.SizeWE; break;
                default: Cursor = _sel.Contains(p) ? Cursors.SizeAll : Cursors.Default; break;
            }
        }

        private void PlaceToolbar()
        {
            int x = Clamp(_sel.Right - _toolbar.Width, 4, ClientSize.Width - _toolbar.Width - 4);
            int y = _sel.Bottom + 8;
            bool below = true;
            if (y + _toolbar.Height + _style.Height + 4 > ClientSize.Height)
            {
                y = _sel.Top - 8 - _toolbar.Height; below = false;
                if (y < 4) { y = _sel.Bottom - _toolbar.Height - 8; below = true; } // inside
            }
            _toolbar.Location = new Point(x, y);
            int sy = below ? y + _toolbar.Height + 4 : y - _style.Height - 4;
            _style.Location = new Point(Clamp(_sel.Right - _style.Width, 4, ClientSize.Width - _style.Width - 4), sy);
        }

        // ---------- output ----------
        private Bitmap RenderResult()
        {
            // 抠图结果直接作为输出（保留 alpha 透明通道）
            if (_matted != null) return (Bitmap)_matted.Clone();

            var bmp = new Bitmap(_sel.Width, _sel.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(_screen, new Rectangle(0, 0, _sel.Width, _sel.Height), _sel, GraphicsUnit.Pixel);
                g.TranslateTransform(-_sel.X, -_sel.Y);
                foreach (var a in _annos) a.Draw(g, GetPixelated());
            }
            return bmp;
        }

        private void Confirm()
        {
            CommitTextBox();
            if (_sel.Width < 1 || _sel.Height < 1) return;
            using (var bmp = RenderResult())
                SetClipboardImage(bmp);
            Close();
        }

        /// <summary>
        /// 写入剪贴板（带重试）。剪贴板被其他程序短暂占用是常见情况，
        /// 之前直接抛异常会让整个截图成果丢失并崩溃，这里改为重试后给出提示。
        /// </summary>
        private static void SetClipboardImage(Bitmap bmp)
        {
            for (int i = 0; i < 6; i++)
            {
                try { Clipboard.SetImage(bmp); return; }
                catch (System.Runtime.InteropServices.ExternalException)
                { System.Threading.Thread.Sleep(70); }
            }
            MessageBox.Show("复制失败：剪贴板正被其他程序占用，请稍后再试。", "快截",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// 智能抠图：对当前选区做 AI 去背景，结果以透明 PNG 预览（棋盘格底），
        /// 之后点“完成”复制或“保存”另存为，透明通道都会保留。
        /// </summary>
        private void RunMatting()
        {
            CommitTextBox();
            if (_sel.Width < 8 || _sel.Height < 8) return;

            var engine = OnnxMatting.Get();
            if (engine == null)
            {
                MessageBox.Show("抠图功能未就绪：plugins\\sam\\ 下需要 encoder.onnx 和 decoder.onnx。\r\n" +
                                "详见 plugins\\MODEL_GUIDE.md。", "快截",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 打开交互式抠图编辑窗：标记前景/背景 → 自动重抠 → 画笔精修 → 复制 / 保存
            using (var src = RenderResult())
            using (var f = new MattingForm(src))
            {
                f.ShowDialog(this);
                var edited = f.Result;
                if (_matted != null) _matted.Dispose();
                _matted = edited;
                Invalidate();
            }
        }

        private void RunOcr()
        {
            CommitTextBox();
            if (_sel.Width < 4 || _sel.Height < 4) return;
            Bitmap crop = RenderResult(); // 位图所有权转交给 OCR 结果窗口
            Hide();

            // 后台线程识别，避免 UI 假死；忙碌窗给出过程反馈
            string text = null;
            string engine = null;
            using (var done = new System.Threading.ManualResetEventSlim(false))
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try { text = Ocr.Recognize(crop, out engine); }
                    catch { text = null; engine = "识别失败"; }
                    done.Set();
                });
                using (var busy = new BusyForm("正在识别文字…"))
                {
                    busy.Show();
                    while (!done.IsSet) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                    busy.Close();
                }
            }

            try
            {
                using (var win = new OcrResultForm(crop, text, engine))
                    win.ShowDialog();
            }
            finally
            {
                crop.Dispose();
            }
            Close();
        }

        private void SaveAs()
        {
            CommitTextBox();
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|位图|*.bmp";
                dlg.FileName = "SnapCut_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                // 使用设置里的图片保存位置，目录无效时回退到系统“图片”文件夹
                string dir = Settings.Current.SaveFolder;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                dlg.InitialDirectory = dir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var bmp = RenderResult())
                    {
                        var fmt = ImageFormat.Png;
                        string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                        if (ext == ".jpg" || ext == ".jpeg") fmt = ImageFormat.Jpeg;
                        else if (ext == ".bmp") fmt = ImageFormat.Bmp;
                        bmp.Save(dlg.FileName, fmt);
                        // “保存后同时复制到剪贴板”设置在这里生效
                        if (Settings.Current.CopyAfterSave) SetClipboardImage(bmp);
                    }
                    Close();
                }
                catch (Exception ex)
                {
                    // 磁盘满 / 无写权限等情况：提示而不是崩溃，且不关闭覆盖层（可重试）
                    MessageBox.Show("保存失败：" + ex.Message, "快截",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private Bitmap GetPixelated()
        {
            if (_pixelated == null)
            {
                const int block = 12;
                int sw = Math.Max(1, _screen.Width / block), sh = Math.Max(1, _screen.Height / block);
                using (var small = new Bitmap(sw, sh))
                {
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.Bilinear;
                        g.DrawImage(_screen, 0, 0, sw, sh);
                    }
                    _pixelated = new Bitmap(_screen.Width, _screen.Height);
                    using (var g = Graphics.FromImage(_pixelated))
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(small, 0, 0, _screen.Width, _screen.Height);
                    }
                }
            }
            return _pixelated;
        }

        // ---------- painting ----------
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // 样式面板上滚动滚轮：线条 / 笔刷粗细 ±1 细粒度微调
            if (_style != null && _style.Visible && _style.Bounds.Contains(e.Location))
                _style.NudgeWidth(e.Delta > 0 ? 0.5f : -0.5f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImageUnscaled(_screen, 0, 0);

            Rectangle active = _state == State.Idle ? _hoverWin : _sel;

            // 抠图结果：选区内以棋盘格衬托透明区域
            if (_matted != null && _state != State.Idle)
            {
                var clip0 = g.Clip.Clone();
                g.SetClip(_sel);
                DrawCheckerboard(g, _sel);
                g.DrawImageUnscaled(_matted, _sel.X, _sel.Y);
                g.Clip = clip0;
            }

            // annotations, clipped to the selection
            if (_state != State.Idle && (_annos.Count > 0 || _current != null))
            {
                var clip = g.Clip.Clone();
                g.SetClip(_sel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var a in _annos) a.Draw(g, GetPixelated());
                if (_current != null) _current.Draw(g, GetPixelated());
                g.SmoothingMode = SmoothingMode.None;
                g.Clip = clip;

                // 选中指示：蓝色虚线包围盒
                if (_selAnno >= 0 && _selAnno < _annos.Count)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var pen = new Pen(Color.FromArgb(0, 120, 215), 1.2f) { DashStyle = DashStyle.Dash })
                        g.DrawRectangle(pen, _annos[_selAnno].Bounds());
                }
            }

            // dim everything outside the active rect
            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                var cs = ClientSize;
                g.FillRectangle(dim, 0, 0, cs.Width, active.Top);
                g.FillRectangle(dim, 0, active.Top, active.Left, active.Height);
                g.FillRectangle(dim, active.Right, active.Top, cs.Width - active.Right, active.Height);
                g.FillRectangle(dim, 0, active.Bottom, cs.Width, cs.Height - active.Bottom);
            }

            // border + handles
            if (active.Width > 0 && active.Height > 0)
            {
                using (var pen = new Pen(WeGreen, 2f))
                    g.DrawRectangle(pen, active.X, active.Y, active.Width, active.Height);

                if (_state == State.Selected || _state == State.Moving || _state == State.Resizing || _state == State.Drawing)
                {
                    using (var br = new SolidBrush(Color.White))
                    using (var pen = new Pen(WeGreen, 1.4f))
                        foreach (var p in HandlePoints())
                        {
                            var r = new Rectangle(p.X - 3, p.Y - 3, 6, 6);
                            g.FillRectangle(br, r);
                            g.DrawRectangle(pen, r);
                        }
                }
                DrawSizeBadge(g, active);
            }

            if (_state == State.Idle || _state == State.Dragging)
                DrawMagnifier(g);
        }

        private void DrawSizeBadge(Graphics g, Rectangle r)
        {
            string txt = r.Width + " × " + r.Height;
            using (var f = new Font("Segoe UI", 9f))
            {
                var sz = g.MeasureString(txt, f);
                int bx = Clamp(r.X, 2, ClientSize.Width - (int)sz.Width - 14);
                int by = r.Y - (int)sz.Height - 10;
                if (by < 2) by = r.Y + 6;
                var rect = new Rectangle(bx, by, (int)sz.Width + 12, (int)sz.Height + 6);
                using (var gp = Toolbar.Rounded(rect, 4))
                using (var br = new SolidBrush(Color.FromArgb(180, 20, 20, 20)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(br, gp);
                    g.SmoothingMode = SmoothingMode.None;
                }
                using (var br = new SolidBrush(Color.White))
                    g.DrawString(txt, f, br, bx + 6, by + 3);
            }
        }

        private void DrawMagnifier(Graphics g)
        {
            const int zoom = 8, cols = 17, rows = 13;
            int w = cols * zoom, h = rows * zoom;
            int mx = _mouse.X + 20, my = _mouse.Y + 24;
            int infoH = 34;
            if (mx + w + 4 > ClientSize.Width) mx = _mouse.X - w - 20;
            if (my + h + infoH + 4 > ClientSize.Height) my = _mouse.Y - h - infoH - 24;

            var src = new Rectangle(_mouse.X - cols / 2, _mouse.Y - rows / 2, cols, rows);
            var dst = new Rectangle(mx, my, w, h);

            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.FillRectangle(Brushes.Black, mx - 1, my - 1, w + 2, h + infoH + 2);
            g.DrawImage(_screen, dst, src, GraphicsUnit.Pixel);
            g.InterpolationMode = InterpolationMode.Default;

            // crosshair
            using (var pen = new Pen(Color.FromArgb(160, WeGreen), zoom))
            {
                g.DrawLine(pen, mx, my + h / 2, mx + w, my + h / 2);
                g.DrawLine(pen, mx + w / 2, my, mx + w / 2, my + h);
            }
            using (var pen = new Pen(Color.White, 1f))
                g.DrawRectangle(pen, mx, my, w, h);

            Color px = _mouse.X >= 0 && _mouse.Y >= 0 && _mouse.X < _screen.Width && _mouse.Y < _screen.Height
                ? _screen.GetPixel(_mouse.X, _mouse.Y) : Color.Black;
            using (var f = new Font("Segoe UI", 8f))
            using (var br = new SolidBrush(Color.White))
            {
                var sp = ToScreen(new Rectangle(_mouse, Size.Empty));
                g.DrawString("POS: " + sp.X + ", " + sp.Y, f, br, mx + 3, my + h + 2);
                g.DrawString(string.Format("RGB: #{0:X2}{1:X2}{2:X2}", px.R, px.G, px.B), f, br, mx + 3, my + h + 17);
                using (var cb = new SolidBrush(px))
                    g.FillRectangle(cb, mx + w - 18, my + h + 6, 14, 14);
            }
        }

        /// <summary>绘制透明棋盘格底（用于展示抠图后的透明区域）。</summary>
        private static void DrawCheckerboard(Graphics g, Rectangle r)
        {
            const int cell = 8;
            using (var light = new SolidBrush(Color.FromArgb(232, 232, 232)))
            using (var white = new SolidBrush(Color.White))
            {
                g.FillRectangle(white, r);
                for (int y = r.Top; y < r.Bottom; y += cell)
                    for (int x = r.Left; x < r.Right; x += cell)
                        if ((((x - r.Left) / cell) + ((y - r.Top) / cell)) % 2 == 0)
                            g.FillRectangle(light, x, y,
                                Math.Min(cell, r.Right - x), Math.Min(cell, r.Bottom - y));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _screen.Dispose();
                if (_pixelated != null) _pixelated.Dispose();
                if (_matted != null) { _matted.Dispose(); _matted = null; }
            }
            base.Dispose(disposing);
        }
    }
}
