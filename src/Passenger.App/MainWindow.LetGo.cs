using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The containers of runs Detach let go of, asked about as the person leaves the folder (quitting, closing the
/// window or the solution, opening another folder here) while any still run: Stop takes them down with the folder, as
/// everything it started goes; Leave Running makes them the person's, which no take-down or clean-up of Passenger's touches
/// from then on (<see cref="ContainerLabels.LeaveToPerson"/>), the Containers window still listing them; Cancel keeps the
/// folder open. A Passenger nobody sees, or an agent's, never asks: they go with the folder.</summary>
public partial class MainWindow
{
    private const string StopThem = "Stop";
    private const string LeaveThemRunning = "Leave Running";

    /// <summary>Whether the window asks the person, closed, about the containers of runs let go of.</summary>
    private bool askingLetGo;

    /// <summary>Whether leaving the folder asks about runs let go of first: some are, and a person is there to answer.</summary>
    private bool AsksOfLetGo => session.RunsLetGo.Count > 0 && !Unseen && !AgentsOwn;

    /// <summary>Asks, where a run let go of still has containers running, whether to stop them or leave them running as the
    /// person's, <paramref name="leaving"/> the folder: true to go on (either answer, or none running), false for Cancel.</summary>
    private async Task<bool> LetGoAskedAsync(string leaving)
    {
        ProjectSession asked = session;
        List<(ContainerRun Run, IReadOnlyList<string> Containers, IReadOnlyList<string> Networks)> up = [];
        foreach (ContainerRun run in asked.RunsLetGo.ToList())
        {
            (IReadOnlyList<string> containers, IReadOnlyList<string> networks) = await BackgroundWork.Run(() => MadeForAsync(run));
            if (containers.Count > 0)
            {
                up.Add((run, containers, networks));
            }
        }
        if (session != asked)
        {
            return false;
        }
        if (up.Count == 0)
        {
            asked.RunsLetGo.Clear();
            return true;
        }
        string names = Plural.Listed([.. up.Select(u => u.Run.Name)]);
        string them = Plural.Agreeing(up.Count, "its", "their");
        string? answer = await Choose("Containers Left Running",
            $"Detach let go of {names}, and {them} containers still run.\n\nStop takes them down, {leaving}, as Passenger takes down all it started. "
            + $"Leave Running leaves them running as yours, beyond Passenger, until you stop them (the Containers window lists them).",
            [StopThem, LeaveThemRunning], StopThem);
        if (answer is null || session != asked)
        {
            return false;
        }
        if (answer == LeaveThemRunning)
        {
            foreach ((ContainerRun run, IReadOnlyList<string> containers, IReadOnlyList<string> networks) in up)
            {
                asked.Containers.EndedProject(run.Plan.Compose);
                ContainerLabels.LeaveToPerson(run.Engine, [.. containers, .. networks]);
            }
        }
        asked.RunsLetGo.Clear();
        return true;
    }

    /// <summary>The containers running, and the networks, this process made for a run's Compose project, by short id; none
    /// where the engine does not answer.</summary>
    private static async Task<(IReadOnlyList<string> Containers, IReadOnlyList<string> Networks)> MadeForAsync(ContainerRun run)
    {
        string[] ours = ["--filter", $"label={ComposeLabels.Project}={run.Plan.ProjectName}", "--filter", $"label={ContainerLabels.Process}={ContainerLabels.ProcessValue}",
            "--filter", $"label={ContainerLabels.Machine}={ContainerLabels.MachineValue}"];
        try
        {
            ChildResult containers = await run.Engine.RunAsync(["ps", "-q", .. ours], ContainerEngine.AskLimit);
            ChildResult networks = await run.Engine.RunAsync(["network", "ls", "-q", .. ours], ContainerEngine.AskLimit);
            return (Ids(containers), Ids(networks));
        }
        catch (Exception unasked) when (ChildFailures.Of(unasked))
        {
            return ([], []);
        }

        static IReadOnlyList<string> Ids(ChildResult said) => said.Succeeded
            ? [.. said.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];
    }

    /// <summary>The window was closed with runs let go of: asked, it closes (a quit it held carrying on) once answered, or
    /// stays open, the quit given up, for Cancel (<see cref="PassengerApp.Answered"/>).</summary>
    private async Task CloseLetGoAsync(bool quit)
    {
        if (askingLetGo)
        {
            return;
        }
        askingLetGo = true;
        ClosesLater = true;
        bool goOn;
        try
        {
            goOn = await LetGoAskedAsync(quit ? "as Passenger quits" : "as the window closes");
        }
        finally
        {
            askingLetGo = false;
            ClosesLater = false;
        }
        if (quit)
        {
            PassengerApp.Answered(carryOn: goOn);
        }
        else if (goOn)
        {
            Close();
        }
    }

    /// <summary>Close Solution once the runs let go of are answered for.</summary>
    private async Task CloseSolutionLetGoAsync()
    {
        if (await LetGoAskedAsync("as the solution closes"))
        {
            CloseSolution();
        }
    }

    /// <summary>Another folder opened here over runs let go of: asked, it opens once answered, else not at all.</summary>
    private async Task OpenLetGoAsync(string path, string folder, Action? opened)
    {
        if (await LetGoAskedAsync($"as {Path.GetFileName(folder)} opens here"))
        {
            await OpenFolder(path, opened: opened);
        }
    }
}
