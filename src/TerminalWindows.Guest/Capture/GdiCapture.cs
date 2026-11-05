using System.Runtime.InteropServices;
using TerminalWindows.Protocol;
using static TerminalWindows.Guest.Native;

namespace TerminalWindows.Guest.Capture;

/// <summary>Last-resort capture via BitBlt of the primary screen, with the cursor drawn on top.</summary>
public sealed class GdiCapture : IScreenCapture
{
    public string Name => "gdi";

    public BgraImage Capture()
    {
        int w = GetSystemMetrics(SM_CXSCREEN), h = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("No screen");

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO
        {
            biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        IntPtr dib = CreateDIBSection(mem, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
            throw new InvalidOperationException("CreateDIBSection failed");
        }
        IntPtr old = SelectObject(mem, dib);
        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("BitBlt failed (secure desktop?)");
            DrawCursor(mem);
            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new BgraImage(w, h, pixels);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static void DrawCursor(IntPtr dc)
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
        DrawIconEx(dc, ci.ptScreenPos.X - hx, ci.ptScreenPos.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
    }

    public void Dispose() { }
}
