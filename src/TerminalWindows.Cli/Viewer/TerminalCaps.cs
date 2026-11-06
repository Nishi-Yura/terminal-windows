namespace TerminalWindows.Cli.Viewer;

public enum GraphicsProtocol
{
    None,
    Sixel,
    Kitty,
    /// <summary>Truecolor half-block characters: works in any terminal with 24-bit colour.</summary>
    Blocks,
}

public sealed record TerminalCaps(string TerminalName, GraphicsProtocol Protocol)
{
    /// <summary>
    /// Priority: kitty graphics (WezTerm) -> Sixel (Windows Terminal 1.22+, WezTerm) -> none (VirtualBox window).
    /// TERMINAL_WINDOWS_GRAPHICS=kitty|sixel|none overrides detection.
    /// </summary>
    public static TerminalCaps Detect(string? overrideProtocol = null)
    {
        var env = Environment.GetEnvironmentVariables();
        string? Get(string k) => env[k] as string;
        var name = Get("TERM_PROGRAM") is { Length: > 0 } tp ? tp
            : Get("WT_SESSION") is { Length: > 0 } ? "Windows Terminal"
            : Get("TERM") ?? "console";

        var forced = overrideProtocol ?? Get("TERMINAL_WINDOWS_GRAPHICS");
        if (!string.IsNullOrEmpty(forced))
        {
            return new TerminalCaps(name, forced.ToLowerInvariant() switch
            {
                "kitty" => GraphicsProtocol.Kitty,
                "sixel" => GraphicsProtocol.Sixel,
                "blocks" => GraphicsProtocol.Blocks,
                _ => GraphicsProtocol.None,
            });
        }

        if (string.Equals(Get("TERM_PROGRAM"), "WezTerm", StringComparison.OrdinalIgnoreCase)) return new(name, GraphicsProtocol.Kitty);
        if ((Get("TERM") ?? "").Contains("kitty", StringComparison.OrdinalIgnoreCase)) return new(name, GraphicsProtocol.Kitty);
        if (!string.IsNullOrEmpty(Get("WT_SESSION"))) return new(name, GraphicsProtocol.Sixel);
        return new(name, GraphicsProtocol.None);
    }
}
