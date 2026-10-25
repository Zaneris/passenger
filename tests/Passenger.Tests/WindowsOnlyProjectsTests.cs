using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A WPF app and a Windows Forms app as <c>dotnet new wpf</c> and <c>dotnet new winforms</c> make them, in a
/// solution never restored, opened off Windows: Passenger restores, evaluates and builds them with EnableWindowsTargeting
/// (the SDK's own way to build a Windows app elsewhere) without writing it into any of their files, so their code has the
/// language server's whole help and they load with no problems; the Solution view marks them Windows only, their run
/// configurations are listed greyed with why, and Run, Debug and Profile say they need Windows before anything is built,
/// rather than fail as the program starts. On Windows they are apps like any other: no mark, nothing greyed.</summary>
public sealed class WindowsOnlyProjectsTests : IClassFixture<WindowsOnlyProjectsTests.Apps>
{
    private readonly Apps apps;

    public WindowsOnlyProjectsTests(Apps apps) => this.apps = apps;

    private static string Framework => AvaloniaApp.Metadata("TargetFramework") + "-windows";

    [AvaloniaFact]
    public void A_wpf_and_a_windows_forms_app_load_with_their_frameworks_code_help_and_build_and_their_files_stay_as_made()
    {
        MainWindow window = apps.Open();
        try
        {
            // Each probe is checked (its own #warning) with no error for a type it uses once the server's restore is in.
            foreach ((string probe, string type) in new[] { ("Gallery/Probe.cs", "System.Windows.Controls.Button"), ("Forms/Probe.cs", "System.Windows.Forms.Button") })
            {
                window.OpenFile(apps.PathOf(probe));
                OpenDocument document = window.EditorArea.Active!;
                Until(() => Codes(window, document, warnings: true) is ["CS1030"] && Codes(window, document, warnings: false) is [], TimeSpan.FromMinutes(4),
                    () => $"{probe}, which makes a {type}, has errors [{string.Join(", ", Codes(window, document, warnings: false))}] and warnings "
                        + $"[{string.Join(", ", Codes(window, document, warnings: true))}] rather than its own #warning alone: its framework's reference assemblies never loaded");
            }
            Until(() => window.CSharpService!.ProjectProblems.Count == 0, TimeSpan.FromMinutes(2),
                () => $"The projects did not load without problems: {string.Join("; ", window.CSharpService!.ProjectProblems.Select(p => $"{p.Project}: {p.Message}"))}");

            window.Perform(AppActions.Build);
            Ui.WaitFor(() => !window.IsBuilding, "the build", TimeSpan.FromMinutes(4));
            string built = window.BottomPanel.Build.Document.Snapshot.ToString();
            Assert.True(window.Status?.StartsWith("Build succeeded", StringComparison.Ordinal) == true, $"The solution of a WPF and a Windows Forms app did not build ({window.Status}):\n{built}");
            Assert.True(File.Exists(apps.PathOf($"Gallery/bin/Debug/{Framework}/Gallery.dll")), $"Build made no Gallery.dll for {Framework}:\n{built}");
            Assert.True(File.Exists(apps.PathOf($"Forms/bin/Debug/{Framework}/Forms.dll")), $"Build made no Forms.dll for {Framework}:\n{built}");
            // The property is the build's own, as the SDK's error suggests: on its command line, and only where it is needed.
            Assert.True(built.Contains("-p:EnableWindowsTargeting=true", StringComparison.Ordinal) != OperatingSystem.IsWindows(),
                $"The build's command line {(OperatingSystem.IsWindows() ? "gives" : "lacks")} EnableWindowsTargeting:\n{built}");
            apps.AssertAsMade();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Off_windows_they_are_marked_windows_only_and_run_debug_and_profile_say_they_need_windows()
    {
        MainWindow window = apps.Open();
        try
        {
            ExplorerTree tree = window.ExplorerView.Tree;
            Ui.WaitFor(() => tree.Rows.Count(r => r.Kind == NodeKind.Project && r.Detail?.EndsWith(Framework, StringComparison.Ordinal) == true) == 2,
                "both projects evaluated in the Solution view");
            ExplorerNode gallery = Assert.Single(tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Gallery");
            ExplorerNode forms = Assert.Single(tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Forms");
            Assert.Equal(["Gallery", "Forms"], window.Configurations.Select(c => c.Name));
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(Framework, gallery.Detail);
                Assert.Null(gallery.Tip);
                Assert.True(window.Configurations.All(c => window.WhyNotHere(c) is null), "On Windows a WPF or Windows Forms app's configuration is greyed as though it could not run");
                Assert.Skip("On Windows these apps run, which would open their windows on the desktop: only that nothing is marked or greyed is checked");
            }
            Assert.Equal($"Windows only · {Framework}", gallery.Detail);
            Assert.Equal($"Windows only · {Framework}", forms.Detail);
            Assert.StartsWith("WPF runs only on Windows: Passenger builds Gallery here for its code help", gallery.Tip, StringComparison.Ordinal);
            Assert.StartsWith("Windows Forms runs only on Windows", forms.Tip, StringComparison.Ordinal);

            Assert.Equal("Gallery cannot run here: WPF runs only on Windows", ToolTip.GetTip(window.FindControl<Button>("RunConfigButton")!));
            Ui.Click(window.FindControl<Button>("RunConfigButton")!);
            ContextMenu chooser = Assert.IsType<ContextMenu>(window.OpenContextMenu);
            MenuItem[] items = [.. chooser.Items.OfType<MenuItem>()];
            Assert.Equal(["Gallery cannot run here: WPF runs only on Windows", "Forms cannot run here: Windows Forms runs only on Windows"],
                items.Take(2).Select(i => ToolTip.GetTip(i) as string));
            Assert.True(items.Take(2).All(i => i.Header is StackPanel { Children: [TextBlock { Classes: var named }, TextBlock] } && named.Contains("muted")),
                "A configuration that cannot run here is listed as one that can, not greyed");
            Ui.Click(items[1]);
            chooser.Close();
            Assert.Equal("Forms", window.Configuration?.Name);

            Assert.Equal("Forms cannot run here: Windows Forms runs only on Windows", Ui.Await(window.RunAsync(), "Run's refusal"));
            Assert.Equal("Forms cannot run here: Windows Forms runs only on Windows", window.Status);
            Assert.True(window.SelectConfiguration("Gallery"));
            Assert.Equal("Gallery cannot run here: WPF runs only on Windows", Ui.Await(window.RunAsync(), "Run's refusal"));
            Assert.Equal("Gallery cannot run here: WPF runs only on Windows", Ui.Await(window.DebugAsync(), "Debug's refusal"));
            Assert.Equal("Gallery cannot run here: WPF runs only on Windows", Ui.Await(window.ProfileAsync(), "Profile's refusal"));
            Assert.True(window.BottomPanel.Build.Document.Snapshot.Length == 0, "Run, Debug or Profile built an app that cannot run here before saying so");

            Assert.True(window.Perform(AppActions.EditConfigurations));
            RunConfigurationsDialog dialog = Assert.IsType<RunConfigurationsDialog>(window.RunConfigurationsDialog);
            Assert.Equal("Generated for the executable project Gallery: a field changed here is kept with the folder over its project, the rest follow it. "
                + "It cannot run here: WPF runs only on Windows.", dialog.Note);
            dialog.Close();
            Ui.Settle();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The codes of a document's errors, or its warnings, as the language server last said them.</summary>
    private static string[] Codes(MainWindow window, OpenDocument document, bool warnings) =>
        [.. (window.CSharpService?.For(document)?.Diagnostics ?? []).Where(d => d.Severity == (warnings ? Passenger.Lsp.DiagnosticSeverity.Warning : Passenger.Lsp.DiagnosticSeverity.Error)).Select(d => d.Code?.ToString() ?? string.Empty).Order(StringComparer.Ordinal)];

    /// <summary>Waits as <see cref="Ui.WaitFor"/> does, failing with what is so by then.</summary>
    private static void Until(Func<bool> condition, TimeSpan limit, Func<string> failure)
    {
        try
        {
            Ui.WaitFor(condition, "a condition", limit);
        }
        catch (TimeoutException)
        {
            Assert.Fail(failure());
        }
    }

    /// <summary>The two apps as dotnet new made them, in a solution, with the run's local feed and no restore: each with a
    /// probe that makes one of its framework's controls and says one #warning.</summary>
    public sealed class Apps : IDisposable
    {
        private readonly Dictionary<string, string> made = new(StringComparer.Ordinal);

        public Apps()
        {
            Folder = TestFolders.Create("windows-only-");
            TestFolders.Prepare(() =>
            {
                WpfApp.Create(PathOf("Gallery"), "Gallery", asMade: true);
                WpfApp.CreateWindowsForms(PathOf("Forms"), "Forms");
                AvaloniaApp.UseFeed(Folder);
                File.WriteAllText(PathOf("Desktop.slnx"), "<Solution>\n  <Project Path=\"Gallery/Gallery.csproj\" />\n  <Project Path=\"Forms/Forms.csproj\" />\n</Solution>\n");
                foreach (string file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
                {
                    made[file] = File.ReadAllText(file);
                }
                File.WriteAllText(PathOf("Gallery/Probe.cs"), "namespace Gallery;\n\n#warning The probe was checked\npublic static class Probe\n{\n    public static System.Windows.Controls.Button Make() => new() { Content = \"Hi\" };\n}\n");
                File.WriteAllText(PathOf("Forms/Probe.cs"), "namespace Forms;\n\n#warning The probe was checked\npublic static class Probe\n{\n    public static System.Windows.Forms.Button Make() => new() { Text = \"Hi\" };\n}\n");
            }, Folder);
        }

        public string Folder { get; }

        public string PathOf(string relative) => Path.Combine(Folder, relative);

        /// <summary>A window on the solution, the folder's state its own.</summary>
        public MainWindow Open()
        {
            MainWindow window = Ui.OpenFolder(PathOf("Desktop.slnx"), width: 1400, height: 880);
            return window;
        }

        /// <summary>Fails naming a file Passenger wrote EnableWindowsTargeting into, or one it added beside the projects'.</summary>
        public void AssertAsMade()
        {
            foreach ((string file, string text) in made)
            {
                Assert.True(File.ReadAllText(file) == text, $"{Path.GetRelativePath(Folder, file)} changed from what dotnet new made:\n{File.ReadAllText(file)}");
            }
            string[] props = [.. Directory.EnumerateFiles(Folder, "Directory.Build.*", SearchOption.AllDirectories)];
            Assert.True(props.Length == 0, $"Passenger wrote {string.Join(", ", props)} into the solution");
        }

        public void Dispose() => TestFolders.Delete(Folder);
    }
}
