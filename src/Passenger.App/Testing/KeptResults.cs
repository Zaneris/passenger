using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Testing;

/// <summary>Each test method's results as its last run left them, by its row's identity, kept in the folder's state so the
/// Unit Tests window shows them when the folder is opened again, as an earlier run's, until the test runs again. Bounded:
/// a result's words to their first (an output to its last) few thousand characters, a method's results to a thousand,
/// the failed first, and the methods to those that ran latest.</summary>
public sealed record TestResultsState : IStateDocument<TestResultsState>
{
    /// <summary>The most methods kept, those that ran longest ago going first.</summary>
    public const int MostMethods = 10_000;

    /// <summary>The most results kept of a method's, a theory's rows: its failed ones first.</summary>
    public const int MostResults = 1_000;

    /// <summary>The most characters kept of a result's message and of its stack trace, from their start, and of its
    /// output, from its end.</summary>
    public const int MostMessage = 4_000;
    public const int MostStackTrace = 8_000;
    public const int MostOutput = 8_000;

    public Dictionary<string, KeptRun> Methods { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned, CodeReadOtherwise];

    /// <summary>A run kept with its code read as <see cref="TestCode"/> no longer reads it says nothing of the code now, so
    /// it is kept with none, and reads as changed by nobody.</summary>
    private static void CodeReadOtherwise(JsonObject state)
    {
        if (state["methods"] is JsonObject methods)
        {
            foreach ((_, JsonNode? run) in methods)
            {
                (run as JsonObject)?.Remove("code");
            }
        }
    }

    /// <summary>Within the bounds, each result of an outcome this build has and a length of time, a method with none left out.</summary>
    public TestResultsState Clamped()
    {
        Dictionary<string, KeptRun> methods = new(StringComparer.Ordinal);
        foreach ((string identity, KeptRun? run) in (Methods ?? []).OrderByDescending(m => m.Value?.Ran ?? default).Take(MostMethods))
        {
            if (run?.Results?.Where(r => r is not null && Enum.IsDefined(r.State) && r.Duration >= TimeSpan.Zero) is { } results
                && Bounded(results) is { Count: > 0 } kept)
            {
                methods[identity] = run with { Results = kept };
            }
        }
        return this with { Methods = methods };
    }

    /// <summary>The methods' runs kept now: these, each in place of what was kept of its method, over what was kept of the
    /// others, within the bounds.</summary>
    public TestResultsState With(IReadOnlyDictionary<string, KeptRun> runs)
    {
        Dictionary<string, KeptRun> methods = new(Methods, StringComparer.Ordinal);
        foreach ((string identity, KeptRun run) in runs)
        {
            methods[identity] = run;
        }
        return (this with { Methods = methods }).Clamped();
    }

    /// <summary>A method's results within the bounds, the failed first, then in their order.</summary>
    internal static List<KeptResult> Bounded(IEnumerable<KeptResult> results) =>
        [.. results.Select((r, at) => (r, at)).OrderBy(p => p.r.State == TestState.Failed ? 0 : 1).ThenBy(p => p.at).Take(MostResults).OrderBy(p => p.at)
            .Select(p => p.r with
            {
                Message = Start(p.r.Message, MostMessage),
                StackTrace = Start(p.r.StackTrace, MostStackTrace),
                Output = End(p.r.Output, MostOutput),
            })];

    /// <summary>How far past its bound a text may go before it is cut, which the note of a cut fits in: a text cut once
    /// is never cut again.</summary>
    private const int Slack = 64;

    private static string? Start(string? text, int most) => text is { } whole && whole.Length > most + Slack ? $"{whole[..most]}\n[{whole.Length - most:N0} more characters left out]" : text;

    private static string? End(string? text, int most) => text is { } whole && whole.Length > most + Slack ? $"[{whole.Length - most:N0} earlier characters left out]\n{whole[^most..]}" : text;
}

/// <summary>A method's results as one run left them: when the run began, the method's code then
/// (<see cref="DeclaredTest.Code"/>; null where the code was not read), and each of its tests' results.</summary>
public sealed record KeptRun
{
    public DateTimeOffset Ran { get; init; }

    public ulong? Code { get; init; }

    public List<KeptResult> Results { get; init; } = [];

    /// <summary>A method's results of a run that began then, its code then, within the bounds; a result an earlier run
    /// left, of a row this one did not run, kept with that run's time.</summary>
    public static KeptRun Of(IEnumerable<TestOutcome> results, DateTimeOffset ran, ulong? code) => new()
    {
        Ran = ran,
        Code = code,
        Results = TestResultsState.Bounded(results.Select(r => new KeptResult
        {
            FullyQualifiedName = r.Test.FullyQualifiedName,
            DisplayName = r.Test.DisplayName,
            Framework = r.Test.Framework,
            Row = r.Row,
            State = r.State,
            Duration = r.Duration,
            Message = r.Message,
            StackTrace = r.StackTrace,
            Output = r.Output,
            Ran = r.Earlier,
        })),
    };
}

/// <summary>A test's result as kept: the test it is of, its data row's name where it is one's, how it ended and took, and
/// what it said.</summary>
public sealed record KeptResult
{
    public string FullyQualifiedName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Framework { get; init; }

    public string? Row { get; init; }

    public TestState State { get; init; }

    public TimeSpan Duration { get; init; }

    public string? Message { get; init; }

    public string? StackTrace { get; init; }

    public string? Output { get; init; }

    /// <summary>When the run it is of began, where that was before its method's run (<see cref="KeptRun.Ran"/>).</summary>
    public DateTimeOffset? Ran { get; init; }

    /// <summary>The result as the tree takes it, of a test of this project, marked as of its run, the one that began then
    /// unless it says another.</summary>
    public TestOutcome Outcome(string project, DateTimeOffset ran) =>
        new(new TestItem(project, FullyQualifiedName, DisplayName) { Framework = Framework, Row = Row }, State, Duration)
        {
            Message = Message,
            StackTrace = StackTrace,
            Output = Output,
            Earlier = Ran ?? ran,
        };
}
