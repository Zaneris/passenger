using System.Runtime.CompilerServices;
using Xunit.Sdk;

namespace Passenger.TestTables;

/// <summary>One test over a table of rows, where a theory would list each row as a test of its own: every row runs
/// whatever another does, and a failure names each row that failed, by its values (or the name given it), with what it
/// found. A row that skips is passed over, said in the test's output, and a table whose every row skipped skips. Each row
/// runs in a copy of the caller's execution context, so nothing ambient a row sets reaches the next. A table
/// keeps a few well-chosen rows, each one that most surely catches a regression of its rule; linked into both test
/// projects.</summary>
public static class Cases
{
    /// <summary>What xunit writes before a skip's reason in its message.</summary>
    private const string SkipToken = "$XunitDynamicSkip$";

    /// <summary>Checks each row with <paramref name="check"/>, named by <paramref name="name"/>, else by its values as a
    /// theory's case would be.</summary>
    public static void Each<T>(IEnumerable<T> rows, Action<T> check, Func<T, string>? name = null)
    {
        List<string> skipped = [];
        Action[] checks = [.. rows.Select(row => (Action)(() => Run(row, name, () => check(row), skipped)))];
        Assert.True(checks.Length > 0, "The table has no rows, so it checks nothing");
        Assert.Multiple(checks);
        SkipWhereEverySkipped(skipped, checks.Length);
    }

    /// <summary><see cref="Each{T}"/> for a check that waits: each row's in turn.</summary>
    public static async Task EachAsync<T>(IEnumerable<T> rows, Func<T, Task> check, Func<T, string>? name = null)
    {
        List<string> skipped = [];
        Func<Task>[] checks = [.. rows.Select(row => (Func<Task>)(() => RunAsync(row, name, () => check(row), skipped)))];
        Assert.True(checks.Length > 0, "The table has no rows, so it checks nothing");
        await Assert.MultipleAsync(checks);
        SkipWhereEverySkipped(skipped, checks.Length);
    }

    /// <summary>Several checks joined into one test, where each was a test of its own: a table whose rows are the checks,
    /// each named by what it holds.</summary>
    public static void All(params (string Holds, Action Check)[] checks) => Each(checks, check => check.Check(), check => check.Holds);

    /// <summary><see cref="All"/> for checks that wait: each in turn.</summary>
    public static Task AllAsync(params (string Holds, Func<Task> Check)[] checks) =>
        EachAsync(checks, check => check.Check(), check => check.Holds);

    /// <summary><see cref="All"/> with each check on an instance of its own of the test's class, made by
    /// <paramref name="make"/> and disposed after it, as each ran once as a test of its own: for a class whose checks set
    /// up, and put back, state of their own (a folder, fields).</summary>
    public static void AllApart<TTest>(Func<TTest> make, params (string Holds, Action<TTest> Check)[] checks) =>
        Each(checks, check => Apart(make, check.Check), check => check.Holds);

    /// <summary><see cref="AllApart{TTest}(Func{TTest}, ValueTuple{string, Action{TTest}}[])"/> for a class made with no
    /// arguments.</summary>
    public static void AllApart<TTest>(params (string Holds, Action<TTest> Check)[] checks) where TTest : new() =>
        AllApart(() => new TTest(), checks);

    /// <summary><see cref="Each{T}"/> with each row checked on an instance of its own of the test's class, as
    /// <see cref="AllApart{TTest}(Func{TTest}, ValueTuple{string, Action{TTest}}[])"/> runs a check.</summary>
    public static void EachApart<TTest, T>(IEnumerable<T> rows, Action<TTest, T> check, Func<T, string>? name = null) where TTest : new() =>
        Each(rows, row => Apart(() => new TTest(), test => check(test, row)), name);

    private static void Apart<TTest>(Func<TTest> make, Action<TTest> check)
    {
        TTest apart = make();
        try
        {
            check(apart);
        }
        finally
        {
            (apart as IDisposable)?.Dispose();
        }
    }

    private static void Run<T>(T row, Func<T, string>? name, Action check, List<string> skipped)
    {
        try
        {
            Flow.InOwnContext(() =>
            {
                check();
                return true;
            });
        }
        catch (SkipException skip)
        {
            Skipped(NameOf(row, name), skip, skipped);
        }
        catch (Exception failure)
        {
            throw new RowFailed(NameOf(row, name), failure);
        }
    }

    private static async Task RunAsync<T>(T row, Func<T, string>? name, Func<Task> check, List<string> skipped)
    {
        try
        {
            await Flow.InOwnContext(check);
        }
        catch (SkipException skip)
        {
            Skipped(NameOf(row, name), skip, skipped);
        }
        catch (Exception failure)
        {
            throw new RowFailed(NameOf(row, name), failure);
        }
    }

    /// <summary>A row that skipped, said in the test's output with why.</summary>
    private static void Skipped(string row, SkipException skip, List<string> skipped)
    {
        string why = skip.Message.StartsWith(SkipToken, StringComparison.Ordinal) ? skip.Message[SkipToken.Length..] : skip.Message;
        skipped.Add($"{row}: {why}");
        TestContext.Current.TestOutputHelper?.WriteLine($"The row {row} skipped: {why}");
    }

    /// <summary>A table whose every row skipped skips, naming each row's reason; one with a row checked holds as it ran.</summary>
    private static void SkipWhereEverySkipped(List<string> skipped, int rows)
    {
        if (skipped.Count == rows)
        {
            Assert.Skip($"Every row skipped: {string.Join("; ", skipped)}");
        }
    }

    /// <summary>A row's name: the one given, else its values as xunit writes a case's arguments, a tuple's each.</summary>
    private static string NameOf<T>(T row, Func<T, string>? name) => name is not null
        ? name(row)
        : row is ITuple values
            ? $"({string.Join(", ", Enumerable.Range(0, values.Length).Select(i => ArgumentFormatter.Format(values[i])))})"
            : ArgumentFormatter.Format(row);

    /// <summary>A row's failure: its name, carrying what failed, which a runner writes after it.</summary>
    private sealed class RowFailed(string row, Exception failure) : XunitException($"The row {row} failed", failure);
}
