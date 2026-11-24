using System.Data.Common;
using System.Text.Json.Nodes;
using Passenger.App.Containers;
using Passenger.App.State;

namespace Passenger.App.Database;

/// <summary>A folder's database connections made by hand, without their secrets (kept in the system's credential store,
/// <see cref="DatabaseSecrets"/>), and the connection each console runs against.</summary>
public sealed record DatabaseState : IStateDocument<DatabaseState>
{
    public List<KeptConnection> Connections { get; init; } = [];

    /// <summary>Each console's file and the id of the connection it runs against.</summary>
    public Dictionary<string, Guid> Consoles { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public DatabaseState Clamped() => this with
    {
        Connections = [.. (Connections ?? []).Where(c => c is { Name.Length: > 0 } && Enum.IsDefined(c.Kind)).DistinctBy(c => c.Id).Take(200)],
        Consoles = (Consoles ?? []).Where(c => c.Key.Length > 0).Take(500).ToDictionary(),
    };
}

/// <summary>A connection made by hand, its connection string with every secret part taken out.</summary>
public sealed record KeptConnection(Guid Id, string Name, DatabaseKind Kind, string ConnectionString)
{
    /// <summary>A connection's string split in two: what may be kept in a file, and its secret parts as a connection string.</summary>
    public static (string Kept, string Secret) Split(DatabaseKind kind, string connection)
    {
        DbConnectionStringBuilder kept = new() { ConnectionString = connection };
        DbConnectionStringBuilder secret = new();
        foreach (string key in kept.Keys.Cast<string>().ToList())
        {
            if (ContainerSecrets.Secret(key) && kept[key] is not (null or ""))
            {
                secret[key] = kept[key];
                kept.Remove(key);
            }
        }
        return (DatabaseKinds.Builder(kind, kept.ConnectionString).ConnectionString, secret.ConnectionString);
    }

    /// <summary>A kept connection's string with its secret parts put back.</summary>
    public static string Joined(DatabaseKind kind, string kept, string? secret)
    {
        DbConnectionStringBuilder joined = new() { ConnectionString = kept };
        if (!string.IsNullOrEmpty(secret))
        {
            DbConnectionStringBuilder parts = new() { ConnectionString = secret };
            foreach (string key in parts.Keys)
            {
                joined[key] = parts[key];
            }
        }
        return DatabaseKinds.Builder(kind, joined.ConnectionString).ConnectionString;
    }
}
