using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit.Sdk;

namespace Passenger.TestTables;

/// <summary>A test told as named steps in turn, each the check a test of its own once made, sharing what the steps before
/// it made: a window test's one window, project and server load. A step's name says what holds, so a failure names the
/// step and what no longer holds, with the steps that passed before it; each passed step and its time go to the test's
/// output. The first step that fails ends the flow, as the steps after it stand on it. Each step runs in a copy of the
/// caller's execution context, so nothing ambient a step sets reaches the next.</summary>
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
            made = InOwnContext(step);
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
            made = await InOwnContext(step);
        }
        catch (Exception failure) when (failure is not SkipException)
        {
            throw Failed(name, failure);
        }
        Pass(name, began);
        return made;
    }

    /// <summary>A step in an instance of its own of the test's class, made by <paramref name="make"/> (its constructor,
    /// with any fixture it takes) and disposed after it, as each step was once a test of its own with its own instance:
    /// for a class whose tests each set up, and put back, what the steps would otherwise share. A class disposed only
    /// asynchronously takes <see cref="StepApartAsync{TTest}(string, Func{TTest}, Func{TTest, Task})"/>.</summary>
    public static void StepApart<TTest>(string name, Func<TTest> make, Action<TTest> step) => Step(name, () =>
    {
        TTest apart = make();
        try
        {
            step(apart);
        }
        finally
        {
            Dispose(apart);
        }
    });

    /// <summary><see cref="StepApart{TTest}(string, Func{TTest}, Action{TTest})"/> for a class made with no
    /// arguments.</summary>
    public static void StepApart<TTest>(string name, Action<TTest> step) where TTest : new() => StepApart(name, () => new TTest(), step);

    /// <summary><see cref="StepApart{TTest}(string, Func{TTest}, Action{TTest})"/> for a step that waits, or a class
    /// disposed asynchronously, whose disposal it awaits.</summary>
    public static Task StepApartAsync<TTest>(string name, Func<TTest> make, Func<TTest, Task> step) => StepAsync(name, async () =>
    {
        TTest apart = make();
        try
        {
            await step(apart);
        }
        finally
        {
            await DisposeAsync(apart);
        }
    });

    /// <summary><see cref="StepApartAsync{TTest}(string, Func{TTest}, Func{TTest, Task})"/> for a class made with no
    /// arguments.</summary>
    public static Task StepApartAsync<TTest>(string name, Func<TTest, Task> step) where TTest : new() => StepApartAsync(name, () => new TTest(), step);

    /// <summary>Disposes an instance made apart, as xunit disposes a test's; one disposed only asynchronously is refused
    /// here, as nothing would put back what it set up.</summary>
    internal static void Dispose(object? apart)
    {
        if (apart is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else if (apart is IAsyncDisposable)
        {
            throw new InvalidOperationException($"{apart.GetType().Name} is disposed asynchronously: run it apart through the waiting form, which awaits its disposal");
        }
    }

    /// <summary>Disposes an instance made apart as <c>await using</c> would: its DisposeAsync where it has one, else its Dispose.</summary>
    internal static async Task DisposeAsync(object? apart)
    {
        if (apart is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
        else
        {
            (apart as IDisposable)?.Dispose();
        }
    }

    /// <summary>A step run in a copy of the caller's execution context, so nothing ambient it sets (an AsyncLocal) reaches
    /// the steps after it, as when each was a test of its own; what it makes is handed on as ever.</summary>
    internal static T InOwnContext<T>(Func<T> step)
    {
        if (ExecutionContext.Capture() is not { } context)
        {
            return step();
        }
        T made = default!;
        ExecutionContext.Run(context, _ => made = step(), null);
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
