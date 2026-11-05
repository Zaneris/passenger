using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>When Passenger connects again to the Godot editor a project's IDE messaging meta file names: an editor that
/// refused its first handshake (busy importing as it opens) is passed over for ten seconds and then tried again, one
/// connected is kept while it stays open, and a meta file that changed is connected to at once.</summary>
public class GodotIdeRetryTests
{
    private const string Meta = "6010\nGodot Editor\n";

    [Fact]
    public void An_editor_that_refused_its_first_handshake_is_tried_again_once_ten_seconds_have_passed()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), GodotIdeRetry.Again);
        Assert.True(GodotIdeRetry.Stands(Meta, answered: false, open: false, TimeSpan.FromSeconds(9.9), Meta), "An editor that refused a moment ago was tried again at once");
        Assert.False(GodotIdeRetry.Stands(Meta, answered: false, open: false, TimeSpan.FromSeconds(10), Meta), "An editor that refused ten seconds ago was never tried again");
        Assert.False(GodotIdeRetry.Stands(Meta, answered: false, open: false, TimeSpan.FromSeconds(1), "6011\nGodot Editor\n"), "A new meta file waited out the last editor's refusal");
    }

    [Fact]
    public void A_connected_editor_is_kept_while_open_and_connected_to_again_once_closed()
    {
        Assert.True(GodotIdeRetry.Stands(Meta, answered: true, open: true, TimeSpan.FromMinutes(5), Meta), "An open connection was made again");
        Assert.False(GodotIdeRetry.Stands(Meta, answered: true, open: false, TimeSpan.FromSeconds(1), Meta), "A closed connection was kept");
        Assert.False(GodotIdeRetry.Stands(null, answered: false, open: false, TimeSpan.Zero, Meta), "An editor that came was waited for as one never found");
    }
}
