using System.Diagnostics;
using Passenger.App.Run;
using Passenger.App.LiveTree;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Lsp;
using Passenger.Inspector;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.Debugging;

/// <summary>What an Apply Changes came to.</summary>
public enum HotReloadResult
{
    /// <summary>The program runs the edits now.</summary>
    Applied,

    /// <summary>The edits change no code the program runs (comments, whitespace on a line).</summary>
    NothingToApply,

    /// <summary>An edit the runtime cannot take while the program runs (Roslyn's rude edits): restarting applies it.</summary>
    RestartNeeded,

    /// <summary>The code does not compile: nothing is applied until it does.</summary>
    Errors,

    /// <summary>Hot Reload could not run or the runtime refused the update; the reason says which.</summary>
    Failed,
}

/// <summary>What an Apply Changes came to, in words for the status bar and the Debug console, with the edits that could not
/// be applied (each at its line, as Problems' Hot Reload tab lists them), the files and modules the update changed, and
/// how long it took.</summary>
public sealed record HotReloadOutcome(HotReloadResult Result, string Said)
{
    public IReadOnlyList<Problem> Problems { get; init; } = [];

    /// <summary>The files whose edits the program now runs (or, with nothing to apply, whose edits needed nothing).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The modules updated, by name.</summary>
    public IReadOnlyList<string> Modules { get; init; } = [];

    /// <summary>How many methods the update replaced or added.</summary>
    public int Methods { get; init; }

    public TimeSpan Took { get; init; }
}

/// <summary>Hot Reload of one debugging session (Edit and Continue): Roslyn's own Edit and Continue service, in the language
/// server through Passenger's extension, compares the solution as the editors have it with what was built (and applied
/// since) and emits the deltas; Passenger's debugger applies them to the program, paused or running. The capabilities
/// Roslyn's rules are given are the ones the program's runtime reports. A UI-thread object.</summary>
public sealed class HotReload
{
    private readonly DebugSession debug;
    private readonly Func<RoslynSession?> server;
    private readonly Func<LiveApp?> inspector;
    private readonly Dictionary<string, XamlSourceUpdate> xaml = new(StringComparer.Ordinal);
    private long managedEdits;
    private long managedApplied;
    private bool ManagedChanged => managedEdits != managedApplied;
    private int? id;
    private Task<string?>? starting;
    private RoslynSession? startedOn;

    /// <summary>Why Hot Reload stopped for the session: the runtime refused an update part way.</summary>
    private string? stopped;

    /// <summary>Each file's text before the session's first edit to it (the build's), and those Roslyn has been given.</summary>
    private readonly Dictionary<string, string> originals = new(StringComparer.Ordinal);
    private readonly HashSet<string> given = new(StringComparer.Ordinal);

    /// <summary>Hot Reload for a session just launched. Roslyn's session begins at the first edit (most sessions have none,
    /// and its start reads every document), given each edited file's text from before its edits, the build's.</summary>
    public HotReload(DebugSession debug, Func<RoslynSession?> server, Func<LiveApp?> inspector)
    {
        this.debug = debug;
        this.server = server;
        this.inspector = inspector;
    }

    /// <summary>Whether the files have been edited since the launch or the last update applied.</summary>
    public bool Changed { get; set; }

    /// <summary>XAML left to apply once the app's UI thread can serve the inspector.</summary>
    internal bool HasXamlChanges => xaml.Count > 0;

    /// <summary>A C# file edited while the program runs: its text before its first edit is kept, and Roslyn's session begins.</summary>
    public void Edited(string path, TextSnapshot before)
    {
        UiThread.Check();
        Changed = true;
        managedEdits++;
        if (!originals.ContainsKey(path))
        {
            originals[path] = before.ToString();
        }
        starting ??= StartAsync();
    }

    /// <summary>Tracks XAML text against the last applied source; its original element positions belong to the inspector.</summary>
    public void EditedXaml(string path, TextSnapshot before, TextSnapshot after)
    {
        UiThread.Check();
        Changed = true;
        xaml[path] = new XamlSourceUpdate(path, xaml.GetValueOrDefault(path)?.Before ?? before.ToString(), after.ToString());
    }

    /// <summary>Why Apply Changes cannot run now, or null when it can.</summary>
    public string? WhyNot => stopped is not null ? stopped
        : debug.State is not (DebugState.Running or DebugState.Paused) ? "The program is not running"
        : null;

    /// <summary>The files' texts from before their edits that Roslyn has not been given yet, marked given.</summary>
    private List<HotReloadOriginal> Ungiven()
    {
        List<HotReloadOriginal> ungiven = [.. originals.Where(o => !given.Contains(o.Key)).Select(o => new HotReloadOriginal(o.Key, o.Value))];
        given.UnionWith(ungiven.Select(o => o.Path));
        return ungiven;
    }

    private async Task<string?> StartAsync()
    {
        if (server() is not { } roslyn)
        {
            return "Hot Reload needs the C# language server, which has not started";
        }
        try
        {
            HotReloadStarted started = await roslyn.ExtensionAsync<HotReloadStarted>(HotReloadHandlers.Start, new HotReloadStart([], Ungiven()));
            if (started.Problem is { } problem)
            {
                return problem;
            }
            id = started.Session;
            startedOn = roslyn;
            return null;
        }
        catch (LanguageServerFailed failed)
        {
            return $"Hot Reload could not start: {failed.Reason}";
        }
    }

    /// <summary>Applies the edits made since the launch or the last update: Roslyn emits them against the program's code and
    /// the debugger applies them; an edit the runtime cannot take, or code that does not compile, stops it, and nothing is
    /// applied. On an update applied, the session's built lines are the files' lines now.</summary>
    public Task<HotReloadOutcome> ApplyAsync() => ApplyAsync(beforeResume: false);

    /// <summary>C# must take effect before the next statement runs; XAML waits until the UI thread can run again.</summary>
    internal Task<HotReloadOutcome> ApplyBeforeResumeAsync() => ApplyAsync(beforeResume: true);

    private async Task<HotReloadOutcome> ApplyAsync(bool beforeResume)
    {
        try
        {
            return beforeResume ? ManagedChanged ? await ApplyManagedTrackedAsync()
                : new HotReloadOutcome(HotReloadResult.NothingToApply, "XAML changes will apply after the app's UI thread resumes")
                : await ApplyCurrentAsync();
        }
        finally
        {
            Changed = xaml.Count > 0 || ManagedChanged;
        }
    }

    private async Task<HotReloadOutcome> ApplyManagedTrackedAsync()
    {
        long version = managedEdits;
        HotReloadOutcome outcome = await ApplyManagedAsync();
        if (outcome.Result is HotReloadResult.Applied or HotReloadResult.NothingToApply)
        {
            managedApplied = version;
        }
        return outcome;
    }

    private async Task<HotReloadOutcome> ApplyCurrentAsync()
    {
        UiThread.Check();
        if (xaml.Count == 0)
        {
            return await ApplyManagedTrackedAsync();
        }
        if (debug.State == DebugState.Paused)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, "XAML changes need the app's UI thread: resume the program, then Apply Changes");
        }
        if (inspector() is not { } app)
        {
            return new HotReloadOutcome(HotReloadResult.RestartNeeded, "These XAML changes need a running Avalonia or WPF inspector; restart to load them");
        }
        XamlSourceUpdate[] pending = [.. xaml.Values];
        HotReloadOutcome managed = new(HotReloadResult.NothingToApply, "No managed code changes to apply");
        try
        {
            XamlUpdateResult prepared = await app.ApplyXamlAsync(new XamlUpdate(pending, ValidateOnly: true));
            if (!prepared.Applied)
            {
                return XamlFailure(prepared);
            }
            if (ManagedChanged)
            {
                managed = await ApplyManagedTrackedAsync();
            }
            if (managed.Result is not (HotReloadResult.Applied or HotReloadResult.NothingToApply))
            {
                return managed;
            }
            XamlUpdateResult applied = await app.ApplyXamlAsync(new XamlUpdate(pending));
            if (!applied.Applied)
            {
                HotReloadOutcome failed = XamlFailure(applied);
                return IncludeAppliedManaged(failed, managed);
            }
            foreach (XamlSourceUpdate source in pending)
            {
                if (xaml.GetValueOrDefault(source.File) == source)
                {
                    xaml.Remove(source.File);
                }
                else if (xaml.GetValueOrDefault(source.File) is { } edited)
                {
                    xaml[source.File] = edited with { Before = source.After };
                }
            }
            return new HotReloadOutcome(applied.Changes > 0 || managed.Result == HotReloadResult.Applied ? HotReloadResult.Applied : HotReloadResult.NothingToApply,
                $"XAML changes applied to {applied.Changes} live {(applied.Changes == 1 ? "property" : "properties")}: {string.Join(", ", pending.Select(p => Path.GetFileName(p.File)))}"
                    + (managed.Result == HotReloadResult.Applied ? $"; {managed.Said}" : ""))
            {
                Files = [.. managed.Files.Concat(pending.Select(p => p.File)).Distinct(StringComparer.Ordinal)],
                Problems = managed.Problems,
                Modules = managed.Modules,
                Methods = managed.Methods,
            };
        }
        catch (LiveAppException error)
        {
            return IncludeAppliedManaged(new HotReloadOutcome(HotReloadResult.Failed, $"The app did not confirm the XAML changes: {error.Message}"), managed);
        }
    }

    private static HotReloadOutcome IncludeAppliedManaged(HotReloadOutcome outcome, HotReloadOutcome managed) => managed.Result == HotReloadResult.Applied
        ? outcome with { Said = $"{managed.Said}; {outcome.Said}", Files = managed.Files, Modules = managed.Modules, Methods = managed.Methods }
        : outcome;

    internal static HotReloadOutcome XamlFailure(XamlUpdateResult result)
    {
        List<Problem> problems = [.. result.Problems.Select(p => new Problem(ProblemSeverity.Error, p.Message, "XAMLHR", p.File,
            new TextPosition(Math.Max(0, p.Line - 1), Math.Max(0, p.Column - 1)), null))];
        bool restart = result.Problems.Any(p => p.RestartRequired);
        return new HotReloadOutcome(restart ? HotReloadResult.RestartNeeded : HotReloadResult.Errors,
            $"{(restart ? "Restart is needed to apply XAML changes" : "Unable to apply XAML changes")}: {Describe(problems)}") { Problems = problems };
    }

    private async Task<HotReloadOutcome> ApplyManagedAsync()
    {
        UiThread.Check();
        Stopwatch took = Stopwatch.StartNew();
        if (WhyNot is { } why)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, why);
        }
        starting ??= StartAsync();
        if (await starting is not null)
        {
            // The server may have been loading at the first edit: Roslyn checks edited files against the PDB's checksums.
            given.Clear();
            starting = StartAsync();
            if (await starting is { } notStarted)
            {
                return new HotReloadOutcome(HotReloadResult.Failed, notStarted);
            }
        }
        if (id is not int session || startedOn is not { } roslyn || roslyn != server())
        {
            return new HotReloadOutcome(HotReloadResult.Failed, "The C# language server restarted since the program was launched: restart to apply changes");
        }
        HotReloadStateResponse state;
        try
        {
            state = await debug.HotReloadStateAsync();
        }
        catch (DapException failed)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, $"The debugger could not say what the program can take: {failed.Reason}");
        }
        if (state.Capabilities is not { } capabilities)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, state.CapabilitiesUnknown ?? "The program's runtime did not say which edits it can take");
        }
        bool paused = debug.State == DebugState.Paused;
        HotReloadEmitted emitted;
        try
        {
            emitted = await roslyn.ExtensionAsync<HotReloadEmitted>(HotReloadHandlers.Emit, new HotReloadEmit(session, paused, capabilities,
                [.. state.Modules.Where(m => m.Editable).Select(m => m.Mvid)],
                [.. state.ActiveStatements.Select(s => new HotReloadActiveStatement(s.Module, s.Token, s.Version, s.ILOffset, s.Document,
                    s.StartLine, s.StartColumn, s.EndLine, s.EndColumn, s.Flags))],
                [.. state.Modules.Where(m => !m.Editable).Select(m => new HotReloadLockedModule(m.Mvid, m.WhyNot ?? $"{m.Name} cannot be edited"))],
                Ungiven()));
        }
        catch (LanguageServerFailed failed)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, $"Hot Reload failed in the C# language server: {failed.Reason}");
        }
        if (emitted.Problem is { } problem)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, $"Hot Reload failed: {problem}");
        }
        List<Problem> problems = [.. emitted.Problems.Select(ToProblem)];
        switch (emitted.Status)
        {
            case "none":
                return new HotReloadOutcome(HotReloadResult.NothingToApply, "No managed code changes to apply.")
                {
                    Problems = problems,
                    Files = emitted.ChangedDocuments,
                    Took = took.Elapsed,
                };
            case "blocked":
                bool compiles = !emitted.Problems.Any(p => p.Error && !p.RestartRequired);
                return new HotReloadOutcome(compiles ? HotReloadResult.RestartNeeded : HotReloadResult.Errors,
                    compiles ? $"Restart is needed to apply the changes: {Describe(problems.Where(p => p.Severity == ProblemSeverity.Error))}"
                        : $"Unable to apply changes due to compilation errors: {Describe(problems.Where(p => p.Severity == ProblemSeverity.Error))}")
                {
                    Problems = problems,
                    Took = took.Elapsed,
                };
        }
        ApplyChangesResponse applied;
        try
        {
            applied = await debug.ApplyChangesAsync([.. emitted.Updates.Select(u => new DapModuleUpdate(u.Module, u.Name, u.Metadata, u.IL, u.Pdb,
                u.UpdatedMethods, u.UpdatedTypes,
                [.. u.Lines.Select(l => new DapLineUpdates(l.Document, [.. l.Updates.Select(x => new DapLineUpdate(x.OldLine, x.NewLine))]))],
                [.. u.ActiveStatements.Select(a => new DapStatementUpdate(a.Token, a.Version, a.ILOffset, a.StartLine, a.StartColumn, a.EndLine, a.EndColumn))]))]);
        }
        catch (DapException failed)
        {
            await TellAsync(roslyn, HotReloadHandlers.Discard, session);
            return new HotReloadOutcome(HotReloadResult.Failed, $"The debugger could not apply the changes: {failed.Reason}") { Problems = problems, Took = took.Elapsed };
        }
        if (applied.Modules.FirstOrDefault(m => !m.Applied) is { } refused)
        {
            await TellAsync(roslyn, HotReloadHandlers.Discard, session);
            stopped = refused.Message ?? $"The runtime refused the update to {refused.Name}: restart to apply changes";
            return new HotReloadOutcome(HotReloadResult.Failed, stopped) { Problems = problems, Took = took.Elapsed };
        }
        await TellAsync(roslyn, HotReloadHandlers.Commit, session);
        int methods = emitted.Updates.Sum(u => u.UpdatedMethods.Count);
        List<string> files = [.. emitted.ChangedDocuments];
        string named = files.Count == 0 ? string.Empty : $": {string.Join(", ", files.Select(Path.GetFileName))}";
        return new HotReloadOutcome(HotReloadResult.Applied,
            $"C# changes applied in {took.ElapsedMilliseconds} ms{named} ({(methods == 1 ? "1 method" : $"{methods} methods")} updated)")
        {
            Problems = problems,
            Files = files,
            Modules = [.. emitted.Updates.Select(u => u.Name)],
            Methods = methods,
            Took = took.Elapsed,
        };
    }

    /// <summary>Ends Roslyn's session with the debugging session's end.</summary>
    public void End()
    {
        if (id is int session && startedOn is { } roslyn)
        {
            id = null;
            roslyn.TellExtension(HotReloadHandlers.End, new HotReloadSessionMessage(session));
        }
    }

    private static async Task TellAsync(RoslynSession roslyn, string handler, int session)
    {
        try
        {
            HotReloadDone done = await roslyn.ExtensionAsync<HotReloadDone>(handler, new HotReloadSessionMessage(session));
            if (done.Problem is { } problem)
            {
                IdeLog.Write(IdeLog.Debugger, $"Hot Reload {handler}: {problem}");
            }
        }
        catch (LanguageServerFailed failed)
        {
            IdeLog.Write(IdeLog.Debugger, $"Hot Reload {handler}: {failed.Reason}");
        }
    }

    /// <summary>The first edits named, each with its file and line, as the status bar says them.</summary>
    private static string Describe(IEnumerable<Problem> problems)
    {
        List<Problem> named = [.. problems];
        string first = string.Join("; ", named.Take(2).Select(p => $"{p.Message} ({p.Where})"));
        return named.Count > 2 ? $"{first}; and {named.Count - 2} more" : first;
    }

    /// <summary>An edit Roslyn could not take, as Problems lists it: its id (ENC0020) the code, its place from zero.</summary>
    private static Problem ToProblem(HotReloadProblem problem) => new(
        problem.Error ? ProblemSeverity.Error : ProblemSeverity.Warning,
        problem.Message,
        problem.Id,
        problem.Path is { } path ? PathNames.Canonical(path) : null,
        new TextPosition(problem.StartLine, problem.StartColumn),
        problem.Project);
}
