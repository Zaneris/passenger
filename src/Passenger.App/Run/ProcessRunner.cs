using System.Diagnostics;
using System.Text;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Run;

/// <summary>A child process whose output and errors stream into a console as they arrive, through its sink: read off the
/// UI thread, colour codes taken out, standard error in the error colour. A program run (not a build) reads what the
/// person types into the console; a build's input is closed, so a step that reads ends rather than waits. Stopping kills
/// the whole process tree. The console is the window's; a runner lets go of it when disposed, its folder left, and
/// another folder's runner writes there.</summary>
public sealed class ProcessRunner : IDisposable, IRunningProgram
{
    private readonly StringBuilder partialLine = new();
    private readonly OutputView console;
    private readonly bool takesInput;
    private Process? process;
    private bool running;
    private Task draining = Task.CompletedTask;
    private Task reading = Task.CompletedTask;
    private Task? deferredStop;

    /// <summary>The wait of a run whose process has yet to start, which Stop ends.</summary>
    private CancellationTokenSource? waiting;

    /// <summary>A start forking off the window's thread (<see cref="StartAsync(ProcessStartInfo, IReadOnlyDictionary{string, string}?)"/>):
    /// a Stop meanwhile ends the process as it comes.</summary>
    private bool forking;
    private TaskCompletionSource<int> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProcessRunner(OutputView console, bool takesInput = false)
    {
        this.console = console;
        this.takesInput = takesInput;
        console.Written += OnWritten;
    }

    private void OnWritten(object? sender, string text)
    {
        if (running && Line is not null)
        {
            Lines(text);
        }
    }

    /// <summary>Lets go of the console, its process stopped first: nothing more it reads is taken as its lines.</summary>
    public void Dispose() => console.Written -= OnWritten;

    /// <summary>A whole line of output, for whoever reads it (the build's error list, dotnet watch's words of a save); UI
    /// thread.</summary>
    public event EventHandler<string>? Line;

    /// <summary>The process ended, with its exit code; UI thread.</summary>
    public event EventHandler<int>? Exited;

    /// <summary>Program output before console formatting, delivered on the UI thread.</summary>
    public event EventHandler<ProcessOutput>? OutputReceived;

    public TextDocument Output => console.Document;

    /// <summary>What the console shows of the process now, what is on its way to it taken in first: what an agent reads of
    /// a run, rather than the window's view of it.</summary>
    public TextSnapshot Shown()
    {
        console.Flush();
        return console.Document.Snapshot;
    }

    /// <summary>From the start until <see cref="Exited"/> is raised, so nothing starts again before whoever waits hears the end.</summary>
    public bool IsRunning => running;

    /// <summary>Whether the last run ended because Stop was pressed.</summary>
    public bool WasStopped { get; private set; }

    /// <summary>Why the last run's process could not start once its wait was over; null for one that started.</summary>
    public string? Unstarted { get; private set; }

    /// <summary>The process last started (dotnet run, dotnet watch, a build), for a test to see it gone.</summary>
    public int? ProcessId => process?.Id;

    /// <summary>The exit code of the process last started, once <see cref="Exited"/> has said it ended: its own end,
    /// whatever starts after it.</summary>
    public Task<int> Ended => ended.Task;

    /// <summary>Starts the .NET command line; why it could not, or null once it runs. Given a wait, it runs from now as its
    /// callers see it (<see cref="IsRunning"/>, <see cref="Stop"/>, <see cref="Ended"/>) and its process starts once the
    /// wait is over; stopped meanwhile, or unable to start then (<see cref="Unstarted"/>), it ends with exit code -1.</summary>
    public string? StartDotnet(IReadOnlyList<string> arguments, string folder, IReadOnlyDictionary<string, string>? environment = null,
        Func<CancellationToken, Task>? after = null)
    {
        ProcessStartInfo start;
        try
        {
            start = ChildProcesses.DotnetCommand(arguments, folder);
        }
        catch (FileNotFoundException missing)
        {
            console.Append($"{missing.Message}\n", error: true);
            return missing.Message;
        }
        return Start(start, environment, after);
    }

    /// <summary><see cref="StartDotnet"/> with no wait, its fork off the window's thread as <see cref="StartAsync(ProcessStartInfo,
    /// IReadOnlyDictionary{string, string}?)"/>'s is.</summary>
    public Task<string?> StartDotnetAsync(IReadOnlyList<string> arguments, string folder, IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessStartInfo start;
        try
        {
            start = ChildProcesses.DotnetCommand(arguments, folder);
        }
        catch (FileNotFoundException missing)
        {
            console.Append($"{missing.Message}\n", error: true);
            return Task.FromResult<string?>(missing.Message);
        }
        return StartAsync(start, environment);
    }

    /// <summary>Starts a program; why it could not (a working folder gone, a missing program), or null once it runs.</summary>
    public string? Start(string file, IReadOnlyList<string> arguments, string folder, IReadOnlyDictionary<string, string>? environment = null) =>
        Start(ChildProcesses.Command(file, arguments, folder), environment);

    public string? Start(ProcessStartInfo start, IReadOnlyDictionary<string, string>? environment = null, Func<CancellationToken, Task>? after = null)
    {
        Prepare(start, environment);
        if (after is null)
        {
            return Launch(start, null);
        }
        running = true;
        IdeErrors.Observe(LaunchAfterAsync(start, after, ended = new(TaskCreationOptions.RunContinuationsAsynchronously)));
        return null;
    }

    /// <summary>Starts a program as <see cref="Start(string, IReadOnlyList{string}, string, IReadOnlyDictionary{string, string}?)"/>
    /// does, its fork off the window's thread, which costs a large process tens of milliseconds (#233): it runs from now as
    /// its callers see it (<see cref="IsRunning"/>, <see cref="Stop"/>, <see cref="Ended"/>), its id known once the task is
    /// done; why it could not start, or null.</summary>
    public Task<string?> StartAsync(string file, IReadOnlyList<string> arguments, string folder, IReadOnlyDictionary<string, string>? environment = null) =>
        StartAsync(ChildProcesses.Command(file, arguments, folder), environment);

    /// <inheritdoc cref="StartAsync(string, IReadOnlyList{string}, string, IReadOnlyDictionary{string, string}?)"/>
    public async Task<string?> StartAsync(ProcessStartInfo start, IReadOnlyDictionary<string, string>? environment = null)
    {
        Prepare(start, environment);
        running = true;
        TaskCompletionSource<int> ending = ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if ((Unstarted = await LaunchAsync(start, ending)) is { } refused)
        {
            running = false;
            ending.TrySetResult(-1);
        }
        return Unstarted;
    }

    /// <summary>A start's program made ready to run in this console: its output read, its input the console's when it takes
    /// one, the environment given put in.</summary>
    private void Prepare(ProcessStartInfo start, IReadOnlyDictionary<string, string>? environment)
    {
        UiThread.Check();
        ObjectDisposedException.ThrowIf(deferredStop is not null, this);
        if (IsRunning)
        {
            throw new InvalidOperationException("The process is still running.");
        }
        WasStopped = false;
        Unstarted = null;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        // Otherwise ChildProcesses closes it as the program starts.
        start.RedirectStandardInput = takesInput;
        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }
    }

    /// <summary>The process started once its wait is over, unless it was stopped meanwhile.</summary>
    private async Task LaunchAfterAsync(ProcessStartInfo start, Func<CancellationToken, Task> after, TaskCompletionSource<int> ending)
    {
        using (CancellationTokenSource stopping = waiting = new())
        {
            try
            {
                await after(stopping.Token);
            }
            catch (OperationCanceledException)
            {
                WasStopped = true;
            }
            catch (Exception error)
            {
                // What it waited for is a courtesy to others: the process starts anyway.
                IdeErrors.Report(error);
            }
            finally
            {
                waiting = null;
            }
        }
        if (WasStopped || (Unstarted = await LaunchAsync(start, ending)) is not null)
        {
            running = false;
            ending.TrySetResult(-1);
            Exited?.Invoke(this, -1);
        }
    }

    private string? Launch(ProcessStartInfo start, TaskCompletionSource<int>? waited)
    {
        console.Append($"{ChildProcesses.Shown(start)}\n");
        Process started;
        try
        {
            started = ChildProcesses.Start(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Refused(start, error);
        }
        Run(started, waited);
        return null;
    }

    /// <summary><see cref="Launch"/> with the fork off the window's thread, held as background work, so what waits for the
    /// window to be quiet finds the program started; one stopped meanwhile is ended as it comes.</summary>
    private async Task<string?> LaunchAsync(ProcessStartInfo start, TaskCompletionSource<int> waited)
    {
        console.Append($"{ChildProcesses.Shown(start)}\n");
        Process started;
        forking = true;
        BackgroundWork.Begin();
        try
        {
            started = await ChildProcesses.StartAsync(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Refused(start, error);
        }
        finally
        {
            forking = false;
            BackgroundWork.End();
        }
        Run(started, waited);
        if (WasStopped)
        {
            ChildProcesses.Kill(started);
        }
        return null;
    }

    /// <summary>Why a program could not start, said in the console.</summary>
    private string Refused(ProcessStartInfo start, Exception error)
    {
        string file = Path.GetFileName(start.FileName);
        string problem = Directory.Exists(start.WorkingDirectory) ? $"Could not start {file}: {error.Message}" : $"Could not start {file}: the folder {start.WorkingDirectory} is gone";
        console.Append($"{problem}\n", error: true);
        return problem;
    }

    /// <summary>A program started in this console: its output read into it, its input the console's, its end landed.</summary>
    private void Run(Process started, TaskCompletionSource<int>? waited)
    {
        process = started;
        running = true;
        TaskCompletionSource<int> ending = waited ?? (ended = new(TaskCreationOptions.RunContinuationsAsynchronously));
        ConsoleInput? input = takesInput ? ProgramInput.Of(started.StandardInput) : null;
        if (input is not null)
        {
            console.Input = input;
        }
        ConsoleSink sink = console.Sink;
        Landing landing = new();
        started.Exited += (_, _) => landing.Ended();
        if (started.HasExited)
        {
            landing.Ended();
        }
        CancellationTokenSource letGo = new();
        Task outputs = reading = Task.WhenAll(Pump(started.StandardOutput, sink, ConsoleStream.Output, letGo.Token), Pump(started.StandardError, sink, ConsoleStream.Error, letGo.Token));
        draining = LandAsync(started, outputs, letGo, input, ending, landing);
    }

    /// <summary>Says in the console that the program ended, once it has exited and the console has what it wrote. A job it
    /// left holding its output (a helper it started) is ended with its group after ChildProcesses' grace, so an ended
    /// program never runs on in the console; one out of the kill's reach is let go, what it writes dropped.</summary>
    private async Task LandAsync(Process started, Task outputs, CancellationTokenSource letGo, ConsoleInput? input, TaskCompletionSource<int> ending, Landing landing)
    {
        // Off the UI thread from here on, whose dispatcher may end first: a program still running holds nothing.
        await started.WaitForExitAsync().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        // However its reads end, a failed one included, they are over.
        Task read = outputs.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (!await ChildProcesses.OutputEndsAsync(started, read).ConfigureAwait(false))
        {
            letGo.Cancel();
        }
        int code = started.ExitCode;
        if (!ReferenceEquals(Volatile.Read(ref process), started))
        {
            ending.TrySetResult(code);
            landing.Landed();
            return;
        }
        UiThread.Post(() =>
        {
            try
            {
                if (!ReferenceEquals(process, started))
                {
                    ending.TrySetResult(code);
                    return;
                }
                // Typing goes on to whatever has had the console since.
                if (console.Input == input)
                {
                    console.Input = null;
                }
                console.Flush();
                if (partialLine.Length > 0)
                {
                    Line?.Invoke(this, partialLine.ToString());
                    partialLine.Clear();
                }
                console.Append($"\nProcess finished with exit code {code}.\n");
                running = false;
                ending.TrySetResult(code);
                Exited?.Invoke(this, code);
            }
            finally
            {
                landing.Landed();
            }
        }, landing.Landed);
    }

    /// <summary>A program's end until the console has all it wrote and says it ended: background work, so a screenshot or
    /// a test finds the last of its output and its exit code there. A program still running holds nothing, since it may
    /// never end; what it writes meanwhile is the sink's to hold as it crosses.</summary>
    private sealed class Landing
    {
        private readonly object gate = new();
        private IDisposable? held;
        private bool landed;

        /// <summary>The program has ended (its process's Exited, on any thread): held until <see cref="Landed"/>.</summary>
        public void Ended()
        {
            lock (gate)
            {
                if (!landed && held is null)
                {
                    held = BackgroundWork.Scope();
                }
            }
        }

        /// <summary>The console says it ended, or never will, its dispatcher gone (a test's, once over); an end heard only
        /// after this holds nothing.</summary>
        public void Landed()
        {
            lock (gate)
            {
                landed = true;
                held?.Dispose();
                held = null;
            }
        }
    }

    /// <summary>Ends the process and all it started, and on macOS and Linux what it left holding its output after it ended,
    /// which the console would otherwise end only after its grace.</summary>
    public void Stop()
    {
        UiThread.Check();
        if (running && waiting is { } wait)
        {
            WasStopped = true;
            wait.Cancel();
        }
        else if (running && forking)
        {
            WasStopped = true;
        }
        else if (running && process is { } started)
        {
            WasStopped = true;
            ChildProcesses.Kill(started);
        }
    }

    /// <summary>Transfers this closing runner's process to background cleanup after its owner's prerequisite finishes.
    /// The runner cannot start again. Completion includes output drainage, without waiting for UI landing.</summary>
    public Task DeferStopAfter(Task prerequisite)
    {
        UiThread.Check();
        if (deferredStop is not null)
        {
            return deferredStop;
        }
        if (waiting is not null || forking)
        {
            Stop();
            return deferredStop = Task.CompletedTask;
        }
        if (!running || process is not { } owned)
        {
            return Task.CompletedTask;
        }
        WasStopped = true;
        running = false;
        process = null;
        console.Input = null;
        Task drained = draining;
        Task readers = reading;
        TaskCompletionSource<int> finished = ended;
        return deferredStop = Task.Run(async () =>
        {
            try
            {
                await prerequisite.ConfigureAwait(false);
            }
            finally
            {
                ChildProcesses.Kill(owned);
                try
                {
                    await drained.ConfigureAwait(false);
                    finished.TrySetResult(owned.ExitCode);
                }
                finally
                {
                    try
                    {
                        owned.StandardOutput.Dispose();
                        owned.StandardError.Dispose();
                        if (takesInput)
                        {
                            owned.StandardInput.Dispose();
                        }
                    }
                    finally
                    {
                        owned.Dispose();
                        try
                        {
                            await readers.ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
                        {
                        }
                    }
                }
            }
        });
    }

    /// <summary>Reads a stream into the sink as it comes, a read at a time, and ends the stream's state at its end; once let
    /// go of (its program ended, and what holds the stream is out of the kill's reach), what still comes is nobody's,
    /// since the console may be another program's by then.</summary>
    private Task Pump(StreamReader reader, ConsoleSink sink, ConsoleStream stream, CancellationToken letGo)
        => sink.PumpAsync(reader, stream, letGo, chunks: text =>
        {
            if (OutputReceived is null)
            {
                return;
            }
            ProcessOutput output = new(text, stream == ConsoleStream.Error);
            UiThread.Post(() => OutputReceived?.Invoke(this, output));
        });

    /// <summary>The longest line told whole; a longer one is told in pieces, so what a program writes with no line end
    /// is never held whole, nor copied again with each batch, while someone listens.</summary>
    private const int LongestLine = 16 * 1024;

    /// <summary>Tells each line a batch ends, at '\n' or a lone '\r' (the console has made "\r\n" one '\n').</summary>
    private void Lines(string text)
    {
        ReadOnlySpan<char> rest = text;
        while (rest.Length > 0)
        {
            int end = rest.IndexOfAny('\n', '\r');
            int taken = Math.Min(end < 0 ? rest.Length : end, LongestLine - partialLine.Length);
            partialLine.Append(rest[..taken]);
            if (taken == end || partialLine.Length == LongestLine)
            {
                Line?.Invoke(this, partialLine.ToString());
                partialLine.Clear();
            }
            rest = rest[(taken == end ? taken + 1 : taken)..];
        }
    }
}

public sealed record ProcessOutput(string Text, bool IsError);
