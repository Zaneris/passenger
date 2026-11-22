using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Words made one Windows command line, as a program run in the Terminal and a program dbgshim starts get it: quoted
/// only where they must be, so cmd.exe reads its /c command as .NET's own start hands it, and split back by the C runtime's
/// rules into the same words; and a batch file's words through cmd.exe, which reads them as given, as does what it runs.</summary>
public sealed class WindowsCommandLineTests
{
    [Fact]
    public void Words_are_quoted_on_a_windows_command_line_only_where_they_must_be() => Cases.All(
        ("A command for cmd with its operators and spaces is one quoted word after a bare switch", A_command_for_cmd_with_its_operators_and_spaces_is_one_quoted_word_after_a_bare_switch),
        ("A trailing backslash stays the words own and never escapes its closing quote", A_trailing_backslash_stays_the_words_own_and_never_escapes_its_closing_quote),
        ("An inner quote is escaped with the backslashes before it doubled", An_inner_quote_is_escaped_with_the_backslashes_before_it_doubled),
        ("A plain word goes bare with its backslashes and an empty one as two quotes", A_plain_word_goes_bare_with_its_backslashes_and_an_empty_one_as_two_quotes));

    private void A_command_for_cmd_with_its_operators_and_spaces_is_one_quoted_word_after_a_bare_switch()
    {
        Check(["/c", "set GIT_PREFIX& echo x"], "/c \"set GIT_PREFIX& echo x\"",
            "cmd.exe would take a quoted /c for no switch, or its command apart at its spaces, and answer that it is not a command");
        Check(["/c", "a& b"], "/c \"a& b\"", "cmd.exe would read \"a& b\" as other words than the run configuration gave");
    }

    private void A_trailing_backslash_stays_the_words_own_and_never_escapes_its_closing_quote()
    {
        Check(["C:\\out\\"], "C:\\out\\", "A folder ending in a backslash would reach the program with a quote in place of it");
        Check(["C:\\my out\\", "next"], "\"C:\\my out\\\\\" next",
            "A folder with a space ending in a backslash would escape its closing quote, running into the next word");
    }

    private void An_inner_quote_is_escaped_with_the_backslashes_before_it_doubled()
    {
        Check(["say \"hi\""], "\"say \\\"hi\\\"\"", "A quote inside a word would end it or reach the program doubled");
        Check(["a\\\"b"], "\"a\\\\\\\"b\"", "A backslash before a quote would escape the quote and be lost");
        Check(["say\"hi"], "\"say\\\"hi\"", "A quote inside a word with no space would end the word's text early");
    }

    private void A_plain_word_goes_bare_with_its_backslashes_and_an_empty_one_as_two_quotes()
    {
        Check(["-NoLogo", "C:\\Program\\x.exe", "", "last"], "-NoLogo C:\\Program\\x.exe \"\" last",
            "A plain word would reach a .bat in quotes, which it keeps, or an empty word would be lost");
    }

    [Fact]
    public void A_batch_files_words_reach_its_program_through_cmd_as_given_so_nothing_in_them_runs() => Cases.All(
        ("A batch files words with cmds operators and variables go through cmd quoted as given", A_batch_files_words_with_cmds_operators_and_variables_go_through_cmd_quoted_as_given),
        ("A batch files words with quotes escapes and backslashes reach the program it runs as given", A_batch_files_words_with_quotes_escapes_and_backslashes_reach_the_program_it_runs_as_given),
        ("A batch file is run by its path in quotes with extensions on and delayed expansion off", A_batch_file_is_run_by_its_path_in_quotes_with_extensions_on_and_delayed_expansion_off),
        ("A line break in a batch files word is refused as no line of cmds carries it", A_line_break_in_a_batch_files_word_is_refused_as_no_line_of_cmds_carries_it));

    private void A_batch_files_words_with_cmds_operators_and_variables_go_through_cmd_quoted_as_given()
    {
        Through(["run", "dev", "R&D", "a|b", "%x%"], "run dev \"R&D\" \"a|b\" \"%%cd:~,%x%%cd:~,%\"",
            "cmd.exe would run D as a command of its own after R, pipe into a command b, or put x's value in place of %x%");
    }

    private void A_batch_files_words_with_quotes_escapes_and_backslashes_reach_the_program_it_runs_as_given()
    {
        Through(["say \"hi\"", "x^y", "!z!", "a\\\"b", "C:\\out\\", "", "plain-1.0/x:y@z"],
            "\"say \"\"hi\"\"\" \"x^y\" \"!z!\" \"a\\\\\"\"b\" \"C:\\out\\\\\" \"\" plain-1.0/x:y@z",
            "a quote would end cmd's quoting there, a caret be taken away, a trailing backslash escape the closing quote, or an empty word be lost");
    }

    private void A_batch_file_is_run_by_its_path_in_quotes_with_extensions_on_and_delayed_expansion_off()
    {
        string line = WindowsCommandLine.ThroughCmd("C:\\Program Files\\nodejs\\pnpm.cmd", ["install"]);
        Assert.True(line == "/e:ON /v:OFF /d /s /c \"\"C:\\Program Files\\nodejs\\pnpm.cmd\" install\"",
            $"pnpm.cmd's line through cmd.exe was {line}: cmd would split a path with a space, read the registry's AutoRun, or expand !z! in a word");
        Assert.True(WindowsCommandLine.IsBatch("C:\\npm\\FUNC.CMD") && WindowsCommandLine.IsBatch("run.bat") && !WindowsCommandLine.IsBatch("func.exe") && !WindowsCommandLine.IsBatch("func"),
            "A .cmd or .bat, in any letter case, would be started without cmd.exe, or a program through it");
    }

    private void A_line_break_in_a_batch_files_word_is_refused_as_no_line_of_cmds_carries_it()
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => WindowsCommandLine.ThroughCmd(Path.Combine("npm", "pnpm.cmd"), ["run", "a\nb"]));
        Assert.StartsWith("pnpm.cmd runs through cmd.exe", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The variables cmd.exe has where the tests read a line as it does.</summary>
    private static readonly Dictionary<string, string> Variables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x"] = "expanded",
        ["PATH"] = "C:\\Windows",
        ["cd"] = "C:\\work",
    };

    /// <summary>A batch file's line through cmd.exe ends in <paramref name="expected"/> after its path, and the words reach a
    /// program the batch file hands them to with <c>%*</c> as given: cmd reading the /c line (each <c>%</c> first, then
    /// its quotes, escapes and operators), the batch file reading its <c>%*</c> line again but its <c>%</c>, and the
    /// program splitting what it is given by the C runtime's rules.</summary>
    private static void Through(string[] words, string expected, string consequence)
    {
        const string Program = "C:\\Users\\me\\AppData\\Roaming\\npm\\pnpm.cmd";
        const string Switches = "/e:ON /v:OFF /d /s /c ";
        string line = WindowsCommandLine.ThroughCmd(Program, words);
        string start = $"{Switches}\"\"{Program}\" ";
        Assert.True(line == start + expected + "\"", $"[{string.Join(", ", words)}] went through cmd.exe as {line}, not as {start}{expected}\": {consequence}");
        // /s takes away the first and last quotes after /c, and what is left is read as a command line.
        string read = CmdReads(Expanded(line[(Switches.Length + 1)..^1]));
        string given = read[(Program.Length + 3)..];
        List<string> split = WindowsCommandLineTests.Split(CmdReads(given));
        Assert.True(split.SequenceEqual(words), $"{line} reached the program as [{string.Join(", ", split)}], not the words it was made of: {consequence}");
    }

    /// <summary>A command line's variables as cmd.exe expands them on its /c line: <c>%NAME%</c> of a variable set is its
    /// value, <c>%NAME:~,%</c> nothing, and any other <c>%</c> kept, read on from the character after it.</summary>
    private static string Expanded(string line)
    {
        StringBuilder expanded = new();
        for (int i = 0; i < line.Length; i++)
        {
            int end = line[i] == '%' ? line.IndexOfAny(['%', ':'], i + 1) : -1;
            string name = end < 0 ? string.Empty : line[(i + 1)..end];
            if (end >= 0 && line[end] == '%' && Variables.TryGetValue(name, out string? value))
            {
                expanded.Append(value);
                i = end;
            }
            else if (end >= 0 && Variables.ContainsKey(name) && line.AsSpan(end).StartsWith(":~,%", StringComparison.Ordinal))
            {
                i = end + 3;
            }
            else
            {
                expanded.Append(line[i]);
            }
        }
        return expanded.ToString();
    }

    /// <summary>A command line as cmd.exe reads it once its variables are in: outside double quotes a caret takes the next
    /// character as it is, and an operator (&amp;, |, &lt;, &gt;) ends the command, which fails the test.</summary>
    private static string CmdReads(string line)
    {
        StringBuilder read = new();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '^' && i + 1 < line.Length)
            {
                c = line[++i];
            }
            else if (!quoted && c is '&' or '|' or '<' or '>')
            {
                Assert.Fail($"cmd.exe would end the command at the {c} after {read} in {line}");
            }
            read.Append(c);
        }
        return read.ToString();
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
    internal static List<string> Split(string line)
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
