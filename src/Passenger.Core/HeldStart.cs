using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace Passenger.Core;

/// <summary>A .NET program held at its start by its runtime's own diagnostic port until what attaches to it (the debugger,
/// the profiler) or Passenger lets it go: the variables that hold it, the one wait for that port, which a runtime opens a
/// moment after its process starts, and the one let-go, which clears the hold first, so a .NET program it starts in turn
/// runs as it would unheld.</summary>
public static class HeldStart
{
    /// <summary>How long a program is given to open its diagnostic port, longer on a slower machine as <see cref="Waits"/>
    /// says: one that never opens it is said to be no .NET program once it has passed.</summary>
    public static TimeSpan PortLimit => Waits.Of(TimeSpan.FromSeconds(10));

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    /// <summary>The variables that hold a program at its start; <paramref name="changeable"/> also opens its code to Hot
    /// Reload, which a program held for the profiler goes without.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Variables(bool changeable)
    {
        yield return new(RuntimeVariables.DefaultDiagnosticPortSuspend, "1");
        if (changeable)
        {
            yield return new(RuntimeVariables.ModifiableAssemblies, "debug");
        }
    }

    /// <summary>Why no debugger can reach a program whose environment (<paramref name="variable"/>: a variable's value, or
    /// null) turns its runtime's debugger off, null while it is on. Such a program is never held at its start, since no
    /// debugger would come to let it go, though its diagnostic port may still hold it.</summary>
    public static string? DebuggerOff(Func<string, string?> variable) =>
        ((string[])[RuntimeVariables.EnableDiagnostics, RuntimeVariables.EnableDiagnosticsDebugger]).FirstOrDefault(name => variable(name) == "0") is { } off
            ? $"Its runtime was started with {off}=0, which turns its debugger off: nothing can attach to it"
            : null;

    /// <summary>Why no debugger can reach a program held at its start (<see cref="DebuggerOff"/>), its environment read through
    /// its diagnostic port, which answers while the runtime is held; null where it can, or where its port cannot be asked.</summary>
    public static async Task<string?> DebuggerOffAsync(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        using (process)
        {
            Dictionary<string, string>? environment = null;
            try
            {
                await AtPortAsync(process, client =>
                {
                    environment = client.GetProcessEnvironment();
                    return Task.CompletedTask;
                }, sinceItStarted: true).ConfigureAwait(false);
            }
            catch (Exception unread) when (DiagnosticsFailures.Of(unread))
            {
                return null;
            }
            return environment is null ? null : DebuggerOff(environment.GetValueOrDefault);
        }
    }

    /// <summary>Whether a runtime started with this environment waits at its start: asked to, with its diagnostic port on.</summary>
    public static bool Holds(Func<string, string?> variable) => variable(RuntimeVariables.DefaultDiagnosticPortSuspend) == "1"
        && variable(RuntimeVariables.EnableDiagnostics) != "0" && variable(RuntimeVariables.EnableDiagnosticsIpc) != "0";

    /// <summary>Lets go the runtime <paramref name="client"/> reaches, held at its start, its hold cleared first; a runtime
    /// not held takes the word as done. Null once let go, else why not (<see cref="DiagnosticsFailures"/>); a port not
    /// listening yet is a <see cref="ServerNotAvailableException"/>, which <see cref="AtPortAsync"/> waits out.</summary>
    public static string? LetGo(DiagnosticsClient client)
    {
        try
        {
            try
            {
                client.SetEnvironmentVariable(RuntimeVariables.DefaultDiagnosticPortSuspend, string.Empty);
            }
            catch (ServerErrorException)
            {
                // A runtime before .NET 6 has no such command: what it starts is still held, but it runs.
            }
            client.ResumeRuntime();
            return null;
        }
        catch (Exception error) when (error is not ServerNotAvailableException && DiagnosticsFailures.Of(error))
        {
            return error.Message;
        }
    }

    /// <summary>Lets the program of <paramref name="processId"/> go as the overload given its process does; one gone already
    /// said so.</summary>
    public static async Task<string?> LetGoAsync(int processId, CancellationToken stop = default, Task? answered = null, bool sinceItStarted = false,
        TimeSpan? within = null)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException)
        {
            return "it had ended";
        }
        using (process)
        {
            return await LetGoAsync(process, stop, answered, sinceItStarted, within).ConfigureAwait(false);
        }
    }

    /// <summary>Lets a program go (<see cref="LetGo"/>) once its port is open, as <see cref="AtPortAsync"/> waits for it.
    /// Null once let go, or once its runtime has <paramref name="answered"/> otherwise; else why not, a port that failed as
    /// it was asked (<see cref="DiagnosticsFailures"/>) said too. <paramref name="stop"/> throws.</summary>
    public static async Task<string?> LetGoAsync(Process process, CancellationToken stop = default, Task? answered = null, bool sinceItStarted = false,
        TimeSpan? within = null)
    {
        string? refused = null;
        return await AtPortAsync(process, client =>
        {
            refused = LetGo(client);
            return Task.CompletedTask;
        }, stop, answered, sinceItStarted, within).ConfigureAwait(false) switch
        {
            PortWait.Reached => refused,
            PortWait.Answered => null,
            PortWait.Ended => "it ended before it could be let go",
            _ => $"it is not a .NET program that can be reached: {(answered is null ? string.Empty : "no runtime in it answered, and ")}it opened no "
                + $"diagnostic port within {PortLimit.TotalSeconds:0} s",
        };
    }

    /// <summary>Runs <paramref name="ask"/> at the program's diagnostic port, waiting for the port while the program lives,
    /// <see cref="PortLimit"/> from now (from the program's start, <paramref name="sinceItStarted"/>), within
    /// <paramref name="within"/> when given, and no longer once its runtime has <paramref name="answered"/> otherwise. What
    /// the ask throws but a port not listening goes to the caller, and <paramref name="stop"/> throws.</summary>
    public static async Task<PortWait> AtPortAsync(Process process, Func<DiagnosticsClient, Task> ask, CancellationToken stop = default, Task? answered = null,
        bool sinceItStarted = false, TimeSpan? within = null)
    {
        DateTime until = (sinceItStarted ? StartOf(process) : DateTime.UtcNow) + PortLimit;
        if (within is { } given && DateTime.UtcNow + given < until)
        {
            until = DateTime.UtcNow + given;
        }
        while (true)
        {
            if (answered is { IsCompleted: true })
            {
                return PortWait.Answered;
            }
            stop.ThrowIfCancellationRequested();
            if (Listed(process.Id))
            {
                try
                {
                    await ask(new DiagnosticsClient(process.Id)).ConfigureAwait(false);
                    return PortWait.Reached;
                }
                catch (ServerNotAvailableException)
                {
                    // None yet, one closing, or one a process of the same id left when it was killed.
                }
            }
            if (Ended(process))
            {
                return PortWait.Ended;
            }
            if (DateTime.UtcNow >= until)
            {
                return PortWait.NoPort;
            }
            await (answered is null ? Task.Delay(Poll, stop) : Task.WhenAny(answered, Task.Delay(Poll, stop))).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the port may be there: on Windows its pipe is looked for first, since a client there waits 30 s on
    /// one that does not exist; elsewhere the client finds the socket at once, with no list of published processes made.</summary>
    private static bool Listed(int processId) => !OperatingSystem.IsWindows() || DiagnosticsClient.GetPublishedProcesses().Contains(processId);

    /// <summary>When the process started, or now when the system will not say.</summary>
    private static DateTime StartOf(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return DateTime.UtcNow;
        }
    }

    /// <summary>Whether the process has ended; one the system will not say of runs on (the deadline then ends the wait).</summary>
    private static bool Ended(Process process) => !DotnetProcesses.Runs(process);
}

/// <summary>How <see cref="HeldStart.AtPortAsync"/>'s wait ended.</summary>
public enum PortWait
{
    /// <summary>The port was there, and the ask was made.</summary>
    Reached,

    /// <summary>The runtime answered otherwise first.</summary>
    Answered,

    /// <summary>The program ended first.</summary>
    Ended,

    /// <summary>No port opened in its time.</summary>
    NoPort,
}
