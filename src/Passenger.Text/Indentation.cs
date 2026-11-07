namespace Passenger.Text;

/// <summary>How a document indents: <see cref="Size"/> columns a level, written as spaces or as tabs
/// <see cref="TabWidth"/> columns wide.</summary>
public readonly record struct IndentOptions(int Size = 4, bool UseTabs = false, int TabWidth = 4)
{
    /// <summary>Four spaces. Without this, <c>new()</c> on a record struct would skip the defaults
    /// above and give a zero-wide indent.</summary>
    public IndentOptions()
        : this(4)
    {
    }

    public string Unit => UseTabs ? "\t" : new string(' ', Size);

    /// <summary>The column a run of spaces and tabs reaches from a line's start, each tab going to its next stop.</summary>
    public int Columns(ReadOnlySpan<char> whitespace)
    {
        int columns = 0;
        foreach (char c in whitespace)
        {
            columns = c == '\t' ? (columns / TabWidth + 1) * TabWidth : columns + 1;
        }
        return columns;
    }

    /// <summary>Leading whitespace reaching a column, in this indentation's own characters: tabs as far as they go and
    /// spaces after, or spaces alone.</summary>
    public string Whitespace(int columns) =>
        UseTabs ? new string('\t', columns / TabWidth) + new string(' ', columns % TabWidth) : new string(' ', columns);
}

/// <summary>A file's indentation found from its own lines, and indentation written elsewhere (a language server's edit, a
/// pasted block) made the file's.</summary>
public static class Indentation
{
    /// <summary>The widest step taken for a level.</summary>
    private const int WidestStep = 8;

    /// <summary>How wide a tab is to a reader whose blocks are its indentation.</summary>
    private const int BlockTabWidth = 8;

    /// <summary>The indentation a file's lines use: tabs when more lines start with a tab than with spaces, else the
    /// step most changes of indentation between neighbouring lines take, when more than half take it. Blank lines and a
    /// block comment's aligned <c>*</c> lines say nothing, nor in <paramref name="markup"/> a line that begins inside a
    /// tag or a comment, where attributes are aligned. Null when fewer than <paramref name="minimumLines"/> lines are
    /// indented, or no step stands out.</summary>
    public static IndentOptions? Detect(ReadOnlySpan<char> text, int tabWidth, int minimumLines = 2, bool markup = false)
    {
        int tabs = 0;
        int spaces = 0;
        Dictionary<int, int> steps = [];
        int? previous = null;
        MarkupState state = default;
        while (text.Length > 0)
        {
            int feed = text.IndexOf('\n');
            ReadOnlySpan<char> line = feed < 0 ? text : text[..feed];
            text = feed < 0 ? [] : text[(feed + 1)..];
            bool continued = markup && state.Inside;
            if (markup)
            {
                state.Read(line);
            }
            int lead = 0;
            while (lead < line.Length && line[lead] is ' ' or '\t')
            {
                lead++;
            }
            if (lead == line.Length || line[lead] == '\r' || continued)
            {
                continue;
            }
            if (line[0] == '\t')
            {
                tabs++;
                previous = null;
                continue;
            }
            if (line[lead] == '*' || line[..lead].Contains('\t'))
            {
                continue;
            }
            if (lead > 0)
            {
                spaces++;
            }
            int step = previous is { } before ? Math.Abs(lead - before) : 0;
            if (step is > 0 and <= WidestStep)
            {
                steps[step] = steps.GetValueOrDefault(step) + 1;
            }
            previous = lead;
        }
        if (tabs + spaces < minimumLines || tabs + spaces == 0)
        {
            return null;
        }
        if (tabs > spaces)
        {
            return new IndentOptions(tabWidth, UseTabs: true, tabWidth);
        }
        if (steps.Count == 0)
        {
            return null;
        }
        (int size, int count) = steps.OrderByDescending(s => s.Value).ThenBy(s => s.Key).Select(s => (s.Key, s.Value)).First();
        return count * 2 > steps.Values.Sum() ? new IndentOptions(size, UseTabs: false, tabWidth) : null;
    }

    /// <summary>Where a scan of markup is between lines: inside a tag (and a quoted value in it) or a comment.</summary>
    private struct MarkupState
    {
        private bool tag;
        private bool comment;
        private char quote;

        public readonly bool Inside => tag || comment;

        public void Read(ReadOnlySpan<char> line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (comment)
                {
                    if (line[i..].StartsWith("-->"))
                    {
                        comment = false;
                        i += 2;
                    }
                }
                else if (quote != '\0')
                {
                    quote = c == quote ? '\0' : quote;
                }
                else if (tag)
                {
                    if (c is '"' or '\'')
                    {
                        quote = c;
                    }
                    else if (c == '>')
                    {
                        tag = false;
                    }
                }
                else if (line[i..].StartsWith("<!--"))
                {
                    comment = true;
                    i += 3;
                }
                else if (c == '<')
                {
                    tag = true;
                }
            }
        }
    }

    /// <summary>A block's indentation as a document's: each level of <paramref name="from"/> one of
    /// <paramref name="to"/>, in its characters, what is left past the last level kept as columns. The first line is
    /// left as it is unless <paramref name="firstLine"/>, since a block put in mid-line starts there.</summary>
    public static string Reindent(string block, IndentOptions from, IndentOptions to, bool firstLine)
    {
        if (from == to || !block.Contains('\n', StringComparison.Ordinal) && !firstLine)
        {
            return block;
        }
        System.Text.StringBuilder written = new(block.Length);
        int at = 0;
        bool first = true;
        while (at <= block.Length)
        {
            int feed = block.IndexOf('\n', at);
            int end = feed < 0 ? block.Length : feed + 1;
            int lead = at;
            while (lead < end && block[lead] is ' ' or '\t')
            {
                lead++;
            }
            bool blank = lead == end || block[lead] is '\r' or '\n';
            if ((first && !firstLine) || lead == at || blank)
            {
                written.Append(block, at, end - at);
            }
            else
            {
                int columns = from.Columns(block.AsSpan(at, lead - at));
                written.Append(to.Whitespace(columns / from.Size * to.Size + columns % from.Size)).Append(block, lead, end - lead);
            }
            first = false;
            if (feed < 0)
            {
                break;
            }
            at = end;
        }
        return written.ToString();
    }

    /// <summary>A block's own indentation, for putting it in a document of <paramref name="to"/>'s: as its lines show it
    /// (the first only when it starts a line), else spaces of the document's width.</summary>
    public static IndentOptions Of(string block, IndentOptions to, bool firstLine)
    {
        ReadOnlySpan<char> lines = firstLine ? block : block.AsSpan(block.IndexOf('\n', StringComparison.Ordinal) + 1);
        return Detect(lines, to.TabWidth, minimumLines: 1) ?? to with { UseTabs = false };
    }

    /// <summary>Every line's leading spaces and tabs written again in <paramref name="to"/>'s characters, at the columns they
    /// reach with tabs <paramref name="from"/>'s width; what follows on the line (alignment within it, a comment's) is left
    /// as it is, and so is each line <paramref name="kept"/> names (one that begins inside a string, whose whitespace is the
    /// string's). One edit per line it changes, in order, and how many lines kept it would have changed.</summary>
    public static (List<TextEdit> Edits, int Kept) Convert(TextSnapshot text, IndentOptions from, IndentOptions to, IReadOnlySet<int>? kept = null)
    {
        List<TextEdit> edits = [];
        int left = 0;
        Span<char> start = stackalloc char[256];
        for (int i = 0; i < text.LineCount; i++)
        {
            LineSpan line = text.GetLine(i);
            int read = Math.Min(line.Length, start.Length);
            text.CopyTo(line.Start, start[..read]);
            // A line whose whitespace runs past what was read is read whole.
            ReadOnlySpan<char> head = read == start.Length && Lead(start) == read ? text.GetText(line.Start, line.Length) : start[..read];
            ReadOnlySpan<char> was = head[..Lead(head)];
            if (was.Length == 0)
            {
                continue;
            }
            string now = to.Whitespace(from.Columns(was));
            if (was.SequenceEqual(now))
            {
                continue;
            }
            if (kept?.Contains(i) == true)
            {
                left++;
                continue;
            }
            edits.Add(new TextEdit(line.Start, was.Length, now));
        }
        return (edits, left);
    }

    /// <summary>The first line whose block a conversion could change, in a file whose blocks are its indentation and whose
    /// reader counts a tab as 8 columns and as 1, refusing a block the two disagree on; null for none. Either way the file's own
    /// tab width must order lines as the reader does (<see cref="ReadsTabsApart"/>). To tabs, a block's parent must be a whole
    /// number of levels deep, as under tabs alone whatever is deeper reads as deeper. A line <paramref name="kept"/> names is
    /// no block's.</summary>
    public static int? FirstBlockAtRisk(TextSnapshot text, IndentOptions from, bool toTabs, IReadOnlySet<int>? kept, IReadOnlySet<string> blockWords)
    {
        for (int i = 0; i < text.LineCount; i++)
        {
            LineSpan span = text.GetLine(i);
            string line = text.GetText(span.Start, span.Length);
            int lead = Lead(line);
            if (lead < line.Length && kept?.Contains(i) != true && (ReadsTabsApart(line.AsSpan(0, lead), from.TabWidth)
                || (toTabs && from.Columns(line.AsSpan(0, lead)) % from.Size != 0 && MayOpenBlock(line, lead, blockWords))))
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>Whether a tab width could order this leading whitespace unlike a reader counting a tab as 8 columns and as 1.
    /// Tabs before spaces, at a width between those, read in between them, so the order the two agree on holds.</summary>
    private static bool ReadsTabsApart(ReadOnlySpan<char> lead, int tabWidth) =>
        tabWidth != BlockTabWidth && lead.Contains('\t') && (tabWidth > BlockTabWidth || lead.TrimStart('\t').Contains('\t'));

    /// <summary>Whether a line begins with a word that opens a block, or a backslash or form feed hiding where its statement starts.</summary>
    private static bool MayOpenBlock(string line, int lead, IReadOnlySet<string> blockWords)
    {
        int end = lead;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
        {
            end++;
        }
        return line[lead] is '\\' or '\f' || blockWords.Contains(line[lead..end]);
    }

    /// <summary>How many spaces and tabs begin a run of text.</summary>
    private static int Lead(ReadOnlySpan<char> text)
    {
        int lead = 0;
        while (lead < text.Length && text[lead] is ' ' or '\t')
        {
            lead++;
        }
        return lead;
    }

    /// <summary>A language server's edits (sorted, not overlapping, as <see cref="LineEndings.Fit"/> leaves them) with the
    /// indentation they write in the text's own characters: a server that writes spaces where the file has tabs, or the
    /// other way, writes the same columns. Only whitespace the server wrote or changed is touched, and an edit that then
    /// changes nothing is left out.</summary>
    public static List<TextEdit> Fit(TextSnapshot text, IReadOnlyList<TextEdit> edits, IndentOptions indent)
    {
        List<TextEdit> fitted = [];
        for (int i = 0; i < edits.Count; i++)
        {
            int floor = fitted.Count > 0 ? fitted[^1].End : 0;
            int ceiling = i + 1 < edits.Count ? edits[i + 1].Start : text.Length;
            int caret = 0;
            if (FitOne(text, edits[i], indent, floor, ceiling, ref caret) is { } edit)
            {
                fitted.Add(edit);
            }
        }
        return fitted;
    }

    /// <summary>One edit fitted as <see cref="Fit(TextSnapshot, IReadOnlyList{TextEdit}, IndentOptions)"/> does, and where
    /// an offset in the text it writes (<paramref name="caret"/>) lands in the fitted edit's text. Null when it comes to nothing.</summary>
    public static TextEdit? Fit(TextSnapshot text, TextEdit edit, IndentOptions indent, ref int caret) =>
        FitOne(text, edit, indent, 0, text.Length, ref caret);

    private static TextEdit? FitOne(TextSnapshot text, TextEdit edit, IndentOptions indent, int floor, int ceiling, ref int caret)
    {
        // Widened to the line's start when it begins in the indentation, so that indentation is seen whole.
        int start = edit.Start;
        LineSpan line = text.GetLine(text.GetLineFromOffset(start));
        bool lineStart = start == line.Start;
        if (!lineStart && line.Start >= floor && OnlyWhitespace(text, line.Start, start))
        {
            start = line.Start;
            lineStart = true;
        }
        string written = text.GetText(start, edit.Start - start) + edit.Text;
        int from = edit.Start - start;
        int to = from + edit.Text.Length;
        // The last line's indentation, when the edit ends in it, goes on over the text's after it.
        int lastStart = written.LastIndexOf('\n') + 1;
        int end = edit.End;
        if ((lastStart > 0 || lineStart) && OnlyWhitespace(written, lastStart, written.Length))
        {
            while (end < ceiling && text[end] is ' ' or '\t')
            {
                end++;
            }
            written += text.GetText(edit.End, end - edit.End);
        }
        bool deletesWhitespace = edit.Length > 0 && OnlyWhitespace(text, edit.Start, edit.End);
        List<int> starts = lineStart ? [0] : [];
        for (int feed = written.IndexOf('\n'); feed >= 0; feed = written.IndexOf('\n', feed + 1))
        {
            starts.Add(feed + 1);
        }
        List<(int At, int Lead, string Own)> rewritten = [];
        foreach (int at in starts)
        {
            int lead = at;
            while (lead < written.Length && written[lead] is ' ' or '\t')
            {
                lead++;
            }
            // Only indentation the server wrote, or took some of, is its to fit.
            bool touched = (at < to && lead > from && to > from) || (deletesWhitespace && at <= from && from <= lead);
            string own = indent.Whitespace(indent.Columns(written.AsSpan(at, lead - at)));
            if (touched && lead > at && !own.AsSpan().SequenceEqual(written.AsSpan(at, lead - at)))
            {
                rewritten.Add((at, lead, own));
            }
        }
        if (rewritten.Count == 0)
        {
            return edit;
        }
        System.Text.StringBuilder made = new(written.Length);
        int copied = 0;
        int moved = caret + from;
        int shift = 0;
        bool placed = false;
        foreach ((int at, int lead, string own) in rewritten)
        {
            made.Append(written, copied, at - copied).Append(own);
            copied = lead;
            if (!placed && moved < lead)
            {
                caret = moved <= at ? moved + shift : at + shift + Math.Min(moved - at, own.Length);
                placed = true;
            }
            shift += own.Length - (lead - at);
        }
        made.Append(written, copied, written.Length - copied);
        caret = placed ? caret : moved + shift;
        string result = made.ToString();
        return end - start == result.Length && text.GetText(start, result.Length) == result ? null : new TextEdit(start, end - start, result);
    }

    private static bool OnlyWhitespace(string text, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (text[i] is not (' ' or '\t'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool OnlyWhitespace(TextSnapshot text, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (text[i] is not (' ' or '\t'))
            {
                return false;
            }
        }
        return true;
    }
}
