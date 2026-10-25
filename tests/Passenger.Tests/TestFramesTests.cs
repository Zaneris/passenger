using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Testing;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The headless Avalonia tests these classes run: an xunit v3 project on the Testing Platform and the same on
/// VSTest, each with a test whose window (a red box on white, a yellow popup over it) fails while a file says to, a test
/// that attaches a frame of its own and passes, and one that passes having caught an exception. Written and restored once
/// for the class, from the packages the tests' own restore brought and no feed but the run's own.</summary>
public sealed class HeadlessTestsFolder : IDisposable
{
    public HeadlessTestsFolder()
    {
        Folder = PathNames.Canonical(TestFolders.Create("frames-"));
        Failing = Path.Combine(Folder, "fail.txt");
        TestFolders.Prepare(() =>
        {
            string xunit = typeof(FactAttribute).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            string avalonia = string.Concat(new[] { "Avalonia.Headless.XUnit", "Avalonia.Skia", "Avalonia.Themes.Fluent" }
                .Select(p => $"    <PackageReference Include=\"{p}\" Version=\"{AvaloniaApp.Version}\" />\n"));
            Write("Boxes.Tests/Boxes.Tests.csproj", Project(xunit, avalonia, vsTest: false));
            Write("Boxes.Tests/BoxTests.cs", Tests("Boxes.Tests"));
            Write("Boxes.VsTests/Boxes.VsTests.csproj", Project(xunit, avalonia, vsTest: true));
            Write("Boxes.VsTests/BoxTests.cs", Tests("Boxes.VsTests"));
            Write("Boxes.slnx", "<Solution>\n  <Project Path=\"Boxes.Tests/Boxes.Tests.csproj\" />\n  <Project Path=\"Boxes.VsTests/Boxes.VsTests.csproj\" />\n</Solution>\n");
            AvaloniaApp.UseFeed(Folder);
            Restore.Run(Path.Combine(Folder, "Boxes.slnx"));
        }, Folder);
    }

    public string Folder { get; }

    /// <summary>While this file is there, the box test fails.</summary>
    public string Failing { get; }

    public void Dispose() => TestFolders.Delete(Folder);

    /// <summary>xunit v3 on the platform alone, or on VSTest (no platform application, VSTest's SDK and xunit's adapter).</summary>
    private static string Project(string xunit, string avalonia, bool vsTest) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <OutputType>Exe</OutputType>\n" +
        "    <ImplicitUsings>enable</ImplicitUsings>\n" + (vsTest ? "    <IsTestingPlatformApplication>false</IsTestingPlatformApplication>\n" : string.Empty) +
        "  </PropertyGroup>\n  <ItemGroup>\n" + $"    <PackageReference Include=\"xunit.v3\" Version=\"{xunit}\" />\n" + avalonia +
        (vsTest ? $"    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{AvaloniaApp.Metadata("TestSdkVersion")}\" />\n"
            + $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{AvaloniaApp.Metadata("XunitRunnerVersion")}\" />\n" : string.Empty) +
        "  </ItemGroup>\n</Project>\n";

    private string Tests(string space) => $$"""
        using Avalonia;
        using Avalonia.Controls;
        using Avalonia.Controls.Primitives;
        using Avalonia.Controls.Shapes;
        using Avalonia.Headless;
        using Avalonia.Headless.XUnit;
        using Avalonia.Media;
        using Avalonia.Media.Imaging;
        using Avalonia.Themes.Fluent;
        using Xunit;

        [assembly: AvaloniaTestApplication(typeof({{space}}.TestApp))]

        namespace {{space}};

        public sealed class TestApp : Application
        {
            public override void Initialize() => Styles.Add(new FluentTheme());

            public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        }

        public sealed class BoxTests : IDisposable
        {
            private readonly List<Window> windows = [];

            // As Passenger's own tests do: the windows go with the test, after its result is known.
            public void Dispose()
            {
                foreach (Window window in windows)
                {
                    window.Close();
                }
            }

            private Window Show(IBrush box)
            {
                Canvas canvas = new() { Background = Brushes.White };
                Rectangle shape = new() { Width = 80, Height = 60, Fill = box };
                Canvas.SetLeft(shape, 100);
                Canvas.SetTop(shape, 50);
                canvas.Children.Add(shape);
                Window window = new() { Width = 400, Height = 300, Title = "Boxes", Content = canvas };
                windows.Add(window);
                window.Show();
                return window;
            }

            [AvaloniaFact]
            public void The_box_is_where_it_was_put()
            {
                Window window = Show(Brushes.Red);
                Popup popup = new()
                {
                    PlacementTarget = (Control)window.Content!,
                    Placement = PlacementMode.AnchorAndGravity,
                    PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft,
                    PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
                    HorizontalOffset = 250,
                    VerticalOffset = 150,
                    Child = new Border { Width = 40, Height = 40, Background = Brushes.Yellow },
                };
                ((Canvas)window.Content!).Children.Add(popup);
                popup.IsOpen = true;
                Assert.False(File.Exists(@"{{Failing}}"), "The box is not where it was put");
            }

            [AvaloniaFact]
            public void Draws_its_own_frame()
            {
                Window window = Show(Brushes.Blue);
                using WriteableBitmap frame = window.CaptureRenderedFrame()!;
                using MemoryStream png = new();
                frame.Save(png);
                TestContext.Current.AddAttachment("Blue box", png.ToArray(), "image/png");
            }

            [AvaloniaFact]
            public void Passes_whatever_it_caught()
            {
                Show(Brushes.Green);
                Assert.Throws<InvalidOperationException>(Throw);
            }

            private static void Throw() => throw new InvalidOperationException("as expected");
        }
        """;

    private void Write(string relative, string text) => TestFolders.Write(Folder, relative, text);
}

/// <summary>A failing Avalonia headless test shows what its windows looked like as it failed: run from the Unit Tests
/// window, through the Testing Platform and through VSTest, its result carries a frame of its window drawn by the
/// headless platform (the popup over it in its place), taken before its cleanup closed the window; the window shows it
/// over the result's details, and a click opens it at its size to zoom. A test's own picture shows the same way; a
/// passing test's caught exception draws nothing; and the frames go with the next run.</summary>
public sealed class TestFramesTests(HeadlessTestsFolder project) : IClassFixture<HeadlessTestsFolder>, IDisposable
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);
    private static readonly Color Yellow = Color.FromRgb(255, 255, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    /// <summary>The frames the runs wrote (xunit writes them in the temp folder), removed with the test.</summary>
    private readonly List<string> frames = [];

    public void Dispose()
    {
        foreach (string frame in frames)
        {
            File.Delete(frame);
            if (Path.GetDirectoryName(frame) is { } folder && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        File.Delete(project.Failing);
    }

    [AvaloniaFact]
    public void A_failing_headless_tests_result_carries_its_windows_frame_on_the_testing_platform_and_on_vstest()
    {
        File.WriteAllText(project.Failing, "fail");
        MainWindow window = Open();
        string runnerLog = Path.Combine(IdeErrors.LogFolder, "vstest.log");
        IdeErrors.Flush();
        int logged = Ui.Shared(runnerLog).Length;

        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Methods(window, "The_box_is_where_it_was_put").Count == 2
            && Methods(window, "The_box_is_where_it_was_put").All(m => m.State is TestState.Passed or TestState.Failed), "the run of both projects' tests", TimeSpan.FromSeconds(240));
        // The project that is no Testing Platform application ran through vstest.console.
        IdeErrors.Flush();
        Assert.Contains("vstest.console of the SDK", Ui.Shared(runnerLog)[Math.Min(logged, Ui.Shared(runnerLog).Length)..], StringComparison.Ordinal);
        foreach (TestNode failed in Methods(window, "The_box_is_where_it_was_put"))
        {
            string runner = failed.ProjectPath!.Contains("VsTests", StringComparison.Ordinal) ? "VSTest" : "the Testing Platform";
            Assert.True(failed.State == TestState.Failed, $"The box test did not fail on {runner}, so it shows no frame:\n{failed.Details}");
            (TestOutcome _, TestAttachment frame) = Assert.Single(failed.Frames);
            frames.Add(frame.Path);
            Assert.Equal("Boxes", frame.Name);
            using Bitmap picture = new(frame.Path);
            // The window as the assertion failed, its popup drawn over it, though Dispose closed the window right after.
            Assert.Equal(new PixelSize(400, 300), picture.PixelSize);
            Assert.True(Pixel(picture, 140, 80) == Red, $"The frame on {runner} has {Pixel(picture, 140, 80)} where the red box is");
            Assert.True(Pixel(picture, 20, 20) == White, $"The frame on {runner} has {Pixel(picture, 20, 20)} where the window is white");
            Assert.True(Pixel(picture, 270, 170) == Yellow, $"The frame on {runner} has {Pixel(picture, 270, 170)} where the popup is over the window");
            Assert.Contains($"Attachments:\n  Boxes: {frame.Path}", failed.Details, StringComparison.Ordinal);
        }
        foreach (TestNode own in Methods(window, "Draws_its_own_frame"))
        {
            Assert.Equal(TestState.Passed, own.State);
            (TestOutcome _, TestAttachment frame) = Assert.Single(own.Frames);
            frames.Add(frame.Path);
            Assert.Equal("Blue box", frame.Name);
            using Bitmap picture = new(frame.Path);
            Assert.Equal(Blue, Pixel(picture, 140, 80));
        }
        Assert.All(Methods(window, "Passes_whatever_it_caught"), m => Assert.True(m.State == TestState.Passed && m.Results.All(r => r.Attachments.Count == 0),
            $"A test that passed having caught an exception carries {m.Results.Sum(r => r.Attachments.Count)} attachments"));

        TestsView tests = window.BottomPanel.Tests;
        tests.Choose(Methods(window, "The_box_is_where_it_was_put").First(m => m.ProjectPath!.Contains("VsTests", StringComparison.Ordinal)));
        Ui.Settle();
        Assert.True(tests.Frames.IsVisible, "The failed test's frame is not shown over its details");
        ShownFrame shown = Assert.Single(tests.Frames.Frames);
        Assert.Equal(new PixelSize(400, 300), shown.Picture!.PixelSize);
        Assert.Single(Thumbnails(tests));
        // The details keep about half the room they share with the strip, its thumbnail drawn smaller in a short window.
        Ui.Settle();
        Assert.True(tests.Details.Bounds.Height >= (tests.Details.Bounds.Height + tests.Frames.Bounds.Height) * 0.4,
            $"The failed test's details are {tests.Details.Bounds.Height:0.#} px tall under a frame strip of {tests.Frames.Bounds.Height:0.#}: the strip leaves them cramped");
        tests.Choose(Methods(window, "Passes_whatever_it_caught")[0]);
        Ui.Settle();
        Assert.False(tests.Frames.IsVisible, "A passing test with no pictures still shows the frames of the one chosen before");
        window.Close();
    }

    [AvaloniaFact]
    public void A_frame_opens_at_its_size_zooms_and_goes_with_the_run_after_the_test_passes()
    {
        File.WriteAllText(project.Failing, "fail");
        MainWindow window = Open();
        TestNode box = Methods(window, "The_box_is_where_it_was_put").First(m => !m.ProjectPath!.Contains("VsTests", StringComparison.Ordinal));
        Ui.Launched(window, window.Tests.RunAsync(window.Folder!, [box]), "the box test's run", TimeSpan.FromSeconds(240));
        Assert.True(box.State == TestState.Failed, $"The box test did not fail, so it shows no frame:\n{box.Details}");
        frames.AddRange(box.Frames.Select(f => f.Frame.Path));
        TestsView tests = window.BottomPanel.Tests;
        tests.Choose(box);
        Ui.Settle();

        Ui.Click(Assert.Single(Thumbnails(tests)));
        FrameViewer viewer = Assert.Single(window.OwnedWindows.OfType<FrameViewer>());
        Assert.Equal("Boxes — The_box_is_where_it_was_put", viewer.Title);
        Assert.Equal(new Size(400, 300), viewer.DrawnSize);
        Assert.Equal("100%", Zoom(viewer));
        // Rider's image editor keys zoom by its steps, and back to the actual size.
        Ui.Press(viewer, Command("EQUALS"));
        Assert.Equal(new Size(500, 375), viewer.DrawnSize);
        Assert.Equal("125%", Zoom(viewer));
        Ui.Press(viewer, Command("MINUS"));
        Ui.Press(viewer, Command("MINUS"));
        Assert.Equal(new Size(300, 225), viewer.DrawnSize);
        Ui.Press(viewer, Command("SLASH"));
        Assert.Equal(new Size(400, 300), viewer.DrawnSize);
        Ui.Press(viewer, Command("EQUALS"));
        Ui.Press(viewer, Command("EQUALS"));
        Ui.Press(viewer, Command("EQUALS"));
        Assert.Equal(new Size(800, 600), viewer.DrawnSize);
        Image image = viewer.GetVisualDescendants().OfType<Image>().Single();
        Point boxInViewer = image.TranslatePoint(new Point(140 * 2, 80 * 2), viewer)!.Value;
        Assert.True(Ui.Pixel(viewer, boxInViewer) == Red, $"The viewer at 200% draws {Ui.Pixel(viewer, boxInViewer)} where the red box is");
        Ui.Press(viewer, "Escape");
        Assert.Empty(window.OwnedWindows.OfType<FrameViewer>());

        File.Delete(project.Failing);
        Ui.Launched(window, window.Tests.RunAsync(window.Folder!, [box]), "the box test's second run", TimeSpan.FromSeconds(240));
        Assert.Equal(TestState.Passed, box.State);
        Assert.Empty(box.Frames);
        Ui.Settle();
        Assert.False(tests.Frames.IsVisible, "The frame of the run before still shows after a run in which the test passed");
        window.Close();
    }

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(project.Folder, languageServer: false);
        window.Perform(AppActions.ShowTests);
        Ui.Settle();
        Ui.WaitFor(() => Methods(window, "The_box_is_where_it_was_put").Count == 2, "both projects' tests read from the code");
        return window;
    }

    private static List<TestNode> Methods(MainWindow window, string name) =>
        [.. window.Tests.Tree.Methods.Where(m => m.Name == name)];

    private static List<Button> Thumbnails(TestsView tests) =>
        [.. tests.Frames.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("frame"))];

    /// <summary>A key with the platform's command modifier, as Rider's keymaps write it.</summary>
    private static string Command(string key) => OperatingSystem.IsMacOS() ? $"meta {key}" : $"control {key}";

    private static string Zoom(FrameViewer viewer) =>
        viewer.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).First(t => t?.EndsWith('%') == true)!;

    /// <summary>A picture's colour at a pixel.</summary>
    private static Color Pixel(Bitmap picture, int x, int y)
    {
        byte[] pixel = new byte[4];
        GCHandle pinned = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try
        {
            picture.CopyPixels(new PixelRect(x, y, 1, 1), pinned.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            pinned.Free();
        }
        return picture.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromRgb(pixel[0], pixel[1], pixel[2]) : Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }
}
