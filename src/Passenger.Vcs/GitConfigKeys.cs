using System.Text.RegularExpressions;

namespace Passenger.Vcs;

/// <summary>The keys of git's config Passenger reads and writes for who commits and how a commit is signed, and those it
/// gives a git in a folder not trusted (<see cref="UntrustedGit"/>), each written here once, so a misspelt one is a build
/// error rather than a setting git never finds.</summary>
[IdentifierHome]
public static class GitConfigKeys
{
    /// <summary>Whether every commit is signed.</summary>
    public const string CommitGpgSign = "commit.gpgsign";

    /// <summary>Whether every tag git makes is an annotated tag, signed.</summary>
    public const string TagGpgSign = "tag.gpgsign";

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

    /// <summary>The program git asks which files changed, or whether the built-in daemon does.</summary>
    public const string CoreFsmonitor = "core.fsmonitor";

    /// <summary>The folder git runs hooks from in the repository's own hooks' place.</summary>
    public const string CoreHooksPath = "core.hooksPath";

    /// <summary>Whether a checkout, a switch, a reset or a restore checks each submodule out too.</summary>
    public const string SubmoduleRecurse = "submodule.recurse";

    /// <summary>Whether a commit, a merge or a fetch runs git's maintenance after it.</summary>
    public const string MaintenanceAuto = "maintenance.auto";

    /// <summary>How many loose objects have a command run a gc after it; 0, never.</summary>
    public const string GcAuto = "gc.auto";

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
