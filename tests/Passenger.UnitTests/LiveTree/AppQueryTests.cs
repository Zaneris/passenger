using Passenger.App.LiveTree;
using Passenger.Inspector;

namespace Passenger.UnitTests.LiveTree;

/// <summary>An app's tree as an agent's tools and the Live Visual Tree walk it: each walk finishing on a small stack
/// however deep the tree, and a query finding the outermost matches of each step, by a type what derives from
/// it.</summary>
public sealed class AppQueryTests
{
    /// <summary>Elements each holding the next, the first outermost.</summary>
    private static Element Chain(int deep, string type = "Border")
    {
        Element at = new(deep - 1, type, $"Link{deep - 1}", [], false, false, true, []);
        for (int i = deep - 2; i >= 0; i--)
        {
            at = new Element(i, type, $"Link{i}", [], false, false, true, [at]);
        }
        return at;
    }

    [Fact]
    public void An_apps_tree_is_walked_on_a_small_stack_however_deep_and_queried_step_by_step_and_by_type() => Cases.All(
        ("every walk of a tree nested past what a stack holds finishes on a small stack", Every_walk_of_a_tree_nested_past_what_a_stack_holds_finishes_on_a_small_stack),
        ("a query finds the outermost matches inside what its last step found", A_query_finds_the_outermost_matches_inside_what_its_last_step_found),
        ("a query by a type finds an element of a type derived from it", A_query_by_a_type_finds_an_element_of_a_type_derived_from_it));

    private void Every_walk_of_a_tree_nested_past_what_a_stack_holds_finishes_on_a_small_stack()
    {
        const int Deep = 100_000;
        Element root = Chain(Deep);
        List<AppNode> nodes = [];
        List<Element>? path = null;
        (bool Itself, bool Other) same = default;
        string written = string.Empty;
        SmallStack.Run(() =>
        {
            nodes = AppQuery.Flatten([root]);
            path = AppQuery.Path([root], Deep - 1);
            same = (AppQuery.Same([root], [Chain(Deep)]), AppQuery.Same([root], [Chain(Deep, "Grid")]));
            written = AppQuery.Write([nodes[0]], null, false, false);
        });
        Assert.Equal(Deep, nodes.Count);
        Assert.True(ReferenceEquals(nodes[^1].Parent, nodes[^2]) && ReferenceEquals(nodes[^1].Window, root) && nodes[^1].Shows,
            "The deepest element's node did not name its parent and window");
        Assert.Equal([.. Enumerable.Range(0, Deep).Select(i => (long)i)], path?.Select(e => e.Id) ?? []);
        Assert.Equal((true, false), same);
        string[] lines = written.Split('\n');
        Assert.Equal(AppQuery.MostLines + 1, lines.Length);
        Assert.Equal($"{new string(' ', 2 * (AppQuery.MostLines - 1))}Border#Link{AppQuery.MostLines - 1} @{AppQuery.MostLines - 1}", lines[^2]);
        Assert.Equal($"... and {Passenger.Core.Numbers.Said(Deep - AppQuery.MostLines)} more elements: narrow it with control, filter or depth", lines[^1]);
    }

    private void A_query_finds_the_outermost_matches_inside_what_its_last_step_found()
    {
        Element save = new(4, "Button", "Save", [], false, false, true, []) { Text = "Save" };
        Element inner = new(3, "Border", "Inner", [], false, false, true, [save]);
        Element outer = new(2, "Border", "Outer", [], false, false, true, [inner]);
        Element other = new(5, "Button", "Other", [], false, false, true, []);
        List<AppNode> nodes = AppQuery.Flatten([new Element(1, "Window", null, [], false, false, true, [outer, other])]);
        Assert.Equal(["Outer"], AppQuery.Find(nodes, "Border").Select(n => n.Element.Name));
        Assert.Equal(["Save"], AppQuery.Find(nodes, "#Inner/Button").Select(n => n.Element.Name));
        Assert.Equal(["Save", "Other"], AppQuery.Find(nodes, "Button").Select(n => n.Element.Name));
        Assert.Equal("Button#Save \"Save\" @4 in Border#Inner @3 in Border#Outer @2 in Window @1", AppQuery.Where(AppQuery.One(nodes, "\"Save\"")));
    }

    /// <summary>A query by a type finds an element of a type derived from it as of its own, however far down, by what the
    /// tree tells each type derives from directly; a tree that tells nothing finds each by its own type alone.</summary>
    private void A_query_by_a_type_finds_an_element_of_a_type_derived_from_it()
    {
        Element lib = new(4, "TextBlock", null, [], false, false, true, []) { Text = "Lib" };
        Element tree = new(3, "TreeList", "Rows", [], false, false, true, [lib]);
        Element plain = new(2, "ListBox", "Plain", [], false, false, true, []);
        Element window = new(1, "Window", null, [], false, false, true, [plain, tree]);
        Dictionary<string, IReadOnlyList<string>> bases = new()
        {
            ["TreeList"] = ["ListBox"],
            ["ListBox"] = ["SelectingItemsControl"],
            ["SelectingItemsControl"] = ["ItemsControl"],
            ["Window"] = ["ContentControl"],
        };
        List<AppNode> nodes = AppQuery.Flatten(new AppTree([window]) { Bases = bases });

        Assert.Equal(4, AppQuery.One(nodes, "ListBox#Rows/\"Lib\"").Element.Id);
        Assert.Equal([2L, 3L], AppQuery.Find(nodes, "ListBox").Select(n => n.Element.Id));
        Assert.Equal([2L, 3L], AppQuery.Find(nodes, "itemscontrol").Select(n => n.Element.Id));
        Assert.Equal([3L], AppQuery.Find(nodes, "TreeList").Select(n => n.Element.Id));
        Assert.Empty(AppQuery.Find(AppQuery.Flatten([window]), "ListBox#Rows"));
    }

    private class Base;

    private class Middle : Base;

    private static class Other
    {
        public sealed class Middle : AppQueryTests.Middle;
    }
}
