using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Passenger.App;
using Passenger.App.Database;
using Passenger.App.Tables;

namespace Passenger.Tests;

public sealed class DatabaseTests
{
    [AvaloniaFact]
    public void Tables_are_searchable_expandable_and_open_filtered_editor_tabs()
    {
        MainWindow window = OpenShop();
        try
        {
            ListBox list = View<ListBox>(window, "DatabaseTables");
            ChooseTable(window, "Products");
            Ui.Click(list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext == list.SelectedItem));
            window.KeyTextInput("ord");
            Assert.Equal("ord", View<TextBox>(window, "DatabaseTableSearch").Text);
            Assert.Equal(["OrderLines", "Orders"], list.Items.OfType<DatabaseTreeRow>().Select(r => r.Table.Name));
            Ui.Press(window, "ESCAPE");
            Assert.Equal(3, list.ItemCount);
            ChooseTable(window, "Products");
            Control item = list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext == list.SelectedItem);
            Ui.Click(item);
            Ui.Press(window, "RIGHT");
            Ui.WaitFor(() => !window.Database.Busy && list.ItemCount == 6, () => window.Database.Said);
            Assert.Contains(list.Items.OfType<DatabaseTreeRow>(), r => r.Title == "Columns");
            Ui.Press(window, "DOWN");
            Ui.Press(window, "RIGHT");
            Assert.Contains(list.Items.OfType<DatabaseTreeRow>(), r => r.Depth == 2 && r.Title.StartsWith("Name ·", StringComparison.Ordinal));
            ChooseTable(window, "Products");
            item = list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext == list.SelectedItem);
            Ui.Click(item);
            Ui.Press(window, "CTRL+ENTER");
            Ui.WaitFor(() => window.EditorArea.Active?.DatabaseTable is not null, "a database editor tab");
            T Tab<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
            Ui.WaitFor(() => Tab<CellGrid>("DatabaseTabResults").Table?.RowCount == 3, "table rows in the editor");
            Passenger.App.Workspace.OpenDocument opened = window.EditorArea.Active!;
            Assert.DoesNotContain(opened, window.EditorArea.Documents);
            Tab<ComboBox>("DatabaseFilterColumn").SelectedItem = "Name";
            Tab<TextBox>("DatabaseFilterValue").Text = "tea";
            Ui.Click(Tab<Button>("DatabaseApplyFilter"));
            Ui.WaitFor(() => Tab<CellGrid>("DatabaseTabResults").Table?.RowCount == 1, "filtered rows");
            Assert.Equal("Tea", Tab<CellGrid>("DatabaseTabResults").Table!.Cell(0, 1));
            Tab<TextBox>("DatabaseFilterValue").Text = "%";
            Ui.Click(Tab<Button>("DatabaseApplyFilter"));
            Ui.WaitFor(() => Tab<CellGrid>("DatabaseTabResults").Table?.RowCount == 0, "a literal wildcard filter");
            Ui.Click(Tab<Button>("DatabaseClearFilter"));
            Ui.WaitFor(() => Tab<CellGrid>("DatabaseTabResults").Table?.RowCount == 3, "unfiltered rows");
            Ui.DoubleClick(item, new Avalonia.Point(item.Bounds.Width - 15, item.Bounds.Height / 2));
            Ui.Settle();
            Assert.Same(opened, window.EditorArea.Active);
            Assert.Single(window.EditorArea.Groups.SelectMany(g => g.Tabs), t => t.Document.DatabaseTable is not null);
            Assert.False(File.Exists(opened.Path));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Unbounded_queries_are_limited_and_running_SQL_can_be_cancelled()
    {
        MainWindow window = OpenShop();
        try
        {
            TextBox sql = View<TextBox>(window, "DatabaseSql");
            sql.Text = "SELECT * FROM Products";
            Tool(window, AppActions.DatabaseQuery);
            Ui.ClickButton(Question(window), "Run");
            Ui.WaitFor(() => !window.Database.Busy, () => window.Database.Said);
            Assert.Contains("LIMIT 1000", sql.Text, StringComparison.Ordinal);
            Assert.Equal(3, window.Database.Rows!.RowCount);
            sql.Text = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT sum(x) FROM n";
            Tool(window, AppActions.DatabaseQuery);
            ChoiceDialog question = Question(window);
            Ui.Click(question.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Run"), settle: false);
            Assert.True(window.Database.Busy);
            Assert.True(View<Button>(window, AppActions.DatabaseCancel).IsEnabled);
            Ui.Click(View<Button>(window, AppActions.DatabaseCancel), settle: false);
            Ui.WaitFor(() => !window.Database.Busy, () => window.Database.Said);
            Assert.Contains("cancelled", window.Database.Said, StringComparison.OrdinalIgnoreCase);
            Assert.False(View<Button>(window, AppActions.DatabaseCancel).IsEnabled);
        }
        finally { window.Close(); }
    }

    private static MainWindow OpenShop()
    {
        string folder = TestFolders.Create("database-tree");
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
        TestFolders.Write(folder, "appsettings.json", """{"ConnectionStrings":{"Shop":"Data Source=shop.db"}}""");
        using (SqliteConnection connection = new($"Data Source={Path.Combine(folder, "shop.db")};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Products(Id INTEGER PRIMARY KEY, Name TEXT); CREATE INDEX ProductName ON Products(Name); CREATE TABLE Orders(Id INTEGER); CREATE TABLE OrderLines(Id INTEGER); INSERT INTO Products VALUES(1,'Coffee'),(2,'Tea'),(3,'Milk')";
            command.ExecuteNonQuery();
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.ShowToolWindow(ToolWindowIds.Database);
        Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy, () => window.Database.Said);
        Run(window, AppActions.DatabaseConnect);
        return window;
    }

    [AvaloniaFact]
    public void A_manual_connection_is_entered_validated_connected_and_kept_through_refresh()
    {
        string folder = TestFolders.Create("database-manual");
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
        using (SqliteConnection connection = new($"Data Source={Path.Combine(folder, "shop.db")};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Products(Id INTEGER PRIMARY KEY, Name TEXT); INSERT INTO Products VALUES(1,'Coffee')";
            command.ExecuteNonQuery();
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Database);
            Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy, () => window.Database.Said);
            Tool(window, AppActions.DatabaseAdd);
            Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseConnectionDialog>().Any(d => d.IsVisible), "the manual database connection dialog");
            DatabaseConnectionDialog dialog = window.OwnedWindows.OfType<DatabaseConnectionDialog>().Single(d => d.IsVisible);
            Ui.Settle();
            DialogButtonsTests.Check(dialog);
            T Field<T>(string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
            Assert.NotEqual(default, Field<TextBox>("DatabasePassword").PasswordChar);
            Assert.False(Field<TextBox>("DatabasePassword").RevealPassword);
            Ui.ClickButton(dialog, "Connect");
            Assert.True(dialog.IsVisible);
            Assert.Contains("host and database", Field<TextBlock>("DatabaseConnectionError").Text, StringComparison.Ordinal);
            Field<ComboBox>("DatabaseProvider").SelectedIndex = (int)DatabaseKind.SqlServer;
            Field<CheckBox>("DatabaseIntegrated").IsChecked = true;
            Assert.Equal(1433m, Field<NumericUpDown>("DatabasePort").Value);
            Assert.False(Field<TextBox>("DatabaseUser").IsEnabled);
            Assert.False(Field<TextBox>("DatabasePassword").IsEnabled);
            Field<ComboBox>("DatabaseProvider").SelectedIndex = (int)DatabaseKind.MySql;
            Assert.Equal(3306m, Field<NumericUpDown>("DatabasePort").Value);
            Assert.True(Field<TextBox>("DatabasePassword").IsEnabled);
            Field<ComboBox>("DatabaseProvider").SelectedIndex = (int)DatabaseKind.Sqlite;
            Field<TextBox>("DatabaseConnectionName").Text = "Manual shop";
            Field<TextBox>("DatabaseName").Text = "shop.db";
            Ui.ClickButton(dialog, "Connect");
            Ui.WaitFor(() => window.Database.Connected && !window.Database.Busy, () => window.Database.Said);
            Assert.Equal("Manual shop", window.Database.Source!.Name);
            Assert.Single(window.Database.Tables);
            Run(window, AppActions.DatabaseRefresh);
            Assert.Single(window.Database.Sources);
            Assert.Equal("Manual shop", window.Database.Source!.Name);
            Run(window, AppActions.DatabaseConnect);
            ChooseTable(window, window.Database.Tables.Single().Name);
            Run(window, AppActions.DatabaseBrowse);
            Assert.Equal("Coffee", window.Database.Rows!.Cell(0, 1));
            Tool(window, AppActions.DatabaseConnectionRename);
            Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible), "rename connection dialog");
            ConfirmDialog rename = window.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible);
            rename.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "DatabaseRenameName").Text = "Renamed shop";
            Ui.ClickButton(rename, "Rename");
            Assert.Equal("Renamed shop", window.Database.Source!.Name);
            Assert.True(window.Database.Connected);
            Tool(window, AppActions.DatabaseConnectionEdit);
            Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseConnectionDialog>().Any(d => d.IsVisible), "edit connection dialog");
            dialog = window.OwnedWindows.OfType<DatabaseConnectionDialog>().Single(d => d.IsVisible);
            Assert.Equal((int)DatabaseKind.Sqlite, Field<ComboBox>("DatabaseProvider").SelectedIndex);
            Assert.Equal("Renamed shop", Field<TextBox>("DatabaseConnectionName").Text);
            Field<TextBox>("DatabaseConnectionName").Text = "Edited shop";
            Ui.ClickButton(dialog, "Save and Connect");
            Ui.WaitFor(() => window.Database.Connected && !window.Database.Busy, () => window.Database.Said);
            Assert.Equal("Edited shop", window.Database.Source!.Name);
            Run(window, AppActions.DatabaseRefresh);
            Assert.Single(window.Database.Sources);
            Assert.Equal("Edited shop", window.Database.Source!.Name);
            Run(window, AppActions.DatabaseConnect);
            Tool(window, AppActions.DatabaseAdd);
            Ui.WaitFor(() => window.OwnedWindows.OfType<DatabaseConnectionDialog>().Any(d => d.IsVisible), "another manual connection dialog");
            Ui.ClickButton(window.OwnedWindows.OfType<DatabaseConnectionDialog>().Single(d => d.IsVisible), "Cancel");
            Assert.Single(window.Database.Sources);
            Assert.True(window.Database.Connected);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_folder_without_connections_explains_why_connect_and_browse_are_disabled()
    {
        string folder = TestFolders.Create("database-empty");
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Database);
            Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy, () => window.Database.Said);
            Assert.Empty(window.Database.Sources);
            Assert.False(View<Button>(window, AppActions.DatabaseConnect).IsEnabled);
            Assert.False(View<Button>(window, AppActions.DatabaseBrowse).IsEnabled);
            Assert.False(View<Button>(window, AppActions.DatabaseQuery).IsEnabled);
            Assert.Contains("No supported connection strings", View<TextBlock>(window, "DatabaseStatus").Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SQLite_is_browsed_paged_queried_and_edited_only_after_confirmation()
    {
        string folder = TestFolders.Create("database");
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
        TestFolders.Write(folder, "appsettings.json", """{"ConnectionStrings":{"Shop":"Data Source=shop.db"}}""");
        using (SqliteConnection connection = new($"Data Source={Path.Combine(folder, "shop.db")};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Note TEXT);
                CREATE INDEX ProductNames ON Products(Name);
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<105)
                INSERT INTO Products SELECT x, 'Product '||x, NULL FROM n;
                """;
            command.ExecuteNonQuery();
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Database);
            Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy, () => window.Database.Said);
            Assert.Equal(Passenger.App.Docking.ToolAnchor.RightBottom, window.DockHost[ToolWindowIds.Database].Anchor);
            Assert.NotEqual(window.DockHost[ToolWindowIds.Migrations].Icon, window.DockHost[ToolWindowIds.Database].Icon);
            Assert.Single(window.Database.Sources);
            Assert.False(window.Database.Connected);
            Assert.Empty(window.Database.Tables);
            Assert.False(View<Button>(window, AppActions.DatabaseBrowse).IsEnabled);
            Run(window, AppActions.DatabaseConnect);
            Assert.False(View<Button>(window, AppActions.DatabaseBrowse).IsEnabled);
            ListBox tables = View<ListBox>(window, "DatabaseTables");
            ChooseTable(window, window.Database.Tables.Single().Name);
            Assert.True(View<Button>(window, AppActions.DatabaseBrowse).IsEnabled);
            Run(window, AppActions.DatabaseBrowse);
            Assert.Equal(100, window.Database.Rows!.RowCount);
            Assert.True(window.Database.Rows.More);
            Assert.Contains(window.Database.Schema!.Columns, c => c.Name == "Id" && c.Primary);
            Assert.Contains(window.Database.Schema.Indexes, i => i.Contains("ProductNames", StringComparison.Ordinal));
            Assert.Equal("NULL", window.Database.Rows.Cell(0, 2));
            Run(window, AppActions.DatabaseNext);
            Assert.Equal(5, window.Database.Rows.RowCount);
            Assert.Equal("101", window.Database.Rows.Cell(0, 0));
            Run(window, AppActions.DatabasePrevious);

            CellGrid grid = View<CellGrid>(window, "DatabaseResults");
            grid.Choose((0, 1), (0, 1));
            Assert.True(window.Perform(AppActions.DatabaseEdit));
            Ui.Settle();
            Assert.NotNull(grid.Writing);
            grid.Writing!.Text = "New product";
            Ui.Press(window, "ENTER");
            ChoiceDialog question = Question(window);
            Assert.Equal("Product 1", window.Database.Rows!.Cell(0, 1));
            Ui.ClickButton(question, "Run");
            Ui.WaitFor(() => !window.Database.Busy, () => window.Database.Said);
            Assert.Equal("New product", window.Database.Rows!.Cell(0, 1));

            TextBox sql = View<TextBox>(window, "DatabaseSql");
            sql.Text = "SELECT Name, Note FROM Products WHERE Id=1";
            Tool(window, AppActions.DatabaseQuery);
            Ui.ClickButton(Question(window), "Run");
            Ui.WaitFor(() => !window.Database.Busy, () => window.Database.Said);
            Assert.Equal("New product", window.Database.Rows!.Cell(0, 0));
            Assert.Single(window.Database.History);
            Assert.False(window.Database.Rows.IsEditable(0, 0));

            sql.Text = "DELETE FROM Products";
            Tool(window, AppActions.DatabaseQuery);
            Ui.ClickButton(Question(window), "Cancel");
            Ui.WaitFor(() => !window.Database.Busy, () => window.Database.Said);
            Assert.Single(window.Database.History);
            ChooseTable(window, window.Database.Tables.Single().Name);
            Run(window, AppActions.DatabaseBrowse);
            Assert.Equal(100, window.Database.Rows!.RowCount);

            using (SqliteConnection connection = new($"Data Source={Path.Combine(folder, "shop.db")};Pooling=False"))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE Products SET Name='Changed elsewhere' WHERE Id=1";
                command.ExecuteNonQuery();
            }
            grid.Choose((0, 1), (0, 1));
            Assert.True(window.Perform(AppActions.DatabaseEdit));
            Ui.Settle();
            grid.Writing!.Text = "Must not overwrite";
            Ui.Press(window, "ENTER");
            Ui.ClickButton(Question(window), "Run");
            Ui.WaitFor(() => !window.Database.Busy, () => window.Database.Said);
            Assert.Contains("row changed", window.Database.Said, StringComparison.Ordinal);
            Run(window, AppActions.DatabaseBrowse);
            Assert.Equal("Changed elsewhere", window.Database.Rows!.Cell(0, 1));

            sql.Text = "SELECT 'Password=database-test-secret' AS Secret";
            Tool(window, AppActions.DatabaseQuery);
            Ui.ClickButton(Question(window), "Run");
            Ui.WaitFor(() => !window.Database.Busy, () => window.Database.Said);
            Assert.DoesNotContain("database-test-secret", window.Database.Rows!.Cell(0, 0), StringComparison.Ordinal);
            Assert.DoesNotContain(window.Database.History, query => query.Contains("database-test-secret", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Discovery_never_connects_and_a_missing_SQLite_database_is_never_created()
    {
        string folder = TestFolders.Create("database-missing");
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(1);");
        TestFolders.Write(folder, "appsettings.json", """
            {"ConnectionStrings":{
                "Missing":"Data Source=missing.db",
                "Cache":"localhost:6379",
                "Postgres":"Host=localhost;Database=shop",
                "SqlServer":"Data Source=localhost;Initial Catalog=shop;Integrated Security=True",
                "MySql":"Server=localhost;Port=3306;Database=shop"
            }}
            """);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Database);
            Run(window, AppActions.DatabaseRefresh);
            Assert.Equal([DatabaseKind.Sqlite, DatabaseKind.Postgres, DatabaseKind.SqlServer, DatabaseKind.MySql], window.Database.Sources.Select(s => s.Kind));
            Assert.False(window.Database.Connected);
            Assert.False(File.Exists(Path.Combine(folder, "missing.db")));
            Assert.False(View<Button>(window, AppActions.DatabaseBrowse).IsEnabled);
            Run(window, AppActions.DatabaseConnect);
            Assert.False(window.Database.Connected);
            Assert.Empty(window.Database.Tables);
            Assert.Equal(window.Database.Said, View<TextBlock>(window, "DatabaseStatus").Text);
            Assert.Contains("Database operation failed", LogsTests.WholeIdeLog(), StringComparison.Ordinal);
            Assert.False(View<Button>(window, AppActions.DatabaseBrowse).IsEnabled);
            Assert.False(File.Exists(Path.Combine(folder, "missing.db")));
        }
        finally
        {
            window.Close();
        }
    }

    private static T View<T>(MainWindow window, string name) where T : Control =>
        window.DatabaseView.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Run(MainWindow window, string action)
    {
        Tool(window, action);
        Ui.WaitFor(() => !window.Database.Busy, () => window.Database.Said);
    }

    internal static void Tool(MainWindow window, string action)
    {
        Ui.Settle();
        Ui.Click(window.DatabaseView.GetVisualDescendants().OfType<Button>().Single(b => b.Name == action), settle: false);
    }

    internal static void ChooseTable(MainWindow window, string name)
    {
        ListBox list = View<ListBox>(window, "DatabaseTables");
        list.SelectedItem = list.Items.OfType<DatabaseTreeRow>().Single(r => r.Depth == 0 && r.Table.Name == name);
    }

    private static ChoiceDialog Question(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(d => d.IsVisible), () => window.Database.Said);
        return window.OwnedWindows.OfType<ChoiceDialog>().Single(d => d.IsVisible);
    }
}
