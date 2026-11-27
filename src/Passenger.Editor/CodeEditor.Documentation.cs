using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
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

    private bool OverRenderedDocumentation(Point point) =>
        point.X >= GutterWidth && Folding.Documentation.ContainsKey(LineAtY(point.Y));

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
        if (!map.Documentation.TryGetValue(line, out FoldRegion region)
            || offset <= text.GetLineStart(line) || offset > region.End)
        {
            return false;
        }
        EditDocumentation(text, line, region);
        return true;
    }

    private void DocumentationRowsChanged()
    {
        UpdateScrollBars();
        ScrollWithinRows();
        Redraw();
        FollowInputMethod();
    }

    private void FinishDocumentation()
    {
        if (documentationEdit is not { } edit)
        {
            return;
        }
        TextSnapshot text = document.Snapshot;
        Focus();
        document.BreakUndoGroup();
        int nextLine = text.GetLineFromOffset(edit.End) + 1;
        int next = text.GetLineStart(nextLine < text.LineCount ? nextLine : text.GetLineFromOffset(edit.Start));
        SetSelections(SelectionSet.Single(next), reveal: false);
        ForgetDocumentationEdit();
        DocumentationRowsChanged();
    }

    private void OpenDocumentationSource(int line)
    {
        if (!Folding.Documentation.TryGetValue(line, out FoldRegion region))
        {
            return;
        }
        TextSnapshot text = document.Snapshot;
        Focus();
        document.BreakUndoGroup();
        EditDocumentation(text, line, region);
        LineSpan header = text.GetLine(line);
        int source = TextNavigation.IndentEnd(text, header);
        SetSelections(SelectionSet.Single(Math.Min(source + 4, header.End)), reveal: false);
        DocumentationRowsChanged();
    }

    private void ResetDocumentationCards()
    {
        ForgetDocumentationEdit();
        documentationCards.Clear();
        documentationLayer.Children.Clear();
        documentationHeights.Clear();
        rowMap = null;
    }

    private int DocumentationIndent(TextSnapshot text, int line)
    {
        LineSpan header = text.GetLine(line);
        int length = TextNavigation.IndentEnd(text, header) - header.Start;
        Span<char> whitespace = length <= 128 ? stackalloc char[length] : new char[length];
        text.CopyTo(header.Start, whitespace);
        return Indent.Columns(whitespace);
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
        double right = Math.Max(1, size.Width - vertical.DesiredSize.Width - metrics.CharWidth);
        (double, double, Metrics, int) layout = (right, GutterWidth, metrics, Indent.TabWidth);
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
        FoldLines folds = before.Folds;
        int editing = folds.EditingDocumentation;
        int[] lines = folds.DocumentationLines;
        int from = Sorted.FirstAtOrAfter(lines, static line => line, firstLine);
        int to = Sorted.FirstAfter(lines, static line => line, lastLine);
        foreach (int line in documentationCards.Keys.Where(line => line < firstLine || line > lastLine
            || folds.IsHidden(line) || (line != editing && !folds.Documentation.ContainsKey(line))).ToArray())
        {
            documentationLayer.Children.Remove(documentationCards[line].View);
            documentationCards.Remove(line);
        }
        bool measured = false;
        for (int i = from; i < to; i++)
        {
            int line = lines[i];
            string xml = folds.Documentation[line].Documentation!;
            Control view = DocumentationCardAt(line, xml, make);
            int indent = DocumentationIndent(text, line);
            double left = Math.Clamp(TextLeft + indent * metrics.CharWidth, GutterWidth, Math.Max(GutterWidth, right - 100));
            view.Width = Math.Max(1, right - left);
            view.Measure(new Size(view.Width, double.PositiveInfinity));
            int rows = Math.Max(1, (int)Math.Ceiling((view.DesiredSize.Height + 4) / metrics.LineHeight));
            if (documentationHeights.GetValueOrDefault((xml, indent), 1) != rows)
            {
                documentationHeights[(xml, indent)] = rows;
                measured = true;
            }
        }
        if (editing >= firstLine && editing <= lastLine && !folds.IsHidden(editing))
        {
            Control done = DocumentationCardAt(editing, xml: null, make);
            done.Height = metrics.LineHeight - 2;
            done.Measure(Size.Infinity);
        }
        if (measured)
        {
            rowMap = null;
            Dispatcher.UIThread.Post(UpdateScrollBars);
        }
        RowMap after = Rows;
        foreach ((int line, DocumentationCard card) in documentationCards)
        {
            Control view = card.View;
            double y = (after.RowOf(line) * metrics.LineHeight) - scroll.Y;
            double width = card.Xml is null ? view.DesiredSize.Width : view.Width;
            view.Arrange(new Rect(right - width, y + (card.Xml is null ? 1 : 2), width, view.DesiredSize.Height));
        }
        if (measured && after.LineOfRow(Math.Max(firstRow, RowAtY(size.Height))).Line > lastLine)
        {
            RedrawChanged();
        }
    }

    private Control DocumentationCardAt(int line, string? xml, Func<string, Control> make)
    {
        if (!documentationCards.TryGetValue(line, out DocumentationCard? card) || card.Xml != xml || card.ReadOnly != IsReadOnly)
        {
            if (card is not null)
            {
                documentationLayer.Children.Remove(card.View);
            }
            Control view = xml is null ? DocumentationButton("Done", FinishDocumentation) : MakeDocumentationCard(line, make(xml));
            documentationCards[line] = card = new DocumentationCard(xml, view, IsReadOnly);
            documentationLayer.Children.Add(view);
        }
        if (card.View is Border box)
        {
            box.Background = palette.InlayHintBack;
            box.BorderBrush = palette.Annotation;
        }
        return card.View;
    }

    private Border MakeDocumentationCard(int line, Control content)
    {
        Button edit = DocumentationButton(IsReadOnly ? "Source" : "Edit", () => OpenDocumentationSource(line));
        Grid.SetColumn(edit, 1);
        content.Margin = new Thickness(0, 0, 10, 0);
        Grid parts = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { content, edit } };
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(9, 5),
            Child = parts,
        };
    }

    private static Button DocumentationButton(string label, Action click)
    {
        Button button = new()
        {
            Content = label, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(9, 2), MinHeight = 0,
        };
        button.Click += (_, _) => click();
        return button;
    }

    private sealed class DocumentationLayer : Panel
    {
        protected override Size MeasureOverride(Size availableSize) => default;
        protected override Size ArrangeOverride(Size finalSize) => finalSize;
    }

    private sealed record DocumentationCard(string? Xml, Control View, bool ReadOnly);
}
