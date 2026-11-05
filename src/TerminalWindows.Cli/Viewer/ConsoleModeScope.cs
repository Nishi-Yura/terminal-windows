using System.Runtime.InteropServices;
using TerminalWindows.Cli.Infrastructure;

namespace TerminalWindows.Cli.Viewer;

/// <summary>Puts the console into raw VT input mode with UTF-8 and restores it afterwards.</summary>
internal sealed class ConsoleModeScope : IDisposable
{
    private const int STD_INPUT = -10, STD_OUTPUT = -11;
    private const uint ENABLE_PROCESSED_INPUT = 0x1, ENABLE_LINE_INPUT = 0x2, ENABLE_ECHO_INPUT = 0x4, ENABLE_QUICK_EDIT = 0x40,
        ENABLE_EXTENDED_FLAGS = 0x80, ENABLE_VIRTUAL_TERMINAL_INPUT = 0x200, ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x4,
        DISABLE_NEWLINE_AUTO_RETURN = 0x8;

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int h);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCP(uint cp);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleOutputCP(uint cp);

    private readonly uint _inMode, _outMode, _cp, _outCp;
    private readonly IntPtr _in, _out;

    public ConsoleModeScope()
    {
        _in = GetStdHandle(STD_INPUT);
        _out = GetStdHandle(STD_OUTPUT);
        if (!GetConsoleMode(_in, out _inMode)) throw new CliException("This command needs an interactive console (stdin is redirected).");
        GetConsoleMode(_out, out _outMode);
        _cp = GetConsoleCP();
        _outCp = GetConsoleOutputCP();
        SetConsoleCP(65001);
        SetConsoleOutputCP(65001);
        uint inMode = (_inMode | ENABLE_VIRTUAL_TERMINAL_INPUT | ENABLE_EXTENDED_FLAGS)
                      & ~(ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_QUICK_EDIT);
        SetConsoleMode(_in, inMode);
        SetConsoleMode(_out, _outMode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN);
    }

    public void Dispose()
    {
        SetConsoleMode(_in, _inMode);
        SetConsoleMode(_out, _outMode);
        SetConsoleCP(_cp);
        SetConsoleOutputCP(_outCp);
    }
}
