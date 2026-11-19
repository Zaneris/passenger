namespace Passenger.App.Testing;

/// <summary>What a VSTest run broken off (its test host crashed, or VSTest could not go on) is said by: why, and the tests
/// running then. A run whose test host ended may end with a cancellation for its error or none, by the order VSTest heard
/// the host's exit and the end of its connection in; a cancellation says nothing of the run, so what VSTest logged of it is
/// said either way.</summary>
public static class BrokenRuns
{
    /// <summary>The words of a run broken off: the error VSTest gave its end (<see cref="Told"/>), else the last error it
    /// logged, and the tests running then by their full names.</summary>
    public static string? Said(Exception? error, string? logged, IReadOnlyList<string> running)
    {
        string? why = Told(error) ?? logged;
        string? named = running.Count == 0 ? null : $"while {string.Join(", ", running)} ran";
        return why is null ? named : named is null ? why : $"{why}, {named}";
    }

    /// <summary>What an error VSTest gave a run's end says: each inner error's words of an aggregate; nothing of none, or of
    /// a cancellation alone.</summary>
    public static string? Told(Exception? error)
    {
        IReadOnlyList<Exception> each = error is AggregateException all ? all.Flatten().InnerExceptions : error is null ? [] : [error];
        return each.Count == 0 || each.All(e => e is OperationCanceledException) ? null : string.Join("; ", each.Select(e => e.Message));
    }
}
