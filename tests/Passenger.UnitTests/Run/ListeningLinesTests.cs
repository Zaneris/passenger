using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>What a program's output says of its serving: each address its host listens at, once, in the order said,
/// whatever logger wrote the line, and its host's start; other lines say nothing.</summary>
public class ListeningLinesTests
{
    [Fact]
    public void An_apps_addresses_are_heard_once_from_its_logs_in_any_format() => Cases.All(
        ("the console loggers lines give each address and the hosts start", The_console_loggers_lines_give_each_address_and_the_hosts_start),
        ("an address is heard in any loggers format up to where it ends", () => Cases.Each<(string, string)>(
        [
            ("\u001b[40m\u001b[32minfo\u001b[39m\u001b[22m\u001b[49m: Now listening on: http://localhost:5000\u001b[0m", "http://localhost:5000"),
            ("{\"Message\":\"Now listening on: http://localhost:5000\",\"State\":{\"address\":\"http://localhost:5000\"}}", "http://localhost:5000"),
        ], row => An_address_is_heard_in_any_loggers_format_up_to_where_it_ends(row.Item1, row.Item2))),
        ("an address said twice is heard once and other lines say nothing", An_address_said_twice_is_heard_once_and_other_lines_say_nothing));

    private void The_console_loggers_lines_give_each_address_and_the_hosts_start()
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

    private void An_address_is_heard_in_any_loggers_format_up_to_where_it_ends(string line, string address)
    {
        ListeningLines heard = new();
        heard.Hear(line);
        Assert.Equal([address], heard.Addresses);
    }

    private void An_address_said_twice_is_heard_once_and_other_lines_say_nothing()
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
