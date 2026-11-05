using System.Text;
using System.Text.Json.Nodes;
using Passenger.App.Debugging;

namespace Passenger.UnitTests.Web;

/// <summary>A web app's development manifest read as the app serves its files by it: each file by the URL it is served at,
/// a Razor class library's under its own and its folder's files come since too, the library's scoped CSS by its bundle's URL
/// without the fingerprint, the file each project's scoped CSS is served from, and where the browser tools its pages load were
/// written, a Blazor Web App's by its WebAssembly client's build.</summary>
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

    /// <summary>Bell's own wwwroot, Kit's (a Razor class library it references) under _content/Kit, and Kit's bundled scoped
    /// CSS, fingerprinted.</summary>
    private static DevelopmentAssets BellWithKit()
    {
        JsonObject kit = Children(
            ("kit.css", Asset(1, "kit.css")),
            ("Kit.h77jbnbuq3.bundle.scp.css", Asset(2, "Kit.bundle.scp.css")));
        kit["Patterns"] = new JsonArray(new JsonObject { ["ContentRootIndex"] = 1, ["Pattern"] = "**", ["Depth"] = 2 });
        byte[] manifest = Manifest([Folder("Bell", "wwwroot"), Folder("Kit", "wwwroot"), Folder("Kit", "obj", "Debug", "net10.0", "scopedcss", "projectbundle")], Children(
            ("app.css", Asset(0, "app.css")),
            ("css", Children(("site.css", Asset(0, "css/site.css")))),
            ("_content", Children(("Kit", kit)))));
        return DevelopmentAssets.Read(manifest) ?? throw new InvalidOperationException("The manifest was not read");
    }

    [Fact]
    public void Each_file_the_app_serves_is_told_by_its_URL_a_referenced_librarys_under_its_own()
    {
        DevelopmentAssets assets = BellWithKit();
        Assert.Equal("app.css", assets.UrlOf(Path.Combine(Solution, "Bell", "wwwroot", "app.css")));
        Assert.Equal("css/site.css", assets.UrlOf(Path.Combine(Solution, "Bell", "wwwroot", "css", "site.css")));
        Assert.True(assets.UrlOf(Path.Combine(Solution, "Kit", "wwwroot", "kit.css")) == "_content/Kit/kit.css",
            "The library's stylesheet was not told by the URL the app serves it at, so the page never applies it");
        Assert.True(assets.UrlOf(Path.Combine(Solution, "Kit", "wwwroot", "images", "logo.png")) == "_content/Kit/images/logo.png",
            "A file come since in the library's wwwroot, which the app serves as it comes, was not told to the page");
        Assert.Null(assets.UrlOf(Path.Combine(Solution, "Kit", "Banner.razor")));
        Assert.Null(assets.UrlOf(Path.Combine(Solution, "Bell", "wwwroot", "added.css")));
    }

    [Fact]
    public void A_referenced_librarys_scoped_CSS_is_told_by_its_bundles_URL_without_the_fingerprint()
    {
        DevelopmentAssets assets = BellWithKit();
        Assert.True(assets.BundleUrlOf(Path.Combine(Solution, "Kit", "Kit.csproj")) == "_content/Kit/Kit.bundle.scp.css",
            $"Kit's scoped CSS was told as {assets.BundleUrlOf(Path.Combine(Solution, "Kit", "Kit.csproj"))}, not the bundle the app serves at its own name");
        Assert.Null(assets.BundleUrlOf(Path.Combine(Solution, "Plain", "Plain.csproj")));
    }

    [Fact]
    public void A_projects_scoped_CSS_is_served_from_its_bundle_the_apps_own_or_a_referenced_librarys()
    {
        string bundles = Folder("Bell", "obj", "Debug", "net10.0", "scopedcss", "bundle");
        string library = Folder("Kit", "obj", "Debug", "net10.0", "scopedcss", "projectbundle");
        DevelopmentAssets? read = DevelopmentAssets.Read(Manifest([bundles, library], Children(
            ("Bell.styles.css", Asset(0, "Bell.styles.css")),
            ("_content", Children(("Kit", Children(("Kit.h77jbnbuq3.bundle.scp.css", Asset(1, "Kit.bundle.scp.css")))))))));
        Assert.NotNull(read);
        Assert.Equal(Path.Combine(bundles, "Bell.styles.css"), read.BundleFileOf(Path.Combine(Solution, "Bell", "Bell.csproj"), own: true));
        Assert.Equal(Path.Combine(library, "Kit.bundle.scp.css"), read.BundleFileOf(Path.Combine(Solution, "Kit", "Kit.csproj"), own: false));
        Assert.Null(read.BundleFileOf(Path.Combine(Solution, "Plain", "Plain.csproj"), own: false));
    }

    [Fact]
    public void A_file_served_at_two_URLs_is_told_by_the_shorter()
    {
        DevelopmentAssets? read = DevelopmentAssets.Read(Manifest([Folder("Bell", "wwwroot")], Children(
            ("favicon.x2rx4ajns0.png", Asset(0, "favicon.png")),
            ("favicon.png", Asset(0, "favicon.png")))));
        Assert.Equal("favicon.png", read?.UrlOf(Path.Combine(Solution, "Bell", "wwwroot", "favicon.png")));
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
