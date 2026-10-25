using Avalonia;
using Avalonia.Controls;
using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>Where the code assistance's popups sit: on the layer over the active editor's window, placed at a line of
/// the editor and kept inside it, and the one list of choices or rename box open at a time, whose answer is carried out
/// as background work.</summary>
internal sealed class PopupPlacement
{
    private readonly AssistContext context;
    private readonly List<Control> cards = [];
    private Canvas layer;
    private Control? modal;

    /// <summary>What undoes what the open modal showed in the editor, run as it closes however it closes.</summary>
    private Action? modalClosed;

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

    /// <summary>What runs as the open modal closes, however it closes: what lets go of what it showed or followed.</summary>
    public void WhenClosed(Action closed) => modalClosed = closed;

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
        CloseModal();
        modal = popup;
        layer.Children.Add(popup);
        Place(popup, offset, above: false);
        popup.Done += (_, choice) => IdeErrors.Observe(Carried(what, () => done(choice)));
    }

    public void ShowModal(RenameBox box, int offset, Func<string?, Task> done)
    {
        CloseModal();
        modal = box;
        layer.Children.Add(box);
        Place(box, offset, above: true);
        box.Done += (_, name) => IdeErrors.Observe(Carried("Rename", () => done(name)));
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
            layer.Children.Remove(modal);
            modal = null;
            Editor.Focus();
        }
        Action? closed = modalClosed;
        modalClosed = null;
        closed?.Invoke();
    }
}
