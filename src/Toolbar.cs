using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>
    /// 微信风格浮动工具条。所有图标均用 GDI+ 代码绘制（无图片资源）。
    /// 通过 Clicked 事件把按钮 id 抛给覆盖层，由覆盖层决定执行什么动作。
    /// </summary>
    internal sealed class Toolbar : Control
    {
        public const int BTN = 30, PAD = 5, SEP = 7;

        private static readonly Color WeGreen = Color.FromArgb(7, 193, 96);
        private static readonly Color IconGray = Color.FromArgb(74, 74, 74);
        private static readonly Color HoverBg = Color.FromArgb(235, 235, 235);
        private static readonly Color ActiveBg = Color.FromArgb(219, 244, 230);

        // 各按钮的中文提示（鼠标悬停时显示）
        private static readonly Dictionary<string, string> Tips = new Dictionary<string, string>
        {
            { "rect", "矩形标注" }, { "ellipse", "椭圆标注" }, { "arrow", "箭头标注" },
            { "pen", "画笔" }, { "mosaic", "马赛克" }, { "text", "文字" },
            { "undo", "撤销 (Ctrl+Z)" }, { "save", "保存到文件 (Ctrl+S)" },
            { "ocr", "文字识别 (OCR)" }, { "longshot", "长截图（滚动拼接）" },
            { "matte", "智能抠图（AI 去背景）" },
            { "record", "录制该区域" }, { "cancel", "取消 (Esc)" }, { "ok", "完成并复制 (Enter)" }
        };

        private readonly string[] _items;
        private readonly List<Rectangle> _bounds = new List<Rectangle>();
        private readonly ToolTip _tip = new ToolTip();
        private int _hover = -1;
        public string ActiveTool = "";
        public event Action<string> Clicked;

        public Toolbar(CaptureMode mode)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            List<string> list;
            if (mode == CaptureMode.Record)
                list = new List<string> { "record", "|", "cancel" };
            else if (mode == CaptureMode.LongShot)
                list = new List<string> { "longshot", "|", "cancel" };
            else
            {
                list = new List<string> { "rect", "ellipse", "arrow", "pen", "mosaic", "text", "|",
                                          "undo", "|", "longshot", "ocr", "save" };
                // 智能抠图：入口默认隐藏（功能暂停推进，代码保留待后续开发）。
                // 在 config.ini 里把 MattingEnabled 设为 True 即可重新显示该按钮。
                if (Settings.Current.MattingEnabled && OnnxMatting.Available) list.Add("matte");
                list.Add("record");
                list.Add("|");
                list.Add("cancel");
                list.Add("ok");
            }
            _items = list.ToArray();

            int w = PAD;
            foreach (var it in _items)
            {
                if (it == "|") { _bounds.Add(new Rectangle(w + 2, 8, 1, BTN - 10)); w += SEP; }
                else { _bounds.Add(new Rectangle(w, PAD, BTN, BTN)); w += BTN + 2; }
            }
            Size = new Size(w + PAD - 2, BTN + PAD * 2);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (var gp = Rounded(new Rectangle(0, 0, Width, Height), 6))
                Region = new Region(gp);
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

        /// <summary>命中测试：返回 p 所在按钮的下标（分隔符返回 -1）。</summary>
        private int HitIndex(Point p)
        {
            for (int i = 0; i < _items.Length; i++)
                if (_items[i] != "|" && _bounds[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitIndex(e.Location);
            if (h != _hover)
            {
                _hover = h;
                Invalidate();
                // 悬停时显示中文功能提示；移出按钮则清除
                string text;
                _tip.SetToolTip(this, h >= 0 && Tips.TryGetValue(_items[h], out text) ? text : null);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            _tip.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // 快速点击时 hover 可能还没来得及更新，这里重新做命中测试，避免丢点击
            int h = HitIndex(e.Location);
            if (h >= 0 && Clicked != null) Clicked(_items[h]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            using (var pen = new Pen(Color.FromArgb(220, 220, 220)))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            for (int i = 0; i < _items.Length; i++)
            {
                string it = _items[i];
                var r = _bounds[i];
                if (it == "|")
                {
                    using (var pen = new Pen(Color.FromArgb(225, 225, 225)))
                        g.DrawLine(pen, r.X, r.Y, r.X, r.Bottom);
                    continue;
                }
                bool active = it == ActiveTool;
                if (active || i == _hover)
                    using (var br = new SolidBrush(active ? ActiveBg : HoverBg))
                    using (var gp = Rounded(r, 4))
                        g.FillPath(br, gp);

                DrawIcon(g, it, r, active);
            }
        }

        private void DrawIcon(Graphics g, string it, Rectangle r, bool active)
        {
            Color c = active ? WeGreen : IconGray;
            if (it == "cancel") c = Color.FromArgb(230, 70, 70);
            if (it == "ok") c = WeGreen;
            if (it == "record") c = Color.FromArgb(230, 70, 70);

            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (var p = new Pen(c, 1.8f))
            using (var br = new SolidBrush(c))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                switch (it)
                {
                    case "rect":
                        g.DrawRectangle(p, cx - 7, cy - 6, 14, 12);
                        break;
                    case "ellipse":
                        g.DrawEllipse(p, cx - 8, cy - 6, 16, 12);
                        break;
                    case "arrow":
                        g.DrawLine(p, cx - 7, cy + 7, cx + 6, cy - 6);
                        g.DrawLine(p, cx + 6, cy - 6, cx - 1, cy - 6);
                        g.DrawLine(p, cx + 6, cy - 6, cx + 6, cy + 1);
                        break;
                    case "pen":
                        g.DrawLine(p, cx - 7, cy + 7, cx + 4, cy - 4);
                        g.DrawLine(p, cx + 4, cy - 4, cx + 7, cy - 7);
                        g.FillPolygon(br, new[] { new Point(cx - 8, cy + 8), new Point(cx - 4, cy + 7), new Point(cx - 7, cy + 4) });
                        break;
                    case "mosaic":
                        for (int ix = 0; ix < 3; ix++)
                            for (int iy = 0; iy < 3; iy++)
                                if ((ix + iy) % 2 == 0)
                                    g.FillRectangle(br, cx - 7 + ix * 5, cy - 7 + iy * 5, 4, 4);
                        break;
                    case "text":
                        using (var f = new Font("Segoe UI", 12f, FontStyle.Bold))
                        {
                            var sz = g.MeasureString("A", f);
                            g.DrawString("A", f, br, cx - sz.Width / 2, cy - sz.Height / 2 + 1);
                        }
                        break;
                    case "undo":
                        g.DrawArc(p, cx - 6, cy - 5, 12, 12, -180, -160);
                        g.FillPolygon(br, new[] { new Point(cx - 9, cy - 5), new Point(cx - 1, cy - 8), new Point(cx - 2, cy - 1) });
                        break;
                    case "save":
                        g.DrawLine(p, cx, cy - 7, cx, cy + 3);
                        g.DrawLine(p, cx - 4, cy - 1, cx, cy + 3);
                        g.DrawLine(p, cx + 4, cy - 1, cx, cy + 3);
                        g.DrawLine(p, cx - 7, cy + 7, cx + 7, cy + 7);
                        break;
                    case "record":
                        g.DrawEllipse(p, cx - 8, cy - 8, 16, 16);
                        g.FillEllipse(br, cx - 4, cy - 4, 8, 8);
                        break;
                    case "matte":
                        // 剪刀 = 智能抠图（AI 去背景）
                        g.DrawLine(p, cx - 6, cy - 6, cx + 5, cy + 5);
                        g.DrawLine(p, cx + 6, cy - 6, cx - 5, cy + 5);
                        g.DrawEllipse(p, cx - 8, cy + 2, 5, 5);
                        g.DrawEllipse(p, cx + 3, cy + 2, 5, 5);
                        break;
                    case "longshot":
                        // page with a downward arrow = capture long / scrolling
                        g.DrawRectangle(p, cx - 5, cy - 8, 10, 16);
                        g.DrawLine(p, cx, cy - 4, cx, cy + 4);
                        g.DrawLine(p, cx - 3, cy + 1, cx, cy + 4);
                        g.DrawLine(p, cx + 3, cy + 1, cx, cy + 4);
                        break;
                    case "ocr":
                        // dashed frame with "T" = text recognition
                        using (var dp = new Pen(c, 1.6f))
                        {
                            dp.DashStyle = DashStyle.Dash;
                            g.DrawRectangle(dp, cx - 8, cy - 7, 16, 14);
                        }
                        using (var tf = new Font("Segoe UI", 8f, FontStyle.Bold))
                        {
                            var sz = g.MeasureString("T", tf);
                            g.DrawString("T", tf, br, cx - sz.Width / 2, cy - sz.Height / 2);
                        }
                        break;
                    case "cancel":
                        using (var p2 = new Pen(c, 2.2f))
                        {
                            p2.StartCap = LineCap.Round; p2.EndCap = LineCap.Round;
                            g.DrawLine(p2, cx - 6, cy - 6, cx + 6, cy + 6);
                            g.DrawLine(p2, cx + 6, cy - 6, cx - 6, cy + 6);
                        }
                        break;
                    case "ok":
                        using (var p2 = new Pen(c, 2.4f))
                        {
                            p2.StartCap = LineCap.Round; p2.EndCap = LineCap.Round;
                            g.DrawLine(p2, cx - 7, cy, cx - 2, cy + 5);
                            g.DrawLine(p2, cx - 2, cy + 5, cx + 7, cy - 5);
                        }
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Color swatches + stepless width slider, shown while a drawing tool is active.
    /// 宽度无级滑块（0.5–30px 连续可调）+ 滚轮 ±0.5 微调，左侧实时显示当前数值。
    /// </summary>
    internal sealed class StylePanel : Control
    {
        private static readonly Color[] Palette =
        {
            Color.FromArgb(230, 60, 50), Color.FromArgb(255, 140, 0), Color.FromArgb(255, 200, 0),
            Color.FromArgb(7, 193, 96), Color.FromArgb(0, 120, 215), Color.FromArgb(150, 60, 190),
            Color.Black, Color.White
        };

        public const float MinWidth = 0.5f;
        public const float MaxWidth = 30f;

        public Color SelColor = Palette[0];
        public float SelWidth = 4f;
        public event Action Changed;

        private readonly List<Rectangle> _colR = new List<Rectangle>();
        private readonly Rectangle _sliderR = new Rectangle(34, 6, 106, 20);
        private bool _sliding;

        public StylePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            int x = 150;
            for (int i = 0; i < Palette.Length; i++) { _colR.Add(new Rectangle(x, 8, 16, 16)); x += 20; }
            Size = new Size(x + 4, 32);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (var gp = Toolbar.Rounded(new Rectangle(0, 0, Width, Height), 6))
                Region = new Region(gp);
        }

        /// <summary>微调宽度（滚轮 / 外部调用），范围 [MinWidth, MaxWidth]。</summary>
        public void NudgeWidth(float delta)
        {
            float w = Math.Max(MinWidth, Math.Min(MaxWidth, SelWidth + delta));
            if (w == SelWidth) return;
            SelWidth = w;
            Fire();
        }

        private void SetFromX(int x)
        {
            float t = (float)Math.Max(0, Math.Min(x - _sliderR.Left, _sliderR.Width)) / _sliderR.Width;
            float w = (float)Math.Round((MinWidth + t * (MaxWidth - MinWidth)) * 2) / 2f;   // 0.5px 步进，足够"无级"且数值干净
            if (w == SelWidth) return;
            SelWidth = w;
            Fire();
        }

        private int KnobX()
        {
            float t = (SelWidth - MinWidth) / (MaxWidth - MinWidth);
            return _sliderR.Left + (int)(t * _sliderR.Width);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_sliderR.Contains(e.Location) ||
                (e.Y >= 0 && e.Y <= Height && e.X >= 20 && e.X < 150))
            {
                _sliding = true;
                SetFromX(e.X);
                return;
            }
            for (int i = 0; i < _colR.Count; i++)
                if (_colR[i].Contains(e.Location)) { SelColor = Palette[i]; Fire(); return; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_sliding && e.Button == MouseButtons.Left) SetFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _sliding = false;
        }

        private void Fire() { Invalidate(); if (Changed != null) Changed(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            using (var pen = new Pen(Color.FromArgb(220, 220, 220)))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            // 左侧：当前宽度数值（px，支持 0.5 步进）
            TextRenderer.DrawText(g, SelWidth.ToString("0.#"),
                new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold),
                new Rectangle(2, 6, 30, 20), Color.FromArgb(80, 80, 80),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // 无级滑块：滑槽 + 已选段（微信绿）+ 旋钮
            int cy = _sliderR.Top + _sliderR.Height / 2;
            int kx = KnobX();
            using (var pen = new Pen(Color.FromArgb(214, 216, 220), 4f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, _sliderR.Left + 3, cy, _sliderR.Right - 3, cy);
            }
            using (var pen = new Pen(Color.FromArgb(7, 193, 96), 4f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, _sliderR.Left + 3, cy, kx, cy);
            }
            using (var br = new SolidBrush(Color.White))
                g.FillEllipse(br, kx - 6, cy - 6, 12, 12);
            using (var pen = new Pen(Color.FromArgb(7, 193, 96), 2f))
                g.DrawEllipse(pen, kx - 6, cy - 6, 12, 12);

            for (int i = 0; i < _colR.Count; i++)
            {
                var r = _colR[i];
                using (var br = new SolidBrush(Palette[i])) g.FillRectangle(br, r);
                using (var pen = new Pen(Palette[i] == SelColor ? Color.FromArgb(80, 80, 80) : Color.FromArgb(200, 200, 200),
                                          Palette[i] == SelColor ? 2f : 1f))
                    g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
        }
    }
}
