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

    [Fact]
    public void An_item_or_member_written_into_json_keeps_the_layout_and_comments_around_it() => Cases.All(
        ("An item goes after the arrays last as its items are laid out and an array missing is made", An_item_goes_after_the_arrays_last_as_its_items_are_laid_out_and_an_array_missing_is_made),
        ("A path through a value that is no object or to one that is no array says so and changes nothing", A_path_through_a_value_that_is_no_object_or_to_one_that_is_no_array_says_so_and_changes_nothing),
        ("Items are rewritten where they stand or taken out with their lines and commas the rest as it was", Items_are_rewritten_where_they_stand_or_taken_out_with_their_lines_and_commas_the_rest_as_it_was),
        ("Items changed on a path through a value that is no object or to one that is no array say so", Items_changed_on_a_path_through_a_value_that_is_no_object_or_to_one_that_is_no_array_say_so),
        ("A member set to nothing is taken out with its line and comma", A_member_set_to_nothing_is_taken_out_with_its_line_and_comma),
        ("Values nested past what is read are said not to read and end nothing", Values_nested_past_what_is_read_are_said_not_to_read_and_end_nothing),
        ("Values as deep as are read read whole", Values_as_deep_as_are_read_read_whole));

    private void An_item_goes_after_the_arrays_last_as_its_items_are_laid_out_and_an_array_missing_is_made() => Cases.Each<(string text, string expected)>(
    [
        ("{\n  \"solution\": {\n    \"path\": \"S.slnx\",\n    \"projects\": [\n      \"A\\\\A.csproj\"\n    ]\n  }\n}\n",
            "{\n  \"solution\": {\n    \"path\": \"S.slnx\",\n    \"projects\": [\n      \"A\\\\A.csproj\",\n      \"B\\\\B.csproj\"\n    ]\n  }\n}\n"),
        ("{\r\n  \"solution\": {\r\n    \"projects\": [\r\n      // the app\r\n      \"A\\\\A.csproj\",\r\n    ]\r\n  }\r\n}\r\n",
            "{\r\n  \"solution\": {\r\n    \"projects\": [\r\n      // the app\r\n      \"A\\\\A.csproj\",\r\n      \"B\\\\B.csproj\",\r\n    ]\r\n  }\r\n}\r\n"),
        ("{ \"solution\": { \"projects\": [ \"A\\\\A.csproj\" ] } }", "{ \"solution\": { \"projects\": [ \"A\\\\A.csproj\", \"B\\\\B.csproj\" ] } }"),
        ("{\n  \"solution\": {\n    \"projects\": []\n  }\n}\n", "{\n  \"solution\": {\n    \"projects\": [\n      \"B\\\\B.csproj\"\n    ]\n  }\n}\n"),
        ("{\n  \"solution\": {\n    \"path\": \"S.slnx\"\n  }\n}\n", "{\n  \"solution\": {\n    \"path\": \"S.slnx\",\n    \"projects\": [\n      \"B\\\\B.csproj\"\n    ]\n  }\n}\n"),
    ], row =>
    {
        (string text, string expected) = row;
        (string? added, string? problem) = JsonText.AddToArray(text, Projects, JsonValue.Create("B\\B.csproj"));
        Assert.Null(problem);
        Assert.Equal(expected, added);
    });

    private void A_path_through_a_value_that_is_no_object_or_to_one_that_is_no_array_says_so_and_changes_nothing()
    {
        string? Refused(string text) => JsonText.AddToArray(text, Projects, JsonValue.Create("B")) is (null, { } problem) ? problem : null;
        Assert.Equal("its projects is not a list", Refused("{ \"solution\": { \"projects\": \"A\" } }"));
        Assert.Equal("its solution is not an object", Refused("{ \"solution\": [] }"));
        Assert.StartsWith("it does not read as JSON", Refused("{ \"solution\": ") ?? string.Empty, StringComparison.Ordinal);
    }

    private void Items_are_rewritten_where_they_stand_or_taken_out_with_their_lines_and_commas_the_rest_as_it_was() => Cases.Each<(string text, string expected)>(
    [
        ("{\n  \"solution\": {\n    \"projects\": [\n      \"A\",\n      \"B\",\n      \"C\"\n    ]\n  }\n}\n",
            "{\n  \"solution\": {\n    \"projects\": [\n      \"A\",\n      \"D\"\n    ]\n  }\n}\n"),
        ("{\r\n  \"solution\": {\r\n    \"projects\": [\r\n      // the app\r\n      \"A\",\r\n      \"C\",\r\n      \"B\",\r\n    ]\r\n  }\r\n}\r\n",
            "{\r\n  \"solution\": {\r\n    \"projects\": [\r\n      // the app\r\n      \"A\",\r\n      \"D\",\r\n    ]\r\n  }\r\n}\r\n"),
        ("{ \"solution\": { \"projects\": [ \"A\", \"C\", \"B\" ] } }", "{ \"solution\": { \"projects\": [ \"A\", \"D\" ] } }"),
        ("{\n  \"solution\": {\n    \"projects\": [\n      \"B\"\n    ]\n  }\n}\n", "{\n  \"solution\": {\n    \"projects\": []\n  }\n}\n"),
        ("{ \"solution\": { \"projects\": [ \"A\", null, 2 ] } }", "{ \"solution\": { \"projects\": [ \"A\", null, 2 ] } }"),
        ("{ \"solution\": { \"path\": \"S.slnx\" } }", "{ \"solution\": { \"path\": \"S.slnx\" } }"),
    ], row =>
    {
        (string text, string expected) = row;
        (string? changed, string? problem) = JsonText.ChangeItems(text, Projects, item => item.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? item.GetValue<string>() switch { "B" => null, "C" => JsonValue.Create("D"), _ => item }
            : item);
        Assert.Null(problem);
        Assert.Equal(expected, changed);
    });

    private void Items_changed_on_a_path_through_a_value_that_is_no_object_or_to_one_that_is_no_array_say_so()
    {
        string? Refused(string text) => JsonText.ChangeItems(text, Projects, _ => null) is (null, { } problem) ? problem : null;
        Assert.Equal("its projects is not a list", Refused("{ \"solution\": { \"projects\": \"A\" } }"));
        Assert.Equal("its solution is not an object", Refused("{ \"solution\": [] }"));
        Assert.StartsWith("it does not read as JSON", Refused("{ \"solution\": ") ?? string.Empty, StringComparison.Ordinal);
    }

    private void A_member_set_to_nothing_is_taken_out_with_its_line_and_comma() => Cases.Each<(string text, string expected)>(
    [
        ("{\n  \"Parameters\": {\n    \"a\": 1,\n    \"b\": 2,\n    \"c\": 3\n  }\n}\n", "{\n  \"Parameters\": {\n    \"a\": 1,\n    \"c\": 3\n  }\n}\n"),
        ("{\n  \"Parameters\": {\n    \"a\": 1,\n    \"b\": 2\n  }\n}\n", "{\n  \"Parameters\": {\n    \"a\": 1\n  }\n}\n"),
        ("{ \"Parameters\": { \"b\": 2, \"a\": 1 } }", "{ \"Parameters\": { \"a\": 1 } }"),
        ("{\n  \"Parameters\": {\n    \"b\": 2\n  }\n}\n", "{\n  \"Parameters\": {}\n}\n"),
    ], row =>
    {
        (string text, string expected) = row;
        (string? changed, string? problem) = JsonText.SetInRoot(text, "Parameters", "b", null);
        Assert.Null(problem);
        Assert.Equal(expected, changed);
    });

    private static string Nested(string open, string inner, string close, int deep) =>
        string.Concat(Enumerable.Repeat(open, deep)) + inner + string.Concat(Enumerable.Repeat(close, deep));

    private void Values_nested_past_what_is_read_are_said_not_to_read_and_end_nothing() => Cases.Each<(string open, string inner, string close)>(
    [
        ("[", "", "]"),
        ("{\"a\":", "1", "}"),
    ], row =>
    {
        (string open, string inner, string close) = row;
        string text = Nested(open, inner, close, 100_000);
        SmallStack.Run(() =>
        {
            Assert.Contains("nested too deeply", JsonText.SetKey(text, ["b"], null).Problem, StringComparison.Ordinal);
            Assert.Throws<FormatException>(() => new JsonText.Reader(text).Document());
        });
    });

    private void Values_as_deep_as_are_read_read_whole() => Cases.Each<(string open, string inner, string close)>(
    [
        ("[", "", "]"),
        ("{\"a\":", "1", "}"),
    ], row =>
    {
        (string open, string inner, string close) = row;
        string text = Nested(open, inner, close, 250);
        SmallStack.Run(() => Assert.Equal(text.Length, new JsonText.Reader(text).Document().End));
    });
}
