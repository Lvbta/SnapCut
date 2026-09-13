using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SimpleShot
{
    /// <summary>
    /// Snapshots the z-ordered list of visible top-level windows so the overlay can
    /// highlight the window under the cursor before the user drags (WeChat behavior).
    /// </summary>
    internal sealed class WindowDetector
    {
        private readonly List<Rectangle> _rects = new List<Rectangle>();

        public WindowDetector(IntPtr ignoreHwnd, Rectangle screenBounds)
        {
            NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (hWnd == ignoreHwnd) return true;
                if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd)) return true;
                if (NativeMethods.GetWindowTextLength(hWnd) == 0) return true;

                // skip UWP "cloaked" ghost windows
                int cloaked;
                if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                        out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

                // DWM frame bounds are tighter than GetWindowRect (no invisible resize borders)
                NativeMethods.RECT r;
                if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                        out r, Marshal.SizeOf(typeof(NativeMethods.RECT))) != 0)
                    NativeMethods.GetWindowRect(hWnd, out r);

                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                rect.Intersect(screenBounds);
                if (rect.Width > 20 && rect.Height > 20) _rects.Add(rect);
                return true;
            }, IntPtr.Zero);

            _rects.Add(screenBounds); // fallback: whole screen
        }

        /// <summary>Topmost window rectangle containing the point (EnumWindows returns in z-order).</summary>
        public Rectangle HitTest(Point p)
        {
            foreach (var r in _rects)
                if (r.Contains(p)) return r;
            return _rects[_rects.Count - 1];
        }
    }
}
