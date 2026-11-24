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
/// settings, its explorer down to a table's key column, a console's statement run into a grid and completion of the table's
/// columns, a table's changes submitted as one, a console's manual transaction rolled back and committed, and a production
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
