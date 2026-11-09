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
/// turned off for one commit and Sign-off kept for the folder; a key's passphrase asked in Passenger's dialog (an SSH key's,
/// a GPG key's through gpg, Git for Windows' own on Windows, and an X.509 certificate's through gpgsm), a wrong one
/// and a key not there each saying why the commit was not made and how to mend it; and Commit Signing… setting SSH up in
/// the repository's config alone, the key listed as an allowed signer; a quit while the dialog asks refusing the
/// passphrase, so git ends on its own, its index.lock gone. Every key is the test's own (ssh-keygen in a folder of its
/// own, no agent), the run's empty global git config left as it is.</summary>
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
    public void Quitting_while_git_waits_for_a_keys_passphrase_refuses_it_so_git_ends_on_its_own_leaving_no_index_lock()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        (string root, _) = Repository("tidal-gate-9");
        MainWindow window = Open(root);
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.SignWith == "Sign with SSH key own.pub", "the Commit window's Sign with own.pub");
        File.WriteAllText(Path.Combine(root, "Shop.cs"), "class Shop\n{\n    int count;\n}\n");
        window.Perform(AppActions.CommitRefresh);
        Ui.Landed(() => commit.Chosen.Count == 1, "Shop.cs's change listed");
        commit.Message = "Never made";
        Task<bool> committing = commit.CommitAsync(push: false);
        Asked(window);
        string held = Path.Combine(root, ".git", "index.lock");
        Assert.True(File.Exists(held), "git holds no index.lock while it waits for the passphrase, so its end proves nothing");

        Ui.CloseAsQuit(window);
        // The process waits for the window's end with its loop stopped, the window's thread running nothing, as Main's end does.
        Ui.Idle(window.Ended!, "what the closed window started to end", MainWindow.EndLimit + TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(held), "git still held .git/index.lock once the window's end was over: the process's end kills it holding the lock, "
            + "and every git after fails until the person deletes it");
        Assert.True(Show(root, "%s") == "First", $"A commit was made with its passphrase never given: {Show(root, "%s")}");
        Assert.False(Ui.Await(committing, "the commit's end, heard once the window's thread runs again"), "The commit whose passphrase was refused says it was made");
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
        TestGit.Run(root, "config", Passenger.Vcs.GitConfigKeys.UserSigningKey, id);
        TestGit.Run(root, "config", Passenger.Vcs.GitConfigKeys.CommitGpgSign, "true");
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
            Ui.AwaitPumped(Waiting.Apart(() => TestGpgKeys.EndAgentAsync()), "the run's gpg-agent ended", TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>With no gpg on the PATH but Git for Windows' own, Passenger's gpg wraps that one: the key's passphrase is asked
    /// in Passenger's dialog, never git's pinentry, and the commit is signed.</summary>
    [AvaloniaFact]
    public void Git_for_windows_own_gpg_asks_its_keys_passphrase_in_passengers_dialog_and_signs_the_commit()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Git for Windows' own gpg is Windows'");
        Assert.SkipWhen(TestGpgKeys.Bundled is null, TestGpgKeys.Gpg is not null ? "A gpg is on the PATH, which git takes before its own"
            : "Git for Windows on the PATH carries no gpg in its usr\\bin");
        Signs("signing-bundled-", "lantern-quay-5", () => TestGpgKeys.MakeAsync("lantern-quay-5", TestGpgKeys.Bundled), [],
            "Passphrase for Passenger Tests <tests@passenger.invalid>", "G", () => TestGpgKeys.EndAgentAsync(TestGpgKeys.Bundled));
    }

    /// <summary>An X.509 certificate's key, signed with through gpgsm: its passphrase asked in Passenger's dialog, as gpgsm
    /// takes one only as it starts, and the commit signed.</summary>
    [AvaloniaFact]
    public void An_x509_keys_passphrase_is_asked_in_passengers_dialog_through_gpgsm_and_signs_the_commit()
    {
        Assert.SkipWhen(TestX509Keys.WhyNot is not null, TestX509Keys.WhyNot ?? string.Empty);
        string made = PathNames.Canonical(TestFolders.Create("signing-x509-keys-"));
        folders.Add(made);
        Signs("signing-x509-", "harbour-gate-6", () => TestX509Keys.MakeAsync("harbour-gate-6", made), [Passenger.Vcs.GitConfigKeys.GpgFormat, "x509"], "Passphrase for 0x", null,
            () => TestGpgKeys.EndAgentAsync(TestX509Keys.Gpgsm));
    }

    /// <summary>A commit of the Commit window signed with a key the test makes, the repository given <paramref name="config"/>
    /// (pairs of name and value) beside the key: its passphrase asked in Passenger's dialog, titled as <paramref name="asked"/>
    /// begins, and the commit signed, %G? giving <paramref name="verified"/> where given, its signature in the commit else.</summary>
    private void Signs(string name, string passphrase, Func<Task<string>> make, string[] config, string asked, string? verified, Func<Task> ended)
    {
        string root = PathNames.Canonical(TestFolders.Create(name));
        folders.Add(root);
        File.WriteAllText(Path.Combine(root, "Shop.cs"), "class Shop\n{\n}\n");
        TestGit.Init(root);
        string id = Ui.AwaitPumped(Waiting.Apart(make), "the test's signing key", TimeSpan.FromMinutes(2));
        TestGit.Run(root, "config", Passenger.Vcs.GitConfigKeys.UserSigningKey, id);
        TestGit.Run(root, "config", Passenger.Vcs.GitConfigKeys.CommitGpgSign, "true");
        for (int at = 0; at + 1 < config.Length; at += 2)
        {
            TestGit.Run(root, "config", config[at], config[at + 1]);
        }
        try
        {
            MainWindow window = Open(root);
            CommitView commit = window.CommitView;
            Ui.Landed(() => commit.Sign, $"the Commit window to sign, where it says \"{commit.SignWith}\"");
            File.WriteAllText(Path.Combine(root, "Shop.cs"), "class Shop\n{\n    int count;\n}\n");
            window.Perform(AppActions.CommitRefresh);
            Ui.Landed(() => commit.Chosen.Count == 1, "Shop.cs's change listed");
            commit.Message = "Signed";
            Task<bool> committing = commit.CommitAsync(push: false);
            Ui.PumpUntil(() => window.GitQuestion is { IsVisible: true }, $"the window to ask for the key's passphrase, where the status bar says \"{window.Status}\"");
            GitQuestionDialog question = DialogButtonsTests.Check(window.GitQuestion!);
            Assert.True((question.Title ?? string.Empty).StartsWith(asked, StringComparison.Ordinal), $"The window asks \"{question.Title}\"");
            question.KeyTextInput(passphrase);
            Ui.Press(question, "ENTER");
            Ui.Landed(() => committing.IsCompleted, "the commit given the key's passphrase");
            if (verified is not null)
            {
                Assert.True(Show(root, "%s %G?") == $"Signed {verified}", $"The commit given its key's passphrase is \"{Show(root, "%s %G?")}\", where the status bar says \"{window.Status}\"");
            }
            else
            {
                string made = TestGit.Run(root, "cat-file", "commit", "HEAD");
                Assert.True(made.Contains("gpgsig -----BEGIN SIGNED MESSAGE-----", StringComparison.Ordinal), $"The commit given its key's passphrase carries no signature ({window.Status}):\n{made}");
            }
            Assert.False(Ui.IdeLog().Contains(passphrase, StringComparison.Ordinal), "The passphrase typed is in the IDE's log");
            window.Close();
        }
        finally
        {
            Ui.AwaitPumped(Waiting.Apart(ended), "the run's gpg-agent ended", TimeSpan.FromMinutes(1));
        }
    }

    [AvaloniaFact]
    public void A_signing_key_that_is_not_there_fails_the_commit_naming_the_key_and_the_way_to_mend_it()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        (string root, string key) = Repository(passphrase: null);
        TestGit.Run(root, "config", Passenger.Vcs.GitConfigKeys.UserSigningKey, Path.Combine(Path.GetDirectoryName(key)!, "gone.pub"));
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
            [Local(Passenger.Vcs.GitConfigKeys.GpgFormat), Local(Passenger.Vcs.GitConfigKeys.UserSigningKey), Local(Passenger.Vcs.GitConfigKeys.CommitGpgSign), PathNames.Canonical(Local(Passenger.Vcs.GitConfigKeys.GpgSshAllowedSignersFile))]);
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

/// <summary>Commit Signing… set for all the person's repositories, in a global git config and a config folder of this test's
/// own for its length, alone so that no other test's git reads them.</summary>
[Collection(Alone.Name)]
public sealed class CommitSigningEverywhereTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("signing-everywhere-"));
    private readonly string? global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
    private readonly string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);
        OwnEnvironment.Set("XDG_CONFIG_HOME", configHome);
        TestFolders.Delete(folder);
    }

    [AvaloniaFact]
    public void Commit_signing_for_all_repositories_names_a_signers_file_every_repository_reads_whatever_this_one_names()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        string root = Directory.CreateDirectory(Path.Combine(folder, "shop")).FullName;
        string other = Directory.CreateDirectory(Path.Combine(folder, "other")).FullName;
        string keys = Directory.CreateDirectory(Path.Combine(folder, "keys")).FullName;
        string config = Path.Combine(folder, "gitconfig");
        File.WriteAllText(config, string.Empty);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(folder, "config"));
        File.WriteAllText(Path.Combine(root, "Shop.cs"), "class Shop\n{\n}\n");
        File.WriteAllText(Path.Combine(other, "Other.cs"), "class Other\n{\n}\n");
        TestGit.Init(root);
        TestGit.Init(other);
        string local = Path.Combine(folder, "shop_signers");
        File.WriteAllText(local, string.Empty);
        TestGit.Run(root, "config", "--local", Passenger.Vcs.GitConfigKeys.GpgSshAllowedSignersFile, local);
        string key = Ui.AwaitPumped(Waiting.Apart(() => TestSshKeys.MakeAsync(keys, "mine")), "the test's key", TimeSpan.FromMinutes(1));

        MainWindow window = Ui.OpenFolder(root, languageServer: false);
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        Assert.True(window.Perform(AppActions.CommitSigning), window.Status);
        Ui.PumpUntil(() => window.CommitSigning is { IsVisible: true }, "Commit Signing's dialog");
        CommitSigningDialog dialog = DialogButtonsTests.Check(window.CommitSigning!);
        TextBox field = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SigningKey");
        Ui.Click(field);
        field.SelectAll();
        dialog.KeyTextInput(key + ".pub");
        Ui.Click(dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Content as string == "All your repositories (your global git config)"));
        Ui.ClickButton(dialog, "OK");
        Ui.Landed(() => commit.SignWith == "Sign with SSH key mine.pub", $"the Commit window signing with mine.pub, where it says \"{commit.SignWith}\"");

        string signers = TestGit.Run(other, "config", "--type=path", "--get", Passenger.Vcs.GitConfigKeys.GpgSshAllowedSignersFile).Trim();
        Assert.True(signers.Length > 0 && !PathNames.Same(signers, local), $"Another repository reads the allowed signers \"{signers}\", so it could not verify a commit signed with the key");
        string publicKey = string.Join(' ', TestSshKeys.Public(key).Split(' ')[..2]);
        Assert.True(File.ReadAllText(signers).Contains(publicKey, StringComparison.Ordinal), $"The global allowed signers file holds\n{File.ReadAllText(signers)}\nnot the key");
        Assert.True(File.ReadAllText(local).Length == 0, "Commit Signing for all repositories wrote the file this repository alone reads");
        window.Close();
    }
}
