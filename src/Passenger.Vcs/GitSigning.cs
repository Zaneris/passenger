using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>How the tree's git config has a commit signed: whether every commit is (commit.gpgsign), in which format
/// (gpg.format, OpenPGP where unset), and with which key (user.signingkey: an SSH key's file or the key itself, a GPG
/// key's id, an X.509 certificate's; for GPG and X.509 the committer's own where unset, for SSH a key command's), with
/// the scope of the config that sets the key (local, global, system), whether the format is set at all, and the gpg the
/// person signs OpenPGP with where they name one (gpg.program, gpg.openpgp.program).</summary>
public sealed record SigningSetup(bool Signs, SigningFormat Format, string? Key, string? KeyScope = null, bool KeyCommand = false, bool FormatSet = false,
    string? Program = null)
{
    /// <summary>The gpgsm git signs an X.509 commit with, where its config names one (gpg.x509.program).</summary>
    public string? X509Program { get; init; }

    public static readonly SigningSetup None = new(false, SigningFormat.OpenPgp, null);

    /// <summary>The setup `git config -z --show-scope --get-regexp` wrote: each setting its scope and a NUL, its name, a
    /// line break and its value, ended by a NUL; a name alone (no value) is true. A later setting wins, as in git.</summary>
    public static SigningSetup Of(string config)
    {
        Dictionary<string, (string Scope, string Value)> set = new(StringComparer.OrdinalIgnoreCase);
        string[] parts = config.Split('\0');
        string? program = null;
        string? x509 = null;
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            string entry = parts[i + 1];
            int line = entry.IndexOf('\n', StringComparison.Ordinal);
            string name = line < 0 ? entry : entry[..line];
            set[name] = (parts[i], line < 0 ? "true" : entry[(line + 1)..]);
            // gpg.program is gpg.openpgp.program's older name: the one set last is the one git runs.
            if (name.Equals(GitConfigKeys.GpgProgram, StringComparison.OrdinalIgnoreCase) || name.Equals(GitConfigKeys.GpgOpenPgpProgram, StringComparison.OrdinalIgnoreCase))
            {
                program = set[name].Value.Trim() is { Length: > 0 } given ? given : null;
            }
            else if (name.Equals(GitConfigKeys.GpgX509Program, StringComparison.OrdinalIgnoreCase))
            {
                x509 = set[name].Value.Trim() is { Length: > 0 } given ? given : null;
            }
        }
        bool signs = set.TryGetValue(GitConfigKeys.CommitGpgSign, out (string Scope, string Value) sign) && Truth(sign.Value);
        SigningFormat format = set.GetValueOrDefault(GitConfigKeys.GpgFormat).Value?.ToLowerInvariant() switch
        {
            "ssh" => SigningFormat.Ssh,
            "x509" => SigningFormat.X509,
            _ => SigningFormat.OpenPgp,
        };
        (string? scope, string? key) = set.TryGetValue(GitConfigKeys.UserSigningKey, out (string Scope, string Value) named) && named.Value.Trim().Length > 0
            ? (named.Scope, named.Value.Trim())
            : (null, null);
        return new SigningSetup(signs, format, key, scope, set.GetValueOrDefault(GitConfigKeys.GpgSshDefaultKeyCommand).Value is { Length: > 0 }, set.ContainsKey(GitConfigKeys.GpgFormat), program)
        {
            X509Program = x509,
        };

        static bool Truth(string value) => value.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1";
    }

    /// <summary>The format as a person names it.</summary>
    public string Kind => new CommitSignature(Format, 'N', null, null).Kind;

    /// <summary>Whether git's config says anything of signing: a format, a key or every commit signed.</summary>
    public bool IsSetUp => Signs || FormatSet || Key is not null || KeyCommand;

    /// <summary>Whether git has a key to sign with, once signing is set up: an SSH signature needs one set (or a command
    /// that gives it), a GPG or X.509 one takes the committer's.</summary>
    public bool CanSign => IsSetUp && (Format is not SigningFormat.Ssh || Key is not null || KeyCommand);

    /// <summary>Whether the key is an SSH key's file, which a passphrase may guard; one given whole (key::, or its text) is
    /// an agent's.</summary>
    public bool KeyIsFile => Format is SigningFormat.Ssh && Key is { } key && !key.StartsWith("key::", StringComparison.Ordinal) && !key.StartsWith("ssh-", StringComparison.Ordinal);

    /// <summary>The key as the Commit window names it: an SSH key's file by its name, one given whole by its comment (or
    /// its type), a GPG or X.509 key by its id, the committer's where none is set.</summary>
    public string KeyShown => Key switch
    {
        null when KeyCommand => $"the {Kind} key {GitConfigKeys.GpgSshDefaultKeyCommand} gives",
        null => Format is SigningFormat.Ssh ? "no key" : $"the committer's {Kind} key",
        { } whole when !KeyIsFile && Format is SigningFormat.Ssh => Whole(whole.StartsWith("key::", StringComparison.Ordinal) ? whole[5..] : whole),
        { } path when KeyIsFile => $"{Kind} key {Path.GetFileName(path)}",
        { } id => $"{Kind} key {id}",
    };

    /// <summary>An SSH key given whole ("ssh-ed25519 AAAA… comment"), by its comment, else its type.</summary>
    private string Whole(string key)
    {
        string[] words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 2 ? $"{Kind} key {string.Join(' ', words[2..])}" : $"{Kind} key {words[0]}";
    }
}

/// <summary>The gpg and gpgsm git signs with as it finds them: the one its config names, else the PATH's, else on Windows
/// Git for Windows' own copy in usr\bin under its top folder, which its git.exe puts on the PATH of the programs it runs
/// (git-wrapper.c's setup_environment) and of no other's.</summary>
public static class GpgPrograms
{
    /// <summary>The gpg git runs for a GPG signature, where it is there.</summary>
    public static string? Gpg(string? named) => Of(named, "gpg");

    /// <summary>The gpgsm git runs for an X.509 signature, where it is there.</summary>
    public static string? Gpgsm(string? named) => Of(named, "gpgsm");

    private static string? Of(string? named, string program)
    {
        string exe = OperatingSystem.IsWindows() ? $"{program}.exe" : program;
        if (named is not null)
        {
            return Path.IsPathRooted(named) ? (File.Exists(named) ? named : null) : Programs.OnPath(named);
        }
        return Programs.OnPath(exe) ?? (OperatingSystem.IsWindows() && Programs.OnPath("git.exe") is { } git ? Bundled(git, exe, File.Exists) : null);
    }

    /// <summary>Git for Windows' own copy of a program, from its git.exe (in cmd\, bin\ or mingw64\bin\ under its top
    /// folder): <c>usr\bin\program</c> under the first folder above that holds one; null for none.</summary>
    public static string? Bundled(string git, string exe, Func<string, bool> exists)
    {
        string? folder = Path.GetDirectoryName(git);
        for (int up = 0; up < 3 && folder is not null; up++, folder = Path.GetDirectoryName(folder))
        {
            string bundled = Path.Combine(folder, "usr", "bin", exe);
            if (exists(bundled))
            {
                return bundled;
            }
        }
        return null;
    }
}

/// <summary>How one commit is made beyond its files and message: signed (true, -S), not (false, --no-gpg-sign) or as git's
/// config says (null), signed off (--signoff), and the config and environment it is made with where Passenger gives git
/// its own gpg (<see cref="GpgLoopback"/>).</summary>
public sealed record CommitSigning(bool? Sign = null, bool SignOff = false, IReadOnlyList<string>? Config = null, IReadOnlyDictionary<string, string>? Environment = null)
{
    public static readonly CommitSigning AsConfigured = new();

    /// <summary>What comes before git's command: the config this commit alone is made with (-c).</summary>
    internal IEnumerable<string> Before => (Config ?? []).SelectMany(c => new[] { "-c", c });

    /// <summary>What follows the commit's own options.</summary>
    internal IEnumerable<string> After =>
    [
        .. Sign switch
        {
            true => new[] { "-S" },
            false => ["--no-gpg-sign"],
            null => [],
        },
        .. SignOff ? new[] { "--signoff" } : [],
    ];
}

/// <summary>How the commits a git makes beyond the Commit window's own (a merge's, a cherry-pick's, a rebase's) are signed:
/// through Passenger's own gpg where it gives one, a key's passphrase asked of <see cref="Asker"/>.</summary>
public sealed record Signer(CommitSigning Signing, GitAsker? Asker);

/// <summary>An allowed signers file's lines (ssh-keygen's ALLOWED SIGNERS: principals, options, the key), as Commit
/// Signing… adds the person's own key to it.</summary>
public static class AllowedSigners
{
    /// <summary>The file with the key listed for <paramref name="signer"/> to sign commits (namespaces="git"), after what
    /// it has; null where a line already has git take that key's signatures for theirs.</summary>
    public static string? With(string file, string signer, string publicKey)
    {
        string[] key = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (key.Length < 2 || !IsKeyType(key[0]) || file.Split('\n', StringSplitOptions.TrimEntries).Any(line => Trusts(line, signer, key[1])))
        {
            return null;
        }
        string before = file.Length == 0 || file.EndsWith('\n') ? file : file + "\n";
        return $"{before}{signer} namespaces=\"git\" {key[0]} {key[1]}\n";
    }

    private static bool IsKeyType(string word) =>
        word.StartsWith("ssh-", StringComparison.Ordinal) || word.StartsWith("ecdsa-", StringComparison.Ordinal) || word.StartsWith("sk-", StringComparison.Ordinal);

    /// <summary>Whether ssh-keygen takes a line to allow the key to sign commits as <paramref name="signer"/>: the key its
    /// own, not a certificate authority's, the signer among its principals ("*", no email known, is any) and git among its
    /// namespaces. A line's own time limits are left to it.</summary>
    private static bool Trusts(string line, string signer, string key)
    {
        List<string> words = Pieces(line, c => c is ' ' or '\t');
        int type = words.Count > 1 && IsKeyType(words[1]) ? 1 : 2;
        if (line.StartsWith('#') || words.Count <= type + 1 || words[type + 1] != key)
        {
            return false;
        }
        List<string> options = type == 2 ? Pieces(words[1], c => c == ',') : [];
        string? namespaces = options.FirstOrDefault(o => o.StartsWith("namespaces=", StringComparison.OrdinalIgnoreCase))?["namespaces=".Length..];
        return !options.Contains("cert-authority", StringComparer.OrdinalIgnoreCase) && (signer == "*" || Listed(signer, words[0])) && (namespaces is null || Listed("git", namespaces));
    }

    /// <summary>A line's or an option list's pieces, parted where <paramref name="parts"/> says outside double quotes.</summary>
    private static List<string> Pieces(string text, Func<char, bool> parts)
    {
        List<string> pieces = [];
        int start = 0;
        bool quoted = false;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || (!quoted && parts(text[i])))
            {
                if (i > start)
                {
                    pieces.Add(text[start..i]);
                }
                start = i + 1;
            }
            else if (text[i] == '"')
            {
                quoted = !quoted;
            }
        }
        return pieces;
    }

    /// <summary>Whether a name is among ssh's patterns, read as ssh-keygen reads them: comma-separated, with '*' and '?',
    /// one led by '!' refusing the name whatever else lists it.</summary>
    private static bool Listed(string name, string patterns)
    {
        bool listed = false;
        foreach (string pattern in patterns.Trim('"').Split(','))
        {
            bool refused = pattern.StartsWith('!');
            if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(refused ? pattern.AsSpan(1) : pattern, name, ignoreCase: false))
            {
                if (refused)
                {
                    return false;
                }
                listed = true;
            }
        }
        return listed;
    }

    /// <summary>The host of a remote's URL where it is reached over SSH: ssh://[user@]host[:port]/path, or scp's
    /// [user@]host:path; null for any other (https, a file path, a drive letter's C:\).</summary>
    public static string? HostOf(string url)
    {
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("git+ssh://", StringComparison.OrdinalIgnoreCase))
        {
            string rest = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
            string authority = rest.Split('/')[0];
            string host = authority[(authority.LastIndexOf('@') + 1)..];
            return host.Split(':')[0] is { Length: > 0 } name ? name : null;
        }
        if (url.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        int colon = url.IndexOf(':', StringComparison.Ordinal);
        int slash = url.IndexOf('/', StringComparison.Ordinal);
        if (colon <= 1 || (slash >= 0 && slash < colon))
        {
            return null;
        }
        string scp = url[..colon];
        return scp[(scp.LastIndexOf('@') + 1)..] is { Length: > 0 } scpHost ? scpHost : null;
    }
}

/// <summary>Why a commit git was to sign was not made, in a person's words with the way to mend it, from what git (and the
/// program it signs with) said in English; null where it said nothing of signing.</summary>
public static class SigningFailure
{
    /// <summary>What the Commit window's set-up is called where a failure says to go there.</summary>
    public const string SetUp = "Commit Signing…";

    /// <summary>What every failure to sign begins with.</summary>
    public const string Unsigned = "The commit was not signed: ";

    public static string? Of(string said, SigningSetup setup)
    {
        string key = setup.KeyShown;
        string? why = said switch
        {
            _ when Has(said, "incorrect passphrase supplied") || Has(said, "Bad passphrase") =>
                $"the passphrase given is not that of {key}. Commit again and give its passphrase",
            _ when Has(said, "Couldn't load public key") || (Has(said, "Load key") && Has(said, "No such file")) =>
                $"{key} is not there ({GitConfigKeys.UserSigningKey}). Choose a key in {SetUp}",
            _ when Has(said, "user.signingKey needs to be set") || Has(said, "either user.signingkey or gpg.ssh.defaultKeyCommand") =>
                $"git signs with SSH, but no key is set to sign with. Choose one in {SetUp}",
            _ when Has(said, "Couldn't get agent socket") || Has(said, "agent refused operation") || Has(said, "Couldn't find key in agent") =>
                $"{key} is to come from an SSH agent, and no agent holds it. Add it to the agent (ssh-add), or choose its file in {SetUp}",
            _ when Has(said, "cannot run gpgsm") =>
                $"gpgsm, which signs with X.509 certificates, is not installed. Install GnuPG's gpgsm, or sign with SSH in {SetUp}",
            _ when Has(said, "cannot run gpg") =>
                $"gpg is not installed. Install GnuPG, or sign with SSH in {SetUp}",
            _ when Has(said, "cannot exec '") || Has(said, "cannot run ") =>
                $"the program git signs with could not be run: {Line(said, "cannot ")}",
            _ when Has(said, "No secret key") || Has(said, "secret key not available") || Has(said, "No such file or directory") && setup.Format is not SigningFormat.Ssh =>
                $"there is no secret key for {key}. Import it, or choose another in {SetUp}",
            _ when Has(said, "Inappropriate ioctl for device") || Has(said, "No pinentry") || Has(said, "Operation cancelled") =>
                $"the passphrase of {key} could not be asked for. Commit again from Passenger, which asks for it",
            _ when Has(said, "failed to sign the data") || Has(said, "signing failed") =>
                Line(said, "signing failed") ?? Line(said, "failed to sign the data"),
            _ => null,
        };
        return why is null ? null : Unsigned + why;

        static bool Has(string said, string words) => said.Contains(words, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first line git said that holds these words, less "error: " and "fatal: ".</summary>
    private static string? Line(string said, string words) => said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(l => l.Contains(words, StringComparison.OrdinalIgnoreCase)) is { } line
        ? line.StartsWith("error: ", StringComparison.Ordinal) || line.StartsWith("fatal: ", StringComparison.Ordinal) ? line[7..] : line
        : null;
}

/// <summary>What Commit Signing… sets: the format and key commits are signed with, whether every commit is signed, in the
/// repository's config or the person's global one, and for SSH the allowed signers file the person's own key is listed in
/// (with their email), so git shows their signatures as good.</summary>
public sealed record SigningChoice(SigningFormat Format, string? Key, bool SignEvery, bool Global, string? AllowedSigners = null, string? PublicKey = null);

public sealed partial class Git
{
    /// <summary>Sets commits signed as chosen, in the scope chosen, each setting by git config; for SSH, the key listed in
    /// the allowed signers file (made where there is none) for the committer's email, and the file named in the same
    /// scope where none reaches that scope: one this repository names alone reaches no other, so it never counts for a
    /// global setting. The first git that fails is the answer.</summary>
    public async Task<GitResult> SetSigningAsync(SigningChoice choice)
    {
        string scope = choice.Global ? "--global" : "--local";
        string format = choice.Format switch
        {
            SigningFormat.Ssh => "ssh",
            SigningFormat.X509 => "x509",
            _ => "openpgp",
        };
        List<string[]> settings =
        [
            ["config", scope, GitConfigKeys.GpgFormat, format],
            choice.Key is { Length: > 0 } key ? ["config", scope, GitConfigKeys.UserSigningKey, key] : ["config", scope, "--unset-all", GitConfigKeys.UserSigningKey],
            ["config", scope, GitConfigKeys.CommitGpgSign, choice.SignEvery ? "true" : "false"],
        ];
        foreach (string[] setting in settings)
        {
            GitResult set = await RunAsync(Root, setting, answers: [5]);
            if (!set.Succeeded && set.Code != 5)
            {
                return set;
            }
        }
        if (choice.Format is not SigningFormat.Ssh || choice.PublicKey is not { } publicKey)
        {
            return new GitResult(0, string.Empty, string.Empty);
        }
        GitResult named = await RunAsync(Root, ["config", .. choice.Global ? [scope] : Array.Empty<string>(), "--type=path", "--get", GitConfigKeys.GpgSshAllowedSignersFile], answers: [1]);
        string file = named.Succeeded && named.Output.Trim() is { Length: > 0 } listed ? System.IO.Path.GetFullPath(listed, Root)
            : choice.AllowedSigners ?? System.IO.Path.Combine(GitDir, "allowed_signers");
        GitResult email = await RunAsync(Root, ["config", "--get", GitConfigKeys.UserEmail], answers: [1]);
        string signer = email.Output.Trim() is { Length: > 0 } address ? address : "*";
        if (AllowedSigners.With(File.Exists(file) ? await File.ReadAllTextAsync(file) : string.Empty, signer, publicKey) is { } written)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            AtomicFile.Write(file, System.Text.Encoding.UTF8.GetBytes(written));
        }
        return named.Succeeded ? named : await RunAsync(Root, ["config", scope, GitConfigKeys.GpgSshAllowedSignersFile, file]);
    }

    /// <summary>The host a push goes to over SSH (the upstream's remote, else origin, else the only one), from its URL
    /// (ssh://user@host/path or user@host:path); null where it goes over another way or there is none.</summary>
    public async Task<string?> PushHostAsync()
    {
        string? remote = (await UpstreamAsync())?.Split('/')[0];
        IReadOnlyList<string> remotes = await RemotesAsync();
        remote ??= remotes.Contains("origin") ? "origin" : remotes.Count == 1 ? remotes[0] : null;
        if (remote is null)
        {
            return null;
        }
        GitResult url = await RunAsync(Root, ["remote", "get-url", "--push", "--end-of-options", remote], answers: [2, NotThere]);
        return url.Succeeded ? AllowedSigners.HostOf(url.Output.Trim()) : null;
    }

    /// <summary>How the tree's git config has a commit signed, read by one git.</summary>
    public async Task<SigningSetup> SigningAsync()
    {
        GitResult config = await RunAsync(Root, ["config", "-z", "--show-scope", "--get-regexp", GitConfigKeys.Pattern(GitConfigKeys.CommitGpgSign, GitConfigKeys.GpgFormat,
            GitConfigKeys.UserSigningKey, GitConfigKeys.GpgSshDefaultKeyCommand, GitConfigKeys.GpgProgram, GitConfigKeys.GpgOpenPgpProgram, GitConfigKeys.GpgX509Program)], answers: [1]);
        return config.Succeeded ? SigningSetup.Of(config.Output) : SigningSetup.None;
    }
}
