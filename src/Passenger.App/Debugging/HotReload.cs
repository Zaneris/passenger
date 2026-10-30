using System.Diagnostics;
using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Lsp;
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

    /// <summary>Of several programs' apply (a container run's services), those whose edits need them restarted, by name.</summary>
    public IReadOnlyList<string> Restart { get; init; } = [];
}

/// <summary>How a restart for edits Hot Reload could not apply is made, given what the apply came to and whether it is for
/// the services of a container run with Hot Reload (else the program debugged): true once restarted. The window's asks as
/// Settings say; an agent's never asks.</summary>
public delegate Task<bool> HotReloadRestart(HotReloadOutcome outcome, bool running);

/// <summary>What Apply Hot Reload Changes came to, every program that took the edits said as one, and whether a restart was
/// made for edits one could not take running.</summary>
public sealed record HotReloadApplied(HotReloadOutcome Outcome, bool Restarted);

/// <summary>A program Hot Reload applies edits to (<see cref="DebugController.ApplyAsync"/>): a Debug tab's, or a service's
/// session under Run with Hot Reload, which is quiet (no tab) and is restarted as its run's service.</summary>
internal interface IHotReloadTarget
{
    string Name { get; }

    HotReload? HotReload { get; }

    bool Quiet { get; }

    /// <summary>Says a line of what an apply came to, where the program's own output is.</summary>
    void Say(string line, bool error);
}

/// <summary>Hot Reload of one debugging session (Edit and Continue): Roslyn's own Edit and Continue service, in the language
/// server through Passenger's extension, compares the solution as the editors have it with what was built (and applied
/// since) and emits the deltas; Passenger's debugger applies them to the program, paused or running. The capabilities
/// Roslyn's rules are given are the ones the program's runtime reports. A UI-thread object.</summary>
public sealed class HotReload
{
    private DebugSession? debug;
    private readonly BuiltLines lines;
    private readonly Func<RoslynSession?> server;
    private readonly EditorArea editors;
    private int? id;
    private Task<string?>? starting;
    private RoslynSession? startedOn;
    private bool ended;

    /// <summary>Why Hot Reload stopped for the session: the runtime refused an update part way.</summary>
    private string? stopped;

    /// <summary>Each file's text before the session's first edit to it (the build's), and those Roslyn has been given.</summary>
    private readonly Dictionary<string, string> originals = new(StringComparer.Ordinal);
    private readonly HashSet<string> given = new(StringComparer.Ordinal);

    /// <summary>The edits made to the files, counted; how many Roslyn had when last asked for an update; and how many the
    /// program has taken in (or a restart for them was declined).</summary>
    private int edits;
    private int read;
    private int settled;

    /// <summary>Hot Reload for a session whose built lines are <paramref name="lines"/>, made as its launch begins so an edit
    /// made while the program starts is one it takes; its program given once open (<see cref="Opened"/>). Roslyn's session
    /// begins at the first edit (most sessions have none, and its start reads every document), given each edited file's
    /// text from before its edits, the build's.</summary>
    public HotReload(BuiltLines lines, Func<RoslynSession?> server, EditorArea editors, DebugSession? debug = null)
    {
        this.debug = debug;
        this.lines = lines;
        this.server = server;
        this.editors = editors;
    }

    /// <summary>Whether it only keeps the edits for the programs of a build still to start (<see cref="Kept"/>), beginning no
    /// session of Roslyn's.</summary>
    private bool keeps;

    /// <summary>The edits made since a build kept for each program of it still to start, whose own Hot Reload is
    /// <see cref="Carried"/> from it: each file's text from before its first edit, the edits counted, nothing applied.</summary>
    public static HotReload Kept(BuiltLines lines, Func<RoslynSession?> server, EditorArea editors) => new(lines, server, editors) { keeps = true };

    /// <summary>The program's session is open: Apply Changes takes the edits to it from now.</summary>
    public void Opened(DebugSession session) => debug = session;

    /// <summary>Hot Reload for this program restarted on the same build, without the edits it took in, whose built lines are
    /// <paramref name="restarted"/>: the files' texts from before their edits kept, every edit since the build one to apply.</summary>
    public HotReload Carried(BuiltLines restarted)
    {
        HotReload carried = new(restarted, server, editors);
        foreach ((string path, string text) in originals)
        {
            carried.originals[path] = text;
        }
        carried.edits = edits;
        return carried;
    }

    /// <summary>Whether the files have been edited since the launch or since the edits the last update was made of.</summary>
    public bool Changed => edits != settled;

    /// <summary>Whether the program has taken an update from it, even one it took only in part.</summary>
    public bool Updated { get; private set; }

    /// <summary>A restart for the edits Roslyn last read was declined: they are not applied, or asked about, again until the
    /// next edit.</summary>
    public void RestartDeclined() => settled = read;

    /// <summary>The apply is over: its update's text is no longer kept apart in the built lines.</summary>
    internal void Over() => lines.Unmark();

    /// <summary>A C# file edited while the program runs: its text before its first edit is kept, and Roslyn's session begins
    /// (none for edits only kept).</summary>
    public void Edited(string path, TextSnapshot before)
    {
        UiThread.Check();
        edits++;
        if (!originals.ContainsKey(path))
        {
            originals[path] = before.ToString();
        }
        if (!keeps)
        {
            starting ??= StartAsync();
        }
    }

    /// <summary>Why Apply Changes cannot run now, or null when it can.</summary>
    public string? WhyNot => stopped is not null ? stopped
        : debug?.State is not (DebugState.Running or DebugState.Paused) ? "The program is not running"
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
            if (ended)
            {
                End();
                return "The debugging session ended";
            }
            return null;
        }
        catch (LanguageServerFailed failed)
        {
            return $"Hot Reload could not start: {failed.Reason}";
        }
    }

    /// <summary>Applies the edits made since the launch or the last update: Roslyn emits them against the program's code and
    /// the debugger applies them; an edit the runtime cannot take, or code that does not compile, stops it, and nothing is
    /// applied. A running program is held stopped from the reading of its threads' statements to the update's end, so
    /// Roslyn places every frame in a method the update changes. An edit made once Roslyn is asked waits for the next update,
    /// the built lines keeping the text Roslyn compiled apart from it until <see cref="Over"/>.</summary>
    public async Task<HotReloadOutcome> ApplyAsync()
    {
        UiThread.Check();
        Stopwatch took = Stopwatch.StartNew();
        string? why = WhyNot;
        if (why is not null || debug is not { } program)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, why ?? "The program is not running");
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
            state = await program.HotReloadStateAsync(hold: program.State == DebugState.Running);
        }
        catch (DapException failed)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, $"The debugger could not say what the program can take: {failed.Reason}");
        }
        try
        {
            return await ApplyAsync(program, state, session, roslyn, took);
        }
        finally
        {
            if (state.Held)
            {
                await ReleaseAsync(program);
            }
        }
    }

    /// <summary>Lets the program held for an update go on; a debugger gone meanwhile has let it go already.</summary>
    private static async Task ReleaseAsync(DebugSession program)
    {
        try
        {
            await program.ReleaseHotReloadAsync();
        }
        catch (DapException failed)
        {
            IdeLog.Write(IdeLog.Debugger, $"Hot Reload could not let the program go on: {failed.Reason}");
        }
    }

    private async Task<HotReloadOutcome> ApplyAsync(DebugSession program, HotReloadStateResponse state, int session, RoslynSession roslyn, Stopwatch took)
    {
        if (state.Capabilities is not { } capabilities)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, state.CapabilitiesUnknown ?? "The program's runtime did not say which edits it can take");
        }
        bool paused = program.State == DebugState.Paused;
        HotReloadEmitted emitted;
        try
        {
            // Roslyn compiles the text as it is now: an edit made while it works waits for the next update, at its lines.
            read = edits;
            lines.Mark();
            emitted = await roslyn.ExtensionAsync<HotReloadEmitted>(HotReloadHandlers.Emit, new HotReloadEmit(session, paused, state.Held, capabilities,
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
        Func<string, TextSnapshot?> texts = ServerPlaces.Texts(editors);
        List<Problem> problems = [.. emitted.Problems.Select(p => ToProblem(p, texts))];
        switch (emitted.Status)
        {
            case "none":
                settled = read;
                return new HotReloadOutcome(HotReloadResult.NothingToApply, NothingApplied(problems))
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
            applied = await program.ApplyChangesAsync([.. emitted.Updates.Select(u => new DapModuleUpdate(u.Module, u.Name, u.Metadata, u.IL, u.Pdb,
                u.UpdatedMethods, u.UpdatedTypes,
                [.. u.Lines.Select(l => new DapLineUpdates(l.Document, [.. l.Updates.Select(x => new DapLineUpdate(x.OldLine, x.NewLine))]))],
                [.. u.ActiveStatements.Select(a => new DapStatementUpdate(a.Token, a.Version, a.ILOffset, a.StartLine, a.StartColumn, a.EndLine, a.EndColumn))],
                [.. u.FirstChanges.Select(c => new DapFirstChange(c.Document, c.Line, c.TailWas, c.TailNow))]))]);
        }
        catch (DapException failed)
        {
            await TellAsync(roslyn, HotReloadHandlers.Discard, session);
            return new HotReloadOutcome(HotReloadResult.Failed, $"The debugger could not apply the changes: {failed.Reason}") { Problems = problems, Took = took.Elapsed };
        }
        // A module the runtime took has changed the program, whatever it made of the rest.
        Updated |= applied.Modules.Any(m => m.Applied);
        if (applied.Modules.FirstOrDefault(m => !m.Applied) is { } refused)
        {
            await TellAsync(roslyn, HotReloadHandlers.Discard, session);
            stopped = refused.Message ?? $"The runtime refused the update to {refused.Name}: restart to apply changes";
            return new HotReloadOutcome(HotReloadResult.Failed, stopped) { Problems = problems, Took = took.Elapsed };
        }
        await TellAsync(roslyn, HotReloadHandlers.Commit, session);
        settled = read;
        Updated = true;
        int methods = emitted.Updates.Sum(u => u.UpdatedMethods.Count);
        List<string> files = [.. emitted.ChangedDocuments];
        string named = files.Count == 0 ? string.Empty : $": {string.Join(", ", files.Select(Path.GetFileName))}";
        return new HotReloadOutcome(HotReloadResult.Applied,
            $"C# changes applied in {took.ElapsedMilliseconds} ms{named} ({Core.Plural.Of(methods, "method")} updated)")
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
        ended = true;
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

    /// <summary>An apply that changed nothing, with what Roslyn said of it when it said anything (a file of a stale project,
    /// ENC1008, whose edits wait for a build), in its words: why an edit made did not reach the program.</summary>
    internal static string NothingApplied(IEnumerable<Problem> problems)
    {
        List<string> said = [.. problems.Select(p => p.Message.TrimEnd('.')).Distinct(StringComparer.Ordinal)];
        return said.Count == 0 ? "No managed code changes to apply."
            : $"No managed code changes to apply: {string.Join("; ", said.Take(2))}{(said.Count > 2 ? $"; and {said.Count - 2} more" : string.Empty)}.";
    }

    /// <summary>The first edits named, each with its file and line, as the status bar says them.</summary>
    private static string Describe(IEnumerable<Problem> problems)
    {
        List<Problem> named = [.. problems];
        string first = string.Join("; ", named.Take(2).Select(p => $"{p.Message} ({p.Where})"));
        return named.Count > 2 ? $"{first}; and {named.Count - 2} more" : first;
    }

    /// <summary>An edit Roslyn could not take, as Problems lists it: its id (ENC0020) the code, its place from zero as the
    /// editor counts its file's lines (Roslyn's are the server's, <see cref="ServerPlaces"/>).</summary>
    private static Problem ToProblem(HotReloadProblem problem, Func<string, TextSnapshot?> texts)
    {
        string? path = problem.Path is { } named ? PathNames.Canonical(named) : null;
        LspPosition at = new(problem.StartLine, problem.StartColumn);
        return new Problem(
            problem.Error ? ProblemSeverity.Error : ProblemSeverity.Warning,
            problem.Message,
            problem.Id,
            path,
            path is not null ? ServerPlaces.PositionOf(texts(path), at) : new TextPosition(at.Line, at.Character),
            problem.Project);
    }
}
