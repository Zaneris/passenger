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

    /// <summary>The consoles that run in manual commit, by their files.</summary>
    public List<string> ManualCommit { get; init; } = [];

    /// <summary>The marks of connections the folder's files name, by <see cref="DatabaseSource.FoundKey"/>.</summary>
    public Dictionary<string, DatabaseMark> FoundMarks { get; init; } = [];

    /// <summary>The SSL and tunnels given connections the folder's files name, by <see cref="DatabaseSource.FoundKey"/>.</summary>
    public Dictionary<string, FoundSetting> FoundSettings { get; init; } = [];

    /// <summary>The values each console's parameters were last given, by its file and the parameter's name.</summary>
    public Dictionary<string, Dictionary<string, string>> Parameters { get; init; } = [];

    /// <summary>What ran on each connection, newest first, by <see cref="DatabaseSource.HistoryKey"/>.</summary>
    public Dictionary<string, List<SqlRan>> History { get; init; } = [];

    /// <summary>The most runs kept for a connection, and the longest statement kept whole.</summary>
    public const int MostRuns = 500;

    public const int LongestRun = 20_000;

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>Whether a kept tunnel names a host, a port in range (0 for ssh's configuration's) and its keys.</summary>
    private static bool Usable(SshTunnel? tunnel) => tunnel is { Host.Length: > 0, Port: >= 0 and < 65536, User: not null, HostKeys: not null };

    public DatabaseState Clamped() => this with
    {
        Connections = [.. (Connections ?? []).Where(c => c is { Name.Length: > 0 } && Enum.IsDefined(c.Kind))
            .Select(c => Enum.IsDefined(c.Mark) ? c : c with { Mark = DatabaseMark.None })
            .Select(c => Usable(c.Tunnel) ? c : c with { Tunnel = null }).DistinctBy(c => c.Id).Take(200)],
        Consoles = (Consoles ?? []).Where(c => c.Key.Length > 0).Take(500).ToDictionary(),
        ManualCommit = [.. (ManualCommit ?? []).Where(p => p is { Length: > 0 }).Distinct().Take(500)],
        FoundMarks = (FoundMarks ?? []).Where(m => m.Key.Length > 0 && m.Value != DatabaseMark.None && Enum.IsDefined(m.Value)).Take(500).ToDictionary(),
        FoundSettings = (FoundSettings ?? []).Where(f => f.Key.Length > 0 && f.Value is not null)
            .Select(f => KeyValuePair.Create(f.Key, f.Value with { Over = f.Value.Over ?? string.Empty, Tunnel = Usable(f.Value.Tunnel) ? f.Value.Tunnel : null }))
            .Where(f => f.Value.Over.Length > 0 || f.Value.Tunnel is not null).Take(500).ToDictionary(),
        Parameters = (Parameters ?? []).Where(p => p.Key.Length > 0 && p.Value is not null).Take(500)
            .ToDictionary(p => p.Key, p => p.Value.Where(v => v.Key.Length > 0 && v.Value is not null).Take(100).ToDictionary()),
        History = (History ?? []).Where(h => h.Key.Length > 0 && h.Value is not null).Take(200)
            .ToDictionary(h => h.Key, h => h.Value.Where(r => r is { Statement.Length: > 0 and <= LongestRun, Outcome: not null }).Take(MostRuns).ToList()),
    };
}

/// <summary>A connection the folder's files name given SSL settings of its own (connection string keys laid over its own,
/// without their secrets) and a tunnel.</summary>
public sealed record FoundSetting(string Over, SshTunnel? Tunnel);

/// <summary>A statement or batch run on a connection, when, and what it came to.</summary>
public sealed record SqlRan(string Statement, DateTime At, string Outcome, bool Failed = false);

/// <summary>A connection made by hand, its connection string with every secret part taken out, its mark and its tunnel.</summary>
public sealed record KeptConnection(Guid Id, string Name, DatabaseKind Kind, string ConnectionString, DatabaseMark Mark = DatabaseMark.None, SshTunnel? Tunnel = null)
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
