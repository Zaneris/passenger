using Passenger.App.Web;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>Run of a Node package's script (<see cref="RunKind.Script"/>): its package.json read as it is now, the Node.js
/// Settings names (else the PATH's) checked against what its <c>engines</c> asks, and the script started by its own package
/// manager (<see cref="ScriptRuns"/>) in the Run window's console, or the Terminal window's where the configuration says, as
/// Run's program: Stop and Rerun end it as they end any.</summary>
public sealed partial class BuildRunController
{
    /// <summary>Runs a script configuration; null once it runs, else why it could not, said.</summary>
    private async Task<string?> RunScriptAsync(RunConfiguration chosen, string? arguments, Working work)
    {
        using IDisposable held = BackgroundWork.Scope();
        if (await parts.Editors.SaveAllLandedAsync(work, work.Stopping) is { } unsaved)
        {
            return Said(unsaved);
        }
        work.Say($"Reading {chosen.Name}'s package.json…");
        string top = session.Folder ?? Path.GetDirectoryName(chosen.Project)!;
        string? configured = parts.Settings().NodeInterpreter;
        string? root = session.NodeFiles.Packages.FirstOrDefault(p => PathNames.Same(p.Manifest, chosen.Project))?.Root;
        (NodePackage? package, NodeRuntime node, IReadOnlyList<string> notes) = await BackgroundWork.Run(async () =>
        {
            NodePackage? read = NodeProjects.Read(top, [chosen.Project]).FirstOrDefault();
            NodeRuntime found = await NodeRuntime.FindAsync(configured);
            // What the package, or its workspace's root, asks of Node.js that this one is not: said first, the script run as its manager runs it.
            IReadOnlyList<string> needs = read is null ? [] : [.. ((string?[])[found.TooOldFor(read.Manifest, read.Name),
                root is null ? null : found.TooOldFor(root, $"{Path.GetFileName(Path.GetDirectoryName(root))}'s workspace")]).OfType<string>().Distinct()];
            return (read, found, needs);
        });
        if (work.Stopping.IsCancellationRequested)
        {
            return Said(StoppedStarting(chosen.Name));
        }
        if (package is null)
        {
            return Said($"{chosen.Name} cannot run: {PathNames.Shown(chosen.Project, session.Folder)} is gone, or does not read as a package.json");
        }
        if (!package.Scripts.Any(s => s.Name == chosen.Script))
        {
            return Said($"{chosen.Name} cannot run: {package.Name}'s package.json has no script named {chosen.Script}");
        }
        IReadOnlyList<string> extra = CommandLine.Split(arguments ?? chosen.Arguments);
        (System.Diagnostics.ProcessStartInfo? start, string? problem) = ScriptRuns.Start(package, chosen.Script, extra, node);
        if (start is null)
        {
            return Said(problem!);
        }
        string running = string.Join("; ", [$"Running {chosen.Name}: {ScriptRuns.Said(package, chosen.Script, extra)} in {PathNames.Shown(package.Folder, session.Folder)}", .. notes]);
        ran = chosen.Name;
        RunProject = package.Folder;
        session.RunPlan = null;
        parts.Views.Run.Fold = null;
        UnfollowWatched();
        Dictionary<string, string> variables = new(chosen.Environment);
        if (chosen.Console == ConsoleKind.Terminal)
        {
            return await RunInTerminalAsync(chosen.Name, start, variables, running);
        }
        parts.Views.Run.Clear();
        ShowInRun(null);
        session.InTerminal = null;
        parts.Views.Run.SetRunning(true);
        parts.Dock.Show(ToolWindowIds.Run);
        foreach (string note in notes)
        {
            parts.Views.Run.Append($"{note}\n");
        }
        parts.Say(running);
        return Started(await session.Runner.StartAsync(start, variables));
    }
}
