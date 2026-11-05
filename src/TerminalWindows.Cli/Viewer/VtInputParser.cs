using System.Text;

namespace TerminalWindows.Cli.Viewer;

public abstract record InputEvent;
/// <summary>Printable text to type into the guest.</summary>
public sealed record TextInput(string Text) : InputEvent;
/// <summary>A key chord in the guest's <c>key</c> syntax, e.g. "ctrl+c", "left", "alt+f4".</summary>
public sealed record KeyInput(string Chord) : InputEvent;
/// <summary>SGR mouse report. Col/Row are 1-based cells. Button: 0 left, 1 middle, 2 right; Wheel: -1 up, +1 down.</summary>
public sealed record MouseInput(int Button, int Col, int Row, bool Pressed, bool Motion, int Wheel, int WheelX) : InputEvent;
/// <summary>Ctrl+G: open the Windows shortcut palette.</summary>
public sealed record PaletteRequest : InputEvent;
/// <summary>Ctrl+]: leave the viewer (the VM keeps running).</summary>
public sealed record ExitRequest : InputEvent;
/// <summary>A terminal response such as the cell-size report CSI 6;h;w t.</summary>
public sealed record TerminalReport(char Final, string Params) : InputEvent;

/// <summary>
/// Parses VT input (ENABLE_VIRTUAL_TERMINAL_INPUT) into viewer events: text, control keys,
/// CSI/SS3 sequences, SGR (1006) mouse reports and terminal responses.
/// </summary>
public sealed class VtInputParser
{
    private readonly List<byte> _pending = new();

    public IReadOnlyList<InputEvent> Feed(ReadOnlySpan<byte> data, bool flushLoneEscape = true)
    {
        _pending.AddRange(data.ToArray());
        var events = new List<InputEvent>();
        var text = new StringBuilder();
        var buf = _pending.ToArray();
        int i = 0;

        void FlushText()
        {
            if (text.Length > 0)
            {
                events.Add(new TextInput(text.ToString()));
                text.Clear();
            }
        }

        while (i < buf.Length)
        {
            byte b = buf[i];
            if (b == 0x1B)
            {
                if (i + 1 >= buf.Length)
                {
                    if (!flushLoneEscape) break; // wait for more bytes
                    FlushText();
                    events.Add(new KeyInput("esc"));
                    i++;
                    continue;
                }
                byte n = buf[i + 1];
                if (n == '[')
                {
                    int end = FindCsiEnd(buf, i + 2);
                    if (end < 0)
                    {
                        if (!flushLoneEscape) break;
                        i = buf.Length; // drop the malformed tail
                        continue;
                    }
                    FlushText();
                    var ev = ParseCsi(Encoding.ASCII.GetString(buf, i + 2, end - (i + 2)), (char)buf[end]);
                    if (ev is not null) events.Add(ev);
                    i = end + 1;
                    continue;
                }
                if (n == 'O' && i + 2 < buf.Length)
                {
                    FlushText();
                    var key = (char)buf[i + 2] switch
                    {
                        'P' => "f1", 'Q' => "f2", 'R' => "f3", 'S' => "f4",
                        'A' => "up", 'B' => "down", 'C' => "right", 'D' => "left", 'H' => "home", 'F' => "end",
                        _ => null,
                    };
                    if (key is not null) events.Add(new KeyInput(key));
                    i += 3;
                    continue;
                }
                if (n == 0x1B)
                {
                    FlushText();
                    events.Add(new KeyInput("esc"));
                    i++;
                    continue;
                }
                // ESC + key = Alt+key
                FlushText();
                int len = Utf8Len(n);
                if (i + 1 + len > buf.Length) break;
                var s = Encoding.UTF8.GetString(buf, i + 1, len);
                var inner = ControlOrChar(s);
                events.Add(new KeyInput("alt+" + inner));
                i += 1 + len;
                continue;
            }

            if (b < 0x20 || b == 0x7F)
            {
                FlushText();
                events.Add(b switch
                {
                    0x07 => new PaletteRequest(),
                    0x1D => new ExitRequest(),
                    0x0D or 0x0A => new KeyInput("enter"),
                    0x09 => new KeyInput("tab"),
                    0x08 or 0x7F => new KeyInput("backspace"),
                    0x00 => new KeyInput("ctrl+space"),
                    >= 0x01 and <= 0x1A => new KeyInput("ctrl+" + (char)('a' + b - 1)),
                    0x1C => new KeyInput("ctrl+\\"),
                    0x1E => new KeyInput("ctrl+^"),
                    0x1F => new KeyInput("ctrl+_"),
                    _ => new KeyInput("esc"),
                });
                i++;
                continue;
            }

            int l = Utf8Len(b);
            if (i + l > buf.Length) break; // incomplete UTF-8 sequence
            text.Append(Encoding.UTF8.GetString(buf, i, l));
            i += l;
        }
        FlushText();
        _pending.RemoveRange(0, i);
        return events;
    }

    private static string ControlOrChar(string s) => s.Length == 1 && s[0] < 0x20 ? "ctrl+" + (char)('a' + s[0] - 1) : s.ToLowerInvariant();

    private static int Utf8Len(byte b) => b < 0x80 ? 1 : b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;

    private static int FindCsiEnd(byte[] buf, int start)
    {
        for (int j = start; j < buf.Length; j++)
            if (buf[j] >= 0x40 && buf[j] <= 0x7E) return j;
        return -1;
    }

    private static string WithMods(string key, int mod)
    {
        if (mod <= 1) return key;
        int m = mod - 1;
        var parts = new List<string>();
        if ((m & 4) != 0) parts.Add("ctrl");
        if ((m & 2) != 0) parts.Add("alt");
        if ((m & 1) != 0) parts.Add("shift");
        parts.Add(key);
        return string.Join('+', parts);
    }

    internal static InputEvent? ParseCsi(string param, char final)
    {
        if (param.StartsWith('<') && (final == 'M' || final == 'm'))
        {
            var p = param[1..].Split(';');
            if (p.Length != 3 || !int.TryParse(p[0], out var code) || !int.TryParse(p[1], out var col) || !int.TryParse(p[2], out var row)) return null;
            bool motion = (code & 32) != 0;
            if ((code & 64) != 0)
            {
                int w = code & 3;
                return new MouseInput(-1, col, row, true, false, w == 0 ? -1 : w == 1 ? 1 : 0, w == 2 ? -1 : w == 3 ? 1 : 0);
            }
            return new MouseInput(code & 3, col, row, final == 'M', motion, 0, 0);
        }

        if (final == 't' || (final == 'c' && param.StartsWith('?'))) return new TerminalReport(final, param);

        var parts = param.Split(';');
        int mod = parts.Length > 1 && int.TryParse(parts[1], out var mm) ? mm : 1;
        string? key = final switch
        {
            'A' => "up", 'B' => "down", 'C' => "right", 'D' => "left",
            'H' => "home", 'F' => "end",
            'P' => "f1", 'Q' => "f2", 'R' => "f3", 'S' => "f4",
            'Z' => "shift+tab",
            '~' => (int.TryParse(parts[0], out var n) ? n : 0) switch
            {
                1 or 7 => "home", 2 => "insert", 3 => "delete", 4 or 8 => "end", 5 => "pageup", 6 => "pagedown",
                15 => "f5", 17 => "f6", 18 => "f7", 19 => "f8", 20 => "f9", 21 => "f10", 23 => "f11", 24 => "f12",
                _ => null,
            },
            _ => null,
        };
        if (key is null) return null;
        return new KeyInput(final == 'Z' ? key : WithMods(key, mod));
    }
}
