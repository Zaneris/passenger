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
/// before, its link's integrity checked; a package's static web asset under <c>_content/Kit/</c>, read from the package
/// folder its restore resolved; the component's own scoped stylesheet; a class's rules on the hover card and Go to
/// Declaration to its rule in the downloaded copy or in the package folder's, read-only; and a class no stylesheet
/// defines marked, quietly, once every stylesheet linked was read. On a Blazor app made by <c>dotnet new blazor</c>, its
/// warm server shared (<see cref="SharedProject"/>).</summary>
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

    private const string Component = "@page \"/styled\"\n\n<h1 class=\"halo\">Styled</h1>\n\n<p class=\"\"></p>\n\n"
        + "<button class=\"btn btn-primary\">Go</button>\n<div class=\"btn-primry kit-card\"></div>\n";

    private readonly StandInHttp cdn;
    private int fetched;

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
            await exchange.ReplyAsync(404, "text/plain", "not here");
        });
    }

    /// <summary>How many times the stand-in CDN was asked for its stylesheet.</summary>
    public int Fetched => Volatile.Read(ref fetched);

    public override async ValueTask InitializeAsync()
    {
        string integrity = "sha384-" + Convert.ToBase64String(SHA384.HashData(Encoding.UTF8.GetBytes(Bootstrap)));
        await TestFolders.PrepareAsync(async () =>
        {
            await BlazorApp.CreateAsync(Folder);
            string page = await File.ReadAllTextAsync(PathOf("Components/App.razor"));
            page = page.Replace("<link rel=\"stylesheet\" href=\"@Assets[\"lib/bootstrap/dist/css/bootstrap.min.css\"]\" />",
                $"<link rel=\"stylesheet\" href=\"{cdn.Root}/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css\" integrity=\"{integrity}\" crossorigin=\"anonymous\" />\n"
                + "    <link rel=\"stylesheet\" href=\"_content/Kit/kit.css\" />", StringComparison.Ordinal);
            Assert.Contains("_content/Kit/kit.css", page, StringComparison.Ordinal);
            File.WriteAllText(PathOf("Components/App.razor"), page);
            Directory.Delete(PathOf("wwwroot/lib"), recursive: true);
            File.WriteAllText(PathOf("Components/Pages/Styled.razor"), Component);
            File.WriteAllText(PathOf("Components/Pages/Styled.razor.css"), ".halo {\n    color: gold;\n}\n");
            Directory.CreateDirectory(PathOf("feed"));
            using (ZipArchive package = ZipFile.Open(PathOf("feed/kit.1.0.0.nupkg"), ZipArchiveMode.Create))
            {
                Entry(package, "Kit.nuspec", "<?xml version=\"1.0\"?>\n<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\n"
                    + "  <metadata>\n    <id>Kit</id>\n    <version>1.0.0</version>\n    <authors>Passenger</authors>\n    <description>A stylesheet</description>\n"
                    + "  </metadata>\n</package>\n");
                Entry(package, "staticwebassets/kit.css", ".kit-card {\n    border: 1px solid #ccc;\n}\n");
            }
            File.WriteAllText(PathOf("nuget.config"), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n"
                + "    <add key=\"kit\" value=\"feed\" />\n  </packageSources>\n</configuration>\n");
            string project = await File.ReadAllTextAsync(PathOf("Shop.csproj"));
            File.WriteAllText(PathOf("Shop.csproj"), project.Replace("</Project>", "  <ItemGroup>\n    <PackageReference Include=\"Kit\" Version=\"1.0.0\" />\n  </ItemGroup>\n\n</Project>", StringComparison.Ordinal));
            Directory.CreateDirectory(PathOf("bin"));
        }, Folder);
        await PrepareAsync();
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
