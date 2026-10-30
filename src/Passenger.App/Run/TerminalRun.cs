using System.Diagnostics;
using Passenger.Core;
using Passenger.Terminal;

namespace Passenger.App.Run;

/// <summary>A program run in a tab of the Terminal window, for a configuration that uses the terminal: a real terminal,
/// which it reads keys from and draws on as it likes. It ends with the IDE, a crash included
/// (<see cref="ChildProcesses.Adopt"/>), Stop ends it and all it started, and its tab says when it ended and its exit
/// code. A UI-thread object.</summary>
public sealed class TerminalRun : IRunningProgram
{
    /// <summary>The exit code a shell gives a program SIGKILL ended, which is how Stop ends one on macOS and Linux.</summary>
    private const int Killed = 128 + 9;

    private readonly TaskCompletionSource<int> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ProcessStartInfo start;
    private long began;
    private bool stopping;

    public TerminalRun(string name, TerminalSession session, ProcessStartInfo start)
    {
        Name = name;
        Session = session;
        this.start = start;
    }

    /// <summary>What runs it, as its tab and the status bar name it: the run configuration's name.</summary>
    public string Name { get; }

    public TerminalSession Session { get; }

    /// <summary>The agent whose call started it (its run, or an action it ran), by its connection; null for a person's run.</summary>
    public Mcp.AgentConnection? StartedBy { get; set; }

    /// <summary>The program's process, once it has started; 0 before, and for one that never did.</summary>
    public int ProcessId { get; private set; }

    /// <summary>Its exit code once it has ended and its last output is on its tab's screen; -1 for one that never started.</summary>
    public Task<int> Ended => ended.Task;

    public bool IsRunning => ProcessId != 0 && !ended.Task.IsCompleted;

    int? IRunningProgram.ProcessId => ProcessId == 0 ? null : ProcessId;

    /// <summary>Whether it ended because the IDE ended it (Stop, or the debugger's).</summary>
    public bool WasStopped => stopping;

    /// <summary>Ends the program and everything it started, now: its process group on macOS and Linux, its process tree on
    /// Windows.</summary>
    public void Stop()
    {
        UiThread.Check();
        if (!IsRunning)
        {
            return;
        }
        Stopping();
        try
        {
            using Process program = Process.GetProcessById(ProcessId);
            ChildProcesses.Kill(program);
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException)
        {
            // It has ended.
        }
    }

    /// <summary>Says the IDE is ending it, the debugger's Stop included: an end Porta.Pty gives no code of its own (a
    /// signal's, on macOS and Linux) is then told as SIGKILL's.</summary>
    public void Stopping() => stopping = true;

    /// <summary>The program has started, as this process.</summary>
    public void Started(int processId)
    {
        ProcessId = processId;
        began = Stopwatch.GetTimestamp();
        ChildProcesses.Adopt(processId, start);
    }

    /// <summary>The program could not start, its tab saying why.</summary>
    public void Failed(string problem)
    {
        Session.Say($"{problem}\r\n");
        ended.TrySetResult(-1);
    }

    /// <summary>The program has ended, its last output on the screen: its tab says so with its exit code, below what it
    /// wrote.</summary>
    public void OnExited(int code)
    {
        int exitCode = stopping && code == 0 && !OperatingSystem.IsWindows() ? Killed : code;
        ChildProcesses.Exited(ProcessId, start, exitCode, Stopwatch.GetElapsedTime(began));
        // After a blank line wherever the program left the cursor, in the terminal's colours, and the cursor hidden.
        Session.Say($"\x1b[0m{(Session.Screen.CursorColumn > 0 ? "\r\n" : string.Empty)}\r\nProcess finished with exit code {exitCode}.\r\n\x1b[?25l");
        ended.TrySetResult(exitCode);
    }
}
