using Passenger.App.Run;
using Passenger.Dap;

namespace Passenger.UnitTests.Run;

/// <summary>What a program's output says of its needing a terminal, from the exceptions .NET writes as a program's input
/// and output are pipes, in pieces as they come on either stream, unhandled, written by the program or inner; and nothing
/// from any other failure, the console's streams' own among them.</summary>
public class TerminalNeedsTests
{
    private const string ReadKey = """
        Unhandled exception. System.InvalidOperationException: Cannot read keys when either application does not have a console or when console input has been redirected. Try Console.Read.
           at System.ConsolePal.ReadKey(Boolean intercept)
           at Program.<Main>$(String[] args) in /src/Keys/Program.cs:line 4

        """;

    [Fact]
    public void A_programs_refusal_of_input_or_a_console_without_a_terminal_names_the_call_needing_one() => Cases.All(
        ("Console ReadKey refusing the input of pipes is a need however its text comes", Console_ReadKey_refusing_the_input_of_pipes_is_a_need_however_its_text_comes),
        ("The runtimes and Spectre Consoles refusals name the call needing a terminal", The_runtimes_and_Spectre_Consoles_refusals_name_the_call_needing_a_terminal),
        ("Other failures and the words alone need no terminal", Other_failures_and_the_words_alone_need_no_terminal),
        ("A line past the longest read is cut and the next line read whole", A_line_past_the_longest_read_is_cut_and_the_next_line_read_whole));

    private void Console_ReadKey_refusing_the_input_of_pipes_is_a_need_however_its_text_comes()
    {
        TerminalNeeds whole = new();
        Assert.True(whole.Hear(ReadKey, error: true), "ReadKey's refusal of a pipe was not heard, so the Run window offers no terminal");
        Assert.Equal("Console.ReadKey", whole.Need);

        // A piece at a time, across its line's middle, with Windows' line ends.
        TerminalNeeds pieces = new();
        string crlf = ReadKey.Replace("\n", "\r\n", StringComparison.Ordinal);
        for (int at = 0; at < crlf.Length; at += 7)
        {
            pieces.Hear(crlf.AsSpan(at, Math.Min(7, crlf.Length - at)), error: true);
        }
        Assert.Equal("Console.ReadKey", pieces.Need);
    }

    private void The_runtimes_and_Spectre_Consoles_refusals_name_the_call_needing_a_terminal() => Cases.Each<(string written, string need)>(
    [
        ("Name? System.InvalidOperationException: Cannot read keys when either application does not have a console or when console input has been redirected. Try Console.Read.\n", "Console.ReadKey"),
        ("Unhandled exception. System.InvalidOperationException: Cannot see if a key has been pressed when either application does not have a console or when console input has been redirected from a file. Try Console.In.Peek.\n"
            + "   at System.Console.get_KeyAvailable()\n", "Console.KeyAvailable"),
        ("Sure? [y/n] (y): Unhandled exception. System.InvalidOperationException: Failed to read input in non-interactive mode.\n"
            + "   at Spectre.Console.DefaultInput.ReadKeyAsync(Boolean intercept, CancellationToken cancellationToken) in /_/src/Spectre.Console/Internal/DefaultInput.cs:line 36\n",
            "Spectre.Console's input"),
        ("Unhandled exception. System.IO.IOException: Das Handle ist ungültig.\n"
            + "   at System.ConsolePal.GetCursorPosition()\n"
            + "   at System.Console.get_CursorLeft()\n"
            + "   at Program.<Main>$(String[] args)\n", "Console.CursorLeft"),
    ], row =>
    {
        (string written, string need) = row;
        TerminalNeeds needs = new();
        Assert.True(needs.Hear(written, error: true), $"No need was heard in:\n{written}");
        Assert.Equal(need, needs.Need);
    });

    private void Other_failures_and_the_words_alone_need_no_terminal() => Cases.Each<string>(
    [
        "Unhandled exception. System.IO.IOException: Broken pipe\n"
            + "   at System.ConsolePal.UnixConsoleStream.Write(ReadOnlySpan`1 buffer)\n"
            + "   at System.IO.StreamWriter.Flush(Boolean flushStream, Boolean flushEncoder)\n",
        "Unhandled exception. System.IO.IOException: The process cannot access the file because it is being used by another process.\n"
            + "   at System.IO.FileSystem.CopyFile(String sourceFullPath, String destFullPath, Boolean overwrite)\n"
            + "   at Program.<Main>$(String[] args)\n",
        "Unhandled exception. System.InvalidOperationException: Sequence contains no elements\n   at System.Linq.ThrowHelper.ThrowNoElementsException()\n",
        "Cannot read keys when either application does not have a console or when console input has been redirected. Try Console.Read.\n",
    ], written =>
    {
        TerminalNeeds needs = new();
        Assert.False(needs.Hear(written, error: true), $"A need for {needs.Need} was heard in:\n{written}");
    });

    [Fact]
    public void An_exception_read_from_either_stream_or_where_a_debugger_stopped_is_read_as_dotnet_writes_it() => Cases.All(
        ("Each stream is read apart so output between an exceptions lines breaks none", Each_stream_is_read_apart_so_output_between_an_exceptions_lines_breaks_none),
        ("An exception a debugger stopped at is read as dotnet writes it its inner and its frames among it", An_exception_a_debugger_stopped_at_is_read_as_dotnet_writes_it_its_inner_and_its_frames_among_it),
        ("An exception a debugger stopped at for anything else needs no terminal", An_exception_a_debugger_stopped_at_for_anything_else_needs_no_terminal));

    private void Each_stream_is_read_apart_so_output_between_an_exceptions_lines_breaks_none()
    {
        TerminalNeeds needs = new();
        needs.Hear("Unhandled exception. System.IO.IOException: The handle is invalid.\n   at System.ConsolePal.Clear()\n", error: true);
        needs.Hear("tick\n", error: false);
        needs.Hear("   at System.Console.Clear()\n   at Program.<Main>$(String[] args)\n", error: true);
        Assert.Equal("Console.Clear", needs.Need);
    }

    private void An_exception_a_debugger_stopped_at_is_read_as_dotnet_writes_it_its_inner_and_its_frames_among_it()
    {
        DapExceptionDetails readKey = new()
        {
            TypeName = "InvalidOperationException",
            FullTypeName = "System.InvalidOperationException",
            Message = "Cannot read keys when either application does not have a console or when console input has been redirected. Try Console.Read.",
            StackTrace = "   at System.ConsolePal.ReadKey(Boolean intercept)\n   at Program.<Main>$(String[] args) in /src/Keys/Program.cs:line 2",
        };
        Assert.Equal("Console.ReadKey", TerminalNeeds.Thrown(readKey));

        // Windows' console call on a handle that is no console's, its frames the stop's, and inside another exception.
        DapExceptionDetails clear = new()
        {
            TypeName = "IOException",
            FullTypeName = "System.IO.IOException",
            Message = "The handle is invalid.",
            StackTrace = "   at System.ConsolePal.GetBufferInfo(Boolean throwOnNoConsole, Boolean& succeeded)\r\n   at System.ConsolePal.Clear()\r\n   at System.Console.Clear()\r\n   at Program.<Main>$(String[] args)",
        };
        Assert.Equal("Console.Clear", TerminalNeeds.Thrown(clear));
        Assert.Equal("Console.Clear", TerminalNeeds.Thrown(new DapExceptionDetails
        {
            TypeName = "AggregateException",
            FullTypeName = "System.AggregateException",
            Message = "One or more errors occurred. (The handle is invalid.)",
            InnerException = [clear],
        }));
    }

    private void An_exception_a_debugger_stopped_at_for_anything_else_needs_no_terminal()
    {
        Assert.Null(TerminalNeeds.Thrown(new DapExceptionDetails
        {
            TypeName = "IOException",
            FullTypeName = "System.IO.IOException",
            Message = "Broken pipe",
            StackTrace = "   at System.ConsolePal.UnixConsoleStream.Write(ReadOnlySpan`1 buffer)\n   at Program.<Main>$(String[] args)",
        }));
        // The words alone, of a type the program made, are no refusal of the runtime's.
        Assert.Null(TerminalNeeds.Thrown(new DapExceptionDetails
        {
            TypeName = "Refused",
            Message = "Cannot read keys when either application does not have a console or when console input has been redirected. Try Console.Read.",
        }));
    }

    private void A_line_past_the_longest_read_is_cut_and_the_next_line_read_whole()
    {
        TerminalNeeds needs = new();
        needs.Hear(new string('x', 5000), error: false);
        needs.Hear(new string('x', 5000) + "\n", error: false);
        Assert.True(needs.Hear(ReadKey, error: false), "A need after a long line was not heard");
    }
}
