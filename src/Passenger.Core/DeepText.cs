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
/// open don't add up over a file, and what nests through blocks is in its braces. Real code goes some tens deep.</summary>
public struct Brackets
{
    /// <summary>Past any code written or generated, and where a parser's cost is still seconds at its worst.</summary>
    private const int Deepest = 1000;

    private static readonly SearchValues<char> Marks = SearchValues.Create("()[]{};#$");

    private static readonly SearchValues<char> CSharpMarks = SearchValues.Create("()[]{};#$<>?n");

    private int statement;
    private int braces;
    private int conditions;
    private int holes;
    private bool dollar;
    private int consumed;

    /// <summary>Whether C#'s links count as a statement's brackets do: each <c>?</c>, a pattern's <c>not</c> and a lambda's
    /// <c>=&gt;</c>, which a parser and a binder nest through with no bracket, and a type's <c>&lt;</c> closed by its
    /// <c>&gt;</c>. A <c>&lt;</c> before another or an <c>=</c> opens nothing, nor does a <c>&gt;</c> after a <c>-</c>; a
    /// stretch's first character is read as having nothing before it.</summary>
    public bool CSharp { get; init; }

    public bool TooDeep { get; private set; }

    /// <summary>Where in all that was read the text first went too deep.</summary>
    public int DeepAt { get; private set; }

    /// <summary>Whether what goes too deep is conditions or an interpolation's holes, each reached by a <c>$</c> of its own:
    /// a lexer's cost goes as their square, so these are too deep to ask a lexer about, and reading stops.</summary>
    public bool TooDeepToLex { get; private set; }

    public void Read(ReadOnlySpan<char> text)
    {
        SearchValues<char> marks = CSharp ? CSharpMarks : Marks;
        for (int at = text.IndexOfAny(marks); at >= 0 && !TooDeepToLex; at = text.IndexOfAny(marks))
        {
            switch (text[at])
            {
                case '(' or '[' or '?':
                    statement++;
                    break;
                case ')' or ']':
                    statement = Math.Max(0, statement - 1);
                    break;
                case '<':
                    statement += Before(text, at) == '<' || After(text, at) is '<' or '=' ? 0 : 1;
                    break;
                case '>':
                    statement = Before(text, at) switch
                    {
                        '=' => statement + 1,
                        '-' => statement,
                        _ => Math.Max(0, statement - 1),
                    };
                    break;
                case 'n':
                    statement += Not(text, at) ? 1 : 0;
                    break;
                case '{':
                    braces++;
                    holes += dollar ? 1 : 0;
                    dollar = false;
                    break;
                case '}':
                    braces = Math.Max(0, braces - 1);
                    holes = Math.Max(0, holes - 1);
                    break;
                case '$':
                    dollar = true;
                    break;
                case '#':
                    conditions += text[(at + 1)..].StartsWith("if") ? 1 : text[(at + 1)..].StartsWith("endif") ? -1 : 0;
                    conditions = Math.Max(0, conditions);
                    break;
                default:
                    statement = 0;
                    break;
            }
            if (!TooDeep && statement + braces + conditions > Deepest)
            {
                TooDeep = true;
                DeepAt = consumed + at;
            }
            TooDeepToLex = conditions > Deepest || holes > Deepest;
            consumed += at + 1;
            text = text[(at + 1)..];
        }
        consumed += text.Length;
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
}
