using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Debugger;

public static class DiagnosticClientHelper
{
    /// <summary>Lets go a process held at its start by its diagnostic port (<see cref="HeldStart"/>), as an attach does once
    /// the debugger is registered for its runtime; one already running is let be, its runtime's answer
    /// (<paramref name="answered"/>) ending the wait for a port. Fails when the process or the session ends first, or no
    /// port opens in its time: no .NET in it, or none the debugger reaches.</summary>
    public static async Task LetGoIfHeld(Process process, Task answered, CancellationToken sessionEnded)
    {
        string? refused = null;
        PortWait waited;
        try
        {
            waited = await HeldStart.AtPortAsync(process, client =>
            {
                refused = HeldStart.LetGo(client);
                return Task.CompletedTask;
            }, sessionEnded, answered);
        }
        catch (OperationCanceledException) when (sessionEnded.IsCancellationRequested)
        {
            throw new InvalidOperationException("the session ended before the debugger could take hold of it");
        }
        switch (waited)
        {
            case PortWait.Reached when refused is not null:
                throw new InvalidOperationException(refused);
            case PortWait.Ended:
                throw new InvalidOperationException("it ended before the debugger could take hold of it");
            case PortWait.NoPort:
                throw new InvalidOperationException($"it is not a .NET program the debugger can reach: no runtime in it answered, and it opened no diagnostic port within {HeldStart.PortLimit.TotalSeconds:0} s");
        }
    }

    /// <summary>Lets go a process's runtime held at its start, as an attach would have: a runtime already running takes the
    /// word as done. Its port is waited for while the process lives, until it is <see cref="HeldStart.PortLimit"/> old, and
    /// within <paramref name="within"/> when given. Null once asked, else why it could not be.</summary>
    public static Task<string?> ResumeAsync(int processId, TimeSpan? within = null) =>
        HeldStart.LetGoAsync(processId, sinceItStarted: true, within: within);
}
