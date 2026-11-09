namespace Passenger.App.Containers;

/// <summary>The <c>docker</c> words each of the Containers window's actions runs on a row, or why it cannot: a container's
/// own command, Compose's for a project or one of its services, and an image's, volume's or network's. The window's actions
/// and an agent's <c>container_action</c> both take them from here.</summary>
public static class ContainerCommands
{
    /// <summary>The actions an agent may name, and what each runs.</summary>
    public static readonly IReadOnlyList<string> AgentActions = ["start", "stop", "restart", "remove"];

    /// <summary>The words for an action on a row, or why it cannot run there.</summary>
    public static (IReadOnlyList<string>? Words, string? WhyNot) For(string action, ContainerRow? row)
    {
        if (row is null || row.Kind == ContainerRowKind.Group)
        {
            return (null, "No container, project, image, volume or network is chosen in the Containers window");
        }
        // No container yet: its Start goes through the run that left it unstarted, which starts it.
        if (row.LeftUnstarted && action != "start")
        {
            return (null, NotStarted(row));
        }
        if (row.Project is { } project)
        {
            List<string> words = [.. project.Arguments];
            // A service's name is a label's, never read as a flag (a "-v" would have rm remove its volumes).
            string[] services = row.Service is { } service ? ["--", service] : [];
            return action switch
            {
                "start" => ([.. words, "start", .. services], null),
                "stop" => ([.. words, "stop", .. services], null),
                "restart" => ([.. words, "restart", .. services], null),
                "pause" => (row.Containers.All(c => c.IsPaused) ? [.. words, "unpause", .. services] : [.. words, "pause", .. services], null),
                "remove" => services.Length > 0 ? ([.. words, "rm", "-f", "-s", .. services], null) : ([.. words, "down"], null),
                "pull" => project.ConfigFiles.Any(File.Exists) ? ([.. words, "pull", .. services], null) : (null, $"{project.Name}'s compose files are not on this computer"),
                _ => (null, null),
            };
        }
        if (row.Container is { } container)
        {
            return action switch
            {
                "start" => (["start", container.Id], null),
                "stop" => (["stop", container.Id], null),
                "restart" => (["restart", container.Id], null),
                "pause" => (container.IsPaused ? ["unpause", container.Id] : ["pause", container.Id], container.IsRunning || container.IsPaused ? null : Refusals.NotRunning(container.Name)),
                "remove" => (["rm", "-f", container.Id], null),
                "pull" => (["pull", container.Image], null),
                _ => (null, null),
            };
        }
        if (row.Object is { } thing)
        {
            return (action, thing.Kind) switch
            {
                ("remove", "image") => (["rmi", thing.Id], null),
                ("remove", "volume") => (["volume", "rm", thing.Name], null),
                ("remove", "network") => (["network", "rm", thing.Id], null),
                ("pull", "image") => thing.Name.Contains(':', StringComparison.Ordinal) || !thing.Name.All(char.IsAsciiHexDigitLower)
                    ? (["pull", thing.Name], null) : (null, "An image with no name has nothing to pull"),
                _ => (null, $"A {thing.Kind} cannot {action}"),
            };
        }
        return (null, null);
    }

    /// <summary>How a service row its run left unstarted says what starts it.</summary>
    public static string NotStarted(ContainerRow row) => Refusals.NotStarted(row.Name, ContainerRun.StartsService);

    /// <summary>The words a stage of the action says while it runs ("Stopping web…").</summary>
    public static string Stage(string action, ContainerRow row) => action switch
    {
        "start" => $"Starting {row.Name}…",
        "stop" => $"Stopping {row.Name}…",
        "restart" => $"Restarting {row.Name}…",
        "pause" => row.Containers.All(c => c.IsPaused) ? $"Unpausing {row.Name}…" : $"Pausing {row.Name}…",
        "remove" => $"Removing {row.Name}…",
        "pull" => $"Pulling {row.Container?.Image ?? row.Name}…",
        _ => $"{action} {row.Name}…",
    };

    /// <summary>What the prune of a heading or an object's kind removes, in the question that asks first, and its words.</summary>
    public static (string What, IReadOnlyList<string> Words) Prune(string kind) => kind switch
    {
        "image" or "Images" => ("every dangling image", ["image", "prune", "-f"]),
        "volume" or "Volumes" => ("every volume no container uses", ["volume", "prune", "-f"]),
        "network" or "Networks" => ("every network no container uses", ["network", "prune", "-f"]),
        _ => ("every stopped container", ["container", "prune", "-f"]),
    };
}
