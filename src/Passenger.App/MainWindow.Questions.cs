using Passenger.App.Settings;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>The window's first-time questions (<see cref="FirstTimeQuestions"/>), each asked at its moment: continuous
/// testing at the person's first run of tests in a repository, a save's tidying at their first save of a C# file there, Hot
/// Reload at their first Run or Debug there of a program it reaches, and signing at their first commit git signs nothing of.</summary>
public partial class MainWindow
{
    private FirstTimeQuestions? questions;

    /// <summary>The window's first-time questions, each asked once in its scope.</summary>
    public FirstTimeQuestions Questions => questions ??= MakeQuestions();

    private FirstTimeQuestions MakeQuestions()
    {
        FirstTimeQuestions made = new(Store, () => Unseen, () => settings, ApplySettings);
        made.Changed += (_, question) =>
        {
            if (question == Declared.ContinuousTesting)
            {
                session.Continuous.Turn(made.IsOn(question));
            }
        };
        return made;
    }

    /// <summary>The person's own run of tests asks, the first time in the folder's repository, whether to test continuously.</summary>
    private void AskContinuousTesting() => Questions.Ask(Declared.ContinuousTesting, this);

    /// <summary>The person's first save of a C# file in the folder's repository asks whether a save tidies it, what the saved
    /// file's .editorconfig decides said as its.</summary>
    private void AskSaveFormatting(IEnumerable<OpenDocument> saved)
    {
        if (saved.FirstOrDefault(d => d.Language == Languages.Language.CSharp && !d.IsModified) is { } csharp)
        {
            Questions.Ask(Declared.SaveFormatting, this, new QuestionMoment { Decided = FirstTimeQuestions.DecidedBy(Declared.SaveFormatting, key => csharp.Conventions[key]) });
        }
    }

    /// <summary>The person's first commit that git's config signs nothing of asks, once, whether to sign commits, naming the
    /// first key of theirs found (SSH's first, the one they push with leading, else GPG's) or how to make one, and the git
    /// configs Commit Signing… may set: Set Up… opens it on that key, where they choose where it is set.</summary>
    private async Task AskCommitSigningAsync()
    {
        VcsController vcs = session.Vcs;
        if (!Questions.WouldAsk(Declared.CommitSigning) || vcs.Repository is not { } repository)
        {
            return;
        }
        IReadOnlyList<SigningKey> ssh = await SigningKeys.FindAsync(SigningFormat.Ssh, repository);
        (SigningFormat format, SigningKey? key) = ssh.Count > 0 ? (SigningFormat.Ssh, ssh[0])
            : await SigningKeys.FindAsync(SigningFormat.OpenPgp, null) is [{ } gpg, ..] ? (SigningFormat.OpenPgp, gpg) : (SigningFormat.Ssh, null);
        if (vcs != session.Vcs)
        {
            return;
        }
        string where = $"your global git config ({PathNames.Shown(VcsController.GlobalConfigFile, null)}) or this repository's own (.git/config)";
        string said = key is null
            ? $"{Declared.CommitSigning.Asked} Passenger found no SSH or GPG key of yours: make one with ssh-keygen -t ed25519, then Set Up… "
                + $"signs your commits with it, set in {where}, as you choose there."
            : $"{Declared.CommitSigning.Asked} Passenger found your {(format is SigningFormat.Ssh ? "SSH" : "GPG")} key {key.Shown}: Set Up… signs "
                + $"your commits with it, set in {where}, as you choose there.";
        Questions.Ask(Declared.CommitSigning, this, new QuestionMoment { Asked = said, Yes = () => ShowCommitSigningAsync((format, key?.Value)) });
    }

    /// <summary>The person's first Run or Debug in the folder's repository of a program Hot Reload reaches asks whether Run
    /// runs with it.</summary>
    private void AskHotReload()
    {
        if (Configuration is { } chosen && buildRun.ReachesHotReload(chosen))
        {
            Questions.Ask(Declared.RunWithHotReload, this);
        }
    }
}
