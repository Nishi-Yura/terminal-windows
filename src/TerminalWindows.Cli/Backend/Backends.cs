using TerminalWindows.Cli.Infrastructure;

namespace TerminalWindows.Cli.Backend;

/// <summary>Picks the hypervisor backend: per machine (machine.json "backend") or for a new machine.</summary>
public static class Backends
{
    public const string VirtualBox = "virtualbox";
    public const string Vmware = "vmware";

    public static string Validate(string name) => name.ToLowerInvariant() switch
    {
        "virtualbox" or "vbox" => VirtualBox,
        "vmware" => Vmware,
        _ => throw new UsageException("--backend must be virtualbox or vmware"),
    };

    public static IVmBackend Create(string name) => name == Vmware ? new VmwareBackend() : new VirtualBoxBackend();

    /// <summary>
    /// Default for a new machine. With Windows' hypervisor on, VirtualBox hung during setup on a real PC,
    /// so VMware Workstation is preferred there when it is installed; otherwise VirtualBox (fast when the
    /// hypervisor is off, and the only free choice that needs no account to download).
    /// </summary>
    public static string DefaultForNewMachine()
    {
        bool vbox = VirtualBoxBackend.InstallDir() is not null, vmware = VmwareBackend.IsInstalled();
        if (vmware && (Commands.Doctor.HypervisorPresent() || !vbox)) return Vmware;
        return VirtualBox;
    }

    /// <summary>The backend that owns <paramref name="vmName"/>; VirtualBox for VMs we do not know.</summary>
    public static string ForVm(string vmName)
    {
        foreach (var n in MachineStore.List())
        {
            var m = MachineStore.Load(n);
            if (m.VmName == vmName) return m.Backend;
        }
        return VirtualBox;
    }
}

/// <summary>Sends every call to the backend of the machine that owns the VM.</summary>
public sealed class RoutedBackend : IVmBackend
{
    private readonly Dictionary<string, IVmBackend> _cache = new();
    private readonly string? _forNew;

    /// <param name="backendForNewVm">Backend used by CreateAsync/PlanCreate (the machine may not be saved yet).</param>
    public RoutedBackend(string? backendForNewVm = null) => _forNew = backendForNewVm;

    private IVmBackend Get(string name)
    {
        if (!_cache.TryGetValue(name, out var b)) _cache[name] = b = Backends.Create(name);
        return b;
    }

    private IVmBackend For(string vm) => Get(Backends.ForVm(vm));
    private IVmBackend ForNew => Get(_forNew ?? Backends.VirtualBox);

    public string Name => ForNew.Name;
    public Task<BackendInfo> ProbeAsync() => ForNew.ProbeAsync();
    public IReadOnlyList<string> PlanCreate(VmSpec spec) => ForNew.PlanCreate(spec);
    public Task CreateAsync(VmSpec spec, Action<string> progress) => ForNew.CreateAsync(spec, progress);
    public Task<VmState> GetStateAsync(string vm) => For(vm).GetStateAsync(vm);
    public Task StartAsync(string vm, bool headless = true) => For(vm).StartAsync(vm, headless);
    public Task SaveStateAsync(string vm) => For(vm).SaveStateAsync(vm);
    public Task ShutdownAsync(string vm, TimeSpan timeout) => For(vm).ShutdownAsync(vm, timeout);
    public Task PowerOffAsync(string vm) => For(vm).PowerOffAsync(vm);
    public Task PauseAsync(string vm) => For(vm).PauseAsync(vm);
    public Task ResumeAsync(string vm) => For(vm).ResumeAsync(vm);
    public Task DiscardSavedStateAsync(string vm) => For(vm).DiscardSavedStateAsync(vm);
    public Task TakeSnapshotAsync(string vm, string label, string? description = null) => For(vm).TakeSnapshotAsync(vm, label, description);
    public Task RestoreSnapshotAsync(string vm, string label) => For(vm).RestoreSnapshotAsync(vm, label);
    public Task DeleteSnapshotAsync(string vm, string label) => For(vm).DeleteSnapshotAsync(vm, label);
    public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string vm) => For(vm).ListSnapshotsAsync(vm);
    public Task CloneLinkedAsync(string sourceVm, string snapshot, string newVm, string baseFolder) => For(sourceVm).CloneLinkedAsync(sourceVm, snapshot, newVm, baseFolder);
    public Task SetHelperPortForwardAsync(string vm, int hostPort, int guestPort) => For(vm).SetHelperPortForwardAsync(vm, hostPort, guestPort);
    public Task EjectInstallMediaAsync(string vm) => For(vm).EjectInstallMediaAsync(vm);
    public Task AttachDvdAsync(string vm, int port, string iso) => For(vm).AttachDvdAsync(vm, port, iso);
    public Task ForgetMediumAsync(string path) =>
        VirtualBoxBackend.InstallDir() is null ? Task.CompletedTask : Get(Backends.VirtualBox).ForgetMediumAsync(path);
    public Task PressBootKeyAsync(string vm) => For(vm).PressBootKeyAsync(vm);
    public Task<bool> ScreenshotPngAsync(string vm, string path) => For(vm).ScreenshotPngAsync(vm, path);
    public Task SetVideoModeHintAsync(string vm, int width, int height) => For(vm).SetVideoModeHintAsync(vm, width, height);
    public Task ShowWindowAsync(string vm) => For(vm).ShowWindowAsync(vm);
    public Task HideWindowAsync(string vm) => For(vm).HideWindowAsync(vm);
    public (string Host, int Port) GetGuestEndpoint(int hostPort) => ("127.0.0.1", hostPort);
}
