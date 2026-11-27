using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Documentation shares the fold map's visible rows. Editing reveals the original text in the same editor.</summary>
public sealed partial class CodeEditor
{
    private AnchoredRange? documentationEdit;
    private readonly DocumentationLayer documentationLayer = new() { ClipToBounds = true };
    private readonly Dictionary<int, DocumentationCard> documentationCards = [];
    private readonly Dictionary<(string Xml, int Indent), int> documentationHeights = [];
    private (double Width, double Gutter, Metrics Metrics, int TabWidth) documentationLayout;

    /// <summary>Makes the host's rich view of a written documentation block.</summary>
    public Func<string, Control>? DocumentationView { get; init; }

    private int DocumentationEditLine(TextSnapshot text) => documentationEdit is { } edit && edit.End > edit.Start
        ? text.GetLineFromOffset(edit.Start) : -1;

    private void ForgetDocumentationEdit()
    {
        documentationEdit?.Remove();
        documentationEdit = null;
    }

    private void EditDocumentation(TextSnapshot text, int line, FoldRegion region)
    {
        ForgetDocumentationEdit();
        documentationEdit = document.RangeAt(TextRange.FromBounds(text.GetLineStart(line), region.End), AnchorGravity.After, AnchorGravity.After);
    }

    /// <summary>Find and navigation may target an offset in the source, as may a typing assist that generated a comment.</summary>
    private bool RevealDocumentationAt(int offset)
    {
        FoldLines map = Folding;
        TextSnapshot text = map.Text;
        int line = map.Shown(text.GetLineFromOffset(Math.Clamp(offset, 0, text.Length)));
        if (!map.Documentation.TryGetValue(line, out int index)
            || offset <= text.GetLineStart(line) || offset > FoldRegions[index].End)
        {
            return false;
        }
        EditDocumentation(text, line, FoldRegions[index]);
        return true;
    }

    private void DocumentationRowsChanged()
    {
        UpdateScrollBars();
        ScrollWithinRows();
        Redraw();
        FollowInputMethod();
    }

    private bool ClickDocumentation(Point at)
    {
        if (at.X < GutterWidth)
        {
            return false;
        }
        (int line, int row) = Rows.LineOfRow(RowAtY(at.Y));
        if (row < 0)
        {
            return false;
        }
        TextSnapshot text = document.Snapshot;
        if (Folding.Documentation.ContainsKey(line))
        {
            return true;
        }
        if (line != DocumentationEditLine(text) || documentationEdit is not { } edit
            || !DocumentationButton(Bounds.Width, TopOf(line) - scroll.Y).Contains(at))
        {
            return false;
        }
        document.BreakUndoGroup();
        int last = text.GetLineFromOffset(edit.End);
        int next = last + 1 < text.LineCount ? text.GetLineStart(last + 1) : text.GetLineStart(line);
        SetSelections(SelectionSet.Single(next), reveal: false);
        ForgetDocumentationEdit();
        DocumentationRowsChanged();
        return true;
    }

    private void OpenDocumentationSource(int line)
    {
        if (!Folding.Documentation.TryGetValue(line, out int index))
        {
            return;
        }
        TextSnapshot text = document.Snapshot;
        Focus();
        document.BreakUndoGroup();
        EditDocumentation(text, line, FoldRegions[index]);
        int source = DocumentationStart(text, line);
        SetSelections(SelectionSet.Single(Math.Min(source + 4, text.GetLine(line).End)), reveal: false);
        DocumentationRowsChanged();
    }

    private static int DocumentationStart(TextSnapshot text, int line)
    {
        LineSpan span = text.GetLine(line);
        int offset = span.Start;
        while (offset < span.End && text[offset] is ' ' or '\t')
        {
            offset++;
        }
        return offset;
    }

    private Rect DocumentationButton(double width, double y)
    {
        double right = width - vertical.DesiredSize.Width - metrics.CharWidth;
        double left = Math.Max(GutterWidth, right - (7 * metrics.CharWidth));
        return new Rect(left, y + 1, Math.Max(0, right - left), metrics.LineHeight - 2);
    }

    private void DrawDocumentationButton(DrawingContext context, Rect box, string text)
    {
        if (box.Width <= 0)
        {
            return;
        }
        if (!labels.TryGet((text, -11), out TextLine? label))
        {
            label = labels.Add((text, -11), Format(text, [], RunWith(palette.Annotation)));
        }
        context.DrawRectangle(palette.InlayHintBack, palette.PenOf(palette.Annotation, 1), box.Deflate(0.5), 3, 3);
        using (context.PushClip(box))
        {
            label.Draw(context, new Point(box.Center.X - label.WidthIncludingTrailingWhitespace / 2, box.Center.Y - label.Height / 2));
        }
    }

    private void ResetDocumentationCards()
    {
        documentationCards.Clear();
        documentationLayer.Children.Clear();
        documentationHeights.Clear();
        rowMap = null;
    }

    private int DocumentationIndent(TextSnapshot text, int line)
    {
        int column = 0;
        int end = DocumentationStart(text, line);
        for (int at = text.GetLineStart(line); at < end; at++)
        {
            column += text[at] == '\t' ? Indent.TabWidth - column % Indent.TabWidth : 1;
        }
        return column;
    }

    private int DocumentationHeight(TextSnapshot text, int line, string xml) =>
        documentationHeights.GetValueOrDefault((xml, DocumentationIndent(text, line)), 1);

    /// <summary>Measures cards as they enter the view. Only the measured row counts are retained when a card scrolls away.</summary>
    private void PrepareDocumentation(TextSnapshot text, Size size)
    {
        if (DocumentationView is not { } make)
        {
            return;
        }
        double width = Math.Max(1, size.Width - vertical.DesiredSize.Width - metrics.CharWidth);
        (double, double, Metrics, int) layout = (width, GutterWidth, metrics, Indent.TabWidth);
        if (documentationLayout != layout)
        {
            documentationLayout = layout;
            documentationHeights.Clear();
            rowMap = null;
        }
        RowMap before = Rows;
        int firstRow = Math.Max(0, RowAtY(0));
        int firstLine = before.LineOfRow(firstRow).Line;
        int lastLine = before.LineOfRow(Math.Max(firstRow, RowAtY(size.Height))).Line;
        int[] lines = before.Folds.DocumentationLines;
        int from = Sorted.FirstAtOrAfter(lines, static line => line, firstLine);
        int to = Sorted.FirstAfter(lines, static line => line, lastLine);
        foreach (int line in documentationCards.Keys.Where(line => line < firstLine || line > lastLine
            || !before.Folds.Documentation.ContainsKey(line)).ToArray())
        {
            Drop(line);
        }
        bool measured = false;
        for (int i = from; i < to; i++)
        {
            int line = lines[i];
            string xml = FoldRegions[before.Folds.Documentation[line]].Documentation!;
            if (!documentationCards.TryGetValue(line, out DocumentationCard? card) || card.Xml != xml)
            {
                Drop(line);
                card = MakeDocumentationCard(line, xml, make(xml));
                documentationCards.Add(line, card);
                documentationLayer.Children.Add(card.Box);
            }
            card.Action.Content = IsReadOnly ? "Source" : "Edit";
            card.Box.Background = palette.InlayHintBack;
            card.Box.BorderBrush = palette.Annotation;
            int indent = DocumentationIndent(text, line);
            double left = Math.Clamp(TextLeft + indent * metrics.CharWidth, GutterWidth, Math.Max(GutterWidth, width - 100));
            card.Left = left;
            card.Width = Math.Max(1, width - left);
            card.Box.Measure(new Size(card.Width, double.PositiveInfinity));
            int rows = Math.Max(1, (int)Math.Ceiling((card.Box.DesiredSize.Height + 4) / metrics.LineHeight));
            if (documentationHeights.GetValueOrDefault((xml, indent), 1) != rows)
            {
                documentationHeights[(xml, indent)] = rows;
                measured = true;
            }
        }
        if (measured)
        {
            rowMap = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateScrollBars);
        }
        RowMap after = Rows;
        foreach ((int line, DocumentationCard card) in documentationCards)
        {
            double y = (after.RowOf(line) * metrics.LineHeight) - scroll.Y;
            card.Box.Arrange(new Rect(card.Left, y + 2, card.Width, card.Box.DesiredSize.Height));
        }
        if (measured && after.LineOfRow(Math.Max(firstRow, RowAtY(size.Height))).Line > lastLine)
        {
            RedrawChanged();
        }

        void Drop(int line)
        {
            if (documentationCards.Remove(line, out DocumentationCard? card))
            {
                documentationLayer.Children.Remove(card.Box);
            }
        }
    }

    private DocumentationCard MakeDocumentationCard(int line, string xml, Control content)
    {
        Button edit = new() { VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(9, 2), [Grid.ColumnProperty] = 1 };
        edit.Click += (_, _) => OpenDocumentationSource(line);
        content.Margin = new Thickness(0, 0, 10, 0);
        Grid parts = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { content, edit } };
        Border box = new()
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(9, 5),
            Child = parts,
        };
        return new DocumentationCard(xml, box, edit);
    }

    private sealed class DocumentationLayer : Panel
    {
        protected override Size MeasureOverride(Size availableSize) => default;
        protected override Size ArrangeOverride(Size finalSize) => finalSize;
    }

    private sealed record DocumentationCard(string Xml, Border Box, Button Action)
    {
        public double Left { get; set; }
        public double Width { get; set; }
    }
}
