using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>What a row of the Containers window stands for.</summary>
public enum ContainerRowKind
{
    /// <summary>A heading: Compose, Aspire, Containers, Testcontainers, Images, Volumes, Networks.</summary>
    Group,
    Project,

    /// <summary>An Aspire AppHost the folder runs, its containers (its orchestrator's) under it.</summary>
    AppHost,
    Service,
    Container,
    Image,
    Volume,
    Network,
}

/// <summary>A row of the Containers window, at its depth: a heading, a Compose project, a service, a container, an image, a
/// volume or a network, with what it stands for. Its key (kind and id) keeps it chosen and expanded across refreshes.</summary>
public sealed class ContainerRow(ContainerRowKind kind, int depth, string key, string label) : ITreeRow
{
    public ContainerRowKind Kind { get; } = kind;

    public int Depth { get; } = depth;

    public string Key { get; } = key;

    public string Label { get; } = label;

    /// <summary>The muted words after the label: an image, a status, a size.</summary>
    public string? Detail { get; init; }

    /// <summary>Whether it has rows under it, and so a chevron.</summary>
    public bool Expands { get; init; }

    public bool Expanded { get; init; }

    bool ITreeRow.IsExpanded => Expanded;

    public ContainerInfo? Container { get; init; }

    public ComposeProject? Project { get; init; }

    /// <summary>A service row's name, with its project in <see cref="Project"/>.</summary>
    public string? Service { get; init; }

    /// <summary>Whether it is a .NET service the folder's container run left unstarted, as its configuration says Don't
    /// start: no container yet, which its Start, Run, Debug or Profile starts.</summary>
    public bool LeftUnstarted { get; init; }

    public EngineObject? Object { get; init; }

    /// <summary>An AppHost row's containers.</summary>
    public IReadOnlyList<ContainerInfo>? Grouped { get; init; }

    /// <summary>The containers it stands for: its own, its service's, its project's or its AppHost's.</summary>
    public IReadOnlyList<ContainerInfo> Containers => Container is { } one ? [one]
        : Project is { } project ? Service is { } service ? [.. project.Services.Where(s => s.Service == service).SelectMany(s => s.Containers)] : [.. project.Containers]
        : Grouped ?? [];

    /// <summary>Whether two ids name one container: the engine's list gives the first 12 hex digits, an orchestrator the whole.</summary>
    public static bool SameId(string one, string other) => one.Length >= 12 && other.Length >= 12
        && (one.StartsWith(other, StringComparison.OrdinalIgnoreCase) || other.StartsWith(one, StringComparison.OrdinalIgnoreCase));

    /// <summary>The row an agent names: a container by its name or id (or the start of one container's id alone), a Compose
    /// project by its name, or one of its services as project/service; null when nothing has that name. The start of
    /// several containers' ids names none of them, an <see cref="ArgumentException"/> listing them: an action names one.</summary>
    public static ContainerRow? Named(ContainerSnapshot snapshot, string name)
    {
        ContainerInfo[] begun = name.Length >= 4 ? [.. snapshot.AllContainers.Where(c => c.Id.StartsWith(name, StringComparison.OrdinalIgnoreCase))] : [];
        ContainerInfo? named = snapshot.AllContainers.FirstOrDefault(c => c.Name == name || c.Name == name.TrimStart('/'));
        if (named is null && begun.Length > 1)
        {
            throw new ArgumentException($"{name} begins the ids of {Numbers.Said(begun.Length)} containers ({string.Join(", ", begun.Select(c => c.Name))}): name one by its name or more of its id");
        }
        if ((named ?? begun.FirstOrDefault()) is { } container)
        {
            return new ContainerRow(ContainerRowKind.Container, 1, $"container:{container.Name}", container.Name) { Container = container };
        }
        string[] parts = name.Split('/', 2);
        if (snapshot.Projects.FirstOrDefault(p => p.Name == parts[0]) is not { } project)
        {
            return null;
        }
        if (parts.Length == 1)
        {
            return new ContainerRow(ContainerRowKind.Project, 1, $"project:{project.Name}", project.Name) { Project = project };
        }
        return project.Services.Any(s => s.Service == parts[1])
            ? new ContainerRow(ContainerRowKind.Service, 2, $"service:{name}", parts[1]) { Project = project, Service = parts[1] }
            : null;
    }

    /// <summary>The name a person and an agent know it by.</summary>
    public string Name => Container?.Name ?? (Service is { } service ? $"{Project!.Name}/{service}" : Project?.Name ?? Object?.Name ?? Label);
}
