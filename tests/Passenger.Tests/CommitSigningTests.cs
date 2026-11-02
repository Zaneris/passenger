using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Signing from the Commit window: whether a commit is signed and with which key as git's config says, Sign
/// turned off for one commit and Sign-off kept for the folder; a key's passphrase asked in Passenger's dialog, a wrong one
/// and a key not there each saying why the commit was not made and how to mend it; and Commit Signing… setting SSH up in
/// the repository's config alone, the key listed as an allowed signer. Every key is the test's own (ssh-keygen in a
/// folder of its own, no agent), the run's empty global git config left as it is.</summary>
public sealed class CommitSigningTests : IDisposable
{
    private readonly List<string> folders = [];

    public void Dispose()
    {
        foreach (string folder in folders)
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void The_commit_window_signs_with_the_key_gits_config_names_turns_signing_off_for_one_commit_and_keeps_sign_off()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        (string root, string key) = Repository(passphrase: null);
        string settings = TestFolders.Create("signing-state-");
        folders.Add(settings);
        MainWindow window = Open(root, settings);
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.SignWith == "Sign with SSH key own.pub" && commit.Sign, $"the Commit window's Sign checked with own.pub, where it says \"{commit.SignWith}\" ({commit.Sign})");

        Commit(window, root, "Signed by its config");
        Assert.True(Show(root, "%G?") == "G", $"The commit made with Sign checked is signed {Show(root, "%G?")}, not with own.pub's good signature");

        Ui.Click(Box(commit, "Sign with"));
        Ui.Click(Box(commit, "Sign-off"));
        Assert.True(!commit.Sign && commit.SignOff, "Sign and Sign-off did not turn over on a click");
        Commit(window, root, "Unsigned, signed off");
        Assert.True(Show(root, "%G?") == "N", $"The commit made with Sign unchecked is signed ({Show(root, "%G?")})");
        Assert.True(Show(root, "%B").Contains("Signed-off-by: Passenger Tests <tests@passenger.invalid>", StringComparison.Ordinal),
            $"The commit made with Sign-off checked has no Signed-off-by line:\n{Show(root, "%B")}");
        Ui.Landed(() => commit.Sign && commit.SignOff, "Sign checked again for the next commit, as git's config has it, and Sign-off kept");
        window.Close();

        MainWindow again = Open(root, settings);
        Ui.Landed(() => again.CommitView.SignOff, "Sign-off kept for the folder once it is opened again");
        again.Close();
    }

    [AvaloniaFact]
    public void A_keys_passphrase_is_asked_in_passengers_dialog_and_a_wrong_one_says_why_the_commit_was_not_made()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        const string Passphrase = "harbour-light-7";
        (string root, _) = Repository(Passphrase);
        MainWindow window = Open(root);
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.SignWith == "Sign with SSH key own.pub", "the Commit window's Sign with own.pub");
        File.WriteAllText(Path.Combine(root, "Shop.cs"), "class Shop\n{\n    int count;\n}\n");
        window.Perform(AppActions.CommitRefresh);
        Ui.Landed(() => commit.Chosen.Count == 1, "Shop.cs's change listed");

        commit.Message = "Signed with a passphrase";
        Task<bool> first = commit.CommitAsync(push: false);
        GitQuestionDialog asked = Asked(window);
        asked.KeyTextInput("not-the-passphrase");
        Ui.Press(asked, "ENTER");
        Ui.Landed(() => first.IsCompleted, "the commit with the wrong passphrase");
        string wrong = "The commit was not signed: the passphrase given is not that of SSH key own.pub. Commit again and give its passphrase";
        Assert.True(commit.Unsigned == wrong && window.Status == wrong,
            $"With the wrong passphrase the Commit window says \"{commit.Unsigned}\" and the status bar \"{window.Status}\", not \"{wrong}\"");
        Assert.True(Show(root, "%s") == "First", $"A commit was made with the wrong passphrase: {Show(root, "%s")}");

        Task<bool> second = commit.CommitAsync(push: false);
        asked = Asked(window);
        asked.KeyTextInput(Passphrase);
        Ui.Press(asked, "ENTER");
        Ui.Landed(() => second.IsCompleted, "the commit with the passphrase");
        Assert.True(Show(root, "%s %G?") == "Signed with a passphrase G", $"The commit given its key's passphrase is {Show(root, "%s %G?")}, not signed good");
        Assert.True(commit.Unsigned is null, $"The Commit window still says \"{commit.Unsigned}\" once the commit is made");
        Assert.False(Ui.IdeLog().Contains(Passphrase, StringComparison.Ordinal), "The passphrase typed is in the IDE's log");
        window.Close();
    }

    [AvaloniaFact]
    public void A_cherry_pick_is_signed_with_the_keys_passphrase_asked_in_passengers_dialog()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        const string Passphrase = "spindle-cove-2";
        (string root, _) = Repository(Passphrase);
        TestGit.Run(root, "switch", "-q", "-c", "side");
        File.WriteAllText(Path.Combine(root, "Side.md"), "side\n");
        TestGit.Run(root, "add", "-A");
        TestGit.Run(root, "-c", "commit.gpgsign=false", "commit", "-q", "-m", "Side");
        string picked = TestGit.Run(root, "rev-parse", "HEAD").Trim();
        TestGit.Run(root, "switch", "-q", "main");
        MainWindow window = Open(root);

        Task<VcsOutcome> picking = window.Vcs.CherryPickAsync(picked);
        GitQuestionDialog asked = Asked(window);
        asked.KeyTextInput(Passphrase);
        Ui.Press(asked, "ENTER");
        Ui.Landed(() => picking.IsCompleted, "the cherry-pick given the key's passphrase");
        Assert.True(Show(root, "%s %G?") == "Side G", $"The cherry-pick given its key's passphrase made \"{Show(root, "%s %G?")}\", where the status bar says \"{window.Status}\"");
        window.Close();
    }

    [AvaloniaFact]
    public void A_gpg_keys_passphrase_is_asked_in_passengers_dialog_through_its_own_gpg_and_signs_the_commit()
    {
        Assert.SkipWhen(TestGpgKeys.WhyNot is not null, TestGpgKeys.WhyNot ?? string.Empty);
        const string Passphrase = "lantern-quay-4";
        string root = PathNames.Canonical(TestFolders.Create("signing-gpg-"));
        folders.Add(root);
        File.WriteAllText(Path.Combine(root, "Shop.cs"), "class Shop\n{\n}\n");
        TestGit.Init(root);
        string id = Ui.AwaitPumped(Waiting.Apart(() => TestGpgKeys.MakeAsync(Passphrase)), "the test's GPG key", TimeSpan.FromMinutes(2));
        TestGit.Run(root, "config", "user.signingkey", id);
        TestGit.Run(root, "config", "commit.gpgsign", "true");
        try
        {
            MainWindow window = Open(root);
            CommitView commit = window.CommitView;
            Ui.Landed(() => commit.SignWith == $"Sign with GPG key {id}" && commit.Sign, $"the Commit window's Sign with GPG key {id}, where it says \"{commit.SignWith}\"");
            File.WriteAllText(Path.Combine(root, "Shop.cs"), "class Shop\n{\n    int count;\n}\n");
            window.Perform(AppActions.CommitRefresh);
            Ui.Landed(() => commit.Chosen.Count == 1, "Shop.cs's change listed");
            commit.Message = "Signed with GPG";
            Task<bool> committing = commit.CommitAsync(push: false);
            Ui.PumpUntil(() => window.GitQuestion is { IsVisible: true }, $"the window to ask for GPG key {id}'s passphrase, where the status bar says \"{window.Status}\"");
            GitQuestionDialog asked = DialogButtonsTests.Check(window.GitQuestion!);
            Assert.True((asked.Title ?? string.Empty).StartsWith("Passphrase for Passenger Tests <tests@passenger.invalid>", StringComparison.Ordinal), $"The window asks \"{asked.Title}\"");
            asked.KeyTextInput(Passphrase);
            Ui.Press(asked, "ENTER");
            Ui.Landed(() => committing.IsCompleted, "the commit given the GPG key's passphrase");
            Assert.True(Show(root, "%s %G?") == "Signed with GPG G", $"The commit given its GPG key's passphrase is \"{Show(root, "%s %G?")}\", where the status bar says \"{window.Status}\"");
            Assert.False(Ui.IdeLog().Contains(Passphrase, StringComparison.Ordinal), "The passphrase typed is in the IDE's log");
            window.Close();
        }
        finally
        {
            Ui.AwaitPumped(Waiting.Apart(TestGpgKeys.EndAgentAsync), "the run's gpg-agent ended", TimeSpan.FromMinutes(1));
        }
    }

    [AvaloniaFact]
    public void A_signing_key_that_is_not_there_fails_the_commit_naming_the_key_and_the_way_to_mend_it()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        (string root, string key) = Repository(passphrase: null);
        TestGit.Run(root, "config", "user.signingkey", Path.Combine(Path.GetDirectoryName(key)!, "gone.pub"));
        MainWindow window = Open(root);
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.SignWith == "Sign with SSH key gone.pub", "the Commit window's Sign with gone.pub");
        File.WriteAllText(Path.Combine(root, "Shop.cs"), "class Shop\n{\n    int count;\n}\n");
        window.Perform(AppActions.CommitRefresh);
        Ui.Landed(() => commit.Chosen.Count == 1, "Shop.cs's change listed");
        commit.Message = "Signed with a key not there";
        Task<bool> committing = commit.CommitAsync(push: false);
        Ui.Landed(() => committing.IsCompleted, "the commit with a key not there");
        string said = "The commit was not signed: SSH key gone.pub is not there (user.signingkey). Choose a key in Commit Signing…";
        Assert.True(commit.Unsigned == said && window.Status == said,
            $"With its key not there the Commit window says \"{commit.Unsigned}\" and the status bar \"{window.Status}\", not \"{said}\"");
        window.Close();
    }

    [AvaloniaFact]
    public void Commit_signing_sets_ssh_up_in_the_repositorys_config_alone_listing_the_key_as_an_allowed_signer()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        string root = PathNames.Canonical(TestFolders.Create("signing-setup-"));
        string keys = PathNames.Canonical(TestFolders.Create("signing-setup-keys-"));
        folders.Add(root);
        folders.Add(keys);
        File.WriteAllText(Path.Combine(root, "Shop.cs"), "class Shop\n{\n}\n");
        TestGit.Init(root);
        string key = Ui.AwaitPumped(Waiting.Apart(() => TestSshKeys.MakeAsync(keys, "mine")), "the test's key", TimeSpan.FromMinutes(1));
        string global = File.ReadAllText(Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!);

        MainWindow window = Open(root);
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.SignWith == "Sign: not set up" && !commit.CanSign, $"the Commit window with no key to sign with, where it says \"{commit.SignWith}\"");
        Assert.True(window.Perform(AppActions.CommitSigning), window.Status);
        Ui.PumpUntil(() => window.CommitSigning is { IsVisible: true }, "Commit Signing's dialog");
        CommitSigningDialog dialog = DialogButtonsTests.Check(window.CommitSigning!);
        Assert.True(dialog.Format is Passenger.Vcs.SigningFormat.Ssh, $"Commit Signing offers {dialog.Format} first, not SSH");
        TextBox field = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SigningKey");
        Ui.Click(field);
        field.SelectAll();
        dialog.KeyTextInput(key + ".pub");
        Ui.Click(dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Content as string == "This repository"));
        Assert.True(dialog.SignEvery, "Sign every commit is not checked to begin with");
        Ui.ClickButton(dialog, "OK");
        Ui.Landed(() => commit.SignWith == "Sign with SSH key mine.pub" && commit.Sign, $"the Commit window's Sign checked with mine.pub, where it says \"{commit.SignWith}\"");

        string Local(string name) => TestGit.Run(root, "config", "--local", "--get", name).Trim();
        string signers = Path.Combine(root, ".git", "allowed_signers");
        Assert.Equal(["ssh", key + ".pub", "true", signers],
            [Local("gpg.format"), Local("user.signingkey"), Local("commit.gpgsign"), PathNames.Canonical(Local("gpg.ssh.allowedSignersFile"))]);
        string publicKey = string.Join(' ', TestSshKeys.Public(key).Split(' ')[..2]);
        Assert.True(File.ReadAllText(signers) == $"tests@passenger.invalid namespaces=\"git\" {publicKey}\n",
            $"The repository's allowed signers file holds\n{File.ReadAllText(signers)}\nnot the committer's email and key");
        Assert.True(File.ReadAllText(Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!) == global, "Commit Signing set in this repository changed the global git config");

        Commit(window, root, "Signed as set up");
        Assert.True(Show(root, "%G? %GS") == "G tests@passenger.invalid", $"The commit after Commit Signing is signed \"{Show(root, "%G? %GS")}\", not good by its committer");
        window.Close();
    }

    /// <summary>A repository whose own config signs every commit with the test's key own (guarded by a passphrase where
    /// one is given), listed as tests@passenger.invalid's in an allowed signers file beside it; the repository and the key.</summary>
    private (string Root, string Key) Repository(string? passphrase)
    {
        string root = PathNames.Canonical(TestFolders.Create("signing-"));
        string keys = PathNames.Canonical(TestFolders.Create("signing-keys-"));
        folders.Add(root);
        folders.Add(keys);
        string key = Ui.AwaitPumped(Waiting.Apart(async () =>
        {
            string made = passphrase is null ? await TestSshKeys.MakeAsync(keys, "own") : await TestSshKeys.MakeAsync(keys, "own", passphrase);
            await TestSshKeys.SigningRepositoryAsync(root, made);
            return made;
        }), "the signing repository", TimeSpan.FromMinutes(3));
        return (root, key);
    }

    /// <summary>A window on the repository with the Commit window shown; given a state folder, one that keeps its state there.</summary>
    private static MainWindow Open(string root, string? settings = null)
    {
        MainWindow window;
        if (settings is null)
        {
            window = Ui.OpenFolder(root, languageServer: false);
        }
        else
        {
            window = new MainWindow { Width = 1200, Height = 800, StartsLanguageServer = false, StateFolder = settings };
            window.Show();
            window.KeepLayout();
            window.OpenFolder(root);
            Ui.Settle();
        }
        window.Perform(AppActions.ShowCommit);
        return window;
    }

    /// <summary>A change committed from the Commit window, its message typed and Commit pressed, once the commit is made.</summary>
    private static void Commit(MainWindow window, string root, string message)
    {
        CommitView commit = window.CommitView;
        File.AppendAllText(Path.Combine(root, "Shop.cs"), $"// {message}\n");
        window.Perform(AppActions.CommitRefresh);
        Ui.Landed(() => commit.Chosen.Count == 1, "Shop.cs's change listed");
        commit.Message = message;
        Ui.ClickButton(commit, "Commit");
        Ui.Landed(() => Show(root, "%s") == message, $"the commit \"{message}\", where the status bar says \"{window.Status}\"");
    }

    /// <summary>The last commit as a git log format shows it.</summary>
    private static string Show(string root, string format) => TestGit.Run(root, "log", "-1", $"--format={format}").Trim();

    /// <summary>One of the Commit window's boxes, by the words it begins with.</summary>
    private static CheckBox Box(CommitView commit, string words) =>
        commit.GetVisualDescendants().OfType<CheckBox>().Single(c => (c.Content as string ?? (c.Content as TextBlock)?.Text ?? string.Empty).StartsWith(words, StringComparison.Ordinal));

    /// <summary>The window's question for the key's passphrase, once it shows, held to the dialogs' rule.</summary>
    private static GitQuestionDialog Asked(MainWindow window)
    {
        Ui.PumpUntil(() => window.GitQuestion is { IsVisible: true }, "the window to ask for the key's passphrase");
        GitQuestionDialog asked = DialogButtonsTests.Check(window.GitQuestion!);
        Assert.Equal("Passphrase for own", asked.Title);
        return asked;
    }
}
