using System.Text;
using Passenger.App.Debugging;
using Passenger.Dap;

namespace Passenger.UnitTests.Dap;

/// <summary>An in-process debug adapter's events to its session (a Godot game's GDScript, a WebAssembly app's): each reaches the
/// session while the adapter runs; one whose write finds the adapter ended, as its end comes on another thread, is let go,
/// its session gone; one that fails of itself while the adapter runs is not.</summary>
public class InProcessEndTests
{
    [Fact]
    public async Task An_event_sent_as_the_adapter_ends_is_let_go_and_one_sent_before_reaches_the_session()
    {
        InProcessEnd end = new("adapter");
        DapConnection adapter = new(end.AdapterInput, end.AdapterOutput);
        end.SendEvent(adapter, DapEvents.Output, new Words("before"));
        byte[] buffer = new byte[4096];
        int read = await end.FromAdapter.ReadAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Contains("\"before\"", Encoding.UTF8.GetString(buffer, 0, read), StringComparison.Ordinal);

        end.SendEvent(adapter, DapEvents.Output, new Ending(end));
        Assert.True(end.HasEnded, "The event's body did not end the adapter as it was written");
        end.SendEvent(adapter, DapEvents.Terminated, null);
        Assert.Throws<IOException>(() => adapter.SendEvent(DapEvents.Terminated));
    }

    [Fact]
    public void A_write_that_fails_while_the_adapter_runs_is_not_let_go()
    {
        InProcessEnd end = new("adapter");
        DapConnection adapter = new(end.AdapterInput, end.AdapterOutput);
        Exception thrown = Assert.ThrowsAny<Exception>(() => end.SendEvent(adapter, DapEvents.Output, new Failing()));
        Assert.True((thrown as IOException ?? thrown.InnerException as IOException)?.Message == Failing.Said, $"The failure came as {thrown}");
        Assert.False(end.HasEnded);
    }

    private sealed record Words(string Text);

    /// <summary>A body whose writing ends the adapter: its end, come between the check that it runs and the write.</summary>
    private sealed class Ending(InProcessEnd end)
    {
        public string Text
        {
            get
            {
                end.End();
                return "after";
            }
        }
    }

    private sealed class Failing
    {
        public const string Said = "the disk is full";

        public string Text => throw new IOException(Said);
    }
}
