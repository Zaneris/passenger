using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Passenger.Core;

/// <summary>Every program Passenger starts is started here, one way: no shell and no window, an input of its own (never
/// the IDE's), UTF-8 streams, none of a git hook's repository variables, and a line in the log when it fails or takes a
/// while, or for every one with the debug log on (<see cref="Started"/>, <see cref="Ended"/>): one the IDE ended itself
/// (<see cref="Kill"/>, <see cref="Ending"/>) did not fail, nor did a question whose exit code is its answer. None outlives
/// the IDE, however the IDE ends (<see cref="Lifeline"/>). A program run in the terminal is taken on (<see cref="Adopt"/>),
/// and one that must outlive the IDE starts apart (<see cref="StartApart"/>).</summary>
public static class ChildProcesses
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>What a git hook exports to point git at its own repository: a child (git, or tests run from a hook) would
    /// otherwise take it for its folder's.</summary>
    private static readonly string[] HookVariables =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_COMMON_DIR", "GIT_PREFIX", "GIT_NAMESPACE", "GIT_QUARANTINE_PATH",
    ];

    /// <summary>A child started; raised on the thread that started it, for the IDE's log.</summary>
    public static event EventHandler<ChildStart>? Started;

    /// <summary>A child ended; raised on any thread, for the IDE's log.</summary>
    public static event EventHandler<ChildEnd>? Ended;

    /// <summary>The children the IDE is ending, by process id, until their end is told.</summary>
    private static readonly ConcurrentDictionary<int, byte> EndedHere = new();

    /// <summary>How long a child that has exited has its output read before what it left holding it is ended: what is in the
    /// pipes is read at once, and only a job it left running, which holds them for as long as it lives, takes longer.</summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);

    /// <summary>How a command line is said wherever Passenger logs or shows one: <see cref="Display"/> until the IDE gives
    /// its mask for secrets as it starts, so no program's secret reaches the log or a window.</summary>
    public static Func<ProcessStartInfo, string> Shown { get; set; } = Display;

    /// <summary>A program and its arguments, to run in a folder (the IDE's own when none is given).</summary>
    public static ProcessStartInfo Command(string program, IEnumerable<string> arguments, string? folder = null)
    {
        ProcessStartInfo start = new(program, arguments);
        if (folder is not null)
        {
            start.WorkingDirectory = folder;
        }
        foreach (string variable in HookVariables)
        {
            start.Environment.Remove(variable);
        }
        return start;
    }

    /// <summary>The .NET command line (<see cref="Dotnet.Host"/>) with these arguments, in English (the IDE reads what it
    /// says) and with no first-run banner. It builds in its own process and worker nodes, never in MSBuild's server, which
    /// would outlive it; a node left for the next build is the IDE's alone, so it ends with the IDE
    /// (<see cref="Lifeline"/>). A <see cref="FileNotFoundException"/> when there is none.</summary>
    public static ProcessStartInfo DotnetCommand(IEnumerable<string> arguments, string? folder = null)
    {
        ProcessStartInfo start = Command(Dotnet.Host(), arguments, folder);
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["DOTNET_NOLOGO"] = "1";
        // MSBuild writes UTF-8, which is how its output is read, on every Windows: by itself it does only from 10 1909 on.
        start.Environment["DOTNET_CLI_FORCE_UTF8_ENCODING"] = "true";
        // The SDK's switch for MSBuild's server, and MSBuild's own, which a person's environment may set over the SDK's.
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["MSBUILDUSESERVER"] = "0";
        // No other build takes up a node of the IDE's, to have it ended under it when the IDE ends.
        start.Environment.TryGetValue("MSBUILDNODEHANDSHAKESALT", out string? salt);
        start.Environment["MSBUILDNODEHANDSHAKESALT"] = $"{salt}passenger-{Environment.ProcessId}";
        // Set by dotnet test for what it runs: each node would hold the command's output open as its own.
        start.Environment.Remove("MSBUILDENSURESTDOUTFORTASKPROCESSES");
        return start;
    }

    /// <summary>Starts a child that ends with the IDE. Its input is the caller's to write when it asked for it, and is
    /// otherwise closed at once, so the child reads nothing of the IDE's. The start is the child's from then on (another
    /// run needs another); what <see cref="Process.Start()"/> throws when it cannot start, this throws.</summary>
    public static Process Start(ProcessStartInfo start) => Start(start, answers: null);

    /// <summary><see cref="Start(ProcessStartInfo)"/> off the caller's thread when it has a context of its own (the UI
    /// thread's), on it otherwise: the fork costs a large process tens of milliseconds, which the window would wait
    /// out. The child is started, its id known, once the task is done; what the start throws, the task throws.</summary>
    public static Task<Process> StartAsync(ProcessStartInfo start) => StartOffAsync(start, answers: null);

    /// <summary>The fork, unheld: what started the child holds its own wait for it, and the window's end may wait for a
    /// start with no UI thread to come back to.</summary>
    private static async Task<Process> StartOffAsync(ProcessStartInfo start, IReadOnlyCollection<int>? answers) =>
        SynchronizationContext.Current is null ? Start(start, answers) : await BackgroundWork.Unheld(() => Start(start, answers));

    /// <summary><see cref="Start(ProcessStartInfo)"/>, with the exit codes that are the child's answer, not its failure.</summary>
    private static Process Start(ProcessStartInfo start, IReadOnlyCollection<int>? answers)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        bool closeInput = !start.RedirectStandardInput;
        start.RedirectStandardInput = true;
        start.StandardInputEncoding ??= Utf8;
        if (start.RedirectStandardOutput)
        {
            start.StandardOutputEncoding ??= Utf8;
        }
        if (start.RedirectStandardError)
        {
            start.StandardErrorEncoding ??= Utf8;
        }
        Lifeline.Prepare(start);
        string command = Display(start);
        long began = Stopwatch.GetTimestamp();
        Process process = new() { StartInfo = start, EnableRaisingEvents = true };
        process.Exited += (_, _) => OnExited(process, start, command, began, answers);
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }
        Lifeline.Adopt(process);
        // An id the IDE ended before, whose end went untold (its process disposed first), is another child's now.
        EndedHere.TryRemove(process.Id, out _);
        Started?.Invoke(null, new ChildStart(process.Id, command, start.WorkingDirectory) { Start = start });
        if (closeInput)
        {
            process.StandardInput.Close();
        }
        return process;
    }

    /// <summary>Takes on a program started in the terminal's pseudo-terminal from <paramref name="start"/>, so it ends
    /// with the IDE and is logged as a child is: on macOS and Linux its own session and process group go on the
    /// watchdog's list (<see cref="Lifeline"/>); on Windows the pseudo-terminal's job holds it already.
    /// <see cref="Kill"/> then ends it with all it started; <see cref="Exited"/> logs its end.</summary>
    public static void Adopt(int processId, ProcessStartInfo start)
    {
        Lifeline.AdoptGroup(processId);
        EndedHere.TryRemove(processId, out _);
        Started?.Invoke(null, new ChildStart(processId, Display(start), start.WorkingDirectory) { Start = start });
    }

    /// <summary>An adopted program's end, for the IDE's log as a child's is told.</summary>
    public static void Exited(int processId, ProcessStartInfo start, int exitCode, TimeSpan time) =>
        Ended?.Invoke(null, new ChildEnd(processId, Display(start), exitCode, time, EndedHere.TryRemove(processId, out _)) { Start = start });

    /// <summary>Says the IDE is ending a child by asking it to (an exit request, its input closed): however it then exits,
    /// a pipe it still writes to closed under it included (SIGPIPE's 141), it did not fail. <see cref="Kill"/> says it too.</summary>
    public static void Ending(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                EndedHere[process.Id] = 0;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // It had ended, or was never started.
        }
    }

    /// <summary>A one-line shell command run on macOS and Linux once the IDE's process has ended, however it ended, after
    /// its children are: for what lives outside any child (the containers an engine runs), which only a command ends. It
    /// takes the place of the one given before; on Windows nothing runs it, and the next start ends what is left.</summary>
    public static void AtIdeEnd(string command) => Lifeline.AtEnd(command);

    /// <summary>Runs a child to its end, its output and errors read whole, <paramref name="input"/> written to it first; an
    /// exit code among <paramref name="answers"/> is its answer (git's "no such file"), not a failure to log. Canceled, or
    /// still running after <paramref name="limit"/>, it is killed with all it started, its output let go, and the task is
    /// canceled or fails with a <see cref="TimeoutException"/>. Once it has exited it is done, whatever the limit: a job it
    /// left holding its output open (a hook's <c>daemon &amp;</c>) is given <see cref="OutputGrace"/>, then ended with its group.</summary>
    public static async Task<ChildResult> RunAsync(ProcessStartInfo start, string? input = null, TimeSpan? limit = null, CancellationToken cancel = default,
        IReadOnlyCollection<int>? answers = null, int? mostOutput = null)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process process = await StartOffAsync(start, answers);
        using CancellationTokenSource ending = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        // Output past what the caller reads of it ends the child as an overrun limit does, said by the size it went past.
        Reading output = new(process.StandardOutput, mostOutput, ending);
        Reading errors = new(process.StandardError, mostOutput, ending);
        Task read = Task.WhenAll(output.Done, errors.Done);
        if (limit is { } most)
        {
            ending.CancelAfter(most);
        }
        try
        {
            try
            {
                if (input is not null)
                {
                    await process.StandardInput.WriteAsync(input.AsMemory(), ending.Token);
                }
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It ended, or closed its input, before reading all it was given: its exit says how it went.
            }
            await process.WaitForExitAsync(ending.Token);
            await OutputEndsAsync(process, read, cancel);
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
            // One given up on was ended, not failed; one that outran its limit hung, and its end says so.
            if (cancel.IsCancellationRequested)
            {
                Kill(process);
            }
            else
            {
                KillTree(process);
            }
            // Disposing the process ends the reads; their ends are nobody's to report.
            Forget(read);
            cancel.ThrowIfCancellationRequested();
            if (output.Overran || errors.Overran)
            {
                throw new ChildOutputException($"{Shown(start)} wrote more than the {mostOutput} characters read of it, and was ended");
            }
            throw new TimeoutException($"{Shown(start)} had not ended after {limit.GetValueOrDefault().TotalSeconds:0} s");
        }
        // What a job out of the kill's reach still holds (on Windows, which ends a tree, one whose parent has gone) is let go.
        Forget(read);
        if (output.Overran || errors.Overran)
        {
            throw new ChildOutputException($"{Shown(start)} wrote more than the {mostOutput} characters read of it");
        }
        return new ChildResult(process.ExitCode, output.Text, errors.Text);
    }

    /// <summary>Whether the reads of a child that has exited end: what is in the pipes is read at once, and a job it left
    /// holding them open (a hook's <c>daemon &amp;</c>, a program's helper) is given <see cref="OutputGrace"/>, then ended with
    /// the child's group, and given it again. False when something out of the kill's reach (on Windows, which ends a tree,
    /// one whose parent has gone) still holds them: its output is the caller's to let go.</summary>
    public static async Task<bool> OutputEndsAsync(Process process, Task read, CancellationToken cancel = default)
    {
        if (await EndsWithin(read, cancel))
        {
            return true;
        }
        KillTree(process);
        return await EndsWithin(read, cancel);
    }

    /// <summary>Whether a child's reads, the child exited, end within <see cref="OutputGrace"/>.</summary>
    private static async Task<bool> EndsWithin(Task read, CancellationToken cancel)
    {
        try
        {
            await read.WaitAsync(OutputGrace, cancel);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>A child's output or errors read to their end, and what was read so far once the reading is let go; with a
    /// most, the reading stops past it, <see cref="Overran"/>, and the run is ended through its <c>ending</c>.</summary>
    private sealed class Reading
    {
        private readonly StringBuilder text = new();
        private readonly int? most;
        private readonly CancellationTokenSource? ending;

        public Reading(StreamReader reader, int? most = null, CancellationTokenSource? ending = null)
        {
            this.most = most;
            this.ending = ending;
            Done = ReadAsync(reader);
        }

        public Task Done { get; }

        /// <summary>The child wrote more than the most read of it.</summary>
        public bool Overran { get; private set; }

        public string Text
        {
            get
            {
                lock (text)
                {
                    return text.ToString();
                }
            }
        }

        private async Task ReadAsync(StreamReader reader)
        {
            char[] buffer = new char[16 * 1024];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
            {
                lock (text)
                {
                    if (text.Length + read > most)
                    {
                        Overran = true;
                    }
                    else
                    {
                        text.Append(buffer, 0, read);
                    }
                }
                if (Overran)
                {
                    try
                    {
                        ending?.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The run was over already.
                    }
                    return;
                }
            }
        }
    }

    /// <summary>Ends a child and everything it started, now, passing over what has ended: its tree on Windows, but for a
    /// process of it Windows will not end (one ending, or not ours where a parent's id was reused); on macOS and Linux its
    /// process group, which holds what it started and what it left behind, whether or not it has ended itself. Never the
    /// runtime's tree kill there, which stops each process before killing it and can then hang for good on macOS.</summary>
    public static void Kill(Process process)
    {
        Ending(process);
        KillTree(process);
    }

    /// <summary><see cref="Kill"/>, its end told as the child's own: one that hung past its limit.</summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (AggregateException partly) when (partly.InnerExceptions.All(e => e is Win32Exception or InvalidOperationException))
                {
                    // Windows refuses a process of the tree that has ended or is ending, or is not ours: the child must still go.
                    process.Kill();
                }
                return;
            }
            Lifeline.KillGroup(process);
            process.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // It had ended, or was ending.
        }
    }

    /// <summary>Starts a program of the person's own that outlives whatever started it (the person's Passenger): a session
    /// of its own, never on the watchdog's list nor in a job, its input and output none of the starter's.</summary>
    public static void StartApart(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        if (!OperatingSystem.IsWindows())
        {
            start.StartDetached = true;
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} did not start");
        Started?.Invoke(null, new ChildStart(process.Id, Display(start), start.WorkingDirectory) { Start = start });
        // What it writes is nobody's: its pipes closed here, its writes to them fail, which .NET's console passes over.
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
    }

    /// <summary>A command as a person would type it, each word quoted that needs to be, as it runs (the log and a console
    /// say it through <see cref="Shown"/>).</summary>
    public static string Display(ProcessStartInfo start) => Display(start, word => word);

    /// <summary><see cref="Display(ProcessStartInfo)"/> with each word (the program, each argument, or the arguments whole when
    /// given as one string) as <paramref name="said"/> says it, before it is quoted.</summary>
    public static string Display(ProcessStartInfo start, Func<string, string> said) =>
        string.Join(' ', start.ArgumentList.Count > 0 || start.Arguments.Length == 0
            ? [Quote(said(start.FileName)), .. start.ArgumentList.Select(a => Quote(said(a)))]
            : [Quote(said(start.FileName)), said(start.Arguments)]);

    /// <summary>A word of a command line as it is shown: in double quotes, its own escaped, when it is empty or holds a
    /// space or a quote, else as it is.</summary>
    public static string Quote(string word) =>
        word.Length > 0 && !word.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? word : $"\"{word.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static void OnExited(Process process, ProcessStartInfo start, string command, long began, IReadOnlyCollection<int>? answers)
    {
        try
        {
            int id = process.Id;
            bool ended = EndedHere.TryRemove(id, out _);
            int code = process.ExitCode;
            Ended?.Invoke(null, new ChildEnd(id, command, code, Stopwatch.GetElapsedTime(began), ended, answers?.Contains(code) == true) { Start = start });
        }
        catch (InvalidOperationException)
        {
            // Disposed by its owner as it ended: there is no exit code to tell.
        }
    }

    private static void Forget(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}

/// <summary>A child that wrote more than its caller reads of it, ended for it.</summary>
public sealed class ChildOutputException(string message) : IOException(message);

/// <summary>How a child run to its end went: its exit code, and what it wrote to its output and errors.</summary>
public sealed record ChildResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>What a failed child said: its errors, else its output (their first six lines, joined), else its exit code.</summary>
    public string Said
    {
        get
        {
            string said = (Error.Trim().Length > 0 ? Error : Output).Trim();
            string[] lines = said.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? $"it exited with {ExitCode}" : string.Join(' ', lines.Take(6));
        }
    }
}

/// <summary>A child's start: its process, the command as it ran, and the folder it runs in (the IDE's own when empty); its
/// line for the log says the command as <see cref="ChildProcesses.Shown"/> does, its secrets masked.</summary>
public sealed record ChildStart(int ProcessId, string Command, string Folder)
{
    /// <summary>What it was started from, for the words of its command line.</summary>
    public ProcessStartInfo? Start { get; init; }

    public override string ToString() =>
        $"Process {ProcessId} started{(Folder.Length > 0 ? $" in {Folder}" : string.Empty)}: {(Start is { } start ? ChildProcesses.Shown(start) : Command)}";
}

/// <summary>A child's end: its process, the command as it ran, its exit code and how long it ran, whether the IDE ended it,
/// and whether its exit code is its answer to the question it was asked; its line for the log says the command as
/// <see cref="ChildProcesses.Shown"/> does, its secrets masked.</summary>
public sealed record ChildEnd(int ProcessId, string Command, int ExitCode, TimeSpan Time, bool EndedByIde = false, bool Answered = false)
{
    /// <summary>What it was started from, for the words of its command line.</summary>
    public ProcessStartInfo? Start { get; init; }

    /// <summary>Worth a line in the IDE's log: the IDE did not end it, and it failed or ran long enough for a person to wait
    /// on it. Git's quick reads run on every save, and would bury the rest.</summary>
    public bool Notable => !EndedByIde && ((ExitCode != 0 && !Answered) || Time >= TimeSpan.FromSeconds(1));

    public override string ToString() =>
        $"Process {ProcessId} {(EndedByIde ? "ended by the IDE, " : string.Empty)}exited with code {ExitCode} after {Time.TotalSeconds:0.00} s: {(Start is { } start ? ChildProcesses.Shown(start) : Command)}";
}
