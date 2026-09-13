using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SimpleShot
{
    /// <summary>
    /// Records recent mouse clicks and wheel scrolls via a low-level hook and paints
    /// fading annotations (an expanding ring for clicks, a chevron for scrolls) into the
    /// recorded frames. Install/Dispose must run on the UI thread (it needs a message
    /// pump); <see cref="Draw"/> is safe to call from the capture thread.
    /// </summary>
    internal sealed class MouseAnnotator : IDisposable
    {
        private struct Ev { public int X, Y, Kind; public long T; }   // Kind: 0 L,1 R,2 M,10 up,11 down

        private readonly List<Ev> _events = new List<Ev>();
        private readonly object _lock = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private NativeMethods.LowLevelMouseProc _proc;   // kept alive to survive GC
        private IntPtr _hook = IntPtr.Zero;

        private const long ClickLifeMs = 480;
        private const long ScrollLifeMs = 650;

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _proc = HookProc;
            IntPtr hMod = NativeMethods.GetModuleHandle(null);
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, hMod, 0);
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int kind = -1;
                switch (msg)
                {
                    case NativeMethods.WM_LBUTTONDOWN: kind = 0; break;
                    case NativeMethods.WM_RBUTTONDOWN: kind = 1; break;
                    case NativeMethods.WM_MBUTTONDOWN: kind = 2; break;
                    case NativeMethods.WM_MOUSEWHEEL: kind = -2; break;
                }
                if (kind != -1)
                {
                    var data = (NativeMethods.MSLLHOOKSTRUCT)
                        Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                    if (kind == -2)
                    {
                        short delta = (short)((data.mouseData >> 16) & 0xFFFF);
                        kind = delta >= 0 ? 10 : 11;
                    }
                    lock (_lock)
                    {
                        _events.Add(new Ev { X = data.pt.X, Y = data.pt.Y, Kind = kind, T = _clock.ElapsedMilliseconds });
                        if (_events.Count > 64) _events.RemoveRange(0, _events.Count - 64);
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>Paints all still-alive annotations, translated into region-local coords.</summary>
        public void Draw(Graphics g, Rectangle region)
        {
            long now = _clock.ElapsedMilliseconds;
            Ev[] snap;
            lock (_lock)
            {
                if (_events.Count == 0) return;
                snap = _events.ToArray();
            }

            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var ev in snap)
            {
                int x = ev.X - region.Left, y = ev.Y - region.Top;
                if (x < -40 || y < -40 || x > region.Width + 40 || y > region.Height + 40) continue;

                if (ev.Kind < 10)
                {
                    long age = now - ev.T;
                    if (age < 0 || age > ClickLifeMs) continue;
                    float p = age / (float)ClickLifeMs;      // 0..1
                    float r = 7 + p * 24;
                    int a = (int)(210 * (1f - p));
                    Color c = ev.Kind == 0 ? Color.FromArgb(a, 255, 80, 60)
                            : ev.Kind == 1 ? Color.FromArgb(a, 70, 150, 255)
                                           : Color.FromArgb(a, 255, 200, 40);
                    using (var pen = new Pen(c, 3f))
                        g.DrawEllipse(pen, x - r, y - r, r * 2, r * 2);
                }
                else
                {
                    long age = now - ev.T;
                    if (age < 0 || age > ScrollLifeMs) continue;
                    float p = age / (float)ScrollLifeMs;
                    int a = (int)(210 * (1f - p));
                    bool up = ev.Kind == 10;
                    int drift = (int)(p * 10);
                    int cy = up ? y - 18 + drift : y + 18 - drift;   // chevron drifts toward travel
                    using (var pen = new Pen(Color.FromArgb(a, 90, 200, 120), 3f))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        if (up)
                        {
                            g.DrawLine(pen, x - 9, cy, x, cy - 9);
                            g.DrawLine(pen, x, cy - 9, x + 9, cy);
                        }
                        else
                        {
                            g.DrawLine(pen, x - 9, cy, x, cy + 9);
                            g.DrawLine(pen, x, cy + 9, x + 9, cy);
                        }
                    }
                }
            }
            g.SmoothingMode = old;
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                try { NativeMethods.UnhookWindowsHookEx(_hook); } catch { }
                _hook = IntPtr.Zero;
            }
            _proc = null;
        }
    }
}
