namespace TerminalWindows.Cli.Infrastructure;

/// <summary>Minimal argument parser: positionals plus <c>--flag</c> / <c>--opt value</c>.</summary>
public sealed class ParsedArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = new();

    /// <param name="flags">Options that take no value.</param>
    public static ParsedArgs Parse(IEnumerable<string> args, params string[] flags)
    {
        var flagSet = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);
        var r = new ParsedArgs();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a == "--")
            {
                r.Positionals.AddRange(list.Skip(i + 1));
                break;
            }
            if (a.StartsWith("--") && a.Length > 2)
            {
                var name = a[2..];
                string? value = null;
                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                else if (!flagSet.Contains(name))
                {
                    if (i + 1 >= list.Count) throw new UsageException($"--{name} needs a value");
                    value = list[++i];
                }
                r._options[name] = value;
            }
            else
            {
                r.Positionals.Add(a);
            }
        }
        return r;
    }

    public bool Flag(string name)
    {
        _used.Add(name);
        return _options.ContainsKey(name);
    }

    public string? Opt(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var v) ? v : null;
    }

    public int Int(string name, int def)
    {
        var v = Opt(name);
        if (v is null) return def;
        return int.TryParse(v, out var i) ? i : throw new UsageException($"--{name} must be an integer");
    }

    public string Pos(int index, string what) =>
        index < Positionals.Count ? Positionals[index] : throw new UsageException($"Missing {what}");

    public string? PosOrNull(int index) => index < Positionals.Count ? Positionals[index] : null;

    public int PosInt(int index, string what) =>
        int.TryParse(Pos(index, what), out var v) ? v : throw new UsageException($"{what} must be an integer");

    /// <summary>Throws on options that no handler asked for (catches typos like --memroy).</summary>
    public void RejectUnknown()
    {
        var unknown = _options.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0) throw new UsageException("Unknown option(s): " + string.Join(", ", unknown.Select(u => "--" + u)));
    }
}

public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}

/// <summary>An error with a message meant for the user (no stack trace).</summary>
public sealed class CliException : Exception
{
    public CliException(string message) : base(message) { }
}
