using System.Globalization;
using Passenger.App.Mcp;

namespace Passenger.UnitTests.Mcp;

/// <summary>What an agent's answer holds of what runs long: a debugged value cut at a few thousand characters, saying its
/// length and the debug_evaluate that reads on, a string's literal never cut inside an escape.</summary>
public sealed class AgentAnswersTests
{
    [Fact]
    public void A_short_value_is_shown_whole_and_says_no_cut()
    {
        Assert.Equal(("\"short\"", (string?)null), AgentAnswers.Value("\"short\"", "text"));
        string fits = new('x', AgentAnswers.ValueLength);
        Assert.Equal((fits, (string?)null), AgentAnswers.Value(fits, "text"));
    }

    [Fact]
    public void A_million_character_string_shows_its_first_characters_and_says_its_length_and_the_substring_that_reads_on()
    {
        (string shown, string? cut) = AgentAnswers.Value($"\"{new string('x', 1_000_000)}\"", "big");
        Assert.True(shown.Length <= AgentAnswers.ValueLength && shown == $"\"{new string('x', AgentAnswers.ValueLength - 2)}…",
            $"A string of a million characters was shown as {shown.Length:N0} characters, an answer an agent's client may refuse: {shown[..40]}…");
        Assert.Equal("1,000,000 characters, the first 3,998 shown: debug_evaluate big.Substring(3998, 3998) reads on", cut);
    }

    [Fact]
    public void A_strings_escapes_are_never_cut_and_its_length_and_places_are_the_strings_own()
    {
        // Each \n is two characters of the literal and one of the string; each \u0001 six and one.
        string literal = "\"" + string.Concat(Enumerable.Repeat("ab\\n\\u0001", 2_000)) + "\"";
        (string shown, string? cut) = AgentAnswers.Value(literal, "text");
        char next = literal[shown.Length - 1];
        Assert.True(shown.Length <= AgentAnswers.ValueLength && next is 'a' or 'b' or '\\',
            $"A string's literal was cut inside an escape, which an agent reads as other text: …{shown[^20..]} before {literal.Substring(shown.Length - 1, 8)}");
        int shownChars = Unescaped(shown[1..^1]);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture,
            $"8,000 characters, the first {shownChars:N0} shown: debug_evaluate text.Substring({shownChars}, {Math.Min(AgentAnswers.ValueLength - 2, 8_000 - shownChars)}) reads on"), cut);
    }

    [Fact]
    public void A_cut_never_parts_a_surrogate_pair()
    {
        string pairs = string.Concat(Enumerable.Repeat("\U0001F600", 5_000));
        (string shown, _) = AgentAnswers.Value($"\"{pairs}\"", "faces");
        Assert.False(char.IsHighSurrogate(shown[^2]), $"The literal's cut parted a surrogate pair: its shown text ends {(int)shown[^2]:X4}");
        (string plain, string? said) = AgentAnswers.Value(pairs, "faces");
        Assert.False(char.IsHighSurrogate(plain[^2]), $"The value's cut parted a surrogate pair: its shown text ends {(int)plain[^2]:X4}");
        Assert.StartsWith("10,000 characters, the first 3,998 shown: debug_evaluate faces.ToString().Substring(3998, 3998)", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_no_expression_names_or_one_that_is_no_variable_is_read_on_through_its_own_substring_or_in_parentheses()
    {
        string big = $"\"{new string('y', 5_000)}\"";
        Assert.Equal("5,000 characters, the first 3,998 shown: debug_evaluate of its .Substring(3998, 1002) reads on", AgentAnswers.Value(big, null).Cut);
        Assert.Equal("5,000 characters, the first 3,998 shown: debug_evaluate (new string('y', 5000)).Substring(3998, 1002) reads on",
            AgentAnswers.Value(big, "new string('y', 5000)").Cut);
        Assert.Equal("5,000 characters, the first 3,998 shown: debug_evaluate order.Lines[2].Note.Substring(3998, 1002) reads on",
            AgentAnswers.Value(big, "order.Lines[2].Note").Cut);
    }

    /// <summary>How many characters a literal's text (its quotes left off) writes.</summary>
    private static int Unescaped(string text)
    {
        int count = 0;
        for (int at = 0; at < text.Length; count++)
        {
            at += text[at] != '\\' ? 1 : text[at + 1] == 'u' ? 6 : 2;
        }
        return count;
    }
}
