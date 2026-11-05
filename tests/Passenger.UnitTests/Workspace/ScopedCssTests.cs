using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A component's scoped stylesheet and its markup: the names its selectors may take are the HTML elements, classes and
/// ids the markup's start tags write (never a component's tag, which takes no scope, nor a Razor expression, a comment or
/// code), and they are offered only where a selector is written, a nested rule's too, never inside a rule's declarations, a
/// comment or a string.</summary>
public class ScopedCssTests
{
    private const string Markup = """
        @page "/bell"

        <div class="bell ringing @(on ? "on" : "")" id="chime">
            <button class="btn btn-primary" @onclick="Ring">Ring</button>
            <Counter class="counted" />
            <my-widget></my-widget>
            <input id="@name" />
        </div>
        """;

    [Fact]
    public void The_names_are_the_markups_elements_and_its_classes_and_ids_written_as_text()
    {
        (IReadOnlyList<string> elements, IReadOnlyList<string> classes, IReadOnlyList<string> ids) = ScopedCss.Names(Markup);
        Assert.Equal(["div", "button", "my-widget", "input"], elements);
        Assert.Equal(["bell", "ringing", "btn", "btn-primary", "counted"], classes);
        Assert.Equal(["chime"], ids);
    }

    [Fact]
    public void A_class_or_id_is_read_in_either_of_markups_quotes()
    {
        (_, IReadOnlyList<string> classes, IReadOnlyList<string> ids) = ScopedCss.Names("<div class='card selected' id='main'></div>\n<p class = \"lead\" id=\"note\" title='it\"s'></p>\n");
        Assert.Equal(["card", "selected", "lead"], classes);
        Assert.Equal(["main", "note"], ids);
    }

    [Fact]
    public void Only_a_start_tags_own_class_and_id_are_names_and_not_what_a_comment_code_or_another_attribute_writes()
    {
        const string Page = """
            <!-- <div class="commented" id="gone"></div> -->
            @* <div class="unrendered" id="unseen"></div> *@
            <div class="real" id="here" data-id="dataid" data-class="dataclass" aria-id='ariaid'></div>
            <script>const html = '<p class="scripted" id="written"></p>';</script>
            @{ string id = "local"; char mark = 'x'; }
            @code {
                private string id = "field";
                private char class_ = 'y';
                private string html = "<section class=\"quoted\"></section>";
                private List<string> Rows = [];
            }
            """;
        (IReadOnlyList<string> elements, IReadOnlyList<string> classes, IReadOnlyList<string> ids) = ScopedCss.Names(Page);
        Assert.Equal(["div", "script"], elements);
        Assert.Equal(["real"], classes);
        Assert.Equal(["here"], ids);
    }

    [Fact]
    public void A_scoped_stylesheet_styles_the_component_or_page_it_is_named_for()
    {
        Assert.Equal(Path.Combine("Pages", "Bell.razor"), ScopedCss.MarkupOf(Path.Combine("Pages", "Bell.razor.css")));
        Assert.Equal(Path.Combine("Pages", "Index.cshtml"), ScopedCss.MarkupOf(Path.Combine("Pages", "Index.cshtml.css")));
        Assert.Null(ScopedCss.MarkupOf(Path.Combine("wwwroot", "app.css")));
    }

    [Fact]
    public void A_selector_is_offered_names_outside_a_rules_braces_after_a_dot_a_class_and_after_a_hash_an_id()
    {
        const string Css = ".bell ::deep button {\n    color: red;\n}\n\nbu";
        Assert.Equal((Css.Length - 2, (char?)null), ScopedCss.SelectorAt(Css, Css.Length));
        Assert.Equal((Css.Length + 1, (char?)'.'), ScopedCss.SelectorAt(Css + ".", Css.Length + 1));
        Assert.Equal((Css.Length + 2, (char?)'#'), ScopedCss.SelectorAt(Css + "\n#ch", Css.Length + 4));
        Assert.Null(ScopedCss.SelectorAt(Css, Css.IndexOf("red", StringComparison.Ordinal)));
        Assert.Null(ScopedCss.SelectorAt("/* bu", 5));
        Assert.Null(ScopedCss.SelectorAt("a:ho", 4));
    }

    [Fact]
    public void An_at_rules_body_holds_selectors_and_its_rules_declarations_do_not()
    {
        const string Css = "@media (max-width: 600px) {\n    bu";
        Assert.Equal((Css.Length - 2, (char?)null), ScopedCss.SelectorAt(Css, Css.Length));
        const string Inner = "@media (max-width: 600px) {\n    .bell {\n        co";
        Assert.Null(ScopedCss.SelectorAt(Inner, Inner.Length));
    }

    [Theory]
    [InlineData(".bell {\n    color: red;\n    .|", '.')]
    [InlineData(".bell {\n    .ri|", '.')]
    [InlineData(".bell {\n    #ch|", '#')]
    [InlineData(".bell {\n    & .ri|", '.')]
    [InlineData(".bell {\n    &.ri|", '.')]
    [InlineData(".bell {\n    &:hover .ri|", '.')]
    [InlineData(".bell {\n    > .ri|", '.')]
    [InlineData(".bell {\n    div .ri|", '.')]
    [InlineData(".bell {\n    a, .ri|", '.')]
    [InlineData(".bell {\n    /* the ring */ .ri|", '.')]
    [InlineData(".bell {\n    .ringing {\n        .ri|", '.')]
    [InlineData(".bell {\n    & bu|", null)]
    [InlineData(".bell {\n    > bu|", null)]
    [InlineData("@media (max-width: 600px) {\n    .bell {\n        .ri|", '.')]
    public void A_rule_nested_in_a_rule_is_offered_names_where_its_selector_is_written(string written, char? mark)
    {
        string css = written.Replace("|", string.Empty, StringComparison.Ordinal);
        int caret = written.IndexOf('|', StringComparison.Ordinal);
        int start = caret;
        while (start > 0 && char.IsLetter(css[start - 1]))
        {
            start--;
        }
        Assert.Equal((start, mark), ScopedCss.SelectorAt(css, caret));
    }

    [Theory]
    [InlineData(".bell {\n    co")]
    [InlineData(".bell {\n    color: re")]
    [InlineData(".bell {\n    color: #ff")]
    [InlineData(".bell {\n    margin: .5")]
    [InlineData(".bell {\n    margin: 0 .5em .2")]
    [InlineData(".bell {\n    color: red;\n    co")]
    [InlineData(".bell {\n    .ringing {\n        co")]
    [InlineData(".bell {\n    :ho")]
    [InlineData(".bell {\n    /* .ri")]
    public void A_declaration_in_a_rule_is_offered_no_names_whatever_its_value_looks_like(string css)
    {
        Assert.Null(ScopedCss.SelectorAt(css, css.Length));
    }
}
