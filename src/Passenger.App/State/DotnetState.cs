using System.Text.Json.Nodes;
using Passenger.Core;

namespace Passenger.App.State;

/// <summary>What the person said of the machine's .NET, in the store's dotnet.json, every folder alike: the installations
/// whose word that the shell's PATH does not name them they hid, each by its folder, so it stays hidden there until another
/// installation is the one found.</summary>
public sealed record DotnetState : IStateDocument<DotnetState>
{
    public IReadOnlyList<string> HiddenOffPath { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public DotnetState Clamped() => this with { HiddenOffPath = [.. (HiddenOffPath ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(PathNames.Comparer)] };

    /// <summary>Whether the person hid the off-PATH word of an installation.</summary>
    public bool Hides(string root) => HiddenOffPath.Any(p => PathNames.Same(p, root));
}
