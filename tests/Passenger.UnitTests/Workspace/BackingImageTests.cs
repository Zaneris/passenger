using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A compose service's backing kind is read from its image's repository whatever its tag, so a compose file
/// written before the backing images moved a major (postgres:17, redis:7, SQL Server 2022) is read as the kinds it holds,
/// beside one written since.</summary>
public sealed class BackingImageTests
{
    [Theory]
    [InlineData("postgres:17", "postgres")]
    [InlineData("postgres:18", "postgres")]
    [InlineData("docker.io/library/redis:7", "redis")]
    [InlineData("redis:8", "redis")]
    [InlineData("mcr.microsoft.com/mssql/server:2022-latest", "sqlserver")]
    [InlineData("mcr.microsoft.com/mssql/server:2025-latest", "sqlserver")]
    public void A_backing_image_of_any_major_is_read_as_its_kind(string image, string service)
    {
        Assert.True(BackingService.OfImage(image)?.Service == service,
            $"{image} was read as {BackingService.OfImage(image)?.Service ?? "no backing service"}, so the Migrations window and the app's connections would not know it for {service}");
    }
}
