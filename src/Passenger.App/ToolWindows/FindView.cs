using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App.ToolWindows;

/// <summary>One usage as the Find window lists it: where it is (lines and columns from 0), its line's text, what it
/// does there, and the project it is in.</summary>
public sealed record Usage(string Path, int Line, int Column, int Length, string LineText, UsageKind Kind, string Project);

/// <summary>A row of the Find window's tree: a kind of usage, a project, a file, or a usage, at its depth.</summary>
public sealed class FindRow(string label, string? detail, int depth, Usage? usage, IReadOnlyList<FindRow> children) : Observable, ITreeRow
{
    private bool expanded = true;

    public string Label { get; } = label;
    public string? Detail { get; } = detail;
    public int Depth { get; } = depth;

    /// <summary>The usage, for a usage's row; null for a group's.</summary>
    public Usage? Usage { get; } = usage;

    public IReadOnlyList<FindRow> Children { get; } = children;

    public bool Expands => Children.Count > 0;

    public bool IsExpanded
    {
        get => expanded;
        internal set => Set(ref expanded, value);
    }
}

/// <summary>The Find window: a search's usages grouped by what they do (declaration, write, read, invocation,
/// import), then project, then file, with the chosen usage's code beside the tree. Each search is a tab to come back
/// to. Enter, F4 or a double-click goes to a usage; Right and Left open and close a group.</summary>
public sealed class FindView : DockPanel
{
    /// <summary>The most searches kept as tabs; the oldest goes when another comes.</summary>
    private const int TabLimit = 10;

    private static readonly (UsageKind Kind, string Name)[] Groups =
    [
        (UsageKind.Declaration, "Declaration"),
        (UsageKind.Write, "Write access"),
        (UsageKind.Read, "Read access"),
        (UsageKind.Invocation, "Invocation"),
        (UsageKind.Import, "Usage in import"),
        (UsageKind.Other, "Usage"),
    ];

    private readonly List<Search> searches = [];
    private readonly TreeRows<FindRow> rows = [];
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox list;
    private readonly CodeEditor preview;
    private readonly TextBlock placeholder;
    private readonly Func<string, string?> textOf;

    /// <summary>The searches' tabs, shown only while there is one: an empty row with a rule under it says nothing.</summary>
    private readonly Border tabBar;

    private Search? current;

    /// <param name="textOf">A file's text as it is now: the open editor's, else the disk's.</param>
    public FindView(Func<string, string?> textOf)
    {
        this.textOf = textOf;
        tabBar = ToolBars.Top(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = tabs, Margin = new Thickness(8, 0) });
        tabBar.IsVisible = false;
        Children.Add(tabBar);
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<FindRow>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
        list.SelectionChanged += (_, _) => ShowPreview();
        list.DoubleTapped += (_, _) => Go();
        TreeKeys.Attach<FindRow>(list, Toggle, row =>
        {
            if (row.Usage is not { } usage)
            {
                return false;
            }
            GoToRequested?.Invoke(this, usage);
            return true;
        });
        preview = new CodeEditor { IsReadOnly = true, ShowErrorStripe = false, LineCommentPrefix = null, FontSize = 12.5, Document = new TextDocument() };
        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("*,1,*") };
        HairlineDivider divider = new(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
        preview[Grid.ColumnProperty] = 2;
        body.Children.Add(list);
        body.Children.Add(divider);
        body.Children.Add(preview);
        placeholder = new TextBlock { Text = "Find Usages lists a symbol's usages here", Classes = { "emptyText" } };
        body.IsVisible = false;
        Children.Add(new Panel { Children = { body, placeholder } });
        Body = body;
    }

    /// <summary>A usage to go to.</summary>
    public event EventHandler<Usage>? GoToRequested;

    /// <summary>The visible rows of the search shown.</summary>
    public IReadOnlyList<FindRow> Rows => rows;

    /// <summary>The searches kept, by their tabs' titles, the latest last.</summary>
    public IReadOnlyList<string> Searches => [.. searches.Select(s => s.Title)];

    /// <summary>The search shown, by its title.</summary>
    public string? Shown => current?.Title;

    /// <summary>The code beside the tree: the chosen usage's file, scrolled to it.</summary>
    public CodeEditor Preview => preview;

    public FindRow? Selected
    {
        get => list.SelectedItem as FindRow;
        set => list.SelectedItem = value;
    }

    private Grid Body { get; }

    /// <summary>Gives the tree the keys, as a click on a row does: the chosen row's, else the list's.</summary>
    public void FocusRows()
    {
        int index = Math.Max(0, list.SelectedIndex);
        // A row scrolled out of sight has no container to take the focus until it is brought into view.
        list.ScrollIntoView(index);
        list.UpdateLayout();
        list.ContainerFromIndex(index)?.Focus();
    }

    /// <summary>A new search's usages in a tab of their own, every group open and the first usage chosen.</summary>
    public void Show(string title, IReadOnlyList<Usage> usages)
    {
        List<FindRow> roots = [];
        foreach ((UsageKind kind, string name) in Groups)
        {
            List<Usage> ofKind = [.. usages.Where(u => u.Kind == kind)];
            if (ofKind.Count == 0)
            {
                continue;
            }
            List<FindRow> projects = [.. ofKind.GroupBy(u => u.Project).OrderBy(g => g.Key, StringComparer.Ordinal).Select(project =>
                new FindRow(project.Key, Core.Plural.Of(project.Count(), "usage"), 1, null, [.. project.GroupBy(u => u.Path).OrderBy(g => g.Key, StringComparer.Ordinal).Select(file =>
                    new FindRow(Path.GetFileName(file.Key), Core.Plural.Of(file.Count(), "usage"), 2, null, [.. file.OrderBy(u => u.Line).ThenBy(u => u.Column).Select(u =>
                        new FindRow(u.LineText.Trim(), (u.Line + 1).ToString(), 3, u, []))]))]))];
            roots.Add(new FindRow(name, Core.Plural.Of(ofKind.Count, "usage"), 0, null, projects));
        }
        Search search = new(title, roots);
        searches.Add(search);
        if (searches.Count > TabLimit)
        {
            searches.RemoveAt(0);
        }
        ShowSearch(search);

    }

    /// <summary>Shows a kept search again, as a click on its tab does.</summary>
    public void ShowSearch(string title)
    {
        if (searches.FirstOrDefault(s => s.Title == title) is { } search)
        {
            ShowSearch(search);
        }
    }

    public void Expand(FindRow row)
    {
        if (row.IsExpanded || !row.Expands || !rows.Contains(row))
        {
            return;
        }
        row.IsExpanded = true;
        rows.Open(row, TreeRows<FindRow>.Shown(row.Children, r => r.Children));
        Choose(row);
    }

    public void Collapse(FindRow row)
    {
        if (!row.IsExpanded || !rows.Contains(row))
        {
            return;
        }
        row.IsExpanded = false;
        rows.Close(row);
        Choose(row);
    }

    /// <summary>Opens a group, or closes it, as a click on its chevron does.</summary>
    public void Toggle(FindRow row)
    {
        if (row.IsExpanded)
        {
            Collapse(row);
        }
        else
        {
            Expand(row);
        }
    }

    private void ShowSearch(Search search)
    {
        current = search;
        rows.Show([.. TreeRows<FindRow>.Shown(search.Roots, r => r.Children)]);
        Body.IsVisible = true;
        placeholder.IsVisible = false;
        DrawTabs();
        list.SelectedItem = rows.FirstOrDefault(r => r.Usage is not null);
    }

    private void DrawTabs()
    {
        tabs.Children.Clear();
        foreach (Search search in searches)
        {
            ToggleButton tab = ToolTabs.Tab(search.Title, search == current, () => Close(search));
            tab.Click += (_, _) => ShowSearch(search);
            tabs.Children.Add(tab);
        }
        tabBar.IsVisible = searches.Count > 0;
    }

    private void Close(Search search)
    {
        int at = searches.IndexOf(search);
        searches.Remove(search);
        if (search != current)
        {
            DrawTabs();
            return;
        }
        if (searches.Count > 0)
        {
            ShowSearch(searches[Math.Min(at, searches.Count - 1)]);
            return;
        }
        current = null;
        rows.Clear();
        tabs.Children.Clear();
        tabBar.IsVisible = false;
        Body.IsVisible = false;
        placeholder.IsVisible = true;
    }

    private void ShowPreview()
    {
        if (Selected?.Usage is not { } usage || textOf(usage.Path) is not { } text)
        {
            return;
        }
        if (preview.Tag as string != usage.Path || preview.Document.Snapshot.ToString() != text)
        {
            TextDocument document = new(text);
            preview.Document = document;
            (preview.Highlighter as IDisposable)?.Dispose();
            preview.Highlighter = Languages.Highlighters.For(Languages.Language.ForPath(usage.Path), document);
            preview.Tag = usage.Path;
        }
        TextSnapshot snapshot = preview.Document.Snapshot;
        int start = snapshot.GetOffset(new TextPosition(Math.Min(usage.Line, snapshot.LineCount - 1), usage.Column));
        preview.SetHighlights([new TextRange(start, Math.Min(usage.Length, snapshot.Length - start))], 0);
        preview.GoTo(snapshot.GetPosition(start));
    }

    private void Go()
    {
        if (Selected is { Usage: { } usage })
        {
            GoToRequested?.Invoke(this, usage);
        }
        else if (Selected is { } group)
        {
            Toggle(group);
        }
    }

    /// <summary>A group opened or closed is the one chosen.</summary>
    private void Choose(FindRow row) => list.SelectedItem = row;

    private Control Row(FindRow row)
    {
        StackPanel panel = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Margin = new Thickness(8 + (row.Depth * 16), 0, 8, 0) };
        if (row.Usage is { } usage)
        {
            TextBlock number = new() { Text = row.Detail, MinWidth = 36, FontFamily = new FontFamily("avares://Passenger.Editor/Fonts#JetBrains Mono"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            number.Classes.Add("muted");
            panel.Children.Add(number);
            panel.Children.Add(new TextBlock { Text = row.Label, FontFamily = new FontFamily("avares://Passenger.Editor/Fonts#JetBrains Mono"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }
        PathIcon chevron = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0) };
        row.FollowWhileShown(chevron, (_, _) => chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(row.IsExpanded ? IconKeys.ChevronDown : IconKeys.ChevronRight)));
        chevron.PointerPressed += (_, e) =>
        {
            Toggle(row);
            e.Handled = true;
        };
        panel.Children.Add(chevron);
        if (row.Depth == 2 && row.Children.FirstOrDefault()?.Usage is { } first)
        {
            panel.Children.Add(new PathIcon { Data = Explorer.FileIcons.ForFile(first.Path), Foreground = Explorer.FileIcons.BrushForFile(first.Path), Margin = new Thickness(0, 0, 6, 0) });
        }
        panel.Children.Add(new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center, FontWeight = row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal });
        TextBlock count = new() { Text = row.Detail, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        count.Classes.Add("muted");
        panel.Children.Add(count);
        return panel;
    }

    private sealed record Search(string Title, IReadOnlyList<FindRow> Roots);
}
