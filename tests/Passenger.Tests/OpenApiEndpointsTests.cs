using System.Globalization;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Endpoints;
using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A minimal API that describes itself with MapOpenApi, its document read from the running app on this machine:
/// Generate Requests from OpenAPI writes a request per operation into its project's .http file, bodies from the request
/// schemas, one Undo taking them back; a request's answer is held to its response schema, a member that breaks it said at
/// that member; and Interactive Test completes a request's empty body from its schema. Its packages come from the package
/// cache alone, Microsoft.AspNetCore.OpenApi from the AspireGraphs restore.</summary>
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
