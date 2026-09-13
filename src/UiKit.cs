using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SimpleShot
{
    // 设计令牌（配色 / 字体 / 圆角）已并入 PreviewForms.cs 的 Ui 类，
    // 本文件只放可复用的现代控件：Card / PillButton / ModernProgress。

    /// <summary>
    /// 应用元信息的**唯一来源**：版本号、作者、联系方式。
    /// 版本号取自程序集（由 build.ps1 写入 src\AssemblyInfo.cs），界面一律经这里读取，
    /// 避免各处硬编码字符串导致改版本时漏改。
    /// 新增任何"显示版本 / 显示作者"的地方，都必须调用本类，不要自己拼。
    /// </summary>
    internal static class AppMeta
    {
        public const string AppName = "快截";
        public const string AppNameEn = "SnapCut";
        public const string Author = "Lvbta";
        public const string Email = "z20160108@s.upc.edu.cn";
        public const string GitHub = "https://github.com/Lvbta/simpleShortCut";

        /// <summary>三段式版本号（如 "1.2.0"）。程序集版本是 4 段，这里截掉末位。</summary>
        public static string Version
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
            }
        }

        /// <summary>带 v 前缀的展示文本（如 "v1.2.0"）。</summary>
        public static string VersionText { get { return "v" + Version; } }

        /// <summary>关于/联系信息的多行文本，供设置页等处直接展示。</summary>
        public static string ContactText
        {
            get
            {
                return "开发者：" + Author + "\r\n"
                     + "邮箱：" + Email + "\r\n"
                     + "GitHub：" + GitHub;
            }
        }
    }

    /// <summary>白底圆角卡片。子控件与卡片边缘至少留 12px，避免直角盖住圆角。</summary>
    internal sealed class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.CardBg;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Ui.Bg);   // 露出窗体底色，圆角外才是"透明"的
            using (var br = new SolidBrush(Ui.CardBg))
            using (var gp = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), 10))
                g.FillPath(br, gp);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var pen = new Pen(Ui.Border))
            using (var gp = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), 10))
                e.Graphics.DrawPath(pen, gp);
        }
    }

    /// <summary>
    /// 现代按钮：Primary（主色实底白字）/ 次要（白底描边灰字），带悬停、按下、禁用三态。
    /// 用 Region 裁圆角，因此放在任何底色上都干净。
    /// </summary>
    internal sealed class PillButton : Control
    {
        private bool _hover, _down;
        public bool Primary;

        public PillButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = false;
            // 注意：这里不能设 Color.Transparent —— 未开启 SupportsTransparentBackColor
            // 会在创建句柄时抛"控件不支持透明的背景色"。圆角由 Region 裁切，背景色本身不可见。
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            using (var gp = Ui.Round(new Rectangle(0, 0, Width, Height), 6))
                Region = new Region(gp);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { _hover = _down = false; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);

            Color fill, text, line;
            if (!Enabled)
            {
                fill = Ui.Disabled; text = Color.FromArgb(168, 172, 178); line = Ui.Disabled;
            }
            else if (Primary)
            {
                fill = _down ? Ui.AccentDark : (_hover ? Color.FromArgb(6, 176, 88) : Ui.Accent);
                text = Color.White; line = fill;
            }
            else
            {
                fill = _down ? Color.FromArgb(236, 239, 242) : (_hover ? Color.FromArgb(249, 250, 251) : Ui.CardBg);
                text = Ui.Text; line = _hover ? Ui.BorderHi : Ui.Border;
            }

            using (var br = new SolidBrush(fill))
            using (var gp = Ui.Round(r, 6))
                g.FillPath(br, gp);
            using (var pen = new Pen(line))
            using (var gp = Ui.Round(r, 6))
                g.DrawPath(pen, gp);

            TextRenderer.DrawText(g, Text, Font, r, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>圆角进度条：绿色圆头进度 + 浅色轨道，右端显示百分比数字。</summary>
    internal sealed class ModernProgress : Control
    {
        private double _pct;

        public ModernProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
        }

        public void Set(double pct)
        {
            _pct = Math.Max(0, Math.Min(100, pct));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int barH = 10;
            int textW = 44;
            var tr = new Rectangle(0, (Height - barH) / 2, Math.Max(10, Width - textW - 8), barH);

            using (var br = new SolidBrush(Color.FromArgb(232, 235, 238)))
            using (var gp = Ui.Round(tr, barH / 2))
                g.FillPath(br, gp);

            int w = (int)(tr.Width * _pct / 100.0 + 0.5);
            if (w > 0)
            {
                using (var br = new SolidBrush(Ui.Accent))
                using (var gp = Ui.Round(new Rectangle(tr.X, tr.Y, Math.Min(w, tr.Width), barH), barH / 2))
                    g.FillPath(br, gp);
            }

            TextRenderer.DrawText(g, ((int)Math.Round(_pct)) + "%", Ui.Sub,
                new Rectangle(Width - textW, 0, textW, Height), Ui.TextSub,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>应用图标：运行时绘制（绿色裁剪框），可指定尺寸。
    /// 放在这里以便各模块（含 --write-icon 导出）共用，不再依赖 MainContext。</summary>
    internal static class AppIcon
    {
        private static readonly Color Green = Color.FromArgb(7, 193, 96);

        public static Icon Create(int size)
        {
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float u = size / 16f;                       // 以 16px 设计为基准等比缩放
                using (var pen = new Pen(Green, 2f * u))
                {
                    g.DrawRectangle(pen, 2 * u, 2 * u, 11 * u, 11 * u);
                    g.DrawLine(pen, 0, 5 * u, 0, 0);
                    g.DrawLine(pen, 0, 0, 5 * u, 0);
                    g.DrawLine(pen, size - 1f, 10 * u, size - 1f, size - 1f);
                    g.DrawLine(pen, size - 1f, size - 1f, 10 * u, size - 1f);
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { NativeMethods.DestroyIcon(h); }
            }
        }
    }

    /// <summary>UI 工具：设计令牌 / 圆角路径 / 胶囊按钮 / 徽标 / 窗体居中，供各弹窗复用。</summary>
    internal static class Ui
    {
        // ---- 设计令牌：统一配色 / 字体层级 ----
        public static readonly Color Accent     = Color.FromArgb(7, 193, 96);    // 主色（微信绿）
        public static readonly Color AccentDark = Color.FromArgb(5, 158, 79);    // 按下态
        public static readonly Color AccentSoft = Color.FromArgb(233, 247, 240); // 选中底 / 浅绿
        public static readonly Color Text       = Color.FromArgb(33, 36, 41);    // 主文字
        public static readonly Color TextSub    = Color.FromArgb(124, 129, 137); // 次要文字
        public static readonly Color Border     = Color.FromArgb(226, 229, 233); // 常规描边
        public static readonly Color BorderHi   = Color.FromArgb(198, 204, 211); // 悬停描边
        public static readonly Color Bg         = Color.FromArgb(246, 247, 249); // 窗体底（浅灰）
        public static readonly Color CardBg     = Color.White;                   // 卡片底
        public static readonly Color Danger     = Color.FromArgb(212, 74, 69);
        public static readonly Color Disabled   = Color.FromArgb(243, 244, 246);

        public static Font Title { get { return new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold); } }
        public static Font Body  { get { return new Font("Microsoft YaHei UI", 9f); } }
        public static Font Sub   { get { return new Font("Microsoft YaHei UI", 8.5f); } }
        public static Font Mono  { get { return new Font("Consolas", 8.75f); } }

        /// <summary>圆角矩形路径（带防退化保护；调用方负责 Dispose）。</summary>
        public static GraphicsPath Round(Rectangle r, int rad)
        {
            var gp = new GraphicsPath();
            if (rad <= 0 || r.Width <= 0 || r.Height <= 0) { gp.AddRectangle(r); return gp; }
            int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        /// <summary>统一的应用版本号文本（如 "v1.2.0"）—— 所有界面版本显示的唯一来源。</summary>
        public static string AppVersion
        {
            get { return AppMeta.VersionText; }   // 统一走 AppMeta，不再各自拼装
        }

        public static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var gp = new GraphicsPath();
            gp.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            gp.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            gp.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            gp.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        /// <summary>
        /// 把窗体居中到"鼠标所在屏幕"的工作区。
        /// 相比 FormStartPosition.CenterScreen 更可靠：后者依赖句柄创建时机，
        /// 无主窗 / 非模态 Show() 时常不生效；且按主屏算，多屏时会跑到别的显示器。
        /// </summary>
        public static void CenterOnScreen(Form f)
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int x = wa.Left + Math.Max(0, (wa.Width - f.Width) / 2);
            int y = wa.Top + Math.Max(0, (wa.Height - f.Height) / 2);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(x, y);
        }

        /// <summary>胶囊按钮：黑底主按钮 / 白底描边次按钮。</summary>
        public static Button Pill(string text, bool primary, int width)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(width, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(30, 30, 30) : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(30, 30, 30),
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(228, 229, 232);
            b.FlatAppearance.MouseOverBackColor =
                primary ? ControlPaint.Light(Color.FromArgb(30, 30, 30)) : Color.FromArgb(245, 245, 245);
            b.HandleCreated += delegate
            {
                using (var gp = Rounded(new Rectangle(0, 0, b.Width, b.Height), 10))
                    b.Region = new Region(gp);
            };
            return b;
        }

        /// <summary>引擎徽标：圆角彩色小标签。</summary>
        public static Control Badge(string text, Color color, Point loc, Font font)
        {
            var p = new Panel { Location = loc };
            int w = TextRenderer.MeasureText(text, font).Width + 18;
            p.Size = new Size(w, 20);
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var gp = Rounded(new Rectangle(0, 0, p.Width, p.Height), 10))
                using (var br = new SolidBrush(color))
                    g.FillPath(br, gp);
                TextRenderer.DrawText(g, text, font, p.ClientRectangle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            return p;
        }

        /// <summary>按引擎名给徽标配色。</summary>
        public static Color EngineColor(string engine)
        {
            if (string.IsNullOrEmpty(engine)) return Color.FromArgb(7, 193, 96);
            if (engine.IndexOf("Umi", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(0, 120, 215);
            if (engine.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(120, 120, 120);
            if (engine.IndexOf("无", StringComparison.Ordinal) >= 0) return Color.FromArgb(220, 80, 80);
            return Color.FromArgb(7, 193, 96);
        }
    }
}
