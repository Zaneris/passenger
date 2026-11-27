using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Documentation shares the fold map's visible rows. Editing reveals the original text in the same editor.</summary>
public sealed partial class CodeEditor
{
    private AnchoredRange? documentationEdit;

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
        bool rendered = Folding.Documentation.TryGetValue(line, out int index);
        bool editing = line == DocumentationEditLine(text);
        if (!rendered && !editing)
        {
            return false;
        }
        double y = TopOf(line) - scroll.Y;
        if (!DocumentationButton(Bounds.Width, y).Contains(at))
        {
            return rendered;
        }
        ClearDocumentationTip();
        document.BreakUndoGroup();
        if (rendered)
        {
            EditDocumentation(text, line, FoldRegions[index]);
            int source = DocumentationStart(text, line);
            SetSelections(SelectionSet.Single(Math.Min(source + 4, text.GetLine(line).End)), reveal: false);
        }
        else if (documentationEdit is { } edit)
        {
            int last = text.GetLineFromOffset(edit.Range.End);
            int next = last + 1 < text.LineCount ? text.GetLineStart(last + 1) : text.GetLineStart(line);
            SetSelections(SelectionSet.Single(next), reveal: false);
            ForgetDocumentationEdit();
        }
        DocumentationRowsChanged();
        return true;
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

    private void DrawDocumentation(DrawingContext context, RowSpan span, LineView view, string text)
    {
        double lineHeight = metrics.LineHeight;
        Rect button = DocumentationButton(frameSize.Width, 0);
        double indent = view.DistanceOf(DocumentationStart(frameText!, span.Line) - span.Start);
        double x = Math.Clamp(TextLeft - scroll.X + indent, frameGutter, Math.Max(frameGutter, button.Left - 12));
        double right = Math.Max(x, button.Right);
        Rect background = new(x, 1, right - x, lineHeight - 2);
        context.FillRectangle(palette.InlayHintBack, background, 3);
        context.FillRectangle(palette.Annotation, new Rect(x, 3, 2, lineHeight - 6), 1);
        double start = x + metrics.CharWidth;
        double room = button.Left - start - metrics.CharWidth;
        if (room > 0)
        {
            using TextLayout layout = new(text, Typeface, FontSize, palette.Text,
                textTrimming: TextTrimming.CharacterEllipsis, maxWidth: room, maxLines: 1);
            layout.Draw(context, new Point(start, (lineHeight - layout.Height) / 2));
        }
        DrawDocumentationButton(context, button, IsReadOnly ? "Source" : "Edit");
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

    private void PointAtDocumentation(Point at)
    {
        int line = LineAtY(at.Y);
        string? text = at.X >= GutterWidth && Folding.Documentation.TryGetValue(line, out int index) ? FoldRegions[index].Documentation : null;
        if (!Equals(ToolTip.GetTip(this), text))
        {
            ToolTip.SetTip(this, text);
        }
    }

    private void ClearDocumentationTip() => ToolTip.SetTip(this, null);
}
