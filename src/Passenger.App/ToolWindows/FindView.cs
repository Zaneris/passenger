using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Passenger.App.ToolWindows;

/// <summary>One usage as the Find window lists it: where it is (lines and columns from 0), its line's text, what it
/// does there, and the project it is in.</summary>
public sealed record Usage(string Path, int Line, int Column, int Length, string LineText, UsageKind Kind, string Project)
{
    /// <summary>Whether it is in generated code: a source generator's, or a file Roslyn takes for one a tool wrote.</summary>
    public bool Generated { get; init; }
}

/// <summary>The usages of a search the Find window shows alone, where the search has both: those in the person's own
/// code, or those in generated code.</summary>
public enum UsageSide
{
    Yours,
    Generated,
}

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
/// import), then project, then file, those in generated code grouped so under a group of their own after them, with the
/// chosen usage's code beside the tree. Each search is a tab to come back to; where it has usages in generated code, a
/// filter for each side with its count shows one side alone. Enter, F4, a double-click or a usage's menu goes to a usage;
/// Right and Left open and close a group.</summary>
public sealed class FindView : DockPanel
{
    /// <summary>The most searches kept as tabs; the oldest goes when another comes.</summary>
    private const int TabLimit = 10;

    /// <summary>The group the usages in generated code are under.</summary>
    public const string GeneratedGroup = "Generated code";

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

    /// <summary>What the search shown went without, above its usages, while it went without anything.</summary>
    private readonly TextBlock note;
    private readonly Func<string, string?> textOf;
    private readonly Func<string, CSharpParseOptions> parseOptions;

    /// <summary>The searches' tabs, shown only while there is one: an empty row with a rule under it says nothing.</summary>
    private readonly Border tabBar;

    /// <summary>The filters for each side, shown while the search shown has usages in generated code.</summary>
    private readonly StackPanel sides;
    private readonly TextBlock yoursCount;
    private readonly TextBlock generatedCount;

    /// <summary>What the list says while the side shown alone has no usages.</summary>
    private readonly TextBlock noneShown;

    private Search? current;
    private UsageSide? alone;

    /// <param name="textOf">A file's text as it is now: the open editor's, else the disk's.</param>
    /// <param name="parseOptions">What a C# file's preview is parsed with: its project's symbols.</param>
    public FindView(Func<string, string?> textOf, Func<string, CSharpParseOptions> parseOptions)
    {
        this.textOf = textOf;
        this.parseOptions = parseOptions;
        YourCode = ToolButtons.Counted(IconKeys.Edit, TokenKeys.Icon, "Show only usages in your code", out yoursCount);
        GeneratedCode = ToolButtons.Counted(IconKeys.Wand, TokenKeys.Icon, "Show only usages in generated code", out generatedCount);
        YourCode.Click += (_, _) => Choose(UsageSide.Yours);
        GeneratedCode.Click += (_, _) => Choose(UsageSide.Generated);
        sides = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsVisible = false };
        sides.Children.AddRange([YourCode, GeneratedCode]);
        DockPanel.SetDock(sides, Dock.Right);
        tabBar = ToolBars.Top(new DockPanel
        {
            Margin = new Thickness(8, 0),
            Children = { sides, new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = tabs } },
        });
        tabBar.IsVisible = false;
        Children.Add(tabBar);
        list = new ListBox
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = rows,
        };
        ReusedRows.Use(list, Row);
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
        ListMenus.Attach(list, () => Selected is not { Usage: { } chosen } ? [] :
        [
            ListMenus.Item("Go to", "F4", () => GoToRequested?.Invoke(this, chosen)),
            ListMenus.Item("Copy Line", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.LineText.Trim()))),
            ListMenus.Item("Copy Path", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Path))),
        ]);
        preview = new CodeEditor { IsReadOnly = true, ShowErrorStripe = false, LineCommentPrefix = null, FontSize = 12.5, Document = new TextDocument() };
        noneShown = new TextBlock { Classes = { StyleClasses.EmptyText }, Text = "No usages in your code", IsVisible = false };
        PaneSplit body = new(new Panel { Children = { list, noneShown } }, preview, "*,1,*");
        placeholder = new TextBlock { Classes = { StyleClasses.EmptyText } };
        ActionTips.SetWords(placeholder, AppActions.FindUsages, keys => $"{ActionTips.Named("Find Usages", keys)} lists a symbol's usages here");
        note = MutedText.Wrapped(string.Empty);
        note.Margin = new Thickness(8, 4);
        note.IsVisible = false;
        DockPanel.SetDock(note, Dock.Top);
        DockPanel shown = new() { IsVisible = false, Children = { note, body } };
        Children.Add(new Panel { Children = { shown, placeholder } });
        Body = shown;
    }

    /// <summary>A usage to go to.</summary>
    public event EventHandler<Usage>? GoToRequested;

    /// <summary>A side was chosen to show alone, or both again, by its filter (<see cref="Alone"/>).</summary>
    public event EventHandler? AloneChanged;

    /// <summary>The filter showing only the usages in the person's own code, with how many the search has.</summary>
    public ToggleButton YourCode { get; }

    /// <summary>The filter showing only the usages in generated code, with how many the search has.</summary>
    public ToggleButton GeneratedCode { get; }

    /// <summary>The side shown alone in a search that has both, null for both; the rows follow at once.</summary>
    public UsageSide? Alone
    {
        get => alone;
        set
        {
            if (alone != value)
            {
                alone = value;
                Filter();
            }
        }
    }

    /// <summary>The visible rows of the search shown.</summary>
    public IReadOnlyList<FindRow> Rows => rows;

    /// <summary>The searches kept, by their tabs' titles, the latest last.</summary>
    public IReadOnlyList<string> Searches => [.. searches.Select(s => s.Title)];

    /// <summary>The search shown, by its title.</summary>
    public string? Shown => current?.Title;

    /// <summary>What the search shown went without, as it says above its usages; null when it went without nothing.</summary>
    public string? Without => current?.Without;

    /// <summary>The code beside the tree: the chosen usage's file, scrolled to it.</summary>
    public CodeEditor Preview => preview;

    public FindRow? Selected
    {
        get => list.SelectedItem as FindRow;
        set => list.SelectedItem = value;
    }

    private DockPanel Body { get; }

    /// <summary>Gives the tree the keys, as a click on a row does: the chosen row's, else the list's.</summary>
    public void FocusRows()
    {
        int index = Math.Max(0, list.SelectedIndex);
        // A row scrolled out of sight has no container to take the focus until it is brought into view.
        list.ScrollIntoView(index);
        list.UpdateLayout();
        list.ContainerFromIndex(index)?.Focus();
    }

    /// <summary>A new search's usages in a tab of their own, every group open and the first usage chosen, with what the
    /// search went without above them (<paramref name="without"/>), when it went without anything. Those in generated
    /// code are grouped alike under <see cref="GeneratedGroup"/>, after the rest.</summary>
    public void Show(string title, IReadOnlyList<Usage> usages, string? without = null)
    {
        List<Usage> yours = [.. usages.Where(u => !u.Generated)];
        List<Usage> generated = [.. usages.Where(u => u.Generated)];
        FindRow? made = generated.Count == 0 ? null : new FindRow(GeneratedGroup, Core.Plural.Of(generated.Count, "usage"), 0, null, Kinds(generated, 1));
        Search search = new(title, Kinds(yours, 0), yours.Count, made, generated.Count, without);
        searches.Add(search);
        if (searches.Count > TabLimit)
        {
            searches.RemoveAt(0);
        }
        ShowSearch(search);
    }

    /// <summary>A text search's matches in a tab of their own, grouped by project, then file, the first chosen.</summary>
    public void ShowMatches(string title, IReadOnlyList<Usage> matches)
    {
        searches.Add(new Search(title, Projects(matches, 0, "match", "matches"), matches.Count, null, 0, null));
        if (searches.Count > TabLimit)
        {
            searches.RemoveAt(0);
        }
        ShowSearch(searches[^1]);
    }

    /// <summary>Usages grouped by what they do, then project, then file, the groups of what they do at a depth.</summary>
    private static List<FindRow> Kinds(IReadOnlyList<Usage> usages, int depth)
    {
        List<FindRow> roots = [];
        foreach ((UsageKind kind, string name) in Groups)
        {
            List<Usage> ofKind = [.. usages.Where(u => u.Kind == kind)];
            if (ofKind.Count == 0)
            {
                continue;
            }
            roots.Add(new FindRow(name, Core.Plural.Of(ofKind.Count, "usage"), depth, null, Projects(ofKind, depth + 1, "usage", null)));
        }
        return roots;
    }

    /// <summary>Usages or matches grouped by project, then file, at a depth, each group counting them by its noun.</summary>
    private static List<FindRow> Projects(IReadOnlyList<Usage> usages, int depth, string noun, string? nouns) =>
        [.. usages.GroupBy(u => u.Project).OrderBy(g => g.Key, StringComparer.Ordinal).Select(project =>
            new FindRow(project.Key, Core.Plural.Of(project.Count(), noun, nouns), depth, null, [.. project.GroupBy(u => u.Path).OrderBy(g => g.Key, StringComparer.Ordinal).Select(file =>
                new FindRow(Path.GetFileName(file.Key), Core.Plural.Of(file.Count(), noun, nouns), depth + 1, null, [.. file.OrderBy(u => u.Line).ThenBy(u => u.Column).Select(u =>
                    new FindRow(u.LineText.Trim(), (u.Line + 1).ToString(), depth + 2, u, []))]))]))];

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

    /// <summary>Opens every group of the search shown, the chosen row kept chosen.</summary>
    public void ExpandAll() => ShowAll(expanded: true);

    /// <summary>Closes every group of the search shown down to those at the top, the first chosen where the chosen row
    /// is under one.</summary>
    public void CollapseAll() => ShowAll(expanded: false);

    private void ShowAll(bool expanded)
    {
        if (current is null)
        {
            return;
        }
        FindRow? chosen = Selected;
        foreach (FindRow row in Every(current.Roots))
        {
            row.IsExpanded = expanded && row.Expands;
        }
        rows.Show([.. TreeRows<FindRow>.Shown(ShownGroups(current), r => r.Children)]);
        list.SelectedItem = chosen is not null && rows.Contains(chosen) ? chosen : rows.FirstOrDefault();

        static IEnumerable<FindRow> Every(IEnumerable<FindRow> level) => level.SelectMany(row => Every(row.Children).Prepend(row));
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
        rows.Show([.. TreeRows<FindRow>.Shown(ShownGroups(search), r => r.Children)]);
        note.Text = search.Without;
        note.IsVisible = search.Without is not null;
        Body.IsVisible = true;
        placeholder.IsVisible = false;
        DrawTabs();
        DrawSides();
        list.SelectedItem = rows.FirstOrDefault(r => r.Usage is not null);
    }

    /// <summary>The groups of a search the rows show: both sides, else the side shown alone where it has both.</summary>
    private IReadOnlyList<FindRow> ShownGroups(Search search) => search.Generated is not { } made ? search.Yours
        : alone == UsageSide.Yours ? search.Yours
        : alone == UsageSide.Generated ? [made]
        : [.. search.Yours, made];

    /// <summary>A side's filter clicked: that side alone, or both again where it was alone already.</summary>
    private void Choose(UsageSide side)
    {
        Alone = alone == side ? null : side;
        AloneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The rows of the side or sides shown now, the chosen row kept where it still shows, else the first usage.</summary>
    private void Filter()
    {
        if (current is null)
        {
            return;
        }
        FindRow? chosen = Selected;
        rows.Show([.. TreeRows<FindRow>.Shown(ShownGroups(current), r => r.Children)]);
        list.SelectedItem = chosen is not null && rows.Contains(chosen) ? chosen : rows.FirstOrDefault(r => r.Usage is not null) ?? rows.FirstOrDefault();
        DrawSides();
    }

    /// <summary>The filters with each side's count, pressed for the side shown alone, while the search shown has both.</summary>
    private void DrawSides()
    {
        sides.IsVisible = current?.Generated is not null;
        yoursCount.Text = (current?.YoursCount ?? 0).ToString(System.Globalization.CultureInfo.CurrentCulture);
        generatedCount.Text = (current?.GeneratedCount ?? 0).ToString(System.Globalization.CultureInfo.CurrentCulture);
        YourCode.IsChecked = sides.IsVisible && alone == UsageSide.Yours;
        GeneratedCode.IsChecked = sides.IsVisible && alone == UsageSide.Generated;
        noneShown.IsVisible = rows.Count == 0 && current is { Generated: not null } && alone == UsageSide.Yours;
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
        DrawSides();
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
            preview.Highlighter = Languages.Highlighters.For(Languages.Language.ForPath(usage.Path), document, parseOptions(usage.Path));
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

    /// <summary>A row for whichever group or usage the list hands its container: a group's chevron, a file's icon, its
    /// name and count; a usage's line number and code.</summary>
    private Control Row()
    {
        TextBlock number = new TextBlock { MinWidth = 36, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        number.Classes.Add(StyleClasses.Muted);
        RowIcon chevron = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0) };
        RowIcon icon = new() { Margin = new Thickness(0, 0, 6, 0) };
        TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock count = new() { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        count.Classes.Add(StyleClasses.Muted);
        StackPanel panel = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { number, chevron, icon, label, count } };
        string? labelFont = null;
        chevron.PointerPressed += (_, e) =>
        {
            if (panel.DataContext is FindRow row)
            {
                Toggle(row);
                e.Handled = true;
            }
        };
        return panel.Showing<FindRow>(row =>
        {
            panel.Margin = new Thickness(8 + (row.Depth * 16), 0, 8, 0);
            bool usage = row.Usage is not null;
            number.IsVisible = usage;
            number.Text = usage ? row.Detail : null;
            chevron.IsVisible = !usage;
            chevron.DataKey = row.IsExpanded ? IconKeys.ChevronDown : IconKeys.ChevronRight;
            FindRow? file = !usage && row.Children.FirstOrDefault()?.Usage is not null ? row.Children[0] : null;
            icon.IsVisible = file is not null;
            icon.Data = file is { Usage: { } first } ? Explorer.FileIcons.ForFile(first.Path) : null;
            icon.Foreground = file is { Usage: { } at } ? Explorer.FileIcons.BrushForFile(at.Path) : null;
            label.Text = row.Label;
            label.FontWeight = !usage && row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal;
            label.Resource(TextBlock.FontFamilyProperty, ref labelFont, usage ? CodeFonts.Key : null);
            if (usage)
            {
                label.FontSize = 12;
            }
            else
            {
                label.ClearValue(TextBlock.FontSizeProperty);
            }
            count.IsVisible = !usage;
            count.Text = usage ? null : row.Detail;
        });
    }

    /// <summary>A search: its usages in the person's own code grouped, and those in generated code under one group of their
    /// own where it has any, with how many each side has.</summary>
    private sealed record Search(string Title, IReadOnlyList<FindRow> Yours, int YoursCount, FindRow? Generated, int GeneratedCount, string? Without)
    {
        /// <summary>Every group at the top, whether shown or not.</summary>
        public IReadOnlyList<FindRow> Roots => Generated is { } made ? [.. Yours, made] : Yours;
    }
}
