using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TerminalWindows.Cli.Infrastructure;

public sealed class MachineConfig
{
    public string Name { get; set; } = "";
    public string VmName { get; set; } = "";
    public string Backend { get; set; } = "virtualbox";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int HostPort { get; set; }
    public int Cpus { get; set; } = 4;
    public int MemoryGb { get; set; } = 8;
    public int DiskGb { get; set; } = 64;
    public string DiskBus { get; set; } = "sata";
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 900;
    public string Locale { get; set; } = "ja-JP";
    public string UiLanguage { get; set; } = "ja-JP";
    public string User { get; set; } = "agent";
    public bool Offline { get; set; }
    public bool AutoSnapshot { get; set; }
    public int AutoSnapshotKeep { get; set; } = 5;
    public string? IsoPath { get; set; }
    public string? ClonedFrom { get; set; }
    public bool SetupComplete { get; set; }
    /// <summary>DPAPI (CurrentUser) protected, base64.</summary>
    public string ProtectedToken { get; set; } = "";
    /// <summary>DPAPI (CurrentUser) protected, base64.</summary>
    public string ProtectedPassword { get; set; } = "";

    [JsonIgnore] public string Token => Secrets.Unprotect(ProtectedToken);
    [JsonIgnore] public string Password => Secrets.Unprotect(ProtectedPassword);
}

public static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("terminal-windows/v1");

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string data) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), Entropy, DataProtectionScope.CurrentUser));

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Random password for the guest-only auto-logon account. Never reuse a real password here.</summary>
    public static string NewPassword(int length = 24)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "-_.!#%";
        var all = upper + lower + digits + symbols;
        var chars = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)],
        };
        while (chars.Count < length) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray());
    }
}

public static class MachineStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$");

    /// <summary>
    /// Data root. Default: %USERPROFILE%\.terminal-windows (override: TERMINAL_WINDOWS_HOME).
    /// Deliberately NOT %LOCALAPPDATA% (spec §4.1): when terminal-windows runs under an MSIX-packaged
    /// app (e.g. as an MCP server started by the Claude desktop app), Windows silently redirects AppData
    /// writes into the package's private LocalCache. VirtualBox (outside the package) then cannot see the
    /// setup ISO, and a normal terminal cannot see notes written by the MCP server.
    /// </summary>
    public static string Home =>
        Environment.GetEnvironmentVariable("TERMINAL_WINDOWS_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".terminal-windows");

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder? name);

    /// <summary>True when this process runs with an MSIX package identity (AppData writes are virtualized).</summary>
    public static bool RunningInPackage()
    {
        int len = 0;
        return GetCurrentPackageFullName(ref len, null) != 15700; // APPMODEL_ERROR_NO_PACKAGE
    }

    /// <summary>True if <paramref name="path"/> lies under %LOCALAPPDATA% or %APPDATA%.</summary>
    public static bool IsUnderAppData(string path)
    {
        var full = Path.GetFullPath(path);
        return new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }
            .Select(Environment.GetFolderPath)
            .Any(root => full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public static string MachinesDir => Path.Combine(Home, "machines");
    public static string MachineDir(string name) => Path.Combine(MachinesDir, ValidateName(name));
    public static string ConfigPath(string name) => Path.Combine(MachineDir(name), "machine.json");
    public static string NotesPath(string name) => Path.Combine(MachineDir(name), "notes.md");
    public static string OutboxDir(string name) => Path.Combine(MachineDir(name), "outbox");
    public static string InboxDir(string name) => Path.Combine(MachineDir(name), "inbox");
    public static string LogsDir(string name) => Path.Combine(MachineDir(name), "logs");
    public static string VmBaseFolder(string name) => Path.Combine(MachineDir(name), "vm");

    public const string DefaultName = "default";

    public static string ValidateName(string name)
    {
        if (!NamePattern.IsMatch(name)) throw new UsageException($"Invalid machine name '{name}' (letters, digits, '-', '_'; max 32)");
        return name;
    }

    public static string VmNameFor(string name) => "twin-" + ValidateName(name);

    public static bool Exists(string name) => File.Exists(ConfigPath(name));

    public static MachineConfig Load(string name)
    {
        var path = ConfigPath(name);
        if (!File.Exists(path)) throw new CliException($"Machine '{name}' does not exist. Create it with: terminal-windows up {name} --iso <path-to-Windows11.iso>");
        return JsonSerializer.Deserialize<MachineConfig>(File.ReadAllText(path), JsonOpts) ?? throw new CliException($"Corrupt {path}");
    }

    public static void Save(MachineConfig config)
    {
        var dir = MachineDir(config.Name);
        Directory.CreateDirectory(dir);
        var path = ConfigPath(config.Name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOpts));
        RestrictToCurrentUser(tmp);
        File.Move(tmp, path, true);
        RestrictToCurrentUser(path);
    }

    public static IEnumerable<string> List() =>
        Directory.Exists(MachinesDir)
            ? Directory.GetDirectories(MachinesDir).Select(Path.GetFileName).Where(n => n is not null && File.Exists(ConfigPath(n!))).Select(n => n!).OrderBy(n => n)
            : Enumerable.Empty<string>();

    /// <summary>Removes inherited ACEs and grants only the current user access to the file.</summary>
    public static void RestrictToCurrentUser(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User!;
        var sec = new FileSecurity();
        sec.SetOwner(sid);
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(sec);
    }
}
