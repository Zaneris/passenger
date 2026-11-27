using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.Editors;

/// <summary>Markdown's blocks drawn as Passenger's own controls, styled in Shell.axaml, fenced code coloured as the editor
/// colours its language, an HTML <c>details</c> folded as a form's rows are (<see cref="FormFold"/>).</summary>
internal static partial class Markdown
{
    /// <summary>Each heading level's class, the first level's first.</summary>
    private static readonly string[] Headings =
        [StyleClasses.MdHeading1, StyleClasses.MdHeading2, StyleClasses.MdHeading3, StyleClasses.MdHeading4, StyleClasses.MdHeading5, StyleClasses.MdHeading6];

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
        Heading heading => MarkdownText.Of(Inlines(heading.Text), page, Headings[Math.Clamp(heading.Level, 1, 6) - 1]),
        Paragraph paragraph => ParagraphOf(Inlines(paragraph.Text), page),
        Phrase phrase => Aligned(ParagraphOf(phrase.Parts, page, phrase.Level is >= 1 and <= 6 ? Headings[phrase.Level - 1] : StyleClasses.MdText), phrase.Aligned),
        Division division => DivisionOf(division, page, depth),
        Details details => DetailsOf(details, page, depth),
        Listing listing => List(listing, page, depth),
        Code code => ColouredCode.Of(code.Text, ColouredCode.LanguageOf(code.Info)),
        Quote quote => QuoteOf(quote, page, depth),
        Table table => TableOf(table, page),
        RichTable table => TableOf([null, null], table.Rows, page, table.HasHeader),
        _ => new Border { Classes = { StyleClasses.MdRule } },
    };

    /// <summary>A paragraph's words; where the page draws an image of it (a file inside the open folder), the image stands in
    /// a row of its own between the words before and after it, no wider than the page.</summary>
    private static Control ParagraphOf(IReadOnlyList<Inline> parts, MarkdownPage page, [IdentifierOf(typeof(StyleClasses))] string kind = StyleClasses.MdText)
    {
        (string Light, string Dark)?[] files = [.. parts.Select(p => p is Image image ? page.FilesOf(image) : null)];
        if (files.All(f => f is null))
        {
            return MarkdownText.Of(parts, page, kind);
        }
        StackPanel rows = new() { Classes = { StyleClasses.MdItem } };
        List<Inline> words = [];
        WrapPanel? pictures = null;
        for (int at = 0; at < parts.Count; at++)
        {
            Inline part = parts[at];
            if (part is Image image && files[at] is { } drawn)
            {
                Flush();
                if (pictures is null)
                {
                    pictures = new WrapPanel();
                    rows.Children.Add(pictures);
                }
                pictures.Children.Add(page.Picture(image, drawn));
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
                rows.Children.Add(MarkdownText.Of([.. words], page, kind));
            }
            words.Clear();
        }
    }

    /// <summary>Blocks read from HTML that stand together, their words standing as its <c>align</c> says.</summary>
    private static Control DivisionOf(Division division, MarkdownPage page, int depth)
    {
        StackPanel content = new() { Classes = { StyleClasses.MdItem } };
        Render(division.Blocks, content, page, depth);
        return Aligned(content, division.Aligned);
    }

    /// <summary>A <c>details</c>: its summary's words a fold's opener, its blocks the rows it opens.</summary>
    private static Control DetailsOf(Details details, MarkdownPage page, int depth)
    {
        FormFold fold = new() { Title = Plain(details.Summary).Trim() };
        StackPanel content = new() { Classes = { StyleClasses.MdItem } };
        Render(details.Blocks, content, page, depth);
        fold.Rows.Children.Add(content);
        fold.IsOpen = details.Open;
        return new StackPanel { Classes = { StyleClasses.MdItem }, Children = { fold.Opener, fold.Rows } };
    }

    /// <summary>A block's words, and its rows of pictures, standing across it as an HTML <c>align</c> says, but words
    /// that say their own (a table's cells, an <c>align</c> inside); the block as it was for none.</summary>
    private static Control Aligned(Control block, Align? align)
    {
        if (align is not { } aligned)
        {
            return block;
        }
        (TextAlignment words, HorizontalAlignment pictures) = aligned switch
        {
            Align.Center => (TextAlignment.Center, HorizontalAlignment.Center),
            Align.Right => (TextAlignment.Right, HorizontalAlignment.Right),
            _ => (TextAlignment.Left, HorizontalAlignment.Left),
        };
        foreach (ILogical inside in (ILogical[])[block, .. block.GetLogicalDescendants()])
        {
            if (inside is MarkdownText text && !text.IsSet(TextBlock.TextAlignmentProperty))
            {
                text.TextAlignment = words;
            }
            else if (inside is WrapPanel row && !row.IsSet(Layoutable.HorizontalAlignmentProperty))
            {
                row.HorizontalAlignment = pictures;
            }
        }
        return block;
    }

    /// <summary>A list's items, each its bullet (or number, or a task's check) beside its blocks; a list inside one is
    /// bulleted as the next depth.</summary>
    private static StackPanel List(Listing listing, MarkdownPage page, int depth)
    {
        StackPanel list = new() { Classes = { StyleClasses.MdList } };
        int number = listing.Start;
        for (int index = 0; index < listing.Items.Count; index++)
        {
            Control mark;
            if (listing.Checks?[index] is { } done)
            {
                // Drawn as the check it is, never one to click: the file's text is what says it is done.
                PathIcon tick = new() { Classes = { StyleClasses.MdTick }, IsVisible = done };
                tick.Bind(PathIcon.DataProperty, tick.GetResourceObservable(IconKeys.Check));
                mark = new Border { Classes = { StyleClasses.Box, StyleClasses.MdTask }, Child = tick };
                mark.Classes.Set(StyleClasses.Done, done);
                Avalonia.Automation.AutomationProperties.SetName(mark, done ? "Done" : "Not done");
                number++;
            }
            else
            {
                mark = new TextBlock
                {
                    Classes = { StyleClasses.MdMark },
                    Text = listing.Ordered ? $"{number++}." : depth % 2 == 0 ? "•" : "◦",
                    TextAlignment = listing.Ordered ? TextAlignment.Right : TextAlignment.Center,
                };
            }
            StackPanel content = new() { Classes = { StyleClasses.MdItem }, [Grid.ColumnProperty] = 1 };
            Render(listing.Items[index], content, page, depth + 1);
            list.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { mark, content } });
        }
        return list;
    }

    private static Border QuoteOf(Quote quote, MarkdownPage page, int depth)
    {
        StackPanel content = new() { Classes = { StyleClasses.MdItem } };
        Render(quote.Blocks, content, page, depth);
        return new Border { Classes = { StyleClasses.MdQuote }, Child = content };
    }

    /// <summary>A table's cells in a grid, each column as wide as its widest cell up to the most a cell is drawn, its words
    /// wrapping past that, the table scrolled across where the page is narrower than it.</summary>
    private static Control TableOf(Table table, MarkdownPage page) => TableOf(table.Aligns,
        [.. new[] { table.Header }.Concat(table.Rows).Select(row => (IReadOnlyList<IReadOnlyList<Inline>>)[.. row.Select(cell => Inlines(cell))])],
        page, hasHeader: true);

    private static Control TableOf(IReadOnlyList<Align?> aligns, IReadOnlyList<IReadOnlyList<IReadOnlyList<Inline>>> rows, MarkdownPage page, bool hasHeader)
    {
        int columns = aligns.Count;
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("Auto", columns))) };
        for (int row = 0; row < rows.Count; row++)
        {
            bool header = hasHeader && row == 0;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int column = 0; column < columns; column++)
            {
                MarkdownText words = MarkdownText.Of(column < rows[row].Count ? rows[row][column] : [], page, header ? StyleClasses.MdCellHead : StyleClasses.MdCellText);
                words.TextAlignment = aligns[column] switch
                {
                    Align.Center => TextAlignment.Center,
                    Align.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };
                // The rules between cells only: the table's frame draws its edges.
                bool beforeAnother = column < columns - 1, aboveAnother = row < rows.Count - 1;
                Border cell = new()
                {
                    Classes = { StyleClasses.MdCell },
                    BorderThickness = new Thickness(0, 0, beforeAnother ? Hairlines.Width : 0, aboveAnother ? Hairlines.Width : 0),
                    Child = words,
                    [Grid.RowProperty] = row,
                    [Grid.ColumnProperty] = column,
                };
                cell.Classes.Set(StyleClasses.Head, header);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Classes = { StyleClasses.Box, StyleClasses.MdTable }, HorizontalAlignment = HorizontalAlignment.Left, Child = grid },
        };
    }
}

/// <summary>A block of Markdown's words, selectable to copy: its strong, emphasis and code drawn as such, and its links
/// in the link colour, the hand over them and a click opening one (when it selected nothing), through the window.</summary>
public sealed class MarkdownText : SelectableWords
{
    private readonly List<WordsLink> links = [];
    private readonly StringBuilder shown = new();
    private readonly MarkdownPage page;

    private MarkdownText(MarkdownPage page)
    {
        this.page = page;
        TextWrapping = TextWrapping.Wrap;
        Inlines ??= [];
        Follow = link => page.Follow((Uri)link.Target);
    }

    /// <summary>The words as drawn.</summary>
    public string Shown => shown.ToString();

    /// <summary>Each link's words and address.</summary>
    public IEnumerable<(string Words, Uri Target)> Links => WordLinks.Select(l => (Shown[l.Start..l.End], (Uri)l.Target));

    internal static MarkdownText Of(IReadOnlyList<Markdown.Inline> parts, MarkdownPage page, [IdentifierOf(typeof(StyleClasses))] string kind)
    {
        MarkdownText block = new(page) { Classes = { kind } };
        block.Add(parts, bold: false, italic: false, link: null);
        block.WordLinks = block.links;
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
                case Markdown.Underline underlined:
                    int first = Inlines!.Count;
                    Add(underlined.Parts, bold, italic, link);
                    foreach (Avalonia.Controls.Documents.Run run in Inlines.Skip(first).OfType<Avalonia.Controls.Documents.Run>())
                    {
                        run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                    }
                    break;
                case Markdown.Link linked when page.LinkOf(linked.Target) is { } target:
                    int start = shown.Length;
                    Add(linked.Parts, bold, italic, target);
                    links.Add(new WordsLink(start, shown.Length, target));
                    break;
                case Markdown.Link unlinked:
                    // A link the page opens nothing of (the notes' to a file) is its words.
                    Add(unlinked.Parts, bold, italic, link);
                    break;
                case Markdown.Image image when page.ImageFile is not null:
                    // An image not drawn (a web one, one outside the folder) is the words it was given to stand for it.
                    Put(image.Alt, bold, italic: true, link, code: false, muted: link is null);
                    break;
                case Markdown.Break:
                    Put("\n", bold, italic, link, code: false);
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
            run.InCodeFont();
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

    /// <summary>Where a link's first word is drawn in the block, for a click on it.</summary>
    public Point PointOf(Uri target) => PointOf(WordLinks.First(l => Equals(l.Target, target)));

}
