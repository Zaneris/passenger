using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Passenger.App;
using Passenger.App.Database;
using Passenger.App.Tables;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.TestTables;

namespace Passenger.Tests;

/// <summary>The Database window over a folder's SQLite database: connections found without connecting, the explorer read as
/// it is opened, a table's data among the editors, and a connection made by hand kept with its password in the system's
/// credential store (the test run's own, in memory).</summary>
public sealed class DatabaseTests
{
    [AvaloniaFact]
    public void The_database_window_finds_explores_and_opens_a_folders_databases()
    {
        string folder = TestFolders.Create("database");
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
        TestFolders.Write(folder, "appsettings.json", """
            {"ConnectionStrings":{
                "Shop":"Data Source=shop.db",
                "Missing":"Data Source=missing.db",
                "Cache":"localhost:6379",
                "Orders":"Host=localhost;Database=orders;Username=app;Password=database-test-secret"
            }}
            """);
        Sql(folder, """
            CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Note TEXT);
            CREATE INDEX ProductNames ON Products(Name);
            CREATE TABLE Orders (Id INTEGER PRIMARY KEY, Product INTEGER REFERENCES Products(Id));
            CREATE VIEW Cheap AS SELECT * FROM Products WHERE Id < 3;
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<105)
            INSERT INTO Products SELECT x, 'Product '||x, NULL FROM n;
            """);
        string settings = TestFolders.Create("database-settings-");
        MainWindow window = Open(folder, settings);
        try
        {
            Flow.Step("the folder's connections are found and none is opened, nor a missing SQLite file made", () =>
            {
                window.ShowToolWindow(ToolWindowIds.Database);
                Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy, () => window.Database.Said);
                Assert.Equal(["Shop", "Missing", "Orders"], window.Database.Sources.Select(s => s.Name));
                Assert.Equal([DatabaseKind.Sqlite, DatabaseKind.Sqlite, DatabaseKind.Postgres], window.Database.Sources.Select(s => s.Kind));
                Assert.DoesNotContain(window.Database.Sources, s => window.Database.IsConnected(s));
                Assert.False(File.Exists(Path.Combine(folder, "missing.db")), "Finding the connections made a SQLite file the folder names");
                Assert.All(Explorer(window).Rows, r => Assert.DoesNotContain("database-test-secret", r.Title + r.Detail, StringComparison.Ordinal));
            });
            Flow.Step("opening a connection reads its tables and views, and a table its columns keys and indexes", () =>
            {
                Open(window, "Shop");
                Assert.Equal(["Tables", "Views"], Under(window, "Shop").Select(r => r.Title));
                Open(window, "Tables");
                Assert.Equal(["Orders", "Products"], Explorer(window).Rows.Where(r => r.Kind == DatabaseNodeKind.Table).Select(r => r.Title));
                Open(window, "Products");
                Open(window, "Columns");
                DatabaseNode id = Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Column && r.Title == "Id");
                Assert.Contains("primary key", id.Detail, StringComparison.Ordinal);
                Open(window, "Indexes");
                Assert.Contains(Explorer(window).Rows, r => r.Kind == DatabaseNodeKind.Index && r.Title.Contains("ProductNames", StringComparison.Ordinal));
                Assert.True(window.Database.IsConnected(window.Database.Sources[0]));
            });
            Flow.Step("a missing SQLite database fails to open, said and logged, and is never made", () =>
            {
                DatabaseNode missing = Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Missing");
                Explorer(window).Choose(missing);
                Ui.Await(Explorer(window).ToggleAsync(missing), "Missing to fail to open");
                Assert.False(missing.IsExpanded);
                Assert.Contains("Database operation failed", LogsTests.WholeIdeLog(), StringComparison.Ordinal);
                Assert.Equal(window.Database.Said, Explorer(window).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DatabaseStatus").Text);
                Assert.False(File.Exists(Path.Combine(folder, "missing.db")));
            });
            Flow.Step("typing in the tree filters the rows read to those whose names hold the words", () =>
            {
                ListBox tree = Explorer(window).GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "DatabaseTree");
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Title == "Products"));
                Ui.Settle();
                Ui.Click(tree.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext == tree.SelectedItem));
                window.KeyTextInput("ord");
                Assert.Equal("ord", Explorer(window).GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "DatabaseSearch").Text);
                Assert.Contains(Explorer(window).Rows, r => r.Title == "Orders");
                Assert.DoesNotContain(Explorer(window).Rows, r => r.Title == "Products");
                Ui.Press(window, "ESCAPE");
                Assert.Contains(Explorer(window).Rows, r => r.Title == "Products");
            });
            Flow.Step("a table's data opens among the editors, paged, sorted by its header and filtered", () =>
            {
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Title == "Products"));
                Assert.True(window.Perform(AppActions.DatabaseOpenTable), "Open Table Data did not run");
                Ui.WaitFor(() => Data(window)?.Rows?.RowCount == 100, "the first page of Products among the editors");
                Assert.DoesNotContain(window.EditorArea.Active, window.EditorArea.Documents);
                Assert.True(Data(window)!.Rows!.More);
                Ui.Click(Tab<Button>(window, "DatabaseNextPage"));
                Ui.WaitFor(() => Data(window)?.Rows is { RowCount: 5, Offset: 100 }, "the second page of Products");
                Assert.True(window.Perform(AppActions.DatabaseOpenTable));
                Ui.Settle();
                Assert.Single(window.EditorArea.Groups.SelectMany(g => g.Tabs), t => t.Document.DatabaseTable is not null);
                Tab<ComboBox>(window, "DatabaseFilterColumn").SelectedItem = "Name";
                Tab<TextBox>(window, "DatabaseFilterValue").Text = "product 10";
                Ui.Click(Tab<Button>(window, "DatabaseApplyFilter"));
                Ui.WaitFor(() => Data(window)?.Rows?.RowCount == 7, "Products whose names hold product 10");
                Tab<TextBox>(window, "DatabaseFilterValue").Text = "%";
                Ui.Click(Tab<Button>(window, "DatabaseApplyFilter"));
                Ui.WaitFor(() => Data(window)?.Rows?.RowCount == 0, "a filter's % taken as itself");
                Ui.Click(Tab<Button>(window, "DatabaseClearFilter"));
                Ui.WaitFor(() => Data(window)?.Rows?.RowCount == 100, "Products unfiltered");
            });
            Flow.Step("a cell is written only once asked, and a row changed since it was read is refused", () =>
            {
                CellGrid grid = Tab<CellGrid>(window, "DatabaseTabResults");
                grid.Choose((0, 1), (0, 1));
                Assert.True(grid.BeginWriting());
                grid.Writing!.Text = "New product";
                Ui.Press(window, "ENTER");
                Ui.ClickButton(Question(window), "Write");
                Ui.WaitFor(() => Data(window)?.Rows?.Cell(0, 1) == "New product", "the written cell read again");
                Sql(folder, "UPDATE Products SET Name='Changed elsewhere' WHERE Id=1");
                grid.Choose((0, 1), (0, 1));
                Assert.True(grid.BeginWriting());
                grid.Writing!.Text = "Must not overwrite";
                Ui.Press(window, "ENTER");
                Ui.ClickButton(Question(window), "Write");
                Ui.WaitFor(() => Tab<TextBlock>(window, "DatabaseTabStatus").Text?.Contains("row changed", StringComparison.Ordinal) == true, "the stale row refused");
                Assert.Equal("Changed elsewhere", Scalar(folder, "SELECT Name FROM Products WHERE Id=1"));
            });
            Flow.Step("a connection's console runs the statement at the caret on Run Statement's keys, its rows in a grid under it", () =>
            {
                string console = Path.Combine(Passenger.App.State.ProjectState.FolderOf(settings, folder), "consoles", "Shop.sql");
                Directory.CreateDirectory(Path.GetDirectoryName(console)!);
                File.WriteAllText(console, "SELECT Name FROM Products WHERE Id < 4 ORDER BY Id;\nSELECT COUNT(*) AS n FROM Orders;\nSELECT * FROM Nope;\n");
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open SQL Console did not run");
                Ui.WaitFor(() => window.EditorArea.Active?.Path == PathNames.Canonical(console) && window.EditorArea.ActiveGroup.Console is not null, "Shop's console with its results under it");
                Assert.True(window.EditorArea.ActiveGroup.SideUnder, "A console's results stand beside its SQL, not under it");
                CodeEditor editor = window.EditorArea.CodeEditor;
                editor.Selections = SelectionSet.Single(5);
                string keys = window.Actions.Keymap.ShortcutsFor(AppActions.KeysIn(AppActions.DatabaseRun))[0].First.Notation();
                Ui.Press(window, keys);
                SqlConsole sql = window.EditorArea.ActiveGroup.Console!.Console!;
                Ui.WaitFor(() => !sql.Running && sql.Results.Count == 1, () => sql.Said);
                Assert.Equal(["Changed elsewhere", "Product 2", "Product 3"], Enumerable.Range(0, 3).Select(r => sql.Results[0].Sets[0].Cell(r, 0)));
                Assert.True(editor.Document.Snapshot.ToString().Count(c => c == '\n') == 3, "Run Statement's keys also broke the console's line");
                CellGrid grid = window.EditorArea.ActiveGroup.Console!.Grid;
                Ui.WaitFor(() => grid.IsVisible && grid.Table?.RowCount == 3, "the result's grid");
                grid.Choose((0, 0), (2, 0));
                Assert.Equal("Changed elsewhere\nProduct 2\nProduct 3", grid.ChosenText().ReplaceLineEndings("\n").TrimEnd());
            });
            Flow.Step("statements chosen run in turn, each set in its own grid, a failure ending the run said under them", () =>
            {
                CodeEditor editor = window.EditorArea.CodeEditor;
                editor.Select(new TextRange(0, editor.Document.Snapshot.Length));
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                SqlConsole sql = window.EditorArea.ActiveGroup.Console!.Console!;
                Ui.WaitFor(() => !sql.Running && sql.Results.Count == 3, () => sql.Said);
                Assert.Equal("0", sql.Results[1].Sets[0].Cell(0, 0));
                Assert.Contains("Nope", sql.Results[2].Error, StringComparison.Ordinal);
                Assert.Contains("Failed after 2 statements", sql.Said, StringComparison.Ordinal);
                SqlConsoleView view = window.EditorArea.ActiveGroup.Console!;
                Assert.Equal(["ConsoleResult1", "ConsoleResult2", "ConsoleOutputTab"], view.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Name?.StartsWith("Console", StringComparison.Ordinal) == true).Select(t => t.Name));
                Assert.Contains("no such table: Nope", view.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "ConsoleOutput").Text, StringComparison.Ordinal);
            });
            Flow.Step("completion offers the live schema: a table's columns after its alias, and the tables its statement may name", () =>
            {
                CodeEditor editor = window.EditorArea.CodeEditor;
                editor.Select(new TextRange(0, editor.Document.Snapshot.Length));
                window.KeyTextInput("SELECT  FROM Products p");
                Ui.Press(window, "ESCAPE");
                editor.Selections = SelectionSet.Single("SELECT ".Length);
                window.KeyTextInput("p");
                window.KeyTextInput(".");
                Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Name") && window.Assist.CompletionLabels.Contains("Note"),
                    () => $"Products' columns after p., where the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
                Assert.DoesNotContain("Orders", window.Assist.CompletionLabels);
                Ui.Press(window, "ESCAPE");
                editor.Selections = SelectionSet.Single(editor.Document.Snapshot.Length);
                window.KeyTextInput(" JOIN ");
                window.KeyTextInput("O");
                Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Orders"), () => $"the tables, where the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
                Ui.Press(window, "ESCAPE");
            });
            Flow.Step("a connection made by hand is kept for the folder, its password in the credential store and never in the state", () =>
            {
                Assert.True(window.Perform(AppActions.DatabaseAdd), "New Database Connection did not run");
                Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseConnectionDialog>().Any(d => d.IsVisible), "the connection dialog");
                DatabaseConnectionDialog dialog = window.OwnedWindows.OfType<DatabaseConnectionDialog>().Single(d => d.IsVisible);
                Ui.Settle();
                DialogButtonsTests.Check(dialog);
                T Field<T>(string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
                Assert.False(Field<TextBox>("DatabasePassword").RevealPassword);
                Ui.ClickButton(dialog, "Connect");
                Assert.True(dialog.IsVisible, "The dialog closed with no host or database");
                Field<ComboBox>("DatabaseProvider").SelectedIndex = (int)DatabaseKind.SqlServer;
                Assert.Equal(1433m, Field<NumericUpDown>("DatabasePort").Value);
                Field<ComboBox>("DatabaseProvider").SelectedIndex = (int)DatabaseKind.Postgres;
                Field<TextBox>("DatabaseConnectionName").Text = "Reports";
                Field<TextBox>("DatabaseHost").Text = "127.0.0.1";
                Field<NumericUpDown>("DatabasePort").Value = 9;
                Field<TextBox>("DatabaseName").Text = "reports";
                Field<TextBox>("DatabaseUser").Text = "app";
                Field<TextBox>("DatabasePassword").Text = "kept-elsewhere-secret";
                Ui.ClickButton(dialog, "Connect");
                Ui.WaitFor(() => !window.Database.Busy && window.Database.Sources.Any(s => s.Name == "Reports"), () => window.Database.Said);
                DatabaseSource reports = window.Database.Sources.Single(s => s.Name == "Reports");
                Assert.Contains("kept-elsewhere-secret", SystemCredentials.Read(DatabaseController.CredentialService, reports.Account), StringComparison.Ordinal);
                Assert.DoesNotContain("kept-elsewhere-secret", window.Database.Said, StringComparison.Ordinal);
                window.Close();
                Ui.Settle();
                string kept = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(settings, "workspaces"), "database.json", SearchOption.AllDirectories)));
                Assert.Contains("Reports", kept, StringComparison.Ordinal);
                Assert.DoesNotContain("kept-elsewhere-secret", kept, StringComparison.Ordinal);
                Assert.DoesNotContain("Password", JsonDocument.Parse(kept).RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
                window = Open(folder, settings);
                window.ShowToolWindow(ToolWindowIds.Database);
                Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy, () => window.Database.Said);
                Assert.Equal("Reports", window.Database.Sources[0].Name);
                Assert.True(window.Database.Sources[0].SecretKept);
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A window on the folder keeping its state in <paramref name="settings"/>, as a person's does.</summary>
    private static MainWindow Open(string folder, string settings)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    private static DatabaseExplorer Explorer(MainWindow window) => window.DatabaseExplorer;

    private static DatabaseTableTab? Data(MainWindow window) => window.EditorArea.Active?.DatabaseTable;

    private static T Tab<T>(MainWindow window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>The rows directly under the row titled <paramref name="title"/>.</summary>
    private static IEnumerable<DatabaseNode> Under(MainWindow window, string title) =>
        Explorer(window).Rows.Single(r => r.Title == title).Children ?? [];

    /// <summary>Opens the first closed row titled <paramref name="title"/> as the person does, waiting for what it reads.</summary>
    internal static void Open(MainWindow window, string title)
    {
        DatabaseNode node = Explorer(window).Rows.First(r => r.Title == title && r.Expands && !r.IsExpanded);
        Explorer(window).Choose(node);
        Ui.Await(Explorer(window).ToggleAsync(node), $"{title} to open");
        Assert.True(node.IsExpanded, $"{title} did not open: {window.Database.Said}");
    }

    private static ChoiceDialog Question(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(d => d.IsVisible), () => window.Database.Said);
        return window.OwnedWindows.OfType<ChoiceDialog>().Single(d => d.IsVisible);
    }

    private static void Sql(string folder, string sql)
    {
        using SqliteConnection connection = new($"Data Source={Path.Combine(folder, "shop.db")};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? Scalar(string folder, string sql)
    {
        using SqliteConnection connection = new($"Data Source={Path.Combine(folder, "shop.db")};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }
}
