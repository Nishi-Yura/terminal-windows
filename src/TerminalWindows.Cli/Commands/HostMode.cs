using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using TerminalWindows.Cli.Infrastructure;

namespace TerminalWindows.Cli.Commands;

/// <summary>
/// <c>host-mode [fast|secure] [--yes]</c>: shows, and on request switches, the HOST settings that make
/// Windows run its own hypervisor (memory integrity / VBS / Windows Hello's VBS protection).
/// While that hypervisor runs, VirtualBox has to go through the Windows Hypervisor Platform, which is
/// slow and hung Windows 11 setup on a real Home PC; with it off the same VM installed in 35 minutes.
/// <para>
/// This is the one command that changes host security settings, so it only does so when the user runs it
/// themselves from an elevated terminal with <c>--yes</c>; without <c>--yes</c> it prints the plan.
/// <c>fast</c> saves the previous values first and <c>secure</c> restores exactly those. Both need a reboot.
/// </para>
/// </summary>
public static class HostMode
{
    public const string DeviceGuardKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    public const string HvciKey = DeviceGuardKey + @"\Scenarios\HypervisorEnforcedCodeIntegrity";
    public const string HelloKey = DeviceGuardKey + @"\Scenarios\WindowsHello";
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";

    /// <summary>One registry DWORD (HKLM) this command manages. Null means "value not present".</summary>
    public sealed record RegSetting(string Key, string Name, string Label);

    public static readonly RegSetting[] Settings =
    {
        new(DeviceGuardKey, "EnableVirtualizationBasedSecurity", "Virtualization-based security (VBS)"),
        new(HvciKey, "Enabled", "Memory integrity (HVCI)"),
        new(HelloKey, "Enabled", "Windows Hello VBS protection"),
    };

    /// <summary>The host state that matters here. LaunchType is bcdedit's hypervisorlaunchtype (null = not set = Auto).</summary>
    public sealed record State(string? LaunchType, IReadOnlyDictionary<string, int?> Values);

    public sealed record Change(string What, string From, string To);

    public static string Id(RegSetting s) => s.Key + "|" + s.Name;

    /// <summary>What 'fast' changes: hypervisor launch off and every VBS switch to 0.</summary>
    public static List<Change> PlanFast(State now)
    {
        var list = new List<Change>();
        if (!string.Equals(now.LaunchType, "Off", StringComparison.OrdinalIgnoreCase))
            list.Add(new("bcdedit hypervisorlaunchtype", now.LaunchType ?? "Auto (default)", "Off"));
        foreach (var s in Settings)
        {
            var v = now.Values.GetValueOrDefault(Id(s));
            if (v is not 0) list.Add(new(s.Label, v?.ToString() ?? "not set", "0"));
        }
        return list;
    }

    /// <summary>What 'secure' changes: back to the saved values, or to Windows' defaults without a backup.</summary>
    public static List<Change> PlanSecure(State now, State? saved)
    {
        var target = saved ?? new State(null, Settings.ToDictionary(Id, _ => (int?)1));
        var list = new List<Change>();
        if (!string.Equals(now.LaunchType ?? "Auto", target.LaunchType ?? "Auto", StringComparison.OrdinalIgnoreCase))
            list.Add(new("bcdedit hypervisorlaunchtype", now.LaunchType ?? "Auto (default)", target.LaunchType ?? "Auto (default)"));
        foreach (var s in Settings)
        {
            int? cur = now.Values.GetValueOrDefault(Id(s)), want = target.Values.GetValueOrDefault(Id(s));
            if (cur != want) list.Add(new(s.Label, cur?.ToString() ?? "not set", want?.ToString() ?? "not set"));
        }
        return list;
    }

    /// <summary>Reads "hypervisorlaunchtype    Off" from `bcdedit /enum {current}` output.</summary>
    public static string? ParseLaunchType(string bcdeditOutput)
    {
        foreach (var line in bcdeditOutput.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase))
                return t["hypervisorlaunchtype".Length..].Trim();
        }
        return null;
    }

    public static string BackupPath => Path.Combine(MachineStore.Home, "host-mode-backup.json");

    public static async Task<int> RunAsync(ParsedArgs a)
    {
        var mode = a.PosOrNull(0);
        bool yes = a.Flag("yes");
        a.RejectUnknown();
        if (mode is not (null or "fast" or "secure")) throw new UsageException("host-mode takes 'fast', 'secure' or nothing (show the current state)");

        PrintStatus();
        if (mode is null)
        {
            Console.WriteLine();
            Console.WriteLine(Doctor.HypervisorPresent()
                ? "To make VMs fast, run from an administrator terminal:  terminal-windows host-mode fast"
                : "The Windows hypervisor is off: VirtualBox uses VT-x directly (fast).");
            return 0;
        }

        if (!IsAdmin())
            throw new CliException("This changes host security settings and needs an administrator terminal. " +
                "Right-click Start > 'Terminal (Admin)', then run: terminal-windows host-mode " + mode + (yes ? " --yes" : ""));

        var now = await ReadStateAsync();
        var saved = LoadBackup();
        var plan = mode == "fast" ? PlanFast(now) : PlanSecure(now, saved);
        Console.WriteLine();
        if (plan.Count == 0)
        {
            Console.WriteLine(mode == "fast" ? "Already in fast mode." : "Already in secure mode.");
            if (mode == "fast" && Doctor.HypervisorPresent()) Console.WriteLine("The hypervisor is still running: reboot (Start > Power > Restart, not shut down).");
            return 0;
        }
        Console.WriteLine(mode == "fast" ? "host-mode fast will change:" : $"host-mode secure will change ({(saved is null ? "no backup found: Windows defaults" : "back to the saved values")}):");
        foreach (var c in plan) Console.WriteLine($"  - {c.What}: {c.From} -> {c.To}");
        if (mode == "fast")
        {
            Console.WriteLine();
            Console.WriteLine("While in fast mode: WSL2, Docker Desktop and Windows Sandbox do not work, and the host loses");
            Console.WriteLine("memory integrity / VBS protection. Undo any time with 'terminal-windows host-mode secure --yes'.");
            foreach (var w in Conflicts()) Console.WriteLine("  ! " + w);
        }
        if (!yes)
        {
            Console.WriteLine();
            Console.WriteLine($"Nothing was changed. To apply, run:  terminal-windows host-mode {mode} --yes");
            return 1;
        }

        if (mode == "fast" && saved is null) SaveBackup(now);
        await ApplyAsync(mode == "fast" ? new State("Off", Settings.ToDictionary(Id, _ => (int?)0)) : saved ?? new State(null, Settings.ToDictionary(Id, _ => (int?)1)));
        if (mode == "secure" && File.Exists(BackupPath)) File.Delete(BackupPath);
        Console.WriteLine();
        Console.WriteLine("Done. Restart Windows (Start > Power > Restart) for it to take effect, then check with 'terminal-windows host-mode'.");
        return 0;
    }

    private static void PrintStatus()
    {
        bool hv = Doctor.HypervisorPresent();
        Console.WriteLine($"Windows hypervisor: {(hv ? "RUNNING (VirtualBox goes through it: slow, can hang during setup)" : "off (fast)")}");
        foreach (var s in Settings)
            Console.WriteLine($"  {s.Label,-36} {ReadDword(s.Key, s.Name)?.ToString() ?? "not set"}");
        if (ReadDword(PolicyKey, "EnableVirtualizationBasedSecurity") is { } p)
            Console.WriteLine($"  ! Group Policy sets VBS ({p}); a policy wins over these settings and host-mode does not change it.");
        if (hv && LastVbsBootReason() is { } why) Console.WriteLine($"  Last boot: {why}");
    }

    private static IEnumerable<string> Conflicts()
    {
        using (var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
            if (lxss?.GetSubKeyNames().Length > 0) yield return $"WSL distributions are installed ({lxss.GetSubKeyNames().Length}); they will not start in fast mode.";
        var docker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker");
        if (Directory.Exists(docker)) yield return "Docker Desktop is installed; it will not start in fast mode.";
    }

    private static string? LastVbsBootReason()
    {
        try
        {
            var r = new ProcessRunner().RunAsync("wevtutil", new[] { "qe", "System",
                "/q:*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and (EventID=153)]]", "/c:1", "/rd:true", "/f:text" },
                TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var line = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Virtualization-based security"));
            return line;
        }
        catch { return null; }
    }

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int? ReadDword(string key, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(name) is int v ? v : null;
    }

    private static async Task<State> ReadStateAsync()
    {
        var r = await new ProcessRunner().RunAsync("bcdedit", new[] { "/enum", "{current}" }, TimeSpan.FromSeconds(20));
        if (!r.Success) throw new CliException("bcdedit /enum failed: " + (r.StdErr + r.StdOut).Trim());
        return new State(ParseLaunchType(r.StdOut), Settings.ToDictionary(Id, s => ReadDword(s.Key, s.Name)));
    }

    private static async Task ApplyAsync(State target)
    {
        var bcd = target.LaunchType is null
            ? new[] { "/deletevalue", "{current}", "hypervisorlaunchtype" }
            : new[] { "/set", "{current}", "hypervisorlaunchtype", target.LaunchType };
        var r = await new ProcessRunner().RunAsync("bcdedit", bcd, TimeSpan.FromSeconds(20));
        if (!r.Success && target.LaunchType is not null) throw new CliException("bcdedit failed: " + (r.StdErr + r.StdOut).Trim());
        Console.WriteLine($"  bcdedit hypervisorlaunchtype -> {target.LaunchType ?? "Auto (default)"}");
        foreach (var s in Settings)
        {
            var v = target.Values.GetValueOrDefault(Id(s));
            using var k = Registry.LocalMachine.CreateSubKey(s.Key, writable: true);
            if (v is null) k.DeleteValue(s.Name, throwOnMissingValue: false);
            else k.SetValue(s.Name, v.Value, RegistryValueKind.DWord);
            Console.WriteLine($"  {s.Label} -> {v?.ToString() ?? "not set"}");
        }
    }

    private static void SaveBackup(State s)
    {
        Directory.CreateDirectory(MachineStore.Home);
        var o = new JsonObject { ["launchType"] = s.LaunchType, ["savedAt"] = DateTime.Now.ToString("s") };
        var vals = new JsonObject();
        foreach (var kv in s.Values) vals[kv.Key] = kv.Value;
        o["values"] = vals;
        File.WriteAllText(BackupPath, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  saved the current values to {BackupPath}");
    }

    public static State? LoadBackup()
    {
        if (!File.Exists(BackupPath)) return null;
        var o = JsonNode.Parse(File.ReadAllText(BackupPath))!.AsObject();
        var vals = o["values"]!.AsObject();
        return new State(o["launchType"]?.GetValue<string>(),
            Settings.ToDictionary(Id, s => vals[Id(s)] is { } n ? n.GetValue<int?>() : null));
    }
}
