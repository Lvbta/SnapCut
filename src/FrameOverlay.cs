using System;
using System.Drawing;
using System.Windows.Forms;

namespace SimpleShot
{
    /// <summary>
    /// A thin click-through frame drawn just OUTSIDE a screen region, used to show the
    /// recording / long-shot boundary. It is a single top-most window whose shape is a
    /// hollow ring (the interior is cut out of the window region), so the region itself is
    /// never covered and is captured cleanly. It never steals focus or receives clicks.
    /// AutoScaleMode.None keeps it pixel-exact under any DPI.
    /// </summary>
    internal sealed class FrameOverlay : Form
    {
        private readonly int _thickness;

        public FrameOverlay(Rectangle region, int thickness, Color color)
        {
            _thickness = thickness;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = color;                    // the whole window is the ring colour

            Bounds = Rectangle.Inflate(region, thickness, thickness);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

            // force exact physical-pixel bounds regardless of any DPI adjustment
            SetBounds(Left, Top, Width, Height);

            // keep the frame visible to the user but invisible to CopyFromScreen,
            // so it is never baked into a long screenshot or a recording
            try { NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE); } catch { }

            // carve out the interior so only the ring remains -> the region is never covered
            var outer = new Rectangle(0, 0, Width, Height);
            var inner = new Rectangle(_thickness, _thickness, Width - 2 * _thickness, Height - 2 * _thickness);
            var rgn = new Region(outer);
            rgn.Exclude(inner);
            Region = rgn;
        }
    }
}
