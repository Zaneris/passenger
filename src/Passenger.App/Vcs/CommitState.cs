using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Vcs;

/// <summary>How a folder's commits are made beyond their files: whether they are signed off, whether the Commit window
/// lists the files under their folders, and the messages committed from it in each repository the folder holds.</summary>
public sealed record CommitState : IStateDocument<CommitState>
{
    /// <summary>The most messages kept for a repository, the most repositories, and the longest message kept.</summary>
    public const int MostMessages = 50;

    public const int MostRepositories = 50;

    public const int LongestMessage = 20_000;

    /// <summary>Whether a commit made here is signed off (git commit --signoff), as the Commit window's Sign-off was left.</summary>
    public bool SignOff { get; init; }

    /// <summary>Whether the Commit window lists its files under their folders, as its Group By menu was left.</summary>
    public bool GroupsByDirectory { get; init; }

    /// <summary>The messages committed from the Commit window, newest first, each once, by the repository's top folder.</summary>
    public Dictionary<string, List<string>> Messages { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public CommitState Clamped() => this with
    {
        Messages = (Messages ?? []).Where(m => m.Key.Length > 0 && m.Value is not null).Take(MostRepositories)
            .ToDictionary(m => m.Key, m => m.Value.Where(t => t is { Length: > 0 and <= LongestMessage }).Distinct(StringComparer.Ordinal).Take(MostMessages).ToList()),
    };
}
