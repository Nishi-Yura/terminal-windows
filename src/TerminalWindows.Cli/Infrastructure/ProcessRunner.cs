using System.Diagnostics;
using System.Text;

namespace TerminalWindows.Cli.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan? timeout = null, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public static bool Verbose { get; set; } = Environment.GetEnvironmentVariable("TERMINAL_WINDOWS_VERBOSE") == "1";

    public async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (Verbose) Console.Error.WriteLine($"$ {Path.GetFileName(file)} {string.Join(' ', args.Select(Quote))}");

        using var p = Process.Start(psi) ?? throw new CliException($"Could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(10));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new CliException($"{Path.GetFileName(file)} timed out");
        }
        return new ProcessResult(p.ExitCode, await stdout, await stderr);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
