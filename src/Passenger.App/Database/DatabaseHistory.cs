using System.Text.Json.Nodes;
using Passenger.App.State;
using Passenger.App.Containers;

namespace Passenger.App.Database;

public sealed record DatabaseHistory : IStateDocument<DatabaseHistory>
{
    public List<string> Queries { get; init; } = [];
    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public DatabaseHistory Clamped() => this with
    {
        Queries = [.. (Queries ?? []).Where(q => !string.IsNullOrWhiteSpace(q) && q.Length <= 16000).Select(q => ContainerSecrets.InSaid(q)).Distinct().Take(100)],
    };
}
