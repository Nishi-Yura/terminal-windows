using System.Runtime.InteropServices;

namespace TerminalWindows.Cli.Infrastructure;

/// <summary>
/// Asks Windows not to idle-sleep while a long operation runs (SetThreadExecutionState with
/// ES_SYSTEM_REQUIRED | ES_CONTINUOUS on a dedicated thread). It changes no settings: the request
/// ends on Dispose or when the process exits. A manual sleep or closing a laptop lid still sleeps.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;

    public KeepAwake()
    {
        // The request belongs to the calling thread, so hold it on a thread we own for the whole duration.
        _thread = new Thread(() =>
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
            _stop.Wait();
            SetThreadExecutionState(ES_CONTINUOUS);
        }) { IsBackground = true, Name = "keep-awake" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}
