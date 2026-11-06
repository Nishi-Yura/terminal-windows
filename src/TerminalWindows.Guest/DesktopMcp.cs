using System.Text.Json.Nodes;
using TerminalWindows.Guest.Capture;
using TerminalWindows.Protocol;

namespace TerminalWindows.Guest;

/// <summary>
/// <c>TerminalWindowsGuest.exe mcp</c>: a local stdio MCP server for an AI agent that runs INSIDE the
/// VM (e.g. Claude Code). It drives this machine's own desktop directly — no network. Coordinates
/// use the same downscaled screenshot space as the host MCP server.
/// Snapshots, restores and handing files to the person are done from the host, not from here.
/// </summary>
public sealed class DesktopMcp
{
    private readonly CaptureManager _capture = new();

    private const string Instructions =
        "You are running INSIDE a disposable Windows VM dedicated to AI work; these tools drive this VM's own desktop. " +
        "Loop: screenshot -> act -> screenshot to verify. Coordinates are pixels of the latest screenshot image. " +
        "Prefer the element tools (click_element, set_text, wait_for, read_text) over pixel clicks when a control has a name. Prefer shell commands for file and system work; use the GUI tools for apps. " +
        "Put deliverables in %USERPROFILE%\\Desktop\\Outbox. Files from the person arrive in %USERPROFILE%\\Desktop\\Inbox. " +
        "Snapshots/restores are done by the person on the host: ask them before destructive changes. " +
        "If status reports a lock/logon screen, stop and tell the person.";

    public Task RunAsync() => McpStdio.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
        "terminal-windows-desktop", Instructions, Tools, CallAsync);

    private ScreenSpace Space()
    {
        var (_, _, w, h) = InputInjector.VirtualScreen();
        return new ScreenSpace(Math.Max(1, w), Math.Max(1, h));
    }

    private static int I(JsonObject a, string k) => a[k]?.GetValue<int>() ?? throw new ArgumentException($"Missing '{k}'");
    private static int OptI(JsonObject a, string k, int def) => a[k]?.GetValue<int>() ?? def;
    private static string S(JsonObject a, string k) => a[k]?.GetValue<string>() ?? throw new ArgumentException($"Missing '{k}'");

    private static void RequireDesktop()
    {
        if (SessionState.Describe() != "desktop")
            throw new InvalidOperationException("The VM is on the lock/logon screen or a secure desktop. Do not try to unlock it; tell the person.");
    }

    public async Task<JsonObject> CallAsync(string name, JsonObject a)
    {
        try
        {
            var s = Space();
            switch (name)
            {
                case "screenshot":
                {
                    var img = _capture.Capture();
                    var small = Imaging.Resize(img, s.ImageWidth, s.ImageHeight);
                    return McpResult.Image(Imaging.EncodePng(small),
                        $"Screenshot {small.Width}x{small.Height} px (screen {img.Width}x{img.Height}). Use these pixel coordinates.");
                }
                case "click":
                    RequireDesktop();
                    await InputInjector.ClickAsync(s.ToScreenX(I(a, "x")), s.ToScreenY(I(a, "y")),
                        a["button"]?.GetValue<string>() ?? "left", Math.Clamp(OptI(a, "count", 1), 1, 3));
                    return McpResult.Text("ok");
                case "drag":
                    RequireDesktop();
                    await InputInjector.DragAsync(s.ToScreenX(I(a, "start_x")), s.ToScreenY(I(a, "start_y")),
                        s.ToScreenX(I(a, "end_x")), s.ToScreenY(I(a, "end_y")), a["button"]?.GetValue<string>() ?? "left");
                    return McpResult.Text("ok");
                case "scroll":
                    RequireDesktop();
                    InputInjector.Scroll(a["x"] is null ? null : s.ToScreenX(I(a, "x")), a["y"] is null ? null : s.ToScreenY(I(a, "y")),
                        OptI(a, "dx", 0), OptI(a, "dy", 0));
                    return McpResult.Text("ok");
                case "type":
                    RequireDesktop();
                    await InputInjector.TypeAsync(S(a, "text"));
                    return McpResult.Text("ok");
                case "key":
                    RequireDesktop();
                    await InputInjector.KeyAsync(S(a, "keys"));
                    return McpResult.Text("ok");
                case "ui_tree":
                {
                    var tree = await UiaTree.GetAsync(a["window"]?.GetValue<string>(), OptI(a, "depth", 6), OptI(a, "max_nodes", 1500), TimeSpan.FromSeconds(20));
                    s.ScaleRects(tree);
                    tree["coordinates"] = "screenshot pixels; rect = [x, y, width, height]";
                    return McpResult.Json(tree);
                }
                case "wait":
                {
                    int ms = Math.Clamp(OptI(a, "ms", 1000), 0, 120_000);
                    if (a["until_change"]?.GetValue<bool>() == true)
                    {
                        var first = Imaging.Fingerprint(Imaging.Resize(_capture.Capture(), 320, 180));
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < ms)
                        {
                            await Task.Delay(300);
                            if (Imaging.Fingerprint(Imaging.Resize(_capture.Capture(), 320, 180)) != first)
                                return McpResult.Text($"screen changed after {sw.ElapsedMilliseconds} ms");
                        }
                        return McpResult.Text($"no change within {ms} ms");
                    }
                    await Task.Delay(ms);
                    return McpResult.Text($"waited {ms} ms");
                }
                case "status":
                {
                    var fg = Native.GetForegroundWindow();
                    return McpResult.Json(new JsonObject
                    {
                        ["session"] = SessionState.Describe(),
                        ["screen"] = new JsonObject { ["width"] = s.ScreenWidth, ["height"] = s.ScreenHeight, ["screenshotWidth"] = s.ImageWidth, ["screenshotHeight"] = s.ImageHeight },
                        ["foregroundWindow"] = fg == IntPtr.Zero ? null : Native.WindowTitle(fg),
                        ["imeOpen"] = InputInjector.ImeOpen(),
                        ["captureBackend"] = _capture.Backend,
                    });
                }
                case "find_element":
                    return Scaled(await UiaActions.FindAsync(a), s);
                case "click_element":
                    RequireDesktop();
                    return Scaled(await UiaActions.ActAsync(a), s);
                case "set_text":
                {
                    RequireDesktop();
                    var o = (JsonObject)a.DeepClone();
                    o["action"] = "set_value";
                    return Scaled(await UiaActions.ActAsync(o), s);
                }
                case "read_text":
                    return McpResult.Json(await UiaActions.TextAsync(a["window"]?.GetValue<string>(), OptI(a, "max_chars", 20000)));
                case "list_windows":
                    return Scaled(await UiaActions.WindowsAsync(), s);
                case "focus_window":
                    RequireDesktop();
                    return McpResult.Json(await UiaActions.FocusWindowAsync(S(a, "window")));
                case "close_window":
                    RequireDesktop();
                    return McpResult.Json(await UiaActions.CloseWindowAsync(S(a, "window"), a["force"]?.GetValue<bool>() == true));
                case "clipboard_get":
                    return McpResult.Json(await UiaActions.ClipboardGetAsync());
                case "clipboard_set":
                    return McpResult.Json(await UiaActions.ClipboardSetAsync(S(a, "text")));
                case "open":
                    RequireDesktop();
                    return McpResult.Json(await UiaActions.OpenAsync(S(a, "target"), a["args"]?.GetValue<string>()));
                case "wait_for":
                {
                    bool gone = a["gone"]?.GetValue<bool>() == true;
                    int timeout = Math.Clamp(OptI(a, "timeout_ms", 30_000), 0, 300_000);
                    var q = (JsonObject)a.DeepClone();
                    q.Remove("gone");
                    q.Remove("timeout_ms");
                    q["max"] = 1;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        JsonObject r;
                        try { r = await UiaActions.FindAsync((JsonObject)q.DeepClone()); }
                        catch (ArgumentException ex) when (ex.Message.StartsWith("No window title contains")) { r = new JsonObject { ["count"] = 0 }; }
                        bool present = r["count"]?.GetValue<int>() > 0;
                        if (present != gone)
                        {
                            r["waitedMs"] = sw.ElapsedMilliseconds;
                            return gone ? McpResult.Text($"gone after {sw.ElapsedMilliseconds} ms") : Scaled(r, s);
                        }
                        if (sw.ElapsedMilliseconds >= timeout)
                            return McpResult.Text(gone ? $"still there after {timeout} ms" : $"not found within {timeout} ms (take a screenshot to see what is on screen)", isError: true);
                        await Task.Delay(500);
                    }
                }
                default:
                    throw new ArgumentException($"Unknown tool '{name}'");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException or FormatException)
        {
            return McpResult.Text($"{name} failed: {ex.Message}", isError: true);
        }
    }

    private static JsonObject Scaled(JsonObject r, ScreenSpace s)
    {
        s.ScaleRects(r);
        r["coordinates"] = "screenshot pixels";
        return r is { } ? McpResult.Json(r) : McpResult.Text("ok");
    }

    private static JsonObject Int(string d) => new() { ["type"] = "integer", ["description"] = d };
    private static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
    private static JsonObject Button() => new() { ["type"] = "string", ["enum"] = new JsonArray("left", "right", "middle"), ["default"] = "left" };

    public static JsonArray Tools()
    {
        var list = BaseTools();
        foreach (var t in DesktopTools.Definitions()) list.Add(t);
        return list;
    }

    private static JsonArray BaseTools() => new(
        McpResult.Tool("screenshot", "Capture this VM's screen as PNG. All other tools use this image's pixel coordinates.", new JsonObject()),
        McpResult.Tool("click", "Click at (x, y) in screenshot pixels. count 2 = double click, 3 = triple click.", new JsonObject
        {
            ["x"] = Int("X"), ["y"] = Int("Y"), ["button"] = Button(),
            ["count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3, ["default"] = 1 },
        }, "x", "y"),
        McpResult.Tool("drag", "Press at the start point, move, release at the end point (screenshot pixels).", new JsonObject
        {
            ["start_x"] = Int("start X"), ["start_y"] = Int("start Y"), ["end_x"] = Int("end X"), ["end_y"] = Int("end Y"), ["button"] = Button(),
        }, "start_x", "start_y", "end_x", "end_y"),
        McpResult.Tool("scroll", "Mouse wheel. dy > 0 scrolls down, dx > 0 right (notches). Optional x/y moves the pointer first.", new JsonObject
        {
            ["dx"] = Int("horizontal notches"), ["dy"] = Int("vertical notches"), ["x"] = Int("pointer X"), ["y"] = Int("pointer Y"),
        }),
        McpResult.Tool("type", "Type text into the focused control (Unicode, Japanese OK without IME). \\n presses Enter.", new JsonObject { ["text"] = Str("text") }, "text"),
        McpResult.Tool("key", "Press chords like 'ctrl+s', 'win+r', 'alt+f4', 'enter'. Space-separate several.", new JsonObject { ["keys"] = Str("chord(s)") }, "keys"),
        McpResult.Tool("ui_tree", "UI Automation tree of the foreground window (or `window` title / 'desktop'): name, controlType, rect in screenshot pixels.", new JsonObject
        {
            ["window"] = Str("title substring, hwnd, or 'desktop'"), ["depth"] = Int("max depth (default 6)"), ["max_nodes"] = Int("max nodes (default 1500)"),
        }),
        McpResult.Tool("wait", "Wait `ms` milliseconds, or with until_change=true until the screen changes (max `ms`).", new JsonObject
        {
            ["ms"] = Int("milliseconds (max 120000)"), ["until_change"] = new JsonObject { ["type"] = "boolean" },
        }),
        McpResult.Tool("status", "Desktop state (locked or not), screen size, foreground window, IME state.", new JsonObject()));
}
