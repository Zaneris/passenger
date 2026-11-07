using Passenger.App.LiveTree;
using Passenger.Inspector;

namespace Passenger.UnitTests.LiveTree;

/// <summary>An app's tree as an agent's tools and the Live Visual Tree walk it: each walk finishing on a small stack however
/// deep the tree, an element that holds more than was read saying so wherever the tree is written, and two trees told
/// apart by where each element is.</summary>
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
    public void Every_walk_of_a_tree_nested_past_what_a_stack_holds_finishes_on_a_small_stack()
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
        Assert.Equal($"... and {Deep - AppQuery.MostLines} more elements: narrow it with control, filter or depth", lines[^1]);
    }

    [Fact]
    public void An_element_that_holds_more_than_was_read_says_so_wherever_the_tree_is_written()
    {
        Element deeper = new(3, "Border", null, [], false, false, true, []) { Deeper = true };
        Element panel = new(2, "StackPanel", "Outer", [], false, false, true, [deeper]);
        Element window = new(1, "Window", null, [], false, false, true, [panel]);
        string note = $"... more inside, not read: the tree is read {Element.DeepestNesting} levels deep";

        Assert.Equal($"Window @1\n  StackPanel#Outer @2\n    Border @3\n      {note}", AppQuery.Write(AppQuery.Flatten([window]).Take(1), null, false, false));
        Assert.Equal("Window @1\n  ... at least 2 inside", AppQuery.Write(AppQuery.Flatten([window]).Take(1), 0, false, false));
        Element hidden = window with { Children = [panel with { Visible = false }] };
        Assert.Equal("Window @1\n  StackPanel#Outer @2 [hidden] (at least 1 inside)", AppQuery.Write(AppQuery.Flatten([hidden]).Take(1), null, false, false));
        // A template's part is not written, so what it holds, and its note, come up to its level.
        Element templated = window with { Children = [panel with { Children = [deeper with { FromTemplate = true }] }] };
        Assert.Equal($"Window @1\n  StackPanel#Outer @2\n    {note}", AppQuery.Write(AppQuery.Flatten([templated]).Take(1), null, false, false));
    }

    [Fact]
    public void Two_trees_holding_the_same_elements_in_other_places_are_not_the_same()
    {
        Element Leaf(long id) => new(id, "TextBlock", null, [], false, false, true, []);
        Element side = new(1, "StackPanel", null, [], false, false, true, [Leaf(2), Leaf(3)]);
        Element under = new(1, "StackPanel", null, [], false, false, true, [Leaf(2) with { Children = [Leaf(3)] }]);
        Assert.True(AppQuery.Same([side], [side with { }]), "A tree was not the same as its copy");
        Assert.False(AppQuery.Same([side], [under]), "Two trees with the same elements in other places read as the same");
        Assert.False(AppQuery.Same([side], [side, Leaf(4)]), "A tree with a window more read as the same");
        Assert.False(AppQuery.Same([side], [side with { Children = [Leaf(2), Leaf(3) with { Deeper = true }] }]), "An element that came to hold more than was read read as the same");
    }

    [Fact]
    public void A_query_finds_the_outermost_matches_inside_what_its_last_step_found()
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
}
