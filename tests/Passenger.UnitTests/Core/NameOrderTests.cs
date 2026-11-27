using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The order a listing's names go in: punctuation before digits and digits before letters, letters whatever their
/// case, a name before the longer ones it begins, and names alike but for case by their characters, whatever order they
/// were listed in (#2396).</summary>
public sealed class NameOrderTests
{
    [Fact]
    public void Names_are_listed_punctuation_first_then_digits_then_letters_whatever_their_case_or_the_order_they_came_in() => Cases.Each<(string[] Listed, string[] Ordered)>(
    [
        (["client-apps", "server", "tests", "_libs", "_shared", "__docs", "___tools"], ["___tools", "__docs", "_libs", "_shared", "client-apps", "server", "tests"]),
        (["banana", "apple2", "Cherry", "Apple"], ["Apple", "apple2", "banana", "Cherry"]),
        (["b", "B1", "2b", ".b", "-b", " b"], [" b", "-b", ".b", "2b", "b", "B1"]),
        (["App_Data", "AppBuilder.cs", "App.axaml.cs", "App.axaml"], ["App.axaml", "App.axaml.cs", "App_Data", "AppBuilder.cs"]),
        (["readme.md", "Readme.md", "README.md"], ["README.md", "Readme.md", "readme.md"]),
    ], row =>
    {
        string[] ordered = [.. row.Listed.Order(NameOrder.Instance)];
        string[] reversed = [.. row.Listed.Reverse().Order(NameOrder.Instance)];
        Assert.True(ordered.SequenceEqual(row.Ordered) && reversed.SequenceEqual(row.Ordered),
            $"[{string.Join(", ", row.Listed)}] were listed as [{string.Join(", ", ordered)}] and, given the other way round, [{string.Join(", ", reversed)}], "
            + $"not [{string.Join(", ", row.Ordered)}]: a folder named to lead the list lands after the letters, or the order hangs on how the disk listed them");
    }, row => string.Join(" ", row.Ordered));
}
