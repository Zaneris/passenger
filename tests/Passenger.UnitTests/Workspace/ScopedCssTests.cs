using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A component's scoped stylesheet and its markup: the names its selectors may take are the HTML elements, classes and
/// ids the markup writes (never a component's tag, which takes no scope, nor a Razor expression), and they are offered only
/// where a selector is written, never inside a rule's declarations, a comment or a string.</summary>
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
}
