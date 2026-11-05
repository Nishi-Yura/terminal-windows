using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Viewer;

/// <summary>
/// <c>view-demo</c>: a fake guest on 127.0.0.1 that streams a synthetic 1600x900 screen (grid with
/// coordinates, an animation, and a log of the input it received). Lets you check the terminal viewer
/// (Sixel / kitty, clicks, drags, wheel, typing, Ctrl+G, Ctrl+]) without a VM. It records input only;
/// nothing is ever sent to the host's mouse or keyboard.
/// </summary>
public sealed class DemoGuest : IAsyncDisposable
{
    public const int Width = 1600, Height = 900;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _token = Secrets.NewToken();
    private readonly ConcurrentQueue<string> _events = new();
    private readonly ConcurrentQueue<(int X, int Y, string Label)> _marks = new();
    private readonly object _renderLock = new();
    private int _frame;
    private (int X, int Y)? _pointer;
    private DateTime _lastDown = DateTime.MinValue;
    private (int X, int Y) _lastDownAt;
    private int _clickRun;

    public MachineConfig Machine { get; private set; } = null!;

    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var protocol = a.Opt("graphics");
        a.RejectUnknown();
        var caps = TerminalCaps.Detect(protocol);
        if (caps.Protocol == GraphicsProtocol.None)
            throw new CliException($"{caps.TerminalName}: no inline graphics detected. Run this in Windows Terminal 1.22+ or WezTerm (or force with --graphics sixel|kitty).");
        await using var demo = new DemoGuest();
        demo.Start();
        await new TerminalViewer(demo.Machine, caps).RunAsync();
        Console.WriteLine("Demo viewer closed. Received input:");
        foreach (var e in demo._events) Console.WriteLine("  " + e);
        return 0;
    }

    public void Start()
    {
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Machine = new MachineConfig { Name = "demo", VmName = "demo", HostPort = port, Width = Width, Height = Height, ProtectedToken = Secrets.Protect(_token) };
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var session = new RpcServerSession(client.GetStream(), _token, () => new JsonObject { ["protocol"] = ProtocolConstants.Version, ["helperVersion"] = "demo" });
                    try { await session.RunAsync(HandleAsync, _cts.Token); } catch { }
                }
            });
        }
    }

    private void Record(string text)
    {
        _events.Enqueue($"{DateTime.Now:HH:mm:ss} {text}");
        while (_events.Count > 12) _events.TryDequeue(out _);
    }

    private void Mark(int x, int y, string label)
    {
        _marks.Enqueue((x, y, label));
        while (_marks.Count > 8) _marks.TryDequeue(out _);
    }

    private Task<Reply> HandleAsync(Message req, RpcServerSession session, CancellationToken ct)
    {
        var a = req.Args;
        int I(string k) => a[k]?.GetValue<int>() ?? 0;
        string S(string k) => a[k]?.GetValue<string>() ?? "";
        switch (req.Op)
        {
            case "status":
                return Task.FromResult(Reply.Of(new JsonObject
                {
                    ["screen"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = Width, ["height"] = Height },
                    ["session"] = "desktop", ["helperVersion"] = "demo",
                }));
            case "stream_start":
            {
                int w = I("width"), h = I("height"), fps = Math.Clamp(I("fps"), 1, 30);
                _ = Task.Run(() => StreamAsync(session, w, h, fps, ct));
                return Task.FromResult(Reply.Of(new JsonObject { ["started"] = true }));
            }
            case "click":
                Record($"click ({I("x")},{I("y")}) {S("button")}");
                Mark(I("x"), I("y"), $"{S("button")} ({I("x")},{I("y")})");
                break;
            case "mouse":
            {
                int x = I("x"), y = I("y");
                var action = S("action");
                if (action == "move")
                {
                    _pointer = (x, y); // drawn as a live cursor; not logged (would flood the log)
                    break;
                }
                _pointer = (x, y);
                if (action == "down")
                {
                    // Same rule Windows uses: second press within 500 ms and a few pixels = double click.
                    var now = DateTime.UtcNow;
                    bool isDouble = (now - _lastDown).TotalMilliseconds < 500 && Math.Abs(x - _lastDownAt.X) < 8 && Math.Abs(y - _lastDownAt.Y) < 8;
                    _clickRun = isDouble ? _clickRun + 1 : 1;
                    _lastDown = now;
                    _lastDownAt = (x, y);
                    var kind = _clickRun switch { 2 => " (double click)", 3 => " (triple click)", _ => "" };
                    Record($"down ({x},{y}) {S("button")}{kind}");
                    Mark(x, y, $"{S("button")} ({x},{y}){kind}");
                }
                else
                {
                    Record($"up ({x},{y}) {S("button")}");
                }
                break;
            }
            case "drag":
                Record($"drag ({I("x1")},{I("y1")}) -> ({I("x2")},{I("y2")})");
                Mark(I("x2"), I("y2"), "drag end");
                break;
            case "scroll":
                Record($"scroll dy={I("dy")} dx={I("dx")} at ({I("x")},{I("y")})");
                break;
            case "type":
                Record($"type \"{S("text")}\"");
                break;
            case "key":
                Record($"key {S("keys")}");
                break;
            default:
                Record($"{req.Op}");
                break;
        }
        return Task.FromResult(Reply.Of(new JsonObject { ["done"] = true }));
    }

    private async Task StreamAsync(RpcServerSession session, int w, int h, int fps, CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        // A newer stream_start for this session simply adds another loop; the viewer reconnects rarely, so keep it simple.
        while (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            try
            {
                var img = Render();
                var (fw, fh) = Imaging.Fit(Width, Height, w, h);
                await session.SendEventAsync("frame", new JsonObject { ["screenWidth"] = Width, ["screenHeight"] = Height, ["encoding"] = "bgra-lz4" },
                    new ReadOnlyMemory<byte>[] { Imaging.CompressFrame(Imaging.Resize(img, fw, fh)) }, ct);
            }
            catch { break; }
            await Task.Delay(interval, ct).ContinueWith(_ => { });
        }
    }

    // ---- GDI rendering ----

    public BgraImage Render()
    {
        lock (_renderLock)
        {
            _frame++;
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr dc = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFO { biSize = 40, biWidth = Width, biHeight = -Height, biPlanes = 1, biBitCount = 32 };
            IntPtr dib = CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr oldBmp = SelectObject(dc, dib);
            IntPtr bigFont = CreateFontW(-36, 0, 0, 0, 700, 0, 0, 0, 128, 0, 0, 5, 0, "Yu Gothic UI");
            IntPtr font = CreateFontW(-20, 0, 0, 0, 400, 0, 0, 0, 128, 0, 0, 5, 0, "Yu Gothic UI");
            IntPtr small = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, 128, 0, 0, 5, 0, "Consolas");
            try
            {
                Fill(dc, 0, 0, Width, Height, 0x00382A1E);              // dark blue background (0x00BBGGRR)
                for (int x = 0; x < Width; x += 100) Fill(dc, x, 0, 1, Height, 0x00604A3A);
                for (int y = 0; y < Height; y += 100) Fill(dc, 0, y, Width, 1, 0x00604A3A);
                SetBkMode(dc, 1);
                SelectObject(dc, small);
                SetTextColor(dc, 0x00A09080);
                for (int x = 0; x < Width; x += 200)
                    for (int y = 0; y < Height; y += 200)
                        Text(dc, x + 4, y + 2, $"{x},{y}");

                SelectObject(dc, bigFont);
                SetTextColor(dc, 0x00FFFFFF);
                Text(dc, 40, 30, "terminal-windows ビューア確認（デモ用ゲスト）");
                SelectObject(dc, font);
                SetTextColor(dc, 0x00D0E0F0);
                Text(dc, 40, 85, "クリック・ドラッグ・ホイール・文字入力（日本語も）・Ctrl+G パレットを試してください。Ctrl+] で終了します。");
                Text(dc, 40, 115, $"画面 {Width}x{Height}  フレーム #{_frame}  {DateTime.Now:HH:mm:ss.f}");

                // Animation: a bar sweeping left to right shows the frame rate.
                int bx = (_frame * 12) % (Width - 120);
                Fill(dc, bx, 160, 120, 24, 0x0000A5FF);

                // Corner markers check that the whole screen is visible.
                Fill(dc, 0, 0, 12, 12, 0x000000FF);
                Fill(dc, Width - 12, 0, 12, 12, 0x0000FF00);
                Fill(dc, 0, Height - 12, 12, 12, 0x00FF0000);
                Fill(dc, Width - 12, Height - 12, 12, 12, 0x00FFFFFF);

                // Click markers
                foreach (var (x, y, label) in _marks)
                {
                    Fill(dc, x - 15, y, 31, 2, 0x003030FF);
                    Fill(dc, x, y - 15, 2, 31, 0x003030FF);
                    SetTextColor(dc, 0x008080FF);
                    Text(dc, x + 8, y + 6, label);
                }

                // Live pointer (real-time mouse mode)
                if (_pointer is { } ptr)
                {
                    Fill(dc, ptr.X - 6, ptr.Y - 6, 12, 12, 0x0000FFFF);
                    Fill(dc, ptr.X - 3, ptr.Y - 3, 6, 6, 0x00000000);
                }

                // Event log
                Fill(dc, 1000, 220, 560, 400, 0x00281C14);
                SetTextColor(dc, 0x00FFFFFF);
                Text(dc, 1016, 230, "受信した入力:");
                int ly = 262;
                SetTextColor(dc, 0x00B0F0B0);
                foreach (var e in _events)
                {
                    Text(dc, 1016, ly, e.Length > 46 ? e[..46] + "…" : e);
                    ly += 28;
                }
                GdiFlush();

                var pixels = new byte[Width * Height * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return new BgraImage(Width, Height, pixels);
            }
            finally
            {
                SelectObject(dc, oldBmp);
                DeleteObject(bigFont);
                DeleteObject(font);
                DeleteObject(small);
                DeleteObject(dib);
                DeleteDC(dc);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }

    private static void Fill(IntPtr dc, int x, int y, int w, int h, uint color)
    {
        var brush = CreateSolidBrush(color);
        var r = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        FillRect(dc, ref r, brush);
        DeleteObject(brush);
    }

    private static void Text(IntPtr dc, int x, int y, string s) => TextOutW(dc, x, y, s, s.Length);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        await Task.Yield();
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant, bmiColors;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(IntPtr dc, int x, int y, string s, int len);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitchFamily, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
