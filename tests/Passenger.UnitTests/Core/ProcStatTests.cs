using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>How a Linux process's /proc stat is read (<see cref="ProcStat"/>): its state, parent and start, after a name
/// that may hold spaces and parentheses, with nothing allocated, as a kill reads it of each process it ends until it has gone.</summary>
public class ProcStatTests
{
    /// <summary>A stat as Linux writes one: 52 fields, the start the 22nd, a line break after.</summary>
    private const string Written = "4242 (sleep) S 4100 4242 4100 34817 4242 4194304 98 0 0 0 0 0 0 0 20 0 1 0 98765432 8519680 230 "
        + "18446744073709551615 1 1 0 0 0 0 0 0 0 0 0 0 17 3 0 0 0 0 0 0 0 0 0 0 0 0 0\n";

    [Fact]
    public void A_stat_gives_its_state_parent_and_start()
    {
        ProcStat? stat = ProcStat.Read(Encoding.ASCII.GetBytes(Written));

        Assert.True(stat == new ProcStat('S', 4100, 98765432),
            $"The stat read as {stat}: a killed program's runtime files would be taken for another's, or left");
    }

    [Theory]
    [InlineData("(sh) -c (x)")]
    [InlineData("a b)")]
    [InlineData(")")]
    [InlineData("")]
    public void A_name_with_spaces_or_parentheses_leaves_the_fields_after_it_read_as_they_are(string name)
    {
        string written = Written.Replace("(sleep)", $"({name})", StringComparison.Ordinal);

        ProcStat? stat = ProcStat.Read(Encoding.UTF8.GetBytes(written));

        Assert.True(stat == new ProcStat('S', 4100, 98765432), $"The stat of a process named \"{name}\" read as {stat}");
    }

    [Fact]
    public void A_zombie_says_so_in_its_state()
    {
        ProcStat? stat = ProcStat.Read(Encoding.ASCII.GetBytes(Written.Replace(") S ", ") Z ", StringComparison.Ordinal)));

        Assert.True(stat?.State == 'Z', $"A zombie's stat read as {stat}: one ended would be taken for running");
    }

    [Theory]
    [InlineData("4242 (sleep) S 4100 4242 4100 34817 4242 4194304 98 0 0 0 0 0 0 0 20 0 1 0")]
    [InlineData("4242 (sleep) S 4100 4242 4100 34817 4242 4194304 98 0 0 0 0 0 0 0 20 0 1 0\n")]
    [InlineData("4242 (sleep) S parent")]
    [InlineData("4242 (sleep)")]
    [InlineData("4242 (sleep")]
    [InlineData("")]
    public void A_stat_cut_short_or_misread_before_the_start_is_none(string written)
    {
        ProcStat? stat = ProcStat.Read(Encoding.ASCII.GetBytes(written));

        Assert.True(stat is null, $"\"{written}\" read as {stat}, a process's parent or start made up");
    }

    [Fact]
    public void Reading_a_stat_allocates_nothing()
    {
        byte[] written = Encoding.ASCII.GetBytes(Written);
        ProcStat.Read(written);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 1000; round++)
        {
            ProcStat.Read(written);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"Reading 1,000 stats allocated {allocated:N0} bytes, which a kill pays for each process it ends, every 5 ms until it has gone");
    }
}
