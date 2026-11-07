using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>What a program's diagnostic port shows once a request of it went unanswered: whether its process still runs,
/// whether its port is there at all (on Windows a client waits on a pipe that is not, where elsewhere it is refused at once),
/// and whether the port answers a request now that runs none of the program's code, which tells a runtime whose diagnostics
/// are held up from one whose own work for the request was.</summary>
internal static class UnansweredPorts
{
    /// <summary>A provider no program registers, so a session of it starts without the program's code running.</summary>
    private const string NoProvider = "Passenger-No-Provider";

    /// <summary>How long the port is given to answer that request.</summary>
    private static TimeSpan AnswerLimit => Waits.Of(TimeSpan.FromSeconds(2));

    /// <summary>The words for process <paramref name="processId"/>, whose port left a request unanswered.</summary>
    public static async Task<string> WhyAsync(int processId, CancellationToken cancel)
    {
        if (!DotnetProcesses.Runs(processId))
        {
            return $"process {processId} has ended";
        }
        if (OperatingSystem.IsWindows() && !DiagnosticsClient.GetPublishedProcesses().Contains(processId))
        {
            return $"process {processId} runs, but its diagnostic port, the pipe dotnet-diagnostic-{processId}, is not there";
        }
        string port = OperatingSystem.IsWindows() ? $"its diagnostic port, the pipe dotnet-diagnostic-{processId}," : "its diagnostic port";
        return await AnswerAsync(processId, cancel).ConfigureAwait(false) switch
        {
            null => $"process {processId} runs, and {port} answered another request at once",
            "" => $"process {processId} runs, and {port} is there but answered no other request either in {AnswerLimit.TotalSeconds:0} s",
            { } failed => $"process {processId} runs, and {port} failed another request: {failed}",
        };
    }

    /// <summary>Null once the port started and stopped a session of no provider within <see cref="AnswerLimit"/>; empty
    /// when it gave no answer by then; else why it failed.</summary>
    private static async Task<string?> AnswerAsync(int processId, CancellationToken cancel)
    {
        using CancellationTokenSource answered = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        answered.CancelAfter(AnswerLimit);
        EventPipeSession session;
        try
        {
            session = await new DiagnosticsClient(processId).StartEventPipeSessionAsync(new EventPipeProvider(NoProvider, EventLevel.Critical), requestRundown: false,
                circularBufferMB: 1, answered.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return string.Empty;
        }
        catch (Exception error) when (DiagnosticsFailures.Of(error))
        {
            return error.Message;
        }
        await EventPipeStops.StopAsync(session, AnswerLimit).ConfigureAwait(false);
        session.Dispose();
        return null;
    }
}
