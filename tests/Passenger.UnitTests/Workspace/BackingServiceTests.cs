using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A connection string's host is this machine's, which in a container is the container itself, where it is a
/// loopback address, localhost or a name under it, in each shape a connection string takes; a name that only begins with
/// localhost, another machine's and a file are not.</summary>
public sealed class BackingServiceTests
{
    [Fact]
    public void A_connection_strings_host_is_this_machines_where_it_is_loopback_or_localhost_in_every_shape_and_no_other() => Cases.All(
        ("a connection strings host on this machine is named", () => Cases.Each<(string, string)>(
        [
            ("Host=localhost;Port=5432;Database=shop", "localhost"),
            ("Server=tcp:127.0.0.1,1433;Database=shop", "127.0.0.1"),
            ("Server=LOCALHOST\\SQLEXPRESS;Database=shop", "LOCALHOST"),
            ("Data Source=[::1]:5432", "::1"),
            ("mongodb://cache.dev.localhost:27017/shop", "cache.dev.localhost"),
            ("cache.localhost:6379,abortConnect=false", "cache.localhost"),
        ], row => A_connection_strings_host_on_this_machine_is_named(row.Item1, row.Item2))),
        ("another machines host or a file is not", () => Cases.Each<string>(
        [
            "Host=localhost.example.com;Port=5432",
            "Host=db;Port=5432",
            "Data Source=app.db",
        ], Another_machines_host_or_a_file_is_not)));

    private void A_connection_strings_host_on_this_machine_is_named(string value, string host)
    {
        Assert.True(BackingService.LocalHost(value) == host,
            $"{value} was not read as reaching this machine at {host}, so a container running its app would not be told it reaches itself: {BackingService.LocalHost(value) ?? "null"}");
    }

    private void Another_machines_host_or_a_file_is_not(string value)
    {
        Assert.True(BackingService.LocalHost(value) is null, $"{value} was read as reaching this machine at {BackingService.LocalHost(value)}");
    }
}
