using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit.Sdk;

namespace Passenger.TestTables;

/// <summary>A test told as named steps in turn, each the check a test of its own once made, sharing what the steps before
/// it made: a window test's one window, project and server load. A step's name says what holds, so a failure names the
/// step and what no longer holds, with the steps that passed before it; each passed step and its time go to the test's
/// output. The first step that fails ends the flow, as the steps after it stand on it.</summary>
public static class Flow
{
    /// <summary>The steps each running test has passed, in turn, by the test.</summary>
    private static readonly ConditionalWeakTable<object, List<string>> Passed = [];

    public static void Step(string name, Action step) => Step(name, () =>
    {
        step();
        return true;
    });

    public static T Step<T>(string name, Func<T> step)
    {
        long began = Stopwatch.GetTimestamp();
        T made;
        try
        {
            made = step();
        }
        catch (Exception failure) when (failure is not SkipException)
        {
            throw Failed(name, failure);
        }
        Pass(name, began);
        return made;
    }

    public static Task StepAsync(string name, Func<Task> step) => StepAsync(name, async () =>
    {
        await step();
        return true;
    });

    public static async Task<T> StepAsync<T>(string name, Func<Task<T>> step)
    {
        long began = Stopwatch.GetTimestamp();
        T made;
        try
        {
            made = await step();
        }
        catch (Exception failure) when (failure is not SkipException)
        {
            throw Failed(name, failure);
        }
        Pass(name, began);
        return made;
    }

    private static void Pass(string name, long began)
    {
        if (TestContext.Current.Test is { } test)
        {
            Passed.GetOrCreateValue(test).Add(name);
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"Step passed in {Passenger.Core.Durations.Said(Stopwatch.GetElapsedTime(began))}: {name}");
    }

    private static StepFailed Failed(string name, Exception failure)
    {
        List<string> before = TestContext.Current.Test is { } test && Passed.TryGetValue(test, out List<string>? passed) ? passed : [];
        string after = before.Count == 0 ? string.Empty : $" (the step before it: {before[^1]})";
        return new StepFailed($"The flow's step {before.Count + 1} failed, so this no longer holds: {name}{after}", failure);
    }

    /// <summary>A step's failure: what no longer holds, carrying what failed, which a runner writes after it.</summary>
    private sealed class StepFailed(string message, Exception failure) : XunitException(message, failure);
}
