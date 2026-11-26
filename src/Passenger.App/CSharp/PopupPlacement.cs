using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>Where the code assistance's popups sit: on the layer over the active editor's window, placed at a line of
/// the editor and kept inside it, and the one list of choices or rename box open at a time, which a press in its window
/// outside it dismisses and whose answer is carried out as background work. Each is what a screen reader meets it as
/// (<see cref="IHeardPopup"/>), and what it says reaches the reader through the window's <see cref="Announcer"/>
/// (<see cref="Tell"/>).</summary>
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

    /// <summary>Where a popup placed at a line of the active editor is held, in its layer's coordinates (<see cref="HeldIn"/>).</summary>
    public Rect Held => HeldIn(Editor, layer);

    /// <summary>The widest a popup placed at a line is shown whole in the editor (<see cref="Place(Control, Point, bool)"/>).</summary>
    public double Widest => Math.Max(0, layer.Bounds.Width - RightEdge);

    /// <summary>The list of choices or the rename box open, null with none.</summary>
    public Control? Modal => modal;

    /// <summary>What the open list of choices (fixes, refactorings, definitions) offers, by its words; empty with none open.</summary>
    public IReadOnlyList<string> Offered => layer.Children.OfType<ChoicePopup>().LastOrDefault()?.Titles ?? [];

    /// <summary>What the list of choices shown says beside each of <see cref="Offered"/>.</summary>
    public IReadOnlyList<string> OfferedDetails => layer.Children.OfType<ChoicePopup>().LastOrDefault()?.Details ?? [];

    /// <summary>What the open list of choices is titled ("Select expression to parenthesize"); null with none open.</summary>
    public string? OfferedTitle => layer.Children.OfType<ChoicePopup>().LastOrDefault()?.Title;

    /// <summary>A card or list that stays on the layer, hidden until it is shown, and goes with it to another window's; a
    /// card the person moved into goes at Escape there (<paramref name="escaped"/>, as Escape in the editor takes it), and
    /// hidden while it has the keys, it gives them back to the editor.</summary>
    public void Add(Control popup, Action? escaped = null)
    {
        popup.IsVisible = false;
        Heard(popup);
        cards.Add(popup);
        layer.Children.Add(popup);
        // Keys lost while it is hidden were lost by its going, once the focus has settled nowhere.
        popup.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsKeyboardFocusWithinProperty && e.NewValue is false && !popup.IsVisible)
            {
                UiThread.Post(() =>
                {
                    if (!popup.IsVisible && TopLevel.GetTopLevel(popup)?.FocusManager?.GetFocusedElement() is null)
                    {
                        Editor.Focus();
                    }
                });
            }
        };
        if (escaped is not null)
        {
            popup.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
                {
                    escaped();
                    e.Handled = true;
                }
            };
        }
    }

    /// <summary>A popup as a screen reader meets it: the role and name it gives (<see cref="IHeardPopup"/>), set only where
    /// they differ, so placing it again costs nothing.</summary>
    private static void Heard(Control popup)
    {
        if (popup is not IHeardPopup heard)
        {
            return;
        }
        if (AutomationProperties.GetControlTypeOverride(popup) != heard.Role)
        {
            AutomationProperties.SetControlTypeOverride(popup, heard.Role);
        }
        if (AutomationProperties.GetName(popup) != heard.Called)
        {
            AutomationProperties.SetName(popup, heard.Called);
        }
    }

    /// <summary>Tells a screen reader what a popup says (<see cref="Announcer.Tell"/>), words about one thing that changes
    /// (<paramref name="about"/>: a list's choice) in place of those it told before and not yet heard.</summary>
    public void Tell(string words, Urgency urgency, object about) => Announcer.Of(TopLevel.GetTopLevel(layer))?.Tell(words, urgency, about);

    /// <summary>A list's choice as it is said: its words, its kind where it has one, and where it stands in the list.</summary>
    public static string Chosen(string words, string? kind, int at, int count) =>
        string.IsNullOrEmpty(kind) ? $"{words}, item {at + 1} of {Numbers.Said(count)}" : $"{words}, {kind}, item {at + 1} of {Numbers.Said(count)}";

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

    /// <summary>Puts a popup under the line at <paramref name="at"/> (its top), or over it when asked or when there is no
    /// room below, kept inside the editor, below the bars over it (<see cref="HeldIn"/>). A card, a list of choices, Rename's
    /// box or the colour picker (<see cref="IHeldToEditor"/>) is held to the editor's width (<see cref="Widest"/>); one that
    /// fits neither under nor over its line goes where there is more room, lowered to it where it gives way in height
    /// (<see cref="IGivesWayInHeight"/>), and where even lowered it cannot show whole, over the tool windows below and above
    /// the editor (the window's <c>LineOverflow</c>); else moved in as far as it must, though it then hides some of the line.</summary>
    public void Place(Control popup, Point at, bool above)
    {
        MainWindow? window = TopLevel.GetTopLevel(layer) as MainWindow;
        if (cards.Contains(popup) || (window is not null && popup.Parent == window.LineOverflow))
        {
            OnLayer(popup);
        }
        if (!PlaceOn(popup, Editor, layer, at, above) && popup is IGivesWayInHeight
            && window?.LineOverflow is { } overflow && layer.TranslatePoint(default, overflow) is { } origin)
        {
            OnLayer(popup, overflow);
            PlaceOn(popup, Editor, overflow, at, above, new Rect(origin.X, 0, layer.Bounds.Width, overflow.Bounds.Height));
        }
    }

    /// <summary>Puts a card under a line of an editor no group shows, a diff's side, as at the active editor's
    /// (<see cref="Place(Control, Point, bool)"/>): on <paramref name="onto"/>, the layer over a diff's own window, else
    /// over the editors.</summary>
    public void Place(Control popup, Passenger.Editor.CodeEditor editor, Canvas? onto, Point at, bool above)
    {
        onto ??= layer;
        OnLayer(popup, onto);
        PlaceOn(popup, editor, onto, at, above);
    }

    /// <summary>A card put back on the layer over the editors (<paramref name="onto"/> null), or onto another window's, from
    /// the one it stands on: a card shown over a diff window's side comes back for the editors'.</summary>
    public void OnLayer(Control popup, Canvas? onto = null)
    {
        onto ??= layer;
        if (popup.Parent != onto)
        {
            (popup.Parent as Panel)?.Children.Remove(popup);
            onto.Children.Add(popup);
        }
    }

    /// <summary>Places a popup at a line on <paramref name="onto"/>, held to <paramref name="held"/> (the editor's rows by
    /// default, <see cref="HeldIn"/>); whether it shows whole there, under or over the line.</summary>
    private bool PlaceOn(Control popup, Passenger.Editor.CodeEditor editor, Canvas onto, Point at, bool above, Rect? held = null)
    {
        Heard(popup);
        at = editor.TranslatePoint(at, onto) ?? at;
        Rect room = held ?? HeldIn(editor, onto);
        (popup as IHeldToEditor)?.Within(Math.Max(0, room.Width - RightEdge));
        (popup as IGivesWayInHeight)?.WithinHeight(double.PositiveInfinity);
        Size size = Measured(popup);
        double lineHeight = editor.LineHeight;
        double below = at.Y + lineHeight + 2;
        double over = at.Y - size.Height - 2;
        if (popup is IGivesWayInHeight lowered && over < room.Top && below + size.Height > room.Bottom)
        {
            lowered.WithinHeight(Math.Max(room.Bottom - below, at.Y - 2 - room.Top));
            size = Measured(popup);
            over = at.Y - size.Height - 2;
        }
        bool fitsOver = over >= room.Top;
        bool fitsUnder = below + size.Height <= room.Bottom;
        double x = Math.Clamp(at.X - 4, room.Left, Math.Max(room.Left, room.Right - size.Width - RightEdge));
        double y = above ? (fitsOver ? over : below) : (fitsUnder || !fitsOver ? below : over);
        if (popup is IHeldToEditor && !fitsOver && !fitsUnder)
        {
            y = Math.Clamp(room.Bottom - below >= at.Y - room.Top ? below : over, room.Top, Math.Max(room.Top, room.Bottom - size.Height));
        }
        Canvas.SetLeft(popup, x);
        Canvas.SetTop(popup, y);
        lines.AddOrUpdate(popup, new StrongBox<double>(at.Y));
        return fitsOver || fitsUnder;
    }

    /// <summary>Where a popup placed at a line of an editor is held, in the coordinates of the layer it is placed on: across
    /// the layer, and down only the editor's own rows, never over the bars above it (its group's tabs, the navigation bar,
    /// a banner, Find) nor below it; the whole layer where the editor is not under it.</summary>
    private static Rect HeldIn(Passenger.Editor.CodeEditor editor, Canvas onto)
    {
        Rect whole = new(onto.Bounds.Size);
        if (editor.TranslatePoint(default, onto) is not { } top)
        {
            return whole;
        }
        double from = Math.Clamp(top.Y, 0, whole.Height);
        return new Rect(0, from, whole.Width, Math.Clamp(top.Y + editor.Bounds.Height, from, whole.Height) - from);
    }

    /// <summary>The size a popup takes as it is now held, measured again.</summary>
    private static Size Measured(Control popup)
    {
        // A list shown again measures as its last rows did until a layout pass measures the rows it shows now.
        if (popup.IsAttachedToVisualTree())
        {
            popup.UpdateLayout();
        }
        else
        {
            popup.Measure(Size.Infinity);
        }
        return popup.DesiredSize;
    }

    /// <summary>A card beside a popup, at its top: to its right where it fits there whole, else to the side with more room,
    /// narrowed to that room so its words wrap rather than run past the editor's edge, which cuts what the layer shows.
    /// Where neither side has room enough for words to read, above or below the popup and the line it was placed at,
    /// where there is more room, as wide as the editor allows; raised where it would end past the editor's foot.</summary>
    public void PlaceBeside(InfoCard card, Control beside)
    {
        card.Within(double.PositiveInfinity);
        card.Measure(Size.Infinity);
        double width = card.DesiredSize.Width;
        // A popup just shown has no bounds until the next layout: its measure says how big it will be.
        if (beside.Bounds.Width == 0)
        {
            beside.Measure(Size.Infinity);
        }
        Size popup = beside.Bounds.Width > 0 ? beside.Bounds.Size : beside.DesiredSize;
        Rect room = HeldIn(Editor, layer).Deflate(Edge);
        double left = Canvas.GetLeft(beside);
        double top = Canvas.GetTop(beside);
        double toRight = room.Right - (left + popup.Width + Gap);
        double toLeft = left - Gap - room.X;
        bool right = width <= toRight || (width > toLeft && toRight >= toLeft);
        double side = right ? toRight : toLeft;
        double x;
        double y;
        if (width <= side || side >= LeastBeside)
        {
            card.Within(side);
            card.Measure(Size.Infinity);
            x = right ? left + popup.Width + Gap : left - Gap - card.DesiredSize.Width;
            y = top;
        }
        else
        {
            card.Within(room.Width);
            card.Measure(Size.Infinity);
            // Over or under the popup and its line together, so the card hides neither the choices nor what is typed.
            (double lineTop, double lineBottom) = lines.TryGetValue(beside, out StrongBox<double>? line) ? (line.Value, line.Value + Editor.LineHeight) : (top, top);
            double bandTop = Math.Min(top, lineTop);
            double bandBottom = Math.Max(top + popup.Height, lineBottom);
            double under = room.Bottom - (bandBottom + Gap);
            double over = bandTop - Gap - room.Y;
            x = Math.Clamp(left, room.X, Math.Max(room.X, room.Right - card.DesiredSize.Width));
            y = card.DesiredSize.Height <= under || under >= over ? bandBottom + Gap : bandTop - Gap - card.DesiredSize.Height;
        }
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, Math.Max(room.Y, Math.Min(y, room.Bottom - card.DesiredSize.Height)));
    }

    /// <summary>How far a popup placed at a line keeps from the editor's right edge.</summary>
    private const double RightEdge = 8;

    /// <summary>How far a card sits from the popup it describes, and from the editor's edge.</summary>
    private const double Gap = Gaps.Tight;
    private const double Edge = 4;

    /// <summary>The narrowest a card beside a popup is made, its words a few to a line; narrower, it goes over or under it.</summary>
    private const double LeastBeside = 200;

    /// <summary>The room a card beside a popup against the editor's edge takes on the popup's other side, at its narrowest:
    /// the card, its gap from the popup and its own from the editor's edge.</summary>
    public const double BesideRoom = LeastBeside + Gap + Edge;

    /// <summary>The top of the line each popup placed at a line was placed at, in the layer's coordinates, kept no longer
    /// than the popup.</summary>
    private readonly ConditionalWeakTable<Control, StrongBox<double>> lines = [];

    /// <summary>The popups go to the layer over the active editor's window, an editor window's when it has the active
    /// group, since a popup is placed in its layer's coordinates and one window's cannot reach another's.</summary>
    public void FollowLayer()
    {
        if (context.Editors.AssistLayer == layer)
        {
            return;
        }
        layer = context.Editors.AssistLayer;
        foreach (Control popup in cards)
        {
            OnLayer(popup);
        }
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
        // Its choice is said as completion's is: by its title as it opens, and at once as the keys move it.
        if (popup.ChosenAt >= 0)
        {
            Tell($"{popup.Title}, {ChoiceSaid(popup)}", Urgency.Polite, popup);
        }
        popup.Moved += (_, _) =>
        {
            if (popup.ChosenAt >= 0)
            {
                Tell(ChoiceSaid(popup), Urgency.Assertive, popup);
            }
        };
    }

    private static string ChoiceSaid(ChoicePopup popup) =>
        Chosen(popup.Titles[popup.ChosenAt], popup.Details[popup.ChosenAt], popup.ChosenAt, popup.Titles.Count);

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
            (modal.Parent as Panel)?.Children.Remove(modal);
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

/// <summary>A popup placed at a line as a screen reader meets it, given it as it is put on the layer or placed
/// (<see cref="PopupPlacement.Place(Control, Point, bool)"/>): what kind of thing it is, and what it is called.</summary>
internal interface IHeardPopup
{
    AutomationControlType Role { get; }

    string Called { get; }
}

/// <summary>A popup placed at a line that is held to the editor (<see cref="PopupPlacement.Place(Control, Point, bool)"/>):
/// narrowed to its width, and moved in to show whole where it fits neither under nor over its line.</summary>
internal interface IHeldToEditor
{
    /// <summary>Held to a room narrower than it would be; as wide as it is of itself in a wider one.</summary>
    void Within(double room);
}

/// <summary>A popup held to the editor that gives way in height too where it fits neither under nor over its line
/// (<see cref="PopupPlacement.Place(Control, Point, bool)"/>): lowered to the room on the side with more, so it shows
/// there whole and covers none of the line.</summary>
internal interface IGivesWayInHeight : IHeldToEditor
{
    /// <summary>Held to a room lower than it would be, as far as it can give way; as tall as it is of itself in a taller
    /// one.</summary>
    void WithinHeight(double room);
}
