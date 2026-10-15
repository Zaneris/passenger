using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
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
public sealed class HierarchyRow(HierarchyNode node, int depth) : Observable
{
    private bool expanded;

    public HierarchyNode Node { get; } = node;
    public int Depth { get; } = depth;

    /// <summary>The next level, once asked for; empty for a leaf.</summary>
    public IReadOnlyList<HierarchyNode>? Children { get; internal set; }

    public bool IsExpanded
    {
        get => expanded;
        internal set => Set(ref expanded, value);
    }
}

/// <summary>Rider's Hierarchy window, for calls (Ctrl+Alt+H) and types (Ctrl+E, H): the symbol at the top, and under it
/// a tree going one way, callers or callees, supertypes or subtypes, each level asked of the language server as it is
/// opened. The toggles switch the direction; Enter or a double-click goes to a row, Right and Left open and close.</summary>
public sealed class HierarchyView : DockPanel
{
    private readonly ObservableCollection<HierarchyRow> rows = [];
    private readonly ListBox list;
    private readonly TextBlock title;
    private readonly ToggleButton first;
    private readonly ToggleButton second;
    private readonly TextBlock placeholder;
    private (HierarchyDirection First, HierarchyDirection Second)? directions;
    private HierarchyNode? root;

    public HierarchyView()
    {
        title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        first = Toggle();
        second = Toggle();
        first.Click += (_, _) => _ = ChooseAsync(first: true);
        second.Click += (_, _) => _ = ChooseAsync(first: false);
        DockPanel top = new() { Height = 32 };
        StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Children = { first, second } };
        DockPanel.SetDock(toggles, Dock.Right);
        top.Children.Add(toggles);
        top.Children.Add(title);
        Border bar = new() { [DockProperty] = Dock.Top, BorderThickness = new Thickness(0, 0, 0, 1), Child = top };
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable("Passenger.Divider"));
        Children.Add(bar);
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<HierarchyRow>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
        list.DoubleTapped += (_, _) => Go();
        list.KeyDown += OnKey;
        placeholder = new TextBlock { Classes = { "emptyHint" }, Text = "Call Hierarchy and Type Hierarchy show the symbol at the caret here" };
        Children.Add(new Panel { Children = { list, placeholder } });
        first.IsVisible = second.IsVisible = false;
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
        root = top;
        directions = (one, other);
        first.Content = one.Label;
        second.Content = other.Label;
        first.IsVisible = second.IsVisible = true;
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
        int at = rows.IndexOf(row);
        if (at < 0)
        {
            return;
        }
        row.IsExpanded = true;
        for (int i = 0; i < row.Children.Count; i++)
        {
            rows.Insert(at + 1 + i, new HierarchyRow(row.Children[i], row.Depth + 1));
        }
        Refresh(row);
    }

    public void Collapse(HierarchyRow row)
    {
        int at = rows.IndexOf(row);
        if (at < 0 || !row.IsExpanded)
        {
            return;
        }
        while (at + 1 < rows.Count && rows[at + 1].Depth > row.Depth)
        {
            rows.RemoveAt(at + 1);
        }
        row.IsExpanded = false;
        Refresh(row);
    }

    private void Refresh(HierarchyRow row)
    {
        // The row's chevron changes with it; its template is drawn again.
        int at = rows.IndexOf(row);
        if (at >= 0)
        {
            rows[at] = row;
            list.SelectedIndex = at;
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (list.SelectedItem is not HierarchyRow row)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter or Key.F4:
                Go();
                e.Handled = true;
                break;
            case Key.Right:
                _ = ExpandAsync(row);
                e.Handled = true;
                break;
            case Key.Left:
                Collapse(row);
                e.Handled = true;
                break;
        }
    }

    private void Go()
    {
        if (list.SelectedItem is HierarchyRow row)
        {
            GoToRequested?.Invoke(this, row.Node);
        }
    }

    private Control Row(HierarchyRow row)
    {
        PathIcon chevron = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 4, 0), IsVisible = row.Children is not { Count: 0 } };
        chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(row.IsExpanded ? "Icon.ChevronDown" : "Icon.ChevronRight"));
        chevron.PointerPressed += (_, e) =>
        {
            if (row.IsExpanded)
            {
                Collapse(row);
            }
            else
            {
                _ = ExpandAsync(row);
            }
            e.Handled = true;
        };
        TextBlock name = new() { Text = row.Node.Item.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal };
        TextBlock detail = new()
        {
            Text = $"{row.Node.Item.Detail}  {System.IO.Path.GetFileName(row.Node.Path)}:{row.Node.Where.Line + 1}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        detail.Bind(TextBlock.ForegroundProperty, detail.GetResourceObservable("Passenger.TextMuted"));
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = 24,
            Margin = new Thickness(8 + (row.Depth * 16), 0, 8, 0),
            Children = { chevron, name, detail },
        };
    }

    // As wide as its words: an option toggle is otherwise an icon's square, which cut "Supertypes" to "S".
    private static ToggleButton Toggle() => new() { Classes = { "option" }, FontSize = 12, Width = double.NaN, Padding = new Thickness(8, 2) };
}
