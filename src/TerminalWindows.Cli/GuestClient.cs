using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli;

public sealed record TransferLimits(long MaxFileBytes, long MaxTotalBytes)
{
    public static readonly TransferLimits Default = new(ProtocolConstants.DefaultMaxFileBytes, ProtocolConstants.DefaultMaxTotalBytes);
}

public sealed record PulledFile(string GuestPath, string HostPath, long Size, string Sha256);

/// <summary>
/// Host-side client of the guest helper. Data from the guest is treated as untrusted:
/// sizes and names are validated, files are written under the destination only, never executed or extracted.
/// </summary>
public sealed class GuestClient : IAsyncDisposable
{
    private readonly RpcClient _rpc;

    private GuestClient(RpcClient rpc) => _rpc = rpc;

    public RpcClient Rpc => _rpc;
    public JsonObject? Hello => _rpc.HelloInfo;

    public static async Task<GuestClient> ConnectAsync(MachineConfig m, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            var rpc = await RpcClient.ConnectAsync("127.0.0.1", m.HostPort, m.Token, timeout ?? TimeSpan.FromSeconds(5), ct);
            return new GuestClient(rpc);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException or RpcException or OperationCanceledException)
        {
            throw new CliException($"Cannot reach the guest helper of '{m.Name}' (127.0.0.1:{m.HostPort}): {ex.Message}. " +
                                   "Is the VM running and logged on? Check 'terminal-windows status " + m.Name + "'.");
        }
    }

    /// <summary>Reachable and (optionally) reporting setup phase "complete".</summary>
    public static async Task<bool> IsReadyAsync(MachineConfig m, bool requireSetupComplete)
    {
        try
        {
            await using var c = await ConnectAsync(m, TimeSpan.FromSeconds(3));
            if (!requireSetupComplete) return true;
            var status = await c.CallAsync("status", timeout: TimeSpan.FromSeconds(10));
            return status["setup"]?["phase"]?.GetValue<string>() == "complete";
        }
        catch (Exception ex) when (ex is CliException or RpcException or TimeoutException or IOException)
        {
            return false;
        }
    }

    public static async Task<bool> IsReachableAsync(MachineConfig m)
    {
        try
        {
            await using var c = await ConnectAsync(m, TimeSpan.FromSeconds(3));
            return true;
        }
        catch (CliException)
        {
            return false;
        }
    }

    public async Task<JsonObject> CallAsync(string op, JsonObject? args = null, TimeSpan? timeout = null)
    {
        var msg = await _rpc.RequestAsync(op, args, timeout: timeout);
        return msg.Result as JsonObject ?? new JsonObject();
    }

    public async Task<(JsonObject Info, byte[] Png)> ScreenshotAsync(int? maxWidth = null, int? maxHeight = null)
    {
        var args = new JsonObject();
        if (maxWidth.HasValue && maxHeight.HasValue)
        {
            args["max_width"] = maxWidth.Value;
            args["max_height"] = maxHeight.Value;
        }
        var msg = await _rpc.RequestAsync("screenshot", args, timeout: TimeSpan.FromSeconds(20));
        if (msg.Attachments.Count != 1) throw new CliException("Guest returned no image");
        return (msg.Result as JsonObject ?? new JsonObject(), msg.Attachments[0]);
    }

    /// <summary>Copies a guest file or folder (default: the Outbox) into <paramref name="destDir"/> without overwriting.</summary>
    public async Task<List<PulledFile>> PullAsync(string? guestRoot, string destDir, TransferLimits limits, Action<string>? progress = null)
    {
        var listing = await CallAsync("file_list", new JsonObject { ["root"] = guestRoot ?? "outbox" }, TimeSpan.FromMinutes(2));
        var baseDir = listing["base"]?.GetValue<string>() ?? throw new CliException("Bad listing");
        var entries = listing["entries"] as JsonArray ?? new JsonArray();

        long total = 0;
        var plan = new List<(string Rel, long Size)>();
        foreach (var e in entries)
        {
            var rel = SafePath.NormalizeRelative(e!["path"]!.GetValue<string>()); // reject traversal from the guest
            var size = e["size"]!.GetValue<long>();
            if (size < 0 || size > limits.MaxFileBytes) throw new CliException($"{rel}: {size} bytes exceeds the per-file limit ({limits.MaxFileBytes})");
            total += size;
            if (total > limits.MaxTotalBytes) throw new CliException($"Transfer exceeds the total limit ({limits.MaxTotalBytes} bytes)");
            plan.Add((rel, size));
        }

        Directory.CreateDirectory(destDir);
        var result = new List<PulledFile>();
        foreach (var (rel, size) in plan)
            result.Add(await PullOneAsync(baseDir, rel, size, destDir, progress));
        return result;
    }

    public async Task<PulledFile> PullOneAsync(string baseDir, string rel, long size, string destDir, Action<string>? progress = null)
    {
        var target = UniqueName(SafePath.ResolveUnder(destDir, rel));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".twpart";
        progress?.Invoke($"{rel} ({size:N0} bytes)");
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write))
            {
                long offset = 0;
                while (offset < size)
                {
                    var msg = await _rpc.RequestAsync("file_read", new JsonObject
                    {
                        ["base"] = baseDir, ["path"] = rel, ["offset"] = offset, ["length"] = ProtocolConstants.FileChunkBytes,
                    }, timeout: TimeSpan.FromMinutes(1));
                    var data = msg.Attachments.Count == 1 ? msg.Attachments[0] : throw new CliException("Missing chunk");
                    if (data.Length == 0) throw new CliException($"{rel} shrank while copying");
                    if (offset + data.Length > size) throw new CliException($"{rel} grew while copying");
                    await fs.WriteAsync(data);
                    sha.AppendData(data);
                    offset += data.Length;
                }
            }
            var local = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            var remote = (await CallAsync("file_hash", new JsonObject { ["base"] = baseDir, ["path"] = rel }, TimeSpan.FromMinutes(5)))["sha256"]!.GetValue<string>();
            if (!string.Equals(local, remote, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new CliException($"SHA-256 mismatch for {rel} (file changed during transfer?)");
            }
            File.Move(part, target);
            return new PulledFile(rel, target, size, local);
        }
    }

    /// <summary>Sends a host file or folder to the guest (default destination: Inbox).</summary>
    public async Task<List<string>> PushAsync(string hostPath, string? guestDest, TransferLimits limits, Action<string>? progress = null)
    {
        var files = new List<(string Full, string Rel)>();
        if (File.Exists(hostPath))
        {
            files.Add((Path.GetFullPath(hostPath), Path.GetFileName(hostPath)));
        }
        else if (Directory.Exists(hostPath))
        {
            var root = Path.GetFullPath(hostPath);
            var top = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                foreach (var info in stack.Pop().EnumerateFileSystemInfos())
                {
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never follow links
                    if (info is DirectoryInfo d) stack.Push(d);
                    else files.Add((info.FullName, top + "/" + Path.GetRelativePath(root, info.FullName).Replace('\\', '/')));
                }
            }
        }
        else
        {
            throw new CliException($"Not found: {hostPath}");
        }

        long total = 0;
        foreach (var f in files)
        {
            var len = new FileInfo(f.Full).Length;
            if (len > limits.MaxFileBytes) throw new CliException($"{f.Rel} exceeds the per-file limit");
            total += len;
            if (total > limits.MaxTotalBytes) throw new CliException("Transfer exceeds the total limit");
        }

        var dest = guestDest ?? "inbox";
        var written = new List<string>();
        foreach (var (full, relRaw) in files)
        {
            var rel = SafePath.NormalizeRelative(relRaw);
            var size = new FileInfo(full).Length;
            progress?.Invoke($"{rel} ({size:N0} bytes)");
            var buffer = new byte[ProtocolConstants.FileChunkBytes];
            string sha;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long offset = 0;
                int n;
                while ((n = await fs.ReadAsync(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    await _rpc.RequestAsync("file_write", new JsonObject
                    {
                        ["dest"] = dest, ["path"] = rel, ["offset"] = offset, ["total"] = size,
                    }, new ReadOnlyMemory<byte>[] { buffer.AsMemory(0, n) }, TimeSpan.FromMinutes(1));
                    offset += n;
                }
                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            var commit = await CallAsync("file_commit", new JsonObject { ["dest"] = dest, ["path"] = rel, ["sha256"] = sha }, TimeSpan.FromMinutes(5));
            written.Add(commit["path"]?.GetValue<string>() ?? rel);
        }
        return written;
    }

    /// <summary>Pushes a new helper build. The guest installs it only if its SHA-256 matches the value announced here.</summary>
    public async Task<bool> UpdateHelperAsync(string exePath)
    {
        var bytes = await File.ReadAllBytesAsync(exePath);
        var sha = SafePath.Sha256(bytes);
        await CallAsync("update_begin", new JsonObject { ["size"] = bytes.LongLength, ["sha256"] = sha });
        for (long off = 0; off < bytes.Length; off += ProtocolConstants.FileChunkBytes)
        {
            int n = (int)Math.Min(ProtocolConstants.FileChunkBytes, bytes.Length - off);
            await _rpc.RequestAsync("update_chunk", new JsonObject { ["offset"] = off },
                new ReadOnlyMemory<byte>[] { bytes.AsMemory((int)off, n) }, TimeSpan.FromMinutes(1));
        }
        var r = await CallAsync("update_commit");
        return r["accepted"]?.GetValue<bool>() == true;
    }

    public static string UniqueName(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var c = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
    }

    public ValueTask DisposeAsync() => _rpc.DisposeAsync();
}
