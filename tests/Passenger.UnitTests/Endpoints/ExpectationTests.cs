using System.Text.Json;
using Passenger.App.Endpoints;

namespace Passenger.UnitTests.Endpoints;

/// <summary>What an answer is held to: a JSON path whose steps cover it whole, any other path no criterion, and an index
/// past any array's length one that finds nothing.</summary>
public sealed class ExpectationTests
{
    private const string Items = """{"items":[{"name":"Ada"}]}""";

    [Fact]
    public void A_json_path_with_text_between_its_steps_is_refused_and_finds_nothing()
    {
        using JsonDocument document = JsonDocument.Parse(Items);

        Assert.Equal("Ada", Expectation.Walk(document.RootElement, "$.items[0].name")?.GetString());
        Assert.True(Expectation.Walk(document.RootElement, "$.items[0]oops.name") is null,
            "$.items[0]oops.name found $.items[0].name: the text between its steps was passed over");
        Assert.True(Expectation.Walk(document.RootElement, "$.items[0].name]") is null, "A path with a stray bracket at its end found an element");
        string? why = Expectation.Problem("json $.items[0]oops.name equals Ada");
        Assert.True(why?.Contains("is no path", StringComparison.Ordinal) == true,
            $"json $.items[0]oops.name was taken as a criterion, where its path is none: {why ?? "no problem"}");
    }

    [Fact]
    public void An_index_past_any_arrays_length_finds_nothing_and_fails_its_check()
    {
        using JsonDocument document = JsonDocument.Parse(Items);

        Assert.Null(Expectation.Walk(document.RootElement, "$.items[99999999999]"));
        Expectation expectation = Expectation.Read("json $.items[99999999999] exists");
        Assert.Null(expectation.Malformed);
        ExpectationResult result = expectation.Check(new EndpointResponse { Status = 200, Body = Items });
        Assert.False(result.Passed, "An element at index 99999999999 of a one-item array was found");
        Assert.Equal("none", result.Actual);
    }
}
