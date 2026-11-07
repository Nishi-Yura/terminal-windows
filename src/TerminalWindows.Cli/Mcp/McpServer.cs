using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerminalWindows.Cli.Backend;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Mcp;

/// <summary>stdio MCP server (JSON-RPC 2.0, one message per line). stdout carries protocol messages only.</summary>
public sealed class McpServer
{
    private readonly string _machineName;
    private readonly MachineManager _manager;
    private readonly bool _syncOutbox;
    private GuestClient? _client;
    private ScreenSpace? _space;
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>This server started the VM (it was stopped/saved), so it may also put it back to rest.</summary>
    private bool _startedByUs;
    private DateTime _lastCall = DateTime.UtcNow;

    public McpServer(string machineName, bool syncOutbox = false)
    {
        _machineName = machineName;
        _syncOutbox = syncOutbox;
        _manager = new MachineManager(log: s => Console.Error.WriteLine(s));
    }

    private static readonly string Instructions =
        "Controls an AI-dedicated Windows VM (VirtualBox or VMware Workstation). Actions affect only the VM, never the host. " +
        "Start every session with read_notes. Loop: screenshot -> act -> screenshot to verify. " +
        "Coordinates are pixels of the latest screenshot image. Prefer the element tools (click_element, set_text, wait_for, read_text) " +
        "over pixel clicks when the control has a name; use run_powershell for files and settings. " +
        "A stopped VM starts automatically on the first action. Take a snapshot before destructive changes. Put deliverables in Desktop\\Outbox and call export_file. " +
        "If status reports a lock/logon screen, stop and tell the user. Record progress with append_notes. " +
        "A VM this server started is saved after a while without tool calls (frees this PC's memory) and resumes on the next action.";

    public async Task RunAsync()
    {
        if (_syncOutbox) _ = Task.Run(() => Commands.Transfer.SyncOutboxLoopAsync(MachineStore.Load(_machineName), CancellationToken.None));
        if (IdleMinutes() > 0) _ = Task.Run(IdleLoopAsync);
        await McpStdio.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), "terminal-windows", Instructions,
            McpTools.Definitions, (name, args) => CallToolAsync(name, args));
    }

    private static JsonObject Text(string text, bool isError = false) => McpResult.Text(text, isError);
    private static JsonObject Json(JsonNode node) => McpResult.Json(node);

    private async Task<GuestClient> ClientAsync()
    {
        if (_client is not null) return _client;
        var m = MachineStore.Load(_machineName);
        // A stopped / saved / paused VM is started on the first action (takes ~10-60 s); progress goes to stderr.
        if (await _manager.Backend.GetStateAsync(m.VmName) != VmState.Running)
        {
            await _manager.EnsureRunningAsync(m);
            _startedByUs = true;
        }
        _client = await GuestClient.ConnectAsync(m);
        _client.Rpc.Disconnected += _ => _client = null;
        return _client;
    }

    private async Task ResetClientAsync()
    {
        var c = _client;
        _client = null;
        _space = null;
        if (c is not null) await c.DisposeAsync();
    }

    private async Task<ScreenSpace> SpaceAsync(GuestClient c)
    {
        if (_space is { } s) return s;
        var st = await c.CallAsync("status");
        var space = new ScreenSpace(st["screen"]!["width"]!.GetValue<int>(), st["screen"]!["height"]!.GetValue<int>());
        _space = space;
        return space;
    }

    private async Task<JsonObject> CallToolAsync(string name, JsonObject args)
    {
        await _gate.WaitAsync();
        try
        {
            _lastCall = DateTime.UtcNow;
            return await DispatchAsync(name, args);
        }
        catch (Exception ex) when (ex is RpcException or CliException or ArgumentException or InvalidOperationException or IOException or TimeoutException or FormatException)
        {
            if (ex is IOException or TimeoutException or RpcException) await ResetClientAsync();
            return Text($"{name} failed: {ex.Message}", isError: true);
        }
        finally
        {
            _lastCall = DateTime.UtcNow;
            _gate.Release();
        }
    }

    /// <summary>TERMINAL_WINDOWS_IDLE_MINUTES (default 15; 0 = never).</summary>
    internal static int IdleMinutes() =>
        int.TryParse(Environment.GetEnvironmentVariable("TERMINAL_WINDOWS_IDLE_MINUTES"), out var v) ? Math.Max(0, v) : 15;

    /// <summary>
    /// A VM that this server started is saved (state kept, RAM given back to this PC) after IdleMinutes
    /// without tool calls, unless something in it is still busy (an AI working inside the VM, an install).
    /// A VM the person started themselves is never touched.
    /// </summary>
    private async Task IdleLoopAsync()
    {
        var idle = TimeSpan.FromMinutes(IdleMinutes());
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            if (!_startedByUs || DateTime.UtcNow - _lastCall < idle) continue;
            if (!await _gate.WaitAsync(0)) continue;
            try
            {
                if (!_startedByUs || DateTime.UtcNow - _lastCall < idle) continue;
                var m = MachineStore.Load(_machineName);
                if (await _manager.Backend.GetStateAsync(m.VmName) != VmState.Running) { _startedByUs = false; continue; }
                if (_client is { } c)
                {
                    var r = await c.CallAsync("exec", new JsonObject
                    {
                        ["script"] = "(Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average",
                        ["timeout_s"] = 30,
                    }, TimeSpan.FromSeconds(45));
                    if (double.TryParse((r["stdout"]?.GetValue<string>() ?? "").Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var load) && load > 20)
                    {
                        _lastCall = DateTime.UtcNow; // busy inside: look again after another idle period
                        continue;
                    }
                }
                Console.Error.WriteLine($"terminal-windows: no tool calls for {idle.TotalMinutes:0} min; saving {m.Name} (it resumes on the next action).");
                await ResetClientAsync();
                await _manager.StopAsync(m, "savestate", false);
                _startedByUs = false;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("terminal-windows: idle save skipped: " + ex.Message);
                _lastCall = DateTime.UtcNow;
            }
            finally { _gate.Release(); }
        }
    }

    private static int I(JsonObject a, string k) => a[k]?.GetValue<int>() ?? throw new ArgumentException($"Missing '{k}'");
    private static int OptI(JsonObject a, string k, int def) => a[k]?.GetValue<int>() ?? def;
    private static string S(JsonObject a, string k) => a[k]?.GetValue<string>() ?? throw new ArgumentException($"Missing '{k}'");

    private async Task<JsonObject> DispatchAsync(string name, JsonObject a)
    {
        switch (name)
        {
            case "screenshot":
            {
                var c = await ClientAsync();
                _space = null; // the resolution may have changed
                var space = await SpaceAsync(c);
                var (info, png) = await c.ScreenshotAsync(space.ImageWidth, space.ImageHeight);
                return new JsonObject
                {
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"Screenshot {info["width"]}x{info["height"]} px. Use these pixel coordinates for click/drag/scroll " +
                                       $"(guest screen is {space.ScreenWidth}x{space.ScreenHeight}, scale {space.Scale:0.###}).",
                        }),
                };
            }

            case "click":
            {
                var c = await ClientAsync();
                var s = await SpaceAsync(c);
                await c.CallAsync("click", new JsonObject
                {
                    ["x"] = s.ToScreenX(I(a, "x")), ["y"] = s.ToScreenY(I(a, "y")),
                    ["button"] = a["button"]?.GetValue<string>() ?? "left",
                    ["count"] = Math.Clamp(OptI(a, "count", 1), 1, 3),
                });
                return Text("ok");
            }

            case "drag":
            {
                var c = await ClientAsync();
                var s = await SpaceAsync(c);
                await c.CallAsync("drag", new JsonObject
                {
                    ["x1"] = s.ToScreenX(I(a, "start_x")), ["y1"] = s.ToScreenY(I(a, "start_y")),
                    ["x2"] = s.ToScreenX(I(a, "end_x")), ["y2"] = s.ToScreenY(I(a, "end_y")),
                    ["button"] = a["button"]?.GetValue<string>() ?? "left",
                });
                return Text("ok");
            }

            case "scroll":
            {
                var c = await ClientAsync();
                var s = await SpaceAsync(c);
                var args = new JsonObject { ["dx"] = OptI(a, "dx", 0), ["dy"] = OptI(a, "dy", 0) };
                if (a["x"] is not null && a["y"] is not null)
                {
                    args["x"] = s.ToScreenX(I(a, "x"));
                    args["y"] = s.ToScreenY(I(a, "y"));
                }
                await c.CallAsync("scroll", args);
                return Text("ok");
            }

            case "type":
                await (await ClientAsync()).CallAsync("type", new JsonObject { ["text"] = S(a, "text") }, TimeSpan.FromMinutes(5));
                return Text("ok");

            case "key":
                KeyChord.ParseSequence(S(a, "keys")); // validate early for a clear error
                await (await ClientAsync()).CallAsync("key", new JsonObject { ["keys"] = S(a, "keys") });
                return Text("ok");

            case "ui_tree":
            {
                var c = await ClientAsync();
                var s = await SpaceAsync(c);
                var args = new JsonObject { ["depth"] = OptI(a, "depth", 6), ["max_nodes"] = OptI(a, "max_nodes", 1500) };
                if (a["window"] is not null) args["window"] = S(a, "window");
                var tree = await c.CallAsync("ui_tree", args, TimeSpan.FromSeconds(30));
                ScaleRects(tree, s);
                tree["coordinates"] = "screenshot pixels; rect = [x, y, width, height]";
                return Json(tree);
            }

            case "wait":
            {
                int ms = Math.Clamp(OptI(a, "ms", 1000), 0, 120_000);
                if (a["until_change"]?.GetValue<bool>() == true)
                {
                    var c = await ClientAsync();
                    var (_, first) = await c.ScreenshotAsync(320, 240);
                    var start = DateTime.UtcNow;
                    while ((DateTime.UtcNow - start).TotalMilliseconds < ms)
                    {
                        await Task.Delay(300);
                        var (_, now) = await c.ScreenshotAsync(320, 240);
                        if (!now.AsSpan().SequenceEqual(first)) return Text($"screen changed after {(DateTime.UtcNow - start).TotalMilliseconds:0} ms");
                    }
                    return Text($"no change within {ms} ms");
                }
                await Task.Delay(ms);
                return Text($"waited {ms} ms");
            }

            case "status":
            {
                var m = MachineStore.Load(_machineName);
                var state = await _manager.Backend.GetStateAsync(m.VmName);
                var o = new JsonObject { ["machine"] = m.Name, ["vmState"] = state.ToString(), ["setupComplete"] = m.SetupComplete };
                try
                {
                    if (state != VmState.Running) throw new CliException($"VM is {state}");
                    var c = await ClientAsync();
                    var st = await c.CallAsync("status");
                    o["helper"] = st;
                    o["note"] = st["session"]?.GetValue<string>() == "desktop" ? null
                        : "The guest is on a lock/logon/secure screen. Do not try to unlock it; tell the user.";
                }
                catch (Exception ex)
                {
                    await ResetClientAsync();
                    o["helper"] = "unreachable: " + ex.Message;
                }
                if (state != VmState.Running)
                    o["note"] = "The VM is not running. Any action tool (screenshot, click, ...) starts it automatically; status does not.";
                return Json(o);
            }

            case "snapshot":
            {
                var m = MachineStore.Load(_machineName);
                var label = await _manager.SnapshotAsync(m, a["label"]?.GetValue<string>());
                return Text($"snapshot '{label}' created");
            }

            case "restore":
            {
                var m = MachineStore.Load(_machineName);
                await ResetClientAsync();
                var safety = await _manager.RestoreAsync(m, S(a, "label"));
                return Text($"Restored '{S(a, "label")}'. The previous state was saved as '{safety}' (restore it to undo).");
            }

            case "export_file":
            {
                var m = MachineStore.Load(_machineName);
                var path = a["path"]?.GetValue<string>();
                var guestPath = string.IsNullOrEmpty(path) ? "outbox"
                    : Path.IsPathFullyQualified(path) ? path
                    : $@"C:\Users\{m.User}\Desktop\Outbox\{SafePath.NormalizeRelative(path).Replace('/', '\\')}";
                var dest = Path.Combine(MachineStore.OutboxDir(m.Name), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                var files = await (await ClientAsync()).PullAsync(guestPath, dest, TransferLimits.Default);
                return Json(new JsonObject
                {
                    ["hostFolder"] = dest,
                    ["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["host"] = f.HostPath, ["size"] = f.Size, ["sha256"] = f.Sha256 }).ToArray()),
                    ["tellTheUser"] = $"Files were copied to {dest}",
                });
            }

            case "list_inbox":
                return Json(await (await ClientAsync()).CallAsync("file_list", new JsonObject { ["root"] = "inbox" }));

            case "read_notes":
            {
                var path = MachineStore.NotesPath(_machineName);
                return Text(File.Exists(path) ? await File.ReadAllTextAsync(path) : "(no notes yet)");
            }

            case "append_notes":
            {
                var path = MachineStore.NotesPath(_machineName);
                await File.AppendAllTextAsync(path, Commands.Notes.FormatEntry(S(a, "text")));
                return Text("appended");
            }

            case "find_element":
            case "click_element":
            case "set_text":
            case "read_text":
            case "list_windows":
            case "focus_window":
            case "close_window":
            case "clipboard_get":
            case "clipboard_set":
            case "open":
            {
                var c = await ClientAsync();
                var (op, args, timeout) = name switch
                {
                    "find_element" => ("uia_find", a, 30),
                    "click_element" => ("uia_act", a, 30),
                    "set_text" => ("uia_act", WithAction(a, "set_value"), 120),
                    "read_text" => ("uia_text", a, 45),
                    "list_windows" => ("windows", a, 30),
                    "focus_window" => ("window_focus", a, 30),
                    "close_window" => ("window_close", a, 30),
                    "clipboard_get" => ("clipboard_get", a, 15),
                    "clipboard_set" => ("clipboard_set", a, 15),
                    _ => ("open", a, 30),
                };
                JsonObject r;
                try { r = await c.CallAsync(op, (JsonObject)args.DeepClone(), TimeSpan.FromSeconds(timeout)); }
                catch (RpcException ex) when (ex.Message.Contains("Unknown op"))
                {
                    throw new CliException("The helper in the VM is older than this tool. Run 'terminal-windows update-helper' on the host (or restart the VM from terminal-windows).");
                }
                ScaleRects(r, await SpaceAsync(c));
                if (name is "find_element" or "click_element" or "set_text" or "list_windows") r["coordinates"] = "screenshot pixels";
                return Json(r);
            }

            case "wait_for":
            {
                var c = await ClientAsync();
                bool gone = a["gone"]?.GetValue<bool>() == true;
                int timeout = Math.Clamp(OptI(a, "timeout_ms", 30_000), 0, 300_000);
                var query = (JsonObject)a.DeepClone();
                query.Remove("gone");
                query.Remove("timeout_ms");
                query["max"] = 1;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    JsonObject r;
                    try { r = await c.CallAsync("uia_find", (JsonObject)query.DeepClone(), TimeSpan.FromSeconds(30)); }
                    catch (RpcException ex) when (ex.Message.Contains("No window title contains") && !gone) { r = new JsonObject { ["count"] = 0 }; }
                    catch (RpcException ex) when (ex.Message.Contains("No window title contains") && gone) { return Text($"gone after {sw.ElapsedMilliseconds} ms (its window closed)"); }
                    bool present = r["count"]?.GetValue<int>() > 0;
                    if (present != gone)
                    {
                        ScaleRects(r, await SpaceAsync(c));
                        r["coordinates"] = "screenshot pixels";
                        r["waitedMs"] = sw.ElapsedMilliseconds;
                        return gone ? Text($"gone after {sw.ElapsedMilliseconds} ms") : Json(r);
                    }
                    if (sw.ElapsedMilliseconds >= timeout)
                        return Text(gone ? $"still there after {timeout} ms" : $"not found within {timeout} ms (take a screenshot to see what is on screen)", isError: true);
                    await Task.Delay(500);
                }
            }

            case "run_powershell":
            {
                var c = await ClientAsync();
                int t = Math.Clamp(OptI(a, "timeout_s", 120), 1, 1800);
                var r = await c.CallAsync("exec", new JsonObject { ["script"] = S(a, "script"), ["timeout_s"] = t }, TimeSpan.FromSeconds(t + 30));
                return Json(r);
            }

            default:
                throw new ArgumentException($"Unknown tool '{name}'");
        }
    }

    private static void ScaleRects(JsonNode? node, ScreenSpace s) => s.ScaleRects(node);

    private static JsonObject WithAction(JsonObject a, string action)
    {
        var o = (JsonObject)a.DeepClone();
        o["action"] = action;
        return o;
    }
}
