using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.App;
using Passenger.App.LiveTree;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;
using static Passenger.Tests.XamlTests;

namespace Passenger.Tests;

/// <summary>WPF's previewer through the window, on Windows: <see cref="WpfXamlTests"/>' <c>dotnet new wpf</c> app (App.xaml's
/// merged brushes and heading style, a <c>d:DataContext</c> view model, a button template drawing its background) built and
/// previewed: the window with App.xaml's resources, an edit followed, broken XAML's error with its line over the last frame,
/// a user control at its design size with its design-time data and inside a window, an element chosen by a click or the
/// caret, content finding the window or page it is drawn in, the project's code in design mode; elsewhere they skip.</summary>
public sealed class WpfPreviewTests : IClassFixture<WpfPreviewTests.Gallery>
{
    private const string OnlyOnWindows = "WPF draws only on Windows, so its previewer runs only there";

    private static readonly Color Accent = Color.Parse("#3574F0");

    private readonly Gallery app;

    public WpfPreviewTests(Gallery app)
    {
        this.app = app;
        if (OperatingSystem.IsWindows())
        {
            app.Reset();
        }
    }

    [AvaloniaFact]
    public void A_window_previews_with_app_xamls_resources_follows_an_edit_and_shows_why_broken_xaml_did_not_load_with_its_line()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        XamlPreview preview = Previewed(window, document);
        Assert.Null(preview.Error);
        Assert.Equal("Live", preview.Status);
        XamlPreviewPane pane = window.PreviewPaneOf(document)!;
        Color At(Point point) => Ui.Pixel(window, pane.Frame.TranslatePoint(point, window) ?? throw new InvalidOperationException("The preview's frame is not on the window"));

        PreviewElement button = ElementOf(preview, editor, "<Button x:Name=\"Go\"");
        Assert.True(button.Box.Width > 100, $"The button is drawn {button.Box.Width} wide, not across the window");
        Assert.Equal(Accent, At(new Point(button.Box.X + 24, button.Box.Center.Y)));

        // Below the panel's children, where its own background shows once the edit gives it one.
        PreviewElement panel = ElementOf(preview, editor, "<StackPanel Margin=\"12\"");
        Point below = new(panel.Box.Center.X, panel.Box.Bottom - 20);
        Color orange = Color.Parse("#E2A33F");
        Assert.NotEqual(orange, At(below));
        Ui.PlaceAfter(editor, "<StackPanel");
        window.KeyTextInput(" Background=\"#E2A33F\"");
        Ui.WaitFor(() => At(below) == orange, "the preview drawing the panel's new background once typing paused", TimeSpan.FromSeconds(30));

        // XAML that does not load: the previewer's XML reader ends a line at the lone '\r' the editor reads inside its line.
        Replace(editor, "Title=\"Gallery\" Height", "Title=\"Gallery\"\r Height");
        Replace(editor, "</StackPanel>", "</StackPane>");
        int line = editor.Document.Snapshot.GetLineFromOffset(editor.Document.Snapshot.ToString().IndexOf("</StackPane>", StringComparison.Ordinal)) + 1;
        Ui.WaitFor(() => preview.Error is not null, "the previewer saying why the XAML did not load", TimeSpan.FromSeconds(30));
        Assert.Contains($"(line {line}, column ", preview.Error, StringComparison.Ordinal);
        Assert.Contains("StackPane", preview.Error, StringComparison.Ordinal);
        Assert.Equal(preview.Error, pane.ErrorShown);
        Assert.Equal(orange, At(below));
        Replace(editor, "</StackPane>", "</StackPanel>");
        Ui.WaitFor(() => preview.Error is null && pane.ErrorShown is null, "the error gone once the XAML loads again", TimeSpan.FromSeconds(30));
        window.Close();
    }

    /// <summary>Its root tag holds a lone '\r', which the previewer's XML reader counts as a line's end and the editor does
    /// not, so the places its boxes come with run a line ahead of the editor's below it: each still lands on its element.</summary>
    [AvaloniaFact]
    public void A_user_control_previews_at_its_design_size_with_its_design_time_data_context()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string card = app.Write("Card.xaml", Card.Replace("d:DesignWidth=\"320\" d:DesignHeight", "d:DesignWidth=\"320\"\r d:DesignHeight", StringComparison.Ordinal));
        MainWindow window = app.Open(card);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        XamlPreview preview = Previewed(window, document);
        Assert.Null(preview.Error);
        Assert.Equal(new Size(320, 120), preview.FrameSize);
        Assert.Equal(new PixelSize(320, 120), preview.Frame!.PixelSize);
        XamlPreviewPane pane = window.PreviewPaneOf(document)!;
        Assert.Equal(Accent, Ui.Pixel(window, pane.Frame.TranslatePoint(new Point(310, 110), window)!.Value));

        PreviewElement greeting = ElementOf(preview, editor, "<TextBlock Text=\"{Binding Greeting}\"");
        PreviewElement owner = ElementOf(preview, editor, "<TextBlock Text=\"{Binding Owner.Name}\"");
        Assert.True(greeting.Box.Width > 10, $"The greeting is drawn {greeting.Box.Width} wide: the view model's \"Hello\" is not shown");
        Assert.True(owner.Box.Width > 10, $"The owner's name is drawn {owner.Box.Width} wide: the view model's \"Ada\" is not shown");

        Replace(editor, "IsDesignTimeCreatable=True", "IsDesignTimeCreatable=False");
        Ui.WaitFor(() => preview.Elements.Any(e => TagOf(editor, e).Contains("{Binding Greeting}", StringComparison.Ordinal) && e.Box.Width == 0),
            "the greeting drawn empty from the stand-in", TimeSpan.FromSeconds(30));
        Assert.Null(preview.Error);
        window.Close();
    }

    [AvaloniaFact]
    public void A_user_control_draws_its_own_xaml_in_a_window_that_uses_it_and_one_with_no_code_behind_draws_nothing_as_the_app_does()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        app.Write("Tile.xaml", Tile);
        app.Write("Tile.xaml.cs", TileCodeBehind);
        app.Write("Badge.xaml", Badge);
        string tiles = app.Write("Tiles.xaml", Tiles);
        MainWindow window = app.Open(tiles);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        XamlPreview preview = Previewed(window, document);
        Assert.Null(preview.Error);
        XamlPreviewPane pane = window.PreviewPaneOf(document)!;

        PreviewElement tile = ElementOf(preview, editor, "<local:Tile");
        Assert.True(tile.Box.Height > 20, $"The tile is drawn {tile.Box.Height} high: its own XAML did not load");
        Point inside = pane.Frame.TranslatePoint(new Point(tile.Box.Right - 6, tile.Box.Bottom - 4), window)!.Value;
        Assert.Equal(Color.Parse("#FFF4E0"), Ui.Pixel(window, inside));

        // Nothing calls a XAML-only control's InitializeComponent, so the app draws it empty, and so does its preview.
        PreviewElement badge = ElementOf(preview, editor, "<local:Badge");
        Assert.Equal(0, badge.Box.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void The_caret_outlines_its_element_from_the_first_frame_and_clicking_an_element_in_the_preview_selects_its_start_tag()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "<Button x:Name=\"G");
        XamlPreview preview = Previewed(window, document);
        XamlPreviewPane pane = window.PreviewPaneOf(document)!;
        Point OnWindow(Point inFrame) => pane.Frame.TranslatePoint(inFrame, window) ?? throw new InvalidOperationException("The preview's frame is not on the window");

        // The caret was in the button's tag before the frame came, so the first frame outlines it.
        PreviewElement button = ElementOf(preview, editor, "<Button x:Name=\"Go\"");
        Assert.Equal(button.Box, pane.Outlined);
        OutlineAround(window, pane, button);
        Point beside = OnWindow(new Point(button.Box.X - (XamlPreviewPane.OutlineWidth / 2), button.Box.Center.Y));
        Color outlined = Ui.Pixel(window, beside);

        PreviewElement heading = ElementOf(preview, editor, "<TextBlock Style=\"{StaticResource Heading}\"");
        Point at = OnWindow(new Point(heading.Box.X + 24, heading.Box.Center.Y));
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
        string text = editor.Document.Snapshot.ToString();
        int start = text.IndexOf("<TextBlock Style=\"{StaticResource Heading}\"", StringComparison.Ordinal);
        int end = text.IndexOf("/>", start, StringComparison.Ordinal) + 2;
        Assert.Equal(new Selection(end, start), editor.Selections.Primary with { PreferredX = double.NaN });
        Assert.Equal(heading.Box, pane.Outlined);
        OutlineAround(window, pane, heading);
        Assert.NotEqual(outlined, Ui.Pixel(window, beside));

        Ui.PlaceAfter(editor, "<ListBox ItemsSource");
        Ui.Settle();
        PreviewElement list = ElementOf(preview, editor, "<ListBox ItemsSource");
        Assert.Equal(list.Box, pane.Outlined);
        OutlineAround(window, pane, list);
        Ui.PlaceAfter(editor, "<Window.Resources>");
        Ui.Settle();
        Assert.Equal(ElementOf(preview, editor, "<Window x:Class").Box, pane.Outlined);
        window.Close();
    }

    /// <summary>The window is drawn as itself, never shown, so what its content finds of it as the app runs is found in the
    /// preview too.</summary>
    [AvaloniaFact]
    public void Content_previewed_in_a_window_finds_the_window_its_binding_shows_the_title_and_window_get_window_answers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        app.Write("WindowFinder.xaml", WindowFinder);
        app.Write("WindowFinder.xaml.cs", WindowFinderCodeBehind);
        string titled = app.Write("Titled.xaml", Titled);
        MainWindow window = app.Open(titled);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        XamlPreview preview = Previewed(window, document);
        Assert.Null(preview.Error);
        XamlPreviewPane pane = window.PreviewPaneOf(document)!;

        FindsItsHolder(window, preview, editor, pane, "Window");
        window.Close();
    }

    /// <summary>A page is drawn in a window, as an app's page is, so its content finds the page and that window.</summary>
    [AvaloniaFact]
    public void Content_previewed_in_a_page_finds_the_page_and_the_window_the_page_is_in()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        app.Write("WindowFinder.xaml", WindowFinder);
        app.Write("WindowFinder.xaml.cs", WindowFinderCodeBehind);
        string paged = app.Write("Paged.xaml", Paged);
        MainWindow window = app.Open(paged);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        XamlPreview preview = Previewed(window, document);
        Assert.Null(preview.Error);
        XamlPreviewPane pane = window.PreviewPaneOf(document)!;

        FindsItsHolder(window, preview, editor, pane, "Page");
        window.Close();
    }

    /// <summary>The preview runs the project's code in design mode, so a control that asks as it is made shows what it shows
    /// at design time; the app run is not in design mode, and the same control shows it is running. The window's
    /// WindowChrome, which WPF leaves to a designer in design mode, still makes the whole window its content's, as the app
    /// draws it, with no title bar drawn above.</summary>
    [AvaloniaFact]
    public void A_window_previews_its_controls_in_design_mode_its_window_chrome_kept_and_the_app_run_shows_them_running()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        app.Write("ModeSign.xaml", ModeSign);
        app.Write("ModeSign.xaml.cs", ModeSignCodeBehind);
        app.Write("MainWindow.xaml.cs", ModesCodeBehind);
        string modes = app.Write("MainWindow.xaml", Modes);
        MainWindow window = app.Open(modes);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        XamlPreview preview = Previewed(window, document);
        Assert.Null(preview.Error);
        XamlPreviewPane pane = window.PreviewPaneOf(document)!;

        Assert.True(Math.Abs(preview.FrameSize.Width - 400) <= 1 && Math.Abs(preview.FrameSize.Height - 300) <= 1,
            $"The window with a WindowChrome is drawn {preview.FrameSize}, not 400 × 300: its chrome was left out and the system's frame drawn");

        // DesignerProperties.GetIsInDesignMode(this) in its constructor: drawn as wide as "design" written out, not "running".
        PreviewElement sign = ElementOf(preview, editor, "<local:ModeSign");
        PreviewElement written = ElementOf(preview, editor, "<TextBlock Text=\"design\"");
        Assert.True(written.Box.Width > 10, $"The word \"design\" written out is drawn {written.Box.Width} wide");
        Assert.True(Math.Abs(sign.Box.Width - written.Box.Width) < 0.5,
            $"The control that says whether it is in design mode is drawn {sign.Box.Width} wide, the word \"design\" {written.Box.Width}: the preview ran it out of design mode");
        // LicenseManager.UsageMode, in its colour.
        Color licence = Ui.Pixel(window, pane.Frame.TranslatePoint(new Point(sign.Box.X + 3, sign.Box.Bottom - 3), window)!.Value);
        Assert.True(licence == Found, licence == Missing
            ? "LicenseManager.UsageMode said Runtime to the control previewed, not Designtime"
            : $"The control that says LicenseManager's usage mode is drawn {licence}, neither the colour of design time nor of run time");

        Ui.Launched(window, window.RunAsync(), "the app to build and start", TimeSpan.FromMinutes(4));
        Ui.WaitFor(() => window.Session.RunApp is { State: LiveAppState.Connected }, $"the app's inspector to say hello ({window.Status})", TimeSpan.FromMinutes(2));
        Assert.Equal("running", TextOf(window.Session.RunApp!, "Said"));
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Runner.IsRunning, "the app to stop");
        window.Close();
    }

    /// <summary>What an element of the running app's, by its name, shows as text, once its window has it.</summary>
    private static string TextOf(LiveApp running, string name)
    {
        string? shown = null;
        Task<AppTree>? asked = null;
        Ui.WaitFor(() =>
        {
            if (asked is { IsCompleted: true })
            {
                shown = asked.IsCompletedSuccessfully ? Named(asked.Result.Roots, name)?.Text : null;
                asked = null;
            }
            if (shown is null)
            {
                asked ??= running.AgentTreeAsync([]);
            }
            return shown is not null;
        }, $"the running app's window to show its {name}", TimeSpan.FromMinutes(1));
        return shown!;
    }

    private static Element? Named(IEnumerable<Element> elements, string name) =>
        elements.Select(e => e.Name == name ? e : Named(e.Children, name)).FirstOrDefault(e => e is not null);

    /// <summary>The title bound through <c>AncestorType</c> drawn as wide as the same words written out, and the user control
    /// whose Loaded asks <c>Window.GetWindow</c> drawn in the colour that says it found one.</summary>
    private static void FindsItsHolder(MainWindow window, XamlPreview preview, CodeEditor editor, XamlPreviewPane pane, string holder)
    {
        PreviewElement bound = ElementOf(preview, editor, "<TextBlock Text=\"{Binding Title");
        PreviewElement written = ElementOf(preview, editor, $"<TextBlock Text=\"{FinderTitle}\"");
        Assert.True(written.Box.Width > 20, $"The title written out is drawn {written.Box.Width} wide");
        Assert.True(Math.Abs(bound.Box.Width - written.Box.Width) < 0.5,
            $"The title bound through AncestorType={holder} is drawn {bound.Box.Width} wide, the same words written out {written.Box.Width}: the binding did not find the {holder.ToLowerInvariant()}");
        PreviewElement finder = ElementOf(preview, editor, "<local:WindowFinder");
        Point inside = pane.Frame.TranslatePoint(new Point(finder.Box.X + 3, finder.Box.Y + 3), window)!.Value;
        Color drawn = Ui.Pixel(window, inside);
        Assert.True(drawn == Found, drawn == Missing
            ? "Window.GetWindow found no window for the control in the preview, as it does in the app"
            : $"The control that asks Window.GetWindow is drawn {drawn}, neither the colour of a window found nor of none");
    }

    /// <summary>The outline is drawn on the window just around an element's box.</summary>
    private static void OutlineAround(MainWindow window, XamlPreviewPane pane, PreviewElement element)
    {
        Assert.True(pane.Outline.IsVisible, "The outline is not shown");
        Point drawn = pane.Outline.TranslatePoint(default, window)!.Value;
        Point expected = pane.Frame.TranslatePoint(element.Box.TopLeft - new Vector(XamlPreviewPane.OutlineWidth, XamlPreviewPane.OutlineWidth), window)!.Value;
        Assert.True(Math.Abs(drawn.X - expected.X) < 1 && Math.Abs(drawn.Y - expected.Y) < 1, $"The outline is drawn at {drawn}, around the element at {expected}");
    }

    /// <summary>The project built, XAML Preview turned on through Find Action, and its first frame drawn.</summary>
    private static XamlPreview Previewed(MainWindow window, OpenDocument document)
    {
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Ui.Run(window, AppActions.FindAction);
        Passenger.App.Navigation.ActionPopup popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
        popup.Query = "xaml preview";
        Ui.Settle();
        Assert.Equal(AppActions.XamlPreview, popup.Rows[0].Id);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
        Ui.WaitFor(() => preview.Frame is not null || preview.Error is not null, $"the previewer's first frame (it says: {preview.Status})", TimeSpan.FromSeconds(90));
        Ui.Settle();
        Assert.True(preview.Frame is not null, $"The previewer drew nothing: {preview.Error}");
        return preview;
    }

    /// <summary>The drawn element whose start tag begins with a stretch of the file.</summary>
    private static PreviewElement ElementOf(XamlPreview preview, CodeEditor editor, string tag)
    {
        int at = editor.Document.Snapshot.ToString().IndexOf(tag, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{tag}' is not in the file");
        return preview.Elements.FirstOrDefault(e => e.Tag.Start == at)
            ?? throw new InvalidOperationException($"The preview drew no element for {tag}; it drew: {string.Join(", ", preview.Elements.Select(e => TagOf(editor, e)))}");
    }

    private static string TagOf(CodeEditor editor, PreviewElement element)
    {
        string text = editor.Document.Snapshot.ToString();
        return element.Tag.End <= text.Length ? text[element.Tag.Start..element.Tag.End] : string.Empty;
    }

    /// <summary>A card of the view model's: its design size and a creatable design-time instance, App.xaml's brush behind.</summary>
    private const string Card = """
        <UserControl x:Class="Gallery.Card"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                     xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                     xmlns:vm="clr-namespace:Gallery.ViewModels"
                     mc:Ignorable="d"
                     d:DesignWidth="320" d:DesignHeight="120"
                     d:DataContext="{d:DesignInstance vm:MainViewModel, IsDesignTimeCreatable=True}">
            <Border Background="{StaticResource Accent}" Padding="8">
                <StackPanel>
                    <TextBlock Text="{Binding Greeting}" HorizontalAlignment="Left" Foreground="White" />
                    <TextBlock Text="{Binding Owner.Name}" HorizontalAlignment="Left" Foreground="White" />
                </StackPanel>
            </Border>
        </UserControl>

        """;

    /// <summary>A user control with its code-behind, whose constructor loads its XAML.</summary>
    private const string Tile = """
        <UserControl x:Class="Gallery.Tile"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Border Background="#FFF4E0" Padding="10">
                <TextBlock Text="A tile, its own XAML" />
            </Border>
        </UserControl>

        """;

    private const string TileCodeBehind = """
        using System.Windows.Controls;

        namespace Gallery;

        public partial class Tile : UserControl
        {
            public Tile()
            {
                InitializeComponent();
            }
        }

        """;

    /// <summary>A user control with no code-behind: MarkupCompile's class, with no constructor to load its XAML.</summary>
    private const string Badge = """
        <UserControl x:Class="Gallery.Badge"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Border Background="#E2A33F" Height="20" />
        </UserControl>

        """;

    /// <summary>A window using both.</summary>
    private const string Tiles = """
        <Window x:Class="Gallery.Tiles"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:Gallery"
                Title="Tiles" Height="300" Width="400">
            <StackPanel Margin="12">
                <local:Tile />
                <local:Badge Margin="0,8" />
            </StackPanel>
        </Window>

        """;

    /// <summary>The title of the window and the page that find themselves.</summary>
    private const string FinderTitle = "Found by its content";

    private static readonly Color Found = Color.Parse("#2E8B57");

    private static readonly Color Missing = Color.Parse("#B22222");

    /// <summary>A user control that asks for its window as it loads, and says whether it found one in its colour.</summary>
    private const string WindowFinder = """
        <UserControl x:Class="Gallery.WindowFinder"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Border x:Name="Answer" Background="#B22222" Height="30" Padding="8,0">
                <TextBlock x:Name="Said" Text="No window" VerticalAlignment="Center" Foreground="White" />
            </Border>
        </UserControl>

        """;

    private const string WindowFinderCodeBehind = """
        using System.Windows;
        using System.Windows.Controls;
        using System.Windows.Media;

        namespace Gallery;

        public partial class WindowFinder : UserControl
        {
            public WindowFinder()
            {
                InitializeComponent();
                Loaded += (_, _) =>
                {
                    if (Window.GetWindow(this) is { } window)
                    {
                        Answer.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57));
                        Said.Text = "Its window: " + window.Title;
                    }
                };
            }
        }

        """;

    /// <summary>A window whose content binds its title and asks for it.</summary>
    private const string Titled = $$$"""
        <Window x:Class="Gallery.Titled"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:Gallery"
                Title="{{{FinderTitle}}}" Height="300" Width="400">
            <StackPanel Margin="12">
                <TextBlock Text="{Binding Title, RelativeSource={RelativeSource AncestorType=Window}}" HorizontalAlignment="Left" />
                <TextBlock Text="{{{FinderTitle}}}" HorizontalAlignment="Left" />
                <local:WindowFinder Margin="0,8" />
            </StackPanel>
        </Window>

        """;

    /// <summary>A page whose content binds its title and asks for its window.</summary>
    private const string Paged = $$$"""
        <Page x:Class="Gallery.Paged"
              xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:local="clr-namespace:Gallery"
              Title="{{{FinderTitle}}}" Height="200" Width="360">
            <StackPanel Margin="12">
                <TextBlock Text="{Binding Title, RelativeSource={RelativeSource AncestorType=Page}}" HorizontalAlignment="Left" />
                <TextBlock Text="{{{FinderTitle}}}" HorizontalAlignment="Left" />
                <local:WindowFinder Margin="0,8" />
            </StackPanel>
        </Page>

        """;

    /// <summary>A user control that says, as it is made, whether it is in design mode (its words) and whether
    /// LicenseManager says design time (its colour: <see cref="Found"/> for design time, <see cref="Missing"/> for run
    /// time).</summary>
    private const string ModeSign = """
        <UserControl x:Class="Gallery.ModeSign"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Border x:Name="Licence" Background="#B22222" Padding="0,0,0,6">
                <TextBlock x:Name="Said" Foreground="White" />
            </Border>
        </UserControl>

        """;

    private const string ModeSignCodeBehind = """
        using System.ComponentModel;
        using System.Windows.Controls;
        using System.Windows.Media;

        namespace Gallery;

        public partial class ModeSign : UserControl
        {
            public ModeSign()
            {
                InitializeComponent();
                Said.Text = DesignerProperties.GetIsInDesignMode(this) ? "design" : "running";
                if (LicenseManager.UsageMode == LicenseUsageMode.Designtime)
                {
                    Licence.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57));
                }
            }
        }

        """;

    /// <summary>The app's main window in place of the gallery's: its own chrome, the control, and the word it says
    /// previewed.</summary>
    private const string Modes = """
        <Window x:Class="Gallery.MainWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:Gallery"
                Title="Modes" Height="300" Width="400">
            <WindowChrome.WindowChrome>
                <WindowChrome CaptionHeight="0" GlassFrameThickness="0" ResizeBorderThickness="0" />
            </WindowChrome.WindowChrome>
            <StackPanel Margin="12">
                <local:ModeSign HorizontalAlignment="Left" />
                <TextBlock Text="design" HorizontalAlignment="Left" />
            </StackPanel>
        </Window>

        """;

    private const string ModesCodeBehind = """
        using System.Windows;

        namespace Gallery;

        public partial class MainWindow : Window
        {
            public MainWindow()
            {
                InitializeComponent();
            }
        }

        """;

    /// <summary><see cref="WpfXamlTests.Gallery"/>'s app, made and restored only where it is previewed.</summary>
    public sealed class Gallery : SharedProject
    {
        public Gallery()
            : base("wpfpreview-")
        {
        }

        /// <summary>WPF's gallery, made and restored on Windows alone, where its previewer runs.</summary>
        public override async ValueTask InitializeAsync()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            await TestFolders.PrepareAsync(() => WpfXamlTests.Gallery.MakeAsync(Folder), Folder);
            await PrepareAsync();
        }
    }
}
