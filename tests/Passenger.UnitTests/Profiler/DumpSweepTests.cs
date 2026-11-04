using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>The heap dumps a sweep of the folder of dumps deletes: those no snapshot has under way, a Passenger's still
/// running kept, one whose name says not whose kept until it is an hour old; that folder the user's own under the temporary
/// folder, so the sweep reads the dumps alone.</summary>
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
    /// <summary>A dump is named in the user's own folder under the temporary folder, made theirs alone, so a sweep reads the
    /// dumps alone: the hundred other files of the temporary folder stay as they were, unread by it.</summary>
    [Fact]
    public void A_dump_lies_in_the_users_own_folder_under_the_temporary_folder_which_the_sweep_reads_alone()
    {
        string temp = Path.Combine(folder, "temp");
        Directory.CreateDirectory(temp);
        for (int i = 0; i < 100; i++)
        {
            File.WriteAllText(Path.Combine(temp, $"passenger-heap-{Dead}-{i}-ffffffff.dmp"), "another folder's");
        }
        string own = DumpTool.FolderIn(temp);
        Assert.Equal(temp, Path.GetDirectoryName(own));
        Assert.Equal(own, DumpTool.FolderIn(temp));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(own));
        }
        string left = Path.Combine(own, $"passenger-heap-{Dead}-17-aaaaaaaa.dmp");
        File.WriteAllText(left, "dump");
        Assert.Equal(own, Path.GetDirectoryName(DumpTool.Named(own, 18)));

        DumpTool.Sweep(own, Environment.ProcessId, _ => false, Now);
        Assert.False(File.Exists(left), "The sweep left a dead Passenger's dump in the folder of dumps");
        Assert.Equal(100, Directory.GetFiles(temp).Length);
    }
}
