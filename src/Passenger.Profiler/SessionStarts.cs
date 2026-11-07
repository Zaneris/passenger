using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>The one start of a session at a program's own diagnostic port for what is heard from it live, begun only once
/// the program is past its start where its runtime could hang (<see cref="ProgramStarts"/>), so its stop is past it too,
/// and given <see cref="Limit"/> for its runtime to take it: a client waits for good on a runtime that never answers (on
/// Windows on a pipe that is not there too). A start refused or past its limit is a <see cref="ProfilingException"/> saying
/// why, one past its limit with what the port showed then (<see cref="UnansweredPorts"/>); whether to ask again is
/// <see cref="AskAgain"/>'s to say.</summary>
public static class SessionStarts
{
    /// <summary>How long a program's runtime is given to take a session at its diagnostic port.</summary>
    public static TimeSpan Limit => Waits.Of(TimeSpan.FromSeconds(10));

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
            return await new DiagnosticsClient(processId).StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB, answered.Token);
        }
        catch (Exception error) when (DiagnosticsFailures.Of(error))
        {
            // No port to reach while the program runs: one that has just started opens it a moment later.
            throw new ProfilingException($"{unread}: {error.Message}", error) { NotYetOpen = error is ServerNotAvailableException && DotnetProcesses.Runs(processId) };
        }
        catch (OperationCanceledException unanswered) when (!cancel.IsCancellationRequested)
        {
            string why = await UnansweredPorts.WhyAsync(processId, cancel);
            throw new ProfilingException($"{unread}: its runtime took no session in {Limit.TotalSeconds:0} s ({why})", unanswered);
        }
    }

    /// <summary>Whether a start that <paramref name="failed"/> is asked again, <paramref name="asking"/> after the first: only
    /// one whose program has yet to open its port, and only within <paramref name="limit"/>, the time a runtime is given to
    /// open it. A program gone, a runtime that took no session in its time and one that refused it are said at once.</summary>
    public static bool AskAgain(ProfilingException failed, TimeSpan asking, TimeSpan limit) => failed.NotYetOpen && asking < limit;
}
