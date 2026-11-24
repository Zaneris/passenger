using System.Data.Common;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Database;
using Passenger.App.Tables;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The Database window against a server in a container of the test's own: its connection found in the folder's
/// settings, its explorer down to a table's key column and trigger, the table's DDL as the server gives it, a console's
/// statement run into a grid, its parameter asked for and kept in its history, completion of the table's columns, a table's
/// changes submitted as one, a console's manual transaction rolled back and committed, the table exported as CSV, its rows
/// copied as inserts and run again, a CSV imported into a new table and a refused row writing nothing, and a production
/// connection asking before an UPDATE, each through its own driver. Skipped, naming the image, where the engine lacks it,
/// as it is never pulled.</summary>
public sealed class DatabaseServersTests
{
    private const string Password = "Passenger-db-7c1d";

    [AvaloniaTheory]
    [InlineData(DatabaseKind.Postgres, "postgres:17-alpine")]
    [InlineData(DatabaseKind.MySql, "mariadb:11.4")]
    [InlineData(DatabaseKind.SqlServer, "mcr.microsoft.com/mssql/server:2022-latest")]
    public void A_server_is_found_explored_run_and_completed_through_its_driver(DatabaseKind kind, string image)
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        Assert.SkipWhen(Docker.Said("image", "inspect", image) is null, $"The engine lacks {image}, which this test never pulls: pull it to run the test");
        string name = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}db{kind}".ToLowerInvariant();
        (int port, string[] environment) = kind switch
        {
            DatabaseKind.Postgres => (5432, (string[])["POSTGRES_PASSWORD=" + Password, "POSTGRES_DB=shop"]),
            DatabaseKind.MySql => (3306, ["MARIADB_ROOT_PASSWORD=" + Password, "MARIADB_DATABASE=shop"]),
            _ => (1433, ["ACCEPT_EULA=Y", "MSSQL_SA_PASSWORD=" + Password]),
        };
        Docker.Run(["run", "-d", "--name", name, "--label", Docker.RunLabel, "-p", $"127.0.0.1::{port}", .. environment.SelectMany(e => new[] { "-e", e }), image]);
        try
        {
            int published = int.Parse(Docker.Run("port", name, port.ToString(CultureInfo.InvariantCulture)).Trim().Split(':')[^1], CultureInfo.InvariantCulture);
            string connection = kind switch
            {
                DatabaseKind.Postgres => $"Host=127.0.0.1;Port={published};Database=shop;Username=postgres;Password={Password}",
                DatabaseKind.MySql => $"Server=127.0.0.1;Port={published};Database=shop;User ID=root;Password={Password}",
                _ => $"Server=127.0.0.1,{published};Initial Catalog=shop;User ID=sa;Password={Password};TrustServerCertificate=True",
            };
            Ui.WaitFor(() => Made(kind, connection), $"{image} to take connections and hold the shop's tables", TimeSpan.FromMinutes(3));
            string folder = TestFolders.Create("database-server");
            TestFolders.Write(folder, "Shop.csproj", TestProjects.Console());
            TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
            TestFolders.Write(folder, "appsettings.json", $$$"""{"ConnectionStrings":{"Shop":"{{{connection}}}"}}""");
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                window.ShowToolWindow(ToolWindowIds.Database);
                Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy, () => window.Database.Said);
                DatabaseSource source = Assert.Single(window.Database.Sources);
                Assert.Equal(kind, source.Kind);
                string schema = kind switch { DatabaseKind.Postgres => "public", DatabaseKind.SqlServer => "dbo", _ => string.Empty };
                foreach (string row in (string[])["Shop", "shop", .. schema.Length > 0 ? [schema] : Array.Empty<string>(), "Tables", "products", "Columns"])
                {
                    DatabaseTests.Open(window, row);
                }
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.Column && r.Title == "id" && r.Detail.Contains("primary key", StringComparison.Ordinal));
                DatabaseTests.Open(window, "Views");
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.View && r.Title == "cheap");

                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open SQL Console did not run");
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console is not null, "the console with its results under it");
                window.KeyTextInput("SELECT name FROM products ORDER BY id;");
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                SqlConsole console = window.EditorArea.ActiveGroup.Console!.Console!;
                Ui.WaitFor(() => !console.Running && console.Results.Count == 1, () => console.Said, TimeSpan.FromMinutes(1));
                Assert.True(console.Results[0].Error is null, console.Said);
                Assert.Equal(["tea", "coffee", "milk"], Enumerable.Range(0, 3).Select(r => console.Results[0].Sets[0].Cell(r, 0)));
                Assert.DoesNotContain(Password, console.Said, StringComparison.Ordinal);

                CodeEditor editor = window.EditorArea.CodeEditor;
                editor.Select(new TextRange(0, editor.Document.Snapshot.Length));
                window.KeyTextInput("SELECT  FROM products p");
                Ui.Press(window, "ESCAPE");
                editor.Selections = SelectionSet.Single("SELECT ".Length);
                window.KeyTextInput("p");
                window.KeyTextInput(".");
                Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("name") && window.Assist.CompletionLabels.Contains("price"),
                    () => $"products' columns after p., where the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
                Ui.Press(window, "ESCAPE");
                Passenger.App.Workspace.OpenDocument sql = window.EditorArea.Active!;

                // A parameter written as the driver takes it is asked for, and the run kept in the connection's history.
                string parameter = kind switch { DatabaseKind.Postgres => "$1", DatabaseKind.MySql => "?", _ => "@id" };
                Replace(window, $"SELECT name FROM products WHERE id = {parameter}");
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible), "the parameter's question");
                ConfirmDialog asked = window.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible);
                asked.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SqlParameter0").Text = "2";
                Ui.ClickButton(asked, "Run");
                Ui.WaitFor(() => !console.Running && console.Results is [{ Statement: var ran }] && ran.Contains(parameter, StringComparison.Ordinal), () => console.Said, TimeSpan.FromMinutes(1));
                Assert.True(console.Results[0].Error is null, console.Said);
                Assert.Equal("coffee", console.Results[0].Sets[0].Cell(0, 0));
                Assert.Equal($"SELECT name FROM products WHERE id = {parameter}", window.Database.HistoryOf(source)[0].Statement);

                // The table's trigger is listed, and its DDL is the server's own, the trigger with it.
                DatabaseTests.Open(window, "Triggers");
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.Trigger && r.Title == "stamp");
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "products"));
                Assert.True(window.Perform(AppActions.DatabaseShowDdl), "Show DDL did not run");
                Ui.WaitFor(() => window.EditorArea.Active?.Name.EndsWith("products.sql", StringComparison.Ordinal) == true && !window.Database.Busy, () => window.Database.Said);
                string ddl = window.EditorArea.Active!.Text.Snapshot.ToString();
                Assert.Contains("CREATE TABLE", ddl, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("price", ddl, StringComparison.Ordinal);
                Assert.Contains("stamp", ddl, StringComparison.Ordinal);
                Assert.True(window.EditorArea.Active!.IsReadOnly, "The DDL opened for writing");
                window.EditorArea.Activate(sql);

                // A column the table lacks is marked; a statement is explained as a plan's tree, never run, its costliest step marked.
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the console again");
                Replace(window, "SELECT nme FROM products WHERE id = 2");
                Ui.WaitFor(() => sql.Markers.Any(m => m.Message.Contains("nme", StringComparison.Ordinal)), () => $"nme marked, where the marks are {string.Join("; ", sql.Markers.Select(m => m.Message))}");
                Replace(window, "UPDATE products SET name = 'planned' WHERE id = 2");
                Ui.WaitFor(() => sql.Markers.Count == 0, () => string.Join("; ", sql.Markers.Select(m => m.Message)));
                Assert.True(window.Perform(AppActions.DatabaseExplain), "Explain Plan did not run");
                Ui.WaitFor(() => !console.Running && console.Plan is not null && console.Planned.StartsWith("UPDATE", StringComparison.Ordinal), () => console.Said, TimeSpan.FromMinutes(1));
                Assert.Equal("coffee", Names(kind, connection)[1]);
                Replace(window, "SELECT p.name FROM products p WHERE p.price > 1 ORDER BY p.name");
                Assert.True(window.Perform(AppActions.DatabaseExplain), "Explain Plan did not run");
                Ui.WaitFor(() => !console.Running && console.Planned.StartsWith("SELECT", StringComparison.Ordinal), () => console.Said, TimeSpan.FromMinutes(1));
                Assert.Contains(window.EditorArea.ActiveGroup.Console!.PlanShown, r => r.Costliest);

                // A cell written, a row deleted and one added wait as changes until Submit writes them as one.
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "products"));
                Assert.True(window.Perform(AppActions.DatabaseOpenTable), "Open Table Data did not run");
                Ui.WaitFor(() => window.EditorArea.Active?.DatabaseTable is { Reading: false, Rows.RowCount: 3 }, "products' rows");
                DatabaseTableTab data = window.EditorArea.Active!.DatabaseTable!;
                CellGrid grid = window.GetVisualDescendants().OfType<CellGrid>().Single(g => g.Name == "DatabaseTabResults");
                Write(window, grid, (0, 1), "green tea");
                grid.Choose((2, 0), (2, 0));
                Assert.True(window.Perform(AppActions.DatabaseDeleteRows), "Delete Rows did not run");
                Assert.True(window.Perform(AppActions.DatabaseAddRow), "Add Row did not run");
                grid.Writing!.Text = "4";
                Ui.Press(window, "ENTER");
                Assert.True(grid.BeginWriting(), "The added row's name could not be written");
                grid.Writing!.Text = "water";
                Ui.Press(window, "ENTER");
                Assert.Equal(["tea", "coffee", "milk"], Names(kind, connection));
                Assert.True(window.Perform(AppActions.DatabaseSubmit), "Submit did not run");
                Ui.WaitFor(() => data is { Reading: false, Edits.IsEmpty: true }, () => data.Said, TimeSpan.FromMinutes(1));
                Assert.Equal(["green tea", "coffee", "water"], Names(kind, connection));

                // A console in manual commit holds its transaction until Commit or Rollback.
                window.EditorArea.Activate(sql);
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the console again");
                window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ConsoleCommit").SelectedIndex = 1;
                Run(window, "UPDATE products SET name = 'oolong' WHERE id = 1");
                Assert.True(console.InTransaction, console.Said);
                Assert.True(window.Perform(AppActions.DatabaseRollback), "Rollback did not run");
                Ui.WaitFor(() => !console.InTransaction && !console.Running, () => console.Said);
                Assert.Equal(["green tea", "coffee", "water"], Names(kind, connection));
                Run(window, "UPDATE products SET name = 'oolong' WHERE id = 1");
                Assert.True(window.Perform(AppActions.DatabaseCommit), "Commit did not run");
                Ui.WaitFor(() => !console.InTransaction && !console.Running, () => console.Said);
                Assert.Equal(["oolong", "coffee", "water"], Names(kind, connection));
                window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ConsoleCommit").SelectedIndex = 0;

                // The table exports as CSV read from the server; its rows copied as inserts and pasted into the console write them again.
                List<string> told = [];
                window.Actions.Work.Told += (_, outcome) => told.Add(outcome.Said);
                ListBox tree = window.DatabaseExplorer.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "DatabaseTree");
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "products"));
                Ui.ChooseFromMenu(tree, "Export Data…");
                Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseExportDialog>().Any(d => d.IsVisible), "the export dialog");
                DatabaseExportDialog export = window.OwnedWindows.OfType<DatabaseExportDialog>().Single(d => d.IsVisible);
                string exported = Path.Combine(folder, "products.csv");
                export.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ExportFile").Text = exported;
                Ui.ClickButton(export, "Export");
                Ui.WaitFor(() => told.Any(t => t.StartsWith("Exported 3 rows", StringComparison.Ordinal)), () => string.Join("; ", told), TimeSpan.FromMinutes(1));
                Assert.Equal("id,name,price\r\n1,oolong,2.50\r\n2,coffee,3.50\r\n4,water,\r\n", File.ReadAllText(exported));
                window.EditorArea.Activate(data.Document);
                Ui.Await(data.ReadAgainAsync(), "products read again since the console changed them");
                CellGrid shown = window.GetVisualDescendants().OfType<CellGrid>().Single(g => g.Name == "DatabaseTabResults");
                shown.Focus();
                shown.Choose((0, 0), (2, 2));
                shown.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
                Ui.Settle();
                Ui.Choose(shown.Menu, "Copy As", "SQL Inserts");
                Ui.WaitFor(() => Ui.Clipboard(window)?.Contains("'water', NULL);", StringComparison.Ordinal) == true, () => Ui.Clipboard(window) ?? "nothing on the clipboard");
                Execute(kind, connection, "DELETE FROM products");
                window.EditorArea.Activate(sql);
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the console again");
                editor.Focus();
                editor.Select(new TextRange(0, editor.Document.Snapshot.Length));
                Assert.True(window.Perform(EditorActions.Paste), "Paste did not run in the console");
                Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("INSERT INTO", StringComparison.Ordinal), () => editor.Document.Snapshot.ToString());
                Assert.True(window.Perform(AppActions.DatabaseRunFile), "Run SQL File did not run");
                Ui.WaitFor(() => !console.Running && console.Results.Count > 0, () => console.Said, TimeSpan.FromMinutes(1));
                Assert.True(console.Results.All(r => r.Error is null), console.Said);
                Assert.Equal(["oolong", "coffee", "water"], Names(kind, connection));

                // A CSV imports into a new table typed from its rows; a row its column's type refuses writes none of the next file.
                File.WriteAllText(Path.Combine(folder, "items.csv"), "code,qty,price,made\nA1,3,2.5,2024-05-01\nB2,12,10,2024-06-30\n");
                File.WriteAllText(Path.Combine(folder, "bad.csv"), "code,qty,price,made\nC3,7,1,2024-01-01\nD4,many,1,2024-01-01\n");
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.First(r => r.Kind == DatabaseNodeKind.Group && r.Title == "Tables"));
                Ui.ChooseFromMenu(tree, "Import Data…");
                Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseImportDialog>().Any(d => d.IsVisible), () => window.Database.Said, TimeSpan.FromMinutes(1));
                DatabaseImportDialog import = window.OwnedWindows.OfType<DatabaseImportDialog>().Single(d => d.IsVisible);
                import.File = Path.Combine(folder, "items.csv");
                Ui.WaitFor(() => !import.IsReading && import.Columns.Count == 4, () => import.Problem);
                Ui.ClickButton(import, "Import");
                Ui.WaitFor(() => told.Any(t => t.StartsWith("Imported 2 rows of items.csv into the new table", StringComparison.Ordinal)), () => string.Join("; ", told), TimeSpan.FromMinutes(1));
                Ui.WaitFor(() => window.DatabaseExplorer.Rows.Any(r => r.Kind == DatabaseNodeKind.Table && r.Title == "items"), "the new table in the tree");
                Assert.Equal("15", Said(kind, connection, "SELECT SUM(qty) FROM items"));
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "items"));
                Ui.ChooseFromMenu(tree, "Import Data…");
                Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseImportDialog>().Any(d => d.IsVisible), () => window.Database.Said, TimeSpan.FromMinutes(1));
                import = window.OwnedWindows.OfType<DatabaseImportDialog>().Single(d => d.IsVisible);
                import.File = Path.Combine(folder, "bad.csv");
                Ui.WaitFor(() => !import.IsReading && import.Columns.Count == 4 && import.Columns.All(c => c.Into is not null), () => import.Problem);
                Ui.ClickButton(import, "Import");
                Ui.WaitFor(() => told.Any(t => t.StartsWith("Row 2 of bad.csv was refused, so nothing was written: ", StringComparison.Ordinal)), () => string.Join("; ", told), TimeSpan.FromMinutes(1));
                Assert.Equal("2", Said(kind, connection, "SELECT COUNT(*) FROM items"));

                // A production connection asks before an UPDATE; a read-only one still reads, through the server's own read-only session.
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection));
                Assert.True(window.Perform(AppActions.DatabaseProduction), "Production Connection did not run");
                Replace(window, "UPDATE products SET name = 'sencha' WHERE id = 1");
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible), "the production question");
                Ui.ClickButton(window.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible), "Run");
                Ui.WaitFor(() => !console.Running && console.Results is [{ Statement: var ran }] && ran.StartsWith("UPDATE", StringComparison.Ordinal), () => console.Said);
                Assert.Equal(["sencha", "coffee", "water"], Names(kind, connection));
                Assert.True(window.Perform(AppActions.DatabaseReadOnly), "Read-Only Connection did not run");
                Run(window, "SELECT name FROM products WHERE id = 2");
                Assert.True(console.Results is [{ Error: null }], console.Said);
                Assert.Equal("coffee", console.Results[0].Sets[0].Cell(0, 0));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Docker.Remove(name);
        }
    }

    /// <summary>Writes a cell as the person does: chosen, opened for writing, its text and Enter.</summary>
    private static void Write(MainWindow window, CellGrid grid, (int Row, int Column) cell, string text)
    {
        grid.Choose(cell, cell);
        Assert.True(grid.BeginWriting(), $"The cell {cell} could not be written");
        grid.Writing!.Text = text;
        Ui.Press(window, "ENTER");
    }

    private static void Replace(MainWindow window, string statement)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Select(new TextRange(0, editor.Document.Snapshot.Length));
        window.KeyTextInput(statement);
        Ui.Press(window, "ESCAPE");
    }

    private static void Run(MainWindow window, string statement)
    {
        Replace(window, statement);
        SqlConsole console = window.EditorArea.ActiveGroup.Console!.Console!;
        Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
        Ui.WaitFor(() => !console.Running, () => console.Said, TimeSpan.FromMinutes(1));
    }

    /// <summary>A query's one value as text, read on a connection of the test's own.</summary>
    private static string? Said(DatabaseKind kind, string connection, string sql)
    {
        using DbConnection opened = Opened(kind, connection);
        using DbCommand command = opened.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The products' names in their ids' order, read on a connection of the test's own.</summary>
    private static string[] Names(DatabaseKind kind, string connection)
    {
        using DbConnection opened = Opened(kind, connection);
        using DbCommand command = opened.CreateCommand();
        command.CommandText = "SELECT name FROM products ORDER BY id";
        using DbDataReader reader = command.ExecuteReader();
        List<string> names = [];
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        return [.. names];
    }

    /// <summary>Whether the server takes a connection, making the shop's tables in it the first time it does.</summary>
    private static bool Made(DatabaseKind kind, string connection)
    {
        string server = kind switch
        {
            DatabaseKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection) { InitialCatalog = "master", ConnectTimeout = 3 }.ConnectionString,
            DatabaseKind.Postgres => new Npgsql.NpgsqlConnectionStringBuilder(connection) { Timeout = 3, Pooling = false }.ConnectionString,
            _ => new MySqlConnector.MySqlConnectionStringBuilder(connection) { ConnectionTimeout = 3, Pooling = false }.ConnectionString,
        };
        try
        {
            if (kind == DatabaseKind.SqlServer)
            {
                Execute(kind, server, "IF DB_ID('shop') IS NULL CREATE DATABASE shop");
            }
            Execute(kind, connection, """
                CREATE TABLE products (id int PRIMARY KEY, name varchar(40) NOT NULL, price decimal(6,2));
                INSERT INTO products VALUES (1, 'tea', 2.5), (2, 'coffee', 3.5), (3, 'milk', 1.25)
                """);
            Execute(kind, connection, "CREATE VIEW cheap AS SELECT id, name FROM products WHERE price < 3");
            Execute(kind, connection, kind switch
            {
                DatabaseKind.Postgres => "CREATE FUNCTION keep() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; "
                    + "CREATE TRIGGER stamp BEFORE UPDATE ON products FOR EACH ROW EXECUTE FUNCTION keep()",
                DatabaseKind.MySql => "CREATE TRIGGER stamp BEFORE UPDATE ON products FOR EACH ROW SET NEW.name = NEW.name",
                _ => "CREATE TRIGGER stamp ON products AFTER UPDATE AS SET NOCOUNT ON",
            });
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    private static void Execute(DatabaseKind kind, string connection, string sql)
    {
        using DbConnection opened = Opened(kind, connection);
        using DbCommand command = opened.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DbConnection Opened(DatabaseKind kind, string connection)
    {
        DbConnection opened = kind switch
        {
            DatabaseKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(connection),
            DatabaseKind.Postgres => new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString),
            _ => new MySqlConnector.MySqlConnection(new MySqlConnector.MySqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString),
        };
        opened.Open();
        return opened;
    }
}
