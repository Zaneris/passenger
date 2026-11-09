using System.Text;

namespace Passenger.Vcs;

/// <summary>What a commit is signed with, as git names the formats (gpg.format): OpenPGP through gpg, SSH through
/// ssh-keygen, X.509 through gpgsm.</summary>
public enum SigningFormat
{
    OpenPgp,
    Ssh,
    X509,
}

/// <summary>What a signature is worth, as the Log marks it.</summary>
public enum SignatureState
{
    /// <summary>Good, by a signer git trusts.</summary>
    Good,

    /// <summary>Signed, but not by a signer git can vouch for, or not checked at all.</summary>
    Unverified,

    /// <summary>The commit is not what was signed, or the key was revoked.</summary>
    Bad,

    /// <summary>Good once, but the signature or its key has expired.</summary>
    Expired,
}

/// <summary>A commit's signature as git checked it: its format (read from the signature itself), git's verdict (%G?: G
/// good, U good by a signer not trusted, E not checkable, B bad, X expired, Y made by an expired key, R by a revoked one,
/// N where git could not check it at all), the signer (%GS) and the key (%GK), and why git could not check it where it
/// said so.</summary>
public sealed record CommitSignature(SigningFormat Format, char Verdict, string? Signer, string? Key, string? Unchecked = null)
{
    public SignatureState State => Verdict switch
    {
        'G' => SignatureState.Good,
        'B' or 'R' => SignatureState.Bad,
        'X' or 'Y' => SignatureState.Expired,
        _ => SignatureState.Unverified,
    };

    /// <summary>The format as a person names it.</summary>
    public string Kind => Format switch
    {
        SigningFormat.Ssh => "SSH",
        SigningFormat.X509 => "X.509",
        _ => "GPG",
    };

    /// <summary>What it is worth, by whom and with which key, in a sentence its status begins.</summary>
    public string Said
    {
        get
        {
            string by = Signer is { Length: > 0 } signer ? $" by {signer}" : string.Empty;
            string key = Key is { Length: > 0 } named ? $", key {named}" : string.Empty;
            return Verdict switch
            {
                'G' => $"Good {Kind} signature{by}{key}",
                'U' => $"Unverified {Kind} signature{by}{key}: " + (Format is SigningFormat.Ssh ? "no allowed signers file lists its signer" : "the key is not trusted"),
                'E' => $"Unverified {Kind} signature{key}: it cannot be checked{Why(Unchecked ?? (Format is SigningFormat.Ssh ? null : "the key is not in the keyring"))}",
                'B' => $"Bad {Kind} signature{by}{key}: the commit is not what was signed",
                'X' => $"Expired {Kind} signature{by}{key}",
                'Y' => $"Expired {Kind} signature{by}{key}: the key has expired",
                'R' => $"Bad {Kind} signature{by}{key}: the key was revoked",
                _ => $"Unverified {Kind} signature: git could not check it{Why(Unchecked)}",
            };

            static string Why(string? why) => why is { Length: > 0 } ? $", as {why}" : string.Empty;
        }
    }

    /// <summary>The formats of those of a batch of commits that are signed, by hash, from what `git cat-file --batch`
    /// writes of them: each "&lt;hash&gt; commit &lt;size&gt;", its bytes and a line break, or "&lt;hash&gt; missing". A
    /// commit's signature is its gpgsig header (gpgsig-sha256 in a SHA-256 repository), named by its armour's first line;
    /// one of an armour git does not know is left out, as git would refuse to check it.</summary>
    internal static Dictionary<string, SigningFormat> Formats(ReadOnlySpan<byte> batch)
    {
        Dictionary<string, SigningFormat> signed = new(StringComparer.Ordinal);
        while (!batch.IsEmpty)
        {
            int line = batch.IndexOf((byte)'\n');
            if (line < 0)
            {
                break;
            }
            string[] header = Encoding.ASCII.GetString(batch[..line]).Split(' ');
            batch = batch[(line + 1)..];
            if (header is not [{ } hash, "commit", { } length] || !int.TryParse(length, System.Globalization.CultureInfo.InvariantCulture, out int size)
                || size > batch.Length)
            {
                continue;
            }
            if (FormatOf(batch[..size]) is { } format)
            {
                signed[hash] = format;
            }
            batch = batch[Math.Min(batch.Length, size + 1)..];
        }
        return signed;
    }

    /// <summary>The format of a commit object's signature, from its headers (up to the blank line before its message).</summary>
    private static SigningFormat? FormatOf(ReadOnlySpan<byte> commit)
    {
        int end = commit.IndexOf("\n\n"u8);
        ReadOnlySpan<byte> headers = end < 0 ? commit : commit[..(end + 1)];
        while (!headers.IsEmpty)
        {
            int line = headers.IndexOf((byte)'\n');
            ReadOnlySpan<byte> header = line < 0 ? headers : headers[..line];
            headers = line < 0 ? [] : headers[(line + 1)..];
            ReadOnlySpan<byte> armour = header.StartsWith("gpgsig "u8) ? header[7..]
                : header.StartsWith("gpgsig-sha256 "u8) ? header[14..]
                : [];
            if (armour.StartsWith("-----BEGIN PGP SIGNATURE-----"u8) || armour.StartsWith("-----BEGIN PGP MESSAGE-----"u8))
            {
                return SigningFormat.OpenPgp;
            }
            if (armour.StartsWith("-----BEGIN SSH SIGNATURE-----"u8))
            {
                return SigningFormat.Ssh;
            }
            if (armour.StartsWith("-----BEGIN SIGNED MESSAGE-----"u8))
            {
                return SigningFormat.X509;
            }
        }
        return null;
    }

    /// <summary>Git's verdicts on signed commits from what `git log --format=%H%x1f%G?%x1f%GS%x1f%GK%x1e` writes, each of
    /// a format read before; <paramref name="errors"/> is what git said while it checked them, the words of which say why
    /// one it could not check at all was not (a verifier not installed).</summary>
    internal static Dictionary<string, CommitSignature> Verdicts(string output, IReadOnlyDictionary<string, SigningFormat> formats, string errors)
    {
        string? why = string.Join("; ", errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.StartsWith("error: ", StringComparison.Ordinal) ? l[7..] : l).Distinct(StringComparer.Ordinal)) is { Length: > 0 } said ? said : null;
        Dictionary<string, CommitSignature> verdicts = new(StringComparer.Ordinal);
        foreach (string record in output.Split('\x1e'))
        {
            string[] field = record.Trim('\n').Split('\x1f');
            if (field is [{ } hash, [char verdict], { } signer, { } key] && formats.TryGetValue(hash, out SigningFormat format))
            {
                verdicts[hash] = new CommitSignature(format, verdict, signer.Length > 0 ? signer : null, key.Length > 0 ? key : null,
                    verdict is 'N' or 'E' ? why : null);
            }
        }
        return verdicts;
    }
}

public sealed partial class Git
{
    /// <summary>The signatures of these commits, by hash, those unsigned left out: which are signed found by one git
    /// reading the commits (no verifier run for one unsigned), then theirs checked by one more, each by its format's
    /// verifier as the person's git config sets it. Where that config names no allowed signers file that is there, an
    /// SSH signature is checked against <paramref name="noSigners"/>, an empty one, so it reads as a good signature by a
    /// signer no file lists rather than as none.</summary>
    public async Task<IReadOnlyDictionary<string, CommitSignature>> SignaturesAsync(IReadOnlyCollection<string> hashes, string noSigners)
    {
        if (hashes.Count == 0)
        {
            return new Dictionary<string, CommitSignature>();
        }
        (_, byte[]? batch) = await RunAsync(Root, ["cat-file", "--batch"], WholeAsync, Encoding.ASCII.GetBytes(string.Concat(hashes.Select(h => $"{h}\n"))));
        Dictionary<string, SigningFormat> formats = CommitSignature.Formats(batch ?? []);
        if (formats.Count == 0)
        {
            return new Dictionary<string, CommitSignature>();
        }
        string[] unlisted = formats.ContainsValue(SigningFormat.Ssh) && !await HasAllowedSignersAsync() ? ["-c", $"{GitConfigKeys.GpgSshAllowedSignersFile}={noSigners}"] : [];
        GitResult checkedThem = await RunAsync(Root, [.. unlisted, "log", "--no-walk=unsorted", "--stdin", "--format=%H%x1f%G?%x1f%GS%x1f%GK%x1e"],
            string.Concat(formats.Keys.Select(h => $"{h}\n")));
        return CommitSignature.Verdicts(checkedThem.Output, formats, checkedThem.Error);
    }

    /// <summary>Whether the person's git config names an allowed signers file that is there, which git checks SSH
    /// signatures against.</summary>
    private async Task<bool> HasAllowedSignersAsync()
    {
        GitResult file = await RunAsync(Root, ["config", "--type=path", "--get", GitConfigKeys.GpgSshAllowedSignersFile], answers: [1]);
        return file.Succeeded && file.Output.Trim() is { Length: > 0 } path && File.Exists(System.IO.Path.GetFullPath(path, Root));
    }
}
