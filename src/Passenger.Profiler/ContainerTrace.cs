using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>Passenger's tools in a container recording a program there (their <see cref="ContainerToolsWords.Trace"/>
/// mode), begun: from then on their standard output is the session's stream, until their input ends. <see cref="Heard"/>
/// ends once their errors, which are heard for as long as they run, have been read to their end.</summary>
internal sealed record ContainerTrace(Process Tools, Task Heard)
{
    /// <summary>How long the tools are given to begin recording.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>Starts the tools' recording of <paramref name="processId"/> in <paramref name="place"/>: with
    /// <paramref name="resume"/> a program held at its start let go once recording, with <paramref name="requests"/>
    /// <see cref="LiveRequests"/>' session. Tools that cannot start, end first or have not begun in 30 s are ended, and a
    /// <see cref="ProfilingException"/> says why: their last word, else that they could not <paramref name="to"/> or did not
    /// begin <paramref name="doing"/>. <paramref name="cancel"/> ends them too, and throws.</summary>
    public static async Task<ContainerTrace> StartAsync(ProfilePlace place, int processId, bool resume, bool requests, string to, string doing,
        CancellationToken cancel)
    {
        List<string> words = [ContainerToolsWords.Trace, ContainerToolsWords.Pid, processId.ToString(CultureInfo.InvariantCulture)];
        if (resume)
        {
            words.Add(ContainerToolsWords.Resume);
        }
        if (requests)
        {
            words.Add(ContainerToolsWords.Requests);
        }
        Process tools;
        try
        {
            ProcessStartInfo start = place.Tools(words);
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            tools = ChildProcesses.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            throw new ProfilingException($"Could not start Passenger's tools in {place.Where} to {to}: {error.Message}", error);
        }
        TaskCompletionSource recording = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The last line the tools said, all that is kept of what a container writes there.
        string? said = null;
        Task heard = ErrorLines.Hear(tools, line =>
        {
            if (line.Trim() == InContainer.Recording)
            {
                recording.TrySetResult();
            }
            else if (line.Trim() is { Length: > 0 } spoken)
            {
                Volatile.Write(ref said, spoken);
            }
        });
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Limit);
        Task waited = await Task.WhenAny(recording.Task, tools.WaitForExitAsync(CancellationToken.None), Task.Delay(Timeout.Infinite, limit.Token)).ConfigureAwait(false);
        if (waited == recording.Task)
        {
            return new ContainerTrace(tools, heard);
        }
        bool ended = tools.HasExited;
        ChildProcesses.Kill(tools);
        // What they said as they ended, read to its end unless that takes a while.
        await Task.WhenAny(heard, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        tools.Dispose();
        cancel.ThrowIfCancellationRequested();
        throw new ProfilingException(ended
            ? Volatile.Read(ref said) ?? $"Passenger's tools in {place.Where} could not {to}"
            : $"Passenger's tools in {place.Where} did not begin {doing} in {Limit.TotalSeconds:0} s");
    }
}
