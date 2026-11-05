using System.Security.Cryptography;

namespace TerminalWindows.Protocol;

/// <summary>
/// Validation for file names that cross the host/guest boundary. Both sides treat names from the
/// peer as untrusted: no traversal (<c>..</c>), no absolute or drive-qualified paths, no UNC,
/// no alternate data streams, no device names.
/// </summary>
public static class SafePath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Validates a relative path and returns it normalized with '/' separators.</summary>
    public static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Empty path");
        if (path.Length > 1024) throw new ArgumentException("Path too long");
        var p = path.Replace('\\', '/');
        if (p.StartsWith('/')) throw new ArgumentException($"Absolute paths are not allowed: {path}");
        if (p.Contains(':')) throw new ArgumentException($"Drive letters / streams are not allowed: {path}");

        var segments = p.Split('/');
        foreach (var s in segments)
        {
            if (s.Length == 0) throw new ArgumentException($"Empty path segment: {path}");
            if (s == "." || s == "..") throw new ArgumentException($"Path traversal is not allowed: {path}");
            if (s.EndsWith('.') || s.EndsWith(' ')) throw new ArgumentException($"Segment may not end with '.' or space: {path}");
            if (s.IndexOfAny(InvalidChars) >= 0) throw new ArgumentException($"Invalid character in path: {path}");
            var stem = s.Split('.')[0];
            if (ReservedNames.Contains(stem)) throw new ArgumentException($"Reserved device name: {path}");
        }
        return string.Join('/', segments);
    }

    private static readonly char[] InvalidChars = Enumerable.Range(0, 32).Select(i => (char)i)
        .Concat(new[] { '<', '>', '"', '|', '?', '*' }).ToArray();

    /// <summary>Resolves a validated relative path under <paramref name="root"/> and double-checks containment.</summary>
    public static string ResolveUnder(string root, string relative)
    {
        var rel = NormalizeRelative(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path escapes its root: {relative}");
        return full;
    }

    /// <summary>True if the path itself or any directory between <paramref name="root"/> and it is a reparse point
    /// (symlink / junction). Such entries are never followed.</summary>
    public static bool HasReparsePoint(string root, string fullPath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(fullPath);
        while (current.Length > fullRoot.Length)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            current = Path.GetDirectoryName(current) ?? fullRoot;
        }
        return false;
    }

    public static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
