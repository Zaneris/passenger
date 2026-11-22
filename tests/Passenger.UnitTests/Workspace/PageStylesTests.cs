using System.Security.Cryptography;
using System.Text;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a page's stylesheets give the markup that links them: every class a rule's selector names, nested in an
/// at-rule or a rule too, escapes read; a rule described a declaration a line; the stylesheets <c>@import</c>s, a page's
/// links and its style blocks name, resolved as a browser resolves them; the classes a page writes, a Razor expression's
/// strings among them; and a link's integrity checked as a browser checks it.</summary>
public class PageStylesTests
{
    private const string Css = """
        /* Buttons */
        @import url("theme.css");
        @import 'print.css' print;
        .btn { display: inline-block; padding: .375rem .75rem }
        .btn-primary, .btn.active:hover { color: #fff; background: url("a{b}.png") }
        @media (min-width: 768px) {
            .col-md-6 { flex: 0 0 auto; width: 50% }
        }
        @keyframes spin { from { transform: rotate(0) } to { transform: rotate(360deg) } }
        .md\:flex { display: flex }
        a[href$=".pdf"] { color: red }
        .w-50 { width: 50% !important }
        """;

    /// <summary>A stylesheet names the classes its rules name, nested, deep or minified, as a browser reads
    /// them.</summary>
    [Fact]
    public void A_stylesheet_names_its_classes_as_a_browser_reads_its_rules() => Cases.All(
        (nameof(A_stylesheet_names_every_class_its_selectors_name_in_order_with_its_imports),
            A_stylesheet_names_every_class_its_selectors_name_in_order_with_its_imports),
        (nameof(A_class_after_deep_is_marked_deep_in_its_selector_alone_the_next_selector_of_a_list_beginning_again),
            A_class_after_deep_is_marked_deep_in_its_selector_alone_the_next_selector_of_a_list_beginning_again));

    /// <summary>A page's stylesheets are the addresses a browser asks for, under its base and its app's
    /// mount.</summary>
    [Fact]
    public void A_pages_stylesheets_are_the_addresses_a_browser_asks_for() => Cases.All(
        (nameof(A_page_links_the_stylesheets_its_link_tags_name_an_assets_path_read_as_its_path_and_a_views_root_as_the_apps),
            A_page_links_the_stylesheets_its_link_tags_name_an_assets_path_read_as_its_path_and_a_views_root_as_the_apps),
        (nameof(A_links_integrity_is_met_by_its_strongest_hash_and_one_of_no_known_algorithm_asks_nothing),
            A_links_integrity_is_met_by_its_strongest_hash_and_one_of_no_known_algorithm_asks_nothing),
        (nameof(A_pages_links_are_what_a_browser_asks_for_under_its_base_href_and_without_its_path_for_an_app_mounted_there),
            A_pages_links_are_what_a_browser_asks_for_under_its_base_href_and_without_its_path_for_an_app_mounted_there),
        (nameof(A_base_on_another_host_makes_a_pages_links_addresses_on_that_host),
            A_base_on_another_host_makes_a_pages_links_addresses_on_that_host),
        (nameof(An_import_is_read_from_its_stylesheets_address_a_parent_stopping_at_the_apps_root),
            An_import_is_read_from_its_stylesheets_address_a_parent_stopping_at_the_apps_root));

    /// <summary>A component's classes and a page's style blocks are read as Razor reads them.</summary>
    [Fact]
    public void A_component_writes_its_classes_and_style_blocks_as_razor_reads_them() => Cases.All(
        (nameof(A_component_writes_its_class_attributes_words_a_components_css_class_and_an_explicit_expressions_strings),
            A_component_writes_its_class_attributes_words_a_components_css_class_and_an_explicit_expressions_strings),
        (nameof(A_razor_pages_style_block_is_read_as_css_its_doubled_at_signs_single),
            A_razor_pages_style_block_is_read_as_css_its_doubled_at_signs_single));

    private void A_stylesheet_names_every_class_its_selectors_name_in_order_with_its_imports()
    {
        CssSheet sheet = CssRules.Read(Css);
        Assert.Equal(["btn", "btn-primary", "btn", "active", "col-md-6", "md:flex", "w-50"], sheet.Rules.Select(r => r.Name));
        Assert.Equal(["theme.css", "print.css"], sheet.Imports);
        CssClassRule primary = sheet.Classes["btn-primary"].Single();
        Assert.Equal(".btn-primary", Css.Substring(primary.At - 1, ".btn-primary".Length));
    }

    private const string Page = """
        <!DOCTYPE html>
        <html>
        <head>
            <link rel="stylesheet" href="https://cdn.example.org/bootstrap.min.css" integrity="sha384-abc" crossorigin="anonymous" />
            <link rel="stylesheet" href="@Assets["app.css"]" />
            <link rel="icon" href="favicon.png" />
            <link href="~/css/site.css" rel="preload stylesheet" />
            <!-- <link rel="stylesheet" href="old.css" /> -->
        </head>
        """;

    private void A_page_links_the_stylesheets_its_link_tags_name_an_assets_path_read_as_its_path_and_a_views_root_as_the_apps()
    {
        Assert.Equal(
            [new LinkedSheet("https://cdn.example.org/bootstrap.min.css", "sha384-abc"), new LinkedSheet("app.css", null), new LinkedSheet("/css/site.css", null)],
            PageLinks.Stylesheets(Page, razor: true));
    }

    private const string Component = """
        @page "/"
        <div class="card shadow @(big ? "card-lg" : "card-sm") btn-@size" id="x">
            <Button CssClass="btn btn-primary" />
            <p class='lead'>@* <b class="hidden"> *@</p>
        </div>
        @code {
            private string html = "<i class=\"not-markup\">";
            private List<int> list = new();
        }
        """;

    private void A_component_writes_its_class_attributes_words_a_components_css_class_and_an_explicit_expressions_strings()
    {
        Assert.Equal(["card", "shadow", "card-lg", "card-sm", "btn", "btn-primary", "lead"], MarkupTags.Classes(Component, razor: true).Select(c => c.Name));
    }

    /// <summary>A component named with marks, and an expression whose names hold them, its string no markup.</summary>
    private const string Marked = """
        <गिनती CssClass="badge" />
        <p>@नमस्ते.गिनती("<i class='hidden'>")</p>
        <p class="lead"></p>
        """;

    private void A_links_integrity_is_met_by_its_strongest_hash_and_one_of_no_known_algorithm_asks_nothing()
    {
        byte[] content = Encoding.UTF8.GetBytes(".btn{}");
        string sha384 = "sha384-" + Convert.ToBase64String(SHA384.HashData(content));
        string sha256 = "sha256-" + Convert.ToBase64String(SHA256.HashData(content));
        Assert.True(PageLinks.Matches(sha384, content));
        Assert.True(PageLinks.Matches($"sha256-wrong {sha384}", content));
        Assert.False(PageLinks.Matches($"{sha256} sha384-wrong", content));
        Assert.False(PageLinks.Matches(sha384, Encoding.UTF8.GetBytes(".btn{ }")));
        Assert.True(PageLinks.Matches("md5-whatever", content));
        Assert.True(PageLinks.Matches(null, content));
    }

    private void A_class_after_deep_is_marked_deep_in_its_selector_alone_the_next_selector_of_a_list_beginning_again()
    {
        CssSheet sheet = CssRules.Read(".panel ::deep .child, .plain .other { color: red; }\n.top::DEEP .nested { }\n.lone { }\n");
        Assert.Equal([("panel", false), ("child", true), ("plain", false), ("other", false), ("top", false), ("nested", true), ("lone", false)],
            sheet.Rules.Select(r => (r.Name, r.Deep)));
    }

    private void A_pages_links_are_what_a_browser_asks_for_under_its_base_href_and_without_its_path_for_an_app_mounted_there()
    {
        const string Page = "<base href=\"/app/\" />\n<link rel=\"stylesheet\" href=\"css/site.css\" />\n<link rel=\"stylesheet\" href=\"/app/css/based.css\" />\n"
            + "<link rel=\"stylesheet\" href=\"/other/x.css\" />\n<link rel=\"stylesheet\" href=\"https://cdn.example/x.css\" />\n<link rel=\"stylesheet\" href=\"@Assets[\"app.css\"]\" />";
        Assert.Equal(
            [new LinkedSheet("/app/css/site.css", null, "/css/site.css"), new LinkedSheet("/app/css/based.css", null, "/css/based.css"), new LinkedSheet("/other/x.css", null),
                new LinkedSheet("https://cdn.example/x.css", null), new LinkedSheet("/app/app.css", null, "/app.css")],
            PageLinks.Stylesheets(Page, razor: true));
        // At the root, or at a view's root wherever it is mounted, as written.
        Assert.Equal([new LinkedSheet("css/site.css", null)], PageLinks.Stylesheets("<base href=\"/\" /><link rel=\"stylesheet\" href=\"css/site.css\" />", razor: false));
        Assert.Equal([new LinkedSheet("css/site.css", null)], PageLinks.Stylesheets("<base href=\"~/\" /><link rel=\"stylesheet\" href=\"css/site.css\" />", razor: true));
        // A base's last name is a file's, and an expression's is known only as the app runs.
        Assert.Equal([new LinkedSheet("css/site.css", null)], PageLinks.Stylesheets("<base href=\"/app\" /><link rel=\"stylesheet\" href=\"css/site.css\" />", razor: false));
        Assert.Equal([new LinkedSheet("/app/css/site.css", null, "/css/site.css")], PageLinks.Stylesheets("<base href=\"/app/index.html\" /><link rel=\"stylesheet\" href=\"css/site.css\" />", razor: false));
        Assert.Equal([new LinkedSheet("css/site.css", null)], PageLinks.Stylesheets("<base href=\"@Nav.BaseUri\" /><link rel=\"stylesheet\" href=\"css/site.css\" />", razor: true));
    }

    private void A_base_on_another_host_makes_a_pages_links_addresses_on_that_host()
    {
        const string Page = "<base href=\"https://cdn.example/lib/\">\n<link rel=\"stylesheet\" href=\"css/a.css\" integrity=\"sha384-abc\">\n<link rel=\"stylesheet\" href=\"/b.css\">\n"
            + "<link rel=\"stylesheet\" href=\"https://other.example/c.css\">\n";
        Assert.Equal([new LinkedSheet("https://cdn.example/lib/css/a.css", "sha384-abc"), new LinkedSheet("https://cdn.example/b.css", null), new LinkedSheet("https://other.example/c.css", null)],
            PageLinks.Stylesheets(Page, razor: false));
        Assert.Equal([new LinkedSheet("https://cdn.example/lib/a.css", null)], PageLinks.Stylesheets("<base href=\"//cdn.example/lib/\"><link rel=\"stylesheet\" href=\"a.css\">", razor: false));
    }

    private void An_import_is_read_from_its_stylesheets_address_a_parent_stopping_at_the_apps_root()
    {
        Assert.Equal("/colors.css", PageLinks.Imported("css/site.css", "../colors.css"));
        Assert.Equal("/css/parts/a.css", PageLinks.Imported("/css/site.css", "parts/a.css"));
        Assert.Equal("/colors.css", PageLinks.Imported("/css/site.css", "../../../colors.css"));
        Assert.Equal("/_content/Kit/base.css", PageLinks.Imported("/_content/Kit/css/kit.css", "../base.css"));
        Assert.Equal("/top.css", PageLinks.Imported("/css/site.css", "/top.css"));
        Assert.Equal("https://cdn.example/a.css", PageLinks.Imported("/css/site.css", "//cdn.example/a.css"));
        Assert.Equal("https://cdn.example/lib/b.css", PageLinks.Imported("https://cdn.example/lib/css/a.css", "../b.css"));
    }

    private void A_razor_pages_style_block_is_read_as_css_its_doubled_at_signs_single()
    {
        Assert.Equal(["colors.css"], PageLinks.Stylesheets("<style>@@import \"colors.css\";</style>", razor: true).Select(l => l.Href));
        const string Page = "<style>@@media print { .printed { top: 0 } }</style>";
        string text = PageLinks.StyleText(Page, razor: true)!;
        CssClassRule rule = Assert.Single(CssRules.Read(text).Rules);
        Assert.Equal("printed", rule.Name);
        Assert.Equal(Page.IndexOf(".printed", StringComparison.Ordinal) + 1, rule.At);
    }

}
