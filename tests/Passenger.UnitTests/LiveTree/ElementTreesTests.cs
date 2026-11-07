using Passenger.Inspector;

namespace Passenger.UnitTests.LiveTree;

/// <summary>An app's tree made into the wire's elements inside the app, and walked there, as both inspectors do: as deep as
/// the tree is read, on a small stack, the element there saying it holds more; each element told where its holder is,
/// and the framework's order kept. An app's code can nest its elements as deep as it likes, and a walk that ran out of
/// stack would end the person's app.</summary>
public sealed class ElementTreesTests
{
    /// <summary>An element of a framework's tree, as an app's code nests them.</summary>
    private sealed class Part(string name)
    {
        public string Name { get; } = name;

        public List<Part> Held { get; } = [];
    }

    /// <summary>Elements each holding the next, the first outermost.</summary>
    private static Part Chain(int deep)
    {
        Part root = new("Link0");
        Part at = root;
        for (int i = 1; i < deep; i++)
        {
            Part next = new($"Link{i}");
            at.Held.Add(next);
            at = next;
        }
        return root;
    }

    /// <summary>The wire's element of a part: its name as its type, and where its holder said it is as its id.</summary>
    private static Element Made(Part root) =>
        ElementTrees.Made(root, 0, part => part.Held, (_, holder) => holder + 1, (part, depth, children) => new Element(depth, part.Name, null, [], false, false, true, children));

    [Fact]
    public void A_tree_nested_past_what_a_stack_holds_is_made_to_its_bound_on_a_small_stack_the_element_there_saying_it_holds_more()
    {
        Part root = Chain(100_000);
        Element? made = null;
        SmallStack.Run(64 * 1024, () => made = Made(root));
        List<Element> all = [.. AppTree.Walk([made!]).Select(walked => walked.Element)];
        Assert.Equal(Element.DeepestNesting, all.Count);
        Assert.Equal([.. Enumerable.Range(0, Element.DeepestNesting).Select(i => $"Link{i}")], all.Select(e => e.Type));
        Assert.Equal([.. Enumerable.Range(1, Element.DeepestNesting).Select(i => (long)i)], all.Select(e => e.Id));
        Assert.True(all[^1] is { Children: [], Deeper: true } && all.Take(all.Count - 1).All(e => e.Deeper is null),
            "The element at the bound did not say it holds more, or another did: the Live Visual Tree and app_tree would not say what they left out");
    }

    [Fact]
    public void A_tree_as_deep_as_the_bound_is_made_whole_and_says_it_holds_nothing_more()
    {
        List<Element> all = [.. AppTree.Walk([Made(Chain(Element.DeepestNesting))]).Select(walked => walked.Element)];
        Assert.Equal(Element.DeepestNesting, all.Count);
        Assert.True(all.All(e => e.Deeper is null), "A tree no deeper than the bound said it holds more than was read");
        List<Element> past = [.. AppTree.Walk([Made(Chain(Element.DeepestNesting + 1))]).Select(walked => walked.Element)];
        Assert.True(past.Count == Element.DeepestNesting && past[^1].Deeper == true, "A tree one level past the bound did not say so at the bound");
    }

    [Fact]
    public void Each_element_is_made_in_the_frameworks_order_from_where_its_holder_is()
    {
        Part window = new("Window");
        Part panel = new("Panel");
        Part first = new("First");
        Part second = new("Second");
        Part status = new("Status");
        window.Held.AddRange([panel, status]);
        panel.Held.AddRange([first, second]);
        List<string> placed = [];
        Element made = ElementTrees.Made(window, string.Empty, part => part.Held, (part, holder) =>
        {
            placed.Add(part.Name);
            return $"{holder}/{part.Name}";
        }, (_, place, children) => new Element(0, place, null, [], false, false, true, children));
        Assert.Equal(["/Window", "/Window/Panel", "/Window/Panel/First", "/Window/Panel/Second", "/Window/Status"],
            AppTree.Walk([made]).Select(walked => walked.Element.Type));
        Assert.Equal(["Window", "Panel", "First", "Second", "Status"], placed);
    }

    [Fact]
    public void What_an_element_holds_is_walked_on_a_small_stack_each_before_what_it_holds_as_the_frameworks_own_walk_goes()
    {
        Part window = new("Window");
        Part panel = new("Panel");
        panel.Held.AddRange([new Part("First"), new Part("Second")]);
        window.Held.AddRange([panel, new Part("Status")]);
        List<string> nested = [];
        Nested(window);
        Assert.Equal(nested, ElementTrees.Below(window, part => part.Held).Select(part => part.Name));

        Part deep = Chain(100_000);
        int count = 0;
        string? last = null;
        SmallStack.Run(64 * 1024, () =>
        {
            foreach (Part part in ElementTrees.Below(deep, part => part.Held))
            {
                count++;
                last = part.Name;
            }
        });
        Assert.Equal((99_999, "Link99999"), (count, last));

        void Nested(Part part)
        {
            foreach (Part held in part.Held)
            {
                nested.Add(held.Name);
                Nested(held);
            }
        }
    }
}
