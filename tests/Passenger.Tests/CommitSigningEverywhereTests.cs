using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Commit Signing… set for all the person's repositories, in a global git config and a config folder of this test's
/// own for its length, alone so that no other test's git reads them.</summary>
[Collection(Alone.Name)]
public sealed class CommitSigningEverywhereTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("signing-everywhere-"));
    private readonly string? global = Environment.GetEnvironmentVariable(GitVariables.ConfigGlobal);
    private readonly string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GitVariables.ConfigGlobal, global);
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
        Environment.SetEnvironmentVariable(GitVariables.ConfigGlobal, config);
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
