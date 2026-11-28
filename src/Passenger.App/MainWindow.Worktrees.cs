using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>Git's worktrees, other checkouts of the repository in folders of their own: made in the New Worktree dialog
/// (the Git menu, or New Worktree from… in a branch's or tag's menu in the Git log's branches), opened in a window of their
/// own or this one, each trusted as the repository is, and removed once the person agrees, told what is lost.</summary>
public partial class MainWindow
{
    private WorktreeDialog? worktreeDialog;

    /// <summary>The New Worktree dialog while it is open.</summary>
    public WorktreeDialog? WorktreeWindow => worktreeDialog;

    /// <summary>New Worktree… from a branch or tag (null for HEAD): the dialog over the window, then the worktree it closes
    /// with made, trusted as the repository is and opened in a window of its own where asked.</summary>
    public async Task ShowNewWorktreeAsync(BranchInfo? from)
    {
        VcsController vcs = session.Vcs;
        if (vcs.WhyNotNewWorktree is { } refused)
        {
            Refuse(refused);
            return;
        }
        WorktreeStart start;
        using (Working? reading = BeginWork(AppActions.NewWorktree, "Reading the branches and worktrees…"))
        {
            if (reading is null)
            {
                return;
            }
            (IReadOnlyList<BranchInfo> branches, _) = await vcs.BranchTreeAsync();
            IReadOnlyList<WorktreeInfo> worktrees = await vcs.WorktreesAsync();
            start = new WorktreeStart(from, branches, worktrees.FirstOrDefault(w => w.IsMain)?.Folder ?? vcs.Repository!.Root);
        }
        WorktreeDialog dialog = new(start, vcs.CommitAtAsync);
        worktreeDialog = dialog;
        dialog.Closed += (_, _) => worktreeDialog = worktreeDialog == dialog ? null : worktreeDialog;
        if (await dialog.ShowDialog<WorktreeChoice?>(this) is not { } choice || vcs != session.Vcs || !(await vcs.AddWorktreeAsync(choice.Plan)).Succeeded)
        {
            return;
        }
        string made = PathNames.Canonical(choice.Plan.Folder);
        TrustAsRepository(made);
        if (choice.Open)
        {
            PassengerApp.OpenProject(made, OpenTarget.NewWindow, this);
        }
    }

    /// <summary>A worktree of the branches panel opened, in a window of its own or this one, trusted as the repository is.</summary>
    public void OpenWorktree(WorktreeInfo worktree, bool newWindow)
    {
        string folder = PathNames.Canonical(worktree.Folder);
        TrustAsRepository(folder);
        PassengerApp.OpenProject(folder, newWindow ? OpenTarget.NewWindow : OpenTarget.ThisWindow, this);
    }

    /// <summary>Remove Worktree…: once the person agrees, told what goes to the Trash with its folder (its changes no commit
    /// holds, the files git ignores there), the folder moved there and the worktree forgotten by git.</summary>
    public async Task RemoveWorktreeAsync(WorktreeInfo worktree)
    {
        VcsController vcs = session.Vcs;
        int? changes = await vcs.WorktreeChangesAsync(worktree);
        string kept = worktree.BranchName is { } branch ? $" The branch {branch} and its commits stay." : string.Empty;
        string moved = $"Move the folder of the worktree {worktree.Name}, {PathNames.Shown(worktree.Folder, null)}, to the Trash and have git forget the worktree?";
        string text = worktree.Gone is not null
            ? $"Forget the worktree {worktree.Name}? Its folder is gone already.{kept}"
            : changes is int changed and > 0
                ? $"{moved} {Plural.Of(changed, "file")} changed there that no commit holds {Plural.Agreeing(changed, "goes", "go")} to the Trash with it, to be found there.{kept}"
                : $"{moved} Its changes are all committed; what git ignores there (build output, local settings) goes to the Trash with it.{kept}";
        if (await Confirm("Remove Worktree", text, "Remove"))
        {
            await vcs.RemoveWorktreeAsync(worktree);
        }
    }

    /// <summary>Why a worktree cannot be removed while a window of this Passenger shows it; null where none does.</summary>
    private static string? WhyNotRemoveWorktree(WorktreeInfo worktree) =>
        PassengerApp.Windows.Any(w => w.Folder is { } shown && PathNames.Same(shown, PathNames.Canonical(worktree.Folder)))
            ? $"A window shows {worktree.Name}: close it there first"
            : null;

    /// <summary>A worktree of a trusted repository trusted as it is, where trusting it trusts it alone (beside the
    /// repository, in no other): its files are the repository's own, checked out elsewhere.</summary>
    private void TrustAsRepository(string worktree)
    {
        if (session.Vcs.Repository is { } repository && WorkspaceTrust.Trusts(repository.Root) && !WorkspaceTrust.Trusts(worktree) && WorkspaceTrust.TrustsAlone(worktree))
        {
            TrustFolder(worktree);
        }
    }
}
