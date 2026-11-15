using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using MauiElement = Microsoft.Maui.Controls.Element;

namespace Passenger.Inspector.Maui;

/// <summary>Select Element and the outline over the element chosen in a MAUI app, drawn over each window on an overlay of
/// MAUI's own (WindowOverlay), which takes a person's taps while choosing: a tap chooses the element under it, which the
/// app never sees. The overlay goes once Passenger has.</summary>
internal sealed class Picker(Inspector inspecting, Connection connection) : Picking<MauiElement>(connection)
{
    private readonly Inspector inspector = inspecting;
    private readonly Dictionary<Window, Overlay> overlays = [];
    private MauiElement? outlined;
    private bool choosing;

    public override void Choose(bool on)
    {
        choosing = on;
        foreach (Window window in on ? inspector.TopLevels() : [.. overlays.Keys])
        {
            if (OverlayOf(window) is { } overlay)
            {
                overlay.DisableUITouchEventPassthrough = on;
            }
        }
    }

    public override void Outline(MauiElement? element)
    {
        MauiElement? before = outlined;
        outlined = element;
        foreach (Window window in new[] { before, element }.OfType<MauiElement>().Select(inspector.TopOf).OfType<Window>().Distinct())
        {
            OverlayOf(window)?.Invalidate();
        }
    }

    /// <summary>Every overlay taken out of its window.</summary>
    public void Stop()
    {
        foreach ((Window window, Overlay overlay) in overlays)
        {
            overlay.Tapped -= OnTapped;
            window.RemoveOverlay(overlay);
        }
        overlays.Clear();
    }

    /// <summary>A window's overlay, put over it the first time; null where its platform takes none.</summary>
    private Overlay? OverlayOf(Window window)
    {
        if (overlays.TryGetValue(window, out Overlay? known))
        {
            return known;
        }
        Overlay overlay = new(window, this);
        if (!window.AddOverlay(overlay))
        {
            InspectorTrace.Step($"{Shown.Called(window)} took no overlay");
            return null;
        }
        overlay.Tapped += OnTapped;
        overlays[window] = overlay;
        return overlay;
    }

    /// <summary>A tap while choosing: the element under it chosen. Called by MAUI on the app's UI thread, where nothing the
    /// inspector throws may reach the app.</summary>
    private void OnTapped(object? sender, WindowOverlayTappedEventArgs args)
    {
        if (!choosing || sender is not Overlay { Window: Window window } overlay)
        {
            return;
        }
        try
        {
            (double x, double y) = overlay.InWindow(args.Point);
            Places places = new(window);
            Picked(Placing.Deepest<MauiElement>(window, Inspector.Children, places.Of, e => e is not VisualElement { IsVisible: false }, x, y));
        }
        catch (Exception error)
        {
            InspectorTrace.Step($"the tap chose nothing: {error}");
            Picked(null);
        }
    }

    /// <summary>The outline over the element chosen, where its window shows it.</summary>
    private sealed class Outlined(Picker picker, Overlay overlay) : IWindowOverlayElement
    {
        public bool Contains(Point point) => false;

        /// <summary>Drawn by MAUI as the app draws its window, where nothing the inspector throws may reach the app.</summary>
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            try
            {
                Outline(canvas);
            }
            catch (Exception error)
            {
                InspectorTrace.Step($"the outline could not be drawn: {error}");
            }
        }

        private void Outline(ICanvas canvas)
        {
            if (picker.outlined is not { } element || picker.inspector.TopOf(element) != overlay.Window || new Places((Window)overlay.Window).Of(element) is not { } box)
            {
                return;
            }
            (double x, double y) = overlay.Origin();
            RectF shown = new((float)(box.X - x), (float)(box.Y - y), (float)box.Width, (float)box.Height);
            canvas.FillColor = Color.FromRgba(OutlineFill.R, OutlineFill.G, OutlineFill.B, OutlineFill.A);
            canvas.FillRectangle(shown);
            canvas.StrokeColor = Color.FromRgba(OutlineEdge.R, OutlineEdge.G, OutlineEdge.B, OutlineEdge.A);
            canvas.StrokeSize = 2;
            canvas.DrawRectangle(shown);
        }
    }

    /// <summary>The overlay over one window, holding the outline: where it is in the window, and a tap's point as the window's.</summary>
    private sealed class Overlay : WindowOverlay
    {
        public Overlay(Window window, Picker picker) : base(window)
        {
            AddWindowElement(new Outlined(picker, this));
        }

        /// <summary>The overlay's top left in its window's units, which it draws from.</summary>
        public (double X, double Y) Origin() =>
            AndroidViews.InWindow(View) is { } at && Density > 0 ? (at.X / Density, at.Y / Density) : (0, 0);

        /// <summary>A tap's point, which the overlay gives on its screen, as a point of the window.</summary>
        public (double X, double Y) InWindow(Point tapped) =>
            AndroidViews.OnScreen(View) is { } screen && AndroidViews.InWindow(View) is { } window
                ? Placing.InWindow(tapped.X, tapped.Y, screen, window, Density)
                : (tapped.X, tapped.Y);

        /// <summary>The overlay's own view, read by name: MAUI types it as each head's view, so a call compiled against the
        /// cross-platform assembly's finds no such member in a head's.</summary>
        private object? View => typeof(WindowOverlay).GetProperty(nameof(GraphicsView))?.GetValue(this);
    }
}
