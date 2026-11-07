using System.Reflection;
using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Cli.Mcp;
using TerminalWindows.Cli.Setup;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Commands;

/// <summary><c>action NAME &lt;verb&gt; ...</c>: one guest operation, JSON on stdout, non-zero exit on failure.
/// Coordinates are raw guest screen pixels (the MCP server rescales; this command does not).</summary>
public static class ActionCommand
{
    public const string Verbs = "status|screenshot|click|double_click|right_click|drag|scroll|type|key|ui_tree|" +
                                "find|click-element|set-text|wait-for|read-text|windows|focus|close|open|clipboard|run";

    /// <summary>Element query options shared by the element verbs: --name --type --id --window --exact.</summary>
    private static JsonObject Query(ParsedArgs a, Action<JsonObject>? extra = null)
    {
        var q = new JsonObject();
        if (a.Opt("name") is { } n) q["name"] = n;
        if (a.Opt("type") is { } t) q["control_type"] = t;
        if (a.Opt("id") is { } id) q["automation_id"] = id;
        if (a.Opt("window") is { } w) q["window"] = w;
        if (a.Flag("exact")) q["exact"] = true;
        extra?.Invoke(q);
        a.RejectUnknown();
        return q;
    }

    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        var verb = a.Pos(1, "action (" + Verbs + ")");
        JsonObject result;
        // Like the MCP server: a stopped or saved VM is started first (progress goes to stderr, JSON to stdout).
        if (verb != "status") await new MachineManager().EnsureRunningAsync(m);
        await using var c = await GuestClient.ConnectAsync(m);
        switch (verb)
        {
            case "screenshot":
            {
                var outPath = a.Opt("out") ?? Path.Combine(MachineStore.MachineDir(m.Name), "screenshots", $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                int? mw = a.Opt("max-width") is { } w ? int.Parse(w) : null;
                int? mh = a.Opt("max-height") is { } h ? int.Parse(h) : null;
                a.RejectUnknown();
                var (info, png) = await c.ScreenshotAsync(mw ?? (mh.HasValue ? int.MaxValue : null), mh ?? (mw.HasValue ? int.MaxValue : null));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                await File.WriteAllBytesAsync(outPath, png);
                info["path"] = Path.GetFullPath(outPath);
                result = info;
                break;
            }
            case "click" or "double_click" or "right_click":
            {
                int x = a.PosInt(2, "X"), y = a.PosInt(3, "Y");
                var button = verb == "right_click" ? "right" : a.Opt("button") ?? "left";
                int count = verb == "double_click" ? 2 : a.Int("count", 1);
                a.RejectUnknown();
                result = await c.CallAsync("click", new JsonObject { ["x"] = x, ["y"] = y, ["button"] = button, ["count"] = count });
                break;
            }
            case "drag":
            {
                var args = new JsonObject { ["x1"] = a.PosInt(2, "X1"), ["y1"] = a.PosInt(3, "Y1"), ["x2"] = a.PosInt(4, "X2"), ["y2"] = a.PosInt(5, "Y2"), ["button"] = a.Opt("button") ?? "left" };
                a.RejectUnknown();
                result = await c.CallAsync("drag", args);
                break;
            }
            case "scroll":
            {
                var args = new JsonObject { ["dy"] = a.PosOrNull(2) is { } d ? int.Parse(d) : a.Int("dy", 0), ["dx"] = a.Int("dx", 0) };
                if (a.Opt("x") is { } x && a.Opt("y") is { } y) { args["x"] = int.Parse(x); args["y"] = int.Parse(y); }
                a.RejectUnknown();
                result = await c.CallAsync("scroll", args);
                break;
            }
            case "type":
            {
                var text = string.Join(' ', a.Positionals.Skip(2));
                if (text.Length == 0) throw new UsageException("Missing TEXT");
                a.RejectUnknown();
                result = await c.CallAsync("type", new JsonObject { ["text"] = text }, TimeSpan.FromMinutes(5));
                break;
            }
            case "key":
            {
                var keys = string.Join(' ', a.Positionals.Skip(2));
                KeyChord.ParseSequence(keys);
                a.RejectUnknown();
                result = await c.CallAsync("key", new JsonObject { ["keys"] = keys });
                break;
            }
            case "ui_tree":
            {
                var args = new JsonObject { ["depth"] = a.Int("depth", 6), ["max_nodes"] = a.Int("max-nodes", 1500) };
                if (a.Opt("window") is { } w) args["window"] = w;
                a.RejectUnknown();
                result = await c.CallAsync("ui_tree", args, TimeSpan.FromSeconds(30));
                break;
            }
            case "status":
                a.RejectUnknown();
                result = await c.CallAsync("status");
                break;

            // ---- element-level verbs (same as the MCP tools; for agents that only have a shell) ----
            case "find":
                result = await c.CallAsync("uia_find", Query(a, extra: q => q["max"] = a.Int("max", 20)), TimeSpan.FromSeconds(30));
                break;
            case "click-element":
                result = await c.CallAsync("uia_act", Query(a, extra: q =>
                {
                    q["action"] = a.Opt("action") ?? "click";
                    q["index"] = a.Int("index", 0);
                }), TimeSpan.FromSeconds(30));
                break;
            case "set-text":
            {
                var value = a.Opt("value") ?? throw new UsageException("set-text needs --value TEXT");
                result = await c.CallAsync("uia_act", Query(a, extra: q =>
                {
                    q["action"] = "set_value";
                    q["value"] = value;
                    q["index"] = a.Int("index", 0);
                }), TimeSpan.FromMinutes(2));
                break;
            }
            case "wait-for":
            {
                bool gone = a.Flag("gone");
                int timeout = a.Int("timeout-ms", 30_000);
                var q = Query(a, extra: o => o["max"] = 1);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    JsonObject r;
                    try { r = await c.CallAsync("uia_find", (JsonObject)q.DeepClone(), TimeSpan.FromSeconds(30)); }
                    catch (RpcException ex) when (ex.Message.Contains("No window title contains")) { r = new JsonObject { ["count"] = 0 }; }
                    if ((r["count"]?.GetValue<int>() > 0) != gone) { r["waitedMs"] = sw.ElapsedMilliseconds; result = r; break; }
                    if (sw.ElapsedMilliseconds >= timeout) throw new CliException(gone ? $"still there after {timeout} ms" : $"not found within {timeout} ms");
                    await Task.Delay(500);
                }
                break;
            }
            case "read-text":
            {
                var args = new JsonObject { ["max_chars"] = a.Int("max-chars", 20000) };
                if (a.Opt("window") is { } w) args["window"] = w;
                a.RejectUnknown();
                result = await c.CallAsync("uia_text", args, TimeSpan.FromSeconds(45));
                break;
            }
            case "windows":
                a.RejectUnknown();
                result = await c.CallAsync("windows");
                break;
            case "focus" or "close":
            {
                var w = a.PosOrNull(2) ?? a.Opt("window") ?? throw new UsageException($"{verb} needs a window title");
                var args = new JsonObject { ["window"] = w };
                if (verb == "close") args["force"] = a.Flag("force");
                a.RejectUnknown();
                result = await c.CallAsync(verb == "focus" ? "window_focus" : "window_close", args);
                break;
            }
            case "open":
            {
                var target = a.Pos(2, "TARGET (program, path or URL)");
                var args = new JsonObject { ["target"] = target };
                if (a.Opt("args") is { } extra) args["args"] = extra;
                a.RejectUnknown();
                result = await c.CallAsync("open", args, TimeSpan.FromSeconds(30));
                break;
            }
            case "clipboard":
            {
                var text = a.Opt("set");
                a.RejectUnknown();
                result = text is null ? await c.CallAsync("clipboard_get") : await c.CallAsync("clipboard_set", new JsonObject { ["text"] = text });
                break;
            }
            case "run":
            {
                var script = string.Join(' ', a.Positionals.Skip(2));
                if (script.Length == 0) throw new UsageException("run needs a PowerShell script");
                int t = a.Int("timeout", 120);
                a.RejectUnknown();
                result = await c.CallAsync("exec", new JsonObject { ["script"] = script, ["timeout_s"] = t }, TimeSpan.FromSeconds(t + 30));
                break;
            }
            default:
                throw new UsageException($"Unknown action '{verb}'. Use one of: {Verbs}");
        }
        Output.Json(result);
        return 0;
    }
}

public static class SnapshotCommands
{
    public static async Task<int> SnapshotAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        var label = a.Opt("name");
        a.RejectUnknown();
        Console.WriteLine(await new MachineManager().SnapshotAsync(m, label));
        return 0;
    }

    /// <summary><c>delete-snapshot [NAME] LABEL --yes</c>: frees the disk space a snapshot holds (it cannot be restored afterwards).</summary>
    public static async Task<int> DeleteAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        var label = a.Pos(1, "LABEL");
        bool yes = a.Flag("yes");
        a.RejectUnknown();
        var backend = new MachineManager().Backend;
        var snaps = await backend.ListSnapshotsAsync(m.VmName);
        if (!snaps.Any(s => s.Name == label))
            throw new CliException($"No snapshot named '{label}'. Available: {string.Join(", ", snaps.Select(s => s.Name))}");
        if (!yes)
        {
            Console.WriteLine($"This deletes snapshot '{label}' of {m.Name}; you cannot go back to it afterwards. Re-run with --yes to delete.");
            return 1;
        }
        await backend.DeleteSnapshotAsync(m.VmName, label);
        Console.WriteLine($"Deleted snapshot '{label}'.");
        return 0;
    }

    public static async Task<int> RestoreAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        var label = a.Pos(1, "LABEL");
        a.RejectUnknown();
        var safety = await new MachineManager().RestoreAsync(m, label);
        Console.WriteLine($"Restored '{label}'. Previous state saved as '{safety}'.");
        return 0;
    }

    public static async Task<int> ListAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        a.RejectUnknown();
        var snaps = await new MachineManager().Backend.ListSnapshotsAsync(m.VmName);
        Output.Json(new JsonArray(snaps.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["current"] = s.IsCurrent }).ToArray()));
        return 0;
    }

    public static async Task<int> CloneAsync(ParsedArgs a)
    {
        var src = MachineStore.Load(a.Machine());
        var newName = a.Pos(1, "NEW");
        var snap = a.Opt("from-snapshot") ?? MachineManager.CleanSetupSnapshot;
        a.RejectUnknown();
        var m = await new MachineManager().CloneAsync(src, newName, snap);
        Console.WriteLine($"Cloned {src.Name}@{snap} -> {m.Name}. Start it with 'terminal-windows up {m.Name}'.");
        return 0;
    }

    public static async Task<int> RefreshPayloadAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        var edition = a.Opt("edition");
        a.RejectUnknown();
        var path = await new MachineManager().RefreshPayloadAsync(m, edition);
        Console.WriteLine($"Setup disc replaced with {path}. It is read at the guest's first logon; if that already happened, use 'update-helper' after setup.");
        return 0;
    }

    public static async Task<int> UpdateHelperAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        a.RejectUnknown();
        var exe = PayloadIso.FindHelperExe() ?? throw new CliException("No bundled TerminalWindowsGuest.exe found");
        await using var c = await GuestClient.ConnectAsync(m);
        var ok = await c.UpdateHelperAsync(exe);
        Console.WriteLine(ok ? "Helper update accepted; it restarts in a few seconds." : "Helper rejected the update (hash mismatch).");
        if (!ok) return 1;
        await c.DisposeAsync();
        if (m.Agent is not null and not "none")
        {
            // Same pacing as the automatic update: the old helper keeps answering until it exits.
            await Task.Delay(4000);
            for (int attempt = 1; ; attempt++)
            {
                await new MachineManager(log: _ => { }).WaitForHelperAsync(m, TimeSpan.FromMinutes(2));
                try { await Agent.RefreshPromptsAsync(m); break; }
                catch (Exception ex) when (attempt < 5 && ex is IOException or CliException or RpcException) { await Task.Delay(3000); }
            }
            Console.WriteLine($"Updated the in-VM {m.Agent} instructions (skill, brief, tool permissions).");
        }
        return 0;
    }
}

public static class IsoInfoCommand
{
    /// <summary><c>iso-info PATH</c>: lists the Windows editions inside an ISO and which one 'create' would pick.</summary>
    public static Task<int> RunAsync(ParsedArgs a)
    {
        var iso = a.Pos(0, "ISO path");
        var edition = a.Opt("edition");
        a.RejectUnknown();
        var images = IsoInspector.ReadImages(iso);
        var pick = IsoInspector.Choose(images, edition);
        Output.Json(new JsonObject
        {
            ["iso"] = Path.GetFullPath(iso),
            ["images"] = new JsonArray(images.Select(i => (JsonNode)new JsonObject
            {
                ["index"] = i.Index, ["name"] = i.Name, ["editionId"] = i.EditionId, ["displayName"] = i.DisplayName,
            }).ToArray()),
            ["selected"] = new JsonObject { ["index"] = pick.Index, ["name"] = pick.Name },
        });
        return Task.FromResult(0);
    }
}

public static class McpCommand
{
    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var name = a.PosOrNull(0) ?? MachineStore.DefaultName;
        bool config = a.Flag("config");
        bool sync = a.Flag("sync-outbox");
        a.RejectUnknown();
        if (config)
        {
            var exe = ConnectCommand.InstalledExe();
            Console.WriteLine("# Easiest: register with every AI tool on this PC (or one: --tool ID; list: --list)");
            Console.WriteLine($"terminal-windows connect {name}");
            Console.WriteLine();
            Console.WriteLine("# Claude Code");
            Console.WriteLine($"claude mcp add --scope user terminal-windows -- \"{exe}\" mcp {name}");
            Console.WriteLine();
            Console.WriteLine("# Most clients (Claude Desktop, Cursor, Windsurf, Gemini CLI, Antigravity, Cline, LM Studio, ...): \"mcpServers\"");
            Output.Json(new JsonObject { ["mcpServers"] = new JsonObject { ["terminal-windows"] = ConnectCommand.Stdio(exe, name) } });
            Console.WriteLine();
            Console.WriteLine("# VS Code (mcp.json): \"servers\"");
            Output.Json(new JsonObject { ["servers"] = new JsonObject { ["terminal-windows"] = ConnectCommand.Stdio(exe, name, withType: true) } });
            Console.WriteLine();
            Console.WriteLine("# Codex CLI (~/.codex/config.toml)");
            Console.Write(ConnectCommand.TomlBlock(exe, name));
            Console.WriteLine();
            Console.WriteLine("# AIs without MCP that can run commands: tell them to run this first, then use 'terminal-windows action'");
            Console.WriteLine("terminal-windows skills get");
            return 0;
        }
        MachineStore.Load(name); // fail fast with a clear message
        await new McpServer(name, sync).RunAsync();
        return 0;
    }
}

public static class SkillsCommand
{
    public static string SkillText()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SKILL.md") ?? throw new CliException("SKILL.md resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static Task<int> RunAsync(ParsedArgs a)
    {
        var verb = a.Pos(0, "install|get");
        switch (verb)
        {
            case "get":
                a.RejectUnknown();
                Console.Write(SkillText());
                return Task.FromResult(0);
            case "install":
            {
                var dir = a.Opt("dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");
                a.RejectUnknown();
                var target = Path.Combine(dir, "terminal-windows", "SKILL.md");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, SkillText());
                Console.WriteLine($"Installed {target}");
                return Task.FromResult(0);
            }
            default:
                throw new UsageException("skills install [--dir PATH] | skills get");
        }
    }
}
