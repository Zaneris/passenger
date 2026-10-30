using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>The heap dumps a sweep of the temporary folder deletes: those no snapshot has under way, a Passenger's still
/// running kept, and one whose name says not whose kept until it is an hour old.</summary>
public sealed class DumpSweepTests : IDisposable
{
    private const int Dead = 1_000_001;
    private const int Live = 1_000_002;
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly string folder = Directory.CreateTempSubdirectory("passenger-unit-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void A_sweep_deletes_the_dumps_left_and_keeps_those_under_way_and_a_running_Passengers()
    {
        int self = Environment.ProcessId;
        string underWay = DumpTool.Named(folder, 11);
        string mineLeft = Dump($"passenger-heap-{self}-12-aaaaaaaa.dmp");
        string deadLeft = Dump($"passenger-heap-{Dead}-13-bbbbbbbb.dmp");
        string living = Dump($"passenger-heap-{Live}-14-cccccccc.dmp");
        string unnamedOld = Dump("passenger-heap-15-dddddddd.dmp", Now - TimeSpan.FromHours(2));
        string unnamedNew = Dump("passenger-heap-16-eeeeeeee.dmp", Now - TimeSpan.FromMinutes(10));
        string other = Dump("other.dmp");
        File.WriteAllText(underWay, "dump");

        DumpTool.Sweep(folder, self, process => process == Live, Now);
        Assert.Equal(new[] { living, other, underWay, unnamedNew }.Order(StringComparer.Ordinal), Left());
        Assert.DoesNotContain(mineLeft, Left());
        Assert.DoesNotContain(deadLeft, Left());
        Assert.DoesNotContain(unnamedOld, Left());

        // Once its snapshot is done with it, it goes; had its delete been refused, the next sweep would take it.
        Assert.True(DumpTool.DeleteAfterAsync(underWay, Task.CompletedTask).IsCompletedSuccessfully);
        Assert.DoesNotContain(underWay, Left());
    }

    private string Dump(string name, DateTime? written = null)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "dump");
        File.SetLastWriteTimeUtc(path, written ?? Now);
        return path;
    }

    private List<string> Left() => [.. Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal)];
}
