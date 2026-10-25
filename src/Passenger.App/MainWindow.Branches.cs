using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>The Git log's branches panel's actions that ask the person something or open something: New Branch from and
/// Rename (a name), Delete (asked first for a branch not merged or a remote's), Compare with Current (a tab of the Git
/// window) and Show Diff with Working Tree (a window of the files that differ); and the favourites, kept with the folder.</summary>
public partial class MainWindow
{
    /// <summary>The Git log's branches panel.</summary>
    public BranchesPanel GitBranches => GitLog.Branches!;

    /// <summary>The folder's favourite branches, kept for the branches panel until the Git window is built.</summary>
    private GitBranchesState branchFavorites = new();

    /// <summary>The branches panel's requests and favourites, as the Git window is built.</summary>
    private void FollowBranches(BranchesPanel panel)
    {
        panel.Favorites = branchFavorites;
        panel.NewBranchRequested += (_, branch) => ShowNewBranchFrom(branch);
        panel.RenameRequested += (_, branch) => ShowRenameBranch(branch);
        panel.DeleteRequested += (_, branch) => IdeErrors.Observe(DeleteBranchAsync(branch));
        panel.CompareRequested += (_, branch) => CompareWithCurrent(branch);
        panel.WorkingTreeDiffRequested += (_, branch) => IdeErrors.Observe(ShowWorkingTreeDiffAsync(branch));
        panel.FavoritesChanged += (_, favorites) => session.State?.GitBranches.Set(favorites);
    }

    /// <summary>Rider's Git.Rename.Local.Branch shares Rename's keys (Ctrl+R, R in its Visual Studio keymap): while the
    /// branches panel has them, they rename the local branch chosen there rather than a symbol.</summary>
    private bool BranchesTake(string action)
    {
        if (action != AppActions.Rename || gitView?.Log.Branches is not { IsKeyboardFocusWithin: true } panel || panel.Selected is not { Branch: { Kind: RefKind.Local } branch })
        {
            return false;
        }
        ShowRenameBranch(branch);
        return true;
    }

    /// <summary>New Branch from: its name asked for (a remote branch's own name to begin with), then made there and
    /// checked out.</summary>
    public NamePopup ShowNewBranchFrom(BranchInfo from)
    {
        string initial = from.Kind switch
        {
            RefKind.Local => from.Name,
            RefKind.Remote => from.Name[(from.Name.IndexOf('/', StringComparison.Ordinal) + 1)..],
            _ => string.Empty,
        };
        NamePopup popup = new($"Create Branch from {from.Name}", initial, async name => (await session.Vcs.NewBranchFromAsync(name.Trim(), from)).Problem);
        popups.Show(popup, _ => { });
        return popup;
    }

    /// <summary>Rename of a local branch; a log filtered to it follows it to its new name.</summary>
    public NamePopup ShowRenameBranch(BranchInfo branch)
    {
        NamePopup popup = new($"Rename Branch {branch.Name}", branch.Name, async name =>
        {
            string renamed = name.Trim();
            bool followed = GitLog.BranchFilter == branch.Ref.FullName;
            VcsOutcome outcome = await session.Vcs.RenameBranchAsync(branch, renamed);
            if (outcome.Succeeded && followed)
            {
                GitLog.FilterBranch(branch with { Ref = branch.Ref with { FullName = $"refs/heads/{renamed}", Name = renamed } });
            }
            return outcome.Problem;
        });
        popups.Show(popup, _ => { });
        return popup;
    }

    /// <summary>Delete: a local branch at once when its commits are all in HEAD's history, else once the person agrees
    /// to lose them; a remote's branch, on the remote, once they agree; a tag at once.</summary>
    public async Task<VcsOutcome?> DeleteBranchAsync(BranchInfo branch)
    {
        bool force = false;
        if (branch.Kind == RefKind.Local && !await session.Vcs.IsMergedAsync(branch))
        {
            if (!await Confirm("Branch Was Not Fully Merged",
                $"The branch {branch.Name} was not fully merged to {session.Vcs.Branch ?? "HEAD"}. Delete it and its unmerged commits?", "Delete"))
            {
                return null;
            }
            force = true;
        }
        else if (branch.Kind == RefKind.Remote && !await Confirm("Delete Remote Branch", $"Delete remote branch {branch.Name}?", "Delete"))
        {
            return null;
        }
        return await session.Vcs.DeleteRefAsync(branch, force, GitBranches.Remotes);
    }

    /// <summary>Compare with Current: a tab of the Git window with the commits the branch has that the current one
    /// lacks above, and those it lacks below.</summary>
    public GitLogView CompareWithCurrent(BranchInfo branch)
    {
        string current = session.Vcs.Branch ?? "HEAD";
        string currentRef = session.Vcs.Branch is { } name ? $"refs/heads/{name}" : "HEAD";
        ShowToolWindow(ToolWindowIds.Git);
        return GitWindow.ShowCompare(new LogRange(currentRef, current, branch.Ref.FullName, branch.Name), new LogRange(branch.Ref.FullName, branch.Name, currentRef, current));
    }

    /// <summary>Show Diff with Working Tree: the files that differ between a branch and the working tree, each opening
    /// as the branch has it beside the file.</summary>
    public async Task<ChangesWindow?> ShowWorkingTreeDiffAsync(BranchInfo branch)
    {
        if (session.Vcs.Repository is not { } repository)
        {
            status.Text = Refusals.NoRepository;
            return null;
        }
        IReadOnlyList<FileChange> files = await session.Vcs.ChangesSinceAsync(branch);
        ChangesWindow window = new(branch.Name, files, repository);
        window.DiffRequested += (_, change) => IdeErrors.Observe(CompareWithAsync(change.Path, branch.Ref.FullName, branch.Name, change.From ?? change.Path));
        ShowOwn(window);
        return window;
    }
}
