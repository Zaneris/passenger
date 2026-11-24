using System.Globalization;
using System.Text;

namespace Passenger.App.Database;

/// <summary>A host as ssh's configuration names it: where it is reached and as whom, the key files to sign in with, the
/// hosts to jump through first (ProxyJump, as written), the agent to ask for keys, and the files of known host keys.</summary>
public sealed record SshHost(string Alias, string HostName, int Port, string User)
{
    public IReadOnlyList<string> IdentityFiles { get; init; } = [];

    /// <summary>Whether IdentityFile named the files, rather than ssh's usual ones being looked for.</summary>
    public bool IdentityFilesNamed { get; init; }

    public bool IdentitiesOnly { get; init; }
    public IReadOnlyList<string> Jumps { get; init; } = [];

    /// <summary>IdentityAgent's socket or pipe, "none" for no agent, or null for SSH_AUTH_SOCK's.</summary>
    public string? IdentityAgent { get; init; }

    public IReadOnlyList<string> KnownHostsFiles { get; init; } = [];

    /// <summary>The name its key is known by in the known hosts files (HostKeyAlias, else its host name).</summary>
    public string KeyAlias { get; init; } = string.Empty;
}

/// <summary>ssh's own configuration read as ssh reads it (ssh_config(5)): keywords in any case, a value after white space
/// or "=", quoted where it holds spaces; a Host line's patterns (* and ?, ! for none) deciding which lines after it apply,
/// Match only for "all" and "host"/"originalhost" (any other criterion applies nowhere, as nothing is run to test it);
/// Include's files read in place, relative to ~/.ssh; the first value of a keyword winning, IdentityFile's adding up; and
/// the tokens %h, %p, %r, %u, %d and %% and ~ expanded in a file's path. Only what a tunnel takes is read.</summary>
public static class SshConfig
{
    /// <summary>ssh's own key files, looked for in its order where IdentityFile names none.</summary>
    public static readonly string[] UsualKeys = ["id_rsa", "id_ecdsa", "id_ed25519"];

    /// <summary>What the configuration at <paramref name="file"/> says of <paramref name="alias"/>, for <paramref name="localUser"/>
    /// whose home is <paramref name="home"/>; <paramref name="read"/> gives a file's text or null, <paramref name="matching"/>
    /// the files an Include's pattern names.</summary>
    public static SshHost Resolve(string alias, string file, string home, string localUser, Func<string, string?> read, Func<string, IReadOnlyList<string>> matching)
    {
        Dictionary<string, string> first = new(StringComparer.Ordinal);
        List<string> identities = [];
        string ssh = Path.Combine(home, ".ssh");
        Read(file, true, 0);
        string hostName = first.TryGetValue("hostname", out string? named) ? Tokens(named, alias, alias, 22, string.Empty, localUser, home) : alias;
        int port = first.TryGetValue("port", out string? written) && int.TryParse(written, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number is > 0 and < 65536 ? number : 22;
        string user = first.GetValueOrDefault("user") is { Length: > 0 } given ? given : localUser;
        string Expanded(string path) => Rooted(Tokens(path, alias, hostName, port, user, localUser, home), home);
        return new SshHost(alias, hostName, port, user)
        {
            IdentityFiles = identities.Count > 0 ? [.. identities.Select(Expanded)] : [.. UsualKeys.Select(k => Path.Combine(ssh, k))],
            IdentityFilesNamed = identities.Count > 0,
            IdentitiesOnly = first.GetValueOrDefault("identitiesonly") is { } only && only.Equals("yes", StringComparison.OrdinalIgnoreCase),
            Jumps = first.GetValueOrDefault("proxyjump") is { } jumps && !jumps.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? [.. jumps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [],
            IdentityAgent = first.GetValueOrDefault("identityagent") is { } agent
                ? agent is "none" or "SSH_AUTH_SOCK" || agent.StartsWith('$') ? agent : Expanded(agent) : null,
            KnownHostsFiles = first.GetValueOrDefault("userknownhostsfile") is { } known
                ? [.. Words(known).Where(k => !k.Equals("none", StringComparison.OrdinalIgnoreCase)).Select(Expanded)]
                : [Path.Combine(ssh, "known_hosts"), Path.Combine(ssh, "known_hosts2")],
            KeyAlias = first.GetValueOrDefault("hostkeyalias") ?? hostName,
        };

        void Read(string path, bool active, int depth)
        {
            if (depth > 16 || read(path) is not { } text)
            {
                return;
            }
            foreach (string line in text.Split('\n'))
            {
                (string keyword, string value) = Line(line);
                switch (keyword)
                {
                    case "":
                        continue;
                    case "host":
                        active = Matches(alias, Words(value));
                        continue;
                    case "match":
                        active = MatchApplies(Words(value), alias, first.GetValueOrDefault("hostname") is { } host ? Tokens(host, alias, alias, 22, string.Empty, localUser, home) : alias);
                        continue;
                }
                if (!active)
                {
                    continue;
                }
                if (keyword == "include")
                {
                    foreach (string pattern in Words(value))
                    {
                        foreach (string included in matching(pattern.StartsWith('~') || Path.IsPathRooted(pattern) ? Rooted(pattern, home) : Path.Combine(ssh, pattern)))
                        {
                            Read(included, true, depth + 1);
                        }
                    }
                }
                else if (keyword == "identityfile")
                {
                    identities.AddRange(Words(value));
                }
                else
                {
                    first.TryAdd(keyword, keyword is "userknownhostsfile" ? value : Words(value).FirstOrDefault() ?? string.Empty);
                }
            }
        }
    }

    /// <summary>A jump as ProxyJump writes it ([user@]host[:port], or an ssh:// address): its host, and the user and port it names.</summary>
    public static (string Host, string? User, int? Port) Jump(string written)
    {
        string rest = written.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ? written["ssh://".Length..].TrimEnd('/') : written;
        string? user = null;
        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            user = rest[..at];
            rest = rest[(at + 1)..];
        }
        int? port = null;
        if (rest.StartsWith('[') && rest.IndexOf(']', StringComparison.Ordinal) is var close and > 0)
        {
            port = rest.Length > close + 2 && rest[close + 1] == ':' && int.TryParse(rest[(close + 2)..], CultureInfo.InvariantCulture, out int bracketed) ? bracketed : null;
            rest = rest[1..close];
        }
        else if (rest.LastIndexOf(':') is var colon and > 0 && rest.IndexOf(':', StringComparison.Ordinal) == colon
            && int.TryParse(rest[(colon + 1)..], CultureInfo.InvariantCulture, out int number))
        {
            port = number;
            rest = rest[..colon];
        }
        return (rest, user, port);
    }

    /// <summary>Whether a host's patterns take <paramref name="name"/>: one of them does and none written with ! does.</summary>
    public static bool Matches(string name, IEnumerable<string> patterns)
    {
        bool taken = false;
        foreach (string pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                if (Wildcard(name, pattern[1..]))
                {
                    return false;
                }
            }
            else
            {
                taken |= Wildcard(name, pattern);
            }
        }
        return taken;
    }

    /// <summary>Whether a pattern of * and ? takes a name, ignoring case as ssh does for host names.</summary>
    public static bool Wildcard(string name, string pattern)
    {
        int n = 0;
        int p = 0;
        int star = -1;
        int resume = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n])))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++resume;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }

    /// <summary>A Match line's criteria: "all", or host and originalhost lists, each of which must take its name.</summary>
    private static bool MatchApplies(IReadOnlyList<string> words, string alias, string host)
    {
        if (words is [var all] && all.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        for (int i = 0; i + 1 < words.Count; i += 2)
        {
            string criterion = words[i].ToLowerInvariant();
            IEnumerable<string> list = words[i + 1].Split(',');
            bool holds = criterion switch
            {
                "host" => Matches(host, list),
                "originalhost" => Matches(alias, list),
                _ => false,
            };
            if (!holds)
            {
                return false;
            }
        }
        return words.Count > 0 && words.Count % 2 == 0;
    }

    /// <summary>A line's keyword (lower case; empty for none) and its value.</summary>
    private static (string Keyword, string Value) Line(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return (string.Empty, string.Empty);
        }
        int end = trimmed.IndexOfAny([' ', '\t', '=']);
        if (end < 0)
        {
            return (trimmed.ToLowerInvariant(), string.Empty);
        }
        string rest = trimmed[end..].TrimStart(' ', '\t');
        if (rest.StartsWith('='))
        {
            rest = rest[1..].TrimStart(' ', '\t');
        }
        return (trimmed[..end].ToLowerInvariant(), rest);
    }

    /// <summary>A value's words, split at white space outside double quotes.</summary>
    public static IReadOnlyList<string> Words(string value)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool quoted = false;
        bool any = false;
        foreach (char c in value)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (!quoted && c is ' ' or '\t')
            {
                Flush();
            }
            else
            {
                word.Append(c);
                any = true;
            }
        }
        Flush();
        return words;

        void Flush()
        {
            if (any)
            {
                words.Add(word.ToString());
            }
            word.Clear();
            any = false;
        }
    }

    /// <summary>A path's ~ and %-tokens expanded as ssh expands them.</summary>
    private static string Tokens(string written, string alias, string host, int port, string user, string localUser, string home)
    {
        StringBuilder expanded = new();
        for (int i = 0; i < written.Length; i++)
        {
            if (written[i] != '%' || i + 1 == written.Length)
            {
                expanded.Append(written[i]);
                continue;
            }
            expanded.Append(written[++i] switch
            {
                'h' => host,
                'n' => alias,
                'p' => port.ToString(CultureInfo.InvariantCulture),
                'r' => user,
                'u' => localUser,
                'd' => home,
                '%' => "%",
                var other => "%" + other,
            });
        }
        return expanded.ToString();
    }

    /// <summary>A path with ~ for the home folder, and one relative to it, made whole.</summary>
    private static string Rooted(string path, string home) =>
        path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..])
        : Path.IsPathRooted(path) ? path : Path.Combine(home, path);
}
