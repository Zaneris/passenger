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

    /// <summary>A component's markup names the classes and ids its scoped stylesheet styles.</summary>
    [Fact]
    public void A_components_markup_names_what_its_scoped_stylesheet_styles() => Cases.All(
        (nameof(The_names_are_the_markups_elements_and_its_classes_and_ids_written_as_text),
            The_names_are_the_markups_elements_and_its_classes_and_ids_written_as_text),
        (nameof(A_scoped_stylesheet_styles_the_component_or_page_it_is_named_for), A_scoped_stylesheet_styles_the_component_or_page_it_is_named_for));

    /// <summary>Names are offered where a selector is written, never in a declaration.</summary>
    [Fact]
    public void A_selector_is_offered_names_only_where_a_selector_is_written() => Cases.All(
        (nameof(A_rule_nested_in_a_rule_is_offered_names_where_its_selector_is_written),
            A_rule_nested_in_a_rule_is_offered_names_where_its_selector_is_written),
        (nameof(A_declaration_in_a_rule_is_offered_no_names_whatever_its_value_looks_like),
            A_declaration_in_a_rule_is_offered_no_names_whatever_its_value_looks_like));

    private void The_names_are_the_markups_elements_and_its_classes_and_ids_written_as_text()
    {
        (IReadOnlyList<string> elements, IReadOnlyList<string> classes, IReadOnlyList<string> ids) = ScopedCss.Names(Markup);
        Assert.Equal(["div", "button", "my-widget", "input"], elements);
        Assert.Equal(["bell", "ringing", "btn", "btn-primary", "counted"], classes);
        Assert.Equal(["chime"], ids);
    }

    private void A_scoped_stylesheet_styles_the_component_or_page_it_is_named_for()
    {
        Assert.Equal(Path.Combine("Pages", "Bell.razor"), ScopedCss.MarkupOf(Path.Combine("Pages", "Bell.razor.css")));
        Assert.Equal(Path.Combine("Pages", "Index.cshtml"), ScopedCss.MarkupOf(Path.Combine("Pages", "Index.cshtml.css")));
        Assert.Null(ScopedCss.MarkupOf(Path.Combine("wwwroot", "app.css")));
    }

    private void A_rule_nested_in_a_rule_is_offered_names_where_its_selector_is_written() => Cases.Each<(string Written, char? Mark)>(
    [
        (".bell {\n    color: red;\n    .|", '.'),
        (".bell {\n    #ch|", '#'),
        (".bell {\n    & .ri|", '.'),
        (".bell {\n    > .ri|", '.'),
        (".bell {\n    /* the ring */ .ri|", '.'),
        (".bell {\n    & bu|", null),
        ("@media (max-width: 600px) {\n    .bell {\n        .ri|", '.'),
    ], row => A_rule_nested_in_a_rule_is_offered_names_where_its_selector_is_written(row.Written, row.Mark));

    private void A_rule_nested_in_a_rule_is_offered_names_where_its_selector_is_written(string written, char? mark)
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

    private void A_declaration_in_a_rule_is_offered_no_names_whatever_its_value_looks_like() => Cases.Each<string>(
    [
        ".bell {\n    co",
        ".bell {\n    margin: .5",
        ".bell {\n    color: red;\n    co",
        ".bell {\n    .ringing {\n        co",
        ".bell {\n    /* .ri",
    ], A_declaration_in_a_rule_is_offered_no_names_whatever_its_value_looks_like);

    private void A_declaration_in_a_rule_is_offered_no_names_whatever_its_value_looks_like(string css)
    {
        Assert.Null(ScopedCss.SelectorAt(css, css.Length));
    }
}
