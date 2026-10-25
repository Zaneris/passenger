using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.State;
using Passenger.App.Vcs;
using Passenger.Editor;
using Passenger.Vcs;

namespace Passenger.App.ToolWindows;

/// <summary>What a row of the branches panel is: HEAD (Current Branch), a top group (Local, Remote, Tags), a remote, a
/// folder of branches named alike ("feature/" of "feature/login"), or a branch or tag.</summary>
public enum BranchNodeKind
{
    Head,
    Group,
    Remote,
    Folder,
    Ref,
}

/// <summary>A row of the branches panel: what it is, its name, how deep it sits, and the branch or tag it is.</summary>
public sealed class BranchNode(BranchNodeKind kind, string title, string key, int depth, BranchInfo? branch = null) : Observable, ITreeRow
{
    private bool isExpanded;

    public BranchNodeKind Kind { get; } = kind;

    public string Title { get; } = title;

    /// <summary>What it is known by from one reading to the next, so it stays open or closed and chosen.</summary>
    public string Key { get; } = key;

    public int Depth { get; } = depth;

    public BranchInfo? Branch { get; } = branch;

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
    public List<string> Favorites { get; init; } = [];

    public List<string> NotFavorites { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public GitBranchesState Clamped() => this with
    {
        Favorites = [.. (Favorites ?? []).Where(f => f is not null).Distinct()],
        NotFavorites = [.. (NotFavorites ?? []).Where(f => f is not null).Distinct()],
    };
}

/// <summary>The branches panel at the Git log's left: HEAD (Current Branch), then Local, Remote by remote and Tags, each
/// grouped by directory, the current branch first, then favourites (main and master starred to begin with), then
/// folders, then the rest by name; a search narrows them. A double-click or Enter on one filters the log to it (Update
/// Branch Filter in Log); its menu checks out, branches, compares, rebases, merges, updates, pushes, renames and
/// deletes.</summary>
public sealed class BranchesPanel : DockPanel
{
    /// <summary>The predefined favourites.</summary>
    private static readonly string[] Predefined = ["refs/heads/master", "refs/heads/main", "refs/remotes/origin/master", "refs/remotes/origin/main"];

    private VcsController vcs;
    private readonly ListBox list;
    private readonly TextBox search;
    private readonly Dictionary<string, bool> expanded = [];
    private readonly Button favorite;
    private List<BranchNode> roots = [];
    private readonly TreeRows<BranchNode> rows = [];
    private int loads;

    public BranchesPanel(VcsController git)
    {
        vcs = git;
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<BranchNode>((node, _) => node is null ? new TextBlock() : Row(node), supportsRecycling: false),
        };
        list.DoubleTapped += (_, e) =>
        {
            if (Selected is { } node)
            {
                Activate(node);
                e.Handled = true;
            }
        };
        TreeKeys.Attach<BranchNode>(list, Toggle, node =>
        {
            if (node.IsGroup)
            {
                return false;
            }
            Activate(node);
            return true;
        });
        list.AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        list.SelectionChanged += (_, _) => UpdateTools();
        ListMenus.Attach(list, () => Selected is { Branch: { } branch } ? Menu(branch) : []);
        search = new TextBox { Classes = { "search" }, PlaceholderText = "Branch or tag", Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        search.TextChanged += (_, _) => Show();
        favorite = ToolButtons.Of(IconKeys.Star, "Mark/Unmark As Favorite", () =>
        {
            if (Selected is { Branch: { } branch })
            {
                ToggleFavorite(branch);
            }
        });
        Border strip = ToolBars.Side(
            ToolButtons.Of(IconKeys.Add, "New Branch…", () => Request(NewBranchRequested)),
            ToolButtons.Of(IconKeys.Update, "Update Selected", () =>
            {
                if (Selected is { Branch: { Kind: RefKind.Local } branch })
                {
                    IdeErrors.Observe(vcs.UpdateBranchAsync(branch));
                }
            }),
            ToolButtons.Of(IconKeys.Clear, "Delete", () => Request(DeleteRequested)),
            favorite,
            ToolButtons.Of(IconKeys.Locate, "Navigate Log to Selected Branch Head", () => Request(NavigateRequested)));
        Border top = ToolBars.Top(search);
        Children.Add(strip);
        Children.Add(top);
        Children.Add(list);
    }

    /// <summary>The rows shown, top to bottom, as the groups open and the search leaves them.</summary>
    public IReadOnlyList<BranchNode> Rows => rows;

    /// <summary>Every branch and tag, as last read.</summary>
    public IReadOnlyList<BranchInfo> Branches { get; private set; } = [];

    /// <summary>The remotes, which a remote branch's name begins with.</summary>
    public IReadOnlyList<string> Remotes { get; private set; } = [];

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

    public event EventHandler<BranchInfo>? NewBranchRequested;

    public event EventHandler<BranchInfo>? RenameRequested;

    public event EventHandler<BranchInfo>? DeleteRequested;

    public event EventHandler<BranchInfo>? CompareRequested;

    public event EventHandler<BranchInfo>? WorkingTreeDiffRequested;

    /// <summary>A branch was marked or unmarked as a favourite, for the folder to keep.</summary>
    public event EventHandler<GitBranchesState>? FavoritesChanged;

    /// <summary>The menu of a branch or tag here: the items that cannot run on it are left out.</summary>
    public IReadOnlyList<Control> Menu(BranchInfo branch)
    {
        string? current = vcs.Branch;
        string here = current is null ? "Current" : Quoted(current);
        string it = Quoted(branch.Name);
        bool isCurrent = branch.IsCurrent;
        List<Control> items = [];
        void Add(string header, Action run, bool enabled = true) => items.Add(ListMenus.Item(header, null, run, enabled));
        void Separate()
        {
            if (items.Count > 0 && items[^1] is not Separator)
            {
                items.Add(new Separator());
            }
        }
        if (!isCurrent)
        {
            Add("Checkout", () => IdeErrors.Observe(vcs.CheckoutRefAsync(branch, Branches)));
        }
        Add($"New Branch from {it}…", () => NewBranchRequested?.Invoke(this, branch));
        if (branch.Kind == RefKind.Local && !isCurrent)
        {
            Add(current is null ? "Checkout and Rebase onto Current" : $"Checkout and Rebase onto {here}", () => IdeErrors.Observe(vcs.CheckoutAndRebaseAsync(branch)), current is not null);
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
            Add(current is null ? "Rebase Current onto Selected" : $"Rebase {here} onto {it}", () => IdeErrors.Observe(vcs.RebaseOntoAsync(branch)), current is not null);
        }
        if (!isCurrent)
        {
            Add(current is null ? "Merge into Current" : $"Merge {it} into {here}", () => IdeErrors.Observe(vcs.MergeAsync(branch)));
        }
        Separate();
        if (branch.Kind == RefKind.Local)
        {
            Add("Update", () => IdeErrors.Observe(vcs.UpdateBranchAsync(branch)), isCurrent || branch.UpstreamRemote is not null);
            Add("Push…", () => IdeErrors.Observe(vcs.PushBranchAsync(branch)));
            Separate();
            Add("Rename…", () => RenameRequested?.Invoke(this, branch));
        }
        if (!isCurrent)
        {
            Add("Delete", () => DeleteRequested?.Invoke(this, branch));
        }
        if (items.Count > 0 && items[^1] is Separator)
        {
            items.RemoveAt(items.Count - 1);
        }
        return items;
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
        if (load != loads)
        {
            return;
        }
        Branches = branches;
        Remotes = remotes;
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

    /// <summary>Gives the chosen row the keys, as a click on it does.</summary>
    public void FocusRows()
    {
        if (list.SelectedIndex < 0)
        {
            return;
        }
        list.ScrollIntoView(list.SelectedIndex);
        list.UpdateLayout();
        list.ContainerFromIndex(list.SelectedIndex)?.Focus();
    }

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
    }

    /// <summary>Mark/Unmark As Favorite.</summary>
    public void ToggleFavorite(BranchInfo branch)
    {
        string full = branch.Ref.FullName;
        bool predefined = Predefined.Contains(full);
        bool now = IsFavorite(branch);
        Favorites = predefined
            ? Favorites with { NotFavorites = now ? [.. Favorites.NotFavorites, full] : [.. Favorites.NotFavorites.Where(f => f != full)] }
            : Favorites with { Favorites = now ? [.. Favorites.Favorites.Where(f => f != full)] : [.. Favorites.Favorites, full] };
        FavoritesChanged?.Invoke(this, Favorites);
        string? chosen = Selected?.Key;
        Show();
        list.SelectedItem = rows.FirstOrDefault(r => r.Key == chosen);
    }

    public bool IsFavorite(BranchInfo branch) => branch.Kind != RefKind.Tag && (Favorites.Favorites.Contains(branch.Ref.FullName)
        || (Predefined.Contains(branch.Ref.FullName) && !Favorites.NotFavorites.Contains(branch.Ref.FullName)));

    /// <summary>Builds the tree from the branches read and lists the rows the groups and the search leave.</summary>
    private void Show()
    {
        string? chosen = Selected?.Key;
        string text = SearchText.Trim();
        bool Keep(BranchInfo b) => text.Length == 0 || b.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
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
        Sort(remote.Children);
        BranchNode tags = Group(BranchNodeKind.Group, "Tags", "tags", 0, false);
        Fill(tags, [.. Branches.Where(b => b.Kind == RefKind.Tag && Keep(b)).Select(b => (b, b.Name))], 1);
        foreach (BranchNode group in (BranchNode[])[local, remote, tags])
        {
            if (group.Children.Count > 0 || (text.Length == 0 && group == local))
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
        rows.Show(shown);
        list.SelectedItem = rows.FirstOrDefault(r => r.Key == chosen);
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

    private string RemoteOf(BranchInfo branch) =>
        Remotes.Where(r => branch.Name.StartsWith(r + "/", StringComparison.Ordinal)).MaxBy(r => r.Length)
        ?? branch.Name[..Math.Max(0, branch.Name.IndexOf('/', StringComparison.Ordinal))];

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

    private void Request(EventHandler<BranchInfo>? requested)
    {
        if (Selected is { Branch: { } branch })
        {
            requested?.Invoke(this, branch);
        }
    }

    private void UpdateTools() => favorite.IsEnabled = Selected is { Branch.Kind: not RefKind.Tag };

    /// <summary>Rider's keys here, beside the tree's (<see cref="TreeKeys"/>): Delete (Backspace too on the Mac) deletes, F2 or
    /// Shift+F6 renames a local branch on the Mac (Rename's Ctrl+R, R elsewhere, which the window routes here), and
    /// Ctrl+Alt+N (Cmd+Alt+N) makes one from the chosen.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        bool mac = OperatingSystem.IsMacOS();
        if (Selected is not { } node)
        {
            return;
        }
        KeyModifiers command = Keystroke.Command;
        if (node.Branch is { } branch && (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None
            || mac && e.Key == Key.Back && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Meta) && !branch.IsCurrent)
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
            return;
        }
        e.Handled = true;
    }

    private Control Row(BranchNode node)
    {
        ShrinkingRow row = new() { Height = ListRow.Height, Margin = new Thickness(node.Depth * 16, 0, 0, 0) };
        if (node.IsGroup)
        {
            PathIcon chevron = new() { Classes = { "chevron" }, Width = 10, Height = 10, Margin = new Thickness(4, 0, 4, 0) };
            node.FollowWhileShown(chevron, (_, _) => chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(node.IsExpanded ? IconKeys.ChevronDown : IconKeys.ChevronRight)));
            Border hit = new() { Background = Brushes.Transparent, Child = chevron, VerticalAlignment = VerticalAlignment.Center };
            hit.PointerPressed += (_, e) =>
            {
                Toggle(node);
                e.Handled = true;
            };
            row.Children.Add(hit);
        }
        else
        {
            row.Children.Add(new Border { Width = 18 });
        }
        if (node.Kind is BranchNodeKind.Ref or BranchNodeKind.Head)
        {
            string icon = node.Branch?.Kind == RefKind.Tag ? IconKeys.Label : node.IsFavorite ? IconKeys.StarFilled : IconKeys.Branch;
            PathIcon mark = new() { Width = 13, Height = 13, Margin = new Thickness(0, 0, 6, 0) };
            mark.Bind(PathIcon.DataProperty, mark.GetResourceObservable(icon));
            if (node.IsFavorite)
            {
                mark.Bind(PathIcon.ForegroundProperty, mark.GetResourceObservable(TokenKeys.Vcs_Favorite));
            }
            else if (node.Branch?.IsCurrent == true || node.Kind == BranchNodeKind.Head)
            {
                mark.Bind(PathIcon.ForegroundProperty, mark.GetResourceObservable(TokenKeys.Accent));
            }
            row.Children.Add(mark);
        }
        TextBlock name = new() { Text = node.Title, VerticalAlignment = VerticalAlignment.Center };
        if (node.Kind is BranchNodeKind.Group)
        {
            name.FontWeight = FontWeight.SemiBold;
        }
        row.Children.Add(name);
        if (node.Branch is { Kind: RefKind.Local, Upstream: { } upstream })
        {
            // Where it pushes and pulls, muted after its name.
            TextBlock tracked = new() { Text = upstream, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            tracked.Classes.Add("muted");
            row.Children.Add(tracked);
        }
        return row;
    }
}
