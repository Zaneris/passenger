using System.Net.Http;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Endpoints;
using Passenger.App.Run;
using Passenger.Profiler;

namespace Passenger.Tests;

/// <summary>The Requests window: a web app on EF Core and SQLite that Run runs, followed while the window shows with no
/// change to the app, lists each request it serves with its status, the route of the folder's code it reached and the SQL
/// commands it issued, a command run again and again for one request marked (an N+1), a value its Password column takes
/// as dots; a request opens at its handler, one that failed where its exception was thrown.</summary>
public sealed class LiveRequestsTests
{
    private const string Program = """
        using Microsoft.EntityFrameworkCore;

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<Shop>(options => options.UseSqlite("Data Source=" + Path.Combine(AppContext.BaseDirectory, "shop.db")).EnableSensitiveDataLogging());
        WebApplication app = builder.Build();
        using (IServiceScope scope = app.Services.CreateScope())
        {
            Shop made = scope.ServiceProvider.GetRequiredService<Shop>();
            made.Database.EnsureDeleted();
            made.Database.EnsureCreated();
            made.Orders.AddRange(new Order { Customer = "Ada" }, new Order { Customer = "Grace" }, new Order { Customer = "Linus" });
            made.SaveChanges();
        }

        app.MapGet("/orders/{id:int}", (int id, Shop db) => db.Orders.Single(o => o.Id == id).Customer);
        app.MapGet("/orders", (Shop db) => string.Join(", ", db.Orders.ToList().Select(o => $"{o.Customer}: {db.Lines.Count(l => l.OrderId == o.Id)}")));
        app.MapGet("/sign-in/{name}", (string name, Shop db) =>
        {
            string password = name + "-secret";
            return db.Orders.Any(o => o.Customer == password) ? "in" : "out";
        });
        app.MapGet("/sign-up/{name}", (string name, Shop db) =>
        {
            Customer customer = new() { Name = name, Password = name + "-first" };
            db.Customers.Add(customer);
            db.SaveChanges();
            customer.Password = name + "-second";
            db.SaveChanges();
            string kept = name + "-second";
            return db.Customers.Count(c => c.Password == kept) == 1 ? "signed up" : "lost";
        });
        app.MapGet("/boom", Fail);
        app.Run();

        static string Fail() => throw new InvalidOperationException("No orders today");

        public class Shop(DbContextOptions<Shop> options) : DbContext(options)
        {
            public DbSet<Order> Orders => Set<Order>();

            public DbSet<Line> Lines => Set<Line>();

            public DbSet<Customer> Customers => Set<Customer>();
        }

        public class Customer
        {
            public int Id { get; set; }

            public string Name { get; set; } = "";

            public string Password { get; set; } = "";
        }

        public class Order
        {
            public int Id { get; set; }

            public string Customer { get; set; } = "";
        }

        public class Line
        {
            public int Id { get; set; }

            public int OrderId { get; set; }
        }
        """;

    [AvaloniaFact]
    public void The_requests_window_lists_the_running_apps_requests_with_their_routes_and_commands_and_opens_their_handlers()
    {
        using HeldPort port = new();
        string folder = TestFolders.Create("requests-");
        WriteShop(folder, port.Port);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Requests);
            Ui.WaitFor(() => window.Session.Routes.All.Count == 5, "the app's five routes, read from its code", TimeSpan.FromMinutes(3));
            port.LetGo(Path.Combine(folder, "gate"));
            Assert.True(window.SelectConfiguration("Shop: http"), $"No configuration Shop: http among {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Ui.Launched(window, window.RunAsync(), "the shop to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("Now listening on:", StringComparison.Ordinal), "the shop to listen",
                TimeSpan.FromMinutes(2));
            try
            {
                Ui.WaitFor(() => window.RequestLog.Following.StartsWith("Following", StringComparison.Ordinal) && window.RequestLog.Following.Contains("process", StringComparison.Ordinal),
                    "the Requests window to follow the shop", TimeSpan.FromMinutes(1));
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException($"The Requests window did not follow the running shop; it says: {window.RequestLog.Following}");
            }

            using (HttpClient client = new())
            {
                Assert.Equal("Grace", Get(client, $"http://127.0.0.1:{port.Port}/orders/2"));
                Assert.Equal("Ada: 0, Grace: 0, Linus: 0", Get(client, $"http://127.0.0.1:{port.Port}/orders"));
                Assert.Equal("out", Get(client, $"http://127.0.0.1:{port.Port}/sign-in/ada"));
                Assert.Equal("signed up", Get(client, $"http://127.0.0.1:{port.Port}/sign-up/ada"));
                HttpResponseMessage failed = Asked(() => client.GetAsync($"http://127.0.0.1:{port.Port}/boom"), "the shop's answer at /boom");
                Assert.Equal(500, (int)failed.StatusCode);
            }
            try
            {
                Ui.WaitFor(() => window.RequestLog.Requests.Count(r => r.Status is not null) == 5 && window.RequestLog.Requests.Sum(r => r.Commands.Count) == 9,
                    "the five requests, listed as they ended, with their nine commands", TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException($"The requests were not all listed: [{string.Join("; ", window.RequestLog.Requests.Select(r => $"{r.Method} {r.Path} status {r.Status}, {r.Commands.Count} commands"))}], "
                    + $"the window says: {window.RequestLog.Following}");
            }

            LiveRequest one = window.RequestLog.Requests.Single(r => r.Path == "/orders/2");
            Assert.Equal(("GET", 200, "GET /orders/{id:int}"), (one.Method, one.Status, one.Route is { } route ? RouteRequests.Title(route) : null));
            LiveCommand read = Assert.Single(one.Commands);
            Assert.Contains("FROM \"Orders\"", read.Text, StringComparison.Ordinal);
            Assert.Equal(2, read.Reads);
            // The app logs its parameters' values (EF's sensitive data logging): shown, but a secret's never in the clear.
            Ui.WaitFor(() => read.Parameters is not null, "the command's parameters, from EF Core's log of it");
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(read.Parameters!, @"^@\w*id\w*='2'"), $"{read.Parameters}; the console: {string.Join(" | ", window.BottomPanel.Run.Document.Snapshot.ToString().Split('\n').Where(l => l.Contains("Parameters", StringComparison.Ordinal)))}");
            LiveCommand signIn = Assert.Single(window.RequestLog.Requests.Single(r => r.Path == "/sign-in/ada").Commands);
            Ui.WaitFor(() => signIn.Parameters is not null, "the sign-in's parameters");
            Assert.Matches(@"^@\w*password\w*='" + Passenger.App.Containers.ContainerSecrets.Masked + "'", signIn.Parameters);
            Assert.DoesNotContain("ada-secret", signIn.Parameters, StringComparison.Ordinal);
            Assert.DoesNotContain("ada-secret", window.Requests.Rows.OfType<RequestsView.CommandRow>().Select(r => r.Command.Text + r.Command.Parameters).Aggregate(string.Empty, string.Concat), StringComparison.Ordinal);
            Assert.False(one.HasRepeats, "A request that ran one command was marked as an N+1");

            // EF's @p0, @p1… name no column: a value is masked by the column its command's text has it fill or meet.
            LiveRequest signUp = window.RequestLog.Requests.Single(r => r.Path == "/sign-up/ada");
            Assert.Equal(["INSERT", "UPDATE", "SELECT"], signUp.Commands.Select(c => c.Text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0]));
            Ui.WaitFor(() => signUp.Commands.All(c => c.Parameters is not null), () => $"the sign-up's parameters, from EF Core's logs of its commands: "
                + $"[{string.Join(" | ", signUp.Commands.Select(c => $"{c.Text} => {c.Parameters ?? "none"}"))}]; the console: "
                + string.Join(" | ", window.BottomPanel.Run.Document.Snapshot.ToString().Split('\n').Where(l => l.Contains("Parameters", StringComparison.Ordinal) || l.Contains("Customers", StringComparison.Ordinal))));
            Assert.Contains("='ada'", signUp.Commands[0].Parameters, StringComparison.Ordinal);
            string[] tips = CommandTips(window, signUp);
            Assert.Equal(3, tips.Length);
            foreach (string said in (string[])[.. signUp.Commands.Select(c => c.Parameters!), .. tips])
            {
                Assert.True(said.Contains($"='{Passenger.App.Containers.ContainerSecrets.Masked}'", StringComparison.Ordinal)
                    && !said.Contains("ada-first", StringComparison.Ordinal) && !said.Contains("ada-second", StringComparison.Ordinal),
                    $"A value of the Password column shows in the clear, or not as dots: {said}");
            }

            // The orders' list counts each order's lines with a command of its own: the same text three times, an N+1.
            LiveRequest all = window.RequestLog.Requests.Single(r => r.Path == "/orders");
            Assert.Equal(4, all.Commands.Count);
            Assert.True(all.HasRepeats, "The request that ran one command for each order was not marked as an N+1");
            Assert.Equal([false, true, true, true], all.Commands.Select(c => c.Repeated));
            Assert.Equal(3, all.Commands[1].Runs);
            Assert.Equal(["/boom", "/sign-up/ada", "/sign-in/ada", "/orders", "/orders/2"], window.Requests.Rows.OfType<LiveRequest>().Select(r => r.Path));

            LiveRequest boom = window.RequestLog.Requests.Single(r => r.Path == "/boom");
            Assert.Equal(500, boom.Status);
            Assert.Contains("No orders today", boom.Error, StringComparison.Ordinal);

            window.Requests.Open(one);
            Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith("Program.cs", StringComparison.Ordinal) == true
                && window.EditorArea.CodeEditor.CaretPosition.Line == LineOf("app.MapGet(\"/orders/{id:int}\""), "the order's handler, opened");
            window.Requests.Open(boom);
            Ui.WaitFor(() => window.EditorArea.CodeEditor.CaretPosition.Line == LineOf("static string Fail()"), "the line that threw, opened");
            window.Requests.Open(window.Requests.Rows.OfType<RequestsView.CommandRow>().First(r => r.Request == one));
            Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith(".sql", StringComparison.Ordinal) == true, "the command's text, opened in a tab");
            Assert.Equal($"-- {read.Parameters}\n{read.Text}", window.EditorArea.CodeEditor.Document.Snapshot.ToString());
            Assert.True(window.EditorArea.CodeEditor.IsReadOnly, "A command's text opened as something to edit");
            LiveCommand insert = signUp.Commands[0];
            window.Requests.Open(window.Requests.Rows.OfType<RequestsView.CommandRow>().Single(r => r.Command == insert));
            Ui.WaitFor(() => window.EditorArea.CodeEditor.Document.Snapshot.ToString().StartsWith("-- ", StringComparison.Ordinal)
                && window.EditorArea.CodeEditor.Document.Snapshot.ToString().Contains("INSERT", StringComparison.Ordinal), "the sign-up's INSERT, opened in a tab");
            string opened = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
            Assert.Equal($"-- {insert.Parameters}\n{insert.Text}", opened);
            Assert.DoesNotContain("ada-first", opened, StringComparison.Ordinal);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning, "the shop to stop");
            window.Close();
        }
    }

    /// <summary>A web app whose configuration runs it in the terminal has its requests followed as one in the Run console
    /// does: Run's program is the same wherever it runs.</summary>
    [AvaloniaFact]
    public void The_requests_window_follows_a_web_app_Run_runs_in_the_terminal()
    {
        using HeldPort port = new();
        string folder = TestFolders.Create("requests-terminal-");
        WriteShop(folder, port.Port);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Requests);
            port.LetGo(Path.Combine(folder, "gate"));
            RunConfiguration shop = window.Configurations.FirstOrDefault(c => c.Name == "Shop: http")
                ?? throw new Xunit.Sdk.XunitException($"No configuration Shop: http among {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            RunConfiguration inTerminal = shop with { Name = "Shop in the terminal", Generated = false, Console = ConsoleKind.Terminal };
            window.SetConfigurations([inTerminal], inTerminal.Name);
            Ui.Launched(window, window.RunAsync(), "the shop to start in the terminal", TimeSpan.FromMinutes(2));
            Assert.True(window.RunInTerminal is { IsRunning: true }, "The shop does not run in the terminal");
            using HttpClient client = new();
            Ui.WaitFor(() => Answers(client, $"http://127.0.0.1:{port.Port}/orders/2"), "the shop to listen", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.RequestLog.Following.StartsWith("Following", StringComparison.Ordinal) && window.RequestLog.Following.Contains("process", StringComparison.Ordinal),
                () => $"the Requests window to follow the shop in the terminal; it says: {window.RequestLog.Following}", TimeSpan.FromMinutes(1));
            Assert.Equal("Grace", Get(client, $"http://127.0.0.1:{port.Port}/orders/2"));
            Ui.WaitFor(() => window.RequestLog.Requests.Any(r => r.Path == "/orders/2" && r.Status == 200),
                () => $"the request, listed as it ended: [{string.Join("; ", window.RequestLog.Requests.Select(r => $"{r.Method} {r.Path} {r.Status}"))}]", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => window.RunInTerminal is not { IsRunning: true }, "the shop to stop");
            window.Close();
        }

        static bool Answers(HttpClient client, string url)
        {
            try
            {
                return Asked(() => client.GetAsync(url), $"the shop's answer at {url}").IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }
    }

    /// <summary>A web app Run runs answers a thousand requests, the window listing as many as it keeps, and the list
    /// scrolled with the wheel as <see cref="LongListsTests"/> scrolls the IDE's own: a step down it allocates what one
    /// down fifty requests does, and its rows are handed other requests, none made again.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_a_thousand_requests_a_running_app_answered_hands_the_rows_other_requests_never_making_one_again()
    {
        using HeldPort port = new();
        string folder = TestFolders.Create("requests-many-");
        TestFolders.Write(folder, "Counter.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(folder, "Properties/launchSettings.json",
            $"{{\n  \"profiles\": {{\n    \"http\": {{ \"commandName\": \"Project\", \"applicationUrl\": \"http://127.0.0.1:{port.Port}\" }}\n  }}\n}}\n");
        TestFolders.Write(folder, "Program.cs", "WebApplication app = WebApplication.Create(args);\napp.MapGet(\"/items/{id:int}\", (int id) => id);\napp.Run();\n");
        Restore.Run(folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Requests);
            port.LetGo(Path.Combine(folder, "gate"));
            Assert.True(window.SelectConfiguration("Counter: http"), $"No configuration Counter: http among {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Ui.Launched(window, window.RunAsync(), "the app to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("Now listening on:", StringComparison.Ordinal), "the app to listen", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.RequestLog.Following.StartsWith("Following", StringComparison.Ordinal) && window.RequestLog.Following.Contains("process", StringComparison.Ordinal),
                () => $"the Requests window to follow the app; it says: {window.RequestLog.Following}", TimeSpan.FromMinutes(1));
            Avalonia.Controls.ListBox list = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.Requests).OfType<Avalonia.Controls.ListBox>().Single();
            using HttpClient client = new();
            int sent = 0;
            LongListsTests.Holds("The Requests window", "requests", 50, 1000, count =>
            {
                int from = sent;
                Ui.AwaitPumped(Waiting.Apart(() => Send(client, port.Port, from, count)), $"the app's answers to requests {from} to {count - 1}", TimeSpan.FromMinutes(2));
                sent = count;
                int listed = Math.Min(count, RequestLog.Most);
                Ui.WaitFor(() => window.RequestLog.Requests.Count(r => r.Status is not null) == listed && window.RequestLog.Requests.Any(r => r.Path == $"/items/{count - 1}"),
                    () => $"the last {listed} of {count} requests listed, where the window lists {window.RequestLog.Requests.Count}: {window.RequestLog.Following}", TimeSpan.FromSeconds(30));
                Ui.Settle();
                return (list, () => { });
            });
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning, "the app to stop");
            window.Close();
        }

        // Requests to the app's one route, twenty at a time.
        static async Task Send(HttpClient client, int port, int from, int to)
        {
            for (int at = from; at < to; at += 20)
            {
                await Task.WhenAll(Enumerable.Range(at, Math.Min(20, to - at)).Select(n => client.GetStringAsync(new Uri($"http://127.0.0.1:{port}/items/{n}"))));
            }
        }
    }

    /// <summary>A request heard with the list full costs the window what one costs with a few listed: its rows go in at
    /// the top, the oldest's come out, and every other row keeps its control. The app's diagnostic events are stood in
    /// for by the events the profiler's reader gives the window; an N+1 of many commands is counted by its
    /// text.</summary>
    [AvaloniaFact]
    public void A_request_heard_costs_the_window_what_it_changed_however_many_are_listed()
    {
        string folder = TestFolders.Create("requests-listed-");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        int heard = 0;
        try
        {
            window.ShowToolWindow(ToolWindowIds.Requests);
            Ui.Settle();
            long few = Costs(5);
            long many = Costs(RequestLog.Most);
            Assert.True(many < few + 64 * 1024,
                $"A request heard with {RequestLog.Most} listed allocated {many / 1024} KB on the UI thread, with 5 listed {few / 1024} KB: the list is made again at each batch");

            IReadOnlyList<object> rows = window.Requests.Rows;
            Assert.Equal(RequestLog.Most * 4, rows.Count);
            Assert.Equal($"/orders/{heard - 1}", Assert.IsType<LiveRequest>(rows[0]).Path);
            Assert.All(rows.Skip(1).Take(3), row => Assert.Same(rows[0], Assert.IsType<RequestsView.CommandRow>(row).Request));
            Assert.Equal($"/orders/{heard - RequestLog.Most}", Assert.IsType<LiveRequest>(rows[^4]).Path);
            Assert.Equal(RequestLog.Most, window.RequestLog.Requests.Count);

            window.RequestLog.Clear();
            Hear(commands: 1000, ended: true);
            Ui.Settle();
            LiveRequest repeated = Assert.Single(window.RequestLog.Requests);
            Assert.True(repeated.HasRepeats && repeated.Commands.All(c => c.Runs == 1000), "The request's thousand like commands were not counted as run a thousand times");
            Assert.Equal(1001, window.Requests.Rows.Count);

            window.RequestLog.Clear();
            string trace = Hear(commands: 2, ended: false);
            Ui.Settle();
            window.RequestLog.Hear(new RequestEnded(trace, DateTime.UtcNow, trace, 404), "Shop", null);
            Ui.Settle();
            Assert.Equal(404, Assert.IsType<LiveRequest>(Assert.Single(window.Requests.Rows, r => r is LiveRequest)).Status);
            Assert.Equal(3, window.Requests.Rows.Count);
        }
        finally
        {
            window.Close();
        }

        // The least a request heard allocated on the UI thread, with as many listed as given, each with three commands.
        long Costs(int listed)
        {
            window.RequestLog.Clear();
            for (int i = 0; i < listed; i++)
            {
                Hear(commands: 3, ended: true);
            }
            Ui.Settle();
            long least = long.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Hear(commands: 3, ended: true);
                Ui.Settle();
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return least;
        }

        // A request as the app tells of it: its start, its commands (all of one text) and, when given, its end.
        string Hear(int commands, bool ended)
        {
            string id = $"request-{heard}";
            DateTime at = DateTime.UtcNow;
            window.RequestLog.Hear(new RequestBegan(id, at, id, "GET", $"/orders/{heard++}", string.Empty), "Shop", null);
            for (int i = 0; i < commands; i++)
            {
                window.RequestLog.Hear(new CommandRan(id, at, $"{id}-{i}", "SELECT \"o\".\"Id\" FROM \"Orders\" AS \"o\" WHERE \"o\".\"Id\" = @id", 1.5, null, null), "Shop", null);
            }
            if (ended)
            {
                window.RequestLog.Hear(new RequestEnded(id, at, id, 200), "Shop", null);
            }
            return id;
        }
    }

    /// <summary>The tooltip of each of a request's command rows, as the pointer resting on the row shows it, each row
    /// scrolled into view first.</summary>
    private static string[] CommandTips(MainWindow window, LiveRequest request)
    {
        Avalonia.Controls.ListBox list = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.Requests).OfType<Avalonia.Controls.ListBox>().Single();
        IReadOnlyList<object> rows = window.Requests.Rows;
        List<string> tips = [];
        for (int at = 0; at < rows.Count; at++)
        {
            if (rows[at] is RequestsView.CommandRow row && row.Request == request)
            {
                list.ScrollIntoView(at);
                Ui.Settle();
                Avalonia.Controls.Control shown = list.ContainerFromIndex(at) ?? throw new Xunit.Sdk.XunitException($"The command row {at} is not shown");
                tips.Add(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(shown).OfType<Avalonia.Controls.Control>()
                    .Select(Avalonia.Controls.ToolTip.GetTip).OfType<string>().First());
            }
        }
        return [.. tips];
    }

    /// <summary>The shop written into a folder and restored from the run's local feed, its launch profile serving at the port
    /// given: this class's and an agent's test of the requests (McpRequestsTests); restored from the UI thread, the
    /// window's loop running meanwhile.</summary>
    internal static void WriteShop(string folder, int port)
    {
        Write(folder, port);
        Restore.Run(folder);
    }

    /// <inheritdoc cref="WriteShop"/>
    internal static Task WriteShopAsync(string folder, int port)
    {
        Write(folder, port);
        return Restore.RunAsync(folder);
    }

    private static void Write(string folder, int port)
    {
        AvaloniaApp.UseFeed(folder);
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>")
            .With($"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"{ShopApp.Version}\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Properties/launchSettings.json",
            $"{{\n  \"profiles\": {{\n    \"http\": {{ \"commandName\": \"Project\", \"applicationUrl\": \"http://127.0.0.1:{port}\" }}\n  }}\n}}\n");
        TestFolders.Write(folder, "Program.cs", Program);
    }

    /// <summary>The app's answer at the address, asked from the UI thread with the window's loop running meanwhile, never
    /// waiting for the window's work to land; a test with no window awaits the client itself.</summary>
    internal static string Get(HttpClient client, string url) => Asked(() => client.GetStringAsync(url), $"the shop's answer at {url}");

    /// <summary>A request of the test's own, sent on the thread pool and waited for on the UI thread for as long as the client
    /// gives it and a moment more, so the client's own timeout fails it first.</summary>
    private static T Asked<T>(Func<Task<T>> request, string what) =>
        Ui.AwaitPumped(Waiting.Apart(request), what, TimeSpan.FromSeconds(100) + TestFolders.Spare);

    private static int LineOf(string text) => Array.FindIndex(Program.Split('\n'), l => l.Contains(text, StringComparison.Ordinal));
}
