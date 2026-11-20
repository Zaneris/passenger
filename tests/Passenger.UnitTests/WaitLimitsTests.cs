using System.Reflection;

namespace Passenger.UnitTests;

/// <summary>A unit test waits for what it checks with no limit of its own: a wait held to a time fails on a machine busy
/// enough, where the code was right, and one that waits for good is named by the run's own limit on a test
/// (<c>--blame-hang-timeout</c>, test-shards.py's HANG, on CI and in <c>just test</c>).</summary>
public sealed class WaitLimitsTests
{
    /// <summary>The waits a time may be given to, by their names: a task's, an event's, a thread's, a queue's, a process's, a
    /// lock's, a token's own end, and a sleep or delay.</summary>
    private static readonly HashSet<string> Waits = new(StringComparer.Ordinal)
    {
        "Wait", "WaitAsync", "WaitOne", "WaitAll", "WaitAny", "Join", "CancelAfter", "TryTake", "TryAdd", "SpinUntil",
        "WaitForExit", "TryEnter", "Sleep", "Delay",
    };

    /// <summary>No method the unit tests write gives a wait a time, or makes a token that cancels itself after one, read from
    /// their built code, a lambda's and a state machine's included; Passenger's code they compile in keeps its own limits.</summary>
    [Fact]
    public void No_unit_test_holds_a_wait_to_a_time_of_its_own()
    {
        List<string> timed = [];
        int read = 0;
        foreach (Type type in typeof(WaitLimitsTests).Assembly.GetTypes().Where(type => type.Namespace?.StartsWith(nameof(Passenger) + "." + nameof(UnitTests), StringComparison.Ordinal) == true))
        {
            foreach (MethodBase method in BuiltCalls.Declared(type))
            {
                foreach ((MethodBase called, _) in BuiltCalls.Of(method))
                {
                    read++;
                    if (GivenATime(called))
                    {
                        timed.Add($"{type.FullName}.{method.Name}: {called.DeclaringType?.Name}.{called.Name}({string.Join(", ", called.GetParameters().Select(p => p.ParameterType.Name))})");
                    }
                }
            }
        }
        Assert.True(read > 0, "No call was read from the built tests, so their waits were not looked at");
        Assert.True(timed.Count == 0, "These waits are held to a time of their own, which a busy machine runs past where the code was right; "
            + "wait for what the test checks, with the test's token alone:\n" + string.Join('\n', timed.Distinct()));
    }

    /// <summary>Whether a call waits up to a time it is given, or makes a token that cancels itself after one.</summary>
    private static bool GivenATime(MethodBase called) =>
        (Waits.Contains(called.Name) || called is ConstructorInfo && called.DeclaringType == typeof(CancellationTokenSource))
        && called.GetParameters().Any(p => p.ParameterType == typeof(TimeSpan) || p.ParameterType == typeof(int) && p.Name?.StartsWith("milliseconds", StringComparison.Ordinal) == true);
}
