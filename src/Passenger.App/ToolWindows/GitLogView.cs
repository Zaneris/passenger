using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Vcs;
using Passenger.Vcs;

namespace Passenger.App.ToolWindows;

/// <summary>The Git window's Log: every branch's commits newest first beside the graph that joins them, each with its
/// branches and tags, author and date, narrowed by text or hash; the chosen commit's files and its message beside the
/// list. A double-click on a file shows what the commit did to it. Given a <see cref="HistoryScope"/> it is a Show
/// History tab: the commits from HEAD that changed a file (under each name it had), a folder or some lines, each listing
/// what it did there; for a file or lines, a double-click or Enter on a commit shows what it did to the file. The Log has
/// the branches panel at its left, a branch chosen there filtering it to that branch's commits (its Branch filter,
/// cleared by its x); given a <see cref="LogRange"/> it lists the commits of one branch another lacks (Compare with
/// Current).</summary>
public sealed class GitLogView : DockPanel, IOwnKeys
{
    private const double LaneWidth = 12;
    private const double RowHeight = ListRow.Height;

    private VcsController vcs;
    private IReadOnlyList<GraphRow> rows = [];
    private readonly ObservableCollection<FileChange> files = [];
    private readonly ListBox list;
    private readonly ListBox fileList;
    private readonly TextBox details;
    private readonly TextBox filter;
    private readonly TextBlock placeholder;
    private readonly Border branchFilter;
    private readonly TextBlock branchFilterText;
    private readonly EventHandler onHistoryChanged;
    private IReadOnlyList<GraphRow> all = [];

    /// <summary>A history's commits' changes there, by hash.</summary>
    private Dictionary<string, IReadOnlyList<FileChange>> changes = [];
    private bool stale = true;
    private int loads;

    /// <summary>How many times the log has been read, which a save never adds to.</summary>
    public int Loads => loads;
    private int showing;

    /// <param name="scope">What Show History lists the commits of, or null for the Log of every branch.</param>
    /// <param name="range">The commits of one branch another lacks, for Compare with Current.</param>
    public GitLogView(VcsController git, HistoryScope? scope = null, LogRange? range = null)
    {
        vcs = git;
        Scope = scope;
        Range = range;
        list = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<GraphRow>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
        list.SelectionChanged += (_, _) => IdeErrors.Observe(ShowCommitAsync());
        list.DoubleTapped += (_, _) => RequestFileDiff();
        // Ahead of the list's own keys, which take Enter for its selection.
        list.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && RequestFileDiff())
            {
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ListMenus.Attach(list, () => Selected is not { } commit ? [] :
        [
            .. FileChangeOf(commit) is { } change ? new Control[]
            {
                ListMenus.Item("Show Diff", null, () => DiffRequested?.Invoke(this, (commit, change))),
                ListMenus.Item("Compare with Local", null, () => CompareRequested?.Invoke(this, (commit, change))),
                new Separator(),
            } : [],
            ListMenus.Item("Copy Revision Number", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, commit.Hash))),
            new Separator(),
            ListMenus.Item("Checkout Revision", null, () => IdeErrors.Observe(vcs.CheckoutRevisionAsync(commit.Hash))),
            ListMenus.Item("New Branch…", null, () => NewBranchRequested?.Invoke(this, commit)),
            new Separator(),
            ListMenus.Item("Cherry-Pick", null, () => IdeErrors.Observe(vcs.CherryPickAsync(commit.Hash))),
            ListMenus.Item("Revert Commit", null, () => IdeErrors.Observe(vcs.RevertAsync(commit.Hash))),
            ListMenus.Sub("Reset Current Branch to Here",
                ListMenus.Item("Soft: the changes since kept staged", null, () => ResetRequested?.Invoke(this, (commit, ResetMode.Soft))),
                ListMenus.Item("Mixed: the changes since kept in the files", null, () => ResetRequested?.Invoke(this, (commit, ResetMode.Mixed))),
                ListMenus.Item("Hard: the changes since dropped…", null, () => ResetRequested?.Invoke(this, (commit, ResetMode.Hard)))),
        ]);
        fileList = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = files,
            ItemTemplate = new FuncDataTemplate<FileChange>((change, _) => change is null ? new TextBlock() : FileRow(change), supportsRecycling: false),
        };
        fileList.DoubleTapped += (_, _) => RequestDiff();
        fileList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter || AppActions.Presses(AppActions.ShowDiff, e))
            {
                RequestDiff();
                e.Handled = true;
            }
        };
        details = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 8),
            FontSize = 12,
        };
        placeholder = new TextBlock { Classes = { "emptyText" } };

        filter = new TextBox { Classes = { "search" }, PlaceholderText = "Text or hash", Width = 240, Margin = new Thickness(8, 0) };
        filter.TextChanged += (_, _) => Filter();
        Button refresh = ToolButtons.Of(IconKeys.Rerun, "Refresh", () => IdeErrors.Observe(LoadAsync()));
        // The Branch filter, shown while the log is filtered to a branch, its x showing every branch again.
        branchFilterText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        PathIcon clearGlyph = new() { Classes = { "small" } };
        clearGlyph.Bind(PathIcon.DataProperty, clearGlyph.GetResourceObservable(IconKeys.Close));
        Button clearBranch = new() { Classes = { "tabPin" }, Content = clearGlyph, Margin = new Thickness(6, 0, 0, 0) };
        ToolTip.SetTip(clearBranch, "Clear Branch Filter");
        clearBranch.Click += (_, _) => FilterBranch(null, all: true);
        branchFilter = new Border
        {
            IsVisible = false,
            Classes = { "tag" },
            Padding = new Thickness(8, 2, 4, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { branchFilterText, clearBranch } },
        };
        branchFilter.Bind(Border.BackgroundProperty, branchFilter.GetResourceObservable(TokenKeys.Selected));
        Border top = ToolBars.Top(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { filter, branchFilter, refresh } });

        Grid right = new() { RowDefinitions = new RowDefinitions("*,1,*") };
        HairlineDivider between = new(GridResizeDirection.Rows) { [Grid.RowProperty] = 1 };
        Grid.SetRow(details, 2);
        right.Children.Add(fileList);
        right.Children.Add(between);
        right.Children.Add(details);
        Grid split = new() { ColumnDefinitions = new ColumnDefinitions("3*,1,2*") };
        HairlineDivider divider = new(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
        Grid.SetColumn(right, 2);
        split.Children.Add(new Panel { Children = { list, placeholder } });
        split.Children.Add(divider);
        split.Children.Add(right);
        if (scope is null && range is null)
        {
            Branches = new BranchesPanel(vcs);
            Branches.FilterRequested += (_, branch) => FilterBranch(branch);
            Branches.NavigateRequested += (_, branch) => IdeErrors.Observe(SelectHashAsync(branch.Hash));
            Grid withBranches = new() { ColumnDefinitions = new ColumnDefinitions("240,1,*") };
            HairlineDivider beside = new(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
            Grid.SetColumn(split, 2);
            withBranches.Children.Add(Branches);
            withBranches.Children.Add(beside);
            withBranches.Children.Add(split);
            Children.Add(top);
            Children.Add(withBranches);
        }
        else
        {
            Children.Add(top);
            Children.Add(split);
        }

        onHistoryChanged = (_, _) =>
        {
            // Read again now when shown, else when it next is: a hidden window (or tab) is out of the visual tree.
            stale = true;
            if (VisualRoot is not null)
            {
                IdeErrors.Observe(LoadAsync());
            }
        };
        vcs.HistoryChanged += onHistoryChanged;
        AttachedToVisualTree += (_, _) =>
        {
            if (stale)
            {
                IdeErrors.Observe(LoadAsync());
            }
        };
    }

    /// <summary>What the commits are of, or null for the Log of every branch.</summary>
    public HistoryScope? Scope { get; }

    /// <summary>The commits of one branch another lacks, for Compare with Current; null elsewhere.</summary>
    public LogRange? Range { get; }

    /// <summary>The Log's branches panel; null in a history or a comparison.</summary>
    public BranchesPanel? Branches { get; }

    /// <summary>The revision the Log is filtered to (refs/heads/feature, or HEAD), or null for every branch.</summary>
    public string? BranchFilter { get; private set; }

    /// <summary>The branch filter as the Log's toolbar shows it.</summary>
    public string? BranchFilterName { get; private set; }

    /// <summary>Update Branch Filter in Log: the Log lists a branch's commits alone (HEAD's for null), or every
    /// branch's again with <paramref name="all"/>.</summary>
    public void FilterBranch(BranchInfo? branch, bool all = false)
    {
        (BranchFilter, BranchFilterName) = all ? (null, null) : branch is null ? ("HEAD", "HEAD") : (branch.Ref.FullName, branch.Name);
        branchFilter.IsVisible = BranchFilter is not null;
        branchFilterText.Text = $"Branch: {BranchFilterName}";
        IdeErrors.Observe(LoadAsync());
    }

    /// <summary>A history's tab closed: it stops following the history.</summary>
    public void Close() => vcs.HistoryChanged -= onHistoryChanged;

    /// <summary>The Log follows another folder's working tree from now on, its session's in place of the last's: every
    /// branch again, read again when shown.</summary>
    public void Bind(VcsController next)
    {
        Close();
        vcs = next;
        vcs.HistoryChanged += onHistoryChanged;
        Branches?.Bind(next);
        (BranchFilter, BranchFilterName) = (null, null);
        branchFilter.IsVisible = false;
        onHistoryChanged(this, EventArgs.Empty);
    }

    /// <summary>A file of a commit whose change to show: the commit and the file.</summary>
    public event EventHandler<(CommitInfo Commit, FileChange File)>? DiffRequested;

    /// <summary>A file as a commit of its history had it, to compare with the file as it is now.</summary>
    public event EventHandler<(CommitInfo Commit, FileChange File)>? CompareRequested;

    /// <summary>A branch to make at a commit, its name to be asked for.</summary>
    public event EventHandler<CommitInfo>? NewBranchRequested;

    /// <summary>The current branch to move to a commit, in a mode (a hard reset to be agreed to first).</summary>
    public event EventHandler<(CommitInfo Commit, ResetMode Mode)>? ResetRequested;

    /// <summary>The rows listed, after the filter.</summary>
    public IReadOnlyList<GraphRow> Rows => rows;

    /// <summary>The chosen commit's files.</summary>
    public IReadOnlyList<FileChange> Files => files;

    public CommitInfo? Selected => (list.SelectedItem as GraphRow)?.Commit;

    public string Details => details.Text ?? string.Empty;

    public string FilterText
    {
        get => filter.Text ?? string.Empty;
        set => filter.Text = value;
    }

    /// <summary>Chooses the commit with this hash, reading the log first if it has not been.</summary>
    public async Task SelectHashAsync(string hash)
    {
        if (!all.Any(r => r.Commit.Hash == hash))
        {
            await LoadAsync();
        }
        FilterText = string.Empty;
        Filter();
        if (rows.FirstOrDefault(r => r.Commit.Hash == hash) is { } row)
        {
            list.SelectedIndex = IndexOf(rows, row);
            list.ScrollIntoView(list.SelectedIndex);
        }
    }

    /// <summary>Chooses a commit, as a click on its row does.</summary>
    public void Select(CommitInfo commit) => list.SelectedItem = rows.FirstOrDefault(r => r.Commit.Hash == commit.Hash);

    /// <summary>Reads the history again.</summary>
    public async Task LoadAsync()
    {
        int load = ++loads;
        stale = false;
        Git? repository = vcs.Repository;
        if (Branches is not null)
        {
            await Branches.LoadAsync();
            if (BranchFilter is { } filtered && filtered != "HEAD" && !Branches.Branches.Any(b => b.Ref.FullName == filtered))
            {
                // The branch filtered to is gone (deleted, renamed): every branch again.
                (BranchFilter, BranchFilterName) = (null, null);
                branchFilter.IsVisible = false;
            }
        }
        IReadOnlyList<HistoryCommit> history = repository is null || Scope is null ? [] : await repository.HistoryAsync(Scope);
        IReadOnlyList<CommitInfo> commits = repository is null ? []
            : Scope is not null ? [.. history.Select(h => h.Commit)]
            : Range is { } range ? await repository.LogAsync(revisions: [$"{range.FromRef}..{range.ToRef}"])
            : BranchFilter is { } branch ? await repository.LogAsync(revisions: [branch])
            : await repository.LogAsync();
        if (load != loads)
        {
            return;
        }
        changes = history.ToDictionary(h => h.Commit.Hash, h => h.Changes);
        all = Scope is null ? CommitGraph.Layout(commits) : CommitGraph.Line(commits);
        Filter();
        string name = Scope is null ? string.Empty : Path.GetFileName(Path.TrimEndingDirectorySeparator(Scope.Path));
        placeholder.Text = repository is null ? Refusals.NoRepository
            : Range is { } compared ? $"{compared.From} contains all commits from {compared.To}"
            : Scope is null ? "No commits yet"
            : Scope.IsLines ? $"No commit changed these lines of {name}"
            : $"Git has no history of {name}";
        placeholder.IsVisible = rows.Count == 0;
        if (Scope is not null && list.SelectedIndex < 0 && rows.Count > 0)
        {
            list.SelectedIndex = 0;
        }
    }

    /// <summary>Gives the commits the keys on the chosen one, as a click on it does.</summary>
    public void FocusRows()
    {
        int index = Math.Max(0, list.SelectedIndex);
        if (rows.Count == 0)
        {
            return;
        }
        list.ScrollIntoView(index);
        list.UpdateLayout();
        list.ContainerFromIndex(index)?.Focus();
    }

    /// <summary>The file a file's or some lines' history follows, as a commit had it; null in the Log or a folder's
    /// history, whose commits change many.</summary>
    private FileChange? FileChangeOf(CommitInfo commit) =>
        Scope is { IsFolder: false } && changes.TryGetValue(commit.Hash, out IReadOnlyList<FileChange>? made) && made.Count == 1 ? made[0] : null;

    /// <summary>A double-click or Enter on a commit of a file's history: what it did to the file. False elsewhere.</summary>
    private bool RequestFileDiff()
    {
        if (Selected is not { } commit || FileChangeOf(commit) is not { } change)
        {
            return false;
        }
        DiffRequested?.Invoke(this, (commit, change));
        return true;
    }

    /// <summary>Lists the rows the filter keeps, the chosen commit kept chosen while it is still listed.</summary>
    private void Filter()
    {
        string text = FilterText.Trim();
        string? chosen = Selected?.Hash;
        rows = [.. all.Where(row => text.Length == 0
            || row.Commit.Hash.StartsWith(text, StringComparison.OrdinalIgnoreCase)
            || row.Commit.Subject.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.Commit.Author.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.Commit.Refs.Any(r => r.Contains(text, StringComparison.OrdinalIgnoreCase)))];
        list.ItemsSource = rows;
        if (chosen is not null && rows.FirstOrDefault(r => r.Commit.Hash == chosen) is { } again)
        {
            list.SelectedIndex = IndexOf(rows, again);
        }
    }

    private static int IndexOf(IReadOnlyList<GraphRow> list, GraphRow row)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], row))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Shows the chosen commit's files and message; when the choice moves (or the log is read again) while
    /// they are read, only the newest reading lands.</summary>
    private async Task ShowCommitAsync()
    {
        int reading = ++showing;
        if (Selected is not { } commit || vcs.Repository is not { } repository)
        {
            files.Clear();
            details.Text = string.Empty;
            return;
        }
        // A history's commit lists what it did there, which the history read with it.
        IReadOnlyList<FileChange> changed = Scope is not null && changes.TryGetValue(commit.Hash, out IReadOnlyList<FileChange>? there)
            ? there
            : await repository.CommitFilesAsync(commit.Hash);
        string message = await repository.MessageAsync(commit.Hash);
        if (reading != showing)
        {
            return;
        }
        files.Clear();
        foreach (FileChange change in changed)
        {
            files.Add(change);
        }
        string refs = commit.Refs.Count > 0 ? $"\n{string.Join(", ", commit.Refs)}" : string.Empty;
        details.Text = $"{message}\n\n{commit.Hash}{refs}\n{commit.Author} <{commit.Email}>\n{commit.Time.ToLocalTime():f}";
    }

    /// <summary>Show Diff's keys, as the log's list of a commit's files takes them (<see cref="AppActions.KeysIn"/>), ahead
    /// of what they run elsewhere while one is chosen.</summary>
    public bool TakesKey(KeyEventArgs e) => fileList.IsKeyboardFocusWithin && fileList.SelectedItem is FileChange && AppActions.Presses(AppActions.ShowDiff, e);

    private void RequestDiff()
    {
        if (Selected is { } commit && fileList.SelectedItem is FileChange change)
        {
            DiffRequested?.Invoke(this, (commit, change));
        }
    }

    private Control Row(GraphRow row)
    {
        Graph graph = new(row) { Width = (row.Width * LaneWidth) + 8, Height = RowHeight };
        // A dock, not a stack, so the subject is given the room left and trims to it.
        DockPanel subject = new() { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        foreach (string label in row.Commit.Refs)
        {
            // A long branch's name ends in an ellipsis and leaves the subject room.
            TextBlock text = new() { Text = label, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(TokenKeys.Vcs_RefText));
            ToolTip.SetTip(text, label);
            Border tag = new() { [DockProperty] = Dock.Left, Classes = { "tag" }, Padding = new Thickness(5, 1), Margin = new Thickness(0, 0, 6, 0), Child = text, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 160 };
            tag.Bind(Border.BackgroundProperty, tag.GetResourceObservable(TokenKeys.Vcs_Ref));
            subject.Children.Add(tag);
        }
        if (FileChangeOf(row.Commit) is { } change && change.Path != Scope!.Path && vcs.Repository is { } repository)
        {
            // Under another name then: the path the commit had the file at.
            TextBlock then = Muted(repository.Relative(change.Path));
            then[DockProperty] = Dock.Right;
            then.MaxWidth = 280;
            subject.Children.Add(then);
        }
        subject.Children.Add(new TextBlock { Text = row.Commit.Subject, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        TextBlock author = Muted(row.Commit.Author);
        TextBlock date = Muted(When(row.Commit.Time));
        // The author and the date at most 150 and 180 wide, and never more than half of a narrow log between them.
        Grid line = new() { Height = RowHeight };
        line.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        line.ColumnDefinitions.Add(new ColumnDefinition(2, GridUnitType.Star));
        line.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = 150 });
        line.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = 180 });
        Grid.SetColumn(subject, 1);
        Grid.SetColumn(author, 2);
        Grid.SetColumn(date, 3);
        line.Children.Add(graph);
        line.Children.Add(subject);
        line.Children.Add(author);
        line.Children.Add(date);
        return line;

        static TextBlock Muted(string text)
        {
            TextBlock block = MutedText.Of(text);
            block.Margin = new Thickness(8, 0);
            return block;
        }
    }

    /// <summary>Dates: Today and Yesterday by the time, older ones by the date.</summary>
    internal static string When(DateTimeOffset time)
    {
        DateTime local = time.ToLocalTime().DateTime;
        string clock = local.ToString("t", CultureInfo.CurrentCulture);
        return (DateTime.Today - local.Date).Days switch
        {
            0 => $"Today {clock}",
            1 => $"Yesterday {clock}",
            _ => local.ToString("d", CultureInfo.CurrentCulture) + " " + clock,
        };
    }

    private Control FileRow(FileChange change)
    {
        PathIcon icon = new()
        {
            Data = Explorer.FileIcons.ForFile(change.Path),
            Foreground = Explorer.FileIcons.BrushForFile(change.Path),
            Width = 14,
            Height = 14,
            Margin = new Thickness(8, 0, 6, 0),
        };
        TextBlock name = new() { Text = change.Name, VerticalAlignment = VerticalAlignment.Center };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable(CommitView.KindToken(change.Kind)));
        string where = vcs.Repository is { } repository ? Path.GetDirectoryName(repository.Relative(change.Path))?.Replace('\\', '/') ?? string.Empty : string.Empty;
        TextBlock folder = new() { Text = where, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12 };
        folder.Classes.Add("muted");
        // The folder gives way first, then the name, each ending in an ellipsis, in a narrow pane.
        return new ShrinkingRow { Height = RowHeight, Children = { icon, name, folder } };
    }

    /// <summary>A row's part of the graph: the lines through it, lane by lane, and the commit's dot.</summary>
    private sealed class Graph(GraphRow row) : Control
    {
        public override void Render(DrawingContext context)
        {
            double mid = Bounds.Height / 2;
            double bottom = Bounds.Height;
            string hash = row.Commit.Hash;
            for (int i = 0; i < row.Above.Count; i++)
            {
                if (row.Above[i] is not { } waiting)
                {
                    continue;
                }
                if (waiting == hash)
                {
                    // A lane that waited for this commit comes into its dot.
                    context.DrawLine(PenFor(i), new Point(X(i), 0), new Point(X(row.Lane), mid));
                }
                else if (IndexOf(row.Below, waiting) is int j and >= 0)
                {
                    context.DrawLine(PenFor(i), new Point(X(i), 0), new Point(X(i), mid));
                    context.DrawLine(PenFor(i), new Point(X(i), mid), new Point(X(j), bottom));
                }
            }
            foreach (string parent in row.Joins)
            {
                if (IndexOf(row.Below, parent) is int j and >= 0)
                {
                    context.DrawLine(PenFor(j), new Point(X(row.Lane), mid), new Point(X(j), bottom));
                }
            }
            IBrush dot = BrushFor(row.Lane);
            context.DrawEllipse(dot, null, new Point(X(row.Lane), mid), 4, 4);
        }

        private static double X(int lane) => 10 + (lane * LaneWidth);

        private static int IndexOf(IReadOnlyList<string?> lanes, string hash)
        {
            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] == hash)
                {
                    return i;
                }
            }
            return -1;
        }

        private IBrush BrushFor(int lane) =>
            this.TryFindResource($"Passenger.Graph.{lane % 6}", ActualThemeVariant, out object? found) && found is IBrush brush ? brush : Brushes.Transparent;

        private Pen PenFor(int lane) => new(BrushFor(lane), 1.6);
    }
}

/// <summary>The commits of <see cref="To"/> that <see cref="From"/> lacks, one way of Compare with Current: the two by
/// their full ref names and as they are shown.</summary>
public sealed record LogRange(string FromRef, string From, string ToRef, string To);
