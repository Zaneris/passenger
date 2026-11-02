using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>New Solution's Godot 4 Game, Passenger's own, beside dotnet new's templates: in C# it makes the game and its
/// solution on the Godot found's Godot.NET.Sdk, restored from that Godot's own packages, opens it built and offers Passenger
/// as Godot's script editor, and Run starts the game, whose script prints; in GDScript it makes the game's folder alone,
/// which opens and runs the same. With no Godot found it is refused saying how to get one. Godot runs headless with the
/// run's user folders; without a Godot 4 .NET build, the tests that run it skip saying why.</summary>
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
