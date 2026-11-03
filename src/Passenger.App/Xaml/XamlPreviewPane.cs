using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.Xaml;

/// <summary>The design view beside a XAML file: a header naming it and what it is doing, the previewer's error over the
/// last frame when the XAML did not load, and the frame at its own size on the editor's background. One larger than the
/// pane is drawn smaller by the previewer itself, never shrunk as a picture, so its text stays as sharp as the IDE's own at
/// any scale; the frame lands pixel for pixel on the screen. Where the previewer says which element is where (WPF's), a click on the frame chooses
/// the element under it and the element at the editor's caret is outlined.</summary>
public sealed class XamlPreviewPane : DockPanel
{
    private readonly XamlPreview preview;
    private readonly TextBlock status;
    private readonly TextBlock error;
    private readonly Border errorBar;
    private readonly Image image;
    private readonly Canvas marks;
    private readonly Border outline;
    private readonly Border edge;
    private readonly ToolWindows.EmptyState waiting;
    private readonly Panel view;

    /// <summary>The frame in its design's units, scaled to the size it is shown at.</summary>
    private readonly LayoutTransformControl scaled;

    /// <summary>Asks the previewer for the scale the pane's size calls for once a resize pauses, so a splitter's drag
    /// draws no frame per step.</summary>
    private readonly Debounce redraw;
    private TopLevel? shownOn;

    /// <summary>The scale the pane's window draws at.</summary>
    private double windowScale = 1;

    /// <summary>How much of its design's size the frame is shown at: 1 where the pane holds it, less where it does not.</summary>
    private double fit = 1;
    private Rect? outlined;

    public XamlPreviewPane(XamlPreview preview)
    {
        this.preview = preview;
        status = new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Border header = ToolWindows.ToolBars.OverEditor(new DockPanel
        {
            Margin = new Thickness(10, 0),
            Children =
            {
                new TextBlock { Text = "Preview", FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) },
                status,
            },
        });
        error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable(TokenKeys.Error));
        errorBar = new Border { [DockProperty] = Dock.Top, Padding = new Thickness(10, 6), IsVisible = false, Child = error };
        errorBar.Bind(Border.BackgroundProperty, errorBar.GetResourceObservable(TokenKeys.Banner));
        image = new Image { Stretch = Stretch.Fill };
        // The element at the caret, drawn over the frame in the frame's own units, so it shrinks with it.
        outline = new Border { BorderThickness = new Thickness(OutlineWidth), IsVisible = false, IsHitTestVisible = false };
        outline.Bind(Border.BorderBrushProperty, outline.GetResourceObservable(TokenKeys.Accent));
        marks = new Canvas { IsHitTestVisible = false, Children = { outline } };
        Panel framed = new() { Children = { image, marks } };
        framed.PointerPressed += OnFramePressed;
        // An edge, so a frame of the editor's own colour still shows where the design ends.
        scaled = new LayoutTransformControl { Child = framed };
        edge = new Border { Child = scaled, BorderThickness = new Thickness(1), Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        edge.Bind(Border.BorderBrushProperty, edge.GetResourceObservable(TokenKeys.Divider));
        waiting = new ToolWindows.EmptyState { MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Center };
        waiting.ActionRequested += (_, action) => ActionRequested?.Invoke(this, action);
        view = new Panel { ClipToBounds = true, Children = { edge, waiting } };
        view.SizeChanged += (_, _) => Fit();
        redraw = new Debounce(TimeSpan.FromMilliseconds(150), () =>
        {
            preview.Scaling = windowScale * fit;
            return Task.CompletedTask;
        });
        Children.Add(header);
        Children.Add(errorBar);
        Children.Add(view);
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Editor_Background));
        preview.Changed += (_, _) => Show();
        AttachedToVisualTree += (_, _) =>
        {
            shownOn = TopLevel.GetTopLevel(this);
            if (shownOn is not null)
            {
                windowScale = shownOn.RenderScaling;
                preview.Scaling = windowScale * fit;
                shownOn.ScalingChanged += Rescaled;
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            shownOn?.ScalingChanged -= Rescaled;
            shownOn = null;
            redraw.Stop();
        };
        Show();
    }

    /// <summary>The action the pane's words offer was asked for (Build for XAML Preview), by its id.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>An element of the file was clicked in the frame; raised on the UI thread.</summary>
    public event EventHandler<PreviewElement>? ElementChosen;

    /// <summary>The frame shown.</summary>
    public Image Frame => image;

    /// <summary>The outline of the element at the caret.</summary>
    public Control Outline => outline;

    /// <summary>The element's box outlined, in the frame's units; null while none is.</summary>
    public Rect? Outlined => outline.IsVisible ? outlined : null;

    /// <summary>Build for XAML Preview's title, which the pane's link shows.</summary>
    public const string BuildForPreviewTitle = "Build for XAML Preview";

    /// <summary>How far the outline stands outside the box it outlines.</summary>
    public const double OutlineWidth = 2;

    /// <summary>The error shown over the frame; null while there is none.</summary>
    public string? ErrorShown => errorBar.IsVisible ? error.Text : null;

    /// <summary>Outlines an element's box in the frame, or none.</summary>
    public void ShowOutline(PreviewElement? element)
    {
        if (element is null || preview.Frame is null)
        {
            outline.IsVisible = false;
            return;
        }
        // Around the box rather than on its edge, so it shows on an element drawn in the accent's own colour.
        Rect box = element.Box;
        outlined = box;
        Canvas.SetLeft(outline, box.X - OutlineWidth);
        Canvas.SetTop(outline, box.Y - OutlineWidth);
        outline.Width = box.Width + (2 * OutlineWidth);
        outline.Height = box.Height + (2 * OutlineWidth);
        outline.IsVisible = true;
    }

    private void OnFramePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(image).Properties.IsLeftButtonPressed || preview.ElementAt(e.GetPosition(image)) is not { } chosen)
        {
            return;
        }
        e.Handled = true;
        ElementChosen?.Invoke(this, chosen);
    }

    /// <summary>The window moved to a screen of another scale: the previewer draws at that one.</summary>
    private void Rescaled(object? sender, EventArgs e)
    {
        windowScale = ((TopLevel)sender!).RenderScaling;
        Fit();
    }

    /// <summary>Works out how much of its design's size the frame fits in the pane at, asks the previewer to draw it so once
    /// the pane's size settles, and shows the frame it has meanwhile.</summary>
    private void Fit()
    {
        Size design = preview.FrameSize;
        // The edge's margin and border stand outside the frame at every scale.
        double room = 2 * (edge.Margin.Left + edge.BorderThickness.Left);
        fit = design.Width > 0 && design.Height > 0 && view.Bounds.Width > room && view.Bounds.Height > room
            ? Math.Min(1, Math.Min((view.Bounds.Width - room) / design.Width, (view.Bounds.Height - room) / design.Height))
            : 1;
        if (Math.Abs(preview.Scaling - (windowScale * fit)) >= 0.01)
        {
            redraw.Restart();
        }
        double shown = fit;
        if (preview.Frame is { } frame && design.Width > 0)
        {
            // Drawn at the scale shown its pixels land one for one; one drawn larger shrinks until the next frame.
            double drawn = frame.PixelSize.Width / design.Width / windowScale;
            shown = drawn <= fit + 0.01 ? drawn : fit;
        }
        if (scaled.LayoutTransform is not ScaleTransform { ScaleX: double was } || Math.Abs(was - shown) > 1e-9)
        {
            scaled.LayoutTransform = new ScaleTransform(shown, shown);
        }
    }

    private void Show()
    {
        status.Text = preview.Status;
        image.Source = preview.Frame;
        image.Width = preview.FrameSize.Width;
        image.Height = preview.FrameSize.Height;
        marks.Width = preview.FrameSize.Width;
        marks.Height = preview.FrameSize.Height;
        edge.IsVisible = preview.Frame is not null;
        waiting.Text = preview.Status;
        waiting.Offer(preview.WantsBuild ? AppActions.BuildForPreview : null, preview.WantsBuild ? BuildForPreviewTitle : null);
        waiting.IsVisible = preview.Frame is null;
        error.Text = preview.Error;
        errorBar.IsVisible = preview.Error is not null;
        if (preview.Frame is null)
        {
            outline.IsVisible = false;
        }
        Fit();
    }
}
