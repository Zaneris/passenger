using System.Diagnostics;
using Passenger.Core;

namespace Passenger.App.ToolWindows;

/// <summary>What a command run into a console came to: its exit code and the last lines it wrote, which say why it failed.</summary>
public sealed record ConsoleOutcome(int ExitCode, IReadOnlyList<string> Last)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>A command whose output reaches a console, the one way: started through <see cref="ChildProcesses"/>, its output
/// and errors read in as they come (<see cref="ConsoleSink.PumpAsync"/>), each line told and the last kept, its output
/// waited for once it exits as <see cref="ChildProcesses.OutputEndsAsync"/> waits; cancelled, it is ended with all it
/// started, and gone before the cancel goes on. Any thread.</summary>
public static class ConsoleCommand
{
    /// <summary>The longest a command ended by a cancel is waited for to be gone.</summary>
    private static readonly TimeSpan Ending = TimeSpan.FromSeconds(5);

    /// <summary>Runs it to its end: <paramref name="lines"/> told each line of either stream, <paramref name="shown"/> making
    /// whole lines of it as the console shows them (secrets masked, lines left out), its errors written as
    /// <paramref name="errors"/>, and its last <paramref name="last"/> lines kept. Throws what its start throws, and
    /// <see cref="OperationCanceledException"/> once a cancel has ended it.</summary>
    public static async Task<ConsoleOutcome> RunAsync(ConsoleSink console, ProcessStartInfo start, CancellationToken cancel, Action<string>? lines = null,
        Func<string, string>? shown = null, ConsoleStream errors = ConsoleStream.Error, int last = 8)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Queue<string> said = new();
        using Process process = await ChildProcesses.StartAsync(start);
        Task read = Task.WhenAll(console.PumpAsync(ChildProcesses.Output(process), ConsoleStream.Output, lines: Line, shown: shown),
            console.PumpAsync(ChildProcesses.Errors(process), errors, lines: Line, shown: shown));
        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            ChildProcesses.Kill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(Ending, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                IdeLog.Write(IdeLog.Ide, $"{ChildProcesses.Shown(start)} had not ended {Ending.TotalSeconds:0} s after it was ended");
            }
            throw;
        }
        await ChildProcesses.OutputEndsAsync(process, read, CancellationToken.None);
        lock (said)
        {
            return new ConsoleOutcome(process.ExitCode, [.. said]);
        }

        void Line(string line)
        {
            lines?.Invoke(line);
            lock (said)
            {
                said.Enqueue(line);
                if (said.Count > last)
                {
                    said.Dequeue();
                }
            }
        }
    }
}
