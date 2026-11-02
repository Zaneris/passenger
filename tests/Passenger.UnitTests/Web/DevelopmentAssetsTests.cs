using System.Text;
using System.Text.Json.Nodes;
using Passenger.App.Debugging;

namespace Passenger.UnitTests.Web;

/// <summary>A web app's development manifest read as the app serves its files by it: where the browser tools its pages load
/// were written, a Blazor Web App's by its WebAssembly client's build.</summary>
public class DevelopmentAssetsTests
{
    private static readonly string Solution = Path.Combine(Path.GetTempPath(), "solution");

    private static string Folder(params string[] parts) => Path.Combine([Solution, .. parts]) + Path.DirectorySeparatorChar;

    /// <summary>A manifest of content roots and a tree of URLs, each leaf an asset in a root.</summary>
    private static byte[] Manifest(IReadOnlyList<string> roots, JsonObject tree) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["ContentRoots"] = new JsonArray([.. roots.Select(r => (JsonNode)r)]), ["Root"] = tree }.ToJsonString());

    private static JsonObject Asset(int root, string below) => new() { ["Asset"] = new JsonObject { ["ContentRootIndex"] = root, ["SubPath"] = below } };

    private static JsonObject Children(params (string Name, JsonNode Node)[] children)
    {
        JsonObject named = [];
        foreach ((string name, JsonNode node) in children)
        {
            named[name] = node;
        }
        return new JsonObject { ["Children"] = named };
    }

    [Fact]
    public void A_Blazor_Web_Apps_browser_tools_are_where_its_WebAssembly_clients_build_wrote_them()
    {
        string tools = Folder("Bwa.Client", "obj", "Debug", "net10.0", "dotnet-watch");
        byte[] manifest = Manifest([Folder("Bwa", "wwwroot"), tools], Children(
            ("app.css", Asset(0, "app.css")),
            ("_framework", Children(
                ("Microsoft.NET.Sdk.WebAssembly.DotNetWatch.BrowserTools.js", Asset(1, "Microsoft.NET.Sdk.WebAssembly.DotNetWatch.BrowserTools.js")),
                ("Microsoft.NET.Sdk.WebAssembly.DotNetWatch.BrowserTools.Config.js", Asset(1, "Microsoft.NET.Sdk.WebAssembly.DotNetWatch.BrowserTools.Config.js"))))));
        DevelopmentAssets? read = DevelopmentAssets.Read(manifest);
        Assert.NotNull(read);
        Assert.True(read.BrowserTools == Path.TrimEndingDirectorySeparator(tools),
            $"The browser tools were taken to be in {read.BrowserTools}, not the client's {tools}, so Passenger would prove itself to the page with no key or another");
    }

    [Fact]
    public void A_build_that_served_no_browser_tools_has_none_and_text_that_is_no_manifest_is_none()
    {
        DevelopmentAssets? read = DevelopmentAssets.Read(Manifest([Folder("Bell", "wwwroot")], Children(
            ("app.css", Asset(0, "app.css")),
            ("_framework", Children(("blazor.web.js", Asset(0, "blazor.web.js")))),
            ("Bell.BrowserTools.Config.js", Asset(0, "Bell.BrowserTools.Config.js")))));
        Assert.NotNull(read);
        Assert.Null(read.BrowserTools);
        Assert.Null(DevelopmentAssets.Read("not a manifest"u8));
        Assert.Null(DevelopmentAssets.Read("{ \"ContentRoots\": [] }"u8));
    }
}
