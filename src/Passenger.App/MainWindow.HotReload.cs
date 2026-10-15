using System.Diagnostics;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Hot Reload while debugging in the window: what an Apply Changes came to in the status bar and Problems' Hot
/// Reload tab, and the restart an edit the runtime cannot take needs: at once (Settings' Always restart, on by default), or
/// asked as dotnet watch asks it, Yes, No, Always or Never.</summary>
public partial class MainWindow
{
    /// <summary>The debugging session the person said Never to restarting for, which is not asked again.</summary>
    private DebugSession? neverRestart;
    private LiveApp? neverRestartRun;

    /// <summary>How long the last restart for an edit Hot Reload could not apply took, from the program stopped to the new
    /// one launched: said with it, and read by tests.</summary>
    public TimeSpan? LastHotReloadRestart { get; private set; }

    /// <summary>dotnet watch's question, and its answers, in the order the dialog shows them (the last its default).</summary>
    public const string RestartQuestion = "Do you want to restart your app?";

    public static readonly string[] RestartAnswers = ["Never", "Always", "No", "Yes"];

    /// <summary>Rider's Apply Hot Reload Changes (Alt+F10 in both its Visual Studio keymaps, after Rerun and Stop in its Run
    /// menu): the program debugged takes the edits through Edit and Continue; one Run runs under dotnet watch takes them as
    /// the files are saved, which dotnet watch applies.</summary>
    private void AddHotReload()
    {
        Add(AppActions.ApplyHotReloadChanges, "Apply Hot Reload Changes", "Run", context =>
        {
            if (session.Debug.HotReload is not null)
            {
                _ = session.Debug.ApplyAndRestartAsync();
            }
            else
            {
                _ = SaveForWatchAsync();
            }
        }, _ => session.Debug.HotReload is not null || session.Debug.IsDebugging ? session.Debug.WhyNotApply()
            : buildRun.WatchesHotReload ? null
            : buildRun.IsRunning ? "This program runs without Hot Reload: Run it with Hot Reload on (Settings, Hot Reload), or Debug it"
            : "Nothing runs: Debug the program, or Run it with Hot Reload on (Settings, Hot Reload)", ("Run", 0, 9));
    }

    /// <summary>The words dotnet watch says an apply came to with, and what each means.</summary>
    private static readonly (string Words, string Said)[] WatchSays =
    [
        ("changes applied", "Hot Reload: dotnet watch applied the changes"),
        ("No managed code changes to apply", "Hot Reload: no managed code changes to apply"),
        ("Restart is needed to apply the changes", "Hot Reload: dotnet watch is restarting the application to apply the changes"),
        ("Restarting application", "Hot Reload: dotnet watch is restarting the application to apply the changes"),
        ("Unable to apply changes due to compilation errors", "Hot Reload: dotnet watch could not apply the changes: the code does not compile"),
    ];

    /// <summary>A program under dotnet watch takes edits as their files are saved: they are saved, and what dotnet watch
    /// says of them (in the Run window) is said in the status bar.</summary>
    private async Task SaveForWatchAsync()
    {
        ProjectSession next = session;
        bool managed = Editors.Documents.Any(d => d.IsModified && !RunXamlHotReload.IsXaml(d.Path));
        TaskCompletionSource<string> said = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<string> heard = (_, text) =>
        {
            foreach ((string words, string meaning) in WatchSays)
            {
                if (text.Contains(words, StringComparison.Ordinal))
                {
                    said.TrySetResult(meaning);
                    return;
                }
            }
        };
        Bottom.Run.Written += heard;
        try
        {
            Editors.SaveAll();
            HotReloadOutcome? xaml = next.RunXaml.Changed ? await next.RunXaml.ApplyAndRestartAsync() : null;
            if (session != next)
            {
                return;
            }
            if (managed)
            {
                status.Text = xaml is null ? "Hot Reload: saved for dotnet watch to apply" : $"{xaml.Said}; saved managed files for dotnet watch";
                if (await Task.WhenAny(said.Task, Task.Delay(TimeSpan.FromSeconds(30))) == said.Task && session == next)
                {
                    status.Text = xaml is null ? said.Task.Result : $"{said.Task.Result}; {xaml.Said}";
                }
            }
            else if (xaml is null)
            {
                status.Text = "Hot Reload: no unsaved changes to apply";
            }
        }
        finally
        {
            Bottom.Run.Written -= heard;
        }
    }

    /// <summary>Apply Hot Reload Changes on the toolbar, and in the Debug and Run windows, while a program runs with Hot
    /// Reload: debugged with it, or run under dotnet watch.</summary>
    private void ShowHotReloadButtons()
    {
        bool debugged = session.Debug.HotReload is not null;
        bool watched = buildRun.WatchesHotReload;
        HotReloadButton.IsVisible = debugged || watched;
        HotReloadButton.IsEnabled = !debugged || session.Debug.WhyNotApply() is null;
        Bottom.Debug.ShowHotReload(debugged, debugged && session.Debug.WhyNotApply() is null);
        Bottom.Run.ShowHotReload(watched);
    }

    private void OnHotReloadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Request(AppActions.ApplyHotReloadChanges);

    private void BindHotReload(ProjectSession next)
    {
        DebugController debugger = next.Debug;
        debugger.Applied += (_, outcome) =>
        {
            if (session == next)
            {
                Bottom.Problems.ShowHotReload(outcome.Said, outcome.Problems);
                if (outcome.Problems.Any(p => p.Severity == Run.ProblemSeverity.Error))
                {
                    Dock.Show("problems");
                }
            }
        };
        debugger.RestartFor = outcome => session == next ? RestartForEditAsync(next, outcome) : Task.FromResult(false);
        next.RunXaml.Applied += (_, outcome) =>
        {
            if (session == next)
            {
                Bottom.Problems.ShowHotReload(outcome.Said, outcome.Problems);
                Bottom.Run.Append($"Hot Reload: {outcome.Said}\n");
                status.Text = outcome.Said;
                if (outcome.Problems.Any(p => p.Severity == ProblemSeverity.Error))
                {
                    Dock.Show("problems");
                }
            }
        };
        next.RunXaml.RestartFor = outcome => session == next ? RestartRunForEditAsync(next, outcome) : Task.FromResult(false);
    }

    private async Task<bool> RestartRunForEditAsync(ProjectSession next, HotReloadOutcome outcome)
    {
        if (next.RunApp is not { State: not LiveAppState.Ended } watched || !buildRun.WatchesHotReload)
        {
            return false;
        }
        if (!settings.RestartOnRudeEdit)
        {
            if (neverRestartRun == watched)
            {
                return false;
            }
            string? answer = await new ChoiceDialog("Hot Reload", $"{outcome.Said}\n\n{RestartQuestion}", RestartAnswers).ShowDialog<string?>(this);
            switch (answer)
            {
                case "Always":
                    ApplySettings(settings with { RestartOnRudeEdit = true });
                    break;
                case "Yes":
                    break;
                case "Never":
                    neverRestartRun = watched;
                    return false;
                default:
                    return false;
            }
            if (session != next || next.RunApp != watched)
            {
                return false;
            }
        }
        status.Text = "Restarting application to apply XAML changes ...";
        string? refused = await buildRun.RestartRunAsync();
        if (session != next)
        {
            return false;
        }
        if (refused is not null)
        {
            status.Text = $"Could not restart to apply XAML changes: {refused}";
            return false;
        }
        Bottom.Problems.ShowHotReload(outcome.Said, outcome.Problems);
        Bottom.Run.Append($"Hot Reload: restarted watch to build the XAML changes: {outcome.Said}\n");
        status.Text = "Hot Reload: restarting the application to build the XAML changes";
        return true;
    }

    /// <summary>Restarts the program Debug launched so the edits Hot Reload could not apply run: stopped, built and launched
    /// again with its configuration and the breakpoints. True once the new session is open.</summary>
    private async Task<bool> RestartForEditAsync(ProjectSession next, HotReloadOutcome outcome)
    {
        if (next.Debug.Session is not { Attached: false } debugged)
        {
            return false;
        }
        if (!settings.RestartOnRudeEdit)
        {
            if (neverRestart == debugged)
            {
                return false;
            }
            string? answer = await new ChoiceDialog("Hot Reload", $"{outcome.Said}\n\n{RestartQuestion}", RestartAnswers).ShowDialog<string?>(this);
            switch (answer)
            {
                case "Always":
                    ApplySettings(settings with { RestartOnRudeEdit = true });
                    break;
                case "Yes":
                    break;
                case "Never":
                    neverRestart = debugged;
                    return false;
                default:
                    return false;
            }
            if (session != next)
            {
                return false;
            }
        }
        status.Text = "Restarting application to apply changes ...";
        Bottom.Debug.Console.Append("Hot Reload: Restarting application to apply changes ...\n");
        Stopwatch took = Stopwatch.StartNew();
        string? refused = await buildRun.RestartDebugAsync();
        LastHotReloadRestart = took.Elapsed;
        if (refused is not null)
        {
            status.Text = $"Could not restart to apply changes: {refused}";
            return false;
        }
        // The restart's build took Problems to its own tab; the edits that needed the restart are what to see.
        Bottom.Problems.Shown = ProblemsTab.HotReload;
        string said = $"Restarted in {took.Elapsed.TotalSeconds:0.0} s to apply changes: {outcome.Said}";
        IdeLog.Write(IdeLog.Debugger, said);
        Bottom.Debug.Console.Append($"Hot Reload: {said}\n");
        // Each edit that needed it, in a compiler message's shape, which the console links to its line.
        foreach (Run.Problem rude in outcome.Problems.Where(p => p.Path is not null))
        {
            Bottom.Debug.Console.Append($"{rude.Path}({rude.Position.Line + 1},{rude.Position.Column + 1}): {(rude.Severity == Run.ProblemSeverity.Error ? "error" : "warning")} {rude.Code}: {rude.Message}\n");
        }
        status.Text = said;
        return true;
    }
}
