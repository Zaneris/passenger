using System.Globalization;
using System.Text;

namespace Passenger.Editor;

/// <summary>The names Vim mode gives the keys that type no character, in Vim's notation (a typed character is its own
/// name): what an editor sends Vim mode, what a mapping writes between &lt; and &gt;, and what a register's control
/// characters press as a macro plays.</summary>
[IdentifierHome]
public static class VimKeys
{
    public const string Escape = "<Esc>";
    public const string Enter = "<CR>";
    public const string Backspace = "<BS>";
    public const string Tab = "<Tab>";
    public const string Delete = "<Del>";
    public const string Up = "<Up>";
    public const string Down = "<Down>";
    public const string CtrlR = "<C-r>";
    public const string CtrlV = "<C-v>";
    public const string CtrlO = "<C-o>";
    public const string CtrlI = "<C-i>";
    public const string CtrlU = "<C-u>";
    public const string CtrlW = "<C-w>";

    /// <summary>Any other press, which Vim mode leaves to the keymap and a macro does not record.</summary>
    public const string Other = "<Other>";

    /// <summary>A key only a replay presses: the caret to a line's start, the number after it counting lines up from the
    /// text's last, no jump.</summary>
    internal const string Go = "<Go>";

    /// <summary>Ctrl and a letter, a to z.</summary>
    public static string Ctrl(char letter) => string.Create(CultureInfo.InvariantCulture, $"<C-{letter}>");

    /// <summary>The letter of Ctrl and a letter (<see cref="Ctrl"/>), null for any other key.</summary>
    public static char? CtrlLetter(string key) => key is ['<', 'C', '-', >= 'a' and <= 'z', '>'] ? key[3] : null;

    /// <summary>Whether a key types one character: one char, or a surrogate pair's two.</summary>
    public static bool IsTyped(string key) => key.Length == 1 || (key.Length == 2 && char.IsSurrogatePair(key[0], key[1]));

    /// <summary>The text a key gives what takes one (a find, r, replace mode, the command line): a typed character its
    /// own, Tab a tab, Enter a line break where <paramref name="enterBreaks"/>; null for any other.</summary>
    public static string? Typed(string key, bool enterBreaks = false) => key switch
    {
        Tab => "\t",
        Enter when enterBreaks => "\n",
        _ when IsTyped(key) => key,
        _ => null,
    };

    /// <summary>Keys as a register keeps them, named keys as the control characters Vim writes.</summary>
    public static string Recorded(IEnumerable<string> keys)
    {
        StringBuilder text = new();
        foreach (string key in keys)
        {
            text.Append(key switch
            {
                Escape => "\u001b",
                Enter => "\r",
                Backspace => "\b",
                Tab => "\t",
                Delete => "\u007f",
                _ when CtrlLetter(key) is { } letter => ((char)(letter - 'a' + 1)).ToString(),
                _ => key,
            });
        }
        return text.ToString();
    }

    /// <summary>A register's text as the keys it presses: its control characters the named keys, a line break Enter.</summary>
    public static List<string> Pressed(string text)
    {
        List<string> keys = [];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            keys.Add(c switch
            {
                '\u001b' => Escape,
                '\r' or '\n' => Enter,
                '\b' => Backspace,
                '\t' => Tab,
                '\u007f' => Delete,
                >= '\u0001' and <= '\u001a' => Ctrl((char)('a' + c - 1)),
                _ when char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) => text.Substring(i++, 2),
                _ => c.ToString(),
            });
        }
        return keys;
    }

    /// <summary>Keys written in Vim's notation, as a mapping writes them: a named key (&lt;CR&gt;, &lt;C-x&gt;) whole, any
    /// other character alone, &lt;leader&gt; the leader.</summary>
    public static List<string> Written(string written, string leader = "\\")
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
                return Enter;
            case "esc":
                return Escape;
            case "bs":
                return Backspace;
            case "tab":
                return Tab;
            case "del":
                return Delete;
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
                return Up;
            case "down":
                return Down;
        }
        return lower.Length == 3 && lower[0] == 'c' && lower[1] == '-' && char.IsAsciiLetter(lower[2]) ? Ctrl(lower[2]) : null;
    }
}
