using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Editor;
using static Passenger.Tests.XamlTests;

namespace Passenger.Tests;

/// <summary>An app of two target frameworks, <see cref="TestProjects.Framework"/> and net11.0, whose window shows a marker
/// that only the newer framework gives a property: its left half says which framework's build draws it, its right half which
/// runtime runs that build. One choice of framework per project, the first by default, drives the XAML preview (that
/// framework's build however the inner builds finished, on its own runtime), XAML's completion and checks (that framework's
/// C#), and the language server's answers for the project's C# files, from a XAML file's switcher and a C# file's alike, and
/// is kept with the folder; the XAML file's problems from a build are its framework's. Avalonia's everywhere (<see cref="MultiTargetedXamlTests"/>); WPF's on Windows alone
/// (<see cref="MultiTargetedWpfPreviewTests"/>).</summary>
public sealed class MultiTargetedXamlTests : IClassFixture<MultiTargetedXamlTests.Pair>
{
    /// <summary>The newer framework, whose build the marker draws in <see cref="Newer"/>, and its symbol.</summary>
    internal const string Second = "net11.0";

    internal static readonly Color Older = Colors.Blue;
    internal static readonly Color Newer = Colors.Red;

    private readonly Pair app;

    public MultiTargetedXamlTests(Pair app)
    {
        this.app = app;
        app.Reset();
    }

    [AvaloniaFact]
    public void One_framework_chosen_per_project_drives_the_preview_completion_checks_and_csharp_and_is_kept() =>
        Check(app, "MainWindow.axaml", TestProjects.Framework, Second);

    /// <summary>A XAML error only the first framework's build finds (the marker's Note, which the second's C# alone gives it)
    /// is the XAML file's only while that framework is chosen: the build lists it as the first framework's, with the second
    /// chosen the file is marked nowhere and Problems lists nothing of it, and with the first chosen again its line is marked
    /// once more.</summary>
    [AvaloniaFact]
    public void A_xaml_error_of_one_frameworks_build_is_the_files_only_while_that_framework_is_chosen()
    {
        app.Rewrite("MainWindow.axaml", AvaloniaWindow.Replace("<local:Marker />", "<local:Marker Note=\"x\" />", StringComparison.Ordinal));
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        OpenDocument document = window.EditorArea.Active!;
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => editor.ContextLabel == TestProjects.Framework, () => $"the XAML file's switcher showing {TestProjects.Framework}, the first framework, chosen (it shows {editor.ContextLabel ?? "nothing"})");

        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Problem[] inFile = [.. window.BottomPanel.Problems.Problems.Where(p => p.Severity == ProblemSeverity.Error && p.Path is { } path && PathNames.Same(path, document.Path))];
        Assert.True(inFile.Length > 0 && inFile.All(p => p.Framework == TestProjects.Framework && p.Project == "Pair"),
            $"The build did not list the Note's error as {TestProjects.Framework}'s alone: {string.Join("; ", inFile.Select(p => $"{p.Project} {p.Framework}: {p.Message}"))}\n{window.Status}");
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), $"the Note's line marked while {TestProjects.Framework}, whose build failed on it, is chosen");

        Choose(window, editor, $"Pair ({Second})");
        Ui.WaitFor(() => document.Markers.Count == 0 && window.BottomPanel.Problems.FileProblems.Count == 0,
            () => $"the file marked nowhere with {Second}, whose build found nothing in it, chosen: {string.Join("; ", document.Markers.Select(m => m.Message))}");

        Choose(window, editor, $"Pair ({TestProjects.Framework})");
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error) && window.BottomPanel.Problems.FileProblems.Count > 0,
            $"the Note's line marked again with {TestProjects.Framework} chosen once more");
        window.Close();
    }

    /// <summary>The one flow of both dialects, on an app of the frameworks <paramref name="first"/> and <paramref name="second"/>:
    /// the app built, its first framework's output made the older of the two, then the preview, completion and checks seen
    /// in the first framework, the second chosen in the XAML file's switcher and all three seen to follow it, a C# file of the
    /// project answered for in it, and the choice found in a new window.</summary>
    internal static void Check(SharedProject app, string xaml, string first, string second)
    {
        const string marker = "<local:Marker";
        const string unknown = "Cannot resolve property 'Note' on Marker";
        string state = TestFolders.Create("frameworks-");
        string name = Path.GetFileNameWithoutExtension(Directory.EnumerateFiles(app.Folder, "*.csproj").Single());
        MainWindow window = Keeping(app, state, app.PathOf(xaml));
        OpenDocument document = window.EditorArea.Active!;
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => editor.ContextLabel == first, () => $"the XAML file's switcher showing {first}, the first framework, chosen (it shows {editor.ContextLabel ?? "nothing"})");
        Assert.Equal([$"{name} ({first})", $"{name} ({second})"], document.ProjectContexts);

        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        string[] builds = [.. Directory.EnumerateFiles(Path.Combine(app.Folder, "bin"), name + ".dll", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        Assert.True(builds.Length == 2, $"The build did not build both frameworks: {string.Join(", ", builds)}\n{window.Status}");
        // However the inner builds finished, the first framework's is made the older.
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(builds.Single(b => InFolderOf(b, first)))!))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromHours(1));
        }

        if (window.PreviewOf(document) is null)
        {
            Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
        }
        XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
        Drawn(window, document, preview, Older, $"the first framework's build ({first}), though the other's was built last");

        // Only the second framework's C# gives the marker its Note.
        Ui.PlaceAfter(editor, marker);
        window.KeyTextInput(" ");
        window.KeyTextInput("N");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Name"), "the marker's properties offered after an 'N'");
        Assert.DoesNotContain("Note", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        window.KeyTextInput("ote=\"x\"");
        Ui.WaitFor(() => document.Markers.Any(m => m.Message == unknown), $"\"{unknown}\" said of the Note {first}'s C# lacks");

        Choose(window, editor, $"{name} ({second})");
        Assert.Equal(second, editor.ContextLabel);
        Ui.WaitFor(() => !document.Markers.Any(m => m.Message == unknown), $"the Note known once {second}, whose C# gives it, was chosen");
        Drawn(window, document, preview, Newer, $"{second}'s build, chosen in the XAML file's switcher");
        Replace(editor, " Note=\"x\"", string.Empty);
        Ui.PlaceAfter(editor, marker);
        window.KeyTextInput(" ");
        window.KeyTextInput("N");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Note"), $"the Note {second}'s C# gives the marker offered after an 'N'");
        Ui.Press(window, "ESCAPE");

        window.OpenFile(app.PathOf("Marker.cs"));
        OpenDocument code = window.EditorArea.Active!;
        Ui.WaitFor(() => window.CSharpService?.For(code) is not null && code.ProjectContexts.Count == 2 && code.ProjectContext == $"{name} ({second})",
            () => $"the C# file answered for in {second}, the project's choice (it shows {code.ProjectContext ?? "nothing"} of {code.ProjectContexts.Count})", TimeSpan.FromMinutes(2));
        Assert.Equal(second, window.EditorArea.CodeEditor.ContextLabel);
        window.Close();

        MainWindow again = Keeping(app, state, app.PathOf(xaml));
        OpenDocument reopened = again.EditorArea.Active!;
        Ui.WaitFor(() => again.EditorArea.CodeEditor.ContextLabel == second, $"the next window's switcher showing {second}, the choice kept");
        if (again.PreviewOf(reopened) is null)
        {
            Assert.True(again.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {again.Status}");
        }
        Drawn(again, reopened, again.PreviewOf(reopened)!, Newer, $"{second}'s build in the next window, the choice kept");
        again.Close();
    }

    /// <summary>Whether a build lies in the folder of a framework's output.</summary>
    private static bool InFolderOf(string build, string framework) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(build)), framework, StringComparison.OrdinalIgnoreCase);

    /// <summary>Waits for the preview to draw the marker in a framework's colour: its left half the build's, its right half
    /// the runtime's, which is the build's own.</summary>
    private static void Drawn(MainWindow window, OpenDocument document, XamlPreview preview, Color colour, string what)
    {
        Image frame = window.PreviewPaneOf(document)!.Frame;
        Color At(double x) => Ui.Pixel(window, frame.TranslatePoint(new Point(frame.Bounds.Width * x, frame.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The preview's frame is not on the window"));
        Ui.WaitFor(() => preview.Frame is not null && At(0.25) == colour && At(0.75) == colour,
            () => $"the preview drawing {what} (it says: {preview.Status}{(preview.Error is { } error ? $"; {error}" : string.Empty)})", TimeSpan.FromSeconds(90));
    }

    /// <summary>A window keeping its state in <paramref name="state"/>, on the app's folder, with a file open and C# ready.</summary>
    private static MainWindow Keeping(SharedProject app, string state, string file)
    {
        MainWindow window = new() { Width = 1400, Height = 800, Autosaves = false, WarmServer = app.Server, StateFolder = state };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(app.Folder);
        window.OpenFile(file);
        Ui.Settle();
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the project ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        return window;
    }

    /// <summary>Clicks the editor's switcher and the framework in the menu it opens.</summary>
    private static void Choose(MainWindow window, CodeEditor editor, string context)
    {
        Rect bounds = editor.ContextSwitcherBounds;
        Assert.True(bounds.Width > 0, "The editor shows no target framework switcher for a file of a project of two");
        Ui.Click(editor, bounds.Center);
        Ui.Choose(Assert.IsType<ContextMenu>(window.OpenContextMenu), context);
        Ui.Settle();
    }

    /// <summary>The marker's code: its left half coloured by the framework compiled, its right half by the runtime that runs
    /// it, and a property only the newer framework compiles.</summary>
    internal const string AvaloniaMarker = """
        using System;
        using Avalonia.Controls;
        using Avalonia.Media;

        namespace Pair;

        public class Marker : Grid
        {
            public Marker()
            {
                ColumnDefinitions = new ColumnDefinitions("*,*");
        #if NET11_0_OR_GREATER
                IBrush built = Brushes.Red;
        #else
                IBrush built = Brushes.Blue;
        #endif
                Children.Add(new Border { Background = built });
                Border runs = new() { Background = Environment.Version.Major >= 11 ? Brushes.Red : Brushes.Blue };
                SetColumn(runs, 1);
                Children.Add(runs);
            }

        #if NET11_0_OR_GREATER
            public string? Note { get; set; }
        #endif
        }

        """;

    internal const string AvaloniaWindow = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                xmlns:local="using:Pair"
                mc:Ignorable="d" d:DesignWidth="400" d:DesignHeight="200"
                x:Class="Pair.MainWindow"
                Title="Pair">
            <local:Marker />
        </Window>

        """;

    /// <summary>A project file's one framework (<paramref name="made"/>) made the two.</summary>
    internal static void TargetBoth(string project, string made, string first, string second)
    {
        string text = File.ReadAllText(project);
        string one = $"<TargetFramework>{made}</TargetFramework>";
        Assert.Contains(one, text, StringComparison.Ordinal);
        File.WriteAllText(project, text.Replace(one, $"<TargetFrameworks>{first};{second}</TargetFrameworks>", StringComparison.Ordinal));
    }

    /// <summary>An Avalonia app as dotnet new makes it, of both frameworks, its window the marker alone.</summary>
    public sealed class Pair : SharedProject
    {
        public Pair()
            : base("pair-")
        {
        }

        public override async ValueTask InitializeAsync()
        {
            await TestFolders.PrepareAsync(async () =>
            {
                await AvaloniaApp.CreateAsync(Folder, "Pair");
                TargetBoth(Path.Combine(Folder, "Pair.csproj"), TestProjects.Framework, TestProjects.Framework, Second);
                File.WriteAllText(Path.Combine(Folder, "Marker.cs"), AvaloniaMarker);
                File.WriteAllText(Path.Combine(Folder, "MainWindow.axaml"), AvaloniaWindow);
                // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
                Directory.CreateDirectory(Path.Combine(Folder, "bin"));
            }, Folder);
            await PrepareAsync();
        }
    }
}

/// <summary>WPF's previewer on an app of two frameworks, on Windows alone, where WPF draws (<see cref="MultiTargetedXamlTests"/>).</summary>
public sealed class MultiTargetedWpfPreviewTests : IClassFixture<MultiTargetedWpfPreviewTests.Pair>
{
    private readonly Pair app;

    public MultiTargetedWpfPreviewTests(Pair app)
    {
        this.app = app;
        if (OperatingSystem.IsWindows())
        {
            app.Reset();
        }
    }

    [AvaloniaFact]
    public void One_framework_chosen_per_project_drives_the_wpf_preview_on_its_own_runtime_completion_checks_and_csharp_and_is_kept()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF draws only on Windows, so its previewer runs only there");
        Assert.SkipUnless(Pair.HasOlderPack(), $"No WindowsDesktop reference pack for {TestProjects.Framework} beside the SDK, which a WPF app of it restores");
        MultiTargetedXamlTests.Check(app, "MainWindow.xaml", TestProjects.Framework + "-windows", MultiTargetedXamlTests.Second + "-windows");
    }

    private const string WpfMarker = """
        using System;
        using System.Windows.Controls;
        using System.Windows.Media;

        namespace Pair;

        public class Marker : Grid
        {
            public Marker()
            {
                ColumnDefinitions.Add(new ColumnDefinition());
                ColumnDefinitions.Add(new ColumnDefinition());
        #if NET11_0_OR_GREATER
                Brush built = Brushes.Red;
        #else
                Brush built = Brushes.Blue;
        #endif
                Children.Add(new Border { Background = built });
                Border runs = new() { Background = Environment.Version.Major >= 11 ? Brushes.Red : Brushes.Blue };
                SetColumn(runs, 1);
                Children.Add(runs);
            }

        #if NET11_0_OR_GREATER
            public string? Note { get; set; }
        #endif
        }

        """;

    private const string WpfWindow = """
        <Window x:Class="Pair.MainWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:Pair"
                Title="Pair" Height="200" Width="400">
            <local:Marker />
        </Window>

        """;

    /// <summary>A WPF app as dotnet new makes it, of both frameworks' Windows targets, its window the marker alone; made on
    /// Windows alone.</summary>
    public sealed class Pair : SharedProject
    {
        public Pair()
            : base("wpfpair-")
        {
        }

        /// <summary>Whether the SDK's folder holds a WindowsDesktop reference pack of the older framework's version, which the
        /// run's local feed does not carry.</summary>
        public static bool HasOlderPack()
        {
            string packs = Path.Combine(Dotnet.Root(), "packs", "Microsoft.WindowsDesktop.App.Ref");
            string major = TestProjects.Framework[3..].Split('.')[0] + ".";
            return Directory.Exists(packs) && Directory.EnumerateDirectories(packs).Any(d => Path.GetFileName(d).StartsWith(major, StringComparison.Ordinal));
        }

        public override async ValueTask InitializeAsync()
        {
            if (!OperatingSystem.IsWindows() || !HasOlderPack())
            {
                return;
            }
            await TestFolders.PrepareAsync(async () =>
            {
                await WpfApp.CreateAsync(Folder, "Pair");
                MultiTargetedXamlTests.TargetBoth(Path.Combine(Folder, "Pair.csproj"), WpfApp.Framework + "-windows", TestProjects.Framework + "-windows",
                    MultiTargetedXamlTests.Second + "-windows");
                File.WriteAllText(Path.Combine(Folder, "Marker.cs"), WpfMarker);
                File.WriteAllText(Path.Combine(Folder, "MainWindow.xaml"), WpfWindow);
                Directory.CreateDirectory(Path.Combine(Folder, "bin"));
            }, Folder);
            await PrepareAsync();
        }
    }
}
