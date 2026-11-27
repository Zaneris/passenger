using Avalonia;
using Avalonia.Controls;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A block's source lines, excluding the end, and its document view once the current text has been parsed.</summary>
public sealed record MarkdownEditorBlock(int FirstLine, int EndLine, Func<Control> Create, bool IsCurrent);

public sealed partial class CodeEditor
{
    private Canvas? markdownBlockLayer;
    private TextSnapshot? markdownBlocksFrom;
    private IReadOnlyList<MarkdownEditorBlock> markdownBlocks = [];
    private readonly List<MarkdownEditorBlock> shownMarkdownBlocks = [];
    private readonly Dictionary<Func<Control>, Control> markdownControls = [];
    private double markdownRowHeight;

    /// <summary>Sets the document views of Markdown blocks, in source order, for the given snapshot.</summary>
    public void ShowMarkdownBlocks(TextSnapshot? from, IReadOnlyList<MarkdownEditorBlock> blocks)
    {
        if (!SameBlockLines(blocks))
        {
            views.Clear();
        }
        markdownBlocksFrom = from;
        markdownBlocks = blocks;
        RedrawChanged();
    }

    private bool SameBlockLines(IReadOnlyList<MarkdownEditorBlock> blocks)
    {
        if (markdownBlocks.Count != blocks.Count)
        {
            return false;
        }
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].FirstLine != markdownBlocks[i].FirstLine || blocks[i].EndLine != markdownBlocks[i].EndLine)
            {
                return false;
            }
        }
        return true;
    }

    private bool MarkdownBlockShows(int line) => BlockContains(shownMarkdownBlocks, line);

    private bool IsMarkdownBlockSource(TextSnapshot text, int line) =>
        ReferenceEquals(text, markdownBlocksFrom) && BlockContains(markdownBlocks, line);

    private static bool BlockContains(IReadOnlyList<MarkdownEditorBlock> blocks, int line)
    {
        int index = Sorted.FirstAfter(blocks, static block => block.EndLine, line);
        return index < blocks.Count && blocks[index].FirstLine <= line;
    }

    private void PlaceMarkdownBlocks(TextSnapshot text, int first, int last, Size size)
    {
        shownMarkdownBlocks.Clear();
        if (presentsMarkdown && ReferenceEquals(text, markdownBlocksFrom) && markdownBlocks.Count > 0)
        {
            int i = Sorted.FirstAfter(markdownBlocks, static block => block.EndLine, first);
            for (; i < markdownBlocks.Count && markdownBlocks[i].FirstLine <= last; i++)
            {
                MarkdownEditorBlock block = markdownBlocks[i];
                if (block.IsCurrent && !IsHidden(block.FirstLine) && !IsHidden(block.EndLine - 1) && !SelectionIn(block, text))
                {
                    shownMarkdownBlocks.Add(block);
                }
            }
        }
        if (shownMarkdownBlocks.Count == 0)
        {
            markdownBlockLayer?.Children.Clear();
            markdownControls.Clear();
            return;
        }

        Canvas layer = markdownBlockLayer ?? AttachMarkdownBlockLayer();
        if (markdownRowHeight != metrics.LineHeight)
        {
            markdownRowHeight = metrics.LineHeight;
            layer.Children.Clear();
            markdownControls.Clear();
        }
        foreach (Func<Control> gone in markdownControls.Keys.Where(create => !shownMarkdownBlocks.Any(block => block.Create == create)).ToArray())
        {
            layer.Children.Remove(markdownControls[gone]);
            markdownControls.Remove(gone);
        }
        double left = Math.Max(GutterWidth, TextLeft - scroll.X);
        double width = Math.Max(0, size.Width - left - vertical.DesiredSize.Width);
        foreach (MarkdownEditorBlock block in shownMarkdownBlocks)
        {
            if (!markdownControls.TryGetValue(block.Create, out Control? control))
            {
                control = block.Create();
                markdownControls.Add(block.Create, control);
                layer.Children.Add(control);
            }
            double top = TopOf(block.FirstLine);
            control.Width = width;
            control.MaxHeight = BottomOf(block.EndLine - 1) - top;
            Canvas.SetLeft(control, left);
            Canvas.SetTop(control, top - scroll.Y);
        }
    }

    private bool SelectionIn(MarkdownEditorBlock block, TextSnapshot text)
    {
        int first = FirstSelectionEndingFrom(text.GetLineStart(block.FirstLine));
        return first < selections.Count && selections.Items[first].Start <= text.GetLine(block.EndLine - 1).End;
    }

    private Canvas AttachMarkdownBlockLayer()
    {
        Canvas layer = new() { ClipToBounds = true };
        LogicalChildren.Add(layer);
        VisualChildren.Insert(2, layer);
        markdownBlockLayer = layer;
        InvalidateMeasure();
        return layer;
    }
}
