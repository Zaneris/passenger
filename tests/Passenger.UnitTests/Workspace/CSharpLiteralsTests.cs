using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A C# string literal's text against the value it says, as a rename of what it names reads and writes it.</summary>
public class CSharpLiteralsTests
{
    [Theory]
    [InlineData("\"Hud/Sc\\\\ore\"", 7, 6)]
    [InlineData("\"Hud/Sc\\\\ore\"", 9, 7)]
    [InlineData("\"Hud/Sc\\\\ore\"", 12, 10)]
    [InlineData("\"a\\u0041b\"", 9, 3)]
    [InlineData("\"a\\x41g\"", 7, 3)]
    [InlineData("\"a\\x41b\"", 7, 2)]
    [InlineData("\"a\\U00000041b\"", 13, 3)]
    [InlineData("\"a\\U0001F600b\"", 13, 4)]
    [InlineData("@\"Sc\\ore\"", 5, 3)]
    [InlineData("@\"say \"\"hi\"\"\"", 12, 8)]
    public void A_place_in_a_literals_text_is_read_as_a_place_in_its_value(string literal, int offset, int value)
    {
        Assert.Equal(value, CSharpLiterals.ValueOffset(literal, offset));
    }

    [Theory]
    [InlineData("\"Hud/Sc\\\\ore\"", "Hud/Sc\\ore", true)]
    [InlineData("@\"Hud/Sc\\ore\"", "Hud/Sc\\ore", true)]
    [InlineData("\"Hud/Score\"", "Hud/Sc\\ore", false)]
    [InlineData("\"Hud/Sc\\\\ores\"", "Hud/Sc\\ore", false)]
    [InlineData("Hud", "Hud", false)]
    [InlineData("", "", false)]
    public void A_literal_is_held_to_the_length_of_the_value_read_from_it(string literal, string value, bool says)
    {
        Assert.Equal(says, CSharpLiterals.Says(literal, value));
    }

    [Theory]
    [InlineData("Hud/Sc\\ore", "\"old\"", "\"Hud/Sc\\\\ore\"")]
    [InlineData("say \"hi\"", "\"old\"", "\"say \\\"hi\\\"\"")]
    [InlineData("Hud/Sc\\ore", "@\"old\"", "@\"Hud/Sc\\ore\"")]
    [InlineData("say \"hi\"", "@\"old\"", "@\"say \"\"hi\"\"\"")]
    public void A_value_is_written_the_way_the_literal_it_replaces_writes_one(string value, string like, string written)
    {
        Assert.Equal(written, CSharpLiterals.Written(value, like));
    }
}
