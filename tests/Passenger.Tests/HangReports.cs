using System.Diagnostics;
using System.Reflection;
using Passenger.Core;
using Xunit.v3;

[assembly: Passenger.Tests.HangReports]

namespace Passenger.Tests;

/// <summary>What a hung test leaves to read: once no test has begun or ended for a minute short of the run's limit on one
/// (<see cref="Limits.Hang"/>), when blame takes the test host for hung and ends it, a report goes into the folder
/// <see cref="Variable"/> names (a run's results', which CI's job keeps): the tests running, then, a part at a time so a
/// part that never comes leaves those before it, each .NET program under the tests with its threads' managed stacks and
/// what the IDE stood on as a wait that gave up says it (<see cref="Ui.StoodParts"/>). Blame's dumps beside it give the
/// test host's own threads, but on macOS, where blame would dump each .NET process whole whatever type is asked (about
/// 7 GB each), so there the host ends itself once its report is written. Nothing is watched where the variable is unset.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class HangReportsAttribute : BeforeAfterTestAttribute
{
    [IdentifierHome]
    public const string Variable = "PASSENGER_TEST_HANGS";

    /// <summary>How long before blame's limit the report is written: time for a few programs' stacks to be read.</summary>
    private static readonly TimeSpan Ahead = TimeSpan.FromMinutes(1);

    private static readonly Lock Gate = new();

    /// <summary>The tests begun and not ended, by their ids, with when each began.</summary>
    private static readonly Dictionary<string, (string Name, long Began)> Running = [];

    /// <summary>Set as a test begins or ends, which starts blame's wait again.</summary>
    private static readonly AutoResetEvent Moved = new(false);

    private static long last = Stopwatch.GetTimestamp();
    private static string? lastEnded;
    private static bool watching;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test) =>
        Move(() => Running[test.UniqueID] = (test.TestDisplayName, Stopwatch.GetTimestamp()));

    public override void After(MethodInfo methodUnderTest, IXunitTest test) => Move(() =>
    {
        Running.Remove(test.UniqueID);
        lastEnded = test.TestDisplayName;
    });

    private static void Move(Action change)
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } folder)
        {
            return;
        }
        lock (Gate)
        {
            change();
            last = Stopwatch.GetTimestamp();
            if (!watching)
            {
                watching = true;
                // As long-lived as the test host, which blame ends at a hang: nothing waits for it.
                Passenger.App.IdeErrors.Observe(BackgroundWork.UnheldThread(() => Watch(folder)));
            }
        }
        Moved.Set();
    }

    /// <summary>Waits for blame's limit less <see cref="Ahead"/> to pass with no test begun or ended, writes the report once,
    /// then waits for the next test to begin or end before it waits again.</summary>
    private static void Watch(string folder)
    {
        while (true)
        {
            TimeSpan left;
            lock (Gate)
            {
                left = Limits.Hang - Ahead - Stopwatch.GetElapsedTime(last);
            }
            if (left > TimeSpan.Zero)
            {
                Moved.WaitOne(left);
                continue;
            }
            Write(folder);
            if (OperatingSystem.IsMacOS())
            {
                Environment.FailFast($"No test began or ended for {Durations.Said(Limits.Hang - Ahead)}: the test host ends itself before blame "
                    + $"would dump it whole, its report in {folder}");
            }
            Moved.WaitOne();
        }
    }

    private static void Write(string folder)
    {
        string file = Path.Combine(folder, $"hang-{Environment.ProcessId}.txt");
        TimeSpan quiet;
        (string Name, long Began)[] running;
        string? ended;
        lock (Gate)
        {
            quiet = Stopwatch.GetElapsedTime(last);
            running = [.. Running.Values.OrderBy(r => r.Began)];
            ended = lastEnded;
        }
        Append(file, $"No test of the test host {Environment.ProcessId} began or ended for {Durations.Said(quiet)}, {Durations.Said(Ahead)} short of "
            + $"the run's limit on a test ({Durations.Said(Limits.Hang)}), past which blame ends it as hung.\n"
            + (running.Length == 0
                ? $"No test was running: the hang lies between tests (a class's fixture, or what ran after {ended ?? "no test"}).\n"
                : $"Running:\n{string.Join('\n', running.Select(r => $"  {r.Name}, for {Durations.Said(Stopwatch.GetElapsedTime(r.Began))}"))}\n"));
        foreach (int id in Processes.Descendants(Environment.ProcessId))
        {
            Append(file, () => Stacks(id));
        }
        foreach (Func<string> part in Ui.StoodParts())
        {
            Append(file, part);
        }
    }

    /// <summary>Where the managed threads of a .NET program under the tests stood, said only in a hang's report.</summary>
    private static string Stacks(int id) => ManagedStacks.Describe(Processes.Describe(id), id);

    /// <summary>A part read as it comes, its failure written in its place: a part that never comes leaves those before it.</summary>
    private static void Append(string file, Func<string> part)
    {
        string text;
        try
        {
            text = part();
        }
        catch (Exception failed)
        {
            text = $"\nA part of the report could not be read: {failed}";
        }
        Append(file, text);
    }

    private static void Append(string file, string text)
    {
        if (text.Length > 0)
        {
            File.AppendAllText(file, text.EndsWith('\n') ? text : text + "\n");
        }
    }
}
