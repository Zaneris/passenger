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
    public void An_apps_tree_crosses_the_wire_flat_as_it_was_and_one_whose_counts_do_not_add_up_is_refused() => Cases.All(
        ("an app tree nested past what a stack holds crosses the wire whole on a small stack", An_app_tree_nested_past_what_a_stack_holds_crosses_the_wire_whole_on_a_small_stack),
        ("every elements words layout and values cross the wire as they were", Every_elements_words_layout_and_values_cross_the_wire_as_they_were),
        ("a tree whose counts do not add up does not read", A_tree_whose_counts_do_not_add_up_does_not_read));

    private void An_app_tree_nested_past_what_a_stack_holds_crosses_the_wire_whole_on_a_small_stack()
    {
        AppTree tree = new([Chain(100_000), Chain(3)]);
        AppTree? read = null;
        SmallStack.Run(() => read = Read(Sent(tree)));
        Assert.True(read is not null && Shape(read).SequenceEqual(Shape(tree)),
            "The tree read back was not the tree sent: the Live Visual Tree would show another app than the one running");
    }

    private void Every_elements_words_layout_and_values_cross_the_wire_as_they_were()
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
        Assert.Null(read.Bases);

        AppTree told = tree with { Bases = new Dictionary<string, IReadOnlyList<string>> { ["TreeList"] = ["ListBox"], ["Row"] = ["ListBoxItem", "Row"] } };
        Assert.Equal(told.Bases, Read(Sent(told))?.Bases);
    }

    private void A_tree_whose_counts_do_not_add_up_does_not_read()
    {
        string sent = System.Text.Encoding.UTF8.GetString(Sent(new AppTree([Chain(3)])));
        Assert.Contains("\"held\":[1,1,0]", sent, StringComparison.Ordinal);
        foreach (string wrong in new[] { "\"held\":[1,1]", "\"held\":[1,1,1]", "\"held\":[1,-1,0]", "\"held\":[1,\"one\",0]" })
        {
            Assert.Throws<JsonException>(() => Read(System.Text.Encoding.UTF8.GetBytes(sent.Replace("\"held\":[1,1,0]", wrong, StringComparison.Ordinal))));
        }
    }
}
