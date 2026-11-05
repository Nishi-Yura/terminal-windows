using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TerminalWindows.Guest;

/// <summary>
/// A process attached to a Windows pseudo console (ConPTY). It has no window on the desktop, so an
/// agent running in it can drive the GUI without ever typing into its own terminal. Output is the
/// VT/UTF-8 byte stream ConPTY renders; input is raw VT bytes.
/// </summary>
public sealed class PseudoConsoleProcess : IDisposable
{
    private IntPtr _hpc;
    private readonly IntPtr _process;
    private readonly IntPtr _thread;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private int _disposed;

    public int ProcessId { get; }
    public Stream Input => _input;
    public Stream Output => _output;

    private PseudoConsoleProcess(IntPtr hpc, PROCESS_INFORMATION pi, FileStream input, FileStream output)
    {
        _hpc = hpc;
        _process = pi.hProcess;
        _thread = pi.hThread;
        ProcessId = pi.dwProcessId;
        _input = input;
        _output = output;
    }

    public static PseudoConsoleProcess Start(string commandLine, short cols, short rows, string? workingDirectory = null)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        int hr = CreatePseudoConsole(new COORD { X = Math.Max((short)20, cols), Y = Math.Max((short)5, rows) }, inRead, outWrite, 0, out var hpc);
        Marshal.ThrowExceptionForHR(hr);
        // The pseudo console holds its own duplicates of these ends.
        inRead.Dispose();
        outWrite.Dispose();

        var attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        var attrList = Marshal.AllocHGlobal(attrSize);
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new STARTUPINFOEX { lpAttributeList = attrList };
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            // Null std handles + STARTF_USESTDHANDLES: otherwise a child of a process whose stdio is
            // redirected inherits those handles instead of talking to the pseudo console.
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            envBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock());
            var cmd = new StringBuilder(commandLine);
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, envBlock,
                    workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ref si, out var pi))
            {
                var err = new Win32Exception();
                ClosePseudoConsole(hpc);
                throw new InvalidOperationException($"Could not start '{commandLine}': {err.Message}");
            }
            return new PseudoConsoleProcess(hpc, pi,
                new FileStream(inWrite, FileAccess.Write, 1),
                new FileStream(outRead, FileAccess.Read, 1));
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }
    }

    /// <summary>
    /// The helper started at logon; tools installed later (Claude Code, Git) changed PATH in the
    /// registry. Children get a fresh PATH built from the machine and user registry values.
    /// </summary>
    public static string BuildEnvironmentBlock()
    {
        var env = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        env["Path"] = Environment.ExpandEnvironmentVariables(string.Join(';', new[] { machine, user }.Where(s => s.Length > 0)));
        env["TERM"] = "xterm-256color";
        var sb = new StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    public void Resize(short cols, short rows)
    {
        if (_hpc != IntPtr.Zero) ResizePseudoConsole(_hpc, new COORD { X = Math.Max((short)20, cols), Y = Math.Max((short)5, rows) });
    }

    private readonly CancellationTokenSource _closed = new();

    /// <summary>Cancelled when this object is disposed (e.g. the host detached), so waiters stop.</summary>
    public CancellationToken Closed => _closed.Token;

    // WAIT_OBJECT_0 = exited; WAIT_FAILED (handle already closed) is also treated as exited.
    public bool HasExited => _disposed == 1 || WaitForSingleObject(_process, 0) is 0 or 0xFFFFFFFF;

    public async Task<int> WaitForExitAsync(CancellationToken ct = default)
    {
        while (!HasExited) await Task.Delay(200, ct);
        ct.ThrowIfCancellationRequested();
        GetExitCodeProcess(_process, out var code);
        return (int)code;
    }

    /// <summary>Closing the pseudo console makes pending reads of <see cref="Output"/> return end-of-stream.</summary>
    public void ClosePseudoConsoleOnly()
    {
        var h = Interlocked.Exchange(ref _hpc, IntPtr.Zero);
        if (h != IntPtr.Zero) ClosePseudoConsole(h);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (WaitForSingleObject(_process, 0) != 0)
        {
            try { Process.GetProcessById(ProcessId).Kill(entireProcessTree: true); } catch { }
        }
        _closed.Cancel();
        ClosePseudoConsoleOnly();
        try { _input.Dispose(); } catch { }
        try { _output.Dispose(); } catch { }
        CloseHandle(_thread);
        CloseHandle(_process);
    }

    // ---- interop ----
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x00000100;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr hpc, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr hpc);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
