using System.Buffers;
using System.Runtime.CompilerServices;
using Passenger.Text;

namespace Passenger.Lsp;

/// <summary>A snapshot's lines as Roslyn counts them, which every position to and from the server is in: a lone '\r',
/// U+0085, U+2028 and U+2029 end a line there as '\n' and "\r\n" do, though the editor's lines end only at '\n', so a
/// server position read as a <see cref="TextPosition"/> lands lines away in a file holding one. Kept per snapshot as
/// the offsets just past those extra breaks; a file without them converts as the editor's lines do. An XML reader's
/// lines (<see cref="OfXml"/>) are kept the same way.</summary>
public sealed class ServerLines
{
    /// <summary>What ends a line for an XML reader and not for the editor: a lone '\r' alone, as XML 1.0 reads breaks.</summary>
    private static readonly SearchValues<char> XmlBreaks = SearchValues.Create("\r");

    /// <summary>The first byte of each break in UTF-8 that the server counts and the editor may not: '\r', U+0085's 0xC2,
    /// and U+2028's and U+2029's 0xE2.</summary>
    private static readonly SearchValues<byte> BreakStarts = SearchValues.Create([0x0D, 0xC2, 0xE2]);

    private static readonly ConditionalWeakTable<TextSnapshot, ServerLines> Known = new();

    private static readonly ConditionalWeakTable<TextSnapshot, ServerLines> KnownXml = new();

    [ThreadStatic]
    private static long read;

    /// <summary>The offset just past each break the server counts and the editor does not, in order.</summary>
    private readonly int[] extra;

    /// <summary>The characters that may end a line here and not in the editor ('\r' only when no '\n' follows).</summary>
    private readonly SearchValues<char> breaks;

    private ServerLines(TextSnapshot text, int[] extra, SearchValues<char> breaks)
    {
        Text = text;
        this.extra = extra;
        this.breaks = breaks;
    }

    public TextSnapshot Text { get; }

    public int LineCount => Text.LineCount + extra.Length;

    /// <summary>How many characters this thread has read to find where the server's lines break: what a key costs here,
    /// counted rather than timed.</summary>
    public static long CharactersRead => read;

    /// <summary>Whether UTF-8 text holds a break the server counts and the editor does not: a '\r' with no '\n' after it,
    /// U+0085, U+2028 or U+2029. A vectorized look for each break's first byte, the rest of it confirmed where one is.</summary>
    public static bool BreaksApart(ReadOnlySpan<byte> utf8)
    {
        int at = 0;
        while (utf8[at..].IndexOfAny(BreakStarts) is int found and >= 0)
        {
            at += found;
            ReadOnlySpan<byte> rest = utf8[at..];
            bool counts = rest[0] switch
            {
                0x0D => rest.Length == 1 || rest[1] != 0x0A,
                0xC2 => rest.Length > 1 && rest[1] == 0x85,
                _ => rest.Length > 2 && rest[1] == 0x80 && rest[2] is 0xA8 or 0xA9,
            };
            if (counts)
            {
                return true;
            }
            at++;
        }
        return false;
    }

    /// <summary>The lines of a snapshot, found once and kept with it; safe from any thread.</summary>
    public static ServerLines Of(TextSnapshot text) => Known.GetValue(text, static text => Find(text, RoslynLines.Breaks));

    /// <summary>The lines of a snapshot as an XML reader counts them, which Avalonia's and WPF's XAML compilers write into a
    /// PDB and an element's place: a lone '\r' ends a line, U+0085, U+2028 and U+2029 do not. Found once and kept with it;
    /// safe from any thread.</summary>
    public static ServerLines OfXml(TextSnapshot text) => KnownXml.GetValue(text, static text => Find(text, XmlBreaks));

    /// <summary>The lines of the text a change made, carried from those of the text before it and kept with it, reading
    /// again only around each edit, so a key in a long file never reads all of it; null when the text before had none,
    /// as nothing asked for them. Carried as the change is heard, before anything moved with it asks for them.</summary>
    public static ServerLines? After(DocumentChangedEventArgs change)
    {
        if (Known.TryGetValue(change.After, out ServerLines? known))
        {
            return known;
        }
        if (!Known.TryGetValue(change.Before, out ServerLines? before))
        {
            return null;
        }
        return Known.GetValue(change.After, after => before.Through(change.Edits, after));
    }

    /// <summary>An offset as the server's line and column (UTF-16 units); between the halves of "\r\n" it is the
    /// column past the '\r', as Roslyn counts it, though it reads a column past a line's end as the line's end, so no
    /// change is sent there (LspDocument).</summary>
    public LspPosition PositionOf(int offset)
    {
        int line = Text.GetLineFromOffset(offset);
        int start = Text.GetLineStart(line);
        int before = Before(offset);
        if (before > 0)
        {
            start = Math.Max(start, extra[before - 1]);
        }
        return new LspPosition(line + before, offset - start);
    }

    public LspRange RangeOf(TextRange range) => new(PositionOf(range.Start), PositionOf(range.End));

    /// <summary>The offset of the server's line and column, the column clamped to the line's content.</summary>
    public int OffsetOf(LspPosition position) => OffsetOf(position.Line, position.Character);

    public int OffsetOf(int line, int character)
    {
        if (extra.Length == 0)
        {
            return Text.GetOffset(new TextPosition(line, character));
        }
        line = Math.Clamp(line, 0, LineCount - 1);
        // How many extra breaks come before the line: they and the editor's lines before it add up to its number.
        int low = 0;
        int high = extra.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (LineAfter(middle) <= line)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        LineSpan span = Text.GetLine(line - low);
        int start = low > 0 && LineAfter(low - 1) == line ? extra[low - 1] : span.Start;
        int end = low < extra.Length && extra[low] <= span.End ? extra[low] - 1 : span.End;
        return start + Math.Clamp(character, 0, end - start);
    }

    /// <summary>The server's line that starts just past an extra break.</summary>
    private int LineAfter(int index) => Text.GetLineFromOffset(extra[index]) + index + 1;

    /// <summary>How many extra breaks end at or before an offset.</summary>
    private int Before(int offset)
    {
        int found = Array.BinarySearch(extra, offset);
        return found >= 0 ? found + 1 : ~found;
    }

    /// <summary>These lines carried through edits (sorted, in this text's offsets) to the text they make. A break the server
    /// counts comes or goes only in text an edit put in, or just before an edit, where a '\r' may lose or meet its '\n';
    /// every other moves with the text.</summary>
    private ServerLines Through(IReadOnlyList<TextEdit> edits, TextSnapshot after)
    {
        List<int>? found = null;
        int e = 0;
        int delta = 0;
        foreach (TextEdit edit in edits)
        {
            while (e < extra.Length && extra[e] < edit.Start)
            {
                Add(ref found, extra[e] + delta);
                e++;
            }
            // A break the edit replaced, or one just before it whose next character it changed: read again below.
            while (e < extra.Length && extra[e] <= edit.End)
            {
                e++;
            }
            int at = edit.Start + delta;
            if (at > 0)
            {
                Check(after, breaks, at - 1, ref found);
            }
            ReadOnlySpan<char> text = edit.Text;
            read += text.Length + 1;
            int i = 0;
            while (i < text.Length && text[i..].IndexOfAny(breaks) is int next and >= 0)
            {
                i += next;
                Check(after, breaks, at + i, ref found);
                i++;
            }
            delta += edit.Text.Length - edit.Length;
        }
        while (e < extra.Length)
        {
            Add(ref found, extra[e] + delta);
            e++;
        }
        return new ServerLines(after, found is null ? [] : [.. found], breaks);
    }

    /// <summary>Notes the character at an offset when it is a break the server counts and the editor does not.</summary>
    private static void Check(TextSnapshot text, SearchValues<char> breaks, int offset, ref List<int>? found)
    {
        char c = text[offset];
        bool counts = c == '\r' ? offset + 1 == text.Length || text[offset + 1] != '\n' : breaks.Contains(c);
        if (counts)
        {
            Add(ref found, offset + 1);
        }
    }

    /// <summary>Adds an offset past a break, once: edits that meet read the character between them twice.</summary>
    private static void Add(ref List<int>? found, int offset)
    {
        found ??= [];
        if (found.Count == 0 || found[^1] < offset)
        {
            found.Add(offset);
        }
    }

    private static ServerLines Find(TextSnapshot text, SearchValues<char> breaks)
    {
        read += text.Length;
        List<int> extra = [];
        int at = 0;
        bool carriage = false;
        text.VisitChunks(0, text.Length, chunk =>
        {
            ReadOnlySpan<char> span = chunk.Span;
            // A '\r' that ended the chunk before is a break of its own unless this one starts with its '\n'.
            if (carriage && span[0] != '\n')
            {
                extra.Add(at);
            }
            carriage = false;
            int i = 0;
            while (i < span.Length && span[i..].IndexOfAny(breaks) is int found and >= 0)
            {
                i += found;
                if (span[i] != '\r' || (i + 1 < span.Length && span[i + 1] != '\n'))
                {
                    extra.Add(at + i + 1);
                }
                else if (i + 1 == span.Length)
                {
                    carriage = true;
                }
                i++;
            }
            at += span.Length;
        });
        if (carriage)
        {
            extra.Add(at);
        }
        return new ServerLines(text, [.. extra], breaks);
    }
}
