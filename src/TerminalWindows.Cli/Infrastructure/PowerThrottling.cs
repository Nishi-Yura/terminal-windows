using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TerminalWindows.Cli.Infrastructure;

/// <summary>
/// Windows 11 applies "power throttling" (EcoQoS) to processes without a visible window: their CPU speed
/// is lowered and their timer-resolution requests are ignored. A headless VirtualBox VM is such a process,
/// and under the Windows Hypervisor Platform that made the guest's clock fall hours behind until Windows
/// Setup hung (real run). This opts the VM processes out, per process, through SetProcessInformation:
/// no system setting is changed and it ends when the VM process exits.
/// </summary>
public static class PowerThrottling
{
    private static readonly string[] VmProcessNames = { "VBoxHeadless", "VirtualBoxVM", "vmware-vmx" };

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;
    private const uint PROCESS_SET_INFORMATION = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static readonly HashSet<int> Done = new();

    /// <summary>The control/state masks that turn both kinds of throttling off (control bit set, state bit clear).</summary>
    public static (uint Control, uint State) ExemptMasks =>
        (PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION, 0);

    /// <summary>Exempts every running VirtualBox VM process of this user. Returns how many were newly exempted.</summary>
    public static int ExemptVmProcesses()
    {
        int count = 0;
        foreach (var name in VmProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    lock (Done) { if (Done.Contains(p.Id)) continue; }
                    if (Exempt(p.Id))
                    {
                        lock (Done) Done.Add(p.Id);
                        count++;
                    }
                }
            }
        }
        return count;
    }

    public static bool Exempt(int pid)
    {
        var h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var (control, state) = ExemptMasks;
            var s = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = control,
                StateMask = state,
            };
            return SetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        finally { CloseHandle(h); }
    }
}
