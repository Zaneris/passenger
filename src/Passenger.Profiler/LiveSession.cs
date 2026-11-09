using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>A session heard live while someone looks (<see cref="LiveMetrics"/>, <see cref="LiveRequests"/>): its stream
/// read to its end on a thread of its own (<see cref="EventPipeReads"/>), its events hooked before, and stopped at
/// <see cref="DisposeAsync"/>, at a program's own diagnostic port by the runtime, in a container by Passenger's tools
/// there ending through their input.</summary>
internal sealed class LiveSession : IAsyncDisposable
{
    private readonly EventPipeEventSource source;
    private readonly Func<Task> stop;

    private LiveSession(Stream stream, Action<EventPipeEventSource> follow, Func<Task> stop)
    {
        source = new EventPipeEventSource(stream);
        this.stop = stop;
        follow(source);
        Ended = EventPipeReads.ToEndAsync(source, lasting: true);
    }

    /// <summary>Done once the stream ends: the program gone, or the session stopped.</summary>
    public Task Ended { get; }

    /// <summary>A session at a running program's diagnostic port, once it is past its start where its runtime could hang
    /// (<paramref name="waiting"/> told how long that is). A program gone, with no port to reach, or whose runtime takes no
    /// session in time, is a <see cref="ProfilingException"/> beginning <paramref name="failed"/> (<see cref="SessionStarts"/>).</summary>
    public static async Task<LiveSession> AtPortAsync(int processId, EventPipeProvider[] providers, int circularBufferMB, string failed,
        Action<EventPipeEventSource> follow, Action<TimeSpan>? waiting, CancellationToken cancel)
    {
        EventPipeSession started = await SessionStarts.StartAsync(processId, providers, circularBufferMB, failed, waiting, cancel);
        return new LiveSession(started.EventStream, follow, async () =>
        {
            await EventPipeStops.StopAsync(started, TimeSpan.FromSeconds(5));
            started.Dispose();
        });
    }

    /// <summary>A session of a program in a container (<paramref name="place"/>) through Passenger's tools there, their
    /// <see cref="ContainerToolsWords.Trace"/> with <paramref name="kind"/> at the program's own diagnostic port, its stream
    /// on their output. Tools that cannot start or reach it are a <see cref="ProfilingException"/> with their last word,
    /// else that they could not <paramref name="to"/> or did not begin <paramref name="doing"/>.</summary>
    public static async Task<LiveSession> InContainerAsync(int processId, ProfilePlace place, string kind, string to, string doing,
        Action<EventPipeEventSource> follow, CancellationToken cancel)
    {
        ContainerTrace recording = await ContainerTrace.StartAsync(place, processId, resume: false, kind, to, doing, cancel);
        Process tools = recording.Tools;
        return new LiveSession(ChildProcesses.OutputStream(tools), follow, async () =>
        {
            // Their input's end stops the session there.
            await ChildProcesses.EndThroughInputAsync(tools, TimeSpan.FromSeconds(5));
            await Task.WhenAny(recording.Heard, Task.Delay(TimeSpan.FromSeconds(2)));
            tools.Dispose();
        });
    }

    /// <summary>Stops the session and lets go of the program, which runs on as it was.</summary>
    public async ValueTask DisposeAsync()
    {
        await stop();
        source.StopProcessing();
        await Ended.WaitAsync(TimeSpan.FromSeconds(5)).Ended();
        source.Dispose();
    }
}
