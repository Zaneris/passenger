using System.Data.Common;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>A connection made or edited by hand: its database, server and sign-in, or a connection string over them; its
/// access; its SSL (<see cref="DatabaseSsl"/>); and an SSH tunnel through a bastion, whose host key is read and trusted
/// by the person here before the connection is kept (<see cref="SshTunnels"/>).</summary>
public sealed class DatabaseConnectionDialog : IdeWindow
{
    private readonly FormDropDown provider = new() { Name = "DatabaseProvider", ItemsSource = new[] { "SQLite", "PostgreSQL", "SQL Server", "MySQL / MariaDB" }, SelectedIndex = 1 };
    private readonly TextBox name = new() { Name = "DatabaseConnectionName", PlaceholderText = "Local shop" };
    private readonly TextBox host = new() { Name = "DatabaseHost", Text = "localhost" };
    private readonly NumericUpDown port = new() { Name = "DatabasePort", Minimum = 1, Maximum = 65535, Value = 5432, FormatString = "0" };
    private readonly TextBox database = new() { Name = "DatabaseName" };
    private readonly TextBox user = new() { Name = "DatabaseUser" };
    private readonly TextBox password = new() { Name = "DatabasePassword", PasswordChar = '•', RevealPassword = false };
    private readonly CheckBox integrated = new() { Name = "DatabaseIntegrated", Content = "Windows authentication" };
    private readonly FormDropDown mark = new() { Name = "DatabaseMark", ItemsSource = new[] { "Read and write", "Read-only", "Production: asks before a change" }, SelectedIndex = 0 };
    private readonly TextBox connection = new() { Name = "DatabaseConnectionString", PasswordChar = '•', RevealPassword = false, PlaceholderText = "Optional: overrides the fields above" };
    private readonly TextBlock error = new() { Name = "DatabaseConnectionError", TextWrapping = TextWrapping.Wrap };
    private readonly FormDropDown ssl = new() { Name = "DatabaseSsl", ItemsSource = DatabaseSsl.Titles, SelectedIndex = 0 };
    private readonly TextBox certificate = new() { Name = "DatabaseSslCertificate", PlaceholderText = "A PEM file, to verify by" };
    private readonly CheckBox tunnel = new() { Name = "DatabaseSshTunnel", Content = "Through an SSH tunnel" };
    private readonly TextBox sshHost = new() { Name = "DatabaseSshHost", PlaceholderText = "bastion.example.com" };
    private readonly NumericUpDown sshPort = new() { Name = "DatabaseSshPort", Minimum = 1, Maximum = 65535, Value = 22, FormatString = "0" };
    private readonly TextBox sshUser = new() { Name = "DatabaseSshUser" };
    private readonly TextBox sshKey = new() { Name = "DatabaseSshKey", PlaceholderText = "Optional: a private key file, else a password" };
    private readonly TextBox sshSecret = new() { Name = "DatabaseSshPassword", PasswordChar = '•', RevealPassword = false };
    private readonly Button connect;
    private readonly string folder;
    private readonly CancellationTokenSource closing = new();
    private readonly DatabaseSource? initial;

    /// <summary>Whether the person chose the SSL here, which is then written over what the connection string says.</summary>
    private bool sslChosen;

    public DatabaseConnectionDialog(string folder, DatabaseSource? initial = null)
    {
        this.initial = initial;
        this.folder = folder;
        Closed += (_, _) => closing.Cancel();
        Title = initial is null ? "Add Database Connection" : "Edit Database Connection";
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StackPanel fields = new() { Spacing = FormRows.Between };
        FormRows.Scope(fields);
        Row("Name", name);
        Row("Provider", provider);
        TextBlock databaseLabel = Row("Database", database);
        Row("Host", host);
        Row("Port", port);
        Row("User", user);
        Row("Password", password);
        Row(null, integrated);
        Row("Connection string", connection);
        Row("Access", mark);
        Grid sslRow = RowOf("SSL", ssl);
        Grid certificateRow = FormRows.Of("Certificate authority", BrowseRow.Of(certificate, () => BrowseAsync(certificate, "Certificate")), out TextBlock certificateLabel);
        fields.Children.Add(certificateRow);
        Grid tunnelRow = RowOf(null, tunnel);
        Grid[] sshRows = [RowOf("SSH host", sshHost), RowOf("SSH port", sshPort), RowOf("SSH user", sshUser),
            RowOf("SSH key file", BrowseRow.Of(sshKey, () => BrowseAsync(sshKey, "SSH Private Key"))), RowOf("Password or key passphrase", sshSecret)];
        provider.SelectionChanged += (_, _) => ShowProvider();
        integrated.IsCheckedChanged += (_, _) => ShowAuthentication();
        tunnel.IsCheckedChanged += (_, _) => ShowTunnel();
        ssl.SelectionChanged += (_, _) =>
        {
            sslChosen = true;
            ShowSsl();
        };
        certificate.TextChanged += (_, _) => sslChosen = true;
        connect = DialogFooter.Default(initial is null ? "Connect" : "Save and Connect");
        connect.Name = "DatabaseConnectionConnect";
        connect.Click += (_, _) => IdeErrors.Observe(ConnectAsync());
        DatabaseSource? Made()
        {
            try
            {
                ContainerSecrets.Mark(password.Text ?? string.Empty);
                MarkConnection(connection.Text ?? string.Empty);
                DatabaseKind kind = (DatabaseKind)provider.SelectedIndex;
                string written = Build(kind);
                MarkConnection(written);
                if (kind == DatabaseKind.Sqlite)
                {
                    SqliteConnectionStringBuilder sqlite = new(written);
                    if (string.IsNullOrWhiteSpace(sqlite.DataSource) || sqlite.DataSource == ":memory:" || sqlite.Mode == SqliteOpenMode.Memory)
                    {
                        throw new ArgumentException("Choose an existing SQLite database file");
                    }
                    sqlite.DataSource = PathNames.Canonical(Path.GetFullPath(sqlite.DataSource, folder));
                    written = sqlite.ConnectionString;
                }
                else if (sslChosen)
                {
                    written = DatabaseSsl.With(kind, written, (SslChoice)Math.Max(0, ssl.SelectedIndex), Located(certificate));
                }
                string title = name.Text?.Trim() is { Length: > 0 } named ? named
                    : TitleOf(kind, written);
                DatabaseMark marked = (DatabaseMark)Math.Max(0, mark.SelectedIndex);
                return initial is null ? new DatabaseSource(title, kind, folder) { ConnectionString = written, Mark = marked }
                    : initial with { Name = title, Kind = kind, ConnectionString = written, Mark = marked };
            }
            catch (Exception failed) when (failed is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
            {
                Say(failed.Message);
                return null;
            }
        }

        // Connect: the connection made, and its tunnel's host key read and trusted where it is new or its bastion moved.
        async Task ConnectAsync()
        {
            if (Made() is not { } made)
            {
                return;
            }
            if (tunnel.IsChecked != true || made.Kind == DatabaseKind.Sqlite)
            {
                Close(made with { Tunnel = null });
                return;
            }
            string host = sshHost.Text?.Trim() ?? string.Empty;
            string user = sshUser.Text?.Trim() ?? string.Empty;
            int port = sshPort.Value is { } given ? decimal.ToInt32(decimal.Truncate(given)) : 22;
            if (host.Length == 0 || user.Length == 0)
            {
                Say(Refusals.NoSshHost);
                return;
            }
            string? trusted = initial?.Tunnel is { } was && was.Host == host && was.Port == port ? was.HostKey : null;
            if (trusted is null)
            {
                connect.IsEnabled = false;
                Say($"Reading {host}'s SSH host key…");
                try
                {
                    (string kind, string fingerprint) = await BackgroundWork.Run(() => SshTunnels.HostKeyAsync(host, port, closing.Token), closing.Token);
                    error.IsVisible = false;
                    ConfirmDialog asked = new("Trust SSH Host Key", $"{host}:{port} gives its {kind} host key, whose fingerprint is {fingerprint}. Trust it for this "
                        + "connection? Compare it with the fingerprint the server's administrator gives first.", "Trust")
                    {
                        EnterAgrees = false,
                        PersonsAlone = Refusals.SshHostKeyByThePerson,
                    };
                    if (!await asked.ShowDialog<bool>(this))
                    {
                        return;
                    }
                    trusted = fingerprint;
                }
                catch (Exception failed) when (failed is IOException or OperationCanceledException)
                {
                    Say(failed.Message);
                    return;
                }
                finally
                {
                    connect.IsEnabled = true;
                }
            }
            string secret = sshSecret.Text ?? string.Empty;
            ContainerSecrets.Mark(secret);
            string? key = Located(sshKey);
            Close(made with { Tunnel = new SshTunnel(host, port, user) { KeyFile = key, HostKey = trusted }, TunnelSecret = secret.Length > 0 ? secret : null });
        }

        void Say(string words)
        {
            error.Text = ContainerSecrets.InSaid(words);
            error.IsVisible = true;
        }
        Content = DialogFooter.Page(new StackPanel
        {
            [DialogFooter.InsetProperty] = true,
            Spacing = FormRows.Between,
            Children =
            {
                fields,
                MutedText.Wrapped("A connection is kept for this folder; its passwords are kept in the system's credential store, never in a file."),
                error,
            },
        }, DialogFooter.OkCancel(connect, () => Close(null)));
        error.IsVisible = false;
        ShowProvider();
        if (initial is not null)
        {
            (SslChoice chosen, string? verifiedBy) = DatabaseSsl.Of(initial.Kind, initial.ConnectionString);
            ssl.SelectedIndex = (int)chosen;
            certificate.Text = verifiedBy;
            if (initial.Tunnel is { } through)
            {
                tunnel.IsChecked = true;
                sshHost.Text = through.Host;
                sshPort.Value = through.Port;
                sshUser.Text = through.User;
                sshKey.Text = through.KeyFile;
                sshSecret.PlaceholderText = "Unchanged";
            }
            sslChosen = false;
            name.Text = initial.Name;
            mark.SelectedIndex = (int)initial.Mark;
            password.PlaceholderText = initial.SecretKept ? "Unchanged" : null;
            provider.SelectedIndex = (int)initial.Kind;
            switch (initial.Kind)
            {
                case DatabaseKind.Sqlite:
                    database.Text = new SqliteConnectionStringBuilder(initial.ConnectionString).DataSource;
                    break;
                case DatabaseKind.Postgres:
                    NpgsqlConnectionStringBuilder pg = new(initial.ConnectionString);
                    host.Text = pg.Host; port.Value = pg.Port; database.Text = pg.Database; user.Text = pg.Username; password.Text = pg.Password;
                    break;
                case DatabaseKind.SqlServer:
                    SqlConnectionStringBuilder sql = new(initial.ConnectionString);
                    string[] address = sql.DataSource.Split(',', 2);
                    host.Text = address[0];
                    port.Value = address.Length == 2 && int.TryParse(address[1], out int number) ? number : 1433;
                    database.Text = sql.InitialCatalog; user.Text = sql.UserID; password.Text = sql.Password; integrated.IsChecked = sql.IntegratedSecurity;
                    break;
                case DatabaseKind.MySql:
                    MySqlConnectionStringBuilder mysql = new(initial.ConnectionString);
                    host.Text = mysql.Server; port.Value = mysql.Port; database.Text = mysql.Database; user.Text = mysql.UserID; password.Text = mysql.Password;
                    break;
            }
        }

        TextBlock Row(string? label, Control control)
        {
            fields.Children.Add(FormRows.Of(label, control, out TextBlock text));
            return text;
        }

        Grid RowOf(string? label, Control control)
        {
            Grid row = FormRows.Of(label, control, out _);
            fields.Children.Add(row);
            return row;
        }

        void ShowSsl() =>
            certificateRow.IsVisible = provider.SelectedIndex != (int)DatabaseKind.Sqlite && ssl.SelectedIndex >= (int)SslChoice.VerifyAuthority;

        void ShowTunnel()
        {
            bool through = tunnel.IsChecked == true && provider.SelectedIndex != (int)DatabaseKind.Sqlite;
            foreach (Grid row in sshRows)
            {
                row.IsVisible = through;
            }
        }

        void ShowProvider()
        {
            bool remote = provider.SelectedIndex != (int)DatabaseKind.Sqlite;
            sslRow.IsVisible = remote;
            tunnelRow.IsVisible = remote;
            ShowSsl();
            certificateLabel.Text = provider.SelectedIndex == (int)DatabaseKind.SqlServer ? "Server certificate" : "Certificate authority";
            ShowTunnel();
            foreach (Control control in new Control[] { host, port, user, password })
            {
                ((Control)control.Parent!).IsVisible = remote;
            }
            databaseLabel.Text = remote ? "Database" : "Database file";
            database.PlaceholderText = remote ? "shop" : "Full path, or a path relative to the open folder";
            port.Value = provider.SelectedIndex switch { (int)DatabaseKind.Postgres => 5432, (int)DatabaseKind.SqlServer => 1433, _ => 3306 };
            ((Control)integrated.Parent!).IsVisible = provider.SelectedIndex == (int)DatabaseKind.SqlServer;
            ShowAuthentication();
        }

        void ShowAuthentication()
        {
            bool credentials = provider.SelectedIndex != (int)DatabaseKind.SqlServer || integrated.IsChecked != true;
            user.IsEnabled = credentials;
            password.IsEnabled = credentials;
        }
    }

    /// <summary>The full path a field names: relative to the open folder, or to the home folder after "~/"; null for none.</summary>
    private string? Located(TextBox field) => field.Text?.Trim() switch
    {
        null or "" => null,
        ['~', '/' or '\\', .. var rest] => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rest),
        var written => Path.GetFullPath(written, folder),
    };

    /// <summary>A file chosen in the system's own panel for a field, from the open folder.</summary>
    private async Task BrowseAsync(TextBox field, string title)
    {
        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder),
        });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
        {
            field.Text = path;
        }
    }

    private static string TitleOf(DatabaseKind kind, string written) => kind switch
    {
        DatabaseKind.Sqlite => Path.GetFileName(new SqliteConnectionStringBuilder(written).DataSource),
        DatabaseKind.Postgres => $"{new NpgsqlConnectionStringBuilder(written).Database} at {new NpgsqlConnectionStringBuilder(written).Host}",
        DatabaseKind.SqlServer => $"{new SqlConnectionStringBuilder(written).InitialCatalog} at {new SqlConnectionStringBuilder(written).DataSource}",
        _ => $"{new MySqlConnectionStringBuilder(written).Database} at {new MySqlConnectionStringBuilder(written).Server}",
    };

    private string Build(DatabaseKind kind)
    {
        if (!string.IsNullOrWhiteSpace(connection.Text))
        {
            return kind switch
            {
                DatabaseKind.Sqlite => new SqliteConnectionStringBuilder(connection.Text).ConnectionString,
                DatabaseKind.Postgres => new NpgsqlConnectionStringBuilder(connection.Text).ConnectionString,
                DatabaseKind.SqlServer => new SqlConnectionStringBuilder(connection.Text).ConnectionString,
                _ => new MySqlConnectionStringBuilder(connection.Text).ConnectionString,
            };
        }
        if (kind != DatabaseKind.Sqlite && (string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(database.Text)))
        {
            throw new ArgumentException("Enter the host and database name");
        }
        if (port.Value is not { } value || value != decimal.Truncate(value) || value is < 1 or > 65535)
        {
            throw new ArgumentException("Enter a whole port number between 1 and 65535");
        }
        int number = decimal.ToInt32(value);
        string baseline = initial?.Kind == kind ? initial.ConnectionString : string.Empty;
        return kind switch
        {
            DatabaseKind.Sqlite => new SqliteConnectionStringBuilder(baseline) { DataSource = database.Text ?? string.Empty }.ConnectionString,
            DatabaseKind.Postgres => new NpgsqlConnectionStringBuilder(baseline) { Host = host.Text!.Trim(), Port = number, Database = database.Text!.Trim(), Username = user.Text ?? string.Empty, Password = password.Text ?? string.Empty }.ConnectionString,
            DatabaseKind.SqlServer => new SqlConnectionStringBuilder(baseline)
            {
                DataSource = SqlAddress(host.Text!.Trim(), number), InitialCatalog = database.Text!.Trim(), IntegratedSecurity = integrated.IsChecked == true,
                UserID = integrated.IsChecked == true ? string.Empty : user.Text ?? string.Empty, Password = integrated.IsChecked == true ? string.Empty : password.Text ?? string.Empty,
            }.ConnectionString,
            _ => new MySqlConnectionStringBuilder(baseline) { Server = host.Text!.Trim(), Port = (uint)number, Database = database.Text!.Trim(), UserID = user.Text ?? string.Empty, Password = password.Text ?? string.Empty }.ConnectionString,
        };
    }

    private string SqlAddress(string written, int number)
    {
        if (initial?.Kind == DatabaseKind.SqlServer)
        {
            string original = new SqlConnectionStringBuilder(initial.ConnectionString).DataSource;
            if (!original.Contains(',', StringComparison.Ordinal) && original == written && number == 1433) { return original; }
        }
        return $"{written},{number}";
    }

    private static void MarkConnection(string text)
    {
        ContainerSecrets.Mark(text);
        try
        {
            DbConnectionStringBuilder read = new() { ConnectionString = text };
            foreach (string key in read.Keys)
            {
                if (ContainerSecrets.Secret(key))
                {
                    ContainerSecrets.Mark(Convert.ToString(read[key], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
        }
        catch (ArgumentException)
        {
            // The provider's builder reports incomplete input beside the form.
        }
    }
}
