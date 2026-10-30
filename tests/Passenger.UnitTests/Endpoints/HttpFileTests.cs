using Passenger.App.Endpoints;

namespace Passenger.UnitTests.Endpoints;

/// <summary>An .http file's criteria as read: one that does not read is a problem of the file at its text, and still its
/// request's criterion, which fails every answer saying why, so a typo never turns a check off.</summary>
public sealed class HttpFileTests
{
    [Fact]
    public void A_criterion_that_does_not_read_is_the_files_problem_and_fails_its_requests_check()
    {
        string[] lines = ["### Health", "# @expect status 20O", "# @expect body contains ok", "GET http://localhost:5000/health"];
        HttpFile file = HttpFile.Parse("api.http", string.Join("\n", lines));

        HttpFileProblem problem = Assert.Single(file.Problems);
        Assert.Equal((1, "status 20O"), (problem.Line, lines[problem.Line].Substring(problem.Column, problem.Length)));
        Assert.Contains("status takes a number", problem.Why, StringComparison.Ordinal);

        EndpointRequest request = Assert.Single(file.Requests);
        EndpointResponse answer = new() { Status = 200, Body = "ok" };
        ExpectationResult[] results = [.. request.Expectations.Select(e => e.Check(answer))];
        Assert.Equal(["status 20O", "body contains ok"], results.Select(r => r.Expectation.ToString()));
        Assert.False(results[0].Passed, "A 200 passed \"status 20O\", a criterion that does not read: a typo turns the request's check off");
        Assert.Contains("status takes a number", results[0].Actual, StringComparison.Ordinal);
        Assert.True(results[1].Passed, $"The criterion that reads failed on a 200 whose body is ok: {results[1].Actual}");
    }

    [Fact]
    public void A_file_whose_criteria_all_read_has_no_problem()
    {
        HttpFile file = HttpFile.Parse("api.http", "# @expect status 2xx\n# @expect json $.items[0].name equals \"Ada\"\nGET http://localhost:5000/items\n");

        Assert.Empty(file.Problems);
        Assert.Equal(["status 2xx", "json $.items[0].name equals Ada"], Assert.Single(file.Requests).Expectations.Select(e => e.ToString()));
    }
}
