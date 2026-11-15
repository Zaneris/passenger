using Passenger.Workspace.GDScript;

namespace Passenger.Workspace;

/// <summary>A word or string of a GDScript's code: what it says, where its characters lie, whether they are written as it
/// says (no escape among them), and for a string given straight to a call, the function's name and which argument it is
/// (from zero); for a word, whether a '.' comes before it, so it names a member of what is before.</summary>
public sealed record GodotScriptToken(bool Word, string Text, int Start, int End, bool Plain, string? Call, int Argument, bool AfterDot)
{
    /// <summary>What a string's call is made on: null for no receiver, the word before the call's '.', or empty for anything
    /// else (a call's result, a chain of members).</summary>
    public string? Receiver { get; init; }

    /// <summary>Whether a string is the whole of an argument, joined to nothing (<c>"a" + "b"</c> is neither's), and no parameter's
    /// default; a branch of an <c>if</c> expression is whole, as the argument is that string when it is taken.</summary>
    public bool Whole { get; init; }

    /// <summary>The quote around a string, and whether it is raw.</summary>
    public char Quote { get; init; }

    public bool Raw { get; init; }

    /// <summary>Whether a string follows the word <c>extends</c>, a path to the script's base.</summary>
    public bool Extends { get; init; }
}

/// <summary>A GDScript's words and strings as Godot's tokenizer reads them: its comments passed over, a string's quotes
/// (single, double or tripled), its <c>r</c>, <c>&amp;</c> and <c>^</c> prefixes and its escapes read, and each string
/// written straight in a call's parentheses told by the call's name, what it is made on and its place among the arguments,
/// so what a script gives Godot's calls by name (<c>Input.is_action_pressed("jump")</c>) is read without its types. Pure.</summary>
public static class GodotScriptStrings
{
    public static IReadOnlyList<GodotScriptToken> Read(string text)
    {
        List<GodotScriptToken> tokens = [];
        GDScriptScan scan = new(text, says: true);
        while (scan.Next())
        {
            if (scan.Piece == GDScriptPiece.String)
            {
                GDScriptBracket bracket = scan.Innermost ?? default;
                bool opens = scan.BeginsArgument || scan.WordBefore == "else";
                tokens.Add(new GodotScriptToken(false, scan.Said, scan.TextStart, scan.TextEnd, scan.TextEnd - scan.TextStart == scan.Said.Length, bracket.Call, bracket.Argument, false)
                {
                    Receiver = bracket.Receiver,
                    Whole = opens && Closes(text, scan.End),
                    Quote = text[scan.Quote],
                    Raw = scan.Raw,
                    Extends = scan.WordBefore == "extends",
                });
            }
            else if (scan.Piece == GDScriptPiece.Word)
            {
                tokens.Add(new GodotScriptToken(true, scan.Word, scan.Start, scan.End, true, null, 0, scan.AfterDot));
            }
        }
        return tokens;
    }

    /// <summary>The strings a script gives straight to a call by one of some names, each the whole of the argument that takes the name.</summary>
    public static IEnumerable<GodotScriptToken> GivenTo(IReadOnlyList<GodotScriptToken> tokens, IReadOnlyDictionary<string, int[]> calls) =>
        tokens.Where(t => !t.Word && t.Whole && t.Call is { } call && calls.TryGetValue(call, out int[]? arguments) && arguments.Contains(t.Argument));

    /// <summary>What a string of a quote writes for some characters so that it says them, its escapes as Godot's tokenizer reads
    /// them; null for a raw string, whose only escape keeps its backslash, holding its own quote.</summary>
    public static string? Written(string said, char quote, bool raw) => raw ? said.Contains(quote) ? null : said
        : said.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(quote.ToString(), $"\\{quote}", StringComparison.Ordinal);

    /// <summary>Whether what comes after a string, past blanks and comments, ends its argument: a ',', a ')', or an <c>if</c>.</summary>
    private static bool Closes(string text, int at)
    {
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '#')
            {
                at = text.IndexOf('\n', at) is var end and >= 0 ? end : text.Length;
            }
            else if (char.IsWhiteSpace(c) || c == '\\')
            {
                at++;
            }
            else
            {
                return c is ')' or ',' || string.CompareOrdinal(text, at, "if", 0, 2) == 0 && GDScriptTokenizer.IdentifierContinue(text, at + 2) == 0;
            }
        }
        return false;
    }
}
