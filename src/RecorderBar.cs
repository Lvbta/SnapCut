using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>
    /// 录屏会话：在区域外绘制红色点击穿透边框，显示深色控制条
    /// （计时 / 暂停 / 停止 / 取消，按住空白处可拖动），并在后台线程
    /// 把区域内容写入 MJPEG AVI。帧率、画质、是否录鼠标、输出目录
    /// 均来自 <see cref="Settings"/>。
    /// </summary>
    internal sealed class RecorderBar : Form
    {
        private readonly int _fps;
        private readonly long _jpegQuality;
        private readonly bool _captureCursor;
        private readonly bool _showBorder;
        private readonly bool _recordAudio;
        private readonly bool _highlightMouse;

        private readonly Rectangle _region;      // screen coords, even-sized
        private FrameOverlay _frame;
        private readonly string _path;

        private AviWriter _writer;
        private AudioCapture _audio;
        private MouseAnnotator _annotator;
        private Thread _worker;
        private volatile bool _running;
        private volatile bool _paused;
        private long _framesWritten;
        private readonly Stopwatch _clock = new Stopwatch();  // counts recorded time only
        private readonly System.Windows.Forms.Timer _uiTimer;
        private bool _blink;

        private Rectangle _pauseR, _stopR, _cancelR;
        private int _hover; // 0 无, 1 暂停, 2 停止, 3 取消

        // 控制条拖动（避免固定位置遮挡录制内容）
        private bool _draggingBar;
        private Point _dragOffset;

        public RecorderBar(Rectangle region)
        {
            var cfg = Settings.Current;
            _fps = cfg.Fps < 5 ? 5 : (cfg.Fps > 60 ? 60 : cfg.Fps);
            _jpegQuality = cfg.JpegQuality;
            _captureCursor = cfg.CaptureCursor;
            _showBorder = cfg.ShowRecordBorder;
            _recordAudio = cfg.RecordAudio;
            _highlightMouse = cfg.HighlightMouse;

            // MJPEG decoders prefer even dimensions
            region.Width &= ~1; region.Height &= ~1;
            if (region.Width < 16) region.Width = 16;
            if (region.Height < 16) region.Height = 16;
            _region = region;

            string dir = cfg.VideoFolder;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            _path = Path.Combine(dir, "SnapCut_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".avi");

            // ---- control bar window ----
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(32, 32, 32);
            Size = new Size(226, 36);
            _pauseR = new Rectangle(Width - 92, 8, 22, 20);
            _stopR = new Rectangle(Width - 62, 8, 22, 20);
            _cancelR = new Rectangle(Width - 32, 8, 22, 20);
            PlaceBar();

            _uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _uiTimer.Tick += delegate { _blink = !_blink; Invalidate(); };

            MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (_draggingBar)
                {
                    // 拖动控制条，并限制在虚拟屏内（防止拖丢找不回来）
                    var wa = SystemInformation.VirtualScreen;
                    int nx = Math.Min(Math.Max(Location.X + e.X - _dragOffset.X, wa.Left - Width + 40), wa.Right - 40);
                    int ny = Math.Min(Math.Max(Location.Y + e.Y - _dragOffset.Y, wa.Top), wa.Bottom - 20);
                    Location = new Point(nx, ny);
                    return;
                }
                int h = _pauseR.Contains(e.Location) ? 1
                      : _stopR.Contains(e.Location) ? 2
                      : _cancelR.Contains(e.Location) ? 3 : 0;
                if (h != _hover) { _hover = h; Invalidate(); }
                Cursor = h != 0 ? Cursors.Hand : Cursors.Default;
            };
            MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (_pauseR.Contains(e.Location)) TogglePause();
                else if (_stopR.Contains(e.Location)) StopRecording(false);
                else if (_cancelR.Contains(e.Location)) StopRecording(true);
                else if (e.Button == MouseButtons.Left)
                {
                    // 按住空白区域开始拖动控制条
                    _draggingBar = true;
                    _dragOffset = e.Location;
                }
            };
            MouseUp += delegate { _draggingBar = false; };
            Load += delegate { CreateBorder(); StartRecording(); };
            FormClosed += delegate { if (_frame != null) _frame.Dispose(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // the timer bar must not appear in the recorded video even if it overlaps the region
            try { NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE); } catch { }
            using (var gp = Toolbar.Rounded(new Rectangle(0, 0, Width, Height), 8))
                Region = new Region(gp);
        }

        private void PlaceBar()
        {
            var wa = SystemInformation.VirtualScreen;
            int x = Math.Min(Math.Max(_region.Right - Width, wa.Left + 4), wa.Right - Width - 4);
            int y = _region.Bottom + 10;
            if (y + Height > wa.Bottom - 4) y = _region.Top - Height - 10;
            if (y < wa.Top + 4) y = _region.Bottom - Height - 10; // inside region as last resort
            Location = new Point(x, y);
        }

        private void CreateBorder()
        {
            if (!_showBorder) return;
            // one click-through frame drawn just OUTSIDE the region (so it is never captured)
            _frame = new FrameOverlay(_region, 3, Color.FromArgb(230, 60, 50));
            _frame.Show();
        }

        // ---------- capture ----------
        private void StartRecording()
        {
            // 系统声音（环回采集）。初始化失败时降级为仅录画面，
            // 但必须明确告知用户“本次没有声音”，而不是静默失败
            if (_recordAudio)
            {
                _audio = new AudioCapture();
                if (!_audio.Start())
                {
                    _audio.Dispose();
                    _audio = null;
                    MainContext.Balloon("快截",
                        "系统声音初始化失败，本次录制将没有声音（画面正常录制）。",
                        ToolTipIcon.Warning);
                }
            }

            try
            {
                _writer = _audio != null
                    ? new AviWriter(_path, _region.Width, _region.Height, _fps, _audio.Channels, _audio.SampleRate)
                    : new AviWriter(_path, _region.Width, _region.Height, _fps);
            }
            catch (Exception ex)
            {
                if (_audio != null) { _audio.Dispose(); _audio = null; }
                MessageBox.Show("Cannot create output file:\n" + ex.Message, "快截",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            // click / scroll annotation hook (installed on this UI thread)
            if (_highlightMouse)
            {
                _annotator = new MouseAnnotator();
                _annotator.Start();
            }

            if (_audio != null) _audio.Drain(); // discard audio buffered during setup, align start

            _running = true;
            _clock.Start();
            _uiTimer.Start();
            _worker = new Thread(CaptureLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _worker.Start();
        }

        private void TogglePause()
        {
            if (!_running) return;
            _paused = !_paused;
            // 恢复录制时丢弃暂停期间积压的音频，保证音画起点重新对齐
            if (!_paused && _audio != null) _audio.Drain();
            if (_paused) _clock.Stop(); else _clock.Start();
            Invalidate();
        }

        /// <summary>文件接近 AVI 2GB 索引上限时自动停止并保留视频，避免整个文件损坏。</summary>
        private void OnSizeLimit()
        {
            if (!_running) return;
            StopRecording(false);
            MessageBox.Show("视频文件已接近 AVI 格式的 2GB 上限，录制已自动停止并保存。",
                "快截", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void CaptureLoop()
        {
            var jpegCodec = GetJpegEncoder();
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, _jpegQuality);

            using (var frame = new Bitmap(_region.Width, _region.Height, PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(frame))
            using (var ms = new MemoryStream(256 * 1024))
            {
                long interval = Stopwatch.Frequency / _fps;
                long next = Stopwatch.GetTimestamp();

                while (_running)
                {
                    if (_paused)
                    {
                        // 暂停期间丢弃已采集的音频（含静音填充），否则恢复后会把
                        // 暂停时长的音频一次性写入，造成音画不同步
                        if (_audio != null) _audio.Drain();
                        Thread.Sleep(50);
                        next = Stopwatch.GetTimestamp(); // 暂停时间不计入帧节奏
                        continue;
                    }

                    g.CopyFromScreen(_region.Left, _region.Top, 0, 0, _region.Size);
                    if (_annotator != null) _annotator.Draw(g, _region);
                    if (_captureCursor) DrawCursor(g);

                    ms.SetLength(0);
                    frame.Save(ms, jpegCodec, encParams);
                    _writer.AddFrame(ms.GetBuffer(), (int)ms.Length);
                    _framesWritten++;

                    if (_audio != null)
                    {
                        byte[] pcm = _audio.Drain();
                        if (pcm != null) _writer.AddAudio(pcm, pcm.Length);
                    }

                    // AVI 索引为 32 位偏移：接近 2GB 时必须停止，否则整个文件损坏
                    if (_writer.Position > 1950000000L)
                    {
                        try { BeginInvoke((MethodInvoker)delegate { OnSizeLimit(); }); } catch { }
                        break;
                    }

                    next += interval;
                    long wait = next - Stopwatch.GetTimestamp();
                    if (wait > 0) Thread.Sleep((int)(wait * 1000 / Stopwatch.Frequency));
                    else next = Stopwatch.GetTimestamp(); // fell behind, resync
                }
            }
        }

        private void DrawCursor(Graphics g)
        {
            var ci = new NativeMethods.CURSORINFO();
            ci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.CURSORINFO));
            if (!NativeMethods.GetCursorInfo(ref ci) || ci.flags != NativeMethods.CURSOR_SHOWING) return;

            IntPtr icon = NativeMethods.CopyIcon(ci.hCursor);
            if (icon == IntPtr.Zero) return;
            try
            {
                NativeMethods.ICONINFO ii;
                int hx = 0, hy = 0;
                if (NativeMethods.GetIconInfo(icon, out ii))
                {
                    hx = ii.xHotspot; hy = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
                }
                int x = ci.ptScreenPos.X - hx - _region.Left;
                int y = ci.ptScreenPos.Y - hy - _region.Top;
                IntPtr hdc = g.GetHdc();
                NativeMethods.DrawIconEx(hdc, x, y, icon, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
                g.ReleaseHdc(hdc);
            }
            finally { NativeMethods.DestroyIcon(icon); }
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            throw new InvalidOperationException("JPEG encoder not found");
        }

        private void StopRecording(bool cancel)
        {
            if (!_running) return;
            _running = false;
            _paused = false;
            _uiTimer.Stop();
            _clock.Stop();
            if (_worker != null) _worker.Join(3000);
            if (_annotator != null) { _annotator.Dispose(); _annotator = null; }
            if (_audio != null)
            {
                byte[] tail = _audio.Drain();
                if (tail != null && _writer != null) _writer.AddAudio(tail, tail.Length);
                _audio.Dispose(); _audio = null;
            }
            if (_writer != null) _writer.Close();

            bool discard = cancel || _framesWritten == 0;
            TimeSpan dur = _clock.Elapsed;
            Rectangle region = _region;
            string path = _path;

            if (discard)
            {
                try { File.Delete(path); } catch { }
                Close();
                return;
            }

            // finished normally -> resident keep / discard bar (no Explorer popup).
            // The .avi is already on disk; the prompt keeps or deletes it.
            Close();
            new RecordConfirmForm(path, dur, region).Show();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running) StopRecording(false);
            base.OnFormClosing(e);
        }

        // ---------- painting ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // blinking record dot (steady amber while paused)
            Color dot = _paused ? Color.FromArgb(230, 170, 40)
                                : (_blink ? Color.FromArgb(230, 60, 50) : Color.FromArgb(120, 60, 50));
            using (var br = new SolidBrush(dot))
                g.FillEllipse(br, 12, 13, 10, 10);

            // timer
            var t = _clock.Elapsed;
            string txt = string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
            using (var f = new Font("Consolas", 11f, FontStyle.Bold))
            using (var br = new SolidBrush(Color.White))
                g.DrawString(txt, f, br, 30, 9);

            DrawHover(g, _pauseR, _hover == 1);
            DrawHover(g, _stopR, _hover == 2);
            DrawHover(g, _cancelR, _hover == 3);

            // pause / resume glyph
            if (_paused)
            {
                // play triangle
                using (var br = new SolidBrush(Color.White))
                {
                    var pts = new[]
                    {
                        new Point(_pauseR.X + 6, _pauseR.Y + 4),
                        new Point(_pauseR.X + 6, _pauseR.Y + 16),
                        new Point(_pauseR.X + 16, _pauseR.Y + 10)
                    };
                    g.FillPolygon(br, pts);
                }
            }
            else
            {
                using (var br = new SolidBrush(Color.White))
                {
                    g.FillRectangle(br, _pauseR.X + 5, _pauseR.Y + 4, 4, 12);
                    g.FillRectangle(br, _pauseR.X + 13, _pauseR.Y + 4, 4, 12);
                }
            }

            // stop button (white square)
            using (var br = new SolidBrush(Color.White))
                g.FillRectangle(br, _stopR.X + 5, _stopR.Y + 4, 12, 12);

            // cancel button (X)
            using (var p = new Pen(Color.White, 2f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                int cx = _cancelR.X + _cancelR.Width / 2, cy = _cancelR.Y + _cancelR.Height / 2;
                g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
            }
        }

        private static void DrawHover(Graphics g, Rectangle r, bool on)
        {
            if (!on) return;
            using (var hb = new SolidBrush(Color.FromArgb(70, 70, 70)))
                g.FillEllipse(hb, r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6);
        }
    }
}
