using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace Passenger.Debugger;

public static class DiagnosticClientHelper
{
    /// <summary>What holds a program at its start until a diagnostics client lets it go.</summary>
    private const string HoldVariable = "DOTNET_DefaultDiagnosticPortSuspend";

    /// <summary>Lets go a process held at its start by its diagnostic port, as an attach does once the debugger is registered
    /// for its runtime; one already running is let be, its runtime's answer (<paramref name="answered"/>) ending the wait
    /// for a port. The variable that held it is cleared in the program first, so a .NET program it starts in turn (a build,
    /// a test host, a child it waits for) runs as it would undebugged rather than held for a debugger that never comes.
    /// Fails when the process ends first, or 10 s pass without a runtime or port: no .NET the debugger reaches.</summary>
    public static async Task LetGoIfHeld(Process process, Task answered, CancellationToken cancellation = default)
    {
        // A program a terminal has only just started may take a while to load its runtime on a busy machine.
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!answered.IsCompleted)
        {
            cancellation.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException("it ended before the debugger could take hold of it");
            }
            // Looked for first: on Windows a client waits 30 s on a pipe that does not exist before it gives up.
            if (DiagnosticsClient.GetPublishedProcesses().Contains(process.Id))
            {
                try
                {
                    Release(new DiagnosticsClient(process.Id));
                    return;
                }
                catch (ServerNotAvailableException)
                {
                    // Its port was closing, or not yet listening.
                }
            }
            if (DateTime.UtcNow >= until && !HasRuntime(process))
            {
                throw new InvalidOperationException("it is not a .NET program the debugger can reach: no runtime in it answered, and it opened no diagnostic port within 10 s");
            }
            // A program only just started opens its port a moment after it starts.
            await Task.WhenAny(answered, Task.Delay(50, cancellation));
        }
    }

    private static bool HasRuntime(Process process)
    {
        try
        {
            process.Refresh();
            return process.Modules.Cast<ProcessModule>().Any(module => module.ModuleName is "coreclr.dll" or "libcoreclr.so" or "libcoreclr.dylib");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static void Release(DiagnosticsClient client)
    {
        try
        {
            client.SetEnvironmentVariable(HoldVariable, string.Empty);
        }
        catch (ServerErrorException)
        {
            // A runtime before .NET 6 has no such command: its children are still held, but it runs.
        }
        client.ResumeRuntime();
    }
}
