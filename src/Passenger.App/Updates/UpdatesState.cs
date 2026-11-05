using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Updates;

/// <summary>What the update check keeps: the newest release's version it has said is out, so each newer version is
/// announced once, however often Passenger starts while it stays the latest; the version a session closed to install,
/// so the next start says whether it did, and why it did not where the install said; and the version whose notes What's
/// New showed last (or a fresh state began on), so a newer one shows its own once.</summary>
public sealed record UpdatesState : IStateDocument<UpdatesState>
{
    public string? Announced { get; init; }

    public string? Installing { get; init; }

    /// <summary>Why the version a session closed to install was not installed, where the install could say.</summary>
    public string? NotInstalled { get; init; }

    /// <summary>The version whose release notes What's New showed last, or the one a fresh state began on; null in a state
    /// a Passenger before What's New kept, which counts as an older version's.</summary>
    public string? NotesShown { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public UpdatesState Clamped() => this with
    {
        Announced = string.IsNullOrWhiteSpace(Announced) ? null : Announced.Trim(),
        Installing = string.IsNullOrWhiteSpace(Installing) ? null : Installing.Trim(),
        NotInstalled = string.IsNullOrWhiteSpace(NotInstalled) ? null : NotInstalled.Trim(),
        NotesShown = string.IsNullOrWhiteSpace(NotesShown) ? null : NotesShown.Trim(),
    };
}
