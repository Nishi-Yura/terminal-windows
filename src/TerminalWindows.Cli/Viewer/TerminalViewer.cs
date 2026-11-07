using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Cli;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Viewer;

/// <summary>
/// Shows the guest screen inside the terminal (kitty graphics or Sixel) and forwards keyboard and
/// mouse input to the guest. Ctrl+G opens a palette of Windows shortcuts the terminal would
/// otherwise swallow (Win, Alt+Tab, ...). Ctrl+] leaves the viewer; the VM keeps running.
/// </summary>
/// <summary>How the viewer was left: plain exit, "back to the menu", or "stop the VM".</summary>
public enum ViewerExit { Quit, Menu, StopVm }

public sealed class TerminalViewer
{
    private readonly MachineConfig _machine;
    private readonly TerminalCaps _caps;
    /// <summary>What is actually drawn: the detected protocol, or after probing the terminal Sixel or blocks.</summary>
    private GraphicsProtocol _protocol;
    private readonly BlockEncoder _blocks = new() { TopRow = 1 };
    private readonly AdaptiveSixelEncoder _sixel = new();
    private readonly object _frameLock = new();
    private BgraImage? _latest;
    private int _screenW = 1, _screenH = 1;
    private int _cellW = 10, _cellH = 20; // Windows Terminal maps Sixel pixels onto a 10x20 virtual cell
    private int _imgCols = 1, _imgRows = 1;
    private int _imgW, _imgH, _guestW, _guestH; // frame size sent vs the guest's own screen size
    private int _frames;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    private double _fps;

    public static readonly (string Label, string Keys)[] Palette =
    {
        ("Win (Start)", "win"),
        ("Win+R (Run)", "win+r"),
        ("Win+E (Explorer)", "win+e"),
        ("Win+D (Desktop)", "win+d"),
        ("Alt+Tab", "alt+tab"),
        ("Ctrl+C (Copy)", "ctrl+c"),
        ("Ctrl+V (Paste)", "ctrl+v"),
        ("Alt+F4 (Close window)", "alt+f4"),
        ("Ctrl+Shift+Esc (Task Manager)", "ctrl+shift+esc"),
        ("Win+Space (Switch input)", "win+space"),
        ("Hankaku/Zenkaku (IME on/off)", "kanji"),
        ("Ctrl+Z (Undo)", "ctrl+z"),
    };

    public TerminalViewer(MachineConfig machine, TerminalCaps caps)
    {
        _machine = machine;
        _caps = caps;
        _protocol = caps.Protocol;
    }

    public ViewerExit Exit { get; private set; } = ViewerExit.Quit;

    /// <summary>The command bar (F12): the menu's actions without leaving the screen.</summary>
    private static string CommandBar => Ui.T(
        " 1:閉じる  2:スナップショット  3:VM を止める  4:Windows キー  5:VM の解像度をこの枠に合わせる（くっきり）  6:元の解像度  その他:取り消し ",
        " 1:close  2:snapshot  3:stop the VM  4:Windows keys  5:fit the VM's resolution to this view (sharp)  6:original resolution  other:cancel ");

    public async Task RunAsync()
    {
        using var console = new ConsoleModeScope();
        var stdout = Console.OpenStandardOutput();
        // One background pump owns stdin; console reads cannot be cancelled, so nobody else may read it.
        var stdin = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var pump = new Thread(() =>
        {
            using var s = Console.OpenStandardInput();
            var b = new byte[4096];
            try
            {
                int n;
                while ((n = s.Read(b, 0, b.Length)) > 0) stdin.Writer.TryWrite(b[..n]);
            }
            catch { }
            stdin.Writer.TryComplete();
        }) { IsBackground = true, Name = "stdin" };
        pump.Start();
        void Write(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            lock (stdout) { stdout.Write(bytes); stdout.Flush(); }
        }

        // Alternate screen, hide cursor, mouse tracking (button-event + SGR).
        Write("\u001b[?1049h\u001b[?25l\u001b[2J\u001b[?1000h\u001b[?1002h\u001b[?1006h");
        var parser = new VtInputParser();
        using var cts = new CancellationTokenSource();
        try
        {
            await DetectCellSizeAsync(Write, stdin, parser);
            if (_protocol == GraphicsProtocol.None) _protocol = GraphicsProtocol.Blocks;
            if (_protocol == GraphicsProtocol.Blocks) (_cellW, _cellH) = (1, 2);

            await using var control = await GuestClient.ConnectAsync(_machine);
            await using var stream = await GuestClient.ConnectAsync(_machine);
            var frameSignal = new SemaphoreSlim(0, 1);
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
                    if (frameSignal.CurrentCount == 0) frameSignal.Release();
                }
                catch { /* corrupt frame: skip */ }
            };

            var (cols, rows) = (Console.WindowWidth, Console.WindowHeight);
            await StartStreamAsync(stream, cols, rows);

            var render = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await frameSignal.WaitAsync(cts.Token);
                    BgraImage? img;
                    lock (_frameLock) img = _latest;
                    if (img is null) continue;
                    var payload = _protocol switch
                    {
                        GraphicsProtocol.Kitty => "\u001b[2;1H" + KittyEncoder.Encode(img, _imgCols, _imgRows),
                        GraphicsProtocol.Blocks => _blocks.Encode(img),
                        _ => "\u001b[2;1H" + _sixel.Encode(img),
                    };
                    if (payload.Length > 0) Write(payload);
                    _frames++;
                    if (_fpsClock.ElapsedMilliseconds > 1000)
                    {
                        _fps = _frames * 1000.0 / _fpsClock.ElapsedMilliseconds;
                        _frames = 0;
                        _fpsClock.Restart();
                        DrawStatus(Write, null);
                    }
                }
            }, cts.Token);

            DrawStatus(Write, null);
            (int Col, int Row, bool Moved)? press = null;
            string? heldButton = null;
            (int X, int Y) lastGuest = (0, 0);
            bool paletteOpen = false, barOpen = false;
            Task? snapshotJob = null;
            while (true)
            {
                if (Console.WindowWidth != cols || Console.WindowHeight != rows)
                {
                    (cols, rows) = (Console.WindowWidth, Console.WindowHeight);
                    Write("\u001b[2J");
                    _blocks.Reset();
                    await StartStreamAsync(stream, cols, rows);
                }

                byte[] chunk;
                using (var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
                {
                    try
                    {
                        if (!await stdin.Reader.WaitToReadAsync(wait.Token)) break; // stdin closed
                    }
                    catch (OperationCanceledException)
                    {
                        continue; // poll for window resize
                    }
                }
                if (!stdin.Reader.TryRead(out chunk!)) continue;
                foreach (var ev in parser.Feed(chunk))
                {
                    if (barOpen)
                    {
                        barOpen = false;
                        DrawStatus(Write, null);
                        switch (ev)
                        {
                            case ExitRequest:
                                Exit = ViewerExit.Quit;
                                return;
                            case TextInput { Text: "1" or "q" or "m" }:
                                Exit = ViewerExit.Menu;
                                return;
                            case TextInput { Text: "2" or "s" }:
                                if (snapshotJob is { IsCompleted: false }) { DrawStatus(Write, Ui.T(" スナップショットはまだ作成中です… ", " A snapshot is still being taken… ")); break; }
                                DrawStatus(Write, Ui.T(" スナップショットを撮っています（1〜3 分。そのまま操作できます）… ", " Taking a snapshot (1-3 min; you can keep working)… "));
                                snapshotJob = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var label = await new MachineManager(log: _ => { }).SnapshotAsync(_machine, null);
                                        DrawStatus(Write, Ui.T($" スナップショット「{label}」を保存しました。戻すとき: terminal-windows restore {label} ", $" Snapshot '{label}' saved. Restore with: terminal-windows restore {label} "));
                                    }
                                    catch (Exception ex) { DrawStatus(Write, Ui.T(" スナップショットに失敗: ", " Snapshot failed: ") + ex.Message); }
                                });
                                break;
                            case TextInput { Text: "3" }:
                                Exit = ViewerExit.StopVm;
                                return;
                            case TextInput { Text: "5" or "6" } fit:
                            {
                                // 5: the guest renders at the size shown here, so nothing is shrunk and text is sharp.
                                // 6: back to the machine's own resolution.
                                if (fit.Text == "5" && _machine.Scale >= 200)
                                {
                                    DrawStatus(Write, Ui.T(" HiDPI 表示（2 倍）なので、解像度は合わせなくてもくっきりします。もっと大きく見るには窓を最大化してください ",
                                                           " The VM uses a HiDPI (2x) display, already sharp. Maximise the window to see it larger "));
                                    break;
                                }
                                var (fw, fh) = fit.Text == "5" ? FitTarget(cols, rows) : Commands.Display.Target(_machine, _machine.Scale >= 200);
                                try
                                {
                                    int shownW = _imgW, beforeW = _guestW, beforeH = _guestH;
                                    var r = await control.CallAsync("set_resolution", new JsonObject { ["width"] = fw, ["height"] = fh }, TimeSpan.FromSeconds(30));
                                    // The display driver only has fixed sizes: a mode smaller than what was already shown
                                    // would be blurrier, so go back and say how to get more room instead.
                                    if (fit.Text == "5" && r["width"]?.GetValue<int>() is int got && got < shownW * 0.98)
                                    {
                                        await control.CallAsync("set_resolution", new JsonObject { ["width"] = beforeW, ["height"] = beforeH }, TimeSpan.FromSeconds(30));
                                        DrawStatus(Write, Ui.T(" この大きさでは VM の解像度を合わせてもくっきりしません。窓を最大化するか Ctrl+- で文字を小さくしてから、もう一度 5 を押してください ",
                                                               " This view is too small for a sharper VM resolution. Maximise the window or press Ctrl+- (smaller text), then press 5 again "));
                                        break;
                                    }
                                    await Task.Delay(800);
                                    Write("[2J");
                                    _blocks.Reset();
                                    await StartStreamAsync(stream, cols, rows);
                                    DrawStatus(Write, Ui.T($" VM の解像度: {r["width"]}x{r["height"]} ", $" VM resolution: {r["width"]}x{r["height"]} "));
                                }
                                catch (Exception ex) when (ex is RpcException or TimeoutException or IOException)
                                {
                                    DrawStatus(Write, Ui.T(" 解像度を変えられませんでした: ", " Could not change the resolution: ") + ex.Message);
                                }
                                break;
                            }
                            case TextInput { Text: "4" }:
                                paletteOpen = true;
                                DrawStatus(Write, string.Join("  ", Palette.Select((p, i) => $"{PaletteKey(i)}:{p.Label}")) + Ui.T("  （他のキー: 取り消し）", "  (other key: cancel)"));
                                break;
                        }
                        continue;
                    }
                    if (paletteOpen)
                    {
                        paletteOpen = false;
                        DrawStatus(Write, null);
                        int choice = ev is TextInput t && t.Text.Length == 1 ? PaletteIndex(t.Text[0]) : -1;
                        if (choice >= 0 && choice < Palette.Length)
                            await Send(control, "key", new JsonObject { ["keys"] = Palette[choice].Keys }, Write);
                        continue;
                    }

                    switch (ev)
                    {
                        case ExitRequest:
                            // Ctrl+]: close the viewer; the VM keeps running (back to the menu when opened from it).
                            Exit = ViewerExit.Menu;
                            return;
                        case KeyInput { Chord: "f12" }:
                            barOpen = true;
                            DrawStatus(Write, CommandBar);
                            break;
                        case PaletteRequest:
                            paletteOpen = true;
                            DrawStatus(Write, string.Join("  ", Palette.Select((p, i) => $"{PaletteKey(i)}:{p.Label}")) + "  (other key: cancel)");
                            break;
                        case TextInput text:
                            await Send(control, "type", new JsonObject { ["text"] = text.Text }, Write);
                            break;
                        case KeyInput key:
                            await Send(control, "key", new JsonObject { ["keys"] = key.Chord }, Write);
                            break;
                        case MouseInput m when m.Wheel != 0 || m.WheelX != 0:
                            if (ToGuest(m.Col, m.Row) is { } wp)
                                await Send(control, "scroll", new JsonObject { ["x"] = wp.X, ["y"] = wp.Y, ["dy"] = m.Wheel * 3, ["dx"] = m.WheelX * 3 }, Write);
                            break;
                        // Real-time mode: press / move / release are forwarded as they happen, so Windows
                        // itself detects double/triple clicks and drags are visible while in progress.
                        case MouseInput m when _realtimeMouse && m.Pressed && !m.Motion:
                            if (ToGuest(m.Col, m.Row) is { } down)
                            {
                                heldButton = ButtonName(m.Button);
                                lastGuest = down;
                                if (!await SendMouse(control, "down", down, heldButton, Write)) press = (m.Col, m.Row, false);
                            }
                            break;
                        case MouseInput m when _realtimeMouse && m.Motion && heldButton is not null:
                            if (ToGuest(m.Col, m.Row) is { } mv && mv != lastGuest)
                            {
                                lastGuest = mv;
                                await SendMouse(control, "move", mv, heldButton, Write);
                            }
                            break;
                        case MouseInput m when _realtimeMouse && !m.Pressed && heldButton is not null:
                        {
                            // Releasing outside the image still releases, at the last position inside it.
                            var up = ToGuest(m.Col, m.Row) ?? lastGuest;
                            await SendMouse(control, "up", up, heldButton, Write);
                            heldButton = null;
                            break;
                        }

                        // Legacy mode (helpers older than the 'mouse' op): one click or drag on release.
                        case MouseInput m when m.Pressed && !m.Motion:
                            press = (m.Col, m.Row, false);
                            break;
                        case MouseInput m when m.Motion && press is { } p0:
                            if (m.Col != p0.Col || m.Row != p0.Row) press = (p0.Col, p0.Row, true);
                            break;
                        case MouseInput m when !m.Pressed:
                            if (press is { } p && ToGuest(m.Col, m.Row) is { } end)
                            {
                                var button = ButtonName(m.Button);
                                if (p.Moved && ToGuest(p.Col, p.Row) is { } start)
                                    await Send(control, "drag", new JsonObject { ["x1"] = start.X, ["y1"] = start.Y, ["x2"] = end.X, ["y2"] = end.Y, ["button"] = button }, Write);
                                else
                                    await Send(control, "click", new JsonObject { ["x"] = end.X, ["y"] = end.Y, ["button"] = button }, Write);
                            }
                            press = null;
                            break;
                    }
                }
            }
        }
        finally
        {
            cts.Cancel();
            Write("\u001b[?1006l\u001b[?1002l\u001b[?1000l\u001b[?25h\u001b[?1049l");
        }
    }

    private static char PaletteKey(int i) => i < 9 ? (char)('1' + i) : (char)('a' + i - 9);
    private static int PaletteIndex(char c) => c is >= '1' and <= '9' ? c - '1' : c is >= 'a' and <= 'z' ? 9 + c - 'a' : -1;

    private async Task StartStreamAsync(GuestClient stream, int cols, int rows)
    {
        int availCols = Math.Max(10, cols);
        // First row: status line. The last row stays empty: a Sixel image that reaches it makes the terminal scroll.
        int availRows = Math.Max(4, rows - 2);
        int maxW = availCols * _cellW, maxH = availRows * _cellH;
        var status = await stream.CallAsync("status");
        int sw = status["screen"]?["width"]?.GetValue<int>() ?? _machine.Width;
        int sh = status["screen"]?["height"]?.GetValue<int>() ?? _machine.Height;
        var (w, h) = Imaging.Fit(sw, sh, maxW, maxH);
        (_guestW, _guestH) = (sw, sh);
        _imgCols = Math.Max(1, (int)Math.Ceiling(w / (double)_cellW));
        _imgRows = Math.Max(1, (int)Math.Ceiling(h / (double)_cellH));
        if (_protocol == GraphicsProtocol.Blocks) h -= h % 2; // whole cells: two pixel rows each
        if (_protocol == GraphicsProtocol.Sixel)
        {
            // Snap to whole cells so a cell maps to a fixed block of image pixels.
            w = Math.Min(w, _imgCols * _cellW);
            h = Math.Min(h, _imgRows * _cellH);
        }
        // Blocks redraw every changed cell as text, so a lower frame rate keeps the terminal responsive.
        int fps = _protocol == GraphicsProtocol.Blocks ? 12 : 30;
        (_imgW, _imgH) = (w, h);
        await stream.CallAsync("stream_start", new JsonObject { ["width"] = w, ["height"] = h, ["fps"] = fps });
    }

    /// <summary>The largest guest resolution that this view shows without shrinking (no smaller than 1024x600).</summary>
    private (int W, int H) FitTarget(int cols, int rows)
    {
        int w = Math.Max(10, cols) * _cellW, h = Math.Max(4, rows - 2) * _cellH;
        return (Math.Max(1024, w - w % 8), Math.Max(600, h - h % 2));
    }

    /// <summary>Maps a 1-based terminal cell to guest screen pixels (cell centre).</summary>
    private (int X, int Y)? ToGuest(int col, int row)
    {
        row -= 1; // row 1 is the status line
        if (col < 1 || row < 1 || col > _imgCols || row > _imgRows) return null;
        int sw, sh;
        lock (_frameLock) (sw, sh) = (_screenW, _screenH);
        int x = (int)((col - 0.5) / _imgCols * sw);
        int y = (int)((row - 0.5) / _imgRows * sh);
        return (Math.Clamp(x, 0, sw - 1), Math.Clamp(y, 0, sh - 1));
    }

    private bool _realtimeMouse = true;

    private static string ButtonName(int b) => b switch { 1 => "middle", 2 => "right", _ => "left" };

    /// <summary>Sends a real-time mouse event. Returns false (and switches to legacy click/drag mode)
    /// if the guest helper is too old to know the 'mouse' op.</summary>
    private async Task<bool> SendMouse(GuestClient c, string action, (int X, int Y) at, string button, Action<string> write)
    {
        try
        {
            await c.CallAsync("mouse", new JsonObject { ["action"] = action, ["x"] = at.X, ["y"] = at.Y, ["button"] = button }, TimeSpan.FromSeconds(15));
            return true;
        }
        catch (RpcException ex) when (ex.Message.Contains("Unknown op"))
        {
            _realtimeMouse = false;
            DrawStatus(write, "guest helper is older: using click-on-release mode (run 'terminal-windows update-helper')");
            return false;
        }
        catch (Exception ex)
        {
            DrawStatus(write, "error: " + ex.Message);
            return true;
        }
    }

    private async Task Send(GuestClient c, string op, JsonObject args, Action<string> write)
    {
        try
        {
            await c.CallAsync(op, args, TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            DrawStatus(write, "error: " + ex.Message);
        }
    }

    private void DrawStatus(Action<string> write, string? message)
    {
        int cols = Console.WindowWidth;
        var mode = _protocol == GraphicsProtocol.Blocks ? Ui.T(" | 簡易表示（Windows Terminal ならフル解像度）", " | low-res text mode (Windows Terminal shows full resolution)") : "";
        // Shrunk noticeably: say how to get a sharp picture.
        int layoutW = _guestW * 100 / Math.Max(100, _machine.Scale); // HiDPI: compare with the layout size, not the pixels
        if (_protocol != GraphicsProtocol.Blocks && layoutW > 0 && _imgW < layoutW * 0.85)
            mode += Ui.T($" | 縮小 {_imgW * 100 / layoutW}%: 最大化か Ctrl+- でくっきり", $" | shrunk to {_imgW * 100 / layoutW}%: maximise or Ctrl+- for sharp");
        var text = message ?? Ui.T(
            $" {_machine.Name} | Ctrl+]: 閉じる | F12: メニュー | Ctrl+G: Windows キー | {_fps:0} fps{mode} ",
            $" {_machine.Name} | Ctrl+]: close | F12: menu | Ctrl+G: Windows keys | {_fps:0} fps{mode} ");
        write($"\u001b7\u001b[1;1H\u001b[7m{Ui.FitWidth(text, cols)}\u001b[0m\u001b8");
    }

    private async Task DetectCellSizeAsync(Action<string> write, System.Threading.Channels.Channel<byte[]> stdin, VtInputParser parser)
    {
        if (Environment.GetEnvironmentVariable("TERMINAL_WINDOWS_CELL") is { } forced && forced.Split('x') is [var fw, var fh]
            && int.TryParse(fw, out var cw) && int.TryParse(fh, out var ch))
        {
            (_cellW, _cellH) = (cw, ch);
            return;
        }
        // CSI 16 t -> CSI 6 ; height ; width t. Followed by DA1 so we always get an answer to wait for.
        write("\u001b[16t\u001b[c");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        try
        {
            while (true)
            {
                var data = await stdin.Reader.ReadAsync(cts.Token);
                foreach (var ev in parser.Feed(data, flushLoneEscape: false))
                {
                    if (ev is TerminalReport { Final: 't' } r && r.Params.Split(';') is ["6", var h, var w]
                        && int.TryParse(h, out var ph) && int.TryParse(w, out var pw) && ph > 0 && pw > 0)
                    {
                        // Windows Terminal scales Sixel images as if cells were 10x20; keep that unless kitty is used.
                        if (_caps.Protocol == GraphicsProtocol.Kitty) (_cellW, _cellH) = (pw, ph);
                    }
                    if (ev is TerminalReport { Final: 'c' } da)
                    {
                        // Primary device attributes: attribute 4 means the terminal draws Sixel graphics.
                        if (_protocol == GraphicsProtocol.None && da.Params.TrimStart('?').Split(';').Contains("4"))
                            _protocol = GraphicsProtocol.Sixel;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
