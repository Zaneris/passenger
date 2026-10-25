using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Passenger.App.Testing;
using Passenger.Core;

namespace Passenger.App.ToolWindows;

/// <summary>The frames of the chosen test's result above its details in the Unit Tests window: a thumbnail of each picture
/// its result carries (the windows of a failing headless test as it failed, or a test's own), with its name and size; a
/// click opens it in <see cref="FrameViewer"/> at its size, to zoom. Shown while the result has any.</summary>
public sealed class TestFramesStrip : Border
{
    /// <summary>The tallest a thumbnail is drawn, and the widest.</summary>
    private const double ThumbnailHeight = 96;
    private const double ThumbnailWidth = 160;

    private readonly StackPanel thumbnails = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 6) };

    /// <summary>The results' frames shown, by the result they are of: a run's results are new ones, though a test run
    /// again writes its frame where it wrote the last.</summary>
    private IReadOnlyList<(TestOutcome Result, TestAttachment Frame)> shown = [];

    public TestFramesStrip()
    {
        IsVisible = false;
        BorderThickness = new Thickness(0, 0, 0, 1);
        this.Bind(BorderBrushProperty, this.GetResourceObservable(TokenKeys.Divider));
        // Held to half the room above the details (Fit), the thumbnails draw smaller rather than leaving the details a line.
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = thumbnails },
        };
    }

    /// <summary>Gives the details under the strip at least half of the room the two share, as a short Unit Tests window
    /// leaves little: the strip is held to the other half and its thumbnails shrink to it.</summary>
    public void Fit(double shared) => MaxHeight = Math.Max(ThumbnailHeight / 2, shared / 2);

    /// <summary>The frames shown now, in order, once their pictures are read.</summary>
    public IReadOnlyList<ShownFrame> Frames { get; private set; } = [];

    /// <summary>The frame viewer opened last, while it is open.</summary>
    public FrameViewer? Viewer { get; private set; }

    /// <summary>A frame viewer opened, before it shows: what the IDE puts in each of its windows (its keys, its popups)
    /// goes into it.</summary>
    public event EventHandler<FrameViewer>? ViewerOpened;

    /// <summary>A test's frames, its name for the viewer's title; nothing, and the strip hidden, for none.</summary>
    public void Show(string test, IReadOnlyList<(TestOutcome Result, TestAttachment Frame)> frames)
    {
        if (frames.Count == shown.Count && frames.Zip(shown).All(f => ReferenceEquals(f.First.Result, f.Second.Result) && f.First.Frame == f.Second.Frame))
        {
            return;
        }
        shown = frames;
        thumbnails.Children.Clear();
        Frames = [];
        IsVisible = frames.Count > 0;
        if (frames.Count == 0)
        {
            return;
        }
        bool several = frames.Select(f => f.Result).Distinct().Count() > 1;
        IdeErrors.Observe(ShowAsync(test, [.. frames.Select(f => (several ? $"{TestNode.ResultName(f.Result)}: {f.Frame.Name}" : f.Frame.Name, f.Frame.Path))], frames));
    }

    private async Task ShowAsync(string test, IReadOnlyList<(string Name, string Path)> frames, IReadOnlyList<(TestOutcome, TestAttachment)> showing)
    {
        List<ShownFrame> read = [];
        foreach ((string name, string path) in frames)
        {
            Bitmap? picture = await BackgroundWork.Run(() => Picture(path));
            if (!ReferenceEquals(showing, shown))
            {
                return;
            }
            ShownFrame frame = new(name, path, picture);
            read.Add(frame);
            thumbnails.Children.Add(Thumbnail(test, frame, read));
        }
        Frames = read;
    }

    /// <summary>A picture as its file has it, or null when the file is gone or is no picture.</summary>
    private static Bitmap? Picture(string path)
    {
        try
        {
            return new Bitmap(path);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private Button Thumbnail(string test, ShownFrame frame, List<ShownFrame> frames)
    {
        Control picture = frame.Picture is { } bitmap ? Outlined(new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = ThumbnailHeight - 2, MaxWidth = ThumbnailWidth })
            : Muted("The picture is gone");
        string size = frame.Picture is { } known ? $"{known.PixelSize.Width} × {known.PixelSize.Height}" : string.Empty;
        Button thumbnail = new()
        {
            Classes = { "frame" },
            Focusable = false,
            IsEnabled = frame.Picture is not null,
            Content = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new Border { Height = ThumbnailHeight, Child = picture },
                    new TextBlock { Text = frame.Name, FontSize = 12, MaxWidth = ThumbnailWidth, TextTrimming = TextTrimming.CharacterEllipsis },
                    Muted(size),
                },
            },
        };
        ToolTip.SetTip(thumbnail, $"{frame.Name}\n{frame.Path}");
        thumbnail.Click += (_, _) => Open(test, frames, frame);
        return thumbnail;
    }

    /// <summary>A frame in the viewer, at its size, the others of the result a key away.</summary>
    public FrameViewer Open(string test, IReadOnlyList<ShownFrame> frames, ShownFrame chosen)
    {
        Viewer?.Close();
        List<ShownFrame> viewable = [.. frames.Where(f => f.Picture is not null)];
        FrameViewer viewer = new(test, viewable, Math.Max(0, viewable.IndexOf(chosen)));
        Viewer = viewer;
        viewer.Closed += (_, _) => Viewer = Viewer == viewer ? null : Viewer;
        ViewerOpened?.Invoke(this, viewer);
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            viewer.Show(owner);
        }
        else
        {
            viewer.Show();
        }
        return viewer;
    }

    /// <summary>A picture with a hairline round it, so a white window stands apart from a light theme.</summary>
    private static Border Outlined(Image picture)
    {
        Border outline = new() { BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = picture };
        outline.Bind(Border.BorderBrushProperty, outline.GetResourceObservable(TokenKeys.Divider));
        return outline;
    }

    private static TextBlock Muted(string text)
    {
        // A caption under a picture, smaller than the page's words.
        TextBlock muted = MutedText.Of(text);
        muted.FontSize = 11;
        return muted;
    }
}

/// <summary>A frame as the Unit Tests window shows it: its name, its file, and its picture once read (null if it could not be).</summary>
public sealed record ShownFrame(string Name, string Path, Bitmap? Picture);
