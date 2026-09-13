using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>
    /// 简易图片编辑器：打开本地图片后可拖拽框选裁剪、旋转 90°、水平镜像、
    /// 复制到剪贴板、另存为。顶栏工具条与设置/更新日志同款现代浅色风格，
    /// 标题栏使用应用图标。画布以“适应窗口”方式缩放显示，裁剪坐标自动换算。
    /// </summary>
    internal sealed class ImageEditorForm : Form
    {
        private static readonly Color SidebarBg = Color.FromArgb(247, 247, 248);
        private static readonly Color TextMain = Color.FromArgb(30, 30, 30);
        private static readonly Color TextSub = Color.FromArgb(140, 140, 140);
        private static readonly Color BorderColor = Color.FromArgb(232, 233, 235);
        private static readonly Color DarkPill = Color.FromArgb(30, 30, 30);

        private Bitmap _img;
        private readonly PictureBox _pic;
        private readonly Label _sizeLabel;
        private RectangleF _selCtrl;   // 框选区域（控件坐标）
        private bool _dragging;
        private Point _dragStart;

        private bool _centered;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 尺寸已最终确定，这里显式居中（CenterScreen 对无主窗的非模态 Show 不可靠）
            if (!_centered) { _centered = true; Ui.CenterOnScreen(this); }
        }

        public ImageEditorForm(Bitmap img)
        {
            _img = img;

            AutoScaleMode = AutoScaleMode.None;
            Text = "图片编辑";
            Icon = MainContext.CreateTrayIcon();
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ForeColor = TextMain;
            ClientSize = new Size(920, 640);
            MinimumSize = new Size(560, 400);
            KeyPreview = true;

            // ---- 顶栏工具条 ----
            var bar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = SidebarBg };
            bar.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor))
                    e.Graphics.DrawLine(p, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            };
            Controls.Add(bar);

            var apply = MakeToolButton("应用裁剪", true);
            apply.Location = new Point(12, 11);
            apply.Click += delegate { ApplyCrop(); };
            bar.Controls.Add(apply);

            var rotate = MakeToolButton("旋转 90°", false);
            rotate.Location = new Point(108, 11);
            rotate.Click += delegate { _img.RotateFlip(RotateFlipType.Rotate90FlipNone); RefreshImage(); };
            bar.Controls.Add(rotate);

            var flip = MakeToolButton("水平镜像", false);
            flip.Location = new Point(196, 11);
            flip.Click += delegate { _img.RotateFlip(RotateFlipType.RotateNoneFlipX); RefreshImage(); };
            bar.Controls.Add(flip);

            var copy = MakeToolButton("复制", false);
            copy.Location = new Point(292, 11);
            copy.Click += delegate { CopyImage(); };
            bar.Controls.Add(copy);

            var save = MakeToolButton("另存为", false);
            save.Location = new Point(364, 11);
            save.Click += delegate { SaveImage(); };
            bar.Controls.Add(save);

            var tip = new Label
            {
                Text = "在图片上拖拽框选裁剪区域",
                Location = new Point(458, 18),
                AutoSize = true,
                ForeColor = TextSub,
                Font = new Font("Microsoft YaHei UI", 8.5f)
            };
            bar.Controls.Add(tip);

            _sizeLabel = new Label
            {
                Text = "",
                AutoSize = true,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                ForeColor = TextSub,
                Font = new Font("Microsoft YaHei UI", 8.5f)
            };
            _sizeLabel.Location = new Point(ClientSize.Width - 110, 18);
            bar.Controls.Add(_sizeLabel);

            // ---- 画布 ----
            _pic = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(244, 245, 246),
                Cursor = Cursors.Cross,
                Image = _img
            };
            Controls.Add(_pic);   // 在 Top 工具条之后添加，填充剩余空间

            _pic.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                _dragging = true;
                _dragStart = e.Location;
                _selCtrl = RectangleF.Empty;
                _pic.Invalidate();
            };
            _pic.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!_dragging) return;
                _selCtrl = RectFrom(_dragStart, e.Location);
                _pic.Invalidate();
            };
            _pic.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || !_dragging) return;
                _dragging = false;
                if (_selCtrl.Width < 4 || _selCtrl.Height < 4) _selCtrl = RectangleF.Empty;
                _pic.Invalidate();
            };
            _pic.Paint += delegate(object s, PaintEventArgs e)
            {
                if (_selCtrl.Width < 1 || _selCtrl.Height < 1) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 框选区域外加深色蒙版，框内保持原样
                using (var dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                {
                    var r = _selCtrl;
                    g.FillRectangle(dim, 0, 0, _pic.Width, r.Top);
                    g.FillRectangle(dim, 0, r.Top, r.Left, r.Height);
                    g.FillRectangle(dim, r.Right, r.Top, _pic.Width - r.Right, r.Height);
                    g.FillRectangle(dim, 0, r.Bottom, _pic.Width, _pic.Height - r.Bottom);
                }
                using (var pen = new Pen(Color.FromArgb(7, 193, 96), 2f))
                    g.DrawRectangle(pen, _selCtrl.X, _selCtrl.Y, _selCtrl.Width, _selCtrl.Height);
            };

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { _selCtrl = RectangleF.Empty; _pic.Invalidate(); }
                else if (e.Control && e.KeyCode == Keys.S) { SaveImage(); }
                else if (e.Control && e.KeyCode == Keys.C) { CopyImage(); }
            };

            UpdateSizeLabel();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Win11 圆角窗口（Win10 上静默退回方角）
            try
            {
                int pref = 2; // DWMCP_ROUND
                NativeMethods.DwmSetWindowAttribute(Handle,
                    NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _img != null) { _img.Dispose(); _img = null; }
            base.Dispose(disposing);
        }

        /// <summary>计算图片在 PictureBox（Zoom 模式）中的实际显示矩形。</summary>
        private RectangleF ImageRectInCtrl()
        {
            float iw = _img.Width, ih = _img.Height;
            float scale = Math.Min(_pic.Width / iw, _pic.Height / ih);
            float w = iw * scale, h = ih * scale;
            return new RectangleF((_pic.Width - w) / 2f, (_pic.Height - h) / 2f, w, h);
        }

        /// <summary>把控件坐标的框选区域换算为图片像素坐标。</summary>
        private Rectangle CtrlToImage(RectangleF ctrl)
        {
            var r = ImageRectInCtrl();
            float sx = _img.Width / r.Width, sy = _img.Height / r.Height;
            float x0 = Math.Max(ctrl.Left, r.Left), y0 = Math.Max(ctrl.Top, r.Top);
            float x1 = Math.Min(ctrl.Right, r.Right), y1 = Math.Min(ctrl.Bottom, r.Bottom);
            return Rectangle.Round(new RectangleF((x0 - r.Left) * sx, (y0 - r.Top) * sy,
                                                  (x1 - x0) * sx, (y1 - y0) * sy));
        }

        /// <summary>应用裁剪：把当前框选区域裁剪为新图片。</summary>
        private void ApplyCrop()
        {
            var r = CtrlToImage(_selCtrl);
            if (r.Width < 2 || r.Height < 2)
            {
                MessageBox.Show(this, "请先在图片上拖拽框选要保留的区域。", "图片编辑",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var ni = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(ni))
                g.DrawImage(_img, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            _img.Dispose();
            _img = ni;
            RefreshImage();
        }

        private void RefreshImage()
        {
            _selCtrl = RectangleF.Empty;
            _pic.Image = _img;   // 重赋引用并刷新，旋转/镜像立即生效
            _pic.Invalidate();
            UpdateSizeLabel();
        }

        private void UpdateSizeLabel()
        {
            _sizeLabel.Text = _img.Width + " × " + _img.Height;
        }

        private void CopyImage()
        {
            for (int i = 0; i < 6; i++)
            {
                try { Clipboard.SetImage(_img); return; }
                catch (System.Runtime.InteropServices.ExternalException)
                { System.Threading.Thread.Sleep(70); }
            }
            MessageBox.Show(this, "复制失败：剪贴板正被其他程序占用。", "图片编辑",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void SaveImage()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|位图|*.bmp";
                dlg.FileName = "SnapCut_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                string dir = Settings.Current.SaveFolder;
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var fmt = ImageFormat.Png;
                    string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    if (ext == ".jpg" || ext == ".jpeg") fmt = ImageFormat.Jpeg;
                    else if (ext == ".bmp") fmt = ImageFormat.Bmp;
                    _img.Save(dlg.FileName, fmt);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "保存失败：" + ex.Message, "图片编辑",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static RectangleF RectFrom(Point a, Point b)
        {
            return new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                                  Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        /// <summary>顶栏工具按钮：白色描边胶囊（主操作为黑底白字）。</summary>
        private static Button MakeToolButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(88, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? DarkPill : Color.White,
                ForeColor = primary ? Color.White : TextMain,
                Font = new Font("Microsoft YaHei UI", 9f)
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = BorderColor;
            b.FlatAppearance.MouseOverBackColor = primary
                ? ControlPaint.Light(DarkPill) : Color.FromArgb(242, 242, 244);
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
