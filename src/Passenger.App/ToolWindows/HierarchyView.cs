using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.ToolWindows;

/// <summary>A symbol in a hierarchy, and where going to it lands: a caller at its call, anything else at its name.</summary>
public sealed record HierarchyNode(HierarchyItem Item, string Path, TextPosition Where);

/// <summary>One of a hierarchy's two directions: its toggle's label and how a node's next level is found.</summary>
public sealed record HierarchyDirection(string Label, Func<HierarchyItem, Task<IReadOnlyList<HierarchyNode>>> Load);

/// <summary>A row of the tree: a node at its depth, open or not, its next level found the first time it opens.</summary>
public sealed class HierarchyRow(HierarchyNode node, int depth) : Observable, ITreeRow
{
    private bool expanded;
    private List<HierarchyRow>? below;

    public HierarchyNode Node { get; } = node;
    public int Depth { get; } = depth;

    /// <summary>The next level, once asked for; empty for a leaf.</summary>
    public IReadOnlyList<HierarchyNode>? Children
    {
        get => children;
        internal set
        {
            if (Set(ref children, value))
            {
                Raise(nameof(Expands));
            }
        }
    }

    private IReadOnlyList<HierarchyNode>? children;

    /// <summary>The next level's rows, made the first time it opens.</summary>
    internal IReadOnlyList<HierarchyRow>? Below => Children is null ? null : below ??= [.. Children.Select(c => new HierarchyRow(c, Depth + 1))];

    /// <summary>Whether it may open: a level not yet asked for may have rows.</summary>
    public bool Expands => Children is not { Count: 0 };

    public bool IsExpanded
    {
        get => expanded;
        internal set => Set(ref expanded, value);
    }
}

/// <summary>The Hierarchy window, for calls (Ctrl+Alt+H) and types (Ctrl+E, H): the symbol at the top, and under it
/// a tree going one way, callers or callees, supertypes or subtypes, each level asked of the language server as it is
/// opened. The toggles switch the direction; Enter, F4, a double-click or a row's menu goes to a row, Right and Left
/// open and close (<see cref="TreeKeys"/>).</summary>
public sealed class HierarchyView : DockPanel
{
    /// <summary>How many levels below the top Expand All opens.</summary>
    private const int ExpandAllDepth = 5;

    private readonly TreeRows<HierarchyRow> rows = [];
    private readonly ListBox list;
    private readonly TextBlock title;
    private readonly ToggleButton first;
    private readonly ToggleButton second;
    private readonly TextBlock placeholder;

    /// <summary>The symbol's name and the toggles, shown only once there is a hierarchy: an empty bar says nothing.</summary>
    private readonly Border bar;

    private (HierarchyDirection First, HierarchyDirection Second)? directions;
    private HierarchyNode? root;

    public HierarchyView()
    {
        title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        first = Toggle();
        second = Toggle();
        first.Click += (_, _) => IdeErrors.Observe(ChooseAsync(first: true));
        second.Click += (_, _) => IdeErrors.Observe(ChooseAsync(first: false));
        DockPanel top = new();
        StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Children = { first, second } };
        DockPanel.SetDock(toggles, Dock.Right);
        top.Children.Add(toggles);
        top.Children.Add(title);
        bar = ToolBars.Top(top);
        bar.IsVisible = false;
        Children.Add(bar);
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
        };
        ReusedRows.Use(list, Row);
        list.DoubleTapped += (_, _) => Go();
        TreeKeys.Attach<HierarchyRow>(list, Toggle, row =>
        {
            GoToRequested?.Invoke(this, row.Node);
            return true;
        });
        ListMenus.Attach(list, () => list.SelectedItem is not HierarchyRow chosen ? [] :
        [
            ListMenus.Item("Go to", "F4", () => GoToRequested?.Invoke(this, chosen.Node)),
            ListMenus.Item("Copy Name", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Node.Item.Name))),
            ListMenus.Item("Copy Path", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Node.Path))),
        ]);
        placeholder = new TextBlock { Classes = { "emptyText" } };
        ActionTips.SetWords(placeholder, keys =>
            $"{ActionTips.Named("Call Hierarchy", keys(AppActions.CallHierarchy))} and {ActionTips.Named("Type Hierarchy", keys(AppActions.TypeHierarchy))} show the symbol at the caret here");
        Children.Add(new Panel { Children = { list, placeholder } });
    }

    /// <summary>A row to go to.</summary>
    public event EventHandler<HierarchyNode>? GoToRequested;

    /// <summary>A level could not be listed, and why, for the status bar.</summary>
    public event EventHandler<string>? Failed;

    public IReadOnlyList<HierarchyRow> Rows => rows;

    public string Title => title.Text ?? string.Empty;

    /// <summary>Which direction the tree goes: the first toggle's or the second's.</summary>
    public bool IsFirst => first.IsChecked == true;

    /// <summary>Shows a symbol's hierarchy, the first direction open from the top.</summary>
    public Task ShowAsync(string heading, HierarchyNode top, HierarchyDirection one, HierarchyDirection other)
    {
        title.Text = heading;
        // Narrowed beside the toggles it ends in an ellipsis; the whole heading is its tip.
        ToolTip.SetTip(title, heading);
        root = top;
        directions = (one, other);
        first.Content = one.Label;
        second.Content = other.Label;
        bar.IsVisible = true;
        placeholder.IsVisible = false;
        return ChooseAsync(first: true);
    }

    /// <summary>Switches the tree's direction, opening it again from the top.</summary>
    public async Task ChooseAsync(bool first)
    {
        this.first.IsChecked = first;
        second.IsChecked = !first;
        if (root is null)
        {
            return;
        }
        rows.Clear();
        HierarchyRow top = new(root, 0);
        rows.Add(top);
        list.SelectedIndex = 0;
        await ExpandAsync(top);
    }

    /// <summary>Opens a row, asking for its next level the first time.</summary>
    public async Task ExpandAsync(HierarchyRow row)
    {
        if (row.IsExpanded || directions is not { } ways)
        {
            return;
        }
        HierarchyDirection way = IsFirst ? ways.First : ways.Second;
        try
        {
            row.Children ??= await way.Load(row.Node.Item);
        }
        catch (LanguageServerFailed failed)
        {
            // Opened empty, as a leaf is; the hierarchy shown again asks afresh.
            row.Children = [];
            Failed?.Invoke(this, $"{way.Label} of {row.Node.Item.Name} could not be listed: {failed.Reason}");
        }
        if (!rows.Contains(row))
        {
            return;
        }
        row.IsExpanded = true;
        rows.Open(row, TreeRows<HierarchyRow>.Shown(row.Below ?? [], r => r.Below));
        Choose(row);
    }

    /// <summary>Opens every row down to <see cref="ExpandAllDepth"/> levels below the top, asking for each level not yet
    /// listed, a level at a time: a call hierarchy goes on without end where a method calls itself.</summary>
    public async Task ExpandAllAsync()
    {
        object? chosen = list.SelectedItem;
        for (int depth = 0; depth < ExpandAllDepth; depth++)
        {
            foreach (HierarchyRow row in rows.Where(r => r.Depth == depth && r.Expands && !r.IsExpanded).ToList())
            {
                await ExpandAsync(row);
            }
        }
        list.SelectedItem = chosen is HierarchyRow kept && rows.Contains(kept) ? kept : rows.FirstOrDefault();
    }

    /// <summary>Closes every row below the top, so none opens again with its own rows open.</summary>
    public void CollapseAll()
    {
        if (rows.FirstOrDefault() is not { } top)
        {
            return;
        }
        foreach (HierarchyRow row in rows.Reverse().Where(r => r != top && r.IsExpanded).ToList())
        {
            Collapse(row);
        }
        foreach (HierarchyRow hidden in Every(top.Below ?? []).Where(r => r.IsExpanded))
        {
            hidden.IsExpanded = false;
        }
        Choose(top);

        static IEnumerable<HierarchyRow> Every(IEnumerable<HierarchyRow> level) => level.SelectMany(row => Every(row.Below ?? []).Prepend(row));
    }

    public void Collapse(HierarchyRow row)
    {
        if (!row.IsExpanded || !rows.Contains(row))
        {
            return;
        }
        row.IsExpanded = false;
        rows.Close(row);
        Choose(row);
    }

    /// <summary>Opens a row, or closes it, as a click on its chevron does.</summary>
    public void Toggle(HierarchyRow row)
    {
        if (row.IsExpanded)
        {
            Collapse(row);
        }
        else
        {
            IdeErrors.Observe(ExpandAsync(row));
        }
    }

    /// <summary>A row opened or closed is the one chosen.</summary>
    private void Choose(HierarchyRow row) => list.SelectedItem = row;

    private void Go()
    {
        if (list.SelectedItem is HierarchyRow row)
        {
            GoToRequested?.Invoke(this, row.Node);
        }
    }

    /// <summary>A row for whichever symbol the list hands its container: its chevron, name, and what and where it is.</summary>
    private Control Row()
    {
        RowIcon chevron = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 4, 0) };
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock detail = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        detail.Classes.Add("muted");
        StackPanel line = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { chevron, name, detail } };
        chevron.PointerPressed += (_, e) =>
        {
            if (line.DataContext is HierarchyRow row)
            {
                Toggle(row);
                e.Handled = true;
            }
        };
        return line.Showing<HierarchyRow>(row =>
        {
            line.Margin = new Thickness(8 + (row.Depth * 16), 0, 8, 0);
            chevron.IsVisible = row.Expands;
            chevron.DataKey = row.IsExpanded ? IconKeys.ChevronDown : IconKeys.ChevronRight;
            name.Text = row.Node.Item.Name;
            name.FontWeight = row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal;
            detail.Text = $"{row.Node.Item.Detail}  {System.IO.Path.GetFileName(row.Node.Path)}:{row.Node.Where.Line + 1}";
        });
    }

    // As wide as its words: an option toggle is otherwise an icon's square.
    private static ToggleButton Toggle() => new() { Classes = { "option" }, FontSize = 12, Width = double.NaN, Padding = new Thickness(8, 2) };
}
