using System.Data.Common;
using System.Globalization;

namespace Passenger.Workspace;

/// <summary>The server a connection string names, as written: a URL's host and port (<c>redis://:secret@cache:6379</c>), or a
/// keyword string's (<c>Host=db;Port=5432</c>, SQL Server's <c>Server=tcp:db,1433</c>), its port null where it names none
/// (its server's default). <see cref="At"/> gives the string again at another host and port, nothing else changed: a URL's
/// text with its host and port replaced, a keyword string read and written again by <see cref="DbConnectionStringBuilder"/>
/// with only its host's and port's values set.</summary>
public sealed class ConnectionServer
{
    /// <summary>The keywords a connection string names its host by, in the providers' spellings.</summary>
    private static readonly string[] HostKeys = ["host", "server", "data source", "address", "addr", "network address"];

    private readonly Func<string, int, bool, string>? moved;

    private ConnectionServer(string host, int? port, Func<string, int, bool, string>? moved)
    {
        Host = host;
        Port = port;
        this.moved = moved;
    }

    /// <summary>The host, as written.</summary>
    public string Host { get; }

    /// <summary>The port, as written; null where the string names none.</summary>
    public int? Port { get; }

    /// <summary>The server a connection string names; null for a string that names none (a file's, a keyword string with
    /// no host, a URL with no host or a port that is no number).</summary>
    public static ConnectionServer? Of(string value) => OfUrl(value) ?? OfKeywords(value);

    /// <summary>The string at another host and port, the rest as it was; a port the string did not name is written after
    /// the host and a comma where <paramref name="besideHost"/> (SQL Server's), else as a Port keyword. Null where the
    /// string cannot be written again: a keyword string <see cref="DbConnectionStringBuilder"/> cannot read.</summary>
    public string? At(string host, int port, bool besideHost = false) => moved?.Invoke(host, port, besideHost);

    /// <summary>A URL's server: its authority's host and port, after any user and password, which the text keeps.</summary>
    private static ConnectionServer? OfUrl(string value)
    {
        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        int begins = value.Length - value.TrimStart().Length;
        if (scheme <= begins || !char.IsAsciiLetter(value[begins]) || !value[begins..scheme].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '.' or '-'))
        {
            return null;
        }
        int authority = scheme + 3;
        int end = value.IndexOfAny(['/', '?', '#'], authority) is int path and >= 0 ? path : Math.Max(authority, value.TrimEnd().Length);
        int at = value.LastIndexOf('@', end - 1, end - authority) is int user and >= 0 ? user + 1 : authority;
        // An IPv6 address's colons are inside its brackets.
        int named = at < end && value[at] == '[' ? value.IndexOf(']', at, end - at) + 1 : at;
        int colon = named > 0 && value.IndexOf(':', named, end - named) is int found and >= 0 ? found : end;
        string host = value[at..colon];
        string written = colon < end ? value[(colon + 1)..end] : string.Empty;
        int? port = written.Length == 0 ? null
            : int.TryParse(written, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number is > 0 and <= 65535 ? number : 0;
        if (named == 0 || host.Length == 0 || port == 0 || host.AsSpan().ContainsAny(", "))
        {
            return null;
        }
        return new ConnectionServer(host, port, (to, toPort, _) => $"{value[..at]}{to}:{toPort.ToString(CultureInfo.InvariantCulture)}{value[end..]}");
    }

    /// <summary>A keyword string's server: its host keyword's value (after SQL Server's <c>tcp:</c>, before its comma and
    /// port) and its Port keyword's. One <see cref="DbConnectionStringBuilder"/> cannot read is read by its separators
    /// alone, and cannot be written again.</summary>
    private static ConnectionServer? OfKeywords(string value)
    {
        if (!value.Contains('=', StringComparison.Ordinal))
        {
            return null;
        }
        Dictionary<string, string> keywords = new(StringComparer.OrdinalIgnoreCase);
        bool readable = true;
        try
        {
            DbConnectionStringBuilder read = new() { ConnectionString = value };
            foreach (string key in read.Keys)
            {
                keywords[key] = read[key]?.ToString() ?? string.Empty;
            }
        }
        catch (ArgumentException)
        {
            readable = false;
            foreach (string[] pair in value.Split(';').Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2))
            {
                keywords.TryAdd(pair[0].Trim(), pair[1].Trim());
            }
        }
        if (HostKeys.FirstOrDefault(keywords.ContainsKey) is not { } hostKey)
        {
            return null;
        }
        string named = keywords[hostKey].Trim();
        string protocol = named.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? named[..4] : string.Empty;
        string[] written = named[protocol.Length..].Split(',', 2);
        string host = written[0].Trim();
        bool portKeyed = keywords.ContainsKey("port");
        int? port = written.Length > 1 ? Number(written[1]) : portKeyed ? Number(keywords["port"]) : null;
        if (host.Length == 0)
        {
            return null;
        }
        return new ConnectionServer(host, port, !readable ? null : (to, toPort, besideHost) =>
        {
            DbConnectionStringBuilder again = new() { ConnectionString = value };
            string number = toPort.ToString(CultureInfo.InvariantCulture);
            bool comma = written.Length > 1 || (!portKeyed && besideHost);
            again[hostKey] = comma ? $"{protocol}{to},{number}" : $"{protocol}{to}";
            if (!comma)
            {
                again["Port"] = number;
            }
            return again.ConnectionString;
        });

        static int? Number(string text) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;
    }
}
