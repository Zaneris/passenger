using System.Diagnostics;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.App.Run;

/// <summary>A Godot game run in a tab of the Terminal window, under Run and Debug alike: on Windows through Godot's console
/// wrapper beside its .exe (<see cref="GodotExecutables.InTerminal"/>), since Godot's own .exe writes to no console it is
/// started in, the game's own process being the one the wrapper starts, which the debugger takes hold of and Hot Reload
/// attaches to (<see cref="TerminalRun.Debuggee"/>); anywhere else the game's executable itself.</summary>
public static class GodotConsole
{
    /// <summary>How long a console wrapper has to start the game: it does at once, the game held at its start meanwhile.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(10);

    /// <summary>What the terminal starts for a plan's program: Godot's console wrapper for a Godot game's, where there is one.</summary>
    public static string Program(LaunchPlan plan, string program) => plan.Godot is not null && !plan.GodotEditor ? GodotExecutables.InTerminal(program) : program;

    /// <summary>The debugger's runInTerminal for a plan run in the terminal: the plan's Godot game through its console
    /// wrapper, the run told the game's own process once the wrapper has started it; any other program as asked.</summary>
    public static async Task<TerminalRun> RunAsync(LaunchPlan plan, RunInTerminalArguments arguments, Func<RunInTerminalArguments, Task<TerminalRun>> run)
    {
        if (arguments.Args is not [string program, ..] || Program(plan, program) is var wrapper && wrapper == program)
        {
            return await run(arguments);
        }
        TerminalRun started = await run(arguments with { Args = [wrapper, .. arguments.Args.Skip(1)] });
        await FollowAsync(started, program, wrapper);
        return started;
    }

    /// <summary>Once the terminal runs <paramref name="started"/> in place of a plan's <paramref name="program"/> (its console
    /// wrapper, <see cref="Program"/>), takes the game the wrapper started, the program by its name, as the run's
    /// <see cref="TerminalRun.Debuggee"/>; nothing for a program run as itself. Throws <see cref="LaunchException"/>, the
    /// wrapper ended, where none came.</summary>
    public static async Task FollowAsync(TerminalRun run, string program, string started)
    {
        UiThread.Check();
        int wrapper = run.ProcessId;
        if (wrapper == 0 || started == program)
        {
            return;
        }
        string name = Path.GetFileName(program);
        long began = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(began) < Waits.Of(StartLimit))
        {
            int game = await BackgroundWork.Run(() => DotnetProcesses.StartedBy(wrapper)
                .FirstOrDefault(id => DotnetProcesses.Describe(id)?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true));
            if (game > 0)
            {
                run.Debuggee = game;
                return;
            }
            if (!DotnetProcesses.Runs(wrapper))
            {
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        run.Stop();
        throw new LaunchException($"{Path.GetFileName(started)} started no Godot within {Waits.Of(StartLimit).TotalSeconds:0} s");
    }
}
