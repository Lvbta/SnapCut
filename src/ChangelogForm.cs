using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>
    /// 应用内更新日志查看器：与设置界面同款的现代浅色风格——
    /// 顶部大标题 + 记录条数副标题，中部只读阅读区，底部右侧黑色胶囊“关闭”按钮。
    /// 读取 exe 同目录下的 CHANGELOG.md，找不到时给出友好提示。
    /// </summary>
    internal sealed class ChangelogForm : Form
    {
        private static readonly Color TextMain = Color.FromArgb(30, 30, 30);
        private static readonly Color TextSub = Color.FromArgb(140, 140, 140);
        private static readonly Color BorderColor = Color.FromArgb(232, 233, 235);
        private static readonly Color DarkPill = Color.FromArgb(30, 30, 30);

        private bool _centered;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_centered) { _centered = true; Ui.CenterOnScreen(this); }
        }

        public ChangelogForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = "更新日志";
            Icon = MainContext.CreateTrayIcon();
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;   // 无 owner 时 CenterParent 不可靠
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(620, 520);
            MinimumSize = new Size(440, 320);

            string text = LoadText();
            int count = CountEntries(text);

            // ---- 顶部标题区 ----
            var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.White };
            header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor))
                    e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            header.Controls.Add(new Label
            {
                Text = "更新日志",
                Location = new Point(24, 16),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
                ForeColor = TextMain
            });
            header.Controls.Add(new Label
            {
                Text = "当前版本 " + Ui.AppVersion + (count > 0 ? "  ·  共 " + count + " 条版本记录" : ""),
                Location = new Point(25, 46),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                ForeColor = TextSub
            });
            Controls.Add(header);

            // ---- 底部按钮区 ----
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.White };
            bottom.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor))
                    e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0);
            };
            var close = MakePill("关闭");
            close.Location = new Point(ClientSize.Width - 16 - 88, 12);
            close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            close.Click += delegate { Close(); };
            bottom.Controls.Add(close);
            Controls.Add(bottom);

            // ---- 阅读区（Fill 最后添加，占据剩余空间）----
            var pad = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 14, 20, 10),
                BackColor = Color.White
            };
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(60, 60, 60),
                Font = new Font("Consolas", 10f),
                WordWrap = true,
                Text = text
            };
            box.Select(0, 0);       // 光标回到行首，避免打开时自动滚动到末尾
            pad.Controls.Add(box);
            Controls.Add(pad);

            AcceptButton = close;   // Enter / Esc 都能关闭
            CancelButton = close;
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

        /// <summary>统计“## v...”开头的版本条目数量。</summary>
        private static int CountEntries(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 0;
            foreach (var line in text.Split('\n'))
            {
                var s = line.Trim();
                if (s.StartsWith("## ", StringComparison.Ordinal)) n++;
            }
            return n;
        }

        private static string LoadText()
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "CHANGELOG.md");
                if (File.Exists(path)) return File.ReadAllText(path);
                return "未找到更新日志文件 CHANGELOG.md。\r\n" +
                       "CHANGELOG.md was not found next to the application.\r\n\r\n" +
                       "预期位置 / expected path:\r\n" + path;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>黑色胶囊按钮（与设置界面“确定”同款）。</summary>
        private static Button MakePill(string text)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(88, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = DarkPill,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(DarkPill);
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
    }
}
