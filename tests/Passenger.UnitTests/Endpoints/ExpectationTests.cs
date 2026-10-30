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

    /// <summary>A body cut at <see cref="EndpointClient.MostRead"/>: what the unread rest could hold fails, naming the cut,
    /// and what the part read holds passes.</summary>
    [Fact]
    public void A_body_cut_short_fails_what_its_unread_rest_could_hold_and_passes_what_its_part_read_holds()
    {
        EndpointResponse cut = new() { Status = 200, Body = new string('a', 1000), Cut = true };

        ExpectationResult lacks = Expectation.Read("body not-contains \"x\"").Check(cut);
        Assert.False(lacks.Passed, "body not-contains \"x\" passed on a body cut short, whose unread rest may hold an x");
        Assert.Contains("cut at 16 MB", lacks.Actual, StringComparison.Ordinal);
        foreach (string unknowable in (string[])["body not-equals \"b\"", "body equals \"a\"", "json $.items exists"])
        {
            ExpectationResult result = Expectation.Read(unknowable).Check(cut);
            Assert.True(!result.Passed && result.Actual.Contains("cut at 16 MB", StringComparison.Ordinal),
                $"{unknowable} on a body cut short did not fail naming the cut: {(result.Passed ? "passed" : "failed")}, got {result.Actual}");
        }

        Assert.True(Expectation.Read("body contains aaa").Check(cut).Passed, "body contains aaa failed on a cut body whose part read holds it");
        Assert.True(Expectation.Read("body exists").Check(cut).Passed, "body exists failed on a cut body");
        ExpectationResult missing = Expectation.Read("body contains x").Check(cut);
        Assert.True(!missing.Passed && missing.Actual.Contains("not in the part read", StringComparison.Ordinal),
            $"body contains x on a cut body without one did not fail saying the part read lacks it: got {missing.Actual}");
        Assert.True(Expectation.Read("body not-contains \"x\"").Check(cut with { Cut = false }).Passed, "body not-contains \"x\" failed on a whole body without one");
    }
}
