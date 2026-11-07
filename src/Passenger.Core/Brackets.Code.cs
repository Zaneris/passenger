using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Passenger.Core;

/// <summary>A C# text as it stands, read for where it certainly is code, so nothing in a string or a comment closes what's
/// open: comments, strings, characters, verbatim and raw strings, and interpolated ones of each kind nested four deep, are
/// read as C#'s lexer reads them, and anything else (a shape the lexer reads as an error, a stretch of lines in a branch that
/// may be left out) leaves the rest of the text unsure. Openers count everywhere; a closer, a semicolon or an <c>#endif</c> only in code outside every <c>#if</c>,
/// whose branch may be text no lexer reads. What a string or a comment opens is set aside at its end, as the lexer's tokens
/// leave it, but for what an unsure stretch opens, which stays.</summary>
public partial struct Brackets
{
    private const string Lines = "\r\n\u0085\u2028\u2029";

    /// <summary>The most interpolated strings read nested, one in a hole of another.</summary>
    private const int MostNestings = 4;

    /// <summary>The most brackets a hole's closers are kept for, two bits each.</summary>
    private const int MostNested = 32;

    private static readonly SearchValues<char> CodeMarks = SearchValues.Create("()[]{};<>?n\"'/@$#" + Lines);

    private static readonly SearchValues<char> HoleMarks = SearchValues.Create("()[]{}<>?n\"'/@$#:" + Lines);

    private static readonly SearchValues<char> DocMarks = SearchValues.Create("([{<>?n#$" + Lines);

    private static readonly SearchValues<char> DocBlockMarks = SearchValues.Create("([{<>?n#$*" + Lines);

    private static readonly SearchValues<char> BlockMarks = SearchValues.Create("*" + Lines);

    private static readonly SearchValues<char> StringMarks = SearchValues.Create("\"\\" + Lines);

    private static readonly SearchValues<char> CharMarks = SearchValues.Create("'\\" + Lines);

    private static readonly SearchValues<char> QuoteMarks = SearchValues.Create("\"" + Lines);

    private static readonly SearchValues<char> ContentMarks = SearchValues.Create("\"\\{}" + Lines);

    private static readonly SearchValues<char> VerbatimContentMarks = SearchValues.Create("\"{}" + Lines);

    private static readonly SearchValues<char> RawContentMarks = SearchValues.Create("\"{}" + Lines);

    private static readonly SearchValues<char> FormatMarks = SearchValues.Create("\"\\{}" + Lines);

    private static readonly SearchValues<char> DirectiveMarks = SearchValues.Create("/\"" + Lines);

    private static readonly SearchValues<char> NewLines = SearchValues.Create(Lines);

    private static readonly int If = Named("if");

    private static readonly int EndIf = Named("endif");

    private static readonly int Else = Named("else");

    private static readonly int Elif = Named("elif");

    /// <summary>Where the text was read to: in code, unsure, or inside what a lexer reads as one token or trivia.</summary>
    private enum Place : byte
    {
        Code,
        Unsure,
        Slash,
        SlashSlash,
        SlashSlashSlash,
        Line,
        DocLine,
        AfterDoc,
        SlashStar,
        SlashStarStar,
        Block,
        BlockStar,
        DocBlock,
        DocBlockStar,
        Directive,
        DirectiveRest,
        Quote,
        QuoteQuote,
        String,
        StringEscape,
        Char,
        CharEscape,
        RawOpen,
        RawAfterOpen,
        Raw,
        RawQuotes,
        At,
        AtDollar,
        Verbatim,
        VerbatimQuote,
        Dollar,
        DollarAt,
        InterpolatedQuote,
        InterpolatedQuoteQuote,
        RawInterpolatedOpen,
        RawInterpolatedAfterOpen,
        RawContent,
        RawContentQuotes,
        RawContentOpen,
        RawContentClose,
        RawHoleClose,
        Content,
        ContentEscape,
        VerbatimContent,
        VerbatimContentQuote,
        ContentOpen,
        ContentClose,
        Hole,
        HoleSlash,
        Format,
    }

    private void ReadCode(ReadOnlySpan<char> text)
    {
        if (!midLine && text.Length > 0 && place == Place.Code && Conflicted(text, 0))
        {
            place = Place.Unsure;
        }
        for (int at = 0; at < text.Length && !TooDeepToLex;)
        {
            at = Step(text, at);
        }
        int line = text.LastIndexOfAny(NewLines);
        lineWritten = text[(line + 1)..].IndexOfAnyExcept(' ', '\t') >= 0 || (line < 0 && lineWritten);
        midLine = text.Length > 0 ? !NewLines.Contains(text[^1]) : midLine;
        consumed += text.Length;
    }

    /// <summary>Whether a C# text read from within, not from its start, starts past more than spaces and tabs on its line.</summary>
    public bool StartsAfterText
    {
        init => lineWritten = value;
    }

    /// <summary>Whether a C# text read from within starts past its line's first character.</summary>
    public bool StartsMidLine
    {
        init => midLine = value;
    }

    /// <summary>Whether a C# text read so far ends in code with no conditional access read ahead through: what follows is read
    /// as on its own.</summary>
    public readonly bool Settled => place == Place.Code && counts.Linked == 0;

    /// <summary>Where a string read within code or a hole goes back to.</summary>
    private readonly Place Back => nestings > 0 ? Place.Hole : Place.Code;

    /// <summary>Reads on from a place in a stretch, to where what's read next starts.</summary>
    private int Step(ReadOnlySpan<char> text, int at)
    {
        char c = text[at];
        switch (place)
        {
            case Place.Code:
                return InCode(text, at);
            case Place.Unsure:
                return Unsure(text, at);
            case Place.Slash:
                place = c switch { '/' => Place.SlashSlash, '*' => Place.SlashStar, _ => Place.Code };
                return place == Place.Code ? at : at + 1;
            case Place.SlashSlash:
                place = c == '/' ? Place.SlashSlashSlash : Place.Line;
                return c == '/' ? at + 1 : at;
            case Place.SlashSlashSlash:
                if (c != '/')
                {
                    Hold();
                }
                place = c == '/' ? Place.Line : Place.DocLine;
                return c == '/' ? at + 1 : at;
            case Place.Line:
                return To(text, at, NewLines, Place.Code);
            case Place.DocLine:
                return InDoc(text, at, DocMarks, Place.DocLine);
            case Place.AfterDoc:
                return AfterDoc(text, at);
            case Place.SlashStar:
                place = c == '*' ? Place.SlashStarStar : Place.Block;
                return c == '*' ? at + 1 : at;
            case Place.SlashStarStar:
                if (c == '/')
                {
                    return To(Place.Code, at + 1);
                }
                Hold();
                return To(Place.DocBlock, at);
            case Place.Block:
                return InBlock(text, at);
            case Place.BlockStar:
                place = c switch { '/' => Place.Code, '*' => Place.BlockStar, _ => Place.Block };
                return place == Place.Block ? at : at + 1;
            case Place.DocBlock:
                return InDoc(text, at, DocBlockMarks, Place.DocBlock);
            case Place.DocBlockStar:
                if (c == '/')
                {
                    Restore();
                }
                place = c switch { '/' => Place.Code, '*' => Place.DocBlockStar, _ => Place.DocBlock };
                return place == Place.DocBlock ? at : at + 1;
            case Place.Directive:
                return InDirective(text, at);
            case Place.DirectiveRest:
                return InDirectiveRest(text, at);
            case Place.Quote:
                return c == '"' ? To(Place.QuoteQuote, at + 1) : To(Place.String, at);
            case Place.QuoteQuote:
                if (c == '"')
                {
                    quotes = 3;
                    return To(Place.RawOpen, at + 1);
                }
                return To(Back, at);
            case Place.String:
                return InString(text, at, StringMarks, '"', Place.StringEscape);
            case Place.StringEscape:
                return Escaped(text, at, Place.String);
            case Place.Char:
                return InString(text, at, CharMarks, '\'', Place.CharEscape);
            case Place.CharEscape:
                return Escaped(text, at, Place.Char);
            case Place.RawOpen:
                if (c == '"')
                {
                    quotes++;
                    return at + 1;
                }
                return To(Place.RawAfterOpen, at);
            case Place.RawAfterOpen:
                if (c is ' ' or '\t')
                {
                    return at + 1;
                }
                lines = IsNewLine(c);
                return To(Place.Raw, at);
            case Place.Raw:
                return InRaw(text, at);
            case Place.RawQuotes:
                if (c == '"')
                {
                    run++;
                    return at + 1;
                }
                return To(run >= quotes ? Back : Place.Raw, at);
            case Place.At:
                return c switch
                {
                    '"' => To(Place.Verbatim, at + 1),
                    '$' => To(Place.AtDollar, at + 1),
                    _ when char.IsLetter(c) || c == '_' => To(Back, at),
                    _ => Lost(at),
                };
            case Place.AtDollar or Place.DollarAt:
                return c == '"' ? Interpolated(at + 1, verbatim: true) : Lost(at);
            case Place.Verbatim:
                return InVerbatim(text, at);
            case Place.VerbatimQuote:
                return c == '"' ? To(Place.Verbatim, at + 1) : To(Back, at);
            case Place.Dollar:
                if (c == '$')
                {
                    dollars++;
                    return at + 1;
                }
                return c switch
                {
                    '"' => Interpolated(at + 1, verbatim: false),
                    '@' when dollars == 1 => To(Place.DollarAt, at + 1),
                    _ => Lost(at),
                };
            case Place.InterpolatedQuote:
                return c == '"' ? To(Place.InterpolatedQuoteQuote, at + 1) : Inner.Dollars == 1 ? To(Place.Content, at) : Lost(at);
            case Place.InterpolatedQuoteQuote:
                if (c == '"')
                {
                    (Inner.Raw, Inner.Quotes) = (true, 3);
                    return To(Place.RawInterpolatedOpen, at + 1);
                }
                if (Inner.Dollars > 1)
                {
                    return Lost(at);
                }
                EndInterpolated();
                return at;
            case Place.RawInterpolatedOpen:
                if (c == '"')
                {
                    Inner.Quotes++;
                    return at + 1;
                }
                return To(Place.RawInterpolatedAfterOpen, at);
            case Place.RawInterpolatedAfterOpen:
                if (c is ' ' or '\t')
                {
                    return at + 1;
                }
                Inner.Lines = IsNewLine(c);
                return To(Place.RawContent, at);
            case Place.RawContent:
                return InRawContent(text, at);
            case Place.RawContentQuotes:
                if (c == '"')
                {
                    run++;
                    return at + 1;
                }
                if (run >= Inner.Quotes)
                {
                    EndInterpolated();
                    return at;
                }
                return To(Place.RawContent, at);
            case Place.RawContentOpen:
                if (c == '{')
                {
                    run++;
                    return at + 1;
                }
                return run < Inner.Dollars ? To(Place.RawContent, at) : run >= 2 * Inner.Dollars ? Lost(at) : OpenHole(at);
            case Place.RawContentClose:
                if (c == '}')
                {
                    run++;
                    return at + 1;
                }
                return run >= Inner.Dollars ? Lost(at) : To(Place.RawContent, at);
            case Place.RawHoleClose:
                if (c == '}')
                {
                    run++;
                    return at + 1;
                }
                // The braces past a hole's own are text, which as many as open a hole would make an error of.
                return run < Inner.Dollars || run - Inner.Dollars >= Inner.Dollars ? Lost(at) : To(Place.RawContent, at);
            case Place.Content:
                return InContent(text, at, ContentMarks);
            case Place.ContentEscape:
                // An escape a brace may come of puts the lexer in its error state, where it reads a hole's quote apart.
                return IsNewLine(c) || c is '{' or '}' or 'u' or 'U' or 'x' ? Lost(at) : To(Place.Content, at + 1);
            case Place.VerbatimContent:
                return InContent(text, at, VerbatimContentMarks);
            case Place.VerbatimContentQuote:
                if (c == '"')
                {
                    return To(Place.VerbatimContent, at + 1);
                }
                EndInterpolated();
                return at;
            case Place.ContentOpen:
                return c == '{' ? To(InnerContent, at + 1) : OpenHole(at);
            case Place.ContentClose:
                return c == '}' ? To(InnerContent, at + 1) : Lost(at);
            case Place.Hole:
                return InHole(text, at);
            case Place.HoleSlash:
                return c is '/' or '*' ? Lost(at) : To(Place.Hole, at);
            default:
                return InFormat(text, at);
        }
    }

    private int InCode(ReadOnlySpan<char> text, int at)
    {
        int next = text[at..].IndexOfAny(CodeMarks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        switch (text[at])
        {
            case '"':
                place = Place.Quote;
                break;
            case '\'':
                place = Place.Char;
                break;
            case '/':
                place = Place.Slash;
                break;
            case '@':
                Hold();
                place = Place.At;
                break;
            case '$':
                Hold();
                (place, dollars) = (Place.Dollar, 1);
                break;
            case '#':
                if (!AtLineStart(text, at))
                {
                    return Lost(at);
                }
                (place, directive, named, directiveAt) = (Place.Directive, 0, 0, consumed + at);
                break;
            case '\r' or '\n' or '\u0085' or '\u2028' or '\u2029':
                if (at + 1 < text.Length && Conflicted(text, at + 1))
                {
                    place = Place.Unsure;
                }
                break;
            default:
                Mark(text, at, closes: counts.Conditions == 0);
                break;
        }
        return at + 1;
    }

    /// <summary>What's past what this reads exactly, where openers count and nothing closes.</summary>
    private int Unsure(ReadOnlySpan<char> text, int at)
    {
        int next = text[at..].IndexOfAny(CSharpMarks);
        if (next < 0)
        {
            return text.Length;
        }
        Mark(text, at + next, closes: false);
        return at + next + 1;
    }

    /// <summary>Reading becomes unsure here, for the rest of the text.</summary>
    private int Lost(int at) => To(Place.Unsure, at);

    private int To(Place then, int at)
    {
        place = then;
        return at;
    }

    /// <summary>Reads on to the first of some characters, where reading goes on in another place.</summary>
    private int To(ReadOnlySpan<char> text, int at, SearchValues<char> marks, Place then)
    {
        int next = text[at..].IndexOfAny(marks);
        return next < 0 ? text.Length : To(then, at + next);
    }

    /// <summary>A documentation comment, which a parser reads: its openers count, until the line or the comment ends.</summary>
    private int InDoc(ReadOnlySpan<char> text, int at, SearchValues<char> marks, Place doc)
    {
        int next = text[at..].IndexOfAny(marks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        if (text[at] == '*')
        {
            return To(Place.DocBlockStar, at + 1);
        }
        if (IsNewLine(text[at]))
        {
            return doc == Place.DocLine ? To(Place.AfterDoc, at) : counts.Conditions > 0 ? Lost(at) : at + 1;
        }
        Mark(text, at, closes: false);
        return at + 1;
    }

    /// <summary>Past a documentation comment's line: another such line goes on with it, as the lexer's one trivia, and what
    /// it opened is set aside only once code comes. Whatever a lexer may take for space is passed over.</summary>
    private int AfterDoc(ReadOnlySpan<char> text, int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at]))
        {
            at++;
        }
        if (at == text.Length)
        {
            return at;
        }
        bool column = at > 0 ? IsNewLine(text[at - 1]) : !midLine;
        if (text[at] != '/')
        {
            Restore();
        }
        return column && Conflicted(text, at) ? Lost(at) : To(Place.Code, at);
    }

    private int InBlock(ReadOnlySpan<char> text, int at)
    {
        int next = text[at..].IndexOfAny(BlockMarks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        return text[at] == '*' ? To(Place.BlockStar, at + 1) : counts.Conditions > 0 ? Lost(at) : at + 1;
    }

    /// <summary>A directive's name: <c>#if</c> opens a condition, and <c>#endif</c> closes one, being one wherever it
    /// lies, as reading stops being sure before anything could hide one.</summary>
    private int InDirective(ReadOnlySpan<char> text, int at)
    {
        char c = text[at];
        if (c is ' ' or '\t' && named == 0)
        {
            return at + 1;
        }
        if (char.IsLetterOrDigit(c) || c == '_')
        {
            directive = c is >= 'a' and <= 'z' && named < 6 ? (directive * 32) + (c - 'a' + 1) : 0;
            named = directive == 0 ? 7 : named + 1;
            return at + 1;
        }
        if (named == 2 && directive == If)
        {
            counts.Conditions++;
            Check(directiveAt);
        }
        else if (named == 5 && directive == EndIf)
        {
            counts.Conditions = Math.Max(0, counts.Conditions - 1);
        }
        else if (named == 4 && (directive == Else || directive == Elif) && counts.Conditions == 0)
        {
            // A text read from within a condition goes on into a branch left out.
            counts.Conditions = 1;
        }
        return To(Place.DirectiveRest, at);
    }

    /// <summary>The rest of a directive's line, which no comment or raw string may carry past.</summary>
    private int InDirectiveRest(ReadOnlySpan<char> text, int at)
    {
        int next = text[at..].IndexOfAny(DirectiveMarks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        if (IsNewLine(text[at]))
        {
            return To(Place.Code, at);
        }
        ReadOnlySpan<char> after = text[(at + 1)..];
        bool opens = text[at] == '/' ? after.IsEmpty || after[0] == '*' : after.Length < 2 || after.StartsWith("\"\"");
        return opens ? Lost(at) : at + 1;
    }

    /// <summary>A string or a character, which ends at its quote and goes on past an escape, or leaves reading unsure at a
    /// line's end, where the lexer ends it and reads on.</summary>
    private int InString(ReadOnlySpan<char> text, int at, SearchValues<char> marks, char quote, Place escape)
    {
        int next = text[at..].IndexOfAny(marks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        char c = text[at];
        return c == quote ? To(Back, at + 1) : c == '\\' ? To(escape, at + 1) : Lost(at);
    }

    /// <summary>An escape's character: a line's end the lexer takes into the string leaves reading unsure.</summary>
    private int Escaped(ReadOnlySpan<char> text, int at, Place then) => IsNewLine(text[at]) ? Lost(at) : To(then, at + 1);

    /// <summary>A raw string, which ends at as many quotes in a row as opened it, or more: one on one line ends there too.</summary>
    private int InRaw(ReadOnlySpan<char> text, int at)
    {
        int next = text[at..].IndexOfAny(QuoteMarks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        if (text[at] == '"')
        {
            run = 1;
            return To(Place.RawQuotes, at + 1);
        }
        return !lines || counts.Conditions > 0 ? Lost(at) : at + 1;
    }

    private int InVerbatim(ReadOnlySpan<char> text, int at)
    {
        int next = text[at..].IndexOfAny(QuoteMarks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        return text[at] == '"' ? To(Place.VerbatimQuote, at + 1) : counts.Conditions > 0 ? Lost(at) : at + 1;
    }

    /// <summary>An interpolated string opens, in code or in a hole of another.</summary>
    private int Interpolated(int at, bool verbatim)
    {
        if (nestings == MostNestings)
        {
            return Lost(at);
        }
        strings[nestings++] = new Interpolation { Verbatim = verbatim, Dollars = verbatim ? 1 : dollars };
        return To(verbatim ? Place.VerbatimContent : Place.InterpolatedQuote, at);
    }

    /// <summary>An interpolated string ends: in code, what its holes opened is set aside.</summary>
    private void EndInterpolated()
    {
        nestings--;
        if (nestings == 0)
        {
            Restore();
        }
        place = Back;
    }

    /// <summary>The innermost interpolated string read into.</summary>
    [UnscopedRef]
    private ref Interpolation Inner => ref strings[nestings - 1];

    private readonly Place InnerContent => strings[nestings - 1] switch
    {
        { Raw: true } => Place.RawContent,
        { Verbatim: true } => Place.VerbatimContent,
        _ => Place.Content,
    };

    /// <summary>A hole opens in an interpolated string's text, its braces counted as code's.</summary>
    private int OpenHole(int at)
    {
        counts.Braces++;
        counts.Depth++;
        Check(consumed + at);
        (Inner.Closers, Inner.Nested) = (0, 0);
        return To(Place.Hole, at);
    }

    /// <summary>A hole's closing brace: a raw string's takes as many as opened it.</summary>
    private int HoleEnded(int at)
    {
        run = 1;
        return To(Inner.Raw ? Place.RawHoleClose : InnerContent, at + 1);
    }

    /// <summary>A raw interpolated string's text: as many quotes as opened it end it, as many braces as its dollars open a
    /// hole, and a line's end leaves reading unsure but in one taking lines.</summary>
    private int InRawContent(ReadOnlySpan<char> text, int at)
    {
        int next = text[at..].IndexOfAny(RawContentMarks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        run = 1;
        return text[at] switch
        {
            '"' => To(Place.RawContentQuotes, at + 1),
            '{' => To(Place.RawContentOpen, at + 1),
            '}' => To(Place.RawContentClose, at + 1),
            _ => Inner.Lines && counts.Conditions == 0 ? at + 1 : Lost(at),
        };
    }

    /// <summary>An interpolated string's text: its quote ends it, a brace opens a hole, and a line's end leaves reading unsure
    /// but in a verbatim one's.</summary>
    private int InContent(ReadOnlySpan<char> text, int at, SearchValues<char> marks)
    {
        int next = text[at..].IndexOfAny(marks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        bool verbatim = place == Place.VerbatimContent;
        switch (text[at])
        {
            case '"' when verbatim:
                return To(Place.VerbatimContentQuote, at + 1);
            case '"':
                EndInterpolated();
                return at + 1;
            case '\\':
                return To(Place.ContentEscape, at + 1);
            case '{':
                return To(Place.ContentOpen, at + 1);
            case '}':
                return To(Place.ContentClose, at + 1);
            default:
                return verbatim && counts.Conditions == 0 ? at + 1 : Lost(at);
        }
    }

    /// <summary>A hole, code a parser reads, whose openers count: its brackets are kept to find its end as the lexer does, and
    /// what the lexer reads as an error there, as a bracket out of place, a comment or a directive, leaves reading unsure.</summary>
    private int InHole(ReadOnlySpan<char> text, int at)
    {
        int next = text[at..].IndexOfAny(HoleMarks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        char c = text[at];
        ref Interpolation hole = ref strings[nestings - 1];
        switch (c)
        {
            case '"':
                return To(Place.Quote, at + 1);
            case '\'':
                return To(Place.Char, at + 1);
            case '@':
                return To(Place.At, at + 1);
            case '$':
                dollars = 1;
                return To(Place.Dollar, at + 1);
            case '/':
                return To(Place.HoleSlash, at + 1);
            case ':' when hole.Nested == 0:
                return To(Place.Format, at + 1);
            case ':':
                return at + 1;
            case '(' or '[' or '{' when hole.Nested < MostNested:
                hole.Closers = (hole.Closers << 2) | (c == '(' ? 1UL : c == '[' ? 2UL : 3UL);
                hole.Nested++;
                Mark(text, at, closes: false);
                return at + 1;
            case '}' when hole.Nested == 0:
                return HoleEnded(at);
            case ')' or ']' or '}' when hole.Nested > 0 && (hole.Closers & 3) == (c == ')' ? 1UL : c == ']' ? 2UL : 3UL):
                hole.Closers >>= 2;
                hole.Nested--;
                return at + 1;
            case '<' or '>' or '?' or 'n':
                Mark(text, at, closes: false);
                return at + 1;
            case '\r' or '\n' or '\u0085' or '\u2028' or '\u2029' when counts.Conditions == 0:
                return at + 1;
            default:
                return Lost(at);
        }
    }

    /// <summary>A hole's format, text to its closing brace.</summary>
    private int InFormat(ReadOnlySpan<char> text, int at)
    {
        int next = text[at..].IndexOfAny(FormatMarks);
        if (next < 0)
        {
            return text.Length;
        }
        at += next;
        return text[at] == '}' ? HoleEnded(at) : Lost(at);
    }

    /// <summary>Keeps what's open before a string or a comment whose openers count, to set it back at its end.</summary>
    private void Hold() => held = counts;

    private void Restore() => counts = held;

    /// <summary>Whether a <c>#</c> is the first of its line but for spaces and tabs, where a lexer reads a directive.</summary>
    private readonly bool AtLineStart(ReadOnlySpan<char> text, int at)
    {
        int line = text[..at].LastIndexOfAny(NewLines);
        return text[(line + 1)..at].IndexOfAnyExcept(' ', '\t') < 0 && (line >= 0 || !lineWritten);
    }

    /// <summary>Whether a line may start with a merge's conflict marker, seven alike, which a lexer reads with what follows
    /// as trivia: as many alike as a stretch holds may be one.</summary>
    private static bool Conflicted(ReadOnlySpan<char> text, int at)
    {
        char c = text[at];
        return c is '<' or '=' or '>' or '|' && text[at..Math.Min(text.Length, at + 7)].IndexOfAnyExcept(c) < 0;
    }

    private static bool IsNewLine(char c) => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';

    private static int Named(string name)
    {
        int packed = 0;
        foreach (char c in name)
        {
            packed = (packed * 32) + (c - 'a' + 1);
        }
        return packed;
    }

    /// <summary>An interpolated string read into: its kind, a raw one's dollars and quotes, and its hole's brackets, two bits
    /// each.</summary>
    private struct Interpolation
    {
        public bool Verbatim;
        public bool Raw;
        public bool Lines;
        public int Dollars;
        public int Quotes;
        public ulong Closers;
        public int Nested;
    }

    [InlineArray(MostNestings)]
    private struct Interpolations
    {
        private Interpolation first;
    }
}
