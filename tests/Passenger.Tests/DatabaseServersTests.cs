using System.Buffers.Binary;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Database;
using Passenger.App.Tables;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The Database window against a server in a container of the test's own: its connection found in the folder's
/// settings, its explorer down to a table's key column and trigger, the table's DDL as the server gives it, a console's
/// statement run into a grid, its parameter asked for and kept in its history, completion of the table's columns, a table's
/// changes submitted as one, a console's manual transaction rolled back and committed, the table exported as CSV, its rows
/// copied as inserts and run again, a CSV imported into a new table and a refused row writing nothing, a production
/// connection asking before an UPDATE, a connection made through an SSH bastion with its SSL verified, and a found one
/// given a client certificate and a tunnel through ~/.ssh/config's jump host, signed in by the agent, its statement stopped
/// through it, the schema drawn as a diagram, and a second server's schema (SQL Server's a second database) compared with it
/// and made as it is by the script, each through its own driver. Skipped, naming the image, where the engine lacks it, as it
/// is never pulled.</summary>
public sealed class DatabaseServersTests
{
    private const string Password = "Passenger-db-7c1d";
    private const string SshPassword = "Passenger-ssh-4e2a";
    private const string ClientKeyPassword = "Passenger-key-91b3";

    /// <summary>ClickHouse's server, its long-term release pinned.</summary>
    private const string ClickHouseImage = "clickhouse/clickhouse-server:26.3.71.3-alpine";

    /// <summary>MongoDB's server, its 8.0 release pinned.</summary>
    private const string MongoImage = "mongo:8.0.38";

    /// <summary>Cosmos DB's emulator, its Linux build that runs on Arm as on x86-64, pinned by its date.</summary>
    private const string CosmosImage = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20261019";

    /// <summary>The key every Cosmos DB emulator signs in with, as its documentation gives it.</summary>
    private const string CosmosKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    /// <summary>Redis's server, and Valkey's, which takes its commands, each pinned.</summary>
    private const string RedisImage = "redis:8.2.11-alpine";

    private const string ValkeyImage = "valkey/valkey:9.0.8-alpine";

    /// <summary>The bastion's image, pinned to the one the test was written against.</summary>
    private const string SshImage = "lscr.io/linuxserver/openssh-server@sha256:c8431b8982e7eb3935ebf9c46607d19c6417e39c12ce884cc66945df2e9334e8";

    [AvaloniaTheory]
    [InlineData(DatabaseKind.Postgres, "postgres:17-alpine")]
    [InlineData(DatabaseKind.MySql, "mariadb:11.4")]
    [InlineData(DatabaseKind.SqlServer, "mcr.microsoft.com/mssql/server:2022-latest")]
    [InlineData(DatabaseKind.ClickHouse, ClickHouseImage)]
    [InlineData(DatabaseKind.Snowflake, "")]
    [InlineData(DatabaseKind.MongoDb, MongoImage)]
    [InlineData(DatabaseKind.CosmosDb, CosmosImage)]
    [InlineData(DatabaseKind.Redis, RedisImage)]
    [InlineData(DatabaseKind.Redis, ValkeyImage)]
    public void A_server_is_found_explored_run_and_completed_through_its_driver(DatabaseKind kind, string image)
    {
        Assert.SkipWhen(kind == DatabaseKind.Snowflake, "Snowflake runs only as Snowflake's own service, which no container stands in for, and this run has no account of it");
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        Assert.SkipWhen(Docker.Said("image", "inspect", image) is null, $"The engine lacks {image}, which this test never pulls: pull it to run the test");
        if (kind == DatabaseKind.ClickHouse)
        {
            ClickHouse();
            return;
        }
        if (kind == DatabaseKind.MongoDb)
        {
            Mongo();
            return;
        }
        if (kind == DatabaseKind.CosmosDb)
        {
            Cosmos();
            return;
        }
        if (kind == DatabaseKind.Redis)
        {
            Redis(image);
            return;
        }
        Assert.SkipWhen(Docker.Said("image", "inspect", SshImage) is null, $"The engine lacks {SshImage}, which this test never pulls: pull it to run the test");
        Assert.SkipWhen(Programs.OnPath("ssh-agent") is null || Programs.OnPath("ssh-add") is null || Programs.OnPath("ssh") is null,
            "ssh, ssh-agent and ssh-add, which hold the tunnel's key and carry a ProxyCommand, are not on PATH");
        string name = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}db{kind}".ToLowerInvariant();
        (int port, string[] environment) = kind switch
        {
            DatabaseKind.Postgres => (5432, (string[])["POSTGRES_PASSWORD=" + Password, "POSTGRES_DB=shop"]),
            DatabaseKind.MySql => (3306, ["MARIADB_ROOT_PASSWORD=" + Password, "MARIADB_DATABASE=shop"]),
            _ => (1433, ["ACCEPT_EULA=Y", "MSSQL_SA_PASSWORD=" + Password]),
        };
        // The server's SSL is its certificate for "db", its name on a network it shares only with the bastion.
        string certificates = TestFolders.Create("database-certificates");
        Certify(certificates);
        string[] serving = kind switch
        {
            DatabaseKind.Postgres => ["--entrypoint", "sh", image, "-c", "chown postgres /certs/server.key && chmod 600 /certs/server.key && exec docker-entrypoint.sh postgres "
                + "-c ssl=on -c ssl_cert_file=/certs/server.crt -c ssl_key_file=/certs/server.key -c ssl_ca_file=/certs/ca.crt"],
            DatabaseKind.MySql => [image, "--ssl-cert=/certs/server.crt", "--ssl-key=/certs/server.key", "--ssl-ca=/certs/ca.crt"],
            _ => [image],
        };
        string ssh = name + "ssh";
        string inner = name + "inner";
        string second = name + "target";
        string? runsSshHome = Environment.GetEnvironmentVariable(SshFiles.HomeVariable);
        string agentSocket = Path.Combine(TestRun.Temporary, "agent" + ((int)kind).ToString(CultureInfo.InvariantCulture));
        Process? agent = null;
        try
        {
            Docker.Run(["network", "create", "--label", Docker.RunLabel, name]);
            Docker.Run(["create", "--name", name, "--label", Docker.RunLabel, "--network", name, "--network-alias", "db", "-p", $"127.0.0.1::{port}",
                .. environment.SelectMany(e => new[] { "-e", e }), .. serving]);
            Docker.Run("cp", certificates, $"{name}:/certs");
            if (kind == DatabaseKind.SqlServer)
            {
                File.WriteAllText(Path.Combine(certificates, "mssql.conf"), "[network]\ntlscert = /certs/server.crt\ntlskey = /certs/server.key\nforceencryption = 0\n");
                Docker.Run("cp", Path.Combine(certificates, "mssql.conf"), $"{name}:/var/opt/mssql/mssql.conf");
            }
            Docker.Run("start", name);
            if (kind != DatabaseKind.SqlServer)
            {
                Docker.Run(["run", "-d", "--name", second, "--label", Docker.RunLabel, "-p", $"127.0.0.1::{port}", .. environment.SelectMany(e => new[] { "-e", e }), image]);
            }
            // The bastion forwards to the server, signing in with a password, or for PostgreSQL a key of the test's own.
            string sshd = Path.Combine(certificates, "sshd", "sshd_config.d");
            Directory.CreateDirectory(sshd);
            File.WriteAllText(Path.Combine(sshd, "forward.conf"), "AllowTcpForwarding yes\n");
            string keyFile = Path.Combine(certificates, "id_rsa");
            string publicKey = SshKey(keyFile);
            Docker.Run(["create", "--name", ssh, "--label", Docker.RunLabel, "--network", name, "-p", "127.0.0.1::2222", "-e", "USER_NAME=passenger",
                "-e", "PASSWORD_ACCESS=true", "-e", "USER_PASSWORD=" + SshPassword, "-e", "PUBLIC_KEY=" + publicKey, SshImage]);
            Docker.Run("cp", Path.Combine(certificates, "sshd"), $"{ssh}:/config/sshd");
            Docker.Run("start", ssh);
            int bastion = int.Parse(Docker.Run("port", ssh, "2222").Trim().Split(':')[^1], CultureInfo.InvariantCulture);
            // A second bastion no port of this machine reaches, signed in to only by the agent's key, through the first.
            Docker.Run(["create", "--name", inner, "--label", Docker.RunLabel, "--network", name, "--network-alias", "bastion", "-e", "USER_NAME=passenger",
                "-e", "PUBLIC_KEY=" + publicKey, SshImage]);
            Docker.Run("cp", Path.Combine(certificates, "sshd"), $"{inner}:/config/sshd");
            Docker.Run("start", inner);
            string? hostKeys = null;
            Ui.WaitFor(() => (hostKeys = Docker.Said("exec", ssh, "sh", "-c", "cat /config/ssh_host_keys/ssh_host_*_key.pub")) is { Length: > 0 }, "the first bastion's host keys");
            File.WriteAllText(keyFile + ".pub", publicKey + "\n");
            string home = SshHome(bastion, agentSocket, hostKeys!, keyFile);
            Environment.SetEnvironmentVariable(SshFiles.HomeVariable, home);
            File.Delete(agentSocket);
            agent = ChildProcesses.Start(ChildProcesses.Command("ssh-agent", ["-D", "-a", agentSocket]));
            Ui.WaitFor(() => File.Exists(agentSocket), "ssh-agent's socket");
            ChildResult added = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("ssh-add", [keyFile], null, [KeyValuePair.Create("SSH_AUTH_SOCK", agentSocket)]))),
                "ssh-add of the test's key");
            Assert.True(added.Succeeded, $"ssh-add failed: {added.Error}");
            int published = int.Parse(Docker.Run("port", name, port.ToString(CultureInfo.InvariantCulture)).Trim().Split(':')[^1], CultureInfo.InvariantCulture);
            string connection = kind switch
            {
                DatabaseKind.Postgres => $"Host=127.0.0.1;Port={published};Database=shop;Username=postgres;Password={Password}",
                DatabaseKind.MySql => $"Server=127.0.0.1;Port={published};Database=shop;User ID=root;Password={Password}",
                _ => $"Server=127.0.0.1,{published};Initial Catalog=shop;User ID=sa;Password={Password};TrustServerCertificate=True",
            };
            Ui.WaitFor(() => Made(kind, connection), $"{image} to take connections and hold the shop's tables", TimeSpan.FromMinutes(3));
            Certified(kind, name, connection);
            string target = kind switch
            {
                DatabaseKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection) { InitialCatalog = "target" }.ConnectionString,
                _ => connection.Replace($"Port={published}", $"Port={Published(second, port)}", StringComparison.Ordinal),
            };
            Ui.WaitFor(() => MadeOtherwise(kind, target), "the second schema to take connections and hold its tables", TimeSpan.FromMinutes(3));
            string folder = TestFolders.Create("database-server");
            TestFolders.Write(folder, "Shop.csproj", TestProjects.Console());
            TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
            // The folder names the server as "db" too, which only a tunnel reaches, as a user only a client certificate signs in as.
            string behindDb = kind switch
            {
                DatabaseKind.Postgres => "Host=db;Port=5432;Database=shop;Username=certified",
                DatabaseKind.MySql => "Server=db;Port=3306;Database=shop;User ID=certified",
                _ => $"Server=db,1433;Initial Catalog=shop;User ID=sa;Password={Password}",
            };
            TestFolders.Write(folder, "appsettings.json", $$$"""{"ConnectionStrings":{"Shop":"{{{connection}}}","Behind":"{{{behindDb}}}","Target":"{{{target}}}"}}""");
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                window.ShowToolWindow(ToolWindowIds.Database);
                Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy, () => window.Database.Said);
                DatabaseSource source = window.Database.Sources.Single(s => s.Name == "Shop");
                Assert.Equal(kind, source.Kind);
                string schema = kind switch { DatabaseKind.Postgres => "public", DatabaseKind.SqlServer => "dbo", _ => string.Empty };
                foreach (string row in (string[])["Shop", "shop", .. schema.Length > 0 ? [schema] : Array.Empty<string>(), "Tables", "products", "Columns"])
                {
                    DatabaseTests.Open(window, row);
                }
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.Column && r.Title == "id" && r.Detail.Contains("primary key", StringComparison.Ordinal));
                DatabaseTests.Open(window, "Views");
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.View && r.Title == "cheap");

                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open Query Console did not run");
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
                // PostgreSQL's numeric takes any price, so its server refuses the third row's qty; the others' decimal(3,1) the second's price.
                File.WriteAllText(Path.Combine(folder, "bad.csv"), "code,qty,price,made\nC3,7,1,2024-01-01\nD4,8,1234.5,2024-01-01\nE5,many,1,2024-01-01\n");
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
                string refused = $"Row {(kind == DatabaseKind.Postgres ? 3 : 2).ToString(CultureInfo.InvariantCulture)} of bad.csv was refused, so nothing was written: ";
                Ui.WaitFor(() => told.Any(t => t.StartsWith(refused, StringComparison.Ordinal)), () => string.Join("; ", told), TimeSpan.FromMinutes(1));
                Assert.Equal("2", Said(kind, connection, "SELECT COUNT(*) FROM items"));

                // A production connection asks before an UPDATE; a read-only one still reads, through the server's own read-only session.
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
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

                // A connection made by hand reaches "db" through the bastion, its host key trusted once, its SSL verified.
                Assert.True(window.Perform(AppActions.DatabaseAdd), "New Database Connection did not run");
                Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseConnectionDialog>().Any(d => d.IsVisible), "the connection dialog");
                DatabaseConnectionDialog dialog = window.OwnedWindows.OfType<DatabaseConnectionDialog>().Single(d => d.IsVisible);
                T Field<T>(string field) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == field);
                Field<ComboBox>("DatabaseProvider").SelectedIndex = (int)kind;
                Field<TextBox>("DatabaseConnectionName").Text = "Direct";
                Field<TextBox>("DatabaseHost").Text = "db";
                Field<TextBox>("DatabaseName").Text = "shop";
                Field<TextBox>("DatabaseUser").Text = kind switch { DatabaseKind.Postgres => "postgres", DatabaseKind.MySql => "root", _ => "sa" };
                Field<TextBox>("DatabasePassword").Text = Password;
                Field<ComboBox>("DatabaseSsl").SelectedIndex = (int)(kind == DatabaseKind.SqlServer ? SslChoice.VerifyAuthority : SslChoice.VerifyHost);
                Field<TextBox>("DatabaseSslCertificate").Text = Path.Combine(certificates, kind == DatabaseKind.SqlServer ? "server.crt" : "ca.crt");
                Field<CheckBox>("DatabaseSshTunnel").IsChecked = true;
                Field<TextBox>("DatabaseSshHost").Text = "127.0.0.1";
                Field<NumericUpDown>("DatabaseSshPort").Value = bastion;
                Field<TextBox>("DatabaseSshUser").Text = "passenger";
                if (kind == DatabaseKind.Postgres)
                {
                    // The key locked by a passphrase as ssh-keygen locks one, the agent holding it unlocked since before.
                    ChildResult locked = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("ssh-keygen", ["-p", "-q", "-P", string.Empty, "-N", SshPassword, "-f", keyFile]))),
                        "ssh-keygen's lock of the test's key");
                    Assert.True(locked.Succeeded, $"ssh-keygen did not lock the key: {locked.Error}");
                    Field<TextBox>("DatabaseSshKey").Text = keyFile;
                }
                Field<TextBox>("DatabaseSshPassword").Text = SshPassword;
                Ui.ClickButton(dialog, "Connect");
                Ui.WaitFor(() => dialog.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible) || !dialog.IsVisible,
                    () => Field<TextBlock>("DatabaseConnectionError").Text ?? "the host key's question", TimeSpan.FromMinutes(1));
                ConfirmDialog trust = dialog.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible);
                Assert.Contains("SHA256:", string.Join(" ", trust.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), StringComparison.Ordinal);
                Ui.ClickButton(trust, "Trust");
                Ui.WaitFor(() => !dialog.IsVisible && window.Database.Sources.Any(s => s.Name == "Direct"), () => window.Database.Said);
                DatabaseSource direct = window.Database.Sources.Single(s => s.Name == "Direct");
                Assert.StartsWith("SHA256:", Assert.Single(direct.Tunnel!.HostKeys).Fingerprint, StringComparison.Ordinal);
                Assert.Equal(SshPassword, SystemCredentials.Read(DatabaseController.CredentialService, direct.TunnelAccount));
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Source.Id == direct.Id));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open Query Console did not run");
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is { } opened && opened != console, "a console of the connection through the bastion");
                SqlConsole through = window.EditorArea.ActiveGroup.Console!.Console!;
                Replace(window, kind switch
                {
                    DatabaseKind.Postgres => "SELECT CASE WHEN ssl THEN 'encrypted' ELSE 'plain' END FROM pg_stat_ssl WHERE pid = pg_backend_pid()",
                    DatabaseKind.MySql => "SELECT CASE WHEN LENGTH(VARIABLE_VALUE) > 0 THEN 'encrypted' ELSE 'plain' END FROM information_schema.SESSION_STATUS WHERE VARIABLE_NAME = 'Ssl_cipher'",
                    _ => "SELECT CASE WHEN encrypt_option = 'TRUE' THEN 'encrypted' ELSE 'plain' END FROM sys.dm_exec_connections WHERE session_id = @@SPID",
                });
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Ui.WaitFor(() => !through.Running && through.Results.Count == 1, () => through.Said, TimeSpan.FromMinutes(1));
                Assert.True(through.Results[0].Error is null, $"{through.Said}, running {through.Results[0].Statement}");
                Assert.Equal("encrypted", through.Results[0].Sets[0].Cell(0, 0));
                Assert.DoesNotContain(SshPassword, through.Said, StringComparison.Ordinal);

                // Disconnected, an agent's read opens the tunnel again itself, the key's passphrase or the password from the system's store.
                window.Database.Disconnect(direct);
                object read = Ui.Await(new Passenger.App.Mcp.PassengerTools(window).Database(connection: "Direct", sql: "SELECT name FROM products WHERE id = 2"),
                    "an agent's query through the tunnel", TimeSpan.FromMinutes(1));
                Passenger.App.Mcp.DatabaseQueryResult answer = Assert.Single(Assert.IsType<Passenger.App.Mcp.DatabaseQueryResult[]>(read));
                Assert.True(answer.Error is null, $"An agent's query through the tunnel failed: {answer.Error}");
                Assert.Equal("coffee", answer.Sets[0].Rows[0][0]);

                // A found connection takes a client certificate and a tunnel to "inner", ~/.ssh/config's bastion behind its jump host, its files untouched.
                string written = File.ReadAllText(Path.Combine(folder, "appsettings.json"));
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Behind"));
                Assert.True(window.Perform(AppActions.DatabaseConnectionEdit), "Edit Database Connection did not run");
                Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseConnectionDialog>().Any(d => d.IsVisible), "the found connection's settings");
                dialog = window.OwnedWindows.OfType<DatabaseConnectionDialog>().Single(d => d.IsVisible);
                Assert.False(Field<TextBox>("DatabaseHost").IsEffectivelyVisible, "A found connection's host was offered for changing");
                Field<ComboBox>("DatabaseSsl").SelectedIndex = (int)(kind == DatabaseKind.SqlServer ? SslChoice.VerifyAuthority : SslChoice.VerifyHost);
                Field<TextBox>("DatabaseSslCertificate").Text = Path.Combine(certificates, kind == DatabaseKind.SqlServer ? "server.crt" : "ca.crt");
                if (DatabaseSsl.TakesClientCertificate(kind))
                {
                    Field<TextBox>("DatabaseSslClientCertificate").Text = Path.Combine(certificates, "client.crt");
                    Field<TextBox>("DatabaseSslClientKey").Text = Path.Combine(certificates, "client.key");
                    Field<TextBox>("DatabaseSslKeyPassword").Text = ClientKeyPassword;
                }
                Field<CheckBox>("DatabaseSshTunnel").IsChecked = true;
                Field<TextBox>("DatabaseSshHost").Text = "inner";
                Ui.ClickButton(dialog, "Save and Connect");
                Ui.WaitFor(() => dialog.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible) || !dialog.IsVisible,
                    () => Field<TextBlock>("DatabaseConnectionError").Text ?? "the inner bastion's host key's question", TimeSpan.FromMinutes(1));
                trust = dialog.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible);
                Assert.Contains("bastion:2222 gives", string.Join(" ", trust.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), StringComparison.Ordinal);
                Ui.ClickButton(trust, "Trust");
                Ui.WaitFor(() => !dialog.IsVisible, () => Field<TextBlock>("DatabaseConnectionError").Text ?? "the settings kept", TimeSpan.FromMinutes(1));
                DatabaseSource behind = window.Database.Sources.Single(s => s.Name == "Behind");
                Assert.NotNull(behind.Found);
                Assert.Equal(["bastion"], behind.Tunnel!.HostKeys.Select(k => k.Host));
                Assert.Equal(written, File.ReadAllText(Path.Combine(folder, "appsettings.json")));
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Behind"));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open Query Console did not run");
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is { } opened && opened != console && opened != through, "a console of the found connection");
                SqlConsole jumped = window.EditorArea.ActiveGroup.Console!.Console!;
                Replace(window, kind switch
                {
                    DatabaseKind.Postgres => "SELECT CASE WHEN s.ssl THEN 'encrypted' ELSE 'plain' END || ' as ' || current_user FROM pg_stat_ssl s WHERE s.pid = pg_backend_pid()",
                    DatabaseKind.MySql => "SELECT CONCAT(CASE WHEN LENGTH(VARIABLE_VALUE) > 0 THEN 'encrypted' ELSE 'plain' END, ' as ', SUBSTRING_INDEX(CURRENT_USER(), '@', 1)) "
                        + "FROM information_schema.SESSION_STATUS WHERE VARIABLE_NAME = 'Ssl_cipher'",
                    _ => "SELECT CASE WHEN encrypt_option = 'TRUE' THEN 'encrypted' ELSE 'plain' END + ' as ' + SUSER_NAME() FROM sys.dm_exec_connections WHERE session_id = @@SPID",
                });
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Ui.WaitFor(() => !jumped.Running && jumped.Results.Count == 1, () => jumped.Said, TimeSpan.FromMinutes(1));
                Assert.True(jumped.Results[0].Error is null, $"{jumped.Said}, running {jumped.Results[0].Statement}");
                Assert.Equal(kind == DatabaseKind.SqlServer ? "encrypted as sa" : "encrypted as certified", jumped.Results[0].Sets[0].Cell(0, 0));

                // A tunnel through ssh's ProxyCommand, its port from the system's file under a Match exec, reaches the server too.
                Assert.True(window.Perform(AppActions.DatabaseAdd), "New Database Connection did not run");
                Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseConnectionDialog>().Any(d => d.IsVisible), "the connection dialog");
                dialog = window.OwnedWindows.OfType<DatabaseConnectionDialog>().Single(d => d.IsVisible);
                Field<ComboBox>("DatabaseProvider").SelectedIndex = (int)kind;
                Field<TextBox>("DatabaseConnectionName").Text = "Proxied";
                Field<TextBox>("DatabaseHost").Text = "db";
                Field<TextBox>("DatabaseName").Text = "shop";
                Field<TextBox>("DatabaseUser").Text = kind switch { DatabaseKind.Postgres => "postgres", DatabaseKind.MySql => "root", _ => "sa" };
                Field<TextBox>("DatabasePassword").Text = Password;
                Field<ComboBox>("DatabaseSsl").SelectedIndex = (int)SslChoice.Required;
                Field<CheckBox>("DatabaseSshTunnel").IsChecked = true;
                Field<TextBox>("DatabaseSshHost").Text = "proxied";
                Ui.ClickButton(dialog, "Connect");
                Ui.WaitFor(() => dialog.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible) || !dialog.IsVisible,
                    () => Field<TextBlock>("DatabaseConnectionError").Text ?? "the proxied bastion's host key's question", TimeSpan.FromMinutes(1));
                trust = dialog.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible);
                Assert.Contains("bastion:2222 gives", string.Join(" ", trust.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), StringComparison.Ordinal);
                Ui.ClickButton(trust, "Trust");
                Ui.WaitFor(() => !dialog.IsVisible, () => Field<TextBlock>("DatabaseConnectionError").Text ?? "the proxied connection kept", TimeSpan.FromMinutes(1));
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Proxied"));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open Query Console did not run");
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is { } opened && opened != jumped && opened != through && opened != console, "a console of the proxied connection");
                SqlConsole proxied = window.EditorArea.ActiveGroup.Console!.Console!;
                Run(window, "SELECT name FROM products WHERE id = 2");
                Assert.True(proxied.Results is [{ Error: null }], proxied.Said);
                Assert.Equal("coffee", proxied.Results[0].Sets[0].Cell(0, 0));
                window.EditorArea.Activate(window.EditorArea.TabDocuments.Single(d => d.Name == "Behind.sql"));

                // Stop Statement ends a statement through the tunnel on the server, well before it would have ended itself.
                (string sleeping, string running) = kind switch
                {
                    DatabaseKind.Postgres => ("SELECT pg_sleep(120)", "SELECT COUNT(*) FROM pg_stat_activity WHERE query = 'SELECT pg_sleep(120)'"),
                    DatabaseKind.MySql => ("SELECT SLEEP(120)", "SELECT COUNT(*) FROM information_schema.PROCESSLIST WHERE INFO = 'SELECT SLEEP(120)'"),
                    _ => ("WAITFOR DELAY '00:02:00'", "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE wait_type = 'WAITFOR'"),
                };
                Replace(window, sleeping);
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                // Pumped, not settled: a settled wait would wait for the statement itself to end.
                Ui.PumpUntil(() => jumped.Running && Said(kind, connection, running) == "1", $"{sleeping} running on the server", TimeSpan.FromSeconds(30));
                Assert.True(window.Perform(AppActions.DatabaseRunStop), "Stop Statement did not run");
                Ui.PumpUntil(() => !jumped.Running && Said(kind, connection, running) == "0", $"{sleeping} stopped on the server", TimeSpan.FromSeconds(20));
                Assert.Equal("Stopped.", jumped.Said);

                // The diagram draws tables and foreign keys, zooms, keeps a box where it was dragged, opens a table, and reads again.
                Execute(kind, connection, "CREATE TABLE orders (id int PRIMARY KEY, product_id int, qty int NOT NULL, FOREIGN KEY (product_id) REFERENCES products(id))");
                SchemaDiagramTab diagram = Diagram(window);
                DiagramCanvas canvas = diagram.Shown!;
                Assert.Subset(canvas.Panel.Boxes.Select(b => b.Table.Name).ToHashSet(), new HashSet<string> { "products", "orders", "items" });
                Assert.Contains(canvas.Panel.Lines, l => l.From == "orders" && l.To == "products");
                Assert.True(canvas.Panel.Boxes.Single(b => b.Table.Name == "orders").X > canvas.Panel.Boxes.Single(b => b.Table.Name == "products").X, "orders stands right of products, which it refers to");
                canvas.ZoomIn();
                Assert.True(canvas.Zoom > 1, $"Zoom In left the diagram at {canvas.Zoom}");
                canvas.ActualSize();
                Ui.Settle();
                Rect title = canvas.Shown("orders")!.Value;
                Point grabbed = canvas.TranslatePoint(new Point(title.X + 30, title.Y + 10), window)!.Value;
                Ui.Drag(window, grabbed, grabbed + new Vector(40, 120));
                Ui.Settle();
                Assert.True(canvas.Places.TryGetValue("orders", out (double X, double Y) left), "The orders box dragged was not kept where it was left");
                DiagramBox laid = canvas.Panel.Boxes.Single(b => b.Table.Name == "orders");
                Assert.True((title.X + 40, title.Y + 120) == (laid.X + canvas.Offset.X, laid.Y + canvas.Offset.Y),
                    $"The orders box stands at {laid} with the diagram at {canvas.Offset} × {canvas.Zoom}, dragged from {title} to be left at {left}");
                Assert.Equal(laid.X, canvas.Panel.Lines.Single(l => l.From == "orders").Points[0].X);
                window.EditorArea.Close(diagram.Document);
                diagram = Diagram(window);
                Assert.Equal(left, (diagram.Shown!.Panel.Boxes.Single(b => b.Table.Name == "orders").X, diagram.Shown.Panel.Boxes.Single(b => b.Table.Name == "orders").Y));
                diagram.Shown.Chosen = "orders";
                diagram.Shown.Focus();
                Ui.Press(window, "ENTER");
                Ui.WaitFor(() => window.EditorArea.Active?.DatabaseTable is { Reading: false, Table.Name: "orders" }, () => window.Database.Said);
                window.EditorArea.Activate(diagram.Document);
                Execute(kind, connection, "CREATE TABLE shipments (id int PRIMARY KEY, order_id int, FOREIGN KEY (order_id) REFERENCES orders(id))");
                Ui.Click(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DatabaseDiagramReadAgain"));
                Ui.WaitFor(() => diagram.Shown.Panel.Boxes.Any(b => b.Table.Name == "shipments"), () => window.Database.Said);
                Assert.Contains(diagram.Shown.Panel.Lines, l => l.From == "shipments" && l.To == "orders");

                // A second schema compared with it takes the script that makes it the same, objects, keys and defaults too, and is.
                Execute(kind, connection, "CREATE SEQUENCE invoice_numbers START WITH 1000");
                Execute(kind, connection, kind switch
                {
                    DatabaseKind.Postgres => "CREATE FUNCTION add_one(x int) RETURNS int LANGUAGE sql AS 'SELECT x + 1'",
                    DatabaseKind.MySql => "CREATE FUNCTION add_one(x int) RETURNS int DETERMINISTIC RETURN x + 1",
                    _ => "CREATE FUNCTION add_one(@x int) RETURNS int AS BEGIN RETURN @x + 1 END",
                });
                Execute(kind, connection, "CREATE TABLE notes (id int NOT NULL, code varchar(10) NOT NULL, flag int DEFAULT 0 NOT NULL, PRIMARY KEY (id))");
                Execute(kind, target, "CREATE TABLE notes (id int NOT NULL, code varchar(10) NOT NULL, flag int DEFAULT 1 NOT NULL, PRIMARY KEY (code))");
                List<DatabaseSource> kinds = [.. window.Database.Sources.Where(s => s.Kind == kind)];
                SchemaCompareDialog compare = CompareWith(window, kinds.FindIndex(s => s.Name == "Target"));
                Ui.ClickButton(compare, "Compare");
                Ui.WaitFor(() => !window.Database.Busy && window.EditorArea.Active?.Name == "Target.sql" && window.EditorArea.CodeEditor.Document.Snapshot.ToString().Contains("-- Makes", StringComparison.Ordinal),
                    () => window.Database.Said, TimeSpan.FromMinutes(1));
                string script = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
                foreach (string made in (string[])["orders", "items", "price", "legacy", "cheap", "note", "invoice_numbers", "add_one", "stamp", "PRIMARY KEY", "flag"])
                {
                    Assert.Contains(made, script, StringComparison.Ordinal);
                }
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the target's console");
                SqlConsole applying = window.EditorArea.ActiveGroup.Console!.Console!;
                // The console's checks read the script's earlier statements as the schema they leave: only a name none makes is marked.
                Passenger.App.Workspace.OpenDocument checking = window.EditorArea.Active!;
                CodeEditor writing = window.EditorArea.CodeEditor;
                writing.Select(new TextRange(script.Length, script.Length));
                window.KeyTextInput("\nSELECT nope FROM products;");
                Ui.WaitFor(() => checking.Markers.Any(m => m.Message.Contains("nope", StringComparison.Ordinal)),
                    () => $"nope marked, where the marks are {string.Join("; ", checking.Markers.Select(m => m.Message))}", TimeSpan.FromMinutes(1));
                Assert.True(checking.Markers.All(m => m.Message.Contains("nope", StringComparison.Ordinal)),
                    $"The script's own names were marked: {string.Join("; ", checking.Markers.Select(m => $"{m.Message} at {m.Range.Start}"))} in {checking.Text.Snapshot}");
                writing.Select(new TextRange(script.Length, writing.Document.Snapshot.Length));
                Ui.Press(window, "BACKSPACE");
                writing.Select(new TextRange(0, writing.Document.Snapshot.Length));
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run the script");
                Ui.WaitFor(() => !applying.Running && applying.Results.Count > 0, () => applying.Said, TimeSpan.FromMinutes(1));
                Assert.True(applying.Results.All(r => r.Error is null), $"{applying.Said} running {applying.Results.LastOrDefault()?.Statement}");
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
                compare = CompareWith(window, kinds.FindIndex(s => s.Name == "Target"));
                Ui.ClickButton(compare, "Compare");
                Ui.WaitFor(() => !window.Database.Busy && window.Database.Said.Contains("holds the same", StringComparison.Ordinal), () => window.Database.Said, TimeSpan.FromMinutes(1));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(SshFiles.HomeVariable, runsSshHome);
            if (agent is not null)
            {
                ChildProcesses.Kill(agent);
                agent.Dispose();
            }
            File.Delete(agentSocket);
            Docker.Remove(name, ssh, inner, second);
            Docker.Ask(["network", "rm", name]);
        }
    }

    /// <summary>Show Schema Diagram on the shop's connection, waited for until its boxes are laid out.</summary>
    private static SchemaDiagramTab Diagram(MainWindow window)
    {
        window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
        Assert.True(window.Perform(AppActions.DatabaseDiagram), "Show Schema Diagram did not run");
        Ui.WaitFor(() => window.EditorArea.Active?.DatabaseTab is SchemaDiagramTab { Shown.Panel.Boxes.Count: > 0 }, () => window.Database.Said);
        return (SchemaDiagramTab)window.EditorArea.Active!.DatabaseTab!;
    }

    /// <summary>Compare Schemas… on the chosen row, its dialog's target the connection at <paramref name="at"/>.</summary>
    private static SchemaCompareDialog CompareWith(MainWindow window, int at)
    {
        Assert.True(window.Perform(AppActions.DatabaseCompare), "Compare Schemas did not run");
        Ui.WaitFor(() => window.OwnedWindows.OfType<SchemaCompareDialog>().Any(d => d.IsVisible), "the compare dialog");
        SchemaCompareDialog dialog = window.OwnedWindows.OfType<SchemaCompareDialog>().Single(d => d.IsVisible);
        dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "CompareTarget").SelectedIndex = at;
        return dialog;
    }

    private static int Published(string container, int port) =>
        int.Parse(Docker.Run("port", container, port.ToString(CultureInfo.InvariantCulture)).Trim().Split(':')[^1], CultureInfo.InvariantCulture);

    /// <summary>Whether the second schema takes a connection, making its tables the first time it does: products with a name
    /// shorter, no price and a note, and a table the shop lacks.</summary>
    private static bool MadeOtherwise(DatabaseKind kind, string connection)
    {
        try
        {
            if (kind == DatabaseKind.SqlServer)
            {
                Execute(kind, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection) { InitialCatalog = "master", ConnectTimeout = 3 }.ConnectionString,
                    "IF DB_ID('target') IS NULL CREATE DATABASE target");
            }
            Execute(kind, connection, "CREATE TABLE products (id int PRIMARY KEY, name varchar(20) NOT NULL, note varchar(20))");
            Execute(kind, connection, "CREATE TABLE legacy (id int PRIMARY KEY)");
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <summary>An SSH home of the test's own: ~/.ssh/config naming "outer", the first bastion by its published port under the
    /// key alias "jumphost", "inner", the second, through it, and "proxied", the second through a ProxyCommand of ssh's own
    /// (given the test's configuration, known hosts and key alone), its port from the system's file under a Match exec;
    /// every host's keys from the test's agent; and known_hosts knowing the first bastion's keys.</summary>
    private static string SshHome(int outerPort, string agentSocket, string hostKeys, string keyFile)
    {
        string home = TestFolders.Create("ssh-home");
        string ssh = Directory.CreateDirectory(Path.Combine(home, ".ssh")).FullName;
        string config = Path.Combine(ssh, "config");
        string known = Path.Combine(ssh, "known_hosts");
        File.WriteAllText(config, $"""
            Host outer
              HostName 127.0.0.1
              Port {outerPort}
              User passenger
              HostKeyAlias jumphost
            Host inner
              HostName bastion
              Port 2222
              User passenger
              ProxyJump outer
            Host proxied
              HostName bastion
              User passenger
              ProxyCommand ssh -F "{config}" -o BatchMode=yes -o UserKnownHostsFile="{known}" -o GlobalKnownHostsFile=/dev/null -o IdentitiesOnly=yes -o IdentityFile="{keyFile}.pub" -W %h:%p outer
            Host *
              IdentityAgent "{agentSocket}"

            """);
        File.WriteAllText(Path.Combine(home, "system_ssh_config"), "Match originalhost proxied exec \"test %h = bastion\"\n  Port 2222\n");
        File.WriteAllLines(known, hostKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ')).Select(key => $"jumphost {key[0]} {key[1]}"));
        return home;
    }

    /// <summary>A user only a client certificate signs in as: PostgreSQL's by its certificate's name, MariaDB's requiring one.</summary>
    private static void Certified(DatabaseKind kind, string container, string connection)
    {
        if (kind == DatabaseKind.Postgres)
        {
            Docker.Run("exec", "-u", "postgres", container, "sh", "-c", "sed -i '1i hostssl all certified all cert' \"$PGDATA/pg_hba.conf\"");
            Execute(kind, connection, "CREATE ROLE certified LOGIN");
            Execute(kind, connection, "GRANT SELECT ON ALL TABLES IN SCHEMA public TO certified");
            Execute(kind, connection, "SELECT pg_reload_conf()");
        }
        else if (kind == DatabaseKind.MySql)
        {
            Execute(kind, connection, "CREATE USER 'certified'@'%' REQUIRE X509");
            Execute(kind, connection, "GRANT SELECT ON shop.* TO 'certified'@'%'");
        }
    }

    /// <summary>Writes an authority's certificate (ca.crt), a server's for "db" alone that it signed with its key (server.crt,
    /// server.key), and a client's for the user "certified" with its key encrypted (client.crt, client.key), into a folder.</summary>
    private static void Certify(string folder)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using RSA authorityKey = RSA.Create(2048);
        CertificateRequest authorityAsk = new("CN=Passenger Test Authority", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityAsk.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityAsk.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using X509Certificate2 authority = authorityAsk.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        using RSA serverKey = RSA.Create(2048);
        CertificateRequest serverAsk = new("CN=db", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName("db");
        serverAsk.CertificateExtensions.Add(names.Build());
        serverAsk.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        serverAsk.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverAsk.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using X509Certificate2 server = serverAsk.Create(authority, now.AddDays(-1), now.AddDays(29), RandomNumberGenerator.GetBytes(16));
        using RSA clientKey = RSA.Create(2048);
        CertificateRequest clientAsk = new("CN=certified", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        clientAsk.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        clientAsk.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        clientAsk.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        using X509Certificate2 client = clientAsk.Create(authority, now.AddDays(-1), now.AddDays(29), RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(folder, "client.crt"), client.ExportCertificatePem() + "\n");
        File.WriteAllText(Path.Combine(folder, "client.key"), clientKey.ExportEncryptedPkcs8PrivateKeyPem(ClientKeyPassword,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)) + "\n");
        File.WriteAllText(Path.Combine(folder, "ca.crt"), authority.ExportCertificatePem() + "\n");
        File.WriteAllText(Path.Combine(folder, "server.crt"), server.ExportCertificatePem() + "\n");
        File.WriteAllText(Path.Combine(folder, "server.key"), serverKey.ExportPkcs8PrivateKeyPem() + "\n");
    }

    /// <summary>Writes an RSA private key file as ssh-keygen's PEM does, returning its public key as authorized_keys takes it.</summary>
    private static string SshKey(string file)
    {
        using RSA key = RSA.Create(2048);
        File.WriteAllText(file, key.ExportRSAPrivateKeyPem() + "\n");
        if (!OperatingSystem.IsWindows())
        {
            // ssh-add takes only a key no one else may read.
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        RSAParameters parts = key.ExportParameters(false);
        using MemoryStream blob = new();
        byte[] length = new byte[4];
        foreach (byte[] field in (byte[][])[System.Text.Encoding.ASCII.GetBytes("ssh-rsa"), Unsigned(parts.Exponent!), Unsigned(parts.Modulus!)])
        {
            BinaryPrimitives.WriteInt32BigEndian(length, field.Length);
            blob.Write(length);
            blob.Write(field);
        }
        return "ssh-rsa " + Convert.ToBase64String(blob.ToArray()) + " passenger-test";

        // SSH's mpint: a positive number whose first byte has its high bit set leads with a zero.
        static byte[] Unsigned(byte[] number) => number[0] >= 0x80 ? [0, .. number] : number;
    }

    /// <summary>The window's one control of a name.</summary>
    private static T Tab<T>(MainWindow window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

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

    /// <summary>ClickHouse in a container of the test's own: its connection found in the folder's settings by its project's
    /// driver, its explorer down to a table's key column with its sorting key, a table's rows and DDL, a console's query run
    /// and explained as a plan's tree, completion of a table's columns, and a table's changes and a manual transaction each
    /// refused, saying why, as ClickHouse writes no row by its key and runs each statement alone.</summary>
    private static void ClickHouse()
    {
        string name = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}dbclickhouse";
        try
        {
            Docker.Run(["run", "-d", "--name", name, "--label", Docker.RunLabel, "-p", "127.0.0.1::8123", "-e", "CLICKHOUSE_DB=shop", "-e", "CLICKHOUSE_USER=passenger",
                "-e", "CLICKHOUSE_PASSWORD=" + Password, "-e", "CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT=1", ClickHouseImage]);
            string connection = $"Host=127.0.0.1;Port={Published(name, 8123)};Database=shop;Username=passenger;Password={Password}";
            Ui.WaitFor(() => ClickHouseMade(connection), $"{ClickHouseImage} to take connections and hold the shop's tables", TimeSpan.FromMinutes(3));
            string folder = TestFolders.Create("database-clickhouse");
            TestFolders.Write(folder, "Shop.csproj", TestProjects.Console().With("  <ItemGroup>\n    <PackageReference Include=\"ClickHouse.Driver\" Version=\"1.5.0\" />\n  </ItemGroup>\n"));
            TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
            TestFolders.Write(folder, "appsettings.json", $$$"""{"ConnectionStrings":{"Shop":"{{{connection}}}"}}""");
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                window.ShowToolWindow(ToolWindowIds.Database);
                Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy && window.Database.Sources.Any(s => s.Name == "Shop"), () => window.Database.Said);
                Assert.Equal(DatabaseKind.ClickHouse, window.Database.Sources.Single(s => s.Name == "Shop").Kind);
                foreach (string row in (string[])["Shop", "shop", "Tables", "products", "Columns"])
                {
                    DatabaseTests.Open(window, row);
                }
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.Column && r.Title == "id" && r.Detail.Contains("primary key", StringComparison.Ordinal));
                DatabaseTests.Open(window, "Keys");
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.Key && r.Title == "ORDER BY · id");

                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "products"));
                Assert.True(window.Perform(AppActions.DatabaseOpenTable), "Open Table Data did not run");
                Ui.WaitFor(() => window.EditorArea.Active?.DatabaseTable is { Reading: false, Rows.RowCount: 3 }, () => $"products' rows ({window.Database.Said})");
                Assert.Equal(Refusals.NoRowEdits("ClickHouse"), window.EditorArea.Active!.DatabaseTable!.WhyNotEdited);
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "products"));
                Assert.True(window.Perform(AppActions.DatabaseShowDdl), "Show DDL did not run");
                Ui.WaitFor(() => window.EditorArea.Active?.Name.EndsWith("products.sql", StringComparison.Ordinal) == true && !window.Database.Busy, () => window.Database.Said);
                Assert.Contains("ENGINE = MergeTree", window.EditorArea.Active!.Text.Snapshot.ToString(), StringComparison.Ordinal);

                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open Query Console did not run");
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the console with its results under it");
                SqlConsole console = window.EditorArea.ActiveGroup.Console!.Console!;
                Replace(window, "SELECT name, price FROM products WHERE id < 3 ORDER BY id");
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Ui.WaitFor(() => !console.Running && console.Results.Count == 1, () => console.Said, TimeSpan.FromMinutes(1));
                Assert.True(console.Results[0].Error is null, console.Said);
                Assert.Equal(["tea", "coffee"], Enumerable.Range(0, 2).Select(r => console.Results[0].Sets[0].Cell(r, 0)));
                Assert.DoesNotContain(Password, console.Said, StringComparison.Ordinal);
                Assert.True(window.Perform(AppActions.DatabaseExplain), "Explain Plan did not run");
                Ui.WaitFor(() => !console.Running && console.Plan is not null, () => console.Said, TimeSpan.FromMinutes(1));
                Assert.Contains(SqlPlans.Each(console.Plan!), step => step.Operation.Contains("ReadFromMergeTree", StringComparison.Ordinal));
                Assert.Equal(Refusals.NoTransactions("ClickHouse"), window.Database.SetManual(console, true));

                CodeEditor editor = window.EditorArea.CodeEditor;
                Replace(window, "SELECT  FROM products p");
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
            Docker.Said("rm", "-f", name);
        }
    }

    /// <summary>MongoDB in a container of the test's own: its connection found by its URI, its explorer down to fields and
    /// indexes, its documents as rows (a missing field apart from null) with the chosen one's tree beside them, fields written,
    /// one added to a document that lacked it, a document deleted and one added, submitted, one changed since read refusing
    /// them; a console of its shell running a find and a pipeline, completion of the collections and a collection's calls, a
    /// read-only mark refusing a delete, and an agent's count read while its insert is refused.</summary>
    private static void Mongo()
    {
        string name = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}dbmongo";
        try
        {
            Docker.Run(["run", "-d", "--name", name, "--label", Docker.RunLabel, "-p", "127.0.0.1::27017", "-e", "MONGO_INITDB_ROOT_USERNAME=passenger",
                "-e", "MONGO_INITDB_ROOT_PASSWORD=" + Password, MongoImage]);
            string uri = $"mongodb://passenger:{Uri.EscapeDataString(Password)}@127.0.0.1:{Published(name, 27017)}/shop?authSource=admin";
            Ui.WaitFor(() => MongoMade(uri), $"{MongoImage} to take connections and hold the shop's orders", TimeSpan.FromMinutes(3));
            using MongoDB.Driver.MongoClient client = new(uri);
            MongoDB.Driver.IMongoCollection<MongoDB.Bson.BsonDocument> orders = client.GetDatabase("shop").GetCollection<MongoDB.Bson.BsonDocument>("orders");
            MongoDB.Bson.BsonDocument? Order(string named) =>
                MongoDB.Driver.IFindFluentExtensions.FirstOrDefault(MongoDB.Driver.IMongoCollectionExtensions.Find(orders, new MongoDB.Bson.BsonDocument("name", named)));
            string folder = TestFolders.Create("database-mongo");
            TestFolders.Write(folder, "Shop.csproj", TestProjects.Console());
            TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
            TestFolders.Write(folder, "appsettings.json", $$$"""{"ConnectionStrings":{"Shop":"{{{uri}}}"}}""");
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                window.ShowToolWindow(ToolWindowIds.Database);
                Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy && window.Database.Sources.Any(s => s.Name == "Shop"), () => window.Database.Said);
                Assert.Equal(DatabaseKind.MongoDb, window.Database.Sources.Single(s => s.Name == "Shop").Kind);
                foreach (string row in (string[])["Shop", "shop", "Collections", "orders", "Fields"])
                {
                    DatabaseTests.Open(window, row);
                }
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.Column && r.Title == "_id" && r.Detail.Contains("primary key", StringComparison.Ordinal));
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.Column && r.Title == "qty" && r.Detail.StartsWith("int", StringComparison.Ordinal));
                DatabaseTests.Open(window, "Indexes");
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.Index && r.Title.StartsWith("_id_", StringComparison.Ordinal));

                // The documents as rows, the chosen one whole beside them; a field written, one deleted and one added, then submitted.
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "orders"));
                Assert.True(window.Perform(AppActions.DatabaseOpenTable), "Open Table Data did not run");
                Ui.WaitFor(() => window.EditorArea.Active?.DatabaseTable is { Reading: false, Rows.RowCount: 3 }, () => $"the orders as rows ({window.Database.Said})");
                DatabaseTableTab data = window.EditorArea.Active!.DatabaseTable!;
                CellGrid grid = Tab<CellGrid>(window, "DatabaseTabResults");
                int named = Array.IndexOf(data.Rows!.Columns, "name");
                int qty = Array.IndexOf(data.Rows.Columns, "qty");
                int Row(string order) => Enumerable.Range(0, data.Rows!.RowCount).Single(r => data.Rows.Cell(r, named) == order);
                int note = Array.IndexOf(data.Rows.Columns, "note");
                Assert.True(data.Shown!.IsAbsent(Row("tea"), note) && !data.Shown.IsAbsent(Row("milk"), note) && data.Shown.Cell(Row("milk"), note) == "NULL",
                    "A field the document lacks reads as NULL does");
                Assert.Equal(["Add Document", "Delete Documents"], new[] { AppActions.DatabaseAddRow, AppActions.DatabaseDeleteRows }
                    .Select(id => window.Actions[id]!.TitleWhere(window.Actions.Context())));
                grid.Choose((Row("tea"), named), (Row("tea"), named));
                DocumentTree tree = Tab<DocumentTree>(window, "DatabaseTabDocument");
                Ui.WaitFor(() => tree.Rows.Any(r => r.Name == "address" && r.Value == "1 field"), () => string.Join(", ", tree.Rows.Select(r => $"{r.Name}={r.Value}")));
                Assert.Contains(tree.Rows, r => r.Name == "_id" && r.Kind == "objectId" && r.Value.StartsWith("ObjectId(", StringComparison.Ordinal));
                Write(window, grid, (Row("tea"), qty), "7");
                Write(window, grid, (Row("coffee"), note), "decaf");
                Assert.False(data.Shown.IsAbsent(Row("coffee"), note), "A field written into a document that lacked it reads as missing still");
                grid.Choose((Row("milk"), named), (Row("milk"), named));
                Assert.True(window.Perform(AppActions.DatabaseDeleteRows), "Delete Rows did not run");
                Write(window, grid, (data.Shown!.RowCount - 1, named), "water");
                Assert.Equal("2 documents changed, 1 document deleted, 1 document added pending", data.EditsSaid);
                Assert.True(window.Perform(AppActions.DatabaseSubmit), "Submit did not run");
                Ui.WaitFor(() => data.Edits.IsEmpty && !data.Reading, () => data.Said);
                Assert.Equal(MongoDB.Bson.BsonType.Int32, Order("tea")!["qty"].BsonType);
                Assert.Equal(7, Order("tea")!["qty"].AsInt32);
                Assert.Null(Order("milk"));
                Assert.NotNull(Order("water"));
                Assert.Equal("decaf", Order("coffee")!["note"].AsString);
                Assert.False(Order("tea")!.Contains("note"), "A field left missing was written");
                orders.UpdateOne(new MongoDB.Bson.BsonDocument("name", "coffee"), new MongoDB.Bson.BsonDocument("$set", new MongoDB.Bson.BsonDocument("qty", 6)));
                Write(window, grid, (Row("coffee"), qty), "9");
                Assert.True(window.Perform(AppActions.DatabaseSubmit), "Submit did not run");
                Ui.WaitFor(() => !data.Reading && data.Said.Contains("changed since it was read", StringComparison.Ordinal), () => data.Said);
                Assert.Equal(6, Order("coffee")!["qty"].AsInt32);
                Assert.True(window.Perform(AppActions.DatabaseRevert), "Revert did not run");

                // A console of MongoDB's shell: a find and a pipeline, the documents' tree beside the rows.
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open Query Console did not run");
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the console with its results under it");
                Assert.EndsWith(".mongodb", window.EditorArea.Active!.Path, StringComparison.Ordinal);
                SqlConsole console = window.EditorArea.ActiveGroup.Console!.Console!;
                Run(window, "db.orders.find({ qty: { $gt: 1 } }).sort({ qty: -1 })");
                Assert.True(console.Results is [{ Error: null }], console.Said);
                DatabaseRows found = console.Results[0].Sets[0];
                Assert.Equal(["tea", "coffee"], Enumerable.Range(0, found.RowCount).Select(r => found.Cell(r, Array.IndexOf(found.Columns, "name"))));
                Ui.WaitFor(() => Tab<DocumentTree>(window, "ConsoleDocument").Rows.Any(r => r.Name == "name" && r.Value == "tea"), "the first document's tree beside the console's rows");
                Run(window, "db.orders.aggregate([{ $group: { _id: null, total: { $sum: \"$qty\" } } }])");
                Assert.True(console.Results is [{ Error: null, Sets: [{ RowCount: 1 } total] }] && total.Cell(0, Array.IndexOf(total.Columns, "total")) == "13", console.Said);

                CodeEditor editor = window.EditorArea.CodeEditor;
                Replace(window, "db");
                window.KeyTextInput(".");
                Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("orders") && window.Assist.CompletionLabels.Contains("getCollectionNames"),
                    () => $"the collections after db., where the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
                Ui.Press(window, "ESCAPE");
                Replace(window, "db.orders");
                window.KeyTextInput(".");
                Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("find") && window.Assist.CompletionLabels.Contains("aggregate"),
                    () => $"a collection's calls after db.orders., where the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
                Ui.Press(window, "ESCAPE");

                // Marked read-only, a delete is refused; an agent reads a count, its insert refused.
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
                Assert.True(window.Perform(AppActions.DatabaseReadOnly), "Read-Only Connection did not run");
                Replace(window, "db.orders.deleteMany({})");
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Ui.WaitFor(() => console.Said == Refusals.ReadOnlyConnection("Shop"), () => console.Said);
                Assert.Equal(3, orders.CountDocuments(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty));
                Passenger.App.Mcp.PassengerTools tools = new(window);
                object counted = Ui.Await(tools.Database(connection: "Shop", sql: "db.orders.countDocuments({})"), "an agent's count of the orders", TimeSpan.FromMinutes(1));
                Assert.Equal("3", Assert.Single(Assert.IsType<Passenger.App.Mcp.DatabaseQueryResult[]>(counted)).Sets[0].Rows[0][0]);
                Task<object> inserting = tools.Database(connection: "Shop", sql: "db.orders.insertOne({ name: 'x' })");
                Ui.WaitFor(() => inserting.IsCompleted, "an agent's insert to be refused");
                Assert.True(inserting.Exception?.InnerException?.Message.Contains("changes data", StringComparison.Ordinal) == true, $"An agent's insert answered: {inserting.Status}");
                Assert.DoesNotContain(Password, console.Said + window.Database.Said, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Docker.Said("rm", "-f", name);
        }
    }

    /// <summary>Whether MongoDB takes connections, the shop's orders put back as the test begins with them once it does.</summary>
    private static bool MongoMade(string uri)
    {
        try
        {
            MongoDB.Driver.MongoClientSettings settings = MongoDB.Driver.MongoClientSettings.FromConnectionString(uri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            using MongoDB.Driver.MongoClient client = new(settings);
            MongoDB.Driver.IMongoCollection<MongoDB.Bson.BsonDocument> orders = client.GetDatabase("shop").GetCollection<MongoDB.Bson.BsonDocument>("orders");
            orders.DeleteMany(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty);
            orders.InsertMany([
                MongoDB.Bson.BsonDocument.Parse("{ name: 'tea', qty: 2, tags: ['hot'], address: { city: 'Oslo' } }"),
                MongoDB.Bson.BsonDocument.Parse("{ name: 'coffee', qty: 5, tags: ['hot', 'strong'] }"),
                MongoDB.Bson.BsonDocument.Parse("{ name: 'milk', qty: 1, note: null }"),
            ]);
            return true;
        }
        catch (Exception unready) when (unready is MongoDB.Driver.MongoException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Cosmos DB's emulator in a container of the test's own: its connection found in the folder's settings, its
    /// explorer down to a container's fields and its partition key, its documents as rows with the chosen one's tree beside
    /// them, a field written and submitted, a console's query of the container its FROM names, completion of an alias's
    /// fields, and an agent's count.</summary>
    private static void Cosmos()
    {
        string name = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}dbcosmos";
        try
        {
            Docker.Run(["run", "-d", "--name", name, "--label", Docker.RunLabel, "-p", "127.0.0.1::8081", CosmosImage]);
            string endpoint = $"http://127.0.0.1:{Published(name, 8081)}/";
            Assert.True(Ui.Await(Waiting.Apart(() => Waiting.UntilAsync(() => CosmosMadeAsync(endpoint), TimeSpan.FromMinutes(5))),
                $"{CosmosImage} to take connections and hold the shop's orders", TimeSpan.FromMinutes(6)), $"{CosmosImage} never took connections");
            using Microsoft.Azure.Cosmos.CosmosClient cosmos = new(endpoint, CosmosKey, new Microsoft.Azure.Cosmos.CosmosClientOptions
            {
                ConnectionMode = Microsoft.Azure.Cosmos.ConnectionMode.Gateway, LimitToEndpoint = true,
            });
            Microsoft.Azure.Cosmos.Container orders = cosmos.GetContainer("shop", "orders");
            string folder = TestFolders.Create("database-cosmos");
            TestFolders.Write(folder, "Shop.csproj", TestProjects.Console());
            TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
            TestFolders.Write(folder, "appsettings.json", $$$"""{"ConnectionStrings":{"Shop":"AccountEndpoint={{{endpoint}}};AccountKey={{{CosmosKey}}};Database=shop"}}""");
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                window.ShowToolWindow(ToolWindowIds.Database);
                Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy && window.Database.Sources.Any(s => s.Name == "Shop"), () => window.Database.Said);
                Assert.Equal(DatabaseKind.CosmosDb, window.Database.Sources.Single(s => s.Name == "Shop").Kind);
                foreach (string row in (string[])["Shop", "shop", "Containers", "orders", "Keys"])
                {
                    DatabaseTests.Open(window, row);
                }
                Assert.Contains(window.DatabaseExplorer.Rows, r => r.Kind == DatabaseNodeKind.Key && r.Title == "partition key /category");

                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == "orders"));
                Assert.True(window.Perform(AppActions.DatabaseOpenTable), "Open Table Data did not run");
                Ui.WaitFor(() => window.EditorArea.Active?.DatabaseTable is { Reading: false, Rows.RowCount: 3 }, () => $"the orders as rows ({window.EditorArea.Active?.DatabaseTable?.Said})");
                DatabaseTableTab data = window.EditorArea.Active!.DatabaseTable!;
                CellGrid grid = Tab<CellGrid>(window, "DatabaseTabResults");
                int named = Array.IndexOf(data.Rows!.Columns, "name");
                int qty = Array.IndexOf(data.Rows.Columns, "qty");
                int tea = Enumerable.Range(0, data.Rows.RowCount).Single(r => data.Rows.Cell(r, named) == "tea");
                grid.Choose((tea, named), (tea, named));
                DocumentTree tree = Tab<DocumentTree>(window, "DatabaseTabDocument");
                Ui.WaitFor(() => tree.Rows.Any(r => r.Name == "category" && r.Value == "drink"), () => string.Join(", ", tree.Rows.Select(r => $"{r.Name}={r.Value}")));
                Write(window, grid, (tea, qty), "7");
                Assert.True(window.Perform(AppActions.DatabaseSubmit), "Submit did not run");
                Ui.WaitFor(() => data.Edits.IsEmpty && !data.Reading, () => data.Said);
                Microsoft.Azure.Cosmos.ItemResponse<Dictionary<string, object>> written = Ui.Await(orders.ReadItemAsync<Dictionary<string, object>>("1", new Microsoft.Azure.Cosmos.PartitionKey("drink")),
                    "the tea order as Cosmos DB has it");
                Assert.Equal(7, Convert.ToInt32(written.Resource["qty"], CultureInfo.InvariantCulture));

                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Shop"));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open Query Console did not run");
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the console with its results under it");
                SqlConsole console = window.EditorArea.ActiveGroup.Console!.Console!;
                Run(window, "SELECT o.name, o.qty FROM orders o WHERE o.qty > 1");
                Assert.True(console.Results is [{ Error: null }], console.Said);
                DatabaseRows found = console.Results[0].Sets[0];
                Assert.Equal(["coffee", "tea"], Enumerable.Range(0, found.RowCount).Select(r => found.Cell(r, Array.IndexOf(found.Columns, "name"))).Order());
                CodeEditor editor = window.EditorArea.CodeEditor;
                Replace(window, "SELECT  FROM orders o");
                editor.Selections = SelectionSet.Single("SELECT ".Length);
                window.KeyTextInput("o");
                window.KeyTextInput(".");
                Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("qty") && window.Assist.CompletionLabels.Contains("category"),
                    () => $"the orders' fields after o., where the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
                Ui.Press(window, "ESCAPE");
                object counted = Ui.Await(new Passenger.App.Mcp.PassengerTools(window).Database(connection: "Shop", sql: "SELECT VALUE COUNT(1) FROM orders o"),
                    "an agent's count of the orders", TimeSpan.FromMinutes(1));
                Assert.Equal("3", Assert.Single(Assert.IsType<Passenger.App.Mcp.DatabaseQueryResult[]>(counted)).Sets[0].Rows[0][0]);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Docker.Said("rm", "-f", name);
        }
    }

    /// <summary>Whether the emulator takes connections, the shop's orders put back as the test begins with them once it does.</summary>
    private static async Task<bool> CosmosMadeAsync(string endpoint)
    {
        try
        {
            using Microsoft.Azure.Cosmos.CosmosClient cosmos = new(endpoint, CosmosKey, new Microsoft.Azure.Cosmos.CosmosClientOptions
            {
                ConnectionMode = Microsoft.Azure.Cosmos.ConnectionMode.Gateway, LimitToEndpoint = true, RequestTimeout = TimeSpan.FromSeconds(5),
            });
            Microsoft.Azure.Cosmos.Database shop = (await cosmos.CreateDatabaseIfNotExistsAsync("shop")).Database;
            Microsoft.Azure.Cosmos.Container orders = (await shop.CreateContainerIfNotExistsAsync("orders", "/category")).Container;
            foreach (Dictionary<string, object> order in (Dictionary<string, object>[])[
                new() { ["id"] = "1", ["category"] = "drink", ["name"] = "tea", ["qty"] = 2 },
                new() { ["id"] = "2", ["category"] = "drink", ["name"] = "coffee", ["qty"] = 5 },
                new() { ["id"] = "3", ["category"] = "food", ["name"] = "bread", ["qty"] = 1 },
            ])
            {
                await orders.UpsertItemAsync(order, new Microsoft.Azure.Cosmos.PartitionKey((string)order["category"]));
            }
            return true;
        }
        catch (Exception unready) when (unready is Microsoft.Azure.Cosmos.CosmosException or System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    /// <summary>Redis (or Valkey) in a container of the test's own: its connection found in the folder's settings by its project's
    /// client, its keys grouped by type with their times to live, each type's value opened, written, added to and deleted
    /// from and submitted, a value changed since it was read refusing them; a key's time to live set; a console of its
    /// commands, completion of its commands and keys, a read-only mark refusing a delete, and an agent's read.</summary>
    private static void Redis(string image)
    {
        string name = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}db{(image == ValkeyImage ? "valkey" : "redis")}";
        try
        {
            Docker.Run(["run", "-d", "--name", name, "--label", Docker.RunLabel, "-p", "127.0.0.1::6379", image, "--requirepass", Password]);
            string configuration = $"127.0.0.1:{Published(name, 6379)},password={Password}";
            Assert.True(Ui.Await(Waiting.Apart(() => Waiting.UntilAsync(() => RedisMadeAsync(configuration), TimeSpan.FromMinutes(2))),
                $"{image} to take connections and hold the shop's keys", TimeSpan.FromMinutes(3)), $"{image} never took connections");
            using StackExchange.Redis.ConnectionMultiplexer redis = Ui.Await(StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(configuration), "the test's own client");
            StackExchange.Redis.IDatabase keys = redis.GetDatabase();
            string folder = TestFolders.Create("database-redis");
            TestFolders.Write(folder, "Shop.csproj", TestProjects.Console().With("  <ItemGroup>\n    <PackageReference Include=\"StackExchange.Redis\" Version=\"3.4.35\" />\n  </ItemGroup>\n"));
            TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
            TestFolders.Write(folder, "appsettings.json", $$$"""{"ConnectionStrings":{"Cache":"{{{configuration}}}"}}""");
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                window.ShowToolWindow(ToolWindowIds.Database);
                Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy && window.Database.Sources.Any(s => s.Name == "Cache"), () => window.Database.Said);
                Assert.Equal(DatabaseKind.Redis, window.Database.Sources.Single(s => s.Name == "Cache").Kind);
                DatabaseTests.Open(window, "Cache");
                DatabaseTests.Open(window, "db0");
                Assert.Equal(["Strings", "Hashes", "Lists", "Sets", "Sorted sets", "Streams"],
                    window.DatabaseExplorer.Rows.Where(r => r.Kind == DatabaseNodeKind.Group).Select(r => r.Title));
                foreach (string group in (string[])["Strings", "Hashes", "Lists", "Sets", "Sorted sets", "Streams"])
                {
                    DatabaseTests.Open(window, group);
                }
                DatabaseNode Key(string key) => window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Table && r.Title == key);
                Assert.StartsWith("expires in", Key("greeting").Detail, StringComparison.Ordinal);
                Assert.Equal("no expiry", Key("user:1").Detail);

                // Each type's value opened, its changes submitted in one transaction.
                DatabaseTableTab Open(string key, int rows)
                {
                    window.DatabaseExplorer.Choose(Key(key));
                    Assert.True(window.Perform(AppActions.DatabaseOpenTable), $"Open Value of {key} did not run");
                    Ui.WaitFor(() => window.EditorArea.Active?.DatabaseTable is { Reading: false, Rows: { } read } tab && tab.Table.Name == key && read.RowCount == rows,
                        () => $"{key}'s value as rows ({window.EditorArea.Active?.DatabaseTable?.Said})");
                    return window.EditorArea.Active!.DatabaseTable!;
                }
                void Submit(DatabaseTableTab data)
                {
                    Assert.True(window.Perform(AppActions.DatabaseSubmit), "Submit did not run");
                    Ui.WaitFor(() => data.Edits.IsEmpty && !data.Reading, () => data.Said);
                }
                int RowOf(DatabaseTableTab data, string first) => Enumerable.Range(0, data.Rows!.RowCount).Single(r => data.Rows.Cell(r, 0) == first);
                CellGrid Grid() => Tab<CellGrid>(window, "DatabaseTabResults");

                DatabaseTableTab hash = Open("user:1", 2);
                Write(window, Grid(), (RowOf(hash, "city"), 1), "Bergen");
                Grid().Choose((RowOf(hash, "name"), 0), (RowOf(hash, "name"), 0));
                Assert.True(window.Perform(AppActions.DatabaseDeleteRows), "Delete Rows did not run");
                Write(window, Grid(), (hash.Shown!.RowCount - 1, 0), "role");
                Write(window, Grid(), (hash.Shown!.RowCount - 2, 1), "admin");
                Submit(hash);
                Assert.Equal(("Bergen", "admin", false), ((string?)keys.HashGet("user:1", "city"), (string?)keys.HashGet("user:1", "role"), keys.HashExists("user:1", "name")));
                keys.HashSet("user:1", "city", "Paris");
                Write(window, Grid(), (RowOf(hash, "city"), 1), "Rome");
                Assert.True(window.Perform(AppActions.DatabaseSubmit), "Submit did not run");
                Ui.WaitFor(() => !hash.Reading && hash.Said.Contains("changed since it was read", StringComparison.Ordinal), () => hash.Said);
                Assert.Equal("Paris", (string?)keys.HashGet("user:1", "city"));
                Assert.True(window.Perform(AppActions.DatabaseRevert), "Revert did not run");

                DatabaseTableTab list = Open("queue", 3);
                Write(window, Grid(), (1, 1), "B");
                Grid().Choose((2, 0), (2, 0));
                Assert.True(window.Perform(AppActions.DatabaseDeleteRows), "Delete Rows did not run");
                Write(window, Grid(), (list.Shown!.RowCount - 1, 1), "d");
                Submit(list);
                Assert.Equal(["a", "B", "d"], keys.ListRange("queue").Select(v => (string?)v));

                DatabaseTableTab set = Open("tags", 2);
                Grid().Choose((RowOf(set, "blue"), 0), (RowOf(set, "blue"), 0));
                Assert.True(window.Perform(AppActions.DatabaseDeleteRows), "Delete Rows did not run");
                Write(window, Grid(), (set.Shown!.RowCount - 1, 0), "green");
                Submit(set);
                Assert.Equal(["green", "red"], keys.SetMembers("tags").Select(v => (string?)v).Order());

                DatabaseTableTab sorted = Open("scores", 2);
                Write(window, Grid(), (RowOf(sorted, "bob"), 1), "7");
                Submit(sorted);
                Assert.Equal(7, keys.SortedSetScore("scores", "bob"));

                DatabaseTableTab stream = Open("events", 1);
                int who = Array.IndexOf(stream.Rows!.Columns, "who");
                Write(window, Grid(), (stream.Shown!.RowCount - 1, who), "bob");
                Submit(stream);
                Assert.Equal(2, keys.StreamLength("events"));

                DatabaseTableTab text = Open("greeting", 1);
                Write(window, Grid(), (0, 1), "hi");
                Submit(text);
                Assert.True((string?)keys.StringGet("greeting") == "hi" && keys.KeyTimeToLive("greeting") is { } kept && kept > TimeSpan.Zero,
                    "Writing a string's value took its time to live away");

                // A key's time to live set, then taken away.
                window.DatabaseExplorer.Choose(Key("tags"));
                Assert.True(window.Perform(AppActions.DatabaseExpire), "Set Time to Live did not run");
                Ui.WaitFor(() => window.Popup is Passenger.App.Vcs.NamePopup, "the time to live's question");
                Passenger.App.Vcs.NamePopup life = (Passenger.App.Vcs.NamePopup)window.Popup!;
                life.Text = "600";
                Task given = life.SubmitAsync();
                Ui.Landed(() => given.IsCompleted, "the time to live given");
                Ui.WaitFor(() => keys.KeyTimeToLive("tags") is { TotalSeconds: > 500 }, () => window.Database.Said);

                // A console of Redis's commands, its replies as rows and completion of its commands and keys.
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Cache"));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open Query Console did not run");
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.Console?.Console is not null, "the console with its results under it");
                Assert.EndsWith(".redis", window.EditorArea.Active!.Path, StringComparison.Ordinal);
                SqlConsole console = window.EditorArea.ActiveGroup.Console!.Console!;
                Run(window, "HGETALL user:1");
                Assert.True(console.Results is [{ Error: null, Sets: [{ RowCount: 2 } pairs] }] && pairs.Columns is ["name", "value"], console.Said);
                Run(window, "ZRANGE scores 0 -1 WITHSCORES");
                Assert.True(console.Results is [{ Error: null, Sets: [{ RowCount: 2 } scored] }] && scored.Cell(0, 0) == "ada" && scored.Cell(1, 1) == "7", console.Said);
                CodeEditor editor = window.EditorArea.CodeEditor;
                editor.Select(new TextRange(0, editor.Document.Snapshot.Length));
                window.KeyTextInput("H");
                Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("HGETALL"), () => $"the commands, where the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
                Ui.Press(window, "ESCAPE");
                Replace(window, "HGETALL ");
                window.KeyTextInput("u");
                Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("user:1"), () => $"the keys, where the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
                Ui.Press(window, "ESCAPE");

                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Title == "Cache"));
                Assert.True(window.Perform(AppActions.DatabaseReadOnly), "Read-Only Connection did not run");
                Replace(window, "DEL tags");
                Assert.True(window.Perform(AppActions.DatabaseRun), "Run Statement did not run");
                Ui.WaitFor(() => console.Said == Refusals.ReadOnlyConnection("Cache"), () => console.Said);
                Assert.True(keys.KeyExists("tags"), "A read-only connection's DEL deleted the key");
                object read = Ui.Await(new Passenger.App.Mcp.PassengerTools(window).Database(connection: "Cache", sql: "GET greeting"), "an agent's read of a string", TimeSpan.FromMinutes(1));
                Assert.Equal("hi", Assert.Single(Assert.IsType<Passenger.App.Mcp.DatabaseQueryResult[]>(read)).Sets[0].Rows[0][0]);
                Assert.DoesNotContain(Password, console.Said + window.Database.Said, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Docker.Said("rm", "-f", name);
        }
    }

    /// <summary>Whether Redis takes connections, the shop's keys put back as the test begins with them once it does.</summary>
    private static async Task<bool> RedisMadeAsync(string configuration)
    {
        try
        {
            StackExchange.Redis.ConfigurationOptions options = StackExchange.Redis.ConfigurationOptions.Parse(configuration);
            options.ConnectTimeout = 2000;
            await using StackExchange.Redis.ConnectionMultiplexer redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(options);
            StackExchange.Redis.IDatabase keys = redis.GetDatabase();
            await keys.KeyDeleteAsync(["greeting", "user:1", "queue", "tags", "scores", "events"]);
            await keys.StringSetAsync("greeting", "hello", TimeSpan.FromHours(1));
            await keys.HashSetAsync("user:1", [new("name", "Ada"), new("city", "Oslo")]);
            await keys.ListRightPushAsync("queue", ["a", "b", "c"]);
            await keys.SetAddAsync("tags", ["red", "blue"]);
            await keys.SortedSetAddAsync("scores", [new("ada", 3), new("bob", 5)]);
            await keys.StreamAddAsync("events", [new("kind", "signup"), new("who", "ada")]);
            return true;
        }
        catch (Exception unready) when (unready is StackExchange.Redis.RedisException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Whether ClickHouse takes connections, its shop's table made and filled once it does.</summary>
    private static bool ClickHouseMade(string connection)
    {
        try
        {
            using ClickHouse.Driver.ADO.ClickHouseConnection made = new(connection);
            made.Open();
            foreach (string statement in (string[])["CREATE TABLE IF NOT EXISTS products (id UInt32, name String, price Float64) ENGINE = MergeTree ORDER BY id",
                "TRUNCATE TABLE products", "INSERT INTO products VALUES (1, 'tea', 2.5), (2, 'coffee', 3), (3, 'milk', 1)"])
            {
                using DbCommand command = made.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
            return true;
        }
        catch (Exception unready) when (unready is DbException or System.Net.Http.HttpRequestException or IOException or InvalidOperationException)
        {
            return false;
        }
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
