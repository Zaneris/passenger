using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Passenger.Editor;

namespace Passenger.App.ToolWindows;

/// <summary>A test's frame at its size: one picture pixel a unit at 100%, scrolled when it is larger than the window;
/// Zoom In and Zoom Out by steps (⌘= and ⌘-, Ctrl on Windows and Linux, or the modifier with the wheel), Actual Size
/// (⌘/) and Fit Zoom to Window; the result's other frames on Left and Right, and Escape closes it. The IDE's keys work
/// there too, these its own.</summary>
public sealed class FrameViewer : Window, IOwnKeys
{
    /// <summary>The zooms Zoom In and Zoom Out step through.</summary>
    private static readonly double[] Steps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 12, 16];

    /// <summary>The most of the screen the window takes as it opens, around a frame at its size.</summary>
    private const double Largest = 0.85;

    private readonly string test;
    private readonly IReadOnlyList<ShownFrame> frames;
    private readonly Image image = new() { Stretch = Stretch.Fill };
    private readonly ScrollViewer scroller;
    private readonly TextBlock zoomText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), FontSize = 12 };
    private readonly TextBlock sizeText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), FontSize = 12 };

    public FrameViewer(string test, IReadOnlyList<ShownFrame> frames, int index)
    {
        this.test = test;
        this.frames = frames;
        Index = Math.Clamp(index, 0, Math.Max(0, frames.Count - 1));
        MinWidth = 320;
        MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Editor_Background));
        sizeText.Classes.Add("muted");

        Border framed = new()
        {
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Gap),
            Child = image,
        };
        framed.Bind(Border.BorderBrushProperty, framed.GetResourceObservable(TokenKeys.Divider));
        scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = framed,
        };
        scroller.AddHandler(PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        StackPanel tools = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ToolButtons.Of(IconKeys.ZoomIn, $"Zoom In ({Keys("=")})", ZoomIn),
                ToolButtons.Of(IconKeys.ZoomOut, $"Zoom Out ({Keys("-")})", ZoomOut),
                ToolButtons.Of(IconKeys.ActualSize, $"Actual Size ({Keys("/")})", () => ZoomTo(1)),
                ToolButtons.Of(IconKeys.ZoomFit, "Fit Zoom to Window", FitToWindow),
                zoomText,
            },
        };
        DockPanel bar = new() { Children = { tools, sizeText } };
        DockPanel.SetDock(tools, Avalonia.Controls.Dock.Left);
        sizeText.HorizontalAlignment = HorizontalAlignment.Right;
        Border top = ToolBars.Top(bar);
        top.Bind(Border.BackgroundProperty, top.GetResourceObservable(TokenKeys.Panel));
        Content = new DockPanel { Children = { top, scroller } };

        KeyDown += OnKey;
        ShowFrame();
        if (Picture is { } shown)
        {
            // Opened around the frame at its size, as far as the screen allows.
            Size screen = Screens?.ScreenFromWindow(this)?.WorkingArea.Size.ToSize(1) ?? new Size(1600, 1000);
            Width = Math.Clamp(shown.Size.Width + (2 * Gap) + 2, MinWidth, Math.Max(MinWidth, screen.Width * Largest));
            Height = Math.Clamp(shown.Size.Height + (2 * Gap) + 2 + 34, MinHeight, Math.Max(MinHeight, screen.Height * Largest));
        }
    }

    /// <summary>The space around the frame.</summary>
    private const double Gap = 12;

    /// <summary>Which of the result's frames is shown.</summary>
    public int Index { get; private set; }

    /// <summary>The zoom the frame is shown at: 1 is one picture pixel a unit.</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>The frame shown.</summary>
    public ShownFrame? Frame => frames.Count > 0 ? frames[Index] : null;

    /// <summary>The picture shown.</summary>
    public Bitmap? Picture => Frame?.Picture;

    /// <summary>The size the frame is drawn at, in units.</summary>
    public Size DrawnSize => new(image.Width, image.Height);

    public void ZoomIn() => ZoomTo(Steps.FirstOrDefault(s => s > Zoom * 1.001, Steps[^1]));

    public void ZoomOut() => ZoomTo(Steps.LastOrDefault(s => s < Zoom / 1.001, Steps[0]));

    /// <summary>The zoom at which the whole frame fits the window.</summary>
    public void FitToWindow()
    {
        if (Picture is not { } picture || scroller.Bounds.Width <= 0 || scroller.Bounds.Height <= 0)
        {
            return;
        }
        double room = Math.Min((scroller.Bounds.Width - (2 * Gap) - 2) / picture.Size.Width, (scroller.Bounds.Height - (2 * Gap) - 2) / picture.Size.Height);
        ZoomTo(Math.Clamp(room, Steps[0], Steps[^1]));
    }

    public void ZoomTo(double zoom)
    {
        Zoom = Math.Clamp(zoom, Steps[0], Steps[^1]);
        if (Picture is { } picture)
        {
            image.Width = picture.Size.Width * Zoom;
            image.Height = picture.Size.Height * Zoom;
        }
        // Enlarged, each pixel is a square of its own colour; reduced, smoothed.
        RenderOptions.SetBitmapInterpolationMode(image, Zoom >= 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        zoomText.Text = string.Create(CultureInfo.CurrentCulture, $"{Zoom * 100:0}%");
    }

    /// <summary>Another of the result's frames, at the zoom this one had.</summary>
    public void ShowFrame(int index)
    {
        if (index < 0 || index >= frames.Count || index == Index)
        {
            return;
        }
        Index = index;
        ShowFrame();
    }

    private void ShowFrame()
    {
        image.Source = Picture;
        Title = frames.Count > 1 ? $"{Frame?.Name} ({Index + 1} of {frames.Count}) — {test}" : $"{Frame?.Name} — {test}";
        sizeText.Text = Picture is { } picture ? $"{picture.PixelSize.Width} × {picture.PixelSize.Height} px" : string.Empty;
        ZoomTo(Zoom);
    }

    /// <summary>Its keys are its own, ahead of the IDE's keymap (whose ⌘- is Back).</summary>
    public bool TakesKey(KeyEventArgs e) => Command(e) is not null;

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (Command(e) is { } run)
        {
            run();
            e.Handled = true;
        }
    }

    private Action? Command(KeyEventArgs e)
    {
        bool held = (e.KeyModifiers & ~KeyModifiers.Shift) == Keystroke.Command;
        return e.Key switch
        {
            Key.OemPlus or Key.Add when held => ZoomIn,
            Key.OemMinus or Key.Subtract when held => ZoomOut,
            Key.Oem2 or Key.Divide when held => () => ZoomTo(1),
            Key.Left when e.KeyModifiers == KeyModifiers.None => () => ShowFrame(Index - 1),
            Key.Right when e.KeyModifiers == KeyModifiers.None => () => ShowFrame(Index + 1),
            Key.Escape when e.KeyModifiers == KeyModifiers.None => Close,
            _ => null,
        };
    }

    /// <summary>The command modifier with the wheel zooms; the wheel alone scrolls.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & Keystroke.Command) == 0 || e.Delta.Y == 0)
        {
            return;
        }
        if (e.Delta.Y > 0)
        {
            ZoomIn();
        }
        else
        {
            ZoomOut();
        }
        e.Handled = true;
    }

    private static string Keys(string key) => OperatingSystem.IsMacOS() ? $"⌘{key}" : $"Ctrl+{key}";
}
