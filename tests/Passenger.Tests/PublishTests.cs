using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Publishing a project to a folder from the window, in a folder whose name has spaces: the Publish dialog over a
/// console app, an ASP.NET Core app and a self-contained app, the profile it saves as the SDK's own .pubxml (which dotnet
/// publish takes as it is), one there already found and changed only once the person agrees, Publish Again after a
/// restart, Stop, a failed build, and Publish greyed with why for a project that does not publish.</summary>
public sealed class PublishTests : IClassFixture<RestoredFolders>, IDisposable
{
    private const string ConsoleProject = $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
        + "    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n</Project>\n";

    private readonly string folder;

    public PublishTests(RestoredFolders folders) => folder = folders.Take("publish shop ", made =>
    {
        TestFolders.Write(made, "Shop.slnx", "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/Hello/Hello.csproj\" />\n    <Project Path=\"src/Site/Site.csproj\" />\n"
            + "    <Project Path=\"src/Solo/Solo.csproj\" />\n    <Project Path=\"src/Tools/Tools.csproj\" />\n    <Project Path=\"src/Checks/Checks.csproj\" />\n  </Folder>\n</Solution>\n");
        TestFolders.Write(made, "src/Hello/Hello.csproj", ConsoleProject);
        TestFolders.Write(made, "src/Hello/Program.cs", "Console.WriteLine($\"Hello from the publish, {args.Length} given\");\n");
        TestFolders.Write(made, "src/Site/Site.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
            + "    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(made, "src/Site/Program.cs", "WebApplication app = WebApplication.Create(args);\napp.MapGet(\"/\", () => \"Hello\");\napp.Run();\n");
        TestFolders.Write(made, "src/Site/appsettings.json", "{\n  \"Shop\": \"open\"\n}\n");
        // At the tests' own target, whose runtime pack for this system the tests' project downloads for its self-contained publish.
        TestFolders.Write(made, "src/Solo/Solo.csproj", ConsoleProject.Replace(TestProjects.Framework, AvaloniaApp.Metadata("TargetFramework"), StringComparison.Ordinal));
        TestFolders.Write(made, "src/Solo/Program.cs", "Console.WriteLine($\"Solo on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\");\n");
        TestFolders.Write(made, "src/Tools/Tools.csproj", TestProjects.Library());
        TestFolders.Write(made, "src/Tools/Tool.cs", "public static class Tool\n{\n    public static int Count => 3;\n}\n");
        TestFolders.Write(made, "src/Checks/Checks.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
            + "    <IsTestProject>true</IsTestProject>\n  </PropertyGroup>\n</Project>\n");
        return Path.Combine(made, "Shop.slnx");
    });

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_console_app_published_to_a_folder_runs_from_there_and_the_status_bar_opens_the_folder()
    {
        MainWindow window = Open();
        try
        {
            PublishDialog dialog = DialogButtonsTests.Check(Show(window, "Hello"));
            string landing = PathNames.Canonical(Path.Combine(folder, "src", "Hello", "bin", "Release", TestProjects.Framework, "publish"));
            Assert.Equal((null, "FolderProfile", "Release", null, false), (dialog.Chosen, dialog.ProfileName, dialog.Configuration, dialog.Runtime, dialog.SelfContained));
            Assert.True(dialog.Destination is { } shown && PathNames.Same(shown, landing), $"The dialog says the app lands in {dialog.Destination}, not in {landing}");
            Assert.Equal($"dotnet publish {Path.Combine("src", "Hello", "Hello.csproj")} -c Release -p:PublishProfile=FolderProfile", dialog.Command);

            BuildOutcome ended = Publish(window, dialog.PublishAsync);
            Assert.True(ended.Succeeded, $"The publish did not succeed: {ended.Message}\n{string.Join('\n', ended.Output)}");
            Assert.True(ended.Published is { } at && PathNames.Same(at, landing), $"The publish says it landed in {ended.Published}, not in {landing}");
            string profile = File.ReadAllText(Path.Combine(folder, "src", "Hello", "Properties", "PublishProfiles", "FolderProfile.pubxml"));
            Assert.True(profile.Contains("<Configuration>Release</Configuration>", StringComparison.Ordinal) && profile.Contains("<PublishProtocol>FileSystem</PublishProtocol>", StringComparison.Ordinal)
                && !profile.Contains("PublishDir", StringComparison.Ordinal), $"The new profile says:\n{profile}");
            Assert.Equal("Hello from the publish, 1 given", Run(Dotnet([Path.Combine(landing, "Hello.dll"), "one"], landing)));

            Assert.Equal($"Published Hello (FolderProfile) to {Path.GetRelativePath(PathNames.Canonical(folder), landing)}", window.Status);
            Assert.Contains($"Published Hello (FolderProfile) to {landing}", window.BottomPanel.Build.Document.Snapshot.ToString(), StringComparison.Ordinal);
            Button said = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StatusMessage");
            Ui.Click(said);
            Assert.Equal([landing], window.ShownInFileManager);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_saved_profile_is_one_undo_step_publishes_again_after_a_restart_and_dotnet_publish_takes_it()
    {
        string state = TestFolders.Create("publish state ");
        string profile = Path.Combine(folder, "src", "Hello", "Properties", "PublishProfiles", "Nightly.pubxml");
        string nightly = PathNames.Canonical(Path.Combine(folder, "out", "nightly"));
        MainWindow window = Keeping(state);
        try
        {
            PublishDialog dialog = Show(window, "Hello");
            dialog.ProfileName = "Nightly";
            dialog.Configuration = "Debug";
            dialog.OutputFolder = "../../out/nightly/";
            Ui.Settle();
            Assert.True(dialog.Destination is { } shown && PathNames.Same(shown, nightly), $"The dialog says the app lands in {dialog.Destination}, not in {nightly}");
            Assert.Null(Done(dialog.SaveAsync(), "the profile to be saved"));
            string saved = File.ReadAllText(profile);
            Assert.True(saved.Contains("<Configuration>Debug</Configuration>", StringComparison.Ordinal) && saved.Contains("<PublishDir>../../out/nightly/</PublishDir>", StringComparison.Ordinal),
                $"The saved profile says:\n{saved}");
            Assert.Equal("Nightly", dialog.Chosen?.Name);
            dialog.Close();
            Ui.Settle();

            window.Confirm = (_, _, _) => Task.FromResult(true);
            Choose(window, "Hello");
            Ui.Run(window, EditorActions.Undo);
            Assert.False(File.Exists(profile), $"Undo left the profile: {window.Status}");
            Choose(window, "Hello");
            Ui.Run(window, EditorActions.Redo);
            Assert.Equal(saved, File.ReadAllText(profile));

            dialog = Show(window, "Hello");
            Assert.Equal(("Nightly", "Debug", "../../out/nightly/"), (dialog.Chosen?.Name, dialog.Configuration, dialog.OutputFolder));
            BuildOutcome ended = Publish(window, dialog.PublishAsync);
            Assert.True(ended.Succeeded && ended.Published is { } at && PathNames.Same(at, nightly), $"The publish ended: {ended.Message}");
            Assert.True(File.Exists(Path.Combine(folder, "src", "Hello", "bin", "Debug", TestProjects.Framework, "Hello.dll")), "The Debug profile published a build of another configuration");
        }
        finally
        {
            window.Close();
        }

        TestFolders.Delete(nightly);
        window = Keeping(state);
        try
        {
            Assert.Null(window.Actions.WhyNot(AppActions.PublishAgain, window.Actions.Context()));
            BuildOutcome again = Publish(window, () => Task.FromResult(window.Actions.Run(AppActions.PublishAgain)));
            Assert.True(again.Succeeded && again.Published is { } at && PathNames.Same(at, nightly) && File.Exists(Path.Combine(nightly, "Hello.dll")),
                $"Publish Again after a restart ended: {again.Message}");
        }
        finally
        {
            window.Close();
        }

        TestFolders.Delete(nightly);
        ChildResult published = Run(Dotnet(["publish", Path.Combine("src", "Hello", "Hello.csproj"), "-c", "Debug", "-p:PublishProfile=Nightly"], folder), check: false);
        Assert.True(published.Succeeded && File.Exists(Path.Combine(nightly, "Hello.dll")), $"dotnet publish with the profile exited with {published.ExitCode}:\n{published.Output}{published.Error}");
        TestFolders.Delete(state);
    }

    [AvaloniaFact]
    public void A_profile_there_already_is_offered_and_changed_only_once_the_person_agrees_keeping_the_rest_of_it()
    {
        // As Visual Studio writes a folder profile, with one for Azure beside it, which Passenger does not publish by yet.
        string vs = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<!--\r\nhttps://go.microsoft.com/fwlink/?LinkID=208121.\r\n-->\r\n<Project>\r\n  <PropertyGroup>\r\n"
            + $"    <Configuration>Release</Configuration>\r\n    <Platform>Any CPU</Platform>\r\n    <PublishDir>bin\\Release\\{TestProjects.Framework}\\publish\\portable\\</PublishDir>\r\n"
            + "    <PublishProtocol>FileSystem</PublishProtocol>\r\n    <_TargetId>Folder</_TargetId>\r\n  </PropertyGroup>\r\n</Project>";
        string profile = Path.Combine(folder, "src", "Hello", "Properties", "PublishProfiles", "FolderProfile.pubxml");
        Write("src/Hello/Properties/PublishProfiles/FolderProfile.pubxml", vs);
        Write("src/Hello/Properties/PublishProfiles/Azure.pubxml", "<Project>\n  <PropertyGroup>\n    <WebPublishMethod>ZipDeploy</WebPublishMethod>\n  </PropertyGroup>\n</Project>\n");
        MainWindow window = Open();
        try
        {
            PublishDialog dialog = Show(window, "Hello");
            Assert.Equal([("Azure", "ZipDeploy"), ("FolderProfile", null)], dialog.Profiles.Select(p => (p.Name, p.Remote)));
            Assert.Equal(("FolderProfile", "Release", $@"bin\Release\{TestProjects.Framework}\publish\portable\"), (dialog.Chosen?.Name, dialog.Configuration, dialog.OutputFolder));
            string portable = PathNames.Canonical(Path.Combine(folder, "src", "Hello", "bin", "Release", TestProjects.Framework, "publish", "portable"));
            Assert.True(dialog.Destination is { } shown && PathNames.Same(shown, portable), $"The dialog says the profile lands in {dialog.Destination}, not in {portable}");

            List<string> asked = [];
            bool agree = false;
            window.Confirm = (_, _, yes) =>
            {
                asked.Add(yes);
                return Task.FromResult(agree);
            };
            dialog.Configuration = "Debug";
            Assert.Equal("FolderProfile.pubxml was left as it was", Done(dialog.SaveAsync(), "the save to be refused"));
            Assert.Equal(["Save"], asked);
            string refused = File.ReadAllText(profile);
            Assert.True(refused == vs, $"The profile was changed though the person said no:\n{refused.Replace("\r", "<CR>", StringComparison.Ordinal)}");
            agree = true;
            Assert.Null(Done(dialog.SaveAsync(), "the profile to be saved"));
            string written = File.ReadAllText(profile);
            Assert.True(written == vs.Replace("<Configuration>Release<", "<Configuration>Debug<", StringComparison.Ordinal),
                $"The profile changed more than its configuration:\n{written.Replace("\r", "<CR>", StringComparison.Ordinal)}");

            dialog.ChooseProfile(null);
            Ui.Settle();
            dialog.ProfileName = "FolderProfile";
            agree = false;
            asked.Clear();
            Assert.Equal("FolderProfile.pubxml was left as it was", Done(dialog.SaveAsync(), "the replacing save to be refused"));
            Assert.Equal(["Replace"], asked);
            dialog.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_aspnet_core_app_publishes_with_its_settings_and_its_visual_studio_profile_says_where_visual_studio_copies_it()
    {
        // Visual Studio's web folder profile: Visual Studio copies the app on to PublishUrl, dotnet publish leaves it in PublishDir.
        Write("src/Site/Properties/PublishProfiles/FolderProfile.pubxml", "<Project>\n  <PropertyGroup>\n    <LastUsedBuildConfiguration>Release</LastUsedBuildConfiguration>\n"
            + "    <PublishProvider>FileSystem</PublishProvider>\n    <PublishUrl>../../site out/</PublishUrl>\n    <WebPublishMethod>FileSystem</WebPublishMethod>\n"
            + "    <_TargetId>Folder</_TargetId>\n  </PropertyGroup>\n</Project>\n");
        string landing = PathNames.Canonical(Path.Combine(folder, "src", "Site", "bin", "Release", TestProjects.Framework, "publish"));
        MainWindow window = Open();
        try
        {
            PublishDialog dialog = Show(window, "Site");
            Assert.Equal(("FolderProfile", "Release", $"bin/Release/{TestProjects.Framework}/publish/"), (dialog.Chosen?.Name, dialog.Configuration, dialog.OutputFolder));
            Assert.Contains($"Visual Studio copies it on to {PathNames.Canonical(Path.Combine(folder, "site out"))}", dialog.DestinationSaid, StringComparison.Ordinal);
            BuildOutcome ended = Publish(window, dialog.PublishAsync);
            Assert.True(ended.Succeeded, $"The publish did not succeed: {ended.Message}\n{string.Join('\n', ended.Output)}");
            Assert.True(ended.Published is { } at && PathNames.Same(at, landing), $"The publish says it landed in {ended.Published}, not in {landing}");
            string[] files = [.. Directory.EnumerateFiles(landing).Select(Path.GetFileName).OfType<string>()];
            Assert.True(files.Contains("Site.dll") && files.Contains("appsettings.json") && files.Contains("web.config"),
                $"The site's folder holds [{string.Join(", ", files)}], not its assembly, settings and web.config");
            // Nothing the person changed, so the profile is as it was.
            Assert.DoesNotContain("<Configuration>", File.ReadAllText(Path.Combine(folder, "src", "Site", "Properties", "PublishProfiles", "FolderProfile.pubxml")), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_self_contained_publish_for_this_system_carries_its_runtime_and_runs_on_its_own()
    {
        MainWindow window = Open();
        try
        {
            PublishDialog dialog = Show(window, "Solo");
            dialog.SelfContained = true;
            Ui.Settle();
            // A self-contained app is one system's: this machine's, as it chose none.
            Assert.Equal(RuntimeInformation.RuntimeIdentifier, dialog.Runtime);
            string landing = PathNames.Canonical(Path.Combine(folder, "src", "Solo", "bin", "Release", AvaloniaApp.Metadata("TargetFramework"), RuntimeInformation.RuntimeIdentifier, "publish"));
            Assert.True(dialog.Destination is { } shown && PathNames.Same(shown, landing), $"The dialog says the app lands in {dialog.Destination}, not in {landing}");
            BuildOutcome ended = Publish(window, dialog.PublishAsync);
            Assert.True(ended.Succeeded, $"The publish did not succeed: {ended.Message}\n{string.Join('\n', ended.Output)}");
            string program = Path.Combine(landing, OperatingSystem.IsWindows() ? "Solo.exe" : "Solo");
            string runtime = OperatingSystem.IsWindows() ? "coreclr.dll" : OperatingSystem.IsMacOS() ? "libcoreclr.dylib" : "libcoreclr.so";
            Assert.True(File.Exists(program) && File.Exists(Path.Combine(landing, runtime)) && File.Exists(Path.Combine(landing, "System.Private.CoreLib.dll")),
                $"The self-contained folder holds [{string.Join(", ", Directory.EnumerateFiles(landing).Select(Path.GetFileName).Take(12))}…], not its apphost and the runtime");
            Assert.StartsWith("Solo on .NET", Run(ChildProcesses.Command(program, [], landing)), StringComparison.Ordinal);
            string saved = File.ReadAllText(Path.Combine(folder, "src", "Solo", "Properties", "PublishProfiles", "FolderProfile.pubxml"));
            Assert.True(saved.Contains($"<RuntimeIdentifier>{RuntimeInformation.RuntimeIdentifier}</RuntimeIdentifier>", StringComparison.Ordinal)
                && saved.Contains("<SelfContained>true</SelfContained>", StringComparison.Ordinal), $"The profile says:\n{saved}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_publish_whose_build_fails_lists_its_error_in_problems_and_never_says_it_published()
    {
        Write("src/Hello/Program.cs", "Console.WriteLine(\"Hello\");\nint count = \"three\";\n");
        MainWindow window = Open();
        try
        {
            BuildOutcome ended = Publish(window, Show(window, "Hello").PublishAsync);
            Assert.Equal(BuildStatus.Failed, ended.Status);
            Assert.StartsWith("Publish failed: 1 error", window.Status, StringComparison.Ordinal);
            Assert.Contains(window.BottomPanel.Problems.Problems, p => p.Code == "CS0029");
            Assert.True(ended.Published is null && window.ShownInFileManager.Count == 0, "A failed publish said where it landed");
            Assert.DoesNotContain("Published Hello", window.BottomPanel.Build.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Stop_ends_a_publish_under_way_and_says_it_stopped()
    {
        // A build step that waits a minute, cross-platform, said first so the test knows it waits.
        Write("src/Hello/Hello.csproj", ConsoleProject.Replace("</Project>",
            "  <Target Name=\"Linger\" BeforeTargets=\"CoreCompile\">\n    <Message Importance=\"high\" Text=\"Lingering a while\" />\n"
            + "    <Exec Command=\"ping -n 60 127.0.0.1 &gt; nul\" Condition=\"'$(OS)' == 'Windows_NT'\" />\n"
            + "    <Exec Command=\"sleep 60\" Condition=\"'$(OS)' != 'Windows_NT'\" />\n  </Target>\n</Project>", StringComparison.Ordinal));
        MainWindow window = Open();
        try
        {
            PublishDialog dialog = Show(window, "Hello");
            _ = dialog.PublishAsync();
            // Pumped, not settled: a settle waits for the publish, which holds background work until it ends.
            Ui.PumpUntil(() => window.BottomPanel.Build.Document.Snapshot.ToString().Contains("Lingering a while", StringComparison.Ordinal), "the publish to reach its waiting step",
                TimeSpan.FromMinutes(2));
            Assert.Equal("Building Hello (FolderProfile)…", window.Actions.Work.Of(AppActions.Publish)?.Stage);
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => window.LastPublish is { IsCompleted: true }, "the stopped publish to end", TimeSpan.FromSeconds(30));
            BuildOutcome ended = Ui.Await(window.LastPublish!, "the stopped publish's outcome");
            Assert.Equal((BuildStatus.Stopped, "Publish stopped"), (ended.Status, window.Status));
            Assert.False(window.IsBuilding, "The publish was stopped but the window still says it builds");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Publish_is_greyed_with_why_for_a_library_and_a_test_project_and_offered_for_an_app()
    {
        MainWindow window = Open();
        try
        {
            Assert.Equal("Tools is a library: set IsPublishable to true in it to publish it", MenuItem(window, "Tools").Tip);
            Assert.Equal("Checks is a test project, which is run rather than published", MenuItem(window, "Checks").Tip);
            Assert.Equal((true, null), (MenuItem(window, "Hello").Enabled, MenuItem(window, "Hello").Tip));
            Assert.Equal("Nothing has been published here yet: Publish… first", window.Actions.WhyNot(AppActions.PublishAgain, window.Actions.Context()));

            // A library that asks to be published is offered.
            Write("src/Tools/Tools.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
                + "    <IsPublishable>true</IsPublishable>\n  </PropertyGroup>\n</Project>\n");
            Ui.WaitFor(() => MenuItem(window, "Tools").Enabled, "Tools to be offered once it asks to be published", TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A project's Publish... in its Explorer menu: whether it can be chosen, and its tip. Right-clicked again until
    /// the menu opens on its row: a project file written makes every row anew whenever the system's watcher tells of the
    /// write, so the row found a moment before may be gone by the click.</summary>
    private static (bool Enabled, string? Tip) MenuItem(MainWindow window, string project)
    {
        (bool Enabled, string? Tip)? found = null;
        Ui.WaitFor(() => (found = MenuItemOnce(window, project)) is not null, $"{project}'s menu, opened on its row", TimeSpan.FromMinutes(1));
        return found!.Value;
    }

    /// <summary>One right click on a project's row and what its menu says of Publish…, or null when the rows were made anew
    /// between finding the row and its menu, so the row is to be found again.</summary>
    private static (bool Enabled, string? Tip)? MenuItemOnce(MainWindow window, string project)
    {
        ExplorerNode row = ProjectRow(window, project);
        bool Remade() => !window.ExplorerView.Tree.Rows.Contains(row);
        if (window.ExplorerView.Rows().ContainerFromItem(row) is not { } shown)
        {
            return null;
        }
        try
        {
            Ui.RightClick(shown);
        }
        catch (Xunit.Sdk.XunitException) when (Remade())
        {
            return null;
        }
        if (window.ExplorerView.OpenMenu is not { } menu || Remade() || !window.ExplorerView.SelectedNodes.Contains(row))
        {
            window.ExplorerView.OpenMenu?.Close();
            Ui.Settle();
            return null;
        }
        Avalonia.Controls.MenuItem publish = menu.Items.OfType<Avalonia.Controls.MenuItem>().FirstOrDefault(i => (string?)i.Header == "Publish…")
            ?? throw new InvalidOperationException($"{project}'s menu has no Publish…: [{string.Join(", ", menu.Items.OfType<Avalonia.Controls.MenuItem>().Select(i => i.Header))}]");
        (bool, string?) found = (publish.IsEnabled, ToolTip.GetTip(publish) as string);
        menu.Close();
        Ui.Settle();
        return found;
    }

    /// <summary>A project's row of the Explorer, its solution folder opened: found again until it is drawn, since the rows
    /// are made again as the projects' evaluations land (a restore's too, later on a slow machine).</summary>
    private static ExplorerNode ProjectRow(MainWindow window, string project)
    {
        ExplorerNode? found = null;
        Ui.WaitFor(() =>
        {
            ExplorerTree tree = window.ExplorerView.Tree;
            if (tree.Rows.FirstOrDefault(r => r.Kind == NodeKind.SolutionFolder && r.Name == "src" && !r.IsExpanded) is { } src)
            {
                tree.Expand(src);
            }
            if (tree.Rows.FirstOrDefault(r => r.Kind == NodeKind.Project && r.Name == project) is not { } row)
            {
                return false;
            }
            ListBox rows = window.ExplorerView.Rows();
            rows.ScrollIntoView(row);
            found = rows.ContainerFromItem(row) is null ? null : row;
            return found is not null;
        }, $"the Explorer to show the project {project}", TimeSpan.FromMinutes(1));
        return found ?? throw new InvalidOperationException($"The Explorer does not show the project {project}");
    }

    /// <summary>Clicks a project's row of the Explorer, as a person chooses one before pressing a key.</summary>
    private static void Choose(MainWindow window, string project) => Ui.ClickRow(window, ProjectRow(window, project));

    /// <summary>The Publish dialog over a project, its profile read.</summary>
    private PublishDialog Show(MainWindow window, string project)
    {
        PublishDialog dialog = window.ShowPublish(PathNames.Canonical(Path.Combine(folder, "src", project, $"{project}.csproj")))
            ?? throw new InvalidOperationException($"Publish did not open for {project}: {window.Status}");
        Ui.WaitFor(() => !dialog.IsReading, $"the Publish dialog to read how {project} publishes", TimeSpan.FromMinutes(1));
        Assert.True(dialog.Problem.Length == 0, $"The Publish dialog over {project} says: {dialog.Problem}");
        return dialog;
    }

    /// <summary>A publish the window starts (the dialog's Publish, Publish Again) waited for to its end.</summary>
    private static BuildOutcome Publish(MainWindow window, Func<Task<string?>> start)
    {
        Task<string?> asked = start();
        Ui.WaitFor(() => window.LastPublish is { IsCompleted: true } || (asked.IsCompleted && asked.Result is not null), "the publish to end", TimeSpan.FromMinutes(4));
        Assert.True(window.LastPublish is not null, $"The publish did not start: {(asked.IsCompleted ? asked.Result : "still asking")}; the status bar says {window.Status}");
        return Ui.Await(window.LastPublish, "the publish's outcome", TimeSpan.FromMinutes(4));
    }

    private static string? Done(Task<string?> task, string what) => Ui.Await(task, what, TimeSpan.FromMinutes(1));

    private static System.Diagnostics.ProcessStartInfo Dotnet(IReadOnlyList<string> arguments, string at) => ChildProcesses.DotnetCommand(arguments, at);

    /// <summary>A program run to its end; its output, failing the test with what it said when it fails.</summary>
    private static string Run(System.Diagnostics.ProcessStartInfo start) => Run(start, check: true).Output.Trim();

    private static ChildResult Run(System.Diagnostics.ProcessStartInfo start, bool check)
    {
        TimeSpan limit = TimeSpan.FromMinutes(4);
        ChildResult result = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(start, limit: limit.Scaled())), ChildProcesses.Display(start), limit + TestFolders.Spare);
        Assert.True(!check || result.Succeeded, $"{ChildProcesses.Display(start)} exited with code {result.ExitCode}:\n{result.Output}{result.Error}");
        return result;
    }

    private MainWindow Open() => Ui.OpenFolder(folder, languageServer: false);

    /// <summary>A window that keeps its state in a folder of the test's own, as the person's does.</summary>
    private MainWindow Keeping(string state)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
