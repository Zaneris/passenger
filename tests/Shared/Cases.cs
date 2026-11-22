using System.Runtime.CompilerServices;
using Xunit.Sdk;

namespace Passenger.TestTables;

/// <summary>One test over a table of rows, where a theory would list each row as a test of its own: every row runs
/// whatever another does, and a failure names each row that failed, by its values (or the name given it), with what it
/// found. A table keeps a few well-chosen rows, each one that most surely catches a regression of its rule; linked into
/// both test projects.</summary>
public static class Cases
{
    /// <summary>Checks each row with <paramref name="check"/>, named by <paramref name="name"/>, else by its values as a
    /// theory's case would be.</summary>
    public static void Each<T>(IEnumerable<T> rows, Action<T> check, Func<T, string>? name = null)
    {
        Action[] checks = [.. rows.Select(row => (Action)(() => Run(row, name, () => check(row))))];
        Assert.True(checks.Length > 0, "The table has no rows, so it checks nothing");
        Assert.Multiple(checks);
    }

    /// <summary><see cref="Each{T}"/> for a check that waits: each row's in turn.</summary>
    public static Task EachAsync<T>(IEnumerable<T> rows, Func<T, Task> check, Func<T, string>? name = null)
    {
        Func<Task>[] checks = [.. rows.Select(row => (Func<Task>)(() => RunAsync(row, name, () => check(row))))];
        Assert.True(checks.Length > 0, "The table has no rows, so it checks nothing");
        return Assert.MultipleAsync(checks);
    }

    /// <summary>Several checks joined into one test, where each was a test of its own: a table whose rows are the checks,
    /// each named by what it holds.</summary>
    public static void All(params (string Holds, Action Check)[] checks) => Each(checks, check => check.Check(), check => check.Holds);

    /// <summary><see cref="All"/> for checks that wait: each in turn.</summary>
    public static Task AllAsync(params (string Holds, Func<Task> Check)[] checks) =>
        EachAsync(checks, check => check.Check(), check => check.Holds);

    private static void Run<T>(T row, Func<T, string>? name, Action check)
    {
        try
        {
            check();
        }
        catch (Exception failure)
        {
            throw new RowFailed(NameOf(row, name), failure);
        }
    }

    private static async Task RunAsync<T>(T row, Func<T, string>? name, Func<Task> check)
    {
        try
        {
            await check();
        }
        catch (Exception failure)
        {
            throw new RowFailed(NameOf(row, name), failure);
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
