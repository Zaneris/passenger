using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>Where the code assistance's popups sit: on the layer over the active editor's window, placed at a line of
/// the editor and kept inside it, and the one list of choices or rename box open at a time, which a press in its window
/// outside it dismisses and whose answer is carried out as background work.</summary>
internal sealed class PopupPlacement
{
    private readonly AssistContext context;
    private readonly List<Control> cards = [];
    private Canvas layer;
    private Control? modal;

    /// <summary>What undoes what the open modal showed in the editor, run as it closes however it closes.</summary>
    private Action? modalClosed;

    /// <summary>The open modal answered as dismissed, its answer null; null with none open.</summary>
    private Action? dismissed;

    /// <summary>The open modal's window's presses, heard while it is open.</summary>
    private IDisposable? presses;

    public PopupPlacement(AssistContext context, Canvas layer)
    {
        this.context = context;
        this.layer = layer;
    }

    private Passenger.Editor.CodeEditor Editor => context.Editor;

    /// <summary>The size of the layer the popups are placed in.</summary>
    public Size Room => layer.Bounds.Size;

    /// <summary>The list of choices or the rename box open, null with none.</summary>
    public Control? Modal => modal;

    /// <summary>What the open list of choices (fixes, refactorings, definitions) offers, by its words; empty with none open.</summary>
    public IReadOnlyList<string> Offered => layer.Children.OfType<ChoicePopup>().LastOrDefault()?.Titles ?? [];

    /// <summary>What the list of choices shown says beside each of <see cref="Offered"/>.</summary>
    public IReadOnlyList<string> OfferedDetails => layer.Children.OfType<ChoicePopup>().LastOrDefault()?.Details ?? [];

    /// <summary>What the open list of choices is titled ("Select expression to parenthesize"); null with none open.</summary>
    public string? OfferedTitle => layer.Children.OfType<ChoicePopup>().LastOrDefault()?.Title;

    /// <summary>A card or list that stays on the layer, hidden until it is shown, and goes with it to another window's.</summary>
    public void Add(Control popup)
    {
        popup.IsVisible = false;
        cards.Add(popup);
        layer.Children.Add(popup);
    }

    /// <summary>Whether the modal last asked for was refused to an agent's action, answered as dismissed without showing.</summary>
    private bool refused;

    /// <summary>What runs as the open modal closes, however it closes: what lets go of what it showed or followed; at once
    /// for one refused.</summary>
    public void WhenClosed(Action closed)
    {
        if (refused)
        {
            closed();
            return;
        }
        modalClosed = closed;
    }

    /// <summary>A point of the editor's in the layer's coordinates, the layer spanning every split.</summary>
    public Point InLayer(Point at) => Editor.TranslatePoint(at, layer) ?? at;

    public void Place(Control popup, int offset, bool above) => Place(popup, Editor.PointOf(offset), above);

    /// <summary>Puts a popup under the line at <paramref name="at"/> (its top), or over it when asked
    /// or when there is no room below, kept inside the editor.</summary>
    public void Place(Control popup, Point at, bool above)
    {
        at = InLayer(at);
        popup.Measure(Size.Infinity);
        Size size = popup.DesiredSize;
        Size room = layer.Bounds.Size;
        double lineHeight = Editor.LineHeight;
        double x = Math.Clamp(at.X - 4, 0, Math.Max(0, room.Width - size.Width - 8));
        double below = at.Y + lineHeight + 2;
        double over = at.Y - size.Height - 2;
        double y = above ? (over >= 0 ? over : below) : (below + size.Height <= room.Height || over < 0 ? below : over);
        Canvas.SetLeft(popup, x);
        Canvas.SetTop(popup, y);
    }

    /// <summary>A card to the right of a popup, at its top; to its left when the right has no room.</summary>
    public void PlaceBeside(Control card, Control beside)
    {
        card.Measure(Size.Infinity);
        double left = Canvas.GetLeft(beside);
        double width = card.DesiredSize.Width;
        // A popup just shown has no bounds until the next layout: its measure says how wide it will be.
        if (beside.Bounds.Width == 0)
        {
            beside.Measure(Size.Infinity);
        }
        double x = left + (beside.Bounds.Width > 0 ? beside.Bounds.Width : beside.DesiredSize.Width) + 4;
        if (x + width > layer.Bounds.Width && left - width - 4 >= 0)
        {
            x = left - width - 4;
        }
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, Canvas.GetTop(beside));
    }

    /// <summary>The popups go to the layer over the active editor's window, an editor window's when it has the active
    /// group, since a popup is placed in its layer's coordinates and one window's cannot reach another's.</summary>
    public void FollowLayer()
    {
        if (context.Editors.AssistLayer == layer)
        {
            return;
        }
        foreach (Control popup in cards)
        {
            layer.Children.Remove(popup);
            context.Editors.AssistLayer.Children.Add(popup);
        }
        layer = context.Editors.AssistLayer;
    }

    public void ShowModal(ChoicePopup popup, int offset, string what, Func<Choice?, Task> done)
    {
        if (Refused(what, () => done(null)))
        {
            return;
        }
        Open(popup, () => IdeErrors.Observe(Carried(what, () => done(null))));
        Place(popup, offset, above: false);
        popup.Done += (_, choice) => IdeErrors.Observe(Carried(what, () => done(choice)));
    }

    public void ShowModal(RenameBox box, int offset, Func<string?, Task> done)
    {
        if (Refused("Rename", () => done(null)))
        {
            return;
        }
        Open(box, () => IdeErrors.Observe(Carried("Rename", () => done(null))));
        Place(box, offset, above: true);
        box.Done += (_, name) => IdeErrors.Observe(Carried("Rename", () => done(name)));
    }

    /// <summary>Whether a modal an agent's action asks for, which would take the person's keys, is refused
    /// (<see cref="AgentAsks.Refused"/>): answered at once as dismissed, as Escape answers it, any modal open left open.</summary>
    private bool Refused(string what, Func<Task> dismissed)
    {
        refused = AgentAsks.Refused();
        if (refused)
        {
            IdeErrors.Observe(Carried(what, dismissed, closeModal: false));
        }
        return refused;
    }

    /// <summary>A modal on the layer in place of any open, its window's presses heard.</summary>
    private void Open(Control popup, Action dismiss)
    {
        CloseModal();
        modal = popup;
        dismissed = dismiss;
        layer.Children.Add(popup);
        presses = TopLevel.GetTopLevel(layer)?.AddDisposableHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Answers the modal open in a window as dismissed, as Escape there does, wherever the keys are in it; false
    /// with none open there.</summary>
    public bool Dismiss(TopLevel window)
    {
        if (modal is null || dismissed is not { } dismiss || TopLevel.GetTopLevel(modal) != window)
        {
            return false;
        }
        dismiss();
        return true;
    }

    /// <summary>A press in the modal's window outside it dismisses it, then goes on to what it was made on: the caret
    /// placed, a tool window's row chosen. A press over the modal is its own, though a menu's layer over the window takes it.</summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (modal is { } open && !(e.Source is Visual pressed && (pressed == open || open.IsVisualAncestorOf(pressed)))
            && !new Rect(open.Bounds.Size).Contains(e.GetPosition(open)))
        {
            dismissed?.Invoke();
        }
    }

    /// <summary>What a modal's answer does (a fix chosen, a new name), carried out as background work. A failure of the
    /// server's, or an edit the disk refused, is said in the status bar, should the work not say it itself; anything else
    /// is the IDE's own bug, reported as an IDE error rather than lost in the event's handler.</summary>
    public async Task Carried(string what, Func<Task> done, bool closeModal = true)
    {
        if (closeModal)
        {
            CloseModal();
        }
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            await done();
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed(what, failed);
        }
        catch (WorkspaceEditFailed failed)
        {
            context.Say($"{what} failed: {failed.Message}");
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    public void CloseModal()
    {
        if (modal is not null)
        {
            bool keysElsewhere = KeysElsewhere(modal);
            layer.Children.Remove(modal);
            modal = null;
            if (!keysElsewhere)
            {
                Editor.Focus();
            }
        }
        dismissed = null;
        presses?.Dispose();
        presses = null;
        Action? closed = modalClosed;
        modalClosed = null;
        closed?.Invoke();
    }

    /// <summary>Whether an element of the modal's window other than the modal has the keys, which keep them as it closes;
    /// those the modal had go back to the editor.</summary>
    private static bool KeysElsewhere(Control open) =>
        TopLevel.GetTopLevel(open) is { } window && window.FocusManager?.GetFocusedElement() is Visual focused
        && focused != window && focused != open && !open.IsVisualAncestorOf(focused);
}
