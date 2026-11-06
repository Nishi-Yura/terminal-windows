using System.Text;
using System.Text.Json.Nodes;
using DiscUtils.Iso9660;
using TerminalWindows.Cli.Infrastructure;

namespace TerminalWindows.Cli.Setup;

/// <summary>
/// Builds the small setup ISO attached next to the Windows ISO. Windows Setup picks up
/// autounattend.xml from the root of any removable/optical drive.
/// Layout:
///   autounattend.xml
///   terminal-windows\setup.cmd
///   terminal-windows\TerminalWindowsGuest.exe
///   terminal-windows\guest.json   (token, auto-logon password, resolution, offline flag)
/// The ISO contains secrets: it is created with a current-user-only ACL and deleted once setup finishes.
/// </summary>
public static class PayloadIso
{
    public static JsonObject GuestJson(MachineConfig m, string token, string password) => new()
    {
        ["token"] = token,
        ["port"] = Protocol.ProtocolConstants.GuestPort,
        ["width"] = m.Width,
        ["height"] = m.Height,
        ["offline"] = m.Offline,
        ["locale"] = m.Locale,
        ["user"] = m.User,
        ["password"] = password,
        ["allowedRemote"] = Protocol.ProtocolConstants.NatSubnet,
    };

    public const string SetupCmd = "@echo off\r\n" +
                                   "rem terminal-windows guest setup (runs inside the VM only)\r\n" +
                                   "\"%~dp0TerminalWindowsGuest.exe\" install --source \"%~dp0.\"\r\n";

    public static void Build(string isoPath, string unattendXml, JsonObject guestJson, string helperExe)
    {
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "TWSETUP" };
        builder.AddFile("autounattend.xml", Encoding.UTF8.GetBytes(unattendXml));
        builder.AddFile(@"terminal-windows\setup.cmd", Encoding.ASCII.GetBytes(SetupCmd));
        builder.AddFile(@"terminal-windows\guest.json", Encoding.UTF8.GetBytes(guestJson.ToJsonString()));
        builder.AddFile(@"terminal-windows\TerminalWindowsGuest.exe", File.ReadAllBytes(helperExe));

        Directory.CreateDirectory(Path.GetDirectoryName(isoPath)!);
        File.WriteAllBytes(isoPath, Array.Empty<byte>());
        MachineStore.RestrictToCurrentUser(isoPath);
        using var fs = new FileStream(isoPath, FileMode.Truncate, FileAccess.Write);
        builder.Build(fs);
    }

    /// <summary>Locates the guest helper binary that ships with the CLI.</summary>
    /// <summary>
    /// The shipped terminal-windows.exe carries the guest helper as a resource. It is written once to
    /// %USERPROFILE%\.terminal-windows\bin under a name that includes its hash, so a newer CLI never reuses an
    /// older helper and a helper that is in use (being copied into a VM) is never overwritten.
    /// </summary>
    public static string? ExtractEmbeddedHelper()
    {
        using var res = typeof(PayloadIso).Assembly.GetManifestResourceStream("TerminalWindowsGuest.exe");
        if (res is null) return null;
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray()))[..12].ToLowerInvariant();
        var dir = Path.Combine(MachineStore.Home, "bin", hash);
        var path = Path.Combine(dir, "TerminalWindowsGuest.exe");
        if (File.Exists(path) && new FileInfo(path).Length == ms.Length) return path;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public static string? FindHelperExe()
    {
        var env = Environment.GetEnvironmentVariable("TERMINAL_WINDOWS_GUEST_EXE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "guest", "TerminalWindowsGuest.exe"),
                     Path.Combine(baseDir, "TerminalWindowsGuest.exe"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        if (ExtractEmbeddedHelper() is { } embedded) return embedded;

        // Development checkout: <repo>/src/TerminalWindows.Guest/bin/<cfg>/<tfm>/win-x64/publish/
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "TerminalWindows.Guest")))
            dir = dir.Parent;
        if (dir is null) return null;
        var bin = Path.Combine(dir.FullName, "src", "TerminalWindows.Guest", "bin");
        if (!Directory.Exists(bin)) return null;
        return Directory.GetFiles(bin, "TerminalWindowsGuest.exe", SearchOption.AllDirectories)
            .Where(p => p.Contains(Path.DirectorySeparatorChar + "publish" + Path.DirectorySeparatorChar))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
