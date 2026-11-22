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
    /// <summary>How long the port is given to answer that request.</summary>
    private static TimeSpan AnswerLimit => Waits.Of(TimeSpan.FromSeconds(2));

    /// <summary>What process <paramref name="processId"/>'s port, which left a request unanswered, shows now: the words, and
    /// whether it answered another request at once, its runtime slow to answer the first rather than held up.</summary>
    public static async Task<(string Why, bool AnswersNow)> WhyAsync(int processId, CancellationToken cancel)
    {
        if (!DotnetProcesses.Runs(processId))
        {
            return ($"process {processId} has ended", false);
        }
        if (OperatingSystem.IsWindows() && !DiagnosticsClient.GetPublishedProcesses().Contains(processId))
        {
            return ($"process {processId} runs, but its diagnostic port, the pipe dotnet-diagnostic-{processId}, is not there", false);
        }
        string port = OperatingSystem.IsWindows() ? $"its diagnostic port, the pipe dotnet-diagnostic-{processId}," : "its diagnostic port";
        return await AnswerAsync(processId, cancel).ConfigureAwait(false) switch
        {
            null => ($"process {processId} runs, and {port} answered another request at once", true),
            "" => ($"process {processId} runs, and {port} is there but answered no other request either in {Durations.Said(AnswerLimit)}", false),
            { } failed => ($"process {processId} runs, and {port} failed another request: {failed}", false),
        };
    }

    /// <summary>Null once the port started and stopped a session of no provider within <see cref="AnswerLimit"/>; empty
    /// when it gave no answer by then; else why it failed.</summary>
    private static async Task<string?> AnswerAsync(int processId, CancellationToken cancel)
    {
        using CancellationTokenSource answered = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        answered.CancelAfter(AnswerLimit);
        try
        {
            await SessionStarts.NoProviderAsync(await DiagnosticPorts.ClientAsync(processId).ConfigureAwait(false), AnswerLimit, answered.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return string.Empty;
        }
        catch (Exception error) when (DiagnosticsFailures.Of(error))
        {
            return error.Message;
        }
        return null;
    }
}
