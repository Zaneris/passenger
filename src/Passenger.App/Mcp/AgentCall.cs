using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Passenger.App.Mcp;

/// <summary>One agent's tool call while it is served: what it does told as MCP progress every <see cref="Every"/> when the
/// call gave a progress token, and at once when it waits on the person, so a client that gives up on a call it hears
/// nothing of (OpenCode's 60 s) waits on and one that waits for hours shows what for; with protocol traces on, its start,
/// waits and end in mcp.log with their times. Made by what serves the call (<see cref="ConnectedTool"/>, <see
/// cref="McpProxy"/>); the tools say their stage through <see cref="Say"/> and <see cref="Follow"/>.</summary>
public sealed class AgentCall : IAsyncDisposable
{
    /// <summary>The variable that gives <see cref="Every"/> in milliseconds.</summary>
    [IdentifierHome]
    public const string EveryVariable = "PASSENGER_MCP_PROGRESS_MS";

    /// <summary>The call the flow serves, so a tool's own code reaches it however deep (the dispatcher flows it too).</summary>
    private static readonly AsyncLocal<AgentCall?> Serving = new();

    private static int numbered;

    private readonly McpSession? agent;
    private readonly string where;
    private readonly string title;
    private readonly bool traced;
    private readonly int number = Interlocked.Increment(ref numbered);
    private readonly long began = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource ended = new();
    private readonly SemaphoreSlim telling = new(1, 1);
    private readonly Task beating;
    private long lastTold;
    private long told;
    private long waitBegan;

    /// <summary>What it waits on, which comes before anything else it would say.</summary>
    private volatile string? waitingOn;

    /// <summary>What the window it was passed on to said last (<c>--mcp-attach</c>'s).</summary>
    private volatile string? heard;

    /// <summary>The stage of the window's work it follows, while that works.</summary>
    private volatile string? stage;

    /// <summary>What the tool said it does, last.</summary>
    private volatile string? said;

    private ActionWork? followed;
    private EventHandler<WorkChange>? following;

    private AgentCall(McpSession? agent, ProgressToken? token, string tool, string title, string where, bool traced)
    {
        this.agent = agent;
        Token = token;
        Tool = tool;
        this.title = title;
        this.where = where;
        this.traced = traced;
        lastTold = began;
        // Not held: it changes nothing drawn, and ends with the call.
        beating = token is null ? Task.CompletedTask : Task.Run(BeatAsync);
    }

    /// <summary>How often a call that takes a while tells its agent so: 10 s, or what <see cref="EveryVariable"/>
    /// gives; a value there that is no whole number of milliseconds above 0 is fatal.</summary>
    public static TimeSpan Every => Environment.GetEnvironmentVariable(EveryVariable) is { Length: > 0 } given
        ? int.TryParse(given, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : throw new FormatException($"{EveryVariable} takes a whole number of milliseconds above 0, not '{given}'")
        : TimeSpan.FromSeconds(10);

    /// <summary>The call this flow serves, or null outside one.</summary>
    public static AgentCall? Current => Serving.Value;

    /// <summary>The token the agent's progress is told under, or null when it asked for none.</summary>
    public ProgressToken? Token { get; }

    /// <summary>The tool called.</summary>
    public string Tool { get; }

    /// <summary>What the call does now, as the agent is told it: what it waits on, else what the window serving it said,
    /// else the stage of the work it follows, else what the tool said, else the tool's title.</summary>
    public string Doing => waitingOn ?? heard ?? stage ?? said ?? title;

    /// <summary>A call begun, which this flow serves from here on: told to its agent over <paramref name="agent"/> when
    /// <paramref name="token"/> is given, logged as <paramref name="where"/>'s when <paramref name="traced"/>.</summary>
    public static AgentCall Begin(McpSession? agent, ProgressToken? token, string tool, string title, string agentName, string where, bool traced)
    {
        AgentCall call = new(agent, token, tool, title, where, traced);
        Serving.Value = call;
        call.Log($"began, from {agentName}{(token is null ? string.Empty : ", told progress")}");
        return call;
    }

    /// <summary>Says what the call this flow serves does now, told at its next beat; null goes back to the tool's title.</summary>
    public static void Say(string? doing)
    {
        if (Serving.Value is { } call)
        {
            call.said = doing;
            call.stage = null;
        }
    }

    /// <summary>The call this flow serves follows the window's work under <paramref name="actions"/> (Build, Run, Debug,
    /// Profile): while one works, its stage as the status bar says it is what the call does. UI thread only.</summary>
    public static void Follow(ActionWork work, params string[] actions) => Serving.Value?.Following(work, actions);

    /// <summary>The call waits on what it cannot hurry (the person's answer, a window to start): logged, and told at once.</summary>
    public void Waits(string onWhat)
    {
        Interlocked.Exchange(ref waitBegan, Stopwatch.GetTimestamp());
        waitingOn = onWhat;
        Log($"waiting: {onWhat}");
        // Not held, and never on the caller's thread (the window's): it changes nothing drawn, and the agent's pipe may be slow.
        IdeErrors.Observe(Task.Run(() => TellAsync()));
    }

    /// <summary>The wait is over, with what it came to.</summary>
    public void Waited(string outcome)
    {
        if (waitingOn is { } on)
        {
            Log($"waited {Seconds(Stopwatch.GetElapsedTime(Interlocked.Read(ref waitBegan)))} ({on}): {outcome}");
        }
        waitingOn = null;
    }

    /// <summary>A step of the call that is no wait, logged with the time since it began.</summary>
    public void Logged(string what) => Log($"{what} at {Seconds(Stopwatch.GetElapsedTime(began))}");

    /// <summary>Progress the window serving this call told, told on to the agent as this call's own, under this call's
    /// rising numbers; the beat waits <see cref="Every"/> again from here.</summary>
    public void Heard(string? message)
    {
        if (message is { Length: > 0 })
        {
            heard = message;
        }
        IdeErrors.Observe(TellAsync(message));
    }

    /// <summary>How the call ended, logged: answered, an error, given up by the agent, failed.</summary>
    public void Ended(string how) => Log($"{how} after {Seconds(Stopwatch.GetElapsedTime(began))}");

    public async ValueTask DisposeAsync()
    {
        await ended.CancelAsync();
        if (followed is { } work && following is { } handler)
        {
            UiThread.Post(() => work.Changed -= handler);
        }
        try
        {
            // Told before the answer, so no beat comes after it; waited briefly, since the answer waits on it.
            await beating.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // A write to an agent that reads nothing: the answer goes out behind it, or not at all.
        }
    }

    private void Following(ActionWork work, string[] actions)
    {
        UiThread.Check();
        if (followed is { } before && following is { } was)
        {
            before.Changed -= was;
        }
        string? Now() => work.Latest is { } latest && actions.Contains(latest.Id, StringComparer.Ordinal) ? latest.Stage
            : actions.Select(work.Of).OfType<Working>().FirstOrDefault()?.Stage;
        followed = work;
        following = (_, change) =>
        {
            if (actions.Contains(change.Work.Id, StringComparer.Ordinal))
            {
                stage = change.Ended ? Now() : change.Work.Stage;
            }
        };
        work.Changed += following;
        stage = Now();
    }

    private async Task BeatAsync()
    {
        try
        {
            while (!ended.IsCancellationRequested)
            {
                TimeSpan due = Every - Stopwatch.GetElapsedTime(Interlocked.Read(ref lastTold));
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due, ended.Token);
                    continue;
                }
                if (heard is not null)
                {
                    // The window serving the call tells its own progress from here, which this one passes on as it comes.
                    Interlocked.Exchange(ref lastTold, Stopwatch.GetTimestamp());
                    continue;
                }
                await TellAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // The call ended.
        }
    }

    /// <summary>Tells the agent what the call does now, its progress one more than last time: one at a time, so the numbers
    /// reach it rising.</summary>
    private async Task TellAsync(string? message = null)
    {
        if (agent is null || Token is not { } token)
        {
            return;
        }
        try
        {
            await telling.WaitAsync(ended.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            Interlocked.Exchange(ref lastTold, Stopwatch.GetTimestamp());
            string words = message is { Length: > 0 } ? message : $"{Doing} ({Seconds(Stopwatch.GetElapsedTime(began))})";
            await agent.NotifyProgressAsync(token, new ProgressNotificationValue { Progress = ++told, Message = words }, cancellationToken: ended.Token);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or McpException)
        {
            // The agent went, or the call ended as it was told.
        }
        finally
        {
            telling.Release();
        }
    }

    private void Log(string what)
    {
        if (traced)
        {
            IdeLog.Write(IdeLog.Mcp, $"[{where} call {number} {Tool}] {what}");
        }
    }

    private static string Seconds(TimeSpan time) => $"{time.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s";
}
