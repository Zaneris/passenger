using Avalonia;
using Avalonia.Input;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>The card when the pointer rests, or Quick Documentation at the caret: the problems there, then a paused
/// program's value (its rows to open, the card taking the pointer and the keys) or what the file's language says of the
/// symbol; an inlay hint's own words over a hint.</summary>
internal sealed class QuickInfo
{
    private readonly AssistContext context;
    private readonly PopupPlacement popups;
    private readonly Func<bool> completing;
    private readonly InfoCard hoverCard = new();
    private CancellationTokenSource? hovering;

    /// <summary>The pause after the pointer leaves a card of a paused program's value (its word, or the card itself) before
    /// the card goes: time to reach the card from the word, on which it stays while the pointer or the keys are on it.</summary>
    private readonly Debounce leaving;

    /// <summary>Takes the card away once the stop its value was read at is gone.</summary>
    private CancellationTokenRegistration hoverGone;

    /// <summary>Where a card of a value was placed: the line's top it hangs from, in the layer, and whether it stands over
    /// the line, so a card that grows as its rows open stays beside the line and inside the editor.</summary>
    private (Point Line, bool Above)? valuesPlace;

    /// <summary>The hover card on the layer, which never shows over an open completion list (<paramref name="completing"/>).</summary>
    public QuickInfo(AssistContext context, PopupPlacement popups, Func<bool> completing)
    {
        this.context = context;
        this.popups = popups;
        this.completing = completing;
        popups.Add(hoverCard);
        leaving = new Debounce(TimeSpan.FromMilliseconds(300), () =>
        {
            if (!HoverHeld)
            {
                Hide();
            }
            return Task.CompletedTask;
        });
        hoverCard.PointerEntered += (_, _) => leaving.Stop();
        hoverCard.PointerExited += (_, _) => LeaveValues();
        hoverCard.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsKeyboardFocusWithinProperty && e.NewValue is false)
            {
                LeaveValues();
            }
        };
        hoverCard.SizeChanged += (_, _) => KeepValuesPlaced();
        hoverCard.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && hoverCard.Values is not null)
            {
                Hide();
                e.Handled = true;
            }
        };
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

    /// <summary>Whether a card of a value is in use: it has the keys, its menu is open, or a value is being typed in it.</summary>
    public bool ValuesInUse => hoverCard.IsVisible && hoverCard.Values is { } values && (hoverCard.IsKeyboardFocusWithin || values.IsInUse);

    /// <summary>Whether a card of a value stays: in use, or the pointer is on it.</summary>
    private bool HoverHeld => ValuesInUse || (hoverCard.IsVisible && hoverCard.Values is not null && hoverCard.IsPointerOver);

    /// <summary>The pointer left the word the card is of: a value's card goes after a pause, any other at once.</summary>
    public void PointerLeft()
    {
        if (hoverCard.IsVisible && hoverCard.Values is not null)
        {
            LeaveValues();
        }
        else
        {
            Hide();
        }
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
        IdeErrors.Observe(Show(context.Caret, Editor.PointOf(context.Caret)));
    }

    public async Task Show(int offset, Point point)
    {
        using (BackgroundWork.Scope())
        {
            await ShowCore(offset, point);
        }
    }

    private async Task ShowCore(int offset, Point point)
    {
        hovering?.Cancel();
        CancellationTokenSource cancel = new();
        hovering = cancel;
        ForgetValues();
        hoverCard.Clear();
        foreach (Marker marker in Editor.MarkersAt(offset))
        {
            hoverCard.AddText(marker.Message, marker.Kind switch
            {
                MarkerKind.Error => InfoCard.Brush(TokenKeys.Error),
                MarkerKind.Warning => InfoCard.Brush(TokenKeys.Warning),
                _ => null,
            });
        }
        DebuggerHover? debuggerValue = DebuggerValue is { } value ? await value(offset, cancel.Token) : null;
        if (cancel.IsCancellationRequested)
        {
            return;
        }
        Debugging.VariablesList? values = null;
        if (debuggerValue is not null)
        {
            if (debuggerValue.Gone.IsCancellationRequested)
            {
                hoverCard.IsVisible = false;
                return;
            }
            if (debuggerValue.Problem is { } problem)
            {
                hoverCard.AddText(problem, InfoCard.Brush(TokenKeys.Error));
            }
            if (debuggerValue.Values is { } rows)
            {
                values = rows;
                // The menu closed, the card goes unless the pointer or the keys are still on it.
                rows.MenuClosed += (_, _) =>
                {
                    if (hoverCard.Values == rows)
                    {
                        LeaveValues();
                    }
                };
            }
            else if (debuggerValue.Code.Length > 0)
            {
                hoverCard.AddCode(debuggerValue.Code);
            }
        }
        else
        {
            try
            {
                await context.Languages.HoverAsync(hoverCard, offset, cancel.Token);
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
        hoverCard.IsVisible = true;
        TextSnapshot text = Editor.Document.Snapshot;
        int lineStart = text.GetLineStart(text.GetLineFromOffset(Math.Min(offset, text.Length)));
        Point anchor = Editor.PointOf(lineStart);
        if (values is not null)
        {
            PlaceValues(values, new Point(point.X, anchor.Y));
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
    /// below holds fewer rows than the room above, its rows scrolling past what that room holds.</summary>
    private void PlaceValues(Debugging.VariablesList values, Point at)
    {
        at = popups.InLayer(at);
        double lineHeight = Editor.LineHeight;
        double below = popups.Room.Height - (at.Y + lineHeight + 2) - 8;
        double over = at.Y - 2 - 8;
        bool above = below < ListRow.Height * 8 && over > below;
        hoverCard.AddValues(values, above ? over : below);
        valuesPlace = (at, above);
        hoverCard.Measure(Size.Infinity);
        Size size = hoverCard.DesiredSize;
        Avalonia.Controls.Canvas.SetLeft(hoverCard, Math.Clamp(at.X - 4, 0, Math.Max(0, popups.Room.Width - size.Width - 8)));
        Avalonia.Controls.Canvas.SetTop(hoverCard, above ? Math.Max(0, at.Y - size.Height - 2) : at.Y + lineHeight + 2);
    }

    /// <summary>A card of a value that grew as its rows opened stays inside the editor, and over its line when it stands over it.</summary>
    private void KeepValuesPlaced()
    {
        if (valuesPlace is not { } place || !hoverCard.IsVisible || hoverCard.Values is null)
        {
            return;
        }
        Size size = hoverCard.Bounds.Size;
        double x = Math.Clamp(Avalonia.Controls.Canvas.GetLeft(hoverCard), 0, Math.Max(0, popups.Room.Width - size.Width - 8));
        if (x != Avalonia.Controls.Canvas.GetLeft(hoverCard))
        {
            Avalonia.Controls.Canvas.SetLeft(hoverCard, x);
        }
        double y = Math.Max(0, place.Line.Y - size.Height - 2);
        if (place.Above && y != Avalonia.Controls.Canvas.GetTop(hoverCard))
        {
            Avalonia.Controls.Canvas.SetTop(hoverCard, y);
        }
    }

    /// <summary>The pointer left a card of a value, or its word, or the keys left the card: it goes after a pause unless
    /// the pointer comes back to it or it is in use.</summary>
    private void LeaveValues()
    {
        if (hoverCard.IsVisible && hoverCard.Values is not null)
        {
            leaving.Restart();
        }
    }

    /// <summary>Lets go of the value the card showed: its pause before going, its stop, its place.</summary>
    private void ForgetValues()
    {
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

    public void Hide()
    {
        hovering?.Cancel();
        bool focused = hoverCard.IsKeyboardFocusWithin;
        ForgetValues();
        hoverCard.IsVisible = false;
        if (hoverCard.Values is not null)
        {
            hoverCard.Clear();
        }
        if (focused)
        {
            Editor.Focus();
        }
    }

    /// <summary>The card for what a language server says of an offset (its symbol's description), added to the card;
    /// none where the server fails, the card then saying what the markers there say.</summary>
    public static async Task ServerHoverAsync(LspDocument lsp, InfoCard card, int offset, CancellationToken cancel)
    {
        try
        {
            Hover? hover = await lsp.HoverAsync(offset, cancel);
            cancel.ThrowIfCancellationRequested();
            if (hover is not null && Shapes.Text(hover.Contents) is { Length: > 0 } markdown)
            {
                card.AddMarkdown(markdown);
            }
        }
        catch (LanguageServerFailed)
        {
            // The card says what the markers there say, without the symbol's description.
        }
    }
}
