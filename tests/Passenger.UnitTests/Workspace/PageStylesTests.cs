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

    [Fact]
    public void A_stylesheet_names_every_class_its_selectors_name_in_order_with_its_imports()
    {
        CssSheet sheet = CssRules.Read(Css);
        Assert.Equal(["btn", "btn-primary", "btn", "active", "col-md-6", "md:flex", "w-50"], sheet.Rules.Select(r => r.Name));
        Assert.Equal(["theme.css", "print.css"], sheet.Imports);
        CssClassRule primary = sheet.Classes["btn-primary"].Single();
        Assert.Equal(".btn-primary", Css.Substring(primary.At - 1, ".btn-primary".Length));
    }

    [Fact]
    public void A_rule_is_described_by_its_selector_and_a_declaration_a_line()
    {
        CssSheet sheet = CssRules.Read(Css);
        Assert.Equal(".col-md-6 {\n    flex: 0 0 auto;\n    width: 50%;\n}", sheet.Describe(sheet.Classes["col-md-6"].Single()));
        Assert.Equal(".btn-primary, .btn.active:hover {\n    color: #fff;\n    background: url(\"a{b}.png\");\n}", sheet.Describe(sheet.Classes["btn-primary"].Single()));
        Assert.Equal(".btn-primary, .btn.active:hover {\n    color: #fff;\n    …\n}", sheet.Describe(sheet.Classes["btn-primary"].Single(), most: 1));
        Assert.Equal(".btn-primary, .btn.active:hover {\n    color: #fff;\n    background: url(\"a{…\n}", sheet.Describe(sheet.Classes["btn-primary"].Single(), widest: 20));
    }

    [Fact]
    public void A_minified_stylesheet_reads_as_the_same_classes()
    {
        CssSheet sheet = CssRules.Read(".a{color:red}.b,.c>.d{margin:0}@media print{.e{display:none}}.f::after{content:\".g\"}");
        Assert.Equal(["a", "b", "c", "d", "e", "f"], sheet.Rules.Select(r => r.Name));
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

    [Fact]
    public void A_page_links_the_stylesheets_its_link_tags_name_an_assets_path_read_as_its_path_and_a_views_root_as_the_apps()
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

    [Fact]
    public void A_component_writes_its_class_attributes_words_a_components_css_class_and_an_explicit_expressions_strings()
    {
        Assert.Equal(["card", "shadow", "card-lg", "card-sm", "btn", "btn-primary", "lead"], MarkupTags.Classes(Component, razor: true).Select(c => c.Name));
    }

    [Fact]
    public void A_class_is_found_where_it_is_written_and_where_one_would_begin_but_never_inside_an_expressions_code()
    {
        int shadow = Component.IndexOf("shadow", StringComparison.Ordinal);
        Assert.Equal((shadow, shadow + "shadow".Length), MarkupTags.ClassAt(Component, shadow + 3, razor: true));
        int lg = Component.IndexOf("card-lg", StringComparison.Ordinal);
        Assert.Equal((lg, lg + "card-lg".Length), MarkupTags.ClassAt(Component, lg + 2, razor: true));
        int big = Component.IndexOf("big ?", StringComparison.Ordinal);
        Assert.Null(MarkupTags.ClassAt(Component, big + 1, razor: true));
        int first = Component.IndexOf("class=\"", StringComparison.Ordinal) + "class=\"".Length;
        Assert.Equal((first, first + "card".Length), MarkupTags.ClassAt(Component, first, razor: true));
        int id = Component.IndexOf("id=\"x", StringComparison.Ordinal) + 4;
        Assert.Null(MarkupTags.ClassAt(Component, id, razor: true));
        string typing = "<div class=\"btn \"></div>";
        Assert.Equal((16, 16), MarkupTags.ClassAt(typing, 16, razor: false));
    }

    [Fact]
    public void A_links_integrity_is_met_by_its_strongest_hash_and_one_of_no_known_algorithm_asks_nothing()
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

    [Fact]
    public void A_class_after_deep_is_marked_deep_in_its_selector_alone_the_next_selector_of_a_list_beginning_again()
    {
        CssSheet sheet = CssRules.Read(".panel ::deep .child, .plain .other { color: red; }\n.top::DEEP .nested { }\n.lone { }\n");
        Assert.Equal([("panel", false), ("child", true), ("plain", false), ("other", false), ("top", false), ("nested", true), ("lone", false)],
            sheet.Rules.Select(r => (r.Name, r.Deep)));
    }

    [Fact]
    public void A_rule_nested_in_a_rule_names_its_classes_wherever_its_selector_says_them()
    {
        const string Nested = """
            .card {
                color: red;
                & .title { color: blue; }
                &.active, .lead > & { margin: 0 }
                .plain { .deeper { top: 0 } }
                @media (min-width: 768px) { .wide { width: 50% } }
                padding: 0;
            }
            .after { }
            """;
        CssSheet sheet = CssRules.Read(Nested);
        Assert.Equal(["card", "title", "active", "lead", "plain", "deeper", "wide", "after"], sheet.Rules.Select(r => r.Name));
        Assert.Equal("& .title {\n    color: blue;\n}", sheet.Describe(sheet.Classes["title"].Single()));
        Assert.Equal(".card {\n    color: red;\n    padding: 0;\n}", sheet.Describe(sheet.Classes["card"].Single()));
    }

    [Fact]
    public void A_block_that_holds_no_rules_names_no_class_though_a_rule_holds_it()
    {
        CssSheet sheet = CssRules.Read("""
            .a { --mixin: { .value: 1 }; background: url(img.icon{1}.png); .b { top: 0 } }
            @keyframes spin { from.step { top: 0 } }
            .c { @keyframes inner { to.step { top: 0 } } @font-face { src: url(a.woff) } }
            @font-face { src: url(a.b{c}.woff); }
            .d { background: url( "e.f{g}.png" ) }
            """);
        Assert.Equal(["a", "b", "c", "d"], sheet.Rules.Select(r => r.Name));
    }

    [Fact]
    public void A_class_of_a_rule_nested_past_deep_is_deep_and_the_rules_after_it_are_not()
    {
        CssSheet sheet = CssRules.Read(".panel ::deep { .child { color: red; } @media print { .printed { top: 0 } } }\n.plain { .nested { top: 0 } }\n");
        Assert.Equal([("panel", false), ("child", true), ("printed", true), ("plain", false), ("nested", false)], sheet.Rules.Select(r => (r.Name, r.Deep)));
    }

    [Fact]
    public void An_import_the_text_ends_is_read_and_one_inside_a_block_is_none_a_browser_takes()
    {
        Assert.Equal(["last.css"], CssRules.Read("@import url(\"last.css\")\n").Imports);
        Assert.Equal(["first.css"], CssRules.Read("@import 'first.css';\n.x { @import 'no.css'; }\n@media print { @import 'nor.css'; }\n").Imports);
    }

    [Fact]
    public void A_pages_links_are_what_a_browser_asks_for_under_its_base_href_and_without_its_path_for_an_app_mounted_there()
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

    [Fact]
    public void A_base_on_another_host_makes_a_pages_links_addresses_on_that_host()
    {
        const string Page = "<base href=\"https://cdn.example/lib/\">\n<link rel=\"stylesheet\" href=\"css/a.css\" integrity=\"sha384-abc\">\n<link rel=\"stylesheet\" href=\"/b.css\">\n"
            + "<link rel=\"stylesheet\" href=\"https://other.example/c.css\">\n";
        Assert.Equal([new LinkedSheet("https://cdn.example/lib/css/a.css", "sha384-abc"), new LinkedSheet("https://cdn.example/b.css", null), new LinkedSheet("https://other.example/c.css", null)],
            PageLinks.Stylesheets(Page, razor: false));
        Assert.Equal([new LinkedSheet("https://cdn.example/lib/a.css", null)], PageLinks.Stylesheets("<base href=\"//cdn.example/lib/\"><link rel=\"stylesheet\" href=\"a.css\">", razor: false));
    }

    [Fact]
    public void A_page_of_a_known_address_links_from_there_unless_its_base_says_otherwise()
    {
        const string Links = "<link rel=\"stylesheet\" href=\"a.css\"><link rel=\"stylesheet\" href=\"../b.css\"><link rel=\"stylesheet\" href=\"/c.css\">";
        Assert.Equal(["/docs/guide/a.css", "/docs/b.css", "/c.css"], PageLinks.Stylesheets(Links, razor: false, "/docs/guide/index.html").Select(l => l.Href));
        Assert.Equal(["/a.css", "/b.css", "/c.css"], PageLinks.Stylesheets("<base href=\"/\">" + Links, razor: false, "/docs/guide/index.html").Select(l => l.Href));
        Assert.Equal(["/docs/guide/css/a.css", "/docs/guide/b.css", "/c.css"], PageLinks.Stylesheets("<base href=\"css/\">" + Links, razor: false, "/docs/guide/index.html").Select(l => l.Href));
    }

    [Fact]
    public void An_import_is_read_from_its_stylesheets_address_a_parent_stopping_at_the_apps_root()
    {
        Assert.Equal("/colors.css", PageLinks.Imported("css/site.css", "../colors.css"));
        Assert.Equal("/css/parts/a.css", PageLinks.Imported("/css/site.css", "parts/a.css"));
        Assert.Equal("/colors.css", PageLinks.Imported("/css/site.css", "../../../colors.css"));
        Assert.Equal("/_content/Kit/base.css", PageLinks.Imported("/_content/Kit/css/kit.css", "../base.css"));
        Assert.Equal("/top.css", PageLinks.Imported("/css/site.css", "/top.css"));
        Assert.Equal("https://cdn.example/a.css", PageLinks.Imported("/css/site.css", "//cdn.example/a.css"));
        Assert.Equal("https://cdn.example/lib/b.css", PageLinks.Imported("https://cdn.example/lib/css/a.css", "../b.css"));
    }

    [Fact]
    public void A_projects_bundle_is_its_package_ids_styles_css_where_its_build_places_it()
    {
        Assert.True(PageLinks.IsBundle("Shop.styles.css", "Shop", null));
        Assert.True(PageLinks.IsBundle("/shop.STYLES.css", "Shop", "/"));
        Assert.True(PageLinks.IsBundle("/Shop.styles.css", "Shop", "_content/Shop"));
        Assert.True(PageLinks.IsBundle("/first/Shop.styles.css", "Shop", "first/"));
        Assert.False(PageLinks.IsBundle("/Shop.styles.css", "Shop", "first"));
        Assert.False(PageLinks.IsBundle("css/site.styles.css", "Shop", null));
        Assert.False(PageLinks.IsBundle("css/Shop.styles.css", "Shop", null));
        Assert.False(PageLinks.IsBundle("Other.styles.css", "Shop", null));
    }

    [Fact]
    public void A_style_blocks_imports_are_links_from_the_pages_base()
    {
        Assert.Equal([new LinkedSheet("/colors.css", null), new LinkedSheet("https://cdn.example/x.css", null)],
            PageLinks.Stylesheets("<style>@import url(\"/colors.css\");\n@import 'https://cdn.example/x.css';\n.a { }</style>", razor: false));
        Assert.Equal([new LinkedSheet("/app/colors.css", null, "/colors.css")], PageLinks.Stylesheets("<base href=\"/app/\"><style>@import \"colors.css\"</style>", razor: false));
    }

    [Fact]
    public void A_razor_pages_style_block_is_read_as_css_its_doubled_at_signs_single()
    {
        Assert.Equal(["colors.css"], PageLinks.Stylesheets("<style>@@import \"colors.css\";</style>", razor: true).Select(l => l.Href));
        const string Page = "<style>@@media print { .printed { top: 0 } }</style>";
        string text = PageLinks.StyleText(Page, razor: true)!;
        CssClassRule rule = Assert.Single(CssRules.Read(text).Rules);
        Assert.Equal("printed", rule.Name);
        Assert.Equal(Page.IndexOf(".printed", StringComparison.Ordinal) + 1, rule.At);
    }

    [Fact]
    public void A_link_an_expression_makes_says_so_and_an_html_pages_at_sign_is_part_of_its_address()
    {
        LinkedSheet made = Assert.Single(PageLinks.Stylesheets("<link rel=\"stylesheet\" href=\"@Theme.Sheet\" />", razor: true));
        Assert.True(made.IsExpression, "A stylesheet an expression names was taken for an address, as if it could be read");
        Assert.Equal("@Theme.Sheet", made.Href);
        Assert.Empty(PageLinks.Stylesheets("<link rel=\"stylesheet\" href=\"\" />", razor: true));
        Assert.Equal([new LinkedSheet("lib/pkg@1.2/x.css", null)], PageLinks.Stylesheets("<link rel=\"stylesheet\" href=\"lib/pkg@1.2/x.css\" />", razor: false));
    }

    [Fact]
    public void A_pages_style_blocks_read_as_a_stylesheet_at_the_pages_own_places()
    {
        const string Page = "<head>\n<style>\n.inline-note { color: red; }\n</style>\n</head>\n<p class=\"inline-note\">x</p>\n";
        string text = PageLinks.StyleText(Page, razor: true)!;
        Assert.Equal(Page.Length, text.Length);
        CssClassRule rule = Assert.Single(CssRules.Read(text).Rules);
        Assert.Equal("inline-note", rule.Name);
        Assert.Equal(Page.IndexOf(".inline-note", StringComparison.Ordinal) + 1, rule.At);
        Assert.Null(PageLinks.StyleText("<p class=\"x\">no style</p>", razor: false));
    }

    [Fact]
    public void A_packages_own_static_web_asset_base_path_is_read_from_its_build_props()
    {
        Assert.Equal("lamp", PageLinks.AssetBasePath("<Project><ItemGroup><StaticWebAsset Include=\"x\"><BasePath>/lamp/</BasePath></StaticWebAsset></ItemGroup></Project>"));
        Assert.Null(PageLinks.AssetBasePath("<Project />"));
    }
}
