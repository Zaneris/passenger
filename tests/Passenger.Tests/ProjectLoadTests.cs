using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Packages;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>What a project loads with. A package with a known vulnerability (from a stand-in feed whose VulnerabilityInfo
/// restore's audit reads, so nothing reaches nuget.org) is a warning naming the package and its severity in the status
/// bar, under the project with its advisory's link in Problems' Toolset, Environment, and marked in the NuGet window,
/// which offers the version that fixes it; updating to it takes the warning away. A project whose SDK cannot be found is
/// an error naming why, in the status bar and under the project in Problems, as is a restore that fails before it writes
/// the project's assets. A solution that does not read is an error in the status bar, the projects under its folder open
/// in its place, and marked where it stops in its open file.</summary>
public sealed class ProjectLoadTests : IDisposable
{
    private const string Advisory = "https://github.com/advisories/GHSA-pass-load-test";

    private const string Warned = "Package 'Passenger.Vulnerable' 1.0.0 has a known moderate severity vulnerability, " + Advisory;

    private readonly string folder = TestFolders.Create("load-");
    private readonly StandInFeed feed = new();
    private readonly string project;

    public ProjectLoadTests()
    {
        feed.Add("Passenger.Vulnerable", "1.0.0");
        feed.Add("Passenger.Vulnerable", "1.0.1");
        feed.Add("Passenger.Vulnerable", "1.1.0");
        feed.Advise("Passenger.Vulnerable", 1, Advisory, "[1.0.0, 1.0.1)");
        // Only the stand-in, for packages and for the audit, and restores kept out of the person's package cache.
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" protocolVersion="3" allowInsecureConnections="true" />
              </packageSources>
              <auditSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" allowInsecureConnections="true" />
              </auditSources>
              <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
            </configuration>
            """);
        Directory.CreateDirectory(Path.Combine(folder, "Web"));
        project = Path.Combine(folder, "Web", "Web.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Passenger.Vulnerable\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(folder, "Web", "Page.cs"), "namespace Web;\n\npublic static class Page\n{\n    public static string Title => \"Villagely\";\n}\n");
    }

    public void Dispose()
    {
        feed.Dispose();
        TestFolders.Delete(folder);
    }

    [Gate]
    [AvaloniaFact]
    public void A_package_with_a_known_vulnerability_is_a_warning_in_the_status_bar_and_problems_with_its_advisory_and_nuget_offers_its_fix()
    {
        Restore.Run(project);
        MainWindow window = Open();
        Ui.WaitFor(() => window.Status?.Contains(Warned, StringComparison.Ordinal) == true,
            $"the status bar to name the vulnerable package and its severity (it says \"{window.Status}\")");
        Assert.Equal($"Warning while loading {Path.Combine("Web", "Web.csproj")}: {Warned}", window.Status);
        Assert.True(window.StatusSeverity == ProblemSeverity.Warning, $"The status bar's load warning is marked as {window.StatusSeverity?.ToString() ?? "nothing"}, not a warning");
        string? tip = ToolTip.GetTip(StatusMessage(window)) as string;
        Assert.True(tip?.StartsWith(window.Status!, StringComparison.Ordinal) == true && tip.EndsWith("Click to see it in Problems, Toolset, Environment", StringComparison.Ordinal),
            $"Hovering the status bar's load warning, which the bar cuts, shows \"{tip}\", not its whole text and where a click on it goes");

        ProblemsModel problems = window.BottomPanel.Problems;
        window.DockHost.Hide(ToolWindowIds.Problems);
        problems.Shown = ProblemsTab.File;
        ClickStatus(window);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Problems) && problems.Shown == ProblemsTab.Toolset,
            $"A click on the status bar's load warning showed {(window.DockHost.IsShown(ToolWindowIds.Problems) ? $"Problems' {problems.Shown} tab" : "nothing")}, not Problems' Toolset, Environment");
        Problem warning = Assert.Single(problems.ToolsetProblems);
        Assert.True(problems.Selected == warning, $"A click on the status bar's load warning chose {problems.Selected?.Message ?? "nothing"} in Problems, not the warning");
        Assert.True(warning is { Severity: ProblemSeverity.Warning, Project: "Web", Message: Warned } && warning.Link == new Uri(Advisory),
            $"Problems lists the load's warning as {warning.Severity} \"{warning.Message}\" under {warning.Project}, its link {warning.Link}");
        Assert.Equal(["Web", Warned], problems.Rows.Select(r => r.Label));
        Button link = Links(window.ProblemsWindow).SingleOrDefault(b => (string?)b.Content == Advisory)
            ?? throw new Xunit.Sdk.XunitException("The warning's row in Problems shows no link to the advisory");
        Ui.Click(link, settle: false);
        Assert.True(window.Browsed.Contains(new Uri(Advisory)), $"Clicking the advisory's link opened [{string.Join(", ", window.Browsed)}]");

        NuGetView nuget = window.NuGet;
        window.Perform(AppActions.ShowNuGet);
        Ui.Landed(() => nuget.Selected is { Id: "Passenger.Vulnerable", IsVulnerable: true } && nuget.Versions.Count == 3, "the vulnerable package marked and its versions");
        Advisory advisory = Assert.Single(nuget.Selected!.Advisories);
        Assert.True(advisory.Severity == AdvisorySeverity.Moderate && advisory.Link == new Uri(Advisory), $"The NuGet window has the package's advisory as {advisory}");
        Assert.True(nuget.Status == "1 package installed, 1 vulnerable", $"The NuGet window's status says \"{nuget.Status}\"");
        Assert.True(nuget.ChosenVersion == "1.0.1", $"The NuGet window offers {nuget.ChosenVersion}, not 1.0.1, the version that fixes the vulnerability");
        Assert.True(Labels(nuget).Contains("Update"), $"Web's line offers [{string.Join(", ", Labels(nuget))}], not Update");
        List<string> said = Texts(nuget);
        Assert.True(said.Contains("Known security vulnerability in 1.0.0. It could be exploited in certain scenarios. See the advisories below for details.")
            && said.Contains("(Severity: Moderate)") && said.Contains("Fixed in 1.0.1"),
            $"The package's details do not say Rider's words on its vulnerability, its severity and its fix: [{string.Join(" | ", said)}]");
        Ui.Click(Links(nuget).Single(b => (string?)b.Content == Advisory), settle: false);
        Assert.True(window.Browsed.Count(u => u == new Uri(Advisory)) == 2, "The NuGet window's advisory link did not open the advisory");
        window.Close();
    }

    /// <summary>An advisory's address is the feed's to name, in any scheme: only an http or https one is a link, and one that
    /// would launch a program (a file: address) is words that open nothing.</summary>
    [Gate]
    [AvaloniaFact]
    public void An_advisory_whose_address_is_no_web_page_is_shown_in_the_nuget_window_as_words_not_a_link()
    {
        const string Local = "file:///System/Applications/Calculator.app";
        feed.Advise("Passenger.Vulnerable", 2, Local, "[1.0.0, 1.0.1)");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        window.Perform(AppActions.ShowNuGet);
        Ui.Landed(() => nuget.Selected is { Id: "Passenger.Vulnerable", IsVulnerable: true, Advisories.Count: 2 }, "the vulnerable package marked with both advisories");
        Advisory local = nuget.Selected!.Advisories.Single(a => a.Address == Local);
        Assert.True(local is { Severity: AdvisorySeverity.High, Link: null }, $"The NuGet window has the file: advisory as {local}");

        List<string> said = Texts(nuget);
        Assert.True(said.Contains(Local) && !Labels(nuget).Contains(Local), $"The file: advisory is not shown as words beside the web one's link: [{string.Join(" | ", said)}]");
        Ui.Click(Links(nuget).Single(b => (string?)b.Content == Advisory), settle: false);
        Assert.True(window.Browsed.SequenceEqual([new Uri(Advisory)]), $"The NuGet window's advisories opened [{string.Join(", ", window.Browsed)}]");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Updating_a_vulnerable_package_to_the_version_that_fixes_it_takes_its_warning_away()
    {
        Restore.Run(project);
        MainWindow window = Open();
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.ToolsetProblems.Count == 1, "the vulnerable package's warning in Problems");
        NuGetView nuget = window.NuGet;
        window.Perform(AppActions.ShowNuGet);
        Ui.Landed(() => nuget.Selected is { IsVulnerable: true } && nuget.ChosenVersion == "1.0.1", "the vulnerable package and its fix offered");

        Button update = Descendants<StackPanel>(nuget).Where(l => l.Children.FirstOrDefault() is TextBlock { Text: "Web" })
            .SelectMany(l => l.Children.OfType<Button>()).FirstOrDefault(b => (string?)b.Content == "Update")
            ?? throw new Xunit.Sdk.XunitException("The NuGet window offers no Update on Web's line");
        Ui.Click(update, settle: false);
        Ui.WaitFor(() => !nuget.IsBusy && Version(window) == "1.0.1", $"Web updated to 1.0.1:\n{nuget.Log}", TimeSpan.FromMinutes(3));

        Ui.WaitFor(() => problems.ToolsetProblems.Count == 0, $"the warning gone from Problems once the project loaded again ({problems.ToolsetProblems.FirstOrDefault()?.Message})",
            TimeSpan.FromMinutes(2));
        Assert.True(window.Status == $"{Path.Combine("Web", "Web.csproj")} loaded without problems" && window.StatusSeverity is null,
            $"Once the project loaded without its warning the status bar says \"{window.Status}\" ({window.StatusSeverity})");
        Ui.Landed(() => nuget.Selected is { Id: "Passenger.Vulnerable", IsVulnerable: false }, "the updated package no longer marked vulnerable");
        Assert.True(nuget.Status == "Done", $"The NuGet window's status says \"{nuget.Status}\" after the update, not that it was done");
        Assert.DoesNotContain(Descendants<Border>(nuget), b => b.Name == "Vulnerability");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void A_project_whose_sdk_cannot_be_found_is_an_error_naming_why_in_the_status_bar_and_under_it_in_problems()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Bad"));
        string bad = Path.Combine(folder, "Bad", "Bad.csproj");
        File.WriteAllText(bad, "<Project Sdk=\"Missing.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.Delete(project);
        MainWindow window = Open();
        const string Reason = "The SDK 'Missing.Sdk' specified could not be found.";
        Assert.True(window.Status == $"Error while loading {Path.Combine("Bad", "Bad.csproj")}: {Reason}",
            $"The status bar says \"{window.Status}\" once the projects loaded, not that Bad.csproj did not load and why");
        Assert.True(window.StatusSeverity == ProblemSeverity.Error, $"The status bar's load error is marked as {window.StatusSeverity?.ToString() ?? "nothing"}, not an error");

        ProblemsModel problems = window.BottomPanel.Problems;
        window.DockHost.Hide(ToolWindowIds.Problems);
        problems.Shown = ProblemsTab.File;
        ClickStatus(window);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Problems) && problems.Shown == ProblemsTab.Toolset,
            $"A click on the status bar's load error showed {(window.DockHost.IsShown(ToolWindowIds.Problems) ? $"Problems' {problems.Shown} tab" : "nothing")}, not Problems' Toolset, Environment");
        Problem error = Assert.Single(problems.ToolsetProblems);
        Assert.True(error is { Severity: ProblemSeverity.Error, Project: "Bad", Message: Reason, Link: null },
            $"Problems lists the load's error as {error.Severity} \"{error.Message}\" under {error.Project}");
        Assert.Equal(["Bad", Reason], problems.Rows.Select(r => r.Label));
        Assert.True(problems.Selected == error, $"A click on the status bar's load error chose {problems.Selected?.Message ?? "nothing"} in Problems, not the error");

        window.DockHost.Hide(ToolWindowIds.Problems);
        problems.Shown = ProblemsTab.File;
        Ui.Press(window, Ui.Shortcut(AppActions.ApplyHotReloadChanges));
        Assert.StartsWith("Nothing runs", window.Status, StringComparison.Ordinal);
        Button said = StatusMessage(window);
        Assert.True(window.StatusPlace is null && !said.Focusable && said.Cursor is null && ToolTip.GetTip(said) as string == window.Status,
            $"The status bar's next words, which go nowhere, still look like a link (place '{window.StatusPlace?.Name}', cursor {said.Cursor}, tooltip \"{ToolTip.GetTip(said)}\")");
        ClickStatus(window);
        Assert.True(!window.DockHost.IsShown(ToolWindowIds.Problems) && problems.Shown == ProblemsTab.File,
            "A click on status bar words that go nowhere showed Problems, as the load error said before them did");

        window.Perform(AppActions.ShowProblems);
        problems.Shown = ProblemsTab.Toolset;
        Ui.Settle();
        ListBox list = Descendants<ListBox>(window.ProblemsWindow).First();
        list.SelectedItem = problems.Rows.Single(r => r.Problem is not null);
        Ui.ChooseFromMenu(list, "Go to");
        Ui.Settle();
        Assert.True(window.EditorArea.Active?.Path is { } opened && PathNames.Same(opened, PathNames.Canonical(bad)),
            $"Going to the load error opened {window.EditorArea.Active?.Path}, not Bad.csproj");
        window.Close();
    }

    [AvaloniaFact]
    public void A_solution_that_does_not_read_is_an_error_in_the_status_bar_its_folders_projects_open_and_its_file_marked_until_it_reads()
    {
        string solution = Path.Combine(folder, "Shop.slnx");
        // A merge left half done: its conflict's markers stand among the solution's projects.
        const string Conflict = "<<<<<<< HEAD\n  <Project Path=\"Web/Web.csproj\" />\n=======\n  <Project Path=\"Web/Web.csproj\" Id=\"1\" />\n>>>>>>> theirs\n";
        File.WriteAllText(solution, $"<Solution>\n{Conflict}</Solution>\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false, width: 1400, height: 900);
        const string Why = "Name cannot begin with the '<' character, hexadecimal value 0x3C";
        string said = $"Shop.slnx does not read, so the projects under its folder are open in its place: {Why} (line 2, column 2)";
        Assert.True(window.Status == said, $"The status bar says \"{window.Status}\" as the folder opens, not that its solution does not read and why");
        Assert.True(window.StatusSeverity == ProblemSeverity.Error, $"The status bar's word on the solution is marked as {window.StatusSeverity?.ToString() ?? "nothing"}, not an error");
        Assert.True(window.WorkspaceModel is { Solution: null, Unread: not null } read && read.Projects.Select(p => p.Path).SequenceEqual([PathNames.Canonical(project)]),
            $"The folder whose solution does not read has {string.Join(", ", window.WorkspaceModel?.Projects.Select(p => p.Name) ?? [])} open, not the project under it");

        Assert.Equal("open Shop.slnx where it stops reading", window.StatusPlace?.Name);
        ClickStatus(window);
        OpenDocument shown = window.EditorArea.Active!;
        Assert.True(PathNames.Same(shown.Path, PathNames.Canonical(solution)), $"A click on the status bar's words opened {shown.Path}, not Shop.slnx");
        Assert.True(window.EditorArea.CodeEditor.CaretPosition == new TextPosition(1, 1),
            $"Shop.slnx opened at {window.EditorArea.CodeEditor.CaretPosition}, not where its reader stopped (line 2, column 2)");
        Marker marked = Assert.Single(shown.Markers);
        Assert.True(marked is { Kind: MarkerKind.Error, Message: $"The solution does not read: {Why}", Range: { Start: 12, Length: 1 } },
            $"The open solution is marked {marked.Kind} \"{marked.Message}\" at {marked.Range}, not as an error on the conflict's second '<'");

        CodeEditor editor = window.EditorArea.CodeEditor;
        string text = editor.Document.Snapshot.ToString();
        editor.Select(new TextRange(text.IndexOf("=======", StringComparison.Ordinal), text.IndexOf("</Solution>", StringComparison.Ordinal) - text.IndexOf("=======", StringComparison.Ordinal)));
        Ui.Press(window, "BACK_SPACE");
        editor.Select(new TextRange(text.IndexOf("<<<<<<<", StringComparison.Ordinal), "<<<<<<< HEAD\n".Length));
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => shown.Markers.Count == 0, () => $"the mark to go once the solution reads: {editor.Document.Snapshot}");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Ui.WaitFor(() => window.WorkspaceModel is { Solution: not null, Unread: null }, "the folder to open by Shop.slnx once it reads");
        Assert.True(window.WorkspaceModel!.Solution!.Projects.Select(p => p.Path).SequenceEqual([PathNames.Canonical(project)]),
            "Shop.slnx read again does not hold its project");
        Assert.True(window.Status?.Contains("does not read", StringComparison.Ordinal) != true, $"The status bar still says \"{window.Status}\" once Shop.slnx reads");
        window.Close();
    }

    [AvaloniaFact]
    public void A_project_under_a_nuget_config_that_does_not_read_says_its_restores_error_until_a_restore_succeeds()
    {
        string config = Path.Combine(folder, "nuget.config");
        string reads = File.ReadAllText(config);
        // An element left without its end: dotnet restore stops before it writes the project's assets.
        File.WriteAllText(config, $"<?xml version=\"1.0\"?>\n<configuration>\n  <packageSources>\n    <add key=\"stand-in\" value=\"{feed.Index}\"\n  </packageSources>\n</configuration>\n");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        MainWindow window = Open();
        Ui.WaitFor(() => window.Status?.Contains("Restore failed", StringComparison.Ordinal) == true,
            () => $"the status bar to say Web's restore failed (it says \"{window.Status}\")");
        string said = window.Status!;
        string loading = $"Error while loading {Path.Combine("Web", "Web.csproj")}: Restore failed: ";
        Assert.True(said.StartsWith(loading, StringComparison.Ordinal)
            && said[loading.Length..].StartsWith("NuGet.Config is not valid XML. Path: '", StringComparison.OrdinalIgnoreCase)
            && said.EndsWith("Name cannot begin with the '<' character, hexadecimal value 0x3C. Line 5, position 3.", StringComparison.Ordinal),
            $"The status bar says \"{said}\", not that Web's restore failed for the nuget.config that does not read and where");
        Assert.True(window.StatusSeverity == ProblemSeverity.Error, $"The status bar's restore failure is marked as {window.StatusSeverity?.ToString() ?? "nothing"}, not an error");
        ProblemsModel problems = window.BottomPanel.Problems;
        Problem failed = Assert.Single(problems.ToolsetProblems);
        Assert.True(failed is { Severity: ProblemSeverity.Error, Project: "Web" } && said.EndsWith(failed.Message, StringComparison.Ordinal),
            $"Problems lists the restore's failure as {failed.Severity} \"{failed.Message}\" under {failed.Project}");

        File.WriteAllText(config, reads);
        // The build's own outcome: the language server's load after the restore may take the status bar first.
        BuildOutcome built = Ui.Await(window.BuildAsync(), "Web's build with its nuget.config reading", TimeSpan.FromMinutes(2));
        Assert.True(built.Succeeded, $"Web's build with its nuget.config reading did not succeed: {built.Message}");
        Ui.WaitFor(() => problems.ToolsetProblems.Count == 0, () => $"the restore's failure gone from Problems once a restore succeeded ({problems.ToolsetProblems.FirstOrDefault()?.Message})",
            TimeSpan.FromMinutes(2));
        window.Close();
    }

    /// <summary>A project in a folder its user cannot write loads with an error saying the folder is read-only, so MSBuild
    /// cannot write what the project loads with, and the way on, in the status bar and under the project in Problems.</summary>
    [AvaloniaFact]
    public void A_project_in_a_read_only_folder_is_said_to_be_read_only_where_msbuild_cannot_write()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A folder's read-only mode keeps no one from writing in it on Windows");
            return;
        }
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        string web = Path.GetDirectoryName(project)!;
        UnixFileMode was = File.GetUnixFileMode(web);
        File.SetUnixFileMode(web, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            Assert.SkipWhen(Writable(web), "This user (root) writes in a folder whatever its mode");
            MainWindow window = Open();
            Ui.WaitFor(() => window.Status?.Contains("is read-only", StringComparison.Ordinal) == true,
                () => $"the status bar to say Web cannot be written (it says \"{window.Status}\")");
            string said = window.Status!;
            Assert.True(said.StartsWith($"Error while loading {Path.Combine("Web", "Web.csproj")}: Web is read-only, so MSBuild cannot write the files the project loads with: "
                + "make it writable, or open a copy of it elsewhere (", StringComparison.Ordinal),
                $"The status bar over a project whose folder cannot be written says \"{said}\", not that Web is read-only and the way on");
            Assert.True(window.StatusSeverity == ProblemSeverity.Error, $"The status bar's word that Web is read-only is marked as {window.StatusSeverity?.ToString() ?? "nothing"}, not an error");
            Problem readOnly = window.BottomPanel.Problems.ToolsetProblems.FirstOrDefault(p => p.Severity == ProblemSeverity.Error)
                ?? throw new Xunit.Sdk.XunitException("Problems lists no error for the project whose folder cannot be written");
            Assert.True(readOnly.Project == "Web" && said.Contains(readOnly.Message, StringComparison.Ordinal),
                $"Problems lists the read-only folder's error as \"{readOnly.Message}\" under {readOnly.Project}");
            window.Close();
        }
        finally
        {
            File.SetUnixFileMode(web, was);
        }

        static bool Writable(string folder)
        {
            try
            {
                File.WriteAllText(Path.Combine(folder, "probe"), string.Empty);
                File.Delete(Path.Combine(folder, "probe"));
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>A nuget.config naming a local source only a teammate has, under a folder the system owns: the restore's
    /// failure is said in NuGet's words alone, in the status bar and under the project in Problems, never as that folder
    /// being read-only.</summary>
    [AvaloniaFact]
    public void A_local_source_not_there_under_a_system_folder_is_said_in_nugets_words_not_as_that_folder_being_read_only()
    {
        string missing = Path.Combine(Path.GetPathRoot(folder)!, "Users", "passenger-teammate", "feeds");
        Assert.SkipWhen(Directory.Exists(missing), $"{missing}, the teammate's source this test names, is here");
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="teammate" value="{missing}" />
              </packageSources>
              <auditSources>
                <clear />
              </auditSources>
              <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
            </configuration>
            """);
        MainWindow window = Open();
        string words = $"The local source '{missing}' doesn't exist.";
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.ToolsetProblems.Any(p => p.Message.Contains(words, StringComparison.Ordinal)),
            () => $"Problems to list Web's restore failing for the teammate's source (it lists [{string.Join(" | ", problems.ToolsetProblems.Select(p => p.Message))}], the status bar says \"{window.Status}\")");
        Problem failed = problems.ToolsetProblems.First(p => p.Message.Contains(words, StringComparison.Ordinal));
        Assert.True(failed is { Severity: ProblemSeverity.Error, Project: "Web" } && !failed.Message.Contains("read-only", StringComparison.Ordinal),
            $"Problems lists the restore's failure as {failed.Severity} \"{failed.Message}\" under {failed.Project}, not NuGet's words alone");
        Assert.True(window.Status?.Contains(words, StringComparison.Ordinal) == true && !window.Status.Contains("read-only", StringComparison.Ordinal),
            $"The status bar says \"{window.Status}\", not NuGet's words alone on the teammate's source");
        window.Close();
    }

    /// <summary>A window on the folder, its projects loaded by the language server.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the projects ({window.CSharpService?.State})");
        Ui.Settle();
        return window;
    }

    private static IEnumerable<T> Descendants<T>(Control parent) => parent.GetVisualDescendants().OfType<T>();

    /// <summary>The status bar's words, a link while they have a place to go.</summary>
    private static Button StatusMessage(MainWindow window) => Descendants<Button>(window).Single(b => b.Name == "StatusMessage");

    /// <summary>A click on the status bar's words, as the pointer makes one.</summary>
    private static void ClickStatus(MainWindow window)
    {
        Button said = StatusMessage(window);
        Ui.Click(said);
    }

    private static IEnumerable<Button> Links(Control parent) => Descendants<Button>(parent).Where(b => b.Classes.Contains(StyleClasses.Link));

    /// <summary>The words on the window's buttons, the projects' Install, Update, Downgrade and Remove among them.</summary>
    private static List<string> Labels(NuGetView nuget) => [.. Descendants<Button>(nuget).Select(b => b.Content).OfType<string>()];

    private static List<string> Texts(NuGetView nuget) => [.. nuget.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).OfType<string>()];

    private static string? Version(MainWindow window) =>
        window.Projects.SelectMany(p => p.Packages).Where(p => p.Name == "Passenger.Vulnerable").Select(p => p.Version).FirstOrDefault();
}
