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
/// change's old lines): one at a time, on the main window's layer or an editor window's, centred under its top (the
/// toolbar, an editor window's tabs) or beside a point of it, kept inside it. A click outside one or Escape dismisses it, a
/// breakpoint's popup and window keeping what was typed in them, as Rider's do; closed, the editor has the keys again. A
/// UI-thread object of the window.</summary>
public sealed class PopupHost
{
    /// <summary>How far below the main window's top its popups open: under its toolbar.</summary>
    private const double WindowPopupTop = 84;

    /// <summary>How far below an editor window's top its popups open: under its tab strip, as the main window's open
    /// under its toolbar.</summary>
    private const double EditorWindowPopupTop = 48;

    private readonly Panel overlay;
    private readonly ContentControl overlayHost;
    private readonly EditorArea editors;
    private readonly Func<BreakpointStore> breakpoints;

    /// <summary>Each editor window's layer for popups, as the overlay is the main window's.</summary>
    private readonly Dictionary<Window, (Panel Layer, ContentControl Host)> windowLayers = [];

    /// <summary>The layer a popup shows on now, or last did; the main window's before any has.</summary>
    private (Panel Layer, ContentControl Host)? shown;
    private Control? fitted;
    private double requestedMaxWidth;
    private Point? position;
    private Thickness shadow;

    /// <param name="overlay">The main window's layer over everything, and the host in it a popup goes in.</param>
    /// <param name="breakpoints">The open folder's breakpoints, which a breakpoint's popup dismissed keeps its edits in.</param>
    public PopupHost(Panel overlay, ContentControl overlayHost, EditorArea editors, Func<BreakpointStore> breakpoints)
    {
        this.overlay = overlay;
        this.overlayHost = overlayHost;
        this.editors = editors;
        this.breakpoints = breakpoints;
        overlay.PointerPressed += OnLayerPressed;
        overlay.SizeChanged += OnLayerSizeChanged;
    }

    private (Panel Layer, ContentControl Host) Layers => shown ??= (overlay, overlayHost);

    /// <summary>Whether a popup is open.</summary>
    public bool IsOpen => Layers.Layer.IsVisible;

    /// <summary>What the open popup is (Go to File, a breakpoint's properties, the Breakpoints window), or null.</summary>
    public Control? Open => IsOpen ? Layers.Host.Content as Control : null;

    /// <summary>A popup over the window the person works in: centred under its top, or beside a point of that window (from
    /// <see cref="PointIn"/>), kept inside it. Another open on another window's layer goes.</summary>
    public void Show(Control popup, Point? at = null)
    {
        RestoreWidth();
        (Panel layer, ContentControl host) = InFront();
        if (Layers.Layer != layer)
        {
            Layers.Layer.IsVisible = false;
            Layers.Host.Content = null;
        }
        shown = (layer, host);
        shadow = MakeRoom(popup, host);
        fitted = popup;
        requestedMaxWidth = popup.MaxWidth;
        position = at;
        Place(layer, host, popup);
        host.Content = popup;
        layer.IsVisible = true;
    }

    /// <summary>The popup's width leaves room for its shadow within the current window, including after resize. Its
    /// requested width remains its own, so widening restores it without recreating its query, selection or focus.</summary>
    private void Place(Panel layer, ContentControl host, Control popup)
    {
        Size room = layer.Bounds.Size is { Width: > 0 } laid ? laid : (layer.Parent as Visual)?.Bounds.Size ?? default;
        double outside = popup is TemplatedControl ? 0 : shadow.Left + shadow.Right;
        popup.MaxWidth = room.Width > 0 ? Math.Min(requestedMaxWidth, Math.Max(0, room.Width - 16 - outside)) : requestedMaxWidth;
        if (position is { } point)
        {
            popup.Measure(Size.Infinity);
            double width = popup.DesiredSize.Width + outside;
            double height = popup.DesiredSize.Height + (popup is TemplatedControl ? 0 : shadow.Top + shadow.Bottom);
            host.HorizontalAlignment = HorizontalAlignment.Left;
            host.Margin = new Thickness(Math.Clamp(point.X - shadow.Left, 8, Math.Max(8, room.Width - width - 8)),
                Math.Clamp(point.Y - shadow.Top, 8, Math.Max(8, room.Height - height - 8)), 0, 0);
        }
        else
        {
            host.HorizontalAlignment = HorizontalAlignment.Center;
            host.Margin = new Thickness(0, (layer == overlay ? WindowPopupTop : EditorWindowPopupTop) - shadow.Top, 0, 0);
        }
    }

    private void OnLayerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (fitted is { } popup && sender == Layers.Layer)
        {
            Place(Layers.Layer, Layers.Host, popup);
        }
    }

    private void RestoreWidth()
    {
        if (fitted is { } popup)
        {
            popup.MaxWidth = requestedMaxWidth;
            fitted = null;
        }
    }

    /// <summary>Room round a popup's frame for its shadow (Shell.axaml's Border.popup), taken from the place the popup
    /// is put so the frame stands where it would without it: Avalonia draws a control no further out than its parent,
    /// so a frame that fills its popup lost its shadow and showed square corners.</summary>
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

    /// <summary>Closes the popup; the editor shown has the keys again.</summary>
    public void Close()
    {
        RestoreWidth();
        Layers.Layer.IsVisible = false;
        Layers.Host.Content = null;
        if (editors.Active is not null)
        {
            editors.CodeEditor.Focus();
        }
    }

    /// <summary>Closes the popup as a click outside it or Escape does: a breakpoint's popup or window keeps what was typed
    /// in it, as Rider's do.</summary>
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

    /// <summary>An editor window's own layer for popups, which its keys open there; gone with the window.</summary>
    public void Attach(EditorWindow editorWindow)
    {
        Window window = editorWindow.Window;
        ContentControl host = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        Panel layer = new() { IsVisible = false, Background = Brushes.Transparent, Children = { host } };
        layer.PointerPressed += OnLayerPressed;
        layer.SizeChanged += OnLayerSizeChanged;
        editorWindow.Layers.Children.Add(layer);
        windowLayers[window] = (layer, host);
        window.Closed += (_, _) =>
        {
            layer.SizeChanged -= OnLayerSizeChanged;
            windowLayers.Remove(window);
            if (shown?.Layer == layer)
            {
                RestoreWidth();
                shown = null;
            }
        };
    }

    /// <summary>A point of a control as the popups of the window in front place one beside it.</summary>
    public Point? PointIn(Visual visual, Point point) => visual.TranslatePoint(point, InFront().Layer);

    /// <summary>The popups' layer of the window the person works in: an editor window's, or the main window's.</summary>
    private (Panel Layer, ContentControl Host) InFront() =>
        editors.InFront is { } front && windowLayers.TryGetValue(front.Window, out (Panel Layer, ContentControl Host) layer) ? layer : (overlay, overlayHost);

    /// <summary>A press on a layer itself, outside the popup on it.</summary>
    private void OnLayerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender)
        {
            Dismiss();
        }
    }
}
