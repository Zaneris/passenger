using Passenger.Inspector;
using Passenger.Inspector.Maui;

namespace Passenger.UnitTests.LiveTree;

/// <summary>Where the MAUI inspector says a MAUI app's elements are, from its platform's numbers alone: a view laid out
/// in pixels as a box in the window's units, and the element a finger at a point reaches. A box off by the window's
/// place on the screen, or a tap given to the wrong element, would send an agent's click to another control than the
/// one it named.</summary>
public sealed class MauiPlacingTests
{
    /// <summary>An element of a tree as a MAUI app's visual tree nests them: its box in the window (none for a shell's item)
    /// and whether a finger reaches it.</summary>
    private sealed class Part(string name, Box? box, bool reached = true)
    {
        public string Name { get; } = name;

        public Box? Box { get; } = box;

        public bool Reached { get; } = reached;

        public List<Part> Held { get; } = [];

        public Part With(params Part[] held)
        {
            Held.AddRange(held);
            return this;
        }
    }

    private static string? At(Part root, double x, double y) =>
        Placing.Deepest(root, part => part.Held, part => part.Box, part => part.Reached, x, y)?.Name;

    [Fact]
    public void A_MAUI_apps_views_are_placed_in_the_windows_units_a_finger_reaches_the_element_it_touches_and_a_deep_tree_is_walked_on_a_small_stack() => Cases.All(
        ("a view laid out in pixels is a box in the windows units to a hundredth", A_view_laid_out_in_pixels_is_a_box_in_the_windows_units_to_a_hundredth),
        ("a finger reaches the last drawn deepest element holding the point passing a shells items through", A_finger_reaches_the_last_drawn_deepest_element_holding_the_point_passing_a_shells_items_through),
        ("a tree nested past what a stack holds is walked to its deepest element on a small stack", A_tree_nested_past_what_a_stack_holds_is_walked_to_its_deepest_element_on_a_small_stack));

    private void A_view_laid_out_in_pixels_is_a_box_in_the_windows_units_to_a_hundredth()
    {
        Assert.Equal(new Box(30.1, 468.95, 351.62, 44.19), Placing.InUnits(79, 1231, 923, 116, 2.625));
        Assert.Equal(new Box(0, 0, 411.43, 731.43), Placing.InUnits(0, 0, 1080, 1920, 2.625));
        Assert.Equal(new Box(5, 6, 7, 8), Placing.InUnits(5, 6, 7, 8, 0));
    }

    private void A_finger_reaches_the_last_drawn_deepest_element_holding_the_point_passing_a_shells_items_through()
    {
        Part root = new Part("Window", new Box(0, 0, 400, 800)).With(
            new Part("Shell", new Box(0, 0, 400, 800)).With(
                new Part("ShellItem", null).With(
                    new Part("Page", new Box(0, 80, 400, 720)).With(
                        new Part("Stack", new Box(0, 80, 400, 720)).With(
                            new Part("Label", new Box(100, 300, 200, 40)),
                            new Part("Button", new Box(30, 460, 350, 44)).With(new Part("Glyph", new Box(40, 470, 20, 20))),
                            new Part("Over", new Box(300, 460, 80, 44)))))));
        Assert.Equal("Label", At(root, 150, 320));
        Assert.Equal("Button", At(root, 200, 480));
        Assert.Equal("Glyph", At(root, 45, 475));
        Assert.Equal("Over", At(root, 310, 480));
        Assert.Equal("Stack", At(root, 10, 700));
        Assert.Equal("Shell", At(root, 10, 40));
        Assert.Null(At(root, 500, 40));
    }

    private void A_tree_nested_past_what_a_stack_holds_is_walked_to_its_deepest_element_on_a_small_stack()
    {
        Part root = new("Link0", new Box(0, 0, 100, 100));
        Part at = root;
        for (int i = 1; i < 100_000; i++)
        {
            Part next = new($"Link{i}", new Box(0, 0, 100, 100));
            at.Held.Add(next);
            at = next;
        }
        string? hit = null;
        SmallStack.Run(64 * 1024, () => hit = At(root, 50, 50));
        Assert.Equal("Link99999", hit);
    }
}
