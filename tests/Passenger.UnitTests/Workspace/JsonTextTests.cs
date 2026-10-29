using System.Text.Json.Nodes;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>An item added to a JSON file's array as text, as a solution filter's projects take one: laid out as the array's
/// items are, its trailing comma, comments and line breaks kept, the array made where the file has none.</summary>
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
}
