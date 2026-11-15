using Passenger.Inspector;
using Passenger.Inspector.Maui;

namespace Passenger.UnitTests.LiveTree;

/// <summary>Where the MAUI inspector says a MAUI app's elements are, from its platform's numbers alone: a view laid out in
/// pixels as a box in the window's units, a tap on the screen as a point of the window, a point as the pixel a tap is given
/// at, and the element a finger at a point reaches. A box off by the window's place on the screen, or a tap given to the
/// wrong element, would send an agent's click to another control than the one it named.</summary>
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
    public void A_view_laid_out_in_pixels_is_a_box_in_the_windows_units_to_a_hundredth()
    {
        Assert.Equal(new Box(30.1, 468.95, 351.62, 44.19), Placing.InUnits(79, 1231, 923, 116, 2.625));
        Assert.Equal(new Box(0, 0, 411.43, 731.43), Placing.InUnits(0, 0, 1080, 1920, 2.625));
        Assert.Equal(new Box(5, 6, 7, 8), Placing.InUnits(5, 6, 7, 8, 0));
    }

    [Fact]
    public void A_tap_on_the_screen_is_a_point_of_the_window_less_where_the_window_is_on_the_screen()
    {
        // The overlay's view is 63 pixels down the screen and 0 down its window: the window starts below the status bar.
        (double x, double y) = Placing.InWindow(100, 200, (0, 63), (0, 0), 2.625);
        Assert.Equal((100.0, 176.0), (x, y));
        Assert.Equal((100.0, 200.0), Placing.InWindow(100, 200, (10, 20), (10, 20), 2.625));
    }

    [Fact]
    public void A_point_of_the_window_is_tapped_at_its_pixel()
    {
        Assert.Equal((540.49f, 1287.56f), Placing.Pixel(205.9, 490.5, 2.625), new FloatsNear(0.01f));
        Assert.Equal((3f, 4f), Placing.Pixel(3, 4, 0));
    }

    [Fact]
    public void A_finger_reaches_the_last_drawn_deepest_element_holding_the_point_passing_a_shells_items_through()
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

    [Fact]
    public void An_element_taking_no_input_is_passed_over_with_what_it_holds_and_an_edge_goes_to_the_element_after_it()
    {
        Part root = new Part("Page", new Box(0, 0, 400, 800)).With(
            new Part("Covering", new Box(0, 0, 400, 800), reached: false).With(new Part("Inside", new Box(0, 0, 100, 100))),
            new Part("Left", new Box(0, 200, 100, 50)),
            new Part("Right", new Box(100, 200, 100, 50)));
        Assert.Equal("Page", At(root, 50, 50));
        Assert.Equal("Right", At(root, 100, 220));
        Assert.Equal("Left", At(root, 99.99, 220));
        Assert.Equal("Page", At(root, 50, 250));
    }

    [Fact]
    public void A_tree_nested_past_what_a_stack_holds_is_walked_to_its_deepest_element_on_a_small_stack()
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

    /// <summary>Two numbers each equal to within a tolerance.</summary>
    private sealed class FloatsNear(float tolerance) : IEqualityComparer<(float X, float Y)>
    {
        public bool Equals((float X, float Y) a, (float X, float Y) b) => Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;

        public int GetHashCode((float X, float Y) obj) => 0;
    }
}
