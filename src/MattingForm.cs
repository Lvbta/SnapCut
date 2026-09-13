using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace SimpleShot
{
    internal sealed class MattingForm : Form
    {
        private static readonly Color TextMain = Color.FromArgb(35, 36, 38);
        private static readonly Color TextSub = Color.FromArgb(130, 133, 138);
        private static readonly Color BorderColor = Color.FromArgb(226, 228, 232);
        private static readonly Color PanelBg = Color.FromArgb(247, 248, 250);
        private static readonly Color Accent = Color.FromArgb(7, 193, 96);
        private static readonly Color Warn = Color.FromArgb(230, 60, 50);
        private static readonly Color EraseColor = Color.FromArgb(0, 120, 215);

        private readonly Bitmap _orig;
        private Bitmap _rgba;
        private readonly Stack<Bitmap> _undo = new Stack<Bitmap>();
        private readonly OnnxMatting _engine;

        private readonly List<Point> _fgPts = new List<Point>();
        private readonly List<Point> _bgPts = new List<Point>();
        private bool _busy;

        private int _mode;      // 0=前景 1=背景 2=擦除 3=恢复
        private int _brushSize = 40;
        private bool _drawing;
        private PointF _lastImg;
        private Point _cursorView = new Point(-1, -1);

        private readonly DbPanel _view;
        private readonly Label _status;
        private Button _btnFg, _btnBg, _btnErase, _btnRestore;
        private readonly TrackBar _slider;
        private readonly Label _brushLbl;

        private sealed class DbPanel : Panel
        {
            public DbPanel() { DoubleBuffered = true; }
        }

        public MattingForm(Bitmap originalRgb)
        {
            _orig = (Bitmap)originalRgb.Clone();
            _rgba = (Bitmap)originalRgb.Clone();
            _engine = OnnxMatting.Get();

            AutoScaleMode = AutoScaleMode.None;
            Text = "AI Matting";
            Icon = MainContext.CreateTrayIcon();
            StartPosition = FormStartPosition.CenterScreen;   // 无 owner 时 CenterParent 不可靠
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(960, 660);
            MinimumSize = new Size(760, 500);

            // ---- Header ----
            var header = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.White };
            header.Paint += (s, e) =>
            {
                using (var p = new Pen(BorderColor))
                    e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            header.Controls.Add(new Label
            {
                Text = "\u667a\u80fd\u62a0\u56fe", Location = new Point(18, 13), AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold), ForeColor = TextMain
            });
            header.Controls.Add(new Label
            {
                Text = "\u70b9\u51fb\u753b\u9762\u6807\u8bb0\u524d\u666f/\u80cc\u666f\uff0c\u53ef\u591a\u70b9\uff0c\u81ea\u52a8\u91cd\u62a0",
                Location = new Point(106, 17), AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5f), ForeColor = TextSub
            });
            var done = MakePill("\u5b8c\u6210", true);
            done.Location = new Point(ClientSize.Width - 16 - 80, 10);
            done.Click += delegate { Close(); };
            header.Controls.Add(done);
            Controls.Add(header);

            // ---- Right sidebar ----
            var side = new Panel { Dock = DockStyle.Right, Width = 160, BackColor = PanelBg };
            side.Paint += (s, e) => { using (var p = new Pen(BorderColor)) e.Graphics.DrawLine(p, 0, 0, 0, side.Height); };

            int y = 14;
            AddGroupLabel(side, "\u4e00\u952e\u62a0\u56fe", ref y);
            _btnFg = AddToolBtn(side, "\u524d\u666f \u2705", Accent, ref y);
            _btnFg.Click += delegate { SetMode(0); };
            _btnBg = AddToolBtn(side, "\u80cc\u666f \u274c", Warn, ref y);
            _btnBg.Click += delegate { SetMode(1); };
            _btnErase = AddToolBtn(side, "\u64e6\u9664 \u270e\ufe0f", EraseColor, ref y);
            _btnErase.Click += delegate { SetMode(2); };
            _btnRestore = AddToolBtn(side, "\u6062\u590d \u21a9\ufe0f", Accent, ref y);
            _btnRestore.Click += delegate { SetMode(3); };

            y += 6;
            AddGroupLabel(side, "\u7b14\u5237", ref y);
            _brushLbl = new Label
            {
                Text = _brushSize + " px", Location = new Point(14, y),
                AutoSize = true, ForeColor = TextMain,
                Font = new Font("Microsoft YaHei UI", 8.5f)
            };
            side.Controls.Add(_brushLbl);
            y += 18;
            _slider = new TrackBar
            {
                Location = new Point(10, y), Width = 140,
                Minimum = 6, Maximum = 120, TickStyle = TickStyle.None, Value = _brushSize
            };
            _slider.ValueChanged += delegate
            {
                _brushSize = _slider.Value;
                _brushLbl.Text = _brushSize + " px";
                InvalidateCursor();
            };
            side.Controls.Add(_slider);
            y += 34;

            AddGroupLabel(side, "\u64cd\u4f5c", ref y);
            var btnAuto = MakePill("\u62a0\u56fe", true);
            btnAuto.Location = new Point(14, y); btnAuto.Size = new Size(132, 32);
            btnAuto.Click += delegate
            {
                _fgPts.Clear(); _bgPts.Clear();
                _fgPts.Add(new Point(_orig.Width / 2, _orig.Height / 2));
                SetMode(0); Rerun();
            };
            side.Controls.Add(btnAuto); y += 38;

            var btnClear = MakePill("\u6e05\u9664", false);
            btnClear.Location = new Point(14, y); btnClear.Size = new Size(62, 28);
            btnClear.Click += delegate { ClearMarks(); };
            side.Controls.Add(btnClear);

            var btnUndo = MakePill("\u64a4\u9500", false);
            btnUndo.Location = new Point(84, y); btnUndo.Size = new Size(62, 28);
            btnUndo.Click += delegate { Undo(); };
            side.Controls.Add(btnUndo);
            y += 34;

            var btnReset = MakePill("\u91cd\u7f6e", false);
            btnReset.Location = new Point(14, y); btnReset.Size = new Size(132, 28);
            btnReset.Click += delegate { ResetAll(); };
            side.Controls.Add(btnReset);

            _status = new Label
            {
                Text = "\u70b9\u51fb\u4e3b\u4f53\u6807\u8bb0\u524d\u666f\uff0c\u6216\u76f4\u63a5\u6309\u300c\u62a0\u56fe\u300d",
                Location = new Point(14, y + 40), AutoSize = false,
                Size = new Size(132, 50), ForeColor = TextSub,
                Font = new Font("Microsoft YaHei UI", 8f)
            };
            side.Controls.Add(_status);
            Controls.Add(side);

            // ---- Preview area ----
            _view = new DbPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(243, 244, 246) };
            _view.Paint += PaintView;
            _view.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (_busy || _engine == null) return;
                var img = ToImage(e.Location);
                if (img.X < 0 || img.Y < 0 || img.X >= _orig.Width || img.Y >= _orig.Height) return;
                if (_mode == 0) { _fgPts.Add(new Point((int)img.X, (int)img.Y)); Rerun(); }
                else if (_mode == 1) { _bgPts.Add(new Point((int)img.X, (int)img.Y)); Rerun(); }
                else { PushUndo(); _drawing = true; _lastImg = img; Stamp(img); InvalidateViewImage(); }
            };
            _view.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (_mode >= 2)
                {
                    InvalidateCursor(); _cursorView = e.Location; InvalidateCursor();
                    if (_drawing) { var p = ToImage(e.Location); StrokeTo(p); _lastImg = p; InvalidateViewImage(); }
                }
                else if (Cursor != Cursors.Cross) Cursor = Cursors.Cross;
            };
            _view.MouseUp += delegate { _drawing = false; };
            _view.MouseLeave += delegate { InvalidateCursor(); _cursorView = new Point(-1, -1); };
            Controls.Add(_view);

            SyncToggles();
            AcceptButton = done;
        }

        public Bitmap Result { get { return (Bitmap)_rgba.Clone(); } }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_mode < 2 || !_view.Bounds.Contains(e.Location)) return;
            int v = Math.Max(6, Math.Min(120, _brushSize + (e.Delta > 0 ? 4 : -4)));
            if (v != _brushSize) { _brushSize = v; _brushLbl.Text = _brushSize + " px"; _slider.Value = v; InvalidateCursor(); }
        }

        private void ClearMarks() { _fgPts.Clear(); _bgPts.Clear(); var old = _rgba; _rgba = (Bitmap)_orig.Clone(); old.Dispose(); _undo.Clear(); _view.Invalidate(); SetStatus("\u5df2\u6e05\u9664"); }
        private void Undo() { if (_undo.Count == 0) { SetStatus("\u65e0\u53ef\u64a4\u9500"); return; } var old = _rgba; _rgba = _undo.Pop(); old.Dispose(); _view.Invalidate(); }
        private void ResetAll() { PushUndo(); _fgPts.Clear(); _bgPts.Clear(); var old = _rgba; _rgba = (Bitmap)_orig.Clone(); old.Dispose(); _view.Invalidate(); SetStatus("\u5df2\u91cd\u7f6e"); }

        private void Rerun()
        {
            if (_busy || _engine == null || _fgPts.Count + _bgPts.Count == 0) return;
            _busy = true; SetStatus("\u5904\u7406\u4e2d\u2026"); Cursor = Cursors.WaitCursor;
            var pts = new List<Point>(_fgPts); pts.AddRange(_bgPts);
            var labels = new List<int>();
            for (int i = 0; i < _fgPts.Count; i++) labels.Add(1);
            for (int i = 0; i < _bgPts.Count; i++) labels.Add(0);

            Bitmap result = null; Exception err = null;
            using (var done = new ManualResetEventSlim(false))
            {
                ThreadPool.QueueUserWorkItem(delegate { try { result = _engine.RemoveBackground(_orig, pts, labels); } catch (Exception ex) { err = ex; } done.Set(); });
                while (!done.IsSet) { Application.DoEvents(); Thread.Sleep(10); }
            }
            _busy = false; Cursor = Cursors.Cross;
            if (err != null) SetStatus("\u5931\u8d25: " + err.Message);
            else { var old = _rgba; _rgba = result; old.Dispose(); _undo.Clear(); SetStatus(_fgPts.Count + " \u524d\u666f / " + _bgPts.Count + " \u80cc\u666f"); }
            _view.Invalidate();
        }

        private void PushUndo() { _undo.Push((Bitmap)_rgba.Clone()); while (_undo.Count > 10) ((Bitmap)_undo.Pop()).Dispose(); }
        private void SetMode(int m) { _mode = m; SyncToggles(); _view.Invalidate(); }

        private void SyncToggles()
        {
            SetBtn(_btnFg, _mode == 0); SetBtn(_btnBg, _mode == 1);
            SetBtn(_btnErase, _mode == 2); SetBtn(_btnRestore, _mode == 3);
        }

        private static void SetBtn(Button b, bool on) { b.BackColor = on ? Color.FromArgb(233, 250, 240) : Color.White; b.ForeColor = on ? Color.FromArgb(4, 138, 70) : TextMain; b.FlatAppearance.BorderColor = on ? Accent : BorderColor; }
        private void SetStatus(string s) { _status.Text = s; _status.Invalidate(); }

        private void Stamp(PointF p)
        {
            float r = _brushSize / 2f;
            using (var g = Graphics.FromImage(_rgba))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (_mode == 2) using (var br = new SolidBrush(Color.Transparent)) g.FillEllipse(br, p.X - r, p.Y - r, r * 2, r * 2);
                else using (var path = new GraphicsPath()) { path.AddEllipse(p.X - r, p.Y - r, r * 2, r * 2); g.SetClip(path); g.DrawImage(_orig, 0, 0, _orig.Width, _orig.Height); }
            }
        }

        private void StrokeTo(PointF p)
        {
            float dx = p.X - _lastImg.X, dy = p.Y - _lastImg.Y, d = (float)Math.Sqrt(dx * dx + dy * dy);
            int n = Math.Max(1, (int)(d / Math.Max(2f, _brushSize / 5f)));
            for (int i = 1; i <= n; i++) { float t = i / (float)n; Stamp(new PointF(_lastImg.X + dx * t, _lastImg.Y + dy * t)); }
            _lastImg = p;
        }

        private Rectangle ImageRect() { float s = FitScale(); return new Rectangle(Math.Max(0, (_view.Width - (int)(_orig.Width * s)) / 2), Math.Max(0, (_view.Height - (int)(_orig.Height * s)) / 2), Math.Max(1, (int)(_orig.Width * s)), Math.Max(1, (int)(_orig.Height * s))); }
        private PointF ToImage(Point p) { var r = ImageRect(); float s = FitScale(); return new PointF((p.X - r.X) / s, (p.Y - r.Y) / s); }
        private float FitScale() { float sx = (_view.Width - 24f) / _orig.Width; float sy = (_view.Height - 24f) / _orig.Height; return Math.Max(0.02f, Math.Min(Math.Min(sx, sy), 6f)); }
        private void InvalidateViewImage() { _view.Invalidate(ImageRect()); }
        private void InvalidateCursor() { if (_cursorView.X < 0) return; float s = FitScale(); int r = (int)(_brushSize / 2f * s) + 6; _view.Invalidate(new Rectangle(_cursorView.X - r, _cursorView.Y - r, r * 2, r * 2)); }

        private void PaintView(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(_view.BackColor);
            var dst = ImageRect(); if (dst.Width <= 1 || dst.Height <= 1) return;
            int tile = Math.Max(6, (int)(8 * FitScale()));
            using (var b1 = new SolidBrush(Color.White)) using (var b2 = new SolidBrush(Color.FromArgb(202, 204, 208)))
            { bool flip = false; for (int y = dst.Top; y < dst.Bottom; y += tile, flip = !flip) { bool f = flip; for (int x = dst.Left; x < dst.Right; x += tile, f = !f) if (f) g.FillRectangle(b2, x, y, Math.Min(tile, dst.Right - x), Math.Min(tile, dst.Bottom - y)); } }
            g.InterpolationMode = FitScale() < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_rgba, dst);
            using (var p = new Pen(BorderColor)) g.DrawRectangle(p, dst.X, dst.Y, dst.Width, dst.Height);
            float scale = FitScale();
            Action<Point, bool> drawMark = delegate(Point ip, bool fg)
            {
                float mx = dst.X + ip.X * scale, my = dst.Y + ip.Y * scale;
                using (var b = new SolidBrush(Color.FromArgb(200, fg ? Accent : Warn))) g.FillEllipse(b, mx - 7, my - 7, 14, 14);
                using (var pen = new Pen(Color.White, 1.6f)) { pen.StartCap = pen.EndCap = LineCap.Round; g.DrawLine(pen, mx - 3, my, mx + 3, my); if (fg) g.DrawLine(pen, mx, my - 3, mx, my + 3); }
            };
            foreach (var p in _fgPts) drawMark(p, true);
            foreach (var p in _bgPts) drawMark(p, false);
            if (_mode >= 2 && _cursorView.X >= 0) { float r = _brushSize / 2f * scale; using (var pen = new Pen(_mode == 2 ? EraseColor : Accent, 1.6f) { DashStyle = DashStyle.Dash }) g.DrawEllipse(pen, _cursorView.X - r, _cursorView.Y - r, r * 2, r * 2); }
        }

        // ---- Widgets ----

        private static void AddGroupLabel(Panel p, string text, ref int y)
        {
            p.Controls.Add(new Label
            {
                Text = text, Location = new Point(14, y), AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Bold), ForeColor = TextSub
            });
            y += 18;
        }

        private static Button AddToolBtn(Panel p, string text, Color color, ref int y)
        {
            var b = new Button
            {
                Text = text, Location = new Point(12, y), Size = new Size(136, 28),
                FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = TextMain,
                Font = new Font("Microsoft YaHei UI", 9f), TextAlign = ContentAlignment.MiddleLeft
            };
            b.FlatAppearance.BorderColor = BorderColor;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 244, 246);
            p.Controls.Add(b);
            y += 32;
            return b;
        }

        private static Button MakePill(string text, bool primary)
        {
            var b = new Button
            {
                Text = text, Size = new Size(80, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Color.White,
                ForeColor = primary ? Color.White : TextMain,
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = primary ? Accent : BorderColor;
            b.HandleCreated += delegate { using (var gp = Ui.Rounded(new Rectangle(0, 0, b.Width, b.Height), 8)) b.Region = new Region(gp); };
            return b;
        }
    }
}
