using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Words made one Windows command line, as a program run in the Terminal and a program dbgshim starts get it: quoted
/// only where they must be, so cmd.exe reads its /c command and a .bat its arguments as .NET's own start hands them, and
/// split back by the C runtime's rules into the same words.</summary>
public sealed class WindowsCommandLineTests
{
    [Fact]
    public void A_command_for_cmd_with_its_operators_and_spaces_is_one_quoted_word_after_a_bare_switch()
    {
        Check(["/c", "set GIT_PREFIX& echo x"], "/c \"set GIT_PREFIX& echo x\"",
            "cmd.exe would take a quoted /c for no switch, or its command apart at its spaces, and answer that it is not a command");
        Check(["/c", "a& b"], "/c \"a& b\"", "cmd.exe would read \"a& b\" as other words than the run configuration gave");
    }

    [Fact]
    public void A_trailing_backslash_stays_the_words_own_and_never_escapes_its_closing_quote()
    {
        Check(["C:\\out\\"], "C:\\out\\", "A folder ending in a backslash would reach the program with a quote in place of it");
        Check(["C:\\my out\\", "next"], "\"C:\\my out\\\\\" next",
            "A folder with a space ending in a backslash would escape its closing quote, running into the next word");
    }

    [Fact]
    public void An_inner_quote_is_escaped_with_the_backslashes_before_it_doubled()
    {
        Check(["say \"hi\""], "\"say \\\"hi\\\"\"", "A quote inside a word would end it or reach the program doubled");
        Check(["a\\\"b"], "\"a\\\\\\\"b\"", "A backslash before a quote would escape the quote and be lost");
        Check(["say\"hi"], "\"say\\\"hi\"", "A quote inside a word with no space would end the word's text early");
    }

    [Fact]
    public void A_plain_word_goes_bare_with_its_backslashes_and_an_empty_one_as_two_quotes()
    {
        Check(["-NoLogo", "C:\\Program\\x.exe", "", "last"], "-NoLogo C:\\Program\\x.exe \"\" last",
            "A plain word would reach a .bat in quotes, which it keeps, or an empty word would be lost");
    }

    /// <summary>The words' command line is <paramref name="expected"/>, and splits back into them.</summary>
    private static void Check(string[] words, string expected, string consequence)
    {
        string line = WindowsCommandLine.Of(words);
        Assert.True(line == expected, $"[{string.Join(", ", words)}] made the command line {line}, not {expected}: {consequence}");
        List<string> split = Split(line);
        Assert.True(split.SequenceEqual(words), $"{line} splits back into [{string.Join(", ", split)}], not the words it was made of: {consequence}");
    }

    /// <summary>A command line's words after a program's name, split as the C runtime splits them: white space apart outside
    /// quotes, backslashes literal unless before a quote, where each pair is one backslash and an odd one escapes the quote,
    /// and two quotes inside quotes one quote.</summary>
    private static List<string> Split(string line)
    {
        List<string> words = [];
        int i = 0;
        while (true)
        {
            while (i < line.Length && line[i] is ' ' or '\t')
            {
                i++;
            }
            if (i == line.Length)
            {
                return words;
            }
            StringBuilder word = new();
            bool quoted = false;
            while (i < line.Length && (quoted || line[i] is not (' ' or '\t')))
            {
                int backslashes = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    backslashes++;
                    i++;
                }
                if (i < line.Length && line[i] == '"')
                {
                    word.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        word.Append('"');
                    }
                    else if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        word.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                    i++;
                }
                else
                {
                    word.Append('\\', backslashes);
                    if (backslashes == 0)
                    {
                        word.Append(line[i]);
                        i++;
                    }
                }
            }
            words.Add(word.ToString());
        }
    }
}
