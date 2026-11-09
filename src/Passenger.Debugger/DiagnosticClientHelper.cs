using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Debugger;

public static class DiagnosticClientHelper
{
    /// <summary>Lets go a process held at its start by its diagnostic port (<see cref="HeldStart"/>), as an attach does once
    /// the debugger is registered for its runtime; one already running is let be, its runtime's answer
    /// (<paramref name="answered"/>) ending the wait for a port. Fails, saying why, when the process or the session ends
    /// first, no port opens in its time (no .NET in it, or none the debugger reaches), or its port fails as it is asked
    /// before its runtime answers.</summary>
    public static async Task LetGoIfHeld(Process process, Task answered, CancellationToken sessionEnded)
    {
        string? refused;
        try
        {
            refused = await HeldStart.LetGoAsync(process, sessionEnded, answered);
        }
        catch (OperationCanceledException) when (sessionEnded.IsCancellationRequested)
        {
            throw new InvalidOperationException("the session ended before the debugger could take hold of it");
        }
        if (refused is not null && !answered.IsCompleted)
        {
            throw new InvalidOperationException(refused);
        }
    }

    /// <summary>Lets go a process's runtime held at its start, as an attach would have: a runtime already running takes the
    /// word as done. Its port is waited for while the process lives, until it is <see cref="HeldStart.PortLimit"/> old, and
    /// within <paramref name="within"/> when given. Null once asked, else why it could not be.</summary>
    public static Task<string?> ResumeAsync(int processId, TimeSpan? within = null) =>
        HeldStart.LetGoAsync(processId, sinceItStarted: true, within: within);
}
