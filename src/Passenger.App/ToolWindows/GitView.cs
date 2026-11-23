using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Passenger.App.Vcs;
using Passenger.Vcs;

namespace Passenger.App.ToolWindows;

/// <summary>The Git tool window: the Log, and a tab beside it for each history Show History opens (a file's, a
/// folder's, some lines'), each comparison Compare with Current opens and each branch's files Show Diff with Working Tree
/// lists, its tabs in the window's header. Asked again for the same thing it goes back to its tab; a tab's × closes it.</summary>
public sealed class GitView : DockPanel
{
    private VcsController vcs;

    /// <summary>The tabs after the Log: each one's view (a <see cref="GitLogView"/>, or a <see cref="WorkingTreeDiffView"/>)
    /// and what it shows, the view with what is laid out around it.</summary>
    private readonly List<(HistoryScope? Scope, string Title, Control View, Control Body)> histories = [];
    private readonly ToolTabs.Strip<Control> tabs = new();
    private readonly ContentControl body = new();

    public GitView(VcsController git)
    {
        vcs = git;
        Log = Take(new GitLogView(vcs));
        Shown = Log;
        Children.Add(body);
        tabs.Chosen += (_, view) => Show(view);
        Show(Log);
    }

    /// <summary>The Log of every branch, the first tab.</summary>
    public GitLogView Log { get; }

    /// <summary>The tab shown: the Log, a history, a comparison's upper list or a branch's files beside the working tree.</summary>
    public Control Shown { get; private set; }

    /// <summary>The tabs, for the tool window's header.</summary>
    public Control Tabs => tabs;

    /// <summary>The tabs' titles, the Log's first.</summary>
    public IReadOnlyList<string> Titles => ["Log", .. histories.Select(h => h.Title)];

    /// <summary>The history open for this, if any.</summary>
    public GitLogView? HistoryOf(HistoryScope scope) => histories.FirstOrDefault(h => h.Scope is not null && h.Scope == scope).View as GitLogView;

    /// <summary>A file of a commit whose change to show, from any tab.</summary>
    public event EventHandler<(CommitInfo Commit, FileChange File)>? DiffRequested;

    /// <summary>A file as a commit of its history had it, to compare with the file now (its path now first).</summary>
    public event EventHandler<(string Local, CommitInfo Commit, FileChange File)>? CompareRequested;

    /// <summary>A branch's file to show beside the file as it is now, from its Show Diff with Working Tree tab.</summary>
    public event EventHandler<(BranchInfo Branch, FileChange File)>? WorkingTreeDiffRequested;

    /// <summary>A commit's file to open as it is now, from any tab.</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>One of the window's actions asked for by a tab's button.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>Reads the shown tab again: its log (a comparison's two), or its files.</summary>
    public Task RefreshAsync() => Task.WhenAll(Avalonia.LogicalTree.LogicalExtensions.GetSelfAndLogicalDescendants((Control)body.Content!)
        .Select(tab => tab switch { GitLogView log => log.LoadAsync(), WorkingTreeDiffView files => files.LoadAsync(), _ => Task.CompletedTask }));

    public event EventHandler<CommitInfo>? NewBranchRequested;

    public event EventHandler<(CommitInfo Commit, ResetMode Mode)>? ResetRequested;

    public event EventHandler<CommitInfo>? InteractiveRebaseRequested;

    /// <summary>Show History's tab for a file, a folder or lines: the one open for it, else a new one after the others.</summary>
    public GitLogView ShowHistory(HistoryScope scope, string title)
    {
        GitLogView view = HistoryOf(scope) ?? Add(scope, title);
        Show(view);
        return view;
    }

    public void ShowLog() => Show(Log);

    /// <summary>Compare with Current: a tab of the commits each of two branches has that the other lacks, one
    /// above the other, the one open for the same two if any. <see cref="Shown"/> is then its upper list.</summary>
    public GitLogView ShowCompare(LogRange missing, LogRange extra)
    {
        string title = $"Compare {missing.From} and {missing.To}";
        if (histories.FirstOrDefault(h => h.Scope is null && h.Title == title).View is GitLogView open)
        {
            Show(open);
            return open;
        }
        GitLogView top = Take(new GitLogView(vcs, range: missing));
        GitLogView bottom = Take(new GitLogView(vcs, range: extra));
        Grid body = new() { RowDefinitions = new RowDefinitions("Auto,*,1,Auto,*") };
        HairlineDivider between = new(GridResizeDirection.Rows) { [Grid.RowProperty] = 2 };
        body.Children.Add(Explanation($"Commits that exist in {missing.To} but don't exist in {missing.From}", 0));
        Grid.SetRow(top, 1);
        body.Children.Add(top);
        body.Children.Add(between);
        body.Children.Add(Explanation($"Commits that exist in {extra.To} but don't exist in {extra.From}", 3));
        Grid.SetRow(bottom, 4);
        body.Children.Add(bottom);
        histories.Add((null, title, top, body));
        Show(top);
        return top;
    }

    /// <summary>Show Diff with Working Tree: a tab of the files that differ between a branch and the working tree, the one
    /// open for the branch if any, read again.</summary>
    public WorkingTreeDiffView ShowWorkingTreeDiff(BranchInfo branch)
    {
        string title = $"Compare {branch.Name} and Working Tree";
        if (histories.FirstOrDefault(h => h.Scope is null && h.Title == title).View is WorkingTreeDiffView open)
        {
            Show(open);
            IdeErrors.Observe(open.LoadAsync());
            return open;
        }
        WorkingTreeDiffView files = new(vcs, branch);
        files.DiffRequested += (_, change) => WorkingTreeDiffRequested?.Invoke(this, (branch, change));
        files.OpenRequested += (_, path) => OpenRequested?.Invoke(this, path);
        Grid body = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(Explanation($"Files that differ between {branch.Name} and the working tree", 0));
        Grid.SetRow(files, 1);
        body.Children.Add(files);
        histories.Add((null, title, files, body));
        Show(files);
        return files;
    }

    /// <summary>What a tab's list holds, muted above it.</summary>
    private static TextBlock Explanation(string text, int row)
    {
        TextBlock said = new() { Classes = { StyleClasses.Caption }, Text = text, Margin = new Thickness(ListRow.TextStart, Gaps.Small) };
        said.Classes.Add(StyleClasses.Muted);
        Grid.SetRow(said, row);
        return said;
    }

    /// <summary>Shows another folder's working tree from now on, its session's in place of the last's: the histories and
    /// comparisons opened for the last one close, and the Log reads the new one's.</summary>
    public void Bind(VcsController next)
    {
        foreach (Control open in histories.Select(h => h.View).ToList())
        {
            Close(open);
        }
        vcs = next;
        Log.Bind(next);
        Show(Log);
    }

    /// <summary>Closes a tab after the Log, as its × does; the tab before it is shown when it was.</summary>
    public void Close(Control view)
    {
        int at = histories.FindIndex(h => h.View == view);
        if (at < 0)
        {
            return;
        }
        Control closing = histories[at].Body;
        histories.RemoveAt(at);
        switch (view)
        {
            case GitLogView log:
                log.Close();
                break;
            case WorkingTreeDiffView files:
                files.Close();
                break;
        }
        // A comparison's lower list goes with its tab.
        foreach (GitLogView inner in (closing as Panel)?.Children.OfType<GitLogView>() ?? [])
        {
            inner.Close();
        }
        if (Shown == view)
        {
            Show(at > 0 ? histories[at - 1].View : Log);
        }
        else
        {
            DrawTabs();
        }
    }

    private GitLogView Add(HistoryScope scope, string title)
    {
        GitLogView view = Take(new GitLogView(vcs, scope));
        histories.Add((scope, title, view, view));
        return view;
    }

    /// <summary>A tab's view, whose requests are the window's.</summary>
    private GitLogView Take(GitLogView view)
    {
        view.DiffRequested += (_, chosen) => DiffRequested?.Invoke(this, chosen);
        view.CompareRequested += (_, chosen) => CompareRequested?.Invoke(this, chosen);
        view.OpenRequested += (_, path) => OpenRequested?.Invoke(this, path);
        view.ActionRequested += (_, action) => ActionRequested?.Invoke(this, action);
        view.NewBranchRequested += (_, commit) => NewBranchRequested?.Invoke(this, commit);
        view.ResetRequested += (_, reset) => ResetRequested?.Invoke(this, reset);
        view.InteractiveRebaseRequested += (_, commit) => InteractiveRebaseRequested?.Invoke(this, commit);
        return view;
    }

    private void Show(Control view)
    {
        Shown = view;
        // One tab in the tree at a time: one out of it reads its history again only when it is next shown.
        body.Content = view == Log ? view : histories.First(h => h.View == view).Body;
        DrawTabs();
    }

    private void DrawTabs()
    {
        tabs.Clear();
        tabs.Add(Log, "Log");
        foreach ((HistoryScope? scope, string title, Control view, Control _) in histories)
        {
            ToggleButton tab = tabs.Add(view, title, () => Close(view));
            if (scope is not null)
            {
                ToolTip.SetTip(tab, vcs.Repository?.Relative(scope.Path));
            }
        }
        tabs.Choice = Shown;
    }
}
