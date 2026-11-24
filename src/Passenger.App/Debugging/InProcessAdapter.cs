using System.Diagnostics;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>What a debug adapter inside the IDE shares (<see cref="WebAssemblyAdapter"/>, <see cref="Godot.GodotScriptAdapter"/>):
/// its connection to its session over an <see cref="InProcessEnd"/>, the capabilities both answer, the program it launched
/// and the input typed for it, the references a stop's values are read through until it goes on, and its line breakpoints,
/// asked for again and hit as every debugger's are (<see cref="BreakpointHits"/>).</summary>
public abstract class InProcessAdapter
{
    private readonly Dictionary<int, Func<Task<IReadOnlyList<DapVariable>>>> inside = [];

    /// <summary>The last reference given, never given again, so one of a stop gone names nothing.</summary>
    private int lastReference;

    protected InProcessAdapter(InProcessEnd end)
    {
        End = end;
        Connection = new DapConnection(end.AdapterInput, end.AdapterOutput, line => IdeLog.Write(IdeLog.Debugger, $"[{end.Name}] {line}")) { Name = end.Name };
    }

    protected InProcessEnd End { get; }

    protected DapConnection Connection { get; }

    /// <summary>The program this adapter launched, whose standard input takes what is typed in the session's console.</summary>
    protected Process? Launched { get; set; }

    /// <summary>What stops, as a refusal names it: the page, the game.</summary>
    protected abstract string Debuggee { get; }

    /// <summary>Why nothing typed in the session's console reaches the program, where this adapter launched none.</summary>
    protected abstract string NoInput { get; }

    /// <summary>The program this adapter launched, as a refusal of what is typed names it.</summary>
    protected abstract string LaunchedName { get; }

    /// <summary>Starts answering its session.</summary>
    public void Start() => Connection.Start();

    /// <summary>What both answer the session's initialize with, data breakpoints refused saying <paramref name="noData"/>.</summary>
    protected static DapCapabilities Capabilities(string noData) => new()
    {
        SupportsConfigurationDoneRequest = true,
        SupportsConditionalBreakpoints = true,
        SupportsHitConditionalBreakpoints = true,
        SupportsLogPoints = true,
        SupportsEvaluateForHovers = true,
        SupportsExceptionInfoRequest = true,
        SupportsDataBreakpoints = false,
        DataBreakpointsUnavailable = noData,
    };

    /// <summary>Text typed into the console of the program launched, for its standard input; its end closes that input.</summary>
    protected async Task<bool> InputAsync(InputArguments arguments)
    {
        if (Launched is not { } process)
        {
            throw new DapException(DapCommands.Input, NoInput);
        }
        try
        {
            if (arguments.Close == true)
            {
                process.StandardInput.Close();
            }
            else
            {
                await process.StandardInput.WriteAsync(arguments.Text);
                await process.StandardInput.FlushAsync();
            }
        }
        catch (Exception gone) when (gone is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new DapException(DapCommands.Input, $"{LaunchedName} takes no more input: {gone.Message}");
        }
        return true;
    }

    /// <summary>A reference to what a row holds, read as it is opened, until the stop is left.</summary>
    protected int Reference(Func<Task<IReadOnlyList<DapVariable>>> read)
    {
        int reference = ++lastReference;
        inside[reference] = read;
        return reference;
    }

    protected async Task<VariablesResponse> VariablesAsync(int reference) => inside.TryGetValue(reference, out Func<Task<IReadOnlyList<DapVariable>>>? read)
        ? new VariablesResponse(await read())
        : throw new DapException(DapCommands.Variables, $"Those values are of a stop the {Debuggee} has gone on from");

    /// <summary>The stop is over, or another came: what was read of it goes.</summary>
    protected virtual void Left() => inside.Clear();

    /// <summary>A line for the session's console.</summary>
    protected void Say(string line) => Tell(DapEvents.Output, new OutputEvent($"{line}\n", DapOutputCategories.Console));

    /// <summary>An event for the session, unless it has gone: the program's last output, or its end, may come as the adapter ends.</summary>
    protected void Tell(string eventName, object? body) => End.SendEvent(Connection, eventName, body);

    protected void Stopped(string reason, string? text, IReadOnlyList<int>? hit, int thread, bool allThreads, string? noSteps = null) => Tell(DapEvents.Stopped, new StoppedEvent
    {
        Reason = reason,
        Text = text,
        Description = text,
        ThreadId = thread,
        AllThreadsStopped = allThreads,
        HitBreakpointIds = hit,
        NoSteps = noSteps,
    });

    /// <summary>A breakpoint refused as asked for, for a hit count the debugger cannot read or a log message it would misread;
    /// null where it is taken.</summary>
    protected static DapBreakpoint? Refused(DapSourceBreakpoint breakpoint) =>
        (BreakpointHits.Unreadable(breakpoint.HitCondition) ?? DisplayFormat.Misread(breakpoint.LogMessage)) is { } problem
        ? new DapBreakpoint { Verified = false, Reason = BreakpointReasons.Failed, Message = problem, Line = breakpoint.Line }
        : null;

    /// <summary>What the session's breakpoints have come to between them: those hit, those temporary gone.</summary>
    protected SessionHits Hits { get; } = new();

    /// <summary>The session told that a temporary breakpoint went at the hit it took.</summary>
    protected void Removed(IHitBreakpoint breakpoint) =>
        Tell(DapEvents.Breakpoint, new BreakpointEvent { Reason = EventReasons.Removed, Breakpoint = new DapBreakpoint { Id = breakpoint.Id } });

    /// <summary>The session told of a breakpoint's message, written at a hit, at its line of <paramref name="path"/>.</summary>
    protected void Told(IHitBreakpoint breakpoint, string path, int line) => Tell(DapEvents.Breakpoint, new BreakpointEvent
    {
        Reason = EventReasons.Changed,
        Breakpoint = new DapBreakpoint { Id = breakpoint.Id, Verified = true, Message = breakpoint.Message, Line = line, Source = new DapSource { Path = path, Name = Path.GetFileName(path) } },
    });

    /// <summary>A line breakpoint as its session asked for it, with its hits so far and the debugger's word on it.</summary>
    protected class AskedLine(int id, DapSourceBreakpoint request) : IHitBreakpoint
    {
        public int Id { get; } = id;

        public DapSourceBreakpoint Request { get; } = request;

        public string? Condition => Request.Condition;

        public string? HitCondition => Request.HitCondition;

        public string? LogMessage => Request.LogMessage;

        /// <summary>Whether a hit stops: as asked, else unless it logs, as the protocol has it.</summary>
        public bool Suspend => Request.Suspend ?? Request.LogMessage is null;

        public bool RunTo => Request.RunTo == true;

        public int? Key => Request.Key;

        public int? WaitFor => Request.WaitFor;

        public bool Temporary => Request.Temporary == true;

        public int HitCount { get; set; }

        public string? Message { get; set; }
    }
}
