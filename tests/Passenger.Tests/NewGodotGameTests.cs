using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>New Solution's Godot 4 Game, Passenger's own, beside dotnet new's templates: in C# it makes the game and its
/// solution on the Godot found's Godot.NET.Sdk, restored from that Godot's own packages, opens it built and offers Passenger
/// as Godot's script editor, and Run starts the game, whose script prints; by default its solution sits beside
/// project.godot, where Godot's own editor builds it; in GDScript it makes the game's folder alone, which opens and runs the
/// same. With no Godot found it is refused saying how to get one. Godot runs headless with the run's user folders; without
/// a Godot 4 .NET build, the tests that run it skip saying why, as Godot's editor's build does where that editor builds
/// nothing with the run's .NET.</summary>
public sealed class NewGodotGameTests : IDisposable
{
    private readonly string settings = TestFolders.Create("settings-");
    private readonly string place = TestFolders.Create("new-game-");
    private readonly GodotFound? godot = GodotExecutables.Find(null);
    private readonly IReadOnlyDictionary<string, string> homes;

    public NewGodotGameTests() => homes = GodotTests.Game.Home(place + "-home");

    public void Dispose()
    {
        TestFolders.Delete(settings);
        TestFolders.Delete(place);
        TestFolders.Delete(place + "-home");
    }

    private void SkipWithoutGodot() => Assert.SkipWhen(godot is not { WhyNot: null, Version: not null, Packages: not null },
        godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
            : $"The Godot found cannot make a C# game: {godot.WhyNot}");

    /// <summary>The Godot 4 Game made in C#, its solution and project in one folder, opens built with Passenger offered as
    /// Godot's script editor, and runs, its script printing.</summary>
    [AvaloniaFact]
    public void A_csharp_godot_game_is_made_on_the_godots_sdk_opens_built_and_runs_printing()
    {
        SkipWithoutGodot();
        MainWindow window = Started();
        try
        {
            NewProjectDialog dialog = Chosen(window, "C#");
            dialog.SameDirectory = true;
            Assert.Contains($"Spinner.csproj, the project on Godot.NET.Sdk/{godot!.Version}", dialog.Command, StringComparison.Ordinal);
            string framework = dialog.Framework ?? throw new InvalidOperationException("The Godot game offers no target framework");
            Created(dialog);

            string folder = PathNames.Canonical(Path.Combine(place, "Spinner"));
            string solution = Path.Combine(folder, $"Spinner.{dialog.SolutionFormat}");
            Ui.WaitFor(() => window.WorkspaceModel?.OpenedAs == solution, () => $"the new game's solution to open ({window.Status})", TimeSpan.FromMinutes(2));
            string project = File.ReadAllText(Path.Combine(folder, "Spinner.csproj"));
            Assert.StartsWith($"<Project Sdk=\"Godot.NET.Sdk/{godot.Version}\">", project, StringComparison.Ordinal);
            Assert.Contains($"<TargetFramework>{framework}</TargetFramework>", project, StringComparison.Ordinal);
            Ui.WaitFor(() => !window.IsBuilding && window.Status?.Contains("Make Passenger Godot's Script Editor…", StringComparison.Ordinal) == true,
                () => $"the new game to be built and Passenger offered as Godot's script editor ({window.Status})", TimeSpan.FromMinutes(4));
            Assert.Equal("make Passenger Godot's Script Editor", window.StatusPlace?.Name);
            Runs(window, "Spinner");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The Godot 4 Game made in C# with the dialog's defaults keeps its solution beside project.godot and the
    /// project, as Godot's editor lays a game out, and Godot's own editor, headless with the test's user folders, builds it
    /// there into its .godot folder.</summary>
    [AvaloniaFact]
    public void A_csharp_godot_games_solution_sits_beside_project_godot_by_default_where_godots_editor_builds_it()
    {
        SkipWithoutGodot();
        MainWindow window = Started();
        Process? editor = null;
        try
        {
            NewProjectDialog dialog = Chosen(window, "C#");
            Assert.True(dialog.SameDirectory, "A C# Godot game was to put its solution beside project.godot unless told otherwise");
            Created(dialog);
            string folder = PathNames.Canonical(Path.Combine(place, "Spinner"));
            string solution = Path.Combine(folder, $"Spinner.{dialog.SolutionFormat}");
            Ui.WaitFor(() => window.WorkspaceModel?.OpenedAs == solution, () => $"the new game's solution to open beside project.godot ({window.Status})", TimeSpan.FromMinutes(2));
            Assert.True(File.Exists(Path.Combine(folder, GodotProject.FileName)) && File.Exists(Path.Combine(folder, "Spinner.csproj")),
                $"project.godot and the project were to sit beside the solution: {string.Join(", ", Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName))}");
            Ui.WaitFor(() => !window.IsBuilding && window.Status?.Contains("Make Passenger Godot's Script Editor…", StringComparison.Ordinal) == true,
                () => $"Passenger's own build of the new game to end ({window.Status})", TimeSpan.FromMinutes(4));

            string log = place + "-editor.log";
            ProcessStartInfo start = ChildProcesses.Command(godot!.Executable, [GodotExecutables.Headless, "--path", folder, "--build-solutions", "--quit", "--log-file", log], folder);
            foreach ((string name, string value) in homes)
            {
                start.Environment[name] = value;
            }
            if (WhyGodotsEditorBuildsNothing(start.Environment) is { } why)
            {
                Assert.Skip(why);
            }
            editor = ChildProcesses.Start(start);
            Ui.Await(editor.WaitForExitAsync(), "Godot's headless editor to build the game and quit", TimeSpan.FromMinutes(5));
            string said = (File.Exists(log) ? string.Join('\n', File.ReadAllLines(log).TakeLast(30)) : "no log") + BuildIssues();
            Assert.True(editor.ExitCode == 0, $"Godot's editor did not build the game where its solution sits ({ExitCodes.Said(editor.ExitCode)}):\n{said}");
            Assert.True(File.Exists(Path.Combine(folder, ".godot", "mono", "temp", "bin", "Debug", "Spinner.dll")), $"Godot's editor built no Spinner.dll:\n{said}");
        }
        finally
        {
            if (editor is not null && DotnetProcesses.Runs(editor.Id))
            {
                ChildProcesses.Kill(editor);
            }
            End(window);
        }
    }

    /// <summary>Why the Godot found, its editor started with this environment, builds no C# game here, or null where it can:
    /// one carrying a Microsoft.Build.Locator before 1.6, whose builds' dotnet has no release SDK of the .NET its editor runs
    /// on, registers MSBuild through that locator, which names a .NET 11 SDK's folders without their separator, and every
    /// build it starts then finds no Microsoft.NET.Sdk (#1715).</summary>
    private string? WhyGodotsEditorBuildsNothing(IDictionary<string, string?> environment)
    {
        const string Locator = "Microsoft.Build.Locator/";
        const string Installed = "/usr/local/share/dotnet/dotnet";
        string deps = Path.Combine(godot!.Sharp!, "Tools", "GodotTools.deps.json");
        string? Given(string name) => environment.TryGetValue(name, out string? value) && Directory.Exists(value) ? value : null;
        DotnetInstallation? Of(string? dotnet) => dotnet is null ? null : DotnetInstallation.Read(Path.GetDirectoryName(PathNames.Canonical(dotnet))!);
        if (!File.Exists(deps))
        {
            return null;
        }
        using JsonDocument read = JsonDocument.Parse(File.ReadAllText(deps));
        string? carried = JsonMembers.Properties(read.RootElement, "libraries").Select(p => p.Name)
            .FirstOrDefault(n => n.StartsWith(Locator, StringComparison.Ordinal))?[Locator.Length..];
        // The editor's own .NET: its DOTNET_ROOT's newest released runtime from 8, as its plugins roll forward.
        string? root = Given(RuntimeVariables.RootFor(RuntimeInformation.ProcessArchitecture.ToString())) ?? Given(RuntimeVariables.Root);
        int? runs = root is null ? null : DotnetInstallation.Read(root).Runtimes.Where(r => !VersionOrder.IsPrerelease(r)).Select(VersionOrder.Major).Max();
        string? onPath = Programs.CommandIn((environment.TryGetValue("PATH", out string? path) ? path ?? string.Empty : string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries), "dotnet");
        // On macOS its builds take the installer's dotnet wherever that is, else the PATH's.
        string? builder = OperatingSystem.IsMacOS() && File.Exists(Installed) ? Installed : onPath;
        if (!System.Version.TryParse(carried, out System.Version? locator) || locator >= new System.Version(1, 6) || runs is not int major || major < 8
            || Of(builder) is not { } builds || builds.Sdks.Any(s => !VersionOrder.IsPrerelease(s) && VersionOrder.Major(s) == major)
            || Of(onPath) is not { NewestSdk: { } sdk } located || VersionOrder.Major(sdk) < 11)
        {
            return null;
        }
        return $"Godot {godot.Version}'s editor builds no C# game here: it builds with {builder}, which has no .NET {major} SDK for the .NET {major} "
            + $"its editor runs on from {root}, so it registers MSBuild through its Microsoft.Build.Locator {carried}, which names the PATH's SDK {sdk} "
            + $"by its folders without their separator (MSBuildSDKsPath {Path.Combine(located.Root, "sdk", sdk)}Sdks), and the build finds no Microsoft.NET.Sdk";
    }

    /// <summary>The errors and warnings of the builds Godot's editor ran, as its build logs under the test's user folders
    /// keep them, after a line break; empty where it kept none.</summary>
    private string BuildIssues() => string.Concat(Directory.EnumerateFiles(place + "-home", "msbuild_issues.csv", SearchOption.AllDirectories)
        .Select(issues => $"\nThe build's issues ({issues}):\n{File.ReadAllText(issues)}"));

    /// <summary>The Godot 4 Game made in GDScript is its folder alone, no solution or project, which opens and runs, its script
    /// printing.</summary>
    [AvaloniaFact]
    public void A_gdscript_godot_game_is_its_folder_alone_which_opens_and_runs_printing()
    {
        SkipWithoutGodot();
        MainWindow window = Started();
        try
        {
            NewProjectDialog dialog = Chosen(window, OwnTemplates.GDScript);
            Assert.False(dialog.AsksProjectName, "A GDScript game, its folder alone, asked a project's name");
            Created(dialog);

            string folder = PathNames.Canonical(Path.Combine(place, "Spinner"));
            Ui.WaitFor(() => window.Folder == folder, () => $"the new game's folder to open ({window.Folder}; {window.Status})", TimeSpan.FromMinutes(2));
            Assert.Equal(["main.gd", "main.tscn", "project.godot"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(n => !n!.StartsWith('.')).Order(StringComparer.Ordinal));
            Runs(window, "Spinner");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>With no Godot where Settings names one, the Godot 4 Game is noted and refused before anything is made,
    /// saying where Godot comes from.</summary>
    [AvaloniaFact]
    public void A_godot_game_with_no_godot_found_is_refused_saying_how_to_get_one()
    {
        MainWindow window = Started();
        NewProjectDialog dialog = new(place, null, godot: Path.Combine(place, "no-godot", "Godot"));
        IdeErrors.Observe(dialog.ShowDialog(window));
        try
        {
            Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "dotnet new's templates to be read", TimeSpan.FromMinutes(3));
            Choose(dialog, "C#");
            Ui.WaitFor(() => dialog.WindowsNote is not null, "the game to be noted as lacking Godot");
            Assert.Contains("godotengine.org/download", dialog.WindowsNote, StringComparison.Ordinal);
            Ui.Click(dialog.CreateButton);
            Ui.WaitFor(() => dialog.Problem is not null, "Create to be refused");
            Assert.StartsWith("A Godot game needs Godot: ", dialog.Problem, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(place));
        }
        finally
        {
            dialog.Close();
            window.Close();
        }
    }

    /// <summary>A GDScript game on a Godot older than 4, by its download's name, is noted and refused before anything is made,
    /// as its Run would refuse that Godot.</summary>
    [AvaloniaFact]
    public void A_gdscript_game_on_a_godot_older_than_4_is_refused_saying_where_godot_4_comes_from()
    {
        string old = TestFolders.Create("old-godot-");
        string three = TestFolders.Write(old, "Godot_v3.5.3-stable_x11.64", string.Empty);
        MainWindow window = Started();
        NewProjectDialog dialog = new(place, null, godot: three);
        IdeErrors.Observe(dialog.ShowDialog(window));
        try
        {
            Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "dotnet new's templates to be read", TimeSpan.FromMinutes(3));
            Choose(dialog, "GDScript");
            Ui.WaitFor(() => dialog.WindowsNote is not null, "the game to be noted as lacking Godot 4");
            Assert.Contains("is Godot 3.5.3", dialog.WindowsNote, StringComparison.Ordinal);
            Ui.Click(dialog.CreateButton);
            Ui.WaitFor(() => dialog.Problem is not null, "Create to be refused");
            Assert.StartsWith("A Godot game needs Godot 4: ", dialog.Problem, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(place));
        }
        finally
        {
            dialog.Close();
            window.Close();
            TestFolders.Delete(old);
        }
    }

    /// <summary>New Solution from the Welcome screen, the Godot 4 Game chosen in a language and named Spinner, in the test's folder.</summary>
    private NewProjectDialog Chosen(MainWindow window, string language)
    {
        Ui.Click(window.Welcome.NewSolutionButton);
        Ui.Settle();
        NewProjectDialog dialog = Assert.Single(window.OwnedWindows.OfType<NewProjectDialog>());
        Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "dotnet new's templates to be read", TimeSpan.FromMinutes(3));
        dialog.Folder = place;
        Choose(dialog, language);
        dialog.SolutionName = "Spinner";
        Ui.Settle();
        return dialog;
    }

    private static void Choose(NewProjectDialog dialog, string language)
    {
        Ui.Click(dialog.FamilyRow("Godot 4 Game") ?? throw new InvalidOperationException($"New Solution lists no Godot 4 Game: [{string.Join(", ", dialog.Families.Select(f => f.Name))}]"));
        Assert.True(dialog.Chosen is { GodotGame: true }, $"Godot 4 Game chose {dialog.Chosen?.Name}");
        dialog.Language = language;
        Ui.Settle();
        Assert.Equal(language, dialog.Language);
    }

    /// <summary>Create, failing with why when the dialog made nothing.</summary>
    private static void Created(NewProjectDialog dialog)
    {
        Ui.Click(dialog.CreateButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem is not null, "the Godot game to be made", TimeSpan.FromMinutes(5));
        Assert.True(dialog.Problem is null, $"The Godot game was not made: {dialog.Problem}");
    }

    /// <summary>The game's generated configuration run headless with the run's user folders, until its script prints.</summary>
    private void Runs(MainWindow window, string name)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.Name == name),
            () => $"the game's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        RunConfiguration generated = window.Configurations.Single(c => c.Generated && c.Name == name);
        window.SetConfigurations([generated with { Name = $"{name} on Godot", Generated = false, Arguments = "--headless", Environment = new(homes) }], $"{name} on Godot");
        Ui.Launched(window, window.RunAsync(), "the game's start", TimeSpan.FromMinutes(3));
        Ui.WaitFor(() => Output(window).Contains(GodotGames.Greeting, StringComparison.Ordinal), () => $"the game's script to print ({window.Status}; {Output(window)})", TimeSpan.FromMinutes(1));
    }

    private static string Output(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }

    private static void End(MainWindow window)
    {
        if (window.IsRunning)
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, () => $"the game to end ({window.Status})", TimeSpan.FromMinutes(1));
        }
        window.Close();
    }

    private MainWindow Started()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false, GodotEnvironment = homes };
        window.Show();
        window.Start(LaunchOptions.Empty);
        Ui.Settle();
        return window;
    }
}
