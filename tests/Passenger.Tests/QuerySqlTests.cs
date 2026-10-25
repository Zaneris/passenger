using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Mcp;
using Passenger.Dap;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>An EF Core query's SQL at a stop: a program on EF Core and SQLite stopped holding a query its provider
/// translates and one it cannot, each listing first among its members a row of the SQL it would run, read only as the row
/// is opened (ToQueryString() run in the program then), in Variables, the hover card and an agent's debug_state
/// alike; the SQL opens whole in a read-only tab, and what keeps a query from SQL is said as its row's value. The tests
/// share the program as written (<see cref="Shop"/>). Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class QuerySqlTests : McpRig, IClassFixture<QuerySqlTests.Shop>
{
    private readonly Shop shop;

    public QuerySqlTests(Shop shop)
    {
        this.shop = shop;
        shop.Reset();
    }

    [AvaloniaFact]
    public void A_querys_SQL_row_reads_as_it_is_opened_in_Variables_and_the_hover_card_and_opens_whole_in_a_tab()
    {
        MainWindow window = shop.Open(shop.PathOf("Program.cs"), languageServer: false);
        try
        {
            window.EditorArea.CodeEditor.GoTo(new TextPosition(Shop.Stop, 0));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the shop to start debugged", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.BottomPanel.Debug.Tree.Rows.Any(r => r.Name == "stocked"),
                $"the stop at the breakpoint and its locals; the status bar said \"{window.Status}\"", TimeSpan.FromMinutes(1));
            VariablesList variables = window.DebugWindow.VariablesList;

            VariableNode stocked = variables.Tree.Rows.Single(r => r.Name == "stocked" && r.Depth == 0);
            VariableNode unread = Unread(window, variables, stocked);
            Assert.Equal(DapRows.QuerySql, variables.Tree.Rows[variables.Tree.Rows.IndexOf(stocked) + 1].Name);

            DebuggerHoverTests.Toggle(window, variables, unread);
            VariableNode sql = Read(variables, stocked);
            Assert.True(sql.Value.Contains("FROM \"Products\" AS \"p\"", StringComparison.Ordinal) && sql.Value.Contains("ORDER BY \"p\".\"Name\"", StringComparison.Ordinal),
                $"The query's row read as: {sql.Value}");
            Assert.True(sql.Value.Contains('\n', StringComparison.Ordinal) && !sql.Shown.Contains('\n', StringComparison.Ordinal),
                $"The SQL's row shows it otherwise than on one line: {sql.Shown}");
            Assert.Equal(VariableRole.Value, sql.Role);
            Ui.DoubleClick(variables.ContainerFromItem(sql)!);
            string opened = Opened(window);
            Assert.Equal(sql.Value, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
            Assert.True(window.EditorArea.CodeEditor.IsReadOnly, "The query's SQL opened as something to edit");
            Assert.StartsWith("SQL ", window.EditorArea.Active!.Origin, StringComparison.Ordinal);

            VariableNode odd = variables.Tree.Rows.Single(r => r.Name == "odd" && r.Depth == 0);
            DebuggerHoverTests.Toggle(window, variables, Unread(window, variables, odd));
            VariableNode refused = Read(variables, odd);
            Assert.True(refused.Role == VariableRole.Error && refused.Value.Contains("could not be translated", StringComparison.Ordinal),
                $"The query EF Core cannot translate read as: {refused.Value}");

            // Laid out on the file again before the pointer is aimed at its words.
            window.OpenFile(shop.PathOf("Program.cs"));
            Ui.Settle();
            VariablesList card = DebuggerHoverTests.Hover(window, "stocked.Count");
            VariableNode hovered = Assert.Single(card.Tree.Rows);
            DebuggerHoverTests.Toggle(window, card, Unread(window, card, hovered));
            VariableNode inCard = Read(card, hovered);
            Assert.Equal(sql.Value, inCard.Value);
            Assert.Contains($"\n  {DapRows.QuerySql} = {inCard.Shown}", window.Assist.HoverText, StringComparison.Ordinal);
            DebuggerHoverTests.Click(window, card.ContainerFromItem(inCard)!);
            Ui.ChooseFromMenu(card, VariablesList.OpenInEditor);
            Assert.Equal(opened, Opened(window));

            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the program to end");
            Assert.Contains("1 stocked", window.BottomPanel.Debug.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public async Task An_agents_debug_state_reads_a_querys_SQL_as_it_opens_its_row()
    {
        await using McpClient client = await Start(["--mcp", shop.Folder], languageServer: false);
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = Shop.Stop + 1 });
        JsonElement stop = await Call(client, McpTools.DebugStart);
        Assert.Equal("paused", stop.GetProperty("state").GetString());
        int query = stop.GetProperty("locals").EnumerateArray().Single(l => l.GetProperty("name").GetString() == "stocked").GetProperty("reference").GetInt32();

        JsonElement row = (await Call(client, McpTools.DebugState, new() { ["reference"] = query })).EnumerateArray().First();
        Assert.Equal((DapRows.QuerySql, DapRows.QuerySqlUnread), (row.GetProperty("name").GetString(), row.GetProperty("value").GetString()));
        JsonElement read = Assert.Single((await Call(client, McpTools.DebugState, new() { ["reference"] = row.GetProperty("reference").GetInt32() })).EnumerateArray());
        string sql = read.GetProperty("value").GetString()!;
        Assert.True(read.GetProperty("name").GetString() == DapRows.QuerySql && sql.Contains("FROM \"Products\" AS \"p\"", StringComparison.Ordinal),
            $"The agent read the query's SQL row as: {read}");
        Assert.Equal(0, read.GetProperty("reference").GetInt32());
        await Call(client, McpTools.Stop);
    }

    /// <summary>A query's row opened, its SQL row first among its members, unread.</summary>
    private static VariableNode Unread(MainWindow window, VariablesList list, VariableNode query)
    {
        DebuggerHoverTests.Toggle(window, list, query);
        Ui.WaitFor(() => DebuggerHoverTests.Member(list, query, DapRows.QuerySql) is not null,
            $"the SQL row of {query.Name}, where the list showed {DebuggerHoverTests.Shown(list)}");
        VariableNode row = DebuggerHoverTests.Member(list, query, DapRows.QuerySql)!;
        Assert.True(row is { IsLazy: true, Value: DapRows.QuerySqlUnread }, $"The SQL row of {query.Name} was read before it was opened: {row.Value}");
        return row;
    }

    /// <summary>The SQL row of a query, once it has read in its place.</summary>
    private static VariableNode Read(VariablesList list, VariableNode query)
    {
        Ui.WaitFor(() => DebuggerHoverTests.Member(list, query, DapRows.QuerySql) is { WasRead: true },
            $"the SQL row of {query.Name} to read, where the list showed {DebuggerHoverTests.Shown(list)}");
        return DebuggerHoverTests.Member(list, query, DapRows.QuerySql)!;
    }

    /// <summary>The tab of SQL the window opened, by its file.</summary>
    private static string Opened(MainWindow window)
    {
        Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith(".sql", StringComparison.Ordinal) == true, "the query's SQL, opened in a tab");
        return window.EditorArea.Active!.Path;
    }

    /// <summary>A console app on EF Core and SQLite (its packages from the package cache through the run's local feed), which
    /// makes a product, then a query its provider translates and one it cannot, and stops on the line that counts the first.</summary>
    public sealed class Shop : SharedProject
    {
        private const string Program = """
            using Microsoft.EntityFrameworkCore;

            using ShopContext shop = new();
            shop.Database.EnsureDeleted();
            shop.Database.EnsureCreated();
            shop.Products.Add(new Product { Name = "Tea", Stock = 3 });
            shop.SaveChanges();
            Console.WriteLine($"{shop.Products.Count()} product");
            int least = 2;
            IQueryable<Product> stocked = shop.Products.Where(p => p.Stock >= least).OrderBy(p => p.Name);
            IQueryable<Product> odd = shop.Products.Where(p => Names.Odd(p.Name));
            Console.WriteLine($"{stocked.Count()} stocked");

            public static class Names
            {
                public static bool Odd(string name) => name.Length % 2 == 1;
            }

            public sealed class Product
            {
                public int Id { get; set; }

                public string Name { get; set; } = "";

                public int Stock { get; set; }
            }

            public sealed class ShopContext : DbContext
            {
                public DbSet<Product> Products => Set<Product>();

                protected override void OnConfiguring(DbContextOptionsBuilder options) =>
                    options.UseSqlite("Data Source=" + Path.Combine(AppContext.BaseDirectory, "shop.db"));
            }

            """;

        /// <summary>The line (from zero) that counts the query translated, where the breakpoint stops: both queries made.</summary>
        public static readonly int Stop = Array.FindIndex(Program.Split('\n'), l => l.Contains("stocked.Count()", StringComparison.Ordinal));

        public Shop()
            : base("querysql-")
        {
            AvaloniaApp.UseFeed(Folder);
            File.WriteAllText(PathOf("Shop.csproj"), TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>")
                .With($"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"{ShopApp.Version}\" />\n  </ItemGroup>\n"));
            File.WriteAllText(PathOf("Program.cs"), Program);
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
            Prepare();
        }
    }
}
