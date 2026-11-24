using System.Runtime.CompilerServices;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Which of a document's decorations were set again.</summary>
public enum DecorationKind
{
    Markers,
    InlayHints,
    Folds,
    Annotations,
    ChangeMarks,
    Bookmarks,
    Links,
    RowsAbove,
}

/// <summary>What is drawn on one document's text for whoever shows it: its markers, inlay hints, folds, rows above lines,
/// line-end annotations, change marks, bookmarks and links. Every editor showing the document reads this one
/// (<see cref="CodeEditor.Show"/>), so a split or another window draws what the first does; an editor given a text alone has
/// one of its own. The markers, the hints, the folds and the rows above are carried through each change ahead of everyone
/// who hears of it, as runs an edit moves only where it reaches; the rest are set again by their owners. Each list
/// is replaced, never changed in place, and kept in order by line, so a frame reads only what is on screen. Read and
/// changed on the thread that changes the document.</summary>
public sealed class DocumentDecorations
{
    private static readonly LineOrdered<LineAnnotation> NoAnnotations = new([], static a => a.Line, static a => a.Line);
    private static readonly LineOrdered<ChangeMark> NoChanges = new([], static c => c.Line, LastOf);
    private static readonly LineOrdered<int> NoBookmarks = new([], static l => l, static l => l);

    /// <summary>The document's version the hints and folds were last carried to.</summary>
    private int carried;

    public DocumentDecorations(TextDocument text)
    {
        Text = text;
        carried = text.Version;
        text.ChangedFirst += OnChangedFirst;
    }

    public TextDocument Text { get; }

    /// <summary>Raised when one kind was set again, its list replaced; never for the markers, hints and folds a change
    /// carried, which each editor follows as it hears of the change.</summary>
    public event EventHandler<DecorationKind>? Changed;

    /// <summary>The language service's markers, in the order of their starts.</summary>
    public MarkerList Markers { get; private set; } = MarkerList.Empty;

    /// <summary>How many of the markers are errors and how many warnings.</summary>
    public (int Errors, int Warnings) Problems => Markers.Problems;

    /// <summary>The hints, in offset order.</summary>
    public HintList InlayHints { get; private set; } = HintList.Empty;

    /// <summary>The rows drawn above lines, in offset order.</summary>
    public RowAboveList RowsAbove { get; private set; } = RowAboveList.Empty;

    /// <summary>The regions that fold, in offset order, each saying whether it is folded.</summary>
    public IReadOnlyList<FoldRegion> FoldRegions { get; private set; } = [];

    /// <summary>Text after lines' code.</summary>
    public IReadOnlyList<LineAnnotation> LineAnnotations => AnnotationsByLine.Items;

    public LineOrdered<LineAnnotation> AnnotationsByLine { get; private set; } = NoAnnotations;

    /// <summary>The lines that differ from another text.</summary>
    public IReadOnlyList<ChangeMark> ChangeMarks => ChangesByLine.Items;

    public LineOrdered<ChangeMark> ChangesByLine { get; private set; } = NoChanges;

    /// <summary>The bookmarked lines.</summary>
    public IReadOnlyList<int> Bookmarks => BookmarksByLine.Items;

    public LineOrdered<int> BookmarksByLine { get; private set; } = NoBookmarks;

    /// <summary>The links on a line (from zero) of a text, in its offsets, as the owner finds them (a console's); null for a
    /// text with none. Asked for each line drawn and the line under the pointer as it moves, so the owner keeps what it found.</summary>
    public Func<TextSnapshot, int, IReadOnlyList<TextLink>>? Links { get; private set; }

    /// <summary>What an address written in a comment or a string opens, as the owner takes it, null leaving it text; null for
    /// a text whose addresses are none. Each editor finds them in the rows it draws, by the colours its highlighter gave.</summary>
    public Func<string, object?>? Addresses { get; private set; }

    /// <summary>Whether a plain click opens a link (a console's); else a click places the caret, and the keymap's gesture that
    /// follows a name follows a link too.</summary>
    public bool ClickOpensLinks { get; private set; }

    /// <summary>How many times markers were given out of order and sorted here: what a host that gives them so costs a key.</summary>
    public int MarkersSorted { get; private set; }

    /// <summary>Sets the markers for the text as it stands, taken as given when they are in order already (a
    /// <see cref="MarkerList"/> always is), and carried through its changes from then on.</summary>
    public void SetMarkers(IReadOnlyList<Marker> markers)
    {
        if (ReferenceEquals(markers, Markers))
        {
            return;
        }
        Markers = MarkerList.Of(markers, out bool sorted);
        MarkersSorted += sorted ? 1 : 0;
        Changed?.Invoke(this, DecorationKind.Markers);
    }

    /// <summary>Sets the hints for the text as it stands, taken as they are when in order (a <see cref="HintList"/> always is).</summary>
    public void SetInlayHints(IReadOnlyList<InlayHint> hints)
    {
        HintList list = HintList.Of(hints);
        if (ReferenceEquals(list, InlayHints))
        {
            return;
        }
        InlayHints = list;
        Changed?.Invoke(this, DecorationKind.InlayHints);
    }

    /// <summary>Sets the rows above lines for the text as it stands, taken as they are when in order (a
    /// <see cref="RowAboveList"/> always is); rows drawn as those shown already are taken without a word to the editors,
    /// which draw nothing again for them.</summary>
    public void SetRowsAbove(IReadOnlyList<RowAbove> rows)
    {
        RowAboveList list = RowAboveList.Of(rows);
        bool alike = RowAboveList.DrawnAlike(list, RowsAbove);
        RowsAbove = list;
        if (!alike)
        {
            Changed?.Invoke(this, DecorationKind.RowsAbove);
        }
    }

    /// <summary>Sets the regions that fold and which of them are folded, for the text as it stands.</summary>
    public void SetFoldRegions(IReadOnlyList<FoldRegion> regions)
    {
        if (ReferenceEquals(regions, FoldRegions))
        {
            return;
        }
        FoldRegions = regions is FoldList || InOrder(regions, static r => r.Start) ? regions : [.. regions.OrderBy(r => r.Start)];
        Changed?.Invoke(this, DecorationKind.Folds);
    }

    public void SetLineAnnotations(IReadOnlyList<LineAnnotation> annotations)
    {
        if (ReferenceEquals(annotations, LineAnnotations))
        {
            return;
        }
        AnnotationsByLine = annotations.Count == 0 ? NoAnnotations : new LineOrdered<LineAnnotation>(annotations, static a => a.Line, static a => a.Line);
        Changed?.Invoke(this, DecorationKind.Annotations);
    }

    public void SetChangeMarks(IReadOnlyList<ChangeMark> marks)
    {
        if (ReferenceEquals(marks, ChangeMarks))
        {
            return;
        }
        ChangesByLine = marks.Count == 0 ? NoChanges : new LineOrdered<ChangeMark>(marks, static c => c.Line, LastOf);
        Changed?.Invoke(this, DecorationKind.ChangeMarks);
    }

    public void SetBookmarks(IReadOnlyList<int> lines)
    {
        if (ReferenceEquals(lines, Bookmarks))
        {
            return;
        }
        BookmarksByLine = lines.Count == 0 ? NoBookmarks : new LineOrdered<int>(lines, static l => l, static l => l);
        Changed?.Invoke(this, DecorationKind.Bookmarks);
    }

    /// <summary>Sets the links the owner finds by line, which a plain click opens or not.</summary>
    public void SetLinks(Func<TextSnapshot, int, IReadOnlyList<TextLink>>? links, bool clickOpens)
    {
        if (ReferenceEquals(links, Links) && clickOpens == ClickOpensLinks)
        {
            return;
        }
        Links = links;
        ClickOpensLinks = clickOpens;
        Changed?.Invoke(this, DecorationKind.Links);
    }

    /// <summary>Sets what an address in a comment or a string opens: each editor showing the text finds them again.</summary>
    public void SetAddresses(Func<string, object?>? addresses)
    {
        if (ReferenceEquals(addresses, Addresses))
        {
            return;
        }
        Addresses = addresses;
        Changed?.Invoke(this, DecorationKind.Links);
    }

    /// <summary>Carries the markers, hints, folds and rows above through a change once, whichever of its hearers asks first:
    /// an editor that heard of it before this did wraps its lines with the hints where the change left them.</summary>
    internal void Follow(DocumentChangedEventArgs change)
    {
        if (change.Version == carried)
        {
            return;
        }
        carried = change.Version;
        if (Markers.Count > 0)
        {
            Markers = Markers.Through(change.Edits);
        }
        if (InlayHints.Count > 0)
        {
            InlayHints = InlayHints.Through(change.Edits);
        }
        if (FoldRegions.Count > 0)
        {
            FoldRegions = FoldList.Of(FoldRegions).Through(change.Edits);
        }
        if (RowsAbove.Count > 0)
        {
            RowsAbove = RowsAbove.Through(change);
        }
    }

    /// <summary>Stops following the text, for decorations no editor shows any more.</summary>
    internal void Release() => Text.ChangedFirst -= OnChangedFirst;

    private void OnChangedFirst(object? sender, DocumentChangedEventArgs change) => Follow(change);

    /// <summary>The last line a change mark covers: its own for a deletion, drawn between lines.</summary>
    private static int LastOf(ChangeMark change) => change.Line + Math.Max(1, change.Count) - 1;

    private static bool InOrder<T>(IReadOnlyList<T> items, Func<T, int> key)
    {
        for (int i = 1; i < items.Count; i++)
        {
            if (key(items[i]) < key(items[i - 1]))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>Items each on a stretch of lines, in the order of their first, with how far down those up to each reach, so the
/// ones on a few lines are found by halving and read alone. Made once for each list given, never for a frame; the list
/// itself is the order when it is in order already.</summary>
public sealed class LineOrdered<T>
{
    private static readonly ConditionalWeakTable<IReadOnlyList<T>, LineOrdered<T>> Made = [];

    private readonly IReadOnlyList<T> ordered;
    private readonly int[] firsts;
    private readonly int[] reach;

    public LineOrdered(IReadOnlyList<T> items, Func<T, int> first, Func<T, int> last)
    {
        Items = items;
        bool inOrder = true;
        for (int i = 1; i < items.Count && inOrder; i++)
        {
            inOrder = first(items[i]) >= first(items[i - 1]);
        }
        ordered = inOrder ? items : [.. items.OrderBy(first)];
        firsts = new int[ordered.Count];
        reach = new int[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            firsts[i] = first(ordered[i]);
            reach[i] = Math.Max(i > 0 ? reach[i - 1] : int.MinValue, last(ordered[i]));
        }
    }

    /// <summary>The order of a list of items each on one line, made once for that list whoever asks: what every editor's
    /// gutter showing a file reads its marks by.</summary>
    public static LineOrdered<T> Of(IReadOnlyList<T> items, Func<T, int> line)
    {
        if (!Made.TryGetValue(items, out LineOrdered<T>? known))
        {
            known = new LineOrdered<T>(items, line, line);
            Made.AddOrUpdate(items, known);
        }
        return known;
    }

    /// <summary>The items as they were given.</summary>
    public IReadOnlyList<T> Items { get; }

    public int Count => ordered.Count;

    /// <summary>An item by its place in line order.</summary>
    public T this[int index] => ordered[index];

    /// <summary>The first line of the item at a place in line order.</summary>
    public int LineOf(int index) => firsts[index];

    /// <summary>The place in line order of the first item reaching a line or past it: none before it reaches that far.</summary>
    public int From(int line) => Sorted.FirstAtOrAfter(reach, line);
}
