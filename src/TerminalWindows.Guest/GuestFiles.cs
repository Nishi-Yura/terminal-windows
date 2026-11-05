using System.Text.Json.Nodes;
using TerminalWindows.Protocol;

namespace TerminalWindows.Guest;

/// <summary>
/// File transfer over the control channel (no shared folders). Paths that come from the host are
/// either the aliases "outbox"/"inbox" or absolute guest paths chosen by the host operator; names of
/// individual entries are always validated relative paths. Reparse points are never followed.
/// </summary>
public sealed class GuestFiles
{
    private readonly string _desktop;
    private readonly long _maxFile;

    public GuestFiles(string desktop, long maxFileBytes = ProtocolConstants.DefaultMaxFileBytes)
    {
        _desktop = desktop;
        _maxFile = maxFileBytes;
    }

    /// <summary>Keeps a listing well under the 4 MB JSON message limit.</summary>
    public const int MaxEntries = 10_000;

    public string Outbox => Path.Combine(_desktop, "Outbox");
    public string Inbox => Path.Combine(_desktop, "Inbox");

    public string ResolveRoot(string? root)
    {
        var r = string.IsNullOrEmpty(root) ? "outbox" : root;
        if (r.Equals("outbox", StringComparison.OrdinalIgnoreCase)) return Outbox;
        if (r.Equals("inbox", StringComparison.OrdinalIgnoreCase)) return Inbox;
        if (!Path.IsPathFullyQualified(r)) throw new ArgumentException($"Guest path must be absolute or 'outbox'/'inbox': {r}");
        return Path.GetFullPath(r);
    }

    /// <summary>Lists files under root (recursively). If root is a file, lists just that file.</summary>
    public JsonObject List(string? rootArg)
    {
        var root = ResolveRoot(rootArg);
        var entries = new JsonArray();
        string baseDir;
        if (File.Exists(root))
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to follow a symbolic link");
            baseDir = Path.GetDirectoryName(root)!;
            var fi = new FileInfo(root);
            entries.Add(Entry(fi.Name, fi));
        }
        else if (Directory.Exists(root))
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to follow a junction");
            baseDir = root;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var info in dir.EnumerateFileSystemInfos())
                {
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue; // symlinks / junctions: skip
                    if (info is DirectoryInfo sub) stack.Push(sub);
                    else if (info is FileInfo fi && !fi.Name.EndsWith(".twpart", StringComparison.OrdinalIgnoreCase))
                    {
                        if (entries.Count >= MaxEntries)
                            throw new IOException($"More than {MaxEntries} files under {root}; pick a narrower folder.");
                        entries.Add(Entry(Path.GetRelativePath(root, fi.FullName).Replace('\\', '/'), fi));
                    }
                }
            }
        }
        else
        {
            throw new FileNotFoundException($"Not found in guest: {root}");
        }
        return new JsonObject { ["base"] = baseDir, ["entries"] = entries };
    }

    private static JsonObject Entry(string rel, FileInfo fi) => new()
    {
        ["path"] = rel,
        ["size"] = fi.Length,
        ["mtime"] = fi.LastWriteTimeUtc.Ticks,
    };

    private string ResolveEntry(string baseDir, string rel)
    {
        var full = SafePath.ResolveUnder(baseDir, rel);
        if (SafePath.HasReparsePoint(baseDir, full)) throw new IOException("Refusing to follow a symbolic link or junction");
        return full;
    }

    public byte[] Read(string baseDir, string rel, long offset, int length)
    {
        length = Math.Clamp(length, 0, ProtocolConstants.FileChunkBytes * 4);
        var full = ResolveEntry(baseDir, rel);
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset < 0 || offset > fs.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        fs.Position = offset;
        var buf = new byte[(int)Math.Min(length, fs.Length - offset)];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n == 0) break;
            read += n;
        }
        return read == buf.Length ? buf : buf[..read];
    }

    public string Hash(string baseDir, string rel) => SafePath.Sha256File(ResolveEntry(baseDir, rel));

    /// <summary>Writes one chunk into "&lt;target&gt;.twpart" under the destination directory.</summary>
    public void WriteChunk(string destDir, string rel, long offset, long total, byte[] data)
    {
        if (total < 0 || total > _maxFile) throw new IOException($"File exceeds the per-file limit ({_maxFile} bytes)");
        if (offset < 0 || offset + data.Length > total) throw new IOException("Chunk is outside the declared file size");
        var full = ResolveEntry(destDir, rel) + ".twpart";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (SafePath.HasReparsePoint(destDir, Path.GetDirectoryName(full)!)) throw new IOException("Refusing to write through a junction");
        using var fs = new FileStream(full, offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None);
        if (fs.Length != offset) throw new IOException($"Out-of-order chunk (have {fs.Length}, got offset {offset})");
        fs.Position = offset;
        fs.Write(data);
    }

    /// <summary>Verifies SHA-256 and moves the part file into place without overwriting existing files.</summary>
    public string Commit(string destDir, string rel, string sha256)
    {
        var target = ResolveEntry(destDir, rel);
        var part = target + ".twpart";
        if (!File.Exists(part))
        {
            // Zero-length files never receive a chunk.
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            File.WriteAllBytes(part, Array.Empty<byte>());
        }
        var actual = SafePath.Sha256File(part);
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new IOException($"SHA-256 mismatch for {rel}");
        }
        var final = UniqueName(target);
        File.Move(part, final);
        return final;
    }

    public static string UniqueName(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
