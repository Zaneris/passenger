using System.Globalization;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Endpoints;
using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A minimal API that describes itself with MapOpenApi, its document read from the running app on this machine:
/// Generate Requests from OpenAPI writes a request per operation into its project's .http file, bodies from the request
/// schemas, one Undo taking them back; a request's answer is held to its response schema, a member that breaks it said at
/// that member; and Interactive Test completes a request's empty body from its schema; and a document the build writes
/// makes the requests of an app not running; an app whose code names no document is held to the one the person names. Its
/// packages come from the package cache alone, Microsoft.AspNetCore.OpenApi's
/// and Microsoft.Extensions.ApiDescription.Server's from the AspireGraphs restore.</summary>
public sealed class OpenApiEndpointsTests
{
    [AvaloniaFact]
    public void A_minimal_APIs_document_makes_its_requests_and_a_response_that_breaks_its_schema_is_said_at_the_member()
    {
        (string folder, int port) = Shop();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Assert.True(window.SelectConfiguration("Web: http"), $"No configuration Web: http among {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Ui.Launched(window, window.RunAsync(), "the shop to start", TimeSpan.FromMinutes(2));
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
    [AvaloniaFact]
    public void A_document_the_build_writes_makes_the_requests_of_an_app_that_is_not_running()
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

    /// <summary>An app whose code names no OpenAPI document of its own (it serves one as a static file): its answers are held
    /// to none, until the person names the document at its path through Name OpenAPI Document…, kept with the folder, and
    /// from then on an answer that breaks its schema is said at the member.</summary>
    [AvaloniaFact]
    public void An_apps_answers_are_held_to_the_document_the_person_names_where_its_code_names_none()
    {
        string folder = TestFolders.Create("openapi-named-");
        int port = AspireApp.FreePort();
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Web/Web.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "Web/Web.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(folder, "Web/Properties/launchSettings.json", $$"""
            { "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://127.0.0.1:{{port.ToString(CultureInfo.InvariantCulture)}}" } } }

            """);
        TestFolders.Write(folder, "Web/Program.cs", """
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            WebApplication app = builder.Build();
            app.UseStaticFiles();
            app.MapGet("/broken", () => Results.Json(new { name = 42 }));
            app.Run();

            """);
        TestFolders.Write(folder, "Web/wwwroot/spec.json", """
            { "openapi": "3.0.1", "info": { "title": "Shop", "version": "1" }, "paths": { "/broken": { "get": { "responses": { "200": { "description": "OK",
              "content": { "application/json": { "schema": { "type": "object", "required": [ "name" ], "properties": { "name": { "type": "string" } } } } } } } } } } }

            """);
        TestFolders.Write(folder, "Web/Web.http", $"@Web_HostAddress = http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}\n\nGET {{{{Web_HostAddress}}}}/broken\nAccept: application/json\n");
        folder = PathNames.Canonical(folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Assert.True(window.SelectConfiguration("Web: http"), $"No configuration Web: http among {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Ui.Launched(window, window.RunAsync(), "the shop to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("Now listening on:", StringComparison.Ordinal), "the shop to listen",
                TimeSpan.FromMinutes(2));
            window.ShowToolWindow(ToolWindowIds.Endpoints);
            Ui.WaitFor(() => window.Endpoints.Requests.Any(r => r.Url.EndsWith("/broken", StringComparison.Ordinal)), "the shop's request listed");
            EndpointRequest broken = window.Endpoints.Requests.Single(r => r.Url.EndsWith("/broken", StringComparison.Ordinal));
            Ui.Await(window.Endpoints.TestAsync(broken), "the answer before a document is named", TimeSpan.FromMinutes(1));
            Assert.True(window.Endpoints.Response.Passed, $"An answer was held to a document its app's code names nowhere: {string.Join("; ", window.Endpoints.Response.Breaks)}");

            window.Perform(AppActions.NameOpenApiDocument);
            Ui.WaitFor(() => window.NamingDocument is not null, "the dialog that asks where the document is");
            OpenApiDocumentDialog dialog = window.NamingDocument!;
            dialog.Address.Text = "https://example.com/spec.json";
            Ui.Settle();
            Assert.False(dialog.Ok.IsEnabled, "A document on another machine could be named, which Passenger would ask");
            dialog.Address.Text = "/spec.json";
            Ui.Settle();
            Ui.Click(dialog.Ok);
            Ui.WaitFor(() => window.NamingDocument is null, "the dialog to close");
            Assert.Equal("/spec.json", window.Session.OpenApiNames.Of(Path.Combine(folder, "Web", "Web.csproj")));

            Ui.Await(window.Endpoints.TestAsync(broken), "the answer held to the named document", TimeSpan.FromMinutes(1));
            Assert.False(window.Endpoints.Response.Passed, "An answer that breaks the named document's schema passed");
            Assert.Contains(new SchemaBreak("/name", "a string was expected, not the number 42"), window.Endpoints.Response.Breaks);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "Stop to end the shop");
            window.Close();
        }
    }

    /// <summary>One minimal API on <c>MapOpenApi</c>, its launch profile on a port of its own: an item by id, one added, and
    /// one route whose answer breaks the schema its document gives it (a number for the name, a word for the price).</summary>
    private static (string Folder, int Port) Shop()
    {
        string folder = TestFolders.Create("openapi-");
        int port = AspireApp.FreePort();
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
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" } }
              }
            }

            """);
        TestFolders.Write(folder, "Web/Program.cs", """
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.Services.AddOpenApi();
            WebApplication app = builder.Build();
            app.MapOpenApi();
            app.MapGet("/items/{id}", (int id) => new Item($"item {id}", 2.5m)).WithName("GetItem");
            app.MapPost("/items", (Item item) => Results.Created("/items/1", item)).WithName("AddItem");
            app.MapGet("/broken", () => Results.Json(new { name = 42, price = "cheap" })).Produces<Item>(200).WithName("Broken");
            app.Run();

            record Item(string Name, decimal Price);

            """);
        AvaloniaApp.UseFeed(folder);
        return (PathNames.Canonical(folder), port);
    }
}
