using System.Text.Json.Nodes;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>An item added to a JSON file's array as text, as a solution filter's projects take one: laid out as the array's
/// items are, its trailing comma, comments and line breaks kept, the array made where the file has none; items rewritten
/// where they stand or taken out with their lines, as a filter's follow its projects moved or removed; and a member taken
/// out of its object the same way.</summary>
public class JsonTextTests
{
    private static readonly string[] Projects = ["solution", "projects"];

    [Theory]
    [InlineData("{\n  \"solution\": {\n    \"path\": \"S.slnx\",\n    \"projects\": [\n      \"A\\\\A.csproj\"\n    ]\n  }\n}\n",
        "{\n  \"solution\": {\n    \"path\": \"S.slnx\",\n    \"projects\": [\n      \"A\\\\A.csproj\",\n      \"B\\\\B.csproj\"\n    ]\n  }\n}\n")]
    [InlineData("{\r\n  \"solution\": {\r\n    \"projects\": [\r\n      // the app\r\n      \"A\\\\A.csproj\",\r\n    ]\r\n  }\r\n}\r\n",
        "{\r\n  \"solution\": {\r\n    \"projects\": [\r\n      // the app\r\n      \"A\\\\A.csproj\",\r\n      \"B\\\\B.csproj\",\r\n    ]\r\n  }\r\n}\r\n")]
    [InlineData("{ \"solution\": { \"projects\": [ \"A\\\\A.csproj\" ] } }", "{ \"solution\": { \"projects\": [ \"A\\\\A.csproj\", \"B\\\\B.csproj\" ] } }")]
    [InlineData("{\n  \"solution\": {\n    \"projects\": []\n  }\n}\n", "{\n  \"solution\": {\n    \"projects\": [\n      \"B\\\\B.csproj\"\n    ]\n  }\n}\n")]
    [InlineData("{\n  \"solution\": {\n    \"path\": \"S.slnx\"\n  }\n}\n", "{\n  \"solution\": {\n    \"path\": \"S.slnx\",\n    \"projects\": [\n      \"B\\\\B.csproj\"\n    ]\n  }\n}\n")]
    public void An_item_goes_after_the_arrays_last_as_its_items_are_laid_out_and_an_array_missing_is_made(string text, string expected)
    {
        (string? added, string? problem) = JsonText.AddToArray(text, Projects, JsonValue.Create("B\\B.csproj"));
        Assert.Null(problem);
        Assert.Equal(expected, added);
    }

    [Fact]
    public void A_path_through_a_value_that_is_no_object_or_to_one_that_is_no_array_says_so_and_changes_nothing()
    {
        string? Refused(string text) => JsonText.AddToArray(text, Projects, JsonValue.Create("B")) is (null, { } problem) ? problem : null;
        Assert.Equal("its projects is not a list", Refused("{ \"solution\": { \"projects\": \"A\" } }"));
        Assert.Equal("its solution is not an object", Refused("{ \"solution\": [] }"));
        Assert.StartsWith("it does not read as JSON", Refused("{ \"solution\": ") ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\n  \"solution\": {\n    \"projects\": [\n      \"A\",\n      \"B\",\n      \"C\"\n    ]\n  }\n}\n",
        "{\n  \"solution\": {\n    \"projects\": [\n      \"A\",\n      \"D\"\n    ]\n  }\n}\n")]
    [InlineData("{\r\n  \"solution\": {\r\n    \"projects\": [\r\n      // the app\r\n      \"A\",\r\n      \"C\",\r\n      \"B\",\r\n    ]\r\n  }\r\n}\r\n",
        "{\r\n  \"solution\": {\r\n    \"projects\": [\r\n      // the app\r\n      \"A\",\r\n      \"D\",\r\n    ]\r\n  }\r\n}\r\n")]
    [InlineData("{ \"solution\": { \"projects\": [ \"B\", \"A\", \"C\" ] } }", "{ \"solution\": { \"projects\": [ \"A\", \"D\" ] } }")]
    [InlineData("{ \"solution\": { \"projects\": [ \"A\", \"C\", \"B\" ] } }", "{ \"solution\": { \"projects\": [ \"A\", \"D\" ] } }")]
    [InlineData("{\n  \"solution\": {\n    \"projects\": [\n      \"B\"\n    ]\n  }\n}\n", "{\n  \"solution\": {\n    \"projects\": []\n  }\n}\n")]
    [InlineData("{\n  \"solution\": {\n    \"projects\": [ \"B\", \"B\" ]\n  }\n}\n", "{\n  \"solution\": {\n    \"projects\": []\n  }\n}\n")]
    [InlineData("{ \"solution\": { \"projects\": [ \"A\", null, 2 ] } }", "{ \"solution\": { \"projects\": [ \"A\", null, 2 ] } }")]
    [InlineData("{ \"solution\": { \"path\": \"S.slnx\" } }", "{ \"solution\": { \"path\": \"S.slnx\" } }")]
    public void Items_are_rewritten_where_they_stand_or_taken_out_with_their_lines_and_commas_the_rest_as_it_was(string text, string expected)
    {
        (string? changed, string? problem) = JsonText.ChangeItems(text, Projects, item => item.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? item.GetValue<string>() switch { "B" => null, "C" => JsonValue.Create("D"), _ => item }
            : item);
        Assert.Null(problem);
        Assert.Equal(expected, changed);
    }

    [Fact]
    public void Items_changed_on_a_path_through_a_value_that_is_no_object_or_to_one_that_is_no_array_say_so()
    {
        string? Refused(string text) => JsonText.ChangeItems(text, Projects, _ => null) is (null, { } problem) ? problem : null;
        Assert.Equal("its projects is not a list", Refused("{ \"solution\": { \"projects\": \"A\" } }"));
        Assert.Equal("its solution is not an object", Refused("{ \"solution\": [] }"));
        Assert.StartsWith("it does not read as JSON", Refused("{ \"solution\": ") ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\n  \"Parameters\": {\n    \"a\": 1,\n    \"b\": 2,\n    \"c\": 3\n  }\n}\n", "{\n  \"Parameters\": {\n    \"a\": 1,\n    \"c\": 3\n  }\n}\n")]
    [InlineData("{\n  \"Parameters\": {\n    \"a\": 1,\n    \"b\": 2\n  }\n}\n", "{\n  \"Parameters\": {\n    \"a\": 1\n  }\n}\n")]
    [InlineData("{ \"Parameters\": { \"b\": 2, \"a\": 1 } }", "{ \"Parameters\": { \"a\": 1 } }")]
    [InlineData("{\n  \"Parameters\": {\n    \"b\": 2\n  }\n}\n", "{\n  \"Parameters\": {}\n}\n")]
    public void A_member_set_to_nothing_is_taken_out_with_its_line_and_comma(string text, string expected)
    {
        (string? changed, string? problem) = JsonText.SetInRoot(text, "Parameters", "b", null);
        Assert.Null(problem);
        Assert.Equal(expected, changed);
    }
}
