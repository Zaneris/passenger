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

/// <summary>Vim's registers, one set for the editors that share it as Vim's editing shares one: the unnamed register, 0 for the last yank, 1 to 9
/// for deletes of a line or more shifting down, - for smaller deletes, a to z (A to Z appending), _ keeping nothing,
/// and + and * standing for the system clipboard, whose text the editor writes and reads.</summary>
public sealed class VimRegisters
{
    private readonly Dictionary<char, VimRegister> held = [];

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
