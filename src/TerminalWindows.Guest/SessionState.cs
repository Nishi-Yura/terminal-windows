using System.Text;
using static TerminalWindows.Guest.Native;

namespace TerminalWindows.Guest;

public static class SessionState
{
    private const uint DESKTOP_READOBJECTS = 0x0001;

    /// <summary>
    /// Name of the desktop currently receiving input. "Default" = the normal user desktop.
    /// "Winlogon" (or access denied) = lock screen, logon screen, or a UAC secure desktop.
    /// The helper only reports this; it never tries to unlock.
    /// </summary>
    public static string InputDesktop()
    {
        var h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return "unavailable";
        try
        {
            var sb = new StringBuilder(256);
            return GetUserObjectInformationW(h, UOI_NAME, sb, sb.Capacity * 2, out _) ? sb.ToString() : "unknown";
        }
        finally
        {
            CloseDesktop(h);
        }
    }

    public static string Describe()
    {
        var d = InputDesktop();
        return d.Equals("Default", StringComparison.OrdinalIgnoreCase) ? "desktop" : "locked_or_secure_desktop";
    }
}
