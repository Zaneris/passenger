using System.Text.Json;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>JSON a person writes by hand reads with its comments and trailing commas, as .NET's own readers of such files
/// take it.</summary>
public sealed class LenientJsonTests
{
    [Fact]
    public void Comments_and_trailing_commas_are_read_past()
    {
        const string Text = "{\n  // the profile\n  \"profiles\": { \"web\": { \"commandName\": \"Project\", }, },\n}\n";
        using JsonDocument read = JsonDocument.Parse(Text, LenientJson.Document);
        Assert.Equal("Project", JsonMembers.Text(JsonMembers.Object(JsonMembers.Object(read.RootElement, "profiles"), "web"), "commandName"));
        Utf8JsonReader reader = new(System.Text.Encoding.UTF8.GetBytes(Text), LenientJson.Reader);
        int tokens = 0;
        while (reader.Read())
        {
            tokens++;
        }
        Assert.True(tokens > 0, "A hand-written file with a comment and trailing commas did not read token by token");
    }
}
