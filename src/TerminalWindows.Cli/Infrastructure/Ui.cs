using System.Globalization;

namespace TerminalWindows.Cli.Infrastructure;

/// <summary>Messages people read (wizard, menu, viewer status line) follow the Windows UI language.</summary>
public static class Ui
{
    public static readonly bool Ja = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";

    public static string T(string ja, string en) => Ja ? ja : en;

    /// <summary>Terminal columns a string occupies (CJK and other wide characters take two).</summary>
    public static int Width(string s) => s.Sum(c => IsWide(c) ? 2 : 1);

    /// <summary>Cuts or pads <paramref name="s"/> to exactly <paramref name="cols"/> terminal columns.</summary>
    public static string FitWidth(string s, int cols)
    {
        var sb = new System.Text.StringBuilder();
        int w = 0;
        foreach (var c in s)
        {
            int cw = IsWide(c) ? 2 : 1;
            if (w + cw > cols) break;
            sb.Append(c);
            w += cw;
        }
        return sb.Append(' ', Math.Max(0, cols - w)).ToString();
    }

    private static bool IsWide(char c) =>
        c is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣'
            or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦';
}
