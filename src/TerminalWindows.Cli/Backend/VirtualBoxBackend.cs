using System.Diagnostics;
using System.Text.RegularExpressions;
using TerminalWindows.Cli.Infrastructure;

namespace TerminalWindows.Cli.Backend;

/// <summary>
/// VirtualBox backend. Everything goes through VBoxManage; only the GPL base package is used
/// (no Extension Pack). No administrator rights are needed.
/// </summary>
public sealed class VirtualBoxBackend : IVmBackend
{
    public const string StorageController = "SATA";
    public const string PortForwardRule = "twhelper";
    public static readonly Version MinimumVersion = new(7, 1);

    private readonly IProcessRunner _runner;
    private readonly string? _vboxManage;

    public VirtualBoxBackend(IProcessRunner? runner = null, string? vboxManagePath = null)
    {
        _runner = runner ?? new ProcessRunner();
        _vboxManage = vboxManagePath ?? Locate();
    }

    public string Name => "virtualbox";

    public static string? InstallDir()
    {
        var env = Environment.GetEnvironmentVariable("VBOX_MSI_INSTALL_PATH")
                  ?? Environment.GetEnvironmentVariable("VBOX_INSTALL_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "VBoxManage.exe"))) return env;
        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Oracle", "VirtualBox");
        return File.Exists(Path.Combine(def, "VBoxManage.exe")) ? def : null;
    }

    private static string? Locate()
    {
        var dir = InstallDir();
        if (dir is not null) return Path.Combine(dir, "VBoxManage.exe");
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(p.Trim(), "VBoxManage.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string? GuestAdditionsIso()
    {
        var dir = InstallDir();
        var iso = dir is null ? null : Path.Combine(dir, "VBoxGuestAdditions.iso");
        return iso is not null && File.Exists(iso) ? iso : null;
    }

    private string Exe => _vboxManage ?? throw new CliException(
        "VBoxManage.exe was not found. Install Oracle VirtualBox 7.1+ (the base package; the Extension Pack is not needed), then run 'terminal-windows doctor'.");

    private async Task<string> VBox(params string[] args)
    {
        var r = await _runner.RunAsync(Exe, args);
        if (!r.Success)
            throw new CliException($"VBoxManage {string.Join(' ', args.Take(2))} failed ({r.ExitCode}): {FirstLines(r.StdErr + r.StdOut)}");
        return r.StdOut;
    }

    private static string FirstLines(string s) => string.Join(" ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4));

    public async Task<BackendInfo> ProbeAsync()
    {
        if (_vboxManage is null) return new BackendInfo(false, null, null, "VBoxManage.exe not found");
        var r = await _runner.RunAsync(_vboxManage, new[] { "--version" }, TimeSpan.FromSeconds(30));
        var version = r.StdOut.Trim();
        if (!r.Success) return new BackendInfo(true, null, _vboxManage, "VBoxManage --version failed: " + FirstLines(r.StdErr));
        var parsed = ParseVersion(version);
        string? problem = parsed is null ? $"Unrecognised version '{version}'"
            : parsed < MinimumVersion ? $"VirtualBox {parsed} is older than {MinimumVersion}" : null;
        return new BackendInfo(true, version, _vboxManage, problem);
    }

    public static Version? ParseVersion(string text)
    {
        var m = Regex.Match(text, @"(\d+)\.(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>The exact VBoxManage invocations used to create a VM (also printed by <c>up --plan</c>).</summary>
    public IReadOnlyList<string> PlanCreate(VmSpec s) => CreateCommands(s).Select(c => "VBoxManage " + string.Join(' ', c.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))).ToList();

    internal static List<string[]> CreateCommands(VmSpec s)
    {
        var vm = s.VmName;
        var disk = Path.Combine(s.BaseFolder, vm, vm + ".vdi");
        var cmds = new List<string[]>
        {
            new[] { "createvm", "--name", vm, "--ostype", "Windows11_64", "--register", "--basefolder", s.BaseFolder },
            new[]
            {
                "modifyvm", vm,
                "--firmware", "efi", "--tpm-type", "2.0",
                "--cpus", s.Cpus.ToString(), "--memory", s.MemoryMb.ToString(),
                "--graphicscontroller", "vboxsvga", "--vram", "128",
                "--ioapic", "on", "--rtc-use-utc", "on",
                "--mouse", "ps2", "--keyboard", "ps2",
                "--boot1", "dvd", "--boot2", "disk", "--boot3", "none", "--boot4", "none",
                // Network: NAT only. The helper port is forwarded from 127.0.0.1 on the host (never 0.0.0.0).
                "--nic1", "nat", "--nat-localhostreachable1", "off",
                "--natpf1", $"{PortForwardRule},tcp,127.0.0.1,{s.HostPort},,{s.GuestPort}",
                // Isolation: nothing from the host is connected to the VM.
                "--clipboard-mode", "disabled", "--drag-and-drop", "disabled",
                "--audio-enabled", "off",
                "--usb-ohci", "off", "--usb-ehci", "off", "--usb-xhci", "off",
            },
            // Secure Boot needs keys enrolled before it can be enabled.
            new[] { "modifynvram", vm, "inituefivarstore" },
            new[] { "modifynvram", vm, "enrollmssignatures" },
            new[] { "modifynvram", vm, "enrollorclpk" },
            new[] { "modifynvram", vm, "secureboot", "--enable" },
            new[] { "createmedium", "disk", "--filename", disk, "--size", s.DiskMb.ToString(), "--format", "VDI", "--variant", "Standard" },
            // Host I/O cache: under the Windows Hypervisor Platform the uncached async path hung the AHCI port (>32 requests) in a real run.
            new[] { "storagectl", vm, "--name", StorageController, "--add", "sata", "--controller", "IntelAhci", "--portcount", "4", "--bootable", "on", "--hostiocache", "on" },
            new[] { "storageattach", vm, "--storagectl", StorageController, "--port", "0", "--device", "0", "--type", "hdd", "--medium", disk, "--nonrotational", "on", "--discard", "on" },
            new[] { "storageattach", vm, "--storagectl", StorageController, "--port", "1", "--device", "0", "--type", "dvddrive", "--medium", s.InstallIso },
            new[] { "storageattach", vm, "--storagectl", StorageController, "--port", "2", "--device", "0", "--type", "dvddrive", "--medium", s.PayloadIso },
        };
        if (s.GuestAdditionsIso is not null)
            cmds.Add(new[] { "storageattach", vm, "--storagectl", StorageController, "--port", "3", "--device", "0", "--type", "dvddrive", "--medium", s.GuestAdditionsIso });
        if (s.SharedFolder is not null)
        {
            var sf = new List<string> { "sharedfolder", "add", vm, "--name", "host-share", "--hostpath", s.SharedFolder, "--automount" };
            if (!s.SharedFolderWritable) sf.Add("--readonly");
            cmds.Add(sf.ToArray());
        }
        return cmds;
    }

    public async Task CreateAsync(VmSpec spec, Action<string> progress)
    {
        if (await GetStateAsync(spec.VmName) != VmState.NotFound)
            throw new CliException($"A VirtualBox VM named '{spec.VmName}' already exists; refusing to overwrite it.");
        foreach (var cmd in CreateCommands(spec))
        {
            progress($"VBoxManage {cmd[0]} {(cmd.Length > 1 ? cmd[1] : "")}");
            await VBox(cmd);
        }
    }

    public async Task<VmState> GetStateAsync(string vm)
    {
        ProcessResult r;
        try
        {
            r = await _runner.RunAsync(Exe, new[] { "showvminfo", vm, "--machinereadable" }, TimeSpan.FromSeconds(60));
        }
        catch (CliException)
        {
            return VmState.Other; // VBoxManage timed out (VBoxSVC busy): not a reason to give up on the VM
        }
        if (r.Success) return ParseState(r.StdOut);
        // Only a definite "no such machine" is NotFound; any other hiccup is transient.
        var text = r.StdErr + r.StdOut;
        return text.Contains("VBOX_E_OBJECT_NOT_FOUND") || text.Contains("Could not find a registered machine")
            ? VmState.NotFound
            : VmState.Other;
    }

    public static VmState ParseState(string machineReadable)
    {
        var m = Regex.Match(machineReadable, "^VMState=\"([^\"]+)\"", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value switch
        {
            "poweroff" => VmState.PoweredOff,
            "running" => VmState.Running,
            "paused" => VmState.Paused,
            "saved" => VmState.Saved,
            "aborted" or "aborted-saved" => VmState.Aborted,
            "starting" or "restoring" => VmState.Starting,
            "stopping" or "saving" => VmState.Stopping,
            _ => VmState.Other,
        } : VmState.Other;
    }

    public async Task StartAsync(string vm, bool headless = true)
    {
        await VBox("startvm", vm, "--type", headless ? "headless" : "separate");
        // startvm returns once the VM process runs; exempt it from power throttling right away (see PowerThrottling).
        PowerThrottling.ExemptVmProcesses();
    }
    public Task SaveStateAsync(string vm) => VBox("controlvm", vm, "savestate");
    public Task PowerOffAsync(string vm) => VBox("controlvm", vm, "poweroff");
    public Task PauseAsync(string vm) => VBox("controlvm", vm, "pause");
    public Task ResumeAsync(string vm) => VBox("controlvm", vm, "resume");
    public Task DiscardSavedStateAsync(string vm) => VBox("discardstate", vm);

    public async Task ShutdownAsync(string vm, TimeSpan timeout)
    {
        await VBox("controlvm", vm, "acpipowerbutton");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await GetStateAsync(vm) is VmState.PoweredOff) return;
            await Task.Delay(2000);
        }
        throw new CliException($"The guest did not shut down within {timeout.TotalSeconds:0}s. Use 'stop --force' to power off.");
    }

    public async Task TakeSnapshotAsync(string vm, string label, string? description = null)
    {
        var state = await GetStateAsync(vm);
        var args = new List<string> { "snapshot", vm, "take", label };
        if (description is not null) { args.Add("--description"); args.Add(description); }
        if (state is VmState.Running or VmState.Paused) args.Add("--live");
        await VBox(args.ToArray());
    }

    public Task RestoreSnapshotAsync(string vm, string label) => VBox("snapshot", vm, "restore", label);
    public Task DeleteSnapshotAsync(string vm, string label) => VBox("snapshot", vm, "delete", label);

    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string vm)
    {
        var r = await _runner.RunAsync(Exe, new[] { "snapshot", vm, "list", "--machinereadable" });
        if (!r.Success) return Array.Empty<SnapshotInfo>(); // "This machine does not have any snapshots"
        return ParseSnapshots(r.StdOut);
    }

    public static IReadOnlyList<SnapshotInfo> ParseSnapshots(string machineReadable)
    {
        var current = Regex.Match(machineReadable, "^CurrentSnapshotName=\"([^\"]*)\"", RegexOptions.Multiline);
        var names = Regex.Matches(machineReadable, "^SnapshotName(?:-[\\d-]+)?=\"([^\"]*)\"", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        return names.Select(n => new SnapshotInfo(n, current.Success && current.Groups[1].Value == n)).ToList();
    }

    public Task CloneLinkedAsync(string sourceVm, string snapshot, string newVm, string baseFolder) =>
        VBox("clonevm", sourceVm, "--snapshot", snapshot, "--options", "link", "--name", newVm, "--register", "--basefolder", baseFolder);

    public async Task SetHelperPortForwardAsync(string vm, int hostPort, int guestPort)
    {
        // Deleting a non-existent rule fails; ignore that.
        await _runner.RunAsync(Exe, new[] { "modifyvm", vm, "--natpf1", "delete", PortForwardRule });
        await VBox("modifyvm", vm, "--natpf1", $"{PortForwardRule},tcp,127.0.0.1,{hostPort},,{guestPort}");
    }

    public async Task EjectInstallMediaAsync(string vm)
    {
        foreach (var port in new[] { "1", "2", "3" })
        {
            await _runner.RunAsync(Exe, new[]
            {
                "storageattach", vm, "--storagectl", StorageController, "--port", port, "--device", "0",
                "--type", "dvddrive", "--medium", "emptydrive", "--forceunmount",
            });
        }
    }

    public async Task AttachDvdAsync(string vm, int port, string iso)
    {
        // On a running VM under the Windows Hypervisor Platform, VBoxManage applies the change but may
        // not return (observed on 7.2.22). Use a short timeout and then check the configuration.
        var args = new[]
        {
            "storageattach", vm, "--storagectl", StorageController, "--port", port.ToString(), "--device", "0",
            "--type", "dvddrive", "--medium", iso, "--forceunmount",
        };
        try
        {
            var r = await _runner.RunAsync(Exe, args, TimeSpan.FromMinutes(2));
            if (r.Success) return;
            throw new CliException($"VBoxManage storageattach failed ({r.ExitCode}): {FirstLines(r.StdErr + r.StdOut)}");
        }
        catch (CliException ex) when (ex.Message.Contains("timed out"))
        {
            var info = await ShowVmInfoAsync(vm);
            var key = $"\"{StorageController}-{port}-0\"=";
            var line = info.Split('\n').FirstOrDefault(l => l.StartsWith(key));
            if (line is null || !line.Replace("\\\\", "\\").Contains(iso, StringComparison.OrdinalIgnoreCase))
                throw new CliException("Changing the DVD timed out and the new disc is not attached.");
        }
    }

    public Task ForgetMediumAsync(string path) => _runner.RunAsync(Exe, new[] { "closemedium", "dvd", path });

    /// <summary>Presses Space (scancode 0x39 make / 0xB9 break) to get past "Press any key to boot from CD or DVD".</summary>
    public Task PressBootKeyAsync(string vm) => _runner.RunAsync(Exe, new[] { "controlvm", vm, "keyboardputscancode", "39", "b9" });

    public Task SetVideoModeHintAsync(string vm, int width, int height) =>
        VBox("controlvm", vm, "setvideomodehint", width.ToString(), height.ToString(), "32");

    public async Task ShowWindowAsync(string vm)
    {
        var dir = Path.GetDirectoryName(Exe)!;
        var gui = Path.Combine(dir, "VirtualBoxVM.exe");
        if (!File.Exists(gui)) throw new CliException($"{gui} not found");
        var state = await GetStateAsync(vm);
        if (state is VmState.Running or VmState.Paused)
        {
            // Attach a separate GUI front-end to the running (headless) VM.
            Process.Start(new ProcessStartInfo(gui) { ArgumentList = { "--startvm", vm, "--separate" }, UseShellExecute = false });
        }
        else
        {
            await VBox("startvm", vm, "--type", "separate");
        }
    }

    public async Task HideWindowAsync(string vm)
    {
        // Closing only the GUI front-end of a "separate" VM leaves the VM running headless.
        var ps = $"Get-CimInstance Win32_Process -Filter \"Name='VirtualBoxVM.exe'\" | Where-Object {{ $_.CommandLine -like '*{vm}*' -and $_.CommandLine -like '*--separate*' }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force; $_.ProcessId }}";
        var r = await _runner.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", ps });
        if (string.IsNullOrWhiteSpace(r.StdOut)) throw new CliException("No VirtualBox window for this VM is open.");
    }

    public (string Host, int Port) GetGuestEndpoint(int hostPort) => ("127.0.0.1", hostPort);

    /// <summary>Saves a screenshot of the VM's display via VirtualBox itself (works before the helper exists,
    /// e.g. during Windows Setup). Returns false if the VM has no display yet.</summary>
    public async Task<bool> ScreenshotPngAsync(string vm, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var r = await _runner.RunAsync(Exe, new[] { "controlvm", vm, "screenshotpng", path }, TimeSpan.FromSeconds(30));
        LastScreenshotError = r.Success ? null : FirstLines(r.StdErr + r.StdOut);
        return r.Success;
    }

    public string? LastScreenshotError { get; private set; }

    public async Task<string> ShowVmInfoAsync(string vm) => await VBox("showvminfo", vm, "--machinereadable");

    /// <summary>Test cleanup only. There is deliberately no CLI command that deletes a VM.</summary>
    internal Task UnregisterAndDeleteAsync(string vm) => VBox("unregistervm", vm, "--delete");
}
