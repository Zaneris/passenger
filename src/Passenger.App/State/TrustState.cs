using System.Text.Json.Nodes;
using Passenger.Core;

namespace Passenger.App.State;

/// <summary>What the person answered of the folders they opened, in the store's trust.json, every window alike: the
/// folders they trusted, each with every folder under it (<see cref="WorkspaceTrust"/>), and those they opened without
/// trusting, which are not asked of again. Canonical paths.</summary>
public sealed record TrustState : IStateDocument<TrustState>
{
    public IReadOnlyList<string> Trusted { get; init; } = [];

    public IReadOnlyList<string> Untrusted { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public static IReadOnlyList<string> Sets => [nameof(Trusted), nameof(Untrusted)];

    public TrustState Clamped() => this with { Trusted = Folders(Trusted), Untrusted = Folders(Untrusted) };

    /// <summary>Whether the person answered for a folder: it, or one above it, was trusted or opened without trusting.</summary>
    public bool Answered(string folder) => Trusted.Concat(Untrusted).Any(f => PathNames.Within(folder, f));

    /// <summary>The state with a folder trusted: no longer among those opened without trusting, nor any folder under it.</summary>
    public TrustState With(string trusted) => this with
    {
        Trusted = [.. Trusted.Where(f => !PathNames.Within(f, trusted)), trusted],
        Untrusted = [.. Untrusted.Where(f => !PathNames.Within(f, trusted))],
    };

    /// <summary>The state with a folder opened without trusting.</summary>
    public TrustState Declined(string folder) => Untrusted.Any(f => PathNames.Same(f, folder)) ? this : this with { Untrusted = [.. Untrusted, folder] };

    /// <summary>The state with a folder trusted no longer, and opened without trusting from now on.</summary>
    public TrustState StoppedTrusting(string folder) => Forgotten(folder).Declined(folder);

    /// <summary>The state with what the person answered for a folder forgotten, so it is asked of again as it opens.</summary>
    public TrustState Forgotten(string folder) => this with
    {
        Trusted = [.. Trusted.Where(f => !PathNames.Same(f, folder))],
        Untrusted = [.. Untrusted.Where(f => !PathNames.Same(f, folder))],
    };

    private static IReadOnlyList<string> Folders(IReadOnlyList<string>? folders) =>
        [.. (folders ?? []).Where(f => !string.IsNullOrWhiteSpace(f) && Path.IsPathFullyQualified(f)).Distinct(PathNames.Comparer)];
}
