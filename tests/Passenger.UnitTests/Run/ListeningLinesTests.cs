using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>What a program's output says of its serving: each address its host listens at, once, in the order said,
/// whatever logger wrote the line, and its host's start; other lines say nothing.</summary>
public class ListeningLinesTests
{
    [Fact]
    public void The_console_loggers_lines_give_each_address_and_the_hosts_start()
    {
        ListeningLines heard = new();
        foreach (string line in new[]
        {
            "info: Microsoft.Hosting.Lifetime[14]",
            "      Now listening on: http://127.0.0.1:5123",
            "info: Microsoft.Hosting.Lifetime[14]",
            "      Now listening on: https://localhost:7123",
            "info: Microsoft.Hosting.Lifetime[0]",
        })
        {
            heard.Hear(line);
        }
        Assert.Equal(["http://127.0.0.1:5123", "https://localhost:7123"], heard.Addresses);
        Assert.False(heard.HostStarted, "The host was taken to have started before it said so, so an agent is answered before every address is heard");
        heard.Hear("      Application started. Press Ctrl+C to shut down.");
        Assert.True(heard.HostStarted, "The host's start was not heard, so an agent waits out its time for a web app that serves");
    }

    [Theory]
    [InlineData("[12:00:01 INF] Now listening on: http://[::]:8080", "http://[::]:8080")]
    [InlineData("\u001b[40m\u001b[32minfo\u001b[39m\u001b[22m\u001b[49m: Now listening on: http://localhost:5000\u001b[0m", "http://localhost:5000")]
    [InlineData("{\"Message\":\"Now listening on: http://localhost:5000\",\"State\":{\"address\":\"http://localhost:5000\"}}", "http://localhost:5000")]
    public void An_address_is_heard_in_any_loggers_format_up_to_where_it_ends(string line, string address)
    {
        ListeningLines heard = new();
        heard.Hear(line);
        Assert.Equal([address], heard.Addresses);
    }

    [Fact]
    public void An_address_said_twice_is_heard_once_and_other_lines_say_nothing()
    {
        ListeningLines heard = new();
        heard.Hear("Now listening on: http://localhost:5000");
        heard.Hear("Now listening on: http://localhost:5000");
        heard.Hear("Distributed application started. Press Ctrl+C to shut down.");
        heard.Hear("Hosting environment: Development");
        Assert.Equal(["http://localhost:5000"], heard.Addresses);
        Assert.False(heard.HostStarted, "Another line was taken for the host's start");
    }
}
