using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace TerminalWindows.Guest;

/// <summary>Configuration written by the installer from the setup ISO (guest.json).</summary>
public sealed class GuestConfig
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; } = Protocol.ProtocolConstants.GuestPort;
    [JsonPropertyName("width")] public int Width { get; set; } = 1600;
    [JsonPropertyName("height")] public int Height { get; set; } = 900;
    [JsonPropertyName("offline")] public bool Offline { get; set; }
    [JsonPropertyName("locale")] public string Locale { get; set; } = "ja-JP";
    [JsonPropertyName("user")] public string User { get; set; } = "agent";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("allowedRemote")] public string AllowedRemote { get; set; } = Protocol.ProtocolConstants.NatSubnet;
    /// <summary>Inside the VM this must be 0.0.0.0 (NAT forwards to the guest's NIC address). Tests use 127.0.0.1.</summary>
    [JsonPropertyName("bindAddress")] public string BindAddress { get; set; } = "0.0.0.0";

    public static GuestConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<GuestConfig>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty guest.json");
        if (string.IsNullOrEmpty(cfg.Token) || cfg.Token.Length < 32) throw new InvalidDataException("guest.json has no valid token");
        return cfg;
    }
}

public static class GuestPaths
{
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "terminal-windows");
    public static string ConfigFile => Path.Combine(DataDir, "guest.json");
    public static string StateFile => Path.Combine(DataDir, "setup-state.json");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "terminal-windows");
    public static string InstalledExe => Path.Combine(InstallDir, "TerminalWindowsGuest.exe");

    public static string UserDesktop(string user) => Path.Combine(
        Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users", user, "Desktop");
}

/// <summary>
/// Refuses to run outside a VirtualBox guest. This prevents accidental execution on the host
/// (which would otherwise relax UAC and enable auto-logon there). It is a guard against mistakes,
/// NOT a security boundary: SMBIOS strings can be spoofed.
/// </summary>
public static class VmCheck
{
    public static (bool IsVirtualBox, string Manufacturer, string Model) Detect()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var manufacturer = key?.GetValue("SystemManufacturer") as string ?? "";
        var model = key?.GetValue("SystemProductName") as string ?? "";
        bool ok = model.Equals("VirtualBox", StringComparison.OrdinalIgnoreCase)
                  || manufacturer.Equals("innotek GmbH", StringComparison.OrdinalIgnoreCase);
        return (ok, manufacturer, model);
    }
}
