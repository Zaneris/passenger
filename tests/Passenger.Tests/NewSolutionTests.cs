using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Settings;
using Passenger.App.Templates;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>New Solution and Add, New Project, from the templates dotnet new has installed, through the window: the
/// Welcome screen's New Solution makes a console app with an option changed, opens it, builds it and runs it, and from a
/// window with a solution open the new one is built in the window it opens in, wherever that is; a solution folder's Add,
/// New Project puts a class library in it, the solution file gaining only its lines; a name taken or a folder not empty
/// is refused with why first; the templates listed are dotnet new list's; and Avalonia's, not installed, install from the
/// run's own feed and make an Avalonia app, free of Accelerate's tools, that builds and runs.</summary>
public sealed class NewSolutionTests : IDisposable
{
    private static readonly string Library = TestProjects.Library();

    private static readonly string Executable = TestProjects.Console();

    /// <summary>As Visual Studio writes a .sln: a byte order mark, CRLF, a solution folder src holding Shop.Core.</summary>
    private const string Sln = "﻿\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n# Visual Studio Version 17\r\nVisualStudioVersion = 17.10.35013.160\r\nMinimumVisualStudioVersion = 10.0.40219.1\r\n"
        + "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Shop.Web\", \"Shop.Web\\Shop.Web.csproj\", \"{A1000000-0000-0000-0000-000000000001}\"\r\nEndProject\r\n"
        + "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"src\", \"src\", \"{B1000000-0000-0000-0000-000000000002}\"\r\nEndProject\r\n"
        + "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Shop.Core\", \"src\\Shop.Core\\Shop.Core.csproj\", \"{A1000000-0000-0000-0000-000000000002}\"\r\nEndProject\r\n"
        + "Global\r\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n\t\tDebug|Any CPU = Debug|Any CPU\r\n\t\tRelease|Any CPU = Release|Any CPU\r\n\tEndGlobalSection\r\n"
        + "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\r\n"
        + "\t\t{A1000000-0000-0000-0000-000000000001}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n\t\t{A1000000-0000-0000-0000-000000000001}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n"
        + "\t\t{A1000000-0000-0000-0000-000000000001}.Release|Any CPU.ActiveCfg = Release|Any CPU\r\n\t\t{A1000000-0000-0000-0000-000000000001}.Release|Any CPU.Build.0 = Release|Any CPU\r\n"
        + "\t\t{A1000000-0000-0000-0000-000000000002}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n\t\t{A1000000-0000-0000-0000-000000000002}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n"
        + "\t\t{A1000000-0000-0000-0000-000000000002}.Release|Any CPU.ActiveCfg = Release|Any CPU\r\n\t\t{A1000000-0000-0000-0000-000000000002}.Release|Any CPU.Build.0 = Release|Any CPU\r\n"
        + "\tEndGlobalSection\r\n\tGlobalSection(SolutionProperties) = preSolution\r\n\t\tHideSolutionNode = FALSE\r\n\tEndGlobalSection\r\n"
        + "\tGlobalSection(NestedProjects) = preSolution\r\n\t\t{A1000000-0000-0000-0000-000000000002} = {B1000000-0000-0000-0000-000000000002}\r\n\tEndGlobalSection\r\n"
        + "EndGlobal\r\n";

    private const string Slnx = "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/Shop.Core/Shop.Core.csproj\" />\n  </Folder>\n"
        + "  <Project Path=\"Shop.Web/Shop.Web.csproj\" />\n</Solution>\n";

    private readonly string settings = TestFolders.Create("settings-");
    private readonly string place = TestFolders.Create("new-");

    public void Dispose()
    {
        TestFolders.Delete(settings);
        TestFolders.Delete(place);
    }

    /// <summary>New Solution from the Welcome screen lists what dotnet new list reports, refuses a name already taken or a
    /// directory not empty with why before anything is made, and makes a console app with the option chosen, opens it
    /// built and runs it.</summary>
    [AvaloniaFact]
    public void New_Solution_from_the_Welcome_screen_makes_a_console_app_with_the_option_chosen_opens_it_built_and_runs_it()
    {
        using IDisposable theme = TestsTheme();
        Write("Taken/notes.txt", "kept\n");
        MainWindow window = Started();
        Ui.Click(window.Welcome.NewSolutionButton);
        NewProjectDialog dialog = Templates(window);
        TemplatesListedAsDotnetNewLists(dialog);

        dialog.Folder = place;
        Type(dialog, "Taken");
        Ui.Click(dialog.CreateButton);
        string taken = Path.Combine(place, "Taken");
        Assert.Equal($"Solution Taken already exists in this location: {taken} is not empty", dialog.Problem);
        Assert.True(dialog.IsVisible, "The dialog closed on a solution it refused, taking the name to correct with it");
        Assert.Equal(["Taken"], Directory.EnumerateFileSystemEntries(place).Select(Path.GetFileName));
        Assert.Equal(["notes.txt"], Directory.EnumerateFileSystemEntries(taken).Select(Path.GetFileName));
        dialog.SolutionName = string.Empty;
        Ui.Click(dialog.CreateButton);
        Assert.Equal("Solution name cannot be empty", dialog.Problem);
        dialog.SolutionName = "Bad:Name";
        Ui.Click(dialog.CreateButton);
        Assert.Equal("Solution name contains invalid characters", dialog.Problem);
        // The dialog Escape closes takes no key's release.
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        Ui.Settle();
        Assert.False(dialog.IsVisible, "Escape left the dialog open");
        Assert.Null(window.Folder);
        Assert.Equal(["Taken"], Directory.EnumerateFileSystemEntries(place).Select(Path.GetFileName));

        Ui.Click(window.Welcome.NewSolutionButton);
        dialog = Templates(window);
        Assert.Equal("Console App", dialog.Chosen?.Name);
        dialog.Folder = place;
        Type(dialog, "Greeter");
        Assert.Equal("Greeter", dialog.SolutionName);
        Assert.Equal("Greeter", dialog.ProjectName);
        Ui.Click(dialog.OptionEditor("Do not use top-level statements")
            ?? throw new InvalidOperationException($"The console template shows no top-level statements option: [{string.Join(", ", dialog.OptionLabels)}]"));
        Assert.Contains("--use-program-main true", dialog.Command, StringComparison.Ordinal);
        string framework = dialog.Framework ?? throw new InvalidOperationException("The console template offers no target framework");
        Ui.Click(dialog.CreateButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem is not null, "dotnet new to make the solution", TimeSpan.FromMinutes(5));
        Assert.True(dialog.Problem is null, $"The solution was not made: {dialog.Problem}");

        string folder = PathNames.Canonical(Path.Combine(place, "Greeter"));
        string solution = Path.Combine(folder, $"Greeter.{dialog.SolutionFormat}");
        Assert.True(window.WorkspaceModel?.OpenedAs == solution, $"The new solution {solution} was made, but the window opened {window.WorkspaceModel?.OpenedAs ?? "nothing"}");
        Assert.Contains("static void Main", File.ReadAllText(Path.Combine(folder, "Greeter", "Program.cs")), StringComparison.Ordinal);
        Assert.Equal(["Greeter"], window.WorkspaceModel!.Projects.Select(p => p.Name));
        Assert.Equal(solution, window.RecentSolutions[0].Path);
        Assert.Equal("Greeter", window.RecentSolutions[0].Name);

        Ui.WaitFor(() => !window.IsBuilding, "the new solution's build", TimeSpan.FromMinutes(3));
        Assert.True(File.Exists(Path.Combine(folder, "Greeter", "bin", "Debug", framework, "Greeter.dll")), $"The new solution was not built: {Ui.Said(window)}");
        Assert.Equal(["Greeter"], window.Configurations.Select(c => c.Name));
        Ui.Press(window, Ui.Shortcut(AppActions.RunProgram));
        Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the new console app to run to its end", TimeSpan.FromMinutes(2));
        Assert.Contains("Hello, World!", Output(window), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>New Solution from a window with a solution open builds the new solution in the window it opens in, once its
    /// session there has begun, whatever Settings' Open project in says: asked, nothing builds while the question waits, and
    /// This Window builds it there once the old solution is left; Current Window builds it there too; New Window builds it in
    /// a window of its own. The solution that was open is never built.</summary>
    [AvaloniaTheory]
    [InlineData(OpenProjectIn.Ask)]
    [InlineData(OpenProjectIn.CurrentWindow)]
    [InlineData(OpenProjectIn.NewWindow)]
    public void New_Solution_from_a_window_with_a_solution_open_builds_the_new_one_in_the_window_it_opens_in(OpenProjectIn openIn)
    {
        using IDisposable theme = TestsTheme();
        Write("Old/Old.csproj", Executable);
        Write("Old/Program.cs", "System.Console.WriteLine(\"Old\");\n");
        string old = PathNames.Canonical(Path.Combine(place, "Old"));
        MainWindow window = Started(old);
        Assert.Equal(old, window.Folder);
        window.ApplySettings(window.Settings with { OpenProjectIn = openIn });
        Assert.True(window.Perform(AppActions.NewSolution), $"New Solution did not open: {Ui.Said(window)}");
        NewProjectDialog dialog = Templates(window);
        Assert.Equal("Console App", dialog.Chosen?.Name);
        dialog.Folder = place;
        Type(dialog, "Fresh");
        string framework = dialog.Framework ?? throw new InvalidOperationException("The console template offers no target framework");
        Ui.Click(dialog.CreateButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem is not null, "dotnet new to make the solution", TimeSpan.FromMinutes(5));
        Assert.True(dialog.Problem is null, $"The solution was not made: {dialog.Problem}");
        string folder = PathNames.Canonical(Path.Combine(place, "Fresh"));
        string solution = Path.Combine(folder, $"Fresh.{dialog.SolutionFormat}");
        string oldBuilt = Path.Combine(old, "bin", "Debug", TestProjects.Framework, "Old.dll");

        MainWindow opened = window;
        if (openIn == OpenProjectIn.Ask)
        {
            OpenProjectDialog asked = window.Asking ?? throw new InvalidOperationException($"New Solution over {old} did not ask where to open it");
            Assert.False(window.IsBuilding || File.Exists(oldBuilt), "The solution open built while Open Solution asked where the new one goes");
            Ui.Click(asked.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == OpenProjectDialog.ThisWindow));
        }
        else if (openIn == OpenProjectIn.NewWindow)
        {
            opened = Assert.Single(PassengerApp.Windows, w => w != window);
            Assert.Equal(old, window.Folder);
        }
        Assert.False(File.Exists(oldBuilt), "New Solution built the solution that was open before it");
        Ui.WaitFor(() => opened.WorkspaceModel?.OpenedAs == solution, "the new solution to open", TimeSpan.FromMinutes(1));
        string built = Path.Combine(folder, "Fresh", "bin", "Debug", framework, "Fresh.dll");
        Assert.True(opened.IsBuilding || File.Exists(built), $"The new solution opened and no build of it began: {Ui.Said(opened)}");
        Ui.WaitFor(() => !opened.IsBuilding, "the new solution's build in the window it opened in", TimeSpan.FromMinutes(3));
        Assert.True(File.Exists(built), $"The new solution was not built: {Ui.Said(opened)}");
        Assert.False(window != opened && window.IsBuilding, "The window New Solution was chosen in built as the new solution opened in another");
        Assert.False(File.Exists(oldBuilt), "New Solution built the solution that was open before it");
        foreach (MainWindow each in PassengerApp.Windows.ToList())
        {
            each.Close();
        }
    }

    [AvaloniaFact]
    public void New_Solution_offers_Avalonias_templates_not_installed_installs_them_and_makes_an_Avalonia_app_that_builds_and_runs()
    {
        using IDisposable theme = TestsTheme();
        // Avalonia.Templates from the run's own feed alone, and the app run headless.
        AvaloniaApp.UseFeed(place);
        RunsHeadless("Gallery");
        MainWindow window = Started();
        Ui.Click(window.Welcome.NewSolutionButton);
        NewProjectDialog dialog = Templates(window);

        TemplateFamily avalonia = dialog.Families.Single(f => f.Name == "Avalonia");
        Assert.True(avalonia.NotInstalled, "Avalonia's family is not marked not installed though dotnet new has none of it");
        Assert.Equal(["App (avalonia.app; C#,F#)", "MVVM App (avalonia.mvvm; C#,F#)", "Cross Platform Application (avalonia.xplat; C#,F#), a solution"],
            avalonia.Types.Select(t => $"{t.TypeName} ({t.ShortName}; {string.Join(',', t.Languages)}){(t.IsSolution ? ", a solution" : string.Empty)}"));
        foreach (string riders in (string[])["aspire-starter", "func", "aichatweb"])
        {
            Assert.True(dialog.Templates.Any(t => t.ShortNames.Contains(riders)), $"New Solution lists nothing that makes {riders}, which Rider offers to install");
        }
        Control row = dialog.FamilyRow("Avalonia")!;
        Assert.Contains("Not installed", row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

        dialog.Folder = place;
        Ui.Click(row);
        Assert.Equal("Avalonia .NET App", dialog.Chosen?.Name);
        Assert.Equal(["App", "MVVM App", "Cross Platform Application"], dialog.TypeNames);
        Assert.Contains("dotnet new install Avalonia.Templates", dialog.Offer, StringComparison.Ordinal);
        Assert.False(dialog.CreateButton.IsEnabled, "Create was offered for a template not installed");

        Ui.Click(dialog.InstallButton);
        Ui.WaitFor(() => !dialog.IsInstalling, "dotnet new install Avalonia.Templates", TimeSpan.FromMinutes(3));
        Assert.True(dialog.Problem is null, $"Avalonia's templates did not install: {dialog.Problem}\n{dialog.InstallOutput}");
        Assert.Contains($"Success: Avalonia.Templates@{AvaloniaApp.TemplatesVersion} installed", dialog.InstallOutput, StringComparison.Ordinal);
        Assert.True(dialog.Chosen is { Pack: null, ShortName: "avalonia.app" }, $"Once installed, the dialog went on to {dialog.Chosen?.Name} ({dialog.Chosen?.Pack?.Id ?? "installed"})");
        Assert.True(dialog.Family is { Name: "Avalonia", NotInstalled: false }, $"Once installed, the dialog's family is {dialog.Family?.Name}, not installed: {dialog.Family?.NotInstalled}");
        Assert.Null(dialog.Offer);
        Assert.True(dialog.CreateButton.IsEnabled, "Create stayed off once the template was installed");
        Assert.Contains(TestProjects.Framework, dialog.Chosen!.Frameworks("C#"));
        Assert.DoesNotContain(dialog.Templates, t => t.Pack?.Id == "Avalonia.Templates");

        ProjectTemplate crossPlatform = dialog.Family!.Types.Single(t => t.ShortName == "avalonia.xplat");
        Assert.True(crossPlatform.IsSolution, "Avalonia's cross-platform template is not taken for the solution it makes");
        dialog.TypeChooser.SelectedItem = crossPlatform;
        Ui.Settle();
        Assert.False(dialog.AsksProjectName, "A solution template asked for a project name");
        Assert.StartsWith($"dotnet new avalonia.xplat --name {dialog.SolutionName} ", dialog.Command, StringComparison.Ordinal);
        dialog.TypeChooser.SelectedItem = dialog.Family.Types.Single(t => t.ShortName == "avalonia.app");
        Ui.Settle();
        Assert.True(dialog.AsksProjectName, "A project template asked for no project name");

        dialog.SolutionName = "Gallery";
        Ui.Click(dialog.CreateButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem is not null, "dotnet new to make the Avalonia app", TimeSpan.FromMinutes(5));
        Assert.True(dialog.Problem is null, $"The Avalonia app was not made: {dialog.Problem}");

        string folder = PathNames.Canonical(Path.Combine(place, "Gallery"));
        string project = Path.Combine(folder, "Gallery", "Gallery.csproj");
        Assert.True(window.WorkspaceModel?.OpenedAs == Path.Combine(folder, $"Gallery.{dialog.SolutionFormat}"), $"The window opened {window.WorkspaceModel?.OpenedAs ?? "nothing"}");
        Assert.DoesNotContain("AvaloniaUI.DiagnosticsSupport", File.ReadAllText(project), StringComparison.Ordinal);
        Assert.DoesNotContain("DeveloperTools", File.ReadAllText(Path.Combine(folder, "Gallery", "Program.cs")), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Avalonia.Desktop\"", File.ReadAllText(project), StringComparison.Ordinal);

        Ui.WaitFor(() => !window.IsBuilding, "the Avalonia app's build", TimeSpan.FromMinutes(4));
        Assert.True(File.Exists(Path.Combine(folder, "Gallery", "bin", "Debug", TestProjects.Framework, "Gallery.dll")),
            $"The Avalonia app was not built: {Ui.Said(window)}\n{window.BottomPanel.Build.Document.Snapshot}");
        Ui.Press(window, Ui.Shortcut(AppActions.RunProgram));
        Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the Avalonia app to run", TimeSpan.FromMinutes(3));
        Assert.Contains("Gallery is up, showing Welcome to Avalonia!", Output(window), StringComparison.Ordinal);
        Assert.Contains("Process finished with exit code 0.", Output(window), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public void Add_New_Project_puts_a_class_library_in_the_solution_folder_and_the_solution_file_gains_only_its_lines(string format)
    {
        string solution = WriteShop(format);
        Write("Docs/readme.md", "# Docs\n");
        byte[] before = File.ReadAllBytes(solution);
        string[] folders = [.. Directory.EnumerateFileSystemEntries(place).Order(StringComparer.Ordinal)];
        MainWindow window = Open();
        object? server = window.CSharpService;

        Ui.Run(window, AppActions.NewProject);
        NewProjectDialog adding = Templates(window);
        Assert.Equal(PathNames.Canonical(place), adding.Folder);
        Type(adding, "Shop.Core");
        Ui.Click(adding.CreateButton);
        Assert.Equal("Project Shop.Core already exists in the solution", adding.Problem);
        adding.ProjectName = "Docs";
        Ui.Click(adding.CreateButton);
        Assert.Equal($"Project Docs already exists in this location: {Path.Combine(PathNames.Canonical(place), "Docs")} is not empty", adding.Problem);
        adding.Cancel();
        Ui.Settle();
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(solution)), "A project refused changed the solution file");
        Assert.Equal(folders, Directory.EnumerateFileSystemEntries(place).Order(StringComparer.Ordinal));
        Assert.Equal(["readme.md"], Directory.EnumerateFileSystemEntries(Path.Combine(place, "Docs")).Select(Path.GetFileName));
        Assert.Equal(["Shop.Core"], Directory.EnumerateFileSystemEntries(Path.Combine(place, "src")).Select(Path.GetFileName));

        Ui.ChooseFromRowMenu(window, Row(window, NodeKind.SolutionFolder, "src"), "Add", "New Project…");
        NewProjectDialog dialog = Templates(window);
        Assert.Equal(PathNames.Canonical(Path.Combine(place, "src")), dialog.Folder);
        Type(dialog, "Shop.Data");
        Assert.DoesNotContain(dialog.Templates, t => t.IsSolution);
        Ui.Click(dialog.FamilyRow("Class Library")
            ?? throw new InvalidOperationException($"New Project lists no Class Library: [{string.Join(", ", dialog.Families.Select(f => f.Name))}]"));
        Assert.Equal("Class Library", dialog.Chosen?.Name);
        Assert.Equal("Shop.Data", dialog.ProjectName);
        Ui.Click(dialog.CreateButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem is not null, "dotnet new to make the class library", TimeSpan.FromMinutes(5));
        Assert.True(dialog.Problem is null, $"The class library was not made: {dialog.Problem}");
        Assert.True(File.Exists(Path.Combine(place, "src", "Shop.Data", "Shop.Data.csproj")), "The class library was not made in the solution folder's folder on disk");

        string[] added = Added(before, File.ReadAllBytes(solution), format);
        if (format == "slnx")
        {
            Assert.Equal(["    <Project Path=\"src/Shop.Data/Shop.Data.csproj\" />\n"], added);
        }
        else
        {
            string id = Regex.Match(added[0], "^Project\\(\"\\{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC\\}\"\\) = \"Shop.Data\", \"src\\\\Shop.Data\\\\Shop.Data.csproj\", \"(\\{[0-9A-F-]{36}\\})\"\r\n$").Groups[1].Value;
            Assert.True(id.Length > 0, $"The project's line is not as Visual Studio writes it: {added[0]}");
            Assert.Equal(
            [
                added[0], "EndProject\r\n",
                $"\t\t{id}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n", $"\t\t{id}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n",
                $"\t\t{id}.Release|Any CPU.ActiveCfg = Release|Any CPU\r\n", $"\t\t{id}.Release|Any CPU.Build.0 = Release|Any CPU\r\n",
                $"\t\t{id} = {{B1000000-0000-0000-0000-000000000002}}\r\n",
            ], added);
        }

        Assert.Equal(["src", "  Shop.Core", "  Shop.Data"],
            window.ExplorerView.Tree.Rows.SkipWhile(r => r.Name != "src").TakeWhile((r, i) => i == 0 || r.Depth > 1).Select(r => new string(' ', (r.Depth - 1) * 2) + r.Name));
        Assert.Equal(NodeKind.Project, window.ExplorerView.Selected?.Kind);
        Assert.Equal("Shop.Data", window.ExplorerView.Selected?.Name);
        Assert.Contains("Shop.Data", window.WorkspaceModel!.Projects.Select(p => p.Name));
        Assert.False(ReferenceEquals(server, window.CSharpService), "The language server was not started again with the new project");
        Assert.Equal(solution, window.CSharpService?.Session?.Solution);
        window.Close();
    }

    /// <summary>That the templates the dialog lists are those dotnet new list reports, Console App first.</summary>
    private static void TemplatesListedAsDotnetNewLists(NewProjectDialog dialog)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<Listed> expected = [];
        string said = string.Empty;
        foreach (string type in (string[])["project", "solution"])
        {
            ChildResult listed = Task.Run(() => ChildProcesses.RunAsync(ProjectTemplates.NewCommand(["list", "--type", type, "--columns-all"], home), limit: TimeSpan.FromMinutes(3)))
                .GetAwaiter().GetResult();
            // 103: none of the type is installed.
            Assert.True(listed.Succeeded || (type == "solution" && listed.ExitCode == 103), $"dotnet new list failed:\n{listed.Output}{listed.Error}");
            // WPF and Windows Forms build on Windows alone, so only there are they listed.
            expected.AddRange(listed.Succeeded ? Table(listed.Output).Where(t => OperatingSystem.IsWindows() || !t.Tags.Any(tag => tag is "WPF" or "WinForms")) : []);
            said += listed.Output;
        }
        Assert.True(expected.Count > 0, $"dotnet new list listed no project templates:\n{said}");
        List<string> missing = [];
        // Those not installed, the known packages' kinds dotnet new has not got, are no row of its list.
        List<ProjectTemplate> unmatched = [.. dialog.Templates.Where(t => t.Pack is null)];
        foreach (Listed row in expected)
        {
            ProjectTemplate? match = unmatched.FirstOrDefault(t => t.ShortNames.SequenceEqual(row.ShortNames) && t.Languages.SequenceEqual(row.Languages)
                && (row.Name.EndsWith("...", StringComparison.Ordinal) ? t.Name.StartsWith(row.Name[..^3], StringComparison.Ordinal) : t.Name == row.Name));
            if (match is null)
            {
                missing.Add($"{row.Name} ({string.Join(',', row.ShortNames)}; {string.Join(',', row.Languages)})");
            }
            else
            {
                unmatched.Remove(match);
            }
        }
        Assert.True(missing.Count == 0 && unmatched.Count == 0,
            $"The dialog does not list what dotnet new list reports. Not listed: [{string.Join("; ", missing)}]. Listed but not reported: "
            + $"[{string.Join("; ", unmatched.Select(t => $"{t.Name} ({string.Join(',', t.ShortNames)}; {string.Join(',', t.Languages)})"))}]\n{said}");
        Assert.Equal("Console App", dialog.Templates[0].Name);
    }

    /// <summary>One row of dotnet new list's table: its name (cut short with "..." when long), short names, languages
    /// (the default's brackets taken off) and tags.</summary>
    private sealed record Listed(string Name, string[] ShortNames, string[] Languages, string[] Tags);

    /// <summary>dotnet new list's table, read by the columns its rule of dashes marks.</summary>
    private static IEnumerable<Listed> Table(string output)
    {
        string[] lines = output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int rule = Array.FindIndex(lines, l => l.StartsWith("---", StringComparison.Ordinal));
        Assert.True(rule > 0, $"dotnet new list wrote no table:\n{output}");
        List<int> starts = [.. Regex.Matches(lines[rule], "-+").Select(m => m.Index)];
        string[] headers = [.. starts.Select((start, i) => Cell(lines[rule - 1], start, i + 1 < starts.Count ? starts[i + 1] : null))];
        int Column(string name) => Array.IndexOf(headers, name) is var at and >= 0 ? at : throw new InvalidOperationException($"dotnet new list has no {name} column: [{string.Join(", ", headers)}]");
        int name = Column("Template Name");
        int shortName = Column("Short Name");
        int language = Column("Language");
        int tags = Column("Tags");
        foreach (string line in lines.Skip(rule + 1).Where(l => l.Trim().Length > 0))
        {
            string[] cells = [.. starts.Select((start, i) => Cell(line, start, i + 1 < starts.Count ? starts[i + 1] : null))];
            yield return new Listed(cells[name], cells[shortName].Split(','), [.. cells[language].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim('[', ']'))],
                cells[tags].Split('/'));
        }

        static string Cell(string line, int start, int? end) =>
            start >= line.Length ? string.Empty : line[start..Math.Min(line.Length, end ?? line.Length)].Trim();
    }

    /// <summary>The dialog the action opened, once dotnet new's templates are listed in it.</summary>
    private static NewProjectDialog Templates(MainWindow window)
    {
        Ui.Settle();
        NewProjectDialog dialog = Assert.Single(window.OwnedWindows.OfType<NewProjectDialog>());
        Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "dotnet new's templates to be read", TimeSpan.FromMinutes(3));
        Assert.True(dialog.Templates.Count > 0, $"The dialog lists no templates: {dialog.Problem}");
        Assert.True(dialog.FocusManager?.GetFocusedElement() is TextBox { IsFocused: true } name && TopLevel.GetTopLevel(name) == dialog,
            $"The dialog opened without the name box having the keys: {dialog.FocusManager?.GetFocusedElement()} has them");
        return dialog;
    }

    /// <summary>Makes the Avalonia app of a name that goes in the test's folder run headless, through its BuildAvaloniaApp:
    /// the folder's Directory.Build.targets gives it Avalonia's headless platform and a Main of its own that says when the
    /// app's window is up and ends it. So the app opens no window on the screen, and runs where there is none.</summary>
    private void RunsHeadless(string name)
    {
        Write("Directory.Build.targets", $"""
            <Project>
              <PropertyGroup Condition="'$(MSBuildProjectName)' == '{name}'">
                <StartupObject>Headless</StartupObject>
              </PropertyGroup>
              <ItemGroup Condition="'$(MSBuildProjectName)' == '{name}'">
                <PackageReference Include="Avalonia.Headless" Version="{AvaloniaApp.Version}" />
                <Compile Include="$(MSBuildThisFileDirectory)Headless.cs" />
              </ItemGroup>
            </Project>
            """);
        Write("Headless.cs", $$"""
            using Avalonia;
            using Avalonia.Controls.ApplicationLifetimes;
            using Avalonia.Headless;
            using Avalonia.Threading;

            internal static class Headless
            {
                [System.STAThread]
                public static int Main(string[] args) => {{name}}.Program.BuildAvaloniaApp()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .AfterSetup(_ => Dispatcher.UIThread.Post(() =>
                    {
                        IClassicDesktopStyleApplicationLifetime desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                        System.Console.WriteLine($"{desktop.MainWindow?.Title} is up, showing {desktop.MainWindow?.Content}");
                        desktop.Shutdown();
                    }))
                    .StartWithClassicDesktopLifetime(args);
            }
            """);
    }

    /// <summary>The window a person starts, its state in the test's folder, on the Welcome screen or the folder given;
    /// without the C# language server, which nothing its tests check asks of (the builds and runs are dotnet's), as are the
    /// windows a new solution opens in.</summary>
    private MainWindow Started(string? folder = null)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.KeepSettings();
        window.Start(folder is null ? LaunchOptions.Empty : LaunchOptions.Empty with { Folder = folder });
        Ui.Settle();
        return window;
    }

    private MainWindow Open() => Ui.OpenFolder(place);

    /// <summary>Shop: Shop.Web at the solution's top, and Shop.Core in the solution folder src, each in its folder on disk.</summary>
    private string WriteShop(string format)
    {
        Write("Shop.Web/Shop.Web.csproj", Executable);
        Write("Shop.Web/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        Write("src/Shop.Core/Shop.Core.csproj", Library);
        Write("src/Shop.Core/Order.cs", "namespace Shop.Core;\n\npublic class Order\n{\n}\n");
        string solution = Path.Combine(place, $"Shop.{format}");
        File.WriteAllBytes(solution, new UTF8Encoding(false).GetBytes(format == "slnx" ? Slnx : Sln));
        Restore.Run(solution);
        return PathNames.Canonical(solution);
    }

    /// <summary>The lines a solution file gained, each with its line ending: what is left once they are taken out must be the
    /// file as it was, byte for byte, its byte order mark and line endings included.</summary>
    private static string[] Added(byte[] before, byte[] after, string format)
    {
        Assert.True(after.AsSpan().StartsWith(Encoding.UTF8.Preamble) == before.AsSpan().StartsWith(Encoding.UTF8.Preamble), $"The .{format}'s byte order mark changed");
        List<string> was = Lines(Encoding.UTF8.GetString(before));
        List<string> now = Lines(Encoding.UTF8.GetString(after));
        List<string> added = [];
        int at = 0;
        foreach (string line in now)
        {
            if (at < was.Count && was[at] == line)
            {
                at++;
            }
            else
            {
                added.Add(line);
            }
        }
        Assert.True(at == was.Count, $"The .{format} lost or changed lines it had, where only the new project's were to be added:\n{Encoding.UTF8.GetString(after)}");
        return [.. added];

        static List<string> Lines(string text) => [.. Regex.Matches(text, "[^\n]*\n|[^\n]+$").Select(m => m.Value)];
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    private static ExplorerNode Row(MainWindow window, NodeKind kind, string name) =>
        window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Kind == kind && r.Name == name)
        ?? throw new InvalidOperationException($"The Explorer shows no {kind} {name}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");

    /// <summary>Text typed where the keys are.</summary>
    private static void Type(TopLevel window, string text)
    {
        window.KeyTextInput(text);
        Ui.Settle();
    }

    private void Write(string relative, string text) => TestFolders.Write(place, relative, text);

    /// <summary>Puts the tests' theme back once a window that keeps its settings has put the person's in force.</summary>
    private static IDisposable TestsTheme() => new ThemeBack(Avalonia.Application.Current!.RequestedThemeVariant);

    private sealed class ThemeBack(Avalonia.Styling.ThemeVariant? theme) : IDisposable
    {
        public void Dispose() => Avalonia.Application.Current!.RequestedThemeVariant = theme;
    }
}
