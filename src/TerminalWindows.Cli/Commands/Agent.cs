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
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        var cwd = a.Opt("cwd");
        a.RejectUnknown();
        var extra = a.Positionals.Skip(1).ToList();
        string command = extra.Count == 0 ? defaultCommand
            : prefix is null ? (extra.Count == 1 ? extra[0] : string.Join(' ', extra.Select(Quote)))
            : prefix + " " + string.Join(' ', extra.Select(Quote));
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new CliException("This command needs an interactive terminal.");

        await using var c = await GuestClient.ConnectAsync(m);
        return await new GuestTerminalClient(c).RunAsync(command, cwd);
    }

    private static string Quote(string s) => s.Length > 0 && s.IndexOfAny(new[] { ' ', '"' }) < 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    public static async Task<int> ExecAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
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

    public static string GuestSkillText()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GUEST_SKILL.md") ?? throw new CliException("guest skill resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>The steps agent-install runs inside the guest (also printed without --yes).</summary>
    public static IReadOnlyList<(string Title, string Script, int Timeout)> InstallSteps(string user) => new (string, string, int)[]
    {
        ("Check network access",
            """
            try { $r = Invoke-WebRequest -UseBasicParsing -Method Head https://claude.ai -TimeoutSec 30; 'reachable (HTTP ' + [int]$r.StatusCode + ')' }
            catch { if ($_.Exception.Response) { 'reachable (HTTP ' + [int]$_.Exception.Response.StatusCode + ')' } else { throw "No internet access from the VM (offline mode?): $_" } }
            """, 60),
        ("Install Git for Windows (Claude Code on Windows needs Git Bash)",
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
            """, 1500),
        ("Install Claude Code (official installer)",
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
            """, 1500),
        ("Check Claude Code",
            "claude --version", 120),
        ("Register the desktop MCP server (screenshot / click / type inside this VM)",
            $"""
            claude mcp remove --scope user desktop 2>$null | Out-Null
            claude mcp add --scope user desktop -- "{GuestHelperPath}" mcp
            claude mcp list
            """, 120),
    };

    public static async Task<int> InstallAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Pos(0, "NAME"));
        bool yes = a.Flag("yes");
        a.RejectUnknown();
        var steps = InstallSteps(m.User);
        if (!yes)
        {
            Console.WriteLine($"agent-install will run these steps inside {m.Name} (nothing is changed on this PC):");
            foreach (var (title, _, _) in steps) Console.WriteLine("  - " + title);
            Console.WriteLine($"  - Copy the guest skill to C:\\Users\\{m.User}\\.claude\\skills\\terminal-windows-desktop\\SKILL.md");
            Console.WriteLine();
            Console.WriteLine("It downloads from the internet inside the VM and accepts the winget source/package agreements");
            Console.WriteLine("(Git for Windows is GPLv2). Claude Code's own terms are shown when you sign in.");
            Console.WriteLine($"Run again with --yes to proceed:  terminal-windows agent-install {m.Name} --yes");
            return 2;
        }

        await using var c = await GuestClient.ConnectAsync(m);
        foreach (var (title, script, timeout) in steps)
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

        Console.Error.WriteLine("==> Install the guest skill");
        var tmp = Path.Combine(Path.GetTempPath(), "tw-guest-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "terminal-windows-desktop"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "terminal-windows-desktop", "SKILL.md"), GuestSkillText(), new UTF8Encoding(false));
            // Ask the guest for its real profile folder instead of assuming C:\Users\<user>.
            var profile = (await RunScriptAsync(c, "$env:USERPROFILE", 60))["stdout"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(profile)) throw new CliException("Could not read USERPROFILE in the guest.");
            var skillsDir = Path.Combine(profile, ".claude", "skills");
            await RunScriptAsync(c, $"Remove-Item -Recurse -Force '{skillsDir}\\terminal-windows-desktop' -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force '{skillsDir}' | Out-Null", 60);
            await c.PushAsync(Path.Combine(tmp, "terminal-windows-desktop"), skillsDir, TransferLimits.Default);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"Claude Code is installed inside {m.Name}. Next:");
        Console.WriteLine($"  1. terminal-windows snapshot {m.Name} --name agent-ready");
        Console.WriteLine($"  2. terminal-windows claude {m.Name}");
        Console.WriteLine("     Sign in yourself when Claude Code asks (open the shown URL in your browser and paste the code back).");
        Console.WriteLine("     Your sign-in is stored inside the VM, where the agent can read it.");
        return 0;
    }

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
