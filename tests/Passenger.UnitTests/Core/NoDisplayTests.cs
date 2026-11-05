using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A program's variables with no display in them, as a Passenger nobody sees sets them over what it starts on Linux.</summary>
public class NoDisplayTests
{
    /// <summary>Taking WAYLAND_DISPLAY out would not do: a Wayland client named no socket connects to the session's wayland-0.</summary>
    [Fact]
    public void Variables_over_a_program_name_no_x_server_and_no_compositors_socket_and_leave_the_rest()
    {
        Dictionary<string, string> variables = new() { [NoDisplay.X11] = ":0", [NoDisplay.Wayland] = "wayland-0", ["WAYLAND_SOCKET"] = "9", ["KEPT"] = "yes" };

        NoDisplay.Over(variables);

        Assert.Equal(new Dictionary<string, string> { [NoDisplay.Wayland] = NoDisplay.Nowhere, ["KEPT"] = "yes" }, variables);
        Assert.DoesNotMatch(@"^wayland-\d+$", NoDisplay.Nowhere);
    }

    [Fact]
    public void Variables_naming_no_display_are_given_a_socket_of_nothing_all_the_same()
    {
        Dictionary<string, string> variables = [];

        NoDisplay.Over(variables);

        Assert.Equal(new Dictionary<string, string> { [NoDisplay.Wayland] = NoDisplay.Nowhere }, variables);
    }
}
