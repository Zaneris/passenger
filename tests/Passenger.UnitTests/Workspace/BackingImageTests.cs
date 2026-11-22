using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A compose service's backing kind is read from its image's repository whatever its tag, so a compose file
/// written before the backing images moved a major (postgres:17, redis:7, SQL Server 2022) is read as the kinds it holds,
/// beside one written since.</summary>
public sealed class BackingImageTests
{
    [Fact]
    public void A_backing_image_of_any_major_is_read_as_its_kind() => Cases.Each<(string, string)>(
        [
            ("postgres:17", "postgres"),
            ("postgres:18", "postgres"),
            ("redis:8", "redis"),
            ("mcr.microsoft.com/mssql/server:2022-latest", "sqlserver"),
        ], row => ReadsAs(row.Item1, row.Item2));

    private static void ReadsAs(string image, string service)
    {
        Assert.True(BackingService.OfImage(image)?.Service == service,
            $"{image} was read as {BackingService.OfImage(image)?.Service ?? "no backing service"}, so the Migrations window and the app's connections would not know it for {service}");
    }
}
