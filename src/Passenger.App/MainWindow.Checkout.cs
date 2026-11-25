using Avalonia.Controls;
using Passenger.App.Settings;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>The person's checkouts (a branch's Checkout, Checkout Revision, a new branch made where another is) with
/// changes not yet committed: brought along as git carries them, stashed, or discarded once the person agrees, as Settings
/// says or the person answers, the question saying where git cannot bring them; and a stash a checkout made, as the person
/// asked, unstashed as the branch it left is checked out again.</summary>
public partial class MainWindow
{
    /// <summary>The ways the question of a checkout's uncommitted changes offers, by their buttons' words.</summary>
    public const string BringChanges = "Bring";
    public const string StashChanges = "Stash";
    public const string DiscardUncommitted = "Discard…";

    /// <summary>The question of a checkout's uncommitted changes, while it is asked.</summary>
    public ChoiceDialog? ChangesQuestion { get; private set; }

    /// <summary>A branch's or tag's Checkout, from the branches panel or a branch menu.</summary>
    public Task<VcsOutcome?> CheckoutAsync(BranchInfo branch, IReadOnlyList<BranchInfo> all) =>
        SwitchWithChangesAsync(branch.Kind == RefKind.Remote && VcsController.LocalOf(branch, all) is { } local ? local : branch.Name,
            VcsController.TargetOf(branch, all), () => session.Vcs.CheckoutRefAsync(branch, all));

    /// <summary>A commit of the Git log checked out on its own (a detached head).</summary>
    public Task<VcsOutcome?> CheckoutRevisionAsync(CommitInfo commit) =>
        SwitchWithChangesAsync(commit.Short, commit.Hash, () => session.Vcs.CheckoutRevisionAsync(commit.Hash));

    /// <summary>A checkout to <paramref name="target"/> (shown as <paramref name="to"/>) by <paramref name="checkout"/>, what
    /// is open saved first: with nothing uncommitted but new files git can bring along, it runs as it is; else they go as
    /// Settings says or the person answers (<see cref="AskChangesAsync"/>), never asked where nobody is or an agent's input
    /// ran it. What it came to; null where the person cancelled.</summary>
    private async Task<VcsOutcome?> SwitchWithChangesAsync(string to, string target, Func<Task<VcsOutcome>> checkout)
    {
        VcsController vcs = session.Vcs;
        if (vcs.Repository is null)
        {
            return await checkout();
        }
        (IReadOnlyList<FileChange> changes, string? unsaved) = await vcs.ChangesSavedAsync();
        if (unsaved is not null)
        {
            status.Text = unsaved;
            return new VcsOutcome(false, unsaved);
        }
        ChangesInTheWay? inTheWay = changes.Count == 0 ? null : await vcs.InTheWayAsync(target);
        CheckoutChanges way = changes.All(c => c.Kind is ChangeKind.Unversioned) && inTheWay is null ? CheckoutChanges.Bring : settings.ChangesAtCheckout;
        if (way == CheckoutChanges.Bring && inTheWay is not null)
        {
            way = CheckoutChanges.Ask;
        }
        if (way == CheckoutChanges.Ask && (Unseen || AgentsOwn || AgentAsks.Beside || AgentAsks.Inputting))
        {
            way = CheckoutChanges.Bring;
        }
        string? from = vcs.Branch;
        if (way == CheckoutChanges.Ask)
        {
            (string? answer, bool back) = await AskChangesAsync(to, from, changes, inTheWay);
            switch (answer)
            {
                case null:
                    return null;
                case DiscardUncommitted:
                    return await DiscardThenAsync(to, changes, checkout);
                default:
                    way = answer == BringChanges ? CheckoutChanges.Bring : back ? CheckoutChanges.StashAndReturn : CheckoutChanges.Stash;
                    break;
            }
        }
        if (way == CheckoutChanges.Bring)
        {
            return await ReturnedAsync(await checkout());
        }
        (VcsOutcome switched, StashInfo? stash) = await vcs.StashedCheckoutAsync(to, checkout);
        if (stash is not null && from is not null && way == CheckoutChanges.StashAndReturn)
        {
            vcs.ReturnOnCheckout(from, stash);
        }
        return await ReturnedAsync(switched);
    }

    /// <summary>The question of a checkout's uncommitted changes: Bring, Stash, Discard or Cancel, Bring greyed saying why
    /// where git cannot bring them, the files listed, a box asking that a stash be unstashed on returning to
    /// <paramref name="from"/>, and Don't ask again, which keeps Bring or Stash in Settings. The answer's words, null for
    /// Cancel, and whether the box was ticked.</summary>
    private async Task<(string? Answer, bool Back)> AskChangesAsync(string to, string? from, IReadOnlyList<FileChange> changes, ChangesInTheWay? inTheWay)
    {
        string what = changes.Count == 1 ? changes[0].Name : $"these {Numbers.Said(changes.Count)} files";
        string? cannot = inTheWay is null ? null : Refusals.CannotBring(to, [.. inTheWay.Files.Select(Path.GetFileName).OfType<string>()], inTheWay.Said);
        CheckBox back = new() { Content = $"Unstash them as {from} is checked out again", IsChecked = settings.ChangesAtCheckout != CheckoutChanges.Stash, IsVisible = from is not null };
        List<Control> detail = [ToolWindows.CommitView.FileList(changes, session.Vcs.Repository)];
        if (cannot is not null)
        {
            detail.Add(DialogFooter.Paragraph($"{cannot}."));
        }
        detail.Add(back);
        ChoiceDialog question = new("Uncommitted Changes", $"Your changes to {what} are not committed. Bring them to {to}, stash them, or discard them?",
            [BringChanges, StashChanges, DiscardUncommitted], cannot is null ? BringChanges : StashChanges, offersNotAgain: true, detail,
            cannot is null ? null : new Dictionary<string, string> { [BringChanges] = cannot });
        ChangesQuestion = question;
        try
        {
            string? answer = await question.ShowDialog<string?>(this);
            bool returning = from is not null && back.IsChecked == true;
            if (question.NotAgain && answer is BringChanges or StashChanges)
            {
                ApplySettings(settings with
                {
                    ChangesAtCheckout = answer == BringChanges ? CheckoutChanges.Bring : returning ? CheckoutChanges.StashAndReturn : CheckoutChanges.Stash,
                });
            }
            return (answer, returning);
        }
        finally
        {
            if (ChangesQuestion == question)
            {
                ChangesQuestion = null;
            }
        }
    }

    /// <summary>Discard…: once the person agrees (no agent's input reaching the question), every change rolled back
    /// as Rollback does it, new files to the Trash, then the checkout.</summary>
    private async Task<VcsOutcome?> DiscardThenAsync(string to, IReadOnlyList<FileChange> changes, Func<Task<VcsOutcome>> checkout)
    {
        VcsController vcs = session.Vcs;
        if (vcs.WhyNotRollback(changes) is { } refused)
        {
            status.Text = refused;
            return new VcsOutcome(false, refused);
        }
        string what = changes.Count == 1 ? changes[0].Name : $"these {Numbers.Said(changes.Count)} files";
        if (!await ConfirmAsync("Discard Changes", $"Discard the changes to {what} and check out {to}?{LostWords(changes)}", "Discard",
            ToolWindows.CommitView.FileList(changes, vcs.Repository), Refusals.DiscardIsThePersons))
        {
            return null;
        }
        VcsOutcome rolled = await vcs.RollbackAsync(changes);
        return rolled.Succeeded ? await ReturnedAsync(await checkout()) : rolled;
    }

    /// <summary>What a rollback of <paramref name="changes"/> loses, as its question says it: files added to Git stay on disk,
    /// new files go to the Trash, which Undo brings back, and the rest cannot be undone.</summary>
    private static string LostWords(IReadOnlyList<FileChange> changes)
    {
        string kept = changes.Any(c => c.Kind is ChangeKind.Added) ? " Files added to Git stay on disk, unversioned." : string.Empty;
        bool unversioned = changes.Any(c => c.Kind is ChangeKind.Unversioned);
        bool tracked = changes.Any(c => c.Kind is not (ChangeKind.Unversioned or ChangeKind.Added));
        return kept + (unversioned ? " Unversioned files are deleted to the Trash, and Undo brings them back." : string.Empty)
            + (!tracked ? string.Empty : unversioned ? " The other changes cannot be undone." : " This cannot be undone.");
    }

    /// <summary>A checkout's outcome, after which a stash made as the branch now checked out was left, as the person asked,
    /// is unstashed.</summary>
    private async Task<VcsOutcome> ReturnedAsync(VcsOutcome switched)
    {
        if (!switched.Succeeded || session.Vcs.Branch is not { } now || await session.Vcs.TakeReturningAsync(now) is not { } stash)
        {
            return switched;
        }
        VcsOutcome unstashed = await session.Vcs.UnstashAsync(stash, pop: true);
        if (unstashed.Succeeded)
        {
            status.Text = $"{switched.Message}, and the changes left on it were unstashed";
        }
        return unstashed.Succeeded ? new VcsOutcome(true, status.Text ?? switched.Message) : unstashed;
    }
}
