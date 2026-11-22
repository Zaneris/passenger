using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A size reads one way wherever it is said, in the IDE's digits whatever the person's culture: plain bytes under
/// a kilobyte, then KB, MB and GB of 1,024 each, to a tenth, its sign kept.</summary>
public sealed class SizesTests
{
    [Fact]
    public void A_size_is_said_in_its_largest_unit_to_a_tenth() => Cases.Each<(long bytes, string said)>(
    [
        (0L, "0 B"),
        (1023L, "1,023 B"),
        (1536L, "1.5 KB"),
        (64L * 1024 * 1024, "64 MB"),
        (2L * 1024 * 1024 * 1024, "2 GB"),
        (-1536L, "-1.5 KB"),
    ], row =>
    {
        (long bytes, string said) = row;
        string written = Sizes.Said(bytes);
        Assert.True(written == said, $"{bytes:N0} bytes were said as {written}, not {said}: a size reads one way in one place and another elsewhere");
    });
}
