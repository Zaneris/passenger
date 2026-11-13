namespace Passenger.Editor;

/// <summary>What a register holds: one piece of text for each caret that yanked or deleted it, its line breaks "\n", and
/// whether it is whole lines (each piece then ending with its break).</summary>
public sealed record VimRegister(IReadOnlyList<string> Parts, bool Linewise)
{
    /// <summary>A block's text, one piece for each of its lines, put back as a block.</summary>
    public bool Blockwise { get; init; }

    /// <summary>The pieces as one text: lines one after another, or pieces joined by a line break.</summary>
    public string Text => Linewise ? string.Concat(Parts) : string.Join('\n', Parts);
}

/// <summary>Vim's registers (and the last search, command and the options :set changes), one set for the editors that share it as Vim's editing shares one: the unnamed register, 0 for the last yank, 1 to 9
/// for deletes of a line or more shifting down, - for smaller deletes, a to z (A to Z appending), _ keeping nothing,
/// and + and * standing for the system clipboard, whose text the editor writes and reads.</summary>
public sealed class VimRegisters
{
    private readonly Dictionary<char, VimRegister> held = [];

    /// <summary>The last search, the register /: its pattern, whether it went backward, and the offset after it.</summary>
    public VimSearch? LastSearch { get; set; }

    /// <summary>The last substitution's pattern, replacement and flags, for :s and &amp; with none.</summary>
    public (string Pattern, string Replacement, string Flags)? LastSubstitute { get; set; }

    /// <summary>The last command line run, the register :, for @:.</summary>
    public string? LastCommand { get; set; }

    /// <summary>The options :set changes, shared as the registers are.</summary>
    public VimOptions Options { get; } = new();

    /// <summary>What p puts with no register named: the text of the last yank, delete or change.</summary>
    public VimRegister? Unnamed { get; private set; }

    /// <summary>Whether a key names a register a command may write or read.</summary>
    public static bool Names(char name) =>
        char.IsAsciiLetter(name) || char.IsAsciiDigit(name) || name is '"' or '-' or '_' or '+' or '*';

    /// <summary>Whether a register stands for the system clipboard.</summary>
    public static bool IsClipboard(char? name) => name is '+' or '*';

    public VimRegister? Get(char? name) => name switch
    {
        null or '"' => Unnamed,
        '_' => null,
        _ => held.GetValueOrDefault(char.ToLowerInvariant(name.Value)),
    };

    /// <summary>A yank: into the register named (appended to with a capital), else 0; the unnamed register follows.</summary>
    public void Yank(char? name, VimRegister text)
    {
        if (name == '_')
        {
            return;
        }
        Unnamed = name is null or '"' ? held['0'] = text : Write(name.Value, text);
    }

    /// <summary>A delete or change: into the register named, and into 1 (the others shifting down) when it was a line or
    /// more or <paramref name="always"/> (its motion was one of Vim's jumps), else - when no register was named.</summary>
    public void Delete(char? name, VimRegister text, bool always)
    {
        if (name == '_')
        {
            return;
        }
        bool small = !text.Linewise && !text.Parts.Any(p => p.Contains('\n', StringComparison.Ordinal));
        VimRegister written = name is null or '"' ? text : Write(name.Value, text);
        if (!small || always)
        {
            for (char i = '9'; i > '1'; i--)
            {
                if (held.TryGetValue((char)(i - 1), out VimRegister? before))
                {
                    held[i] = before;
                }
            }
            held['1'] = text;
        }
        if (small && name is null or '"')
        {
            held['-'] = text;
        }
        Unnamed = written;
    }

    /// <summary>Keys q recorded, kept as text in the register named (appended to with a capital); the unnamed register
    /// stays as it was.</summary>
    public void Record(char name, string keys)
    {
        char lower = char.ToLowerInvariant(name);
        held[lower] = new VimRegister([char.IsAsciiLetterUpper(name) && held.TryGetValue(lower, out VimRegister? before) ? before.Text + keys : keys], false);
    }

    /// <summary>Text the system clipboard held, for p from + or *: whole lines where it ends with a line break.</summary>
    public static VimRegister FromClipboard(string text)
    {
        string lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return new VimRegister([lines], lines.EndsWith('\n'));
    }

    private VimRegister Write(char name, VimRegister text)
    {
        char lower = char.ToLowerInvariant(name);
        if (char.IsAsciiLetterUpper(name) && held.TryGetValue(lower, out VimRegister? before))
        {
            // Appending: whole lines either side make it whole lines, a line break between where only one side is.
            string joined = before.Linewise == text.Linewise ? (before.Linewise ? before.Text + text.Text : before.Text + text.Text)
                : before.Linewise ? before.Text + text.Text + "\n" : before.Text + "\n" + text.Text;
            text = new VimRegister([joined], before.Linewise || text.Linewise);
        }
        held[lower] = text;
        return text;
    }
}

/// <summary>A search as / and ? leave it for n and N: its pattern in Vim's dialect, whether it went backward, and its
/// offset ("e", "s-1", "+2"), empty with none.</summary>
public sealed record VimSearch(string Pattern, bool Back, string Offset = "");

/// <summary>The options Vim mode honours, as :set names them.</summary>
public sealed class VimOptions
{
    public bool IgnoreCase { get; set; }

    public bool SmartCase { get; set; }

    /// <summary>Whether a search's matches are shown, as the editor's own find shows them; on, as Passenger's find has it.</summary>
    public bool HighlightSearch { get; set; } = true;

    public bool WrapScan { get; set; } = true;

    /// <summary>Two spaces after a sentence's end for J; off, as Neovim and code have it.</summary>
    public bool JoinSpaces { get; set; }

    /// <summary>Whether :s replaces every match on a line without g (and g then only the first).</summary>
    public bool GlobalDefault { get; set; }
}
