using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;

namespace TerminalWindows.Cli.Commands;

/// <summary>
/// <c>connect [NAME] [--list] [--tool ID,..] [--remove] [--dry-run]</c>: lets the AI tools on this PC use the VM.
/// For each tool it registers the MCP server with the installed exe's absolute path (works before PATH changes
/// reach running apps) and, where the tool reads one, writes the usage guide (skill / AGENTS.md / GEMINI.md section).
/// Without --tool it handles every tool that is installed; with --tool it also prepares tools not installed yet.
/// Other settings in those files are kept; each file is backed up once before the first change.
/// </summary>
public static class ConnectCommand
{
    public const string ServerName = "terminal-windows";
    private const string Begin = "<!-- terminal-windows:begin -->";
    private const string End = "<!-- terminal-windows:end -->";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>One AI tool: how to tell it is installed, where its MCP config lives, and how to (un)register.</summary>
    public sealed record Target(
        string Id, string Name, Func<bool> Installed, Func<IEnumerable<string>> ConfigFiles,
        Func<string, string, bool, Task<string>> Connect, Func<bool, Task<string>> Remove);

    /// <summary>A JSON-config tool: servers live under <paramref name="key"/> in each of <paramref name="files"/>.</summary>
    private static Target JsonTool(string id, string name, Func<bool> installed, Func<IEnumerable<string>> files, string key = "mcpServers", bool typed = false) =>
        new(id, name, installed, files,
            (exe, m, dry) => Task.FromResult(string.Join("; ", files().Select(f => JsonServer(f, key, Stdio(exe, m, typed), dry)))),
            dry => Task.FromResult(string.Join("; ", files().Select(f => JsonRemove(f, key, dry)))));

    public static IReadOnlyList<Target> Targets() => new[]
    {
        new Target("claude-code", "Claude Code",
            () => ClaudeExe() is not null, () => new[] { Path.Combine(Home, ".claude.json") }, ClaudeCodeAsync, ClaudeCodeRemoveAsync),
        new Target("claude-desktop", "Claude Desktop",
            () => ClaudeDesktopConfigs().Any() || AnyExists(Path.Combine(LocalAppData, "AnthropicClaude", "claude.exe")),
            () => ClaudeDesktopConfigs().DefaultIfEmpty(Path.Combine(AppData, "Claude", "claude_desktop_config.json")),
            (exe, m, dry) =>
            {
                // The running app rewrites its config from memory and would drop our entry again.
                if (!dry && ClaudeDesktopRunning())
                    throw new InvalidOperationException(Ui.T(
                        "Claude アプリが起動中です（書き込んでもアプリに上書きされます）。Claude を完全に終了してから terminal-windows connect --tool claude-desktop を実行してください。",
                        "the Claude app is running and would overwrite the change. Quit Claude completely, then run: terminal-windows connect --tool claude-desktop"));
                return Task.FromResult(string.Join("; ", ClaudeDesktopConfigs().DefaultIfEmpty(Path.Combine(AppData, "Claude", "claude_desktop_config.json"))
                    .Select(f => JsonServer(f, "mcpServers", Stdio(exe, m), dry))));
            },
            dry =>
            {
                if (!dry && ClaudeDesktopRunning())
                    throw new InvalidOperationException(Ui.T("Claude アプリを完全に終了してから実行してください。", "quit the Claude app completely first."));
                return Task.FromResult(string.Join("; ", ClaudeDesktopConfigs().Select(f => JsonRemove(f, "mcpServers", dry))));
            }),
        new Target("codex", "Codex CLI (OpenAI / ChatGPT)",
            () => FindOnPath("codex") is not null || File.Exists(Path.Combine(Home, ".codex", "config.toml")),
            () => new[] { Path.Combine(Home, ".codex", "config.toml") }, CodexAsync, CodexRemoveAsync),
        new Target("gemini", "Gemini CLI (Google)",
            () => FindOnPath("gemini") is not null || File.Exists(Path.Combine(Home, ".gemini", "settings.json")),
            () => new[] { Path.Combine(Home, ".gemini", "settings.json") }, GeminiAsync,
            dry => Task.FromResult(JsonRemove(Path.Combine(Home, ".gemini", "settings.json"), "mcpServers", dry) + "; " +
                                   SectionRemove(Path.Combine(Home, ".gemini", "GEMINI.md"), dry))),
        JsonTool("antigravity", "Antigravity (Google)",
            () => AnyExists(Path.Combine(LocalAppData, "Programs", "Antigravity", "Antigravity.exe")) || File.Exists(Path.Combine(Home, ".gemini", "antigravity", "mcp_config.json")),
            () => new[] { Path.Combine(Home, ".gemini", "antigravity", "mcp_config.json") }),
        JsonTool("cursor", "Cursor",
            () => AnyExists(Path.Combine(LocalAppData, "Programs", "cursor", "Cursor.exe"), Path.Combine(ProgramFiles, "Cursor", "Cursor.exe")) || Directory.Exists(Path.Combine(Home, ".cursor")),
            () => new[] { Path.Combine(Home, ".cursor", "mcp.json") }),
        JsonTool("vscode", "VS Code (Copilot agent mode)",
            () => AnyExists(Path.Combine(LocalAppData, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(ProgramFiles, "Microsoft VS Code", "Code.exe")) || FindOnPath("code") is not null,
            () => new[] { Path.Combine(AppData, "Code", "User", "mcp.json") }, key: "servers", typed: true),
        JsonTool("cline", "Cline (VS Code extension)",
            () => Directory.Exists(ClineDir()),
            () => new[] { Path.Combine(ClineDir(), "settings", "cline_mcp_settings.json") }),
        JsonTool("windsurf", "Windsurf",
            () => AnyExists(Path.Combine(LocalAppData, "Programs", "Windsurf", "Windsurf.exe")) || Directory.Exists(Path.Combine(Home, ".codeium", "windsurf")),
            () => new[] { Path.Combine(Home, ".codeium", "windsurf", "mcp_config.json") }),
        JsonTool("lmstudio", "LM Studio (local models, free)",
            () => AnyExists(Path.Combine(LocalAppData, "Programs", "LM Studio", "LM Studio.exe")) || Directory.Exists(Path.Combine(Home, ".lmstudio")),
            () => new[] { Path.Combine(Home, ".lmstudio", "mcp.json") }),
    };

    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var name = a.PosOrNull(0) ?? MachineStore.ImplicitMachine() ?? MachineStore.DefaultName;
        bool dry = a.Flag("dry-run"), list = a.Flag("list"), remove = a.Flag("remove");
        var only = a.Opt("tool")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()).ToHashSet();
        a.RejectUnknown();
        var all = Targets();
        if (only is not null && only.FirstOrDefault(id => all.All(t => t.Id != id)) is { } unknown)
            throw new UsageException($"Unknown tool '{unknown}'. Known: {string.Join(", ", all.Select(t => t.Id))}");

        if (list)
        {
            Console.WriteLine(Ui.T("  ID              ツール                          入っている  登録済み", "  ID              Tool                            installed   registered"));
            foreach (var t in all)
                Console.WriteLine($"  {t.Id,-15} {Ui.FitWidth(t.Name, 31)} {Ui.FitWidth(t.Installed() ? Ui.T("はい", "yes") : "-", 11)} {(IsRegistered(t) ? Ui.T("はい", "yes") : "-")}");
            Console.WriteLine(Ui.T("  登録: terminal-windows connect [--tool ID]   解除: terminal-windows connect --remove [--tool ID]",
                                   "  Register: terminal-windows connect [--tool ID]   Unregister: terminal-windows connect --remove [--tool ID]"));
            return 0;
        }

        if (!remove) MachineStore.Load(name);
        var exe = InstalledExe();
        int done = 0, failed = 0;
        foreach (var t in all)
        {
            bool chosen = only is null ? (remove ? IsRegistered(t) : t.Installed()) : only.Contains(t.Id);
            if (!chosen) continue;
            try
            {
                var what = remove ? await t.Remove(dry) : await t.Connect(exe, name, dry);
                Console.WriteLine($"  [{(dry ? "--" : "OK")}] {t.Name}: {what}");
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CliException or InvalidOperationException)
            {
                Console.WriteLine($"  [!!] {t.Name}: {ex.Message}");
                failed++;
            }
        }
        if (done == 0 && failed == 0)
            Console.WriteLine(remove ? Ui.T("  登録されているツールはありませんでした。", "  Nothing was registered.")
                                     : Ui.T("  対応している AI ツールが見つかりませんでした（一覧: terminal-windows connect --list）。", "  No supported AI tool found (see: terminal-windows connect --list)."));
        else if (!dry)
            Console.WriteLine(Ui.T("  それぞれのツールを開き直すと反映されます。", "  Restart each tool to apply."));
        if (!remove)
        {
            Console.WriteLine(Ui.T("  一覧にないツールには: terminal-windows mcp --config の設定を貼る / コマンドを実行できる AI なら", "  Other tools: paste the config from 'terminal-windows mcp --config', or, for AIs that run commands,"));
            Console.WriteLine(Ui.T("  「VM を使う前に terminal-windows skills get を実行して」と伝えるだけで使えます。", "  just tell them: \"before using the Windows VM, run terminal-windows skills get\"."));
        }
        return 0;
    }

    private static bool IsRegistered(Target t)
    {
        foreach (var f in t.ConfigFiles())
        {
            try
            {
                if (File.Exists(f) && File.ReadAllText(f).Contains($"\"{ServerName}\"", StringComparison.Ordinal)) return true;
                if (f.EndsWith(".toml") && File.Exists(f) && File.ReadAllText(f).Contains($"[mcp_servers.{ServerName}]")) return true;
            }
            catch (IOException) { }
        }
        return false;
    }

    // ------------------------------------------------------------------ per tool

    private static string? ClaudeExe()
    {
        var native = Path.Combine(Home, ".local", "bin", "claude.exe");
        return File.Exists(native) ? native : FindOnPath("claude");
    }

    private static Task<ProcessResult> Claude(params string[] args)
    {
        var claude = ClaudeExe() ?? throw new CliException("Claude Code is not installed");
        var run = new ProcessRunner();
        // An npm install is a .cmd shim, which only cmd.exe can start.
        return claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? run.RunAsync("cmd.exe", new[] { "/c", claude }.Concat(args).ToArray(), TimeSpan.FromSeconds(60))
            : run.RunAsync(claude, args, TimeSpan.FromSeconds(60));
    }

    private static string ClaudeSkillPath => Path.Combine(Home, ".claude", "skills", ServerName, "SKILL.md");

    private static async Task<string> ClaudeCodeAsync(string exe, string machine, bool dry)
    {
        if (dry) return $"would run 'claude mcp add --scope user' and write {ClaudeSkillPath}";
        Directory.CreateDirectory(Path.GetDirectoryName(ClaudeSkillPath)!);
        File.WriteAllText(ClaudeSkillPath, SkillsCommand.SkillText());
        await Claude("mcp", "remove", "--scope", "user", ServerName);
        var r = await Claude("mcp", "add", "--scope", "user", ServerName, "--", exe, "mcp", machine);
        if (!r.Success) throw new CliException("claude mcp add failed: " + (r.StdErr + r.StdOut).Trim());
        return $"MCP (user scope) + skill {ClaudeSkillPath}";
    }

    private static async Task<string> ClaudeCodeRemoveAsync(bool dry)
    {
        if (dry) return $"would run 'claude mcp remove --scope user' and delete {ClaudeSkillPath}";
        await Claude("mcp", "remove", "--scope", "user", ServerName);
        if (File.Exists(ClaudeSkillPath)) Directory.Delete(Path.GetDirectoryName(ClaudeSkillPath)!, recursive: true);
        return "removed MCP and skill";
    }

    /// <summary>
    /// Claude Desktop's config: current builds keep it in %LOCALAPPDATA%\Claude-Data\claude_(package)\, older ones
    /// in %APPDATA%\Claude\.
    /// </summary>
    private static IEnumerable<string> ClaudeDesktopConfigs()
    {
        var found = new List<string>();
        var data = Path.Combine(LocalAppData, "Claude-Data");
        if (Directory.Exists(data))
            found.AddRange(Directory.GetDirectories(data, "claude_*").Select(d => Path.Combine(d, "claude_desktop_config.json")));
        // Older builds only: once Claude-Data exists the app no longer reads %APPDATA%\Claude.
        var legacy = Path.Combine(AppData, "Claude", "claude_desktop_config.json");
        if (found.Count == 0 && File.Exists(legacy)) found.Add(legacy);
        return found;
    }

    /// <summary>The Claude desktop app (not the Claude Code CLI, which is also claude.exe) is running.</summary>
    private static bool ClaudeDesktopRunning()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("claude"))
        {
            try
            {
                var path = p.MainModule?.FileName ?? "";
                if (path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return false;
    }

    private static string ClineDir() => Path.Combine(AppData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev");

    private static string CodexConfig => Path.Combine(Home, ".codex", "config.toml");
    private static string CodexGuide => Path.Combine(Home, ".codex", "AGENTS.md");

    private static Task<string> CodexAsync(string exe, string machine, bool dry)
    {
        if (dry) return Task.FromResult($"would add [mcp_servers.{ServerName}] to {CodexConfig} and a section to {CodexGuide}");
        Directory.CreateDirectory(Path.GetDirectoryName(CodexConfig)!);
        var toml = File.Exists(CodexConfig) ? File.ReadAllText(CodexConfig) : "";
        Backup(CodexConfig);
        File.WriteAllText(CodexConfig, SetTomlServer(toml, exe, machine), new UTF8Encoding(false));
        WriteSection(CodexGuide, GuideText());
        return Task.FromResult($"MCP in {CodexConfig} + guide in {CodexGuide}");
    }

    private static Task<string> CodexRemoveAsync(bool dry)
    {
        if (dry) return Task.FromResult($"would remove [mcp_servers.{ServerName}] from {CodexConfig} and our section from {CodexGuide}");
        if (File.Exists(CodexConfig)) File.WriteAllText(CodexConfig, RemoveTomlServer(File.ReadAllText(CodexConfig)), new UTF8Encoding(false));
        return Task.FromResult("removed from config.toml; " + SectionRemove(CodexGuide, false));
    }

    private static Task<string> GeminiAsync(string exe, string machine, bool dry)
    {
        var dir = Path.Combine(Home, ".gemini");
        var guide = Path.Combine(dir, "GEMINI.md");
        var what = JsonServer(Path.Combine(dir, "settings.json"), "mcpServers", Stdio(exe, machine), dry);
        if (dry) return Task.FromResult($"{what}; would add a section to {guide}");
        WriteSection(guide, GuideText());
        return Task.FromResult($"{what} + guide in {guide} (Gemini CLI runs MCP servers only in folders you trust)");
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

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Adds or replaces our server under <paramref name="key"/> in a JSON config, keeping everything else.</summary>
    internal static string JsonServer(string path, string key, JsonObject server, bool dry)
    {
        if (dry) return $"would add \"{ServerName}\" to {path}";
        var root = ReadJson(path) ?? new JsonObject();
        if (root[key] is not JsonObject servers) root[key] = servers = new JsonObject();
        servers[ServerName] = server;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(Pretty), new UTF8Encoding(false));
        return $"MCP in {path}";
    }

    internal static string JsonRemove(string path, string key, bool dry)
    {
        var root = ReadJson(path);
        if (root?[key] is not JsonObject servers || !servers.ContainsKey(ServerName)) return $"not in {path}";
        if (dry) return $"would remove \"{ServerName}\" from {path}";
        servers.Remove(ServerName);
        File.WriteAllText(path, root.ToJsonString(Pretty), new UTF8Encoding(false));
        return $"removed from {path}";
    }

    /// <summary>The config as a JSON object (null when missing or empty); backs it up before anything changes it.</summary>
    private static JsonObject? ReadJson(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
        var text = File.ReadAllText(path);
        if (HasJsonComments(text))
            throw new InvalidOperationException($"{path} contains comments; change it by hand (see 'terminal-windows mcp --config') so they are not lost");
        var root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException($"{path} is not a JSON object; left unchanged");
        Backup(path);
        return root;
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

    /// <summary>The [mcp_servers.terminal-windows] table for a Codex config.toml (TOML literal strings: no escaping).</summary>
    internal static string TomlBlock(string exe, string machine)
    {
        string Q(string s) => "'" + s.Replace("'", "") + "'";
        return $"[mcp_servers.{ServerName}]\ncommand = {Q(exe)}\nargs = [{Q("mcp")}, {Q(machine)}]\n";
    }

    /// <summary>Adds or replaces the [mcp_servers.terminal-windows] table in a Codex config.toml.</summary>
    internal static string SetTomlServer(string toml, string exe, string machine)
    {
        var block = TomlBlock(exe, machine);
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

    internal static string RemoveTomlServer(string toml)
    {
        var lines = toml.Replace("\r\n", "\n").Split('\n').ToList();
        int start = lines.FindIndex(l => l.Trim() == $"[mcp_servers.{ServerName}]");
        if (start < 0) return toml;
        int end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
        lines.RemoveRange(start, end - start);
        return string.Join("\n", lines).TrimEnd('\n') + "\n";
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

    internal static string StripSection(string existing)
    {
        int b = existing.IndexOf(Begin, StringComparison.Ordinal);
        int e = existing.IndexOf(End, StringComparison.Ordinal);
        if (b < 0 || e < b) return existing;
        return (existing[..b].TrimEnd() + "\n" + existing[(e + End.Length)..].TrimStart('\r', '\n')).Trim() + "\n";
    }

    private static void WriteSection(string path, string text)
    {
        var old = File.Exists(path) ? File.ReadAllText(path) : "";
        if (old.Length > 0) Backup(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, MergeSection(old, text), new UTF8Encoding(false));
    }

    private static string SectionRemove(string path, bool dry)
    {
        if (!File.Exists(path) || !File.ReadAllText(path).Contains(Begin)) return $"no guide in {path}";
        if (dry) return $"would remove our section from {path}";
        File.WriteAllText(path, StripSection(File.ReadAllText(path)), new UTF8Encoding(false));
        return $"removed our section from {path}";
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
    internal static string InstalledExe()
    {
        var installed = Path.Combine(Home, ".terminal-windows", "bin", "terminal-windows.exe");
        return File.Exists(installed) ? installed : Environment.ProcessPath ?? installed;
    }

    private static bool AnyExists(params string[] paths) => paths.Any(File.Exists);

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
