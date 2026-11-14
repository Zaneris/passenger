using System.Globalization;

namespace Passenger.Editor;

/// <summary>The modes a mapping takes keys in: normal mode, visual mode, an operator waiting for its motion, insert mode.</summary>
[Flags]
public enum VimMapModes
{
    None = 0,
    Normal = 1,
    Visual = 2,
    Operator = 4,
    Insert = 8,
}

/// <summary>One mapping: the keys typed in its modes, the keys pressed in their place, and whether those are mapped again.</summary>
public sealed record VimMapping(VimMapModes Modes, IReadOnlyList<string> Keys, IReadOnlyList<string> To, bool Remaps);

/// <summary>Vim mode's mappings file read: its map, noremap and unmap lines by mode (n, v, x, o, i, and map and map! for
/// several), the leader let mapleader sets for &lt;leader&gt;, and its set lines for the options; each line it cannot
/// read passed over and kept with why, comments (") and blank lines passed over too. Keys are written in Vim's notation:
/// &lt;CR&gt;, &lt;Esc&gt;, &lt;Space&gt;, &lt;C-x&gt;, &lt;lt&gt;, &lt;Bar&gt;, &lt;Nop&gt; and the rest.</summary>
public sealed class VimMappings
{
    public static readonly VimMappings None = new([], [], []);

    private readonly IReadOnlyList<VimMapping> mappings;

    private VimMappings(IReadOnlyList<VimMapping> mappings, IReadOnlyList<string> settings, IReadOnlyList<(int Line, string Why)> problems)
    {
        this.mappings = mappings;
        Settings = settings;
        Problems = problems;
    }

    public IReadOnlyList<VimMapping> All => mappings;

    /// <summary>What each set line names, for :set to do in turn.</summary>
    public IReadOnlyList<string> Settings { get; }

    /// <summary>The lines passed over, from 1, and why.</summary>
    public IReadOnlyList<(int Line, string Why)> Problems { get; }

    /// <summary>Whether any mapping takes keys in <paramref name="mode"/>.</summary>
    public bool Maps(VimMapModes mode) => mappings.Any(m => (m.Modes & mode) != 0);

    /// <summary>The mapping whose keys are exactly <paramref name="keys"/> in the mode, and whether a longer one begins
    /// with them, so more keys may yet make it.</summary>
    public (VimMapping? Exact, bool Longer) Find(VimMapModes mode, IReadOnlyList<string> keys)
    {
        VimMapping? exact = null;
        bool longer = false;
        foreach (VimMapping mapping in mappings)
        {
            if ((mapping.Modes & mode) == 0 || mapping.Keys.Count < keys.Count || !StartsWith(mapping.Keys, keys))
            {
                continue;
            }
            if (mapping.Keys.Count == keys.Count)
            {
                exact = mapping;
            }
            else
            {
                longer = true;
            }
        }
        return (exact, longer);
    }

    private static bool StartsWith(IReadOnlyList<string> keys, IReadOnlyList<string> start)
    {
        for (int i = 0; i < start.Count; i++)
        {
            if (keys[i] != start[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A mappings file's text read.</summary>
    public static VimMappings Read(string text)
    {
        List<VimMapping> mappings = [];
        List<string> settings = [];
        List<(int, string)> problems = [];
        string leader = "\\";
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            while (line.StartsWith(':'))
            {
                line = line[1..].TrimStart();
            }
            if (line.Length == 0 || line.StartsWith('"'))
            {
                continue;
            }
            int nameEnd = 0;
            while (nameEnd < line.Length && (char.IsAsciiLetter(line[nameEnd]) || line[nameEnd] == '!'))
            {
                nameEnd++;
            }
            string name = line[..nameEnd];
            string rest = line[nameEnd..].Trim();
            string? why = name switch
            {
                "set" or "se" => Setting(rest, settings),
                "let" => Leader(rest, ref leader),
                _ when MapCommand(name) is { } map => Map(map.Modes, map.Remaps, map.Removes, rest, leader, mappings),
                _ => $"{(name.Length > 0 ? name : line)} is not a command Vim mode's mappings file reads: it reads map, noremap, unmap (n, v, x, o and i), let mapleader and set",
            };
            if (why is not null)
            {
                problems.Add((i + 1, why));
            }
        }
        return new VimMappings(mappings, settings, problems);
    }

    private static string? Setting(string rest, List<string> settings)
    {
        if (rest.Length == 0)
        {
            return "set names the options it sets";
        }
        settings.Add(rest);
        return null;
    }

    private static string? Leader(string rest, ref string leader)
    {
        int equals = rest.IndexOf('=', StringComparison.Ordinal);
        string name = equals < 0 ? rest : rest[..equals].Trim();
        if (name is not ("mapleader" or "g:mapleader") || equals < 0)
        {
            return $"let sets only mapleader here, not {name}";
        }
        string value = rest[(equals + 1)..].Trim();
        if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
        {
            value = value[1..^1];
        }
        value = value.Replace("\\<", "<", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        List<string> keys = KeysOf(value, leader);
        if (keys.Count != 1)
        {
            return $"mapleader is one key, not {value}";
        }
        leader = keys[0];
        return null;
    }

    private const VimMapModes NormalVisualOperator = VimMapModes.Normal | VimMapModes.Visual | VimMapModes.Operator;

    /// <summary>Each map command by its names: the modes it maps in, whether what it maps to is mapped again, and whether
    /// it takes a mapping away.</summary>
    private static readonly Dictionary<string, (VimMapModes Modes, bool Remaps, bool Removes)> MapCommands = new(StringComparer.Ordinal)
    {
        ["map"] = (NormalVisualOperator, true, false),
        ["nmap"] = (VimMapModes.Normal, true, false),
        ["nm"] = (VimMapModes.Normal, true, false),
        ["vmap"] = (VimMapModes.Visual, true, false),
        ["xmap"] = (VimMapModes.Visual, true, false),
        ["omap"] = (VimMapModes.Operator, true, false),
        ["imap"] = (VimMapModes.Insert, true, false),
        ["map!"] = (VimMapModes.Insert, true, false),
        ["noremap"] = (NormalVisualOperator, false, false),
        ["no"] = (NormalVisualOperator, false, false),
        ["nnoremap"] = (VimMapModes.Normal, false, false),
        ["nn"] = (VimMapModes.Normal, false, false),
        ["vnoremap"] = (VimMapModes.Visual, false, false),
        ["vn"] = (VimMapModes.Visual, false, false),
        ["xnoremap"] = (VimMapModes.Visual, false, false),
        ["xn"] = (VimMapModes.Visual, false, false),
        ["onoremap"] = (VimMapModes.Operator, false, false),
        ["ono"] = (VimMapModes.Operator, false, false),
        ["inoremap"] = (VimMapModes.Insert, false, false),
        ["ino"] = (VimMapModes.Insert, false, false),
        ["noremap!"] = (VimMapModes.Insert, false, false),
        ["unmap"] = (NormalVisualOperator, false, true),
        ["nunmap"] = (VimMapModes.Normal, false, true),
        ["vunmap"] = (VimMapModes.Visual, false, true),
        ["xunmap"] = (VimMapModes.Visual, false, true),
        ["ounmap"] = (VimMapModes.Operator, false, true),
        ["iunmap"] = (VimMapModes.Insert, false, true),
        ["unmap!"] = (VimMapModes.Insert, false, true),
    };

    private static (VimMapModes Modes, bool Remaps, bool Removes)? MapCommand(string name) =>
        MapCommands.TryGetValue(name, out (VimMapModes, bool, bool) command) ? command : null;

    private static string? Map(VimMapModes modes, bool remaps, bool removes, string rest, string leader, List<VimMapping> mappings)
    {
        // <silent>, <nowait> and the like change nothing Vim mode does.
        while (rest.StartsWith('<') && rest.IndexOf('>', StringComparison.Ordinal) is int close and > 0
            && rest[1..close].ToLowerInvariant() is "silent" or "nowait" or "unique" or "special" or "script" or "buffer" or "expr")
        {
            if (rest[1..close].Equals("expr", StringComparison.OrdinalIgnoreCase))
            {
                return "<expr> mappings are not done: what they press is fixed text here";
            }
            rest = rest[(close + 1)..].TrimStart();
        }
        int split = 0;
        while (split < rest.Length && rest[split] is not (' ' or '\t'))
        {
            split++;
        }
        List<string> keys = KeysOf(rest[..split], leader);
        if (keys.Count == 0)
        {
            return "a mapping names the keys it takes";
        }
        if (removes)
        {
            int removed = mappings.RemoveAll(m => m.Keys.SequenceEqual(keys) && (m.Modes & modes) != 0);
            return removed > 0 ? null : $"{rest[..split]} is not mapped there";
        }
        string to = rest[split..].Trim();
        if (to.Length == 0)
        {
            return $"{rest[..split]} is mapped to nothing: <Nop> maps a key to do nothing";
        }
        if (rest.Contains("<Plug>", StringComparison.OrdinalIgnoreCase) || rest.Contains("<SID>", StringComparison.OrdinalIgnoreCase))
        {
            return "<Plug> and <SID> name a plugin's or script's functions, which Vim mode has none of";
        }
        mappings.RemoveAll(m => m.Keys.SequenceEqual(keys) && m.Modes == modes);
        mappings.Add(new VimMapping(modes, keys, to.Equals("<Nop>", StringComparison.OrdinalIgnoreCase) ? [] : KeysOf(to, leader), remaps));
        return null;
    }

    /// <summary>Keys written in Vim's notation as Vim mode names keys: a named key (&lt;CR&gt;, &lt;C-x&gt;) whole, any
    /// other character alone, &lt;leader&gt; the leader.</summary>
    public static List<string> KeysOf(string written, string leader = "\\")
    {
        List<string> keys = [];
        for (int i = 0; i < written.Length; i++)
        {
            if (written[i] == '<' && written.IndexOf('>', i + 1) is int close and > 0 && Named(written[(i + 1)..close], leader) is { } named)
            {
                keys.Add(named);
                i = close;
                continue;
            }
            if (char.IsHighSurrogate(written[i]) && i + 1 < written.Length)
            {
                keys.Add(written.Substring(i, 2));
                i++;
                continue;
            }
            keys.Add(written[i].ToString());
        }
        return keys;
    }

    /// <summary>A key's name between &lt; and &gt; as Vim mode names it; null for a name it does not know.</summary>
    private static string? Named(string name, string leader)
    {
        string lower = name.ToLowerInvariant();
        switch (lower)
        {
            case "cr" or "enter" or "return":
                return "<CR>";
            case "esc":
                return Vim.Escape;
            case "bs":
                return "<BS>";
            case "tab":
                return "<Tab>";
            case "del":
                return "<Del>";
            case "space":
                return " ";
            case "lt":
                return "<";
            case "bar":
                return "|";
            case "bslash":
                return "\\";
            case "leader":
                return leader;
            case "up":
                return "<Up>";
            case "down":
                return "<Down>";
        }
        if (lower.Length == 3 && lower[0] == 'c' && lower[1] == '-' && char.IsAsciiLetter(lower[2]))
        {
            return string.Create(CultureInfo.InvariantCulture, $"<C-{lower[2]}>");
        }
        return null;
    }
}
