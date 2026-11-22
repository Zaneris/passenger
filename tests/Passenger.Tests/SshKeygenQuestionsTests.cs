using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The SSH keys signing tests make: ssh-keygen asks for a key's passphrase only through the test's askpass, never a
/// console or terminal, and one asking for what the test does not give is ended at once, saying what it asked, where it
/// would have waited out its limit.</summary>
[Collection(Beside.Name)]
public sealed class SshKeygenQuestionsTests : IDisposable
{
    private const string Passphrase = "quay-bell-3";

    private readonly string keys = PathNames.Canonical(TestFolders.Create("ssh-keygen-"));

    public void Dispose() => TestFolders.Delete(keys);

    /// <summary>The tests' own ssh-keygen as one flow, every step needing ssh-keygen, each in an instance of its own: a
    /// key made with a passphrase asked for it twice through the tests' askpass and opening with it, and one asking
    /// more than the test gives ended at once naming what it asked.</summary>
    [Fact]
    public async Task An_ssh_keygen_the_tests_run_asks_only_through_their_askpass_and_ends_at_once_on_a_question_they_do_not_answer()
    {
        await Flow.StepApartAsync("a key made with a passphrase is asked for it twice through the tests askpass and opens with it", () => new SshKeygenQuestionsTests(), t => t.A_key_made_with_a_passphrase_is_asked_for_it_twice_through_the_tests_askpass_and_opens_with_it());
        await Flow.StepApartAsync("an ssh keygen making a key that asks more than the test gives is ended at once naming what it asked", () => new SshKeygenQuestionsTests(), t => t.An_ssh_keygen_making_a_key_that_asks_more_than_the_test_gives_is_ended_at_once_naming_what_it_asked());
    }

    private async Task A_key_made_with_a_passphrase_is_asked_for_it_twice_through_the_tests_askpass_and_opens_with_it()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        string key = await TestSshKeys.MakeAsync(keys, "guarded", Passphrase);

        SshKeygenRun opened = await TestSshKeys.RunAsync(keys, ["-y", "-f", key], [Passphrase]);
        string derived = opened.Succeeded("did not open key guarded given its passphrase").Output;
        Assert.True(Halves(derived) == Halves(TestSshKeys.Public(key)) && opened.Asked.Count == 1,
            $"Key guarded, given its passphrase once, opened as \"{derived.Trim()}\", not as its public half \"{TestSshKeys.Public(key)}\", "
            + $"so a signing test's key is not the one it means: {opened}");
    }

    /// <summary>Given the passphrase once, ssh-keygen making a key asks for it again, which nothing answers: left to itself it
    /// would ask both again for as long as its answers differ, or wait for good on an answer that never comes.</summary>
    private async Task An_ssh_keygen_making_a_key_that_asks_more_than_the_test_gives_is_ended_at_once_naming_what_it_asked()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        string key = Path.Combine(keys, "half");

        SshKeygenRun made = await TestSshKeys.RunAsync(keys, ["-q", "-t", "ed25519", "-C", "half", "-f", key], [Passphrase]);
        Assert.True(made.Unanswered is { } why && why.Contains("Enter same passphrase again", StringComparison.Ordinal),
            $"ssh-keygen asking for key half's passphrase again, which the test does not give, was not ended naming its question, "
            + $"so a test making a key would wait out its limit saying nothing of why: {made}");
    }

    /// <summary>A public key's type and key, as ssh-keygen writes them before its comment.</summary>
    private static string Halves(string line) => string.Join(' ', line.Trim().Split(' ')[..2]);
}
