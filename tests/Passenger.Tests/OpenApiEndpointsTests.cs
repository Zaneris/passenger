using System.Globalization;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Endpoints;
using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A minimal API that describes itself with MapOpenApi, its document read from the running app on this
/// machine: Generate Requests from OpenAPI writes a request per operation into its project's .http file, bodies from
/// the request schemas, one Undo taking them back; a request's answer is held to its response schema, a member that
/// breaks it said at that member; and a document the build writes makes the requests of an app not running. Its
/// packages come from the package cache alone, Microsoft.AspNetCore.OpenApi's and
/// Microsoft.Extensions.ApiDescription.Server's from the AspireGraphs restore.</summary>
public sealed class OpenApiEndpointsTests
{
    /// <summary>An app's OpenAPI document, running or built, makes its requests and holds its answers to their
    /// schemas.</summary>
    [AvaloniaFact]
    public void An_apps_openapi_document_makes_its_requests_and_checks_its_answers()
    {
        Flow.Step(nameof(A_minimal_APIs_document_makes_its_requests_and_a_response_that_breaks_its_schema_is_said_at_the_member),
            A_minimal_APIs_document_makes_its_requests_and_a_response_that_breaks_its_schema_is_said_at_the_member);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_document_the_build_writes_makes_the_requests_of_an_app_that_is_not_running),
            A_document_the_build_writes_makes_the_requests_of_an_app_that_is_not_running);
        OpenWindows.CloseAll();
        Flow.Step(nameof(An_openapi_3_2_documents_query_and_additional_operation_are_listed_and_sent),
            An_openapi_3_2_documents_query_and_additional_operation_are_listed_and_sent);
    }

    private void A_minimal_APIs_document_makes_its_requests_and_a_response_that_breaks_its_schema_is_said_at_the_member()
    {
        using HeldPort held = new();
        int port = held.Port;
        string folder = Shop(port);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Assert.True(window.SelectConfiguration("Web: http"), $"No configuration Web: http among {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Ui.Launched(window, window.RunAsync(), "the shop to start", TimeSpan.FromMinutes(2));
            held.LetGo(HeldPort.GateIn(folder));
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("Now listening on:", StringComparison.Ordinal), "the shop to listen",
                TimeSpan.FromMinutes(2));

            window.ShowToolWindow(ToolWindowIds.Endpoints);
            string http = Path.Combine(folder, "Web", "Web.http");
            window.Perform(AppActions.GenerateOpenApiRequests);
            Ui.WaitFor(() => File.Exists(http) && window.Status?.StartsWith("Wrote 3 requests", StringComparison.Ordinal) == true,
                () => $"the requests the shop's OpenAPI document makes: {window.Status}", TimeSpan.FromMinutes(1));
            string written = File.ReadAllText(http);
            Assert.StartsWith($"@Web_HostAddress = http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}\n", written, StringComparison.Ordinal);
            Assert.Contains("# OpenAPI: GetItem\n@id = 0\nGET {{Web_HostAddress}}/items/{{id}}\nAccept: application/json\n", written, StringComparison.Ordinal);
            Assert.Contains("# OpenAPI: AddItem\nPOST {{Web_HostAddress}}/items\nAccept: application/json\nContent-Type: application/json\n\n{\n  \"name\": \"string\",\n  \"price\": 0\n}\n",
                written, StringComparison.Ordinal);
            Assert.Contains("# OpenAPI: Broken\nGET {{Web_HostAddress}}/broken\n", written, StringComparison.Ordinal);
            Ui.WaitFor(() => window.Endpoints.Requests.Count(r => r.File == http) == 3, "the three requests listed in the Endpoints window");

            // A document read again makes nothing a file calls already.
            window.Perform(AppActions.GenerateOpenApiRequests);
            Ui.WaitFor(() => window.Status?.StartsWith("Every operation of Web's OpenAPI document has a request", StringComparison.Ordinal) == true,
                () => window.Status ?? string.Empty);
            Assert.Equal(written, File.ReadAllText(http));

            EndpointRequest add = window.Endpoints.Requests.Single(r => r.File == http && r.Method == "POST");
            Ui.Await(window.Endpoints.TestAsync(add), "the POST's answer", TimeSpan.FromMinutes(1));
            Assert.True(window.Endpoints.Response.Passed, $"The POST made from the document failed: {window.Endpoints.Response.Summary} {string.Join("; ", window.Endpoints.Response.Breaks)}");
            Assert.Empty(window.Endpoints.Response.Breaks);

            EndpointRequest broken = window.Endpoints.Requests.Single(r => r.File == http && r.Url.EndsWith("/broken", StringComparison.Ordinal));
            Ui.Await(window.Endpoints.TestAsync(broken), "the broken answer", TimeSpan.FromMinutes(1));
            ResponseView answer = window.Endpoints.Response;
            Assert.False(answer.Passed, "An answer that breaks its schema passed");
            Assert.Contains(new SchemaBreak("/name", "a string or null was expected, not the number 42"), answer.Breaks);
            Assert.Contains(answer.Breaks, b => b.At == "/price" && b.Said.StartsWith("the string \"cheap\" does not match its pattern", StringComparison.Ordinal));

            // A request written by hand with no body has its body made from its schema.
            InteractiveTestDialog dialog = window.OpenInteractiveTest(add with { Body = string.Empty });
            Ui.Settle();
            Assert.Equal("{\n  \"name\": \"string\",\n  \"price\": 0\n}", (dialog.Body.Text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal));
            dialog.Close();
            Ui.Settle();

            // The requests went in as one command, which one Undo takes back.
            window.Confirm = (_, _, _) => Task.FromResult(true);
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Kind == NodeKind.Solution));
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => !File.Exists(http), () => $"Undo to take the generated Web.http away: {window.Status}");
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "Stop to end the shop");
            window.Close();
        }
    }

    /// <summary>A minimal API that serves no document of its own but whose build writes one (Microsoft.Extensions.ApiDescription.Server):
    /// built, never run, Generate Requests from OpenAPI reads the document the build wrote and makes its requests.</summary>
    private void A_document_the_build_writes_makes_the_requests_of_an_app_that_is_not_running()
    {
        string folder = TestFolders.Create("openapi-built-");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Web/Web.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "Web/Web.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>").With($"""
              <ItemGroup>
                <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="{AvaloniaApp.Metadata("AspNetCoreOpenApiVersion")}" />
                <PackageReference Include="Microsoft.Extensions.ApiDescription.Server" Version="{AvaloniaApp.Metadata("ApiDescriptionServerVersion")}" />
              </ItemGroup>

            """));
        TestFolders.Write(folder, "Web/Program.cs", """
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.Services.AddOpenApi();
            WebApplication app = builder.Build();
            app.MapGet("/items/{id}", (int id) => new Item($"item {id}", 2.5m)).WithName("GetItem");
            app.MapPost("/items", (Item item) => Results.Created("/items/1", item)).WithName("AddItem");
            app.Run();

            record Item(string Name, decimal Price);

            """);
        AvaloniaApp.UseFeed(folder);
        folder = PathNames.Canonical(folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            BuildOutcome built = Ui.Landed(window.BuildAsync(), "the build that writes the document");
            Assert.True(built.Succeeded, $"{built.Message}{Ui.BuildErrors(window)}");
            Assert.True(File.Exists(Path.Combine(folder, "Web", "obj", "Web.json")), "The build wrote no OpenAPI document where its OpenApiDocumentsDirectory says");
            string http = Path.Combine(folder, "Web", "Web.http");
            window.Perform(AppActions.GenerateOpenApiRequests);
            Ui.WaitFor(() => File.Exists(http) && window.Status?.StartsWith("Wrote 2 requests", StringComparison.Ordinal) == true,
                () => $"the requests the build's OpenAPI document makes: {window.Status}", TimeSpan.FromMinutes(1));
            string written = File.ReadAllText(http);
            Assert.Contains("# OpenAPI: GetItem\n@id = 0\nGET {{Web_HostAddress}}/items/{{id}}\n", written, StringComparison.Ordinal);
            Assert.Contains("# OpenAPI: AddItem\nPOST {{Web_HostAddress}}/items\nAccept: application/json\nContent-Type: application/json\n\n{\n  \"name\": \"string\",\n  \"price\": 0\n}\n",
                written, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An app whose OpenAPI 3.2 document, served at MapOpenApi's path, names a QUERY of its items (a body to filter
    /// them by) and a PURGE of them among its <c>additionalOperations</c>: Generate Requests from OpenAPI writes a request of
    /// each, the Endpoints window lists both, and each sent is answered as its document says, the QUERY's body taken.</summary>
    private void An_openapi_3_2_documents_query_and_additional_operation_are_listed_and_sent()
    {
        using HeldPort held = new();
        int port = held.Port;
        string folder = Shop(port, """"
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            WebApplication app = builder.Build();
            app.MapGet("/openapi/v1.json", () => Results.Text("""
                { "openapi": "3.2.0", "info": { "title": "Shop", "version": "1" }, "paths": { "/items": {
                    "query": { "operationId": "FindItems",
                      "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Filter" } } } },
                      "responses": { "200": { "description": "OK", "content": { "application/json": {
                        "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Item" } } } } } } },
                    "additionalOperations": { "PURGE": { "operationId": "PurgeItems", "responses": { "204": { "description": "Purged" } } } } } },
                  "components": { "schemas": {
                    "Filter": { "type": "object", "required": [ "name" ], "properties": { "name": { "type": "string" } } },
                    "Item": { "type": "object", "required": [ "name", "price" ], "properties": { "name": { "type": "string" }, "price": { "type": "number" } } } } } }
                """, "application/json"));
            app.MapMethods("/items", ["QUERY"], (Filter filter) => new[] { new Item($"{filter.Name} found", 2.5m) });
            app.MapMethods("/items", ["PURGE"], () => Results.NoContent());
            app.Run();

            record Filter(string Name);

            record Item(string Name, decimal Price);

            """");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Assert.True(window.SelectConfiguration("Web: http"), $"No configuration Web: http among {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Ui.Launched(window, window.RunAsync(), "the shop to start", TimeSpan.FromMinutes(2));
            held.LetGo(HeldPort.GateIn(folder));
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("Now listening on:", StringComparison.Ordinal), "the shop to listen",
                TimeSpan.FromMinutes(2));

            window.ShowToolWindow(ToolWindowIds.Endpoints);
            string http = Path.Combine(folder, "Web", "Web.http");
            window.Perform(AppActions.GenerateOpenApiRequests);
            Ui.WaitFor(() => File.Exists(http) && window.Status?.StartsWith("Wrote 2 requests", StringComparison.Ordinal) == true,
                () => $"a request of each operation the 3.2 document names, its QUERY and its PURGE: {window.Status}", TimeSpan.FromMinutes(1));
            string written = File.ReadAllText(http);
            Assert.Contains("# OpenAPI: FindItems\nQUERY {{Web_HostAddress}}/items\nAccept: application/json\nContent-Type: application/json\n\n{\n  \"name\": \"string\"\n}\n",
                written, StringComparison.Ordinal);
            Assert.Contains("# OpenAPI: PurgeItems\nPURGE {{Web_HostAddress}}/items\n", written, StringComparison.Ordinal);
            Ui.WaitFor(() => window.Endpoints.Requests.Count(r => r.File == http) == 2, "the QUERY and the PURGE listed in the Endpoints window");

            EndpointRequest query = window.Endpoints.Requests.Single(r => r.File == http && r.Method == "QUERY");
            Ui.Await(window.Endpoints.TestAsync(query), "the QUERY's answer", TimeSpan.FromMinutes(1));
            ResponseView answer = window.Endpoints.Response;
            Assert.True(answer is { Passed: true, Response: { Status: 200, Body: { } found } } && found.Contains("string found", StringComparison.Ordinal),
                $"The QUERY made from the 3.2 document was not answered with what its body asked: {answer.Summary} {answer.Response?.Body} {string.Join("; ", answer.Breaks)}");
            EndpointRequest purge = window.Endpoints.Requests.Single(r => r.File == http && r.Method == "PURGE");
            Ui.Await(window.Endpoints.TestAsync(purge), "the PURGE's answer", TimeSpan.FromMinutes(1));
            Assert.True(answer is { Passed: true, Response.Status: 204 }, $"The PURGE made from the 3.2 document's additionalOperations was not answered as purged: {answer.Summary}");
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "Stop to end the shop");
            window.Close();
        }
    }

    /// <summary>One minimal API on <c>MapOpenApi</c>, its launch profile on the port given, which it listens at once past its
    /// gate (<see cref="HeldPort"/>): an item by id, one added, and one route whose answer breaks the schema its document gives
    /// it (a number for the name, a word for the price); or the <paramref name="program"/> given in its place.</summary>
    private static string Shop(int port, string? program = null)
    {
        string folder = TestFolders.Create("openapi-");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Web/Web.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "Web/Web.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>").With($"""
              <ItemGroup>
                <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="{AvaloniaApp.Metadata("AspNetCoreOpenApiVersion")}" />
              </ItemGroup>

            """));
        TestFolders.Write(folder, "Web/Properties/launchSettings.json", $$"""
            {
              "profiles": {
                "http": { "commandName": "Project", "applicationUrl": "http://127.0.0.1:{{port.ToString(CultureInfo.InvariantCulture)}}",
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", {{HeldPort.GateMember(HeldPort.GateIn(folder))}} } }
              }
            }

            """);
        TestFolders.Write(folder, "Web/Program.cs", HeldPort.Gated(program ?? """
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.Services.AddOpenApi();
            WebApplication app = builder.Build();
            app.MapOpenApi();
            app.MapGet("/items/{id}", (int id) => new Item($"item {id}", 2.5m)).WithName("GetItem");
            app.MapPost("/items", (Item item) => Results.Created("/items/1", item)).WithName("AddItem");
            app.MapGet("/broken", () => Results.Json(new { name = 42, price = "cheap" })).Produces<Item>(200).WithName("Broken");
            app.Run();

            record Item(string Name, decimal Price);

            """));
        AvaloniaApp.UseFeed(folder);
        return PathNames.Canonical(folder);
    }
}
