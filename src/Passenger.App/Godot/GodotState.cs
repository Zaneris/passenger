using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Godot;

/// <summary>What a folder keeps of its Godot games: whether their audio is muted as their debugging starts.</summary>
public sealed record GodotState : IStateDocument<GodotState>
{
    /// <summary>Whether a game debugged here starts with its audio muted, as the person last left Mute Game Audio.</summary>
    public bool AudioMuted { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public GodotState Clamped() => this;
}
