using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SimpleShot
{
    internal enum ToolKind { None, Rect, Ellipse, Arrow, Pen, Mosaic, Text }

    /// <summary>一条标注。坐标使用全屏（覆盖层客户区）坐标系。</summary>
    internal sealed class Annotation
    {
        public ToolKind Kind;
        public Color Color;
        public float Width;             // 描边宽度（无级，0.5–30px）
        public Point A, B;              // 矩形 / 椭圆 / 箭头的两个端点
        public List<Point> Path;        // 画笔 / 马赛克的笔迹
        public string Text;             // 文字工具内容
        public Font Font;               // 文字工具字体

        /// <summary>把标注整体平移 (dx, dy)。选区移动时调用，保持标注与画面的相对位置。</summary>
        public void Offset(int dx, int dy)
        {
            A = new Point(A.X + dx, A.Y + dy);
            B = new Point(B.X + dx, B.Y + dy);
            if (Path != null)
                for (int i = 0; i < Path.Count; i++)
                    Path[i] = new Point(Path[i].X + dx, Path[i].Y + dy);
        }

        /// <summary>把标注绘制到 g 上。pixelated 是预先马赛克化的整屏位图。</summary>
        public void Draw(Graphics g, Bitmap pixelated)
        {
            switch (Kind)
            {
                case ToolKind.Rect:
                    using (var p = MakePen())
                        g.DrawRectangle(p, Norm().X, Norm().Y, Norm().Width, Norm().Height);
                    break;

                case ToolKind.Ellipse:
                    using (var p = MakePen())
                        g.DrawEllipse(p, Norm());
                    break;

                case ToolKind.Arrow:
                    DrawArrow(g);
                    break;

                case ToolKind.Pen:
                    if (Path != null && Path.Count > 1)
                        using (var p = MakePen())
                        {
                            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                            p.LineJoin = LineJoin.Round;
                            g.DrawLines(p, Path.ToArray());
                        }
                    break;

                case ToolKind.Mosaic:
                    DrawMosaic(g, pixelated);
                    break;

                case ToolKind.Text:
                    if (!string.IsNullOrEmpty(Text))
                        using (var br = new SolidBrush(Color))
                            g.DrawString(Text, Font, br, A);
                    break;
            }
        }

        private Pen MakePen() { return new Pen(Color, Width); }

        /// <summary>标注的包围盒（选中框 / 命中测试用），含描边与箭头头部的外扩。</summary>
        public Rectangle Bounds()
        {
            int pad = (int)Math.Ceiling(Width) + 12;
            if (Kind == ToolKind.Text)
            {
                Size sz = TextRenderer.MeasureText(Text ?? "", Font ?? SystemFonts.DefaultFont);
                return new Rectangle(A.X - 2, A.Y - 2, sz.Width + 4, sz.Height + 4);
            }
            int x0 = Math.Min(A.X, B.X), x1 = Math.Max(A.X, B.X);
            int y0 = Math.Min(A.Y, B.Y), y1 = Math.Max(A.Y, B.Y);
            if (Path != null)
                foreach (var p in Path)
                {
                    x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
                    y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
                }
            return Rectangle.Inflate(new Rectangle(x0, y0, x1 - x0, y1 - y0), pad, pad);
        }

        internal Rectangle Norm()
        {
            return new Rectangle(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y),
                                 Math.Abs(A.X - B.X), Math.Abs(A.Y - B.Y));
        }

        /// <summary>Solid tapered arrow like WeChat's.</summary>
        private void DrawArrow(Graphics g)
        {
            double dx = B.X - A.X, dy = B.Y - A.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 2) return;
            double ux = dx / len, uy = dy / len;          // unit vector
            double px = -uy, py = ux;                     // perpendicular

            double headLen = Math.Min(10.0 + Width * 3, len * 0.4);
            double headW = headLen * 0.6;
            double tailW = Math.Max(1.0, Width * 0.8);

            var baseP = new PointF((float)(B.X - ux * headLen), (float)(B.Y - uy * headLen));
            var pts = new PointF[]
            {
                new PointF((float)(A.X + px * tailW), (float)(A.Y + py * tailW)),
                new PointF((float)(baseP.X + px * tailW * 1.6), (float)(baseP.Y + py * tailW * 1.6)),
                new PointF((float)(baseP.X + px * headW), (float)(baseP.Y + py * headW)),
                new PointF(B.X, B.Y),
                new PointF((float)(baseP.X - px * headW), (float)(baseP.Y - py * headW)),
                new PointF((float)(baseP.X - px * tailW * 1.6), (float)(baseP.Y - py * tailW * 1.6)),
                new PointF((float)(A.X - px * tailW), (float)(A.Y - py * tailW)),
            };
            using (var br = new SolidBrush(Color))
                g.FillPolygon(br, pts);
        }

        /// <summary>Reveals the pre-pixelated screen through block-aligned squares along the stroke.</summary>
        private void DrawMosaic(Graphics g, Bitmap pixelated)
        {
            if (Path == null || pixelated == null || Path.Count == 0) return;
            const int block = 12;
            int brush = (int)(12 + Width * 8); // brush size scales with stroke width
            var done = new HashSet<long>();

            using (var region = new Region(Rectangle.Empty))
            {
                foreach (var pt in Path)
                {
                    int gx0 = (pt.X - brush / 2) / block, gy0 = (pt.Y - brush / 2) / block;
                    int gx1 = (pt.X + brush / 2) / block, gy1 = (pt.Y + brush / 2) / block;
                    for (int gx = gx0; gx <= gx1; gx++)
                        for (int gy = gy0; gy <= gy1; gy++)
                        {
                            long key = ((long)gx << 32) | (uint)gy;
                            if (!done.Add(key)) continue;
                            region.Union(new Rectangle(gx * block, gy * block, block, block));
                        }
                }

                var oldClip = g.Clip.Clone();
                g.IntersectClip(region);
                g.DrawImage(pixelated, 0, 0, pixelated.Width, pixelated.Height);
                g.Clip = oldClip;
            }
        }
    }
}
