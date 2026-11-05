namespace TerminalWindows.Protocol;

/// <summary>The non-modifier part of a chord: either a virtual-key code, or a character whose
/// virtual key (and shift state) must be resolved against the guest's active keyboard layout.</summary>
public readonly record struct KeyTarget(ushort VirtualKey, char? Character)
{
    public static KeyTarget Vk(ushort vk) => new(vk, null);
    public static KeyTarget Char(char c) => new(0, c);
    public bool IsCharacter => Character.HasValue;
}

/// <summary>A key chord like <c>ctrl+shift+esc</c>. Modifiers are virtual-key codes.</summary>
public sealed record KeyChord(IReadOnlyList<ushort> Modifiers, KeyTarget Key)
{
    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;

    private static readonly Dictionary<string, ushort> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = VK_CONTROL, ["control"] = VK_CONTROL,
        ["shift"] = VK_SHIFT,
        ["alt"] = VK_MENU, ["menu"] = VK_MENU,
        ["win"] = VK_LWIN, ["windows"] = VK_LWIN, ["super"] = VK_LWIN, ["meta"] = VK_LWIN, ["cmd"] = VK_LWIN,
    };

    public static readonly IReadOnlyDictionary<string, ushort> NamedKeys = BuildNamedKeys();

    private static Dictionary<string, ushort> BuildNamedKeys()
    {
        var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["enter"] = 0x0D, ["return"] = 0x0D,
            ["tab"] = 0x09,
            ["esc"] = 0x1B, ["escape"] = 0x1B,
            ["space"] = 0x20,
            ["backspace"] = 0x08, ["bs"] = 0x08,
            ["delete"] = 0x2E, ["del"] = 0x2E,
            ["insert"] = 0x2D, ["ins"] = 0x2D,
            ["home"] = 0x24, ["end"] = 0x23,
            ["pageup"] = 0x21, ["pgup"] = 0x21,
            ["pagedown"] = 0x22, ["pgdn"] = 0x22,
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["printscreen"] = 0x2C, ["prtsc"] = 0x2C,
            ["pause"] = 0x13,
            ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91,
            ["apps"] = 0x5D, ["contextmenu"] = 0x5D,
            ["plus"] = 0xBB, ["minus"] = 0xBD,
            // Japanese keyboard keys
            ["kanji"] = 0x19, ["hankaku"] = 0x19, ["zenkaku"] = 0x19, ["hankaku_zenkaku"] = 0x19,
            ["convert"] = 0x1C, ["henkan"] = 0x1C,
            ["nonconvert"] = 0x1D, ["muhenkan"] = 0x1D,
            ["kana"] = 0x15, ["katakana_hiragana"] = 0x15,
            ["eisu"] = 0xF0,
            ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["volumemute"] = 0xAD,
        };
        for (int i = 1; i <= 24; i++) d["f" + i] = (ushort)(0x70 + i - 1);
        for (int i = 0; i <= 9; i++) d["num" + i] = (ushort)(0x60 + i);
        return d;
    }

    /// <summary>Keys that must be sent with KEYEVENTF_EXTENDEDKEY.</summary>
    public static bool IsExtended(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // nav + arrows
        or 0x2D or 0x2E // insert/delete
        or 0x2C // print screen
        or 0x5B or 0x5C or 0x5D // win keys, apps
        or 0x6F // divide
        or 0x90 // numlock
        or 0xA3 or 0xA5; // right ctrl / right alt

    /// <summary>Parses one chord such as <c>ctrl+c</c>, <c>win+r</c>, <c>alt+f4</c>, <c>ctrl++</c>.</summary>
    public static KeyChord Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Empty key");
        text = text.Trim();

        var parts = new List<string>();
        // Split on '+', treating a trailing '+' (e.g. "ctrl++") or a lone "+" as the plus key itself.
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '+' && i > start)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        if (parts[^1].Length == 0) throw new FormatException($"Invalid key: '{text}'");

        var mods = new List<ushort>();
        for (int i = 0; i < parts.Count - 1; i++)
        {
            var p = parts[i].Trim();
            if (!ModifierNames.TryGetValue(p, out var vk)) throw new FormatException($"Unknown modifier '{p}' in '{text}'");
            if (!mods.Contains(vk)) mods.Add(vk);
        }

        var last = parts[^1].Trim();
        if (last.Length == 0) last = parts[^1]; // keep a literal space-less key such as "+"
        KeyTarget target;
        if (ModifierNames.TryGetValue(last, out var modAsKey))
        {
            target = KeyTarget.Vk(modAsKey); // e.g. "win" alone opens Start
        }
        else if (NamedKeys.TryGetValue(last, out var named))
        {
            target = KeyTarget.Vk(named);
        }
        else if (last.Length == 1)
        {
            char c = last[0];
            if (c is >= 'a' and <= 'z') target = KeyTarget.Vk((ushort)char.ToUpperInvariant(c));
            else if (c is >= 'A' and <= 'Z') target = KeyTarget.Vk(c);
            else if (c is >= '0' and <= '9') target = KeyTarget.Vk(c);
            else target = KeyTarget.Char(c); // punctuation: resolved with VkKeyScanEx on the guest (JIS vs US layouts differ)
        }
        else
        {
            throw new FormatException($"Unknown key '{last}' in '{text}'");
        }
        return new KeyChord(mods, target);
    }

    /// <summary>Parses a whitespace-separated sequence of chords, e.g. <c>"ctrl+a ctrl+c"</c>.</summary>
    public static IReadOnlyList<KeyChord> ParseSequence(string text)
    {
        var chords = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToList();
        if (chords.Count == 0) throw new FormatException("Empty key sequence");
        return chords;
    }
}
