using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>A program to profile: what to start, where, with what arguments and environment.</summary>
public sealed record ProfileTarget(string Program, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>What the snapshot is named for, where the program is another's host (Godot, running a game's assembly);
    /// null for <see cref="Name"/>'s own.</summary>
    public string? Called { get; init; }

    /// <summary>Why the program starts on Windows on a desktop of its own, which nobody sees (<see cref="ChildProcesses.OnDesktopOfItsOwn"/>);
    /// null where it starts on the person's.</summary>
    public string? OwnDesktop { get; init; }

    /// <summary>The program's start through ChildProcesses, on a desktop of its own where <see cref="OwnDesktop"/> says so.</summary>
    internal ProcessStartInfo Start()
    {
        ProcessStartInfo start = ChildProcesses.Command(Program, Arguments, WorkingDirectory);
        return OwnDesktop is { } why ? ChildProcesses.OnDesktopOfItsOwn(start, why) : start;
    }

    /// <summary>What the snapshot is named for: <see cref="Called"/>, else the program, or the assembly the dotnet host runs.</summary>
    public string Name => Called ?? (Path.GetFileNameWithoutExtension(Program) is "dotnet" && Arguments.Count > 0 ? Path.GetFileNameWithoutExtension(Arguments[0]) : Path.GetFileNameWithoutExtension(Program));
}

/// <summary>How a program's standard output, or its standard error (<paramref name="error"/>), is read into its caller's
/// console as it comes, to its end (the console's own pump), each line read also told to <paramref name="lines"/> when
/// given; done once the stream has ended, on any thread.</summary>
public delegate Task ProgramPump(TextReader reader, bool error, Action<string>? lines);

/// <summary>A sampling profiler's run: the program is started held at a diagnostic port of its own
/// (DOTNET_DiagnosticPorts), so the run begins at its first instruction; the port is cleared in the program before it
/// goes on, so a .NET program it starts is not held too. Or a program already running is attached to at its own
/// diagnostic port, and goes on as it was after. Its EventPipe stream (the sampler, the runtime's method loads to name
/// the frames, and an Avalonia app's frames from Passenger's inspector in it) is written to a .nettrace file until
/// <see cref="StopAsync"/> or the program's end, then read into a <see cref="Snapshot"/>. A program elsewhere (a
/// container's, <see cref="Place"/>) is recorded there by Passenger's tools, the same session streamed out over their
/// standard output into the same file, and its memory snapshots are taken there too
/// (<see cref="ContainerHeap"/>).</summary>
public sealed class ProfilingSession : IAsyncDisposable
{
    /// <summary>The runtime's events dotnet-trace's cpu-sampling profile asks for: what names the frames (loader, JIT,
    /// rundown) and the GC.</summary>
    private const long RuntimeKeywords = 0x14C14FCCBD;

    /// <summary>The program's process, or, for one elsewhere, the tools recording it there (their docker exec).</summary>
    private readonly Process process;
    private readonly DiagnosticsClientConnector? connector;

    /// <summary>The session, null for a program elsewhere, whose session is the tools' there.</summary>
    private readonly EventPipeSession? session;
    private readonly Task copying;
    private readonly Task reading;
    private readonly string? port;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int stopped;
    private int letGo;
    private int disposed;
    private HeapHistory? heapHistory;
    private ContainerHeap? containerHeap;

    /// <summary>Whether the program ran before the profiler came, where that is not told by how it is reached (a game's frames
    /// alone, which no port reaches).</summary>
    private readonly bool? attached;

    private ProfilingSession(Process process, DiagnosticsClientConnector? connector, EventPipeSession? session, Task copying, Task reading, string trace, string? port,
        int? processId = null, ProfilePlace? place = null, bool samples = true, bool? attached = null)
    {
        Samples = samples;
        this.attached = attached;
        this.process = process;
        this.connector = connector;
        this.session = session;
        this.copying = copying;
        this.reading = reading;
        this.port = port;
        TracePath = trace;
        ProcessId = processId ?? process.Id;
        Place = place;
    }

    /// <summary>The program's process, known after the run is put away too: the place's own for one elsewhere.</summary>
    public int ProcessId { get; }

    /// <summary>Where the program runs when not on this machine (its container), or null.</summary>
    public ProfilePlace? Place { get; }

    /// <summary>Whether the program has ended, as far as the run can tell: one put away (<see cref="DisposeAsync"/>) is
    /// heard of no more. For a program elsewhere, whether its recording there has ended: with the program, its container,
    /// or the run.</summary>
    public bool HasExited => Volatile.Read(ref disposed) != 0 || !DotnetProcesses.Runs(process);

    /// <summary>Whether the program was running before the profiler came, and is not the profiler's to end or to type to.</summary>
    public bool Attached => attached ?? (connector is null && Samples);

    /// <summary>Whether the run samples the program's .NET code; false for a Godot game of GDScript alone, whose run is its
    /// frames as Godot's own profiler measures them (<see cref="Game"/>), its trace kept empty.</summary>
    public bool Samples { get; }

    /// <summary>Why a run of a game's frames alone has no heap.</summary>
    public const string NoHeapOfScripts = "A Godot game of GDScript alone runs no .NET code, so it has no .NET heap to snapshot";

    /// <summary>How long it has run.</summary>
    public TimeSpan Elapsed => clock.Elapsed;

    public string TracePath { get; }

    /// <summary>The program's standard input, kept open as a console's is for a program that waits on a read: what the
    /// person types goes here. None for a program attached to, whose input is another's.</summary>
    public StreamWriter? Input => Attached ? null : ChildProcesses.Input(process);

    /// <summary>What a run asks EventPipe for, here and in a container alike (<see cref="InContainer.TraceAsync"/>): the
    /// sampler, the runtime's events (verbose for the GC's allocation ticks, a type and a stack every 100 KB or so
    /// allocated), and the frames of an Avalonia app with Passenger's inspector in it.</summary>
    internal static EventPipeProvider[] Providers =>
    [
        new EventPipeProvider(Snapshot.SampleProvider, EventLevel.Informational),
        new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, RuntimeKeywords),
        new EventPipeProvider(UiFrames.Provider, EventLevel.Informational),
    ];

    /// <summary>Starts a program under the profiler. The trace goes into <paramref name="folder"/>, and off Windows the port's
    /// socket into <paramref name="sockets"/> (Passenger's socket folder, <see cref="SocketFolders"/>); the program's output
    /// and errors are read by <paramref name="pump"/>, its caller's console's, each to its end. A start that fails, for
    /// whatever reason, is a <see cref="ProfilingException"/> saying why, with the program ended and the port's listener
    /// gone, its socket too.</summary>
    public static async Task<ProfilingSession> StartAsync(ProfileTarget target, string folder, string sockets, ProgramPump pump,
        CancellationToken cancellation = default)
    {
        if (!Directory.Exists(target.WorkingDirectory))
        {
            throw new ProfilingException($"Could not profile {target.Name}: its working folder {target.WorkingDirectory} does not exist");
        }
        // One named by its path must be there; a name alone is looked for on the PATH by the start.
        if (Path.IsPathRooted(target.Program) && !File.Exists(target.Program))
        {
            throw new ProfilingException($"Could not profile {target.Name}: {target.Program} does not exist");
        }
        string trace;
        try
        {
            trace = NewTrace(folder, target.Name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProfilingException($"Could not profile {target.Name}: no snapshot can be written in {folder}: {error.Message}", error);
        }
        // Named for this process first, so a later Passenger takes away one a crash left; never .sock, which agents look for.
        string port = OperatingSystem.IsWindows()
            ? $"passenger-profile-{Environment.ProcessId}-{Guid.NewGuid():N}"
            : Path.Combine(sockets, $"{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}.profiler");
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        Task<DiagnosticsClientConnector?> connecting = DiagnosticsClientConnector.FromDiagnosticPort(port, limit.Token)!;

        ProcessStartInfo start = target.Start();
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        // An input of its own that stays open, for a program that waits on a read.
        start.RedirectStandardInput = true;
        foreach ((string key, string value) in target.Environment)
        {
            start.Environment[key] = value;
        }
        start.Environment[RuntimeVariables.DiagnosticPorts] = $"{port},suspend";
        Process process;
        try
        {
            process = ChildProcesses.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            await StopListeningAsync(connecting, limit, port);
            throw new ProfilingException($"Could not start {Path.GetFileName(target.Program)} to profile {target.Name}: {error.Message}", error);
        }
        // The last line it wrote on its standard error, which says why when it ends before the profiler sees it.
        string? lastSaid = null;
        Task reading = Task.WhenAll(pump(ChildProcesses.Output(process), false, null), pump(ChildProcesses.Errors(process), true, line =>
        {
            if (line.Trim() is { Length: > 0 } said)
            {
                lastSaid = said.Length > 2000 ? said[^2000..] : said;
            }
        }));

        DiagnosticsClientConnector? connector = null;
        try
        {
            // A program that ends first never connects: no use waiting out the limit for it.
            Task ended = process.WaitForExitAsync(CancellationToken.None);
            if (await Task.WhenAny(connecting, ended) == connecting || connecting.IsCompleted)
            {
                // Out of time, the connector hands back nothing rather than throw.
                connector = await connecting;
            }
        }
        catch (OperationCanceledException)
        {
        }
        // ArgumentException among them: a port's path past what a Unix socket's address holds (104 bytes on macOS).
        catch (Exception error) when (DiagnosticsFailures.Of(error))
        {
            await FailAsync();
            throw new ProfilingException($"The profiler could not listen for {target.Name}: {error.Message}", error);
        }
        if (connector is null)
        {
            string? said = await SaidLastAsync();
            string why = !DotnetProcesses.Runs(process)
                ? $"{target.Name} never reached the profiler: it ended first, with {ExitCodes.Said(process.ExitCode)}{(said is null ? ". Is it a .NET program?" : $": {said}")}"
                : $"{target.Name} never reached the profiler in 30 s: is it a .NET program?";
            await FailAsync();
            cancellation.ThrowIfCancellationRequested();
            throw new ProfilingException(why);
        }
        DiagnosticsClient client = connector.Instance;
        EventPipeSession? session = null;
        try
        {
            // The port is the program's alone; what it starts runs freely.
            client.SetEnvironmentVariable(RuntimeVariables.DiagnosticPorts, string.Empty);
            session = await client.StartEventPipeSessionAsync(Providers, requestRundown: true, circularBufferMB: 512, cancellation);
            Task copying = Copy(session, trace);
            client.ResumeRuntime();
            return new ProfilingSession(process, connector, session, copying, reading, trace, port);
        }
        catch (Exception error) when (DiagnosticsFailures.Of(error))
        {
            string? said = await SaidLastAsync();
            string why = !DotnetProcesses.Runs(process)
                ? $"{target.Name} ended as the profiler began, with {ExitCodes.Said(process.ExitCode)}{(said is null ? string.Empty : $": {said}")}"
                : $"The profiler could not begin recording {target.Name}: {error.Message}";
            session?.Dispose();
            await FailAsync();
            throw new ProfilingException(why, error);
        }
        catch
        {
            session?.Dispose();
            await FailAsync();
            throw;
        }

        // The last line it wrote on its standard error once it has ended, read to its end unless that takes a while.
        async Task<string?> SaidLastAsync()
        {
            if (!DotnetProcesses.Runs(process))
            {
                await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(2)));
            }
            return lastSaid;
        }

        // The program ended, and nothing of the run left: no connection, no listener, no socket.
        async Task FailAsync()
        {
            ChildProcesses.Kill(process);
            await StopListeningAsync(connecting, limit, port);
            process.Dispose();
        }
    }

    /// <summary>Starts a Godot game of GDScript alone under Godot's own profiler alone (<see cref="Samples"/> false): no .NET code
    /// runs in it to sample, so no diagnostic port holds it; the run is the frames <see cref="Game"/> records, kept beside an
    /// empty trace in <paramref name="folder"/> that names the run. Its output and errors are read by <paramref name="pump"/>; a
    /// start that fails is a <see cref="ProfilingException"/> saying why.</summary>
    public static async Task<ProfilingSession> StartFramesAsync(ProfileTarget target, string folder, ProgramPump pump)
    {
        if (!Directory.Exists(target.WorkingDirectory))
        {
            throw new ProfilingException($"Could not profile {target.Name}: its working folder {target.WorkingDirectory} does not exist");
        }
        string trace;
        try
        {
            trace = NewTrace(folder, target.Name);
            new FileStream(trace, FileMode.CreateNew, FileAccess.Write).Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProfilingException($"Could not profile {target.Name}: no snapshot can be written in {folder}: {error.Message}", error);
        }
        ProcessStartInfo start = target.Start();
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        foreach ((string key, string value) in target.Environment)
        {
            start.Environment[key] = value;
        }
        Process process;
        try
        {
            process = await ChildProcesses.StartAsync(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            throw new ProfilingException($"Could not start {Path.GetFileName(target.Program)} to profile {target.Name}: {error.Message}", error);
        }
        Task reading = Task.WhenAll(pump(ChildProcesses.Output(process), false, null), pump(ChildProcesses.Errors(process), true, null));
        return new ProfilingSession(process, null, null, Task.CompletedTask, reading, trace, null, samples: false);
    }

    /// <summary>Profiles a Godot game of GDScript alone already running under Godot's own profiler alone (<see cref="Samples"/>
    /// false), from now until <see cref="StopAsync"/> or its end, leaving it as it was: no .NET code runs in it to sample, so
    /// no diagnostic port is asked of it; the run is the frames <see cref="Game"/> records, beside an empty trace in
    /// <paramref name="folder"/> that names the run. A game gone, or a snapshot that cannot be written, is a
    /// <see cref="ProfilingException"/> saying why.</summary>
    public static ProfilingSession AttachFrames(int processId, string name, string folder)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new ProfilingException($"Could not profile {name}: it has ended", error);
        }
        try
        {
            string trace = NewTrace(folder, name);
            new FileStream(trace, FileMode.CreateNew, FileAccess.Write).Dispose();
            return new ProfilingSession(process, null, null, Task.CompletedTask, Task.CompletedTask, trace, null, samples: false, attached: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            process.Dispose();
            throw new ProfilingException($"Could not profile {name}: no snapshot can be written in {folder}: {error.Message}", error);
        }
    }

    /// <summary>Profiles a .NET program already running, from now until <see cref="StopAsync"/> or its end, at the
    /// diagnostic port its runtime opens for itself, leaving it as it was. With <paramref name="resume"/> a program
    /// held at its start (DOTNET_DefaultDiagnosticPortSuspend=1, its port waited for as its runtime opens it) is let go
    /// once recording. A program gone, with no port to reach, or whose snapshot cannot be written, is a
    /// <see cref="ProfilingException"/> saying why, the program left recording nothing.</summary>
    public static async Task<ProfilingSession> AttachAsync(int processId, string name, string folder, CancellationToken cancellation = default, bool resume = false)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new ProfilingException($"Could not profile {name}: it has ended", error);
        }
        string trace;
        try
        {
            trace = NewTrace(folder, name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            process.Dispose();
            throw new ProfilingException($"Could not profile {name}: no snapshot can be written in {folder}: {error.Message}", error);
        }
        // The session goes with any failure, or the program goes on recording for no one.
        EventPipeSession? session = null;
        try
        {
            if (!resume)
            {
                session = await new DiagnosticsClient(processId).StartEventPipeSessionAsync(Providers, requestRundown: true, circularBufferMB: 512, cancellation);
            }
            else
            {
                // A program just started held opens its port as its runtime starts.
                PortWait waited = await HeldStart.AtPortAsync(process, async client =>
                    session = await client.StartEventPipeSessionAsync(Providers, requestRundown: true, circularBufferMB: 512, cancellation), cancellation);
                if (session is null)
                {
                    throw new ProfilingException(waited == PortWait.Ended ? $"Could not profile {name}: it has ended"
                        : $"The profiler could not reach {name}: it opened no diagnostic port in {HeldStart.PortLimit.TotalSeconds:0} s");
                }
                // Recorded either way: a runtime not held at its start may refuse what it never waits for, and runs.
                _ = await HeldStart.LetGoAsync(processId);
            }
            return new ProfilingSession(process, null, session, Copy(session, trace), Task.CompletedTask, trace, null);
        }
        catch (Exception error) when (DiagnosticsFailures.Of(error))
        {
            bool gone = !DotnetProcesses.Runs(processId);
            bool reached = session is not null;
            session?.Dispose();
            process.Dispose();
            throw new ProfilingException(gone ? $"Could not profile {name}: it has ended"
                : reached ? $"Could not profile {name}: no snapshot can be written in {folder}: {error.Message}"
                : $"The profiler could not reach {name}: {error.Message}", error);
        }
        catch
        {
            session?.Dispose();
            process.Dispose();
            throw;
        }
    }

    /// <summary>Profiles a .NET program running elsewhere (in a container) from now, leaving it as it was: Passenger's
    /// tools there (<c>trace</c>) start the session at its own diagnostic port and stream it out, written to the trace
    /// here as it comes. With <paramref name="resume"/> a program held at its start is let go once recording. One the
    /// tools cannot reach, or whose snapshot cannot be written, is a <see cref="ProfilingException"/> saying why,
    /// nothing left recording it: its file is made before the tools start.</summary>
    public static async Task<ProfilingSession> AttachAsync(int processId, string name, string folder, ProfilePlace place, bool resume = false,
        CancellationToken cancellation = default)
    {
        string trace;
        FileStream file;
        try
        {
            trace = NewTrace(folder, name);
            file = new FileStream(trace, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProfilingException($"Could not profile {name}: no snapshot can be written in {folder}: {error.Message}", error);
        }
        ContainerTrace recording;
        try
        {
            recording = await ContainerTrace.StartAsync(place, processId, resume, kind: null, $"profile {name}", $"recording {name}", cancellation).ConfigureAwait(false);
        }
        catch
        {
            Unmade(file, trace);
            throw;
        }
        Process tools = recording.Tools;
        // Not held: copied for as long as the recording lasts.
        Task copying = BackgroundWork.Unheld(async () =>
        {
            await using (file)
            {
                try
                {
                    await tools.StandardOutput.BaseStream.CopyToAsync(file).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // The tools were ended mid-write: what was written is the run.
                }
            }
        }, CancellationToken.None);
        return new ProfilingSession(tools, null, null, copying, recording.Heard, trace, null, processId, place);
    }

    /// <summary>A trace's file let go and deleted, its recording never begun: no snapshot to list.</summary>
    private static void Unmade(FileStream file, string trace)
    {
        file.Dispose();
        try
        {
            File.Delete(trace);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The session's stream written to the trace as it comes, until the session or the program ends.</summary>
    private static Task Copy(EventPipeSession session, string trace)
    {
        FileStream file = new(trace, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        // Not held: written for as long as the session lasts.
        return BackgroundWork.Unheld(async () =>
        {
            await using (file)
            {
                try
                {
                    await session.EventStream.CopyToAsync(file);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // The program ended mid-write: what was written is the run.
                }
            }
        });
    }

    /// <summary>A file for a run's trace, named for its program and when it began, never one written before.</summary>
    private static string NewTrace(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string trace = Path.Combine(folder, $"{name}-{stamp}.nettrace");
        for (int again = 2; File.Exists(trace); again++)
        {
            trace = Path.Combine(folder, $"{name}-{stamp}-{again}.nettrace");
        }
        return trace;
    }

    /// <summary>Ends the wait for a program at the port: the listener goes, a connection that came all the same with it,
    /// and its socket file, which a listener ended mid-accept may leave.</summary>
    private static async Task StopListeningAsync(Task<DiagnosticsClientConnector?> connecting, CancellationTokenSource limit, string port)
    {
        await limit.CancelAsync();
        try
        {
            if (await connecting is { } late)
            {
                await late.DisposeAsync();
            }
        }
        catch (Exception error) when (error is OperationCanceledException || DiagnosticsFailures.Of(error))
        {
            // What failed was the listening itself: there is nothing more of it to end.
        }
        RemovePort(port);
    }

    private static void RemovePort(string? port)
    {
        if (port is null || OperatingSystem.IsWindows())
        {
            // A named pipe, which goes with its last handle.
            return;
        }
        try
        {
            File.Delete(port);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Ends the run and reads what it found. The program goes on, the person's to end (<see cref="Kill"/>), with
    /// nothing of the profiler's left listening for it.</summary>
    public async Task<Snapshot> StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0 && DotnetProcesses.Runs(process) && Samples)
        {
            if (session is null)
            {
                // The tools there stop their session as their input ends, its rundown flushed, and end.
                CloseToolsInput();
            }
            else
            {
                await EventPipeStops.StopAsync(session, TimeSpan.FromSeconds(10));
            }
        }
        try
        {
            await copying.WaitAsync(TimeSpan.FromSeconds(Place is null ? 30 : 90));
        }
        finally
        {
            await LetGoAsync();
        }
        return await BackgroundWork.Run(Read);
    }

    /// <summary>A Godot game's own frame profile, recorded beside the samples for as long as the run lasts: ended as the run
    /// is read, and kept beside its trace for the snapshot (<see cref="GameProfile"/>); null for any other program.</summary>
    public GameRecording? Game { get; set; }

    /// <summary>The run's trace read, knowing where it ran, with the game's frames recorded beside it.</summary>
    private Snapshot Read()
    {
        if (Game is { } game)
        {
            game.Stop();
            game.Write(TracePath);
        }
        Snapshot snapshot = Snapshot.Read(TracePath);
        snapshot.Place = Place;
        return snapshot;
    }

    /// <summary>Ends what the tools elsewhere read, which they take for the end of the run.</summary>
    private void CloseToolsInput()
    {
        ChildProcesses.Ending(process);
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception gone) when (gone is IOException or InvalidOperationException)
        {
        }
    }

    /// <summary>Waits for the program to end by itself, then reads the run.</summary>
    public async Task<Snapshot> WaitForExitAsync(CancellationToken cancellation = default)
    {
        await process.WaitForExitAsync(cancellation);
        Interlocked.Exchange(ref stopped, 1);
        await copying.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        return await BackgroundWork.Run(Read, cancellation);
    }

    /// <summary>Where a memory snapshot's dump lies while it is read, the user's own folder of them.</summary>
    public static string DumpFolder => DumpTool.Folder;

    /// <summary>Why no memory snapshot of the program can be taken whatever is tried: a run of scripts alone has no .NET heap;
    /// null where one may be. One whose memory no other program may read is read from its runtime's own heap dump.</summary>
    public string? WhyNoHeap => !Samples ? NoHeapOfScripts : null;

    /// <summary>What the program's heap holds now, comparable object by object with the run's earlier ones; the run
    /// goes on. Where only the program's runtime can write it, putting createdump's lines into the program's output, it
    /// is taken only <paramref name="intoOutput"/>, else a <see cref="DumpIntoOutputException"/> says so first.</summary>
    public Task<MemorySnapshot> TakeMemorySnapshotAsync(bool intoOutput = false, CancellationToken cancellation = default)
    {
        if (!DotnetProcesses.Runs(process))
        {
            throw new InvalidOperationException("The program has ended; a memory snapshot needs it running");
        }
        if (!Samples)
        {
            throw new InvalidOperationException(NoHeapOfScripts);
        }
        if (Place is { } place)
        {
            containerHeap ??= new ContainerHeap(place, ProcessId);
            return containerHeap.TakeAsync(intoOutput, cancellation);
        }
        heapHistory ??= new HeapHistory(process.Id);
        return heapHistory.TakeAsync(intoOutput, cancellation);
    }

    /// <summary>Finishes when the program has ended, by itself or killed, and what it wrote has been read: all of it, unless
    /// something it started holds its output on past a moment.</summary>
    public async Task ExitedAsync()
    {
        await process.WaitForExitAsync();
        await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(2)));
    }

    /// <summary>Ends the program, unless it was attached to: that one is another's to end.</summary>
    public void Kill()
    {
        if (!Attached && Volatile.Read(ref disposed) == 0)
        {
            ChildProcesses.Kill(process);
        }
    }

    /// <summary>Lets go of the run as it is, unread: what was written stays, and nothing of the profiler's is left
    /// listening for the program (its session, the port and the port's socket, the heap's history), which goes on or
    /// ends as it would. Once is enough; again does nothing.</summary>
    public async ValueTask LetGoAsync()
    {
        if (Interlocked.Exchange(ref letGo, 1) != 0)
        {
            return;
        }
        if (heapHistory is not null)
        {
            await heapHistory.DisposeAsync();
        }
        if (containerHeap is not null)
        {
            await containerHeap.DisposeAsync();
        }
        session?.Dispose();
        if (session is null && Samples && DotnetProcesses.Runs(process))
        {
            // The tools elsewhere stop as their input ends; a CLI that does not go is ended, which closes it there too.
            CloseToolsInput();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                ChildProcesses.Kill(process);
            }
        }
        if (connector is not null)
        {
            await connector.DisposeAsync();
            CloseLeftListener(connector);
        }
        RemovePort(port);
    }

    /// <summary>Closes the pipe instance the diagnostics library's Windows listener can leave listening once it is let
    /// go of, which would otherwise keep the program connected to the port for as long as it ran.</summary>
    private static void CloseLeftListener(DiagnosticsClientConnector connector)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        const System.Reflection.BindingFlags Own = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? server = typeof(DiagnosticsClientConnector).GetField("_server", Own)?.GetValue(connector);
        object? transport = server?.GetType().GetField("_transport", Own)?.GetValue(server);
        if (transport?.GetType().GetField("_stream", Own)?.GetValue(transport) is System.IO.Pipes.PipeStream left)
        {
            left.Dispose();
        }
    }

    /// <summary>Lets go of the run and of the program's process, whose end is never heard after (<see cref="ExitedAsync"/>):
    /// a program the profiler started is put away once it has ended.</summary>
    public async ValueTask DisposeAsync()
    {
        await LetGoAsync();
        Volatile.Write(ref disposed, 1);
        process.Dispose();
        putAway.TrySetResult();
    }

    private readonly TaskCompletionSource putAway = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Finishes once the run is put away (<see cref="DisposeAsync"/>), its program ended or going on unwatched,
    /// <see cref="HasExited"/> true from then on: what a wait for the program's end, beside <see cref="ExitedAsync"/>, ends at.</summary>
    public Task PutAway => putAway.Task;
}

/// <summary>Why a program could not be profiled, in words for the person: its folder or program missing, a start that
/// failed, or a program that never reached the profiler.</summary>
public sealed class ProfilingException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Whether the program runs but its runtime has yet to open its diagnostic port, so the same start may succeed a
    /// moment later (<see cref="SessionStarts.AskAgain"/>).</summary>
    public bool NotYetOpen { get; init; }
}
