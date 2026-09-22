using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace SimpleShot
{
    // 原 Ui 工具类（设计令牌 / 圆角路径 / 胶囊按钮 / 徽标 / 窗体居中）已迁移到 UiKit.cs：
    // 它没有任何业务依赖，放在公共位置后各模块（含在线更新）都能独立复用与测试。

    /// <summary>
    /// 长截图预览（现代浅色风格）：顶部标题与尺寸信息，中部可滚动长图，
    /// 底部“复制 / 保存 / 关闭”胶囊按钮；Esc 关闭。
    /// </summary>
    internal sealed class LongShotPreview : Form
    {
        private static readonly Color ContentBg = Color.White;
        private static readonly Color TextMain = Color.FromArgb(30, 30, 30);
        private static readonly Color TextSub = Color.FromArgb(140, 140, 140);
        private static readonly Color BorderColor = Color.FromArgb(228, 229, 232);
        private static readonly Color ImageBg = Color.FromArgb(246, 247, 249);

        private readonly Bitmap _image;
        private readonly Button _copy;
        private readonly Timer _resetCopy;

        private bool _centered;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_centered) { _centered = true; Ui.CenterOnScreen(this); }
        }

        public LongShotPreview(Bitmap image)
        {
            _image = image;
            Icon = MainContext.CreateTrayIcon();
            AutoScaleMode = AutoScaleMode.None;
            Text = "长截图预览";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = ContentBg;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            int w = Math.Min(Math.Max(image.Width + 64, 480), 860);
            ClientSize = new Size(w, Math.Min(720, image.Height + 220));
            MinimumSize = new Size(420, 360);

            // ---- 中部（Fill）：可滚动长图 ----
            var body = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg, Padding = new Padding(16, 14, 16, 12) };
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ImageBg };
            scroll.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor))
                    e.Graphics.DrawRectangle(p, 0, 0, scroll.Width - 1, scroll.Height - 1);
            };
            int picW = Math.Min(image.Width, w - 40);
            var pic = new PictureBox
            {
                Image = image,
                SizeMode = PictureBoxSizeMode.Zoom,
                Width = picW,
                Height = (int)((long)picW * image.Height / image.Width),
                Location = new Point(0, 0)
            };
            scroll.Controls.Add(pic);
            body.Controls.Add(scroll);
            Controls.Add(body);

            // ---- 底部按钮区（Bottom）----
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = ContentBg };
            bottom.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor)) e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0);
            };
            bottom.Controls.Add(new Label
            {
                Text = "上下滚动查看完整长图", Location = new Point(18, 22), AutoSize = true,
                ForeColor = TextSub, Font = new Font("Microsoft YaHei UI", 8.5f)
            });
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, Width = 424, WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 14, 16, 0), BackColor = ContentBg
            };
            _copy = Ui.Pill("复制", true, 96);
            var save = Ui.Pill("保存", false, 96);
            var edit = Ui.Pill("编辑", false, 96);
            var close = Ui.Pill("关闭", false, 76);
            _copy.Click += delegate { DoCopy(); };
            save.Click += delegate { DoSave(); };
            edit.Click += delegate { DoEdit(); };
            close.Click += delegate { Close(); };
            close.Margin = new Padding(0, 0, 0, 0);
            edit.Margin = new Padding(8, 0, 0, 0);
            save.Margin = new Padding(8, 0, 0, 0);
            _copy.Margin = new Padding(8, 0, 0, 0);
            flow.Controls.Add(close);
            flow.Controls.Add(save);
            flow.Controls.Add(_copy);
            flow.Controls.Add(edit);
            bottom.Controls.Add(flow);
            Controls.Add(bottom);

            // ---- 顶部标题区（Top，最后添加先 Dock）----
            var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = ContentBg };
            header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor))
                    e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            header.Controls.Add(new Label
            {
                Text = "长截图预览", Location = new Point(22, 12), AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold), ForeColor = TextMain
            });
            header.Controls.Add(new Label
            {
                Text = image.Width + " × " + image.Height + " 像素", Location = new Point(23, 46), AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5f), ForeColor = TextSub
            });
            Controls.Add(header);

            _resetCopy = new Timer { Interval = 1300 };
            _resetCopy.Tick += delegate { _resetCopy.Stop(); _copy.Text = "复制"; };

            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int pref = 2; // DWMCP_ROUND
                NativeMethods.DwmSetWindowAttribute(Handle,
                    NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
            }
            catch { }
        }

        private void DoCopy()
        {
            try
            {
                Clipboard.SetImage(_image);
                _copy.Text = "已复制 ✓";
                _resetCopy.Stop();
                _resetCopy.Start();
            }
            catch { }
        }

        /// <summary>把长图交给图片编辑器继续标注（克隆位图，编辑器自行管理生命周期）。</summary>
        private void DoEdit()
        {
            try
            {
                new ImageEditorForm((Bitmap)_image.Clone()).Show(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开编辑器：" + ex.Message, "快截",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DoSave()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg";
                dlg.InitialDirectory = Settings.ImageDir();
                dlg.FileName = "SnapCut_Long_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    _image.Save(dlg.FileName, ext == ".jpg" || ext == ".jpeg" ? ImageFormat.Jpeg : ImageFormat.Png);
                    Settings.RememberImageDir(dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "保存失败：" + ex.Message, "快截",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_resetCopy != null) _resetCopy.Dispose();
                if (_image != null) _image.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 识别结果窗口（现代浅色风格，与设置/更新日志一致）：
    /// 左侧原图预览、右侧可编辑的识别文本；顶部引擎徽标与字数统计，
    /// 底部“自动换行 / 复制全部 / 另存为 .txt / 关闭”；支持 Ctrl+S/Esc。
    /// </summary>
    internal sealed class OcrResultForm : Form
    {
        private static readonly Color ContentBg = Color.White;
        private static readonly Color TextMain = Color.FromArgb(30, 30, 30);
        private static readonly Color TextSub = Color.FromArgb(140, 140, 140);
        private static readonly Color BorderColor = Color.FromArgb(228, 229, 232);
        private static readonly Color ImageBg = Color.FromArgb(246, 247, 249);

        private readonly TextBox _box;
        private readonly Button _copy;
        private readonly Timer _resetCopy;
        private readonly string _fallback;

        private bool _centered;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_centered) { _centered = true; Ui.CenterOnScreen(this); }
        }

        public OcrResultForm(Bitmap source, string text, string engine)
        {
            Icon = MainContext.CreateTrayIcon();
            AutoScaleMode = AutoScaleMode.None;
            Text = "文字识别结果";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = ContentBg;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(900, 620);
            MinimumSize = new Size(720, 470);

            string shown = string.IsNullOrEmpty(text) ? "(未识别到文字)" : text;
            _fallback = shown;
            int lineCount = shown.Replace("\r", "").Split('\n').Length;
            int charCount = shown.Replace("\r", "").Replace("\n", "").Length;

            // ---- 中部（Fill）：先加右侧 Fill，再加左侧 Left，保证 Dock 顺序正确 ----
            var body = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg };

            var rightWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 14, 16, 12), BackColor = ContentBg };
            var textBorder = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(10, 8, 10, 8) };
            textBorder.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor))
                    e.Graphics.DrawRectangle(p, 0, 0, textBorder.Width - 1, textBorder.Height - 1);
            };
            _box = new TextBox
            {
                Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = TextMain,
                Font = new Font("Microsoft YaHei UI", 10.5f), WordWrap = true, Text = shown
            };
            _box.Select(0, 0);
            textBorder.Controls.Add(_box);
            var textCap = new Label
            {
                Dock = DockStyle.Bottom, Height = 18, Text = "识别文本（可直接编辑后复制）",
                ForeColor = TextSub, Font = new Font("Microsoft YaHei UI", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            rightWrap.Controls.Add(textBorder);
            rightWrap.Controls.Add(textCap);

            var leftWrap = new Panel { Dock = DockStyle.Left, Width = 320, Padding = new Padding(16, 14, 6, 12), BackColor = ContentBg };
            var imgCap = new Label
            {
                Dock = DockStyle.Bottom, Height = 18, Text = "原图 · " + source.Width + "×" + source.Height,
                ForeColor = TextSub, Font = new Font("Microsoft YaHei UI", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            var imgBox = new Panel { Dock = DockStyle.Fill, BackColor = ImageBg };
            imgBox.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor))
                    e.Graphics.DrawRectangle(p, 0, 0, imgBox.Width - 1, imgBox.Height - 1);
            };
            var pic = new PictureBox { Dock = DockStyle.Fill, Image = source, SizeMode = PictureBoxSizeMode.Zoom, BackColor = ImageBg };
            imgBox.Controls.Add(pic);
            leftWrap.Controls.Add(imgBox);
            leftWrap.Controls.Add(imgCap);

            body.Controls.Add(rightWrap);
            body.Controls.Add(leftWrap);
            Controls.Add(body);

            // ---- 底部按钮区（Bottom）：左侧换行开关 + 右侧按钮组(FlowLayoutPanel 贴右) ----
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = ContentBg };
            bottom.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor)) e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0);
            };
            var wrap = new CheckBox { Text = "自动换行", Checked = true, Location = new Point(18, 21), AutoSize = true, ForeColor = TextSub };
            wrap.CheckedChanged += delegate { _box.WordWrap = wrap.Checked; };
            bottom.Controls.Add(wrap);
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, Width = 300, WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 14, 16, 0), BackColor = ContentBg
            };
            _copy = Ui.Pill("复制全部", true, 104);
            var save = Ui.Pill("另存为 .txt", false, 100);
            var close = Ui.Pill("关闭", false, 76);
            _copy.Click += delegate { DoCopy(); };
            save.Click += delegate { DoSave(); };
            close.Click += delegate { Close(); };
            close.Margin = new Padding(0, 0, 0, 0);
            save.Margin = new Padding(8, 0, 0, 0);
            _copy.Margin = new Padding(8, 0, 0, 0);
            flow.Controls.Add(close);
            flow.Controls.Add(save);
            flow.Controls.Add(_copy);
            bottom.Controls.Add(flow);
            Controls.Add(bottom);

            // ---- 顶部标题区（Top，最后添加先 Dock）----
            var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = ContentBg };
            header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var p = new Pen(BorderColor))
                    e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            var title = new Label
            {
                Text = "文字识别结果", Location = new Point(22, 12), AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold), ForeColor = TextMain
            };
            header.Controls.Add(title);
            int titleW = TextRenderer.MeasureText(title.Text, title.Font).Width;
            header.Controls.Add(Ui.Badge(string.IsNullOrEmpty(engine) ? "OCR" : engine,
                Ui.EngineColor(engine), new Point(24 + titleW + 12, 16), new Font("Microsoft YaHei UI", 8f)));
            header.Controls.Add(new Label
            {
                Text = charCount + " 字符  ·  " + lineCount + " 行", Location = new Point(23, 46), AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5f), ForeColor = TextSub
            });
            Controls.Add(header);

            _resetCopy = new Timer { Interval = 1300 };
            _resetCopy.Tick += delegate { _resetCopy.Stop(); _copy.Text = "复制全部"; };

            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.Control && e.KeyCode == Keys.S) { DoSave(); e.SuppressKeyPress = true; }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int pref = 2; // DWMCP_ROUND
                NativeMethods.DwmSetWindowAttribute(Handle,
                    NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
            }
            catch { }
        }

        private void DoCopy()
        {
            try
            {
                string t = _box.SelectionLength > 0 ? _box.SelectedText : _box.Text;
                if (string.IsNullOrEmpty(t)) t = _fallback;
                Clipboard.SetText(t);
                _copy.Text = "已复制 ✓";
                _resetCopy.Stop();
                _resetCopy.Start();
            }
            catch { }
        }

        private void DoSave()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "文本文件|*.txt";
                dlg.InitialDirectory = Settings.ImageDir();
                dlg.FileName = "SnapCut_OCR_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string t = string.IsNullOrEmpty(_box.Text) ? _fallback : _box.Text;
                    File.WriteAllText(dlg.FileName, t, new System.Text.UTF8Encoding(true));
                    Settings.RememberImageDir(dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "保存失败：" + ex.Message, "快截",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _resetCopy != null) _resetCopy.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>轻量忙碌指示：无边框圆角小窗，旋转弧 + 文本，居中显示，TopMost。</summary>
    internal sealed class BusyForm : Form
    {
        private readonly Timer _timer;
        private float _angle;
        private readonly string _text;

        public BusyForm(string text)
        {
            _text = text;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(45, 45, 48);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(240, 96);
            HandleCreated += delegate
            {
                using (var gp = Ui.Rounded(new Rectangle(0, 0, Width, Height), 14))
                    Region = new Region(gp);
            };
            _timer = new Timer { Interval = 28 };
            _timer.Tick += delegate { _angle = (_angle + 14) % 360; Invalidate(); };
            _timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int sz = 30;
            var rect = new Rectangle((Width - sz) / 2, 14, sz, sz);
            using (var pen = new Pen(Color.FromArgb(7, 193, 96), 3f))
            {
                pen.StartCap = LineCap.Round;
                g.DrawArc(pen, rect, _angle, 265);
            }
            TextRenderer.DrawText(g, _text, Font, new Rectangle(0, 52, Width, 36), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _timer != null) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
