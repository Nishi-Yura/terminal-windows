using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public McpServer(string machineName, bool syncOutbox = false)
    {
        _machineName = machineName;
        _syncOutbox = syncOutbox;
        _manager = new MachineManager(log: s => Console.Error.WriteLine(s));
    }

    private static readonly string Instructions =
        "Controls an AI-dedicated Windows VM (VirtualBox or VMware Workstation). Actions affect only the VM, never the host. " +
        "Start every session with read_notes. Loop: screenshot -> act -> screenshot to verify. " +
        "Coordinates are pixels of the latest screenshot image. Use ui_tree to locate controls precisely. " +
        "Take a snapshot before destructive changes. Put deliverables in Desktop\\Outbox and call export_file. " +
        "If status reports a lock/logon screen, stop and tell the user. Record progress with append_notes.";

    public async Task RunAsync()
    {
        if (_syncOutbox) _ = Task.Run(() => Commands.Transfer.SyncOutboxLoopAsync(MachineStore.Load(_machineName), CancellationToken.None));
        await McpStdio.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), "terminal-windows", Instructions,
            McpTools.Definitions, (name, args) => CallToolAsync(name, args));
    }

    private static JsonObject Text(string text, bool isError = false) => McpResult.Text(text, isError);
    private static JsonObject Json(JsonNode node) => McpResult.Json(node);

    private async Task<GuestClient> ClientAsync()
    {
        if (_client is not null) return _client;
        var m = MachineStore.Load(_machineName);
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
            return await DispatchAsync(name, args);
        }
        catch (Exception ex) when (ex is RpcException or CliException or ArgumentException or InvalidOperationException or IOException or TimeoutException or FormatException)
        {
            if (ex is IOException or TimeoutException or RpcException) await ResetClientAsync();
            return Text($"{name} failed: {ex.Message}", isError: true);
        }
        finally
        {
            _gate.Release();
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

            default:
                throw new ArgumentException($"Unknown tool '{name}'");
        }
    }

    private static void ScaleRects(JsonNode? node, ScreenSpace s) => s.ScaleRects(node);
}
