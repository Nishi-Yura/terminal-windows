using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using TerminalWindows.Protocol;

namespace TerminalWindows.Guest;

/// <summary>
/// Pseudo-console sessions and one-shot command execution for the host (host -> guest only; the
/// host is the controller). Used to run an AI agent such as Claude Code INSIDE the VM while the person
/// talks to it from the host terminal. Sessions belong to the connection that opened them and are
/// closed when it disconnects.
/// </summary>
public sealed class GuestTerminals
{
    private readonly ConcurrentDictionary<int, (PseudoConsoleProcess Proc, RpcServerSession Owner)> _ptys = new();
    private int _nextId;

    /// <summary>
    /// CreateProcess searches the CALLER's PATH, which for the helper is the PATH at logon. Resolve the
    /// program against the fresh registry PATH instead, and run .cmd/.bat shims through cmd.exe.
    /// </summary>
    public static string ResolveCommand(string commandLine)
    {
        var trimmed = commandLine.TrimStart();
        string exe, rest;
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            exe = end > 0 ? trimmed[1..end] : trimmed.Trim('"');
            rest = end > 0 ? trimmed[(end + 1)..] : "";
        }
        else
        {
            int sp = trimmed.IndexOf(' ');
            exe = sp < 0 ? trimmed : trimmed[..sp];
            rest = sp < 0 ? "" : trimmed[sp..];
        }
        if (Path.IsPathRooted(exe)) return commandLine;

        var block = PseudoConsoleProcess.BuildEnvironmentBlock();
        var path = block.Split('\0').FirstOrDefault(l => l.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))?[5..] ?? "";
        var exts = Path.HasExtension(exe) ? new[] { "" } : new[] { ".exe", ".cmd", ".bat", ".com" };
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), exe + ext); } catch { continue; }
                if (!File.Exists(candidate)) continue;
                var quoted = $"\"{candidate}\"{rest}";
                return candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || candidate.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                    ? $"cmd.exe /c \"{quoted}\""
                    : quoted;
            }
        }
        throw new ArgumentException($"'{exe}' was not found on PATH in the guest. Is it installed? (try 'terminal-windows agent-install')");
    }

    public JsonObject Open(RpcServerSession owner, string command, short cols, short rows, string? cwd)
    {
        var proc = PseudoConsoleProcess.Start(ResolveCommand(command), cols, rows, cwd);
        int id = Interlocked.Increment(ref _nextId);
        _ptys[id] = (proc, owner);
        Log.Write($"pty {id} started: {command} (pid {proc.ProcessId})");
        _ = Task.Run(() => PumpOutputAsync(id, proc, owner));
        _ = Task.Run(async () =>
        {
            int code = await proc.WaitForExitAsync();
            await Task.Delay(500);            // let the last output drain
            proc.ClosePseudoConsoleOnly();    // ends the output stream
            Log.Write($"pty {id} exited with {code}");
            try { await owner.SendEventAsync("pty_exit", new JsonObject { ["id"] = id, ["exitCode"] = code }); } catch { }
            Close(id);
        });
        return new JsonObject { ["id"] = id, ["pid"] = proc.ProcessId };
    }

    private static async Task PumpOutputAsync(int id, PseudoConsoleProcess proc, RpcServerSession owner)
    {
        var buf = new byte[32 * 1024];
        try
        {
            int n;
            while ((n = await proc.Output.ReadAsync(buf)) > 0)
                await owner.SendEventAsync("pty_output", new JsonObject { ["id"] = id }, new ReadOnlyMemory<byte>[] { buf.AsMemory(0, n).ToArray() });
        }
        catch { /* pty closed or peer gone */ }
    }

    private PseudoConsoleProcess Get(int id, RpcServerSession caller) =>
        _ptys.TryGetValue(id, out var p) && p.Owner == caller ? p.Proc : throw new ArgumentException($"No terminal {id} on this connection");

    public void Input(RpcServerSession caller, int id, byte[] data)
    {
        var p = Get(id, caller);
        p.Input.Write(data);
        p.Input.Flush();
    }

    public void Resize(RpcServerSession caller, int id, short cols, short rows) => Get(id, caller).Resize(cols, rows);

    public void Close(RpcServerSession caller, int id)
    {
        Get(id, caller);
        Close(id);
    }

    private void Close(int id)
    {
        if (_ptys.TryRemove(id, out var p)) p.Proc.Dispose();
    }

    public void CloseAll(RpcServerSession owner)
    {
        foreach (var kv in _ptys.Where(kv => kv.Value.Owner == owner).ToList()) Close(kv.Key);
    }

    /// <summary>Runs a PowerShell script and returns its exit code and (truncated) output.</summary>
    public static async Task<JsonObject> ExecAsync(string script, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                     "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script })
            psi.ArgumentList.Add(a);
        // Fresh PATH (tools installed after logon must be found).
        var block = PseudoConsoleProcess.BuildEnvironmentBlock();
        var path = block.Split('\0').FirstOrDefault(l => l.StartsWith("Path=", StringComparison.OrdinalIgnoreCase));
        if (path is not null) psi.Environment["Path"] = path[5..];

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        bool timedOut = false;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { p.Kill(true); } catch { }
            }
        }
        static string Cap(string s) => s.Length > 256 * 1024 ? s[..(256 * 1024)] + "\n…(truncated)" : s;
        async Task<string> Read(Task<string> t) => await Task.WhenAny(t, Task.Delay(5000)) == t ? Cap(t.Result) : "(output still open)";
        return new JsonObject
        {
            ["exitCode"] = timedOut ? -1 : p.ExitCode,
            ["timedOut"] = timedOut,
            ["stdout"] = await Read(stdout),
            ["stderr"] = await Read(stderr),
        };
    }
}
