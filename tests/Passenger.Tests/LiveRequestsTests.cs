using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Endpoints;
using Passenger.App.Run;
using Passenger.Profiler;
using Passenger.Roslyn.Extension;

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

    /// <summary>Two requests of one trace (a page's two calls carrying its trace on) served at once by one app, each running
    /// a query of its own, each list only their own command: a command is tied to the request whose activity it ran under,
    /// not to the trace's latest.</summary>
    [AvaloniaFact]
    public void Two_requests_of_one_trace_served_at_once_each_list_only_their_own_commands()
    {
        using HeldPort port = new();
        string folder = TestFolders.Create("requests-pair-");
        WriteShop(folder, port.Port, Program.Replace("app.MapGet(\"/boom\", Fail);", Paired, StringComparison.Ordinal) + PairedTypes);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Requests);
            port.LetGo(Path.Combine(folder, "gate"));
            Assert.True(window.SelectConfiguration("Shop: http"), $"No configuration Shop: http among {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Ui.Launched(window, window.RunAsync(), "the shop to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("Now listening on:", StringComparison.Ordinal), "the shop to listen",
                TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.RequestLog.Following.StartsWith("Following", StringComparison.Ordinal) && window.RequestLog.Following.Contains("process", StringComparison.Ordinal),
                () => $"the Requests window to follow the shop; it says: {window.RequestLog.Following}", TimeSpan.FromMinutes(1));

            using HttpClient client = new();
            string[] answers = Asked(() => Task.WhenAll(Traced(client, port.Port, "orders"), Traced(client, port.Port, "customers")), "the shop's answers to the two requests");
            Assert.Equal(["3 orders", "0 customers"], answers);
            LiveRequest[] Pair() => [.. window.RequestLog.Requests.Where(r => r.Path.StartsWith("/pair/", StringComparison.Ordinal))];
            Ui.WaitFor(() => Pair().Count(r => r.Status is not null) == 2 && Pair().Sum(r => r.Commands.Count) == 2,
                () => $"the two requests, listed as they ended, with their two commands: [{string.Join("; ", Pair().Select(r => $"{r.Path} status {r.Status}: {string.Join(" | ", r.Commands.Select(c => c.Text))}"))}]",
                TimeSpan.FromSeconds(30));
            LiveRequest orders = Pair().Single(r => r.Path == "/pair/orders");
            LiveRequest customers = Pair().Single(r => r.Path == "/pair/customers");
            Assert.True(orders.Trace.Length > 0 && orders.Trace == customers.Trace, $"The two requests are not of one trace: {orders.Trace} and {customers.Trace}");
            Assert.Contains("FROM \"Orders\"", Assert.Single(orders.Commands).Text, StringComparison.Ordinal);
            Assert.Contains("FROM \"Customers\"", Assert.Single(customers.Commands).Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning, "the shop to stop");
            window.Close();
        }

        // Both carry one trace, as a page's calls carry the page's on.
        static async Task<string> Traced(HttpClient client, int port, string side)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"http://127.0.0.1:{port}/pair/{side}");
            request.Headers.Add("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
            using HttpResponseMessage answer = await client.SendAsync(request);
            return await answer.Content.ReadAsStringAsync();
        }
    }

    /// <summary>A route whose two requests wait for each other before each runs its own query, so they are served at once.</summary>
    private const string Paired = """
        app.MapGet("/pair/{side}", async (string side, Shop db) =>
        {
            if (Interlocked.Increment(ref Pair.Arrived) == 2)
            {
                Pair.Both.TrySetResult();
            }
            await Pair.Both.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return side == "orders" ? $"{await db.Orders.CountAsync()} orders" : $"{await db.Customers.CountAsync()} customers";
        });
        app.MapGet("/boom", Fail);
        """;

    private const string PairedTypes = """

        static class Pair
        {
            public static int Arrived;

            public static readonly TaskCompletionSource Both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        """;

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
            // The log alone, as it keeps the requests while no one looks: a drawn row's cost is held by the test above.
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

    /// <summary>A request heard costs the window what it costs with five routes when the folder has five hundred: each
    /// route's template is split once, and the request's path is walked against them where it lies. The routes are stood
    /// in for by the catalog's answer from the C# server, the app's events by the profiler reader's.</summary>
    [AvaloniaFact]
    public void A_request_heard_costs_the_window_what_it_costs_with_five_routes_when_the_folder_has_five_hundred()
    {
        string folder = TestFolders.Create("requests-routes-");
        string project = Path.Combine(folder, "Shop.csproj");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        int heard = 0;
        try
        {
            // The log alone, as it keeps the requests while no one looks: a drawn row's cost is held by the test above.
            Ui.Settle();
            long few = Costs(5);
            long many = Costs(500);
            Assert.True(many < few + 1024,
                $"A request heard with 500 routes allocated {many:N0} bytes on the UI thread, with 5 routes {few:N0}: its path is matched against each route by making it again");
            Assert.Equal("/orders/{id}", window.RequestLog.Requests[^1].Route?.Template);
        }
        finally
        {
            window.Close();
        }

        // The least a request heard allocated on the UI thread with this many routes, the one it reaches the last.
        long Costs(int routes)
        {
            window.RequestLog.Clear();
            List<WebRoute> declared = [];
            for (int i = 0; i < routes - 1; i++)
            {
                string template = (i % 3) switch
                {
                    0 => $"/api/things{i}",
                    1 => $"/api/things{i}/{{id}}/parts",
                    _ => $"/files{i}/{{name}}.{{extension}}",
                };
                declared.Add(Route(template));
            }
            declared.Add(Route("/orders/{id}"));
            window.Session.Routes.Took(new RoutesFound(declared, [project]), window.Session.Routes.ToRead());
            for (int i = 0; i < 3; i++)
            {
                Hear(Request());
            }
            long least = long.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                WebEvent[] told = Request();
                long before = GC.GetAllocatedBytesForCurrentThread();
                Hear(told);
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return least;
        }

        WebRoute Route(string template) => new(project, RouteKinds.Minimal, ["GET"], template, null, [], "Handle", Path.Combine(folder, "Program.cs"),
            new Place(0, 0), new Place(0, 1), string.Empty);

        // A request as the app tells of it, made before it is measured: its start and its end.
        WebEvent[] Request()
        {
            string id = $"request-{heard}";
            DateTime at = DateTime.UtcNow;
            return [new RequestBegan(id, at, id, "GET", $"/orders/{heard++}", string.Empty), new RequestEnded(id, at, id, 200)];
        }

        void Hear(WebEvent[] told)
        {
            foreach (WebEvent one in told)
            {
                window.RequestLog.Hear(one, "Shop", null);
            }
            Ui.Settle();
        }
    }

    /// <summary>A 2 KB SQL command of hundreds of words, its parameters' values logged, costs the window what one of the
    /// same length with a few long words costs: its text is read for its columns as ranges of its characters, with no
    /// string made for each word, and the log masked once.</summary>
    [AvaloniaFact]
    public void A_two_kilobyte_SQL_command_costs_the_window_no_string_for_each_of_its_words()
    {
        string folder = TestFolders.Create("requests-sql-");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        int heard = 0;
        try
        {
            // The log alone, as it keeps the requests while no one looks: a row drawn shapes each word of its text.
            Ui.Settle();
            long words = Costs(round => string.Concat(Enumerable.Repeat($"c{round}, ", 300)));
            long few = Costs(round => new string((char)('a' + round), 1200) + ", ");
            Assert.True(words < few + 1024,
                $"A 2 KB command of 300 words cost the UI thread {words:N0} bytes, one of its length in a few words {few:N0}: each word of its text is made a string");
            LiveCommand command = Assert.Single(window.RequestLog.Requests[^1].Commands);
            Assert.Equal("@p0='••••••', @p1='Ada' (Size = 3)", command.Parameters);
        }
        finally
        {
            window.Close();
        }

        // The least a request of one such command (its log heard first) allocated on the UI thread, each text new to it.
        long Costs(Func<int, string> selected)
        {
            for (int i = 0; i < 3; i++)
            {
                Hear(Request(Sql(selected(i))));
            }
            long least = long.MaxValue;
            for (int round = 3; round < 8; round++)
            {
                WebEvent[] told = Request(Sql(selected(round)));
                long before = GC.GetAllocatedBytesForCurrentThread();
                Hear(told);
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return least;
        }

        // A command's text of 2 KB: the columns selected, then a WHERE that compares a secret's column and a name's.
        static string Sql(string selected)
        {
            string where = " FROM \"Customers\" AS \"c\" WHERE \"c\".\"Password\" = @p0 AND \"c\".\"Name\" = @p1";
            string text = "SELECT " + selected + "1" + where;
            return text + new string(' ', 2048 - text.Length);
        }

        // A request as the app tells of it, made before it is measured: its start, EF Core's log of its command, the command and its end.
        WebEvent[] Request(string sql)
        {
            string id = $"request-{heard++}";
            DateTime at = DateTime.UtcNow;
            string logged = $"Executed DbCommand (1ms) [Parameters=[@p0='hunter22' (Size = 8), @p1='Ada' (Size = 3)], CommandType='Text', CommandTimeout='30']\n{sql}";
            return [new RequestBegan(id, at, id, "GET", "/customers", string.Empty), new CommandLogged(id, at, logged),
                new CommandRan(id, at, $"{id}-0", sql, 1.5, null, null), new RequestEnded(id, at, id, 200)];
        }

        void Hear(WebEvent[] told)
        {
            foreach (WebEvent one in told)
            {
                window.RequestLog.Hear(one, "Shop", null);
            }
            Ui.Settle();
        }
    }

    /// <summary>A command's row shows the command's own tooltip where the row last showed a request: with more requests
    /// listed than the window shows, each command heard in a batch of its own is handed the row of a request scrolled off
    /// the list's foot, as a slow machine's batches hand them. The app's events are stood in for by the profiler reader's.</summary>
    [AvaloniaFact]
    public void A_command_handed_the_row_of_a_request_shows_its_own_tooltip()
    {
        string folder = TestFolders.Create("requests-reused-");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Requests);
            Ui.Settle();
            int heard = 0;
            ListBox list = window.Requests.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.True(list.Bounds.Height >= 4 * ListRow.Height, $"The Requests window's list is {list.Bounds.Height} high, too low to show a request and its three commands");
            while (window.Requests.Rows.Count * ListRow.Height < list.Bounds.Height + (4 * ListRow.Height))
            {
                string each = $"request-{heard}";
                Hear(new RequestBegan(each, DateTime.UtcNow, each, "GET", $"/orders/{heard++}", string.Empty), new RequestEnded(each, DateTime.UtcNow, each, 200));
            }

            string id = "signing-up";
            DateTime at = DateTime.UtcNow;
            string[] texts = ["INSERT INTO \"Customers\" (\"Name\") VALUES (@p0)", "UPDATE \"Customers\" SET \"Name\" = @p0", "SELECT COUNT(*) FROM \"Customers\""];
            Hear(new RequestBegan(id, at, id, "GET", "/sign-up/ada", string.Empty));
            for (int i = 0; i < texts.Length; i++)
            {
                Hear(new CommandRan(id, at, $"{id}-{i}", texts[i], 1.5, null, null));
            }
            Hear(new RequestEnded(id, at, id, 200));
            string[] tips = CommandTips(window, window.RequestLog.Requests.Single(r => r.Path == "/sign-up/ada"));
            Assert.True(tips.SequenceEqual(texts), $"The pointer resting on the sign-up's commands, each in the row of a request before, reads [{string.Join(" | ", tips)}], not their own texts [{string.Join(" | ", texts)}]");
        }
        finally
        {
            window.Close();
        }

        // Events the window takes in one batch, laid out before the next.
        void Hear(params WebEvent[] told)
        {
            foreach (WebEvent one in told)
            {
                window.RequestLog.Hear(one, "Shop", null);
            }
            Ui.Settle();
        }
    }

    /// <summary>The tooltip of each of a request's command rows, as the pointer resting on the row's words finds it (never
    /// on a part the row hides), each row found by its command and scrolled into view first.</summary>
    private static string[] CommandTips(MainWindow window, LiveRequest request)
    {
        ListBox list = window.Requests.GetVisualDescendants().OfType<ListBox>().Single();
        List<string> tips = [];
        foreach (RequestsView.CommandRow row in window.Requests.Rows.OfType<RequestsView.CommandRow>().Where(r => r.Request == request).ToList())
        {
            list.ScrollIntoView(window.Requests.Rows.ToList().IndexOf(row));
            Ui.Settle();
            Control shown = list.ContainerFromIndex(window.Requests.Rows.ToList().IndexOf(row)) ?? throw new Xunit.Sdk.XunitException($"The row of the command {row.Command.Text} is not shown");
            TextBlock words = shown.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 })
                ?? throw new Xunit.Sdk.XunitException($"The row of the command {row.Command.Text} shows no words");
            Point at = words.TranslatePoint(new Point(2, words.Bounds.Height / 2), window) ?? throw new Xunit.Sdk.XunitException("The command's row is not laid out");
            // What covers the row (a tooltip the window opened under a pointer that never moved) is passed over.
            Visual? found = window.InputHitTest(at, v => v == shown || v.IsVisualAncestorOf(shown) || shown.IsVisualAncestorOf(v)) as Visual;
            tips.Add((found ?? list).GetSelfAndVisualAncestors().TakeWhile(v => v != list).OfType<Control>().Select(ToolTip.GetTip).OfType<string>().FirstOrDefault()
                ?? throw new Xunit.Sdk.XunitException($"The pointer on the words of the command {row.Command.Text}'s row finds {found?.GetType().Name ?? "nothing"}, with no tooltip"));
        }
        return [.. tips];
    }

    /// <summary>The shop written into a folder and restored from the run's local feed, its launch profile serving at the port
    /// given: this class's and an agent's test of the requests (McpRequestsTests); restored from the UI thread, the
    /// window's loop running meanwhile.</summary>
    internal static void WriteShop(string folder, int port, string program = Program)
    {
        Write(folder, port, program);
        Restore.Run(folder);
    }

    /// <inheritdoc cref="WriteShop"/>
    internal static Task WriteShopAsync(string folder, int port)
    {
        Write(folder, port, Program);
        return Restore.RunAsync(folder);
    }

    private static void Write(string folder, int port, string program)
    {
        AvaloniaApp.UseFeed(folder);
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>")
            .With($"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"{ShopApp.Version}\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Properties/launchSettings.json",
            $"{{\n  \"profiles\": {{\n    \"http\": {{ \"commandName\": \"Project\", \"applicationUrl\": \"http://127.0.0.1:{port}\" }}\n  }}\n}}\n");
        TestFolders.Write(folder, "Program.cs", program);
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
