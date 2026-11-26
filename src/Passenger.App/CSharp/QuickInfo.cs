using Avalonia;
using Avalonia.Input;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>The card when the pointer rests, or Quick Documentation at the caret: the problems there, then a paused
/// program's value (its rows to open) or what the file's language says of the symbol; an inlay hint's own words over a
/// hint. The pointer may go to the card, which stays while it or the keys are on it, so its words can be selected and
/// copied. A card a key shows is read to a screen reader, one the pointer brings never.</summary>
internal sealed class QuickInfo
{
    private readonly AssistContext context;
    private readonly PopupPlacement popups;
    private readonly Func<bool> completing;
    private readonly InfoCard hoverCard;
    private CancellationTokenSource? hovering;
    private (Workspace.OpenDocument File, TextSnapshot Text, int Offset)? declarationOrigin;

    /// <summary>The pause after the pointer leaves a card (its word, or the card itself) before the card goes: time to reach
    /// the card from the word, on which it stays while the pointer or the keys are on it.</summary>
    private readonly Debounce leaving;

    /// <summary>Takes the card away once the stop its value was read at is gone.</summary>
    private CancellationTokenRegistration hoverGone;

    /// <summary>Where a card of a value was placed: the line's top it hangs from, in the layer, and whether it stands over
    /// the line, so a card that grows as its rows open stays beside the line and inside the editor.</summary>
    private (Point Line, bool Above)? valuesPlace;

    /// <summary>The diff's side the card was last asked for, while it is one (<see cref="ShowIn"/>); null for the active editor.</summary>
    private DiffSide? side;

    /// <summary>The hover card on the layer, which never shows over an open completion list (<paramref name="completing"/>).</summary>
    public QuickInfo(AssistContext context, PopupPlacement popups, Func<bool> completing)
    {
        this.context = context;
        this.popups = popups;
        this.completing = completing;
        hoverCard = new InfoCard("Quick Documentation", context.Browse);
        popups.Add(hoverCard, Hide);
        hoverCard.Play = context.Play;
        leaving = new Debounce(TimeSpan.FromMilliseconds(300), () =>
        {
            if (!HoverHeld)
            {
                Hide();
            }
            return Task.CompletedTask;
        });
        hoverCard.PointerEntered += (_, _) => leaving.Stop();
        hoverCard.PointerExited += (_, _) => Leave();
        hoverCard.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsKeyboardFocusWithinProperty && e.NewValue is false)
            {
                Leave();
            }
        };
        hoverCard.SizeChanged += (_, _) => KeepValuesPlaced();
    }

    private CodeEditor Editor => context.Editor;

    public bool IsVisible => hoverCard.IsVisible;

    /// <summary>What the hover card says while it shows, null otherwise.</summary>
    public string? Text => hoverCard.IsVisible ? hoverCard.Text : null;

    /// <summary>The rows of a paused program's value the hover card shows, while it shows them.</summary>
    public Debugging.VariablesList? Values => hoverCard.IsVisible ? hoverCard.Values : null;

    /// <summary>The hover card while it shows, its frame the popups' (<c>Border.popup</c>).</summary>
    public Avalonia.Controls.Border? Card => hoverCard.IsVisible ? hoverCard : null;

    /// <summary>While the debugger is paused, the value of what the pointer rests on, which the hover card shows
    /// instead of the symbol's description; in a debugged app's .axaml file, a binding's.</summary>
    public Func<int, CancellationToken, Task<DebuggerHover?>>? DebuggerValue { get; set; }

    /// <summary>What the gutter says beside a line (from zero) of the file shown, a line of words each (a breakpoint's
    /// state); null where it says nothing.</summary>
    public Func<int, IReadOnlyList<string>?>? GutterWords { get; set; }

    /// <summary>What a word of a row above a line says more of (a commit's), null where it says nothing.</summary>
    public Func<RowAboveWord, string?>? AboveWords { get; set; }

    /// <summary>Whether the card shown is in use (<see cref="InfoCard.InUse"/>): its keys, a menu of its, a value typed in it.</summary>
    public bool InUse => hoverCard.IsVisible && hoverCard.InUse;

    /// <summary>Whether the card shown stays: in use, or the pointer is on it.</summary>
    private bool HoverHeld => InUse || (hoverCard.IsVisible && hoverCard.IsPointerOver);

    /// <summary>The pointer left the word the card is of: the card goes after a pause, unless the pointer reaches it.</summary>
    public void PointerLeft() => Leave();

    /// <summary>A peek at the caret (Quick Implementations, Error Description, Expression Type Info): the card filled by
    /// <paramref name="fill"/>, placed and told as Quick Documentation's is; <paramref name="none"/> said where it fills
    /// nothing, and a server's failure said for <paramref name="what"/>.</summary>
    public async Task PeekAsync(string what, string none, Func<InfoCard, CancellationToken, Task> fill)
    {
        hovering?.Cancel();
        CancellationTokenSource cancel = new();
        hovering = cancel;
        ForgetValues();
        hoverCard.Clear();
        hoverCard.IsVisible = false;
        int offset = context.Caret;
        try
        {
            await fill(hoverCard, cancel.Token);
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed(what, failed);
            return;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return;
        }
        if (cancel.IsCancellationRequested)
        {
            return;
        }
        if (hoverCard.IsEmpty)
        {
            context.Say(none);
            return;
        }
        Place(offset, Editor.PointOf(offset), null, null, null);
        Said();
    }

    /// <summary>Quick Documentation: the card for what is at the caret (its problems, a paused program's value, the symbol's
    /// description); pressed again while a value's card shows, the card takes the keys, its rows to open with the arrows.</summary>
    public void QuickDocumentation()
    {
        if (hoverCard.IsVisible && hoverCard.Values is { } values)
        {
            values.FocusRow();
            return;
        }
        IdeErrors.Observe(Show(context.Caret, Editor.PointOf(context.Caret), byKey: true));
    }

    /// <summary>What is at an offset as plain text: a paused program's value as its card shows it, while one does; else each
    /// problem on the offset's line, by its kind and place, then what the file's language says of the offset, as its card
    /// says it (<see cref="HoverWords"/>).</summary>
    public async Task<string> WordsAtAsync(int offset, CancellationToken cancel)
    {
        if (hoverCard.IsVisible && hoverCard.Values is not null)
        {
            return hoverCard.Text;
        }
        HoverWords words = new();
        TextSnapshot text = Editor.Document.Snapshot;
        LineSpan line = text.GetLine(text.GetLineFromOffset(Math.Clamp(offset, 0, text.Length)));
        foreach (Marker marker in Editor.Markers.Where(m => m.Range.Start <= line.End && (m.Range.End > line.Start || m.Range.Start == line.Start)).OrderBy(m => m.Range.Start))
        {
            int start = Math.Clamp(marker.Range.Start, line.Start, line.End);
            words.AddText($"{KindOf(marker.Kind)} at line {line.Line + 1}, column {start - line.Start + 1}: {marker.Message}");
        }
        if (context.Editors.Active is { } shown)
        {
            await context.Languages.HoverAsync(words, shown, offset, cancel);
        }
        return words.Text;

        static string KindOf(MarkerKind kind) => kind switch
        {
            MarkerKind.Error => "Error",
            MarkerKind.Warning => "Warning",
            MarkerKind.Unnecessary => "Unnecessary code",
            _ => "Note",
        };
    }

    /// <summary>The card for an offset, hung at a point's left; told to a screen reader where a key asked for it.</summary>
    public async Task Show(int offset, Point point, bool byKey = false)
    {
        using (BackgroundWork.Scope())
        {
            await ShowCore(offset, point, byKey, null);
        }
    }

    /// <summary>The card for an offset of a diff's side that is an open file's own text, hung at a point's left in that
    /// side, on <paramref name="layer"/> (over the diff's own window), else over the editors: what the file's markers and
    /// its language say there, as in its editor, with no paused program's value.</summary>
    public async Task ShowIn(CodeEditor editor, Workspace.OpenDocument file, Avalonia.Controls.Canvas? layer, int offset, Point point)
    {
        using (BackgroundWork.Scope())
        {
            await ShowCore(offset, point, byKey: false, new DiffSide(editor, file, layer));
        }
    }

    /// <summary>The card goes with the diff's side it shows for (typed in, or gone from its window), and off the layer over
    /// the side's own window (<paramref name="layer"/>), which it would hold once that window has closed.</summary>
    public void LetGo(CodeEditor editor, Avalonia.Controls.Canvas? layer)
    {
        if (side?.Editor == editor)
        {
            Hide();
        }
        if (layer is not null && hoverCard.Parent == layer)
        {
            popups.OnLayer(hoverCard);
        }
    }

    private async Task ShowCore(int offset, Point point, bool byKey, DiffSide? over)
    {
        hovering?.Cancel();
        CancellationTokenSource cancel = new();
        hovering = cancel;
        ForgetValues();
        hoverCard.Clear();
        side = over;
        foreach (Marker marker in (over?.Editor ?? Editor).MarkersAt(offset))
        {
            hoverCard.AddText(marker.Message, marker.Kind switch
            {
                MarkerKind.Error => InfoCard.Brush(TokenKeys.Error),
                MarkerKind.Warning => InfoCard.Brush(TokenKeys.Warning),
                _ => null,
            });
        }
        DebuggerHover? debuggerValue = over is null && DebuggerValue is { } value ? await value(offset, cancel.Token) : null;
        if (cancel.IsCancellationRequested)
        {
            return;
        }
        Debugging.VariablesList? values = null;
        if (debuggerValue is not null)
        {
            if (!AddValue(debuggerValue, out values))
            {
                return;
            }
        }
        else
        {
            try
            {
                if ((over?.File ?? context.Editors.Active) is { } shown)
                {
                    if (over is null && shown.Language == Languages.Language.CSharp)
                    {
                        declarationOrigin = (shown, shown.Text.Snapshot, offset);
                        hoverCard.OpenDeclaration = OpenDeclaration;
                    }
                    await context.Languages.HoverAsync(hoverCard, shown, offset, cancel.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if ((hoverCard.IsEmpty && values is null) || completing())
        {
            hoverCard.IsVisible = false;
            return;
        }
        Place(offset, point, values, debuggerValue, over);
        if (byKey)
        {
            Said();
        }
    }

    /// <summary>The card shown, its words told to a screen reader.</summary>
    private void Said() => popups.Tell(hoverCard.Text, Urgency.Polite, hoverCard);

    private void OpenDeclaration(string name, string key)
    {
        if (hoverCard.IsVisible && declarationOrigin is { } origin
            && context.Editors.Active == origin.File && origin.File.Text.Snapshot == origin.Text)
        {
            Hide();
            IdeErrors.Observe(context.Languages.CSharp.OpenHoverDeclarationAsync(origin.File, origin.Text, origin.Offset, name, key));
        }
    }

    /// <summary>Quick Evaluate Expression's card: a value read for an offset, shown as a hovered one is under its line, its
    /// row taking the keys at once, as Quick Documentation pressed again gives them.</summary>
    public void ShowValue(int offset, DebuggerHover value)
    {
        hovering?.Cancel();
        hovering = new CancellationTokenSource();
        ForgetValues();
        hoverCard.Clear();
        if (!AddValue(value, out Debugging.VariablesList? values) || values is null)
        {
            return;
        }
        Place(offset, Editor.PointOf(Math.Min(offset, Editor.Document.Snapshot.Length)), values, value, null);
        Said();
        // Once laid out, so the row itself has a container to take them.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (hoverCard.IsVisible && hoverCard.Values == values)
            {
                values.FocusRow();
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>A paused program's value added to the card: what went wrong reading it, then its rows or its code; false,
    /// the card hidden, once the stop it was read at is gone.</summary>
    private bool AddValue(DebuggerHover value, out Debugging.VariablesList? values)
    {
        values = null;
        if (value.Gone.IsCancellationRequested)
        {
            hoverCard.IsVisible = false;
            return false;
        }
        if (value.Problem is { } problem)
        {
            hoverCard.AddText(problem, InfoCard.Brush(TokenKeys.Error));
        }
        if (value.Values is { } rows)
        {
            values = rows;
            // The menu closed, the card goes unless the pointer or the keys are still on it.
            rows.MenuClosed += (_, _) =>
            {
                if (hoverCard.Values == rows)
                {
                    Leave();
                }
            };
        }
        else if (value.Code.Length > 0)
        {
            hoverCard.AddCode(value.Code);
        }
        return true;
    }

    /// <summary>The card shown under the line of an offset, at a point's left, in the active editor or a diff's side
    /// (<paramref name="over"/>): a value's rows as they fit (<see cref="PlaceValues"/>), gone with the stop a debugger's
    /// value was read at.</summary>
    private void Place(int offset, Point point, Debugging.VariablesList? values, DebuggerHover? debuggerValue, DiffSide? over)
    {
        hoverCard.IsVisible = true;
        CodeEditor editor = over?.Editor ?? Editor;
        TextSnapshot text = editor.Document.Snapshot;
        int lineStart = text.GetLineStart(text.GetLineFromOffset(Math.Min(offset, text.Length)));
        Point anchor = editor.PointOf(lineStart);
        if (values is not null)
        {
            popups.OnLayer(hoverCard);
            PlaceValues(values, new Point(point.X, anchor.Y));
        }
        else if (over is not null)
        {
            popups.Place(hoverCard, over.Editor, over.Layer, new Point(point.X, anchor.Y), above: false);
        }
        else
        {
            popups.Place(hoverCard, new Point(point.X, anchor.Y), above: false);
        }
        if (debuggerValue is not null)
        {
            // Read at a stop, the value goes with it: the program going on, another program's tab chosen, the folder left.
            hoverGone = debuggerValue.Gone.Register(Hide);
        }
    }

    /// <summary>Puts a card of a value's rows under the line at <paramref name="at"/> (its top), or over it when the room
    /// below holds fewer rows than the room above, its rows scrolling past what that room holds; held to the editor's
    /// width, where its rows give way (<see cref="PopupPlacement.Widest"/>).</summary>
    private void PlaceValues(Debugging.VariablesList values, Point at)
    {
        at = popups.InLayer(at);
        Rect held = popups.Held;
        double below = held.Bottom - (at.Y + Editor.LineHeight + 2) - 8;
        double over = at.Y - held.Top - 2 - 8;
        bool above = below < ListRow.Height * 8 && over > below;
        hoverCard.AddValues(values, above ? over : below, held.Height);
        hoverCard.Within(popups.Widest);
        valuesPlace = (at, above);
        hoverCard.Measure(Size.Infinity);
        Size size = hoverCard.DesiredSize;
        Avalonia.Controls.Canvas.SetLeft(hoverCard, Math.Clamp(at.X - 4, held.Left, Math.Max(held.Left, held.Right - size.Width - 8)));
        Avalonia.Controls.Canvas.SetTop(hoverCard, ValuesTop(at, above, size.Height));
    }

    /// <summary>A card of a value that grew as its rows opened stays inside the editor, and over its line when it stands over it.</summary>
    private void KeepValuesPlaced()
    {
        if (valuesPlace is not { } place || !hoverCard.IsVisible || hoverCard.Values is null)
        {
            return;
        }
        Size size = hoverCard.Bounds.Size;
        Rect held = popups.Held;
        double x = Math.Clamp(Avalonia.Controls.Canvas.GetLeft(hoverCard), held.Left, Math.Max(held.Left, held.Right - size.Width - 8));
        if (x != Avalonia.Controls.Canvas.GetLeft(hoverCard))
        {
            Avalonia.Controls.Canvas.SetLeft(hoverCard, x);
        }
        double y = ValuesTop(place.Line, place.Above, size.Height);
        if (y != Avalonia.Controls.Canvas.GetTop(hoverCard))
        {
            Avalonia.Controls.Canvas.SetTop(hoverCard, y);
        }
    }

    /// <summary>Where a card of a value's rows stands, over or under the line whose top is <paramref name="line"/>: in an
    /// editor too short for its least rows, moved in to end at its edge, over some of the line.</summary>
    private double ValuesTop(Point line, bool above, double height) => above
        ? Math.Max(popups.Held.Top, line.Y - height - 2)
        : Math.Max(popups.Held.Top, Math.Min(line.Y + Editor.LineHeight + 2, popups.Held.Bottom - height));

    /// <summary>The pointer left a card, or its word, or the keys left the card: it goes after a pause unless the pointer
    /// comes back to it or it is in use.</summary>
    private void Leave()
    {
        if (hoverCard.IsVisible)
        {
            leaving.Restart();
        }
    }

    /// <summary>Lets go of the value the card showed: its pause before going, its stop, its place, and the diff's side
    /// it showed over.</summary>
    private void ForgetValues()
    {
        declarationOrigin = null;
        hoverCard.OpenDeclaration = null;
        side = null;
        leaving.Stop();
        hoverGone.Dispose();
        hoverGone = default;
        valuesPlace = null;
    }

    /// <summary>The card for an inlay hint the pointer rests on: what the server says of it (a type's description, a
    /// parameter's), or none when it says nothing.</summary>
    public async Task ShowHint(InlayHint hint, Point point)
    {
        hovering?.Cancel();
        CancellationTokenSource cancel = new();
        hovering = cancel;
        ForgetValues();
        hoverCard.Clear();
        if (hint.Data is not LspInlayHint server || context.Current?.Lsp is not { } lsp)
        {
            hoverCard.IsVisible = false;
            return;
        }
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            string tooltip = await lsp.InlayHintTooltipAsync(server, cancel.Token);
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            if (tooltip.Length == 0 || completing())
            {
                hoverCard.IsVisible = false;
                return;
            }
            hoverCard.AddMarkdown(tooltip);
            hoverCard.IsVisible = true;
            TextSnapshot text = Editor.Document.Snapshot;
            int lineStart = text.GetLineStart(text.GetLineFromOffset(Math.Min(hint.Offset, text.Length)));
            popups.Place(hoverCard, new Point(point.X, Editor.PointOf(lineStart).Y), above: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (LanguageServerFailed)
        {
            // A server that no longer has the hint (the file changed since) shows no card for it.
            hoverCard.IsVisible = false;
        }
    }

    /// <summary>What the card says of a line-end annotation the pointer rests on (a running game's value), added to the card;
    /// false where it says nothing of it.</summary>
    public Func<LineAnnotation, IHoverCard, bool>? AnnotationCard { get; set; }

    /// <summary>The card for a line-end annotation the pointer rests on (<see cref="AnnotationCard"/>), hung under its line;
    /// none where nothing is said of it.</summary>
    public void ShowAnnotation(LineAnnotation annotation, Point point)
    {
        hovering?.Cancel();
        ForgetValues();
        hoverCard.Clear();
        TextSnapshot text = Editor.Document.Snapshot;
        if (completing() || annotation.Line >= text.LineCount || AnnotationCard?.Invoke(annotation, hoverCard) != true)
        {
            hoverCard.IsVisible = false;
            return;
        }
        hoverCard.IsVisible = true;
        popups.Place(hoverCard, new Point(point.X, Editor.PointOf(text.GetLineStart(annotation.Line)).Y), above: false);
    }

    /// <summary>The card for a word of a row above a line the pointer rests on (<see cref="AboveWords"/>), hung under the
    /// row; none where it says nothing.</summary>
    public void ShowAbove(RowAboveWord word, Point point)
    {
        hovering?.Cancel();
        ForgetValues();
        hoverCard.Clear();
        if (AboveWords?.Invoke(word) is not { Length: > 0 } words || completing())
        {
            hoverCard.IsVisible = false;
            return;
        }
        hoverCard.AddText(words);
        hoverCard.IsVisible = true;
        popups.Place(hoverCard, new Point(point.X, point.Y - (Editor.LineHeight / 2)), above: false);
    }

    /// <summary>The card for what the gutter shows beside a line the pointer rests on (<see cref="GutterWords"/>), hung under
    /// the line; none where it says nothing.</summary>
    public void ShowGutter(int line, Point point)
    {
        hovering?.Cancel();
        ForgetValues();
        hoverCard.Clear();
        TextSnapshot text = Editor.Document.Snapshot;
        if (GutterWords?.Invoke(line) is not { Count: > 0 } words || completing() || line >= text.LineCount)
        {
            hoverCard.IsVisible = false;
            return;
        }
        hoverCard.AddText(string.Join('\n', words));
        hoverCard.IsVisible = true;
        popups.Place(hoverCard, new Point(point.X, Editor.PointOf(text.GetLineStart(line)).Y), above: false);
    }

    /// <summary>The card gone, the keys it had given back to the editor (<see cref="PopupPlacement.Add"/>).</summary>
    public void Hide()
    {
        hovering?.Cancel();
        ForgetValues();
        hoverCard.IsVisible = false;
        if (hoverCard.Values is not null)
        {
            hoverCard.Clear();
        }
    }

    /// <summary>A diff's side that is an open file's own text, the card shown over it: the side, its file, and the layer
    /// over the diff's own window (null for a diff among the editors).</summary>
    private sealed record DiffSide(CodeEditor Editor, Workspace.OpenDocument File, Avalonia.Controls.Canvas? Layer);

    /// <summary>The card for what a language server says of an offset (its symbol's description), added to the card;
    /// none where the server fails, the card then saying what the markers there say.</summary>
    public static async Task ServerHoverAsync(LspDocument lsp, IHoverCard card, int offset, CancellationToken cancel)
    {
        try
        {
            Hover? hover = await lsp.HoverAsync(offset, cancel);
            cancel.ThrowIfCancellationRequested();
            if (hover is not null)
            {
                card.AddHover(hover, csharp: lsp.LanguageId == LanguageIds.CSharp);
            }
        }
        catch (LanguageServerFailed)
        {
            // The card says what the markers there say, without the symbol's description.
        }
    }
}
