using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Each commit's signature in the Git log: a good one, one by a signer no allowed signers file lists, and none
/// marked as a person sees them, each mark's tip and the commit's details naming the signer and the key. The keys are the
/// test's own (ssh-keygen, no passphrase, no agent), set in the repository's own config alone.</summary>
public sealed class GitSignaturesTests : IDisposable
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
    public void The_log_marks_a_good_signature_and_an_unknown_signers_and_none_on_an_unsigned_commit_its_details_naming_signer_and_key()
    {
        Assert.SkipWhen(TestSshKeys.WhyNot is not null, TestSshKeys.WhyNot ?? string.Empty);
        string root = PathNames.Canonical(TestFolders.Create("signatures-"));
        string keys = PathNames.Canonical(TestFolders.Create("signatures-keys-"));
        folders.Add(root);
        folders.Add(keys);
        (string known, string unknown) = Ui.AwaitPumped(Waiting.Apart(() => TestSshKeys.SignedRepositoryAsync(root, keys)), "the signed repository", TimeSpan.FromMinutes(3));

        MainWindow window = Ui.OpenFolder(root, languageServer: false);
        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Count == 3, "the log's three commits");
        ListBox list = log.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IReadOnlyList<GraphRow>);
        string knownKey = Ui.AwaitPumped(Waiting.Apart(() => TestSshKeys.FingerprintAsync(known)), "the known key's fingerprint", TimeSpan.FromMinutes(1));
        string unknownKey = Ui.AwaitPumped(Waiting.Apart(() => TestSshKeys.FingerprintAsync(unknown)), "the unknown key's fingerprint", TimeSpan.FromMinutes(1));

        Ui.Landed(() => Mark(list, log, "Signed by a known signer").Key == IconKeys.SignatureGood, "the known signer's commit marked good");
        (string? key, string? tip) = Mark(list, log, "Signed by a known signer");
        Assert.True(tip == $"Good SSH signature by tests@passenger.invalid, key {knownKey}",
            $"The good signature's mark tells \"{tip}\", where it is tests@passenger.invalid's with key {knownKey}");

        Ui.Landed(() => Mark(list, log, "Signed by an unknown signer").Key == IconKeys.SignatureUnverified, "the unknown signer's commit marked unverified");
        (key, tip) = Mark(list, log, "Signed by an unknown signer");
        Assert.True(tip == $"Unverified SSH signature, key {unknownKey}: no allowed signers file lists its signer",
            $"The unknown signer's mark tells \"{tip}\", where its key {unknownKey} is in no allowed signers file");

        (key, tip) = Mark(list, log, "First");
        Assert.True(key is null && tip is null, $"The unsigned commit is marked {key} (\"{tip}\"), where it has no signature");

        Choose(log, "Signed by a known signer");
        Assert.True(log.Details.EndsWith($"\nGood SSH signature by tests@passenger.invalid, key {knownKey}", StringComparison.Ordinal),
            $"The known signer's commit's details end\n{log.Details}\nwithout its good signature, its signer and its key");
        Choose(log, "Signed by an unknown signer");
        Assert.True(log.Details.EndsWith($"\nUnverified SSH signature, key {unknownKey}: no allowed signers file lists its signer", StringComparison.Ordinal),
            $"The unknown signer's commit's details end\n{log.Details}\nwithout its unverified signature and its key");
        Choose(log, "First");
        Assert.True(!log.Details.Contains("signature", StringComparison.OrdinalIgnoreCase),
            $"The unsigned commit's details speak of a signature:\n{log.Details}");
        window.Close();
    }

    /// <summary>The mark a commit's row shows beside its subject (its icon's key and its tip), or nulls where it shows none.</summary>
    private static (string? Key, string? Tip) Mark(ListBox list, GitLogView log, string subject)
    {
        Control row = Ui.Row(list, log.Rows.First(r => r.Commit.Subject == subject));
        string[] marks = [IconKeys.SignatureGood, IconKeys.SignatureUnverified, IconKeys.SignatureBad, IconKeys.SignatureExpired];
        RowIcon? mark = row.GetVisualDescendants().OfType<RowIcon>().FirstOrDefault(i => i.IsEffectivelyVisible && marks.Contains(i.DataKey));
        return (mark?.DataKey, mark is null ? null : ToolTip.GetTip(mark) as string);
    }

    private static void Choose(GitLogView log, string subject)
    {
        log.Select(log.Rows.First(r => r.Commit.Subject == subject).Commit);
        Ui.Landed(() => log.Details.StartsWith(subject, StringComparison.Ordinal), $"the details of \"{subject}\"");
    }
}
