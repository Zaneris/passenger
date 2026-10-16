using System.Collections.ObjectModel;
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
public sealed class FindRow(string label, string? detail, int depth, Usage? usage, IReadOnlyList<FindRow> children) : Observable
{
    private bool expanded = true;

    public string Label { get; } = label;
    public string? Detail { get; } = detail;
    public int Depth { get; } = depth;

    /// <summary>The usage, for a usage's row; null for a group's.</summary>
    public Usage? Usage { get; } = usage;

    public IReadOnlyList<FindRow> Children { get; } = children;

    public bool IsExpanded
    {
        get => expanded;
        internal set => Set(ref expanded, value);
    }
}

/// <summary>Rider's Find window: a search's usages grouped by what they do (declaration, write, read, invocation,
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
    private readonly ObservableCollection<FindRow> rows = [];
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox list;
    private readonly CodeEditor preview;
    private readonly TextBlock placeholder;
    private readonly Func<string, string?> textOf;
    private Search? current;

    /// <param name="textOf">A file's text as it is now: the open editor's, else the disk's.</param>
    public FindView(Func<string, string?> textOf)
    {
        this.textOf = textOf;
        Border bar = new()
        {
            [DockProperty] = Dock.Top,
            Height = 32,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = tabs, Margin = new Thickness(8, 0) },
        };
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable("Passenger.Divider"));
        Children.Add(bar);
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<FindRow>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
        list.SelectionChanged += (_, _) => ShowPreview();
        list.DoubleTapped += (_, _) => Go();
        // Seen before the list's own keys, which take Enter and the arrows for its selection.
        list.AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        preview = new CodeEditor { IsReadOnly = true, ShowErrorStripe = false, LineCommentPrefix = null, FontSize = 12.5, Document = new TextDocument() };
        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("*,1,*") };
        HairlineDivider divider = new(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
        preview[Grid.ColumnProperty] = 2;
        body.Children.Add(list);
        body.Children.Add(divider);
        body.Children.Add(preview);
        placeholder = new TextBlock { Classes = { "emptyHint" }, Text = "Find Usages lists a symbol's usages here" };
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
                new FindRow(project.Key, Count(project.Count()), 1, null, [.. project.GroupBy(u => u.Path).OrderBy(g => g.Key, StringComparer.Ordinal).Select(file =>
                    new FindRow(Path.GetFileName(file.Key), Count(file.Count()), 2, null, [.. file.OrderBy(u => u.Line).ThenBy(u => u.Column).Select(u =>
                        new FindRow(u.LineText.Trim(), (u.Line + 1).ToString(), 3, u, []))]))]))];
            roots.Add(new FindRow(name, Count(ofKind.Count), 0, null, projects));
        }
        Search search = new(title, roots);
        searches.Add(search);
        if (searches.Count > TabLimit)
        {
            searches.RemoveAt(0);
        }
        ShowSearch(search);

        static string Count(int count) => count == 1 ? "1 usage" : $"{count} usages";
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
        int at = rows.IndexOf(row);
        if (at < 0 || row.IsExpanded || row.Children.Count == 0)
        {
            return;
        }
        row.IsExpanded = true;
        int index = at + 1;
        foreach (FindRow child in Visible(row.Children))
        {
            rows.Insert(index++, child);
        }
        Redraw(row);
    }

    public void Collapse(FindRow row)
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
        Redraw(row);
    }

    private void ShowSearch(Search search)
    {
        current = search;
        rows.Clear();
        foreach (FindRow row in Visible(search.Roots))
        {
            rows.Add(row);
        }
        Body.IsVisible = true;
        placeholder.IsVisible = false;
        DrawTabs();
        list.SelectedItem = rows.FirstOrDefault(r => r.Usage is not null);
    }

    private static IEnumerable<FindRow> Visible(IEnumerable<FindRow> level)
    {
        foreach (FindRow row in level)
        {
            yield return row;
            if (row.IsExpanded)
            {
                foreach (FindRow child in Visible(row.Children))
                {
                    yield return child;
                }
            }
        }
    }

    private void DrawTabs()
    {
        tabs.Children.Clear();
        foreach (Search search in searches)
        {
            TextBlock name = new() { Text = search.Title, VerticalAlignment = VerticalAlignment.Center };
            PathIcon glyph = new() { Classes = { "small" } };
            glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable("Icon.Close"));
            Button close = new() { Classes = { "tabClose" }, Content = glyph, Margin = new Thickness(6, 0, 0, 0) };
            ToolTip.SetTip(close, "Close");
            close.Click += (_, e) =>
            {
                Close(search);
                e.Handled = true;
            };
            ToggleButton tab = new()
            {
                Classes = { "panelTab" },
                IsChecked = search == current,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, close } },
            };
            tab.Click += (_, _) => ShowSearch(search);
            tabs.Children.Add(tab);
        }
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
            preview.Highlighter = Languages.Highlighters.For(Workspace.Language.ForPath(usage.Path).Name, document);
            preview.Tag = usage.Path;
        }
        TextSnapshot snapshot = preview.Document.Snapshot;
        int start = snapshot.GetOffset(new TextPosition(Math.Min(usage.Line, snapshot.LineCount - 1), usage.Column));
        preview.SetHighlights([new TextRange(start, Math.Min(usage.Length, snapshot.Length - start))], 0);
        preview.GoTo(snapshot.GetPosition(start));
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (Selected is not { } row)
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
                Expand(row);
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
        if (Selected is { Usage: { } usage })
        {
            GoToRequested?.Invoke(this, usage);
        }
        else if (Selected is { } group)
        {
            if (group.IsExpanded)
            {
                Collapse(group);
            }
            else
            {
                Expand(group);
            }
        }
    }

    private void Redraw(FindRow row)
    {
        // The row's chevron changes with it; its template is drawn again.
        int at = rows.IndexOf(row);
        if (at >= 0)
        {
            rows[at] = row;
            list.SelectedIndex = at;
        }
    }

    private Control Row(FindRow row)
    {
        StackPanel panel = new() { Orientation = Orientation.Horizontal, Height = 24, Margin = new Thickness(8 + (row.Depth * 16), 0, 8, 0) };
        if (row.Usage is { } usage)
        {
            TextBlock number = new() { Text = row.Detail, MinWidth = 36, FontFamily = new FontFamily("avares://Passenger.Editor/Fonts#JetBrains Mono"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            number.Bind(TextBlock.ForegroundProperty, number.GetResourceObservable("Passenger.TextMuted"));
            panel.Children.Add(number);
            panel.Children.Add(new TextBlock { Text = row.Label, FontFamily = new FontFamily("avares://Passenger.Editor/Fonts#JetBrains Mono"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }
        PathIcon chevron = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0) };
        chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(row.IsExpanded ? "Icon.ChevronDown" : "Icon.ChevronRight"));
        chevron.PointerPressed += (_, e) =>
        {
            if (row.IsExpanded)
            {
                Collapse(row);
            }
            else
            {
                Expand(row);
            }
            e.Handled = true;
        };
        panel.Children.Add(chevron);
        if (row.Depth == 2 && row.Children.FirstOrDefault()?.Usage is { } first)
        {
            panel.Children.Add(new PathIcon { Data = Explorer.FileIcons.ForFile(first.Path), Foreground = Explorer.FileIcons.BrushForFile(first.Path), Margin = new Thickness(0, 0, 6, 0) });
        }
        panel.Children.Add(new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center, FontWeight = row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal });
        TextBlock count = new() { Text = row.Detail, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        count.Bind(TextBlock.ForegroundProperty, count.GetResourceObservable("Passenger.TextMuted"));
        panel.Children.Add(count);
        return panel;
    }

    private sealed record Search(string Title, IReadOnlyList<FindRow> Roots);
}
