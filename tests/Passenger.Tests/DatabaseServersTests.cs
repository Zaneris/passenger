using System.Data.Common;
using System.Globalization;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Database;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The Database window against a server in a container of the test's own: its connection found in the folder's
/// settings, its explorer down to a table's key column, a console's statement run into a grid and completion of the table's
/// columns, each through its own driver. Skipped, naming the image, where the engine lacks it, as it is never pulled.</summary>
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
        using DbConnection opened = kind switch
        {
            DatabaseKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(connection),
            DatabaseKind.Postgres => new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString),
            _ => new MySqlConnector.MySqlConnection(new MySqlConnector.MySqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString),
        };
        opened.Open();
        using DbCommand command = opened.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
