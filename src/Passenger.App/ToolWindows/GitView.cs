using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Passenger.App.Vcs;
using Passenger.Vcs;

namespace Passenger.App.ToolWindows;

/// <summary>The Git tool window: the Log, and a tab beside it for each history Show History opens (a file's, a
/// folder's, some lines') and each comparison Compare with Current opens, its tabs in the window's header. Show History
/// (or Compare) again for the same thing goes back to its tab; a tab's × closes it.</summary>
public sealed class GitView : DockPanel
{
    private VcsController vcs;
    private readonly List<(HistoryScope? Scope, string Title, GitLogView View, Control Body)> histories = [];
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl body = new();

    public GitView(VcsController git)
    {
        vcs = git;
        Log = Take(new GitLogView(vcs));
        Shown = Log;
        Children.Add(body);
        Show(Log);
    }

    /// <summary>The Log of every branch, the first tab.</summary>
    public GitLogView Log { get; }

    /// <summary>The tab shown: the Log or a history.</summary>
    public GitLogView Shown { get; private set; }

    /// <summary>The tabs, for the tool window's header.</summary>
    public Control Tabs => tabs;

    /// <summary>The tabs' titles, the Log's first.</summary>
    public IReadOnlyList<string> Titles => ["Log", .. histories.Select(h => h.Title)];

    /// <summary>The history open for this, if any.</summary>
    public GitLogView? HistoryOf(HistoryScope scope) => histories.FirstOrDefault(h => h.Scope is not null && h.Scope == scope).View;

    /// <summary>A file of a commit whose change to show, from any tab.</summary>
    public event EventHandler<(CommitInfo Commit, FileChange File)>? DiffRequested;

    /// <summary>A file as a commit of its history had it, to compare with the file now (its path now first).</summary>
    public event EventHandler<(string Local, CommitInfo Commit, FileChange File)>? CompareRequested;

    public event EventHandler<CommitInfo>? NewBranchRequested;

    public event EventHandler<(CommitInfo Commit, ResetMode Mode)>? ResetRequested;

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
        if (histories.FirstOrDefault(h => h.Scope is null && h.Title == title).View is { } open)
        {
            Show(open);
            return open;
        }
        GitLogView top = Take(new GitLogView(vcs, range: missing));
        GitLogView bottom = Take(new GitLogView(vcs, range: extra));
        Grid body = new() { RowDefinitions = new RowDefinitions("Auto,*,1,Auto,*") };
        HairlineDivider between = new(GridResizeDirection.Rows) { [Grid.RowProperty] = 2 };
        body.Children.Add(Explanation(missing, 0));
        Grid.SetRow(top, 1);
        body.Children.Add(top);
        body.Children.Add(between);
        body.Children.Add(Explanation(extra, 3));
        Grid.SetRow(bottom, 4);
        body.Children.Add(bottom);
        histories.Add((null, title, top, body));
        Show(top);
        return top;

        static Control Explanation(LogRange range, int row)
        {
            TextBlock text = new() { Text = $"Commits that exist in {range.To} but don't exist in {range.From}", Margin = new Thickness(10, 6), FontSize = 12 };
            text.Classes.Add("muted");
            Grid.SetRow(text, row);
            return text;
        }
    }

    /// <summary>Shows another folder's working tree from now on, its session's in place of the last's: the histories and
    /// comparisons opened for the last one close, and the Log reads the new one's.</summary>
    public void Bind(VcsController next)
    {
        foreach (GitLogView open in histories.Select(h => h.View).ToList())
        {
            Close(open);
        }
        vcs = next;
        Log.Bind(next);
        Show(Log);
    }

    /// <summary>Closes a history's tab, as its × does; the tab before it is shown when it was.</summary>
    public void Close(GitLogView view)
    {
        int at = histories.FindIndex(h => h.View == view);
        if (at < 0)
        {
            return;
        }
        Control closing = histories[at].Body;
        histories.RemoveAt(at);
        view.Close();
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
        view.CompareRequested += (_, chosen) => CompareRequested?.Invoke(this, (view.Scope!.Path, chosen.Commit, chosen.File));
        view.NewBranchRequested += (_, commit) => NewBranchRequested?.Invoke(this, commit);
        view.ResetRequested += (_, reset) => ResetRequested?.Invoke(this, reset);
        return view;
    }

    private void Show(GitLogView view)
    {
        Shown = view;
        // One tab in the tree at a time: one out of it reads its history again only when it is next shown.
        body.Content = view == Log ? view : histories.First(h => h.View == view).Body;
        DrawTabs();
    }

    private void DrawTabs()
    {
        tabs.Children.Clear();
        tabs.Children.Add(Tab("Log", null, Log));
        foreach ((HistoryScope? scope, string title, GitLogView view, Control _) in histories)
        {
            tabs.Children.Add(Tab(title, scope is null ? null : vcs.Repository?.Relative(scope.Path), view));
        }
    }

    private ToggleButton Tab(string title, string? tip, GitLogView view)
    {
        ToggleButton tab = ToolTabs.Tab(title, view == Shown, view == Log ? null : () => Close(view));
        if (tip is not null)
        {
            ToolTip.SetTip(tab, tip);
        }
        tab.Click += (_, _) => Show(view);
        return tab;
    }
}
