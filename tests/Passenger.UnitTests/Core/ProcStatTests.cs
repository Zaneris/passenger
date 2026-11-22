using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>How a Linux process's /proc stat is read (<see cref="ProcStat"/>): its state, parent, start, group and its
/// terminal's foreground group, after a name that may hold spaces and parentheses, with nothing allocated, as a kill reads it
/// of each process it ends until it has gone.</summary>
public class ProcStatTests
{
    /// <summary>A stat as Linux writes one: 52 fields, the start the 22nd, a line break after; its terminal's foreground
    /// group (the 8th) another's, as a shell's is while a job it started runs in front.</summary>
    private const string Written = "4242 (sleep) S 4100 4242 4100 34817 4300 4194304 98 0 0 0 0 0 0 0 20 0 1 0 98765432 8519680 230 "
        + "18446744073709551615 1 1 0 0 0 0 0 0 0 0 0 0 17 3 0 0 0 0 0 0 0 0 0 0 0 0 0\n";

    [Fact]
    public void A_linux_processs_stat_is_read_past_a_name_of_any_characters_and_none_where_it_is_cut_short() => Cases.All(
        ("A stat gives its state parent start group and its terminals foreground group", A_stat_gives_its_state_parent_start_group_and_its_terminals_foreground_group),
        ("A process with no terminal reads its terminals group as none", A_process_with_no_terminal_reads_its_terminals_group_as_none),
        ("A name with spaces or parentheses leaves the fields after it read as they are", A_name_with_spaces_or_parentheses_leaves_the_fields_after_it_read_as_they_are),
        ("A zombie says so in its state", A_zombie_says_so_in_its_state),
        ("A stat cut short or misread before the start is none", A_stat_cut_short_or_misread_before_the_start_is_none),
        ("Reading a stat allocates nothing", Reading_a_stat_allocates_nothing));

    private void A_stat_gives_its_state_parent_start_group_and_its_terminals_foreground_group()
    {
        ProcStat? stat = ProcStat.Read(Encoding.ASCII.GetBytes(Written));

        Assert.True(stat == new ProcStat('S', 4100, 98765432, 4242, 4300),
            $"The stat read as {stat}: a killed program's runtime files would be taken for another's, or left, or a shell running a job taken for one at its prompt");
    }

    private void A_process_with_no_terminal_reads_its_terminals_group_as_none()
    {
        ProcStat? stat = ProcStat.Read(Encoding.ASCII.GetBytes(Written.Replace(" 34817 4300 ", " 0 -1 ", StringComparison.Ordinal)));

        Assert.True(stat is { TerminalGroup: -1, Group: 4242 }, $"The stat of a process with no terminal read as {stat}, a terminal made up");
    }

    private void A_name_with_spaces_or_parentheses_leaves_the_fields_after_it_read_as_they_are() => Cases.Each<string>(
    [
        "(sh) -c (x)",
        "a b)",
        ")",
        "",
    ], name =>
    {
        string written = Written.Replace("(sleep)", $"({name})", StringComparison.Ordinal);

        ProcStat? stat = ProcStat.Read(Encoding.UTF8.GetBytes(written));

        Assert.True(stat == new ProcStat('S', 4100, 98765432, 4242, 4300), $"The stat of a process named \"{name}\" read as {stat}");
    });

    private void A_zombie_says_so_in_its_state()
    {
        ProcStat? stat = ProcStat.Read(Encoding.ASCII.GetBytes(Written.Replace(") S ", ") Z ", StringComparison.Ordinal)));

        Assert.True(stat?.State == 'Z', $"A zombie's stat read as {stat}: one ended would be taken for running");
    }

    private void A_stat_cut_short_or_misread_before_the_start_is_none() => Cases.Each<string>(
    [
        "4242 (sleep) S 4100 4242 4100 34817 4242 4194304 98 0 0 0 0 0 0 0 20 0 1 0",
        "4242 (sleep) S 4100 4242 4100 34817 4242 4194304 98 0 0 0 0 0 0 0 20 0 1 0\n",
        "4242 (sleep) S parent",
        "4242 (sleep)",
        "4242 (sleep",
        "",
    ], written =>
    {
        ProcStat? stat = ProcStat.Read(Encoding.ASCII.GetBytes(written));

        Assert.True(stat is null, $"\"{written}\" read as {stat}, a process's parent or start made up");
    });

    private void Reading_a_stat_allocates_nothing()
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
