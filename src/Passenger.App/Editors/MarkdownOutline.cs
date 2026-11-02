using Passenger.App.Navigation;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>A Markdown file's outline: its headings as its preview reads them (<see cref="Markdown.ParsePlaced"/>), each
/// under the nearest heading of a higher level before it and spanning its lines up to the next heading of its level or
/// higher, named by its words as a person reads them. Read from the open file's text as the editor has it, off the
/// window's thread.</summary>
internal sealed class MarkdownOutline(TextDocument document) : FileOutline
{
    public override Task<Outline<SymbolRow>> ReadAsync(string path, Outline<SymbolRow>? before, CancellationToken cancel)
    {
        TextSnapshot text = document.Snapshot;
        return BackgroundWork.Run(() => Of(text, path, before, cancel), cancel);
    }

    /// <summary>The outline of a Markdown text, its rows leading to <paramref name="path"/>; a heading with no words is left out.</summary>
    public static Outline<SymbolRow> Of(TextSnapshot text, string path, Outline<SymbolRow>? before = null, CancellationToken cancel = default)
    {
        List<Part> roots = [];
        Stack<Part> open = [];
        foreach (Markdown.Placed placed in Markdown.ParsePlaced(text.ToString()))
        {
            if (placed.Block is not Markdown.Heading heading || Markdown.Plain(Markdown.Inlines(heading.Text)).Trim() is not { Length: > 0 } words)
            {
                continue;
            }
            while (open.TryPeek(out Part? above) && above.Level >= heading.Level)
            {
                open.Pop().Last = placed.Line - 1;
            }
            string line = text.GetLineText(placed.Line);
            int column = line.Length - line.TrimStart(' ').TrimStart('#').TrimStart(' ').Length;
            Part part = new(heading.Level, new SymbolRow(words, null, null, SymbolRow.HeadingKind, path, new TextPosition(placed.Line, column)), placed.Line);
            (open.TryPeek(out Part? parent) ? parent.Children : roots).Add(part);
            open.Push(part);
        }
        foreach (Part left in open)
        {
            left.Last = text.LineCount - 1;
        }
        return Outline<SymbolRow>.Of<Part>(roots, part => part.Children, (part, depth) => (part.Row with { Depth = depth }, part.First, part.Last), before, cancel);
    }

    /// <summary>A heading's row and its level, with the lines (from zero) it spans and the headings under it.</summary>
    private sealed class Part(int level, SymbolRow row, int first)
    {
        public int Level { get; } = level;

        public SymbolRow Row { get; } = row;

        public int First { get; } = first;

        public int Last { get; set; } = first;

        public List<Part> Children { get; } = [];
    }
}
