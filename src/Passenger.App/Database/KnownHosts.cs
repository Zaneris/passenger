using System.Security.Cryptography;
using System.Text;

namespace Passenger.App.Database;

/// <summary>What ssh's known hosts files say of a host's key: nothing, that it is the key known, that the host is known by
/// another key of its kind (changed, as ssh refuses it), or that the key is revoked.</summary>
public enum KnownHostKey
{
    Unknown,
    Known,
    Changed,
    Revoked,
}

/// <summary>ssh's known hosts files read as ssh reads them (sshd(8)'s SSH_KNOWN_HOSTS FILE FORMAT), never written: a line's
/// host patterns (* and ?, ! for none, "[host]:port" off port 22) or a hashed name (|1|salt|hash, HMAC-SHA1), its key's
/// kind and its key; @revoked marks a key refused, and @cert-authority lines are passed over.</summary>
public static class KnownHosts
{
    /// <summary>What becomes of a host's key, as ssh takes one, by what each known hosts file <paramref name="said"/> of it and
    /// whether the person trusted it before: revoked where any file revokes it, trusted or not; known where trusted or known
    /// anywhere; else changed where a file knows another key of its kind, else unknown.</summary>
    public static KnownHostKey Verdict(IEnumerable<KnownHostKey> said, bool trusted)
    {
        KnownHostKey[] all = [.. said];
        return all.Contains(KnownHostKey.Revoked) ? KnownHostKey.Revoked
            : trusted || all.Contains(KnownHostKey.Known) ? KnownHostKey.Known
            : all.Contains(KnownHostKey.Changed) ? KnownHostKey.Changed : KnownHostKey.Unknown;
    }

    /// <summary>What the files' <paramref name="text"/> says of <paramref name="host"/>'s key of <paramref name="kind"/>,
    /// <paramref name="key"/> its blob as the server sent it.</summary>
    public static KnownHostKey Judge(string text, string host, int port, string kind, byte[] key)
    {
        string name = port == 22 ? host : $"[{host}]:{port}";
        string written = Convert.ToBase64String(key);
        KnownHostKey said = KnownHostKey.Unknown;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            string? marker = fields[0].StartsWith('@') ? fields[0] : null;
            int at = marker is null ? 0 : 1;
            if (fields.Length < at + 3 || marker is "@cert-authority" || !Names(fields[at], name))
            {
                continue;
            }
            bool same = fields[at + 2] == written;
            if (marker == "@revoked")
            {
                if (same)
                {
                    return KnownHostKey.Revoked;
                }
                continue;
            }
            if (same && fields[at + 1] == kind)
            {
                said = KnownHostKey.Known;
            }
            else if (fields[at + 1] == kind && said == KnownHostKey.Unknown)
            {
                said = KnownHostKey.Changed;
            }
        }
        return said;
    }

    /// <summary>Whether a line's hosts take <paramref name="name"/>: hashed, or patterns of which one takes it and none
    /// written with ! does.</summary>
    private static bool Names(string hosts, string name)
    {
        if (hosts.StartsWith("|1|", StringComparison.Ordinal))
        {
            string[] parts = hosts.Split('|');
            if (parts.Length != 4)
            {
                return false;
            }
            try
            {
                byte[] hash = HMACSHA1.HashData(Convert.FromBase64String(parts[2]), Encoding.UTF8.GetBytes(name));
                return Convert.ToBase64String(hash) == parts[3];
            }
            catch (FormatException)
            {
                return false;
            }
        }
        return SshConfig.Matches(name, hosts.Split(','));
    }
}
