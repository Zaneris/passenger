using System.Text.RegularExpressions;

namespace Passenger.Vcs;

/// <summary>The keys of git's config Passenger reads and writes for who commits and how a commit is signed, each written
/// here once, so a misspelt one is a build error rather than a setting git never finds.</summary>
[IdentifierHome]
public static class GitConfigKeys
{
    /// <summary>Whether every commit is signed.</summary>
    public const string CommitGpgSign = "commit.gpgsign";

    /// <summary>The format a commit is signed in: openpgp (where unset), ssh or x509.</summary>
    public const string GpgFormat = "gpg.format";

    /// <summary>The key a commit is signed with: an SSH key's file or the key itself, a GPG key's id, a certificate's.</summary>
    public const string UserSigningKey = "user.signingkey";

    /// <summary>The command that gives the SSH key to sign with where no key is named.</summary>
    public const string GpgSshDefaultKeyCommand = "gpg.ssh.defaultKeyCommand";

    /// <summary>The file of who may sign with which SSH key, which git checks SSH signatures against.</summary>
    public const string GpgSshAllowedSignersFile = "gpg.ssh.allowedSignersFile";

    /// <summary>The gpg git signs OpenPGP with, by its older name.</summary>
    public const string GpgProgram = "gpg.program";

    /// <summary>The gpg git signs OpenPGP with.</summary>
    public const string GpgOpenPgpProgram = "gpg.openpgp.program";

    /// <summary>The gpgsm git signs X.509 with.</summary>
    public const string GpgX509Program = "gpg.x509.program";

    /// <summary>The committer's address.</summary>
    public const string UserEmail = "user.email";

    /// <summary>A pattern `git config --get-regexp` takes for the keys given, each whole: git matches it against a key's
    /// section and name in lower case.</summary>
    public static string Pattern(params ReadOnlySpan<string> keys)
    {
        List<string> each = [];
        foreach (string key in keys)
        {
            each.Add(Regex.Escape(key.ToLowerInvariant()));
        }
        return $"^({string.Join('|', each)})$";
    }
}
