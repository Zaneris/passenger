using Passenger.App;

namespace Passenger.UnitTests.Launch;

/// <summary>A crash kept in the log folder reads back as the error it was: when it came, its type's name and its message,
/// the window's words saying a run before this one ended on it, and the file's text whole for its details.</summary>
public class CrashTextTests
{
    private static readonly DateTime At = new(2026, 11, 14, 9, 30, 15, 250);

    [Fact]
    public void A_crash_kept_reads_back_as_its_time_type_and_message_with_its_text_whole()
    {
        string text = CrashText.Of(Thrown(new InvalidOperationException("No font is listed")), At, 4321, "0.4.0 (abc1234)");

        KeptCrash? read = CrashText.Read(text, DateTime.MinValue);

        Assert.True(read is not null && read.At == At && read.Kind == "InvalidOperationException"
            && read.Message == "Passenger ended on this error as it last ran: No font is listed",
            $"The next start would say the crash otherwise than it came: {read}");
        Assert.True(read.Details == text.Trim() && read.Details.StartsWith("2026-11-14 09:30:15.250 [4321] Passenger 0.4.0 (abc1234) ended on this error:", StringComparison.Ordinal)
            && read.Details.Contains(nameof(Thrown), StringComparison.Ordinal),
            $"The error's details would not hold the file's text, its version and where it was thrown:\n{read.Details}");
    }

    [Fact]
    public void An_exception_with_no_message_inner_exceptions_and_lines_written_on_Windows_reads_as_its_outer_type()
    {
        string text = CrashText.Of(new AggregateException(new ArgumentException("inner")), At, 1, "0.4.0 (no commit)").ReplaceLineEndings("\r\n");
        string bare = CrashText.Of(new NotSupportedException(string.Empty), At, 1, "0.4.0 (no commit)");

        KeptCrash? read = CrashText.Read(text, DateTime.MinValue);
        KeptCrash? plain = CrashText.Read(bare, DateTime.MinValue);

        Assert.True(read is { Kind: "AggregateException" } && read.Message.StartsWith("Passenger ended on this error as it last ran: One or more errors occurred.", StringComparison.Ordinal),
            $"An exception holding another would be said as {read}");
        Assert.True(plain is { Kind: "NotSupportedException", Message: "Passenger ended on this error as it last ran" },
            $"An exception with no message would be said as {plain}");
    }

    [Fact]
    public void A_file_holding_no_crash_says_nothing_and_one_with_no_time_takes_the_files_own()
    {
        DateTime written = new(2026, 11, 13, 22, 0, 0);

        KeptCrash? edited = CrashText.Read("System.IO.IOException: Disk full\n   at Somewhere()", written);

        Assert.Null(CrashText.Read(string.Empty, written));
        Assert.Null(CrashText.Read("\n\n", written));
        Assert.Null(CrashText.Read(CrashText.Of(string.Empty, At, 1, "0.4.0").Trim(), written));
        Assert.True(edited is { Kind: "IOException", Message: "Passenger ended on this error as it last ran: Disk full" } && edited.At == written,
            $"A crash's text with no first line of its own would be said as {edited}");
    }

    private static Exception Thrown(Exception error)
    {
        try
        {
            throw error;
        }
        catch (Exception thrown) when (thrown == error)
        {
            return thrown;
        }
    }
}
