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

/// <summary>A connection made or edited by hand: its database, server and sign-in, and its access, with its SSL
/// (<see cref="DatabaseSsl"/>), an SSH tunnel and a connection string over the fields each folded away until wanted. A
/// connection the folder's files name keeps what they say and takes only its access, SSL and tunnel here. A tunnel is
/// opened and signed in to as Connect is pressed, each host key no one trusted yet asked of the person first
/// (<see cref="SshTunnels"/>).</summary>
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
    private readonly TextBox clientCertificate = new() { Name = "DatabaseSslClientCertificate", PlaceholderText = "Optional: a PEM file the server asks for" };
    private readonly TextBox clientKey = new() { Name = "DatabaseSslClientKey", PlaceholderText = "Its key's PEM file" };
    private readonly TextBox clientKeyPassword = new() { Name = "DatabaseSslKeyPassword", PasswordChar = '•', RevealPassword = false, PlaceholderText = "Where its key is encrypted" };
    private readonly CheckBox tunnel = new() { Name = "DatabaseSshTunnel", Content = "Through an SSH tunnel" };
    private readonly TextBox sshHost = new() { Name = "DatabaseSshHost", PlaceholderText = "bastion.example.com, or a Host ~/.ssh/config names" };
    private readonly NumericUpDown sshPort = new() { Name = "DatabaseSshPort", Minimum = 1, Maximum = 65535, Value = null, FormatString = "0" };
    private readonly TextBox sshUser = new() { Name = "DatabaseSshUser" };
    private readonly TextBox sshKey = new() { Name = "DatabaseSshKey", PlaceholderText = "Optional" };
    private readonly TextBox sshSecret = new() { Name = "DatabaseSshPassword", PasswordChar = '•', RevealPassword = false };
    private readonly FormFold sslFold = new() { Title = "SSL" };
    private readonly FormFold tunnelFold = new() { Title = "SSH tunnel" };
    private readonly FormFold stringFold = new() { Title = "Connection string" };
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
        bool foundOnly = initial?.Found is not null;
        Closed += (_, _) => closing.Cancel();
        Title = initial is null ? "Add Database Connection" : foundOnly ? "Database Connection Settings" : "Edit Database Connection";
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StackPanel fields = new() { Spacing = FormRows.Between };
        FormRows.Scope(fields);
        List<Grid> own = [];
        own.Add(Row(fields, "Name", name));
        own.Add(Row(fields, "Provider", provider));
        Grid databaseRow = Row(fields, "Database", database, out TextBlock databaseLabel);
        own.Add(databaseRow);
        Grid hostRow = Row(fields, "Host", host);
        Grid portRow = Row(fields, "Port", port);
        Grid userRow = Row(fields, "User", user);
        Grid passwordRow = Row(fields, "Password", password);
        Grid integratedRow = Row(fields, null, integrated);
        own.AddRange([hostRow, portRow, userRow, passwordRow, integratedRow]);
        Row(fields, "Access", mark);
        Row(sslFold.Rows, "Mode", ssl);
        Grid certificateRow = Row(sslFold.Rows, "Certificate authority", BrowseRow.Of(certificate, () => BrowseAsync(certificate, "Certificate")), out TextBlock certificateLabel);
        Grid[] client = [Row(sslFold.Rows, "Client certificate", BrowseRow.Of(clientCertificate, () => BrowseAsync(clientCertificate, "Client Certificate"))),
            Row(sslFold.Rows, "Client key", BrowseRow.Of(clientKey, () => BrowseAsync(clientKey, "Client Key"))), Row(sslFold.Rows, "Key password", clientKeyPassword)];
        Row(tunnelFold.Rows, null, tunnel);
        Grid[] sshRows = [Row(tunnelFold.Rows, "SSH host", sshHost), Row(tunnelFold.Rows, "SSH port", sshPort), Row(tunnelFold.Rows, "SSH user", sshUser),
            Row(tunnelFold.Rows, "SSH key file", BrowseRow.Of(sshKey, () => BrowseAsync(sshKey, "SSH Private Key"))), Row(tunnelFold.Rows, "Password or key passphrase", sshSecret)];
        TextBlock sshNote = MutedText.Wrapped("A host ~/.ssh/config names takes its HostName, Port, User, IdentityFile and ProxyJump; an empty port or user is its, "
            + "else 22 and your own name. The agent's keys are tried first, and a host key ssh knows is trusted.");
        tunnelFold.Rows.Children.Add(sshNote);
        Row(stringFold.Rows, "Connection string", connection);
        sslFold.Opener.Name = "DatabaseSslSection";
        tunnelFold.Opener.Name = "DatabaseSshSection";
        stringFold.Opener.Name = "DatabaseConnectionStringSection";
        foreach (FormFold fold in (FormFold[])[sslFold, tunnelFold, stringFold])
        {
            fields.Children.Add(fold.Opener);
            fields.Children.Add(fold.Rows);
        }
        provider.SelectionChanged += (_, _) => ShowProvider();
        integrated.IsCheckedChanged += (_, _) => ShowAuthentication();
        tunnel.IsCheckedChanged += (_, _) => ShowTunnel();
        ssl.SelectionChanged += (_, _) =>
        {
            sslChosen = true;
            ShowSsl();
        };
        foreach (TextBox changed in (TextBox[])[certificate, clientCertificate, clientKey, clientKeyPassword])
        {
            changed.TextChanged += (_, _) => sslChosen = true;
        }
        connect = DialogFooter.Default(initial is null ? "Connect" : "Save and Connect");
        connect.Name = "DatabaseConnectionConnect";
        connect.Click += (_, _) => IdeErrors.Observe(ConnectAsync());
        DatabaseSource? Made()
        {
            try
            {
                ContainerSecrets.Mark(password.Text ?? string.Empty);
                ContainerSecrets.Mark(clientKeyPassword.Text ?? string.Empty);
                MarkConnection(connection.Text ?? string.Empty);
                DatabaseKind kind = (DatabaseKind)provider.SelectedIndex;
                DatabaseMark marked = (DatabaseMark)Math.Max(0, mark.SelectedIndex);
                SslSettings chosen = new((SslChoice)Math.Max(0, ssl.SelectedIndex), Located(certificate), Located(clientCertificate), Located(clientKey));
                string? keyPassword = clientKeyPassword.Text is { Length: > 0 } typed ? typed : null;
                if (initial is { Found: not null })
                {
                    return initial with { Mark = marked, Over = sslChosen ? DatabaseSsl.With(initial.Kind, initial.Over, chosen, keyPassword) : initial.Over };
                }
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
                    written = DatabaseSsl.With(kind, written, chosen, keyPassword);
                }
                string title = name.Text?.Trim() is { Length: > 0 } named ? named
                    : TitleOf(kind, written);
                return initial is null ? new DatabaseSource(title, kind, folder) { ConnectionString = written, Mark = marked }
                    : initial with { Name = title, Kind = kind, ConnectionString = written, Mark = marked };
            }
            catch (Exception failed) when (failed is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
            {
                Say(failed.Message);
                return null;
            }
        }

        // Connect: the connection made, its tunnel opened and signed in to, each host key no one trusted yet asked first.
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
            if (sshHost.Text?.Trim() is not { Length: > 0 } through)
            {
                Say(Refusals.NoSshHost);
                return;
            }
            string typed = sshSecret.Text ?? string.Empty;
            ContainerSecrets.Mark(typed);
            string? secret = typed.Length > 0 ? typed
                : initial?.Tunnel is not null ? SystemCredentials.Read(DatabaseController.CredentialService, initial.TunnelAccount) : null;
            SshTunnel given = new(through, sshPort.Value is { } number ? decimal.ToInt32(decimal.Truncate(number)) : 0, sshUser.Text?.Trim() ?? string.Empty)
            {
                KeyFile = Located(sshKey),
                HostKeys = initial?.Tunnel?.HostKeys ?? [],
            };
            connect.IsEnabled = false;
            try
            {
                while (true)
                {
                    Say($"Opening the SSH tunnel through {through}…");
                    try
                    {
                        await BackgroundWork.Run(() => SshTunnels.CheckAsync(given, secret, closing.Token), closing.Token);
                        break;
                    }
                    catch (SshHostKeyException asked)
                    {
                        error.IsVisible = false;
                        string place = asked.Port == 22 ? asked.Host : $"{asked.Host}:{asked.Port}";
                        string question = asked.Changed
                            ? $"{place} now gives a {asked.Kind} host key whose fingerprint is {asked.Fingerprint}, not the key trusted or known for it. Trust the new "
                                + "key only if the server's administrator says it changed."
                            : $"{place} gives its {asked.Kind} host key, whose fingerprint is {asked.Fingerprint}. Trust it for this connection? Compare it with the "
                                + "fingerprint the server's administrator gives first.";
                        ConfirmDialog trust = new("Trust SSH Host Key", question, "Trust") { EnterAgrees = false, PersonsAlone = Refusals.SshHostKeyByThePerson };
                        if (!await trust.ShowDialog<bool>(this))
                        {
                            return;
                        }
                        given = given with { HostKeys = [.. given.HostKeys.Where(k => k.Host != asked.Host || k.Port != asked.Port), new TrustedHostKey(asked.Host, asked.Port, asked.Fingerprint)] };
                    }
                }
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
            Close(made with { Tunnel = given, TunnelSecret = typed.Length > 0 ? typed : null });
        }

        void Say(string words)
        {
            error.Text = ContainerSecrets.InSaid(words);
            error.IsVisible = true;
        }
        TextBlock about = MutedText.Wrapped(foundOnly
            ? $"{char.ToUpperInvariant(initial!.Found![0])}{initial.Found[1..]}: its server and sign-in come from there. Its access, SSL and tunnel are kept for this folder."
            : "A connection is kept for this folder; its passwords are kept in the system's credential store, never in a file.");
        Content = DialogFooter.Page(new StackPanel
        {
            [DialogFooter.InsetProperty] = true,
            Spacing = FormRows.Between,
            Children = { fields, about, error },
        }, DialogFooter.OkCancel(connect, () => Close(null)));
        error.IsVisible = false;
        ShowProvider();
        if (initial is not null)
        {
            provider.SelectedIndex = (int)initial.Kind;
            SslSettings had = DatabaseSsl.Of(initial.Kind, initial.Found is null ? initial.ConnectionString : DatabaseKinds.LaidOver(initial.Kind, initial.ConnectionString, initial.Over));
            ssl.SelectedIndex = (int)had.Choice;
            certificate.Text = had.Authority;
            clientCertificate.Text = had.Certificate;
            clientKey.Text = had.Key;
            clientKeyPassword.PlaceholderText = had.Certificate is not null ? "Unchanged" : clientKeyPassword.PlaceholderText;
            sslFold.IsOpen = had != new SslSettings(SslChoice.Driver);
            if (initial.Tunnel is { } through)
            {
                tunnel.IsChecked = true;
                sshHost.Text = through.Host;
                sshPort.Value = through.Port > 0 ? through.Port : null;
                sshUser.Text = through.User;
                sshKey.Text = through.KeyFile;
                sshSecret.PlaceholderText = "Unchanged";
                tunnelFold.IsOpen = true;
            }
            sslChosen = false;
            name.Text = initial.Name;
            mark.SelectedIndex = (int)initial.Mark;
            password.PlaceholderText = initial.SecretKept ? "Unchanged" : null;
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
            if (foundOnly)
            {
                own.ForEach(row => row.IsVisible = false);
                stringFold.Opener.IsVisible = false;
            }
        }

        void ShowSsl()
        {
            bool remote = provider.SelectedIndex != (int)DatabaseKind.Sqlite;
            certificateRow.IsVisible = remote && ssl.SelectedIndex >= (int)SslChoice.VerifyAuthority;
            foreach (Grid row in client)
            {
                row.IsVisible = remote && DatabaseSsl.TakesClientCertificate((DatabaseKind)provider.SelectedIndex) && ssl.SelectedIndex != (int)SslChoice.Off;
            }
        }

        void ShowTunnel()
        {
            bool through = tunnel.IsChecked == true;
            foreach (Grid row in sshRows)
            {
                row.IsVisible = through;
            }
            sshNote.IsVisible = through;
        }

        void ShowProvider()
        {
            bool remote = provider.SelectedIndex != (int)DatabaseKind.Sqlite;
            sslFold.Opener.IsVisible = remote;
            tunnelFold.Opener.IsVisible = remote;
            sslFold.IsOpen &= remote;
            tunnelFold.IsOpen &= remote;
            certificateLabel.Text = provider.SelectedIndex == (int)DatabaseKind.SqlServer ? "Server certificate" : "Certificate authority";
            ShowSsl();
            ShowTunnel();
            foreach (Grid row in (Grid[])[hostRow, portRow, userRow, passwordRow])
            {
                row.IsVisible = remote && !foundOnly;
            }
            databaseLabel.Text = remote ? "Database" : "Database file";
            database.PlaceholderText = remote ? "shop" : "Full path, or a path relative to the open folder";
            port.Value = provider.SelectedIndex switch { (int)DatabaseKind.Postgres => 5432, (int)DatabaseKind.SqlServer => 1433, _ => 3306 };
            integratedRow.IsVisible = provider.SelectedIndex == (int)DatabaseKind.SqlServer && !foundOnly;
            ShowAuthentication();
        }

        void ShowAuthentication()
        {
            bool credentials = provider.SelectedIndex != (int)DatabaseKind.SqlServer || integrated.IsChecked != true;
            user.IsEnabled = credentials;
            password.IsEnabled = credentials;
        }
    }

    /// <summary>Adds a labelled row to a form's panel.</summary>
    private static Grid Row(Panel panel, string? label, Control field) => Row(panel, label, field, out _);

    private static Grid Row(Panel panel, string? label, Control field, out TextBlock text)
    {
        Grid row = FormRows.Of(label, field, out text);
        panel.Children.Add(row);
        return row;
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
