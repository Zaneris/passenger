using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Passenger.App.Editors;

/// <summary>Markdown read into blocks and their words, as CommonMark's common part reads them (ATX headings, paragraphs,
/// bullet and numbered lists to any depth, block quotes, fenced code, rules; code spans, strong, emphasis, links, images
/// and autolinks) with GitHub's tables and task lists, and drawn as Passenger's own controls, styled in Shell.axaml, fenced
/// code coloured as the editor colours its language. HTML comments are left out; what a link may open and whether an
/// image is drawn are the page's (<see cref="MarkdownPage"/>): the notes' a web link alone and no image. Nothing is fetched.</summary>
internal static partial class Markdown
{
    internal abstract record Block;

    internal sealed record Heading(int Level, string Text) : Block;

    internal sealed record Paragraph(string Text) : Block;

    /// <summary>A list; a task list's items have their check (GitHub's <c>- [x]</c>), null for an item with none.</summary>
    internal sealed record Listing(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<Block>> Items, IReadOnlyList<bool?>? Checks = null) : Block;

    /// <summary>Fenced code with its info string's first word (<c>csharp</c>), empty for none.</summary>
    internal sealed record Code(string Text, string Info = "") : Block;

    /// <summary>GitHub's table: each column's alignment (null for none), the header's cells and each row's, as written.</summary>
    internal sealed record Table(IReadOnlyList<TextAlignment?> Aligns, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : Block;

    internal sealed record Quote(IReadOnlyList<Block> Blocks) : Block;

    internal sealed record Rule : Block;

    internal abstract record Inline;

    internal sealed record Words(string Text) : Inline;

    internal sealed record CodeSpan(string Text) : Inline;

    internal sealed record Strong(IReadOnlyList<Inline> Parts) : Inline;

    internal sealed record Emphasis(IReadOnlyList<Inline> Parts) : Inline;

    /// <summary>A link's words and where it goes, as written: what it may open is the page's.</summary>
    internal sealed record Link(IReadOnlyList<Inline> Parts, string Target) : Inline;

    /// <summary>An image's words, which stand for it where it is not drawn, and where its file is, as written.</summary>
    internal sealed record Image(string Alt, string Target) : Inline;

    /// <summary>A block of a text's top level with the lines it was read from (from zero, <see cref="End"/> past its last)
    /// and those lines as written, by which a page tells a block that changed from one that did not.</summary>
    internal sealed record Placed(Block Block, int Line, int End, string Source);

    /// <summary>The blocks of a Markdown text.</summary>
    public static IReadOnlyList<Block> Parse(string text) =>
        Blocks([.. text.ReplaceLineEndings("\n").Split('\n').Select(Untabbed)]);

    /// <summary>The blocks of a text's top level, each with its lines, which are the editor's: a "\r\n", a '\r' or a '\n'
    /// ends one.</summary>
    public static IReadOnlyList<Placed> ParsePlaced(string text)
    {
        string[] written = EditorLines().Split(text);
        List<(int Start, int End)> spans = [];
        List<Block> blocks = Blocks([.. written.Select(Untabbed)], spans);
        List<Placed> placed = new(blocks.Count);
        for (int b = 0; b < blocks.Count; b++)
        {
            (int start, int end) = spans[b];
            placed.Add(new Placed(blocks[b], start, end, string.Join('\n', written, start, end - start)));
        }
        return placed;
    }

    /// <summary>Draws blocks into a panel, a control each; a link's click is handed to <paramref name="browse"/>, which
    /// takes a web link alone, and no image is drawn.</summary>
    public static void Render(IReadOnlyList<Block> blocks, Panel into, Action<Uri> browse) => Render(blocks, into, new MarkdownPage(browse), 0);

    /// <summary>Draws blocks into a panel, a control each, as the page draws its links and images.</summary>
    public static void Render(IReadOnlyList<Block> blocks, Panel into, MarkdownPage page, int depth = 0)
    {
        foreach (Block block in blocks)
        {
            into.Children.Add(Control(block, page, depth));
        }
    }

    /// <summary>A block's control, as the page draws it.</summary>
    public static Control Control(Block block, MarkdownPage page, int depth = 0) => block switch
    {
        Heading heading => MarkdownText.Of(Inlines(heading.Text), page, $"mdHeading{Math.Clamp(heading.Level, 1, 6)}"),
        Paragraph paragraph => ParagraphOf(Inlines(paragraph.Text), page),
        Listing listing => List(listing, page, depth),
        Code code => MarkdownCode.Of(code),
        Quote quote => QuoteOf(quote, page, depth),
        Table table => TableOf(table, page),
        _ => new Border { Classes = { "mdRule" } },
    };

    /// <summary>A paragraph's words; where the page draws an image of it (a file inside the open folder), the image stands in
    /// a row of its own between the words before and after it, no wider than the page.</summary>
    private static Control ParagraphOf(IReadOnlyList<Inline> parts, MarkdownPage page)
    {
        if (page.ImageFile is not { } fileOf || !parts.Any(p => p is Image image && fileOf(image.Target) is not null))
        {
            return MarkdownText.Of(parts, page, "mdText");
        }
        StackPanel rows = new() { Classes = { "mdItem" } };
        List<Inline> words = [];
        WrapPanel? pictures = null;
        foreach (Inline part in parts)
        {
            if (part is Image image && fileOf(image.Target) is { } file)
            {
                Flush();
                if (pictures is null)
                {
                    pictures = new WrapPanel { Classes = { "mdImages" } };
                    rows.Children.Add(pictures);
                }
                pictures.Children.Add(page.Picture(file, image.Alt));
            }
            else if (pictures is null || part is not Words { Text: var space } || !string.IsNullOrWhiteSpace(space))
            {
                // The spaces between images in a row are the row's gaps.
                pictures = null;
                words.Add(part);
            }
        }
        Flush();
        return rows;

        void Flush()
        {
            if (words.Any(w => w is not Words { Text: var text } || !string.IsNullOrWhiteSpace(text)))
            {
                rows.Children.Add(MarkdownText.Of([.. words], page, "mdText"));
            }
            words.Clear();
        }
    }

    /// <summary>A list's items, each its bullet (or number, or a task's check) beside its blocks; a list inside one is
    /// bulleted as the next depth.</summary>
    private static StackPanel List(Listing listing, MarkdownPage page, int depth)
    {
        StackPanel list = new() { Classes = { "mdList" } };
        int number = listing.Start;
        for (int index = 0; index < listing.Items.Count; index++)
        {
            Control mark;
            if (listing.Checks?[index] is { } done)
            {
                // Drawn as the check it is, never one to click: the file's text is what says it is done.
                PathIcon tick = new() { Classes = { "mdTick" }, IsVisible = done };
                tick.Bind(PathIcon.DataProperty, tick.GetResourceObservable(IconKeys.Check));
                mark = new Border { Classes = { "box", "mdTask" }, Child = tick };
                mark.Classes.Set("done", done);
                Avalonia.Automation.AutomationProperties.SetName(mark, done ? "Done" : "Not done");
                number++;
            }
            else
            {
                mark = new TextBlock
                {
                    Classes = { "mdMark" },
                    Text = listing.Ordered ? $"{number++}." : depth % 2 == 0 ? "•" : "◦",
                    TextAlignment = listing.Ordered ? TextAlignment.Right : TextAlignment.Center,
                };
            }
            StackPanel content = new() { Classes = { "mdItem" }, [Grid.ColumnProperty] = 1 };
            Render(listing.Items[index], content, page, depth + 1);
            list.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { mark, content } });
        }
        return list;
    }

    private static Border QuoteOf(Quote quote, MarkdownPage page, int depth)
    {
        StackPanel content = new() { Classes = { "mdItem" } };
        Render(quote.Blocks, content, page, depth);
        return new Border { Classes = { "mdQuote" }, Child = content };
    }

    /// <summary>A table's cells in a grid, each column as wide as its widest cell up to the most a cell is drawn, its words
    /// wrapping past that, the table scrolled across where the page is narrower than it.</summary>
    private static Control TableOf(Table table, MarkdownPage page)
    {
        int columns = table.Aligns.Count;
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("Auto", columns))) };
        IReadOnlyList<IReadOnlyList<string>> rows = [table.Header, .. table.Rows];
        for (int row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int column = 0; column < columns; column++)
            {
                MarkdownText words = MarkdownText.Of(Inlines(column < rows[row].Count ? rows[row][column] : ""), page, row == 0 ? "mdCellHead" : "mdCellText");
                words.TextAlignment = table.Aligns[column] ?? TextAlignment.Left;
                // The rules between cells only: the table's frame draws its edges.
                Border cell = new()
                {
                    Classes = { "mdCell" },
                    BorderThickness = new Thickness(0, 0, column < columns - 1 ? 1 : 0, row < rows.Count - 1 ? 1 : 0),
                    Child = words,
                    [Grid.RowProperty] = row,
                    [Grid.ColumnProperty] = column,
                };
                cell.Classes.Set("head", row == 0);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            Classes = { "mdTableScroll" },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Classes = { "box", "mdTable" }, HorizontalAlignment = HorizontalAlignment.Left, Child = grid },
        };
    }

    /// <summary>Lines into blocks: each block begins where the last ended, a blank line or a line that starts another
    /// kind ending a paragraph. Each block's lines go into <paramref name="spans"/> when given.</summary>
    private static List<Block> Blocks(IReadOnlyList<string> lines, List<(int Start, int End)>? spans = null)
    {
        List<Block> blocks = [];
        int i = 0;
        while (i < lines.Count)
        {
            int start = i;
            if (BlockAt(lines, ref i) is { } block)
            {
                blocks.Add(block);
                spans?.Add((start, i));
            }
        }
        return blocks;
    }

    /// <summary>The block that begins at a line, or null for a line that begins none (a blank one, a comment's), with the
    /// line after it in <paramref name="i"/>.</summary>
    private static Block? BlockAt(IReadOnlyList<string> lines, ref int i)
    {
        string line = lines[i];
        if (string.IsNullOrWhiteSpace(line))
        {
            i++;
            return null;
        }
        if (line.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
        {
            i = AfterComment(lines, i);
            return null;
        }
        if (Fence().Match(line) is { Success: true } fence)
        {
            return Fenced(lines, ref i, fence.Groups[1].Length, fence.Groups[2].Value, fence.Groups[3].Value);
        }
        if (HeadingLine().Match(line) is { Success: true } heading)
        {
            i++;
            return new Heading(heading.Groups[1].Length, heading.Groups[2].Value.Trim());
        }
        if (RuleLine().IsMatch(line))
        {
            i++;
            return new Rule();
        }
        if (QuoteLine().IsMatch(line))
        {
            List<string> quoted = [];
            while (i < lines.Count && QuoteLine().Match(lines[i]) is { Success: true } inside)
            {
                quoted.Add(inside.Groups[1].Value);
                i++;
            }
            return new Quote(Blocks(quoted));
        }
        if (Marker().Match(line) is { Success: true })
        {
            return ListFrom(lines, ref i);
        }
        if (TableAt(lines, i) is { } aligns)
        {
            return TableFrom(lines, ref i, aligns);
        }
        List<string> said = [];
        while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]) && (said.Count == 0 || (!Interrupts(lines[i]) && TableAt(lines, i) is null)))
        {
            said.Add(lines[i].Trim());
            i++;
        }
        return new Paragraph(string.Join(' ', said));
    }

    /// <summary>The columns' alignments of a table whose header row is at a line (its next line the delimiter row, of as
    /// many cells, both with a pipe), or null when no table begins there.</summary>
    private static IReadOnlyList<TextAlignment?>? TableAt(IReadOnlyList<string> lines, int i)
    {
        if (i + 1 >= lines.Count || !lines[i].Contains('|', StringComparison.Ordinal) || !lines[i + 1].Contains('|', StringComparison.Ordinal)
            || !DelimiterRow().IsMatch(lines[i + 1]))
        {
            return null;
        }
        List<string> delimiters = Cells(lines[i + 1]);
        if (Cells(lines[i]).Count != delimiters.Count)
        {
            return null;
        }
        return [.. delimiters.Select(d => d.StartsWith(':') && d.EndsWith(':') ? TextAlignment.Center
            : d.EndsWith(':') ? TextAlignment.Right
            : d.StartsWith(':') ? TextAlignment.Left
            : (TextAlignment?)null)];
    }

    private static Table TableFrom(IReadOnlyList<string> lines, ref int i, IReadOnlyList<TextAlignment?> aligns)
    {
        List<string> header = Cells(lines[i]);
        i += 2;
        List<IReadOnlyList<string>> rows = [];
        while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]) && !Interrupts(lines[i]))
        {
            List<string> cells = Cells(lines[i]);
            // A row of more cells than the header drops the rest; one of fewer is filled out empty.
            rows.Add([.. cells.Take(aligns.Count), .. Enumerable.Repeat("", Math.Max(0, aligns.Count - cells.Count))]);
            i++;
        }
        return new Table(aligns, header, rows);
    }

    /// <summary>A table row's cells: split at each pipe neither escaped nor in a code span, the row's outer pipes taken off,
    /// each cell trimmed.</summary>
    private static List<string> Cells(string row)
    {
        string text = row.Trim();
        if (text.StartsWith('|'))
        {
            text = text[1..];
        }
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal))
        {
            text = text[..^1];
        }
        List<string> cells = [];
        StringBuilder cell = new();
        int code = 0;
        for (int at = 0; at < text.Length; at++)
        {
            char c = text[at];
            if (c == '\\' && at + 1 < text.Length)
            {
                cell.Append(c).Append(text[++at]);
                continue;
            }
            if (c == '`')
            {
                int run = Run(text, at, '`');
                code = code == 0 ? run : code == run ? 0 : code;
                cell.Append('`', run);
                at += run - 1;
                continue;
            }
            if (c == '|' && code == 0)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }
            cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    /// <summary>A heading's words as a person reads them, of which its anchor is made.</summary>
    internal static string Plain(IReadOnlyList<Inline> parts)
    {
        StringBuilder text = new();
        foreach (Inline part in parts)
        {
            text.Append(part switch
            {
                Words words => words.Text,
                CodeSpan code => code.Text,
                Strong strong => Plain(strong.Parts),
                Emphasis emphasis => Plain(emphasis.Parts),
                Link link => Plain(link.Parts),
                Image image => image.Alt,
                _ => "",
            });
        }
        return text.ToString();
    }

    /// <summary>A heading's anchor as GitHub makes it, which a <c>#heading</c> link names: its words in lower case, spaces
    /// as hyphens, and every mark but a hyphen or an underscore left out.</summary>
    internal static string Slug(string words)
    {
        StringBuilder slug = new(words.Length);
        foreach (char c in words.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }
        return slug.ToString();
    }

    /// <summary>Whether a line begins another block, which ends the paragraph before it.</summary>
    private static bool Interrupts(string line) =>
        HeadingLine().IsMatch(line) || Fence().IsMatch(line) || RuleLine().IsMatch(line) || QuoteLine().IsMatch(line)
        || Marker().IsMatch(line) || line.TrimStart().StartsWith("<!--", StringComparison.Ordinal);

    /// <summary>The line after an HTML comment that begins at a line, however many lines it runs over.</summary>
    private static int AfterComment(IReadOnlyList<string> lines, int i)
    {
        int start = lines[i].IndexOf("<!--", StringComparison.Ordinal) + 4;
        for (int at = i; at < lines.Count; at++)
        {
            if (lines[at].IndexOf("-->", at == i ? start : 0, StringComparison.Ordinal) >= 0)
            {
                return at + 1;
            }
        }
        return lines.Count;
    }

    private static Code Fenced(IReadOnlyList<string> lines, ref int i, int indent, string fence, string info)
    {
        List<string> code = [];
        i++;
        while (i < lines.Count)
        {
            string line = lines[i++];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.TrimEnd().All(c => c == fence[0]))
            {
                break;
            }
            int strip = Math.Min(indent, line.Length - trimmed.Length);
            code.Add(line[strip..]);
        }
        return new Code(string.Join('\n', code), info.Trim().Split(' ', 2)[0]);
    }

    /// <summary>A list from its first marker: each item's lines are those indented to its content, and the lines of its
    /// paragraph that run on unindented, read as blocks of their own; it ends at a line of neither, or another kind of marker.</summary>
    private static Listing ListFrom(IReadOnlyList<string> lines, ref int i)
    {
        Match first = Marker().Match(lines[i]);
        int indent = first.Groups[1].Length;
        bool ordered = first.Groups[3].Success;
        int start = ordered && int.TryParse(first.Groups[3].Value, out int number) ? number : 1;
        List<IReadOnlyList<Block>> items = [];
        List<bool?> checks = [];
        while (i < lines.Count)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                // A blank line between two items keeps the list going.
                int next = i;
                while (next < lines.Count && string.IsNullOrWhiteSpace(lines[next]))
                {
                    next++;
                }
                if (next < lines.Count && Marker().Match(lines[next]) is { Success: true } after && after.Groups[1].Length == indent
                    && after.Groups[3].Success == ordered)
                {
                    i = next;
                    continue;
                }
                break;
            }
            Match marker = Marker().Match(lines[i]);
            if (!marker.Success || marker.Groups[1].Length != indent || marker.Groups[3].Success != ordered)
            {
                break;
            }
            int content = indent + marker.Groups[2].Length + Math.Max(1, Math.Min(marker.Groups[4].Length, 4));
            string opening = marker.Groups[5].Value;
            Match task = TaskMark().Match(opening);
            checks.Add(task.Success ? task.Groups[1].Value != " " : null);
            List<string> item = [task.Success ? task.Groups[2].Value : opening];
            i++;
            while (i < lines.Count)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    int next = i;
                    while (next < lines.Count && string.IsNullOrWhiteSpace(lines[next]))
                    {
                        next++;
                    }
                    if (next < lines.Count && Indent(lines[next]) >= content)
                    {
                        item.AddRange(lines.Skip(i).Take(next - i).Select(_ => string.Empty));
                        i = next;
                        continue;
                    }
                    break;
                }
                if (Indent(line) >= content)
                {
                    item.Add(line[content..]);
                }
                else if (!Interrupts(line) && !string.IsNullOrWhiteSpace(item[^1]))
                {
                    // A paragraph's lazy line, which runs on unindented.
                    item.Add(line.Trim());
                }
                else
                {
                    break;
                }
                i++;
            }
            items.Add(Blocks(item));
        }
        return new Listing(ordered, start, items, checks.Any(c => c is not null) ? checks : null);
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    /// <summary>A line's tabs as the spaces they stand for, four a stop.</summary>
    private static string Untabbed(string line)
    {
        if (!line.Contains('\t', StringComparison.Ordinal))
        {
            return line;
        }
        StringBuilder spaced = new(line.Length + 8);
        foreach (char c in line)
        {
            if (c == '\t')
            {
                spaced.Append(' ', 4 - spaced.Length % 4);
            }
            else
            {
                spaced.Append(c);
            }
        }
        return spaced.ToString();
    }

    /// <summary>A block's words: code spans first, then links, strong and emphasis, an escaped mark as itself.</summary>
    internal static IReadOnlyList<Inline> Inlines(string text)
    {
        List<Inline> parts = [];
        StringBuilder words = new();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && Escapable(text[i + 1]))
            {
                words.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                int run = Run(text, i, '`');
                int close = CodeClose(text, i + run, run);
                if (close < 0)
                {
                    words.Append('`', run);
                    i += run;
                    continue;
                }
                Flush();
                string code = text[(i + run)..close];
                parts.Add(new CodeSpan(code.Length > 1 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0 ? code[1..^1] : code));
                i = close + run;
                continue;
            }
            if (text.AsSpan(i).StartsWith("<!--") && text.IndexOf("-->", i + 4, StringComparison.Ordinal) is var end and >= 0)
            {
                i = end + 3;
                continue;
            }
            if (c == '<' && Autolink().Match(text, i) is { Success: true } auto && auto.Index == i && Web(auto.Groups[1].Value) is { } address)
            {
                Flush();
                parts.Add(new Link([new Words(auto.Groups[1].Value)], address.OriginalString));
                i += auto.Length;
                continue;
            }
            if (c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '['))
            {
                bool image = c == '!';
                if (LinkAt(text, image ? i + 1 : i) is { } link)
                {
                    Flush();
                    parts.Add(image ? new Image(WebUtility.HtmlDecode(link.Label), link.Target) : new Link(Inlines(link.Label), link.Target));
                    i = link.End;
                    continue;
                }
            }
            if (c is '*' or '_')
            {
                int run = Run(text, i, c);
                int width = run >= 2 ? 2 : 1;
                if (Opens(text, i, width, c) && DelimiterClose(text, i + width, c, width) is var close and >= 0)
                {
                    Flush();
                    IReadOnlyList<Inline> inner = Inlines(text[(i + width)..close]);
                    parts.Add(width == 2 ? new Strong(inner) : new Emphasis(inner));
                    i = close + width;
                    continue;
                }
                words.Append(c, run);
                i += run;
                continue;
            }
            words.Append(c);
            i++;
        }
        Flush();
        return parts;

        void Flush()
        {
            if (words.Length > 0)
            {
                parts.Add(new Words(WebUtility.HtmlDecode(words.ToString())));
                words.Clear();
            }
        }
    }

    /// <summary>Whether a backslash before a character makes it itself: ASCII's punctuation and symbols.</summary>
    private static bool Escapable(char c) => c < 128 && (char.IsPunctuation(c) || char.IsSymbol(c));

    /// <summary>A web address a link may open: http or https alone.</summary>
    internal static Uri? Web(string target) =>
        Uri.TryCreate(target.Trim().Trim('<', '>'), UriKind.Absolute, out Uri? address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp)
            ? address
            : null;

    private static int Run(string text, int at, char c)
    {
        int end = at;
        while (end < text.Length && text[end] == c)
        {
            end++;
        }
        return end - at;
    }

    /// <summary>Where a code span's closing run of the same length begins, or -1.</summary>
    private static int CodeClose(string text, int from, int run)
    {
        for (int at = text.IndexOf('`', from); at >= 0; at = text.IndexOf('`', at))
        {
            int length = Run(text, at, '`');
            if (length == run)
            {
                return at;
            }
            at += length;
        }
        return -1;
    }

    /// <summary>Whether a run of * or _ can open emphasis there: words follow it, and an underscore does not stand inside a word.</summary>
    private static bool Opens(string text, int at, int width, char c) =>
        at + width < text.Length && !char.IsWhiteSpace(text[at + width])
        && (c == '*' || at == 0 || !char.IsLetterOrDigit(text[at - 1]));

    /// <summary>Where emphasis opened at <paramref name="from"/> closes: the same mark after words, a code span passed over; -1 for none.</summary>
    private static int DelimiterClose(string text, int from, char c, int width)
    {
        for (int at = from + 1; at < text.Length; at++)
        {
            if (text[at] == '`')
            {
                int run = Run(text, at, '`');
                int close = CodeClose(text, at + run, run);
                at = close < 0 ? at + run - 1 : close + run - 1;
                continue;
            }
            if (text[at] != c || char.IsWhiteSpace(text[at - 1]))
            {
                continue;
            }
            int length = Run(text, at, c);
            if (length < width || (width == 1 && length != 1))
            {
                at += length - 1;
                continue;
            }
            int after = at + width;
            if (c == '_' && after < text.Length && char.IsLetterOrDigit(text[after]))
            {
                continue;
            }
            return at;
        }
        return -1;
    }

    /// <summary>A link's label, its target and where it ends, from its opening bracket, or null when it is no link.</summary>
    private static (string Label, string Target, int End)? LinkAt(string text, int open)
    {
        int depth = 0;
        int close = -1;
        for (int at = open; at < text.Length; at++)
        {
            if (text[at] == '\\')
            {
                at++;
            }
            else if (text[at] == '[')
            {
                depth++;
            }
            else if (text[at] == ']' && --depth == 0)
            {
                close = at;
                break;
            }
        }
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            return null;
        }
        int parens = 0;
        for (int at = close + 1; at < text.Length; at++)
        {
            if (text[at] == '(')
            {
                parens++;
            }
            else if (text[at] == ')' && --parens == 0)
            {
                string inside = text[(close + 2)..at].Trim();
                // A title after the address ("…" or '…') is no part of it.
                string target = TitledTarget().Match(inside) is { Success: true } titled ? titled.Groups[1].Value : inside;
                return (text[(open + 1)..close], target, at + 1);
            }
        }
        return null;
    }

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ ]+(.*?))?(?:[ ]+#+)?[ ]*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^ {0,3}(?:(?:-[ ]*){3,}|(?:\*[ ]*){3,}|(?:_[ ]*){3,})$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^( {0,3})(`{3,}|~{3,})(.*)$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\[([ xX])\](?:[ ]+(.*))?$")]
    private static partial Regex TaskMark();

    [GeneratedRegex(@"^ {0,3}\|?[ ]*:?-+:?[ ]*(?:\|[ ]*:?-+:?[ ]*)*\|?[ ]*$")]
    private static partial Regex DelimiterRow();

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex EditorLines();

    [GeneratedRegex(@"^ {0,3}> ?(.*)$")]
    private static partial Regex QuoteLine();

    [GeneratedRegex(@"^( *)([-*+]|(\d{1,9})[.)])( +|$)(.*)$")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\G<(https?://[^\s<>]+)>")]
    private static partial Regex Autolink();

    [GeneratedRegex(@"^(\S+)\s+(?:""[^""]*""|'[^']*')$")]
    private static partial Regex TitledTarget();
}

/// <summary>A block of Markdown's words, selectable to copy: its strong, emphasis and code drawn as such, and its links
/// in the link colour, the hand over them and a click opening one (when it selected nothing), through the window.</summary>
public sealed class MarkdownText : SelectableTextBlock
{
    /// <summary>The pointer over a link, made once for every block: a move over one makes none.</summary>
    private static Cursor? hand;

    private readonly List<(int Start, int End, Uri Target)> links = [];
    private readonly StringBuilder shown = new();
    private readonly MarkdownPage page;

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    private MarkdownText(MarkdownPage page)
    {
        this.page = page;
        TextWrapping = TextWrapping.Wrap;
        Inlines ??= [];
    }

    /// <summary>The words as drawn.</summary>
    public string Shown => shown.ToString();

    /// <summary>Each link's words and address.</summary>
    public IEnumerable<(string Words, Uri Target)> Links => links.Select(l => (Shown[l.Start..l.End], l.Target));

    internal static MarkdownText Of(IReadOnlyList<Markdown.Inline> parts, MarkdownPage page, string kind)
    {
        MarkdownText block = new(page) { Classes = { kind } };
        block.Add(parts, bold: false, italic: false, link: null);
        return block;
    }

    private void Add(IReadOnlyList<Markdown.Inline> parts, bool bold, bool italic, Uri? link)
    {
        foreach (Markdown.Inline part in parts)
        {
            switch (part)
            {
                case Markdown.Words words:
                    Put(words.Text, bold, italic, link, code: false);
                    break;
                case Markdown.CodeSpan code:
                    Put(code.Text, bold, italic, link, code: true);
                    break;
                case Markdown.Strong strong:
                    Add(strong.Parts, bold: true, italic, link);
                    break;
                case Markdown.Emphasis emphasis:
                    Add(emphasis.Parts, bold, italic: true, link);
                    break;
                case Markdown.Link linked when page.LinkOf(linked.Target) is { } target:
                    int start = shown.Length;
                    Add(linked.Parts, bold, italic, target);
                    links.Add((start, shown.Length, target));
                    break;
                case Markdown.Link unlinked:
                    // A link the page opens nothing of (the notes' to a file) is its words.
                    Add(unlinked.Parts, bold, italic, link);
                    break;
                case Markdown.Image image when page.ImageFile is not null:
                    // An image not drawn (a web one, one outside the folder) is the words it was given to stand for it.
                    Put(image.Alt, bold, italic: true, link, code: false, muted: link is null);
                    break;
            }
        }
    }

    private void Put(string text, bool bold, bool italic, Uri? link, bool code, bool muted = false)
    {
        // Set only where it differs, so the block's own weight (a heading's) holds for the rest.
        Avalonia.Controls.Documents.Run run = new(text);
        if (bold)
        {
            run.FontWeight = FontWeight.Bold;
        }
        if (italic)
        {
            run.FontStyle = FontStyle.Italic;
        }
        if (code)
        {
            run.FontFamily = CodeBlock.Font;
            run.Bind(TextElement.BackgroundProperty, this.GetResourceObservable(TokenKeys.Hover));
        }
        if (link is not null)
        {
            run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable(TokenKeys.Link));
        }
        else if (muted)
        {
            run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable(TokenKeys.TextMuted));
        }
        Inlines!.Add(run);
        shown.Append(text);
    }

    /// <summary>The link drawn at a point of the block, or null: the one whose words are drawn there, on any of its lines.</summary>
    public Uri? LinkAt(Point point)
    {
        // Not the hit's IsInside, which is false below a block's first line.
        int at = TextLayout.HitTestPoint(point).TextPosition;
        (int start, int end, Uri? target) = links.FirstOrDefault(l => at >= l.Start && at < l.End);
        return target is not null && TextLayout.HitTestTextRange(start, end - start).Any(drawn => drawn.Contains(point)) ? target : null;
    }

    /// <summary>Where a link's first word is drawn in the block, for a click on it.</summary>
    public Point PointOf(Uri target)
    {
        (int start, int end, _) = links.First(l => l.Target == target);
        Rect first = TextLayout.HitTestTextRange(start, end - start).First();
        return first.Center;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (LinkAt(e.GetPosition(this)) is null)
        {
            ClearValue(CursorProperty);
        }
        else
        {
            Cursor = hand ??= new Cursor(StandardCursorType.Hand);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left && SelectionStart == SelectionEnd && LinkAt(e.GetPosition(this)) is { } target)
        {
            e.Handled = true;
            page.Follow(target);
        }
    }
}
