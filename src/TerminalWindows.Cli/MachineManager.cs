using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TerminalWindows.Cli.Backend;
using TerminalWindows.Cli.Infrastructure;
using TerminalWindows.Cli.Setup;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli;

public sealed record CreateOptions(
    string Name,
    string Iso,
    int Cpus = 4,
    int MemoryGb = 8,
    int DiskGb = 64,
    int Width = 1600,
    int Height = 900,
    bool Offline = false,
    string Locale = "ja-JP",
    string? UiLanguage = null,
    string? TimeZone = null,
    int? ImageIndex = null,
    string? ProductKey = null,
    bool AutoSnapshot = false,
    string? Share = null,
    bool ShareWritable = false,
    string? Edition = null,
    string DiskBus = "sata",
    string? Paravirt = null,
    string? Backend = null);

/// <summary>VM lifecycle orchestration shared by the CLI commands and the MCP server.</summary>
public sealed class MachineManager
{
    public const string CleanSetupSnapshot = "clean-setup";
    /// <summary>Disk the VM actually writes during setup (the VDI is dynamic; 64 GB is only its maximum).</summary>
    public const int MinFreeGbForInstall = 25;
    private readonly IVmBackend _backend;
    private readonly Action<string> _log;

    public MachineManager(IVmBackend? backend = null, Action<string>? log = null)
    {
        _backend = backend ?? new RoutedBackend();
        _log = log ?? (s => Console.Error.WriteLine(s));
    }

    public IVmBackend Backend => _backend;

    public static int PickFreePort()
    {
        for (int i = 0; i < 50; i++)
        {
            int port = RandomNumberGenerator.GetInt32(20000, 60000);
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                l.Stop();
                if (!MachineStore.List().Any(n => MachineStore.Load(n).HostPort == port)) return port;
            }
            catch (SocketException) { }
        }
        throw new CliException("Could not find a free local port");
    }

    /// <summary>The backend that creates a new machine (the router only knows machines that already exist).</summary>
    private IVmBackend CreatorFor(string backend) => _backend is RoutedBackend ? Backends.Create(backend) : _backend;

    public static VmSpec SpecFor(MachineConfig m, string iso, string payloadIso, string? share = null, bool shareWritable = false) => new(
        m.VmName, MachineStore.VmBaseFolder(m.Name), m.Cpus, m.MemoryGb * 1024, m.DiskGb * 1024,
        m.HostPort, ProtocolConstants.GuestPort, iso, payloadIso, VirtualBoxBackend.GuestAdditionsIso(), share, shareWritable, m.DiskBus, m.Paravirt);

    public static string PayloadIsoPath(string name) => Path.Combine(MachineStore.MachineDir(name), "setup-payload.iso");

    public IReadOnlyList<string> Plan(CreateOptions o)
    {
        var steps = new List<string>();
        var name = MachineStore.ValidateName(o.Name);
        if (MachineStore.Exists(name))
        {
            steps.Add($"Machine '{name}' exists: it will NOT be reinstalled.");
            steps.Add($"VBoxManage startvm {MachineStore.VmNameFor(name)} --type headless   (if not running)");
            steps.Add("Wait for the guest helper on 127.0.0.1:<port>, then open the viewer.");
            return steps;
        }
        var m = new MachineConfig { Name = name, VmName = MachineStore.VmNameFor(name), HostPort = 0, Cpus = o.Cpus, MemoryGb = o.MemoryGb, DiskGb = o.DiskGb, DiskBus = o.DiskBus };
        steps.Add($"Create {MachineStore.MachineDir(name)} (machine.json with DPAPI-protected token/password, user-only ACL)");
        steps.Add($"Generate autounattend.xml (user '{m.User}', locale {o.Locale}, auto-logon, OOBE skipped) and build {PayloadIsoPath(name)}");
        var backendName = o.Backend ?? Backends.DefaultForNewMachine();
        steps.Add($"Backend: {backendName}");
        steps.AddRange(CreatorFor(backendName).PlanCreate(SpecFor(m, o.Iso, PayloadIsoPath(name), o.Share, o.ShareWritable)).Select(s => s.Replace(",0,,", ",<random-port>,,")));
        steps.Add($"VBoxManage startvm {m.VmName} --type headless  (+ press Space to boot from DVD)");
        steps.Add("Wait for unattended setup: Windows install -> first logon -> helper install -> Guest Additions -> reboot");
        steps.Add($"Set resolution {o.Width}x{o.Height}, eject install media, delete the setup ISO, take snapshot '{CleanSetupSnapshot}'");
        return steps;
    }

    /// <param name="waitForSetup">false: return once the VM has booted the installer; a later
    /// start/up/status finishes setup (eject media, snapshot) when the helper answers.</param>
    public async Task<MachineConfig> CreateAsync(CreateOptions o, bool waitForSetup = true)
    {
        var name = MachineStore.ValidateName(o.Name);
        if (MachineStore.Exists(name)) throw new CliException($"Machine '{name}' already exists (it is never reinstalled).");
        if (!File.Exists(o.Iso)) throw new CliException($"ISO not found: {o.Iso}");
        if (o.Cpus < 1 || o.MemoryGb < 4 || o.DiskGb < 64)
            throw new CliException("Windows 11 needs at least 1 CPU, 4 GB memory and 64 GB disk.");
        var helper = PayloadIso.FindHelperExe() ?? throw new CliException(
            "TerminalWindowsGuest.exe not found next to terminal-windows.exe (guest\\TerminalWindowsGuest.exe). Run build.ps1 or set TERMINAL_WINDOWS_GUEST_EXE.");
        var backendName = o.Backend ?? Backends.DefaultForNewMachine();
        var creator = CreatorFor(backendName);
        var probe = await creator.ProbeAsync();
        if (!probe.Installed || probe.Problem is not null) throw new CliException($"{backendName} problem: " + (probe.Problem ?? "not installed") + ". Run 'terminal-windows doctor'.");
        bool vmware = backendName == Backends.Vmware;

        Directory.CreateDirectory(MachineStore.MachinesDir);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(MachineStore.MachinesDir))!).AvailableFreeSpace / 1024.0 / 1024 / 1024;
        if (free < MinFreeGbForInstall)
            throw new CliException($"Only {free:0} GB free on the data drive; a Windows 11 install needs about {MinFreeGbForInstall} GB. " +
                                   "Free some space or set TERMINAL_WINDOWS_HOME to another drive.");

        var (imageIndex, productKey) = ResolveEdition(o.Iso, o.Edition, o.ImageIndex, o.ProductKey);

        var token = Secrets.NewToken();
        var password = Secrets.NewPassword();
        var m = new MachineConfig
        {
            Name = name,
            VmName = MachineStore.VmNameFor(name),
            HostPort = PickFreePort(),
            Cpus = o.Cpus,
            MemoryGb = o.MemoryGb,
            DiskGb = o.DiskGb,
            DiskBus = o.DiskBus,
            Paravirt = o.Paravirt,
            Width = o.Width,
            Height = o.Height,
            Locale = o.Locale,
            UiLanguage = o.UiLanguage ?? o.Locale,
            Offline = o.Offline,
            AutoSnapshot = o.AutoSnapshot,
            IsoPath = Path.GetFullPath(o.Iso),
            ProtectedToken = Secrets.Protect(token),
            ProtectedPassword = Secrets.Protect(password),
            Backend = backendName,
        };
        if (vmware)
        {
            m.MacAddress = VmwareBackend.NewMac();
            m.VncPort = PickFreePort();
            m.ProtectedVncPassword = Secrets.Protect(Secrets.NewPassword(8));
            m.AllowedRemote = VmwareBackend.NatSubnet() ?? throw new CliException(
                "VMware's NAT network (VMnet8) was not found. Open VMware Workstation > Edit > Virtual Network Editor and restore the defaults.");
        }
        Directory.CreateDirectory(MachineStore.OutboxDir(name));
        Directory.CreateDirectory(MachineStore.InboxDir(name));
        MachineStore.Save(m);

        if (!vmware && Commands.Doctor.HypervisorPresent())
            _log("WARNING: the Windows hypervisor is running on this PC, so VirtualBox goes through it. Setup is much slower and may hang. " +
                 "See 'terminal-windows host-mode' (turning it off needs an admin terminal and a reboot).");
        _log("Building setup ISO (autounattend.xml + guest helper)...");
        var unattend = UnattendGenerator.Generate(new UnattendOptions(
            UnattendGenerator.ComputerNameFor(name), m.User, password, m.Locale, m.UiLanguage,
            o.TimeZone ?? UnattendGenerator.DefaultTimeZone(m.Locale), imageIndex, productKey, BypassTpmCheck: vmware));
        var payload = PayloadIsoPath(name);
        PayloadIso.Build(payload, unattend, PayloadIso.GuestJson(m, token, password), helper);

        _log($"Creating {(vmware ? "VMware" : "VirtualBox")} VM {m.VmName}...");
        await creator.CreateAsync(SpecFor(m, m.IsoPath, payload, o.Share, o.ShareWritable), s => _log("  " + s));

        _log("Starting unattended Windows setup (headless). This usually takes 20-60 minutes.");
        await _backend.StartAsync(m.VmName);
        await PressBootKeysAsync(m);
        if (!waitForSetup) return m;
        await WaitForHelperAsync(m, TimeSpan.FromMinutes(240), installing: true);
        await FinalizeSetupAsync(m);
        return m;
    }

    /// <summary>Picks the image index and the key that lets unattended Setup install it without prompting.</summary>
    private (int? Index, string? Key) ResolveEdition(string iso, string? edition, int? imageIndex, string? productKey)
    {
        try
        {
            var images = IsoInspector.ReadImages(iso);
            var pick = imageIndex is int ix
                ? images.FirstOrDefault(i => i.Index == ix) ?? throw new CliException($"The ISO has no image index {ix}")
                : IsoInspector.Choose(images, edition);
            _log($"Edition: {pick.Name} (index {pick.Index} of {images.Count}). Change it with --edition.");
            if (productKey is null && images.Count > 1)
            {
                // Multi-edition media stops at the product-key page without a key (verified on 26300).
                productKey = UnattendGenerator.InstallKeyFor(pick.EditionId) ?? throw new CliException(
                    $"{pick.Name} has no published setup key. Pass your own --product-key, or choose --edition Pro.");
                _log("  using Microsoft's published KMS client setup key to select the edition (it does not activate Windows).");
            }
            return (pick.Index, productKey);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            if (edition is not null) throw new CliException(ex.Message);
            _log("  warning: could not read the editions in the ISO (" + ex.Message + "); setup may stop at edition selection.");
            return (imageIndex, productKey);
        }
    }

    /// <summary>
    /// Rebuilds the setup ISO with the current guest helper (same token, password and answers) and swaps it
    /// into the running VM. Useful while Windows Setup is still copying files: the helper is read from this
    /// disc only at first logon, so a fixed helper can still be delivered.
    /// </summary>
    public async Task<string> RefreshPayloadAsync(MachineConfig m, string? edition = null)
    {
        if (m.SetupComplete) throw new CliException($"'{m.Name}' has finished setup; use 'update-helper' instead.");
        // Swapping the DVD of a RUNNING VM froze the VM in a real run (VirtualBox 7.2 on the Windows
        // Hypervisor Platform: the suspend for the medium change never finished). Only allow it powered off.
        var state = await _backend.GetStateAsync(m.VmName);
        if (state != VmState.PoweredOff)
            throw new CliException($"{m.VmName} is {state}. The setup disc can only be replaced while the VM is powered off.");
        var helper = PayloadIso.FindHelperExe() ?? throw new CliException("No bundled TerminalWindowsGuest.exe found");
        var (imageIndex, productKey) = ResolveEdition(m.IsoPath ?? throw new CliException("Unknown install ISO"), edition, null, null);
        var unattend = UnattendGenerator.Generate(new UnattendOptions(
            UnattendGenerator.ComputerNameFor(m.Name), m.User, m.Password, m.Locale, m.UiLanguage,
            UnattendGenerator.DefaultTimeZone(m.Locale), imageIndex, productKey, BypassTpmCheck: m.Backend == Backends.Vmware));
        var path = Path.Combine(MachineStore.MachineDir(m.Name), $"setup-payload-{DateTime.Now:yyyyMMddHHmmss}.iso");
        PayloadIso.Build(path, unattend, PayloadIso.GuestJson(m, m.Token, m.Password), helper);
        await _backend.AttachDvdAsync(m.VmName, 2, path);
        foreach (var old in OldPayloads(m.Name).Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            await _backend.ForgetMediumAsync(old);
        return path;
    }

    private static IEnumerable<string> OldPayloads(string name) =>
        Directory.Exists(MachineStore.MachineDir(name))
            ? Directory.GetFiles(MachineStore.MachineDir(name), "setup-payload*.iso")
            : Enumerable.Empty<string>();

    public static string SetupScreenPath(string name) => Path.Combine(MachineStore.LogsDir(name), "setup-screen.png");

    /// <summary>
    /// "Press any key to boot from CD or DVD..." is shown for ~5 s once the firmware has a display.
    /// Under the Windows Hypervisor Platform (WSL2 etc.) the firmware needs ~50 s to get there, so we
    /// first wait for the display to exist, then keep pressing Space for a while.
    /// </summary>
    private async Task PressBootKeysAsync(MachineConfig m)
    {
        var shot = SetupScreenPath(m.Name);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(5) && !await _backend.ScreenshotPngAsync(m.VmName, shot))
            await Task.Delay(1000);
        _log($"  firmware display is up after {sw.Elapsed.TotalSeconds:0}s; pressing Space to boot from DVD");
        for (int i = 0; i < 60; i++)
        {
            await _backend.PressBootKeyAsync(m.VmName);
            await Task.Delay(700);
        }
    }

    public async Task WaitForHelperAsync(MachineConfig m, TimeSpan timeout, bool installing = false)
    {
        // An idle-sleep during Windows Setup breaks the install (seen in a real run), so keep the
        // machine awake while we wait. This is a per-process request (like a video player), not a
        // settings change; Windows drops it when this process exits.
        using var awake = installing ? new KeepAwake() : null;
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        string lastScreenHash = "";
        var lastScreenChange = TimeSpan.Zero;
        while (sw.Elapsed < timeout)
        {
            var state = await _backend.GetStateAsync(m.VmName);
            if (state is VmState.PoweredOff or VmState.Aborted or VmState.NotFound)
                throw new CliException($"VM stopped unexpectedly (state: {state}). Inspect it with 'terminal-windows show {m.Name}'.");
            // Covers VM processes started before this command (or a GUI window attached later).
            if (PowerThrottling.ExemptVmProcesses() > 0)
                _log("  VM process exempted from Windows power throttling.");
            // During setup, "reachable" is not enough: the guest must report setup phase "complete"
            // (the helper can be reachable before the post-install reboot on older guest builds).
            if (state == VmState.Running && await GuestClient.IsReadyAsync(m, requireSetupComplete: installing)) return;
            if (sw.Elapsed - lastReport > (installing ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(10)))
            {
                lastReport = sw.Elapsed;
                if (installing)
                {
                    // A hypervisor-level screenshot shows where setup is (or where it got stuck).
                    var shot = SetupScreenPath(m.Name);
                    await _backend.ScreenshotPngAsync(m.VmName, shot);
                    _log($"  ...setup in progress ({sw.Elapsed:hh\\:mm\\:ss}). Latest screen: {shot}");
                    // Stall detection: Windows Setup's spinner changes the picture constantly, so an
                    // identical screenshot for 20 minutes means the VM (or setup) is stuck.
                    var hash = File.Exists(shot) ? SafePath.Sha256File(shot) : "";
                    if (hash.Length > 0 && hash == lastScreenHash)
                    {
                        if (sw.Elapsed - lastScreenChange > TimeSpan.FromMinutes(20))
                            _log($"  WARNING: the VM screen has not changed for {(sw.Elapsed - lastScreenChange).TotalMinutes:0} minutes. " +
                                 $"Setup may be stuck; check {shot} or 'terminal-windows show {m.Name}'.");
                    }
                    else
                    {
                        lastScreenHash = hash;
                        lastScreenChange = sw.Elapsed;
                    }
                }
                else
                {
                    _log(Ui.T($"  …Windows の起動を待っています（{sw.Elapsed:mm\\:ss}。普通は 1 分以内）", $"  ...waiting for Windows to start ({sw.Elapsed:mm\\:ss}; usually under a minute)"));
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(installing ? 10 : 2));
        }
        throw new CliException($"The guest helper did not respond within {timeout.TotalMinutes:0} minutes. Use 'terminal-windows show {m.Name}' to look at the VM.");
    }

    /// <summary>Runs once after unattended setup: resolution, eject media, delete secrets ISO, snapshot.</summary>
    public async Task FinalizeSetupAsync(MachineConfig m)
    {
        if (m.SetupComplete) return;
        _log("Guest helper is up. Finalising setup...");
        try { await _backend.SetVideoModeHintAsync(m.VmName, m.Width, m.Height); }
        catch (CliException ex) { _log("  resolution hint failed (Guest Additions missing?): " + ex.Message); }
        await using (var c = await GuestClient.ConnectAsync(m))
        {
            try { RecordSize(m, await c.CallAsync("set_resolution", new() { ["width"] = m.Width, ["height"] = m.Height })); } catch { }
        }
        await _backend.EjectInstallMediaAsync(m.VmName);
        // The setup ISOs contain the token and the auto-logon password: unregister and delete them all.
        foreach (var payload in OldPayloads(m.Name).ToList())
        {
            await _backend.ForgetMediumAsync(payload);
            try { File.Delete(payload); } catch (IOException ex) { _log($"  could not delete {payload}: {ex.Message}"); }
        }
        await Task.Delay(3000);
        // Snapshot a powered-off VM: a live snapshot would also write the guest's RAM (several GB) to disk.
        // Restoring clean-setup then cold-boots, and auto-logon brings the helper back.
        await SnapshotPoweredOffAsync(m, CleanSetupSnapshot, "terminal-windows: unattended setup finished", () =>
        {
            m.SetupComplete = true;
            MachineStore.Save(m);
        });
        _log("Setup complete.");
    }

    public async Task EnsureRunningAsync(MachineConfig m)
    {
        var state = await _backend.GetStateAsync(m.VmName);
        switch (state)
        {
            case VmState.NotFound:
                throw new CliException($"{m.Backend} has no VM named {m.VmName}. It may have been removed outside terminal-windows.");
            case VmState.Paused:
                await _backend.ResumeAsync(m.VmName);
                break;
            case VmState.PoweredOff or VmState.Saved or VmState.Aborted:
                _log(Ui.T($"VM を起動しています（{m.VmName}）…", $"Starting {m.VmName} (headless)..."));
                await _backend.StartAsync(m.VmName);
                break;
        }
        await WaitForHelperAsync(m, m.SetupComplete ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(240), installing: !m.SetupComplete);
        if (!m.SetupComplete) await FinalizeSetupAsync(m);
        await EnsureHelperCurrentAsync(m);
        await EnsureResolutionAsync(m);
    }

    /// <summary>
    /// Re-applies the configured resolution when the guest runs at another one. On VMware the display
    /// driver (VMware Tools) only becomes active a reboot after setup applied the resolution once.
    /// </summary>
    /// <summary>The guest may pick the closest mode its display driver has; remember what it used.</summary>
    private static void RecordSize(MachineConfig m, System.Text.Json.Nodes.JsonObject reply)
    {
        if (reply["width"]?.GetValue<int>() is int w && reply["height"]?.GetValue<int>() is int h && (w != m.Width || h != m.Height))
        {
            m.Width = w; m.Height = h;
            MachineStore.Save(m);
        }
    }

    public async Task EnsureResolutionAsync(MachineConfig m)
    {
        try
        {
            await using var c = await GuestClient.ConnectAsync(m);
            var screen = (await c.CallAsync("status"))["screen"];
            if (screen?["width"]?.GetValue<int>() == m.Width && screen?["height"]?.GetValue<int>() == m.Height) return;
            var r = await c.CallAsync("set_resolution", new() { ["width"] = m.Width, ["height"] = m.Height });
            if (r["ok"]?.GetValue<bool>() != true)
            {
                _log($"  could not set {m.Width}x{m.Height} (display driver result {r["result"]}).");
                return;
            }
            RecordSize(m, r);
            _log($"Screen set to {m.Width}x{m.Height}.");
        }
        catch (Exception ex) when (ex is CliException or Protocol.RpcException or TimeoutException or IOException) { }
    }

    /// <summary>Self-update: if the bundled helper differs from the one running in the guest, send it over.</summary>
    public async Task EnsureHelperCurrentAsync(MachineConfig m)
    {
        var local = PayloadIso.FindHelperExe();
        if (local is null) return;
        var localSha = SafePath.Sha256File(local);
        await using var c = await GuestClient.ConnectAsync(m);
        var remoteSha = c.Hello?["helperSha256"]?.GetValue<string>();
        if (string.IsNullOrEmpty(remoteSha) || remoteSha.Equals(localSha, StringComparison.OrdinalIgnoreCase)) return;
        _log("Updating the guest helper...");
        if (!await c.UpdateHelperAsync(local)) throw new CliException("The guest rejected the helper update (hash mismatch).");
        await c.DisposeAsync();
        await Task.Delay(4000);
        await WaitForHelperAsync(m, TimeSpan.FromMinutes(2));
        // Verify the swap really happened (e.g. a read-only exe makes 'move' fail and the old helper restarts).
        await using var after = await GuestClient.ConnectAsync(m);
        var nowSha = after.Hello?["helperSha256"]?.GetValue<string>();
        if (!string.Equals(nowSha, localSha, StringComparison.OrdinalIgnoreCase))
            throw new CliException("The helper restarted but is still the old build (the file swap failed inside the guest). " +
                                   @"See C:\ProgramData\terminal-windows\logs\helper.log in the guest.");
        _log("Guest helper updated.");
    }

    public async Task StopAsync(MachineConfig m, string mode, bool autoSnapshot)
    {
        var state = await _backend.GetStateAsync(m.VmName);
        if (state is VmState.Running or VmState.Paused)
        {
            switch (mode)
            {
                case "force": await _backend.PowerOffAsync(m.VmName); break;
                case "shutdown": await _backend.ShutdownAsync(m.VmName, TimeSpan.FromMinutes(3)); break;
                default: await _backend.SaveStateAsync(m.VmName); break; // keeps open apps and windows
            }
        }
        else
        {
            _log($"{m.VmName} is not running ({state}).");
        }
        if (autoSnapshot || m.AutoSnapshot) await AutoSnapshotAsync(m);
    }

    /// <summary>Powers the VM off, unregisters it with its disks and snapshots, and removes the machine folder.</summary>
    public async Task DeleteAsync(MachineConfig m)
    {
        var state = await _backend.GetStateAsync(m.VmName);
        if (state is VmState.Running or VmState.Paused)
        {
            _log($"Powering off {m.VmName}...");
            await _backend.PowerOffAsync(m.VmName);
            for (int i = 0; i < 60 && await _backend.GetStateAsync(m.VmName) is VmState.Running or VmState.Paused; i++)
                await Task.Delay(1000);
            await Task.Delay(2000); // the VM process releases its disk locks just after the state flips
        }
        if (state is not VmState.NotFound)
        {
            _log($"Unregistering {m.VmName} and deleting its disks...");
            if (m.Backend == Backends.Vmware) await new VmwareBackend().DeleteVmAsync(m.VmName);
            else await new VirtualBoxBackend().UnregisterAndDeleteAsync(m.VmName);
        }
        var dir = MachineStore.MachineDir(m.Name);
        for (int attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); break; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10) { await Task.Delay(1000); }
        }
    }

    public async Task AutoSnapshotAsync(MachineConfig m)
    {
        var label = "auto-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        await _backend.TakeSnapshotAsync(m.VmName, label, "terminal-windows automatic snapshot");
        _log($"Snapshot '{label}' taken.");
        // Prune only automatic snapshots; manual ones are never deleted.
        var autos = (await _backend.ListSnapshotsAsync(m.VmName)).Where(s => s.Name.StartsWith("auto-")).Select(s => s.Name).OrderBy(n => n).ToList();
        foreach (var old in autos.Take(Math.Max(0, autos.Count - Math.Max(1, m.AutoSnapshotKeep))))
        {
            await _backend.DeleteSnapshotAsync(m.VmName, old);
            _log($"Deleted old snapshot '{old}'.");
        }
    }

    /// <summary>
    /// Shuts the guest down, takes the snapshot and starts it again. Unlike a live snapshot it stores no
    /// copy of the guest's RAM (6-8 GB less on the disk), and the restart applies settings that need one.
    /// </summary>
    public async Task SnapshotPoweredOffAsync(MachineConfig m, string label, string description, Action? beforeStart = null)
    {
        _log("Shutting the guest down for the snapshot (no copy of its memory is stored)...");
        if (await _backend.GetStateAsync(m.VmName) is VmState.Running or VmState.Paused)
        {
            try
            {
                await _backend.ShutdownAsync(m.VmName, TimeSpan.FromMinutes(5));
            }
            catch (CliException)
            {
                _log("  ACPI shutdown timed out; powering off.");
                await _backend.PowerOffAsync(m.VmName);
                await WaitForStateAsync(m, s => s is VmState.PoweredOff or VmState.Aborted, TimeSpan.FromMinutes(1));
            }
        }
        _log($"Taking snapshot '{label}'...");
        await _backend.TakeSnapshotAsync(m.VmName, label, description);
        beforeStart?.Invoke();
        await _backend.StartAsync(m.VmName);
        await WaitForHelperAsync(m, TimeSpan.FromMinutes(10));
    }

    public async Task<string> SnapshotAsync(MachineConfig m, string? label)
    {
        label ??= "snap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (label.Any(c => c is '"' or '\n' or '\r')) throw new CliException("Invalid snapshot label");
        await _backend.TakeSnapshotAsync(m.VmName, label, "terminal-windows snapshot");
        return label;
    }

    /// <summary>
    /// Restores a snapshot. The current state is always saved first as pre-restore-YYYYMMDD-HHMMSS
    /// so that a wrong restore never loses work. A running VM is powered off for the restore and restarted.
    /// </summary>
    public async Task<string> RestoreAsync(MachineConfig m, string label)
    {
        var snaps = await _backend.ListSnapshotsAsync(m.VmName);
        if (!snaps.Any(s => s.Name == label))
            throw new CliException($"No snapshot named '{label}'. Available: {string.Join(", ", snaps.Select(s => s.Name))}");

        var state = await _backend.GetStateAsync(m.VmName);
        var safety = "pre-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        await _backend.TakeSnapshotAsync(m.VmName, safety, $"terminal-windows: state before restoring '{label}'");
        _log($"Saved current state as '{safety}'.");

        bool wasRunning = state is VmState.Running or VmState.Paused;
        if (wasRunning) await _backend.PowerOffAsync(m.VmName);
        await WaitForStateAsync(m, s => s is VmState.PoweredOff or VmState.Saved or VmState.Aborted, TimeSpan.FromMinutes(1));
        await _backend.RestoreSnapshotAsync(m.VmName, label);
        _log($"Restored '{label}'.");
        if (wasRunning)
        {
            await _backend.StartAsync(m.VmName);
            await WaitForHelperAsync(m, TimeSpan.FromMinutes(5));
        }
        return safety;
    }

    private async Task WaitForStateAsync(MachineConfig m, Func<VmState, bool> ok, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (ok(await _backend.GetStateAsync(m.VmName))) return;
            await Task.Delay(1000);
        }
    }

    public async Task<MachineConfig> CloneAsync(MachineConfig src, string newName, string snapshot)
    {
        newName = MachineStore.ValidateName(newName);
        if (MachineStore.Exists(newName)) throw new CliException($"Machine '{newName}' already exists.");
        if (!src.SetupComplete) throw new CliException($"'{src.Name}' has not finished setup yet.");
        var snaps = await _backend.ListSnapshotsAsync(src.VmName);
        if (!snaps.Any(s => s.Name == snapshot)) throw new CliException($"'{src.Name}' has no snapshot '{snapshot}'.");

        var m = new MachineConfig
        {
            Name = newName,
            VmName = MachineStore.VmNameFor(newName),
            HostPort = PickFreePort(),
            Cpus = src.Cpus, MemoryGb = src.MemoryGb, DiskGb = src.DiskGb, DiskBus = src.DiskBus, Paravirt = src.Paravirt,
            Width = src.Width, Height = src.Height, Locale = src.Locale, UiLanguage = src.UiLanguage,
            User = src.User, Offline = src.Offline, AutoSnapshot = src.AutoSnapshot, AutoSnapshotKeep = src.AutoSnapshotKeep,
            // The guest image already contains this token/password, so the clone shares them.
            ProtectedToken = src.ProtectedToken, ProtectedPassword = src.ProtectedPassword,
            ClonedFrom = $"{src.Name}@{snapshot}",
            SetupComplete = true,
            Backend = src.Backend, AllowedRemote = src.AllowedRemote,
        };
        if (src.Backend == Backends.Vmware)
        {
            m.MacAddress = VmwareBackend.NewMac();
            m.VncPort = PickFreePort();
            m.ProtectedVncPassword = Secrets.Protect(Secrets.NewPassword(8));
            MachineStore.Save(m); // the VMware backend finds a VM's files through its machine.json
        }
        Directory.CreateDirectory(MachineStore.OutboxDir(newName));
        Directory.CreateDirectory(MachineStore.InboxDir(newName));

        _log($"Creating linked clone {m.VmName} from {src.VmName}@{snapshot}...");
        await _backend.CloneLinkedAsync(src.VmName, snapshot, m.VmName, MachineStore.VmBaseFolder(newName));
        if (m.Backend == Backends.Vmware)
            VmwareBackend.SetIdentity(VmwareBackend.VmxPath(MachineStore.VmBaseFolder(newName), m.VmName), m.MacAddress!, m.VncPort, m.VncPassword);
        if (await _backend.GetStateAsync(m.VmName) == VmState.Saved)
        {
            // NAT rules cannot be changed while a saved state exists; the clone cold-boots instead (auto-logon).
            await _backend.DiscardSavedStateAsync(m.VmName);
        }
        await _backend.SetHelperPortForwardAsync(m.VmName, m.HostPort, ProtocolConstants.GuestPort);
        MachineStore.Save(m);
        return m;
    }
}
