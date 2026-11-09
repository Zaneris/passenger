using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A size reads one way wherever it is said, in the IDE's digits whatever the person's culture: plain bytes under
/// a kilobyte, then KB, MB and GB of 1,024 each, to a tenth, its sign kept.</summary>
public sealed class SizesTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(41_734_963L, "39.8 MB")]
    [InlineData(64L * 1024 * 1024, "64 MB")]
    [InlineData(2L * 1024 * 1024 * 1024, "2 GB")]
    [InlineData(-1536L, "-1.5 KB")]
    public void A_size_is_said_in_its_largest_unit_to_a_tenth(long bytes, string said)
    {
        string written = Sizes.Said(bytes);
        Assert.True(written == said, $"{bytes:N0} bytes were said as {written}, not {said}: a size reads one way in one place and another elsewhere");
    }
}
