using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowWorks.App
{
    internal sealed class TopmostOutlineWindow : NativeWindow, IDisposable
    {
        private const int WsPopup = unchecked((int)0x80000000);
        private const int WsExLayered = 0x80000;
        private const int WsExTransparent = 0x20;
        private const int WsExToolWindow = 0x80;
        private const int WsExNoActivate = 0x08000000;
        private const int RgnDiff = 4;
        private const int SwHide = 0;
        private const uint SwpNoActivate = 0x10;
        private const uint SwpShowWindow = 0x40;
        private const uint SwpNoZOrder = 0x04;
        private const uint LwaAlpha = 2;
        private const uint DwmwaExtendedFrameBounds = 9;
        private const uint DwmwaCloaked = 14;
        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);
        private readonly IntPtr _target;
        private Color _color = Color.FromArgb(0xCC, 0xFF, 0xFF, 0);
        private (int Width, int Height, int Thickness, int Radius, int Offset, bool Inset)? _geometry;

        public TopmostOutlineWindow(IntPtr target)
        {
            _target = target;
            IntPtr previousDpiContext = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            if (previousDpiContext == IntPtr.Zero)
                throw new InvalidOperationException("Per-monitor DPI awareness is required for physical-pixel outline positioning.");
            try
            {
                CreateHandle(new CreateParams
                {
                    Caption = "WindowWorks always-on-top outline",
                    Style = WsPopup,
                    ExStyle = WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate,
                    X = 0, Y = 0, Width = 1, Height = 1
                });
            }
            finally { SetThreadDpiAwarenessContext(previousDpiContext); }
        }

        public void Update(string? color, int thickness, int radius, int reparentBorderThickness = 0)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(color ?? "", @"^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
                color = "#CCFFFF00";
            string hex = color.Length == 7 ? "FF" + color.Substring(1) : color.Substring(1);
            uint argb = uint.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            _color = Color.FromArgb((int)(argb >> 24), (int)(argb >> 16 & 0xFF), (int)(argb >> 8 & 0xFF), (int)(argb & 0xFF));
            SetLayeredWindowAttributes(Handle, 0, _color.A, LwaAlpha);

            IntPtr previousDpiContext = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            if (previousDpiContext == IntPtr.Zero) { Hide(); return; }
            try
            {
                if (!IsWindowVisible(_target) || IsIconic(_target) ||
                    (DwmGetWindowAttribute(_target, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
                { Hide(); return; }
                RECT rect;
                bool haveFrame = false;
                try { haveFrame = DwmGetWindowAttribute(_target, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0; }
                catch { rect = default; }
                if (!haveFrame && !GetWindowRect(_target, out rect)) { Hide(); return; }
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0) { Hide(); return; }
                bool inset = IsZoomed(_target);
                thickness = inset ? Math.Min(Math.Clamp(thickness, 1, 64), 8) : Math.Clamp(thickness, 1, 64);
                int gap = reparentBorderThickness > 0
                    ? (inset ? Math.Min(reparentBorderThickness, 8) : Math.Clamp(reparentBorderThickness, 1, 64)) + 2
                    : 0;
                int x = inset ? rect.Left : rect.Left - thickness - gap;
                int y = inset ? rect.Top : rect.Top - thickness - gap;
                int w = inset ? width : width + (thickness + gap) * 2;
                int h = inset ? height : height + (thickness + gap) * 2;
                int offset = inset ? gap : 0;
                if (w <= (thickness + offset) * 2 || h <= (thickness + offset) * 2) { Hide(); return; }
                radius = Math.Min(Math.Clamp(radius, 0, 128), (Math.Min(w, h) - offset * 2) / 2);
                var geometry = (w, h, thickness, radius, offset, inset);
                if (_geometry != geometry)
                {
                    if (!SetRegion(w, h, thickness, radius, offset)) { Hide(); return; }
                    _geometry = geometry;
                }
                IntPtr previous = GetWindow(_target, 3); // GW_HWNDPREV: insert immediately above the target.
                uint flags = SwpNoActivate | SwpShowWindow;
                if (previous == Handle) flags |= SwpNoZOrder;
                else if (previous == IntPtr.Zero) previous = new IntPtr(-1);
                if (!SetWindowPos(Handle, previous, x, y, w, h, flags)) Hide();
                else InvalidateRect(Handle, IntPtr.Zero, true);
            }
            finally { SetThreadDpiAwarenessContext(previousDpiContext); }
        }

        private bool SetRegion(int width, int height, int thickness, int radius, int offset)
        {
            IntPtr outer = radius == 0 ? CreateRectRgn(offset, offset, width - offset, height - offset)
                : CreateRoundRectRgn(offset, offset, width - offset, height - offset, radius * 2, radius * 2);
            if (outer == IntPtr.Zero) return false;
            int innerRadius = Math.Min(Math.Max(0, radius - thickness), Math.Min(width - thickness * 2, height - thickness * 2) / 2);
            IntPtr inner = innerRadius == 0
                ? CreateRectRgn(offset + thickness, offset + thickness, width - offset - thickness, height - offset - thickness)
                : CreateRoundRectRgn(offset + thickness, offset + thickness, width - offset - thickness, height - offset - thickness, innerRadius * 2, innerRadius * 2);
            if (inner == IntPtr.Zero) { DeleteObject(outer); return false; }
            bool valid = CombineRgn(outer, outer, inner, RgnDiff) != 0;
            DeleteObject(inner);
            if (!valid || SetWindowRgn(Handle, outer, true) == 0) { DeleteObject(outer); return false; }
            return true;
        }

        public void Hide() { if (Handle != IntPtr.Zero) ShowWindow(Handle, SwHide); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; }
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
            if (m.Msg == 0x000F)
            {
                IntPtr dc = BeginPaint(Handle, out var paint);
                if (dc != IntPtr.Zero)
                {
                    GetClientRect(Handle, out var area);
                    IntPtr brush = CreateSolidBrush((uint)(_color.R | _color.G << 8 | _color.B << 16));
                    if (brush != IntPtr.Zero) { FillRect(dc, ref area, brush); DeleteObject(brush); }
                    EndPaint(Handle, ref paint);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT
        {
            public IntPtr Hdc; public int Erase; public RECT Paint; public int Restore, IncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
        }
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT paint);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT paint);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
        [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out RECT rect, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
    }
}