using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;

namespace Passenger.App.ToolWindows;

/// <summary>A test's frame at its size in a <see cref="ZoomedPicture"/>, zoomed as it zooms (by steps, to its actual size or
/// to fit, from its bar, its keys or the wheel); the result's other frames on Left and Right, and Escape closes it. The
/// IDE's keys work there too, these its own.</summary>
public sealed class FrameViewer : IdeWindow, IOwnKeys
{
    /// <summary>The most of the screen the window takes as it opens, around a frame at its size.</summary>
    private const double Largest = 0.85;

    private readonly string test;
    private readonly IReadOnlyList<ShownFrame> frames;
    private readonly ZoomedPicture picture = new();
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
        sizeText.Classes.Add(StyleClasses.Muted);

        DockPanel bar = new() { Children = { picture.Tools, sizeText } };
        DockPanel.SetDock(picture.Tools, Avalonia.Controls.Dock.Left);
        sizeText.HorizontalAlignment = HorizontalAlignment.Right;
        Border top = ToolBars.Top(bar);
        top.Bind(Border.BackgroundProperty, top.GetResourceObservable(TokenKeys.Panel));
        Content = new DockPanel { Children = { top, picture } };

        KeyDown += OnKey;
        ShowFrame();
        if (Picture is { } shown)
        {
            // Opened around the frame at its size, as far as the screen allows.
            Size screen = Screens?.ScreenFromWindow(this)?.WorkingArea.Size.ToSize(1) ?? new Size(1600, 1000);
            Width = Math.Clamp(shown.Size.Width + (2 * ZoomedPicture.Gap) + 2, MinWidth, Math.Max(MinWidth, screen.Width * Largest));
            Height = Math.Clamp(shown.Size.Height + (2 * ZoomedPicture.Gap) + 2 + 34, MinHeight, Math.Max(MinHeight, screen.Height * Largest));
        }
    }

    /// <summary>Which of the result's frames is shown.</summary>
    public int Index { get; private set; }

    /// <summary>The zoom the frame is shown at: 1 is one picture pixel a unit.</summary>
    public double Zoom => picture.Zoom;

    /// <summary>The frame shown.</summary>
    public ShownFrame? Frame => frames.Count > 0 ? frames[Index] : null;

    /// <summary>The picture shown.</summary>
    public Bitmap? Picture => Frame?.Picture;

    /// <summary>The size the frame is drawn at, in units.</summary>
    public Size DrawnSize => picture.DrawnSize;

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
        picture.Picture = Picture;
        Title = frames.Count > 1 ? $"{Frame?.Name} ({Index + 1} of {frames.Count}) — {test}" : $"{Frame?.Name} — {test}";
        sizeText.Text = Picture is { } shown ? $"{shown.PixelSize.Width} × {shown.PixelSize.Height} px" : string.Empty;
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

    private Action? Command(KeyEventArgs e) => picture.CommandFor(e) ?? e.Key switch
    {
        Key.Left when e.KeyModifiers == KeyModifiers.None => () => ShowFrame(Index - 1),
        Key.Right when e.KeyModifiers == KeyModifiers.None => () => ShowFrame(Index + 1),
        Key.Escape when e.KeyModifiers == KeyModifiers.None => Close,
        _ => null,
    };
}
