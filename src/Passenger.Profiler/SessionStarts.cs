using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>The one start of a session at a program's own diagnostic port for what is heard from it live, begun only once
/// the program is past its start where its runtime could hang (<see cref="ProgramStarts"/>), so its stop is past it too,
/// and given <see cref="Limit"/> for its runtime to take it: a client waits for good on a runtime that never answers (on
/// Windows on a pipe that is not there too). A start refused or past its limit is a <see cref="ProfilingException"/> saying
/// why, one past its limit with what the port showed then (<see cref="UnansweredPorts"/>); whether to ask again is
/// <see cref="AskAgain"/>'s to say. Every session that must hear the program from its first moment starts through
/// <see cref="OnAsync"/>.</summary>
public static class SessionStarts
{
    /// <summary>A provider no program registers, so a session of it starts without the program's code running.</summary>
    internal const string NoProvider = "Passenger-No-Provider";

    /// <summary>The variable that gives <see cref="Limit"/> in milliseconds, before the machine's scale.</summary>
    [IdentifierHome]
    public const string LimitVariable = "PASSENGER_SESSION_START_MS";

    /// <summary>How long a program's runtime is given to take a session at its diagnostic port: 10 s, or what
    /// <see cref="LimitVariable"/> gives (or <see cref="HoldLimit"/> holds), as much longer as <see cref="Waits"/> says; a
    /// value there that is no whole number of milliseconds above 0 is fatal.</summary>
    public static TimeSpan Limit => Waits.Of(Volatile.Read(ref heldLimit) is > 0 and long held
        ? TimeSpan.FromTicks(held)
        : EnvironmentValues.Milliseconds(LimitVariable, TimeSpan.FromSeconds(10)));

    /// <summary>The ticks <see cref="HoldLimit"/> holds <see cref="Limit"/> to before the machine's scale; 0 when none.</summary>
    private static long heldLimit;

    /// <summary>For a test standing in for a runtime that takes no session: <see cref="Limit"/> is <paramref name="limit"/>
    /// (before the machine's scale) in this process alone until the hold is disposed, so its starts give up sooner, while
    /// the programs it starts meanwhile keep their own.</summary>
    public static IDisposable HoldLimit(TimeSpan limit) => new HeldLimit(Interlocked.Exchange(ref heldLimit, limit.Ticks));

    private sealed class HeldLimit(long before) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref heldLimit, before);
    }

    /// <summary>A session of <paramref name="providers"/> at process <paramref name="processId"/>'s port, its failure said
    /// after <paramref name="unread"/> ("Its metrics could not be read"); <paramref name="waiting"/> told how long it waits
    /// first for the program to finish starting, where it does.</summary>
    internal static async Task<EventPipeSession> StartAsync(int processId, IEnumerable<EventPipeProvider> providers, int circularBufferMB, string unread,
        Action<TimeSpan>? waiting, CancellationToken cancel)
    {
        TimeSpan due = await BackgroundWork.Run(() => ProgramStarts.DueAt(processId), cancel);
        if (due > TimeSpan.Zero)
        {
            waiting?.Invoke(due);
            await Task.Delay(due, cancel);
        }
        using CancellationTokenSource answered = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        answered.CancelAfter(Limit);
        try
        {
            DiagnosticsClient client = new(processId);
            EventPipeSession started = await client.StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB, answered.Token);
            return await TurnedOnAsync(client, started, cancel);
        }
        catch (Exception error) when (DiagnosticsFailures.Of(error))
        {
            // No port to reach while the program runs: one that has just started opens it a moment later.
            throw new ProfilingException($"{unread}: {error.Message}", error) { NotYetOpen = error is ServerNotAvailableException && DotnetProcesses.Runs(processId) };
        }
        catch (OperationCanceledException unanswered) when (!cancel.IsCancellationRequested)
        {
            string why = await UnansweredPorts.WhyAsync(processId, cancel);
            throw new ProfilingException($"{unread}: its runtime took no session in {Durations.Said(Limit)} ({why})", unanswered);
        }
    }

    /// <summary>A session of <paramref name="providers"/> at the port <paramref name="client"/> reaches, once its providers
    /// are on (<see cref="TurnedOnAsync"/>). Given up as <paramref name="cancel"/> is, the session let go.</summary>
    public static async Task<EventPipeSession> OnAsync(DiagnosticsClient client, IEnumerable<EventPipeProvider> providers, int circularBufferMB,
        CancellationToken cancel) =>
        await TurnedOnAsync(client, await client.StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB, cancel), cancel);

    /// <summary><paramref name="session"/>, once its providers are on: a runtime may answer a session's start before it turns
    /// them on (.NET 11 does), and its diagnostics server takes one request at a time, so a session of no provider asked for
    /// next is answered only once they are, within <see cref="Limit"/> of its own. The session is let go if that fails.</summary>
    private static async Task<EventPipeSession> TurnedOnAsync(DiagnosticsClient client, EventPipeSession session, CancellationToken cancel)
    {
        bool on = false;
        try
        {
            await NoProviderAsync(client, Limit, cancel);
            on = true;
            return session;
        }
        finally
        {
            if (!on)
            {
                session.Dispose();
            }
        }
    }

    /// <summary>Starts a session of no provider at the port <paramref name="client"/> reaches and stops it, each within
    /// <paramref name="limit"/>, its stream read to the end as it stops, since a runtime stopping a session whose stream no
    /// one reads (a pipe, on Windows) holds its every other request. Throws as a start does, cancelled once past the limit.</summary>
    internal static async Task NoProviderAsync(DiagnosticsClient client, TimeSpan limit, CancellationToken cancel)
    {
        using CancellationTokenSource asked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        asked.CancelAfter(limit);
        using EventPipeSession session = await client.StartEventPipeSessionAsync(new EventPipeProvider(NoProvider, EventLevel.Critical), requestRundown: false,
            circularBufferMB: 1, asked.Token);
        Task read = session.EventStream.CopyToAsync(Stream.Null, cancel);
        UnawaitedWork.Dismiss(read);
        if (await EventPipeStops.StopAsync(session, limit, cancel))
        {
            await read.WaitAsync(limit, cancel).Ended();
        }
    }

    /// <summary>Whether a start that <paramref name="failed"/> is asked again, <paramref name="asking"/> after the first: only
    /// one whose program has yet to open its port, and only within <paramref name="limit"/>, the time a runtime is given to
    /// open it. A program gone, a runtime that took no session in its time and one that refused it are said at once.</summary>
    public static bool AskAgain(ProfilingException failed, TimeSpan asking, TimeSpan limit) => failed.NotYetOpen && asking < limit;
}
