using Passenger.App.Editors;

namespace Passenger.UnitTests.Editors;

/// <summary>The order the Switcher lists tabs in: the one that came forward last first, one gone left out, and those never
/// used after the used ones in the order given.</summary>
public sealed class UsedOrderTests
{
    [Fact]
    public void What_came_forward_last_comes_first_and_coming_again_moves_it_up()
    {
        UsedOrder<string> order = new();
        order.Came("one");
        order.Came("two");
        order.Came("three");
        Assert.Equal(["three", "two", "one"], order.Of(["one", "two", "three"]));
        order.Came("one");
        Assert.Equal(["one", "three", "two"], order.Of(["one", "two", "three"]));
    }

    [Fact]
    public void One_gone_or_no_longer_open_is_left_out_and_never_used_ones_follow_in_the_order_given()
    {
        UsedOrder<string> order = new();
        order.Came("one");
        order.Came("two");
        order.Came("three");
        order.Went("three");
        Assert.Equal(["two", "one"], order.Of(["one", "two"]));
        Assert.Equal(["two", "one", "four", "five"], order.Of(["four", "one", "five", "two"]));
        order.Keep(item => item != "two");
        Assert.Equal(["one", "two"], order.Of(["two", "one"]));
    }
}
