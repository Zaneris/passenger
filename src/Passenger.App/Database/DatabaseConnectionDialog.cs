using System.Data.Common;
using Avalonia;
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

public sealed class DatabaseConnectionDialog : IdeWindow
{
    private readonly ComboBox provider = new() { Name = "DatabaseProvider", ItemsSource = new[] { "SQLite", "PostgreSQL", "SQL Server", "MySQL / MariaDB" }, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox name = new() { Name = "DatabaseConnectionName", PlaceholderText = "Local shop" };
    private readonly TextBox host = new() { Name = "DatabaseHost", Text = "localhost" };
    private readonly NumericUpDown port = new() { Name = "DatabasePort", Minimum = 1, Maximum = 65535, Value = 5432, FormatString = "0" };
    private readonly TextBox database = new() { Name = "DatabaseName" };
    private readonly TextBox user = new() { Name = "DatabaseUser" };
    private readonly TextBox password = new() { Name = "DatabasePassword", PasswordChar = '•', RevealPassword = false };
    private readonly CheckBox integrated = new() { Name = "DatabaseIntegrated", Content = "Windows authentication" };
    private readonly TextBox connection = new() { Name = "DatabaseConnectionString", PasswordChar = '•', RevealPassword = false, PlaceholderText = "Optional: overrides the fields above" };
    private readonly TextBlock error = new() { Name = "DatabaseConnectionError", TextWrapping = TextWrapping.Wrap };
    private readonly DatabaseSource? initial;

    public DatabaseConnectionDialog(string folder, DatabaseSource? initial = null)
    {
        this.initial = initial;
        Title = initial is null ? "Add Database Connection" : "Edit Database Connection";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Grid fields = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        Row("Name", name);
        Row("Provider", provider);
        TextBlock databaseLabel = Row("Database", database);
        TextBlock hostLabel = Row("Host", host);
        TextBlock portLabel = Row("Port", port);
        TextBlock userLabel = Row("User", user);
        TextBlock passwordLabel = Row("Password", password);
        TextBlock integratedLabel = Row(string.Empty, integrated);
        provider.SelectionChanged += (_, _) => ShowProvider();
        integrated.IsCheckedChanged += (_, _) => ShowAuthentication();
        Button connect = DialogFooter.Default(initial is null ? "Connect" : "Save and Connect");
        connect.Name = "DatabaseConnectionConnect";
        connect.Click += (_, _) =>
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
                string title = name.Text?.Trim() is { Length: > 0 } named ? named
                    : TitleOf(kind, written);
                Close(initial is null ? new DatabaseSource(title, kind, folder) { ConnectionString = written }
                    : initial with { Name = title, Kind = kind, ConnectionString = written });
            }
            catch (Exception failed) when (failed is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
            {
                error.Text = ContainerSecrets.InSaid(failed.Message);
                error.IsVisible = true;
            }
        };
        Content = DialogFooter.Page(new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                fields,
                new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "Connection string (advanced)" }, connection } },
                MutedText.Wrapped("Connections are kept for this open folder. Credentials are not saved to disk."),
                error,
            },
        }, DialogFooter.OkCancel(connect, () => Close(null)));
        error.IsVisible = false;
        ShowProvider();
        if (initial is not null)
        {
            name.Text = initial.Name;
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

        TextBlock Row(string label, Control control)
        {
            int row = fields.RowDefinitions.Count;
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBlock text = new() { Text = label, Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center, [Grid.RowProperty] = row };
            control.Margin = new Thickness(0, 0, 0, 8);
            control[Grid.RowProperty] = row;
            control[Grid.ColumnProperty] = 1;
            fields.Children.Add(text);
            fields.Children.Add(control);
            return text;
        }

        void ShowProvider()
        {
            bool remote = provider.SelectedIndex != (int)DatabaseKind.Sqlite;
            foreach (Control control in new Control[] { hostLabel, host, portLabel, port, userLabel, user, passwordLabel, password })
            {
                control.IsVisible = remote;
            }
            databaseLabel.Text = remote ? "Database" : "Database file";
            database.PlaceholderText = remote ? "shop" : "Full path, or a path relative to the open folder";
            port.Value = provider.SelectedIndex switch { (int)DatabaseKind.Postgres => 5432, (int)DatabaseKind.SqlServer => 1433, _ => 3306 };
            integrated.IsVisible = provider.SelectedIndex == (int)DatabaseKind.SqlServer;
            integratedLabel.IsVisible = integrated.IsVisible;
            ShowAuthentication();
        }

        void ShowAuthentication()
        {
            bool credentials = provider.SelectedIndex != (int)DatabaseKind.SqlServer || integrated.IsChecked != true;
            user.IsEnabled = credentials;
            password.IsEnabled = credentials;
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
