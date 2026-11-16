using Passenger.App.Run;

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
    public void Console_ReadKey_refusing_the_input_of_pipes_is_a_need_however_its_text_comes()
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

    [Theory]
    // Written by the program itself, after words of its own on the line.
    [InlineData("Name? System.InvalidOperationException: Cannot read keys when either application does not have a console or when console input has been redirected. Try Console.Read.\n", "Console.ReadKey")]
    // An inner exception, an AggregateException's whose own line says it in other words.
    [InlineData("Unhandled exception. System.AggregateException: One or more errors occurred. (Cannot read keys when either application does not have a console or when console input has been redirected. Try Console.Read.)\n"
        + " ---> System.InvalidOperationException: Cannot read keys when either application does not have a console or when console input has been redirected. Try Console.Read.\n", "Console.ReadKey")]
    [InlineData("Unhandled exception. System.InvalidOperationException: Cannot see if a key has been pressed when either application does not have a console or when console input has been redirected from a file. Try Console.In.Peek.\n"
        + "   at System.Console.get_KeyAvailable()\n", "Console.KeyAvailable")]
    [InlineData("Unhandled exception. System.NotSupportedException: Cannot show selection prompt since the current terminal isn't interactive.\n"
        + "   at Spectre.Console.ListPrompt`1.Show(ListPromptTree`1 tree, Func`2 converter, SelectionMode selectionMode) in /_/src/Spectre.Console/Prompts/List/ListPrompt.cs:line 29\n",
        "Spectre.Console's selection prompt")]
    [InlineData("Sure? [y/n] (y): Unhandled exception. System.InvalidOperationException: Failed to read input in non-interactive mode.\n"
        + "   at Spectre.Console.DefaultInput.ReadKeyAsync(Boolean intercept, CancellationToken cancellationToken) in /_/src/Spectre.Console/Internal/DefaultInput.cs:line 36\n",
        "Spectre.Console's input")]
    // Windows' console calls on a handle that is no console's: the outermost of the console's frames is the program's call.
    [InlineData("Unhandled exception. System.IO.IOException: The handle is invalid.\n"
        + "   at System.ConsolePal.GetBufferInfo(Boolean throwOnNoConsole, Boolean& succeeded)\n"
        + "   at System.ConsolePal.Clear()\n"
        + "   at System.Console.Clear()\n"
        + "   at Program.<Main>$(String[] args) in C:\\src\\Keys\\Program.cs:line 5\n", "Console.Clear")]
    [InlineData("Unhandled exception. System.IO.IOException: Das Handle ist ungültig.\n"
        + "   at System.ConsolePal.GetCursorPosition()\n"
        + "   at System.Console.get_CursorLeft()\n"
        + "   at Program.<Main>$(String[] args)\n", "Console.CursorLeft")]
    public void The_runtimes_and_Spectre_Consoles_refusals_name_the_call_needing_a_terminal(string written, string need)
    {
        TerminalNeeds needs = new();
        Assert.True(needs.Hear(written, error: true), $"No need was heard in:\n{written}");
        Assert.Equal(need, needs.Need);
    }

    [Theory]
    // A pipe closed under the console's stream is no terminal's want.
    [InlineData("Unhandled exception. System.IO.IOException: Broken pipe\n"
        + "   at System.ConsolePal.UnixConsoleStream.Write(ReadOnlySpan`1 buffer)\n"
        + "   at System.IO.StreamWriter.Flush(Boolean flushStream, Boolean flushEncoder)\n")]
    [InlineData("Unhandled exception. System.IO.IOException: The process cannot access the file because it is being used by another process.\n"
        + "   at System.IO.FileSystem.CopyFile(String sourceFullPath, String destFullPath, Boolean overwrite)\n"
        + "   at Program.<Main>$(String[] args)\n")]
    [InlineData("Unhandled exception. System.InvalidOperationException: Failed to read input in non-interactive mode.\n"
        + "   at Shop.Input.Read()\n")]
    [InlineData("Unhandled exception. System.InvalidOperationException: Sequence contains no elements\n   at System.Linq.ThrowHelper.ThrowNoElementsException()\n")]
    [InlineData("Cannot read keys when either application does not have a console or when console input has been redirected. Try Console.Read.\n")]
    public void Other_failures_and_the_words_alone_need_no_terminal(string written)
    {
        TerminalNeeds needs = new();
        Assert.False(needs.Hear(written, error: true), $"A need for {needs.Need} was heard in:\n{written}");
    }

    [Fact]
    public void Each_stream_is_read_apart_so_output_between_an_exceptions_lines_breaks_none()
    {
        TerminalNeeds needs = new();
        needs.Hear("Unhandled exception. System.IO.IOException: The handle is invalid.\n   at System.ConsolePal.Clear()\n", error: true);
        needs.Hear("tick\n", error: false);
        needs.Hear("   at System.Console.Clear()\n   at Program.<Main>$(String[] args)\n", error: true);
        Assert.Equal("Console.Clear", needs.Need);
    }

    [Fact]
    public void A_line_past_the_longest_read_is_cut_and_the_next_line_read_whole()
    {
        TerminalNeeds needs = new();
        needs.Hear(new string('x', 5000), error: false);
        needs.Hear(new string('x', 5000) + "\n", error: false);
        Assert.True(needs.Hear(ReadKey, error: false), "A need after a long line was not heard");
    }
}
