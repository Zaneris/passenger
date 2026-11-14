using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Vcs;
using Passenger.Core;
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
    private readonly EmptyState placeholder;
    private readonly Border branchFilter;
    private readonly TextBlock branchFilterText;
    private readonly EventHandler onHistoryChanged;
    private readonly EventHandler onSignatures;

    /// <summary>Raised when signatures the rows asked for have been read, for the rows shown to mark them.</summary>
    private event EventHandler? SignaturesLanded;
    private IReadOnlyList<GraphRow> all = [];

    /// <summary>A history's commits' changes there, by hash.</summary>
    private Dictionary<string, IReadOnlyList<FileChange>> changes = [];
    private bool stale = true;
    private int loads;

    /// <summary>How many commits the Log reads: the newest <see cref="Git.LogLength"/>, more once a commit further down is
    /// asked for (a blame's), kept until another folder's working tree is followed.</summary>
    private int most = Git.LogLength;

    /// <summary>How many times the log has been read, which a save never adds to.</summary>
    public int Loads => loads;

    /// <summary>How many pens the graphs have made: one for each lane's colour when a theme gives other brushes, none for
    /// a line drawn.</summary>
    public static int PensMade { get; private set; }
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
            Classes = { StyleClasses.Tree },
        };
        ReusedRows.Use(list, Row);
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
                ListMenus.Item("Compare with Local", null, () => CompareRequested?.Invoke(this, (Scope!.Path, commit, change))),
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
            Classes = { StyleClasses.Tree },
            ItemsSource = files,
        };
        ReusedRows.Use(fileList, FileRow);
        fileList.DoubleTapped += (_, _) => RequestDiff();
        fileList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter || AppActions.Presses(AppActions.ShowDiff, e))
            {
                RequestDiff();
                e.Handled = true;
            }
            else if (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None && fileList.SelectedItem is FileChange { Kind: not ChangeKind.Deleted } shown)
            {
                OpenRequested?.Invoke(this, shown.Path);
                e.Handled = true;
            }
        };
        // A commit's file offers what the Commit window's changed files do.
        ListMenus.Attach(fileList, () => Selected is not { } commit || fileList.SelectedItem is not FileChange change ? [] :
        [
            ListMenus.Item("Show Diff", "Enter", () => DiffRequested?.Invoke(this, (commit, change))),
            ListMenus.Item("Compare with Local", null, () => CompareRequested?.Invoke(this, (change.Path, commit, change)), change.Kind is not ChangeKind.Deleted),
            ListMenus.Item("Jump to Source", "F4", () => OpenRequested?.Invoke(this, change.Path), change.Kind is not ChangeKind.Deleted),
            new Separator(),
            ListMenus.Item("Copy Path", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, change.Path))),
        ]);
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
        placeholder = new EmptyState();
        placeholder.ActionRequested += (_, action) => ActionRequested?.Invoke(this, action);

        filter = new TextBox { Classes = { StyleClasses.Search }, PlaceholderText = "Text or hash", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        ToolFlow.SetFills(filter, new ToolLayout.Fills(120, 246));
        filter.TextChanged += (_, _) => Filter();
        Button refresh = ToolButtons.Of(AppActions.GitLogRefresh, IconKeys.Rerun, "Refresh", action => ActionRequested?.Invoke(this, action));
        // The Branch filter, shown while the log is filtered to a branch, its x showing every branch again.
        branchFilterText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        PathIcon clearGlyph = new() { Classes = { StyleClasses.Small } };
        clearGlyph.Bind(PathIcon.DataProperty, clearGlyph.GetResourceObservable(IconKeys.Close));
        Button clearBranch = new() { Classes = { StyleClasses.TabPin }, Content = clearGlyph, Margin = new Thickness(6, 0, 0, 0) };
        ToolButtons.Tip(clearBranch, "Clear Branch Filter");
        clearBranch.Click += (_, _) => FilterBranch(null, all: true);
        branchFilter = new Border
        {
            IsVisible = false,
            Classes = { StyleClasses.Tag },
            Padding = new Thickness(8, 2, 4, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { branchFilterText, clearBranch } },
        };
        branchFilter.Bind(Border.BackgroundProperty, branchFilter.GetResourceObservable(TokenKeys.Selected));
        Border top = ToolBars.Top(new ToolFlow { Margin = new Thickness(8, 2), Children = { filter, branchFilter, refresh } });

        Grid right = new() { RowDefinitions = new RowDefinitions("*,1,*") };
        HairlineDivider between = new(GridResizeDirection.Rows) { [Grid.RowProperty] = 1 };
        Grid.SetRow(details, 2);
        right.Children.Add(fileList);
        right.Children.Add(between);
        right.Children.Add(details);
        PaneSplit split = new(new Panel { Children = { list, placeholder } }, right, "3*,1,2*");
        if (scope is null && range is null)
        {
            Branches = new BranchesPanel(vcs);
            Branches.FilterRequested += (_, branch) => FilterBranch(branch);
            Branches.NavigateRequested += (_, branch) => IdeErrors.Observe(SelectHashAsync(branch.Hash));
            Children.Add(top);
            Children.Add(new PaneSplit(Branches, split, "240,1,*", "*,1,3*"));
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
        onSignatures = (_, _) => SignaturesLanded?.Invoke(this, EventArgs.Empty);
        vcs.Signatures.Landed += onSignatures;
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
        ShowBranchFilter(all ? (null, null) : branch is null ? ("HEAD", "HEAD") : (branch.Ref.FullName, branch.Name));
        IdeErrors.Observe(LoadAsync());
    }

    private void ShowBranchFilter((string? Revision, string? Name) filter)
    {
        (BranchFilter, BranchFilterName) = filter;
        branchFilter.IsVisible = BranchFilter is not null;
        branchFilterText.Text = $"Branch: {BranchFilterName}";
    }

    /// <summary>A history's tab closed: it stops following the history.</summary>
    public void Close()
    {
        vcs.HistoryChanged -= onHistoryChanged;
        vcs.Signatures.Landed -= onSignatures;
    }

    /// <summary>The Log follows another folder's working tree from now on, its session's in place of the last's: every
    /// branch again, read again when shown.</summary>
    public void Bind(VcsController next)
    {
        Close();
        vcs = next;
        vcs.HistoryChanged += onHistoryChanged;
        vcs.Signatures.Landed += onSignatures;
        Branches?.Bind(next);
        (BranchFilter, BranchFilterName) = (null, null);
        branchFilter.IsVisible = false;
        most = Git.LogLength;
        onHistoryChanged(this, EventArgs.Empty);
    }

    /// <summary>A file of a commit whose change to show: the commit and the file.</summary>
    public event EventHandler<(CommitInfo Commit, FileChange File)>? DiffRequested;

    /// <summary>A file as a commit had it, to compare with the file as it is now (its path now first).</summary>
    public event EventHandler<(string Local, CommitInfo Commit, FileChange File)>? CompareRequested;

    /// <summary>A commit's file to open as it is now.</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>One of the window's actions asked for by its button.</summary>
    public event EventHandler<string>? ActionRequested;

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

    /// <summary>Chooses the commit with this hash, reading the log first if it has not been, and down to the commit when it
    /// is older than those read; a filter that hides it, the text's or a branch's whose history lacks it, shows it again.</summary>
    public async Task SelectHashAsync(string hash)
    {
        await ReachAsync(hash);
        if (!all.Any(r => r.Commit.Hash == hash) && BranchFilter is not null)
        {
            ShowBranchFilter((null, null));
            await ReachAsync(hash);
        }
        FilterText = string.Empty;
        Filter();
        if (rows.FirstOrDefault(r => r.Commit.Hash == hash) is { } row)
        {
            list.SelectedIndex = IndexOf(rows, row);
            list.ScrollIntoView(list.SelectedIndex);
        }
    }

    /// <summary>Reads the log if it has not been, and down to the commit when it is older than those read.</summary>
    private async Task ReachAsync(string hash)
    {
        if (!all.Any(r => r.Commit.Hash == hash))
        {
            await LoadAsync();
        }
        if (!all.Any(r => r.Commit.Hash == hash) && Scope is null && Range is null && vcs.Repository is { } repository
            && await repository.LogDepthAsync(hash, BranchFilter is { } branch ? [branch] : null) is int depth && depth > most)
        {
            most = depth;
            await LoadAsync();
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
            : BranchFilter is { } branch ? await repository.LogAsync(most, [branch])
            : await repository.LogAsync(most);
        IReadOnlyList<GraphRow> laid = await BackgroundWork.Run(() => Scope is null ? CommitGraph.Layout(commits) : CommitGraph.Line(commits));
        if (load != loads)
        {
            return;
        }
        changes = history.ToDictionary(h => h.Commit.Hash, h => h.Changes);
        all = laid;
        Filter();
        string name = Scope is null ? string.Empty : Path.GetFileName(Path.TrimEndingDirectorySeparator(Scope.Path));
        placeholder.Text = repository is null ? vcs.NoRepository
            : Range is { } compared ? $"{compared.From} contains all commits from {compared.To}"
            : Scope is null ? "No commits yet"
            : Scope.IsLines ? $"No commit changed these lines of {name}"
            : $"Git has no history of {name}";
        bool creates = repository is null && Scope is null && Range is null && vcs.WhyNotCreate is null;
        placeholder.Offer(creates ? AppActions.CreateRepository : null, VcsController.CreateRepository);
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
            : await repository.CommitFilesAsync(commit);
        string message = await repository.MessageAsync(commit.Hash);
        CommitSignature? signature = (await vcs.Signatures.ReadAsync(repository, [commit.Hash])).GetValueOrDefault(commit.Hash);
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
        string signed = signature is null ? string.Empty : $"\n{signature.Said}";
        details.Text = $"{message}\n\n{commit.Hash}{refs}\n{commit.Author} <{commit.Email}>\n{commit.Time.ToLocalTime():f}{signed}";
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

    /// <summary>A commit's row for whichever commit the list hands its container: its part of the graph, its refs as tags,
    /// the path it had the file at (under another name then), its subject, its signature's mark (read as the row is
    /// shown), author and date.</summary>
    private Control Row()
    {
        Graph graph = new() { Height = RowHeight };
        // A dock, not a stack, so the subject is given the room left and trims to it.
        DockPanel subject = new() { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        TextBlock then = Muted();
        then[DockProperty] = Dock.Right;
        then.MaxWidth = 280;
        TextBlock words = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        RowIcon mark = new() { [DockProperty] = Dock.Right, Width = 14, Height = 14, IsVisible = false, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        string? marked = null;
        subject.Children.Add(mark);
        subject.Children.Add(then);
        subject.Children.Add(words);
        List<(Border Tag, TextBlock Text)> tags = [];
        TextBlock author = Muted();
        TextBlock date = Muted();
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
        EventHandler landed = (_, _) => Mark();
        line.AttachedToVisualTree += (_, _) => SignaturesLanded += landed;
        line.DetachedFromVisualTree += (_, _) => SignaturesLanded -= landed;
        return line.Showing<GraphRow>(row =>
        {
            graph.Show(row);
            graph.Width = (row.Width * LaneWidth) + 8;
            IReadOnlyList<string> refs = row.Commit.Refs;
            while (tags.Count < refs.Count)
            {
                // A long branch's name ends in an ellipsis and leaves the subject room.
                TextBlock text = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(TokenKeys.Vcs_RefText));
                Border tag = new() { [DockProperty] = Dock.Left, Classes = { StyleClasses.Tag }, Padding = new Thickness(5, 1), Margin = new Thickness(0, 0, 6, 0), Child = text, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 160 };
                tag.Bind(Border.BackgroundProperty, tag.GetResourceObservable(TokenKeys.Vcs_Ref));
                subject.Children.Insert(tags.Count, tag);
                tags.Add((tag, text));
            }
            for (int i = 0; i < tags.Count; i++)
            {
                tags[i].Tag.IsVisible = i < refs.Count;
                tags[i].Text.Text = i < refs.Count ? refs[i] : null;
                ToolTip.SetTip(tags[i].Text, i < refs.Count ? refs[i] : null);
            }
            // Under another name then: the path the commit had the file at.
            string? moved = FileChangeOf(row.Commit) is { } change && change.Path != Scope!.Path && vcs.Repository is { } repository ? repository.Relative(change.Path) : null;
            then.IsVisible = moved is not null;
            then.Text = moved;
            words.Text = row.Commit.Subject;
            author.Text = row.Commit.Author;
            date.Text = When(row.Commit.Time);
            marked = row.Commit.Hash;
            Mark();
        });

        // The shown commit's signature where it has been read, else asked for; a commit unsigned has none.
        void Mark()
        {
            CommitSignature? signature = null;
            if (marked is not null && vcs.Repository is { } repository && !vcs.Signatures.TryGet(repository, marked, out signature))
            {
                vcs.Signatures.Want(repository, marked);
            }
            mark.IsVisible = signature is not null;
            (mark.DataKey, mark.ForegroundKey) = signature?.State switch
            {
                SignatureState.Good => (IconKeys.SignatureGood, TokenKeys.Vcs_Signature_Good),
                SignatureState.Bad => (IconKeys.SignatureBad, TokenKeys.Vcs_Signature_Bad),
                SignatureState.Expired => (IconKeys.SignatureExpired, TokenKeys.Vcs_Signature_Expired),
                SignatureState.Unverified => (IconKeys.SignatureUnverified, TokenKeys.Vcs_Signature_Unverified),
                _ => (null, null),
            };
            ToolTip.SetTip(mark, signature?.Said);
        }

        static TextBlock Muted()
        {
            TextBlock block = MutedText.Of(string.Empty);
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

    /// <summary>A changed file's row for whichever file the list hands its container (<see cref="ChangedFileRow"/>), its
    /// folder from the repository's top.</summary>
    private Control FileRow()
    {
        ChangedFileRow row = new();
        return row.Line.Showing<FileChange>(change => row.Show(change, vcs.Repository));
    }

    /// <summary>The lanes' colours as a theme gives them, and a pen of each.</summary>
    private sealed record LaneColours(IBrush[] Fills, Pen[] Lines);

    /// <summary>A row's part of the graph: the lines through it, lane by lane, and the commit's dot, drawn in the colours
    /// its theme gives, which every row shares while the theme gives the same brushes.</summary>
    private sealed class Graph : Control
    {
        private static readonly string[] Keys = [TokenKeys.Graph_0, TokenKeys.Graph_1, TokenKeys.Graph_2, TokenKeys.Graph_3, TokenKeys.Graph_4, TokenKeys.Graph_5];

        /// <summary>The colours the theme last looked up gave, with their pens.</summary>
        private static LaneColours? shared;

        private GraphRow? row;
        private LaneColours? colours;

        public Graph()
        {
            // A theme, or a look laid over the application's resources (Settings), gives other brushes.
            ActualThemeVariantChanged += (_, _) => Recolour();
            ResourcesChanged += (_, _) => Recolour();
        }

        /// <summary>Draws another commit's part of the graph.</summary>
        public void Show(GraphRow shown)
        {
            row = shown;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (row is null)
            {
                return;
            }
            LaneColours lanes = colours ??= Colours();
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
                    context.DrawLine(lanes.Lines[i % Keys.Length], new Point(X(i), 0), new Point(X(row.Lane), mid));
                }
                else if (IndexOf(row.Below, waiting) is int j and >= 0)
                {
                    context.DrawLine(lanes.Lines[i % Keys.Length], new Point(X(i), 0), new Point(X(i), mid));
                    context.DrawLine(lanes.Lines[i % Keys.Length], new Point(X(i), mid), new Point(X(j), bottom));
                }
            }
            for (int p = 0; p < row.Joins.Count; p++)
            {
                if (IndexOf(row.Below, row.Joins[p]) is int j and >= 0)
                {
                    context.DrawLine(lanes.Lines[j % Keys.Length], new Point(X(row.Lane), mid), new Point(X(j), bottom));
                }
            }
            context.DrawEllipse(lanes.Fills[row.Lane % Keys.Length], null, new Point(X(row.Lane), mid), 4, 4);
        }

        private void Recolour()
        {
            colours = null;
            InvalidateVisual();
        }

        /// <summary>The colours the theme gives this graph: the shared ones while it gives their brushes, else new ones
        /// shared from now on.</summary>
        private LaneColours Colours()
        {
            if (shared is { } last && Gives(last.Fills))
            {
                return last;
            }
            IBrush[] fills = new IBrush[Keys.Length];
            Pen[] lines = new Pen[Keys.Length];
            for (int i = 0; i < Keys.Length; i++)
            {
                fills[i] = Tokens.Brush(this, Keys[i]);
                lines[i] = new Pen(fills[i], 1.6);
                PensMade++;
            }
            return shared = new LaneColours(fills, lines);
        }

        private bool Gives(IBrush[] fills)
        {
            for (int i = 0; i < Keys.Length; i++)
            {
                if (!ReferenceEquals(Tokens.Brush(this, Keys[i]), fills[i]))
                {
                    return false;
                }
            }
            return true;
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
    }
}

/// <summary>The commits of <see cref="To"/> that <see cref="From"/> lacks, one way of Compare with Current: the two by
/// their full ref names and as they are shown.</summary>
public sealed record LogRange(string FromRef, string From, string ToRef, string To);
