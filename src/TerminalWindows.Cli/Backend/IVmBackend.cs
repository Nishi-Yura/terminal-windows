namespace TerminalWindows.Cli.Backend;

public enum VmState
{
    NotFound,
    PoweredOff,
    Running,
    Paused,
    Saved,
    Aborted,
    Starting,
    Stopping,
    Other,
}

public sealed record VmSpec(
    string VmName,
    string BaseFolder,
    int Cpus,
    int MemoryMb,
    int DiskMb,
    int HostPort,
    int GuestPort,
    string InstallIso,
    string PayloadIso,
    string? GuestAdditionsIso,
    string? SharedFolder,
    bool SharedFolderWritable,
    string DiskBus = "sata",
    string? Paravirt = null);

public sealed record BackendInfo(bool Installed, string? Version, string? Path, string? Problem);

public sealed record SnapshotInfo(string Name, bool IsCurrent);

/// <summary>
/// Abstraction over the hypervisor. Today only VirtualBox (works on Windows Home). A future
/// Hyper-V backend (Pro and above) would implement this and could swap the TCP/NAT transport for
/// Hyper-V sockets via <see cref="GetGuestEndpoint"/>.
/// </summary>
public interface IVmBackend
{
    string Name { get; }
    Task<BackendInfo> ProbeAsync();
    IReadOnlyList<string> PlanCreate(VmSpec spec);
    Task CreateAsync(VmSpec spec, Action<string> progress);
    Task<VmState> GetStateAsync(string vm);
    Task StartAsync(string vm, bool headless = true);
    Task SaveStateAsync(string vm);
    Task ShutdownAsync(string vm, TimeSpan timeout);
    Task PowerOffAsync(string vm);
    Task PauseAsync(string vm);
    Task ResumeAsync(string vm);
    Task DiscardSavedStateAsync(string vm);
    Task TakeSnapshotAsync(string vm, string label, string? description = null);
    Task RestoreSnapshotAsync(string vm, string label);
    Task DeleteSnapshotAsync(string vm, string label);
    Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string vm);
    Task CloneLinkedAsync(string sourceVm, string snapshot, string newVm, string baseFolder);
    Task SetHelperPortForwardAsync(string vm, int hostPort, int guestPort);
    Task EjectInstallMediaAsync(string vm);
    /// <summary>Puts a disc image into the DVD drive on the given SATA port (works while the VM runs).</summary>
    Task AttachDvdAsync(string vm, int port, string iso);
    /// <summary>Removes a detached image from the hypervisor's media registry (ignores errors).</summary>
    Task ForgetMediumAsync(string path);
    Task PressBootKeyAsync(string vm);
    /// <summary>Screenshot taken by the hypervisor (works before the guest helper exists). False if no display yet.</summary>
    Task<bool> ScreenshotPngAsync(string vm, string path);
    Task SetVideoModeHintAsync(string vm, int width, int height);
    Task ShowWindowAsync(string vm);
    Task HideWindowAsync(string vm);
    (string Host, int Port) GetGuestEndpoint(int hostPort);
}
