using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Containers;
using Passenger.App.Tables;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>A SQL file run against a connection: a console the Database window opens, or any .sql file once a connection is
/// chosen for it, and what its last run came to, each result set and each statement's outcome.</summary>
public sealed class SqlConsole(string path)
{
    /// <summary>The most rows a result set is read to; what is past them is never sent.</summary>
    public const int RowLimit = 500;

    private Working? running;

    public string Path { get; } = path;

    /// <summary>The connection it runs against, by id.</summary>
    public Guid? Connection { get; internal set; }

    /// <summary>What the last run's statements came to, in turn.</summary>
    public IReadOnlyList<SqlResult> Results { get; internal set; } = [];

    public string Said { get; internal set; } = "Run a statement, or the statements chosen, to see what they return here.";

    public bool Running => running is not null;

    /// <summary>Whether a file of a language is run against a connection as a console is: SQL's, MongoDB's shell and Redis's commands.</summary>
    public static bool Speaks(Languages.Language? language) => language is not null && (language == Languages.Language.Sql || language == LanguageOf(DatabaseKind.MongoDb)
        || language == LanguageOf(DatabaseKind.Redis));

    /// <summary>The language a console of a kind of database is written in, and the extension of its file.</summary>
    public static Languages.Language LanguageOf(DatabaseKind kind) => kind switch
    {
        DatabaseKind.MongoDb => Languages.Language.MongoDb,
        DatabaseKind.Redis => Languages.Language.Redis,
        _ => Languages.Language.Sql,
    };

    public static string ExtensionOf(DatabaseKind kind) => kind switch
    {
        DatabaseKind.MongoDb => ".mongodb",
        DatabaseKind.Redis => ".redis",
        _ => ".sql",
    };

    /// <summary>The plan of the statement last explained, and the statement; null until one is.</summary>
    public PlanStep? Plan { get; private set; }

    public string Planned { get; private set; } = string.Empty;

    /// <summary>Whether it runs in manual commit: its statements in a transaction left open until Commit or Rollback.</summary>
    public bool Manual { get; internal set; }

    /// <summary>Its connection held open in manual commit, with the transaction open on it; null in automatic commit and
    /// until its first statement runs.</summary>
    internal SqlSession? Session { get; set; }

    /// <summary>Whether a transaction its statements began is open.</summary>
    public bool InTransaction => Session is { InTransaction: true };

    public event EventHandler? Changed;

    internal void Run(Working working)
    {
        running = working;
        Raise();
    }

    internal void Ran(IReadOnlyList<SqlResult> results, string said)
    {
        running = null;
        Results = results;
        Said = said;
        Raise();
    }

    /// <summary>A statement's plan explained, or why not, which its Plan tab shows.</summary>
    internal void Explained(string statement, PlanStep? plan, string said)
    {
        running = null;
        Planned = statement;
        Plan = plan ?? Plan;
        Said = said;
        Raise();
    }

    public void Stop() => running?.Cancel();

    internal void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>What one statement came to: its result sets' rows, the rows it changed, or why it failed.</summary>
    public static string Outcome(SqlResult result) => result.Error is { } error ? $"failed: {error}"
        : result.Sets.Count > 0 ? string.Join(", ", result.Sets.Select(s => s.More ? $"first {RowLimit.ToString(CultureInfo.InvariantCulture)} rows" : Plural.Of(s.RowCount, "row")))
        : result.Affected >= 0 ? $"{Plural.Of(result.Affected, "row")} changed" : "done";

    /// <summary>What a run's results say in a line: each statement's rows or changes, or the failure that ended it.</summary>
    public static string Summary(IReadOnlyList<SqlResult> results, TimeSpan took) => results switch
    {
        [] => "Nothing ran.",
        [.., { Error: { } error }] => $"Failed after {Plural.Of(results.Count - 1, "statement")}: {error}",
        _ => $"{Plural.Of(results.Count, "statement")} in {Durations.Said(took)}: " + string.Join("; ", results.Select(Outcome)),
    };
}

/// <summary>A row of a plan's tree: its step, how deep it stands, and whether it costs the most alone.</summary>
public sealed class PlanRow(PlanStep step, int depth, bool costliest) : Observable, ITreeRow
{
    private bool expanded = true;

    public PlanStep Step { get; } = step;
    public int Depth { get; } = depth;
    public bool Costliest { get; } = costliest;
    public List<PlanRow> Children { get; } = [];
    public bool Expands => Children.Count > 0;
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
}

/// <summary>A console's results under its editor: its connection, Run and Stop, its commit (automatic, or manual with
/// Commit and Rollback and whether a transaction is open) and its connection's mark over a tab for each result set, one
/// for the run's output, one for its connection's history and one for the plan last explained, each set in a grid its
/// header sorts, the history a list the filter searches whose statement Enter or a double click writes into the console
/// again, the plan a tree of steps with their cost and rows, the costliest alone marked.</summary>
public sealed class SqlConsoleView : DockPanel
{
    /// <summary>The commits a console runs in, as its bar offers them.</summary>
    public static readonly string[] Commits = ["Auto-commit", "Manual commit"];

    private readonly ToolDropDown connections = new() { Name = "ConsoleConnection" };
    private readonly ToolDropDown commits = new() { Name = "ConsoleCommit", ItemsSource = Commits, SelectedIndex = 0 };
    private readonly Border transaction = Tags.Of("transaction open", TokenKeys.Warning);
    private readonly Button commit;
    private readonly Button rollback;
    private readonly Banner marked = new() { Name = "ConsoleMark" };
    private readonly TextBlock said = MutedText.Of(string.Empty);
    private readonly ToolTabs.Strip<int> tabs = new() { Name = "ConsoleTabs" };
    private readonly TextBox filter = SearchBox.Of("Filter rows", "Filter rows");
    private readonly Button export;
    private readonly CellGrid grid = new() { Name = "ConsoleResults" };
    private readonly DocumentTree document = new() { Name = "ConsoleDocument" };
    private readonly PaneSplit results;
    private readonly SelectableWords output = new() { Name = "ConsoleOutput", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(Gaps.Normal) };
    private readonly ScrollViewer outputPlace;
    private readonly ListBox history = new() { Name = "ConsoleHistory" };
    private readonly EmptyState noHistory = new() { Text = "Nothing has run on this connection yet." };
    private readonly Panel historyPlace;
    private readonly TreeList plan = new() { Name = "ConsolePlan", Classes = { StyleClasses.Tree } };
    private readonly TreeRows<PlanRow> planRows = [];
    private PlanStep? planShown;
    private readonly Func<IReadOnlyList<DatabaseSource>> sources;
    private readonly Func<SqlConsole, IReadOnlyList<SqlRan>> ran;
    private SqlConsole? shown;
    private bool showing;

    /// <summary>The tabs past the result sets': the run's output and the connection's history.</summary>
    public const int OutputTab = 0;

    public const int HistoryTab = -1;

    public const int PlanTab = -2;

    /// <param name="ran">What ran on a console's connection, newest first.</param>
    public SqlConsoleView(Func<IReadOnlyList<DatabaseSource>> sources, Func<SqlConsole, IReadOnlyList<SqlRan>> ran, Action<string> request)
    {
        this.sources = sources;
        this.ran = ran;
        ReusedRows.Use(history, HistoryRow);
        ListKeys.Act(history, new ListActs
        {
            Open = () =>
            {
                if (shown is not null && history.SelectedItem is SqlRan chosen)
                {
                    HistoryChosen?.Invoke(this, (shown, chosen));
                }
            },
        });
        historyPlace = new Panel { Children = { history, noHistory } };
        plan.ItemsSource = planRows;
        ReusedRows.Use(plan, PlanRowControl);
        TreeKeys.Attach<PlanRow>(plan, Toggle);
        said.Name = "ConsoleSaid";
        said.VerticalAlignment = VerticalAlignment.Center;
        said.TextTrimming = TextTrimming.CharacterEllipsis;
        ReusedRows.Use(connections, () =>
        {
            TextBlock text = new();
            return text.Showing<DatabaseSource>(row => text.Text = ContainerSecrets.InSaid(row.Name));
        });
        connections.SelectionChanged += (_, _) =>
        {
            if (!showing && shown is not null && connections.SelectedItem is DatabaseSource chosen)
            {
                ConnectionChosen?.Invoke(this, (shown, chosen));
            }
        };
        commits.SelectionChanged += (_, _) =>
        {
            if (!showing && shown is not null && commits.SelectedIndex >= 0)
            {
                CommitChosen?.Invoke(this, (shown, commits.SelectedIndex == 1));
            }
        };
        transaction.Name = "ConsoleTransaction";
        commit = ToolButtons.Of(AppActions.DatabaseCommit, IconKeys.Commit, "Commit", request);
        rollback = ToolButtons.Of(AppActions.DatabaseRollback, IconKeys.Rollback, "Roll Back", request);
        Border bar = ToolBars.OverEditor(new ToolFlow
        {
            Margin = new Thickness(Gaps.Normal, Gaps.Hair),
            Spacing = Gaps.Small,
            Children =
            {
                ToolButtons.Of(AppActions.DatabaseRun, IconKeys.Run, "Run Statement", request),
                ToolButtons.Of(AppActions.DatabaseRunStop, IconKeys.Stop, "Stop Statement", request),
                ToolButtons.Of(AppActions.DatabaseExplain, IconKeys.DatabasePlan, "Explain Plan", request),
                ToolButtons.Of(AppActions.DatabaseHistory, IconKeys.History, "Query History", request),
                connections,
                commits,
                commit,
                rollback,
                transaction,
                said,
            },
        });
        outputPlace = new ScrollViewer { Content = output };
        tabs.Chosen += (_, _) => ShowTab();
        filter.Name = "ConsoleFilter";
        filter.TextChanged += (_, _) => ShowTab();
        grid.SortRequested += (_, wanted) => grid.Table = wanted is { } by && grid.Table is { } table
            ? TableSort.By(table.Unsorted, by.Column, by.Descending) : grid.Table?.Unsorted;
        export = ToolButtons.Of(AppActions.DatabaseExport, IconKeys.Export, "Export Data…", request);
        export.Name = "ConsoleExport";
        Border strip = ToolBars.Top(new ToolFlow { Margin = new Thickness(Gaps.Normal, 0), Spacing = Gaps.Small, Children = { tabs, filter, export } });
        Children.Add(bar);
        Children.Add(marked);
        Children.Add(strip);
        // A document database's results: the grid of their fields, and the chosen document whole beside it.
        results = new PaneSplit(grid, null, "3*,1,2*");
        grid.CaretMoved += (_, _) => FollowDocument();
        grid.TableChanged += (_, _) => FollowDocument();
        Children.Add(new Border { Classes = { StyleClasses.Framed }, Child = new Panel { Children = { results, outputPlace, historyPlace, plan } } });
    }

    /// <summary>The plan's rows shown, for a test to read.</summary>
    public IReadOnlyList<PlanRow> PlanShown => planRows;

    /// <summary>The document of the row chosen, whole, where the set shown is a document database's.</summary>
    private void FollowDocument() => document.Show(grid.Table is { RowCount: > 0 } table && table.Unsorted is DatabaseRows { Documents.Length: > 0 } set
        && grid.Caret.Row < table.RowCount && table.FileRow(grid.Caret.Row) is var at && at < set.Documents.Length ? set.Documents[at] : null);

    private void Toggle(PlanRow row)
    {
        if (!row.Expands)
        {
            return;
        }
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded)
        {
            planRows.Open(row, TreeRows<PlanRow>.Shown(row.Children, r => r.IsExpanded ? r.Children : null));
        }
        else
        {
            planRows.Close(row);
        }
    }

    /// <summary>A plan's rows, every step open, the one that costs the most alone marked.</summary>
    private void ShowPlan(PlanStep step)
    {
        PlanStep? costliest = SqlPlans.Costliest(step);
        PlanRow Row(PlanStep at, int depth)
        {
            PlanRow row = new(at, depth, ReferenceEquals(at, costliest) && at.Own > 0);
            row.Children.AddRange(at.Steps.Select(s => Row(s, depth + 1)));
            return row;
        }
        planRows.Show([.. TreeRows<PlanRow>.Shown([Row(step, 0)], r => r.IsExpanded ? r.Children : null)]);
    }

    private Control PlanRowControl()
    {
        RowGlyphs glyphs = new() { IconKey = IconKeys.DatabasePlan };
        TextBlock operation = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock detail = MutedText.Of(string.Empty);
        detail.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        detail.VerticalAlignment = VerticalAlignment.Center;
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        Border costliest = Tags.Of("costliest", TokenKeys.Warning);
        costliest.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        TextBlock figures = MutedText.Of(string.Empty);
        figures.Margin = new Thickness(Gaps.Wide, 0, 0, 0);
        figures.VerticalAlignment = VerticalAlignment.Center;
        StackPanel words = new() { Orientation = Orientation.Horizontal, Children = { operation, costliest, detail } };
        DockPanel line = new() { Height = ListRow.Height, Children = { glyphs, figures, words } };
        SetDock(glyphs, Avalonia.Controls.Dock.Left);
        SetDock(figures, Avalonia.Controls.Dock.Right);
        glyphs.ChevronPressed += (_, e) =>
        {
            if (line.DataContext is PlanRow row)
            {
                Toggle(row);
                e.Handled = true;
            }
        };
        return line.Showing<PlanRow>(row =>
        {
            line.Margin = new Thickness(RowExpander.Column * row.Depth, 0, Gaps.Normal, 0);
            glyphs.HasChevron = row.Expands;
            glyphs.Turned = row.IsExpanded;
            operation.Text = ContainerSecrets.InSaid(row.Step.Operation);
            operation.Classes.Set(StyleClasses.Strong, row.Costliest);
            costliest.IsVisible = row.Costliest;
            detail.Text = ContainerSecrets.InSaid(row.Step.Detail);
            figures.Text = string.Join(" · ", new[]
            {
                row.Step.Cost is { } cost ? $"cost {cost.ToString("0.##", CultureInfo.InvariantCulture)}" + (row.Step.Own is { } own && row.Step.Steps.Count > 0 ? $" ({own.ToString("0.##", CultureInfo.InvariantCulture)} alone)" : string.Empty) : null,
                row.Step.Rows is { } rows ? Plural.Of((long)Math.Round(rows), "row") : null,
            }.OfType<string>());
        });
    }

    /// <summary>A statement of the history chosen, to write into the console again.</summary>
    public event EventHandler<(SqlConsole Console, SqlRan Ran)>? HistoryChosen;

    /// <summary>Shows the console's history, its filter's box given the keys.</summary>
    public void ShowHistory()
    {
        tabs.Choice = HistoryTab;
        ShowTab();
        filter.Focus();
    }

    /// <summary>The history's rows shown, newest first, for a test to read.</summary>
    public IReadOnlyList<SqlRan> HistoryShown => [.. history.Items.OfType<SqlRan>()];

    private static Control HistoryRow()
    {
        TextBlock statement = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock outcome = MutedText.Of(string.Empty);
        outcome.Margin = new Thickness(Gaps.Wide, 0, 0, 0);
        outcome.VerticalAlignment = VerticalAlignment.Center;
        DockPanel line = new() { Height = ListRow.Height, Margin = new Thickness(Gaps.Normal, 0), Children = { outcome, statement } };
        SetDock(outcome, Avalonia.Controls.Dock.Right);
        return line.Showing<SqlRan>(row =>
        {
            string first = row.Statement.Trim().Split('\n')[0].Trim();
            statement.Text = ContainerSecrets.InSaid(first + (row.Statement.Trim().Contains('\n', StringComparison.Ordinal) ? " …" : string.Empty));
            outcome.Text = ContainerSecrets.InSaid($"{row.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · {row.Outcome}");
        });
    }

    /// <summary>A connection chosen for the console shown.</summary>
    public event EventHandler<(SqlConsole Console, DatabaseSource Source)>? ConnectionChosen;

    /// <summary>A commit chosen for the console shown: true for manual.</summary>
    public event EventHandler<(SqlConsole Console, bool Manual)>? CommitChosen;

    /// <summary>Shows the console again as its connections changed (one renamed or marked).</summary>
    public void Refresh() => Follow(keepTab: true);

    public SqlConsole? Console => shown;

    /// <summary>The grid of the result set shown.</summary>
    public CellGrid Grid => grid;

    /// <summary>The result set whose tab is chosen, with the statement's run that read it and its tab's title; null on another tab.</summary>
    public (SqlResult Result, DatabaseRows Rows, string Title)? ChosenResult
    {
        get
        {
            (SqlResult Result, DatabaseRows Rows)[] sets = [.. (shown?.Results ?? []).SelectMany(r => r.Sets.Select(s => (r, s)))];
            int chosen = tabs.Choice;
            return chosen > 0 && chosen <= sets.Length ? (sets[chosen - 1].Result, sets[chosen - 1].Rows, $"Result {chosen.ToString(CultureInfo.InvariantCulture)}") : null;
        }
    }

    /// <summary>Shows a console, following it as it runs; null lets go of the one shown.</summary>
    public void Show(SqlConsole? console)
    {
        if (shown is not null)
        {
            shown.Changed -= OnChanged;
        }
        shown = console;
        if (console is not null)
        {
            console.Changed += OnChanged;
        }
        // The plan a console shown again had is its own, its tab chosen only as a statement is explained.
        planShown = console?.Plan;
        if (planShown is not null)
        {
            ShowPlan(planShown);
        }
        else
        {
            planRows.Clear();
        }
        Follow(keepTab: false);
    }

    private void OnChanged(object? sender, EventArgs e) => Follow(keepTab: false);

    private void Follow(bool keepTab)
    {
        showing = true;
        try
        {
            IReadOnlyList<DatabaseSource> listed = sources();
            connections.ItemsSource = listed;
            DatabaseSource? source = listed.FirstOrDefault(s => s.Id == shown?.Connection);
            connections.SelectedItem = source;
            bool manual = shown?.Manual == true;
            commits.SelectedIndex = manual ? 1 : 0;
            commit.IsVisible = manual;
            rollback.IsVisible = manual;
            string? ended = shown?.Running == true ? Refusals.SqlRunning : shown?.InTransaction != true ? Refusals.NoTransaction : null;
            ToolButtons.Grey(commit, ended, ActionTips.Of(commit));
            ToolButtons.Grey(rollback, ended, ActionTips.Of(rollback));
            transaction.IsVisible = shown?.InTransaction == true;
            marked.Say(source?.Mark switch
            {
                DatabaseMark.Production => $"{ContainerSecrets.InSaid(source.Name)} is marked production: a statement that changes data or schema asks before it runs",
                DatabaseMark.ReadOnly => Refusals.ReadOnlyConnection(ContainerSecrets.InSaid(source.Name)),
                _ => null,
            });
            said.Text = ContainerSecrets.InSaid(shown?.Running == true ? "Running…" : shown?.Said ?? string.Empty);
            int before = tabs.Choice;
            tabs.Clear();
            IReadOnlyList<DatabaseRows> sets = [.. (shown?.Results ?? []).SelectMany(r => r.Sets)];
            for (int i = 0; i < sets.Count; i++)
            {
                tabs.Add(i + 1, $"Result {(i + 1).ToString(CultureInfo.InvariantCulture)}", name: $"ConsoleResult{(i + 1).ToString(CultureInfo.InvariantCulture)}");
            }
            tabs.Add(OutputTab, "Output", name: "ConsoleOutputTab");
            tabs.Add(HistoryTab, "History", name: "ConsoleHistoryTab");
            if (shown?.Plan is not null)
            {
                tabs.Add(PlanTab, "Plan", name: "ConsolePlanTab");
            }
            bool planned = shown?.Plan is not null && !ReferenceEquals(shown.Plan, planShown);
            if (planned)
            {
                planShown = shown!.Plan;
                ShowPlan(planShown!);
            }
            tabs.Choice = planned ? PlanTab : keepTab && tabs.Keys.Contains(before) ? before
                : sets.Count > 0 && shown?.Results is not [.., { Error: not null }] ? 1 : OutputTab;
            output.Text = Output();
        }
        finally
        {
            showing = false;
        }
        ShowTab();
    }

    private void ShowTab()
    {
        IReadOnlyList<DatabaseRows> sets = [.. (shown?.Results ?? []).SelectMany(r => r.Sets)];
        int chosen = tabs.Choice;
        results.IsVisible = chosen > 0;
        outputPlace.IsVisible = chosen == OutputTab;
        historyPlace.IsVisible = chosen == HistoryTab;
        plan.IsVisible = chosen == PlanTab;
        filter.IsVisible = chosen > 0 || chosen == HistoryTab;
        export.IsVisible = chosen > 0;
        filter.PlaceholderText = chosen == HistoryTab ? "Filter history" : "Filter rows";
        if (chosen == HistoryTab)
        {
            string sought = filter.Text?.Trim() ?? string.Empty;
            IReadOnlyList<SqlRan> all = shown is null ? [] : ran(shown);
            SqlRan[] listed = [.. all.Where(r => sought.Length == 0 || r.Statement.Contains(sought, StringComparison.OrdinalIgnoreCase))];
            if (!history.Items.OfType<SqlRan>().SequenceEqual(listed))
            {
                history.ItemsSource = listed;
            }
            noHistory.Text = all.Count == 0 ? "Nothing has run on this connection yet." : "No statement run here holds these words.";
            noHistory.IsVisible = listed.Length == 0;
        }
        DatabaseRows? set = chosen > 0 && chosen <= sets.Count ? sets[chosen - 1] : null;
        results.Second = set is { Documents.Length: > 0 } ? document : null;
        Table? table = set is not null && filter.Text?.Trim() is { Length: > 0 } words ? set.Holding(words) : set;
        if (!ReferenceEquals(grid.Table?.Unsorted, table) && !(table is DatabaseRows a && grid.Table?.Unsorted is DatabaseRows b && a.Same(b)))
        {
            grid.Table = table;
        }
    }

    /// <summary>Each statement run, with what it came to and how long it took.</summary>
    private string Output() => shown is null ? string.Empty : ContainerSecrets.InSaid(string.Join("\n\n", shown.Results.Select(r =>
        $"{r.Statement}\n-- {SqlConsole.Outcome(r)}" + (r.Error is null ? $" in {Durations.Said(r.Took)}" : string.Empty))));
}
