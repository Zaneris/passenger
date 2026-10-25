using System.Diagnostics;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Hot Reload while debugging in the window: what an Apply Changes came to in the status bar and Problems' Hot
/// Reload tab, and the restart an edit the runtime cannot take needs: at once (Settings' Always restart, on by
/// default), or asked: Yes, No, Always or Never.</summary>
public partial class MainWindow
{
    /// <summary>The debugging session the person said Never to restarting for, which is not asked again.</summary>
    private DebugSession? neverRestart;

    /// <summary>How long the last restart for an edit Hot Reload could not apply took, from the program stopped to the
    /// new one launched, said with it.</summary>
    public TimeSpan? LastHotReloadRestart { get; private set; }

    /// <summary>dotnet watch's question, and its answers, in the order the dialog shows them (the last its default).</summary>
    public const string RestartQuestion = "Do you want to restart your app?";

    public static readonly string[] RestartAnswers = ["Never", "Always", "No", "Yes"];

    /// <summary>Apply Hot Reload Changes (Alt+F10 in both of Rider's Visual Studio keymaps): the program debugged takes
    /// the edits through Edit and Continue; one Run runs under dotnet watch takes them as the files are saved, which
    /// dotnet watch applies.</summary>
    private void AddHotReload()
    {
        Add(AppActions.ApplyHotReloadChanges, "Apply Hot Reload Changes", "Run", context =>
        {
            // A container run's services take the edits through the debugger attached to each, whatever else is debugged.
            if (session.Debug.HotReload is not null || session.Debug.Running.Count > 0)
            {
                IdeErrors.Observe(ApplyHotReloadAsync(session.Debug.SettingsRestart));
            }
            else
            {
                // Done once dotnet watch has said what it made of the save; the status bar follows its later words.
                IdeErrors.Observe(WorkingAsync(AppActions.ApplyHotReloadChanges, "Applying Hot Reload changes…", () => SaveForWatchAsync(TimeSpan.Zero)));
            }
        }, _ => session.Debug.Running.Count > 0 ? null
            : session.Debug.HotReload is not null || session.Debug.IsDebugging ? session.Debug.WhyNotApply()
            : buildRun.WatchesHotReload ? null
            : buildRun.IsRunning ? "This program runs without Hot Reload: Run it with Hot Reload on (Settings, Hot Reload), or Debug it"
            : "Nothing runs: Debug the program, or Run it with Hot Reload on (Settings, Hot Reload)", ("Run", 0, 9));
        // What dotnet watch says from now is of the save, not of the one followed.
        Editors.Opened += (_, document) => document.Saved += (_, _) => UnfollowWatch();
    }

    /// <summary>The words dotnet watch says an apply came to with, on a line of its own that begins "dotnet watch" (as Run
    /// starts it, its emojis suppressed), what each means, and what it came to.</summary>
    private static readonly (string Words, string Said, HotReloadResult Result)[] WatchSays =
    [
        ("changes applied", "Hot Reload: dotnet watch applied the changes", HotReloadResult.Applied),
        ("No managed code changes to apply", "Hot Reload: no managed code changes to apply", HotReloadResult.NothingToApply),
        ("Restart is needed to apply the changes", "Hot Reload: dotnet watch is restarting the application to apply the changes", HotReloadResult.RestartNeeded),
        ("Attempted to invoke a deleted method implementation",
            "Hot Reload: the program called a method as it was before the edit changed its signature: dotnet watch is restarting the application to apply the changes",
            HotReloadResult.RestartNeeded),
        ("Restarting application", "Hot Reload: dotnet watch is restarting the application to apply the changes", HotReloadResult.RestartNeeded),
        ("Unable to apply changes due to compilation errors", "Hot Reload: dotnet watch could not apply the changes: the code does not compile", HotReloadResult.Errors),
    ];

    /// <summary>The variable that gives <see cref="WatchSaysLater"/> in milliseconds.</summary>
    [IdentifierHome]
    public const string WatchSaysLaterVariable = "PASSENGER_WATCH_LATER_MS";

    /// <summary>How long an agent's answer waits, once dotnet watch has applied the changes, for the restart they can
    /// still end in when the program next calls a method the update deleted (one whose signature changed); the status
    /// bar follows on whatever the answer. 10 s, or what <see cref="WatchSaysLaterVariable"/> gives; a value there that
    /// is no whole number of milliseconds above 0 is fatal.</summary>
    internal static TimeSpan WatchSaysLater => Environment.GetEnvironmentVariable(WatchSaysLaterVariable) is { Length: > 0 } given
        ? int.TryParse(given, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : throw new FormatException($"{WatchSaysLaterVariable} takes a whole number of milliseconds above 0, not '{given}'")
        : TimeSpan.FromSeconds(10);

    /// <summary>Ends following what dotnet watch says of the last save made for it; null when none is followed.</summary>
    private Action? unfollowWatch;

    private void UnfollowWatch()
    {
        Action? unfollow = unfollowWatch;
        unfollowWatch = null;
        unfollow?.Invoke();
    }

    /// <summary>A program under dotnet watch takes edits as their files are saved: they are saved, and what dotnet watch says
    /// of them, a line at a time in the Run window, is said in the status bar however late (its last word, or a restart,
    /// standing) until the next save or the run's end, as a changed signature restarts the program only at its next call.
    /// The answer comes once dotnet watch has said what it made of them, <paramref name="later"/> after an apply unless a
    /// restart comes sooner; null if it said nothing of them in 30 s or before the following ended.</summary>
    internal async Task<HotReloadOutcome?> SaveForWatchAsync(TimeSpan later)
    {
        UnfollowWatch();
        // Before the following begins, which this save would end.
        Editors.SaveAll();
        status.Text = "Hot Reload: saved for dotnet watch to apply";
        ProjectSession current = session;
        ProcessRunner run = current.Runner;
        TaskCompletionSource said = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HotReloadOutcome? last = null;
        EventHandler<string> heard = (_, line) =>
        {
            // A restart said stands: dotnet watch can tell of the apply after it, the deleted method called first.
            if (session != current || WatchMeant(line) is not { } meant
                || (last?.Result == HotReloadResult.RestartNeeded && meant.Result == HotReloadResult.Applied))
            {
                return;
            }
            // Words meaning what the last meant keep its words: "Restarting application" after the reason for it.
            string words = last?.Result == meant.Result ? last.Said : meant.Said;
            // dotnet watch writes the compiler's errors in the Run window.
            Say(words, meant.Result == HotReloadResult.Errors ? StatusPlace.Window(Dock, ToolWindowIds.Run) : null);
            last = new HotReloadOutcome(meant.Result, words);
            said.TrySetResult();
            if (meant.Result != HotReloadResult.Applied)
            {
                settled.TrySetResult();
            }
        };
        EventHandler<int> ended = (_, _) => UnfollowWatch();
        run.Line += heard;
        run.Exited += ended;
        unfollowWatch = () =>
        {
            run.Line -= heard;
            run.Exited -= ended;
            // An answer still waiting is what dotnet watch had said by then.
            said.TrySetResult();
            settled.TrySetResult();
        };
        if (await Task.WhenAny(said.Task, Task.Delay(TimeSpan.FromSeconds(30))) == said.Task && later > TimeSpan.Zero)
        {
            await Task.WhenAny(settled.Task, Task.Delay(later));
        }
        return last;
    }

    /// <summary>What a line of dotnet watch's says an apply came to, or null: the program's own lines are never its.</summary>
    private static (string Said, HotReloadResult Result)? WatchMeant(string line)
    {
        if (!line.StartsWith("dotnet watch", StringComparison.Ordinal))
        {
            return null;
        }
        foreach ((string words, string meaning, HotReloadResult result) in WatchSays)
        {
            if (line.Contains(words, StringComparison.Ordinal))
            {
                return (meaning, result);
            }
        }
        return null;
    }

    /// <summary>Whether Apply Hot Reload Changes goes to dotnet watch: what Run runs with Hot Reload, nothing being debugged.</summary>
    internal bool HotReloadWatched => session.Debug.HotReload is null && !session.Debug.IsDebugging && session.Debug.Running.Count == 0 && buildRun.WatchesHotReload;

    /// <summary>Apply Hot Reload Changes to what is debugged or run with Hot Reload through the debugger, under the action's
    /// work, as the window's action and an agent's hot_reload run it (<see cref="DebugController.ApplyAllAsync"/>), each
    /// restart made as <paramref name="restart"/> says; null when one is under way already.</summary>
    internal async Task<HotReloadApplied?> ApplyHotReloadAsync(HotReloadRestart restart)
    {
        if (work.Begin(AppActions.ApplyHotReloadChanges, "Applying Hot Reload changes…", stoppable: false) is not { } working)
        {
            return null;
        }
        using (working)
        {
            ProjectSession current = session;
            return await current.Debug.ApplyAllAsync((outcome, running) => session == current && !current.IsEnded
                ? restart(outcome, running) : Task.FromResult(false));
        }
    }

    /// <summary>Why Apply Hot Reload Changes cannot run, as the action says it, or null.</summary>
    internal string? WhyNoHotReload() => actions!.WhyNot(AppActions.ApplyHotReloadChanges, actions.Context());

    /// <summary>The restart for edits Hot Reload could not apply, as an agent asks it: never the question, which is the
    /// person's; <paramref name="running"/> for the services of a container run with Hot Reload, else the program debugged.</summary>
    internal Task<bool> RestartForAgentAsync(HotReloadOutcome outcome, bool running) => running
        ? RestartOwnedServicesAsync(session, outcome, outcome.Restart)
        : RestartForAgentAsync(outcome, session.Debug.Chosen, session.Debug.Session);

    private async Task<bool> RestartForAgentAsync(HotReloadOutcome outcome, DebugTab? tab, DebugSession? debugged, CancellationToken cancel = default)
    {
        ProjectSession next = session;
        DebugController debugger = next.Debug;
        if (cancel.IsCancellationRequested || next.IsEnded)
        {
            return false;
        }
        if (tab is null || debugged is null || !tab.IsLive || tab.Session != debugged || debugger.TabOf(debugged) != tab)
        {
            return false;
        }
        if (tab.RestartFor is { } restart)
        {
            bool restarted = await restart(outcome);
            return restarted && session == next && !next.IsEnded && !cancel.IsCancellationRequested;
        }
        return debugger.Chosen == tab && await RestartForEditAsync(next, outcome, ask: false)
            && !cancel.IsCancellationRequested;
    }

    /// <summary>Apply Hot Reload Changes on the toolbar, and in the Debug and Run windows, while a program runs with Hot
    /// Reload: debugged with it, or run under dotnet watch.</summary>
    private void ShowHotReloadButtons()
    {
        bool debugged = session.Debug.HotReload is not null;
        bool watched = buildRun.WatchesHotReload || session.Debug.Running.Count > 0;
        HotReloadButton.IsVisible = debugged || watched;
        HotReloadButton.IsEnabled = !debugged || session.Debug.Running.Count > 0 || session.Debug.WhyNotApply() is null;
        Bottom.Debug.ShowHotReload(debugged, debugged && session.Debug.WhyNotApply() is null);
        Bottom.Run.ShowHotReload(watched);
    }

    private void BindHotReload(ProjectSession next)
    {
        UnfollowWatch();
        DebugController debugger = next.Debug;
        debugger.Applied += (_, outcome) =>
        {
            if (session == next && next.Debug == debugger)
            {
                Bottom.Problems.ShowHotReload(outcome.Said, outcome.Problems);
                if (outcome.Problems.Any(p => p.Severity == Run.ProblemSeverity.Error))
                {
                    Dock.Show(ToolWindowIds.Problems);
                }
                // The debugger has said it; its edits that could not apply, or warned, are where to see more.
                if (outcome.Problems.Count > 0)
                {
                    Say(outcome.Said, HotReloadPlace);
                }
            }
        };
        debugger.RestartFor = outcome => session == next && !next.IsEnded
            ? RestartForEditAsync(next, outcome) : Task.FromResult(false);
        // A container run's services, under Run with Hot Reload: built again and restarted, as dotnet watch restarts its program.
        debugger.RestartRunningFor = outcome => session == next && !next.IsEnded && settings.RestartOnRudeEdit
            ? RestartOwnedServicesAsync(next, outcome, outcome.Restart)
            : Task.FromResult(false);
    }

    private async Task<bool> RestartOwnedServicesAsync(ProjectSession current, HotReloadOutcome outcome, IReadOnlyList<string> names)
    {
        if (session != current || current.IsEnded)
        {
            return false;
        }
        // Each run of services restarts its own, a container run's and an Aspire AppHost's the same way.
        bool restarted = names.Count > 0;
        foreach (ServiceRun run in session.ServiceRuns.ToList())
        {
            string[] its = [.. names.Where(name => run.Services.Any(service => run.SessionName(service) == name))];
            if (its.Length > 0)
            {
                restarted &= await buildRun.RestartServicesForEditAsync(outcome, its);
            }
            if (session != current || current.IsEnded)
            {
                return false;
            }
        }
        return restarted;
    }

    /// <summary>Restarts the program Debug launched so the edits Hot Reload could not apply run: stopped, built and launched
    /// again with its configuration and the breakpoints, asked first as Settings says unless <paramref name="ask"/> is false.
    /// True once the new session is open.</summary>
    private async Task<bool> RestartForEditAsync(ProjectSession next, HotReloadOutcome outcome, bool ask = true)
    {
        DebugController debugger = next.Debug;
        DebugTab? tab = debugger.Chosen;
        DebugSession? debugged = tab?.Session;
        if (session != next || next.IsEnded || tab is null || !tab.IsLive || debugged is null)
        {
            return false;
        }
        // A container run's services are built again and restarted, each attached to again as it starts held.
        if (tab.RestartFor is null && tab.Group is not null && debugged.Attached)
        {
            bool restarted = (!ask || settings.RestartOnRudeEdit)
                && await buildRun.RestartServicesForEditAsync(outcome, outcome.Restart.Count > 0 ? outcome.Restart : [tab.Name]);
            return restarted && session == next && !next.IsEnded;
        }
        if (debugged.Attached)
        {
            return false;
        }
        if (ask && !settings.RestartOnRudeEdit)
        {
            if (neverRestart == debugged)
            {
                return false;
            }
            string? answer = await new ChoiceDialog("Hot Reload", $"{outcome.Said}\n\n{RestartQuestion}", RestartAnswers, "Yes").ShowDialog<string?>(this);
            if (session != next || next.IsEnded || debugger.Chosen != tab || tab.Session != debugged || !tab.IsLive)
            {
                return false;
            }
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
            if (session != next || next.IsEnded || debugger.Chosen != tab || tab.Session != debugged || !tab.IsLive)
            {
                return false;
            }
        }
        status.Text = "Restarting application to apply changes ...";
        tab.Console.Append("Hot Reload: Restarting application to apply changes ...\n");
        if (tab.RestartFor is { } restart)
        {
            bool restarted = await restart(outcome);
            return restarted && session == next && !next.IsEnded;
        }
        Stopwatch took = Stopwatch.StartNew();
        string? refused = await buildRun.RestartDebugAsync(tab.Name);
        if (session != next || next.IsEnded)
        {
            return false;
        }
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
        tab.Console.Append($"Hot Reload: {said}\n");
        // Each edit that needed it, in a compiler message's shape, at the compiler's line, which the console links to its line.
        foreach (Run.Problem rude in outcome.Problems.Where(p => p.Path is not null))
        {
            Lsp.LspPosition at = CSharp.ServerPlaces.ServerPositionOf(CSharp.ServerPlaces.TextOf(Editors, rude.Path!), rude.Position);
            tab.Console.Append($"{rude.Path}({at.Line + 1},{at.Character + 1}): {(rude.Severity == Run.ProblemSeverity.Error ? "error" : "warning")} {rude.Code}: {rude.Message}\n");
        }
        Say(said, outcome.Problems.Count > 0 ? HotReloadPlace : null);
        return true;
    }

    /// <summary>Problems' Hot Reload tab, the first edit it lists chosen.</summary>
    private StatusPlace HotReloadPlace => StatusPlace.Problems(Dock, Bottom.Problems, ProblemsTab.HotReload, _ => true);
}
