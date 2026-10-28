using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>A part of a window that takes some keys ahead of the IDE's keymap, as a diff takes F7 to its next difference
/// and a merge its Accept keys: the IDE leaves a key alone when the element with the keys, one it is inside or its window
/// says it takes it.</summary>
public interface IOwnKeys
{
    bool TakesKey(KeyEventArgs e);
}

/// <summary>The IDE's keys and mouse gestures in each window it opens but a dialog (the main window, an editor, floating
/// tool, diff, merge or changes window, a test's frame): the main window's one keymap before any control, Escape closing
/// what is over it first, a part's own keys left to it (<see cref="IOwnKeys"/>), its popups over it. Of the UI thread.</summary>
/// <param name="main">The main window, whose popups' layer is its own.</param>
/// <param name="keys">The main window's keymap, which runs in each.</param>
/// <param name="dismissed">Escape with no popup over a window: whether it closed the code's choices or rename box.</param>
/// <param name="pressed">What a key not taken here does in the window it was pressed in: the keymap's action.</param>
/// <param name="sideClicked">What a side button does in the window it was pressed in: the keymap's action for its gesture.</param>
/// <param name="activity">A key or a click, in any of them.</param>
/// <param name="left">One of them was left, perhaps for another program.</param>
public sealed class IdeKeys(Window main, KeymapDispatcher keys, PopupHost popups, Func<TopLevel, bool> dismissed, Action<TopLevel, object?, KeyEventArgs> pressed,
    Action<TopLevel, string> sideClicked, Action activity, Action left)
{
    /// <summary>The window a key is being handled in, while it is: what an action it runs acts on there.</summary>
    public TopLevel? Pressing { get; private set; }

    /// <summary>Takes a window in as the IDE's own, before it shows: its keys, its chords (forgotten on a click, a move of the
    /// focus or the window left), its activity, its popups, and the gestures of its editors and of the side buttons.</summary>
    public void Attach(TopLevel window)
    {
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(window, e), RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyDownEvent, (_, _) => activity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
        {
            keys.Reset();
            popups.WorkIn(window);
            activity();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) => OnSideButton(window, e), RoutingStrategies.Tunnel);
        CodeEditor.SetGestures(window, gesture => keys.Keymap.ActionFor(gesture));
        window.AddHandler(InputElement.GotFocusEvent, (_, _) => keys.Reset(), RoutingStrategies.Bubble, handledEventsToo: true);
        if (window is WindowBase shown)
        {
            shown.Activated += (_, _) => popups.WorkIn(window);
            shown.Deactivated += (_, _) =>
            {
                keys.Reset();
                // Posted: the window coming to the front, one of the IDE's or another program's, is active by then.
                Dispatcher.UIThread.Post(left, DispatcherPriority.Background);
            };
        }
        if (window != main)
        {
            popups.Attach(window);
        }
    }

    /// <summary>A key's action that throws is reported as an IDE error, never the IDE's end.</summary>
    private void OnKeyDown(TopLevel window, KeyEventArgs e)
    {
        Pressing = window;
        popups.WorkIn(window);
        try
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && Escaped(window))
            {
                // Escape closes what is over the window before anything under it hears it.
                e.Handled = true;
                return;
            }
            object? focused = window.FocusManager?.GetFocusedElement();
            if (!TakenThere(window, focused, e))
            {
                pressed(window, focused, e);
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            e.Handled = true;
        }
        finally
        {
            Pressing = null;
        }
    }

    /// <summary>A side button's gesture runs its action wherever it is pressed, as no control takes those buttons, where a
    /// left or middle press is the control's under it (an editor's through <see cref="CodeEditor.GesturesProperty"/>). One
    /// that throws is reported as an IDE error, as a key's is.</summary>
    private void OnSideButton(TopLevel window, PointerPressedEventArgs e)
    {
        MouseShortcut gesture = MouseShortcut.Of(e);
        if (gesture.Button is not (MouseButton.XButton1 or MouseButton.XButton2) || keys.Keymap.ActionFor(gesture) is not { } action)
        {
            return;
        }
        e.Handled = true;
        try
        {
            sideClicked(window, action);
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    /// <summary>Escape over a window: its popup closed, else the code's list of choices or rename box there; false with
    /// neither open.</summary>
    private bool Escaped(TopLevel window)
    {
        if (popups.IsOpenOver(window))
        {
            popups.Dismiss();
            return true;
        }
        return dismissed(window);
    }

    /// <summary>Whether the element with the keys, one it is inside or the window takes the key itself.</summary>
    private static bool TakenThere(TopLevel window, object? focused, KeyEventArgs e)
    {
        for (Visual? at = focused as Visual ?? window; at is not null; at = at.GetVisualParent())
        {
            if (at is IOwnKeys owner && owner.TakesKey(e))
            {
                return true;
            }
        }
        return false;
    }
}
