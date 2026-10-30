using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>An exit code as a person is told it: the signal a code above 128 stands for on macOS and Linux, a negative
/// code's hex on Windows, any other code as it is.</summary>
public class ExitCodesTests
{
    [Fact]
    public void A_code_above_128_on_macOS_and_Linux_names_the_signal_that_ended_the_program()
    {
        Assert.Equal("exit code 137 (SIGKILL)", ExitCodes.Said(137, windows: false));
        Assert.Equal("exit code 139 (SIGSEGV)", ExitCodes.Said(139, windows: false));
        Assert.Equal("exit code 134 (SIGABRT)", ExitCodes.Said(134, windows: false));
        Assert.Equal("exit code 143 (SIGTERM)", ExitCodes.Said(143, windows: false));
        // Numbered apart on macOS and Linux (SIGBUS, SIGUSR1), so said by number rather than named wrong on one of them.
        Assert.Equal("exit code 138 (signal 10)", ExitCodes.Said(138, windows: false));
    }

    [Fact]
    public void A_code_no_signal_gives_is_said_as_it_is()
    {
        Assert.Equal("exit code 0", ExitCodes.Said(0, windows: false));
        Assert.Equal("exit code 3", ExitCodes.Said(3, windows: false));
        Assert.Equal("exit code 128", ExitCodes.Said(128, windows: false));
        Assert.Equal("exit code 200", ExitCodes.Said(200, windows: false));
        Assert.Equal("exit code 137", ExitCodes.Said(137, windows: true));
    }

    [Fact]
    public void A_negative_code_on_Windows_is_said_with_its_hex_as_its_status_codes_are_written()
    {
        Assert.Equal("exit code -1 (0xFFFFFFFF)", ExitCodes.Said(-1, windows: true));
        Assert.Equal("exit code -1073741819 (0xC0000005)", ExitCodes.Said(unchecked((int)0xC0000005), windows: true));
    }
}
