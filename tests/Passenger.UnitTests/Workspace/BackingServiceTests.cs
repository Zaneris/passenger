using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A connection string's host is this machine's, which in a container is the container itself, where it is a
/// loopback address, localhost or a name under it, in each shape a connection string takes; a name that only begins with
/// localhost, another machine's and a file are not.</summary>
public sealed class BackingServiceTests
{
    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=shop", "localhost")]
    [InlineData("Host=db.dev.localhost;Port=5432;Database=shop", "db.dev.localhost")]
    [InlineData("Server=tcp:127.0.0.1,1433;Database=shop", "127.0.0.1")]
    [InlineData("Server=LOCALHOST\\SQLEXPRESS;Database=shop", "LOCALHOST")]
    [InlineData("Data Source=[::1]:5432", "::1")]
    [InlineData("mongodb://cache.dev.localhost:27017/shop", "cache.dev.localhost")]
    [InlineData("cache.localhost:6379,abortConnect=false", "cache.localhost")]
    [InlineData("Address=::1", "::1")]
    public void A_connection_strings_host_on_this_machine_is_named(string value, string host)
    {
        Assert.True(BackingService.LocalHost(value) == host,
            $"{value} was not read as reaching this machine at {host}, so a container running its app would not be told it reaches itself: {BackingService.LocalHost(value) ?? "null"}");
    }

    [Theory]
    [InlineData("Host=localhost.example.com;Port=5432")]
    [InlineData("Host=db;Port=5432")]
    [InlineData("Server=10.0.0.5,1433")]
    [InlineData("Data Source=app.db")]
    [InlineData("redis.example.com:6379")]
    public void Another_machines_host_or_a_file_is_not(string value)
    {
        Assert.True(BackingService.LocalHost(value) is null, $"{value} was read as reaching this machine at {BackingService.LocalHost(value)}");
    }
}
