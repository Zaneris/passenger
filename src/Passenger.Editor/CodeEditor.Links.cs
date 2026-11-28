using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A stretch of text a click opens something from (a console's file location or web address, an address written in
/// a comment): its range, and what it opens, which only the host reads.</summary>
public sealed record TextLink(TextRange Range, object Target);

/// <summary>Links: the decorations' links found by line (<see cref="DocumentDecorations.Links"/>) and the addresses in each
/// row's comments and strings (<see cref="DocumentDecorations.Addresses"/>), each drawn underlined in the link colour. Where
/// a plain click opens them the pointer over one takes the hand and a click on it that selects nothing opens it
/// (<see cref="LinkClicked"/>); a drag that begins on one selects as anywhere else. While a mouse gesture's keys are held
/// over a stretch its click acts on (<see cref="ClickReach"/>), that stretch is underlined the same and takes the hand.</summary>
public sealed partial class CodeEditor
{
    /// <summary>How long the pointer rests with a gesture's keys held before the host is asked what its click reaches.</summary>
    private static readonly TimeSpan ReachDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The link under the pointer.</summary>
    private readonly HeldLink hovered = new();

    /// <summary>The link a press began on, opened by the release if it lands on it with nothing selected.</summary>
    private readonly HeldLink pressed = new();

    /// <summary>What a held gesture's click reaches under the pointer, its target the gesture's action.</summary>
    private readonly HeldLink pointed = new();

    /// <summary>Where the pointer stands over the editor; null once it left.</summary>
    private Point? pointer;

    /// <summary>The gesture's action, the stretch under the pointer and the text the host was last asked of.</summary>
    private (string Action, TextRange Token, int Offset, TextSnapshot Text)? reachAsked;

    private Debounce? reachWait;
    private CancellationTokenSource? reaching;
    private bool reachRunning;

    /// <summary>The links on a line (from zero) of a text, in its offsets, as the host finds them, which a plain click opens
    /// (a console's): the decorations' (<see cref="DocumentDecorations.SetLinks"/>), whose host's highlighter gives them
    /// the link colour.</summary>
    public Func<TextSnapshot, int, IReadOnlyList<TextLink>>? Links
    {
        get => decorations.Links;
        set => decorations.SetLinks(value, clickOpens: true);
    }

    /// <summary>The link under the pointer, which a click opens; null when it is over none.</summary>
    public TextLink? HoveredLink => hovered.Link;

    /// <summary>A link was clicked: the host opens what it names.</summary>
    public event EventHandler<TextLink>? LinkClicked;

    /// <summary>The stretch a click of a mouse gesture would act from at an offset of the text as it stands, as the host's
    /// language finds it, given the gesture's action from the keymap: while that gesture's keys are held over it, it is
    /// underlined as a link and the pointer takes the hand. Null where the click would act on nothing there; asked once the
    /// pointer rests, and cancelled as it moves on.</summary>
    public Func<string, int, CancellationToken, Task<TextRange?>>? ClickReach { get; set; }

    /// <summary>The stretch a held gesture's click reaches under the pointer, underlined; null with none.</summary>
    public TextRange? PointedRange => pointed.Link?.Range;

    /// <summary>Whether the pointer shows the hand: over a link a click opens, or a stretch a held gesture reaches.</summary>
    public bool ShowsHand => ReferenceEquals(Cursor, HandCursor);

    /// <summary>The link holding the character at an offset: the decorations' for its line, else an address in the row's
    /// comments and strings; null on none.</summary>
    public TextLink? LinkOn(int offset)
    {
        TextSnapshot text = document.Snapshot;
        if (offset < 0 || offset >= text.Length)
        {
            return null;
        }
        (RowSpan span, _) = RowOfOffset(text, offset, beforeWrap: false);
        return LinkIn(text, span, ViewOf(text, span), offset);
    }

    /// <summary>The stretch a click at an offset is on, as the editor draws it: the link holding it, else its coloured run (a
    /// name, a keyword, a string) unless a comment's, else its word (<see cref="WordCharacters"/>); null on none.</summary>
    public TextRange? TokenAt(int offset)
    {
        TextSnapshot text = document.Snapshot;
        if (offset < 0 || offset >= text.Length)
        {
            return null;
        }
        (RowSpan span, _) = RowOfOffset(text, offset, beforeWrap: false);
        LineView view = ViewOf(text, span);
        if (LinkIn(text, span, view, offset) is { } link)
        {
            return link.Range;
        }
        int column = offset - span.Start;
        foreach (LineRun run in view.Runs ?? [])
        {
            if (run.Start <= column && column < run.Start + run.Length)
            {
                if (run.Kind is TokenKind.Comment or TokenKind.Punctuation or TokenKind.Operator
                    or TokenKind.Brace1 or TokenKind.Brace2 or TokenKind.Brace3 or TokenKind.Brace4 or TokenKind.Brace5 or TokenKind.Brace6
                    || TokenKinds.InDocComment(run.Kind))
                {
                    break;
                }
                return new TextRange(span.Start + run.Start, run.Length);
            }
        }
        return WordCharacters.IsWordAt(text, offset) ? TextNavigation.WordAt(text, offset) : null;
    }

    /// <summary>The character under a point in the control, the row it is on and its view; null past a row's end.</summary>
    private (int Offset, RowSpan Span, LineView View)? CharacterAt(Point point)
    {
        if (point.X < GutterWidth || point.Y + scroll.Y < 0 || RowAtY(point.Y) >= RowCount)
        {
            return null;
        }
        TextSnapshot text = document.Snapshot;
        (int line, int row) = Rows.LineOfRow(RowAtY(point.Y));
        if (line < 0 || line >= text.LineCount || row < 0)
        {
            return null;
        }
        RowSpan span = RowSpanOf(text, line, row);
        LineView view = ViewOf(text, span);
        Gap[] gaps = GapsOn(span);
        double x = point.X - TextLeft + scroll.X - span.Indent;
        if (x < 0 || x >= XWith(view, gaps, span.Length, afterHints: false))
        {
            return null;
        }
        int column = ColumnWith(view, gaps, x, span.Length);
        if (column > 0 && XWith(view, gaps, column) > x)
        {
            // The nearest boundary is after the character the point is over.
            column--;
        }
        return (span.Start + column, span, view);
    }

    /// <summary>The link under a point in the control: the one holding the character the point is over, never one the
    /// row ends before.</summary>
    private TextLink? LinkAt(Point point) =>
        CharacterAt(point) is (int offset, RowSpan span, LineView view) ? LinkIn(document.Snapshot, span, view, offset) : null;

    /// <summary>The link holding an offset of a row: the decorations' for its line, else an address in its view.</summary>
    private TextLink? LinkIn(TextSnapshot text, RowSpan span, LineView view, int offset)
    {
        if (decorations.Links is { } links)
        {
            foreach (TextLink link in links(text, span.Line))
            {
                if (link.Range.Start <= offset && offset < link.Range.End)
                {
                    return link;
                }
            }
        }
        foreach (Address address in AddressesOf(view))
        {
            int start = span.Start + address.Start;
            if (start <= offset && offset < start + address.Length)
            {
                return new TextLink(new TextRange(start, address.Length), address.Target);
            }
        }
        return null;
    }

    /// <summary>The addresses written in a row's comments and strings, in its columns; none for a long line's window.</summary>
    private Address[] AddressesOf(LineView view) =>
        decorations.Addresses is { } by && view.Long is null && view.Runs is not null && view.Text is { } formatted ? formatted.Addresses(by) : [];

    /// <summary>The pointer moved: over a link a click opens, if no drag is under way, it takes the hand.</summary>
    private void FollowLinks(Point position, bool dragging)
    {
        if (!decorations.ClickOpensLinks)
        {
            return;
        }
        hovered.Hold(document, dragging ? null : LinkAt(position));
        if (HoveredLink is not null)
        {
            Cursor = HandCursor;
        }
    }

    /// <summary>A press with no keys held, once: the link it is on, which its release opens where a click opens links.</summary>
    private void PressLink(Point position, KeyModifiers modifiers, int clicks) =>
        pressed.Hold(document, decorations.ClickOpensLinks && modifiers == KeyModifiers.None && clicks == 1 ? LinkAt(position) : null);

    /// <summary>A release: the link the press began on is opened if the release is on it and nothing was selected between.</summary>
    private void ReleaseLink(Point position)
    {
        TextLink? link = pressed.Link;
        pressed.Release();
        if (link is not null && selections.Primary.IsEmpty && !selections.HasMultiple && LinkAt(position)?.Range == link.Range)
        {
            LinkClicked?.Invoke(this, link);
        }
    }

    /// <summary>The links held read after an edit where their ranges carried them, as output arrives and the oldest goes.</summary>
    private void FollowLinkEdits()
    {
        hovered.Follow();
        pressed.Follow();
        pointed.Follow();
    }

    /// <summary>Lets go of the links held, for decorations or links other than those they were found in.</summary>
    private void ForgetLinks()
    {
        hovered.Release();
        pressed.Release();
        DropReach();
    }

    /// <summary>The pointer or the keys held moved: where a gesture's keys are held over a stretch, the host is asked, once
    /// the pointer rests, what its click reaches there; anywhere else what was underlined goes.</summary>
    private void FollowGesture(KeyModifiers keys)
    {
        if (ClickReach is null || pointer is not { } at || keys == KeyModifiers.None || drag.Mode != DragMode.None
            || GesturesKeymap?.ActionFor(new MouseShortcut(MouseButton.Left, keys)) is not { } action
            || CharacterAt(at) is not (int offset, _, _))
        {
            DropReach();
            return;
        }
        if (pointed.Link is { } shown && (string)shown.Target == action && shown.Range.Start <= offset && offset < shown.Range.End)
        {
            Cursor = HandCursor;
            return;
        }
        Unpoint();
        TextSnapshot text = document.Snapshot;
        if (TokenAt(offset) is not { } token)
        {
            StopReaching();
            return;
        }
        if (reachAsked is { } asked && asked.Action == action && asked.Token == token && ReferenceEquals(asked.Text, text))
        {
            return;
        }
        StopReaching();
        reachAsked = (action, token, offset, text);
        (reachWait ??= new Debounce(ReachDelay, ReachAsync)).Restart();
    }

    /// <summary>Asks the host what the gesture's click reaches where the pointer rests, one ask at a time, and underlines it
    /// if the pointer is still there with the same keys and the text is as it was.</summary>
    private async Task ReachAsync()
    {
        if (reachRunning || reachAsked is not { } asked || ClickReach is not { } reach)
        {
            return;
        }
        reachRunning = true;
        CancellationTokenSource cancel = reaching = new CancellationTokenSource();
        TextRange? found;
        try
        {
            found = await reach(asked.Action, asked.Offset, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            found = null;
        }
        finally
        {
            reachRunning = false;
            if (reaching == cancel)
            {
                reaching = null;
            }
            cancel.Dispose();
        }
        if (reachAsked != asked)
        {
            // The pointer moved on while the host answered: what it rests on now is asked in turn.
            if (reachAsked is not null)
            {
                reachWait?.Restart();
            }
            return;
        }
        if (found is not { } range || !ReferenceEquals(document.Snapshot, asked.Text) || range.Start > asked.Offset || asked.Offset >= range.End)
        {
            return;
        }
        pointed.Hold(document, new TextLink(range, asked.Action));
        Cursor = HandCursor;
        RedrawChanged();
    }

    /// <summary>Stops asking what a gesture reaches and lets go of what it underlined.</summary>
    private void DropReach()
    {
        StopReaching();
        Unpoint();
    }

    private void StopReaching()
    {
        reachAsked = null;
        reachWait?.Stop();
        reaching?.Cancel();
    }

    /// <summary>Lets go of what a held gesture underlined, the pointer back to the text's I-beam.</summary>
    private void Unpoint()
    {
        if (pointed.Link is null)
        {
            return;
        }
        pointed.Release();
        if (pointer is { } at && HoveredLink is null)
        {
            Cursor = at.X < GutterWidth ? Cursor.Default : IbeamCursor;
        }
        RedrawChanged();
    }

    /// <summary>A key went down or up: the keys held over the text may now make or end a gesture.</summary>
    private void HeardKeys(object? sender, KeyEventArgs e)
    {
        if (pointer is not null && e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
        {
            FollowGesture(e.KeyModifiers);
        }
    }

    /// <summary>A link held on a range of the text, carried with it; one an edit touched is gone.</summary>
    private sealed class HeldLink
    {
        private AnchoredRange? at;

        public TextLink? Link { get; private set; }

        /// <summary>Holds a link of the text as it stands, or none, in place of the one held.</summary>
        public void Hold(TextDocument text, TextLink? link)
        {
            if (link is not null && link == Link)
            {
                return;
            }
            Release();
            Link = link;
            at = link is null ? null : text.MarkAt(link.Range);
        }

        /// <summary>Reads the link's range after an edit: a range of another length was touched, and the link goes.</summary>
        public void Follow()
        {
            if (Link is not { } link || at is null || at.Range == link.Range)
            {
                return;
            }
            if (at.Range.Length == link.Range.Length)
            {
                Link = link with { Range = at.Range };
            }
            else
            {
                Release();
            }
        }

        public void Release()
        {
            at?.Remove();
            at = null;
            Link = null;
        }
    }

    /// <summary>An address written in a row's comment or string: where it starts in the row, how long it is, what it opens.</summary>
    private readonly record struct Address(int Start, int Length, object Target);

    /// <summary>The addresses in a formatted row's comments and strings, a run of them read as one stretch, each taken by
    /// <paramref name="by"/> or left text.</summary>
    private static Address[] FindAddresses(string content, LineRun[] runs, Func<string, object?> by)
    {
        List<Address>? found = null;
        for (int i = 0; i < runs.Length; i++)
        {
            if (!HoldsAddresses(runs[i].Kind))
            {
                continue;
            }
            int start = runs[i].Start;
            int end = start + runs[i].Length;
            while (i + 1 < runs.Length && runs[i + 1].Start == end && HoldsAddresses(runs[i + 1].Kind))
            {
                i++;
                end = runs[i].Start + runs[i].Length;
            }
            foreach (ValueMatch match in WebAddresses.In(content.AsSpan(start, end - start)))
            {
                if (by(content.Substring(start + match.Index, match.Length)) is { } target)
                {
                    (found ??= []).Add(new Address(start + match.Index, match.Length, target));
                }
            }
        }
        return found is null ? [] : [.. found];
    }

    private static bool HoldsAddresses(TokenKind kind) => kind is TokenKind.Comment or TokenKind.String || TokenKinds.InDocComment(kind);

    /// <summary>The links' underlines on a row, in the link's underline colour, under the text's baseline: the decorations'
    /// links, its addresses and what a held gesture reaches.</summary>
    private void DrawLinks(DrawingContext context, TextSnapshot text, LineView view, Gap[] gaps, double left, double y)
    {
        double under = Math.Round(y + ((metrics.LineHeight - view.Height) / 2) + view.Baseline) + 1.5;
        IPen? pen = null;
        RowSpan span = view.Span;
        if (decorations.Links is { } links)
        {
            foreach (TextLink link in links(text, span.Line))
            {
                Underline(link.Range.Start, link.Range.End);
            }
        }
        TextRange? reach = pointed.Link?.Range;
        foreach (Address address in AddressesOf(view))
        {
            TextRange range = new(span.Start + address.Start, address.Length);
            Underline(range.Start, range.End);
            // What a held gesture reaches is drawn once where it is an address already.
            reach = reach == range ? null : reach;
        }
        if (reach is { } reached)
        {
            Underline(reached.Start, reached.End);
        }

        void Underline(int start, int end)
        {
            int from = Math.Max(start, span.Start) - span.Start;
            int to = Math.Min(end, span.End) - span.Start;
            if (to > from)
            {
                pen ??= palette.PenOf(palette.LinkUnderline, 1);
                context.DrawLine(pen, new Point(left + XWith(view, gaps, from), under), new Point(left + XWith(view, gaps, to, afterHints: false), under));
            }
        }
    }
}
