using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>Signing as the Commit window reads and sets it: git's config read with each setting's scope, the later
/// winning; the key named as the window names it; one commit's -S, --no-gpg-sign and --signoff; a signing failure in a
/// person's words with its fix; the person's key added once to an allowed signers file; the host a push goes to; and
/// ssh-keygen's passphrase prompt read as signing's.</summary>
public class SigningTests
{
    [Fact]
    public void Gits_config_is_read_with_the_scope_that_sets_the_key_the_later_setting_winning()
    {
        SigningSetup set = SigningSetup.Of("global\0commit.gpgsign\ntrue\0global\0user.signingkey\n~/.ssh/old.pub\0local\0gpg.format\nssh\0local\0user.signingkey\n/home/me/.ssh/id_ed25519.pub\0");
        Assert.Equal(new SigningSetup(true, SigningFormat.Ssh, "/home/me/.ssh/id_ed25519.pub", "local", false, true), set);
        Assert.True(set.CanSign && set.KeyIsFile && set.IsSetUp);
        Assert.Equal("SSH key id_ed25519.pub", set.KeyShown);

        SigningSetup none = SigningSetup.Of(string.Empty);
        Assert.False(none.IsSetUp || none.CanSign);
        SigningSetup sshWithoutKey = SigningSetup.Of("local\0gpg.format\nssh\0local\0commit.gpgsign\0");
        Assert.True(sshWithoutKey.IsSetUp && sshWithoutKey.Signs && !sshWithoutKey.CanSign);
        Assert.Equal("no key", sshWithoutKey.KeyShown);
        Assert.Equal("the committer's GPG key", SigningSetup.Of("global\0commit.gpgsign\nyes\0").KeyShown);
        Assert.Equal("GPG key 0123ABCD", SigningSetup.Of("global\0user.signingkey\n0123ABCD\0").KeyShown);
        Assert.Equal("SSH key me@laptop", SigningSetup.Of("local\0gpg.format\nssh\0local\0user.signingkey\nkey::ssh-ed25519 AAAAC3Nza me@laptop\0").KeyShown);
        Assert.Equal((SigningFormat.X509, "X.509"), (SigningSetup.Of("local\0gpg.format\nx509\0").Format, SigningSetup.Of("local\0gpg.format\nx509\0").Kind));
    }

    [Fact]
    public void One_commit_is_signed_not_signed_or_signed_off_as_asked()
    {
        Assert.Empty(CommitSigning.AsConfigured.After);
        Assert.Equal(["-S", "--signoff"], new CommitSigning(true, SignOff: true).After);
        Assert.Equal(["--no-gpg-sign"], new CommitSigning(false).After);
        Assert.Equal(["-c", "gpg.program=/x/gpg"], new CommitSigning(Config: ["gpg.program=/x/gpg"]).Before);
    }

    [Fact]
    public void A_failure_to_sign_names_its_cause_and_its_fix()
    {
        SigningSetup ssh = new(true, SigningFormat.Ssh, "/k/own.pub", "local");
        SigningSetup gpg = new(true, SigningFormat.OpenPgp, "ABCD", "global");
        Assert.Equal("The commit was not signed: the passphrase given is not that of SSH key own.pub. Commit again and give its passphrase",
            SigningFailure.Of("error: Load key \"/k/own\": incorrect passphrase supplied to decrypt private key?\n\nfatal: failed to write commit object\n", ssh));
        Assert.Equal("The commit was not signed: SSH key own.pub is not there (user.signingkey). Choose a key in Commit Signing…",
            SigningFailure.Of("error: Couldn't load public key /k/own.pub: No such file or directory?\n", ssh));
        Assert.Equal("The commit was not signed: git signs with SSH, but no key is set to sign with. Choose one in Commit Signing…",
            SigningFailure.Of("error: user.signingKey needs to be set for ssh signing\nfatal: failed to write commit object\n", ssh with { Key = null }));
        Assert.Equal("The commit was not signed: SSH key own.pub is to come from an SSH agent, and no agent holds it. Add it to the agent (ssh-add), or choose its file in Commit Signing…",
            SigningFailure.Of("error: Couldn't get agent socket?\n", ssh));
        Assert.Equal("The commit was not signed: gpg is not installed. Install GnuPG, or sign with SSH in Commit Signing…",
            SigningFailure.Of("error: cannot run gpg: No such file or directory\nerror: gpg failed to sign the data:\n(no gpg output)\n", gpg));
        Assert.Equal("The commit was not signed: gpgsm, which signs with X.509 certificates, is not installed. Install GnuPG's gpgsm, or sign with SSH in Commit Signing…",
            SigningFailure.Of("error: cannot run gpgsm: No such file or directory\n", gpg with { Format = SigningFormat.X509 }));
        Assert.Equal("The commit was not signed: there is no secret key for GPG key ABCD. Import it, or choose another in Commit Signing…",
            SigningFailure.Of("gpg: skipped \"ABCD\": No secret key\ngpg: signing failed: No secret key\nerror: gpg failed to sign the data\n", gpg));
        Assert.Equal("The commit was not signed: gpg failed to sign the data: something new",
            SigningFailure.Of("error: gpg failed to sign the data: something new\n", gpg));
        Assert.Null(SigningFailure.Of("error: pathspec 'x' did not match any file(s) known to git\n", gpg));
    }

    [Fact]
    public void The_persons_key_is_listed_once_for_their_email_after_what_the_file_has()
    {
        const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample me@laptop";
        Assert.Equal("me@example.org namespaces=\"git\" ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample\n", AllowedSigners.With(string.Empty, "me@example.org", Key));
        Assert.Equal("# mine\nother@example.org ssh-rsa AAAB\nme@example.org namespaces=\"git\" ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample\n",
            AllowedSigners.With("# mine\nother@example.org ssh-rsa AAAB", "me@example.org", Key));
        Assert.Null(AllowedSigners.With("me@example.org ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample\n", "me@example.org", Key));
        Assert.Null(AllowedSigners.With(string.Empty, "me@example.org", "not a key"));
    }

    [Fact]
    public void A_push_goes_over_ssh_to_the_host_its_url_names()
    {
        Assert.Equal("github.com", AllowedSigners.HostOf("git@github.com:me/shop.git"));
        Assert.Equal("git.example.org", AllowedSigners.HostOf("ssh://git@git.example.org:2222/me/shop.git"));
        Assert.Equal("example.org", AllowedSigners.HostOf("example.org:shop.git"));
        Assert.Null(AllowedSigners.HostOf("https://github.com/me/shop.git"));
        Assert.Null(AllowedSigners.HostOf("/srv/git/shop.git"));
        Assert.Null(AllowedSigners.HostOf(@"C:\git\shop.git"));
        Assert.Null(AllowedSigners.HostOf("../shop/.git"));
    }

    [Fact]
    public void Ssh_keygens_and_passengers_gpgs_prompts_ask_for_a_passphrase_to_sign()
    {
        GitQuestion ssh = GitQuestion.Of("Enter passphrase for \"/home/me/.ssh/id_ed25519\": ");
        Assert.Equal((GitQuestionKind.Passphrase, "/home/me/.ssh/id_ed25519", "id_ed25519", true), (ssh.Kind, ssh.About, ssh.Place, ssh.Signs));
        Assert.Equal("the passphrase of key id_ed25519", ssh.Asks);
        GitQuestion gpg = GitQuestion.Of("Enter passphrase for GPG key Me <me@example.org> (0123ABCD): ");
        Assert.Equal((GitQuestionKind.Passphrase, "Me <me@example.org> (0123ABCD)", true, true), (gpg.Kind, gpg.Place, gpg.Signs, gpg.IsGpg));
        Assert.False(GitQuestion.Of("Enter passphrase for key '/home/me/.ssh/id_ed25519': ").Signs);
        Assert.Equal(("gpg", "ssh-keygen"), (gpg.SignsWith, ssh.SignsWith));
        GitQuestion x509 = GitQuestion.Of("Enter passphrase for X.509 key 0xA1B2C3: ");
        Assert.Equal((GitQuestionKind.Passphrase, "0xA1B2C3", true, "gpgsm"), (x509.Kind, x509.Place, x509.Signs, x509.SignsWith));
    }

    /// <summary>gpgsm is the one git's config names for X.509 (gpg.x509.program), beside gpg's own setting.</summary>
    [Fact]
    public void Gits_config_names_the_gpgsm_an_x509_commit_is_signed_with()
    {
        SigningSetup setup = SigningSetup.Of("local\0gpg.format\nx509\0local\0gpg.x509.program\n/opt/gnupg/bin/gpgsm\0local\0gpg.program\n/usr/bin/gpg\0");
        Assert.Equal((SigningFormat.X509, "/opt/gnupg/bin/gpgsm", "/usr/bin/gpg"), (setup.Format, setup.X509Program, setup.Program));
        Assert.Null(SigningSetup.Of("local\0gpg.format\nx509\0").X509Program);
    }

    /// <summary>Git for Windows' own gpg is found in usr\bin under its top folder from its git.exe, wherever that is: cmd\,
    /// bin\ or mingw64\bin\; none where Git carries none.</summary>
    [Theory]
    [InlineData("cmd")]
    [InlineData("bin")]
    [InlineData("mingw64/bin")]
    public void Git_for_windows_own_gpg_is_found_under_its_top_folder_from_its_git(string at)
    {
        string top = Path.Combine(Path.GetTempPath(), "Git");
        string git = Path.Combine(top, at.Replace('/', Path.DirectorySeparatorChar), "git.exe");
        string gpg = Path.Combine(top, "usr", "bin", "gpg.exe");
        Assert.Equal(gpg, GpgPrograms.Bundled(git, "gpg.exe", path => path == gpg));
        Assert.Null(GpgPrograms.Bundled(git, "gpg.exe", _ => false));
    }
}
