using System.Runtime.InteropServices;
using TerminalWindows.Protocol;
using static TerminalWindows.Guest.Native;

namespace TerminalWindows.Guest.Capture;

/// <summary>Draws the current mouse cursor onto a captured frame (DXGI duplication does not include it).</summary>
internal static class CursorOverlay
{
    public static void Draw(BgraImage img)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var info))
        {
            hx = info.xHotspot;
            hy = info.yHotspot;
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }

        // Only a small window around the cursor is copied through GDI.
        const int box = 64;
        int x0 = Math.Clamp(ci.ptScreenPos.X - hx, 0, Math.Max(0, img.Width - 1));
        int y0 = Math.Clamp(ci.ptScreenPos.Y - hy, 0, Math.Max(0, img.Height - 1));
        int w = Math.Min(box, img.Width - x0), h = Math.Min(box, img.Height - y0);
        if (w <= 0 || h <= 0) return;

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(img.Pixels, ((y0 + y) * img.Width + x0) * 4, bits + y * w * 4, w * 4);
            DrawIconEx(mem, ci.ptScreenPos.X - hx - x0, ci.ptScreenPos.Y - hy - y0, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
            for (int y = 0; y < h; y++)
                Marshal.Copy(bits + y * w * 4, img.Pixels, ((y0 + y) * img.Width + x0) * 4, w * 4);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
