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
/// any scale; the frame lands pixel for pixel on the screen. Where the previewer says which element is where, a click on the frame chooses
/// the element under it and the element at the editor's caret is outlined; under the frame, what an element sets itself, as
/// its XAML says it (<see cref="ShowProperties"/>).</summary>
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

    /// <summary>The size the root is drawn at: its design's, a phone's or a tablet's, shown while the previewer takes one.</summary>
    private readonly ComboBox sizes;

    /// <summary>What a MAUI page is drawn as: its screen, platform and theme, shown while its previewer is MAUI's.</summary>
    private readonly ComboBox screens;
    private readonly ComboBox platforms;
    private readonly ComboBox themes;

    /// <summary>Whether the choosers are being set to what the preview draws, which chooses nothing.</summary>
    private bool showingLook;

    /// <summary>The chosen element's own values, under the frame, while it shows them.</summary>
    private readonly Border properties;
    private readonly TextBlock propertiesTitle;
    private readonly StackPanel propertyRows;

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
        sizes = new ComboBox
        {
            [DockProperty] = Dock.Right,
            Classes = { StyleClasses.Compact },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            IsVisible = false,
            ItemsSource = (string[])[DesignSize, .. Passenger.Xaml.Features.PreviewSize.Offered.Select(s => s.Shown)],
            SelectedIndex = 0,
        };
        ToolTip.SetTip(sizes, "The size the preview draws its root at");
        sizes.SelectionChanged += (_, _) => preview.Size = sizes.SelectedIndex > 0 ? Passenger.Xaml.Features.PreviewSize.Offered[sizes.SelectedIndex - 1] : null;
        screens = Chooser([.. MauiLook.Screens.Select(s => s.Shown)], "The screen the page is drawn on, and the idiom OnIdiom reads there");
        platforms = Chooser([.. MauiLook.Platforms.Select(p => p.Shown)], "The platform OnPlatform reads");
        themes = Chooser([.. Themes], "The theme AppThemeBinding reads");
        screens.SelectionChanged += (_, _) => Choose(preview.Look with { Screen = screens.SelectedIndex >= 0 ? MauiLook.Screens[screens.SelectedIndex] : null });
        platforms.SelectionChanged += (_, _) => Choose(preview.Look with { Platform = platforms.SelectedIndex >= 0 ? MauiLook.Platforms[platforms.SelectedIndex].Platform : null });
        themes.SelectionChanged += (_, _) => Choose(preview.Look with { Theme = themes.SelectedIndex >= 0 ? Themes[themes.SelectedIndex] : null });
        Border header = ToolWindows.ToolBars.OverEditor(new DockPanel
        {
            Margin = new Thickness(10, 0),
            Children =
            {
                new TextBlock { Text = "Preview", FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) },
                sizes,
                themes,
                platforms,
                screens,
                status,
            },
        });
        error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        error.Classes.Add(StyleClasses.Error);
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
        propertiesTitle = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 12 };
        propertyRows = new StackPanel();
        TextBlock unknown = new()
        {
            Text = "What a style, theme or template gives it, what it inherits and its defaults show in the Live Visual Tree once its app runs.",
            Classes = { StyleClasses.Muted },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        properties = new Border
        {
            [DockProperty] = Dock.Bottom,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 6),
            // One height for every element, so the frame above stays where it is as the caret moves.
            Height = 200,
            IsVisible = false,
            Child = new ScrollViewer { Content = new StackPanel { Spacing = 4, Children = { propertiesTitle, propertyRows, unknown } } },
        };
        properties.Bind(Border.BorderBrushProperty, properties.GetResourceObservable(TokenKeys.Divider));
        Children.Add(header);
        Children.Add(errorBar);
        Children.Add(properties);
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

    /// <summary>A MAUI page's screen, platform or theme was chosen; raised on the UI thread with what the preview now draws.</summary>
    public event EventHandler<MauiLook>? LookChosen;

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

    /// <summary>The chooser of the size the root is drawn at.</summary>
    public ComboBox Sizes => sizes;

    /// <summary>The choosers of a MAUI page's screen, platform and theme.</summary>
    public ComboBox Screens => screens;

    public ComboBox Platforms => platforms;

    public ComboBox ThemeChooser => themes;

    /// <summary>The themes a MAUI page is drawn in, as the chooser names them and as the previewer is told them.</summary>
    private static readonly string[] Themes = [Passenger.MauiPreview.MauiPreviewThemes.Light, Passenger.MauiPreview.MauiPreviewThemes.Dark];

    /// <summary>The chooser's first item: the root at the size its XAML designs it at.</summary>
    public const string DesignSize = "Design size";

    /// <summary>Build for XAML Preview's title, which the pane's link shows.</summary>
    public const string BuildForPreviewTitle = "Build for XAML Preview";

    /// <summary>How far the outline stands outside the box it outlines.</summary>
    public const double OutlineWidth = 2;

    /// <summary>The error shown over the frame; null while there is none.</summary>
    public string? ErrorShown => errorBar.IsVisible ? error.Text : null;

    /// <summary>The element whose own values show under the frame, and each value as its row shows it (property, value,
    /// where it comes from); none while they are hidden.</summary>
    public (string? Element, IReadOnlyList<(string Property, string Value, string From)> Values) PropertiesShown { get; private set; } = (null, []);

    /// <summary>Shows what an element of the file sets itself under the frame, as its XAML says it, or hides them (null).</summary>
    public void ShowProperties(string? element, IReadOnlyList<(string Property, string Value, string From)> values)
    {
        if (PropertiesShown.Element == element && PropertiesShown.Values.SequenceEqual(values))
        {
            return;
        }
        PropertiesShown = (element, values);
        properties.IsVisible = element is not null;
        propertiesTitle.Text = element;
        propertyRows.Children.Clear();
        foreach ((string property, string value, string from) in values)
        {
            propertyRows.Children.Add(new DockPanel
            {
                Height = ListRow.Height,
                Children =
                {
                    new TextBlock { [DockProperty] = Dock.Right, Text = from, Classes = { StyleClasses.Muted }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) },
                    new TextBlock { [DockProperty] = Dock.Left, Text = property, Width = 130, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = value, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }.InCodeFont(),
                },
            });
        }
        if (values.Count == 0)
        {
            propertyRows.Children.Add(new TextBlock { Text = "Its XAML sets nothing itself.", Classes = { StyleClasses.Muted }, FontSize = 12 });
        }
    }

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

    /// <summary>A chooser of the header's, hidden until the previewer takes what it chooses.</summary>
    private static ComboBox Chooser(string[] items, string tip)
    {
        ComboBox chooser = new()
        {
            [DockProperty] = Dock.Right,
            Classes = { StyleClasses.Compact },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            IsVisible = false,
            ItemsSource = items,
        };
        ToolTip.SetTip(chooser, tip);
        return chooser;
    }

    /// <summary>A chooser's choice, made the preview's unless the chooser is only being shown what it draws.</summary>
    private void Choose(MauiLook look)
    {
        if (showingLook || look == preview.Look)
        {
            return;
        }
        preview.Look = look;
        LookChosen?.Invoke(this, look);
    }

    /// <summary>The choosers shown what a MAUI page is drawn as: its own choice, else its build's platform, that platform's
    /// kind of screen and the IDE's theme.</summary>
    private void ShowLook()
    {
        bool looks = preview.TakesLooks;
        screens.IsVisible = looks;
        platforms.IsVisible = looks;
        themes.IsVisible = looks;
        if (!looks)
        {
            return;
        }
        MauiLook look = preview.Look;
        string platform = look.Platform ?? preview.BuiltPlatform;
        string theme = look.Theme ?? (ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? Themes[1] : Themes[0]);
        showingLook = true;
        screens.SelectedIndex = MauiLook.Screens.ToList().IndexOf(look.Screen ?? MauiLook.ScreenFor(platform));
        platforms.SelectedIndex = MauiLook.Platforms.Select(p => p.Platform).ToList().IndexOf(platform);
        themes.SelectedIndex = Array.IndexOf(Themes, theme);
        showingLook = false;
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
        sizes.IsVisible = preview.TakesSizes;
        ShowLook();
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
