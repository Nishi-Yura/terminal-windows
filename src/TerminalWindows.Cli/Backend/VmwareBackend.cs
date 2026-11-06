using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Backend;

/// <summary>
/// VMware Workstation Pro backend (free for personal use). Unlike VirtualBox it stays stable when
/// Windows' own hypervisor is on (memory integrity, WSL2, Docker), so it is the choice for PCs that
/// cannot turn that off. Everything goes through vmrun / vmware-vdiskmanager and a .vmx file we write.
/// <list type="bullet">
/// <item>Network: NAT (vmnet8). The host reaches the helper at the guest's NAT address, found in VMware's
///   DHCP leases by the VM's fixed MAC address; the guest only accepts the vmnet8 subnet.</item>
/// <item>Before guest software exists (Windows Setup), the screen and the boot key go through the VM's
///   own VNC server, bound to 127.0.0.1 with a random password.</item>
/// <item>Windows 11's TPM requirement is bypassed in Setup (VMware's vTPM needs VM encryption).</item>
/// </list>
/// </summary>
public sealed class VmwareBackend : IVmBackend
{
    private readonly IProcessRunner _runner;
    private readonly string? _dir;

    public VmwareBackend(IProcessRunner? runner = null, string? installDir = null)
    {
        _runner = runner ?? new ProcessRunner();
        _dir = installDir ?? InstallDir();
    }

    public string Name => "vmware";

    public static string? InstallDir()
    {
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\VMware, Inc.\VMware Workstation", @"SOFTWARE\VMware, Inc.\VMware Workstation" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            if (k?.GetValue("InstallPath") is string p && File.Exists(Path.Combine(p, "vmrun.exe"))) return p;
        }
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var p = Path.Combine(Environment.GetFolderPath(root), "VMware", "VMware Workstation");
            if (File.Exists(Path.Combine(p, "vmrun.exe"))) return p;
        }
        return null;
    }

    public static bool IsInstalled() => InstallDir() is not null;

    private string Dir => _dir ?? throw new CliException(
        "VMware Workstation Pro was not found. Install it (free for personal use) from Broadcom's support portal, then run 'terminal-windows doctor'.");
    private string Vmrun => Path.Combine(Dir, "vmrun.exe");

    /// <summary>The VMware Tools ISO for Windows guests, if this Workstation ships one.</summary>
    public static string? ToolsIso()
    {
        var dir = InstallDir();
        if (dir is null) return null;
        var iso = Path.Combine(dir, "windows.iso");
        return File.Exists(iso) ? iso : null;
    }

    private async Task<string> Run(params string[] args)
    {
        var r = await _runner.RunAsync(Vmrun, new[] { "-T", "ws" }.Concat(args).ToArray(), TimeSpan.FromMinutes(10));
        if (!r.Success) throw new CliException($"vmrun {args[0]} failed ({r.ExitCode}): {(r.StdOut + r.StdErr).Trim()}");
        return r.StdOut;
    }

    public async Task<BackendInfo> ProbeAsync()
    {
        if (_dir is null) return new BackendInfo(false, null, null, "VMware Workstation not found");
        var exe = Path.Combine(_dir, "vmware.exe");
        var version = File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion : null;
        var r = await _runner.RunAsync(Vmrun, new[] { "-T", "ws", "list" }, TimeSpan.FromSeconds(60));
        return new BackendInfo(true, version, Vmrun, r.Success ? null : "vmrun list failed: " + (r.StdOut + r.StdErr).Trim());
    }

    // ---- where things are ----

    public static string VmxPath(string baseFolder, string vm) => Path.Combine(baseFolder, vm, vm + ".vmx");

    /// <summary>Finds the .vmx of a terminal-windows machine by its VM name.</summary>
    public static string VmxFor(string vm)
    {
        foreach (var n in MachineStore.List())
        {
            var m = MachineStore.Load(n);
            if (m.VmName == vm) return VmxPath(MachineStore.VmBaseFolder(n), vm);
        }
        throw new CliException($"No terminal-windows machine uses the VM '{vm}'.");
    }

    private static MachineConfig? MachineFor(string vm) =>
        MachineStore.List().Select(MachineStore.Load).FirstOrDefault(m => m.VmName == vm);

    // ---- create ----

    /// <summary>The .vmx we write (also printed by <c>up --plan</c>).</summary>
    public static string VmxText(VmSpec s, string mac, int vncPort, string vncPassword, string? toolsIso)
    {
        var vm = s.VmName;
        var lines = new List<(string, string)>
        {
            (".encoding", "UTF-8"), ("config.version", "8"), ("virtualHW.version", "20"),
            ("displayName", vm), ("guestOS", "windows11-64"),
            ("firmware", "efi"), ("uefi.secureBoot.enabled", "TRUE"),
            ("numvcpus", s.Cpus.ToString()), ("cpuid.coresPerSocket", s.Cpus.ToString()),
            ("memsize", s.MemoryMb.ToString()),
            // PCIe root ports: without them the NVMe disk and the e1000e NIC have no slot ("No PCIe slot available").
            ("pciBridge0.present", "TRUE"),
            ("pciBridge4.present", "TRUE"), ("pciBridge4.virtualDev", "pcieRootPort"), ("pciBridge4.functions", "8"),
            ("pciBridge5.present", "TRUE"), ("pciBridge5.virtualDev", "pcieRootPort"), ("pciBridge5.functions", "8"),
            ("pciBridge6.present", "TRUE"), ("pciBridge6.virtualDev", "pcieRootPort"), ("pciBridge6.functions", "8"),
            ("pciBridge7.present", "TRUE"), ("pciBridge7.virtualDev", "pcieRootPort"), ("pciBridge7.functions", "8"),
            ("hpet0.present", "TRUE"), ("vmci0.present", "TRUE"),
            ("nvme0.present", "TRUE"), ("nvme0:0.present", "TRUE"), ("nvme0:0.fileName", vm + ".vmdk"),
            ("sata0.present", "TRUE"),
            ("sata0:1.present", "TRUE"), ("sata0:1.deviceType", "cdrom-image"), ("sata0:1.fileName", s.InstallIso), ("sata0:1.startConnected", "TRUE"),
            ("sata0:2.present", "TRUE"), ("sata0:2.deviceType", "cdrom-image"), ("sata0:2.fileName", s.PayloadIso), ("sata0:2.startConnected", "TRUE"),
            ("bios.bootOrder", "cdrom,hdd"),
            // Network: NAT only, fixed MAC so the host can find the guest's address in VMware's DHCP leases.
            ("ethernet0.present", "TRUE"), ("ethernet0.connectionType", "nat"), ("ethernet0.virtualDev", "e1000e"),
            ("ethernet0.addressType", "static"), ("ethernet0.address", mac),
            // Isolation: nothing from the host is connected to the VM.
            ("isolation.tools.copy.disable", "TRUE"), ("isolation.tools.paste.disable", "TRUE"),
            ("isolation.tools.dnd.disable", "TRUE"), ("isolation.tools.hgfs.disable", "TRUE"),
            ("usb.present", "FALSE"), ("ehci.present", "FALSE"), ("usb_xhci.present", "FALSE"),
            ("sound.present", "FALSE"), ("floppy0.present", "FALSE"), ("serial0.present", "FALSE"),
            // Screen and keys during Windows Setup: the VM's own VNC server, loopback only, random password.
            ("RemoteDisplay.vnc.enabled", "TRUE"), ("RemoteDisplay.vnc.port", vncPort.ToString()),
            ("RemoteDisplay.vnc.ip", "127.0.0.1"), ("RemoteDisplay.vnc.password", vncPassword),
            // Never stop on a dialog nobody can see (we run without a window).
            ("msg.autoAnswer", "TRUE"),
            ("tools.syncTime", "TRUE"), ("svga.autodetect", "TRUE"),
        };
        if (toolsIso is not null)
            lines.AddRange(new[] { ("sata0:3.present", "TRUE"), ("sata0:3.deviceType", "cdrom-image"), ("sata0:3.fileName", toolsIso), ("sata0:3.startConnected", "TRUE") });
        var sb = new StringBuilder();
        foreach (var (k, v) in lines) sb.Append(k).Append(" = \"").Append(v.Replace("\"", "|22")).Append("\"\r\n");
        return sb.ToString();
    }

    /// <summary>A random MAC in the range VMware reserves for manually assigned addresses (00:50:56:00-3F).</summary>
    public static string NewMac()
    {
        var b = System.Security.Cryptography.RandomNumberGenerator.GetBytes(3);
        return $"00:50:56:{b[0] & 0x3F:X2}:{b[1]:X2}:{b[2]:X2}";
    }

    public IReadOnlyList<string> PlanCreate(VmSpec s) => new[]
    {
        $"vmware-vdiskmanager -c -s {s.DiskMb}MB -a lsilogic -t 0 {Path.Combine(s.BaseFolder, s.VmName, s.VmName + ".vmdk")}",
        $"write {VmxPath(s.BaseFolder, s.VmName)} (EFI + Secure Boot, NVMe disk, NAT, VNC on 127.0.0.1, clipboard/drag&drop/shared folders off)",
    };

    public async Task CreateAsync(VmSpec s, Action<string> progress)
    {
        if (s.SharedFolder is not null) throw new CliException("--share is not supported with VMware yet.");
        var vmx = VmxPath(s.BaseFolder, s.VmName);
        if (File.Exists(vmx)) throw new CliException($"{vmx} already exists; refusing to overwrite it.");
        var m = MachineFor(s.VmName) ?? throw new CliException("Save the machine config before creating its VMware VM.");
        Directory.CreateDirectory(Path.GetDirectoryName(vmx)!);
        var disk = Path.Combine(Path.GetDirectoryName(vmx)!, s.VmName + ".vmdk");
        progress("vmware-vdiskmanager (create disk)");
        var r = await _runner.RunAsync(Path.Combine(Dir, "vmware-vdiskmanager.exe"),
            new[] { "-c", "-s", $"{s.DiskMb}MB", "-a", "lsilogic", "-t", "0", disk }, TimeSpan.FromMinutes(5));
        if (!r.Success) throw new CliException("vmware-vdiskmanager failed: " + (r.StdOut + r.StdErr).Trim());
        progress("write " + Path.GetFileName(vmx));
        File.WriteAllText(vmx, VmxText(s, m.MacAddress!, m.VncPort, m.VncPassword, ToolsIso()), new UTF8Encoding(false));
    }

    // ---- power ----

    public async Task<VmState> GetStateAsync(string vm)
    {
        string vmx;
        try { vmx = VmxFor(vm); } catch (CliException) { return VmState.NotFound; }
        if (!File.Exists(vmx)) return VmState.NotFound;
        var r = await _runner.RunAsync(Vmrun, new[] { "-T", "ws", "list" }, TimeSpan.FromSeconds(60));
        if (!r.Success) return VmState.Other;
        if (r.StdOut.Split('\n').Any(l => string.Equals(l.Trim(), vmx, StringComparison.OrdinalIgnoreCase))) return VmState.Running;
        return Directory.EnumerateFiles(Path.GetDirectoryName(vmx)!, "*.vmss").Any() ? VmState.Saved : VmState.PoweredOff;
    }

    public async Task StartAsync(string vm, bool headless = true)
    {
        var vmx = VmxFor(vm);
        DisconnectMissingMedia(vmx);
        await Run("start", vmx, headless ? "nogui" : "gui");
    }

    /// <summary>Gives a cloned VM its own MAC address and VNC port (a clone copies the source's).</summary>
    public static void SetIdentity(string vmx, string mac, int vncPort, string vncPassword)
    {
        var set = new Dictionary<string, string>
        {
            ["ethernet0.addressType"] = "static", ["ethernet0.address"] = mac,
            ["RemoteDisplay.vnc.port"] = vncPort.ToString(), ["RemoteDisplay.vnc.password"] = vncPassword,
        };
        var lines = File.ReadAllLines(vmx)
            .Where(l => !set.Keys.Any(k => l.StartsWith(k + " ")) && !l.StartsWith("ethernet0.generatedAddress"))
            .ToList();
        lines.AddRange(set.Select(kv => $"{kv.Key} = \"{kv.Value}\""));
        File.WriteAllLines(vmx, lines);
    }

    /// <summary>A CD image that was deleted (the setup ISO holds secrets and is removed) must not stop the next boot.</summary>
    public static void DisconnectMissingMedia(string vmx)
    {
        var lines = File.ReadAllLines(vmx).ToList();
        bool changed = false;
        foreach (var dev in lines.Select(l => Regex.Match(l, "^(sata0:\\d+)\\.fileName = \"(.*)\"$")).Where(m => m.Success).ToList())
        {
            if (File.Exists(dev.Groups[2].Value)) continue;
            var present = dev.Groups[1].Value + ".present";
            int i = lines.FindIndex(l => l.StartsWith(present + " "));
            if (i >= 0 && lines[i].Contains("TRUE")) { lines[i] = present + " = \"FALSE\""; changed = true; }
        }
        if (changed) File.WriteAllLines(vmx, lines);
    }

    public async Task SaveStateAsync(string vm) => await Run("suspend", VmxFor(vm));
    public async Task PowerOffAsync(string vm) => await Run("stop", VmxFor(vm), "hard");
    public async Task PauseAsync(string vm) => await Run("pause", VmxFor(vm));
    public async Task ResumeAsync(string vm) => await Run("unpause", VmxFor(vm));

    public Task DiscardSavedStateAsync(string vm)
    {
        var dir = Path.GetDirectoryName(VmxFor(vm))!;
        foreach (var f in Directory.EnumerateFiles(dir, "*.vmss").Concat(Directory.EnumerateFiles(dir, "*.vmem"))) File.Delete(f);
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(string vm, TimeSpan timeout)
    {
        // "soft" needs VMware Tools; without them it fails at once and the caller powers off.
        var r = await _runner.RunAsync(Vmrun, new[] { "-T", "ws", "stop", VmxFor(vm), "soft" }, timeout);
        if (!r.Success) throw new CliException("Soft shutdown failed: " + (r.StdOut + r.StdErr).Trim());
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await GetStateAsync(vm) is VmState.PoweredOff) return;
            await Task.Delay(2000);
        }
        throw new CliException($"The guest did not shut down within {timeout.TotalSeconds:0}s.");
    }

    // ---- snapshots ----

    public async Task TakeSnapshotAsync(string vm, string label, string? description = null) => await Run("snapshot", VmxFor(vm), label);
    public async Task RestoreSnapshotAsync(string vm, string label) => await Run("revertToSnapshot", VmxFor(vm), label);
    public async Task DeleteSnapshotAsync(string vm, string label) => await Run("deleteSnapshot", VmxFor(vm), label);

    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string vm)
    {
        var r = await _runner.RunAsync(Vmrun, new[] { "-T", "ws", "listSnapshots", VmxFor(vm) });
        return r.Success ? ParseSnapshots(r.StdOut) : Array.Empty<SnapshotInfo>();
    }

    public static IReadOnlyList<SnapshotInfo> ParseSnapshots(string text) => text.Split('\n')
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Total snapshots"))
        .Select(n => new SnapshotInfo(n, false)).ToList();

    public async Task CloneLinkedAsync(string sourceVm, string snapshot, string newVm, string baseFolder)
    {
        var dest = VmxPath(baseFolder, newVm);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await Run("clone", VmxFor(sourceVm), dest, "linked", $"-snapshot={snapshot}", $"-cloneName={newVm}");
    }

    // ---- media, display, keys ----

    public Task SetHelperPortForwardAsync(string vm, int hostPort, int guestPort) => Task.CompletedTask; // NAT address, no forward

    public async Task EjectInstallMediaAsync(string vm)
    {
        var vmx = VmxFor(vm);
        foreach (var dev in new[] { "sata0:1", "sata0:2", "sata0:3" })
            await _runner.RunAsync(Vmrun, new[] { "-T", "ws", "disconnectNamedDevice", vmx, dev });
    }

    public Task AttachDvdAsync(string vm, int port, string iso)
    {
        var vmx = VmxFor(vm);
        var key = $"sata0:{port}.fileName";
        var lines = File.ReadAllLines(vmx).Where(l => !l.StartsWith(key + " ") && !l.StartsWith($"sata0:{port}.present ")).ToList();
        lines.Add($"sata0:{port}.present = \"TRUE\"");
        lines.Add($"{key} = \"{iso}\"");
        File.WriteAllLines(vmx, lines);
        return Task.CompletedTask;
    }

    public Task ForgetMediumAsync(string path) => Task.CompletedTask;

    // VMware frees a VNC slot only long after the client disconnects (40 slots; a run of 60 short
    // connections hit "Reached maximum remote connections"), so one connection per VM is kept and reused.
    private readonly Dictionary<string, VncClient> _vnc = new();

    private async Task<T?> WithVncAsync<T>(string vm, Func<VncClient, Task<T>> use)
    {
        var m = MachineFor(vm);
        if (m is null) return default;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!_vnc.TryGetValue(vm, out var c))
                    _vnc[vm] = c = await VncClient.ConnectAsync(m.VncPort, m.VncPassword, TimeSpan.FromSeconds(10));
                return await use(c);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (_vnc.Remove(vm, out var dead)) await dead.DisposeAsync();
            }
        }
        return default;
    }

    public async Task DisposeVncAsync()
    {
        foreach (var c in _vnc.Values) await c.DisposeAsync();
        _vnc.Clear();
    }

    public Task TypeAsync(string vm, string text, bool jis) => WithVncAsync(vm, async c => { await c.TypeAsync(text, jis); return true; });

    public Task PressBootKeyAsync(string vm) =>
        WithVncAsync(vm, async c => { await c.KeyAsync(0x20); return true; }); // Space: "Press any key to boot from CD or DVD"

    public async Task<bool> ScreenshotPngAsync(string vm, string path)
    {
        // Tap Shift first: an idle guest turns its display off and VNC then shows only black.
        var img = await WithVncAsync(vm, async c =>
        {
            await c.KeyAsync(0xffe1);
            await Task.Delay(1500);
            return await c.CaptureAsync(TimeSpan.FromSeconds(20));
        });
        if (img is null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Imaging.EncodePng(img));
        return true;
    }

    public Task SetVideoModeHintAsync(string vm, int width, int height) => Task.CompletedTask; // the helper sets it from inside

    public Task ShowWindowAsync(string vm)
    {
        Process.Start(new ProcessStartInfo(Path.Combine(Dir, "vmware.exe"), $"\"{VmxFor(vm)}\"") { UseShellExecute = false });
        return Task.CompletedTask;
    }

    public Task HideWindowAsync(string vm) =>
        throw new CliException("Close the VMware Workstation window; the VM keeps running in the background.");

    public (string Host, int Port) GetGuestEndpoint(int hostPort) => throw new NotSupportedException("Use GuestAddress(machine).");

    // ---- guest address ----

    /// <summary>The vmnet8 (NAT) subnet the host talks to guests from, e.g. "192.168.153.0/24".</summary>
    public static string? NatSubnet()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Name.Contains("VMnet8", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("VMnet8", StringComparison.OrdinalIgnoreCase));
        var ip = nic?.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
        if (ip is null) return null;
        var b = ip.Address.GetAddressBytes();
        int prefix = ip.PrefixLength is > 0 and <= 32 ? ip.PrefixLength : 24;
        uint mask = prefix == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
        uint addr = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) & mask;
        return $"{addr >> 24}.{(addr >> 16) & 255}.{(addr >> 8) & 255}.{addr & 255}/{prefix}";
    }

    public static string LeasesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VMware", "vmnetdhcp.leases");

    /// <summary>The newest DHCP lease for <paramref name="mac"/> in VMware's lease file (ISC dhcpd format).</summary>
    public static string? FindLease(string leasesText, string mac)
    {
        string? found = null;
        foreach (Match block in Regex.Matches(leasesText, @"lease\s+(\d+\.\d+\.\d+\.\d+)\s*\{(.*?)\}", RegexOptions.Singleline))
        {
            var hw = Regex.Match(block.Groups[2].Value, @"hardware ethernet ([0-9a-fA-F:]+);");
            if (hw.Success && string.Equals(hw.Groups[1].Value, mac, StringComparison.OrdinalIgnoreCase)) found = block.Groups[1].Value;
        }
        return found;
    }

    /// <summary>The guest's NAT address: DHCP lease by MAC, else the host's neighbour (ARP) table.</summary>
    public static string? GuestAddress(MachineConfig m)
    {
        if (m.MacAddress is null) return null;
        try
        {
            using var fs = new FileStream(LeasesPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            if (FindLease(sr.ReadToEnd(), m.MacAddress) is { } ip) return ip;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            var arp = Process.Start(new ProcessStartInfo("arp", "-a") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var text = arp.StandardOutput.ReadToEnd();
            arp.WaitForExit(5000);
            var dashed = m.MacAddress.Replace(':', '-');
            var line = text.Split('\n').FirstOrDefault(l => l.Contains(dashed, StringComparison.OrdinalIgnoreCase));
            var ipm = line is null ? null : Regex.Match(line, @"\d+\.\d+\.\d+\.\d+");
            if (ipm is { Success: true } && IPAddress.TryParse(ipm.Value, out _)) return ipm.Value;
        }
        catch (System.ComponentModel.Win32Exception) { }
        return null;
    }

    /// <summary>Test cleanup and 'delete': removes the VM and its files.</summary>
    internal async Task DeleteVmAsync(string vm)
    {
        var vmx = VmxFor(vm);
        await _runner.RunAsync(Vmrun, new[] { "-T", "ws", "deleteVM", vmx });
    }
}
