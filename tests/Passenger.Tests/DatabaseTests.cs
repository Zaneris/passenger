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
            tables.SelectedItem = window.Database.Tables.Single();
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
            tables.SelectedItem = window.Database.Tables.Single();
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

    private static ChoiceDialog Question(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(d => d.IsVisible), () => window.Database.Said);
        return window.OwnedWindows.OfType<ChoiceDialog>().Single(d => d.IsVisible);
    }
}
