using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>The status bar's Git, in a working tree: the commits the branch's upstream has that it lacks and those it has
/// that the upstream lacks, as last fetched, while it tracks one; and the files changed since the last commit. Each is a
/// click to the window that lists them: the Git window's comparison of the branch with its upstream, and the Commit window.
/// Worded from what the window's Git read last (<see cref="VcsController.Divergence"/>, <see cref="VcsController.Changes"/>),
/// never by a git of its own.</summary>
public partial class MainWindow
{
    /// <summary>The status bar's Git as the window's Git last read it: nothing outside a working tree, and no commits for a
    /// detached head or a branch that tracks none.</summary>
    private void ShowGitStatus()
    {
        VcsController vcs = session.Vcs;
        bool tree = vcs.Repository is not null;
        GitChangesButton.IsVisible = tree;
        int changed = vcs.Changes.Count;
        int unversioned = vcs.Changes.Count(c => c.Kind is ChangeKind.Unversioned);
        GitChangesText.Text = changed.ToString(CultureInfo.CurrentCulture);
        string files = changed == 0 ? "No changes to commit"
            : $"{Plural.Of(changed, "changed file")} to commit{(unversioned > 0 ? $", {unversioned} of them unversioned" : string.Empty)}";
        ToolTip.SetTip(GitChangesButton, $"{files}\n\nClick to open the Commit window");
        if (!tree || vcs.Branch is not { } branch || vcs.Upstream is not { } upstream || vcs.Divergence is not { } apart)
        {
            GitSyncButton.IsVisible = false;
            return;
        }
        GitSyncButton.IsVisible = true;
        GitIncomingText.Text = apart.Behind.ToString(CultureInfo.CurrentCulture);
        GitOutgoingText.Text = apart.Ahead.ToString(CultureInfo.CurrentCulture);
        ToolTip.SetTip(GitSyncButton, $"{Plural.Of(apart.Behind, "incoming commit")} from {upstream}, as last fetched\n"
            + $"{Plural.Of(apart.Ahead, "outgoing commit")} of {branch} to push\n\nClick to compare {branch} with {upstream} in the Git window");
    }

    private void OnGitSyncClicked(object? sender, RoutedEventArgs e) => CompareWithUpstream();

    private void OnGitChangesClicked(object? sender, RoutedEventArgs e) => ShowToolWindow(ToolWindowIds.Commit);

    /// <summary>The branch beside its upstream in the Git window, as Compare with Current shows a branch: the commits the
    /// upstream has that the branch lacks (incoming) above, and those the branch has that the upstream lacks (outgoing)
    /// below. Null, said, where the branch tracks none.</summary>
    public GitLogView? CompareWithUpstream()
    {
        if (session.Vcs.Branch is not { } branch || session.Vcs.Upstream is not { } upstream)
        {
            status.Text = session.Vcs.Branch is { } untracked ? Refusals.Untracked(untracked) : "A detached head tracks no remote's branch";
            return null;
        }
        string current = $"refs/heads/{branch}";
        ShowToolWindow(ToolWindowIds.Git);
        return GitWindow.ShowCompare(new LogRange(current, branch, upstream, upstream), new LogRange(upstream, upstream, current, branch));
    }
}
