using System.Buffers;
using System.Data.Common;
using System.Globalization;
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

    /// <summary>Where the person left a diagram's boxes, by its connection, database and schema (<see cref="DatabaseController.DiagramKey"/>) and table.</summary>
    public Dictionary<string, Dictionary<string, DiagramSpot>> Diagrams { get; init; } = [];

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
            .Select(c => Usable(c.Tunnel) ? c : c with { Tunnel = null })
            .Select(c => CloudSignIns.Usable(c.SignIn) ? c : c with { SignIn = null }).DistinctBy(c => c.Id).Take(200)],
        Consoles = (Consoles ?? []).Where(c => c.Key.Length > 0).Take(500).ToDictionary(),
        ManualCommit = [.. (ManualCommit ?? []).Where(p => p is { Length: > 0 }).Distinct().Take(500)],
        FoundMarks = (FoundMarks ?? []).Where(m => m.Key.Length > 0 && m.Value != DatabaseMark.None && Enum.IsDefined(m.Value)).Take(500).ToDictionary(),
        FoundSettings = (FoundSettings ?? []).Where(f => f.Key.Length > 0 && f.Value is not null)
            .Select(f => KeyValuePair.Create(f.Key, f.Value with
            {
                Over = f.Value.Over ?? string.Empty,
                Tunnel = Usable(f.Value.Tunnel) ? f.Value.Tunnel : null,
                SignIn = CloudSignIns.Usable(f.Value.SignIn) ? f.Value.SignIn : null,
            }))
            .Where(f => f.Value.Over.Length > 0 || f.Value.Tunnel is not null || f.Value.SignIn is not null).Take(500).ToDictionary(),
        Diagrams = (Diagrams ?? []).Where(d => d.Key.Length > 0 && d.Value is not null).Take(200)
            .ToDictionary(d => d.Key, d => d.Value.Where(s => s.Key.Length > 0 && s.Value is { X: var x, Y: var y } && double.IsFinite(x) && double.IsFinite(y)).Take(1000).ToDictionary()),
        Parameters = (Parameters ?? []).Where(p => p.Key.Length > 0 && p.Value is not null).Take(500)
            .ToDictionary(p => p.Key, p => p.Value.Where(v => v.Key.Length > 0 && v.Value is not null).Take(100).ToDictionary()),
        History = (History ?? []).Where(h => h.Key.Length > 0 && h.Value is not null).Take(200)
            .ToDictionary(h => h.Key, h => h.Value.Where(r => r is { Statement.Length: > 0 and <= LongestRun, Outcome: not null }).Take(MostRuns).ToList()),
    };
}

/// <summary>A connection the folder's files name given SSL settings of its own (connection string keys laid over its own,
/// without their secrets), a tunnel and a cloud sign-in.</summary>
public sealed record FoundSetting(string Over, SshTunnel? Tunnel, CloudSignIn? SignIn = null);

/// <summary>Where the person left a table's box in a diagram, in the diagram's units.</summary>
public sealed record DiagramSpot(double X, double Y);

/// <summary>A statement or batch run on a connection, when, and what it came to.</summary>
public sealed record SqlRan(string Statement, DateTime At, string Outcome, bool Failed = false);

/// <summary>A connection made by hand, its connection string with every secret part taken out, its mark, its tunnel and its
/// cloud sign-in.</summary>
public sealed record KeptConnection(Guid Id, string Name, DatabaseKind Kind, string ConnectionString, DatabaseMark Mark = DatabaseMark.None, SshTunnel? Tunnel = null,
    CloudSignIn? SignIn = null)
{
    /// <summary>What a secret held inside another part's value is kept under: MongoDB's URI's password as "Uri password".</summary>
    private const string Inside = " password";

    /// <summary>A connection's string split in two: what may be kept in a file, and its secret parts as a connection string,
    /// a password inside a part's value too (a URL's user's, StackExchange.Redis's configuration's password=) under
    /// <see cref="Inside"/>.</summary>
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
            else if (Convert.ToString(kept[key], CultureInfo.InvariantCulture) is { } value && PasswordIn(key, value) is { } inside)
            {
                kept[key] = inside.Without;
                secret[key + Inside] = inside.Password;
            }
        }
        return (DatabaseKinds.Builder(kind, kept.ConnectionString).ConnectionString, secret.ConnectionString);
    }

    /// <summary>A kept connection's string with its secret parts put back, a password kept from inside a part's value put
    /// back inside it.</summary>
    public static string Joined(DatabaseKind kind, string kept, string? secret)
    {
        DbConnectionStringBuilder joined = new() { ConnectionString = kept };
        if (!string.IsNullOrEmpty(secret))
        {
            DbConnectionStringBuilder parts = new() { ConnectionString = secret };
            foreach (string key in parts.Keys)
            {
                string value = Convert.ToString(parts[key], CultureInfo.InvariantCulture) ?? string.Empty;
                string outer = key.EndsWith(Inside, StringComparison.OrdinalIgnoreCase) ? key[..^Inside.Length] : string.Empty;
                if (outer.Length > 0 && joined.TryGetValue(outer, out object? held) && Convert.ToString(held, CultureInfo.InvariantCulture) is { } within && HoldsPassword(outer, within))
                {
                    joined[outer] = WithPassword(outer, within, value);
                }
                else
                {
                    joined[key] = value;
                }
            }
        }
        return DatabaseKinds.Builder(kind, joined.ConnectionString).ConnectionString;
    }

    /// <summary>The password a part's value holds inside it, and the value without it: a URL's user's (mongodb://app:pw@host,
    /// as written there), or StackExchange.Redis's configuration's password= part; null where it holds none.</summary>
    private static (string Without, string Password)? PasswordIn(string key, string value)
    {
        if (key.Equals("Configuration", StringComparison.OrdinalIgnoreCase) && !value.Contains("://", StringComparison.Ordinal))
        {
            string[] options = value.Split(',');
            int at = Array.FindIndex(options, o => o.Trim().StartsWith("password=", StringComparison.OrdinalIgnoreCase));
            return at < 0 || options[at].Trim().Length == "password=".Length ? null
                : (string.Join(',', options.Where((_, i) => i != at)), options[at].Trim()["password=".Length..]);
        }
        if (UserInfo(value) is not { } user)
        {
            return null;
        }
        int colon = value.IndexOf(':', user.Start, user.End - user.Start);
        return colon < 0 || colon + 1 == user.End ? null : (value[..colon] + value[user.End..], value[(colon + 1)..user.End]);
    }

    /// <summary>A part's value with a kept password put back inside it, where it has a user and no password of its own.</summary>
    private static string WithPassword(string key, string value, string password)
    {
        if (key.Equals("Configuration", StringComparison.OrdinalIgnoreCase) && !value.Contains("://", StringComparison.Ordinal))
        {
            return value.Split(',').Any(o => o.Trim().StartsWith("password=", StringComparison.OrdinalIgnoreCase)) ? value : $"{value},password={password}";
        }
        return UserInfo(value) is { } user && value.IndexOf(':', user.Start, user.End - user.Start) < 0
            ? value[..user.End] + ":" + password + value[user.End..] : value;
    }

    /// <summary>Where a URL's user (and its password) stand, from after its scheme's // to its @; null where it names none.</summary>
    private static (int Start, int End)? UserInfo(string value)
    {
        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0 || value.AsSpan(0, scheme).ContainsAnyExcept(SchemeCharacters))
        {
            return null;
        }
        int from = scheme + 3;
        int end = value.IndexOfAny(['/', '?', '#'], from) is var stop and >= 0 ? stop : value.Length;
        int at = end > from ? value.LastIndexOf('@', end - 1, end - from) : -1;
        return at > from ? (from, at) : null;
    }

    /// <summary>Whether a part's value is one a password is kept from inside of: StackExchange.Redis's configuration, a URL
    /// naming a user.</summary>
    private static bool HoldsPassword(string key, string value) =>
        key.Equals("Configuration", StringComparison.OrdinalIgnoreCase) && !value.Contains("://", StringComparison.Ordinal) || UserInfo(value) is not null;

    private static readonly SearchValues<char> SchemeCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-.");
}
