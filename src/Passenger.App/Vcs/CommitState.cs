using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Vcs;

/// <summary>How a folder's commits are made beyond their files and message: whether they are signed off.</summary>
public sealed record CommitState : IStateDocument<CommitState>
{
    /// <summary>Whether a commit made here is signed off (git commit --signoff), as the Commit window's Sign-off was left.</summary>
    public bool SignOff { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public CommitState Clamped() => this;
}
