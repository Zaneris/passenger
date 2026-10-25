using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.App;
using Xunit;

namespace Passenger.Tests;

/// <summary>A click on an action that takes a while is answered at once: its button turns a ring and the status bar says
/// each stage until the work is done, a second click starts nothing more, and Stop ends the start.</summary>
public sealed class WorkingActionsTests : WorkingActionsFolders, IClassFixture<RestoredFolders>
{
    public WorkingActionsTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [AvaloniaFact]
    public void Every_toolbar_button_runs_an_action_so_it_shows_that_actions_work()
    {
        MainWindow window = Open();
        Control end = window.FindControl<Control>("ToolbarEnd")!;
        List<string> unbound = [.. end.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tool") && ActionButtons.GetAction(b) is null)
            .Select(b => b.Name ?? ToolTip.GetTip(b)?.ToString() ?? b.GetType().Name)];
        Assert.True(unbound.Count == 0, "Every toolbar button should run its action through ActionButtons, so a click on it shows the action's work "
            + $"at once and a second click starts nothing more, but these are wired by hand: {string.Join(", ", unbound)}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_click_on_Run_turns_its_ring_and_says_the_first_stage_at_once_and_a_second_click_runs_nothing_more()
    {
        MainWindow window = Open();
        Button run = Toolbar(window, "RunButton");
        Point placed = run.TranslatePoint(default, window)!.Value;
        int builds = 0;
        window.Builder.Exited += (_, _) => builds++;

        IReadOnlyList<string> said = Said(window);
        Ui.Click(run, settle: false);
        Assert.True(ActionButtons.IsWorking(run), "Run should show it is working the moment it is clicked");
        Assert.True(said.Count > 0 && said[0].StartsWith("Working out how to run Hello", StringComparison.Ordinal),
            $"The status bar should say Run's first stage the moment it is clicked, but said: {string.Join(" | ", said)}");
        Assert.True(Toolbar(window, "StopButton").IsEnabled, "Stop should be there to click from the click on, to end the start");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Assert.True(run.TranslatePoint(default, window) == placed, "Run should stay where it was clicked, so a second click lands on it and not on what moved under the pointer");
        Color accent = ((ISolidColorBrush)window.FindResource(window.ActualThemeVariant, TokenKeys.Accent)!).Color;
        int drawn = Ring(window, run).Count(c => Math.Abs(c.R - accent.R) + Math.Abs(c.G - accent.G) + Math.Abs(c.B - accent.B) < 90);
        Assert.True(drawn >= 9, $"Run's ring should be drawn round its icon in the accent in the frame after the click, but {drawn} of 16 points round it were");

        int before = said.Count;
        Ui.Click(run, settle: false);
        Assert.True(said.Skip(before).Any(s => s.StartsWith("Already under way: ", StringComparison.Ordinal)),
            $"A second click on Run should be told it is under way, but the status bar said: {string.Join(" | ", said.Skip(before))}");
        Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the program to run to its end", TimeSpan.FromMinutes(2));
        Assert.True(builds == 1, $"Two clicks on Run built {builds} times, where the second should have started nothing");
        Assert.Single(Lines(Output(window)), l => l == "Hello from the run");
        Assert.False(ActionButtons.IsWorking(run), "Run should stop showing work once its program runs");
        window.Close();
    }

    [AvaloniaFact]
    public void Stop_clicked_as_Run_starts_ends_the_start_and_nothing_runs()
    {
        MainWindow window = Open();
        Button run = Toolbar(window, "RunButton");
        Button stop = Toolbar(window, "StopButton");

        Ui.Click(run, settle: false);
        Ui.Click(stop, settle: false);
        Ui.WaitFor(() => !ActionButtons.IsWorking(run), "Run's start to end");
        Ui.Settle();
        Assert.Equal("Hello was stopped as it started", Ui.Said(window));
        Assert.False(window.Runner.IsRunning || window.IsBuilding, "Nothing should run or build once Stop ended Run's start");
        Assert.False(stop.IsEnabled, "Stop should be greyed once there is nothing to stop");
        Assert.Equal(string.Empty, Output(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Run_stopped_through_its_work_during_its_build_stops_the_build() => StoppedDuringBuild(window => window.RunAsync(), "Run");

    [AvaloniaFact]
    public void Profile_stopped_through_its_work_during_its_build_stops_the_build() => StoppedDuringBuild(window => window.ProfileAsync(), "Profile");

    /// <summary>Starts the program, and once its build is under way stops the starts the way an agent that gives up on its
    /// call does, through their work alone: the build is stopped there, not left to run to its end, and nothing runs.</summary>
    private void StoppedDuringBuild(Func<MainWindow, Task<string?>> start, string action)
    {
        MainWindow window = Open();
        List<bool> builds = [];
        window.Builder.Exited += (_, _) => builds.Add(window.Builder.WasStopped);
        Task<string?> starting = start(window);
        // The first build of a folder never built is under way long enough to be stopped.
        Ui.PumpUntil(() => window.Builder.IsRunning || starting.IsCompleted, $"{action}'s build to begin", TimeSpan.FromMinutes(1));
        Assert.False(starting.IsCompleted, $"{action} ended before its build began: {(starting.IsCompleted ? starting.Result : null)}");
        window.Actions.Work.Stop();
        Assert.Equal("Hello was stopped as it started", Ui.Await(starting, $"{action}'s start to end once stopped during its build", TimeSpan.FromMinutes(2)));
        Assert.True(builds is [true], $"{action} stopped during its build should stop the build, but its builds ended {string.Join(", ", builds.Select(b => b ? "stopped" : "run to their end"))}");
        Assert.False(window.Runner.IsRunning || window.IsBuilding || window.Profiling is not null, $"Something ran or built once {action}'s start was stopped");
        window.Close();
    }

    [AvaloniaFact]
    public void Fetch_says_it_is_fetching_at_once_and_a_second_fetch_says_it_is_under_way()
    {
        string repository = TestFolders.Create("working-git-");
        File.WriteAllText(Path.Combine(repository, "readme.txt"), "a\n");
        TestGit.Init(repository);
        MainWindow window = Ui.OpenFolder(repository);
        try
        {
            Assert.True(window.Perform(AppActions.Fetch));
            Assert.Equal("Fetching…", window.Status);
            Assert.False(window.Perform(AppActions.Fetch), "A second Fetch started while the first one worked");
            Assert.Equal("Already under way: Fetching…", window.Actions.WhyNot(AppActions.Fetch, window.Actions.Context()));
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.Fetch) is null, "the fetch to end");
            Assert.Equal("Fetched", window.Status);
        }
        finally
        {
            window.Close();
            TestFolders.Delete(repository);
        }
    }
}

/// <summary>A double click on Debug answered as a click on any action that takes a while is. Debugs through the window, so
/// the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class WorkingActionsDebuggingTests : WorkingActionsFolders, IClassFixture<RestoredFolders>
{
    public WorkingActionsDebuggingTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [AvaloniaFact]
    public void A_double_click_on_Debug_builds_and_debugs_the_program_once()
    {
        MainWindow window = Open();
        Button debug = Toolbar(window, "DebugButton");
        int builds = 0;
        HashSet<object> sessions = [];
        window.Builder.Exited += (_, _) => builds++;
        window.Debugger.StateChanged += (_, _) =>
        {
            if (window.Debugger.Session is { } session)
            {
                sessions.Add(session);
            }
        };

        List<string> stages = [];
        window.Actions.Work.Changed += (_, change) =>
        {
            if (!change.Ended)
            {
                stages.Add(change.Work.Stage);
            }
        };
        IReadOnlyList<string> said = Said(window);

        Ui.Click(debug, settle: false);
        Ui.Click(debug, settle: false);
        // Told the stage the first click's work had reached, which may have gone on to its build before the second came.
        Assert.True(stages.Count > 0 && stages[0].StartsWith("Working out how to debug Hello", StringComparison.Ordinal)
            && said.Any(s => stages.Any(stage => s == $"Already under way: {stage}")),
            $"A second click on Debug should be told the first's stage is under way; Debug said {string.Join(" | ", stages)}, the status bar {string.Join(" | ", said)}");
        Ui.WaitFor(() => !ActionButtons.IsWorking(debug) && !window.Debugger.IsDebugging, "the debugged program to start and end", TimeSpan.FromMinutes(3));
        Assert.True(builds == 1 && sessions.Count == 1, $"A double click on Debug built {builds} times and opened {sessions.Count} sessions, where it should have done each once");
        window.Close();
    }
}

/// <summary>The solution each working action's test opens, restored, and the helpers that read the toolbar and the Run
/// window.</summary>
public abstract class WorkingActionsFolders : IDisposable
{
    private readonly RestoredFolders folders;
    private string? taken;

    protected WorkingActionsFolders(RestoredFolders folders) => this.folders = folders;

    /// <summary>The test's own solution's folder, restored, taken as its window opens: Fetch's test opens a repository instead.</summary>
    protected string Folder => taken ??= folders.Take("working-", made =>
    {
        Write(made, "Sample.slnx", "<Solution>\n  <Project Path=\"Hello/Hello.csproj\" />\n</Solution>\n");
        Write(made, "Hello/Hello.csproj",
            TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
        Write(made, "Hello/Program.cs", "Console.WriteLine(\"Hello from the run\");\n");
        return Path.Combine(made, "Sample.slnx");
    });

    public void Dispose()
    {
        if (taken is not null)
        {
            TestFolders.Delete(taken);
        }
    }

    /// <summary>A window on the test's solution, with no C# language server, which its actions ask nothing of.</summary>
    protected MainWindow Open() => Ui.OpenFolder(Folder, languageServer: false);

    protected static Button Toolbar(MainWindow window, string name) =>
        window.FindControl<Button>(name) ?? throw new InvalidOperationException($"The toolbar has no {name}");

    /// <summary>The window's pixels at sixteen points of the circle the ring round a button's icon is drawn on.</summary>
    protected static Color[] Ring(MainWindow window, Button button)
    {
        WorkingRing ring = button.GetVisualDescendants().OfType<WorkingRing>().SingleOrDefault()
            ?? throw new InvalidOperationException($"{button.Name} shows no ring");
        double radius = (Math.Min(ring.Bounds.Width, ring.Bounds.Height) - WorkingRing.Stroke) / 2 - 1;
        Point centre = ring.TranslatePoint(new Point(ring.Bounds.Width / 2, ring.Bounds.Height / 2), window)!.Value;
        return [.. Enumerable.Range(0, 16).Select(i => Ui.Pixel(window, new Point(centre.X + radius * Math.Cos(i * Math.PI / 8), centre.Y + radius * Math.Sin(i * Math.PI / 8))))];
    }

    protected static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    /// <summary>Everything the status bar shows from now on, in order: what a click was told stays listed once a later stage
    /// of the work under way has taken its place, which the frames a click draws may let it.</summary>
    protected static IReadOnlyList<string> Said(MainWindow window)
    {
        TextBlock words = window.FindControl<TextBlock>("StatusText") ?? throw new InvalidOperationException("The status bar has no StatusText");
        List<string> said = [];
        words.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty && words.Text is { Length: > 0 } text)
            {
                said.Add(text);
            }
        };
        return said;
    }

    protected static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries);

    protected static void Write(string root, string relative, string text) => TestFolders.Write(root, relative, text);
}
