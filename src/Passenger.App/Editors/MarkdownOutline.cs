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
        return BackgroundWork.Run(() => Of(text, path, before, cancel, document), cancel);
    }

    /// <summary>The outline of a Markdown text, its rows leading to <paramref name="path"/>; a heading with no words is left out.
    /// A document's text is read again from its last read, its preview's or its outline's, where it changed.</summary>
    public static Outline<SymbolRow> Of(TextSnapshot text, string path, Outline<SymbolRow>? before = null, CancellationToken cancel = default, TextDocument? of = null)
    {
        List<Part> roots = [];
        Stack<Part> open = [];
        Markdown.Parsed read = of is null ? Markdown.ParsePlaced(text, null) : Markdown.ReadOf(of, text);
        foreach (Markdown.Placed placed in read.Placed)
        {
            if (placed.Block is not Markdown.Heading heading || Words.GetValue(heading, static said => Markdown.Plain(Markdown.Inlines(said.Text)).Trim()) is not { Length: > 0 } words)
            {
                continue;
            }
            // Its line as read may begin after a '\r' standing alone, inside a line of the document's.
            string line = placed.Source;
            int column = line.Length - line.TrimStart(' ').TrimStart('#').TrimStart(' ').Length;
            TextPosition at = text.GetPosition(read.Text.Start(placed.Line) + column);
            while (open.TryPeek(out Part? above) && above.Level >= heading.Level)
            {
                Part ended = open.Pop();
                ended.Last = Math.Max(ended.First, at.Line - 1);
            }
            Part part = new(heading.Level, new SymbolRow(words, null, null, SymbolRow.HeadingKind, path, at), at.Line);
            (open.TryPeek(out Part? parent) ? parent.Children : roots).Add(part);
            open.Push(part);
        }
        foreach (Part left in open)
        {
            left.Last = text.LineCount - 1;
        }
        return Outline<SymbolRow>.Of<Part>(roots, part => part.Children, (part, depth) => (part.Row with { Depth = depth }, part.First, part.Last), before, cancel);
    }

    /// <summary>Each heading's words as a person reads them, by the heading read, which a read of its text again keeps.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Markdown.Heading, string> Words = new();

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
