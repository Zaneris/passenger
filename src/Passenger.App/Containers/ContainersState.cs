using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Containers;

/// <summary>The store's <c>containers.json</c>: whether Passenger has ever labelled something an engine made for this
/// state folder, without which a start runs no docker command to look for what a crash left; and what the person chose to
/// leave running as theirs as Passenger quit or a folder closed, by its id, which no take-down or clean-up of Passenger's
/// touches.</summary>
public sealed record ContainersState : IStateDocument<ContainersState>
{
    public bool Labelled { get; init; }

    /// <summary>The containers and networks left running as the person's (<see cref="ContainerLabels.LeaveToPerson"/>), by
    /// the short id the engine lists them by.</summary>
    public IReadOnlyList<string> Left { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public ContainersState Clamped() => this;
}
