using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>A picture at a zoom, one piece for every view of one (an image's preview, a test's frame): one picture pixel a
/// unit at 100%, framed, scrolled when larger than its room. Zoom In and Zoom Out go by steps, Actual Size to 100% and Fit
/// Zoom to Window to its room, from <see cref="Tools"/> (with the zoom shown), the keys its owner hands it
/// (<see cref="CommandFor"/>: ⌘= ⌘- ⌘/, Ctrl elsewhere) or the wheel with that modifier. Enlarged, each pixel is a square
/// of its own colour; reduced, smoothed. Fitted, it follows its room as that changes, until it is zoomed.</summary>
public sealed class ZoomedPicture : ScrollViewer
{
    /// <summary>The zooms Zoom In and Zoom Out step through.</summary>
    private static readonly double[] Steps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 12, 16];

    /// <summary>The space around the picture.</summary>
    public const double Gap = 12;

    private readonly Image image = new() { Stretch = Stretch.Fill };
    private readonly TextBlock zoomText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), FontSize = 12 };

    /// <summary>How the zoom follows the room: not at all once zoomed, fitted but never enlarged (a preview's start), or
    /// fitted whole (Fit Zoom to Window).</summary>
    private enum Fitting
    {
        None,
        Down,
        Whole,
    }

    private Fitting fitting;

    public ZoomedPicture()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Border framed = new()
        {
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Gap),
            Child = image,
        };
        framed.Bind(Border.BorderBrushProperty, framed.GetResourceObservable(TokenKeys.Divider));
        Content = framed;
        AddHandler(PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SizeChanged += (_, _) => Refit();
        Tools = new StackPanel
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
        ZoomTo(1);
    }

    /// <summary>Styled as the ScrollViewer it is.</summary>
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    /// <summary>Its buttons and its zoom, for its owner's bar.</summary>
    public StackPanel Tools { get; }

    /// <summary>The picture shown, at the zoom the one before had, or fitted again while it is fitted.</summary>
    public Bitmap? Picture
    {
        get => image.Source as Bitmap;
        set
        {
            image.Source = value;
            if (fitting == Fitting.None || !Refit())
            {
                Show(Zoom);
            }
        }
    }

    /// <summary>The zoom the picture is shown at: 1 is one picture pixel a unit.</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>The size the picture is drawn at, in units.</summary>
    public Size DrawnSize => new(image.Width, image.Height);

    public void ZoomIn() => ZoomTo(Steps.FirstOrDefault(s => s > Zoom * 1.001, Steps[^1]));

    public void ZoomOut() => ZoomTo(Steps.LastOrDefault(s => s < Zoom / 1.001, Steps[0]));

    public void ZoomTo(double zoom)
    {
        fitting = Fitting.None;
        Show(zoom);
    }

    /// <summary>The zoom at which the whole picture fits its room, kept as the room changes.</summary>
    public void FitToWindow()
    {
        fitting = Fitting.Whole;
        Refit();
    }

    /// <summary>The picture shrunk to fit its room but never enlarged, so a small icon shows as it is, kept as the room
    /// changes until it is zoomed: a preview's start.</summary>
    public void FitDown()
    {
        fitting = Fitting.Down;
        Refit();
    }

    /// <summary>What a key does here (the zoom's keys, with the platform's command modifier), for its owner to run; null
    /// for a key that is not its.</summary>
    public Action? CommandFor(KeyEventArgs e)
    {
        bool held = (e.KeyModifiers & ~KeyModifiers.Shift) == Keystroke.Command;
        return e.Key switch
        {
            Key.OemPlus or Key.Add when held => ZoomIn,
            Key.OemMinus or Key.Subtract when held => ZoomOut,
            Key.Oem2 or Key.Divide when held => () => ZoomTo(1),
            _ => null,
        };
    }

    /// <summary>The zoom fitted again to the room as it now is, while it is fitted; false while it has no room yet.</summary>
    private bool Refit()
    {
        if (fitting == Fitting.None || Picture is not { } picture || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return false;
        }
        double room = Math.Min((Bounds.Width - (2 * Gap) - 2) / picture.Size.Width, (Bounds.Height - (2 * Gap) - 2) / picture.Size.Height);
        Show(fitting == Fitting.Down ? Math.Min(1, room) : room);
        return true;
    }

    private void Show(double zoom)
    {
        Zoom = Math.Clamp(zoom, Steps[0], Steps[^1]);
        if (Picture is { } picture)
        {
            image.Width = picture.Size.Width * Zoom;
            image.Height = picture.Size.Height * Zoom;
        }
        RenderOptions.SetBitmapInterpolationMode(image, Zoom >= 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        zoomText.Text = string.Create(CultureInfo.CurrentCulture, $"{Zoom * 100:0}%");
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
