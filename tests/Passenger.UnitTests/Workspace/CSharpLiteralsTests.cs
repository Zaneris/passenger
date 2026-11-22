using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A C# string literal's text against the value it says, as a rename of what it names reads and writes it.</summary>
public class CSharpLiteralsTests
{
    [Fact]
    public void A_literals_text_is_read_as_its_value_and_a_value_written_as_the_literal_it_replaces_writes_one() => Cases.All(
        ("a place in a literal's text is read as a place in its value", () => Cases.Each(
        [
            ("\"Hud/Sc\\\\ore\"", 9, 7),
            ("\"a\\x41g\"", 7, 3),
            ("\"a\\U0001F600b\"", 13, 4),
            ("@\"say \"\"hi\"\"\"", 12, 8),
        ], row => Assert.Equal(row.Item3, CSharpLiterals.ValueOffset(row.Item1, row.Item2)))),
        ("a literal is held to the length of the value read from it", () => Cases.Each(
        [
            ("\"Hud/Sc\\\\ore\"", "Hud/Sc\\ore", true),
            ("\"Hud/Sc\\\\ores\"", "Hud/Sc\\ore", false),
        ], row => Assert.Equal(row.Item3, CSharpLiterals.Says(row.Item1, row.Item2)))),
        ("a value is written the way the literal it replaces writes one, line separators escaped as a newline is", () => Cases.Each(
        [
            ("Hud/Sc\\ore", "\"old\"", "\"Hud/Sc\\\\ore\""),
            ("say \"hi\"", "\"old\"", "\"say \\\"hi\\\"\""),
            ("say \"hi\"", "@\"old\"", "@\"say \"\"hi\"\"\""),
            ("a\u2028b\u0085c", "\"old\"", "\"a\\u2028b\\u0085c\""),
        ], row =>
        {
            Assert.Equal(row.Item3, CSharpLiterals.Written(row.Item1, row.Item2));
            Assert.Equal(row.Item1.Length, CSharpLiterals.ValueOffset(row.Item3, row.Item3.Length - 1));
        })));
}
