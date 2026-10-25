namespace Passenger.App.Containers;

/// <summary>What one open folder had the engine start, ended with the folder (<see cref="ProjectSession"/>'s end): each
/// container removed and each Compose project taken down. Its <see cref="Labels"/> go on all of it, so what a crash
/// leaves is removed at the next start (<see cref="ContainerLabels"/>). A UI-thread object of the session's.</summary>
public sealed class FolderContainers(string? folder, ContainerEngine engine)
{
    private readonly List<string> containers = [];
    private readonly List<IReadOnlyList<string>> projects = [];

    /// <summary>The engine the folder's window uses.</summary>
    public ContainerEngine Engine { get; } = engine;

    /// <summary>The <c>--label</c> words for what the folder starts: the state's, the process's and the folder's.</summary>
    public IReadOnlyList<string> Labels => ContainerLabels.Arguments(Engine, folder);

    /// <summary>The containers and Compose projects started for the folder and not yet ended.</summary>
    public int Count => containers.Count + projects.Count;

    /// <summary>A container started for the folder, removed with it.</summary>
    public void Started(string container)
    {
        UiThread.Check();
        containers.Add(container);
    }

    /// <summary>A Compose project brought up for the folder (its <c>compose -p NAME -f …</c> words), taken down with it.</summary>
    public void StartedProject(IReadOnlyList<string> compose)
    {
        UiThread.Check();
        projects.Add(compose);
    }

    /// <summary>A container or project ended by other means (its Stop), which the folder's end leaves alone.</summary>
    public void Ended(string container) => containers.Remove(container);

    /// <summary>A Compose project taken down by other means (a run's Stop), which the folder's end leaves alone.</summary>
    public void EndedProject(IReadOnlyList<string> compose)
    {
        UiThread.Check();
        projects.RemoveAll(p => p.SequenceEqual(compose));
    }

    /// <summary>Whether a Compose project (its words) is one the folder started and has yet to end.</summary>
    public bool Holds(IReadOnlyList<string> compose) => projects.Any(p => p.SequenceEqual(compose));

    /// <summary>Ends what the folder started: its containers removed, and of its Compose projects the containers and
    /// networks this process's labels are on (what its own <c>up</c> made, never the person's own of the project, as
    /// <c>down</c> would take), off the UI thread, done once the engine has answered (or given up after its limit). A run's
    /// <c>up</c> still under way, <paramref name="upEnded"/> (ended by the folder's end), is waited for first, and its
    /// projects looked at again a moment after, since the engine can finish a container it was making as Compose ended.</summary>
    public Task EndAsync(Task? upEnded = null)
    {
        string[] removing = [.. containers];
        IReadOnlyList<string>[] downing = [.. projects];
        containers.Clear();
        projects.Clear();
        if (removing.Length == 0 && downing.Length == 0)
        {
            return Task.CompletedTask;
        }
        bool coming = upEnded is { IsCompleted: false };
        // Not held: nothing it does is drawn, and the process waits for it once the window's loop has stopped.
        Task ended = Task.Run(async () =>
        {
            if (coming)
            {
                await Task.WhenAny(upEnded!, Task.Delay(TimeSpan.FromSeconds(10)));
            }
            List<Task> ending = [.. downing.Select(project => TakeDownAsync(Engine, project, folder, "as the folder closed"))];
            if (removing.Length > 0)
            {
                ending.Add(Quietly(Engine, ["rm", "-f", .. removing], "as the folder closed"));
            }
            await Task.WhenAll(ending);
        });
        if (coming)
        {
            // Not waited for by the folder's end, nor held: the IDE's own end removes what its process made should it go first.
            IdeErrors.Observe(ended.ContinueWith(async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await Task.WhenAll(downing.Select(project => TakeDownAsync(Engine, project, folder, "as the folder closed")));
            }, TaskScheduler.Default).Unwrap());
        }
        return ended;
    }

    /// <summary>Takes down what this process's <c>up</c> made of a Compose project (its <c>compose -p NAME …</c> words) for
    /// a folder: the containers (of the services given, else every one) and the networks this machine's, process's and
    /// folder's labels are on, never the person's own of the same project, as <c>docker compose down</c> would take. What fails
    /// is the IDE's error, said with <paramref name="when"/>.</summary>
    public static async Task TakeDownAsync(ContainerEngine engine, IReadOnlyList<string> compose, string? folder, string when, IReadOnlyList<string>? services = null)
    {
        int named = compose.ToList().IndexOf("-p");
        if (named < 0 || named + 1 >= compose.Count)
        {
            return;
        }
        string[] filters = ["--filter", $"label={ComposeLabels.Project}={compose[named + 1]}", "--filter", $"label={ContainerLabels.Machine}={ContainerLabels.MachineValue}",
            "--filter", $"label={ContainerLabels.Process}={ContainerLabels.ProcessValue}",
            .. folder is null ? Array.Empty<string>() : ["--filter", $"label={ContainerLabels.Folder}={ContainerLabels.FolderValue(folder)}"]];
        // Several filters of one label key are any of them to the engine, so each service is asked apart.
        IEnumerable<string[]> asked = services is { Count: > 0 } ? services.Select(s => (string[])[.. filters, "--filter", $"label={ComposeLabels.Service}={s}"]) : [filters];
        foreach (string[] each in asked)
        {
            if (await IdsAsync(["ps", "-aq", .. each]) is { Length: > 0 } made)
            {
                IdeLog.Write(IdeLog.Ide, $"Containers: {made.Length} of {compose[named + 1]}'s containers removed {when}");
                await Quietly(engine, ["rm", "-f", .. made], when);
            }
        }
        if (services is not { Count: > 0 } && await IdsAsync(["network", "ls", "-q", .. filters]) is { Length: > 0 } networks)
        {
            await Quietly(engine, ["network", "rm", .. networks], when);
        }

        async Task<string[]> IdsAsync(IReadOnlyList<string> words)
        {
            try
            {
                Core.ChildResult listed = await engine.RunAsync(words, ContainerEngine.AskLimit);
                return listed.Succeeded ? listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
            }
            catch (Exception failed) when (failed is TimeoutException or FileNotFoundException or System.ComponentModel.Win32Exception)
            {
                IdeErrors.Write($"Containers: {ContainerEngine.Words(words)} failed {when}: {failed.Message}");
                return [];
            }
        }
    }

    /// <summary>Removes containers by id, as the folder's end removes its own: what fails is the IDE's error, said with
    /// <paramref name="when"/>. Whether the engine removed them. Any thread.</summary>
    public static Task<bool> RemoveAsync(ContainerEngine engine, IReadOnlyList<string> ids, string when) =>
        ids.Count == 0 ? Task.FromResult(true) : Quietly(engine, ["rm", "-f", .. ids], when);

    private static async Task<bool> Quietly(ContainerEngine engine, IReadOnlyList<string> words, string when)
    {
        try
        {
            Core.ChildResult result = await engine.RunAsync(words, ContainerEngine.AskLimit * 3);
            if (!result.Succeeded)
            {
                IdeErrors.Write($"Containers: {ContainerEngine.Words(words)} failed {when}: {ContainerEngine.Said(result)}");
            }
            return result.Succeeded;
        }
        catch (Exception failed) when (failed is TimeoutException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            IdeErrors.Write($"Containers: {ContainerEngine.Words(words)} failed {when}: {failed.Message}");
            return false;
        }
    }
}
