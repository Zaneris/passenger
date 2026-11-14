using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Text;
using static Passenger.Tests.XamlTests;

namespace Passenger.Tests;

/// <summary>A .NET MAUI page's preview beside its XAML, drawn by Passenger's MAUI previewer from the project's last build,
/// on the .NET 11 MAUI template's files (<see cref="MauiXamlTests.Gallery"/>, a plain net11.0 project that builds without
/// MAUI's workload): the page on a phone's screen with App.xaml's styles, the element at the caret outlined, a click on one
/// choosing its tag, a misspelt property said over the last frame, and OnPlatform and AppThemeBinding drawn as the platform
/// and theme chosen, which the window keeps for the next page.</summary>
public sealed class MauiPreviewTests : IClassFixture<MauiXamlTests.Gallery>
{
    /// <summary>How long a previewer may take to start and draw its first frame.</summary>
    private static readonly TimeSpan FirstFrame = TimeSpan.FromSeconds(60);

    /// <summary>How long a frame may take once the previewer runs.</summary>
    private static readonly TimeSpan Drawing = TimeSpan.FromSeconds(30);

    private readonly MauiXamlTests.Gallery app;

    public MauiPreviewTests(MauiXamlTests.Gallery app)
    {
        this.app = app;
        app.Reset();
    }

    [AvaloniaFact]
    public void The_template_page_previews_from_its_build_outlines_the_carets_element_chooses_one_clicked_and_says_a_misspelt_property_over_its_frame()
    {
        MainWindow window = app.Open(app.PathOf("MainPage.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        EditorGroup group = window.EditorArea.ActiveGroup;
        Assert.True(group.PreviewToggle.IsVisible && ActionTips.Of(group.PreviewToggle) == "XAML Preview",
            $"The MAUI page's tab strip offers no XAML Preview toggle (shown {group.PreviewToggle.IsVisible}, tip {ActionTips.Of(group.PreviewToggle)})");
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Ui.PlaceAfter(editor, "<Butt");
        XamlPreview preview = Previewing(window, document);
        XamlPreviewPane pane = window.PreviewPaneOf(document)!;
        Assert.True(Near(preview, new Size(390, 844)),
            $"The page of a build for no platform is drawn {preview.FrameSize}, not on Android's phone of 390 × 844");
        Assert.True(pane.Screens.IsEffectivelyVisible && pane.Platforms.IsEffectivelyVisible && pane.ThemeChooser.IsEffectivelyVisible, "The MAUI page's preview offers no screens, platforms or themes");
        Assert.Equal("Phone (390 × 844)", pane.Screens.SelectedItem);
        Assert.Equal("Android", pane.Platforms.SelectedItem);
        Assert.False(pane.Sizes.IsVisible, "The MAUI page's preview offers an Avalonia window's sizes");

        PreviewElement Around(string text) => preview.ElementAround(editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The preview placed no element around {text} ({preview.Elements.Count} placed: {preview.Status})");
        Ui.WaitFor(() => preview.Elements.Count > 0 && pane.Outlined == Around("Text=\"Click me\"").Box,
            () => $"the button at the caret outlined once the preview placed its elements ({preview.Status}; {preview.Elements.Count} placed)", Drawing);
        Assert.True(Around("Text=\"Click me\"").Box is { Width: > 100, Height: > 20 }, $"The button was placed at {Around("Text=\"Click me\"").Box}");

        PreviewElement hello = Around("Text=\"Hello, World!\"");
        Point at = pane.Frame.TranslatePoint(hello.Box.Center, window) ?? throw new InvalidOperationException("The preview's frame is not on the window");
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
        Assert.Equal(new Selection(hello.Tag.End, hello.Tag.Start), editor.Selections.Primary with { PreferredX = double.NaN });
        Assert.StartsWith("<Label", editor.Document.Snapshot.ToString()[hello.Tag.Start..], StringComparison.Ordinal);
        Assert.Equal(hello.Box, pane.Outlined);

        Replace(editor, "Text=\"Click me\"", "Txt=\"Click me\"");
        Ui.WaitFor(() => preview.Error is not null, () => $"the previewer saying why the page did not load ({preview.Status})", Drawing);
        int line = editor.Document.Snapshot.GetLineFromOffset(editor.Document.Snapshot.ToString().IndexOf("Txt=", StringComparison.Ordinal)) + 1;
        Assert.Contains("Cannot assign property \"Txt\"", preview.Error, StringComparison.Ordinal);
        Assert.Contains($"(line {line}, column ", preview.Error, StringComparison.Ordinal);
        Assert.True(preview.Frame is not null && pane.ErrorShown == preview.Error, "The error is not shown over the last frame");
        Replace(editor, "Txt=\"Click me\"", "Text=\"Click me\"");
        Ui.WaitFor(() => preview.Error is null && preview.Status == "Live", () => $"the error gone once the page loads again ({preview.Status} {preview.Error})", Drawing);
        Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(document) is null, "XAML Preview stayed on");
        window.Close();
    }

    [AvaloniaFact]
    public void OnPlatform_and_AppThemeBinding_draw_the_platform_and_theme_chosen_which_the_next_page_previews_with()
    {
        MainWindow window = app.Open(app.PathOf("PeoplePage.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        XamlPreview preview = Previewing(window, document);
        XamlPreviewPane pane = window.PreviewPaneOf(document)!;
        string ide = window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "Dark" : "Light";
        Assert.Equal(ide, pane.ThemeChooser.SelectedItem);

        // The grid's padding is OnPlatform's: 20 above its first row on iOS alone.
        double Top() => preview.ElementAround(editor.Document.Snapshot.ToString().IndexOf("{Binding Owner.Name}", StringComparison.Ordinal))?.Box.Y ?? double.NaN;
        Ui.WaitFor(() => Math.Abs(Top()) <= Pixel(preview), () => $"the owner's label at the top on Android, not at {Top()} ({preview.Status} {preview.Error})", Drawing);
        pane.Platforms.SelectedIndex = MauiLook.Platforms.Select(p => p.Shown).ToList().IndexOf("iOS");
        Ui.WaitFor(() => Math.Abs(Top() - 20) <= Pixel(preview), () => $"the owner's label under iOS's padding of 20, not at {Top()} ({preview.Status} {preview.Error})", Drawing);
        Assert.True(Near(preview, new Size(390, 844)), $"iOS's page is drawn {preview.FrameSize}, not on a phone of 390 × 844");

        // The page's background is AppThemeBinding's, white or #1f1f1f, shown above the grid's first row by iOS's padding.
        Color dark = Color.Parse("#1F1F1F");
        Color Ground(XamlPreviewPane shown, Point inFrame) =>
            Ui.Pixel(window, shown.Frame.TranslatePoint(inFrame, window) ?? throw new InvalidOperationException("The preview's frame is not on the window"));
        Point corner = new(preview.FrameSize.Width - 10, 10);
        foreach ((string theme, Color ground) in ((string, Color)[])[("Light", Colors.White), ("Dark", dark), ("Light", Colors.White), ("Dark", dark)])
        {
            pane.ThemeChooser.SelectedItem = theme;
            Ui.WaitFor(() => Ground(pane, corner) == ground, () => $"the page's background {ground} in the {theme} theme, not {Ground(pane, corner)} ({preview.Status} {preview.Error})", Drawing);
        }

        // The window keeps the choices for the next page it previews.
        window.OpenFile(app.PathOf("MainPage.xaml"));
        Ui.Settle();
        OpenDocument main = window.EditorArea.Active!;
        XamlPreview next = window.PreviewOf(main) ?? throw new InvalidOperationException("The next page opened while previewing showed no preview");
        Ui.WaitFor(() => next.Frame is not null, () => $"the next page's first frame ({next.Status} {next.Error})", FirstFrame);
        XamlPreviewPane nextPane = window.PreviewPaneOf(main)!;
        Assert.Equal("iOS", nextPane.Platforms.SelectedItem);
        Assert.Equal("Dark", nextPane.ThemeChooser.SelectedItem);
        Point below = new(next.FrameSize.Width / 2, next.FrameSize.Height - 20);
        Ui.WaitFor(() => Ground(nextPane, below) == dark, () => $"the next page dark as chosen, not {Ground(nextPane, below)}", Drawing);
        Assert.Equal("Desktop (1280 × 800)", nextPane.Screens.Items.Cast<string>().Last());
        nextPane.Screens.SelectedIndex = 2;
        Ui.WaitFor(() => Near(next, new Size(1280, 800)), () => $"the page on a desktop's screen, not {next.FrameSize} ({next.Status} {next.Error})", Drawing);
        Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(main) is null, "XAML Preview stayed on");
        window.Close();
    }

    /// <summary>In the XAML workspace a MAUI page opens beside its preview, which shows under its frame what the caret's
    /// element sets itself, as its XAML says it.</summary>
    [AvaloniaFact]
    public void In_the_xaml_workspace_a_maui_page_shows_what_the_carets_element_sets_itself_under_its_preview()
    {
        MainWindow window = app.Open(app.PathOf("MainPage.xaml"));
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<Butt");
        window.ApplySettings(window.Settings with { XamlWorkspace = true });
        Ui.WaitFor(() => window.PreviewPaneOf(document) is { PropertiesShown.Element: "Button #CounterBtn" },
            () => $"the button's own values under the preview, where it shows {window.PreviewPaneOf(document)?.PropertiesShown.Element}");
        XamlPreviewPane pane = window.PreviewPaneOf(document)!;
        Assert.Contains(("Text", "Click me", "Local"), pane.PropertiesShown.Values);
        Assert.Contains(pane.PropertiesShown.Values, v => v.Property == "HorizontalOptions" && v.Value == "Fill");
        XamlPreview preview = window.PreviewOf(document)!;
        Ui.WaitFor(() => preview.Frame is not null, () => $"the page's frame beside it ({preview.Status} {preview.Error})", FirstFrame);
        window.ApplySettings(window.Settings with { XamlWorkspace = false });
        Ui.Settle();
        if (window.PreviewOf(document) is not null)
        {
            Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(document) is null, "XAML Preview stayed on");
        }
        window.Close();
    }

    /// <summary>A colour edited in Colors.xaml and not saved is drawn in the page that takes it through App.xaml's merged
    /// dictionaries, which the build's compiled dictionary would otherwise give the previewer as it was built.</summary>
    [AvaloniaFact]
    public void An_open_dictionarys_unsaved_colour_is_drawn_in_the_page_that_takes_it_through_app_xaml()
    {
        MainWindow window = app.Open(app.PathOf("MainPage.xaml"));
        OpenDocument page = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        string colours = app.PathOf("Resources/Styles/Colors.xaml");
        window.OpenFile(colours);
        Ui.Settle();
        Replace(window.EditorArea.CodeEditor, "<Color x:Key=\"Primary\">#512BD4</Color>", "<Color x:Key=\"Primary\">#E2A33F</Color>");
        window.OpenFile(app.PathOf("MainPage.xaml"));
        Ui.Settle();
        XamlPreview preview = Previewing(window, page);
        XamlPreviewPane pane = window.PreviewPaneOf(page)!;
        pane.ThemeChooser.SelectedItem = "Light";
        Color orange = Color.Parse("#E2A33F");
        Color Button() => preview.ElementAround(page.Text.Snapshot.ToString().IndexOf("Text=\"Click me\"", StringComparison.Ordinal)) is { } button
            && pane.Frame.TranslatePoint(new Point(button.Box.X + 6, button.Box.Center.Y), window) is { } inside ? Ui.Pixel(window, inside) : default;
        Ui.WaitFor(() => Button() == orange, () => $"the button drawn in Colors.xaml's unsaved Primary, not {Button()} ({preview.Status} {preview.Error})", Drawing);
        Assert.DoesNotContain("#E2A33F", File.ReadAllText(colours), StringComparison.Ordinal);
        Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(page) is null, "XAML Preview stayed on");
        window.Close();
    }

    /// <summary>A MAUI project of a platform alone, which this machine may lack the SDK to build for (Android's JDK), is built
    /// by Build for XAML Preview for no platform into a folder of Passenger's own, its own bin left as it was, and its page
    /// previews from that build. Skips, saying why, where the project does not restore from the package cache alone (no
    /// Android workload, or MAUI's packages for it never restored here: tests/MauiGraphs).</summary>
    [AvaloniaFact]
    public void Build_for_xaml_preview_builds_a_maui_project_of_a_platform_alone_for_none_and_its_page_previews_from_that()
    {
        string folder = PathNames.Canonical(TestFolders.Create("maui-plain"));
        string version = AvaloniaApp.Metadata("MauiVersion");
        File.WriteAllText(Path.Combine(folder, "Phone.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net{Passenger.App.Mobile.MobilePlatforms.FirstOnCoreClr}.0-android</TargetFrameworks>
                <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
                <OutputType>Library</OutputType>
                <RootNamespace>Phone</RootNamespace>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Maui.Controls" Version="{version}" />
              </ItemGroup>
            </Project>

            """);
        File.WriteAllText(Path.Combine(folder, "App.xaml"), "<Application x:Class=\"Phone.App\" />\n");
        File.WriteAllText(Path.Combine(folder, "App.xaml.cs"), "namespace Phone;\n\npublic partial class App : Application\n{\n    public App()\n    {\n        InitializeComponent();\n    }\n}\n");
        File.WriteAllText(Path.Combine(folder, "MainPage.xaml"), """
            <ContentPage x:Class="Phone.MainPage">
                <VerticalStackLayout Padding="30" Spacing="20">
                    <Label Text="Built for no platform" FontSize="24" />
                    <Button Text="Go" />
                </VerticalStackLayout>
            </ContentPage>

            """);
        File.WriteAllText(Path.Combine(folder, "MainPage.xaml.cs"), "namespace Phone;\n\npublic partial class MainPage : ContentPage\n{\n    public MainPage()\n    {\n        InitializeComponent();\n    }\n}\n");
        ChildResult restored = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["restore", "Phone.csproj"], folder), limit: Restoring)),
            "the Android project's restore", Restoring);
        if (!restored.Succeeded)
        {
            Assert.Skip("A MAUI project built for Android does not restore here from the package cache alone (its workload, and MAUI's packages for it "
                + $"restored by tests/MauiGraphs): {restored.Output.Split('\n').FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal))?.Trim() ?? restored.FirstLine}");
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(Path.Combine(folder, "MainPage.xaml"));
        Ui.Settle();
        OpenDocument document = window.EditorArea.Active!;
        Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
        Ui.Settle();
        XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
        Ui.WaitFor(() => preview.WantsBuild, () => $"the preview asking for a build of the project that has none ({preview.Status})", FirstFrame);
        Assert.True(window.Perform(AppActions.BuildForPreview), $"Build for XAML Preview could not run: {window.Status}");
        Ui.WaitFor(() => preview.Frame is not null, () => $"the page drawn from the build for no platform ({preview.Status} {preview.Error}; the Build window: {window.BottomPanel.Build.Document.Snapshot})",
            Restoring);
        Assert.Null(preview.Error);
        Assert.False(Directory.Exists(Path.Combine(folder, "bin")), "Build for XAML Preview built into the project's own bin, which its own builds keep");
        Assert.Contains(preview.Elements, e => document.Text.Snapshot.ToString()[e.Tag.Start..].StartsWith("<Button", StringComparison.Ordinal));
        Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(document) is null, "XAML Preview stayed on");
        window.Close();
    }

    /// <summary>How long a project's restore or its build for the preview may take.</summary>
    private static readonly TimeSpan Restoring = TimeSpan.FromMinutes(4);

    /// <summary>Whether a preview's frame is a screen of this size in its units: drawn smaller for the pane, it is whole
    /// pixels, so its size stands up to two of its pixels off.</summary>
    private static bool Near(XamlPreview preview, Size wanted) =>
        Math.Abs(preview.FrameSize.Width - wanted.Width) <= 2 * Pixel(preview) && Math.Abs(preview.FrameSize.Height - wanted.Height) <= 2 * Pixel(preview);

    /// <summary>A pixel of the preview's frame in its units, which the layout places what it draws on.</summary>
    private static double Pixel(XamlPreview preview) => preview.Frame is { } frame ? preview.FrameSize.Width / frame.PixelSize.Width : 1;

    /// <summary>XAML Preview turned on beside a file, and its first frame waited for.</summary>
    private static XamlPreview Previewing(MainWindow window, OpenDocument document)
    {
        Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
        Ui.Settle();
        XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
        Ui.WaitFor(() => preview.Frame is not null, () => $"the previewer's first frame; the preview says: {preview.Status} {preview.Error}", FirstFrame);
        Assert.Null(preview.Error);
        Assert.Equal("Live", preview.Status);
        return preview;
    }
}
