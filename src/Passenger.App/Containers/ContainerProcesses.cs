using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>The .NET programs in the engine's running containers, as Attach to Process lists them under each container. A
/// container with Passenger's tools in it (mounted where Passenger started it, or copied in before) says them itself, by
/// its own process ids (<see cref="ContainerTools.ProcessesAsync"/>); one without is read from outside by <c>docker top</c>,
/// which needs nothing of the image, its .NET programs told by their command lines (the dotnet host, a .dll) or, in an
/// image of .NET's (DOTNET_RUNNING_IN_CONTAINER), by being no shell: their ids there are known once the tools, copied in
/// as the person attaches, say them. Off the UI thread.</summary>
public static class ContainerProcesses
{
    /// <summary>What the dialog says under a container that has no tools in it yet: that attaching copies them in.</summary>
    public const string CopiesTools = "attaching copies Passenger's container tools into it (" + ContainerTools.Copied + ")";

    /// <summary>Programs in a container's processes that are no .NET program of the person's.</summary>
    private static readonly HashSet<string> NotDotnet = new(StringComparer.Ordinal)
    {
        "sh", "bash", "ash", "dash", "zsh", "sleep", "tail", "cat", "tini", "dumb-init", "docker-init", "s6-svscan", "supervisord", "passenger-tools",
    };

    /// <summary>Every running container's .NET programs; none when there is no CLI or the engine is not running.</summary>
    public static async Task<IReadOnlyList<DotnetProcess>> ListAsync(ContainerEngine engine, CancellationToken cancel)
    {
        if (engine.Cli is null || !(await engine.StateAsync(cancel: cancel)).IsReady)
        {
            return [];
        }
        IReadOnlyList<JsonElement> running = await engine.JsonLinesAsync(["ps", "--format", "json"], cancel);
        string[] ids = [.. running.Select(ContainerInfo.IdOf).OfType<string>()];
        if (ids.Length == 0)
        {
            return [];
        }
        string engineArchitecture = await ContainerTools.EngineArchitectureAsync(engine, cancel);
        // Each asked on its own: one that ended since it was listed leaves the others listed.
        IEnumerable<Task<IReadOnlyList<DotnetProcess>>> each = ids.Select(id => OfAsync(engine, id, engineArchitecture, cancel));
        return [.. (await Task.WhenAll(each)).SelectMany(p => p).OrderBy(p => p.Container, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>One container's .NET programs; none for one that ended meanwhile or would not be described.</summary>
    private static async Task<IReadOnlyList<DotnetProcess>> OfAsync(ContainerEngine engine, string id, string engineArchitecture, CancellationToken cancel)
    {
        string name = id;
        try
        {
            JsonElement inspected = await engine.JsonAsync(["inspect", "--type", "container", id], cancel);
            JsonElement container = inspected.ValueKind == JsonValueKind.Array && inspected.GetArrayLength() > 0 ? inspected[0] : default;
            name = (JsonMembers.Scalar(container, "Name") ?? id).TrimStart('/');
            bool dotnetImage = container.ValueKind == JsonValueKind.Object && container.TryGetProperty("Config", out JsonElement config)
                && config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Env", out JsonElement env) && env.ValueKind == JsonValueKind.Array
                && env.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() is { } variable
                    && (variable == "DOTNET_RUNNING_IN_CONTAINER=true" || variable.StartsWith("DOTNET_VERSION=", StringComparison.Ordinal)));
            IReadOnlyList<DotnetProcess> outside = await TopAsync(engine, name, dotnetImage, cancel);
            if (outside.Count == 0)
            {
                return [];
            }
            ContainerSystem system = await ContainerTools.SystemOfContainerAsync(engine, name, cancel);
            string? emulated = system.Architecture == engineArchitecture ? null : ContainerTools.Emulation(system.Architecture, engineArchitecture);
            IReadOnlyList<DotnetProcess> found = await ContainerTools.InContainerAsync(engine, name, cancel) is { } tools
                ? await ContainerTools.ProcessesAsync(engine, name, tools, cancel)
                : outside;
            return [.. found.Select(p => emulated is null ? p : p with { Note = ContainerTools.Noted(p.Note, emulated) })];
        }
        catch (Exception unread) when (unread is ContainerCommandException or ContainerToolsException or JsonException or TimeoutException)
        {
            // A container that ended meanwhile, or one the engine would not describe: not listed.
            IdeLog.Write(IdeLog.Debugger, $"Attach to Process could not list the programs in container {name}: {unread.Message}");
            return [];
        }
    }

    /// <summary>A container's .NET programs as <c>docker top</c> shows them from outside: their ids there unknown (0).</summary>
    private static async Task<IReadOnlyList<DotnetProcess>> TopAsync(ContainerEngine engine, string name, bool dotnetImage, CancellationToken cancel)
    {
        ChildResult top = await engine.RunAsync(["top", name, "-eo", "user,args"], cancel: cancel);
        if (!top.Succeeded)
        {
            top = await engine.RunAsync(["top", name], cancel: cancel);
        }
        if (!top.Succeeded)
        {
            return [];
        }
        string[] lines = top.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return [];
        }
        string[] header = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int user = Array.FindIndex(header, h => h is "USER" or "UID");
        int command = Array.FindIndex(header, h => h is "COMMAND" or "CMD");
        List<DotnetProcess> found = [];
        foreach (string line in lines.Skip(1))
        {
            string[] cells = line.Split(' ', command + 1, StringSplitOptions.RemoveEmptyEntries);
            if (command < 0 || cells.Length <= command)
            {
                continue;
            }
            string commandLine = cells[command].Trim();
            string executable = Path.GetFileName(commandLine.Split(' ', 2)[0]);
            bool hosted = executable == "dotnet" || commandLine.Contains(".dll", StringComparison.OrdinalIgnoreCase);
            if (!hosted && (!dotnetImage || NotDotnet.Contains(executable)))
            {
                continue;
            }
            found.Add(new DotnetProcess(0, executable, commandLine, user >= 0 ? cells[user] : string.Empty, true) { Container = name });
        }
        return found;
    }
}
