using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.App.Testing;
using Passenger.Core;

namespace Passenger.App.ToolWindows;

/// <summary>A row of the Coverage window's tree: a project, namespace, type or member at its depth, open or not, the rows
/// under it as the filter and the order leave them.</summary>
public sealed class CoverageRow(CoverageNode node, int depth) : Observable, ITreeRow
{
    private bool expanded;

    public CoverageNode Node { get; } = node;

    public int Depth { get; } = depth;

    /// <summary>The rows under it, as listed now.</summary>
    internal List<CoverageRow> Below { get; } = [];

    public bool Expands => Below.Count > 0;

    public bool IsExpanded
    {
        get => expanded;
        internal set => Set(ref expanded, value);
    }
}

/// <summary>The Coverage window: what the last run with coverage measured, by project, namespace, type and member, each with
/// the share of its lines the tests ran and the share of its branches they took (how many of how many in its tip), sorted by
/// any column (a title's click, again for the other way) and narrowed by the filter's words; a row's member or type is gone
/// to by Enter, F4 or a double-click. Its side bar runs every test with coverage, exports the coverage as Cobertura and
/// clears it. Its summary says what the coverage came to and what it left out (tests run where it is not measured).</summary>
public sealed class CoverageView : DockPanel
{
    private const double LinesWidth = 52;
    private const double BranchesWidth = 68;

    private readonly TreeRows<CoverageRow> rows = [];
    private readonly TreeList list;
    private readonly EmptyState placeholder;
    private readonly TextBox filter = SearchBox.Finding("Filter coverage");
    private readonly TextBlock summary = MutedText.Caption(string.Empty);
    private readonly ColumnTitles titles;
    private readonly Panel table;
    private List<CoverageRow> top = [];
    private CoverageReport? report;
    private IReadOnlyList<string> unmeasured = [];

    /// <summary>The column the rows are ordered by (0 the name), and whether the most comes first.</summary>
    private (int Column, bool Descending) order = (0, false);

    public CoverageView()
    {
        filter.TextChanged += (_, _) => List();
        filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list!.ItemCount > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex);
                ListKeys.Hold(list, chooseFirst: true);
                e.Handled = true;
            }
        };
        // Its own wrapped line under the filter, as the window is often narrow beside the editor.
        summary.TextWrapping = TextWrapping.Wrap;
        summary.Margin = new Thickness(Gaps.Wide, Gaps.Small, Gaps.Wide, Gaps.Small);
        DockPanel.SetDock(summary, Dock.Top);
        filter.Margin = new Thickness(Gaps.Small, 0, Gaps.Tight, 0);
        Children.Add(ToolBars.Side(
            ToolButtons.Of(AppActions.CoverAllTests, IconKeys.Coverage, "Run All Tests with Coverage", Request),
            ToolButtons.Of(AppActions.ExportCoverage, IconKeys.Export, "Export Coverage as Cobertura…", Request),
            ToolButtons.Of(AppActions.ClearCoverage, IconKeys.Clear, "Clear Coverage", Request)));
        Children.Add(ToolBars.Top(filter));
        Children.Add(summary);
        titles = ColumnTitles.OfNumbers(("Name", 0), ("Lines", LinesWidth), ("Branches", BranchesWidth)).Sorting(Sort);
        list = new TreeList { Classes = { StyleClasses.Tree }, ItemsSource = rows };
        ReusedRows.Use(list, Row);
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) },
        });
        TreeKeys.Attach<CoverageRow>(list, Toggle, row =>
        {
            GoToRequested?.Invoke(this, row.Node);
            return row.Node.Path is not null;
        });
        ListMenus.Attach(list, () => list.SelectedItem is not CoverageRow chosen ? [] :
        [
            ListMenus.Source(() => GoToRequested?.Invoke(this, chosen.Node), chosen.Node.Path is null ? Refusals.CoverageUnplaced : null),
            ListMenus.Item("Copy Name", () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Node.Name))),
        ]);
        placeholder = new EmptyState();
        table = new DockPanel { Children = { titles, list } };
        Children.Add(new Panel { Children = { table, placeholder } });
        Show(null, []);
    }

    /// <summary>A side bar's button pressed: the app action it runs.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>A row's member or type to go to.</summary>
    public event EventHandler<CoverageNode>? GoToRequested;

    private void Request(string action) => ActionRequested?.Invoke(this, action);

    /// <summary>The rows listed, as the filter, the order and the rows opened leave them.</summary>
    public IReadOnlyList<CoverageRow> Rows => rows;

    /// <summary>What the window says over the rows: what the coverage came to, and what it left out.</summary>
    public string Summary => summary.Text ?? string.Empty;

    /// <summary>What the window says in place of rows; null while it lists some.</summary>
    public string? Said => placeholder.IsVisible ? placeholder.Text : null;

    /// <summary>The filter's words.</summary>
    public string Filter
    {
        get => filter.Text ?? string.Empty;
        set => filter.Text = value;
    }

    /// <summary>Gives the filter the keys, its text chosen: Find's keys while the window has them.</summary>
    public void FocusFilter()
    {
        filter.Focus();
        filter.SelectAll();
    }

    /// <summary>Shows what a run with coverage measured, and what it left out; none says how to measure it.</summary>
    public void Show(CoverageReport? measured, IReadOnlyList<string> leftOut)
    {
        UiThread.Check();
        report = measured;
        unmeasured = leftOut;
        string left = leftOut.Count == 0 ? string.Empty : $" Not measured: {string.Join("; ", leftOut)}.";
        summary.Text = measured is null ? string.Empty
            : measured.Files.Count == 0 ? $"No coverage was measured.{left}"
            : $"{measured.Counts.LinesSaid}, {measured.Counts.BranchesSaid} run.{left}";
        summary.IsVisible = summary.Text.Length > 0;
        List();
    }

    /// <summary>Orders the rows by a column, as its title's click does, the least first (a number's the least covered): again
    /// by the same one the other way.</summary>
    public void Sort(int column)
    {
        order = (column, order.Column == column && !order.Descending);
        List();
    }

    /// <summary>Opens or closes a row, as a click on its chevron does.</summary>
    public void Toggle(CoverageRow row)
    {
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            rows.Close(row);
        }
        else if (row.Expands)
        {
            row.IsExpanded = true;
            rows.Open(row, TreeRows<CoverageRow>.Shown(row.Below, r => r.Below));
        }
    }

    /// <summary>Every row opened.</summary>
    public void ExpandAll()
    {
        foreach (CoverageRow row in All(top))
        {
            row.IsExpanded = row.Expands;
        }
        Relist();
    }

    /// <summary>Every row closed.</summary>
    public void CollapseAll()
    {
        foreach (CoverageRow row in All(top))
        {
            row.IsExpanded = false;
        }
        Relist();
    }

    /// <summary>The rows the filter leaves in order, those open before open again, a project alone opened, and a filter's
    /// match opened down to it.</summary>
    private void List()
    {
        string[] words = Filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        HashSet<CoverageNode> open = new(All(top).Where(r => r.IsExpanded).Select(r => r.Node), ReferenceEqualityComparer.Instance);
        top = [.. Made(report?.Projects ?? [], 0, words, open)];
        if (top.Count == 1)
        {
            top[0].IsExpanded = top[0].Expands;
        }
        placeholder.IsVisible = top.Count == 0;
        table.IsVisible = top.Count > 0;
        if (report is null)
        {
            placeholder.SayWithKeys(keys => $"{ActionTips.Named("Run All Tests with Coverage", keys(AppActions.CoverAllTests))} shows here which lines and branches the tests ran");
        }
        else
        {
            placeholder.Text = report.Files.Count == 0 ? "The run measured no code of the folder's own" : "Nothing measured fits the filter";
        }
        Relist();
    }

    /// <summary>A level's rows in order, those the filter leaves: a row whose name or a row's under it holds every word, opened
    /// while words narrow it.</summary>
    private List<CoverageRow> Made(IReadOnlyList<CoverageNode> nodes, int depth, string[] words, IReadOnlySet<CoverageNode> open)
    {
        List<CoverageRow> made = [];
        foreach (CoverageNode node in Ordered(nodes))
        {
            CoverageRow row = new(node, depth);
            bool fits = words.All(w => node.FullName.Contains(w, StringComparison.OrdinalIgnoreCase));
            row.Below.AddRange(Made(node.Children, depth + 1, fits ? [] : words, open));
            if (fits || row.Below.Count > 0)
            {
                row.IsExpanded = row.Below.Count > 0 && ((words.Length > 0 && !fits) || open.Contains(node));
                made.Add(row);
            }
        }
        return made;
    }

    private IEnumerable<CoverageNode> Ordered(IReadOnlyList<CoverageNode> nodes)
    {
        Func<CoverageNode, double> key = order.Column switch
        {
            1 => n => n.Counts.LineRate ?? -1,
            2 => n => n.Counts.BranchRate ?? -1,
            _ => _ => 0,
        };
        IOrderedEnumerable<CoverageNode> sorted = order.Column == 0
            ? order.Descending ? nodes.OrderByDescending(n => n.Name, StringComparer.OrdinalIgnoreCase) : nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            : order.Descending ? nodes.OrderByDescending(key) : nodes.OrderBy(key);
        return sorted.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The rows listed again from the top, each open row's under it.</summary>
    private void Relist()
    {
        object? chosen = list.SelectedItem is CoverageRow was ? was.Node : null;
        rows.Clear();
        rows.InsertRange(0, TreeRows<CoverageRow>.Shown(top, r => r.Below));
        list.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Node, chosen));
    }

    private static IEnumerable<CoverageRow> All(IEnumerable<CoverageRow> level) => level.SelectMany(r => All(r.Below).Prepend(r));

    /// <summary>A row for whichever node the list hands its container: its chevron and name, cut short where the window is
    /// narrow, its lines' share and its branches' share, how many of how many in its tip.</summary>
    private Control Row()
    {
        RowExpander chevron = RowExpander.Of<CoverageRow>(Toggle);
        DockPanel.SetDock(chevron, Dock.Left);
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel named = new() { Children = { chevron, name } };
        TextBlock[] numbers = [Number(), Number()];
        Grid row = titles.Row([named, .. numbers]);
        return row.Showing<CoverageRow>(shown =>
        {
            CoverageNode node = shown.Node;
            chevron.Expands = shown.Expands;
            chevron.IsOpen = shown.IsExpanded;
            name.Text = node.Name;
            name.Classes.Set(StyleClasses.Strong, node.Kind == CoverageNodeKind.Project);
            CoverageCounts counts = node.Counts;
            ToolTip.SetTip(row, $"{node.FullName}{(node.Path is { } path ? $"\n{Path.GetFileName(path)}:{node.Line}" : string.Empty)}\n{counts.LinesSaid}, {counts.BranchesSaid}");
            named.Margin = new Thickness(shown.Depth * Gaps.Indent, 0, 0, 0);
            Put(numbers[0], CoverageCounts.Percent(counts.LineRate), counts.LinesRun == 0);
            Put(numbers[1], CoverageCounts.Percent(counts.BranchRate), counts.Branches == 0);
        });

        static TextBlock Number() => new() { Classes = { StyleClasses.Code }, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

        static void Put(TextBlock cell, string text, bool none)
        {
            cell.Text = text;
            cell.Classes.Set(StyleClasses.Muted, none);
        }
    }
}
