using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Passenger.Core;

/// <summary>What keeps a reader that nobody can tell to stop from ending the process on a person's text: Roslyn's parser
/// checks its stack in some places and recurses unchecked in others, as deep as a text's brackets go, and a stack that runs
/// out ends the process, whatever would have caught what. Its cost grows as the square or cube of that depth besides.</summary>
public static class DeepText
{
    /// <summary>What a character may take of a stack at the most: a level of a C# pattern, three characters, takes 2 KB.</summary>
    private const int PerCharacter = 1024;

    /// <summary>What a read takes of a stack with no text.</summary>
    private const int Least = 1024 * 1024;

    /// <summary>The most a stack is given, of the two gigabytes a thread's size can say.</summary>
    private const int Most = 1 << 30;

    [ThreadStatic]
    private static long readBytes;

    /// <summary>What reads this thread asked for allocated on threads of their own, which
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> leaves out.</summary>
    public static long ReadBytes => readBytes;

    /// <summary>A read of a text of so many characters, on a stack the text can't run out of up to what <see cref="Most"/>
    /// holds. A stack takes memory only as far as it is used, and goes with its thread.</summary>
    public static T Read<T>(int characters, Func<T> read)
    {
        if (characters < 64 && RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return read();
        }
        T? result = default;
        long allocated = 0;
        ExceptionDispatchInfo? failed = null;
        Thread thread = new(() =>
        {
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                result = read();
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception failure)
            {
                failed = ExceptionDispatchInfo.Capture(failure);
            }
        }, (int)Math.Min(Least + ((long)characters * PerCharacter), Most))
        {
            IsBackground = true,
            Name = "Deep text",
        };
        thread.Start();
        thread.Join();
        failed?.Throw();
        readBytes += allocated;
        return result!;
    }

    /// <summary>Whether a read of so many characters gets all the stack they ask for: one past that may still run out.</summary>
    public static bool Holds(int characters) => Least + ((long)characters * PerCharacter) <= Most;

    /// <summary>Whether a text's brackets go past what's worth a parser's time, a C# text's links too (<see
    /// cref="Brackets.CSharp"/>).</summary>
    public static bool TooDeep(string text, bool csharp = false)
    {
        Brackets brackets = new() { CSharp = csharp };
        brackets.Read(text);
        return brackets.TooDeep;
    }
}

/// <summary>How deep a text's brackets and conditions go, read a stretch at a time. Strings and comments count, for a text no
/// parser has read: a statement's round and square brackets stop counting at its semicolon, so those a text's strings leave
/// open don't add up over a file, and what nests through blocks is in its braces. Real code goes some tens deep. In a C# text
/// as it stands a closer or a semicolon counts only where the text certainly is code (<see cref="ReadCode"/>).</summary>
public partial struct Brackets
{
    /// <summary>Past any code written or generated, and where a parser's cost is still seconds at its worst.</summary>
    private const int Deepest = 1000;

    /// <summary>The most conditional accesses a C# parser reads ahead through at once (<see cref="Link"/>): it reads what follows
    /// each twice or more, so its time grows threefold a level at the worst, some 60 ms at this many. Real code reaches 6.</summary>
    private const int MostLinks = 11;

    private static readonly SearchValues<char> Marks = SearchValues.Create("()[]{};#$");

    private static readonly SearchValues<char> CSharpMarks = SearchValues.Create("()[]{};#$<>?n");

    private Counts counts;
    private int consumed;

    /// <summary>Where a C# text as it stands is read to (<see cref="ReadCode"/>).</summary>
    private Place place;

    /// <summary>Whether the line a stretch starts in has more than spaces and tabs before it.</summary>
    private bool lineWritten;

    /// <summary>Whether a stretch starts past its line's first character.</summary>
    private bool midLine;

    private int directive;
    private int named;
    private int directiveAt;

    /// <summary>The interpolated strings read into, each within a hole of the one before.</summary>
    private Interpolations strings;

    private int nestings;

    /// <summary>The dollars before an interpolated string's quote.</summary>
    private int dollars;

    /// <summary>A raw string's quotes: how many open it, how many in a row are read, and whether it takes lines.</summary>
    private int quotes;

    private int run;
    private bool lines;

    /// <summary>What was open before the string or comment being read, set back at its end.</summary>
    private Counts held;

    /// <summary>Whether C#'s links count as a statement's brackets do: each <c>?</c>, a pattern's <c>not</c> and a lambda's
    /// <c>=&gt;</c>, which a parser and a binder nest through with no bracket, and a type's <c>&lt;</c> closed by its
    /// <c>&gt;</c>, a <c>&lt;</c> before another or an <c>=</c> opening nothing, nor a <c>&gt;</c> after a <c>-</c>; and each
    /// <c>?[</c> once more as a conditional access (<see cref="MostLinks"/>). A stretch's first character has nothing before it.</summary>
    public bool CSharp { get; init; }

    /// <summary>Whether what's read is a lexer's tokens, or text read as code on its own, every closer real.</summary>
    public bool Tokens { get; init; }

    public bool TooDeep { get; private set; }

    /// <summary>Where in all that was read the text first went too deep.</summary>
    public int DeepAt { get; private set; }

    /// <summary>The most brackets and conditions open at once in all that was read, as <see cref="TooDeep"/> counts them.</summary>
    public int Reached { get; private set; }

    /// <summary>Whether what goes too deep is conditions or an interpolation's holes, each reached by a <c>$</c> of its own:
    /// a lexer's cost goes as their square, so these are too deep to ask a lexer about, and reading stops.</summary>
    public bool TooDeepToLex { get; private set; }

    public void Read(ReadOnlySpan<char> text)
    {
        if (CSharp && !Tokens)
        {
            ReadCode(text);
            return;
        }
        SearchValues<char> marks = CSharp ? CSharpMarks : Marks;
        for (int at = text.IndexOfAny(marks); at >= 0 && !TooDeepToLex; at = text.IndexOfAny(marks))
        {
            Mark(text, at, closes: true);
            consumed += at + 1;
            text = text[(at + 1)..];
        }
        consumed += text.Length;
    }

    /// <summary>Counts a mark; a closer, a semicolon or an <c>#endif</c> only where it <paramref name="closes"/> what's open,
    /// which nothing may lie in a string or a comment to do.</summary>
    private void Mark(ReadOnlySpan<char> text, int at, bool closes)
    {
        switch (text[at])
        {
            case '?':
                counts.Statement++;
                if (CSharp && OpensElement(text, at))
                {
                    Link();
                }
                break;
            case '(' or '[':
                counts.Statement++;
                counts.Depth++;
                break;
            case ')' or ']' when closes:
                counts.Statement = Math.Max(0, counts.Statement - 1);
                Close();
                break;
            case '<':
                counts.Statement += Before(text, at) == '<' || After(text, at) is '<' or '=' ? 0 : 1;
                break;
            case '>':
                counts.Statement = Before(text, at) switch
                {
                    '=' => counts.Statement + 1,
                    '-' => counts.Statement,
                    _ when closes => Math.Max(0, counts.Statement - 1),
                    _ => counts.Statement,
                };
                break;
            case 'n':
                counts.Statement += Not(text, at) ? 1 : 0;
                break;
            case '{':
                counts.Braces++;
                counts.Depth++;
                counts.Holes += counts.Dollar ? 1 : 0;
                counts.Dollar = false;
                break;
            case '}' when closes:
                counts.Braces = Math.Max(0, counts.Braces - 1);
                counts.Holes = Math.Max(0, counts.Holes - 1);
                Close();
                break;
            case '$':
                counts.Dollar = true;
                break;
            case '#':
                counts.Conditions += text[(at + 1)..].StartsWith("if") ? 1 : closes && text[(at + 1)..].StartsWith("endif") ? -1 : 0;
                counts.Conditions = Math.Max(0, counts.Conditions);
                break;
            case ';' when closes:
                counts.Statement = 0;
                EndLinks(counts.Depth);
                break;
        }
        Check(consumed + at);
    }

    /// <summary>Counts levels a parser nests through where what's read has no mark of them, as a documentation comment's
    /// elements, whose tags its lexer's tokens close.</summary>
    public void Nest(int levels)
    {
        counts.Statement += levels;
        Check(consumed);
    }

    /// <summary>Notes where the text first goes too deep, and whether it goes too deep to lex.</summary>
    private void Check(int at)
    {
        Reached = Math.Max(Reached, counts.Statement + counts.Braces + counts.Conditions);
        if (!TooDeep && (counts.Statement + counts.Braces + counts.Conditions > Deepest || counts.Linked > MostLinks))
        {
            TooDeep = true;
            DeepAt = at;
        }
        TooDeepToLex = counts.Conditions > Deepest || counts.Holes > Deepest;
    }

    /// <summary>The expression a lexer's tokens are at ended where a closing brace left it, as the token after says: the
    /// conditional accesses read ahead through at its level end with it.</summary>
    public void Ended() => EndLinks(counts.Depth);

    /// <summary>A conditional access, or a conditional with a collection, whose <c>?[</c> a parser reads past to see which it
    /// is: what follows to the end of the expression it starts, a nested one's own reading among it.</summary>
    private void Link()
    {
        if (counts.Levels > 0 && counts.Links[counts.Levels - 1].Depth == counts.Depth)
        {
            counts.Links[counts.Levels - 1].Count++;
        }
        else if (counts.Levels < MostLinks + 1)
        {
            counts.Links[counts.Levels++] = (counts.Depth, 1);
        }
        counts.Linked++;
    }

    private void Close()
    {
        counts.Depth = Math.Max(0, counts.Depth - 1);
        EndLinks(counts.Depth + 1);
    }

    /// <summary>Ends the links read ahead from this depth and those within it.</summary>
    private void EndLinks(int from)
    {
        while (counts.Levels > 0 && counts.Links[counts.Levels - 1].Depth >= from)
        {
            counts.Linked -= counts.Links[--counts.Levels].Count;
        }
    }

    /// <summary>Whether a <c>?</c> may come before a <c>[</c>, as far as the text shows: a comment or a directive may lie
    /// between them, and a stretch's end may cut what follows.</summary>
    private static bool OpensElement(ReadOnlySpan<char> text, int at)
    {
        int next = text[(at + 1)..].IndexOfAnyExcept(" \t\r\n");
        return next < 0 || text[at + 1 + next] is '[' or '/' or '#';
    }

    /// <summary>Whether a text holds the word <c>not</c> at a place, as far as the text shows: a stretch's end may cut it.</summary>
    private static bool Not(ReadOnlySpan<char> text, int at)
    {
        ReadOnlySpan<char> from = text[at..];
        return !InWord(Before(text, at)) && (from.Length > 3 ? from.StartsWith("not") && !InWord(from[3]) : "not".AsSpan().StartsWith(from));
    }

    private static char Before(ReadOnlySpan<char> text, int at) => at > 0 ? text[at - 1] : '\0';

    private static char After(ReadOnlySpan<char> text, int at) => at + 1 < text.Length ? text[at + 1] : '\0';

    private static bool InWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '@';

    /// <summary>What's open where the text is read to, kept whole so a string read on its own can be set aside.</summary>
    private struct Counts
    {
        public int Statement;
        public int Braces;
        public int Conditions;
        public int Holes;
        public bool Dollar;
        public int Depth;
        public LinkLevels Links;
        public int Levels;
        public int Linked;
    }

    /// <summary>The depths conditional accesses are read ahead from, and how many at each: one past the most is too deep.</summary>
    [InlineArray(MostLinks + 1)]
    private struct LinkLevels
    {
        private (int Depth, int Count) first;
    }
}
