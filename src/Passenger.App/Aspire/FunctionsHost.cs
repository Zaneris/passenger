using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>An Azure Functions app (.NET isolated worker) an AppHost adds with AddAzureFunctionsProject, which it asks the IDE
/// to start as a launch of its own type: run as dotnet run runs one, by Azure Functions Core Tools' <c>func start</c> in its
/// build's output folder, which starts the app's own program, its worker. Debugged, the worker waits at its start for a
/// debugger (<see cref="Debugged"/>), which attaches to it once found among what func started (<see cref="WorkerAsync"/>).</summary>
public static class FunctionsHost
{
    /// <summary>The launch configuration's type an AppHost gives a Functions app, which the IDE says it starts.</summary>
    public const string LaunchType = "azure-functions";

    /// <summary>What has func's host tell the worker to wait for a debugger at its start.</summary>
    private const string DebugFlag = "--dotnet-isolated-debug";

    /// <summary>What the host gives every worker it starts on its command line, which tells the worker among its programs.</summary>
    private const string WorkerArgument = "--functions-worker-id";

    /// <summary>Core Tools' func on the PATH, on Windows its program before npm's script of that name; null where none is.</summary>
    public static string? Func() => OperatingSystem.IsWindows() ? Programs.OnPath("func.exe") ?? Programs.OnPath("func.cmd") : Programs.OnPath("func");

    /// <summary>The app's plan as func start runs it, in the folder its build writes, with the arguments given: the AppHost's
    /// (its <c>--port</c>), else the launch profile's.</summary>
    public static LaunchPlan Plan(LaunchPlan app, string func, IReadOnlyList<string> arguments)
    {
        bool script = func.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        List<string> leading = script ? ["/d", "/c", func, "start"] : ["start"];
        return app with
        {
            Program = script ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : func,
            Arguments = [.. leading, .. arguments],
            Leading = leading.Count,
            ArgumentsOfProject = false,
            ByAppHost = false,
            WorkingDirectory = Path.GetDirectoryName(app.Target ?? app.Built ?? app.Program)!,
        };
    }

    /// <summary>The plan with its worker held at its start until a debugger attaches.</summary>
    public static LaunchPlan Debugged(LaunchPlan functions) => functions with { Arguments = [.. functions.Arguments, DebugFlag] };

    /// <summary>The worker func started, by the argument its host gives each, waited for while func runs until
    /// <paramref name="stop"/>; null where none came.</summary>
    public static async Task<int?> WorkerAsync(int func, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested && DotnetProcesses.Runs(func))
        {
            if (DotnetProcesses.Under(func).FirstOrDefault(p => p.CommandLine.Contains(WorkerArgument, StringComparison.Ordinal)) is { } worker)
            {
                return worker.Id;
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return null;
            }
        }
        return null;
    }
}
