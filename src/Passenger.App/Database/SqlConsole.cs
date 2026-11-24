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

    public void Stop() => running?.Cancel();

    internal void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>What a run's results say in a line: each statement's rows or changes, or the failure that ended it.</summary>
    public static string Summary(IReadOnlyList<SqlResult> results, TimeSpan took) => results switch
    {
        [] => "Nothing ran.",
        [.., { Error: { } error }] => $"Failed after {Plural.Of(results.Count - 1, "statement")}: {error}",
        _ => $"{Plural.Of(results.Count, "statement")} in {Durations.Said(took)}: "
            + string.Join("; ", results.Select(r => r.Sets.Count > 0
                ? string.Join(", ", r.Sets.Select(s => s.More ? $"first {RowLimit.ToString(CultureInfo.InvariantCulture)} rows" : Plural.Of(s.RowCount, "row")))
                : r.Affected >= 0 ? $"{Plural.Of(r.Affected, "row")} changed" : "done")),
    };
}

/// <summary>A console's results under its editor: its connection and Run and Stop over a tab for each result set and one
/// for the run's output, each set in a grid its header sorts.</summary>
public sealed class SqlConsoleView : DockPanel
{
    private readonly ToolDropDown connections = new() { Name = "ConsoleConnection" };
    private readonly TextBlock said = MutedText.Of(string.Empty);
    private readonly ToolTabs.Strip<int> tabs = new() { Name = "ConsoleTabs" };
    private readonly TextBox filter = SearchBox.Of("Filter rows", "Filter rows");
    private readonly CellGrid grid = new() { Name = "ConsoleResults" };
    private readonly SelectableWords output = new() { Name = "ConsoleOutput", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(Gaps.Normal) };
    private readonly ScrollViewer outputPlace;
    private readonly Func<IReadOnlyList<DatabaseSource>> sources;
    private SqlConsole? shown;
    private bool showing;

    public SqlConsoleView(Func<IReadOnlyList<DatabaseSource>> sources, Action<string> request)
    {
        this.sources = sources;
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
        Border bar = ToolBars.OverEditor(new ToolFlow
        {
            Margin = new Thickness(Gaps.Normal, Gaps.Hair),
            Spacing = Gaps.Small,
            Children =
            {
                ToolButtons.Of(AppActions.DatabaseRun, IconKeys.Run, "Run Statement", request),
                ToolButtons.Of(AppActions.DatabaseRunStop, IconKeys.Stop, "Stop Statement", request),
                connections,
                said,
            },
        });
        outputPlace = new ScrollViewer { Content = output };
        tabs.Chosen += (_, _) => ShowTab();
        filter.Name = "ConsoleFilter";
        filter.TextChanged += (_, _) => ShowTab();
        grid.SortRequested += (_, wanted) => grid.Table = wanted is { } by && grid.Table is { } table
            ? TableSort.By(table.Unsorted, by.Column, by.Descending) : grid.Table?.Unsorted;
        Border strip = ToolBars.Top(new ToolFlow { Margin = new Thickness(Gaps.Normal, 0), Spacing = Gaps.Small, Children = { tabs, filter } });
        Children.Add(bar);
        Children.Add(strip);
        Children.Add(new Border { Classes = { StyleClasses.Framed }, Child = new Panel { Children = { grid, outputPlace } } });
    }

    /// <summary>A connection chosen for the console shown.</summary>
    public event EventHandler<(SqlConsole Console, DatabaseSource Source)>? ConnectionChosen;

    public SqlConsole? Console => shown;

    /// <summary>The grid of the result set shown.</summary>
    public CellGrid Grid => grid;

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
            connections.SelectedItem = listed.FirstOrDefault(s => s.Id == shown?.Connection);
            said.Text = ContainerSecrets.InSaid(shown?.Running == true ? "Running…" : shown?.Said ?? string.Empty);
            int before = tabs.Choice;
            tabs.Clear();
            IReadOnlyList<DatabaseRows> sets = [.. (shown?.Results ?? []).SelectMany(r => r.Sets)];
            for (int i = 0; i < sets.Count; i++)
            {
                tabs.Add(i + 1, $"Result {(i + 1).ToString(CultureInfo.InvariantCulture)}", name: $"ConsoleResult{(i + 1).ToString(CultureInfo.InvariantCulture)}");
            }
            tabs.Add(0, "Output", name: "ConsoleOutputTab");
            tabs.Choice = keepTab && tabs.Keys.Contains(before) ? before : sets.Count > 0 && shown?.Results is not [.., { Error: not null }] ? 1 : 0;
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
        grid.IsVisible = chosen > 0;
        outputPlace.IsVisible = chosen == 0;
        filter.IsVisible = chosen > 0;
        DatabaseRows? set = chosen > 0 && chosen <= sets.Count ? sets[chosen - 1] : null;
        Table? table = set is not null && filter.Text?.Trim() is { Length: > 0 } words ? set.Holding(words) : set;
        if (!ReferenceEquals(grid.Table?.Unsorted, table) && !(table is DatabaseRows a && grid.Table?.Unsorted is DatabaseRows b && a.Same(b)))
        {
            grid.Table = table;
        }
    }

    /// <summary>Each statement run, with what it came to and how long it took.</summary>
    private string Output() => shown is null ? string.Empty : ContainerSecrets.InSaid(string.Join("\n\n", shown.Results.Select(r =>
        $"{r.Statement}\n-- " + (r.Error is { } error ? $"failed: {error}"
            : (r.Sets.Count > 0 ? string.Join(", ", r.Sets.Select(s => s.More ? $"first {SqlConsole.RowLimit.ToString(CultureInfo.InvariantCulture)} rows" : Plural.Of(s.RowCount, "row")))
                : r.Affected >= 0 ? $"{Plural.Of(r.Affected, "row")} changed" : "done") + $" in {Durations.Said(r.Took)}"))));
}
