using System.Security.Cryptography;
using System.Text;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a page's stylesheets give the markup that links them: every class a rule's selector names, nested in an
/// at-rule too, escapes read; a rule described a declaration a line; the stylesheets <c>@import</c>s and a page's links
/// name; the classes a page writes, a Razor expression's strings among them; and a link's integrity checked as a browser
/// checks it.</summary>
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
}
