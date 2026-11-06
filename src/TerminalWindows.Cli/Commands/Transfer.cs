using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Commands;

public static class Transfer
{
    public static async Task<int> PullAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        var guestPath = a.PosOrNull(1);
        var to = a.Opt("to");
        bool sync = a.Flag("sync-outbox");
        a.RejectUnknown();

        if (sync)
        {
            Console.Error.WriteLine($"Syncing {m.Name}'s Outbox to {Path.Combine(MachineStore.OutboxDir(m.Name), "sync")} (Ctrl+C to stop)...");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            await SyncOutboxLoopAsync(m, cts.Token);
            return 0;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dest = to is null ? Path.Combine(MachineStore.OutboxDir(m.Name), stamp) : Path.Combine(Path.GetFullPath(to), stamp);
        await using var c = await GuestClient.ConnectAsync(m);
        var files = await c.PullAsync(guestPath, dest, TransferLimits.Default, s => Console.Error.WriteLine("  " + s));
        Output.Json(new JsonObject
        {
            ["hostFolder"] = dest,
            ["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["guest"] = f.GuestPath, ["host"] = f.HostPath, ["size"] = f.Size, ["sha256"] = f.Sha256 }).ToArray()),
        });
        return 0;
    }

    public static async Task<int> PushAsync(ParsedArgs a)
    {
        var m = MachineStore.Load(a.Machine());
        var hostPath = a.Pos(1, "HOST_PATH");
        var to = a.Opt("to");
        a.RejectUnknown();
        await using var c = await GuestClient.ConnectAsync(m);
        var written = await c.PushAsync(hostPath, to, TransferLimits.Default, s => Console.Error.WriteLine("  " + s));
        Output.Json(new JsonObject { ["guestFiles"] = new JsonArray(written.Select(w => (JsonNode)w).ToArray()) });
        return 0;
    }

    /// <summary>
    /// --sync-outbox: copies files from the guest Outbox once they stop changing (same size and
    /// mtime for two polls ~3 s apart). Files land in machines\NAME\outbox\sync\ and are never overwritten.
    /// </summary>
    public static async Task SyncOutboxLoopAsync(MachineConfig m, CancellationToken ct)
    {
        var dest = Path.Combine(MachineStore.OutboxDir(m.Name), "sync");
        var seen = new Dictionary<string, (long Size, long Mtime, int Stable)>();
        var copied = new Dictionary<string, (long Size, long Mtime)>();
        GuestClient? c = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                c ??= await GuestClient.ConnectAsync(m);
                var listing = await c.CallAsync("file_list", new JsonObject { ["root"] = "outbox" });
                var baseDir = listing["base"]!.GetValue<string>();
                foreach (var e in listing["entries"]!.AsArray())
                {
                    var rel = SafePath.NormalizeRelative(e!["path"]!.GetValue<string>());
                    long size = e["size"]!.GetValue<long>(), mtime = e["mtime"]!.GetValue<long>();
                    if (size > ProtocolConstants.DefaultMaxFileBytes) continue;
                    if (copied.TryGetValue(rel, out var done) && done == (size, mtime)) continue;
                    if (seen.TryGetValue(rel, out var prev) && prev.Size == size && prev.Mtime == mtime)
                    {
                        if (prev.Stable + 1 >= 2)
                        {
                            var f = await c.PullOneAsync(baseDir, rel, size, dest);
                            copied[rel] = (size, mtime);
                            Console.Error.WriteLine($"[sync] {rel} -> {f.HostPath}");
                        }
                        else seen[rel] = (size, mtime, prev.Stable + 1);
                    }
                    else seen[rel] = (size, mtime, 0);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine("[sync] " + ex.Message);
                if (c is not null) await c.DisposeAsync();
                c = null;
            }
            try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
        }
        if (c is not null) await c.DisposeAsync();
    }
}

public static class Notes
{
    public static string FormatEntry(string text) =>
        $"\n## {DateTime.Now:yyyy-MM-dd HH:mm}\n\n{text.TrimEnd()}\n";

    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var name = a.Machine();
        MachineStore.Load(name);
        bool edit = a.Flag("edit");
        a.RejectUnknown();
        var path = MachineStore.NotesPath(name);
        if (!File.Exists(path)) await File.WriteAllTextAsync(path, $"# Work notes: {name}\n");
        if (edit)
        {
            var editor = Environment.GetEnvironmentVariable("EDITOR") ?? "notepad.exe";
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(editor) { ArgumentList = { path }, UseShellExecute = false });
            if (p is not null) await p.WaitForExitAsync();
            return 0;
        }
        Console.Write(await File.ReadAllTextAsync(path));
        return 0;
    }
}

public static class Output
{
    public static readonly System.Text.Json.JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep 日本語 and quotes readable
    };

    public static readonly System.Text.Json.JsonSerializerOptions Compact = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Json(JsonNode node) => Console.WriteLine(node.ToJsonString(Pretty));
}
