using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The classes a Blazor component's pages link, completed where a class is written, through the window: a
/// stylesheet on a CDN (a local stand-in, never a real one) only once Settings allow its download and never fetched
/// before, its link's integrity checked; a package's static web asset under <c>_content/Kit/</c>, or under its own base
/// path, read from the package folder its restore resolved, and a referenced library's under the base path it sets; the component's own scoped stylesheet, a parent's ::deep
/// classes in its child; the host page's style block and a link under its base href; an HTML file's; a class's rules on
/// the hover card and Go to Declaration to its rule, read-only; a class no stylesheet defines marked, quietly, once every
/// stylesheet linked was read; and what a page's stylesheets could not give said, never called complete. On a Blazor app
/// made by <c>dotnet new blazor</c>, its warm server shared.</summary>
public sealed class StylesheetClassesTests : IClassFixture<StyledShop>
{
    private readonly StyledShop app;

    public StylesheetClassesTests(StyledShop app)
    {
        this.app = app;
        app.Reset();
    }

    [AvaloniaFact]
    public void A_cdns_class_completes_in_a_component_only_once_settings_allow_its_download_beside_a_packages_and_the_components_own()
    {
        NoCopies();
        int fetched = app.Fetched;
        MainWindow window = app.Open("Components/Pages/Styled.razor");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<p class=\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("kit-card"), () => $"the package's class among {Listed(window)}");
        Assert.Contains(("kit-card", "kit.css"), window.Assist.CompletionDetails);
        Assert.Contains(("halo", "Styled.razor.css"), window.Assist.CompletionDetails);
        Assert.Contains(("blazor-error-boundary", "app.css"), window.Assist.CompletionDetails);
        Assert.DoesNotContain("btn-outline-secondary", window.Assist.CompletionLabels);
        Assert.Equal(fetched, app.Fetched);
        Ui.Press(window, "ESCAPE");

        window.ApplySettings(window.Settings with { DownloadStylesheets = true });
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("btn-outline-secondary"), () => $"the CDN's class once its download is allowed, among {Listed(window)}");
        Assert.Contains(("btn-outline-secondary", "bootstrap.min.css"), window.Assist.CompletionDetails);
        Assert.Equal(fetched + 1, app.Fetched);
        window.KeyTextInput("btn-out");
        Ui.Settle();
        Assert.Equal("btn-outline-secondary", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.Contains("<p class=\"btn-outline-secondary\">", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_classs_rules_show_on_its_hover_card_and_go_to_declaration_opens_its_rule_in_the_downloaded_copy_read_only()
    {
        MainWindow window = app.Open("Components/Pages/Styled.razor", before: w => w.ApplySettings(w.Settings with { DownloadStylesheets = true }));
        CodeEditor editor = window.EditorArea.CodeEditor;
        int written = editor.Document.Snapshot.ToString().IndexOf("btn-primary", StringComparison.Ordinal) + 4;
        Point point = editor.TranslatePoint(editor.PointOf(written) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText?.Contains(".btn-primary {", StringComparison.Ordinal) == true, () => $"the class's rule on the card; it says {window.Assist.HoverText}");
        Assert.Contains("background-color: #0d6efd;", window.Assist.HoverText, StringComparison.Ordinal);
        Assert.Contains("bootstrap.min.css", window.Assist.HoverText, StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "class=\"btn btn-pr");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.Assist.Offered.Count == 2, () => $"the class's two rules to choose from, the CDN's and app.css's; offered [{string.Join("; ", window.Assist.Offered)}], said {window.Status}");
        Assert.Equal("Rules", window.Assist.OfferedTitle);
        Assert.Equal([".btn-primary in app.css", ".btn-primary in bootstrap.min.css"], window.Assist.Offered.Order(StringComparer.Ordinal));
        int cdn = window.Assist.Offered.ToList().IndexOf(".btn-primary in bootstrap.min.css");
        for (int i = 0; i < cdn; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "bootstrap.min.css", () => $"the downloaded stylesheet opened at the rule; the window shows {window.EditorArea.Active?.Name}, and said {window.Status}");
        OpenDocument copy = window.EditorArea.Active!;
        Assert.True(RemoteStylesheets.IsCopy(copy.Path), $"{copy.Path} is not the downloaded copy");
        Assert.True(copy.IsReadOnly, "the downloaded copy can be typed into, though nothing typed reaches the CDN");
        CodeEditor shown = window.EditorArea.CodeEditor;
        Assert.StartsWith("btn-primary{", shown.Document.Snapshot.ToString()[shown.Selections.Primary.Caret..], StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Go_to_declaration_of_a_packages_class_opens_its_stylesheet_in_the_package_folder_read_only_naming_the_package()
    {
        MainWindow window = app.Open("Components/Pages/Styled.razor");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "btn-primry kit-c");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "kit.css", () => $"the package's stylesheet opened at its rule; the window shows {window.EditorArea.Active?.Name}, and said {window.Status}");
        OpenDocument sheet = window.EditorArea.Active!;
        string written = File.ReadAllText(sheet.Path);
        string root = window.Projects.Single(p => p.Name == "Shop").Property(Passenger.Host.CarriedProperties.NuGetPackageRoot)!;
        Assert.True(PathNames.IsUnder(sheet.Path, PathNames.Canonical(root)), $"{sheet.Path} is not the package's copy in {root}");
        Assert.True(sheet.IsReadOnly && sheet.Text.IsReadOnly && window.EditorArea.CodeEditor.IsReadOnly,
            $"{sheet.Path} opened writable, so a key typed there would change every project's copy of Kit 1.0.0");

        window.KeyTextInput("x");
        Ui.Settle();
        Assert.False(sheet.IsModified, "A key typed into the package's stylesheet changed its text");
        Assert.Equal(written, sheet.Text.Snapshot.ToString());
        Assert.True(window.EditorArea.ActiveGroup.BannerText?.Contains("A file of the package Kit 1.0.0", StringComparison.Ordinal) == true,
            $"The bar over the package's stylesheet says '{window.EditorArea.ActiveGroup.BannerText}', not which package it is part of");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Equal("kit.css is read-only", window.Status);
        Assert.Equal(written, File.ReadAllText(sheet.Path));
        window.Close();
    }

    [AvaloniaFact]
    public void A_class_no_linked_stylesheet_defines_is_marked_quietly_once_every_stylesheet_linked_was_read()
    {
        NoCopies();
        MainWindow window = app.Open("Components/Pages/Styled.razor");
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => window.CSharpService?.For(document)?.Lsp is not null, "the component's document at the server");
        Ui.Settle();
        Assert.DoesNotContain(document.Markers, m => m.Message.Contains("btn-primry", StringComparison.Ordinal));

        window.ApplySettings(window.Settings with { DownloadStylesheets = true });
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("btn-primry", StringComparison.Ordinal)), () => $"the misspelt class marked; the markers say {string.Join("; ", document.Markers.Select(m => m.Message))}");
        Marker marked = Assert.Single(document.Markers, m => m.Message.Contains("defines the class", StringComparison.Ordinal));
        Assert.Equal(MarkerKind.Info, marked.Kind);
        Assert.Equal("btn-primry", document.Text.Snapshot.GetText(marked.Range.Start, marked.Range.Length));
        window.Close();
    }

    [AvaloniaFact]
    public void A_parents_deep_class_is_offered_in_the_child_it_renders_and_its_own_scoped_class_is_not()
    {
        MainWindow window = app.Open("Components/Pages/Child.razor");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<p class=\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("from-parent"), () => $"the parent's ::deep class among {Listed(window)}");
        Assert.Contains(("from-parent", "Parent.razor.css"), window.Assist.CompletionDetails);
        Assert.DoesNotContain("parent-own", window.Assist.CompletionLabels);
        Assert.DoesNotContain("halo", window.Assist.CompletionLabels);
        window.Close();
    }

    [AvaloniaFact]
    public void A_style_blocks_class_a_link_under_the_pages_base_href_and_a_packages_own_base_path_are_offered_as_the_app_serves_them()
    {
        MainWindow window = app.Open("Components/Pages/Styled.razor");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<p class=\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("lamp-glow") && window.Assist.CompletionLabels.Contains("based-note")
            && window.Assist.CompletionLabels.Contains("inline-note"), () => $"the style block's, the based link's and the package's classes among {Listed(window)}");
        Assert.Contains(("inline-note", "<style> in App.razor"), window.Assist.CompletionDetails);
        Assert.Contains(("based-note", "based.css"), window.Assist.CompletionDetails);
        Assert.Contains(("lamp-glow", "lamp.css"), window.Assist.CompletionDetails);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("gear-spin"), () => $"the referenced library's class under its own base path among {Listed(window)}");
        Assert.Contains(("gear-spin", "gear.css"), window.Assist.CompletionDetails);
        window.Close();
    }

    [AvaloniaFact]
    public void An_html_files_class_completes_from_the_stylesheet_it_links_and_a_misspelt_one_is_marked()
    {
        Assert.SkipWhen(Programs.Node() is null, "Node.js is not on the PATH, and an HTML file's language server runs on it");
        MainWindow window = app.Open("wwwroot/plain.html");
        OpenDocument document = window.EditorArea.Active!;
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<p class=\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("based-note"), () => $"the linked stylesheet's class among {Listed(window)}");
        Assert.Contains(("based-note", "based.css"), window.Assist.CompletionDetails);
        Ui.Press(window, "ESCAPE");
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("based-nte", StringComparison.Ordinal)),
            () => $"the misspelt class marked; the markers say {string.Join("; ", document.Markers.Select(m => m.Message))}", TimeSpan.FromMinutes(1));
        Marker marked = Assert.Single(document.Markers, m => m.Message.Contains("defines the class", StringComparison.Ordinal));
        Assert.Equal("based-nte", document.Text.Snapshot.GetText(marked.Range.Start, marked.Range.Length));
        window.Close();
    }

    [AvaloniaFact]
    public void A_stylesheet_is_downloaded_again_once_its_link_gives_another_integrity_and_what_is_not_that_integrity_is_kept_nowhere()
    {
        NoCopies();
        app.Latest = StyledShop.LatestBefore;
        MainWindow window = OpenAllowingDownloads();
        Assert.Contains("from-before", Classes(window, "latest-before.html").Classes.Keys);

        app.Latest = StyledShop.LatestAfter;
        PageClasses after = Classes(window, "latest-after.html");
        Assert.True(after.Complete, $"The page's new integrity found the copy of the old bytes: {string.Join("; ", after.Unread)}");
        Assert.Contains("from-after", after.Classes.Keys);
        Assert.DoesNotContain("from-before", after.Classes.Keys);

        int asked = app.LatestFetched;
        PageClasses ahead = Classes(window, "latest-ahead.html");
        Assert.False(ahead.Complete, "A page's integrity no bytes the CDN serves meet gave a stylesheet all the same");
        Assert.Contains("integrity", Assert.Single(ahead.Unread), StringComparison.Ordinal);
        Classes(window, "latest-ahead.html");
        Assert.Equal(asked + 1, app.LatestFetched);
        window.Close();

        MainWindow again = OpenAllowingDownloads();
        Assert.Contains("from-after", Classes(again, "latest-after.html").Classes.Keys);
        Assert.Equal(asked + 1, app.LatestFetched);
        again.Close();
    }

    [AvaloniaFact]
    public void A_stylesheet_that_cannot_be_read_is_said_and_read_again_once_it_can_be()
    {
        MainWindow window = app.Open(null, languageServer: false);
        using (new FileStream(app.PathOf("Gear/wwwroot/css/locked.css"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            PageClasses held = Classes(window, "locked.html");
            Assert.False(held.Complete, "A stylesheet no one could read counted as read, so its classes would be marked as defined by nothing");
            Assert.Contains("locked.css", Assert.Single(held.Unread), StringComparison.Ordinal);
            Assert.DoesNotContain("locked-note", held.Classes.Keys);
        }
        PageClasses free = Classes(window, "locked.html");
        Assert.True(free.Complete, string.Join("; ", free.Unread));
        Assert.Contains("locked-note", free.Classes.Keys);
        window.Close();
    }

    [AvaloniaFact]
    public void An_import_past_the_depth_followed_is_said_never_passed_over()
    {
        MainWindow window = app.Open(null, languageServer: false);
        PageClasses found = Classes(window, "deep.html");
        Assert.False(found.Complete, "An import cut off counted as every stylesheet read, so a class of the last would be marked as defined by nothing");
        Assert.Contains("depth9.css", Assert.Single(found.Unread), StringComparison.Ordinal);
        Assert.Contains("at-depth-8", found.Classes.Keys);
        Assert.DoesNotContain("at-depth-9", found.Classes.Keys);
        window.Close();
    }

    [AvaloniaFact]
    public void A_link_an_expression_makes_is_a_stylesheet_unread()
    {
        MainWindow window = app.Open("Components/Pages/Child.razor", languageServer: false);
        OpenDocument document = window.EditorArea.Active!;
        Typing.Replace(window, "<p class=\"\">Child</p>", "<link rel=\"stylesheet\" href=\"@Theme.Sheet\" />");
        PageClasses found = Ui.Landed(window.Session.Stylesheets.ClassesAsync(document.Path, CancellationToken.None), "the classes of a component whose link is an expression");
        Assert.False(found.Complete, "A stylesheet only the running app can name counted as read");
        Assert.Contains(found.Unread, why => why.Contains("@Theme.Sheet", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void A_components_own_scoped_stylesheet_alone_never_makes_its_classes_complete()
    {
        string folder = TestFolders.Create("lone-component-");
        File.WriteAllText(Path.Combine(folder, "Card.razor"), "<div class=\"kept hosted\"></div>\n");
        File.WriteAllText(Path.Combine(folder, "Card.razor.css"), ".kept {\n    color: red;\n}\n");
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        PageClasses found = Ui.Landed(window.Session.Stylesheets.ClassesAsync(PathNames.Canonical(Path.Combine(folder, "Card.razor")), CancellationToken.None), "a lone component's classes");
        Assert.Contains("kept", found.Classes.Keys);
        Assert.False(found.Complete, "A component no known page shows was told its page's stylesheets define none of its other classes");
        window.Close();
    }

    private MainWindow OpenAllowingDownloads() => app.Open(null, before: w => w.ApplySettings(w.Settings with { DownloadStylesheets = true }), languageServer: false);

    /// <summary>The classes a page of the library's wwwroot links, once its evaluation says it serves that folder.</summary>
    private PageClasses Classes(MainWindow window, string page)
    {
        Ui.WaitFor(() => window.Projects.Any(p => p.Name == "Gear" && p.IsWeb), "the library's evaluation, which says it serves its wwwroot");
        string path = PathNames.Canonical(app.PathOf(Path.Combine("Gear", "wwwroot", page)));
        return Ui.Landed(window.Session.Stylesheets.ClassesAsync(path, CancellationToken.None), $"the classes {page} links");
    }

    /// <summary>No copy of a stylesheet on the web is kept: one another test downloaded would be used without asking the CDN.</summary>
    private static void NoCopies()
    {
        if (Directory.Exists(RemoteStylesheets.Folder))
        {
            Directory.Delete(RemoteStylesheets.Folder, recursive: true);
        }
    }

    private static string Listed(MainWindow window) => string.Join(", ", window.Assist.CompletionLabels.Take(40));
}

/// <summary>A Blazor app whose App.razor links a stylesheet on a CDN by its integrity (a stand-in on a loopback port that
/// counts what it is asked), the app's own app.css and a package's (Kit, from a folder feed the app's nuget.config alone
/// names, holding <c>staticwebassets/kit.css</c>), and a component with a scoped stylesheet writing a class misspelt.</summary>
public sealed class StyledShop : SharedProject
{
    /// <summary>What the stand-in CDN serves for Bootstrap: a few of its rules, as minified as Bootstrap's.</summary>
    public const string Bootstrap = ".btn{display:inline-block;padding:.375rem .75rem}.btn-primary{color:#fff;background-color:#0d6efd}"
        + ".btn-outline-secondary{color:#6c757d;border-color:#6c757d}@media (min-width:768px){.col-md-6{width:50%}}";

    /// <summary>What the stand-in CDN's latest.css holds at first, and once it moved on.</summary>
    public const string LatestBefore = ".from-before{color:red}";

    public const string LatestAfter = ".from-after{color:green}";

    private const string Component = "@page \"/styled\"\n\n<h1 class=\"halo\">Styled</h1>\n\n<p class=\"\"></p>\n\n"
        + "<button class=\"btn btn-primary\">Go</button>\n<div class=\"btn-primry kit-card gear-spin\"></div>\n";

    private readonly StandInHttp cdn;
    private int fetched;
    private int latestFetched;
    private volatile string latest = LatestBefore;

    public StyledShop()
        : base("styled-")
    {
        cdn = new StandInHttp(async exchange =>
        {
            if (exchange.Path.EndsWith("/bootstrap.min.css", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref fetched);
                await exchange.ReplyAsync(200, "text/css", Bootstrap);
                return;
            }
            if (exchange.Path.EndsWith("/latest.css", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref latestFetched);
                await exchange.ReplyAsync(200, "text/css", latest);
                return;
            }
            await exchange.ReplyAsync(404, "text/plain", "not here");
        });
    }

    /// <summary>How many times the stand-in CDN was asked for its stylesheet.</summary>
    public int Fetched => Volatile.Read(ref fetched);

    /// <summary>How many times the stand-in CDN was asked for latest.css.</summary>
    public int LatestFetched => Volatile.Read(ref latestFetched);

    /// <summary>What the stand-in CDN serves as latest.css now.</summary>
    public string Latest
    {
        set => latest = value;
    }

    public override async ValueTask InitializeAsync()
    {
        string integrity = "sha384-" + Convert.ToBase64String(SHA384.HashData(Encoding.UTF8.GetBytes(Bootstrap)));
        await TestFolders.PrepareAsync(async () =>
        {
            await BlazorApp.CreateAsync(Folder);
            string page = await File.ReadAllTextAsync(PathOf("Components/App.razor"));
            page = page.Replace("<link rel=\"stylesheet\" href=\"@Assets[\"lib/bootstrap/dist/css/bootstrap.min.css\"]\" />",
                $"<link rel=\"stylesheet\" href=\"{cdn.Root}/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css\" integrity=\"{integrity}\" crossorigin=\"anonymous\" />\n"
                + "    <link rel=\"stylesheet\" href=\"_content/Kit/kit.css\" />\n"
                + "    <link rel=\"stylesheet\" href=\"/app/css/based.css\" />\n"
                + "    <link rel=\"stylesheet\" href=\"lamp/lamp.css\" />\n"
                + "    <link rel=\"stylesheet\" href=\"gear/gear.css\" />\n"
                + "    <style>\n        .inline-note { color: teal; }\n    </style>", StringComparison.Ordinal);
            Assert.Contains("_content/Kit/kit.css", page, StringComparison.Ordinal);
            // Served under a path base: its relative links after it, its links under it from the app's root.
            page = page.Replace("<base href=\"/\" />", "<base href=\"/app/\" />", StringComparison.Ordinal);
            Assert.Contains("<base href=\"/app/\" />", page, StringComparison.Ordinal);
            File.WriteAllText(PathOf("Components/App.razor"), page);
            Directory.Delete(PathOf("wwwroot/lib"), recursive: true);
            File.WriteAllText(PathOf("Components/Pages/Styled.razor"), Component);
            File.WriteAllText(PathOf("Components/Pages/Styled.razor.css"), ".halo {\n    color: gold;\n}\n");
            File.WriteAllText(PathOf("Components/Pages/Parent.razor"), "@page \"/parent\"\n\n<div class=\"parent-own\">\n    <Child />\n</div>\n");
            File.WriteAllText(PathOf("Components/Pages/Parent.razor.css"), ".parent-own {\n    margin: 0;\n}\n\n.parent-own ::deep .from-parent {\n    color: plum;\n}\n");
            File.WriteAllText(PathOf("Components/Pages/Child.razor"), "<p class=\"\">Child</p>\n");
            Directory.CreateDirectory(PathOf("wwwroot/css"));
            File.WriteAllText(PathOf("wwwroot/css/based.css"), ".based-note {\n    color: navy;\n}\n");
            File.WriteAllText(PathOf("wwwroot/plain.html"), "<!DOCTYPE html>\n<html>\n<head>\n    <link rel=\"stylesheet\" href=\"css/based.css\" />\n</head>\n"
                + "<body>\n    <p class=\"\"></p>\n    <div class=\"based-nte\"></div>\n</body>\n</html>\n");
            Directory.CreateDirectory(PathOf("feed"));
            using (ZipArchive package = ZipFile.Open(PathOf("feed/kit.1.0.0.nupkg"), ZipArchiveMode.Create))
            {
                Entry(package, "Kit.nuspec", "<?xml version=\"1.0\"?>\n<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\n"
                    + "  <metadata>\n    <id>Kit</id>\n    <version>1.0.0</version>\n    <authors>Passenger</authors>\n    <description>A stylesheet</description>\n"
                    + "  </metadata>\n</package>\n");
                Entry(package, "staticwebassets/kit.css", ".kit-card {\n    border: 1px solid #ccc;\n}\n");
            }
            // A package whose assets its own StaticWebAssetBasePath serves, as its build's props say.
            using (ZipArchive package = ZipFile.Open(PathOf("feed/lamp.1.0.0.nupkg"), ZipArchiveMode.Create))
            {
                Entry(package, "Lamp.nuspec", "<?xml version=\"1.0\"?>\n<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\n"
                    + "  <metadata>\n    <id>Lamp</id>\n    <version>1.0.0</version>\n    <authors>Passenger</authors>\n    <description>A stylesheet</description>\n"
                    + "  </metadata>\n</package>\n");
                Entry(package, "staticwebassets/lamp.css", ".lamp-glow {\n    color: orange;\n}\n");
                Entry(package, "build/Microsoft.AspNetCore.StaticWebAssets.props", "<Project>\n  <ItemGroup>\n"
                    + "    <StaticWebAsset Include=\"$([System.IO.Path]::GetFullPath($(MSBuildThisFileDirectory)..\\staticwebassets\\lamp.css))\">\n"
                    + "      <SourceType>Package</SourceType>\n      <SourceId>Lamp</SourceId>\n      <BasePath>lamp</BasePath>\n      <RelativePath>lamp.css</RelativePath>\n"
                    + "    </StaticWebAsset>\n  </ItemGroup>\n</Project>\n");
            }
            File.WriteAllText(PathOf("nuget.config"), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n"
                + "    <add key=\"kit\" value=\"feed\" />\n  </packageSources>\n</configuration>\n");
            // A Razor class library the app references, serving its static web assets under a base path of its own.
            Directory.CreateDirectory(PathOf("Gear/wwwroot"));
            File.WriteAllText(PathOf("Gear/Gear.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Razor\">\n  <PropertyGroup>\n"
                + $"    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <StaticWebAssetBasePath>gear</StaticWebAssetBasePath>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(PathOf("Gear/wwwroot/gear.css"), ".gear-spin {\n    rotate: 90deg;\n}\n");
            WritePages();
            string project = await File.ReadAllTextAsync(PathOf("Shop.csproj"));
            File.WriteAllText(PathOf("Shop.csproj"), project.Replace("</Project>", "  <ItemGroup>\n    <PackageReference Include=\"Kit\" Version=\"1.0.0\" />\n    <PackageReference Include=\"Lamp\" Version=\"1.0.0\" />\n"
                + "    <ProjectReference Include=\"Gear/Gear.csproj\" />\n  </ItemGroup>\n\n  <PropertyGroup>\n    <DefaultItemExcludes>$(DefaultItemExcludes);Gear/**</DefaultItemExcludes>\n  </PropertyGroup>\n\n</Project>", StringComparison.Ordinal));
            Directory.CreateDirectory(PathOf("bin"));
        }, Folder);
        await PrepareAsync();
    }

    /// <summary>The library's pages, each what one way of naming a stylesheet needs. They are the library's, as the app's
    /// Razor files take the app's pages alone: what they leave unread leaves the app's complete.</summary>
    private void WritePages()
    {
        void Write(string name, string text)
        {
            string path = PathOf($"Gear/wwwroot/{name}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        string LinkOf(string text) => $"<link rel=\"stylesheet\" href=\"{cdn.Root}/latest.css\" integrity=\"sha384-{Convert.ToBase64String(SHA384.HashData(Encoding.UTF8.GetBytes(text)))}\" />\n";
        Write("latest-before.html", LinkOf(LatestBefore));
        Write("latest-after.html", LinkOf(LatestAfter));
        Write("latest-ahead.html", LinkOf(".from-ahead{color:blue}"));
        Write("css/locked.css", ".locked-note {\n    color: teal;\n}\n");
        Write("locked.html", "<link rel=\"stylesheet\" href=\"css/locked.css\" />\n");
        for (int depth = 0; depth < 10; depth++)
        {
            Write($"deep/depth{depth}.css", (depth < 9 ? $"@import \"depth{depth + 1}.css\";\n" : string.Empty) + $".at-depth-{depth} {{\n    color: teal;\n}}\n");
        }
        Write("deep.html", "<link rel=\"stylesheet\" href=\"deep/depth0.css\" />\n");
    }

    public override async ValueTask DisposeAsync()
    {
        await cdn.DisposeAsync();
        await base.DisposeAsync();
    }

    private static void Entry(ZipArchive package, string name, string text)
    {
        using StreamWriter writer = new(package.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
