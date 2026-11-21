using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Templates;

/// <summary>The templates New Solution and New Project made something from lately, by the short name a template is known
/// by across reads, with when: what their lists show under Recent, the newest first. Kept by name rather than as a list,
/// so two windows' Creates merge entry by entry.</summary>
public sealed record RecentTemplatesState : IStateDocument<RecentTemplatesState>
{
    /// <summary>How many are kept, the oldest dropped past it.</summary>
    public const int Limit = 5;

    public Dictionary<string, DateTimeOffset> Used { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>The templates' names, the newest first.</summary>
    public IReadOnlyList<string> Newest() => [.. Used.OrderByDescending(u => u.Value).ThenBy(u => u.Key, StringComparer.Ordinal).Select(u => u.Key)];

    /// <summary>With a template made from now, newest of all even on a clock that has not moved since the last.</summary>
    public RecentTemplatesState Made(string template, DateTimeOffset now)
    {
        DateTimeOffset latest = Used.Values.DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        return this with { Used = Kept(new(Used) { [template] = now > latest ? now : latest.AddTicks(1) }) };
    }

    /// <summary>An entry without a name is left out, and only the newest are kept.</summary>
    public RecentTemplatesState Clamped() => this with
    {
        Used = Kept((Used ?? []).Where(u => !string.IsNullOrEmpty(u.Key)).ToDictionary(u => u.Key, u => u.Value)),
    };

    private static Dictionary<string, DateTimeOffset> Kept(Dictionary<string, DateTimeOffset> all) =>
        all.Count <= Limit ? all : all.OrderByDescending(u => u.Value).Take(Limit).ToDictionary(u => u.Key, u => u.Value);
}
