using System.Text;
using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>The terminal's screen reading a control sequence's parameters: it keeps a sequence's first 32 and drops the
/// rest, so a binary file written out, millions of separators after an escape, grows nothing the screen keeps.</summary>
public class TerminalScreenTests
{
    [Fact]
    public void A_sequence_keeps_its_first_32_parameters_and_drops_the_rest()
    {
        TerminalScreen screen = new(80, 24);
        screen.Feed($"\e[{Repeated("0;", 31)}4mA\e[m\e[{Repeated("0;", 32)}4mB\e[m");
        Assert.True(Flags(screen, 0, 0) == CellFlags.Underline, $"A sequence's 32nd parameter was dropped: A is drawn {Flags(screen, 0, 0)}");
        Assert.True(Flags(screen, 0, 1) == CellFlags.None, $"A sequence's 33rd parameter was taken: B is drawn {Flags(screen, 0, 1)}");
    }

    [Fact]
    public void A_sequence_of_a_million_parameters_is_capped_and_leaves_nothing_held()
    {
        TerminalScreen screen = new(80, 24);
        // Bold a million times, then underline past the cap.
        byte[] million = Encoding.ASCII.GetBytes($"\e[1{Repeated(";1", 999_999)};4mx\e[m\r\n");
        screen.Feed("\e[1;1;4mw\e[m\r\n"u8);
        long before = GC.GetAllocatedBytesForCurrentThread();
        screen.Feed(million);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0,
            $"Reading a sequence of a million parameters allocated {allocated:N0} bytes: a binary file written to the terminal grows what its tab holds");
        Assert.True(screen.Lines[1].Text() == "x" && Flags(screen, 1, 0) == CellFlags.Bold,
            $"The million parameters' first were not kept and the rest dropped: '{screen.Lines[1].Text()}' is drawn {Flags(screen, 1, 0)}");

        screen.Feed("\e[0;4my"u8);
        Assert.True(Flags(screen, 2, 0) == CellFlags.Underline, $"The sequence after the million was not read whole: y is drawn {Flags(screen, 2, 0)}");
    }

    private static CellFlags Flags(TerminalScreen screen, int row, int column) => screen.Lines[row].Cells[column].Style.Flags;

    private static string Repeated(string text, int times) => string.Concat(Enumerable.Repeat(text, times));
}
