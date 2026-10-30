using System.Text;
using System.Text.RegularExpressions;

namespace Passenger.Workspace;

/// <summary>A package.json's scripts as JSON text: the runs of a script by its name from another (npm's, Yarn's and pnpm's
/// <c>run</c>), found written plain, in double quotes or in single quotes for the shell, and written again for a new name,
/// quoted where a shell would split or read it otherwise. A double-quoted name is read alike by the shells npm runs scripts
/// with, sh and cmd.exe.</summary>
public static class NpmScripts
{
    /// <summary>The characters a shell reads in a word as more than its name: a name holding one is written quoted.</summary>
    private const string Special = "\"'`$&|;<>()*?[]{}#~!^%\\";

    /// <summary>The edits renaming a script's runs in JSON text (a package.json's scripts, each string as JSON escapes it):
    /// where each run names the script, quotes and all, and the new name written there, in the quotes the run had (double
    /// ones where a single-quoted name holds a single quote), plain where it had none unless the new name needs them.</summary>
    public static IReadOnlyList<(int Start, int Length, string Text)> Renamed(string json, string script, string name)
    {
        List<(int, int, string)> edits = [];
        foreach (Match run in Runs(script).Matches(json))
        {
            Group named = run.Groups["script"];
            char? quote = run.Groups["double"].Success ? '"' : run.Groups["single"].Success ? '\'' : null;
            char? written = quote == '\'' && name.Contains('\'', StringComparison.Ordinal) ? '"' : quote ?? (NeedsQuotes(name) ? '"' : null);
            edits.Add((named.Index, named.Length, StringContent(Word(name, written))));
        }
        return edits;
    }

    /// <summary>Whether a script's name is read as more than one word, or as more than its name, written plain.</summary>
    public static bool NeedsQuotes(string name) => name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || Special.Contains(c, StringComparison.Ordinal));

    /// <summary>A script's runs by name, as JSON text holds them: plain only where the name needs no quotes, and followed by
    /// the end of the command or of its string.</summary>
    private static Regex Runs(string script)
    {
        string inDouble = Regex.Escape(StringContent(Word(script, '"')));
        string inSingle = Regex.Escape(StringContent(Word(script, '\'')));
        string plain = NeedsQuotes(script) ? string.Empty : $@"|{Regex.Escape(StringContent(script))}(?=$|[\s""&;|])";
        return new Regex($@"\b(?:npm|yarn|pnpm)\s+run(?:-script)?\s+(?<script>(?<double>{inDouble})|(?<single>{inSingle}){plain})");
    }

    /// <summary>A name as a shell word in the quotes given (none for null): in double quotes a quote is escaped.</summary>
    private static string Word(string name, char? quote) => quote switch
    {
        '"' => $"\"{name.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
        '\'' => $"'{name}'",
        _ => name,
    };

    /// <summary>Text as a JSON string holds it, read as typed: only what JSON must escape is (a quote, a backslash, a control
    /// character), where a serializer's encoder escapes every letter outside ASCII and <c>+</c>, <c>&lt;</c> and the like.</summary>
    public static string StringContent(string text)
    {
        StringBuilder written = new(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '"' or '\\':
                    written.Append('\\').Append(c);
                    break;
                case '\n':
                    written.Append("\\n");
                    break;
                case '\r':
                    written.Append("\\r");
                    break;
                case '\t':
                    written.Append("\\t");
                    break;
                case < ' ':
                    written.Append($"\\u{(int)c:x4}");
                    break;
                default:
                    written.Append(c);
                    break;
            }
        }
        return written.ToString();
    }
}
