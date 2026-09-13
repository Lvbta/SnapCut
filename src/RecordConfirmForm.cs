using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>
    /// 录制停止后显示在区域旁的常驻小条：文件夹按钮（在资源管理器中定位文件）、
    /// 对勾（保留）和叉（删除）。文件本就已落盘，“保留”即不管它，“删除”才移除；
    /// 其他任何关闭方式都视为保留（绝不静默丢失已完成的录制）。
    /// 若设置开启“录制完成后打开文件夹”，显示本条时会自动定位一次文件。
    /// </summary>
    internal sealed class RecordConfirmForm : Form
    {
        private readonly string _path;
        private readonly string _caption;
        private readonly string _name;

        private Rectangle _folderR, _okR, _noR;
        private int _hover;      // 0 无, 1 保留, 2 删除, 3 打开文件夹
        private bool _decided;

        public RecordConfirmForm(string path, TimeSpan duration, Rectangle region)
        {
            _path = path;
            long bytes = 0; try { bytes = new FileInfo(path).Length; } catch { }
            _caption = string.Format("\u5f55\u5236\u5b8c\u6210  {0:00}:{1:00}  \u00b7  {2}",
                (int)duration.TotalMinutes, duration.Seconds, FormatSize(bytes));
            _name = Path.GetFileName(path);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            BackColor = Color.FromArgb(32, 32, 32);
            Size = new Size(368, 66);
            _folderR = new Rectangle(Width - 148, 13, 40, 40);
            _okR = new Rectangle(Width - 100, 13, 40, 40);
            _noR = new Rectangle(Width - 52, 13, 40, 40);
            PlaceBar(region);

            MouseMove += delegate (object s, MouseEventArgs e)
            {
                int h = _okR.Contains(e.Location) ? 1
                      : _noR.Contains(e.Location) ? 2
                      : _folderR.Contains(e.Location) ? 3 : 0;
                if (h != _hover) { _hover = h; Invalidate(); }
                Cursor = h != 0 ? Cursors.Hand : Cursors.Default;
            };
            MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (_okR.Contains(e.Location)) Keep();
                else if (_noR.Contains(e.Location)) Discard();
                else if (_folderR.Contains(e.Location)) OpenFolder();
            };
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) Keep();
                else if (e.KeyCode == Keys.Escape) Keep();   // 绝不因误按丢失已完成的文件
            };

            // “录制完成后打开文件夹”设置在这里生效：自动定位一次新文件
            if (Settings.Current.OpenFolderAfterRecord) OpenFolderExplorer();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (var gp = Toolbar.Rounded(new Rectangle(0, 0, Width, Height), 8))
                Region = new Region(gp);
        }

        private void PlaceBar(Rectangle region)
        {
            var wa = SystemInformation.VirtualScreen;
            int x = Math.Min(Math.Max(region.Right - Width, wa.Left + 4), wa.Right - Width - 4);
            int y = region.Bottom + 12;
            if (y + Height > wa.Bottom - 4) y = region.Top - Height - 12;
            if (y < wa.Top + 4) y = region.Bottom - Height - 12;
            Location = new Point(x, y);
        }

        private void Keep()
        {
            if (_decided) return;
            _decided = true;
            Close();   // 文件保留在磁盘上
        }

        /// <summary>在资源管理器中打开所在文件夹并选中文件，然后按“保留”关闭。</summary>
        private void OpenFolder()
        {
            OpenFolderExplorer();
            Keep();
        }

        /// <summary>仅打开资源管理器并选中生成的文件（不改变保留/删除状态）。</summary>
        private void OpenFolderExplorer()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _path + "\""); }
            catch { }
        }

        private void Discard()
        {
            if (_decided) return;
            _decided = true;
            try { File.Delete(_path); } catch { }
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            using (var br = new SolidBrush(Color.White))
                g.DrawString(_caption, f, br, 12, 11);

            using (var f = new Font("Microsoft YaHei UI", 8f))
                TextRenderer.DrawText(g, _name, f,
                    new Rectangle(12, 34, Width - 168, 20), Color.FromArgb(170, 170, 170),
                    TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            DrawFolder(g, _folderR, _hover == 3);
            DrawCheck(g, _okR, _hover == 1);
            DrawCross(g, _noR, _hover == 2);
        }

        /// <summary>画文件夹图标按钮（悬停时变亮黄色）。</summary>
        private static void DrawFolder(Graphics g, Rectangle r, bool hover)
        {
            var back = Color.FromArgb(70, 70, 70);
            using (var b = new SolidBrush(hover ? Color.FromArgb(230, 175, 55) : back))
                g.FillEllipse(b, r);
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (var w = new SolidBrush(Color.White))
            {
                g.FillRectangle(w, cx - 8, cy - 6, 7, 3);   // 页签
                g.FillRectangle(w, cx - 8, cy - 3, 16, 11); // 主体
            }
            using (var p = new Pen(back, 1.2f))
                g.DrawLine(p, cx - 8, cy - 1, cx + 8, cy - 1); // 文件夹开口
        }

        private static void DrawCheck(Graphics g, Rectangle r, bool hover)
        {
            var back = Color.FromArgb(7, 193, 96);
            using (var b = new SolidBrush(hover ? ControlPaint.Light(back) : back))
                g.FillEllipse(b, r);
            using (var p = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.DrawLines(p, new[]
                {
                    new Point(cx - 8, cy),
                    new Point(cx - 2, cy + 6),
                    new Point(cx + 8, cy - 6)
                });
            }
        }

        private static void DrawCross(Graphics g, Rectangle r, bool hover)
        {
            var back = Color.FromArgb(70, 70, 70);
            using (var b = new SolidBrush(hover ? Color.FromArgb(200, 60, 50) : back))
                g.FillEllipse(b, r);
            using (var p = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.DrawLine(p, cx - 6, cy - 6, cx + 6, cy + 6);
                g.DrawLine(p, cx + 6, cy - 6, cx - 6, cy + 6);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024d / 1024 / 1024).ToString("0.0") + " GB";
            if (bytes >= 1024 * 1024) return (bytes / 1024d / 1024).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024d).ToString("0") + " KB";
            return bytes + " B";
        }
    }
}
