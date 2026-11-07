using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;

namespace TerminalWindows.Cli.Commands;

/// <summary>
/// <c>connect [NAME] [--dry-run]</c>: lets every AI tool on this PC use the VM, not only Claude Code. For each
/// tool it finds (Claude Code, Claude Desktop, Codex CLI, Gemini CLI, Cursor, VS Code, Windsurf) it registers the
/// MCP server with the installed exe's absolute path (so it works before PATH changes reach running apps) and,
/// where the tool reads one, writes the usage guide (skill / AGENTS.md / GEMINI.md section).
/// Other settings in those files are kept; each file is backed up once before the first change.
/// </summary>
public static class ConnectCommand
{
    public const string ServerName = "terminal-windows";
    private const string Begin = "<!-- terminal-windows:begin -->";
    private const string End = "<!-- terminal-windows:end -->";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>One AI tool: whether it is on this PC, and how to connect it.</summary>
    public sealed record Target(string Name, Func<bool> Present, Func<string, string, bool, Task<string>> Connect);

    public static IReadOnlyList<Target> Targets() => new[]
    {
        new Target("Claude Code", () => FindOnPath("claude") is not null || File.Exists(Path.Combine(Home, ".local", "bin", "claude.exe")), ClaudeCodeAsync),
        new Target("Claude Desktop", () => Directory.Exists(Path.Combine(AppData, "Claude")),
            (exe, m, dry) => Task.FromResult(JsonServer(Path.Combine(AppData, "Claude", "claude_desktop_config.json"), "mcpServers", Stdio(exe, m), dry))),
        new Target("Codex CLI", () => Directory.Exists(Path.Combine(Home, ".codex")) || FindOnPath("codex") is not null, CodexAsync),
        new Target("Gemini CLI", () => Directory.Exists(Path.Combine(Home, ".gemini")) || FindOnPath("gemini") is not null, GeminiAsync),
        new Target("Cursor", () => Directory.Exists(Path.Combine(Home, ".cursor")) || Directory.Exists(Path.Combine(AppData, "Cursor")),
            (exe, m, dry) => Task.FromResult(JsonServer(Path.Combine(Home, ".cursor", "mcp.json"), "mcpServers", Stdio(exe, m), dry))),
        new Target("VS Code", () => Directory.Exists(Path.Combine(AppData, "Code", "User")),
            (exe, m, dry) => Task.FromResult(JsonServer(Path.Combine(AppData, "Code", "User", "mcp.json"), "servers", Stdio(exe, m, withType: true), dry))),
        new Target("Windsurf", () => Directory.Exists(Path.Combine(Home, ".codeium", "windsurf")),
            (exe, m, dry) => Task.FromResult(JsonServer(Path.Combine(Home, ".codeium", "windsurf", "mcp_config.json"), "mcpServers", Stdio(exe, m), dry))),
    };

    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var name = a.PosOrNull(0) ?? MachineStore.ImplicitMachine() ?? MachineStore.DefaultName;
        bool dry = a.Flag("dry-run");
        a.RejectUnknown();
        MachineStore.Load(name);
        var exe = InstalledExe();

        int connected = 0;
        foreach (var t in Targets())
        {
            if (!t.Present()) continue;
            try
            {
                var what = await t.Connect(exe, name, dry);
                Console.WriteLine($"  [{(dry ? "--" : "OK")}] {t.Name}: {what}");
                connected++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CliException or InvalidOperationException)
            {
                Console.WriteLine($"  [!!] {t.Name}: {ex.Message}");
            }
        }
        if (connected == 0)
            Console.WriteLine(Ui.T("  対応している AI ツール（Claude Code、Claude Desktop、Codex、Gemini CLI、Cursor、VS Code、Windsurf）が見つかりませんでした。",
                                   "  No supported AI tool found (Claude Code, Claude Desktop, Codex, Gemini CLI, Cursor, VS Code, Windsurf)."));
        else if (!dry)
            Console.WriteLine(Ui.T("  それぞれのツールを開き直すと使えます。", "  Restart each tool to load it."));
        Console.WriteLine(Ui.T("  ほかのツールには次の設定を貼ってください:", "  For any other MCP client, paste this:"));
        Output.Json(new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = Stdio(exe, name) } });
        return 0;
    }

    // ------------------------------------------------------------------ per tool

    private static async Task<string> ClaudeCodeAsync(string exe, string machine, bool dry)
    {
        var skill = Path.Combine(Home, ".claude", "skills", ServerName, "SKILL.md");
        if (dry) return $"would run 'claude mcp add --scope user' and write {skill}";
        Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
        File.WriteAllText(skill, SkillsCommand.SkillText());
        var native = Path.Combine(Home, ".local", "bin", "claude.exe");
        var claude = File.Exists(native) ? native : FindOnPath("claude")!;
        var run = new ProcessRunner();
        // An npm install is a .cmd shim, which only cmd.exe can start.
        Task<ProcessResult> Claude(params string[] args) => claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? run.RunAsync("cmd.exe", new[] { "/c", claude }.Concat(args).ToArray(), TimeSpan.FromSeconds(60))
            : run.RunAsync(claude, args, TimeSpan.FromSeconds(60));
        await Claude("mcp", "remove", "--scope", "user", ServerName);
        var r = await Claude("mcp", "add", "--scope", "user", ServerName, "--", exe, "mcp", machine);
        if (!r.Success) throw new CliException("claude mcp add failed: " + (r.StdErr + r.StdOut).Trim());
        return $"MCP (user scope) + skill {skill}";
    }

    private static Task<string> CodexAsync(string exe, string machine, bool dry)
    {
        var dir = Path.Combine(Home, ".codex");
        var config = Path.Combine(dir, "config.toml");
        var guide = Path.Combine(dir, "AGENTS.md");
        if (dry) return Task.FromResult($"would add [mcp_servers.{ServerName}] to {config} and a section to {guide}");
        Directory.CreateDirectory(dir);
        var toml = File.Exists(config) ? File.ReadAllText(config) : "";
        Backup(config);
        File.WriteAllText(config, SetTomlServer(toml, exe, machine), new UTF8Encoding(false));
        WriteSection(guide, GuideText());
        return Task.FromResult($"MCP in {config} + guide in {guide}");
    }

    private static Task<string> GeminiAsync(string exe, string machine, bool dry)
    {
        var dir = Path.Combine(Home, ".gemini");
        var guide = Path.Combine(dir, "GEMINI.md");
        var what = JsonServer(Path.Combine(dir, "settings.json"), "mcpServers", Stdio(exe, machine), dry);
        if (dry) return Task.FromResult($"{what}; would add a section to {guide}");
        WriteSection(guide, GuideText());
        return Task.FromResult($"{what} + guide in {guide}");
    }

    // ------------------------------------------------------------------ helpers (internal for tests)

    internal static JsonObject Stdio(string exe, string machine, bool withType = false)
    {
        var o = new JsonObject();
        if (withType) o["type"] = "stdio";
        o["command"] = exe;
        o["args"] = new JsonArray("mcp", machine);
        return o;
    }

    /// <summary>Adds or replaces our server under <paramref name="key"/> in a JSON config, keeping everything else.</summary>
    internal static string JsonServer(string path, string key, JsonObject server, bool dry)
    {
        if (dry) return $"would add \"{ServerName}\" to {path}";
        JsonObject root;
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            var text = File.ReadAllText(path);
            if (HasJsonComments(text))
                throw new InvalidOperationException($"{path} contains comments; add the server by hand (config below) so they are not lost");
            root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException($"{path} is not a JSON object; left unchanged");
            Backup(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            root = new JsonObject();
        }
        if (root[key] is not JsonObject servers) root[key] = servers = new JsonObject();
        servers[ServerName] = server;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        return $"MCP in {path}";
    }

    internal static bool HasJsonComments(string json)
    {
        bool inString = false;
        for (int i = 0; i < json.Length - 1; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '/' && json[i + 1] is '/' or '*') return true;
        }
        return false;
    }

    /// <summary>Adds or replaces the [mcp_servers.terminal-windows] table in a Codex config.toml.</summary>
    internal static string SetTomlServer(string toml, string exe, string machine)
    {
        string Q(string s) => "'" + s.Replace("'", "") + "'"; // TOML literal string: no escapes needed for paths
        var block = $"[mcp_servers.{ServerName}]\ncommand = {Q(exe)}\nargs = [{Q("mcp")}, {Q(machine)}]\n";
        var lines = toml.Replace("\r\n", "\n").Split('\n').ToList();
        int start = lines.FindIndex(l => l.Trim() == $"[mcp_servers.{ServerName}]");
        if (start >= 0)
        {
            int end = start + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
            lines.RemoveRange(start, end - start);
            lines.InsertRange(start, block.TrimEnd('\n').Split('\n').Append(""));
            return string.Join("\n", lines).TrimEnd('\n') + "\n";
        }
        var body = string.Join("\n", lines).TrimEnd('\n');
        return (body.Length > 0 ? body + "\n\n" : "") + block;
    }

    /// <summary>Puts <paramref name="text"/> between our markers in a Markdown file, keeping the rest of the file.</summary>
    internal static string MergeSection(string existing, string text)
    {
        var section = $"{Begin}\n{text.Trim()}\n{End}";
        int b = existing.IndexOf(Begin, StringComparison.Ordinal);
        int e = existing.IndexOf(End, StringComparison.Ordinal);
        if (b >= 0 && e > b)
            return existing[..b] + section + existing[(e + End.Length)..];
        return (existing.TrimEnd() + "\n\n" + section + "\n").TrimStart();
    }

    private static void WriteSection(string path, string text)
    {
        var old = File.Exists(path) ? File.ReadAllText(path) : "";
        if (old.Length > 0) Backup(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, MergeSection(old, text), new UTF8Encoding(false));
    }

    /// <summary>The host skill without its front matter, for tools that read a plain instructions file.</summary>
    private static string GuideText()
    {
        var md = SkillsCommand.SkillText().Replace("\r\n", "\n");
        if (md.StartsWith("---"))
        {
            int end = md.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0) md = md[(md.IndexOf('\n', end + 1) + 1)..];
        }
        return md;
    }

    /// <summary>Keeps the person's original once: file.bak-terminal-windows (never overwritten).</summary>
    private static void Backup(string path)
    {
        var bak = path + ".bak-terminal-windows";
        if (File.Exists(path) && !File.Exists(bak)) File.Copy(path, bak);
    }

    /// <summary>The installed copy when there is one (a dist/ or Downloads copy may move or be deleted).</summary>
    private static string InstalledExe()
    {
        var installed = Path.Combine(Home, ".terminal-windows", "bin", "terminal-windows.exe");
        return File.Exists(installed) ? installed : Environment.ProcessPath ?? installed;
    }

    private static string? FindOnPath(string command)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in new[] { ".exe", ".cmd" })
            {
                string p;
                try { p = Path.Combine(dir.Trim().Trim('"'), command + ext); } catch (ArgumentException) { continue; }
                if (File.Exists(p)) return p;
            }
        return null;
    }
}
