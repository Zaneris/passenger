using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Debugging;
using Passenger.App.Editors;

namespace Passenger.App;

/// <summary>The popups over the window the person works in (Go to File, Find Action, a breakpoint's properties, a
/// change's old lines): one at a time, on the main window's layer or another of the IDE's windows'
/// (<see cref="IdeKeys"/>), centred under its top or beside a point of it, kept inside it. A click outside one or
/// Escape dismisses it, a breakpoint's popup and window keeping what was typed in them; closed, what had the keys has
/// them again. A UI-thread object of the window.</summary>
public sealed class PopupHost
{
    /// <summary>How far below the main window's top its popups open: under its toolbar.</summary>
    private const double WindowPopupTop = 84;

    /// <summary>How far below an editor window's top its popups open: under its tab strip, as the main window's open
    /// under its toolbar.</summary>
    private const double EditorWindowPopupTop = 48;

    /// <summary>How far below another window's top its popups open: under a diff's or merge's toolbar and the titles of
    /// its sides, a tool window's header, a test frame's toolbar.</summary>
    private const double OtherWindowPopupTop = 64;

    private readonly Panel overlay;
    private readonly ContentControl overlayHost;
    private readonly EditorArea editors;
    private readonly Func<BreakpointStore> breakpoints;

    /// <summary>Each other window's layer for popups, as the overlay is the main window's.</summary>
    private readonly Dictionary<TopLevel, (Panel Layer, ContentControl Host)> windowLayers = [];

    /// <summary>The layer a popup shows on now, or last did; the main window's before any has.</summary>
    private (Panel Layer, ContentControl Host)? shown;

    /// <summary>The window of the IDE's the person last worked in: its keys, a click in it or its coming to the front.</summary>
    private TopLevel? worked;

    /// <summary>The window a popup is over, what had the keys there as it opened and, where that was a list's row, the row
    /// with its item, which have them back as it closes.</summary>
    private (TopLevel Window, Control? Had, RowWithKeys? Row)? over;

    /// <param name="overlay">The main window's layer over everything, and the host in it a popup goes in.</param>
    /// <param name="breakpoints">The open folder's breakpoints, which a breakpoint's popup dismissed keeps its edits in.</param>
    public PopupHost(Panel overlay, ContentControl overlayHost, EditorArea editors, Func<BreakpointStore> breakpoints)
    {
        this.overlay = overlay;
        this.overlayHost = overlayHost;
        this.editors = editors;
        this.breakpoints = breakpoints;
        overlay.PointerPressed += OnLayerPressed;
        overlay.SizeChanged += (_, _) => Refit(overlay, overlayHost);
    }

    private (Panel Layer, ContentControl Host) Layers => shown ??= (overlay, overlayHost);

    /// <summary>Whether a popup is open.</summary>
    public bool IsOpen => Layers.Layer.IsVisible;

    /// <summary>Whether a popup is open over this window.</summary>
    public bool IsOpenOver(TopLevel window) => IsOpen && TopLevel.GetTopLevel(Layers.Layer) == window;

    /// <summary>What the open popup is (Go to File, a breakpoint's properties, the Breakpoints window), or null.</summary>
    public Control? Open => IsOpen ? Layers.Host.Content as Control : null;

    /// <summary>A popup over the window the person works in: centred under its top, or beside a point of that window (from
    /// <see cref="PointIn"/>), kept inside it. Another open on another window's layer goes.</summary>
    public void Show(Control popup, Point? at = null)
    {
        (Panel layer, ContentControl host) = InFront();
        if (Layers.Layer != layer)
        {
            Layers.Layer.IsVisible = false;
            Layers.Host.Content = null;
        }
        if (!layer.IsVisible)
        {
            Control? had = TopLevel.GetTopLevel(layer)?.FocusManager?.GetFocusedElement() as Control;
            over = TopLevel.GetTopLevel(layer) is { } window ? (window, had, RowWithKeys.Of(had)) : null;
        }
        shown = (layer, host);
        Thickness shadow = MakeRoom(popup, host);
        // A layer never shown yet was never laid out: it covers what holds it.
        Size room = layer.Bounds.Size is { Width: > 0 } laid ? laid : (layer.Parent as Visual)?.Bounds.Size ?? default;
        if (at is { } point)
        {
            popup.Measure(Size.Infinity);
            // The frame's own size: a popup that holds its frame has the room inside it, the host has it round any other.
            Thickness inside = popup is TemplatedControl ? shadow : default;
            double width = (popup.Width is double w && !double.IsNaN(w) ? w : popup.DesiredSize.Width) - inside.Left - inside.Right;
            double height = popup.DesiredSize.Height - inside.Top - inside.Bottom;
            host.HorizontalAlignment = HorizontalAlignment.Left;
            host.Margin = new Thickness(Math.Clamp(point.X, 8, Math.Max(8, room.Width - width - 8)) - shadow.Left,
                Math.Clamp(point.Y, 8, Math.Max(8, room.Height - height - 8)) - shadow.Top, 0, 0);
        }
        else
        {
            host.HorizontalAlignment = HorizontalAlignment.Center;
            double top = layer == overlay ? WindowPopupTop : ShowsEditors(TopLevel.GetTopLevel(layer)) ? EditorWindowPopupTop : OtherWindowPopupTop;
            host.Margin = new Thickness(0, top - shadow.Top, 0, 0);
        }
        Fit(popup, host, room);
        host.Content = popup;
        layer.IsVisible = true;
    }

    /// <summary>A popup that answers, shown as <see cref="Show(Control, Point?)"/> shows one: as it answers, it closes
    /// (while it is still the one open) and <paramref name="done"/> is given the answer.</summary>
    public void Show<T>(IAnswers<T> popup, Action<T> done, Point? at = null)
    {
        Control shown = popup as Control ?? throw new ArgumentException($"{popup.GetType().Name} is no control to show", nameof(popup));
        popup.Done += (_, answer) =>
        {
            if (Open == shown)
            {
                Close();
            }
            done(answer);
        };
        Show(shown, at);
    }

    /// <summary>A popup no wider than the window it is over, its frame's shadow inside it, and no taller than the room
    /// from where it opens to a gap above the window's foot, the shadow under its frame free to run past: so one over a
    /// window narrower or shorter than the popups' sizes (a floating tool window, the smallest window) shows its frame
    /// whole, its list or preview giving way; as the window is resized, it follows.</summary>
    private static void Fit(Control popup, ContentControl host, Size room)
    {
        Thickness outside = popup is TemplatedControl ? default : host.Padding;
        if (room.Width > 0)
        {
            popup.MaxWidth = Math.Max(0, room.Width - outside.Left - outside.Right);
        }
        if (room.Height > 0)
        {
            Thickness inside = popup is TemplatedControl framed ? framed.Padding : default;
            popup.MaxHeight = Math.Max(0, room.Height - host.Margin.Top - outside.Top - Gap + inside.Bottom);
        }
    }

    /// <summary>The least room between a popup's frame and the edge of the window it is over.</summary>
    private const double Gap = 8;

    /// <summary>A layer's popup fitted again to the window it is over, resized.</summary>
    private static void Refit(Panel layer, ContentControl host)
    {
        if (layer.IsVisible && host.Content is Control popup)
        {
            Fit(popup, host, layer.Bounds.Size);
        }
    }

    /// <summary>Room round a popup's frame for its shadow (Shell.axaml's Border.popup), taken from the place the popup
    /// is put so the frame stands where it would without it: a control is drawn no further out than its
    /// parent.</summary>
    private static Thickness MakeRoom(Control popup, ContentControl host)
    {
        Thickness room = new(32, 24, 32, 40);
        if (popup is TemplatedControl framed)
        {
            host.Padding = default;
            if (framed.Padding != room)
            {
                framed.Padding = room;
                if (!double.IsNaN(framed.Width))
                {
                    framed.Width += room.Left + room.Right;
                }
            }
        }
        else
        {
            host.Padding = room;
        }
        return room;
    }

    /// <summary>Closes the popup: what had the keys as it opened has them again (a list's chosen row where a row of it
    /// had them), else, where that is gone from a window of the editors', the editor shown.</summary>
    public void Close()
    {
        Layers.Layer.IsVisible = false;
        Layers.Host.Content = null;
        (TopLevel Window, Control? Had, RowWithKeys? Row)? was = over;
        over = null;
        if (was is { } back && GiveBack(back.Window, back.Had, back.Row))
        {
            return;
        }
        if (editors.Active is not null && (was is null || ShowsEditors(was.Value.Window)))
        {
            editors.CodeEditor.Focus();
        }
    }

    /// <summary>Whether a window shows the editors: the main window or an editor window.</summary>
    private bool ShowsEditors(TopLevel? window) => window is not null && (window == TopLevel.GetTopLevel(overlay) || editors.Windows.Any(w => w.Window == window));

    /// <summary>Gives the keys back to what had them in a window, still shown there: a list's row to that row, or the row
    /// of the item it showed, the list may have made again meanwhile (<see cref="ListKeys.Hold(RowWithKeys)"/>); false
    /// where nothing of it is left.</summary>
    private static bool GiveBack(TopLevel window, Control? had, RowWithKeys? row)
    {
        if (row is not null && Shown(row.List) && ListKeys.Hold(row))
        {
            return true;
        }
        return had is not null && had != window && Shown(had) && had.Focus();

        bool Shown(Control control) => TopLevel.GetTopLevel(control) == window && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;
    }

    /// <summary>Closes the popup as a click outside it or Escape does: a breakpoint's popup or window keeps what was
    /// typed in it.</summary>
    public void Dismiss()
    {
        switch (Layers.Host.Content)
        {
            case BreakpointPopup breakpoint:
                breakpoint.Close(breakpoints(), more: false);
                break;
            case BreakpointsWindow window:
                window.Close();
                break;
            default:
                Close();
                break;
        }
    }

    /// <summary>A window's own layer for popups, over what it shows, which its keys open there; gone with the window.</summary>
    public void Attach(TopLevel window)
    {
        ContentControl host = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        Panel layer = new() { IsVisible = false, Background = Brushes.Transparent, Children = { host } };
        layer.PointerPressed += OnLayerPressed;
        layer.SizeChanged += (_, _) => Refit(layer, host);
        Control? shows = window.Content as Control;
        window.Content = null;
        Panel layers = new();
        if (shows is not null)
        {
            layers.Children.Add(shows);
        }
        layers.Children.Add(layer);
        window.Content = layers;
        windowLayers[window] = (layer, host);
        window.Closed += (_, _) =>
        {
            windowLayers.Remove(window);
            if (shown?.Layer == layer)
            {
                shown = null;
            }
            if (worked == window)
            {
                worked = null;
            }
            if (over?.Window == window)
            {
                over = null;
            }
        };
    }

    /// <summary>The window the person works in now: its keys, a click in it, its coming to the front.</summary>
    public void WorkIn(TopLevel window) => worked = window;

    /// <summary>A point of a control as the popups of the window in front place one beside it.</summary>
    public Point? PointIn(Visual visual, Point point) => visual.TranslatePoint(point, InFront().Layer);

    /// <summary>The popups' layer of the window the person works in: one of the IDE's beside its editors' (a diff, a
    /// floating tool window) while it was the last worked in, else the editors' in front, an editor window's or the main
    /// window's.</summary>
    private (Panel Layer, ContentControl Host) InFront()
    {
        if (worked is { } other && windowLayers.TryGetValue(other, out (Panel Layer, ContentControl Host) own) && editors.Windows.All(w => w.Window != other))
        {
            return own;
        }
        return editors.InFront is { } front && windowLayers.TryGetValue(front.Window, out (Panel Layer, ContentControl Host) layer) ? layer : (overlay, overlayHost);
    }

    /// <summary>A press on a layer itself, outside the popup on it.</summary>
    private void OnLayerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender)
        {
            Dismiss();
        }
    }
}
