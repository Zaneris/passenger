using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>New Solution's Godot 4 Game, Passenger's own, beside dotnet new's templates: in C# it makes the game and
/// its solution on the Godot found's Godot.NET.Sdk, restored from that Godot's own packages, opens it built and offers
/// Passenger as Godot's script editor, and Run starts the game, whose script prints; it targets the newest .NET Godot's
/// own editor builds and loads it on; in GDScript it makes the game's folder alone, which opens and runs the same.
/// Godot runs headless with the run's user folders; without a Godot 4 .NET build, the tests that run it skip saying
/// why.</summary>
[AboutMacOS]
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

    /// <summary>A new Godot game, C# or GDScript, is made, opens, builds and runs on the .NET its Godot
    /// takes.</summary>
    [AvaloniaFact]
    public void A_new_godot_game_is_made_opens_builds_and_runs()
    {
        Flow.StepApart<NewGodotGameTests>(nameof(A_csharp_godot_game_is_made_on_the_godots_sdk_opens_built_and_runs_printing),
            test => test.A_csharp_godot_game_is_made_on_the_godots_sdk_opens_built_and_runs_printing());
        OpenWindows.CloseAll();
        Flow.StepApart<NewGodotGameTests>(nameof(A_gdscript_godot_game_is_its_folder_alone_which_opens_and_runs_printing),
            test => test.A_gdscript_godot_game_is_its_folder_alone_which_opens_and_runs_printing());
    }

    /// <summary>The Godot 4 Game made in C#, its solution and project in one folder, opens built with Passenger offered as
    /// Godot's script editor, and runs, its script printing.</summary>
    private void A_csharp_godot_game_is_made_on_the_godots_sdk_opens_built_and_runs_printing()
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

    /// <summary>A C# Godot game on .NET 10 where Godot's editor on macOS builds with the installer's dotnet, which holds a
    /// released .NET 10 SDK, while DOTNET_ROOT names another installation, whose newest .NET it would otherwise run on and
    /// find no released SDK of there: Godot's editor started through Passenger runs on the installer's .NET and builds the
    /// game, where every build it started failed (#1726).</summary>
    [AvaloniaFact]
    public void A_csharp_godot_game_on_dotnet_10_is_built_by_godots_editor_through_passenger_where_dotnet_root_names_another_installation()
    {
        SkipWithoutGodot();
        string? root = Environment.GetEnvironmentVariable(RuntimeVariables.RootFor(RuntimeInformation.ProcessArchitecture.ToString())) ?? Environment.GetEnvironmentVariable(RuntimeVariables.Root);
        Assert.SkipUnless(OperatingSystem.IsMacOS() && root is { Length: > 0 } && !PathNames.Same(PathNames.Canonical(root), GodotEditorDotnet.InstallerRoot)
            && DotnetInstallation.Read(GodotEditorDotnet.InstallerRoot).Sdks.Any(s => !VersionOrder.IsPrerelease(s) && VersionOrder.Major(s) == 10),
            $"Godot's editor builds with the installer's dotnet on macOS alone, and this test needs DOTNET_ROOT naming another installation ({root ?? "unset"}) "
            + $"and a released .NET 10 SDK in {GodotEditorDotnet.InstallerRoot}");
        MainWindow window = Started();
        try
        {
            NewProjectDialog dialog = Chosen(window, "C#");
            dialog.Framework = "net10.0";
            Assert.SkipWhen(dialog.Framework != "net10.0", $"New Solution offers the Godot game no net10.0 here, only {dialog.Framework}");
            Created(dialog);
            string folder = PathNames.Canonical(Path.Combine(place, "Spinner"));
            Ui.WaitFor(() => window.WorkspaceModel?.OpenedAs is not null, () => $"the new game's solution to open ({window.Status})", TimeSpan.FromMinutes(2));
            (string said, int code, bool built) = BuiltByGodotsEditor(window, folder);
            Assert.True(code == 0 && built && Unbuilt(said) is null, $"Godot's editor started through Passenger did not build the net10.0 game ({ExitCodes.Said(code)}), "
                + $"DOTNET_ROOT naming {root}:\n{said}");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Where the newest .NET whose game Godot's editor builds and runs here is older than Passenger's own (on macOS,
    /// the installer's .NET 10, which a Godot opened from the Dock runs on, beside the .NET 11 Passenger runs on), the C# Godot
    /// game targets it by default, the dialog saying so under the framework and warning of a newer one picked; Godot's editor
    /// started through Passenger builds the game, and opened on that .NET loads it, where the net11.0 game made before was
    /// built by the one and failed to load in the other (#1727).</summary>
    [AvaloniaFact]
    public void A_csharp_godot_game_targets_the_newest_dotnet_its_godot_builds_and_runs_which_godots_editor_builds_and_loads()
    {
        SkipWithoutGodot();
        GodotEditorDotnet.GameTargets? targets = GodotEditorDotnet.TargetsOf(godot!, place);
        int own = DotnetMachine.Read().Installation?.Runtimes.Where(r => !VersionOrder.IsPrerelease(r)).Select(VersionOrder.Major).Max() ?? 0;
        Assert.SkipUnless(targets is { Newest.Major: int newest } && newest < own, targets is null
            ? "Nothing here says which .NET Godot's editor builds and runs a game on (macOS and Godot's .NET build alone do)"
            : $"Godot's editor builds and runs a game here on .NET {targets.Newest?.Major.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, no older than Passenger's .NET {own}");
        GodotEditorDotnet.GameDotnet godots = targets!.Newest!;
        MainWindow window = Started();
        try
        {
            NewProjectDialog dialog = Chosen(window, "C#");
            string fits = $"net{godots.Major}.0";
            Assert.True(dialog.Framework == fits, $"The game targets {dialog.Framework} by default, which Godot's editor here builds or runs no game of: {targets.WhyNot(dialog.Framework)}");
            Assert.Equal($"{targets.Editor} builds and runs a game here on .NET {godots.Major} at most, that of {godots.Root}, older than Passenger's .NET {own}", dialog.FrameworkNote);
            Assert.Null(dialog.WindowsNote);
            string newer = $"net{own}.0";
            dialog.Framework = newer;
            Ui.Settle();
            Assert.True(dialog.Framework == newer && dialog.WindowsNote == targets.WhyNot(newer) && dialog.WindowsNote is not null,
                $"A {newer} game picked was noted as \"{dialog.WindowsNote}\" (chosen {dialog.Framework})");
            dialog.Framework = fits;
            Ui.Settle();
            Assert.Null(dialog.WindowsNote);
            Created(dialog);
            string folder = PathNames.Canonical(Path.Combine(place, "Spinner"));
            Ui.WaitFor(() => window.WorkspaceModel?.OpenedAs is not null, () => $"the new game's solution to open ({window.Status})", TimeSpan.FromMinutes(2));
            Assert.Contains($"<TargetFramework>{fits}</TargetFramework>", File.ReadAllText(Path.Combine(folder, "Spinner.csproj")), StringComparison.Ordinal);
            (string said, int code, bool built) = BuiltByGodotsEditor(window, folder);
            Assert.True(code == 0 && built && Unbuilt(said) is null, $"Godot's editor started through Passenger did not build the {fits} game ({ExitCodes.Said(code)}):\n{said}");
            string opened = OpenedByGodotsEditor(window, godots.Root);
            Assert.True(!opened.Contains("Could not load file or assembly", StringComparison.Ordinal) && Unbuilt(opened) is null,
                $"Godot's editor on the .NET of {godots.Root} did not load the {fits} game:\n{opened}");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>What the Run window said as Godot's editor opened the game built and quit, started through the game's Godot
    /// Editor configuration, headless with the test's user folders, DOTNET_ROOT and this processor's naming
    /// <paramref name="root"/> as the person's own, so it runs on that .NET; the editor loads the game's assembly as it opens.</summary>
    private string OpenedByGodotsEditor(MainWindow window, string root)
    {
        RunConfiguration editor = window.Configurations.First(c => c.Generated && c.GodotEditor);
        window.SetConfigurations([editor with { Name = "Godot opens", Generated = false, Arguments = $"{GodotExecutables.Headless} --quit", BuildBeforeLaunch = false,
            Environment = new(homes) { [RuntimeVariables.Root] = root, [RuntimeVariables.RootFor(RuntimeInformation.ProcessArchitecture.ToString())] = root } }], "Godot opens");
        Ui.Launched(window, window.RunAsync(), "Godot's editor's start", TimeSpan.FromMinutes(3));
        int code = Ui.Await(window.RunEnd, "Godot's editor to open the game and quit", TimeSpan.FromMinutes(5));
        return $"{Output(window)}\n(Godot's editor {ExitCodes.Said(code)})";
    }

    /// <summary>The game's build by Godot's own editor, once Passenger's own has ended: the editor started through the game's
    /// generated Godot Editor configuration, headless with the test's user folders, building the solution and quitting,
    /// Passenger building nothing first. What the Run window said (and Godot's build logs), the editor's exit code, and
    /// whether Godot's own build ended well, as its build log under the test's user folders keeps it, the game's assembly there.</summary>
    private (string Said, int Code, bool Built) BuiltByGodotsEditor(MainWindow window, string folder)
    {
        Ui.WaitFor(() => !window.IsBuilding && window.Status?.Contains("Make Passenger Godot's Script Editor…", StringComparison.Ordinal) == true,
            () => $"Passenger's own build of the new game to end ({window.Status})", TimeSpan.FromMinutes(4));
        Ui.WaitFor(() => window.Configurations.Any(c => c.Generated && c.GodotEditor),
            () => $"the game's Godot Editor configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})", TimeSpan.FromMinutes(1));
        RunConfiguration editor = window.Configurations.First(c => c.Generated && c.GodotEditor);
        window.SetConfigurations([editor with { Name = "Godot builds", Generated = false, Arguments = $"{GodotExecutables.Headless} --build-solutions --quit",
            BuildBeforeLaunch = false, Environment = new(homes) }], "Godot builds");
        Ui.Launched(window, window.RunAsync(), "Godot's editor's start", TimeSpan.FromMinutes(3));
        int code = Ui.Await(window.RunEnd, "Godot's editor to build the game and quit", TimeSpan.FromMinutes(5));
        bool built = Directory.EnumerateFiles(place + "-home", "msbuild_log.txt", SearchOption.AllDirectories)
            .Any(log => File.ReadLines(log).LastOrDefault(l => l.StartsWith("Done building project", StringComparison.Ordinal)) == "Done building project \"Spinner.csproj\".");
        return (Output(window) + BuildIssues(), code, built && File.Exists(Path.Combine(folder, IgnoredFolders.GodotCache, "mono", "temp", "bin", "Debug", "Spinner.dll")));
    }

    /// <summary>What Passenger said in the Run window of why Godot's editor builds or runs no C# of the game; null for nothing.</summary>
    private static string? Unbuilt(string said) => said.Split('\n').FirstOrDefault(l => l.Contains("'s editor builds no C# here", StringComparison.Ordinal)
        || l.Contains("'s editor runs no C# of the game here", StringComparison.Ordinal));

    /// <summary>The errors and warnings of the builds Godot's editor ran, as its build logs under the test's user folders
    /// keep them, after a line break; empty where it kept none.</summary>
    private string BuildIssues() => string.Concat(Directory.EnumerateFiles(place + "-home", "msbuild_issues.csv", SearchOption.AllDirectories)
        .Select(issues => $"\nThe build's issues ({issues}):\n{File.ReadAllText(issues)}"));

    /// <summary>The Godot 4 Game made in GDScript is its folder alone, no solution or project, which opens and runs, its script
    /// printing.</summary>
    private void A_gdscript_godot_game_is_its_folder_alone_which_opens_and_runs_printing()
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
