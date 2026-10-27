using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>What Passenger's container tools run inside a container for the profiler outside it, over docker exec's
/// standard input and output: a run's EventPipe stream (<see cref="TraceAsync"/>), the heap's snapshots
/// (<see cref="HeapAsync"/>) and the sources of methods from the modules' PDBs there (<see cref="Sources"/>). Each
/// reaches the program at the diagnostic port its runtime opens for itself, in the container's temporary
/// folder.</summary>
public static class InContainer
{
    /// <summary>The line <see cref="TraceAsync"/> writes on its error stream once the session has begun (and the program
    /// held at its start has been let go): from then on its output is the trace.</summary>
    public const string Recording = "recording";

    /// <summary>The command <see cref="HeapAsync"/> takes a snapshot for, one a line.</summary>
    public const string TakeSnapshot = "snapshot";

    /// <summary>The command <see cref="HeapAsync"/> takes a snapshot for even where only the runtime can write it, into
    /// the program's output (<see cref="DumpIntoOutputException"/>).</summary>
    public const string TakeSnapshotIntoOutput = "snapshot into-output";

    /// <summary>Records a program from now: its EventPipe session written to <paramref name="output"/> as it comes,
    /// until <paramref name="input"/> ends, when the session is stopped and its rundown flushed, or the program ends.
    /// With <paramref name="resume"/> a runtime held at its start is let go once the session has begun, and nothing it
    /// starts is held so; with <paramref name="requests"/> the session is <see cref="LiveRequests"/>'. Problems go to
    /// <paramref name="said"/>; the exit code is 0 once recorded.</summary>
    public static async Task<int> TraceAsync(int processId, bool resume, Stream input, Stream output, TextWriter said, bool requests = false)
    {
        EventPipeSession? session = null;
        try
        {
            if (!resume)
            {
                session = await StartAsync(new DiagnosticsClient(processId)).ConfigureAwait(false);
            }
            else
            {
                // A program just started held opens its port as its runtime starts.
                using Process process = Process.GetProcessById(processId);
                await HeldStart.AtPortAsync(process, async client => session = await StartAsync(client).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is DiagnosticsClientException or IOException or UnauthorizedAccessException or SocketException or TimeoutException
            or PlatformNotSupportedException or ArgumentException or InvalidOperationException)
        {
            await said.WriteLineAsync(Unreachable(processId, error)).ConfigureAwait(false);
            return 1;
        }
        if (session is null)
        {
            await said.WriteLineAsync(Unreachable(processId, null)).ConfigureAwait(false);
            return 1;
        }
        using (session)
        {
            if (resume)
            {
                // Recorded either way: a runtime not held at its start may refuse what it never waits for, and runs.
                _ = await HeldStart.LetGoAsync(processId).ConfigureAwait(false);
            }
            await said.WriteLineAsync(Recording).ConfigureAwait(false);
            await said.FlushAsync().ConfigureAwait(false);
            Task copying = CopyAsync(session.EventStream, output);
            Task ended = EndOfAsync(input);
            if (await Task.WhenAny(copying, ended).ConfigureAwait(false) == ended)
            {
                using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
                try
                {
                    await session.StopAsync(limit.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is ServerNotAvailableException or IOException or TimeoutException or OperationCanceledException)
                {
                    // The program ended as the run did; its stream is closed anyway.
                }
                if (await Task.WhenAny(copying, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false) != copying)
                {
                    await said.WriteLineAsync("The program's runtime did not finish the run's rundown in 60 s: the trace may name fewer methods").ConfigureAwait(false);
                }
            }
            else
            {
                await copying.ConfigureAwait(false);
            }
        }
        return 0;

        // A web app's requests need no rundown: nothing in them is named by address.
        Task<EventPipeSession> StartAsync(DiagnosticsClient client) => requests
            ? client.StartEventPipeSessionAsync(LiveRequests.Providers, requestRundown: false, circularBufferMB: 64)
            : client.StartEventPipeSessionAsync(ProfilingSession.Providers, requestRundown: true, circularBufferMB: 512);
    }

    /// <summary>Why a program's runtime could not be reached for a session, in words for the person: no error when its port
    /// never opened.</summary>
    private static string Unreachable(int processId, Exception? error) =>
        error is null or ServerNotAvailableException or ArgumentException
            ? $"Process {processId} has no diagnostic port to reach in this container: it has ended, is no .NET program, or its runtime was started with DOTNET_EnableDiagnostics=0"
            : $"The profiler could not reach process {processId}: {error.Message}";

    /// <summary>The session's stream to the output until either ends: the output gone (the IDE's docker CLI ended) ends it too.</summary>
    private static async Task CopyAsync(Stream events, Stream output)
    {
        try
        {
            await events.CopyToAsync(output).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The program ended mid-write, or no one reads any more: what was written is the run.
        }
    }

    /// <summary>Done when the input ends: the IDE's asking for the run to end.</summary>
    private static async Task EndOfAsync(Stream input)
    {
        byte[] buffer = new byte[256];
        try
        {
            while (await input.ReadAsync(buffer).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Takes a snapshot of the program's heap for each <see cref="TakeSnapshot"/> or
    /// <see cref="TakeSnapshotIntoOutput"/> line on <paramref name="input"/> and writes it to <paramref name="output"/>
    /// (<see cref="HeapWire"/>), its objects followed from one snapshot to the next through the collections between
    /// them; one that fails is written as why. Ends with the input. Exit code 2 for a line it does not know.</summary>
    public static async Task<int> HeapAsync(int processId, Stream input, Stream output, TextWriter said)
    {
        await using HeapHistory history = new(processId);
        using StreamReader commands = new(input);
        while (await commands.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            bool intoOutput = line.Trim() == TakeSnapshotIntoOutput;
            if (!intoOutput && line.Trim() != TakeSnapshot)
            {
                await said.WriteLineAsync($"heap takes one command a line, {TakeSnapshot} or {TakeSnapshotIntoOutput}, not '{line}'").ConfigureAwait(false);
                return 2;
            }
            try
            {
                MemorySnapshot taken = await history.TakeAsync(intoOutput, CancellationToken.None).ConfigureAwait(false);
                HeapWire.WriteSnapshot(output, taken);
            }
            catch (DumpIntoOutputException only)
            {
                HeapWire.WriteIntoOutput(output, only.Message);
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or ServerNotAvailableException or DiagnosticsClientException
                or TimeoutException or HeapUnreadableException or UnauthorizedAccessException or SocketException)
            {
                HeapWire.WriteFailure(output, error is ServerNotAvailableException ? Unreachable(processId, error) : error.Message);
            }
            await output.FlushAsync().ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>For each line of <paramref name="input"/> naming a method by its token and module's path
    /// (<c>TOKEN\tPATH</c>), a line of its source file and line from the module's PDB here (<c>FILE\tLINE</c>), or an empty
    /// line where it has none; exit code 2 for a line not so written.</summary>
    public static int Sources(TextReader input, TextWriter output, TextWriter said)
    {
        using MethodSources sources = new();
        while (input.ReadLine() is { } line)
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int token))
            {
                said.WriteLine($"sources takes a method a line, its token and its module's path apart by a tab, not '{line}'");
                return 2;
            }
            output.WriteLine(sources.Find(new MethodCode(parts[1], token)) is { } at ? $"{at.File}\t{at.Line.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
        }
        output.Flush();
        return 0;
    }
}
