using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Passenger started on a folder with <c>--run</c>, as from a shell: once the folder's projects are read, the
/// configuration is run as Run runs it, its build first and its output in the Run window; a name no configuration of the
/// folder has is refused naming those it has, and nothing runs in its place.</summary>
public sealed class CommandLineRunTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    /// <summary>A folder of one console project, Hello, with no solution, so its project comes only once the walk for it
    /// has landed, restored.</summary>
    public CommandLineRunTests(RestoredFolders folders) => folder = folders.Take("command-line-run-", made =>
    {
        TestFolders.Write(made, "Hello.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(made, "Program.cs", "Console.WriteLine(\"Hello from --run\");\n");
        return Path.Combine(made, "Hello.csproj");
    });

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_folder_opened_with_run_builds_its_configuration_and_runs_it_in_the_Run_window()
    {
        MainWindow window = Start([folder, "--run"]);
        try
        {
            Ui.Launched(window, window.StartedRun, "the run --run started, built first", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), () => $"Hello's run to end: {Ui.Said(window)}", TimeSpan.FromMinutes(1));

            string output = Output(window);
            Assert.True(window.Configuration?.Name == "Hello" && output.Contains("Hello from --run", StringComparison.Ordinal)
                && output.Contains("Process finished with exit code 0.", StringComparison.Ordinal),
                $"--run was to run Hello as Run runs it, its output in the Run window; the configuration is {window.Configuration?.Name} and the Run window shows:\n{output}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Run_naming_a_configuration_the_folder_lacks_is_refused_naming_those_it_has_and_runs_nothing()
    {
        MainWindow window = Start([folder, "--run", "Nope"]);
        try
        {
            string? refused = Ui.Await(window.StartedRun, "--run Nope's answer, once the folder's projects were read", TimeSpan.FromMinutes(1));
            Ui.Settle();

            string expected = $"--run 'Nope' names no run configuration of {Path.GetFileName(PathNames.Canonical(folder))}, whose configurations are 'Hello'";
            Assert.True(refused == expected, $"--run Nope was to be refused naming Hello, the folder's one configuration; it came to: {refused ?? "a run"}");
            Assert.True(!window.IsRunning && !window.IsBuilding && Output(window).Length == 0,
                $"--run Nope ran something in its place: {Ui.Said(window)}\n{Output(window)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A configuration that can't build leaves the window open on its failure, as a person's Run does, its status bar
    /// and Problems saying so, and says why in the log too, its first error named, for whoever started it from a shell or a
    /// script that sees no window: nothing waits in silence for a run that will never come.</summary>
    [AvaloniaFact]
    public void Run_of_a_configuration_that_cannot_build_says_why_in_the_log_and_the_window_and_runs_nothing()
    {
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "manConsole.WriteLine(\"Hello from --run\");\n");
        MainWindow window = Start([folder, "--run", "Hello"]);
        try
        {
            string? refused = Ui.Await(window.StartedRun, "--run's answer, once its build ended", TimeSpan.FromMinutes(2));
            Ui.Settle();

            Assert.True(refused is null, $"A failed build ends a person's Run with its window open, its errors to fix; --run came to: {refused}");
            string said = window.NotStarted ?? string.Empty;
            Assert.True(said.StartsWith("--run: Hello cannot run: Build failed: 1 error", StringComparison.Ordinal) && said.Contains("Program.cs:1:", StringComparison.Ordinal),
                $"--run was to say the build's failure and its first error; it says: {said}");
            Assert.True(window.Status?.StartsWith("Build failed", StringComparison.Ordinal) == true, $"The window was to say the build failed; its status says: {window.Status}");
            IdeLog.Flush();
            string log = Ui.Shared(IdeLog.PathOf(IdeLog.Ide));
            Assert.True(log.Contains($"Launch: {said}", StringComparison.Ordinal), $"The log was to say why the run never came:\n{log}");
            Assert.True(!window.IsRunning && !Output(window).Contains("Process finished", StringComparison.Ordinal), $"--run ran something with no build: {Output(window)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A person's window started as Passenger's command line starts it, with no language server, which Run needs none of.</summary>
    private static MainWindow Start(IReadOnlyList<string> args)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        window.Start(LaunchOptions.Parse(args));
        return window;
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();
}
