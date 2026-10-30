using System.Text;
using Passenger.App.Mcp;
using Passenger.Terminal;
using Passenger.UnitTests.Core;

namespace Passenger.UnitTests.Mcp;

/// <summary>The Claude Code and Codex commands Settings give for adding Passenger, written for the Terminal's shell: run
/// there, each reaches the agent's CLI as the same words, Passenger's path one of them whatever it holds (R&amp;D, O'Brien,
/// a space, $, ^ or %), in PowerShell, cmd and sh alike.</summary>
public sealed class AgentSetupTests
{
    /// <summary>Where Passenger may be installed: folders holding what each shell reads as more than a name.</summary>
    public static readonly TheoryData<string> Paths = new()
    {
        "C:\\Users\\O'Brien\\R&D\\Passenger.exe",
        "C:\\Program Files\\R&D (x86)\\Passenger\\Passenger.exe",
        "C:\\Users\\a$b\\5^2 100%\\Passenger.exe",
        "/Users/o'brien/R&D/Passenger.app/Contents/MacOS/Passenger",
        "/opt/my apps/$(touch ran);`x`|y/Passenger",
        "/Applications/Passenger.app/Contents/MacOS/Passenger",
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public void PowerShell_runs_the_commands_with_Passengers_path_as_one_word(string path) => Check(path, ShellKind.PowerShell, PowerShellReads);

    [Theory]
    [MemberData(nameof(Paths))]
    public void Cmd_runs_the_commands_with_Passengers_path_as_one_word(string path) => Check(path, ShellKind.Cmd, CmdReads);

    [Theory]
    [MemberData(nameof(Paths))]
    public void Sh_runs_the_commands_with_Passengers_path_as_one_word(string path) => Check(path, ShellKind.Posix, ShReads);

    [Fact]
    public void The_commands_are_written_for_the_shell_the_Terminals_command_line_starts()
    {
        Assert.Equal(ShellKind.PowerShell, AgentSetup.ShellOf("pwsh -NoLogo"));
        Assert.Equal(ShellKind.PowerShell, AgentSetup.ShellOf("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo"));
        Assert.Equal(ShellKind.Cmd, AgentSetup.ShellOf("C:\\WINDOWS\\system32\\cmd.exe"));
        Assert.Equal(ShellKind.Posix, AgentSetup.ShellOf("/bin/zsh -l"));
        Assert.Equal(ShellKind.Fish, AgentSetup.ShellOf("/opt/homebrew/bin/fish"));
    }

    /// <summary>Both commands, read by <paramref name="shell"/>'s rules, give the words they were made of.</summary>
    private static void Check(string path, ShellKind shell, Func<string, List<string>?> reads)
    {
        AgentSetup setup = new(path, null, shell);
        foreach ((string agent, string command) in new[] { ("claude", setup.ClaudeCode), ("codex", setup.Codex) })
        {
            string[] meant = [agent, "mcp", "add", "passenger", "--", path, "--mcp-attach"];
            List<string>? read = reads(command);
            Assert.True(read is not null && read.SequenceEqual(meant),
                $"{shell} runs {command} as {(read is null ? "something else: a character in the path runs, expands or ends the line" : $"[{string.Join(", ", read)}]")}, "
                + $"so {agent} adds no Passenger at {path}");
        }
    }

    /// <summary>A line's words as sh reads them, or null where something in it would run, expand or redirect: single quotes
    /// keep all, a backslash outside them keeps the next character, double quotes all but $, ` and \.</summary>
    private static List<string>? ShReads(string line)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool inWord = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is ' ' or '\t')
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }
                continue;
            }
            inWord = true;
            if (c == '\'')
            {
                int end = line.IndexOf('\'', i + 1);
                if (end < 0)
                {
                    return null;
                }
                word.Append(line, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '"')
            {
                for (i++; i < line.Length && line[i] != '"'; i++)
                {
                    if (line[i] is '$' or '`')
                    {
                        return null;
                    }
                    if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] is '$' or '`' or '"' or '\\')
                    {
                        i++;
                    }
                    word.Append(line[i]);
                }
                if (i == line.Length)
                {
                    return null;
                }
            }
            else if (c == '\\')
            {
                if (++i == line.Length)
                {
                    return null;
                }
                word.Append(line[i]);
            }
            else if ("&;|<>()$`*?[]{}~#!".Contains(c, StringComparison.Ordinal))
            {
                return null;
            }
            else
            {
                word.Append(c);
            }
        }
        if (inWord)
        {
            words.Add(word.ToString());
        }
        return words;
    }

    /// <summary>The quotes PowerShell reads as a single quote: the ASCII one and the typographic ones.</summary>
    private const string PowerShellQuotes = "'\u2018\u2019\u201A\u201B";

    /// <summary>A line's words as PowerShell hands them to a program it runs, or null where something in it would run,
    /// expand or end the line: in single quotes all is kept, two quotes one; bare, its operators and sigils are not.</summary>
    private static List<string>? PowerShellReads(string line)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool inWord = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is ' ' or '\t')
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }
                continue;
            }
            inWord = true;
            if (PowerShellQuotes.Contains(c, StringComparison.Ordinal))
            {
                for (i++; ; i++)
                {
                    if (i == line.Length)
                    {
                        return null;
                    }
                    if (PowerShellQuotes.Contains(line[i], StringComparison.Ordinal))
                    {
                        if (i + 1 < line.Length && PowerShellQuotes.Contains(line[i + 1], StringComparison.Ordinal))
                        {
                            word.Append(line[++i]);
                            continue;
                        }
                        break;
                    }
                    word.Append(line[i]);
                }
            }
            else if ("\"`$@&|;(){}<>,#".Contains(c, StringComparison.Ordinal))
            {
                return null;
            }
            else
            {
                word.Append(c);
            }
        }
        if (inWord)
        {
            words.Add(word.ToString());
        }
        return words;
    }

    /// <summary>A line's words as cmd runs it, or null where something in it would expand or end the command: a %NAME% is
    /// expanded inside quotes or out, outside them a caret keeps the next character and &amp;, |, &lt; and &gt; are
    /// operators; the program then splits what is left by the C runtime's rules.</summary>
    private static List<string>? CmdReads(string line)
    {
        int open = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '%')
            {
                continue;
            }
            if (open >= 0 && i > open + 1 && line[(open + 1)..i].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '(' or ')' or ' '))
            {
                return null;
            }
            open = i;
        }
        StringBuilder run = new();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '^')
            {
                if (++i == line.Length)
                {
                    return null;
                }
                c = line[i];
            }
            else if (!quoted && c is '&' or '|' or '<' or '>')
            {
                return null;
            }
            run.Append(c);
        }
        return WindowsCommandLineTests.Split(run.ToString());
    }
}
