using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>A run's memory snapshots of a program in a container, taken there by Passenger's tools (<see cref="ContainerToolsWords.Heap"/>,
/// started with the first snapshot and kept for the run), since only there does the container's own DAC load; each
/// comes back whole over docker exec (<see cref="HeapWire"/>). One at a time; its end ends the tools there.</summary>
internal sealed class ContainerHeap(ProfilePlace place, int processId) : IAsyncDisposable
{
    private readonly SemaphoreSlim taking = new(1, 1);

    /// <summary>The run's snapshots that keep their objects here, the tools there following those of the newest as many.</summary>
    private readonly KeptSnapshots kept = new();

    /// <summary>The last line the tools wrote to their errors: all that is kept of them, whatever they write.</summary>
    private string? lastError;
    private Process? tools;
    private Task errorsRead = Task.CompletedTask;
    private int disposed;

    /// <summary>The run's last snapshot's number, which the tools started again number theirs after.</summary>
    private int last;

    /// <summary>The snapshots before the tools running now, theirs numbered from 1 again.</summary>
    private int before;

    /// <summary>How long the tools may take over a snapshot (the heap dumped, read with the DAC and sent back) before they are
    /// ended: 10 minutes, generous for a heap of millions of objects, which take a minute or two.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    /// <summary>Takes the next snapshot, into the program's output only <paramref name="intoOutput"/>
    /// (<see cref="HeapHistory.TakeAsync"/>). Its answer is read whole or not at all: a frame left half read would be
    /// taken for the next one's, so one given up (<paramref name="cancel"/>) or past <see cref="Limit"/> ends the tools,
    /// which the next snapshot starts again, and fails with a <see cref="ProfilingException"/> saying why.</summary>
    public async Task<MemorySnapshot> TakeAsync(bool intoOutput, CancellationToken cancel)
    {
        await taking.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            Process running = Started();
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                await running.StandardInput.WriteLineAsync(intoOutput ? InContainer.TakeSnapshotIntoOutput : InContainer.TakeSnapshot).ConfigureAwait(false);
                await running.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception gone) when (gone is IOException or ObjectDisposedException)
            {
                throw new InvalidOperationException(await GoneAsync().ConfigureAwait(false), gone);
            }
            (byte Kind, byte[] Body)? frame;
            Task<(byte Kind, byte[] Body)?> reading = HeapWire.ReadFrameAsync(running.StandardOutput.BaseStream);
            try
            {
                frame = await reading.WaitAsync(Limit, cancel).ConfigureAwait(false);
            }
            catch (Exception stopped) when (stopped is TimeoutException or OperationCanceledException)
            {
                // Its answer is never read now: the tools go, which ends the read, and the next snapshot starts them again.
                End(running);
                UnawaitedWork.Dismiss(reading);
                throw new ProfilingException(stopped is TimeoutException
                    ? $"Passenger's tools in {place.Where} took no memory snapshot in {Limit.TotalMinutes:0} minutes, and were ended"
                    : $"The memory snapshot of the program in {place.Where} was stopped, and Passenger's tools there ended", stopped);
            }
            catch (InvalidDataException unreadable)
            {
                // What comes after cannot be told apart from it: the tools go, and the next snapshot starts them again.
                End(running);
                throw new InvalidOperationException(unreadable.Message, unreadable);
            }
            if (frame is not { } answer)
            {
                throw new InvalidOperationException(await GoneAsync().ConfigureAwait(false));
            }
            if (answer.Kind == HeapWire.IntoOutput)
            {
                throw new DumpIntoOutputException(Encoding.UTF8.GetString(answer.Body));
            }
            if (answer.Kind == HeapWire.Failure)
            {
                throw new InvalidOperationException(ErrorLines.Last(Encoding.UTF8.GetString(answer.Body)) ?? $"Passenger's tools in {place.Where} took no snapshot");
            }
            TimeSpan asked = clock.Elapsed;
            int numberedAfter = before;
            MemorySnapshot taken;
            try
            {
                // Off the caller's thread and back on none of its, held by whoever asked: a heap of millions of objects takes a while.
                taken = await BackgroundWork.Unheld(() => HeapWire.ReadSnapshot(answer.Body, kept, asked, numberedAfter), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception unreadable) when (unreadable is InvalidDataException or IOException or FormatException or OverflowException or ArgumentException
                or IndexOutOfRangeException or OutOfMemoryException)
            {
                // A container's answer that does not hold together: nothing after it is trusted either.
                End(running);
                throw new InvalidOperationException($"The snapshot Passenger's tools in {place.Where} sent could not be read: {unreadable.Message}", unreadable);
            }
            last = taken.Number;
            kept.Took(taken);
            return taken;
        }
        finally
        {
            taking.Release();
        }
    }

    /// <summary>The tools' heap mode in the container, started once.</summary>
    private Process Started()
    {
        if (tools is { HasExited: false } running)
        {
            return running;
        }
        tools?.Dispose();
        // A snapshot of the tools started again follows none of those before: numbered after them, compared with none.
        before = last;
        Volatile.Write(ref lastError, null);
        ProcessStartInfo start = place.Tools([ContainerToolsWords.Heap, ContainerToolsWords.Pid, processId.ToString(CultureInfo.InvariantCulture)]);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        try
        {
            tools = ChildProcesses.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            throw new InvalidOperationException($"Could not start Passenger's tools in {place.Where} for a memory snapshot: {error.Message}", error);
        }
        Process started = tools;
        errorsRead = ErrorLines.Hear(started, line =>
        {
            if (line.Trim() is { Length: > 0 } said)
            {
                Volatile.Write(ref lastError, said);
            }
        });
        return started;
    }

    /// <summary>Why the tools gave no snapshot: the last thing they said as they ended, else that they ended.</summary>
    private async Task<string> GoneAsync()
    {
        await Task.WhenAny(errorsRead, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        return Volatile.Read(ref lastError) ?? $"Passenger's tools in {place.Where} ended before the snapshot was taken";
    }

    /// <summary>Ends the tools whose answer could not be trusted, so the next snapshot starts them again, never asks them.</summary>
    private void End(Process running)
    {
        try
        {
            running.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        ChildProcesses.Kill(running);
        if (tools == running)
        {
            tools = null;
            running.Dispose();
        }
    }

    /// <summary>Ends the tools there: their input closed, which ends their wait for the next snapshot, then, should they
    /// still be at one, the CLI ended, which closes it there too.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || tools is not { } running)
        {
            return;
        }
        ChildProcesses.Ending(running);
        try
        {
            running.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        try
        {
            await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ChildProcesses.Kill(running);
        }
        running.Dispose();
    }
}

/// <summary>What Passenger's tools in a container write to their errors, heard a line at a time as it comes, each line cut
/// at <see cref="Longest"/> characters: a container writes there what it likes, and the IDE keeps a line of it at most.</summary>
internal static class ErrorLines
{
    public const int Longest = 2048;

    /// <summary>Hears the tools' errors to their end, each line given to <paramref name="line"/>, its end taken off.</summary>
    public static Task Hear(Process tools, Action<string> line) => ChildProcesses.Hear(tools, errors: line, longest: Longest);

    /// <summary>The last line of a text that says something, cut at <see cref="Longest"/>; null for none.</summary>
    public static string? Last(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) is { } last ? last.Length > Longest ? last[..Longest] : last : null;
}
