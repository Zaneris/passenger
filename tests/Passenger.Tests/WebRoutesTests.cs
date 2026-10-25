using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Endpoints;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A web app's routes, read from its code with no build and no running app: the Endpoints window lists each kind
/// under its project with its methods, template and parameters; the icon on a route's line sends its request to the
/// running app, from an entry its project's .http file gains for it; Go to Declaration goes from that request to the route,
/// and Find Usages from the route to the request.</summary>
public sealed class WebRoutesTests
{
    /// <summary>Each route the shop declares, as the Endpoints window titles it, with what declares it.</summary>
    private static readonly (string Title, string Kind)[] Declared =
    [
        ("GET /health", RouteKinds.Minimal),
        ("GET /api/todos/{id:int}", RouteKinds.Minimal),
        ("POST /api/todos", RouteKinds.Minimal),
        ("DELETE /admin/cache", RouteKinds.Minimal),
        ("PUT /orders/{id:guid}", RouteKinds.Minimal),
        ("GET /api/Products/{id}", RouteKinds.Controller),
        ("POST /api/Products", RouteKinds.Controller),
        ("GET /Admin/Reports/Daily", RouteKinds.Controller),
        ("ANY /Home/About/{id?}", RouteKinds.Controller),
        ("POST /Home/Save/{id?}", RouteKinds.Controller),
        ("GET /", RouteKinds.RazorPage),
        ("GET /Orders/Edit/{id:int}", RouteKinds.RazorPage),
        ("POST /Orders/Edit/{id:int}?handler=Cancel", RouteKinds.RazorPage),
        ("GET /counter", RouteKinds.Blazor),
        ("HUB /chat", RouteKinds.SignalR),
    ];

    [AvaloniaFact]
    public void The_endpoints_window_lists_each_kind_of_route_the_apps_code_declares_with_its_parameters()
    {
        string folder = Shop(port: 5555);
        // A file of routes 20,000 lines long, and one as long that declares none: the lines one comment.
        string notes = $"/*\n{string.Concat(Enumerable.Range(0, 20_000).Select(i => $"The orders' notes, line {i}\n"))}*/\n";
        File.AppendAllText(Path.Combine(folder, "Orders.cs"), notes);
        TestFolders.Write(folder, "Notes.cs", "namespace Shop;\n\npublic static class Notes\n{\n}\n" + notes);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Endpoints);
            Ui.WaitFor(() => Declared.All(d => Titles(window).Contains(d.Title)), "every route the shop declares, listed",
                TimeSpan.FromMinutes(3));
            Assert.Equal([.. Declared.OrderBy(d => d.Title, StringComparer.Ordinal)],
                [.. window.Endpoints.Routes.Select(r => (RouteRequests.Title(r), r.Kind)).OrderBy(d => d.Item1, StringComparer.Ordinal)]);

            WebRoute todo = Route(window, "GET /api/todos/{id:int}");
            RouteParameter id = Assert.Single(todo.Parameters);
            Assert.Equal(("id", "route", "int"), (id.Name, id.Source, id.Constraints));
            WebRoute order = Route(window, "PUT /orders/{id:guid}");
            Assert.Equal("Orders.Update", order.Handler);
            Assert.Equal("Order", order.BodyType);
            Assert.Equal("{\n  \"customer\": \"\",\n  \"lines\": [\n    {\n      \"product\": \"\",\n      \"quantity\": 0\n    }\n  ],\n  \"status\": \"Open\"\n}",
                order.Body);
            WebRoute product = Route(window, "GET /api/Products/{id}");
            Assert.Equal(["id (route)", "details (query)"], product.Parameters.Select(p => $"{p.Name} ({p.Source})"));
            Assert.Equal("Product", Route(window, "POST /api/Products").BodyType);
            Assert.Equal(["Send"], Route(window, "HUB /chat").Operations);

            // A key typed in a file of routes costs the UI thread what it costs in a file as long that declares none.
            long plain = KeyCosts(Path.Combine(folder, "Notes.cs"), routes: false);
            long routed = KeyCosts(Path.Combine(folder, "Orders.cs"), routes: true);
            Assert.True(routed < plain + 512 * 1024,
                $"A key in Orders.cs, 20,000 lines declaring a route, allocated {routed / 1024} KB on the UI thread, one in Notes.cs, as long with none, {plain / 1024} KB: the file's text is hashed at each key");
            Passenger.App.Workspace.OpenDocument orders = window.EditorArea.Active!;
            Ui.WaitFor(() => orders.RouteMarks.Count > 0, "Orders.cs's icon back once its routes are read again", TimeSpan.FromMinutes(1));

            window.OpenFile(Path.Combine(folder, "Program.cs"));
            Ui.Settle();
            Typing.Replace(window, "app.MapGet(\"/health\", () => \"ok\");", "app.MapGet(\"/health\", () => \"ok\");\napp.MapGet(\"/version\", () => \"1\");");
            Ui.WaitFor(() => Titles(window).Contains("GET /version"), "the route typed into Program.cs, listed", TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }

        // The least a key typed at a long file's end allocated on the UI thread, the icons of its routes shown first.
        long KeyCosts(string file, bool routes)
        {
            window.OpenFile(file);
            Ui.Settle();
            Passenger.App.Workspace.OpenDocument shown = window.EditorArea.Active!;
            if (routes)
            {
                Ui.WaitFor(() => shown.RouteMarks.Count > 0, $"{Path.GetFileName(file)}'s icons", TimeSpan.FromMinutes(1));
            }
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "line 19999");
            long least = long.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                window.KeyTextInput("x");
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            Ui.Settle();
            return least;
        }
    }

    [AvaloniaFact]
    public void A_routes_icon_sends_its_request_to_the_running_app_from_an_entry_written_into_its_http_file_and_each_goes_to_the_other()
    {
        using HeldPort port = new();
        string folder = Shop(port.Port);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.OpenFile(Path.Combine(folder, "Program.cs"));
            Ui.Settle();
            Passenger.App.Workspace.OpenDocument program = window.EditorArea.Active!;
            int line = Line(program, "todos.MapGet(");
            try
            {
                Ui.WaitFor(() => program.RouteMarks.Any(m => m.Line == line), "the send icon beside the todo's route", TimeSpan.FromMinutes(3));
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException($"No send icon on line {line}: marks [{string.Join(", ", program.RouteMarks.Select(m => m.Line))}], "
                    + $"text hash {TextHash.Of(program.Text.Snapshot.ToString())}, routes: {string.Join("; ", window.Session.Routes.All.Select(r => $"{RouteRequests.Title(r)} {r.Path}:{r.At.Line} {r.SourceHash}"))}, doc {program.Path}");
            }

            port.LetGo(Path.Combine(folder, "gate"));
            Assert.True(window.SelectConfiguration("Shop: http"), $"No configuration Shop: http among {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Ui.Launched(window, window.RunAsync(), "the shop to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("Now listening on:", StringComparison.Ordinal), "the shop to listen",
                TimeSpan.FromMinutes(2));

            MenuItem send = Assert.Single(GutterMenu(window, line));
            Assert.Equal("Send GET /api/todos/{id:int}", send.Header);
            Ui.Choose(window.RouteMarkMenu!, "Send GET /api/todos/{id:int}");
            Ui.WaitFor(() => window.Endpoints.Response.Response is not null, "the running shop's answer", TimeSpan.FromMinutes(1));
            Assert.True(window.Endpoints.Response.Passed, $"The route's request failed: {window.Endpoints.Response.Summary}");
            Assert.Contains("Write tests", window.Endpoints.Response.Shown, StringComparison.Ordinal);

            string http = Path.Combine(folder, "Shop.http");
            Ui.WaitFor(() => File.Exists(http), "Shop.http, written for the request");
            string written = File.ReadAllText(http);
            Assert.Contains($"@Shop_HostAddress = http://127.0.0.1:{port.Port}\n", written, StringComparison.Ordinal);
            Assert.Contains("@id = 1\nGET {{Shop_HostAddress}}/api/todos/{{id}}\nAccept: application/json\n", written, StringComparison.Ordinal);

            int post = Line(program, "todos.MapPost(");
            Ui.WaitFor(() => program.RouteMarks.Any(m => m.Line == post), "the send icon beside the todo's POST");
            GutterMenu(window, post);
            Ui.Choose(window.RouteMarkMenu!, "Send POST /api/todos");
            Ui.WaitFor(() => window.Endpoints.Response.Response is not null && window.Endpoints.Response.Summary.StartsWith("201", StringComparison.Ordinal),
                "the shop's answer to the POST", TimeSpan.FromMinutes(1));
            written = File.ReadAllText(http);
            Assert.Contains("POST {{Shop_HostAddress}}/api/todos\nAccept: application/json\nContent-Type: application/json\n\n{\n  \"id\": 0,\n  \"title\": \"\",\n  \"done\": false\n}\n",
                written, StringComparison.Ordinal);
            Assert.Equal(1, written.Split("GET {{Shop_HostAddress}}/api/todos/{{id}}").Length - 1);

            GutterMenu(window, line);
            Ui.Choose(window.RouteMarkMenu!, "Send GET /api/todos/{id:int}");
            Ui.WaitFor(() => window.Endpoints.Response.Response is not null && window.Endpoints.Response.Shown.Contains("Write tests", StringComparison.Ordinal),
                "the second answer", TimeSpan.FromMinutes(1));
            Assert.Equal(written, File.ReadAllText(http));

            window.OpenFile(Path.Combine(folder, "Controllers", "ProductsController.cs"));
            Ui.Settle();
            Passenger.App.Workspace.OpenDocument products = window.EditorArea.Active!;
            int action = Line(products, "[HttpGet(\"{id}\")]");
            Ui.WaitFor(() => products.RouteMarks.Any(m => m.Line == action), "the send icon beside the controller's action");
            GutterMenu(window, action);
            Ui.Choose(window.RouteMarkMenu!, "Send GET /api/Products/{id}");
            Ui.WaitFor(() => window.Endpoints.Response.Response is not null && window.Endpoints.Response.Shown.Contains("Tea", StringComparison.Ordinal),
                "the controller's answer", TimeSpan.FromMinutes(1));
            written = File.ReadAllText(http);
            Assert.Contains("# Controller: ProductsController.Get\n@details = true\nGET {{Shop_HostAddress}}/api/Products/{{id}}?details={{details}}\n", written, StringComparison.Ordinal);

            window.OpenFile(Path.Combine(folder, "Program.cs"));
            Ui.Settle();
            window.OpenFile(http);
            Ui.Settle();
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "GET {{Shop_Host");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith("Program.cs", StringComparison.Ordinal) == true
                && window.EditorArea.CodeEditor.CaretPosition.Line == line, "Go to Declaration to the route's MapGet");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "todos.Map");
            Ui.Run(window, AppActions.FindUsages);
            Ui.WaitFor(() => window.Find.Rows.Any(r => r.Usage is not null), "the route's requests in the Find window");
            Passenger.App.ToolWindows.Usage usage = Assert.Single(window.Find.Rows.Select(r => r.Usage).OfType<Passenger.App.ToolWindows.Usage>());
            Assert.EndsWith("Shop.http", usage.Path, StringComparison.Ordinal);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning, "the shop to stop");
            window.Close();
        }
    }

    /// <summary>gRPC's packages at the versions tests/AspireGraphs' AppHost restores into the package cache (Aspire's
    /// dashboard client brings them), so the service restores from there, never from nuget.org.</summary>
    private static readonly (string Id, string Version)[] GrpcPackages = [("Grpc.AspNetCore", "2.83.0"), ("Google.Protobuf", "3.36.1")];

    [AvaloniaFact]
    public void A_grpc_services_methods_are_listed_by_their_paths_from_its_proto()
    {
        string cache = AvaloniaApp.Metadata("NuGetPackageRoot");
        foreach ((string id, string version) in GrpcPackages)
        {
            Assert.True(Directory.Exists(Path.Combine(cache, id.ToLowerInvariant(), version)),
                $"{id} {version} is not in the package cache at {cache}: restore the tests first (tests/AspireGraphs' AppHost brings it)");
        }
        string folder = TestFolders.Create("routes-grpc-");
        TestFolders.Write(folder, "Greeter.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>").With(
            "  <ItemGroup>\n" + string.Concat(GrpcPackages.Select(p => $"    <PackageReference Include=\"{p.Id}\" Version=\"{p.Version}\" />\n"))
            + "    <Protobuf Include=\"Protos/greet.proto\" GrpcServices=\"Server\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Protos/greet.proto", """
            syntax = "proto3";

            option csharp_namespace = "Greetings";

            package greet;

            service Greeter {
              rpc SayHello (HelloRequest) returns (HelloReply);
              rpc SayGoodbye (HelloRequest) returns (HelloReply);
            }

            message HelloRequest {
              string name = 1;
            }

            message HelloReply {
              string message = 1;
            }
            """);
        TestFolders.Write(folder, "GreeterService.cs", """
            using Grpc.Core;

            namespace Greetings;

            public class GreeterService : Greeter.GreeterBase
            {
                public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context) =>
                    Task.FromResult(new HelloReply { Message = "Hello " + request.Name });

                public override Task<HelloReply> SayGoodbye(HelloRequest request, ServerCallContext context) =>
                    Task.FromResult(new HelloReply { Message = "Goodbye " + request.Name });
            }
            """);
        TestFolders.Write(folder, "Program.cs", """
            using Greetings;

            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.Services.AddGrpc();
            WebApplication app = builder.Build();
            app.MapGrpcService<GreeterService>();
            app.Run();
            """);
        AvaloniaApp.UseFeed(folder);
        Restore.Run(folder);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Endpoints);
            window.OpenFile(Path.Combine(folder, "GreeterService.cs"));
            Ui.WaitFor(() => Titles(window).Contains("POST /greet.Greeter/SayGoodbye"), "the gRPC service's methods, listed", TimeSpan.FromMinutes(3));
            Assert.Equal(["POST /greet.Greeter/SayGoodbye", "POST /greet.Greeter/SayHello"], Titles(window).Order(StringComparer.Ordinal));
            WebRoute hello = Route(window, "POST /greet.Greeter/SayHello");
            Assert.Equal((RouteKinds.Grpc, "GreeterService.SayHello", "HelloRequest"), (hello.Kind, hello.Handler, hello.BodyType));
            // A gRPC method takes a gRPC client, not the Endpoints window's HTTP request: no icon sends it.
            Ui.Settle();
            Assert.Empty(window.EditorArea.Active!.RouteMarks);
        }
        finally
        {
            window.Close();
        }
    }

    private static IReadOnlyList<string> Titles(MainWindow window) => [.. window.Endpoints.Routes.Select(RouteRequests.Title)];

    private static WebRoute Route(MainWindow window, string title) =>
        window.Endpoints.Routes.FirstOrDefault(r => RouteRequests.Title(r) == title) ?? throw new Xunit.Sdk.XunitException($"{title} is not listed: {string.Join(", ", Titles(window))}");

    private static int Line(Passenger.App.Workspace.OpenDocument document, string text)
    {
        int line = Array.FindIndex(document.Text.Snapshot.ToString().Split('\n'), l => l.Contains(text, StringComparison.Ordinal));
        Assert.True(line >= 0, $"No line has {text}");
        return line;
    }

    /// <summary>The menu of the icon beside a line of the file shown, opened by a click there.</summary>
    private static MenuItem[] GutterMenu(MainWindow window, int line)
    {
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        TextSnapshot text = editor.Document.Snapshot;
        editor.ScrollTo(new Vector(0, Math.Max(0, line * editor.LineHeight - editor.Bounds.Height / 3)));
        Ui.Settle();
        double x = Enumerable.Range(0, 400).First(x => editor.IsOnGutterColumn(editor.Gutter!.Marks, new Point(x, 0))) + 3;
        Ui.Click(editor, new Point(x, editor.PointOf(text.GetLineStart(line)).Y + editor.LineHeight / 2));
        return [.. Assert.IsType<MenuFlyout>(window.RouteMarkMenu).Items.OfType<MenuItem>()];
    }

    /// <summary>A web app of every kind of route, restored, its launch profile serving at the port given: minimal APIs (a
    /// group in a local, a group its convention keeps, a group mapped in a method of its own), API controllers (an area's
    /// too), Razor Pages with a named handler, a Blazor page and a SignalR hub.</summary>
    internal static string Shop(int port)
    {
        string folder = TestFolders.Create("routes-");
        WriteShop(folder, port);
        Restore.Run(folder);
        return folder;
    }

    /// <summary>The shop's files written into a folder: this class's and an agent's test of the routes (McpLanguagesTests).</summary>
    internal static void WriteShop(string folder, int port)
    {
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>"));
        TestFolders.Write(folder, "Properties/launchSettings.json",
            $"{{\n  \"profiles\": {{\n    \"http\": {{ \"commandName\": \"Project\", \"applicationUrl\": \"http://127.0.0.1:{port}\" }}\n  }}\n}}\n");
        TestFolders.Write(folder, "Program.cs", """
            using Shop;

            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();
            builder.Services.AddRazorPages();
            builder.Services.AddSignalR();
            WebApplication app = builder.Build();

            app.MapGet("/health", () => "ok");
            RouteGroupBuilder todos = app.MapGroup("/api/todos");
            todos.MapGet("/{id:int}", (int id) => new Todo(id, "Write tests", false));
            todos.MapPost("/", (Todo todo) => Results.Created($"/api/todos/{todo.Id}", todo));
            app.MapGroup("/admin").WithTags("admin").MapDelete("/cache", () => Results.NoContent());
            app.MapOrders();
            app.MapControllers();
            app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
            app.MapRazorPages();
            app.MapHub<ChatHub>("/chat");
            app.Run();
            """);
        TestFolders.Write(folder, "Orders.cs", """
            namespace Shop;

            public static class Orders
            {
                public static void MapOrders(this IEndpointRouteBuilder app)
                {
                    RouteGroupBuilder orders = app.MapGroup("/orders");
                    orders.MapPut("/{id:guid}", Update);
                }

                private static IResult Update(Guid id, Order order) => Results.Ok(order);
            }

            public record Todo(int Id, string Title, bool Done);

            public record Order(string Customer, List<Line> Lines, Status Status);

            public record Line(string Product, int Quantity);

            public enum Status
            {
                Open,
                Shipped,
            }
            """);
        TestFolders.Write(folder, "ChatHub.cs", """
            using Microsoft.AspNetCore.SignalR;

            namespace Shop;

            public class ChatHub : Hub
            {
                public Task Send(string message) => Clients.All.SendAsync("said", message);
            }
            """);
        TestFolders.Write(folder, "Controllers/ProductsController.cs", """
            using Microsoft.AspNetCore.Mvc;

            namespace Shop.Controllers;

            [ApiController]
            [Route("api/[controller]")]
            public class ProductsController : ControllerBase
            {
                [HttpGet("{id}")]
                public ActionResult<Product> Get(int id, [FromQuery] bool details) => new Product(id, "Tea");

                [HttpPost]
                public IActionResult Create(Product product) => Ok(product);
            }

            [Area("Admin")]
            [Route("[area]/[controller]/[action]")]
            public class ReportsController : ControllerBase
            {
                [HttpGet]
                public string Daily() => "ok";
            }

            // No route of its own: the conventional route's.
            public class HomeController : ControllerBase
            {
                public string About(int? id) => "about";

                [HttpPost]
                public string Save() => "saved";
            }

            public record Product(int Id, string Name);
            """);
        TestFolders.Write(folder, "Pages/Index.cshtml", "@page\n<h1>Shop</h1>\n");
        TestFolders.Write(folder, "Pages/Orders/Edit.cshtml", "@page \"{id:int}\"\n@model Shop.Pages.Orders.EditModel\n<h1>Order @Model.Id</h1>\n");
        TestFolders.Write(folder, "Pages/Orders/Edit.cshtml.cs", """
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Mvc.RazorPages;

            namespace Shop.Pages.Orders;

            public class EditModel : PageModel
            {
                public int Id { get; set; }

                public void OnGet(int id) => Id = id;

                public IActionResult OnPostCancel(int id) => RedirectToPage("/Index");
            }
            """);
        TestFolders.Write(folder, "Components/Counter.razor", "@page \"/counter\"\n\n<h1>Counter</h1>\n");
    }
}
