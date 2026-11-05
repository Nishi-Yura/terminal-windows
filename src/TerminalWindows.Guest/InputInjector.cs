using System.Runtime.InteropServices;
using TerminalWindows.Protocol;
using static TerminalWindows.Guest.Native;

namespace TerminalWindows.Guest;

/// <summary>
/// Injects input with SendInput. Coordinates are physical pixels of the virtual screen — the same
/// coordinate system as screenshots (the process is per-monitor DPI aware v2).
/// </summary>
public static class InputInjector
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        uint sent = SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput sent {sent}/{inputs.Length} events (error {Marshal.GetLastWin32Error()}; secure desktop or UIPI?)");
    }

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
    };

    private static INPUT Key(ushort vk, bool up, ushort scan = 0, uint extraFlags = 0)
    {
        uint flags = extraFlags | (up ? KEYEVENTF_KEYUP : 0);
        if (vk != 0 && KeyChord.IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        if (vk != 0 && scan == 0) scan = (ushort)MapVirtualKeyW(vk, 0);
        return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };
    }

    public static (int X, int Y, int W, int H) VirtualScreen() => (
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    private static INPUT MoveTo(int x, int y)
    {
        var (vx, vy, vw, vh) = VirtualScreen();
        if (x < vx || y < vy || x >= vx + vw || y >= vy + vh)
            throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) is outside the screen {vw}x{vh}");
        int nx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        return Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny);
    }

    private static (uint Down, uint Up) ButtonFlags(string button) => button.ToLowerInvariant() switch
    {
        "left" => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => throw new ArgumentException($"Unknown button '{button}'"),
    };

    public static void Move(int x, int y) => Send(MoveTo(x, y));

    /// <summary>Presses or releases a button at (x, y). Used by the viewer to forward mouse events in real
    /// time, so Windows itself detects double/triple clicks and shows drags as they happen.</summary>
    public static void Button(int x, int y, string button, bool down)
    {
        var (d, u) = ButtonFlags(button);
        Send(MoveTo(x, y), Mouse(down ? d : u));
    }

    public static async Task ClickAsync(int x, int y, string button = "left", int count = 1)
    {
        if (count is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(count), "count must be 1-3");
        var (down, up) = ButtonFlags(button);
        Send(MoveTo(x, y));
        await Task.Delay(15);
        for (int i = 0; i < count; i++)
        {
            Send(Mouse(down), Mouse(up));
            if (i + 1 < count) await Task.Delay(40);
        }
    }

    public static async Task DragAsync(int x1, int y1, int x2, int y2, string button = "left", int steps = 20)
    {
        var (down, up) = ButtonFlags(button);
        Send(MoveTo(x1, y1));
        await Task.Delay(30);
        Send(Mouse(down));
        await Task.Delay(60);
        steps = Math.Clamp(steps, 2, 200);
        for (int i = 1; i <= steps; i++)
        {
            Send(MoveTo(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps));
            await Task.Delay(10);
        }
        await Task.Delay(60);
        Send(Mouse(up));
    }

    /// <summary>dy &gt; 0 scrolls down (content moves up), dx &gt; 0 scrolls right. Units are wheel notches.</summary>
    public static void Scroll(int? x, int? y, int dx, int dy)
    {
        var list = new List<INPUT>();
        if (x.HasValue && y.HasValue) list.Add(MoveTo(x.Value, y.Value));
        if (dy != 0) list.Add(Mouse(MOUSEEVENTF_WHEEL, data: -dy * 120));
        if (dx != 0) list.Add(Mouse(MOUSEEVENTF_HWHEEL, data: dx * 120));
        Send(list.ToArray());
    }

    /// <summary>
    /// Types text as Unicode characters (KEYEVENTF_UNICODE), so Japanese text needs no IME.
    /// The IME of the foreground window is switched off while typing (otherwise it can re-interpret
    /// the characters) and restored afterwards.
    /// </summary>
    public static async Task TypeAsync(string text, int delayMs = 5)
    {
        var fg = GetForegroundWindow();
        var ime = fg == IntPtr.Zero ? IntPtr.Zero : ImmGetDefaultIMEWnd(fg);
        bool wasOpen = ime != IntPtr.Zero && SendMessageW(ime, WM_IME_CONTROL, IMC_GETOPENSTATUS, IntPtr.Zero) != IntPtr.Zero;
        if (wasOpen) SendMessageW(ime, WM_IME_CONTROL, IMC_SETOPENSTATUS, IntPtr.Zero);
        try
        {
            foreach (var ch in text.Replace("\r\n", "\n"))
            {
                if (ch == '\n') Send(Key(0x0D, false), Key(0x0D, true));
                else if (ch == '\t') Send(Key(0x09, false), Key(0x09, true));
                else Send(Key(0, false, ch, KEYEVENTF_UNICODE), Key(0, true, ch, KEYEVENTF_UNICODE));
                if (delayMs > 0) await Task.Delay(delayMs);
            }
        }
        finally
        {
            if (wasOpen) SendMessageW(ime, WM_IME_CONTROL, IMC_SETOPENSTATUS, new IntPtr(1));
        }
    }

    /// <summary>Presses chords like <c>ctrl+c</c>. Punctuation is resolved against the foreground
    /// window's keyboard layout, so JIS (106/109) and US layouts both produce the requested character.</summary>
    public static async Task KeyAsync(string keys)
    {
        foreach (var chord in KeyChord.ParseSequence(keys))
        {
            var mods = chord.Modifiers.ToList();
            ushort vk;
            if (chord.Key.IsCharacter)
            {
                var hkl = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
                short res = VkKeyScanExW(chord.Key.Character!.Value, hkl);
                if (res == -1) throw new ArgumentException($"Character '{chord.Key.Character}' has no key on the current layout");
                vk = (ushort)(res & 0xFF);
                int shift = (res >> 8) & 0xFF;
                if ((shift & 1) != 0 && !mods.Contains(KeyChord.VK_SHIFT)) mods.Add(KeyChord.VK_SHIFT);
                if ((shift & 2) != 0 && !mods.Contains(KeyChord.VK_CONTROL)) mods.Add(KeyChord.VK_CONTROL);
                if ((shift & 4) != 0 && !mods.Contains(KeyChord.VK_MENU)) mods.Add(KeyChord.VK_MENU);
            }
            else
            {
                vk = chord.Key.VirtualKey;
            }

            var seq = new List<INPUT>();
            foreach (var m in mods) seq.Add(Key(m, false));
            seq.Add(Key(vk, false));
            seq.Add(Key(vk, true));
            for (int i = mods.Count - 1; i >= 0; i--) seq.Add(Key(mods[i], true));
            Send(seq.ToArray());
            await Task.Delay(30);
        }
    }

    public static bool? ImeOpen()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var ime = ImmGetDefaultIMEWnd(fg);
        if (ime == IntPtr.Zero) return null;
        return SendMessageW(ime, WM_IME_CONTROL, IMC_GETOPENSTATUS, IntPtr.Zero) != IntPtr.Zero;
    }
}
