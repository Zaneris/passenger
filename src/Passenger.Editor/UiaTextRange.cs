using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Avalonia;
using Passenger.Text;

namespace Passenger.Editor.UiAutomation;

/// <summary>A range of a control's text as UI Automation's text pattern reads it, by its two ends' offsets: read, moved by
/// a character, a word, a line or the whole text as <see cref="AccessibleText"/> finds them (a format a word, a paragraph
/// a line, a page the whole), placed on the screen, selected and scrolled into view. The text changes under a range kept,
/// so its ends are taken within the text as it stands whenever it is read. Each call is answered on the UI thread.</summary>
[GeneratedComClass]
internal sealed partial class UiaTextRange : ITextRangeProvider
{
    private readonly UiaTextElement element;
    private int start;
    private int end;

    public UiaTextRange(UiaTextElement element, int start, int end)
    {
        this.element = element;
        this.start = start;
        this.end = end;
    }

    private AccessibleText Text => element.Text;

    /// <summary>The range's ends within the text as it stands, in order.</summary>
    internal (int Start, int End) Ends
    {
        get
        {
            int length = Text.Length;
            int from = Math.Clamp(start, 0, length);
            return (from, Math.Clamp(end, from, length));
        }
    }

    public nint Clone() => Uia.Of<ITextRangeProvider>(Copy());

    public int Compare(nint range) => Compare(Other(range)) ? 1 : 0;

    public int CompareEndpoints(int endpoint, nint targetRange, int targetEndpoint) =>
        CompareEndpoints(endpoint, Other(targetRange) ?? throw new ArgumentException("The range is not of this text", nameof(targetRange)), targetEndpoint);

    internal UiaTextRange Copy() => element.OnUi(() => new UiaTextRange(element, start, end));

    /// <summary>Whether another range of this text holds the same.</summary>
    internal bool Compare(UiaTextRange? other) => other is not null && element.OnUi(() => other.Ends == Ends);

    /// <summary>How far one of this range's ends stands after one of another's, negative where before.</summary>
    internal int CompareEndpoints(int endpoint, UiaTextRange target, int targetEndpoint) =>
        element.OnUi(() => End(endpoint) - target.End(targetEndpoint));

    public void ExpandToEnclosingUnit(int unit) => element.OnUi(() =>
    {
        (int from, _) = Ends;
        TextRange enclosing = UnitAt(unit, from);
        (start, end) = (enclosing.Start, enclosing.End);
    });

    /// <summary>The range where it is all of one value of an attribute: only whether the text is read-only is known, the
    /// whole text's.</summary>
    public nint FindAttribute(int attributeId, Variant value, int backward) =>
        attributeId == Uia.IsReadOnlyAttribute && value.Bool is { } readOnly && element.OnUi(() => Text.IsReadOnly) == readOnly ? Clone() : 0;

    public nint FindText(string text, int backward, int ignoreCase) => Uia.Of<ITextRangeProvider>(Find(text, backward != 0, ignoreCase != 0));

    /// <summary>The first place within the range holding a text, or the last going backward; null where none does.</summary>
    internal UiaTextRange? Find(string text, bool backward, bool ignoreCase) => element.OnUi(() =>
    {
        (int from, int to) = Ends;
        return Text.Find(TextRange.FromBounds(from, to), text, backward, ignoreCase) is { } found ? new UiaTextRange(element, found.Start, found.End) : null;
    });

    public unsafe void GetAttributeValue(int attributeId, ComVariant* value)
    {
        if (attributeId == Uia.IsReadOnlyAttribute)
        {
            *value = ComVariant.Create(element.OnUi(() => Text.IsReadOnly));
            return;
        }
        *value = OperatingSystem.IsWindows() ? Uia.NotSupported() : default;
    }

    public nint GetBoundingRectangles() => Uia.Doubles(BoundingRectangles());

    /// <summary>A rectangle on the screen for each row the range is drawn on, as UI Automation lays them out: left, top,
    /// width and height after one another.</summary>
    internal double[] BoundingRectangles() => element.OnUi(() =>
    {
        (int from, int to) = Ends;
        IReadOnlyList<Rect> rows = Text.BoundsOf(TextRange.FromBounds(from, to));
        double[] rectangles = new double[rows.Count * 4];
        for (int i = 0; i < rows.Count; i++)
        {
            UiaRect row = element.ScreenRect(rows[i]);
            (rectangles[i * 4], rectangles[(i * 4) + 1], rectangles[(i * 4) + 2], rectangles[(i * 4) + 3]) = (row.Left, row.Top, row.Width, row.Height);
        }
        return rectangles;
    });

    public nint GetEnclosingElement() => Uia.Of<IRawElementProviderSimple>(element);

    public string GetText(int maxLength)
    {
        if (maxLength < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "A range's text is read whole, or up to a length");
        }
        return element.OnUi(() =>
        {
            (int from, int to) = Ends;
            return Text.Read(from, maxLength < 0 ? to - from : Math.Min(to - from, maxLength));
        });
    }

    /// <summary>Moves the range by whole units: an empty range as a caret steps over that many boundaries; a longer one taken
    /// back to the start of its first unit, stepped from unit to unit, and made one unit long, never moved onto the text's
    /// end. How many units it moved, short of the count where the text ends first.</summary>
    public int Move(int unit, int count) => element.OnUi(() =>
    {
        (int from, int to) = Ends;
        bool empty = from == to;
        int at = empty ? from : UnitAt(unit, from).Start;
        int moved = 0;
        int length = Text.Length;
        while (moved < count && Next(unit, at) is { } next && (empty || next < length))
        {
            at = next;
            moved++;
        }
        while (moved > count && Previous(unit, at) is { } previous)
        {
            at = previous;
            moved--;
        }
        if (moved != 0)
        {
            (start, end) = empty ? (at, at) : (at, UnitAt(unit, at).End);
        }
        return moved;
    });

    /// <summary>Moves one end over that many unit boundaries, taking the other end along where it passes it; how many it
    /// moved.</summary>
    public int MoveEndpointByUnit(int endpoint, int unit, int count) => element.OnUi(() =>
    {
        int at = End(endpoint);
        int moved = 0;
        while (moved < count && Next(unit, at) is { } next)
        {
            at = next;
            moved++;
        }
        while (moved > count && Previous(unit, at) is { } previous)
        {
            at = previous;
            moved--;
        }
        Place(endpoint, at);
        return moved;
    });

    public void MoveEndpointByRange(int endpoint, nint targetRange, int targetEndpoint) =>
        MoveEndpointByRange(endpoint, Other(targetRange) ?? throw new ArgumentException("The range is not of this text", nameof(targetRange)), targetEndpoint);

    internal void MoveEndpointByRange(int endpoint, UiaTextRange target, int targetEndpoint) =>
        element.OnUi(() => Place(endpoint, target.End(targetEndpoint)));

    public void Select() => element.OnUi(() =>
    {
        (int from, int to) = Ends;
        Text.Select(TextRange.FromBounds(from, to));
    });

    public void AddToSelection() => throw new InvalidOperationException("A reader chooses one range at a time");

    public void RemoveFromSelection() => throw new InvalidOperationException("A reader chooses one range at a time");

    public void ScrollIntoView(int alignToTop) => element.OnUi(() =>
    {
        (int from, int to) = Ends;
        Text.Show(TextRange.FromBounds(from, to));
    });

    public nint GetChildren() => Uia.Unknowns<ITextRangeProvider>([]);

    /// <summary>The offset of one of the range's ends: its start for UI Automation's TextPatternRangeEndpoint_Start.</summary>
    private int End(int endpoint) => endpoint == Uia.StartEndpoint ? Ends.Start : Ends.End;

    /// <summary>Puts one end at an offset, and the other there too where the range would turn inside out.</summary>
    private void Place(int endpoint, int at)
    {
        (int from, int to) = Ends;
        (start, end) = endpoint == Uia.StartEndpoint ? (at, Math.Max(at, to)) : (Math.Min(at, from), at);
    }

    /// <summary>A range of the same text UI Automation hands back, null for any other.</summary>
    private UiaTextRange? Other(nint range) => Uia.From<UiaTextRange>(range) is { } other && ReferenceEquals(other.element, element) ? other : null;

    /// <summary>The unit holding an offset, the text's end a unit of nothing.</summary>
    private TextRange UnitAt(int unit, int at) => unit switch
    {
        Uia.CharacterUnit => Text.CharacterAt(at),
        Uia.FormatUnit or Uia.WordUnit => Text.WordAt(at),
        Uia.LineUnit or Uia.ParagraphUnit => Text.Line(Text.LineOf(at)) is var line ? TextRange.FromBounds(line.Start, line.EndIncludingBreak) : default,
        _ => new TextRange(0, Text.Length),
    };

    /// <summary>The first unit boundary after an offset, none at the text's end.</summary>
    private int? Next(int unit, int at) => at < Text.Length ? UnitAt(unit, at).End : null;

    /// <summary>The last unit boundary before an offset, none at the text's start.</summary>
    private int? Previous(int unit, int at) => at > 0 ? UnitAt(unit, at - 1).Start : null;
}
