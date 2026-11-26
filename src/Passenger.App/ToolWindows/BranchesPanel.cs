using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.State;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Vcs;

namespace Passenger.App.ToolWindows;

/// <summary>What a row of the branches panel is: HEAD (Current Branch), a top group (Local, Remote, Tags, Worktrees), a
/// remote, a folder of branches named alike ("feature/" of "feature/login"), a branch or tag, or a working tree.</summary>
public enum BranchNodeKind
{
    Head,
    Group,
    Remote,
    Folder,
    Ref,
    Worktree,
}

/// <summary>A row of the branches panel: what it is, its name, how deep it sits, and the branch or tag or working tree it
/// is.</summary>
public sealed class BranchNode(BranchNodeKind kind, string title, string key, int depth, BranchInfo? branch = null, WorktreeInfo? worktree = null) : Observable, ITreeRow
{
    private bool isExpanded;

    public BranchNodeKind Kind { get; } = kind;

    public string Title { get; } = title;

    /// <summary>What it is known by from one reading to the next, so it stays open or closed and chosen.</summary>
    public string Key { get; } = key;

    public int Depth { get; } = depth;

    public BranchInfo? Branch { get; } = branch;

    public WorktreeInfo? Worktree { get; } = worktree;

    public List<BranchNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    public bool IsFavorite { get; set; }

    public bool HasFavorites => Children.Any(c => c.IsFavorite || c.HasFavorites);

    public bool IsGroup => Kind is BranchNodeKind.Group or BranchNodeKind.Remote or BranchNodeKind.Folder;

    bool ITreeRow.Expands => IsGroup;

    public override string ToString() => Title;
}

/// <summary>The branches marked as favourites in a folder: those marked, and the predefined ones (main and master, and
/// origin's) unmarked. Full ref names.</summary>
public sealed record GitBranchesState : IStateDocument<GitBranchesState>
{
    /// <summary>The predefined favourites.</summary>
    public static readonly IReadOnlyList<string> Predefined = ["refs/heads/master", "refs/heads/main", "refs/remotes/origin/master", "refs/remotes/origin/main"];

    public List<string> Favorites { get; init; } = [];

    public List<string> NotFavorites { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public GitBranchesState Clamped() => this with
    {
        Favorites = [.. (Favorites ?? []).Where(f => f is not null).Distinct()],
        NotFavorites = [.. (NotFavorites ?? []).Where(f => f is not null).Distinct()],
    };

    /// <summary>Whether a branch is a favourite here: marked, or predefined and not unmarked; a tag never is.</summary>
    public bool Holds(BranchInfo branch) => branch.Kind != RefKind.Tag && (Favorites.Contains(branch.Ref.FullName)
        || (Predefined.Contains(branch.Ref.FullName) && !NotFavorites.Contains(branch.Ref.FullName)));
}

/// <summary>The branches panel at the Git log's left: HEAD (Current Branch), then Local, Remote by remote and Tags, each
/// grouped by directory, the current branch first, then favourites (main and master starred to begin with), then
/// folders, then the rest by name; a search narrows them. A double-click or Enter on one filters the log to it (Update
/// Branch Filter in Log); its menu checks out, branches, tags, compares, rebases, merges, updates, pushes, renames and
/// deletes, and a tag's moves it, pushes it and deletes it here or on a remote.</summary>
public sealed class BranchesPanel : DockPanel
{
    private VcsController vcs;
    private readonly TreeList list;
    private readonly TextBox search;
    private readonly Dictionary<string, bool> expanded = [];
    private readonly Button newBranch;
    private readonly Button update;
    private readonly Button delete;
    private readonly Button favorite;
    private readonly Button navigate;
    private List<BranchNode> roots = [];
    private readonly TreeRows<BranchNode> rows = [];
    private int loads;

    /// <summary>Whether the rows are being listed again, the choice let go and taken again in the same moment.</summary>
    private bool listing;

    public BranchesPanel(VcsController git)
    {
        vcs = git;
        list = new TreeList
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = rows,
        };
        ReusedRows.Use(list, Row);
        TreeKeys.Attach<BranchNode>(list, Toggle, node =>
        {
            if (node.IsGroup)
            {
                return false;
            }
            Activate(node);
            return true;
        }, new ListActs { Keys = Take });
        // A listing again passes through no choice, which would grey every button for that moment and let go of one pressed.
        list.SelectionChanged += (_, _) =>
        {
            if (!listing)
            {
                UpdateTools();
            }
        };
        ListMenus.Attach(list, () => Selected switch
        {
            { Branch: { } branch } => Menu(branch),
            { Worktree: { } worktree } => WorktreeMenu(worktree),
            { Kind: BranchNodeKind.Remote } remote => RemoteMenu(remote.Title),
            { Kind: BranchNodeKind.Group, Key: "remote" } =>
            [
                ListMenus.Item("Add Remote…", () => AddRemoteRequested?.Invoke(this, EventArgs.Empty)),
                ListMenus.Item("Manage Remotes…", () => ManageRemotesRequested?.Invoke(this, EventArgs.Empty)),
            ],
            _ => [],
        });
        search = SearchBox.Of("Filter branches and tags");
        search.Margin = new Thickness(Gaps.Small, 0, Gaps.Tight, 0);
        search.TextChanged += (_, _) => Show();
        newBranch = ToolButtons.Of(IconKeys.Add, "New Branch…", () => Request(NewBranchRequested, WhyNotFrom));
        update = ToolButtons.Of(IconKeys.Update, "Update Selected", () =>
        {
            if (Selected?.Branch is { } branch && WhyNotUpdate(branch) is null)
            {
                IdeErrors.Observe(vcs.UpdateBranchAsync(branch));
            }
        });
        delete = ToolButtons.Of(IconKeys.Clear, "Delete", () => Request(DeleteRequested, WhyNotDelete));
        favorite = ToolButtons.Of(IconKeys.Star, "Mark/Unmark As Favorite", () =>
        {
            if (Selected?.Branch is { } branch && WhyNotFavorite(branch) is null)
            {
                ToggleFavorite(branch);
            }
        });
        navigate = ToolButtons.Of(IconKeys.Locate, "Navigate Log to Selected Branch Head", () => Request(NavigateRequested, WhyNotFrom));
        Border strip = ToolBars.Side(newBranch, update, delete, favorite, navigate);
        Border top = ToolBars.Top(search);
        Children.Add(strip);
        Children.Add(top);
        Children.Add(list);
        UpdateTools();
    }

    /// <summary>The rows shown, top to bottom, as the groups open and the search leaves them.</summary>
    public IReadOnlyList<BranchNode> Rows => rows;

    /// <summary>Every branch and tag, as last read.</summary>
    public IReadOnlyList<BranchInfo> Branches { get; private set; } = [];

    /// <summary>The remotes, which a remote branch's name begins with.</summary>
    public IReadOnlyList<string> Remotes { get; private set; } = [];

    /// <summary>The repository's working trees, the main one first, as last read.</summary>
    public IReadOnlyList<WorktreeInfo> Worktrees { get; private set; } = [];

    /// <summary>Why a working tree cannot be removed from here beyond what git refuses (a window of this Passenger shows
    /// it), or null; the window's.</summary>
    public Func<WorktreeInfo, string?>? WhyNotRemoveWorktree { get; set; }

    public BranchNode? Selected => list.SelectedItem as BranchNode;

    public string SearchText
    {
        get => search.Text ?? string.Empty;
        set => search.Text = value;
    }

    /// <summary>The favourites marked and unmarked, the folder's.</summary>
    public GitBranchesState Favorites { get; set; } = new();

    /// <summary>A branch to filter the log to; null for HEAD (Current Branch).</summary>
    public event EventHandler<BranchInfo?>? FilterRequested;

    public event EventHandler<BranchInfo>? NavigateRequested;

    /// <summary>A branch or tag to check out, with the branches its menu was made among, which the window asks of where
    /// changes are not yet committed.</summary>
    public event EventHandler<(BranchInfo Branch, IReadOnlyList<BranchInfo> All)>? CheckoutRequested;

    public event EventHandler<BranchInfo>? NewBranchRequested;

    public event EventHandler<BranchInfo>? RenameRequested;

    /// <summary>Push… of a local branch, which the window asks of in its Push dialog.</summary>
    public event EventHandler<BranchInfo>? PushRequested;

    public event EventHandler<BranchInfo>? DeleteRequested;

    public event EventHandler<BranchInfo>? CompareRequested;

    public event EventHandler<BranchInfo>? WorkingTreeDiffRequested;

    /// <summary>A tag to make where a branch is, asked for in the New Tag dialog.</summary>
    public event EventHandler<BranchInfo>? NewTagRequested;

    /// <summary>A tag to move to another commit, asked for in the New Tag dialog.</summary>
    public event EventHandler<BranchInfo>? MoveTagRequested;

    public event EventHandler<BranchInfo>? PushTagRequested;

    public event EventHandler<BranchInfo>? DeleteRemoteTagRequested;

    /// <summary>A working tree to add, its branch the one given where it is free, else a new one made from it.</summary>
    public event EventHandler<BranchInfo>? NewWorktreeRequested;

    /// <summary>A working tree to open, in a window of its own or in this one.</summary>
    public event EventHandler<(WorktreeInfo Worktree, bool NewWindow)>? OpenWorktreeRequested;

    public event EventHandler<WorktreeInfo>? RemoveWorktreeRequested;

    /// <summary>A local branch to push to any remote, asked for in the Push dialog.</summary>
    public event EventHandler<BranchInfo>? PushToRequested;

    public event EventHandler? AddRemoteRequested;

    public event EventHandler? ManageRemotesRequested;

    /// <summary>A remote, by its name, to rename or give another URL in the Edit Remote dialog.</summary>
    public event EventHandler<string>? EditRemoteRequested;

    /// <summary>A remote, by its name, to remove once the person agrees.</summary>
    public event EventHandler<string>? RemoveRemoteRequested;

    /// <summary>A branch was marked or unmarked as a favourite, for the folder to keep.</summary>
    public event EventHandler<GitBranchesState>? FavoritesChanged;

    /// <summary>The menu of a branch or tag here, or among <paramref name="all"/> another list read (the branch menu's): the
    /// items that cannot run on it are left out.</summary>
    public IReadOnlyList<Control> Menu(BranchInfo branch, IReadOnlyList<BranchInfo>? all = null)
    {
        IReadOnlyList<BranchInfo> among = all ?? Branches;
        string? current = vcs.Branch;
        string here = current is null ? "Current" : Quoted(current);
        string it = Quoted(branch.Name);
        bool isCurrent = branch.IsCurrent;
        List<Control> items = [];
        void Add(string header, Action run, string? whyNot = null) => items.Add(ListMenus.Item(header, run, whyNot));
        string? detached = current is null ? Refusals.Detached : null;
        void Separate()
        {
            if (items.Count > 0 && items[^1] is not Separator)
            {
                items.Add(new Separator());
            }
        }
        if (!isCurrent)
        {
            Add("Checkout", () => CheckoutRequested?.Invoke(this, (branch, among)));
            IdeErrors.Observe(GreyWhenRefusedAsync((MenuItem)items[^1], vcs.WhyNotCheckoutRefAsync(branch, among)));
        }
        Add($"New Branch from {it}…", () => NewBranchRequested?.Invoke(this, branch));
        if (branch.Kind != RefKind.Tag)
        {
            Add($"New Tag at {it}…", () => NewTagRequested?.Invoke(this, branch));
        }
        Add($"New Worktree from {it}…", () => NewWorktreeRequested?.Invoke(this, branch), vcs.WhyNotNewWorktree);
        if (branch.Kind == RefKind.Local && !isCurrent)
        {
            Add(current is null ? "Checkout and Rebase onto Current" : $"Checkout and Rebase onto {here}", () => IdeErrors.Observe(vcs.CheckoutAndRebaseAsync(branch)), detached);
        }
        Separate();
        if (!isCurrent)
        {
            Add(current is null ? "Compare with Current" : $"Compare with {here}", () => CompareRequested?.Invoke(this, branch));
        }
        Add("Show Diff with Working Tree", () => WorkingTreeDiffRequested?.Invoke(this, branch));
        Separate();
        if (!isCurrent && branch.Kind != RefKind.Tag)
        {
            Add(current is null ? "Rebase Current onto Selected" : $"Rebase {here} onto {it}", () => IdeErrors.Observe(vcs.RebaseOntoAsync(branch)), detached);
        }
        if (!isCurrent)
        {
            Add(current is null ? "Merge into Current" : $"Merge {it} into {here}", () => IdeErrors.Observe(vcs.MergeAsync(branch)));
        }
        Separate();
        if (branch.Kind == RefKind.Local)
        {
            string? notUpdated = WhyNotUpdate(branch);
            Add("Update", () => IdeErrors.Observe(vcs.UpdateBranchAsync(branch)), notUpdated);
            Add("Push…", () => PushRequested?.Invoke(this, branch), vcs.WhyNotRemote);
            Add("Push to…", () => PushToRequested?.Invoke(this, branch), vcs.WhyNotRemote);
            Separate();
            Add("Rename…", () => RenameRequested?.Invoke(this, branch));
        }
        if (branch.Kind == RefKind.Tag)
        {
            Add("Move Tag…", () => MoveTagRequested?.Invoke(this, branch));
            Add("Push…", () => PushTagRequested?.Invoke(this, branch), vcs.WhyNotRemote);
            Separate();
        }
        if (WhyNotDelete(branch) is null)
        {
            Add("Delete", () => DeleteRequested?.Invoke(this, branch));
        }
        if (branch.Kind == RefKind.Tag)
        {
            Add("Delete on Remote…", () => DeleteRemoteTagRequested?.Invoke(this, branch), vcs.WhyNotRemote);
        }
        if (items.Count > 0 && items[^1] is Separator)
        {
            items.RemoveAt(items.Count - 1);
        }
        return items;
    }

    /// <summary>The menu of a working tree here: open it in a window of its own or this one, or remove it, greyed saying
    /// why where it cannot be.</summary>
    public IReadOnlyList<Control> WorktreeMenu(WorktreeInfo worktree)
    {
        string? here = IsThisWindows(worktree) ? $"This window shows {worktree.Name}" : null;
        string? gone = worktree.Gone is { } why ? $"{worktree.Name} cannot be opened: {why}" : null;
        return
        [
            ListMenus.Item("Open in New Window", () => OpenWorktreeRequested?.Invoke(this, (worktree, true)), here ?? gone),
            ListMenus.Item("Open in This Window", () => OpenWorktreeRequested?.Invoke(this, (worktree, false)), here ?? gone),
            new Separator(),
            ListMenus.Item("Remove Worktree…", () => RemoveWorktreeRequested?.Invoke(this, worktree), WhyNotRemove(worktree)),
        ];
    }

    /// <summary>The menu of a remote here: fetch its branches, rename it or give it another URL, or remove it.</summary>
    public IReadOnlyList<Control> RemoteMenu(string remote) =>
    [
        ListMenus.Item("Fetch", () => IdeErrors.Observe(vcs.FetchRemoteAsync(remote)), vcs.WhyNotRemote),
        new Separator(),
        ListMenus.Item("Edit Remote…", () => EditRemoteRequested?.Invoke(this, remote)),
        ListMenus.Item("Remove Remote…", () => RemoveRemoteRequested?.Invoke(this, remote)),
    ];

    /// <summary>Why a working tree cannot be removed: the main one holds the repository itself, git keeps a locked one, and
    /// one a window shows is closed there first.</summary>
    private string? WhyNotRemove(WorktreeInfo worktree) => worktree switch
    {
        { IsMain: true } => $"{worktree.Name} is the repository's main worktree, which holds the repository itself",
        { Locked: { } reason } => $"{worktree.Name} is locked{(reason.Length > 0 ? $": {reason}" : string.Empty)}",
        _ when IsThisWindows(worktree) => $"This window shows {worktree.Name}: open another to remove it",
        _ => WhyNotRemoveWorktree?.Invoke(worktree),
    };

    private bool IsThisWindows(WorktreeInfo worktree) => vcs.Repository is { } shown && PathNames.Same(PathNames.Canonical(worktree.Folder), shown.Root);

    /// <summary>A menu's item greyed once what it does is found refused (a checkout writing out a file git cannot here), its
    /// tip saying why.</summary>
    private static async Task GreyWhenRefusedAsync(MenuItem item, Task<string?> refused)
    {
        if (await refused is { } why)
        {
            ListMenus.Grey(item, why);
        }
    }

    /// <summary>A name quoted for a menu, a long one cut short.</summary>
    private static string Quoted(string name) => $"'{(name.Length > 40 ? name[..39] + "…" : name)}'";

    /// <summary>Lists another folder's branches from now on, its session's working tree in place of the last's.</summary>
    public void Bind(VcsController next) => vcs = next;

    /// <summary>Reads the branches again and shows them, the choice and what is open kept.</summary>
    public async Task LoadAsync()
    {
        int load = ++loads;
        (IReadOnlyList<BranchInfo> branches, IReadOnlyList<string> remotes) = await vcs.BranchTreeAsync();
        IReadOnlyList<WorktreeInfo> worktrees = await vcs.WorktreesAsync();
        if (load != loads)
        {
            return;
        }
        Branches = branches;
        Remotes = remotes;
        Worktrees = worktrees;
        Show();
    }

    /// <summary>Chooses the row of a branch or tag by its name as shown (main, origin/main, v1.0), opening what holds it.</summary>
    public bool Select(string name)
    {
        BranchNode? found = Find(roots, n => n.Branch?.Name == name || (n.Kind == BranchNodeKind.Head && name == "HEAD"));
        if (found is null)
        {
            return false;
        }
        foreach (BranchNode holder in Path(roots, found))
        {
            expanded[holder.Key] = true;
        }
        Show();
        list.SelectedItem = rows.FirstOrDefault(r => r.Key == found.Key);
        return true;
    }

    /// <summary>Gives the search box the keys once the panel is laid out (it may have just been shown), its words chosen,
    /// so what is typed narrows the branches afresh.</summary>
    public void FocusSearch() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        search.Focus();
        search.SelectAll();
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>Gives the chosen row the keys, as a click on it does.</summary>
    public void FocusRows() => ListKeys.Hold(list);

    /// <summary>Opens or closes a group, as a click on its arrow does.</summary>
    public void Toggle(BranchNode node)
    {
        if (!node.IsGroup)
        {
            return;
        }
        node.IsExpanded = !node.IsExpanded;
        expanded[node.Key] = node.IsExpanded;
        if (node.IsExpanded)
        {
            List<BranchNode> below = [];
            foreach (BranchNode child in node.Children)
            {
                Flatten(child, below, SearchText.Trim().Length > 0);
            }
            rows.Open(node, below);
        }
        else
        {
            rows.Close(node);
        }
        list.SelectedItem = node;
    }

    /// <summary>A double-click or Enter: a group opens or closes, a branch or HEAD filters the log to it.</summary>
    public void Activate(BranchNode node)
    {
        if (node.IsGroup)
        {
            Toggle(node);
        }
        else if (node.Kind == BranchNodeKind.Head)
        {
            FilterRequested?.Invoke(this, null);
        }
        else if (node.Branch is { } branch)
        {
            FilterRequested?.Invoke(this, branch);
        }
        else if (node.Worktree?.Branch is { } checkedOut && Branches.FirstOrDefault(b => b.Ref.FullName == checkedOut) is { } its)
        {
            FilterRequested?.Invoke(this, its);
        }
    }

    /// <summary>Mark/Unmark As Favorite.</summary>
    public void ToggleFavorite(BranchInfo branch)
    {
        string full = branch.Ref.FullName;
        bool predefined = GitBranchesState.Predefined.Contains(full);
        bool now = IsFavorite(branch);
        Favorites = predefined
            ? Favorites with { NotFavorites = now ? [.. Favorites.NotFavorites, full] : [.. Favorites.NotFavorites.Where(f => f != full)] }
            : Favorites with { Favorites = now ? [.. Favorites.Favorites.Where(f => f != full)] : [.. Favorites.Favorites, full] };
        FavoritesChanged?.Invoke(this, Favorites);
        string? chosen = Selected?.Key;
        Show();
        list.SelectedItem = rows.FirstOrDefault(r => r.Key == chosen);
    }

    public bool IsFavorite(BranchInfo branch) => Favorites.Holds(branch);

    /// <summary>Builds the tree from the branches read and lists the rows the groups and the search leave; a folder in no
    /// repository has no HEAD or groups to list.</summary>
    private void Show()
    {
        string? chosen = Selected?.Key;
        string text = SearchText.Trim();
        bool Keep(BranchInfo b) => text.Length == 0 || b.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (vcs.Repository is null)
        {
            roots = [];
            rows.Show([]);
            UpdateTools();
            return;
        }
        List<BranchNode> made = [new BranchNode(BranchNodeKind.Head, "HEAD (Current Branch)", "head", 0)];
        BranchNode local = Group(BranchNodeKind.Group, "Local", "local", 0, true);
        Fill(local, [.. Branches.Where(b => b.Kind == RefKind.Local && Keep(b)).Select(b => (b, b.Name))], 1);
        BranchNode remote = Group(BranchNodeKind.Group, "Remote", "remote", 0, true);
        List<BranchInfo> remoteBranches = [.. Branches.Where(b => b.Kind == RefKind.Remote && Keep(b))];
        foreach (IGrouping<string, BranchInfo> byRemote in remoteBranches.GroupBy(RemoteOf).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            BranchNode node = Group(BranchNodeKind.Remote, byRemote.Key, $"remote/{byRemote.Key}", 1, true);
            Fill(node, [.. byRemote.Select(b => (b, b.Name[(byRemote.Key.Length + 1)..]))], 2);
            remote.Children.Add(node);
        }
        // A remote not fetched yet, or with no branches, is listed too, so its menu is there to fetch or change it.
        foreach (string bare in Remotes.Where(r => text.Length == 0 && remote.Children.All(c => c.Title != r)))
        {
            remote.Children.Add(Group(BranchNodeKind.Remote, bare, $"remote/{bare}", 1, true));
        }
        Sort(remote.Children);
        BranchNode tags = Group(BranchNodeKind.Group, "Tags", "tags", 0, false);
        Fill(tags, [.. Branches.Where(b => b.Kind == RefKind.Tag && Keep(b)).Select(b => (b, b.Name))], 1);
        BranchNode worktrees = Group(BranchNodeKind.Group, "Worktrees", "worktrees", 0, Worktrees.Count > 1);
        worktrees.Children.AddRange(Worktrees
            .Where(w => text.Length == 0 || w.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || w.BranchName?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
            .Select(w => new BranchNode(BranchNodeKind.Worktree, w.Name, $"worktree/{w.Folder}", 1, worktree: w)));
        foreach (BranchNode group in (BranchNode[])[local, remote, tags, worktrees])
        {
            if (group.Children.Count > 0 || (text.Length == 0 && (group == local || group == remote || group == worktrees)))
            {
                made.Add(group);
            }
        }
        roots = made;
        List<BranchNode> shown = [];
        foreach (BranchNode root in roots)
        {
            Flatten(root, shown, text.Length > 0);
        }
        listing = true;
        try
        {
            rows.Show(shown);
            list.SelectedItem = rows.FirstOrDefault(r => r.Key == chosen);
        }
        finally
        {
            listing = false;
        }
        UpdateTools();
    }

    private BranchNode Group(BranchNodeKind kind, string title, string key, int depth, bool open)
    {
        BranchNode node = new(kind, title, key, depth) { IsExpanded = expanded.GetValueOrDefault(key, open) };
        return node;
    }

    /// <summary>A group's branches under it, those named alike in folders (Group by Directory).</summary>
    private void Fill(BranchNode group, IReadOnlyList<(BranchInfo Branch, string Path)> branches, int depth)
    {
        foreach (IGrouping<string, (BranchInfo Branch, string Path)> folder in branches.Where(b => b.Path.Contains('/')).GroupBy(b => b.Path[..b.Path.IndexOf('/')]))
        {
            BranchNode node = Group(BranchNodeKind.Folder, folder.Key, $"{group.Key}/{folder.Key}/", depth, true);
            Fill(node, [.. folder.Select(b => (b.Branch, b.Path[(folder.Key.Length + 1)..]))], depth + 1);
            group.Children.Add(node);
        }
        foreach ((BranchInfo branch, string path) in branches.Where(b => !b.Path.Contains('/')))
        {
            group.Children.Add(new BranchNode(BranchNodeKind.Ref, path, branch.Ref.FullName, depth, branch) { IsFavorite = IsFavorite(branch) });
        }
        Sort(group.Children);
    }

    /// <summary>The order: the current branch, favourites, folders holding favourites, other folders, then the rest, each
    /// by name.</summary>
    private static void Sort(List<BranchNode> nodes) =>
        nodes.Sort((a, b) => Weight(a) != Weight(b) ? Weight(a).CompareTo(Weight(b)) : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

    private static int Weight(BranchNode node) => node switch
    {
        { Branch.IsCurrent: true } => 0,
        { Kind: BranchNodeKind.Ref, IsFavorite: true } => 1,
        { IsGroup: true, HasFavorites: true } => 2,
        { IsGroup: true } => 3,
        _ => 4,
    };

    private static void Flatten(BranchNode node, List<BranchNode> into, bool searching)
    {
        into.Add(node);
        if (node.IsGroup && (node.IsExpanded || searching))
        {
            foreach (BranchNode child in node.Children)
            {
                Flatten(child, into, searching);
            }
        }
    }

    private string RemoteOf(BranchInfo branch) => VcsController.RemoteOf(branch, Remotes);

    private static BranchNode? Find(IEnumerable<BranchNode> nodes, Func<BranchNode, bool> match)
    {
        foreach (BranchNode node in nodes)
        {
            if (match(node))
            {
                return node;
            }
            if (Find(node.Children, match) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>The groups that hold a row, outermost first.</summary>
    private static IEnumerable<BranchNode> Path(IEnumerable<BranchNode> nodes, BranchNode target)
    {
        foreach (BranchNode node in nodes)
        {
            if (node == target)
            {
                return [];
            }
            if (Find(node.Children, n => n == target) is not null)
            {
                return [node, .. Path(node.Children, target)];
            }
        }
        return [];
    }

    /// <summary>Asks for what a button does to the chosen branch or tag, where <paramref name="whyNot"/> lets it.</summary>
    private void Request(EventHandler<BranchInfo>? requested, Func<BranchInfo?, string?> whyNot)
    {
        if (Selected?.Branch is { } branch && whyNot(branch) is null)
        {
            requested?.Invoke(this, branch);
        }
    }

    /// <summary>Each button greyed where the row chosen gives it nothing to act on, saying why; its menu and keys agree.</summary>
    private void UpdateTools()
    {
        BranchInfo? chosen = Selected?.Branch;
        ToolButtons.Grey(newBranch, WhyNotFrom(chosen));
        ToolButtons.Grey(update, WhyNotUpdate(chosen));
        ToolButtons.Grey(delete, WhyNotDelete(chosen));
        ToolButtons.Grey(favorite, WhyNotFavorite(chosen));
        ToolButtons.Grey(navigate, WhyNotFrom(chosen));
    }

    /// <summary>Why New Branch… and Navigate can't act: HEAD, a group or no row is chosen, which names no commit of its own.</summary>
    private static string? WhyNotFrom(BranchInfo? branch) => branch is null ? Refusals.NoRefChosen : null;

    /// <summary>Why Update Selected can't act: it brings a local branch up to the remote's branch it tracks, as
    /// <see cref="VcsController.UpdateBranchAsync"/> does, and nothing else, where a remote can be reached.</summary>
    private string? WhyNotUpdate(BranchInfo? branch) => branch switch
    {
        { Kind: RefKind.Local, Upstream: null } => Refusals.Untracked(branch.Name),
        { Kind: RefKind.Local } => vcs.WhyNotRemote,
        { Kind: RefKind.Remote } => Refusals.FetchedNotUpdated(branch.Name),
        _ => Refusals.NoLocalBranchChosen,
    };

    /// <summary>Why Delete can't act: git deletes no branch while it is checked out, and a remote's only where the remote
    /// can be reached.</summary>
    private string? WhyNotDelete(BranchInfo? branch) => branch switch
    {
        null => Refusals.NoRefChosen,
        { IsCurrent: true } => Refusals.CheckedOut(branch.Name),
        { CheckedOutElsewhere: { } worktree } => Refusals.InWorktree(branch.Name, worktree),
        { Kind: RefKind.Remote } => vcs.WhyNotRemote,
        _ => null,
    };

    /// <summary>Why Mark/Unmark As Favorite can't act: a tag is never a favourite (<see cref="GitBranchesState.Holds"/>).</summary>
    private static string? WhyNotFavorite(BranchInfo? branch) => branch is { Kind: not RefKind.Tag } ? null : Refusals.NoBranchChosen;

    /// <summary>Rider's keys here, beside the tree's (<see cref="TreeKeys"/>): Delete (Backspace too on the Mac) deletes, F2 or
    /// Shift+F6 renames a local branch on the Mac (Rename's Ctrl+R, R elsewhere, which the window routes here), and
    /// Ctrl+Alt+N (Cmd+Alt+N) makes one from the chosen.</summary>
    private bool Take(KeyEventArgs e)
    {
        bool mac = OperatingSystem.IsMacOS();
        if (Selected is not { } node)
        {
            return false;
        }
        KeyModifiers command = Keystroke.Command;
        if (node.Branch is { } branch && (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None
            || mac && e.Key == Key.Back && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Meta) && WhyNotDelete(branch) is null)
        {
            DeleteRequested?.Invoke(this, branch);
        }
        else if (mac && node.Branch is { Kind: RefKind.Local } local && (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None || e.Key == Key.F6 && e.KeyModifiers == KeyModifiers.Shift))
        {
            RenameRequested?.Invoke(this, local);
        }
        else if (node.Branch is { } from && e.Key == Key.N && e.KeyModifiers == (command | KeyModifiers.Alt))
        {
            NewBranchRequested?.Invoke(this, from);
        }
        else
        {
            return false;
        }
        return true;
    }

    /// <summary>What a row says muted after its name: where a local branch pushes and the worktree that has it checked out;
    /// what a worktree has checked out and whether this window shows it, it is locked or its folder is gone.</summary>
    private string? Detail(BranchNode node)
    {
        if (node.Worktree is { } worktree)
        {
            string checkedOut = worktree.BranchName ?? (worktree.Head is { } head ? $"detached at {GitHashes.Short(head)}" : "no commit yet");
            return string.Join(", ", ((string?[])[checkedOut, IsThisWindows(worktree) ? "this window" : null, worktree.Locked is not null ? "locked" : null,
                worktree.Gone is not null ? "folder gone" : null]).OfType<string>());
        }
        if (node.Branch is { Kind: RefKind.Local } branch)
        {
            string? elsewhere = branch.CheckedOutElsewhere is { } at ? $"in worktree {System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(at))}" : null;
            string?[] said = [branch.Upstream, elsewhere];
            return said.Any(s => s is not null) ? string.Join(", ", said.OfType<string>()) : null;
        }
        return null;
    }

    /// <summary>A row for whichever group, branch or tag the list hands its container: a group's chevron, a branch's or
    /// tag's mark (a favourite's star; the current branch's in the chosen mark's colour, which reads on every ground a row
    /// stands on, the chosen row's too), its name, and where a local branch pushes.</summary>
    private Control Row()
    {
        RowExpander chevron = RowExpander.Of<BranchNode>(Toggle);
        RowIcon mark = new() { Margin = new Thickness(0, 0, Gaps.Small, 0) };
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        // Where it pushes and pulls, muted after its name.
        TextBlock tracked = new() { Classes = { StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0) };
        tracked.Classes.Add(StyleClasses.Muted);
        ShrinkingRow row = new() { Height = ListRow.Height, Children = { chevron, mark, name, tracked } };
        return row.Showing<BranchNode>(node =>
        {
            row.Margin = new Thickness(node.Depth * Gaps.Indent, 0, 0, 0);
            chevron.Expands = node.IsGroup;
            chevron.IsOpen = node.IsExpanded;
            mark.IsVisible = node.Kind is BranchNodeKind.Ref or BranchNodeKind.Head or BranchNodeKind.Worktree;
            mark.DataKey = node.Kind is BranchNodeKind.Worktree ? IconKeys.Explorer : node.Branch?.Kind == RefKind.Tag ? IconKeys.Label : node.IsFavorite ? IconKeys.StarFilled : IconKeys.Branch;
            mark.ForegroundKey = node.IsFavorite ? TokenKeys.Vcs_Favorite
                : node.Branch?.IsCurrent == true || node.Kind == BranchNodeKind.Head || node.Worktree is { } shown && IsThisWindows(shown) ? TokenKeys.ChosenMark : null;
            name.Text = node.Title;
            name.Classes.Set(StyleClasses.Strong, node.Kind is BranchNodeKind.Group);
            tracked.Text = Detail(node);
            tracked.IsVisible = tracked.Text is not null;
        });
    }
}
