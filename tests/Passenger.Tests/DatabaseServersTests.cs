using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
/// connection asking before an UPDATE, and a connection made through an SSH bastion with its SSL verified, each through
/// its own driver. Skipped, naming the image, where the engine lacks it, as it is never pulled.</summary>
public sealed class DatabaseServersTests
{
    private const string Password = "Passenger-db-7c1d";
    private const string SshPassword = "Passenger-ssh-4e2a";

    /// <summary>The bastion's image, pinned to the one the test was written against.</summary>
    private const string SshImage = "lscr.io/linuxserver/openssh-server@sha256:c8431b8982e7eb3935ebf9c46607d19c6417e39c12ce884cc66945df2e9334e8";

    [AvaloniaTheory]
    [InlineData(DatabaseKind.Postgres, "postgres:17-alpine")]
    [InlineData(DatabaseKind.MySql, "mariadb:11.4")]
    [InlineData(DatabaseKind.SqlServer, "mcr.microsoft.com/mssql/server:2022-latest")]
    public void A_server_is_found_explored_run_and_completed_through_its_driver(DatabaseKind kind, string image)
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        Assert.SkipWhen(Docker.Said("image", "inspect", image) is null, $"The engine lacks {image}, which this test never pulls: pull it to run the test");
        Assert.SkipWhen(Docker.Said("image", "inspect", SshImage) is null, $"The engine lacks {SshImage}, which this test never pulls: pull it to run the test");
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
                + "-c ssl=on -c ssl_cert_file=/certs/server.crt -c ssl_key_file=/certs/server.key"],
            DatabaseKind.MySql => [image, "--ssl-cert=/certs/server.crt", "--ssl-key=/certs/server.key"],
            _ => [image],
        };
        string ssh = name + "ssh";
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

                // A connection made by hand reaches "db" through the bastion, its host key trusted once, its SSL verified.
                Assert.True(window.Perform(AppActions.DatabaseAdd), "New Database Connection did not run");
                Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseConnectionDialog>().Any(d => d.IsVisible), "the connection dialog");
                DatabaseConnectionDialog dialog = window.OwnedWindows.OfType<DatabaseConnectionDialog>().Single(d => d.IsVisible);
                T Field<T>(string field) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == field);
                Field<ComboBox>("DatabaseProvider").SelectedIndex = (int)kind;
                Field<TextBox>("DatabaseConnectionName").Text = "Behind";
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
                    Field<TextBox>("DatabaseSshKey").Text = keyFile;
                }
                else
                {
                    Field<TextBox>("DatabaseSshPassword").Text = SshPassword;
                }
                Ui.ClickButton(dialog, "Connect");
                Ui.WaitFor(() => dialog.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible) || !dialog.IsVisible,
                    () => Field<TextBlock>("DatabaseConnectionError").Text ?? "the host key's question", TimeSpan.FromMinutes(1));
                ConfirmDialog trust = dialog.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible);
                Assert.Contains("SHA256:", string.Join(" ", trust.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), StringComparison.Ordinal);
                Ui.ClickButton(trust, "Trust");
                Ui.WaitFor(() => !dialog.IsVisible && window.Database.Sources.Any(s => s.Name == "Behind"), () => window.Database.Said);
                DatabaseSource behind = window.Database.Sources.Single(s => s.Name == "Behind");
                Assert.StartsWith("SHA256:", behind.Tunnel?.HostKey, StringComparison.Ordinal);
                Assert.Equal(kind == DatabaseKind.Postgres ? null : SshPassword, SystemCredentials.Read(DatabaseController.CredentialService, behind.TunnelAccount));
                window.DatabaseExplorer.Choose(window.DatabaseExplorer.Rows.Single(r => r.Kind == DatabaseNodeKind.Connection && r.Source.Id == behind.Id));
                Assert.True(window.Perform(AppActions.DatabaseConsole), "Open SQL Console did not run");
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
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Docker.Remove(name, ssh);
            Docker.Ask(["network", "rm", name]);
        }
    }

    /// <summary>Writes an authority's certificate (ca.crt), and a server's for "db" alone that it signed, with its key
    /// (server.crt, server.key), into a folder.</summary>
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
        File.WriteAllText(Path.Combine(folder, "ca.crt"), authority.ExportCertificatePem() + "\n");
        File.WriteAllText(Path.Combine(folder, "server.crt"), server.ExportCertificatePem() + "\n");
        File.WriteAllText(Path.Combine(folder, "server.key"), serverKey.ExportPkcs8PrivateKeyPem() + "\n");
    }

    /// <summary>Writes an RSA private key file as ssh-keygen's PEM does, returning its public key as authorized_keys takes it.</summary>
    private static string SshKey(string file)
    {
        using RSA key = RSA.Create(2048);
        File.WriteAllText(file, key.ExportRSAPrivateKeyPem() + "\n");
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
