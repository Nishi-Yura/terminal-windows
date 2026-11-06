using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Cli.Viewer;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Commands;

/// <summary>
/// Running an AI agent INSIDE the VM:
///   term NAME [-- COMMAND...]      a terminal session in the guest (default: PowerShell)
///   claude NAME [-- ARGS...]       Claude Code running in the guest, shown in this terminal
///   exec NAME SCRIPT               run one PowerShell script in the guest
///   agent-install NAME --yes       install Git, Claude Code, the desktop MCP and its skill in the guest
/// The host stays the manager: snapshots, restores, file hand-off and stopping the VM.
/// </summary>
public static class Agent
{
    public const string GuestHelperPath = @"C:\Program Files\terminal-windows\TerminalWindowsGuest.exe";
    public const string DetachHint = "Ctrl+] to detach (the program inside the VM is closed)";

    public static Task<int> TermAsync(ParsedArgs a) => OpenTerminalAsync(a, defaultCommand: "powershell.exe -NoLogo", prefix: null);

    public static Task<int> ClaudeAsync(ParsedArgs a) => OpenTerminalAsync(a, defaultCommand: "claude", prefix: "claude");

    private static async Task<int> OpenTerminalAsync(ParsedArgs a, string defaultCommand, string? prefix)
    {
        var m = MachineStore.Load(a.Machine());
        var cwd = a.Opt("cwd");
        bool window = a.Flag("window");
        var protocol = a.Opt("graphics");
        a.RejectUnknown();
        var extra = a.Positionals.Skip(1).ToList();
        string command = extra.Count == 0 ? defaultCommand
            : prefix is null ? (extra.Count == 1 ? extra[0] : string.Join(' ', extra.Select(Quote)))
            : prefix + " " + string.Join(' ', extra.Select(Quote));
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new CliException("This command needs an interactive terminal.");
        if (window) return await OpenInDesktopWindowAsync(m, command, cwd, protocol);

        await using var c = await GuestClient.ConnectAsync(m);
        return await new GuestTerminalClient(c).RunAsync(command, cwd);
    }

    /// <summary>
    /// --window: runs the command in a Windows Terminal window on the VM's own desktop (so it is part of
    /// what the screen shows, and the AI can see itself work), then opens the viewer in this terminal.
    /// An already running window with that title is brought to the front instead of starting another.
    /// </summary>
    private static async Task<int> OpenInDesktopWindowAsync(MachineConfig m, string command, string? cwd, string? protocol)
    {
        await new MachineManager().EnsureRunningAsync(m);
        var title = command.Split(' ')[0] switch { "claude" => "Claude Code", "codex" => "Codex", "gemini" => "Gemini CLI", var other => other };
        string Ps(string s) => "'" + s.Replace("'", "''") + "'";
        var script = $$"""
            $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
            $dir = {{(cwd is null ? "$env:USERPROFILE" : Ps(cwd))}}
            $title = {{Ps(title)}}
            $open = Get-Process WindowsTerminal -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*$title*" }
            if ($open) {
              Add-Type -Namespace TW -Name Fg -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);'
              [TW.Fg]::ShowWindow($open[0].MainWindowHandle, 9) | Out-Null
              [TW.Fg]::SetForegroundWindow($open[0].MainWindowHandle) | Out-Null
              'focused'
            } else {
              # One argument string: Start-Process joins an array without quoting, which splits "Claude Code".
              Start-Process wt.exe -ArgumentList ('-w new --maximized new-tab --title "{0}" --suppressApplicationTitle -d "{1}" cmd /k {2}' -f $title, $dir, {{Ps(command)}})
              'started'
            }
            """;
        await using (var c = await GuestClient.ConnectAsync(m))
        {
            var r = await RunScriptAsync(c, script, 60);
            if (r["exitCode"]?.GetValue<int>() != 0) throw new CliException("Could not open the window in the VM: " + r["stderr"]);
        }
        Console.WriteLine($"{title} is open on the VM's desktop. Showing the VM screen here: type into it directly (Ctrl+] leaves the viewer; it keeps running).");
        await Task.Delay(1500);
        var view = ParsedArgs.Parse(protocol is null ? new[] { m.Name } : new[] { m.Name, "--graphics", protocol }, Program.Flags);
        return await Lifecycle.ViewAsync(view);
    }

    private static string Quote(string s) => s.Length > 0 && s.IndexOfAny(new[] { ' ', '"' }) < 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    public static async Task<int> ExecAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        var script = string.Join(' ', a.Positionals.Skip(1));
        int timeout = a.Int("timeout", 300);
        a.RejectUnknown();
        if (script.Length == 0) throw new UsageException("Missing SCRIPT");
        await using var c = await GuestClient.ConnectAsync(m);
        var r = await RunScriptAsync(c, script, timeout);
        Console.Write(r["stdout"]?.GetValue<string>());
        var err = r["stderr"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(err)) Console.Error.Write(err);
        return r["exitCode"]?.GetValue<int>() ?? 1;
    }

    private static async Task<JsonObject> RunScriptAsync(GuestClient c, string script, int timeoutSeconds)
    {
        try
        {
            return await c.CallAsync("exec", new JsonObject { ["script"] = script, ["timeout_s"] = timeoutSeconds }, TimeSpan.FromSeconds(timeoutSeconds + 30));
        }
        catch (RpcException ex) when (ex.Message.Contains("Unknown op"))
        {
            throw new CliException("The guest helper is too old for this. Run 'terminal-windows update-helper NAME' first.");
        }
    }

    /// <summary>
    /// Pre-approves the in-VM desktop MCP tools (screenshot, click, type...) so Claude does not stop for
    /// permission on every GUI step. Shell commands and file edits keep Claude Code's normal prompts.
    /// Merges into an existing settings.json and writes it without a BOM.
    /// </summary>
    public const string ClaudeSettingsScript = """
        $p = Join-Path $env:USERPROFILE '.claude\settings.json'
        New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
        $s = if (Test-Path $p) { Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json } else { New-Object PSObject }
        if (-not $s.PSObject.Properties['permissions']) { $s | Add-Member -NotePropertyName permissions -NotePropertyValue (New-Object PSObject) }
        if (-not $s.permissions.PSObject.Properties['allow']) { $s.permissions | Add-Member -NotePropertyName allow -NotePropertyValue @() }
        $s.permissions.allow = @(@($s.permissions.allow) + 'mcp__desktop' | Where-Object { $_ } | Select-Object -Unique)
        [IO.File]::WriteAllText($p, ($s | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
        'allow: ' + ($s.permissions.allow -join ', ')
        """;

    public static string GuestBriefText()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GUEST_BRIEF.md") ?? throw new CliException("guest brief resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static string GuestSkillText()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GUEST_SKILL.md") ?? throw new CliException("guest skill resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>An AI agent CLI that agent-install can set up inside the guest.</summary>
    public sealed record AgentProfile(
        string Id,
        string DisplayName,
        string Command,
        string ProbeUrl,
        IReadOnlyList<(string Title, string Script, int Timeout)> InstallSteps,
        string InstructionsFile,  // relative to the guest USERPROFILE
        bool InstructionsAsSkill, // true: write the skill as-is (with front matter); false: plain instructions file
        string SignInNote);

    public static readonly string[] AgentIds = { "claude", "codex", "gemini" };

    public static AgentProfile Profile(string id) => id.ToLowerInvariant() switch
    {
        "claude" => new("claude", "Claude Code", "claude", "https://claude.ai",
            new[] { GitStep, ClaudeStep, ("Register the desktop MCP server (screenshot / click / type inside this VM)",
                $"""
                claude mcp remove --scope user desktop 2>$null | Out-Null
                claude mcp add --scope user desktop -- "{GuestHelperPath}" mcp
                claude mcp list
                """, 120) },
            @".claude\skills\terminal-windows-desktop\SKILL.md", true,
            "Sign in when Claude Code asks: open the shown URL in your browser and paste the code back."),
        "codex" => new("codex", "OpenAI Codex CLI", "codex", "https://api.openai.com",
            new[] { NodeStep, NpmStep("@openai/codex", "codex"), ("Register the desktop MCP server in ~/.codex/config.toml",
                $$"""
                $dir = Join-Path $env:USERPROFILE '.codex'
                New-Item -ItemType Directory -Force $dir | Out-Null
                $cfg = Join-Path $dir 'config.toml'
                $text = if (Test-Path $cfg) { Get-Content $cfg -Raw } else { '' }
                if ($text -notmatch '(?m)^\[mcp_servers\.desktop\]') {
                  $block = "`n[mcp_servers.desktop]`ncommand = '{{GuestHelperPath}}'`nargs = [""mcp""]`n"
                  [IO.File]::AppendAllText($cfg, $block, (New-Object Text.UTF8Encoding $false))
                  'added [mcp_servers.desktop]'
                } else { 'already configured' }
                """, 60) },
            @".codex\AGENTS.md", false,
            "Sign in when Codex asks (ChatGPT account or OPENAI_API_KEY). Usage may be billed to that account."),
        "gemini" => new("gemini", "Google Gemini CLI", "gemini", "https://generativelanguage.googleapis.com",
            new[] { NodeStep, NpmStep("@google/gemini-cli", "gemini"), ("Register the desktop MCP server in ~/.gemini/settings.json",
                $$"""
                $dir = Join-Path $env:USERPROFILE '.gemini'
                New-Item -ItemType Directory -Force $dir | Out-Null
                $file = Join-Path $dir 'settings.json'
                $s = if (Test-Path $file) { Get-Content $file -Raw | ConvertFrom-Json } else { New-Object psobject }
                if (-not $s.PSObject.Properties['mcpServers']) { $s | Add-Member -NotePropertyName mcpServers -NotePropertyValue (New-Object psobject) }
                $server = [pscustomobject]@{ command = '{{GuestHelperPath}}'; args = @('mcp') }
                $s.mcpServers | Add-Member -NotePropertyName desktop -NotePropertyValue $server -Force
                [IO.File]::WriteAllText($file, ($s | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
                'mcpServers.desktop registered'
                """, 60) },
            @".gemini\GEMINI.md", false,
            "Sign in when Gemini CLI asks (Google account or GEMINI_API_KEY). Usage limits depend on that account."),
        _ => throw new UsageException($"Unknown agent '{id}'. Use one of: {string.Join(", ", AgentIds)}"),
    };

    private static (string, string, int) NpmStep(string package, string command) =>
        ($"Install {package} (npm)",
            $$"""
            $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
            if (-not (Get-Command {{command}} -ErrorAction SilentlyContinue)) {
              npm install -g {{package}}
              if ($LASTEXITCODE -ne 0) { throw "npm install failed ($LASTEXITCODE)" }
            }
            $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
            if (-not (Get-Command {{command}} -ErrorAction SilentlyContinue)) { throw '{{command}} was not found after installing' }
            '{{command}} ' + (& {{command}} --version)
            """, 1500);

    private static readonly (string, string, int) NodeStep = ("Install Node.js LTS (needed by npm-based agents)",
        """
        if (Get-Command node -ErrorAction SilentlyContinue) { 'node already installed: ' + (node --version); exit 0 }
        try { Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction SilentlyContinue } catch {}
        if (Get-Command winget -ErrorAction SilentlyContinue) {
          winget install --id OpenJS.NodeJS.LTS -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
          if ($LASTEXITCODE -eq 0) { 'installed with winget'; exit 0 }
        }
        # Fallback: the official MSI of the newest LTS release from nodejs.org.
        $v = (Invoke-RestMethod -UseBasicParsing https://nodejs.org/dist/index.json | Where-Object { $_.lts } | Select-Object -First 1).version
        $msi = Join-Path $env:TEMP "node-$v-x64.msi"
        Invoke-WebRequest -UseBasicParsing "https://nodejs.org/dist/$v/node-$v-x64.msi" -OutFile $msi
        $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$msi`"", '/qn', '/norestart' -Wait -PassThru
        'msiexec exit ' + $p.ExitCode
        exit $p.ExitCode
        """, 1500);

    private static readonly (string, string, int) ClaudeStep = ("Install Claude Code (official installer)",
        """
        if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { irm https://claude.ai/install.ps1 | iex }
        # The installer may only print "not in your PATH": add %USERPROFILE%\.local\bin to the user PATH.
        $bin = Join-Path $env:USERPROFILE '.local\bin'
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ((Test-Path (Join-Path $bin 'claude.exe')) -and (($userPath -split ';') -notcontains $bin)) {
          [Environment]::SetEnvironmentVariable('Path', (@($userPath, $bin) | Where-Object { $_ }) -join ';', 'User')
          'added ' + $bin + ' to the user PATH'
        }
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { throw 'claude.exe was not found after installing' }
        'claude ' + (claude --version)
        """, 1500);

    private static string NetworkCheck(string url) =>
        $$"""
        try { $r = Invoke-WebRequest -UseBasicParsing -Method Head {{url}} -TimeoutSec 30; 'reachable (HTTP ' + [int]$r.StatusCode + ')' }
        catch { if ($_.Exception.Response) { 'reachable (HTTP ' + [int]$_.Exception.Response.StatusCode + ')' } else { throw "No internet access from the VM (offline mode?): $_" } }
        """;

    /// <summary>All steps for one agent: network check, then the agent's own steps.</summary>
    public static IReadOnlyList<(string Title, string Script, int Timeout)> InstallSteps(string agentId)
    {
        var p = Profile(agentId);
        return new[] { ("Check network access", NetworkCheck(p.ProbeUrl), 60) }.Concat(p.InstallSteps).ToList();
    }

    /// <summary>Writes the agent's instructions file in the guest (base64, so no quoting issues).</summary>
    public static string InstructionsScript(AgentProfile p, string skillText)
    {
        var text = p.InstructionsAsSkill ? skillText : StripFrontMatter(skillText);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return $$"""
            $path = Join-Path $env:USERPROFILE '{{p.InstructionsFile}}'
            New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
            $text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{b64}}'))
            $marker = '<!-- terminal-windows-desktop -->'
            if ({{(p.InstructionsAsSkill ? "$true" : "$false")}}) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false)) }
            else {
              # Keep the person's own instructions: replace only our section.
              $old = if (Test-Path $path) { Get-Content $path -Raw } else { '' }
              $i = $old.IndexOf($marker)
              if ($i -ge 0) { $old = $old.Substring(0, $i).TrimEnd() }
              [IO.File]::WriteAllText($path, ($old + "`n`n" + $marker + "`n" + $text).TrimStart(), (New-Object Text.UTF8Encoding $false))
            }
            'wrote ' + $path
            """;
    }

    private static string StripFrontMatter(string md)
    {
        if (!md.StartsWith("---")) return md;
        int end = md.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? md : md[(md.IndexOf('\n', end + 1) + 1)..];
    }

    private static readonly (string, string, int) GitStep = ("Install Git for Windows (Claude Code on Windows needs Git Bash)",
            """
            if (Get-Command git -ErrorAction SilentlyContinue) { 'git already installed: ' + (git --version); exit 0 }
            try { Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction SilentlyContinue } catch {}
            $winget = Get-Command winget -ErrorAction SilentlyContinue
            if ($winget) {
              winget install --id Git.Git -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
              if ($LASTEXITCODE -eq 0) { 'installed with winget'; exit 0 }
            }
            # Fallback: the official installer from the Git for Windows GitHub releases.
            $rel = Invoke-RestMethod -UseBasicParsing https://api.github.com/repos/git-for-windows/git/releases/latest
            $asset = $rel.assets | Where-Object { $_.name -match '^Git-[\d.]+-64-bit\.exe$' } | Select-Object -First 1
            if (-not $asset) { throw 'Git for Windows installer not found in the latest release' }
            $exe = Join-Path $env:TEMP $asset.name
            Invoke-WebRequest -UseBasicParsing $asset.browser_download_url -OutFile $exe
            $p = Start-Process $exe -ArgumentList '/VERYSILENT','/NORESTART','/SP-' -Wait -PassThru
            'installer exit ' + $p.ExitCode
            exit $p.ExitCode
            """, 1500);

    public static async Task<int> InstallAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        bool yes = a.Flag("yes");
        var agent = Profile(a.Opt("agent") ?? "claude");
        a.RejectUnknown();
        var steps = InstallSteps(agent.Id);
        if (!yes)
        {
            Console.WriteLine($"agent-install will set up {agent.DisplayName} inside {m.Name} (nothing is changed on this PC):");
            foreach (var (title, _, _) in steps) Console.WriteLine("  - " + title);
            Console.WriteLine($"  - Write the in-VM instructions to %USERPROFILE%\\{agent.InstructionsFile}");
            Console.WriteLine();
            Console.WriteLine("It downloads from the internet inside the VM and accepts the winget source/package agreements.");
            Console.WriteLine($"{agent.DisplayName}'s own terms are shown when you sign in. {agent.SignInNote}");
            Console.WriteLine($"Run again with --yes to proceed:  terminal-windows agent-install {m.Name} --agent {agent.Id} --yes");
            return 2;
        }

        await using var c = await GuestClient.ConnectAsync(m);
        var all = steps.Append(("Write the in-VM instructions", InstructionsScript(agent, GuestSkillText()), 60)).ToList();
        if (agent.Id == "claude")
        {
            // CLAUDE.md is always loaded (a skill only when it looks relevant): the short brief goes there.
            all.Add(("Write the always-on brief (~/.claude/CLAUDE.md)",
                InstructionsScript(agent with { InstructionsFile = @".claude\CLAUDE.md", InstructionsAsSkill = false }, GuestBriefText()), 60));
            all.Add(("Allow the desktop tools without asking each time (~/.claude/settings.json)", ClaudeSettingsScript, 60));
        }
        foreach (var (title, script, timeout) in all)
        {
            Console.Error.WriteLine($"==> {title}");
            var r = await RunScriptAsync(c, script, timeout);
            var outText = (r["stdout"]?.GetValue<string>() ?? "").Trim();
            var errText = (r["stderr"]?.GetValue<string>() ?? "").Trim();
            if (outText.Length > 0) Console.Error.WriteLine(Indent(outText));
            int code = r["exitCode"]?.GetValue<int>() ?? -1;
            if (code != 0 || r["timedOut"]?.GetValue<bool>() == true)
            {
                if (errText.Length > 0) Console.Error.WriteLine(Indent(errText));
                throw new CliException($"Step failed ({title}), exit code {code}. Inspect it with: terminal-windows term {m.Name}");
            }
        }

        m.Agent = agent.Id;
        MachineStore.Save(m);
        Console.WriteLine();
        Console.WriteLine($"{agent.DisplayName} is installed inside {m.Name}. Next:");
        Console.WriteLine($"  1. terminal-windows snapshot {m.Name} --name {agent.Id}-ready");
        Console.WriteLine($"  2. terminal-windows {(agent.Id is "claude" or "codex" or "gemini" ? agent.Id : "term")} {m.Name}");
        Console.WriteLine("     " + agent.SignInNote);
        Console.WriteLine("     Your sign-in is stored inside the VM, where the agent can read it.");
        return 0;
    }

    public static Task<int> CodexAsync(ParsedArgs a) => OpenTerminalAsync(a, defaultCommand: "codex", prefix: "codex");
    public static Task<int> GeminiAsync(ParsedArgs a) => OpenTerminalAsync(a, defaultCommand: "gemini", prefix: "gemini");

    private static string Indent(string s) => string.Join('\n', s.Split('\n').Select(l => "    " + l.TrimEnd('\r')));
}

/// <summary>
/// Attaches this console to a pseudo console in the guest: raw VT input goes up, the program's VT
/// output comes down. Ctrl+] detaches (and closes the guest program).
/// </summary>
public sealed class GuestTerminalClient
{
    private readonly GuestClient _client;
    public GuestTerminalClient(GuestClient client) => _client = client;

    public async Task<int> RunAsync(string command, string? cwd)
    {
        using var console = new ConsoleModeScope();
        var stdout = Console.OpenStandardOutput();
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int ptyId = -1;
        // The guest can send output (and even the exit) before the pty_open reply tells us the id:
        // keep such early events and replay them once the id is known.
        var early = new List<Protocol.Message>();
        var gate = new object();

        void Handle(Protocol.Message msg)
        {
            var data = msg.Json["data"] as JsonObject;
            if (msg.Event == "pty_output" && msg.Attachments.Count == 1)
            {
                lock (stdout) { stdout.Write(msg.Attachments[0]); stdout.Flush(); }
            }
            else if (msg.Event == "pty_exit")
            {
                done.TrySetResult(data?["exitCode"]?.GetValue<int>() ?? 0);
            }
        }

        _client.Rpc.EventReceived += msg =>
        {
            if (msg.Event is not ("pty_output" or "pty_exit")) return;
            var id = (msg.Json["data"] as JsonObject)?["id"]?.GetValue<int>();
            lock (gate) // also keeps replayed early output ahead of later output
            {
                if (ptyId < 0) { early.Add(msg); return; }
                if (id == ptyId) Handle(msg);
            }
        };
        _client.Rpc.Disconnected += _ => done.TrySetResult(-1);

        var (cols, rows) = (Console.WindowWidth, Console.WindowHeight);
        JsonObject opened;
        try
        {
            var args = new JsonObject { ["command"] = command, ["cols"] = cols, ["rows"] = rows };
            if (cwd is not null) args["cwd"] = cwd;
            opened = await _client.CallAsync("pty_open", args, TimeSpan.FromSeconds(30));
        }
        catch (RpcException ex) when (ex.Message.Contains("Unknown op"))
        {
            throw new CliException("The guest helper is too old for terminals. Run 'terminal-windows update-helper NAME' first.");
        }
        WriteStatus(stdout, $"[terminal-windows] connected to the VM: {command} — {Agent.DetachHint}");
        lock (gate)
        {
            ptyId = opened["id"]!.GetValue<int>();
            foreach (var m in early.Where(m => (m.Json["data"] as JsonObject)?["id"]?.GetValue<int>() == ptyId)) Handle(m);
            early.Clear();
        }

        // stdin pump on its own thread (console reads cannot be cancelled).
        var input = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var pump = new Thread(() =>
        {
            using var s = Console.OpenStandardInput();
            var b = new byte[4096];
            try
            {
                int n;
                while ((n = s.Read(b, 0, b.Length)) > 0) input.Writer.TryWrite(b[..n]);
            }
            catch { }
            input.Writer.TryComplete();
        }) { IsBackground = true, Name = "stdin" };
        pump.Start();

        bool detached = false;
        while (!done.Task.IsCompleted)
        {
            if (Console.WindowWidth != cols || Console.WindowHeight != rows)
            {
                (cols, rows) = (Console.WindowWidth, Console.WindowHeight);
                try { await _client.CallAsync("pty_resize", new JsonObject { ["id"] = ptyId, ["cols"] = cols, ["rows"] = rows }); } catch { }
            }
            using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            byte[]? chunk = null;
            try
            {
                if (await input.Reader.WaitToReadAsync(wait.Token)) input.Reader.TryRead(out chunk);
                else break;
            }
            catch (OperationCanceledException) { continue; }
            if (chunk is null) continue;

            int detachAt = Array.IndexOf(chunk, (byte)0x1D); // Ctrl+]
            if (detachAt >= 0)
            {
                chunk = chunk[..detachAt];
                detached = true;
            }
            if (chunk.Length > 0)
            {
                try
                {
                    await _client.Rpc.RequestAsync("pty_input", new JsonObject { ["id"] = ptyId }, new ReadOnlyMemory<byte>[] { chunk }, TimeSpan.FromSeconds(15));
                }
                catch (Exception ex) when (ex is RpcException or TimeoutException) { break; }
            }
            if (detached)
            {
                try { await _client.CallAsync("pty_close", new JsonObject { ["id"] = ptyId }); } catch { }
                break;
            }
        }

        int code = done.Task.IsCompleted ? await done.Task : 0;
        WriteStatus(stdout, detached ? "[terminal-windows] detached; the program in the VM was closed."
            : $"[terminal-windows] the program in the VM exited ({code}).");
        return code < 0 ? 1 : code;
    }

    private static void WriteStatus(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes($"\r\n\u001b[7m {text} \u001b[0m\r\n");
        lock (s) { s.Write(bytes); s.Flush(); }
    }
}
