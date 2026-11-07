using Passenger.App.Aspire;

namespace Passenger.UnitTests.Aspire;

/// <summary>An AppHost's words that the port chosen for its resource service was taken as it bound it are heard as they are
/// written, whichever loopback it bound and however the line begins, so its start is tried again on another port; words of
/// another port, another host or another failure are not.</summary>
public sealed class TakenPortsTests
{
    [Theory]
    [InlineData("Unhandled exception. System.IO.IOException: Failed to bind to address http://127.0.0.1:52341: address already in use.")]
    [InlineData("      System.IO.IOException: Failed to bind to address https://127.0.0.1:52341: address already in use.")]
    [InlineData("Unhandled exception. System.IO.IOException: Failed to bind to address http://[::1]:52341: address already in use.")]
    public void The_servers_words_of_the_chosen_port_taken_at_loopback_are_heard(string line)
    {
        Assert.True(TakenPorts.SaidIn(line, 52341), $"\"{line}\" was not heard as port 52341 taken, so the AppHost's start would fail where it could start again on another");
    }

    [Theory]
    [InlineData("Unhandled exception. System.IO.IOException: Failed to bind to address http://127.0.0.1:52342: address already in use.")]
    [InlineData("Unhandled exception. System.IO.IOException: Failed to bind to address http://10.0.0.5:52341: address already in use.")]
    [InlineData("Unhandled exception. System.IO.IOException: Failed to bind to address http://127.0.0.1:52341.")]
    [InlineData("info: Aspire.Hosting.DistributedApplication[0] Now listening on: http://127.0.0.1:52341")]
    [InlineData("Failed to bind to address : address already in use.")]
    public void Words_of_another_port_host_or_failure_are_not(string line)
    {
        Assert.False(TakenPorts.SaidIn(line, 52341), $"\"{line}\" was heard as port 52341 taken, so an AppHost that failed for its own reason would be started again");
    }
}
