using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using TerminalWindows.Cli.Backend;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Cli.Setup;
using TerminalWindows.Cli.Viewer;

namespace TerminalWindows.Cli.Commands;

/// <summary>Environment checks. Only reports and advises; never installs or changes settings.</summary>
public static class Doctor
{
    private sealed record Check(string Name, string Status, string Detail);

    [DllImport("kernel32.dll")] private static extern bool IsProcessorFeaturePresent(int feature);
    private const int PF_VIRT_FIRMWARE_ENABLED = 21;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public static double TotalMemoryGb()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref mem) ? mem.ullTotalPhys / 1024.0 / 1024 / 1024 : 0;
    }

    public static bool VirtualizationEnabledInFirmware() => HypervisorPresent() || IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED);

    public static bool HypervisorPresent()
    {
        if (!X86Base.IsSupported) return false;
        var (_, _, ecx, _) = X86Base.CpuId(1, 0);
        return (ecx & (1 << 31)) != 0;
    }

    public static async Task<int> RunAsync(ParsedArgs a)
    {
        bool json = a.Flag("json");
        a.RejectUnknown();
        var checks = new List<Check>();

        // Windows edition
        using (var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            var edition = cv?.GetValue("EditionID") as string ?? "?";
            var product = cv?.GetValue("ProductName") as string ?? "?";
            var build = cv?.GetValue("CurrentBuildNumber") as string ?? "?";
            // Windows 11 still reports "Windows 10" in ProductName; the build number tells them apart.
            if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            checks.Add(new("host-os", "ok", $"{product} ({edition}, build {build}). Home is supported via VirtualBox; Hyper-V is not required."));
        }

        // VirtualBox
        var vbox = new VirtualBoxBackend();
        var info = await vbox.ProbeAsync();
        if (!info.Installed)
            checks.Add(VmwareBackend.IsInstalled()
                ? new("virtualbox", "ok", "VirtualBox not installed (not needed: VMware Workstation is used)")
                : new("virtualbox", "error", "VirtualBox is not installed. 'terminal-windows setup' installs it with winget when you agree " +
                                             "(or install Oracle VirtualBox 7.1+ from https://www.virtualbox.org/; the Extension Pack is not needed)."));
        else if (info.Problem is not null)
            checks.Add(new("virtualbox", "error", $"{info.Problem} ({info.Path}). Update to 7.1 or newer."));
        else
            checks.Add(new("virtualbox", "ok", $"VirtualBox {info.Version} ({info.Path})"));

        var vmw = new VmwareBackend();
        var vinfo = await vmw.ProbeAsync();
        checks.Add(vinfo.Installed
            ? new("vmware", vinfo.Problem is null ? "ok" : "warn", $"VMware Workstation {vinfo.Version} ({vinfo.Path})" + (vinfo.Problem is null ? "" : ": " + vinfo.Problem) +
                  (VmwareBackend.ToolsIso() is null ? " - no windows.iso (VMware Tools); the guest display stays at its default driver" : ""))
            : new("vmware", "ok", "VMware Workstation Pro not installed (optional; free for personal use; works with the Windows hypervisor on)"));
        checks.Add(new("backend", "ok", $"New machines use: {Backends.DefaultForNewMachine()} (override with --backend)"));

        var ga = VirtualBoxBackend.GuestAdditionsIso();
        checks.Add(ga is null
            ? new("guest-additions-iso", info.Installed ? "warn" : "error", "VBoxGuestAdditions.iso not found in the VirtualBox folder (needed for the display driver).")
            : new("guest-additions-iso", "ok", ga));

        // Virtualization support
        bool hv = HypervisorPresent();
        bool fw = IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED);
        if (hv && VmwareBackend.IsInstalled())
            checks.Add(new("hypervisor", "ok", "The Windows hypervisor is on; new machines use VMware Workstation, which works alongside it."));
        else if (hv)
            checks.Add(new("hypervisor", "warn",
                "A Windows hypervisor is active (WSL2, Docker Desktop, Virtual Machine Platform, Hyper-V or Memory Integrity). " +
                "VirtualBox then runs through the Windows Hypervisor Platform: much slower, and Windows 11 setup hung on a real PC. " +
                "Run 'terminal-windows host-mode' to see why it is on, and 'terminal-windows host-mode fast' (admin, reboot) to turn it off."));
        else if (!fw)
            checks.Add(new("vt-x", "error", "Hardware virtualization (VT-x / AMD-V) appears to be disabled in BIOS/UEFI. Enable it in the firmware settings."));
        else
            checks.Add(new("vt-x", "ok", "Hardware virtualization is enabled"));

        // Memory
        double gb = TotalMemoryGb();
        checks.Add(new("memory", gb >= 15.5 ? "ok" : "warn", $"{gb:0.0} GB installed (16 GB+ recommended); new VMs get {Lifecycle.DefaultMemoryGb()} GB by default"));

        // Data location vs. MSIX AppData virtualization
        var home = MachineStore.Home;
        bool packaged = MachineStore.RunningInPackage();
        if (packaged && MachineStore.IsUnderAppData(home))
            checks.Add(new("data-dir", "error", $"{home} is under AppData and this process runs inside a packaged app, so writes are redirected where VirtualBox cannot see them. Unset TERMINAL_WINDOWS_HOME or point it outside AppData."));
        else
            checks.Add(new("data-dir", "ok", home + (packaged ? " (running inside a packaged app; path is outside AppData, so it is not virtualized)" : "")));

        // Disk
        var root = Path.GetPathRoot(Path.GetFullPath(home))!;
        var drive = new DriveInfo(root);
        double freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
        checks.Add(new("disk", freeGb >= 64 ? "ok" : "warn", $"{freeGb:0} GB free on {root} (data: {home}; 64 GB+ recommended)"));

        // Guest helper
        var helper = PayloadIso.FindHelperExe();
        checks.Add(helper is null
            ? new("guest-helper", "error", "TerminalWindowsGuest.exe not found (expected in guest\\ next to terminal-windows.exe). Run build.ps1.")
            : new("guest-helper", "ok", helper));
        var isoProblem = PayloadIso.SelfCheck();
        checks.Add(isoProblem is null
            ? new("setup-iso", "ok", "the setup disc can be built")
            : new("setup-iso", "error", "building the setup disc failed: " + isoProblem));

        // Terminal
        var gfx = TerminalCaps.Detect();
        checks.Add(new("terminal", gfx.Protocol == GraphicsProtocol.None ? "warn" : "ok",
            gfx.Protocol == GraphicsProtocol.None
                ? $"{gfx.TerminalName}: no inline graphics detected. 'view' shows the screen as coloured text blocks (low resolution). Windows Terminal 1.22+ or WezTerm show it at full resolution; 'view --window' opens a window instead."
                : $"{gfx.TerminalName}: {gfx.Protocol}"));

        if (json)
        {
            var arr = new JsonArray(checks.Select(c => (JsonNode)new JsonObject { ["check"] = c.Name, ["status"] = c.Status, ["detail"] = c.Detail }).ToArray());
            Console.WriteLine(new JsonObject { ["checks"] = arr }.ToJsonString(new() { WriteIndented = true }));
        }
        else
        {
            foreach (var c in checks)
            {
                var mark = c.Status switch { "ok" => "[ OK ]", "warn" => "[WARN]", _ => "[FAIL]" };
                Console.WriteLine($"{mark} {c.Name,-20} {c.Detail}");
            }
        }
        return checks.Any(c => c.Status == "error") ? 1 : 0;
    }
}
