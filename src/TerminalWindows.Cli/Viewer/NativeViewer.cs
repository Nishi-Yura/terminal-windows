using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Viewer;

/// <summary>
/// The VM's screen in a window of its own (plain Win32 + GDI, no extra libraries). Used when the terminal
/// has no Sixel/kitty graphics, when there is no console at all, or with <c>view --window</c>.
/// Full resolution, real-time mouse (Windows in the VM sees real double-clicks and drags) and keyboard;
/// Japanese typed with this PC's IME arrives as text. The window menu (the icon, or Alt+Space) has the
/// keys this PC keeps for itself (Windows key, Alt+Tab, …), a snapshot and "stop the VM".
/// </summary>
public sealed class NativeViewer
{
    private readonly MachineConfig _machine;
    private readonly object _frameLock = new();
    private BgraImage? _latest;
    private int _screenW, _screenH;
    private IntPtr _hwnd;
    private readonly InputQueue _input = new();
    private char _highSurrogate;
    private int _buttonsDown;
    private Task? _snapshotJob;
    private WndProc? _proc; // kept alive while the window exists

    public ViewerExit Exit { get; private set; } = ViewerExit.Menu;

    public NativeViewer(MachineConfig machine) => _machine = machine;

    private string BaseTitle => $"terminal-windows — {_machine.Name}";

    private static readonly (string Label, string Keys)[] MenuKeys =
    {
        ("Windows キー (スタート) / Windows key", "win"),
        ("Win+R (ファイル名を指定して実行 / Run)", "win+r"),
        ("Win+E (エクスプローラー / Explorer)", "win+e"),
        ("Win+D (デスクトップ / Desktop)", "win+d"),
        ("Alt+Tab", "alt+tab"),
        ("Ctrl+Shift+Esc (タスク マネージャー / Task Manager)", "ctrl+shift+esc"),
        ("Win+Space (入力切り替え / Switch input)", "win+space"),
    };
    private const int IdKeyBase = 0x1000, IdSnapshot = 0x1800, IdStop = 0x1810;

    public async Task RunAsync()
    {
        await using var control = await GuestClient.ConnectAsync(_machine);
        await using var stream = await GuestClient.ConnectAsync(_machine);
        var status = await control.CallAsync("status");
        _screenW = status["screen"]?["width"]?.GetValue<int>() ?? _machine.Width;
        _screenH = status["screen"]?["height"]?.GetValue<int>() ?? _machine.Height;

        stream.Rpc.EventReceived += msg =>
        {
            if (msg.Event != "frame" || msg.Attachments.Count != 1) return;
            try
            {
                var img = Imaging.DecompressFrame(msg.Attachments[0]);
                var data = msg.Json["data"] as JsonObject;
                lock (_frameLock)
                {
                    _latest = img;
                    _screenW = data?["screenWidth"]?.GetValue<int>() ?? img.Width;
                    _screenH = data?["screenHeight"]?.GetValue<int>() ?? img.Height;
                }
                if (_hwnd != IntPtr.Zero) InvalidateRect(_hwnd, IntPtr.Zero, false);
            }
            catch { /* corrupt frame: skip */ }
        };
        // The guest's own resolution: the window scales it, so it stays sharp at any window size.
        await stream.CallAsync("stream_start", new JsonObject { ["width"] = _screenW, ["height"] = _screenH, ["fps"] = 30 });

        using var cts = new CancellationTokenSource();
        var sender = Task.Run(() => SendLoopAsync(control, cts.Token));
        var done = new TaskCompletionSource();
        var ui = new Thread(() =>
        {
            try { WindowLoop(); done.TrySetResult(); }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true, Name = "viewer-window" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        try { await done.Task; }
        finally
        {
            cts.Cancel();
            try { await sender; } catch (OperationCanceledException) { }
        }
    }

    // ---- input → guest ----

    private async Task SendLoopAsync(GuestClient c, CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var (op, args) = await _input.TakeAsync(ct);
            try
            {
                await c.CallAsync(op, args, TimeSpan.FromSeconds(15));
                failures = 0;
            }
            catch (RpcException ex)
            {
                // The guest refused this one (lock screen, bad key): show it and carry on.
                SetTitle($"{BaseTitle}  ({ex.Message})");
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                if (++failures >= 3)
                {
                    SetTitle($"{BaseTitle}  (" + Ui.T("VM との接続が切れました", "lost the connection to the VM") + ")");
                    return;
                }
            }
        }
    }

    private (int X, int Y) ToGuest(int cx, int cy)
    {
        var (dx, dy, dw, dh) = DestRect();
        int sw, sh;
        lock (_frameLock) (sw, sh) = (_screenW, _screenH);
        if (dw <= 0 || dh <= 0) return (0, 0);
        int x = (int)((cx - dx + 0.5) * sw / dw), y = (int)((cy - dy + 0.5) * sh / dh);
        return (Math.Clamp(x, 0, sw - 1), Math.Clamp(y, 0, sh - 1));
    }

    /// <summary>The image's place in the client area: as large as fits, aspect kept, centred.</summary>
    private (int X, int Y, int W, int H) DestRect()
    {
        GetClientRect(_hwnd, out var rc);
        int cw = rc.Right - rc.Left, ch = rc.Bottom - rc.Top;
        int sw, sh;
        lock (_frameLock) (sw, sh) = _latest is { } img ? (img.Width, img.Height) : (_screenW, _screenH);
        if (cw <= 0 || ch <= 0 || sw <= 0 || sh <= 0) return (0, 0, 0, 0);
        double s = Math.Min(cw / (double)sw, ch / (double)sh);
        int w = Math.Max(1, (int)Math.Round(sw * s)), h = Math.Max(1, (int)Math.Round(sh * s));
        return ((cw - w) / 2, (ch - h) / 2, w, h);
    }

    private void Mouse(string action, IntPtr lParam, string button)
    {
        var (x, y) = ToGuest(LoWord(lParam), HiWord(lParam));
        _input.Add("mouse", new JsonObject { ["action"] = action, ["x"] = x, ["y"] = y, ["button"] = button });
    }

    private static bool Down(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    /// <summary>Keys that type no character, sent as chords (with whatever modifiers are held).</summary>
    internal static string? SpecialKeyName(int vk) => vk switch
    {
        0x08 => "backspace", 0x09 => "tab", 0x0D => "enter", 0x1B => "esc",
        0x21 => "pageup", 0x22 => "pagedown", 0x23 => "end", 0x24 => "home",
        0x25 => "left", 0x26 => "up", 0x27 => "right", 0x28 => "down",
        0x2C => "printscreen", 0x2D => "insert", 0x2E => "delete", 0x13 => "pause", 0x5D => "apps",
        >= 0x70 and <= 0x87 => "f" + (vk - 0x6F),
        _ => null,
    };

    /// <summary>The chord name of a character key, for Ctrl/Alt combinations (ctrl+c, alt+f, ctrl+/).</summary>
    internal static string? CharKeyName(int vk)
    {
        if (vk is >= 'A' and <= 'Z') return ((char)(vk + 32)).ToString();
        if (vk is >= '0' and <= '9') return ((char)vk).ToString();
        if (vk == 0x20) return "space";
        if (vk is >= 0x60 and <= 0x69) return "num" + (vk - 0x60);
        uint ch = MapVirtualKeyW((uint)vk, 2 /* MAPVK_VK_TO_CHAR */) & 0x7FFF;
        return ch is > 0x20 and < 0x7F ? ((char)ch).ToString() : null;
    }

    private bool OnKeyDown(int vk)
    {
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) return true; // modifiers alone
        bool ctrl = Down(0x11), alt = Down(0x12), shift = Down(0x10);
        if (alt && vk == 0x20) return false; // Alt+Space: this window's menu
        var special = SpecialKeyName(vk);
        if (special is null && !ctrl && !alt) return true; // a character: WM_CHAR types it
        var name = special ?? CharKeyName(vk);
        if (name is null) return true;
        var mods = new StringBuilder();
        if (ctrl) mods.Append("ctrl+");
        if (alt) mods.Append("alt+");
        if (shift) mods.Append("shift+");
        _input.Add("key", new JsonObject { ["keys"] = mods + name });
        return true;
    }

    private void OnChar(char c)
    {
        if (Down(0x11) && !Down(0x12)) return; // Ctrl+key was sent as a chord
        if (c < 0x20 || c == 0x7F) return;    // Enter, Tab, Backspace, Esc: sent as keys
        if (char.IsHighSurrogate(c)) { _highSurrogate = c; return; }
        string text = char.IsLowSurrogate(c) && _highSurrogate != 0 ? new string(new[] { _highSurrogate, c }) : c.ToString();
        _highSurrogate = '\0';
        _input.AddText(text);
    }

    private void OnMenu(int id)
    {
        if (id >= IdKeyBase && id < IdKeyBase + MenuKeys.Length * 16)
        {
            _input.Add("key", new JsonObject { ["keys"] = MenuKeys[(id - IdKeyBase) / 16].Keys });
        }
        else if (id == IdSnapshot)
        {
            if (_snapshotJob is { IsCompleted: false }) return;
            SetTitle(BaseTitle + Ui.T("  （スナップショットを撮っています…）", "  (taking a snapshot…)"));
            _snapshotJob = Task.Run(async () =>
            {
                try
                {
                    var label = await new MachineManager(log: _ => { }).SnapshotAsync(_machine, null);
                    SetTitle(BaseTitle + Ui.T($"  （スナップショット「{label}」を保存しました）", $"  (snapshot '{label}' saved)"));
                }
                catch (Exception ex) { SetTitle(BaseTitle + Ui.T("  （スナップショットに失敗: ", "  (snapshot failed: ") + ex.Message + ")"); }
            });
        }
        else if (id == IdStop)
        {
            Exit = ViewerExit.StopVm;
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void SetTitle(string t)
    {
        if (_hwnd != IntPtr.Zero) SetWindowTextW(_hwnd, t);
    }

    // ---- window ----

    private void WindowLoop()
    {
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { } // per-monitor v2: sharp on high-DPI screens
        var hInst = GetModuleHandleW(null);
        _proc = WindowProc;
        var cls = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0x0002 | 0x0001, // CS_HREDRAW | CS_VREDRAW (no CS_DBLCLKS: the guest detects double-clicks itself)
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInst,
            hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
            hIcon = ExtractIconW(hInst, Environment.ProcessPath ?? "", 0),
            lpszClassName = "TerminalWindowsViewer",
        };
        RegisterClassExW(ref cls);

        // Initial size: the guest at this screen's scale, at most 90% of the screen.
        uint dpi = GetDpiForSystemSafe();
        int sw, sh;
        lock (_frameLock) (sw, sh) = (_screenW, _screenH);
        double scale = dpi / 96.0;
        int maxW = (int)(GetSystemMetrics(0) * 0.9), maxH = (int)(GetSystemMetrics(1) * 0.85);
        scale = Math.Min(scale, Math.Min(maxW / (double)sw, maxH / (double)sh));
        var rc = new RECT { Right = (int)(sw * scale), Bottom = (int)(sh * scale) };
        const uint style = 0x00CF0000; // WS_OVERLAPPEDWINDOW
        AdjustWindowRectEx(ref rc, style, false, 0);
        _hwnd = CreateWindowExW(0, cls.lpszClassName, BaseTitle, style | 0x10000000 /* WS_VISIBLE */,
            unchecked((int)0x80000000), unchecked((int)0x80000000), rc.Right - rc.Left, rc.Bottom - rc.Top,
            IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new CliException("Could not open the viewer window (error " + Marshal.GetLastWin32Error() + ").");

        var menu = GetSystemMenu(_hwnd, false);
        AppendMenuW(menu, 0x0800 /* MF_SEPARATOR */, IntPtr.Zero, null);
        for (int i = 0; i < MenuKeys.Length; i++) AppendMenuW(menu, 0, new IntPtr(IdKeyBase + i * 16), "VM へ送る: " + MenuKeys[i].Label);
        AppendMenuW(menu, 0x0800, IntPtr.Zero, null);
        AppendMenuW(menu, 0, new IntPtr(IdSnapshot), Ui.T("スナップショットを撮る", "Take a snapshot"));
        AppendMenuW(menu, 0, new IntPtr(IdStop), Ui.T("VM を止めて閉じる（状態は保存）", "Stop the VM and close (state saved)"));
        SetForegroundWindow(_hwnd);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        _hwnd = IntPtr.Zero;
    }

    private const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_ERASEBKGND = 0x0014,
        WM_KEYDOWN = 0x0100, WM_CHAR = 0x0102, WM_SYSKEYDOWN = 0x0104, WM_SYSCHAR = 0x0106, WM_SYSCOMMAND = 0x0112,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
        WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E, WM_CAPTURECHANGED = 0x0215;

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_PAINT:
                Paint(hwnd);
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                Mouse("move", lParam, "left");
                return IntPtr.Zero;
            case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN:
                if (_buttonsDown++ == 0) SetCapture(hwnd);
                Mouse("down", lParam, msg == WM_LBUTTONDOWN ? "left" : msg == WM_RBUTTONDOWN ? "right" : "middle");
                return IntPtr.Zero;
            case WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP:
                Mouse("up", lParam, msg == WM_LBUTTONUP ? "left" : msg == WM_RBUTTONUP ? "right" : "middle");
                if (_buttonsDown > 0 && --_buttonsDown == 0) ReleaseCapture();
                return IntPtr.Zero;
            case WM_CAPTURECHANGED:
                _buttonsDown = 0;
                return IntPtr.Zero;
            case WM_MOUSEWHEEL or WM_MOUSEHWHEEL:
            {
                var pt = new POINT { X = LoWord(lParam), Y = HiWord(lParam) };
                ScreenToClient(hwnd, ref pt);
                var (x, y) = ToGuest(pt.X, pt.Y);
                int notches = (short)((long)wParam >> 16) / 120;
                if (notches == 0) return IntPtr.Zero;
                var args = new JsonObject { ["x"] = x, ["y"] = y };
                if (msg == WM_MOUSEWHEEL) args["dy"] = -notches * 3; else args["dx"] = notches * 3;
                _input.Add("scroll", args);
                return IntPtr.Zero;
            }
            case WM_KEYDOWN or WM_SYSKEYDOWN:
                if (OnKeyDown((int)wParam)) return IntPtr.Zero;
                break;
            case WM_CHAR:
                OnChar((char)(int)wParam);
                return IntPtr.Zero;
            case WM_SYSCHAR:
                if ((int)wParam == ' ') break; // Alt+Space opens the window menu
                return IntPtr.Zero; // no beep for Alt+letter (already sent as a chord)
            case WM_SYSCOMMAND:
                int id = (int)wParam & 0xFFF0;
                if (id < 0xF000) { OnMenu(id); return IntPtr.Zero; }
                break;
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Paint(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        try
        {
            GetClientRect(hwnd, out var client);
            BgraImage? img;
            lock (_frameLock) img = _latest;
            var (x, y, w, h) = DestRect();
            var black = GetStockObject(4 /* BLACK_BRUSH */);
            if (img is null || w == 0)
            {
                FillRect(hdc, ref client, black);
                return;
            }
            // Letterbox bars only (painting the image area too would flicker).
            var bars = new[]
            {
                new RECT { Left = 0, Top = 0, Right = client.Right, Bottom = y },
                new RECT { Left = 0, Top = y + h, Right = client.Right, Bottom = client.Bottom },
                new RECT { Left = 0, Top = y, Right = x, Bottom = y + h },
                new RECT { Left = x + w, Top = y, Right = client.Right, Bottom = y + h },
            };
            foreach (var r in bars) { var rr = r; if (rr.Right > rr.Left && rr.Bottom > rr.Top) FillRect(hdc, ref rr, black); }
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = img.Width, biHeight = -img.Height, // top-down
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            // Shrinking: HALFTONE (smooth text). Same size or larger: plain copy (sharp pixels).
            SetStretchBltMode(hdc, w < img.Width ? 4 /* HALFTONE */ : 3 /* COLORONCOLOR */);
            SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
            StretchDIBits(hdc, x, y, w, h, 0, 0, img.Width, img.Height, img.Pixels, ref bmi, 0, 0x00CC0020 /* SRCCOPY */);
        }
        finally { EndPaint(hwnd, ref ps); }
    }

    private static int LoWord(IntPtr p) => (short)((long)p & 0xFFFF);
    private static int HiWord(IntPtr p) => (short)(((long)p >> 16) & 0xFFFF);

    private static uint GetDpiForSystemSafe()
    {
        try { return GetDpiForSystem(); } catch (EntryPointNotFoundException) { return 96; }
    }

    /// <summary>Input waiting to be sent, in order. Mouse moves and typed text are merged so a slow
    /// guest never falls behind the pointer.</summary>
    internal sealed class InputQueue
    {
        private readonly LinkedList<(string Op, JsonObject Args)> _items = new();
        private readonly SemaphoreSlim _count = new(0);

        public void Add(string op, JsonObject args)
        {
            lock (_items)
            {
                if (op == "mouse" && (string?)args["action"] == "move" && _items.Last is { } last
                    && last.Value.Op == "mouse" && (string?)last.Value.Args["action"] == "move")
                {
                    last.Value = (op, args); // replace the stale position
                    return;
                }
                _items.AddLast((op, args));
            }
            _count.Release();
        }

        public void AddText(string text)
        {
            lock (_items)
            {
                if (_items.Last is { } last && last.Value.Op == "type")
                {
                    last.Value.Args["text"] = (string?)last.Value.Args["text"] + text;
                    return;
                }
                _items.AddLast(("type", new JsonObject { ["text"] = text }));
            }
            _count.Release();
        }

        public async Task<(string Op, JsonObject Args)> TakeAsync(CancellationToken ct)
        {
            await _count.WaitAsync(ct);
            lock (_items)
            {
                var v = _items.First!.Value;
                _items.RemoveFirst();
                return v;
            }
        }

        public int Count { get { lock (_items) return _items.Count; } }
    }

    // ---- Win32 ----

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc; public bool fErase; public RECT rcPaint; public bool fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint type);
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr h, bool revert);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? item);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr h, string text);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRectEx(ref RECT r, uint style, bool menu, uint ex);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIconW(IntPtr inst, string path, uint index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")]
    private static extern int StretchDIBits(IntPtr dc, int xd, int yd, int wd, int hd, int xs, int ys, int ws, int hs, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);
}
