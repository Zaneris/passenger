using System.Text.Json;
using Passenger.Inspector;

namespace Passenger.UnitTests.LiveTree;

/// <summary>An app's tree as the inspector's wire carries it and as both ends walk it: flat, so neither end's
/// System.Text.Json walks deeper than an element on its thread's stack, however deep the tree; every element's words,
/// layout and values as they were; and a tree whose counts do not add up refused as a line that does not read.</summary>
public sealed class AppTreeWireTests
{
    /// <summary>Elements each holding the next, the first outermost, the last holding <paramref name="last"/>.</summary>
    private static Element Chain(int deep, IReadOnlyList<Element>? last = null)
    {
        Element at = new(deep - 1, "Border", $"Link{deep - 1}", [], false, false, true, last ?? []);
        for (int i = deep - 2; i >= 0; i--)
        {
            at = new Element(i, "Border", $"Link{i}", [], false, false, true, [at]);
        }
        return at;
    }

    private static byte[] Sent(AppTree tree) => JsonSerializer.SerializeToUtf8Bytes(new Reply { Id = 7, Tree = tree }, WireJson.Default.Reply);

    private static AppTree? Read(byte[] line) => JsonSerializer.Deserialize(line, WireJson.Default.Reply)?.Tree;

    /// <summary>Where each element is: its id, depth and how many it holds, in tree order.</summary>
    private static List<(long Id, int Depth, int Held)> Shape(AppTree tree) =>
        [.. AppTree.Walk(tree.Roots).Select(walked => (walked.Element.Id, walked.Depth, walked.Element.Children.Count))];

    /// <summary>Each element's own JSON, what it holds left out, in tree order.</summary>
    private static List<string> Each(AppTree tree) =>
        [.. AppTree.Walk(tree.Roots).Select(walked => JsonSerializer.Serialize(walked.Element with { Children = [] }, WireJson.Default.Element))];

    [Fact]
    public void An_app_tree_nested_past_what_a_stack_holds_crosses_the_wire_whole_on_a_small_stack()
    {
        AppTree tree = new([Chain(100_000), Chain(3)]);
        AppTree? read = null;
        SmallStack.Run(() => read = Read(Sent(tree)));
        Assert.True(read is not null && Shape(read).SequenceEqual(Shape(tree)),
            "The tree read back was not the tree sent: the Live Visual Tree would show another app than the one running");
    }

    [Fact]
    public void An_app_tree_on_the_wire_nests_no_deeper_than_an_element_however_deep_the_tree()
    {
        Utf8JsonReader reader = new(Sent(new AppTree([Chain(300)])));
        int deepest = 0;
        while (reader.Read())
        {
            deepest = Math.Max(deepest, reader.CurrentDepth);
        }
        Assert.True(deepest <= 6, $"A tree 300 deep nested {deepest} deep on the wire, which System.Text.Json walks on its thread's stack at either end");
    }

    [Fact]
    public void Every_elements_words_layout_and_values_cross_the_wire_as_they_were()
    {
        Element label = new(3, "TextBlock", "Greeting", ["title", "big"], true, true, false, [])
        {
            Bounds = new Box(1.5, 2, 30, 12.25),
            Text = "Hello, \"Ada\"",
            Disabled = true,
            Focused = true,
            OutOfView = true,
            Values = new Dictionary<string, string> { ["Grid.Row"] = "2", ["IsEnabled"] = "False" },
        };
        Element held = new(4, "Border", null, [], false, false, true, []) { Deeper = true };
        AppTree tree = new([
            new Element(1, "Window", "Main", [], false, true, true, [new Element(2, "StackPanel", null, [], false, false, true, [label, held])]) { Text = "Main" },
            new Element(5, "PopupRoot", null, [], false, false, true, []),
        ]);
        AppTree? read = Read(Sent(tree));
        Assert.NotNull(read);
        Assert.Equal(Each(tree), Each(read));
        Assert.Equal(Shape(tree), Shape(read));
    }

    [Fact]
    public void A_tree_whose_counts_do_not_add_up_does_not_read()
    {
        string sent = System.Text.Encoding.UTF8.GetString(Sent(new AppTree([Chain(3)])));
        Assert.Contains("\"held\":[1,1,0]", sent, StringComparison.Ordinal);
        foreach (string wrong in new[] { "\"held\":[1,1]", "\"held\":[1,1,1]", "\"held\":[1,-1,0]", "\"held\":[1,\"one\",0]" })
        {
            Assert.Throws<JsonException>(() => Read(System.Text.Encoding.UTF8.GetBytes(sent.Replace("\"held\":[1,1,0]", wrong, StringComparison.Ordinal))));
        }
    }

    [Fact]
    public void A_walk_goes_each_element_before_what_it_holds_at_its_depth_into_only_what_it_is_let_into()
    {
        Element first = new(3, "TextBlock", "First", [], false, false, true, []);
        Element panel = new(2, "StackPanel", "Panel", [], false, false, true, [first, new Element(4, "TextBlock", "Second", [], false, false, true, [])]);
        Element closed = new(6, "Border", "Closed", [], false, false, true, [new Element(7, "TextBlock", "Inside", [], false, false, true, [])]);
        Element window = new(1, "Window", "Main", [], false, false, true, [panel, new Element(5, "TextBlock", "Status", [], false, false, true, []), closed]);
        Assert.Equal(["Main 0 True", "Panel 1 True", "First 2 False", "Second 2 False", "Status 1 False", "Closed 1 True", "Inside 2 False"],
            AppTree.Walk([window]).Select(walked => $"{walked.Element.Name} {walked.Depth} {walked.Open}"));
        List<(Element, int)> asked = [];
        Assert.Equal(["Panel 4 True", "First 5 False", "Second 5 False", "Status 4 False", "Closed 4 False"],
            AppTree.Walk(window.Children, 4, (element, depth) =>
            {
                asked.Add((element, depth));
                return element != closed;
            }).Select(walked => $"{walked.Element.Name} {walked.Depth} {walked.Open}"));
        Assert.Equal([(panel, 4), (closed, 4)], asked);
    }
}
