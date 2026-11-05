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
    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        var verb = a.Pos(1, "action (screenshot|click|double_click|right_click|drag|scroll|type|key|ui_tree|status)");
        JsonObject result;
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
            default:
                throw new UsageException($"Unknown action '{verb}'");
        }
        Output.Json(result);
        return 0;
    }
}

public static class SnapshotCommands
{
    public static async Task<int> SnapshotAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        var label = a.Opt("name");
        a.RejectUnknown();
        Console.WriteLine(await new MachineManager().SnapshotAsync(m, label));
        return 0;
    }

    public static async Task<int> RestoreAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        var label = a.Pos(1, "LABEL");
        a.RejectUnknown();
        var safety = await new MachineManager().RestoreAsync(m, label);
        Console.WriteLine($"Restored '{label}'. Previous state saved as '{safety}'.");
        return 0;
    }

    public static async Task<int> ListAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        a.RejectUnknown();
        var snaps = await new MachineManager().Backend.ListSnapshotsAsync(m.VmName);
        Output.Json(new JsonArray(snaps.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["current"] = s.IsCurrent }).ToArray()));
        return 0;
    }

    public static async Task<int> CloneAsync(ParsedArgs a)
    {
        var src = MachineStore.Load(a.Pos(0, "NAME"));
        var newName = a.Pos(1, "NEW");
        var snap = a.Opt("from-snapshot") ?? MachineManager.CleanSetupSnapshot;
        a.RejectUnknown();
        var m = await new MachineManager().CloneAsync(src, newName, snap);
        Console.WriteLine($"Cloned {src.Name}@{snap} -> {m.Name}. Start it with 'terminal-windows up {m.Name}'.");
        return 0;
    }

    public static async Task<int> RefreshPayloadAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        var edition = a.Opt("edition");
        a.RejectUnknown();
        var path = await new MachineManager().RefreshPayloadAsync(m, edition);
        Console.WriteLine($"Setup disc replaced with {path}. It is read at the guest's first logon; if that already happened, use 'update-helper' after setup.");
        return 0;
    }

    public static async Task<int> UpdateHelperAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        a.RejectUnknown();
        var exe = PayloadIso.FindHelperExe() ?? throw new CliException("No bundled TerminalWindowsGuest.exe found");
        await using var c = await GuestClient.ConnectAsync(m);
        var ok = await c.UpdateHelperAsync(exe);
        Console.WriteLine(ok ? "Helper update accepted; it restarts in a few seconds." : "Helper rejected the update (hash mismatch).");
        return ok ? 0 : 1;
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
            var exe = Environment.ProcessPath ?? "terminal-windows.exe";
            var server = new JsonObject { ["command"] = exe, ["args"] = new JsonArray("mcp", name) };
            Console.WriteLine("# Claude Code:");
            Console.WriteLine($"claude mcp add terminal-windows -- \"{exe}\" mcp {name}");
            Console.WriteLine();
            Console.WriteLine("# Generic MCP client config (Claude Desktop, Cursor, ... ):");
            Output.Json(new JsonObject { ["mcpServers"] = new JsonObject { ["terminal-windows"] = server } });
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
