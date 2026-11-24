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
/// it is opened, a table's data among the editors, an object's DDL and scripts, a console's parameters, history, files run
/// whole and SQL laid out, rows copied, exported (a million of them streamed) and imported, and a connection made by hand
/// kept with its password in the system's credential store (the test run's own, in memory).</summary>
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
            CREATE TRIGGER Noted AFTER INSERT ON Orders BEGIN UPDATE Products SET Note = 'ordered' WHERE Id = NEW.Product; END;
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
                Filter(window, "product 10");
                Ui.WaitFor(() => Data(window)?.Rows?.RowCount == 7, "Products whose names hold product 10");
                Filter(window, "%");
                Ui.WaitFor(() => Data(window)?.Rows?.RowCount == 0, "a filter's % taken as itself");
                Filter(window, string.Empty);
                Ui.WaitFor(() => Data(window)?.Rows?.RowCount == 100, "Products unfiltered");
            });
            Flow.Step("cells written, rows added and deleted wait as changes, Undo and Redo stepping through them, until Submit writes them as one", () =>
            {
                CellGrid grid = Tab<CellGrid>(window, "DatabaseTabResults");
                DatabaseTableTab data = Data(window)!;
                Write(window, grid, (0, 1), "New product");
                Write(window, grid, (1, 2), "a note");
                Assert.Equal(CellMark.Changed, data.Shown!.MarkOf(0, 1));
                Assert.Equal("Product 1", Scalar(folder, "SELECT Name FROM Products WHERE Id=1"));
                Assert.True(data.Document.HasPending, "A table with changes not written shows no mark on its tab");
                grid.Choose((3, 0), (3, 0));
                Assert.True(window.Perform(AppActions.DatabaseDeleteRows), "Delete Rows did not run");
                Assert.Equal(CellMark.Deleted, data.Shown.RowMark(3));
                Assert.True(window.Perform(AppActions.DatabaseAddRow), "Add Row did not run");
                Assert.NotNull(grid.Writing);
                grid.Writing!.Text = "500";
                Ui.Press(window, "ENTER");
                Assert.True(grid.BeginWriting(), "The added row's next cell could not be written");
                grid.Writing!.Text = "Added";
                Ui.Press(window, "ENTER");
                Assert.Equal(CellMark.Added, data.Shown.RowMark(100));
                Assert.Equal("Added", data.Shown.Cell(100, 1));
                Assert.True(data.Shown.IsNumber(100, 0), "An added row's Id does not stand where its column's numbers do");
                ShrinkingRow bar = Tab<ShrinkingRow>(window, "DatabaseTableBar");
                window.Width = 1800;
                Ui.Settle();
                Assert.DoesNotContain(bar.Children, ShrinkingRow.HasGone);
                window.Width = MainWindow.SmallestWidth;
                Ui.Settle();
                Assert.Contains(bar.Children, ShrinkingRow.HasGone);
                Assert.True(bar.Bounds.Height < 2 * Tab<Button>(window, "DatabaseTableMore").Bounds.Height, $"The table's bar stands {bar.Bounds.Height} high, in rows, at the window's narrowest");
                window.Width = 1200;
                Ui.Settle();
                Assert.True(window.Perform(Passenger.Editor.EditorActions.Undo), "Undo did not run in the table");
                Assert.Equal(string.Empty, data.Shown.Cell(100, 1));
                Assert.True(window.Perform(Passenger.Editor.EditorActions.Redo), "Redo did not run in the table");
                Assert.Equal("Added", data.Shown.Cell(100, 1));
                Assert.True(window.Perform(AppActions.DatabaseRevert), "Revert did not run");
                Assert.True(data.Edits.IsEmpty);
                Assert.True(window.Perform(Passenger.Editor.EditorActions.Undo), "Undo did not take back the revert");
                Assert.Equal("2 rows changed, 1 row deleted, 1 row added pending", data.Edits.Said);
                Assert.Equal("Product 1", Scalar(folder, "SELECT Name FROM Products WHERE Id=1"));
                grid.Focus();
                Ui.Press(window, window.Actions.Keymap.ShortcutsFor(AppActions.KeysIn(AppActions.DatabaseSubmit))[0].First.Notation());
                Ui.WaitFor(() => data.Edits.IsEmpty && !data.Reading, () => data.Said);
                Assert.Equal(100, data.Rows?.RowCount);
                Assert.False(data.Document.HasPending);
                Assert.Equal("New product", Scalar(folder, "SELECT Name FROM Products WHERE Id=1"));
                Assert.Equal("a note", Scalar(folder, "SELECT Note FROM Products WHERE Id=2"));
                Assert.Null(Scalar(folder, "SELECT Name FROM Products WHERE Id=4"));
                Assert.Equal("Added", Scalar(folder, "SELECT Name FROM Products WHERE Id=500"));
            });
            Flow.Step("a row changed elsewhere since it was read refuses the changes, none written, and the tab asks before it closes with them", () =>
            {
                CellGrid grid = Tab<CellGrid>(window, "DatabaseTabResults");
                DatabaseTableTab data = Data(window)!;
                Write(window, grid, (1, 1), "Not this either");
                Write(window, grid, (0, 1), "Must not overwrite");
                Sql(folder, "UPDATE Products SET Name='Changed elsewhere' WHERE Id=1");
                Assert.True(window.Perform(AppActions.DatabaseSubmit), "Submit did not run");
                Ui.WaitFor(() => Tab<TextBlock>(window, "DatabaseTabStatus").Text?.Contains("changed since it was read", StringComparison.Ordinal) == true, () => data.Said);
                Assert.Equal("Changed elsewhere", Scalar(folder, "SELECT Name FROM Products WHERE Id=1"));
                Assert.Equal("Product 2", Scalar(folder, "SELECT Name FROM Products WHERE Id=2"));
                Assert.False(data.Edits.IsEmpty, "The changes refused were dropped");
                Assert.NotNull(window.CloseSolution());
                Ui.ClickButton(Question(window), "Cancel");
                Ui.Settle();
                Assert.True(window.Folder is not null && !data.Edits.IsEmpty, "Close Solution dropped the table's changes not written");
                Assert.False(window.EditorArea.CloseTab(window.EditorArea.ActiveGroup, data.Document), "The tab closed with changes not written");
                Ui.ClickButton(Question(window), "Discard");
                Ui.WaitFor(() => window.EditorArea.Groups.SelectMany(g => g.Tabs).All(t => t.Document.DatabaseTable is null), "the table's tab closed");
                Assert.Equal("Product 2", Scalar(folder, "SELECT Name FROM Products WHERE Id=2"));
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
                Assert.Equal(["ConsoleResult1", "ConsoleResult2", "ConsoleOutputTab", "ConsoleHistoryTab"], view.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Name?.StartsWith("Console", StringComparison.Ordinal) == true).Select(t => t.Name));
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
            Flow.Step("an object's DDL opens read-only as SQL, a table's triggers are listed, and its scripts are written into the console, never run", () =>
            {
                Open(window, "Orders");
                Open(window, "Triggers");
                DatabaseNode trigger = Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Trigger);
                Assert.Equal(("Noted", "after insert"), (trigger.Title, trigger.Detail));
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "Products"));
                Assert.True(window.Perform(AppActions.DatabaseShowDdl), "Show DDL did not run");
                Ui.WaitFor(() => window.EditorArea.Active?.Name == "Products.sql" && !window.Database.Busy, () => window.Database.Said);
                Passenger.App.Workspace.OpenDocument ddl = window.EditorArea.Active!;
                Assert.Equal("CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Note TEXT);\n\nCREATE INDEX ProductNames ON Products(Name);\n",
                    ddl.Text.Snapshot.ToString());
                Assert.True(ddl.IsReadOnly, "A table's DDL opened for writing");
                Assert.Equal(Passenger.Languages.Language.Sql, ddl.Language);
                ListBox tree = Explorer(window).GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "DatabaseTree");
                Explorer(window).Choose(trigger);
                Ui.Settle();
                Ui.Click(tree.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext == trigger));
                Ui.Press(window, window.Actions.Keymap.ShortcutsFor(AppActions.GoToDeclaration)[0].First.Notation());
                Ui.WaitFor(() => window.EditorArea.Active?.Name == "Orders.Noted.sql" && !window.Database.Busy, () => window.Database.Said);
                Assert.StartsWith("CREATE TRIGGER Noted AFTER INSERT ON Orders", window.EditorArea.Active!.Text.Snapshot.ToString(), StringComparison.Ordinal);
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Index && r.Title.StartsWith("ProductNames", StringComparison.Ordinal)));
                Assert.True(window.Perform(AppActions.DatabaseScriptDrop), "Generate Drop Script did not run");
                Ui.WaitFor(() => window.EditorArea.Active?.Name == "Shop.sql" && !window.Database.Busy, () => window.Database.Said);
                CodeEditor editor = window.EditorArea.CodeEditor;
                Assert.EndsWith("\nDROP INDEX \"ProductNames\";\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
                Selection written = editor.Selections.Primary;
                Assert.True("DROP INDEX \"ProductNames\";" == editor.Document.Snapshot.ToString()[written.Start..written.End],
                    $"The script written is not what is chosen: {written} of {editor.Document.Snapshot.Length} in {editor.Document.Snapshot.ToString().ReplaceLineEndings("⏎")}");
                Assert.Equal("ProductNames", Scalar(folder, "SELECT name FROM sqlite_schema WHERE type = 'index' AND name = 'ProductNames'"));
                Open(window, "Views");
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.View && r.Title == "Cheap"));
                Assert.True(window.Perform(AppActions.DatabaseScriptAlter), "Generate Alter Script did not run");
                Ui.WaitFor(() => !window.Database.Busy && editor.Document.Snapshot.ToString().Contains("DROP VIEW", StringComparison.Ordinal), () => window.Database.Said);
                Assert.EndsWith("DROP VIEW \"Cheap\";\nCREATE VIEW Cheap AS SELECT * FROM Products WHERE Id < 3;\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            });
            Flow.Step("a statement's parameters are asked for before it runs and remembered, a file runs whole to its first failure, and the history runs one again", () =>
            {
                SqlConsole sql = window.EditorArea.ActiveGroup.Console!.Console!;
                Replace(window, "SELECT Name FROM Products WHERE Id = :id");
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                ConfirmDialog asked = Confirm(window);
                TextBox id = asked.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SqlParameter0");
                Assert.Equal(string.Empty, id.Text);
                id.Text = "2";
                Ui.ClickButton(asked, "Run");
                Ui.WaitFor(() => !sql.Running && sql.Results is [{ Error: null }], () => sql.Said);
                Assert.Equal("Product 2", sql.Results[0].Sets[0].Cell(0, 0));
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                asked = Confirm(window);
                Assert.Equal("2", asked.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SqlParameter0").Text);
                Ui.ClickButton(asked, "Cancel");
                Ui.WaitFor(() => sql.Said == "Not run.", () => sql.Said);

                Replace(window, "UPDATE Products SET Note = 'one' WHERE Id = 3;\nSELECT Note FROM Products WHERE Id = 3;\nSELECT * FROM Nope;\nUPDATE Products SET Note = 'never' WHERE Id = 3;");
                window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
                Assert.True(window.Perform(AppActions.DatabaseRunFile), "Run SQL File did not run");
                Ui.WaitFor(() => !sql.Running && sql.Results.Count == 3, () => sql.Said);
                Assert.Equal("one", Scalar(folder, "SELECT Note FROM Products WHERE Id = 3"));
                Assert.Contains("Failed after 2 statements", sql.Said, StringComparison.Ordinal);
                string output = Tab<SelectableTextBlock>(window, "ConsoleOutput").Text ?? string.Empty;
                Assert.Contains("-- 1 row changed", output, StringComparison.Ordinal);
                Assert.Contains("no such table: Nope", output, StringComparison.Ordinal);

                Assert.True(window.Perform(AppActions.DatabaseHistory), "Query History did not run");
                SqlConsoleView view = window.EditorArea.ActiveGroup.Console!;
                Ui.WaitFor(() => Tab<ListBox>(window, "ConsoleHistory").IsEffectivelyVisible, "the console's history");
                Assert.Equal(["SELECT * FROM Nope", "SELECT Note FROM Products WHERE Id = 3", "UPDATE Products SET Note = 'one' WHERE Id = 3"],
                    view.HistoryShown.Take(3).Select(r => r.Statement));
                Assert.True(view.HistoryShown[0].Failed, "The failed statement is not kept as failed");
                Tab<TextBox>(window, "ConsoleFilter").Text = "count";
                Ui.Settle();
                SqlRan counted = Assert.Single(view.HistoryShown);
                Assert.Equal("SELECT COUNT(*) AS n FROM Orders", counted.Statement);
                ListBox history = Tab<ListBox>(window, "ConsoleHistory");
                history.SelectedItem = counted;
                Ui.Settle();
                Ui.Click(history.GetVisualDescendants().OfType<ListBoxItem>().Single(i => Equals(i.DataContext, counted)));
                Ui.Press(window, "ENTER");
                CodeEditor editor = window.EditorArea.CodeEditor;
                Assert.EndsWith("\n\nSELECT COUNT(*) AS n FROM Orders;\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Ui.WaitFor(() => !sql.Running && sql.Results is [{ Error: null, Statement: "SELECT COUNT(*) AS n FROM Orders" }], () => sql.Said);
                Tab<TextBox>(window, "ConsoleFilter").Text = string.Empty;
            });
            Flow.Step("a console marks a column its connection's schema lacks, and explains a statement as a plan's tree without running it", () =>
            {
                Passenger.App.Workspace.OpenDocument file = window.EditorArea.Active!;
                Replace(window, "SELECT Nme FROM Products WHERE Id = 2");
                Ui.WaitFor(() => file.Markers.Any(m => m.Message.Contains("Nme", StringComparison.Ordinal)), () => $"the unknown column marked, where the marks are {string.Join("; ", file.Markers.Select(m => m.Message))}");
                Marker unknown = file.Markers.Single();
                Assert.Equal("Nme", file.Text.Snapshot.ToString()[unknown.Range.Start..unknown.Range.End]);
                Replace(window, "UPDATE Products SET Note = 'planned' WHERE Id = 2");
                Ui.WaitFor(() => file.Markers.Count == 0, () => string.Join("; ", file.Markers.Select(m => m.Message)));
                Assert.True(window.Perform(AppActions.DatabaseExplain), "Explain Plan did not run");
                SqlConsole sql = window.EditorArea.ActiveGroup.Console!.Console!;
                SqlConsoleView view = window.EditorArea.ActiveGroup.Console!;
                Ui.WaitFor(() => !sql.Running && sql.Plan is not null && Tab<TreeList>(window, "ConsolePlan").IsEffectivelyVisible, () => sql.Said);
                Assert.Contains(view.PlanShown, r => r.Step.Operation.StartsWith("SEARCH Products", StringComparison.Ordinal));
                Assert.Equal("a note", Scalar(folder, "SELECT Note FROM Products WHERE Id = 2"));
            });
            Flow.Step("Reformat Code lays a console's SQL out, its comments and strings as written", () =>
            {
                Replace(window, "select Name, 'a;b' from Products -- kept\nwhere Id = 1 and Note is null");
                Assert.True(window.Perform(AppActions.Reformat), "Reformat Code did not run");
                Ui.WaitFor(() => window.EditorArea.CodeEditor.Document.Snapshot.ToString().StartsWith("SELECT", StringComparison.Ordinal), "the statement laid out");
                Assert.Equal("SELECT Name,\n       'a;b'\nFROM Products -- kept\nWHERE Id = 1\n    AND Note IS NULL", window.EditorArea.CodeEditor.Document.Snapshot.ToString());
            });
            Flow.Step("a table's chosen cells copy as CSV, tab-separated, JSON and SQL inserts, and the inserts pasted into its console add the rows again", () =>
            {
                Passenger.App.Workspace.OpenDocument console = window.EditorArea.Active!;
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "Products"));
                Assert.True(window.Perform(AppActions.DatabaseOpenTable), "Open Table Data did not run");
                Ui.WaitFor(() => Data(window) is { Reading: false, Rows.RowCount: 100 }, "Products' first page");
                CellGrid grid = Tab<CellGrid>(window, "DatabaseTabResults");
                grid.Focus();
                grid.Choose((0, 0), (1, 2));
                Assert.Equal("Id,Name,Note\n1,Changed elsewhere,\n2,Product 2,a note\n", grid.CopiedAs(AppActions.CopyAsCsv)?.ReplaceLineEndings("\n"));
                Assert.Equal("1\tChanged elsewhere\tNULL\n2\tProduct 2\ta note\n", grid.CopiedAs(AppActions.CopyAsTsv)?.ReplaceLineEndings("\n"));
                Assert.Equal("[\n  {\"Id\":1,\"Name\":\"Changed elsewhere\",\"Note\":null},\n  {\"Id\":2,\"Name\":\"Product 2\",\"Note\":\"a note\"}\n]\n",
                    grid.CopiedAs(AppActions.CopyAsJson)?.ReplaceLineEndings("\n"));
                grid.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
                Ui.Settle();
                Ui.Choose(grid.Menu, "Copy As", "SQL Inserts");
                const string Inserts = "INSERT INTO \"Products\" (\"Id\", \"Name\", \"Note\") VALUES (1, 'Changed elsewhere', NULL);\n"
                    + "INSERT INTO \"Products\" (\"Id\", \"Name\", \"Note\") VALUES (2, 'Product 2', 'a note');\n";
                Ui.WaitFor(() => Ui.Clipboard(window)?.ReplaceLineEndings("\n") == Inserts, () => $"the rows as inserts on the clipboard, where it holds {Ui.Clipboard(window)}");
                Sql(folder, "DELETE FROM Products WHERE Id IN (1, 2)");
                window.EditorArea.Activate(console);
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the console again");
                CodeEditor editor = window.EditorArea.CodeEditor;
                editor.Focus();
                Assert.Equal(Refusals.NoTableCells, window.Actions.Run(AppActions.CopyAsJson));
                editor.Select(new TextRange(0, editor.Document.Snapshot.Length));
                Assert.True(window.Perform(EditorActions.Paste), "Paste did not run in the console");
                Ui.WaitFor(() => editor.Document.Snapshot.ToString().ReplaceLineEndings("\n") == Inserts, () => editor.Document.Snapshot.ToString());
                SqlConsole sql = window.EditorArea.ActiveGroup.Console!.Console!;
                Assert.True(window.Perform(AppActions.DatabaseRunFile), "Run SQL File did not run");
                Ui.WaitFor(() => !sql.Running && sql.Results is [{ Error: null }, { Error: null }], () => sql.Said);
                Assert.Equal("Changed elsewhere", Scalar(folder, "SELECT Name FROM Products WHERE Id = 1"));
                Assert.Equal("a note", Scalar(folder, "SELECT Note FROM Products WHERE Id = 2"));
            });
            Flow.Step("a table, a filtered table and a result export to CSV, JSON and SQL inserts, a million rows streamed holding a row at a time, and Stop leaves the file as it was", () =>
            {
                string exports = TestFolders.Create("database-exports-");
                List<string> told = Told(window);
                ListBox tree = Explorer(window).GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "DatabaseTree");
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "Products"));
                Ui.ChooseFromMenu(tree, "Export Data…");
                DatabaseExportDialog dialog = Dialog<DatabaseExportDialog>(window);
                DialogButtonsTests.Check(dialog);
                Assert.Contains("Every row of Products", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).OfType<string>().First(t => t.StartsWith("Every row", StringComparison.Ordinal)), StringComparison.Ordinal);
                Field<ComboBox>(dialog, "ExportFormat").SelectedIndex = (int)ExportFormat.Inserts;
                Assert.Contains("SQLite's SQL", dialog.Holds, StringComparison.Ordinal);
                Field<TextBox>(dialog, "ExportTable").Text = "Copied";
                Assert.Equal(".sql", Path.GetExtension(Field<TextBox>(dialog, "ExportFile").Text));
                Field<TextBox>(dialog, "ExportFile").Text = Path.Combine(exports, "products.sql");
                Ui.ClickButton(dialog, "Export");
                Ui.WaitFor(() => told.Any(t => t.StartsWith("Exported 105 rows of Products to products.sql", StringComparison.Ordinal)), () => string.Join("; ", told));
                Sql(folder, "CREATE TABLE Copied (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Note TEXT);\n" + File.ReadAllText(Path.Combine(exports, "products.sql")));
                Assert.Equal(105L, Count(folder, "Copied"));
                Assert.Equal("a note", Scalar(folder, "SELECT Note FROM Copied WHERE Id = 2"));

                window.EditorArea.Activate(window.EditorArea.Groups.SelectMany(g => g.Tabs).Select(t => t.Document).Single(d => d.DatabaseTable?.Table.Name == "Products"));
                Tab<ComboBox>(window, "DatabaseFilterColumn").SelectedItem = "Name";
                Filter(window, "product 1");
                Ui.WaitFor(() => Data(window) is { Reading: false, Rows.RowCount: 16 }, "Products whose names hold product 1");
                Tab<CellGrid>(window, "DatabaseTabResults").Focus();
                Assert.True(window.Perform(AppActions.DatabaseExport), "Export Data did not run from the table");
                dialog = Dialog<DatabaseExportDialog>(window);
                Field<ComboBox>(dialog, "ExportFormat").SelectedIndex = (int)ExportFormat.Json;
                Field<TextBox>(dialog, "ExportFile").Text = Path.Combine(exports, "products.json");
                Ui.ClickButton(dialog, "Export");
                Ui.WaitFor(() => told.Any(t => t.StartsWith("Exported 16 rows of Products to products.json", StringComparison.Ordinal)), () => string.Join("; ", told));
                using (JsonDocument exported = JsonDocument.Parse(File.ReadAllText(Path.Combine(exports, "products.json"))))
                {
                    Assert.Equal(16, exported.RootElement.GetArrayLength());
                    Assert.Equal("Product 10", exported.RootElement[0].GetProperty("Name").GetString());
                }
                Filter(window, string.Empty);

                Sql(folder, "CREATE TABLE Big (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Price REAL); WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 1000000) "
                    + "INSERT INTO Big SELECT x, 'Item ' || x, x * 0.25 FROM n;");
                window.EditorArea.Activate(window.EditorArea.Documents.Single(d => d.Name == "Shop.sql"));
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the console again");
                window.EditorArea.CodeEditor.Focus();
                Run(window, "SELECT Id, Name, Price FROM Big ORDER BY Id");
                SqlConsoleView view = window.EditorArea.ActiveGroup.Console!;
                Assert.True(view.ChosenResult is { Rows.More: true }, "The million rows' result was not cut at its first rows");
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<RowExport> held = window.Database.HoldExport(500_000, release.Task);
                Ui.Click(Tab<Button>(window, "ConsoleExport"));
                dialog = Dialog<DatabaseExportDialog>(window);
                Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Every row its statement returns, run again", StringComparison.Ordinal) == true);
                string big = Path.Combine(exports, "big.csv");
                Field<ComboBox>(dialog, "ExportFormat").SelectedIndex = (int)ExportFormat.Csv;
                Field<TextBox>(dialog, "ExportFile").Text = big;
                ExportUnsettled(dialog);
                RowExport export = Ui.AwaitPumped(held, "the export held halfway");
                Ui.PumpUntil(() => window.Actions.Work.Of(AppActions.DatabaseExport)?.Stage.Contains("500,000 rows written", StringComparison.Ordinal) == true,
                    "the export saying the rows it has written");
                (long bytes, string most) = HeldBytes.Of(export, o => o is Avalonia.AvaloniaObject or Delegate or DatabaseSource);
                Assert.True(bytes < 4 << 20, $"Halfway through a million rows the export holds {bytes:N0} bytes, the most in {most}: it holds rows it has written");
                string writing = Assert.Single(Directory.GetFiles(exports, "*" + FileText.SaveSuffix));
                TestContext.Current.TestOutputHelper?.WriteLine($"Halfway through a million rows the export held {bytes:N0} bytes (the most in {most}), {new FileInfo(writing).Length:N0} on disk");
                Assert.True(new FileInfo(writing).Length > 8 << 20, $"Halfway through a million rows only {new FileInfo(writing).Length:N0} bytes are on disk: the rows are not written as they come");
                Assert.False(File.Exists(big), "The file is there before the export has written it whole");
                release.SetResult();
                Ui.WaitFor(() => told.Any(t => t.StartsWith("Exported 1,000,000 rows of Result 1 to big.csv", StringComparison.Ordinal)), () => string.Join("; ", told), TimeSpan.FromMinutes(2));
                Assert.Equal(1_000_001, File.ReadLines(big).LongCount());
                Assert.Equal("Id,Name,Price", File.ReadLines(big).First());
                Assert.Equal("1000000,Item 1000000,250000", File.ReadLines(big).Last());

                long written = new FileInfo(big).Length;
                TaskCompletionSource again = new(TaskCreationOptions.RunContinuationsAsynchronously);
                held = window.Database.HoldExport(100_000, again.Task);
                Ui.Click(Tab<Button>(window, "ConsoleExport"));
                dialog = Dialog<DatabaseExportDialog>(window);
                Assert.Equal(exports, Path.GetDirectoryName(Field<TextBox>(dialog, "ExportFile").Text));
                Field<TextBox>(dialog, "ExportFile").Text = big;
                ExportUnsettled(dialog);
                Ui.AwaitPumped(held, "the second export held");
                Assert.True(window.Perform(AppActions.Stop), "Stop did not run while the export worked");
                again.SetResult();
                Ui.WaitFor(() => told.Contains("Export stopped: big.csv is as it was"), () => string.Join("; ", told));
                Assert.Equal(written, new FileInfo(big).Length);
                Assert.Empty(Directory.GetFiles(exports, "*" + FileText.SaveSuffix));
            });
            Flow.Step("a CSV imports into an existing table by its columns' names, a JSON file into a new table typed from its rows, and the first refused row writes nothing", () =>
            {
                string imports = TestFolders.Create("database-imports-");
                List<string> told = Told(window);
                File.WriteAllText(Path.Combine(imports, "more.csv"), "Name,Note,Id\r\n\"Imported, one\",\"has \"\"quotes\"\"\",900\r\nTwo,,901\r\n");
                window.EditorArea.Activate(window.EditorArea.Groups.SelectMany(g => g.Tabs).Select(t => t.Document).Single(d => d.DatabaseTable?.Table.Name == "Products"));
                Tab<CellGrid>(window, "DatabaseTabResults").Focus();
                Assert.True(window.Perform(AppActions.DatabaseImport), "Import Data did not run from the table");
                DatabaseImportDialog dialog = Dialog<DatabaseImportDialog>(window);
                DialogButtonsTests.Check(dialog);
                Assert.Equal("Products", Field<ComboBox>(dialog, "ImportInto").SelectedItem);
                dialog.File = Path.Combine(imports, "more.csv");
                Ui.WaitFor(() => !dialog.IsReading && dialog.Preview is not null && dialog.Columns.Count == 3, () => dialog.Problem);
                Assert.Equal("Name, Note, Id", string.Join(", ", dialog.Columns.Select(c => c.Into)));
                Assert.Equal("has \"quotes\"", dialog.Preview!.Rows[0][1]);
                Ui.ClickButton(dialog, "Import");
                Ui.WaitFor(() => told.Contains("Imported 2 rows of more.csv into Products"), () => string.Join("; ", told));
                Assert.Equal("Imported, one", Scalar(folder, "SELECT Name FROM Products WHERE Id = 900"));
                Assert.Equal("has \"quotes\"", Scalar(folder, "SELECT Note FROM Products WHERE Id = 900"));
                Assert.Null(Value(folder, "SELECT Note FROM Products WHERE Id = 901"));

                File.WriteAllText(Path.Combine(imports, "again.csv"), "Id,Name\n902,Fine\n900,Taken\n");
                Assert.True(window.Perform(AppActions.DatabaseImport), "Import Data did not run again");
                dialog = Dialog<DatabaseImportDialog>(window);
                dialog.File = Path.Combine(imports, "again.csv");
                Ui.WaitFor(() => !dialog.IsReading && dialog.Columns.Count == 2, () => dialog.Problem);
                Ui.ClickButton(dialog, "Import");
                Ui.WaitFor(() => told.Any(t => t.StartsWith("Row 2 of again.csv was refused, so nothing was written: ", StringComparison.Ordinal)), () => string.Join("; ", told));
                Assert.Contains("UNIQUE", told[^1], StringComparison.Ordinal);
                Assert.Null(Scalar(folder, "SELECT Name FROM Products WHERE Id = 902"));

                File.WriteAllText(Path.Combine(imports, "stock.json"), """
                    [{"code": "A1", "qty": 3, "price": 2.5, "when": "2024-05-01", "ok": true},
                     {"code": "B2", "qty": 12, "price": 10, "when": "2024-06-30", "ok": false, "late": "yes"}]
                    """);
                ListBox tree = Explorer(window).GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "DatabaseTree");
                Explorer(window).Choose(Explorer(window).Rows.First(r => r.Kind == DatabaseNodeKind.Group && r.Title == "Tables"));
                Ui.ChooseFromMenu(tree, "Import Data…");
                dialog = Dialog<DatabaseImportDialog>(window);
                Assert.Equal(DatabaseImportDialog.NewTable, Field<ComboBox>(dialog, "ImportInto").SelectedItem);
                dialog.File = Path.Combine(imports, "stock.json");
                Ui.WaitFor(() => !dialog.IsReading && dialog.Columns.Count == 6, () => dialog.Problem);
                Assert.Equal("stock", Field<TextBox>(dialog, "ImportTableName").Text);
                Assert.Equal(["TEXT", "INTEGER", "REAL", "DATE", "BOOLEAN", "TEXT"], dialog.Columns.Select(c => c.Type));
                Ui.ClickButton(dialog, "Import");
                Ui.WaitFor(() => told.Contains("Imported 2 rows of stock.json into the new table stock"), () => string.Join("; ", told));
                Ui.WaitFor(() => Explorer(window).Rows.Any(r => r.Kind == DatabaseNodeKind.Table && r.Title == "stock"), "the new table in the tree, the tree as it was open");
                Assert.Equal(12L, Value(folder, "SELECT qty FROM stock WHERE code = 'B2'"));
                Assert.Equal(0L, Value(folder, "SELECT ok FROM stock WHERE code = 'B2'"));
                Assert.Null(Value(folder, "SELECT late FROM stock WHERE code = 'A1'"));
                window.EditorArea.Activate(window.EditorArea.Documents.Single(d => d.Name == "Shop.sql"));
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the console again");
                window.EditorArea.CodeEditor.Focus();
            });
            Flow.Step("a console in manual commit holds its transaction across statements until Commit or Rollback, and asks before it closes with one open", () =>
            {
                SqlConsole sql = window.EditorArea.ActiveGroup.Console!.Console!;
                Tab<ComboBox>(window, "ConsoleCommit").SelectedIndex = 1;
                Assert.True(sql.Manual, "Manual commit was not taken");
                Run(window, "UPDATE Products SET Note='held' WHERE Id=2");
                Assert.True(sql.InTransaction, sql.Said);
                Assert.True(Tab<Border>(window, "ConsoleTransaction").IsVisible, "The console does not show its transaction open");
                Assert.Equal("a note", Scalar(folder, "SELECT Note FROM Products WHERE Id=2"));
                Assert.True(window.Perform(AppActions.DatabaseRollback), "Rollback did not run");
                Ui.WaitFor(() => !sql.InTransaction && !sql.Running, () => sql.Said);
                Run(window, "SELECT Note FROM Products WHERE Id=2");
                Assert.Equal("a note", sql.Results[0].Sets[0].Cell(0, 0));
                Run(window, "UPDATE Products SET Note='held' WHERE Id=2");
                Run(window, "COMMIT");
                Assert.False(sql.InTransaction, $"COMMIT run in the console left its transaction open: {sql.Said}");
                Assert.Equal("held", Scalar(folder, "SELECT Note FROM Products WHERE Id=2"));
                Run(window, "UPDATE Products SET Note='kept' WHERE Id=2");
                window.Close();
                Ui.ClickButton(Question(window), "Cancel");
                Ui.Settle();
                Assert.True(window.IsVisible && sql.InTransaction, "Closing the window dropped the console's open transaction");
                Passenger.App.Workspace.OpenDocument console = window.EditorArea.Active!;
                Assert.False(window.EditorArea.CloseTab(window.EditorArea.ActiveGroup, console), "The console closed with its transaction open");
                Ui.ClickButton(Question(window), "Commit");
                Ui.WaitFor(() => window.EditorArea.Active != console, "the console closed once committed");
                Assert.Equal("kept", Scalar(folder, "SELECT Note FROM Products WHERE Id=2"));
            });
            Flow.Step("a connection marked production asks before a statement that changes data, and one marked read-only refuses it", () =>
            {
                ListBox tree = Explorer(window).GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "DatabaseTree");
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
                Ui.ChooseFromMenu(tree, "Production");
                Assert.Equal(DatabaseMark.Production, window.Database.Sources.Single(s => s.Name == "Shop").Mark);
                Assert.Equal(DatabaseMark.Production, Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop").Mark);
                Ui.ChooseFromMenu(tree, "Open SQL Console");
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console is not null, "Shop's console");
                Assert.Equal(DatabaseMark.Production, window.EditorArea.MarkOf!(window.EditorArea.Active!));
                Assert.Contains("production", Tab<Banner>(window, "ConsoleMark").Said, StringComparison.Ordinal);
                Tab<ComboBox>(window, "ConsoleCommit").SelectedIndex = 0;
                SqlConsole sql = window.EditorArea.ActiveGroup.Console!.Console!;
                Run(window, "SELECT COUNT(*) FROM Products");
                Assert.DoesNotContain(window.OwnedWindows.OfType<ConfirmDialog>(), d => d.IsVisible);
                Replace(window, "UPDATE Products SET Note='production' WHERE Id=2");
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Ui.ClickButton(Confirm(window), "Cancel");
                Ui.WaitFor(() => sql.Said == "Not run.", () => sql.Said);
                Assert.Equal("kept", Scalar(folder, "SELECT Note FROM Products WHERE Id=2"));
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Ui.ClickButton(Confirm(window), "Run");
                Ui.WaitFor(() => !sql.Running && sql.Results is [{ Error: null } ran] && ran.Statement.StartsWith("UPDATE", StringComparison.Ordinal), () => sql.Said);
                Assert.Equal("production", Scalar(folder, "SELECT Note FROM Products WHERE Id=2"));
                Assert.True(window.Perform(AppActions.DatabaseReadOnly), "Read-Only Connection did not run");
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Assert.Equal(Refusals.ReadOnlyConnection("Shop"), sql.Said);
                Assert.DoesNotContain(window.OwnedWindows.OfType<ConfirmDialog>(), d => d.IsVisible);
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Title == "Products"));
                Assert.True(window.Perform(AppActions.DatabaseOpenTable), "Open Table Data did not run");
                Ui.WaitFor(() => Data(window) is { Reading: false, Rows.RowCount: 100 }, "Products read-only");
                CellGrid grid = Tab<CellGrid>(window, "DatabaseTabResults");
                grid.Choose((0, 1), (0, 1));
                Assert.False(grid.BeginWriting(), "A cell of a read-only connection's table opened for writing");
                Assert.Equal(Refusals.ReadOnlyConnection("Shop"), Tab<TextBlock>(window, "DatabaseTabStatus").Text);
                Assert.Equal(Refusals.ReadOnlyConnection("Shop"), window.Actions.Run(AppActions.DatabaseAddRow));
                Explorer(window).Choose(Explorer(window).Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
                Assert.True(window.Perform(AppActions.DatabaseProduction), "Production Connection did not run");
                Assert.Equal(DatabaseMark.Production, window.Database.Sources.Single(s => s.Name == "Shop").Mark);
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
                Ui.WaitFor(() => !window.Database.Busy && window.Database.Sources.Any(s => s.Name == "Shop"),
                    () => $"{window.Database.Said} [{string.Join(", ", window.Database.Sources.Select(s => s.Name))}] of {window.Session.Projects.Count} projects");
                Assert.Equal(DatabaseMark.Production, window.Database.Sources.Single(s => s.Name == "Shop").Mark);
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A window on the folder keeping its state in <paramref name="settings"/>, seen, as a person's is, so what would
    /// lose a table's changes or a transaction asks.</summary>
    private static MainWindow Open(string folder, string settings)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false, Unseen = false };
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

    /// <summary>Filters the table's rows by the words given, as Enter in the filter's box does, on a bar narrow enough to
    /// have let go of its Apply.</summary>
    private static void Filter(MainWindow window, string words)
    {
        TextBox value = Tab<TextBox>(window, "DatabaseFilterValue");
        value.Text = words;
        value.Focus();
        Ui.Press(window, "ENTER");
    }

    /// <summary>Writes a cell as the person does: chosen, opened for writing, its text and Enter.</summary>
    private static void Write(MainWindow window, CellGrid grid, (int Row, int Column) cell, string text)
    {
        grid.Choose(cell, cell);
        Assert.True(grid.BeginWriting(), $"The cell {cell} could not be written");
        grid.Writing!.Text = text;
        Ui.Press(window, "ENTER");
    }

    /// <summary>The console's text replaced by what is typed, a line at a time with Enter between, the caret after it.</summary>
    private static void Replace(MainWindow window, string statement)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Select(new TextRange(0, editor.Document.Snapshot.Length));
        string[] lines = statement.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                Ui.Press(window, "ENTER");
            }
            window.KeyTextInput(lines[i]);
        }
        Ui.Press(window, "ESCAPE");
        Assert.Equal(statement, editor.Document.Snapshot.ToString());
    }

    /// <summary>Runs one statement in the console shown, waiting for what it came to.</summary>
    private static void Run(MainWindow window, string statement)
    {
        Replace(window, statement);
        SqlConsole sql = window.EditorArea.ActiveGroup.Console!.Console!;
        Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
        Ui.WaitFor(() => !sql.Running, () => sql.Said);
    }

    /// <summary>What each work of the window's tells it came to from now on, in turn.</summary>
    private static List<string> Told(MainWindow window)
    {
        List<string> told = [];
        window.Actions.Work.Told += (_, outcome) => told.Add(outcome.Said);
        return told;
    }

    private static T Dialog<T>(MainWindow window) where T : Window
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<T>().Any(d => d.IsVisible), () => window.Status ?? string.Empty);
        Ui.Settle();
        return window.OwnedWindows.OfType<T>().Single(d => d.IsVisible);
    }

    /// <summary>Export pressed while the export it starts is held, which the test pumps through itself.</summary>
    private static void ExportUnsettled(DatabaseExportDialog dialog) =>
        Ui.Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Export"), settle: false);

    private static T Field<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static ConfirmDialog Confirm(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible), () => window.Database.Said);
        return window.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible);
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

    private static long Count(string folder, string table) => (long)Value(folder, $"SELECT COUNT(*) FROM {table}")!;

    private static object? Value(string folder, string sql)
    {
        using SqliteConnection connection = new($"Data Source={Path.Combine(folder, "shop.db")};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() is { } value and not DBNull ? value : null;
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
