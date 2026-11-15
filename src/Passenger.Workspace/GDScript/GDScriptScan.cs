namespace Passenger.Workspace.GDScript;

/// <summary>What a piece of a GDScript's code that <see cref="GDScriptScan"/> stops at is.</summary>
internal enum GDScriptPiece
{
    /// <summary>A comment, from its '#' to its line's end.</summary>
    Comment,

    /// <summary>A string, from its prefix or opening quote to where the code goes on after it.</summary>
    String,

    /// <summary>A name or a keyword, read by Godot's identifier rule.</summary>
    Word,
}

/// <summary>A bracket a scan stands inside: its character and offset, and for a '(' straight after a word, the call it opens:
/// the function's name and where it starts, and what it is made on (null where no '.' comes before the name, the word before
/// the '.', or empty for anything else); the argument the scan is at counts from zero.</summary>
internal readonly record struct GDScriptBracket(char Mark, int At, string? Call, int CallStart, string? Receiver, int Argument);

/// <summary>A GDScript's code read from its start as Godot's tokenizer splits it, lightly enough to hold while it is half
/// written: it stops at each comment, string and word (<see cref="Next"/>), a string read through the tokenizer's own reading
/// of its prefix, quotes and escapes and a word by the tokenizer's identifier rule, and passes over numbers, a bare node path
/// after '$' and the rest, keeping the brackets open with the call each opens and the argument it is at. What it says of a
/// piece holds until the next. Pure.</summary>
internal sealed class GDScriptScan(string text, bool says = false)
{
    private readonly List<GDScriptBracket> open = [];
    private int at;
    private bool stopped;

    /// <summary>The last character of code before the piece, comments and blanks passed over: 'a' after a word, number or
    /// node path, '"' after a string.</summary>
    private char before;

    private string? lastWord;
    private int lastWordStart;
    private bool wordLast;
    private bool lastDotted;
    private string? lastReceiver;
    private string dotted = string.Empty;

    public GDScriptPiece Piece { get; private set; }

    /// <summary>Where the piece starts: a comment's '#', a string's prefix or quote, a word's first character.</summary>
    public int Start { get; private set; }

    /// <summary>Where the code goes on after the piece: a comment's line break (the text's end on its last line).</summary>
    public int End { get; private set; }

    /// <summary>A string's opening quote.</summary>
    public int Quote { get; private set; }

    /// <summary>Whether a string is raw (<c>r"…"</c>).</summary>
    public bool Raw { get; private set; }

    /// <summary>Where a string's characters start and end.</summary>
    public int TextStart { get; private set; }

    public int TextEnd { get; private set; }

    /// <summary>What a string says, its escapes read, where the scan was asked to read it; else empty.</summary>
    public string Said { get; private set; } = string.Empty;

    /// <summary>A word's name.</summary>
    public string Word { get; private set; } = string.Empty;

    /// <summary>The word just before the piece, only blanks and comments between; null for anything else.</summary>
    public string? WordBefore => wordLast ? lastWord : null;

    /// <summary>Whether a '.' comes just before the piece, only blanks and comments between: the piece names a member.</summary>
    public bool AfterDot => before == '.';

    /// <summary>Whether the piece begins a bracket's first argument or one after a ',', only blanks and comments between.</summary>
    public bool BeginsArgument => before is '(' or ',';

    /// <summary>The innermost bracket open before the piece, or before the offset the scan has read to; null for none.</summary>
    public GDScriptBracket? Innermost => open.Count > 0 ? open[^1] : null;

    /// <summary>Goes on to the next comment, string or word that starts before an offset, taking in everything before it
    /// and the piece it stood at; false where none is left before it, everything before it taken in.</summary>
    public bool Next(int until = int.MaxValue)
    {
        TakeIn();
        int limit = Math.Min(until, text.Length);
        while (at < limit)
        {
            char c = text[at];
            if (c == '#')
            {
                return Stop(GDScriptPiece.Comment, at, text.IndexOf('\n', at) is var end and >= 0 ? end : text.Length);
            }
            if (c is '"' or '\'' || c is 'r' or '&' or '^' && at + 1 < text.Length && text[at + 1] is '"' or '\'')
            {
                Raw = c == 'r';
                Quote = c is '"' or '\'' ? at : at + 1;
                int after;
                if (says)
                {
                    (Said, TextStart, TextEnd, after) = GDScriptTokenizer.ReadString(text, Quote, Raw);
                }
                else
                {
                    (TextStart, TextEnd, after) = GDScriptTokenizer.StringBounds(text, Quote, Raw);
                }
                return Stop(GDScriptPiece.String, at, after);
            }
            if (c is >= '0' and <= '9')
            {
                at = Passed(at, number: true);
                Value();
                continue;
            }
            if (c == '$' && at + 1 < text.Length && text[at + 1] is not ('"' or '\''))
            {
                // A node's path written bare holds names of nodes, no word of the code's.
                at = Passed(at + 1, number: false);
                Value();
                continue;
            }
            if (GDScriptTokenizer.IdentifierStart(text, at) is > 0 and int first)
            {
                int end = at + first;
                for (int width; (width = GDScriptTokenizer.IdentifierContinue(text, end)) > 0;)
                {
                    end += width;
                }
                Word = text[at..end];
                return Stop(GDScriptPiece.Word, at, end);
            }
            Mark(c);
            at++;
        }
        return false;
    }

    private bool Stop(GDScriptPiece piece, int start, int end)
    {
        Piece = piece;
        Start = start;
        End = end;
        stopped = true;
        return true;
    }

    /// <summary>What the piece stood at changes for what comes after it, the scan going on past it.</summary>
    private void TakeIn()
    {
        if (!stopped)
        {
            return;
        }
        stopped = false;
        at = End;
        if (Piece == GDScriptPiece.String)
        {
            wordLast = false;
            before = '"';
        }
        else if (Piece == GDScriptPiece.Word)
        {
            lastDotted = before == '.';
            lastReceiver = lastDotted ? dotted : null;
            lastWord = Word;
            lastWordStart = Start;
            wordLast = true;
            before = 'a';
        }
    }

    /// <summary>Where a number's characters, or a bare node path's names and slashes, end.</summary>
    private int Passed(int from, bool number)
    {
        while (from < text.Length)
        {
            if (GDScriptTokenizer.IdentifierContinue(text, from) is > 0 and int width)
            {
                from += width;
            }
            else if (number ? text[from] == '.' && !(from + 1 < text.Length && text[from + 1] == '.') : text[from] is '/' or '%')
            {
                from++;
            }
            else
            {
                break;
            }
        }
        return from;
    }

    private void Value()
    {
        wordLast = false;
        before = 'a';
    }

    /// <summary>A character of code that is no comment, string, word, number or path: a bracket opens or closes, a ',' goes on
    /// to the next argument, a '.' after a word makes it what the next word is a member of.</summary>
    private void Mark(char c)
    {
        if (c is '(' or '[' or '{')
        {
            bool called = c == '(' && wordLast;
            open.Add(new GDScriptBracket(c, at, called ? lastWord : null, lastWordStart, called ? lastReceiver : null, 0));
        }
        else if (c is ')' or ']' or '}' && open.Count > 0)
        {
            open.RemoveAt(open.Count - 1);
        }
        else if (c == ',' && open.Count > 0)
        {
            open[^1] = open[^1] with { Argument = open[^1].Argument + 1 };
        }
        else if (c == '.')
        {
            dotted = wordLast && !lastDotted ? lastWord! : string.Empty;
        }
        if (!char.IsWhiteSpace(c))
        {
            wordLast = false;
            before = c;
        }
    }
}
