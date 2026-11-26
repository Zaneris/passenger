using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.ToolWindows;

/// <summary>A changed file in the Commit window, and whether it goes into the next commit: while git's index holds staged
/// changes, as listed under Staged (<paramref name="staged"/> true) or Unstaged (false), its box saying which.</summary>
public sealed class CommitRow(FileChange change, bool chosen, bool? staged = null) : Observable, ITreeRow
{
    private bool isChosen = chosen && IsChoosable(change);
    private int depth = 1;

    public FileChange Change { get; } = change;

    /// <summary>Whether it is listed under Staged (true) or Unstaged (false), while git's index holds staged changes; null
    /// while what is ticked goes in.</summary>
    public bool? Staged { get; } = staged;

    /// <summary>How deep it is listed: under its group, or under its folders while the window groups by directory.</summary>
    public int Depth
    {
        get => depth;
        internal set => Set(ref depth, value);
    }

    public bool Expands => false;

    public bool IsExpanded => false;

    /// <summary>Whether it has a box: a file in conflict is resolved first, and a submodule whose own files alone changed
    /// has nothing to commit here.</summary>
    public bool CanChoose => IsChoosable(Change);

    private static bool IsChoosable(FileChange change) => change.Kind is not ChangeKind.Conflicted && change.Uncommittable is null;

    /// <summary>The changes that go in where only some of the file's do; null where all or none do.</summary>
    public PartialCommit? Partial { get; private set; }

    /// <summary>The file's box: chosen, not, or null where only some of its changes go in.</summary>
    public bool? CheckState => Partial is null ? IsChosen : null;

    /// <summary>Whether each change of <paramref name="diff"/> goes in, asked as a diff of the file is shown: a choice of
    /// some changes made on another text of the file moves onto the changes it still makes (<see cref="PartialCommit.Following"/>).</summary>
    public bool[] ChosenIn(FileDiff diff)
    {
        if (Partial is { } partial && !ReferenceEquals(partial.Diff, diff))
        {
            Choose(diff, partial.Following(diff));
        }
        if (Partial is { } chosen)
        {
            HashSet<DiffHunk> taken = [.. chosen.Hunks];
            return [.. diff.Hunks.Select(taken.Contains)];
        }
        return [.. diff.Hunks.Select(_ => IsChosen)];
    }

    public void ChooseHunk(FileDiff diff, int index, bool take)
    {
        bool[] chosen = ChosenIn(diff);
        chosen[index] = take;
        Choose(diff, [.. diff.Hunks.Where((_, i) => chosen[i])]);
    }

    private void Choose(FileDiff diff, IReadOnlyList<DiffHunk> hunks)
    {
        IsChosen = hunks.Count > 0;
        if (hunks.Count > 0 && hunks.Count < diff.Hunks.Count)
        {
            Partial = new PartialCommit(diff, hunks, Change.From);
            Raise(nameof(CheckState));
        }
    }

    public bool IsChosen
    {
        get => isChosen;
        set
        {
            Partial = null;
            Set(ref isChosen, value);
            Raise(nameof(CheckState));
        }
    }
}

/// <summary>A row the Commit window's files sit under, a group or a folder: every file under it, the rows right under it,
/// whether it is open, and whether every file under it goes in (true), none (false) or some (null).</summary>
public abstract class CommitBranch(string key, int depth, IReadOnlyList<CommitRow> rows, IReadOnlyList<ITreeRow> children) : Observable, ITreeRow
{
    private bool? isChosen;
    private bool isExpanded = true;

    /// <summary>What it is kept open or closed by, as the list is filled again.</summary>
    public string Key { get; } = key;

    public int Depth { get; } = depth;

    /// <summary>Every file under it, however deep.</summary>
    public IReadOnlyList<CommitRow> Rows { get; } = rows;

    /// <summary>The folders and files right under it, folders first.</summary>
    public IReadOnlyList<ITreeRow> Children { get; } = children;

    /// <summary>Whether it has a box: some file under it can be chosen.</summary>
    public bool CanChoose { get; } = rows.Any(r => r.CanChoose);

    public bool Expands => true;

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    public bool? IsChosen
    {
        get => isChosen;
        private set => Set(ref isChosen, value);
    }

    /// <summary>Works out <see cref="IsChosen"/> again from the rows right under it, its folders worked out first.</summary>
    internal void Update()
    {
        int some = 0;
        int boxed = 0;
        bool whole = true;
        foreach (ITreeRow child in Children)
        {
            (bool? state, bool can) = child switch
            {
                CommitRow file => (file.CheckState, file.CanChoose),
                CommitBranch branch => (branch.IsChosen, branch.CanChoose),
                _ => ((bool?)false, false),
            };
            some += state != false ? 1 : 0;
            boxed += can ? 1 : 0;
            whole &= state != null;
        }
        IsChosen = some == 0 ? false : some == boxed && whole ? true : null;
    }
}

/// <summary>A group of the Commit window's files: Changes, Unversioned Files or Merge Conflicts.</summary>
public sealed class CommitGroup(string name, IReadOnlyList<CommitRow> rows, IReadOnlyList<ITreeRow> children) : CommitBranch(name, 0, rows, children)
{
    public string Name { get; } = name;
}

/// <summary>A folder of a group's files as Group by Directory lists them, by its path from the repository's top; a folder
/// holding only one folder is one row with it ("src/App").</summary>
public sealed class CommitFolder(string key, string name, int depth, IReadOnlyList<CommitRow> rows, IReadOnlyList<ITreeRow> children)
    : CommitBranch(key, depth, rows, children)
{
    /// <summary>Its name, or the names of the folders it stands for with '/' between them.</summary>
    public string Name { get; } = name;
}

/// <summary>The Commit window: the changed files under Changes and the new ones under Unversioned Files, each with a box
/// for whether it goes in (changes are in and new files out until chosen), and files in conflict above them under Merge
/// Conflicts, with its Resolve link and no box; Amend, Sign-off (kept for the folder) and whether this commit is signed
/// and with which key (git's config, turned on or off for one commit; Commit Signing… sets it up) and the message history,
/// then the message (a merge's own message while one is under way); Commit and Commit and Push. Group By › Directory lists each group's files
/// under their folders, each folder with a box for every file under it. Double-click opens a file's diff, or a
/// conflicted file's merge; Rollback puts the chosen ones back. In a folder of no repository it shows its empty state
/// alone.</summary>
public sealed class CommitView : DockPanel, IOwnKeys
{
    /// <summary>A check box's box, as the theme draws one with no words.</summary>
    private const double CheckWidth = 20;

    /// <summary>A row's check box and the room after it, which a row with none leaves, so its name lines up with its
    /// neighbours'.</summary>
    private const double BoxRoom = CheckWidth + Gaps.Hair;

    private VcsController vcs;
    private readonly TreeRows<ITreeRow> rows = [];
    private readonly Dictionary<string, bool> chosen = [];

    /// <summary>The groups as last filled, and every group and folder under them, each before those under it.</summary>
    private List<CommitGroup> groups = [];
    private List<CommitBranch> branches = [];

    /// <summary>The groups and folders the person closed, by their keys, kept closed as the list is filled again.</summary>
    private readonly HashSet<string> closed = [];
    private readonly TreeList list;
    private readonly ToolTabs.Strip<bool> pages = new();

    /// <summary>The repositories the folder holds, one tab each, while it holds more than one; the one chosen is listed.</summary>
    private readonly ToolTabs.Strip<VcsController> repositories = new();
    private readonly Border repositoriesRow;
    private bool listingRepositories;
    private readonly Panel pagesShown = new();
    private readonly ToolFlow bar;
    private readonly DockPanel changes;
    private readonly StackPanel tools;
    private StashesView? stashes;
    private readonly TextBox message;
    private readonly CheckBox amend;
    private readonly CheckBox signOff;
    private readonly CheckBox sign;
    private readonly TextBlock signWith;
    private readonly TextBlock unsigned;

    /// <summary>How git's config has commits signed, as last read; whether the person turned Sign over for this commit.</summary>
    private SigningSetup signing = SigningSetup.None;
    private bool signTurned;
    private Git? signingOf;
    private bool showingSigning;
    private int signingReads;
    private readonly Button commit;
    private readonly Button commitAndPush;
    private readonly Button rollback;
    private readonly Button groupBy;

    /// <summary>Whether the list was last filled grouped by directory.</summary>
    private bool grouped;

    /// <summary>Whether the list was last filled while git's index held staged changes (<see cref="VcsController.HoldsStaged"/>):
    /// its files under Staged and Unstaged, a box staging or unstaging its file, and Commit taking what is staged.</summary>
    private bool staging;
    private readonly EmptyState placeholder;
    private readonly Border form;
    private readonly StackPanel underWay;
    private readonly TextBlock underWayText;
    private readonly Button continueRebase;
    private readonly Button skipCommit;
    private readonly Button abort;
    private string? beforeAmend;

    /// <summary>The merge's message the box was given, taken away again if the merge ends some other way.</summary>
    private string? prefilled;

    /// <summary>The offer to commit the chosen files' moves first, shown where git would lose one: the box, why, and the
    /// first commit's message, which the person may change.</summary>
    private readonly StackPanel movesOffer;
    private readonly CheckBox movesFirst;
    private readonly TextBlock movesSaid;
    private readonly TextBox movesMessage;

    /// <summary>The first commit's message as the window last wrote it, kept until the person types another.</summary>
    private string movesWritten = string.Empty;

    /// <summary>What the last look for lost moves was of (the status and the chosen files), and how many have begun.</summary>
    private (IReadOnlyList<FileChange> Changes, string Chosen)? lostOf;
    private int lostLooks;

    public CommitView(VcsController git)
    {
        vcs = git;
        rollback = ToolButtons.Of(IconKeys.Rollback, "Rollback", RollbackChosenOrSelected);
        groupBy = ToolButtons.Of(IconKeys.GroupBy, "Group By", ShowGroupBy);
        list = new TreeList
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = rows,
            SelectionMode = SelectionMode.Multiple,
        };
        ReusedRows.Use(list, Row);
        ListMenus.Attach(list, () => list.SelectedItem is not CommitRow chosen ? [] : chosen.Change.Kind is ChangeKind.Conflicted ?
        [
            ListMenus.Open("Merge…", () => MergeRequested?.Invoke(this, chosen.Change.Path)),
            ListMenus.Item("Resolve Conflicts…", () => ResolveRequested?.Invoke(this, EventArgs.Empty)),
            ListMenus.Source(() => OpenRequested?.Invoke(this, chosen.Change.Path)),
            new Separator(),
            ListMenus.Item("Copy Path", () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Change.Path))),
        ] :
        [
            ListMenus.Open("Show Diff", () => DiffRequested?.Invoke(this, chosen.Change.Path)),
            ListMenus.Source(() => OpenRequested?.Invoke(this, chosen.Change.Path), Deleted(chosen.Change)),
            new Separator(),
            ListMenus.Item("Rollback…", () => RollbackRequested?.Invoke(this, [.. ChosenChanges(chosen)]), vcs.WhyNotRollback([.. ChosenChanges(chosen)])),
            ListMenus.Keyed("Stage", AppActions.Stage, () => StageRequested?.Invoke(this, ([.. ChosenChanges(chosen)], true)), WhyNotStage([.. ChosenChanges(chosen)], stage: true)),
            ListMenus.Keyed("Unstage", AppActions.Unstage, () => StageRequested?.Invoke(this, ([.. ChosenChanges(chosen)], false)), WhyNotStage([.. ChosenChanges(chosen)], stage: false)),
            ListMenus.Item("Delete…", () => DeleteRequested?.Invoke(this, chosen.Change.Path), Deleted(chosen.Change)),
            ListMenus.Item("Add to .gitignore", () => IgnoreRequested?.Invoke(this, chosen.Change.Path), chosen.Change.Kind is ChangeKind.Unversioned ? null : Refusals.Tracked),
            new Separator(),
            ListMenus.Item("Copy Path", () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Change.Path))),
        ]);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItems is [CommitRow { Change.Kind: not ChangeKind.Conflicted } chosen])
            {
                ChoiceMoved?.Invoke(this, chosen.Change.Path);
            }
            UpdateRollback();
        };
        // A double click and Enter show the diff, F4 the file, Space flips the boxes of the files selected and under them.
        TreeKeys.Attach<ITreeRow>(list, Toggle, acts: new ListActs
        {
            Open = OpenChosen,
            Source = () =>
            {
                if (list.SelectedItem is CommitRow { Change.Kind: not ChangeKind.Deleted } shown)
                {
                    OpenRequested?.Invoke(this, shown.Change.Path);
                }
            },
            Keys = e =>
            {
                if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
                {
                    CommitRow[] selected = [.. SelectedFiles().Where(r => r.CanChoose)];
                    if (staging)
                    {
                        Restage(selected, !selected.All(r => r.IsChosen));
                    }
                    else
                    {
                        Choose(selected, !selected.All(r => r.IsChosen));
                    }
                    return true;
                }
                bool diff = AppActions.Presses(AppActions.ShowDiff, e);
                if (diff)
                {
                    OpenChosen();
                }
                return diff;
            },
        });
        placeholder = new EmptyState();
        placeholder.ActionRequested += (_, action) => ActionRequested?.Invoke(this, action);

        // What git is in the middle of, said over Amend until it ends, its conflicts resolved or a rebase stopped at an edit.
        underWayText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        continueRebase = new Button { Classes = { StyleClasses.Link }, Content = "Continue Rebase" };
        continueRebase.Click += (_, _) => ActionRequested?.Invoke(this, AppActions.ContinueRebase);
        skipCommit = new Button { Classes = { StyleClasses.Link }, Content = "Skip Commit" };
        skipCommit.Click += (_, _) => ActionRequested?.Invoke(this, AppActions.SkipRebase);
        abort = new Button { Classes = { StyleClasses.Link } };
        abort.Click += (_, _) =>
        {
            if (AbortOf(vcs.Merge.Operation) is { } action)
            {
                ActionRequested?.Invoke(this, action.Id);
            }
        };
        StackPanel links = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Wide, Children = { continueRebase, skipCommit, abort } };
        underWay = new StackPanel { Margin = new Thickness(ListRow.TextStart, Gaps.Small, ListRow.TextStart, 0), Spacing = Gaps.Hair, IsVisible = false, Children = { underWayText, links } };

        amend = new CheckBox { Content = "Amend" };
        amend.IsCheckedChanged += (_, _) => IdeErrors.Observe(AmendChangedAsync());
        signOff = new CheckBox { Content = "Sign-off" };
        ToolTip.SetTip(signOff, "Adds a Signed-off-by line with your name and email to the message (git commit --signoff), kept for this folder");
        signOff.IsCheckedChanged += (_, _) =>
        {
            if (!showingSigning)
            {
                vcs.SignOff = signOff.IsChecked == true;
            }
        };
        signWith = new TextBlock { Text = "Sign" };
        sign = new CheckBox { Content = signWith };
        sign.IsCheckedChanged += (_, _) =>
        {
            if (!showingSigning)
            {
                signTurned = true;
            }
        };
        Button setUp = new() { Classes = { StyleClasses.Link }, Content = SigningFailure.SetUp, VerticalAlignment = VerticalAlignment.Center };
        setUp.Click += (_, _) => ActionRequested?.Invoke(this, AppActions.CommitSigning);
        WrapPanel choices = new() { ItemSpacing = Gaps.Group, Children = { amend, signOff, sign, setUp } };
        Button history = ToolButtons.Of(AppActions.CommitMessageHistory, IconKeys.History, "Commit Message History", action => ActionRequested?.Invoke(this, action));
        history.VerticalAlignment = VerticalAlignment.Top;
        DockPanel options = new() { Margin = new Thickness(ListRow.TextStart, Gaps.Tight, ListRow.TextStart, 0), Children = { history, choices } };
        SetDock(history, Dock.Right);
        unsigned = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(ListRow.TextStart, Gaps.Hair, ListRow.TextStart, 0) };
        unsigned.Classes.Add(StyleClasses.Error);
        movesFirst = new CheckBox { Content = MovesFirstTitle, IsChecked = true };
        movesSaid = MutedText.Wrapped(string.Empty);
        movesMessage = new TextBox { PlaceholderText = "Message of the moves' commit", [Avalonia.Automation.AutomationProperties.NameProperty] = "Message of the moves' commit" };
        movesFirst.IsCheckedChanged += (_, _) => movesMessage.IsEnabled = movesFirst.IsChecked == true;
        movesOffer = new StackPanel { Margin = new Thickness(ListRow.TextStart, Gaps.Small, ListRow.TextStart, 0), Spacing = Gaps.Tight, IsVisible = false, Children = { movesSaid, movesFirst, movesMessage } };
        message = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "Commit Message",
            [Avalonia.Automation.AutomationProperties.NameProperty] = "Commit message",
            MinHeight = 90,
            MaxHeight = 220,
            Margin = new Thickness(ListRow.TextStart, Gaps.Tight, ListRow.TextStart, Gaps.Normal),
        };
        message.TextChanged += (_, _) => UpdateButtons();
        // Ctrl+Enter (Cmd+Enter on the Mac) commits, seen as it tunnels: the box takes any Enter as a new line first.
        message.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == Keystroke.Command)
            {
                IdeErrors.Observe(CommitAsync(push: false));
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        commit = new Button { Content = "Commit", Classes = { StyleClasses.Primary } };
        commit.Click += (_, _) => IdeErrors.Observe(CommitAsync(push: false));
        commitAndPush = new Button { Content = "Commit and Push…" };
        commitAndPush.Click += (_, _) => IdeErrors.Observe(CommitAsync(push: true));
        // Narrower than both, Commit and Push… goes under Commit rather than being cut at the window's edge.
        WrapPanel buttons = new()
        {
            ItemSpacing = Gaps.Normal,
            LineSpacing = Gaps.Normal,
            Margin = new Thickness(ListRow.TextStart, 0, ListRow.TextStart, ListRow.TextStart),
            Children = { commit, commitAndPush },
        };
        // A window shorter than the form scrolls it, so Commit is still reached sharing a column with another window.
        form = new Border
        {
            [DockProperty] = Dock.Bottom,
            BorderThickness = Hairlines.Above,
            Child = new ScrollViewer { Content = new StackPanel { Children = { underWay, options, unsigned, movesOffer, message, buttons } } },
        };
        form.Bind(Border.BorderBrushProperty, form.GetResourceObservable(TokenKeys.Divider));

        tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gaps.Hair,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ToolButtons.Of(AppActions.CommitRefresh, IconKeys.Rerun, "Refresh", action => ActionRequested?.Invoke(this, action)),
                rollback,
                ToolButtons.Of(AppActions.ShowDiff, IconKeys.Diff, "Show Diff", _ =>
                {
                    if (list.SelectedItem is CommitRow row)
                    {
                        DiffRequested?.Invoke(this, row.Change.Path);
                    }
                }),
                groupBy,
            },
        };
        pages.Add(false, "Changes", name: "commitChangesTab");
        pages.Add(true, "Stashes", name: "commitStashesTab");
        pages.Choice = false;
        pages.Chosen += (_, shown) => ShowStashes(shown);
        bar = new ToolFlow { Margin = new Thickness(Gaps.Small, Gaps.Hair), Children = { pages, ToolFlow.Spring(), tools } };
        Border top = ToolBars.Top(bar);
        changes = new DockPanel { Children = { form, new Panel { Children = { list, placeholder } } } };

        repositories.Chosen += (_, chosen) =>
        {
            if (!listingRepositories && chosen != vcs)
            {
                Bind(chosen);
                RepositoryChosen?.Invoke(this, chosen);
            }
        };
        repositoriesRow = ToolBars.Top(new ToolFlow { Margin = new Thickness(Gaps.Small, Gaps.Hair), Children = { repositories } });
        repositoriesRow.IsVisible = false;

        Children.Add(repositoriesRow);
        Children.Add(top);
        Children.Add(pagesShown);
        pagesShown.Children.Add(changes);
        vcs.Changed += OnChanged;
        vcs.BusyChanged += OnBusyChanged;
        vcs.Refreshed += OnSigning;
        vcs.SigningChanged += OnSigning;
        vcs.KeptChanged += OnKept;
        Fill();
        ShowSigning();
    }

    /// <summary>The Stashes tab: every stash, its files and what Pop, Apply and Drop do; built as it is first shown or asked for.</summary>
    public StashesView Stashes => stashes ??= MakeStashes();

    private StashesView MakeStashes()
    {
        StashesView made = new(vcs) { IsVisible = false };
        made.Tools.IsVisible = false;
        made.DiffRequested += (_, asked) => StashDiffRequested?.Invoke(this, asked);
        made.Drop = stash => DropStash?.Invoke(stash) ?? Task.CompletedTask;
        made.ActionRequested += (_, action) => ActionRequested?.Invoke(this, action);
        bar.Children.Add(made.Tools);
        pagesShown.Children.Add(made);
        return made;
    }

    /// <summary>The repository whose changes are listed and committed.</summary>
    public VcsController Vcs => vcs;

    /// <summary>Another of the folder's repositories chosen to list and commit.</summary>
    public event EventHandler<VcsController>? RepositoryChosen;

    /// <summary>The tab of each repository the folder holds, by its name, branch and how many files changed, shown while
    /// there are several: the one listed chosen, the first where the one listed has no repository.</summary>
    public void ShowRepositories(IReadOnlyList<VcsController> shown)
    {
        repositoriesRow.IsVisible = shown.Count > 1;
        listingRepositories = true;
        try
        {
            // The same repositories keep their tabs, only their words said again, as each status read calls here.
            if (shown.Count > 1 && repositories.Keys.SequenceEqual(shown))
            {
                foreach (VcsController each in shown)
                {
                    repositories[each].Content = Titled(each);
                    repositories[each].ContextMenu = RepositoryMenu(each);
                }
            }
            else
            {
                repositories.Clear();
                foreach (VcsController each in shown.Count > 1 ? shown : [])
                {
                    repositories.Add(each, Titled(each)).ContextMenu = RepositoryMenu(each);
                }
            }
            if (shown.Count > 1)
            {
                repositories.Choice = shown.Contains(vcs) ? vcs : shown[0];
            }
        }
        finally
        {
            listingRepositories = false;
        }
        if (shown.Count > 0 && !shown.Contains(vcs) && vcs.Repository is null)
        {
            Bind(shown[0]);
            RepositoryChosen?.Invoke(this, shown[0]);
        }
    }

    /// <summary>Push… of a repository's tab, which the window asks of in its Push dialog.</summary>
    public event EventHandler<VcsController>? PushRequested;

    /// <summary>A repository's tab's menu: its own Update Project, Push… and Fetch, whichever tab is chosen.</summary>
    private ContextMenu RepositoryMenu(VcsController repository) => new()
    {
        ItemsSource = (Control[])
        [
            ListMenus.Item("Update Project…", () => IdeErrors.Observe(repository.UpdateAsync()), repository.WhyNotRemote),
            ListMenus.Item("Push…", () => PushRequested?.Invoke(this, repository), repository.WhyNotRemote),
            ListMenus.Item("Fetch", () => IdeErrors.Observe(repository.FetchAsync()), repository.WhyNotRemote),
        ],
    };

    /// <summary>The repositories' tabs, for a test to choose in.</summary>
    public IReadOnlyList<Avalonia.Controls.Primitives.ToggleButton> RepositoryTabs => repositoriesRow.IsVisible ? repositories.Toggles : [];

    /// <summary>A repository's tab's words: its name, its branch, and how many files changed.</summary>
    public static string Titled(VcsController repository) =>
        $"{(repository.Repository is { } tree ? VcsController.NameOf(tree) : "?")} · {repository.Branch ?? "detached"}{(repository.Changes.Count > 0 ? $" ({Numbers.Said(repository.Changes.Count)})" : string.Empty)}";

    /// <summary>The Stashes tab's own toggle in the toolbar.</summary>
    public Avalonia.Controls.Primitives.ToggleButton StashesTab => pages[true];

    /// <summary>Whether the Stashes tab is shown, else the changes and the commit's form.</summary>
    public bool ShowsStashes => pages.Choice;

    /// <summary>The Changes or the Stashes tab shown, with its toolbar's buttons.</summary>
    public void ShowStashes(bool shown)
    {
        pages.Choice = shown;
        changes.IsVisible = !shown;
        tools.IsVisible = !shown;
        if (shown || stashes is not null)
        {
            Stashes.IsVisible = shown;
            Stashes.Tools.IsVisible = shown;
        }
    }

    /// <summary>A file of a stash to show beside what it was before the stash.</summary>
    public event EventHandler<(StashInfo Stash, FileChange File)>? StashDiffRequested;

    /// <summary>Drops a stash once the person agrees, done when it is dropped or kept; the window's.</summary>
    public Func<StashInfo, Task>? DropStash { get; set; }

    /// <summary>Lists another folder's changes from now on, its session's working tree in place of the last's.</summary>
    public void Bind(VcsController next)
    {
        stashes?.Bind(next);
        vcs.Changed -= OnChanged;
        vcs.BusyChanged -= OnBusyChanged;
        vcs.Refreshed -= OnSigning;
        vcs.SigningChanged -= OnSigning;
        vcs.KeptChanged -= OnKept;
        vcs = next;
        vcs.Changed += OnChanged;
        vcs.BusyChanged += OnBusyChanged;
        vcs.Refreshed += OnSigning;
        vcs.SigningChanged += OnSigning;
        vcs.KeptChanged += OnKept;
        signTurned = false;
        unsigned.IsVisible = false;
        Fill();
        ShowSigning();
    }

    private void OnSigning(object? sender, EventArgs e) => ShowSigning();

    /// <summary>Group by Directory turned on or off lists the files again.</summary>
    private void OnKept(object? sender, EventArgs e)
    {
        if (grouped != vcs.GroupsByDirectory)
        {
            Fill();
        }
    }

    /// <summary>The Group By menu while it is open, for a test to choose in.</summary>
    public ContextMenu? GroupByMenu { get; private set; }

    /// <summary>Group By's menu: Directory, ticked while the files are listed under their folders.</summary>
    private void ShowGroupBy()
    {
        GroupByMenu?.Close();
        ContextMenu menu = new()
        {
            ItemsSource = (Control[])[ListMenus.Toggle("Directory", vcs.GroupsByDirectory, _ => ActionRequested?.Invoke(this, AppActions.GroupByDirectory), action: AppActions.GroupByDirectory)],
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };
        GroupByMenu = menu;
        menu.Closed += (_, _) => GroupByMenu = GroupByMenu == menu ? null : GroupByMenu;
        menu.Open(groupBy);
    }

    /// <summary>Reads git's config for how commits are signed and says it: Sign with the key, checked as the config has it
    /// unless the person turned it over for this commit; off with no key to sign with. Sign-off as the folder keeps it.</summary>
    private void ShowSigning() => IdeErrors.Observe(ShowSigningAsync());

    private async Task ShowSigningAsync()
    {
        int read = ++signingReads;
        signingOf = vcs.Repository;
        SigningSetup setup = await vcs.SigningAsync();
        if (read != signingReads)
        {
            return;
        }
        signing = setup;
        showingSigning = true;
        try
        {
            signOff.IsChecked = vcs.SignOff;
            sign.IsEnabled = setup.CanSign && vcs.Repository is not null;
            signWith.Text = setup.CanSign ? $"Sign with {setup.KeyShown}" : setup.IsSetUp ? "Sign: no key is set to sign with" : "Sign: not set up";
            if (!signTurned || !sign.IsEnabled)
            {
                sign.IsChecked = setup.CanSign && setup.Signs;
                signTurned = false;
            }
        }
        finally
        {
            showingSigning = false;
        }
    }

    /// <summary>How this commit is to be signed and signed off, as the window says it: Sign as git's config has it is left
    /// to the config.</summary>
    public CommitSigning Signing
    {
        get
        {
            bool signs = sign.IsChecked == true;
            return new CommitSigning(signs == (signing.CanSign && signing.Signs) ? null : signs, signOff.IsChecked == true);
        }
    }

    /// <summary>Whether this commit is to be signed.</summary>
    public bool Sign
    {
        get => sign.IsChecked == true;
        set => sign.IsChecked = value;
    }

    /// <summary>The Sign box's words: the key it signs with.</summary>
    public string SignWith => signWith.Text ?? string.Empty;

    /// <summary>Whether Sign can be turned on: git has a key to sign with.</summary>
    public bool CanSign => sign.IsEnabled;

    public bool SignOff
    {
        get => signOff.IsChecked == true;
        set => signOff.IsChecked = value;
    }

    /// <summary>Why the last commit was not made for its signing, as the window says it under the signing line; null
    /// while it says none.</summary>
    public string? Unsigned => unsigned.IsVisible ? unsigned.Text : null;

    private void OnChanged(object? sender, EventArgs e)
    {
        Fill();
        // The folder's working tree found, or another taken: how it signs is read for it.
        if (vcs.Repository != signingOf)
        {
            ShowSigning();
        }
    }

    /// <summary>Amend checked starts from the last commit's message, unless something is typed before git answers or Amend
    /// is unchecked by then, and unchecked gives back what was typed.</summary>
    private async Task AmendChangedAsync()
    {
        if (amend.IsChecked == true)
        {
            string? typed = message.Text;
            beforeAmend = typed;
            if (await vcs.LastMessageAsync() is { } last && (typed ?? string.Empty).Trim().Length == 0 && amend.IsChecked == true && message.Text == typed)
            {
                message.Text = last;
            }
        }
        else if (beforeAmend is not null)
        {
            message.Text = beforeAmend;
            beforeAmend = null;
        }
        UpdateButtons();
        LookForLostMoves();
    }

    private void OnBusyChanged(object? sender, EventArgs e) => UpdateButtons();

    /// <summary>The Merge Conflicts group's Resolve link: the Conflicts dialog.</summary>
    public event EventHandler? ResolveRequested;

    /// <summary>Asks the person to agree (a title, the question, the button that agrees).</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>A conflicted file to merge, double-clicked.</summary>
    public event EventHandler<string>? MergeRequested;

    /// <summary>Show Diff's keys, as the Commit window takes them (<see cref="AppActions.KeysIn"/>), ahead of what they run
    /// elsewhere while a file is chosen.</summary>
    public bool TakesKey(KeyEventArgs e) => list.IsKeyboardFocusWithin && list.SelectedItem is CommitRow && AppActions.Presses(AppActions.ShowDiff, e);

    /// <summary>What a double-click or Enter opens: a conflicted file's merge, any other file's diff; a group or folder
    /// opens or closes.</summary>
    private void OpenChosen()
    {
        if (list.SelectedItem is CommitRow row)
        {
            Open(row.Change);
        }
        else if (list.SelectedItem is CommitBranch branch)
        {
            Toggle(branch);
            list.Retell();
        }
    }

    /// <summary>Opens or closes a group or folder, kept so as the list is filled again.</summary>
    private void Toggle(ITreeRow row)
    {
        if (row is not CommitBranch branch)
        {
            return;
        }
        branch.IsExpanded = !branch.IsExpanded;
        if (branch.IsExpanded)
        {
            closed.Remove(branch.Key);
            rows.Open(branch, TreeRows<ITreeRow>.Shown(branch.Children, ChildrenOf));
        }
        else
        {
            closed.Add(branch.Key);
            rows.Close(branch);
        }
    }

    private static IEnumerable<ITreeRow>? ChildrenOf(ITreeRow row) => (row as CommitBranch)?.Children;

    /// <summary>Every file listed, open or not.</summary>
    private IEnumerable<CommitRow> Files => groups.SelectMany(g => g.Rows);

    /// <summary>The files selected and those under the groups and folders selected, each once.</summary>
    private IEnumerable<CommitRow> SelectedFiles() => (list.SelectedItems?.OfType<ITreeRow>() ?? [])
        .SelectMany(r => r is CommitBranch branch ? branch.Rows : r is CommitRow file ? [file] : [])
        .Distinct();

    private void Open(FileChange change)
    {
        if (change.Kind is ChangeKind.Conflicted)
        {
            MergeRequested?.Invoke(this, change.Path);
        }
        else
        {
            DiffRequested?.Invoke(this, change.Path);
        }
    }

    /// <summary>One changed file chosen in the list, which a diff following the choice shows (<see cref="Vcs.DiffTab.Follows"/>).</summary>
    public event EventHandler<string>? ChoiceMoved;

    /// <summary>Moves the choice to the next (1) or previous (-1) changed file as the list shows them, the diff following it;
    /// false where the choice is no file, or none comes after it that way.</summary>
    public bool StepChoice(int step)
    {
        CommitRow[] files = [.. rows.OfType<CommitRow>().Where(r => r.Change.Kind != ChangeKind.Conflicted)];
        int at = list.SelectedItem is CommitRow chosen ? Array.IndexOf(files, chosen) : -1;
        if (at < 0 || at + step < 0 || at + step >= files.Length)
        {
            return false;
        }
        list.SelectedItem = files[at + step];
        list.ScrollIntoView(files[at + step]);
        return true;
    }

    /// <summary>A file to open in the editor.</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>One of the window's actions asked for by its button.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>A changed file to move to the Trash, from its menu.</summary>
    public event EventHandler<string>? DeleteRequested;

    /// <summary>An unversioned file to leave out of Git for good, by its path in the repository's .gitignore.</summary>
    public event EventHandler<string>? IgnoreRequested;

    /// <summary>The rows chosen with the one the menu is for: every one when it is among them, else it alone.</summary>
    private IEnumerable<FileChange> ChosenChanges(CommitRow row)
    {
        List<CommitRow> chosen = [.. list.SelectedItems?.OfType<CommitRow>() ?? []];
        return chosen.Contains(row) ? chosen.Select(r => r.Change) : [row.Change];
    }

    /// <summary>A file's diff against the last commit, to show.</summary>
    public event EventHandler<string>? DiffRequested;

    /// <summary>Rollback pressed: the files to put back, once the person has said so.</summary>
    public event EventHandler<IReadOnlyList<FileChange>>? RollbackRequested;

    /// <summary>Rollback pressed where it puts nothing back: why.</summary>
    public event EventHandler<string>? RollbackRefused;

    /// <summary>The files and the groups and folders over them, as the window shows them.</summary>
    public IReadOnlyList<object> Rows => rows;

    /// <summary>The files that go into the next commit.</summary>
    public IReadOnlyList<FileChange> Chosen => [.. Files.Where(r => r.IsChosen).Select(r => r.Change)];

    /// <summary>Gives a diff of a changed file against the last commit a box beside each change, for whether it goes in,
    /// the file's row found as the list stands; none while git's index holds staged changes, which a commit takes instead,
    /// nor while a merge's commit, which takes files whole, is under way.</summary>
    public void ConfigureDiff(string path, DiffView view)
    {
        if (ChoosableRow(path) is null)
        {
            return;
        }
        view.ChooseForCommit(diff => ChoosableRow(path)?.ChosenIn(diff), (diff, index, take) => ChoosableRow(path)?.ChooseHunk(diff, index, take));
        CommitRow? followed = null;
        void Follow(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => view.RefreshCommitChoices();
        void Refollow(object? sender, EventArgs e)
        {
            if (followed is not null)
            {
                followed.PropertyChanged -= Follow;
            }
            followed = sender is null ? null : ChoosableRow(path);
            if (followed is not null)
            {
                followed.PropertyChanged += Follow;
            }
            view.RefreshCommitChoices();
        }
        view.AttachedToVisualTree += (_, _) =>
        {
            Listed += Refollow;
            Refollow(this, EventArgs.Empty);
        };
        view.DetachedFromVisualTree += (_, _) =>
        {
            Listed -= Refollow;
            Refollow(null, EventArgs.Empty);
        };
        if (view.IsAttachedToVisualTree())
        {
            Listed += Refollow;
            Refollow(this, EventArgs.Empty);
        }
    }

    /// <summary>The row of a changed file whose changes are chosen one by one, while what is ticked goes in: a text file
    /// modified or renamed, its mode as it was, no submodule.</summary>
    private CommitRow? ChoosableRow(string path) =>
        staging || vcs.Merge.CommitsWhole ? null
        : Files.FirstOrDefault(r => PathNames.Same(r.Change.Path, path)) is { CanChoose: true, Change: { Kind: ChangeKind.Modified or ChangeKind.Renamed, Mode: null, Submodule: SubmoduleChange.None } } row
            ? row : null;

    public string Message
    {
        get => message.Text ?? string.Empty;
        set => message.Text = value;
    }

    public bool Amend
    {
        get => amend.IsChecked == true;
        set => amend.IsChecked = value;
    }

    /// <summary>Puts the keys in the message (the Commit shortcut), once laid out: the form's scroller places the message in
    /// the tree only at its first layout, and a window just shown has had none.</summary>
    public void FocusMessage() => Avalonia.Threading.Dispatcher.UIThread.Post(() => message.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>A message from the history put in the box in place of what it held, the keys in it at its end.</summary>
    public void TakeMessage(string taken)
    {
        message.Text = taken;
        message.CaretIndex = taken.Length;
        FocusMessage();
    }

    /// <summary>Whether what has the keys is the message box, where the history's keys are its.</summary>
    public bool HasMessageKeys(object? focused) => focused is Visual inside && (inside == message || message.IsVisualAncestorOf(inside));

    /// <summary>A commit was made, with the message it took and the Git it was made in.</summary>
    public event EventHandler<(VcsController Git, string Message)>? Committed;

    /// <summary>Commits the chosen files with the message (pushing after when asked), then empties the message. Whether the
    /// commit was made, whatever came of the push.</summary>
    public async Task<bool> CommitAsync(bool push)
    {
        // A merge's commit takes in every change but those left out, which stay changes, once the person agrees.
        bool excluding = vcs.Merge.CommitsWhole && !Amend && vcs.Conflicts.Count == 0
            && Files.Any(r => r.CanChoose && r.CheckState != true && r.Change.Kind is not ChangeKind.Unversioned);
        if (excluding && Confirm is { } ask
            && !await ask("Changes Excluded from Merge Commit", "Are you sure you want to exclude these changed files from merge commit?", "Commit"))
        {
            return false;
        }
        Dictionary<string, PartialCommit> partial = Partial();
        string? first = movesOffer.IsVisible && movesFirst.IsChecked == true && !Amend
            ? (movesMessage.Text is { } typed && typed.Trim().Length > 0 ? typed : movesWritten) : null;
        CommitResult made = staging ? await vcs.CommitAsync(Chosen, Message, Amend, push, signing: Signing, staged: true)
            : await vcs.CommitAsync(Chosen, Message, Amend, push, signing: Signing, partial: partial, movesFirst: first);
        bool notSigned = !made.Commit.Succeeded && made.Commit.Message.StartsWith(SigningFailure.Unsigned, StringComparison.Ordinal);
        unsigned.Text = notSigned ? made.Commit.Message : null;
        unsigned.IsVisible = notSigned;
        if (!made.Commit.Succeeded)
        {
            return false;
        }
        foreach (CommitRow row in Files.Where(r => partial.TryGetValue(r.Change.Path, out PartialCommit? submitted) && ReferenceEquals(r.Partial, submitted)))
        {
            row.IsChosen = false;
        }
        // A merge's message as git wrote it is git's, not one the person wrote.
        if (Message != prefilled)
        {
            Committed?.Invoke(this, (vcs, Message));
        }
        beforeAmend = null;
        amend.IsChecked = false;
        message.Text = string.Empty;
        signTurned = false;
        if (!signing.IsSetUp)
        {
            CommittedUnsigned?.Invoke(this, EventArgs.Empty);
        }
        ShowSigning();
        return true;
    }

    /// <summary>A commit was made that git's config signs nothing of.</summary>
    public event EventHandler? CommittedUnsigned;

    private Dictionary<string, PartialCommit> Partial() => Files.Where(r => r.IsChosen && r.Partial is not null)
        .ToDictionary(r => r.Change.Path, r => r.Partial!, PathNames.Comparer);

    /// <summary>The words of the box that commits the moves first.</summary>
    public const string MovesFirstTitle = "Commit the moves first, then the edits";

    /// <summary>Whether the window offers to commit the chosen files' moves first, as git would lose one of them.</summary>
    public bool OffersMovesFirst => movesOffer.IsVisible;

    /// <summary>Why it offers to commit the moves first, as it says it; null while it offers nothing.</summary>
    public string? MovesSaid => movesOffer.IsVisible ? movesSaid.Text : null;

    /// <summary>Whether the moves go first, in a commit of their own, where the window offers it.</summary>
    public bool MovesFirst
    {
        get => movesFirst.IsChecked == true;
        set => movesFirst.IsChecked = value;
    }

    /// <summary>The message of the moves' own commit, as written in its box.</summary>
    public string MovesMessage
    {
        get => movesMessage.Text ?? string.Empty;
        set => movesMessage.Text = value;
    }

    /// <summary>Looks again, where what it was of changed, for chosen moves git would lose committed with their edits, off
    /// the window's thread, and offers to commit the moves first where it finds one; the latest look's answer wins.</summary>
    private void LookForLostMoves()
    {
        IReadOnlyList<FileChange> chosen = Amend || vcs.Merge.CommitsWhole || staging ? [] : Chosen;
        string moved = string.Join('\0', chosen.Where(c => c.From is not null).Select(c => $"{c.From}\0{c.Path}"));
        if (lostOf is { } last && ReferenceEquals(last.Changes, vcs.Changes) && last.Chosen == moved)
        {
            return;
        }
        lostOf = (vcs.Changes, moved);
        IdeErrors.Observe(LookForLostMovesAsync(chosen, ++lostLooks));
    }

    private async Task LookForLostMovesAsync(IReadOnlyList<FileChange> chosen, int look)
    {
        IReadOnlyList<FileChange> lost = chosen.Any(c => c.From is not null) ? await vcs.LostMovesAsync(chosen, Partial()) : [];
        if (look != lostLooks)
        {
            return;
        }
        movesOffer.IsVisible = lost.Count > 0;
        if (lost.Count == 0)
        {
            return;
        }
        movesSaid.Text = lost is [{ } one]
            ? $"Committed with its edits, {one.Name} would no longer be seen by Git as {Moved(one)}: too much of it changed. Committing the moves first, on their own, keeps its history."
            : $"Committed with their edits, {Plural.Of(lost.Count, "moved file")} would no longer be seen by Git as moved: too much of them changed. Committing the moves first, on their own, keeps their history.";
        List<FileChange> moves = [.. chosen.Where(c => c.From is not null && !Partial().ContainsKey(c.Path))];
        string written = moves is [{ } only] ? $"{(InPlace(only) ? "Rename" : "Move")} {Path.GetFileName(only.From)} to {(InPlace(only) ? only.Name : Shown(only.Path))}"
            : $"{(moves.All(InPlace) ? "Rename" : "Move")} {Plural.Of(moves.Count, "file")}";
        // The window's own words follow the moves; what the person typed stays.
        if (MovesMessage.Length == 0 || MovesMessage == movesWritten)
        {
            MovesMessage = written;
        }
        movesWritten = written;

        string Moved(FileChange change) => InPlace(change) ? $"renamed from {Path.GetFileName(change.From)}" : $"moved from {Shown(change.From!)}";
        static bool InPlace(FileChange change) => Path.GetDirectoryName(change.From) == Path.GetDirectoryName(change.Path);
        string Shown(string path) => vcs.Repository is { } repository ? repository.Relative(path) : Path.GetFileName(path);
    }

    /// <summary>Rollback of the files selected (those under a group or folder selected too), else of those chosen: refused where a chosen file goes in only in part, as
    /// a rollback puts back whole files, or where git cannot write one out here (<see cref="RollbackOf"/>).</summary>
    private void RollbackChosenOrSelected()
    {
        (IReadOnlyList<FileChange> changes, string? refused) = RollbackOf();
        if (refused is not null)
        {
            RollbackRefused?.Invoke(this, refused);
            return;
        }
        RollbackRequested?.Invoke(this, changes);
    }

    /// <summary>What Rollback puts back, the files selected else those chosen, or why it puts back none of them.</summary>
    private (IReadOnlyList<FileChange> Changes, string? Refused) RollbackOf()
    {
        if (!SelectedFiles().Any() && Files.FirstOrDefault(r => r.Partial is not null) is { } part)
        {
            return ([], $"Rollback puts back whole files, and only some changes to {part.Change.Name} are chosen: select the files to roll back");
        }
        IReadOnlyList<FileChange> changes = RollbackTargets();
        return (changes, vcs.WhyNotRollback(changes));
    }

    /// <summary>The files selected, else those ticked; while git's index holds staged changes, whose boxes say what is
    /// staged, every file listed under Staged and Unstaged.</summary>
    private IReadOnlyList<FileChange> RollbackTargets() =>
        SelectedFiles().ToArray() is { Length: > 0 } selected ? [.. selected.Select(r => r.Change).Distinct()]
        : staging ? [.. groups.Where(g => g.Name is StagedGroup or UnstagedGroup).SelectMany(g => g.Rows).Select(r => r.Change).Distinct()]
        : Chosen;

    /// <summary>Rollback greyed where it would put back a file git cannot write out here, its tip saying why.</summary>
    private void UpdateRollback() => ToolButtons.Grey(rollback, vcs.WhyNotRollback(RollbackTargets()));

    /// <summary>Why a file's own item (Go to Source, Delete…) cannot act on a change that deleted it; null where it can.</summary>
    private static string? Deleted(FileChange change) => change.Kind is ChangeKind.Deleted ? Refusals.FileDeleted : null;

    /// <summary>Lists the status again in place: a file still changed as it was keeps its row, its box and whether it is
    /// selected, so a refresh with nothing new (the window coming to the front, a save) leaves the list as it was, and
    /// what Rollback acts on with it. A file changed another way (modified, now deleted) is selected again by path. Under
    /// Group by Directory each group's files are listed under their folders; a group or folder closed stays closed.</summary>
    private void Fill()
    {
        HashSet<string> selected = [.. list.SelectedItems?.OfType<CommitRow>().Select(r => r.Change.Path) ?? []];
        Dictionary<(FileChange, bool?), CommitRow> kept = [];
        Dictionary<string, CommitBranch> was = branches.ToDictionary(b => b.Key);
        foreach (CommitRow row in Files)
        {
            // A row with no box (a conflicted file): once it has one, it is chosen as any change is.
            if (row.CanChoose)
            {
                chosen[row.Change.Path] = row.Partial is null && row.IsChosen;
            }
            kept.TryAdd((row.Change, row.Staged), row);
        }
        IReadOnlyList<FileChange> changes = vcs.Changes;
        List<CommitGroup> filled = [];
        List<CommitBranch> under = [];
        staging = vcs.HoldsStaged;
        // Set before the rows' depths move, as a row shown hears its depth change and shows its folder by it.
        grouped = vcs.GroupsByDirectory;
        Group(MergeConflicts, [.. changes.Where(c => c.Kind is ChangeKind.Conflicted).OrderBy(c => c.Path, StringComparer.Ordinal).Select(c => Row(c, false))]);
        IEnumerable<FileChange> tracked = changes.Where(c => c.Kind is not (ChangeKind.Unversioned or ChangeKind.Conflicted)).OrderBy(c => c.Path, StringComparer.Ordinal);
        if (staging)
        {
            // A file staged in part is under both, its staged change under Staged and the rest under Unstaged.
            Group(StagedGroup, [.. tracked.Where(c => c.Staged != StagedState.None).Select(c => Row(c, true, staged: true))]);
            Group(UnstagedGroup, [.. tracked.Where(c => c.Staged != StagedState.Whole).Select(c => Row(c, false, staged: false))]);
        }
        else
        {
            Group("Changes", [.. tracked.Select(c => Row(c, true))]);
        }
        Group("Unversioned Files", [.. changes.Where(c => c.Kind is ChangeKind.Unversioned).OrderBy(c => c.Path, StringComparer.Ordinal).Select(c => Row(c, false, staged: staging ? false : null))]);

        groups = filled;
        branches = under;
        foreach (CommitBranch branch in branches)
        {
            branch.IsExpanded = !closed.Contains(branch.Key);
        }
        rows.Show([.. TreeRows<ITreeRow>.Shown(groups, ChildrenOf)]);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is CommitRow row && selected.Contains(row.Change.Path) && !list.Selection.IsSelected(i))
            {
                list.Selection.Select(i);
            }
        }
        placeholder.Text = vcs.Repository is null ? vcs.NoRepository : "No changes";
        bool creates = vcs.Repository is null && vcs.WhyNotCreate is null;
        placeholder.Offer(creates ? AppActions.CreateRepository : null, VcsController.CreateRepository);
        placeholder.IsVisible = rows.Count == 0;
        // Nothing of the form can be used before there is a repository, and the window is its empty state's.
        form.IsVisible = vcs.Repository is not null;
        underWay.IsVisible = vcs.Merge.UnderWay is not null;
        underWayText.Text = vcs.Merge.UnderWay;
        continueRebase.IsVisible = vcs.Merge.Operation is MergeOperation.Rebase;
        // Only where it stopped for conflicts: at an edit its commit is made already.
        skipCommit.IsVisible = vcs.WhyNotSkip is null;
        abort.Content = AbortOf(vcs.Merge.Operation)?.Title;
        // A merge under way gives the commit that finishes it the message git wrote for it, until it ends.
        string? merging = vcs.Merge.CommitsWhole ? vcs.Merge.Message : null;
        if (merging is not null && Message.Trim().Length == 0 && merging != prefilled)
        {
            Message = merging;
            prefilled = merging;
        }
        else if (merging is null && prefilled is not null)
        {
            if (Message == prefilled)
            {
                Message = string.Empty;
            }
            prefilled = null;
        }
        Recount();
        Listed?.Invoke(this, EventArgs.Empty);

        CommitRow Row(FileChange change, bool byDefault, bool? staged = null)
        {
            if (kept.Remove((change, staged), out CommitRow? same))
            {
                return same;
            }
            // A conflicted file or a submodule with nothing to commit here never goes in; one listed under Staged does.
            CommitRow row = new(change, staged ?? (chosen.TryGetValue(change.Path, out bool before) ? before : byDefault), staged);
            row.PropertyChanged += (_, e) =>
            {
                if (!choosing && e.PropertyName != nameof(CommitRow.Depth))
                {
                    Recount();
                }
            };
            return row;
        }

        void Group(string name, CommitRow[] members)
        {
            if (members.Length == 0)
            {
                return;
            }
            int at = under.Count;
            IReadOnlyList<ITreeRow> children = vcs.GroupsByDirectory ? Folders(name, FolderTree.Of(members, vcs.Repository), 1) : Flat(members);
            CommitGroup group = Kept(new CommitGroup(name, members, children));
            filled.Add(group);
            under.Insert(at, group);
        }

        static IReadOnlyList<ITreeRow> Flat(CommitRow[] members)
        {
            foreach (CommitRow member in members)
            {
                member.Depth = 1;
            }
            return members;
        }

        // A folder holding one folder and no file is one row with it; folders come before files, each by name.
        IReadOnlyList<ITreeRow> Folders(string key, FolderTree tree, int depth)
        {
            List<ITreeRow> children = [];
            foreach ((string name, FolderTree folder) in tree.Folders)
            {
                string shown = name;
                FolderTree inner = folder;
                while (inner.Files.Count == 0 && inner.Folders.Count == 1)
                {
                    (string only, inner) = inner.Folders.First();
                    shown += "/" + only;
                }
                string path = key + "/" + shown;
                int at = under.Count;
                IReadOnlyList<ITreeRow> below = Folders(path, inner, depth + 1);
                CommitFolder made = Kept(new CommitFolder(path, shown, depth, [.. inner.All()], below));
                under.Insert(at, made);
                children.Add(made);
            }
            foreach (CommitRow file in tree.Files)
            {
                file.Depth = depth;
                children.Add(file);
            }
            return children;
        }

        // The row shown before for the same group or folder with the same rows under it, so it stays selected.
        T Kept<T>(T made)
            where T : CommitBranch =>
            was.TryGetValue(made.Key, out CommitBranch? before) && before is T same && same.Rows.SequenceEqual(made.Rows) && same.Children.SequenceEqual(made.Children)
                ? same : made;
    }

    /// <summary>A group's files by their folders from the repository's top, each folder's names in ordinal order.</summary>
    private sealed class FolderTree
    {
        public SortedDictionary<string, FolderTree> Folders { get; } = new(StringComparer.Ordinal);

        public List<CommitRow> Files { get; } = [];

        public IEnumerable<CommitRow> All() => Folders.Values.SelectMany(f => f.All()).Concat(Files);

        public static FolderTree Of(IEnumerable<CommitRow> files, Git? repository)
        {
            FolderTree top = new();
            foreach (CommitRow file in files)
            {
                FolderTree at = top;
                string folder = ChangedFileRow.Folder(repository?.Relative(file.Change.Path) ?? file.Change.Path);
                foreach (string name in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    at = at.Folders.TryGetValue(name, out FolderTree? next) ? next : at.Folders[name] = new FolderTree();
                }
                at.Files.Add(file);
            }
            return top;
        }
    }

    /// <summary>The group of the files git's index holds changes of, while it holds staged changes.</summary>
    public const string StagedGroup = "Staged";

    /// <summary>The group of the files with changes git's index does not hold, while it holds staged changes.</summary>
    public const string UnstagedGroup = "Unstaged";

    /// <summary>Files to stage (true) or unstage, from their boxes, Space or their menu, each once.</summary>
    public event EventHandler<(IReadOnlyList<FileChange> Changes, bool Stage)>? StageRequested;

    /// <summary>The list filled again, after a read of the status.</summary>
    public event EventHandler? Listed;

    /// <summary>Stages the rows' files not staged yet (<paramref name="stage"/>), or unstages those staged.</summary>
    private void Restage(IReadOnlyList<CommitRow> chosen, bool stage)
    {
        FileChange[] changes = [.. chosen.Where(r => r.Staged != stage || r.Staged is null).Select(r => r.Change).Distinct()];
        if (changes.Length > 0)
        {
            StageRequested?.Invoke(this, (changes, stage));
        }
    }

    /// <summary>Why files can't be staged (or unstaged): git's index holds them whole already (or holds nothing of them), or
    /// they can't be at all (<see cref="VcsController.WhyNotStage"/>).</summary>
    public string? WhyNotStage(IReadOnlyList<FileChange> changes, bool stage) => vcs.WhyNotStage(changes)
        ?? (stage && changes.All(c => c.Staged == StagedState.Whole && !c.MovedOnlyInIndex) ? $"{Core.Plural.Agreeing(changes.Count, "It is", "They are")} staged whole already"
        : !stage && changes.All(c => c.Staged == StagedState.None) ? $"Nothing of {Core.Plural.Agreeing(changes.Count, "it", "them")} is staged"
        : null);

    /// <summary>The files selected and those under the groups and folders selected, for Stage and Unstage's keys.</summary>
    public IReadOnlyList<FileChange> SelectedChanges => [.. SelectedFiles().Select(r => r.Change).Distinct()];

    /// <summary>Whether the files selected are staged whole, as their row under Staged shows them, so Stage's keys unstage them.</summary>
    public bool SelectedStaged => SelectedFiles().ToArray() is { Length: > 0 } selected
        && (staging ? selected.All(r => r.Staged == true) : selected.All(r => r.Change.Staged == StagedState.Whole && r.Change.HoldsStaged));

    /// <summary>Whether rows' boxes are being set together, so no row's change counts the groups until all have.</summary>
    private bool choosing;

    /// <summary>How many rows the groups' counts have looked at, a measure of what a box costs: a group ticked whole is
    /// counted once, however many rows it has.</summary>
    public long RowsCounted { get; private set; }

    /// <summary>Sets rows' boxes as one change: the groups are counted once, after them all.</summary>
    private void Choose(IEnumerable<CommitRow> chosen, bool to)
    {
        choosing = true;
        try
        {
            foreach (CommitRow row in chosen)
            {
                row.IsChosen = to;
            }
        }
        finally
        {
            choosing = false;
        }
        Recount();
    }

    /// <summary>Each group's and folder's box and the buttons follow the rows' boxes, the deepest folders first.</summary>
    private void Recount()
    {
        for (int i = branches.Count - 1; i >= 0; i--)
        {
            RowsCounted += branches[i].Children.Count;
            branches[i].Update();
        }
        UpdateButtons();
        LookForLostMoves();
    }

    /// <summary>Commit and Commit and Push… greyed while no commit can be made (and the push while none can go), their tips
    /// saying why as a press would.</summary>
    private void UpdateButtons()
    {
        string? why = vcs.Repository is null ? vcs.NoRepository : vcs.WhyNotCommit(groups.Any(g => g.IsChosen != false), Message, Amend);
        foreach (Button button in (Button[])[commit, commitAndPush])
        {
            string? refused = button == commitAndPush ? why ?? vcs.WhyNotRemote : why;
            ToolButtons.Grey(button, refused);
            // Off while a commit (or a stash, a rollback) runs, so a second press cannot start another on git's index lock.
            button.IsEnabled = refused is null && !vcs.IsBusy;
        }
        UpdateRollback();
    }

    private const string MergeConflicts = "Merge Conflicts";

    /// <summary>What git is in the middle of as the Commit window says it; null with nothing under way.</summary>
    public string? UnderWay => underWay.IsVisible ? underWayText.Text : null;

    /// <summary>The Abort action of what is under way, by its id and title; null with nothing to abort.</summary>
    private static (string Id, string Title)? AbortOf(MergeOperation operation) => operation switch
    {
        MergeOperation.Rebase => (AppActions.AbortRebase, "Abort Rebase"),
        MergeOperation.Merge => (AppActions.AbortMerge, "Abort Merge"),
        MergeOperation.CherryPick => (AppActions.AbortCherryPick, "Abort Cherry-Pick"),
        MergeOperation.Revert => (AppActions.AbortRevert, "Abort Revert"),
        _ => null,
    };

    /// <summary>A row for whichever group, folder or file the list hands its container, set in by its depth: a group's or
    /// folder's chevron, box, name and count (a folder with its icon; Merge Conflicts with its Resolve link and no box), or
    /// a file's box and line (a conflicted file's with no box: it is resolved first). A box follows its item while shown,
    /// and a click on it chooses the item it shows, a group's or folder's every file under it.</summary>
    private Control Row()
    {
        RowExpander toggle = RowExpander.Of<ITreeRow>(Toggle);
        CheckBox box = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Gaps.Hair, 0) };
        RowIcon folderIcon = new() { Margin = new Thickness(0, 0, Gaps.Small, 0) };
        TextBlock branchName = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock count = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Gaps.Normal, 0, 0, 0),
            Classes = { StyleClasses.Caption },
        };
        count.Classes.Add(StyleClasses.Muted);
        Button resolve = new() { Classes = { StyleClasses.Link }, Content = "Resolve", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0) };
        resolve.Click += (_, _) => ResolveRequested?.Invoke(this, EventArgs.Empty);
        StackPanel branchLine = new() { Orientation = Orientation.Horizontal, Children = { folderIcon, branchName, count, resolve } };
        ChangedFileRow shown = new() { Line = { Margin = default } };
        Panel content = new() { Children = { branchLine, shown.Line } };
        DockPanel.SetDock(toggle, Dock.Left);
        DockPanel.SetDock(box, Dock.Left);
        DockPanel row = new() { Height = ListRow.Height, Children = { toggle, box, content } };
        bool setting = false;
        string? nameColour = null;
        box.IsCheckedChanged += (_, _) =>
        {
            if (setting)
            {
                return;
            }
            if (staging && row.DataContext is ITreeRow shownRow)
            {
                // The index answers: the box shows what it holds until the status read after the stage says otherwise.
                bool stage = box.IsChecked != false;
                setting = true;
                box.IsChecked = shownRow is CommitRow one ? one.CheckState : (shownRow as CommitBranch)?.IsChosen;
                setting = false;
                Restage(shownRow is CommitBranch rows ? [.. rows.Rows.Where(r => r.CanChoose)] : [(CommitRow)shownRow], stage);
            }
            else if (row.DataContext is CommitBranch branch)
            {
                Choose(branch.Rows.Where(r => r.CanChoose), box.IsChecked != false);
            }
            else if (row.DataContext is CommitRow file)
            {
                file.IsChosen = box.IsChecked == true;
            }
        };
        return row.Showing<ITreeRow>(item =>
        {
            setting = true;
            row.Margin = new Thickness(Gaps.Tight + (item.Depth * Gaps.Indent), 0, 0, 0);
            toggle.Expands = item.Expands;
            toggle.IsOpen = item.IsExpanded;
            branchLine.IsVisible = item is CommitBranch;
            shown.Line.IsVisible = item is CommitRow;
            bool boxed = false;
            if (item is CommitBranch branch)
            {
                bool conflicts = branch is CommitGroup { Name: MergeConflicts };
                // A group none of whose files can be chosen (a submodule's own changes alone) has no box either.
                boxed = !conflicts && branch.CanChoose;
                box.IsChecked = branch.IsChosen;
                CommitFolder? folder = branch as CommitFolder;
                folderIcon.IsVisible = folder is not null;
                if (folder is not null)
                {
                    string last = folder.Name[(folder.Name.LastIndexOf('/') + 1)..];
                    folderIcon.DataKey = Explorer.FileIcons.FolderKey(last, branch.IsExpanded);
                    folderIcon.ForegroundKey = Explorer.FileIcons.FolderBrushKey(last);
                }
                branchName.Text = folder?.Name ?? ((CommitGroup)branch).Name;
                branchName.Classes.Set(StyleClasses.Strong, folder is null);
                branchName.Resource(TextBlock.ForegroundProperty, ref nameColour, conflicts ? TokenKeys.Vcs_File_Conflicted : null);
                count.Text = Core.Plural.Of(branch.Rows.Count, "file");
                resolve.IsVisible = conflicts;
            }
            else if (item is CommitRow file)
            {
                boxed = file.CanChoose;
                box.IsChecked = file.CheckState;
                ShowFile(shown, file.Change, vcs.Repository, underFolders: grouped);
            }
            box.IsVisible = boxed;
            content.Margin = new Thickness(boxed ? 0 : BoxRoom, 0, 0, 0);
            setting = false;
        });
    }

    /// <summary>The files as this window lists them, without their boxes, for Rollback's question to name.</summary>
    internal static Control FileList(IReadOnlyList<FileChange> changes, Git? repository)
    {
        StackPanel lines = new() { Margin = new Thickness(Gaps.Normal, Gaps.Hair) };
        foreach (FileChange change in changes.OrderBy(c => c.Kind is ChangeKind.Unversioned).ThenBy(c => c.Path, StringComparer.Ordinal))
        {
            lines.Children.Add(FileLine(change, repository));
        }
        return new Border
        {
            Classes = { StyleClasses.Framed },
            MaxHeight = (24 * 8) + 6,
            Child = new ScrollViewer { Content = lines, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
    }

    /// <summary>A changed file as the window shows it, with no box (<see cref="ChangedFileRow"/>).</summary>
    private static Control FileLine(FileChange change, Git? repository)
    {
        ChangedFileRow line = new() { Line = { Margin = default } };
        ShowFile(line, change, repository);
        return line.Line;
    }

    /// <summary>Shows a changed file in a line, its folder from the repository's top, none where it is listed under its
    /// folders.</summary>
    private static void ShowFile(ChangedFileRow line, FileChange change, Git? repository, bool underFolders = false)
    {
        // A change with nothing to commit here says why in its folder's place.
        line.Show(change, repository, change.Uncommittable ?? (underFolders ? string.Empty : null));
        ToolTip.SetTip(line.Line, change.From is { } from ? $"{change.Kind}: {from} -> {change.Path}"
            : change.Uncommittable is { } why ? $"{change.Kind}: {change.Path}\n{why}"
            : $"{change.Kind}: {change.Path}");
    }
}
