using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>What git and ssh ask their askpass helper, read from the prompt: a login's user and password for a URL (the
/// password then named with the user given), a key's passphrase, ssh's password for a user at a host, whether to trust a
/// host, and anything else kept secret; each named by its place.</summary>
public class GitQuestionTests
{
    [Fact]
    public void What_git_and_ssh_ask_is_read_from_the_prompt_by_its_kind_and_place_a_secret_kept_secret() => Cases.All(
        ("gits login is a user then a password for the url with that user", Gits_login_is_a_user_then_a_password_for_the_url_with_that_user),
        ("sshs passphrase password and host question name the key the user and the host", Sshs_passphrase_password_and_host_question_name_the_key_the_user_and_the_host),
        ("any other prompt is asked in its own words and kept secret", Any_other_prompt_is_asked_in_its_own_words_and_kept_secret));

    private void Gits_login_is_a_user_then_a_password_for_the_url_with_that_user()
    {
        GitQuestion user = GitQuestion.Of("Username for 'https://git.example.com:8443': ");
        Assert.Equal(new GitQuestion(GitQuestionKind.User, "Username for 'https://git.example.com:8443': ", "https://git.example.com:8443"), user);
        Assert.Equal("git.example.com:8443", user.Place);
        Assert.False(user.IsSecret);
        Assert.Equal("https://me@example.org@git.example.com:8443", user.WithUser("me@example.org"));

        GitQuestion password = GitQuestion.Of("Password for 'https://me@example.org@git.example.com:8443': ");
        Assert.Equal((GitQuestionKind.Password, "https://me@example.org@git.example.com:8443", "me@example.org"), (password.Kind, password.About, password.User));
        Assert.Equal("git.example.com:8443", password.Place);
        Assert.True(password.IsSecret);
        Assert.Null(password.WithUser("someone"));

        GitQuestion bare = GitQuestion.Of("Password for 'http://127.0.0.1:48765': ");
        Assert.Equal((GitQuestionKind.Password, null), (bare.Kind, bare.User));
    }

    private void Sshs_passphrase_password_and_host_question_name_the_key_the_user_and_the_host()
    {
        GitQuestion key = GitQuestion.Of("Enter passphrase for key '/Users/me/.ssh/id_ed25519': ");
        Assert.Equal((GitQuestionKind.Passphrase, "/Users/me/.ssh/id_ed25519", "id_ed25519"), (key.Kind, key.About, key.Place));
        Assert.True(key.IsSecret);

        GitQuestion password = GitQuestion.Of("git@git.example.com's password: ");
        Assert.Equal((GitQuestionKind.Password, "git.example.com", "git"), (password.Kind, password.About, password.User));

        GitQuestion host = GitQuestion.Of("The authenticity of host 'git.example.com (192.0.2.7)' can't be established.\n"
            + "ED25519 key fingerprint is SHA256:abc.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ");
        Assert.Equal((GitQuestionKind.YesNo, "git.example.com"), (host.Kind, host.Place));
        Assert.False(host.IsSecret);
    }

    private void Any_other_prompt_is_asked_in_its_own_words_and_kept_secret()
    {
        GitQuestion other = GitQuestion.Of("Benutzername für 'https://git.example.com': ");
        Assert.Equal((GitQuestionKind.Other, null, "the remote"), (other.Kind, other.About, other.Place));
                Assert.True(other.IsSecret);
    }
}
