using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>A path dropped on the terminal, written as one word the shell it runs takes as it is: PowerShell's single
/// quotes, cmd's double quotes with each % outside them, sh's single quotes, fish's with backslashes and quotes escaped,
/// Nushell's raw string; bare where nothing in it means anything to that shell.</summary>
public class ShellWordsTests
{
    [Fact]
    public void PowerShell_takes_a_path_in_single_quotes_each_quote_in_it_doubled_so_nothing_in_it_runs()
    {
        Assert.Equal("'$(x) it''s.txt'", ShellWords.Quoted("$(x) it's.txt", ShellKind.PowerShell));
        Assert.True(ShellWords.Quoted("C:\\a\\it\u2019s $env:HOME.txt", ShellKind.PowerShell) == "'C:\\a\\it\u2019\u2019s $env:HOME.txt'",
            "A typographic quote, which PowerShell reads as a single quote, was left alone: it ends the quotes and what follows runs");
        Assert.True(ShellWords.Quoted("C:\\a,b.txt", ShellKind.PowerShell) == "'C:\\a,b.txt'", "A comma left bare makes PowerShell pass two words");
    }

    [Fact]
    public void Cmd_takes_a_path_in_double_quotes_each_percent_behind_a_caret_outside_them()
    {
        Assert.True(ShellWords.Quoted("C:\\a b\\%PATH%.txt", ShellKind.Cmd) == "\"C:\\a b\\\\\"^%\"PATH\"^%\".txt\"",
            $"cmd's form of a name holding %PATH% is {ShellWords.Quoted("C:\\a b\\%PATH%.txt", ShellKind.Cmd)}: cmd expands it into another path, "
            + "or a program takes the backslash before a closing quote as escaping it");
        Assert.Equal("\"C:\\it's & here.txt\"", ShellWords.Quoted("C:\\it's & here.txt", ShellKind.Cmd));
        Assert.Equal("\"C:\\a b\\\\\"", ShellWords.Quoted("C:\\a b\\", ShellKind.Cmd));
    }

    [Fact]
    public void Sh_takes_a_path_in_single_quotes_each_quote_closed_escaped_and_opened_again()
    {
        Assert.Equal("'/tmp/$(touch ran) it'\\''s.txt'", ShellWords.Quoted("/tmp/$(touch ran) it's.txt", ShellKind.Posix));
        Assert.True(ShellWords.Quoted("/tmp/a\\b", ShellKind.Posix) == "'/tmp/a\\b'", "A backslash left bare is taken by sh as an escape, and the path loses it");
    }

    /// <summary>Names holding what fish's single quotes and Nushell's strings read: quotes, backslashes, a backslash at
    /// the end, a quote followed by #s, and a command.</summary>
    public static readonly TheoryData<string> Awkward = new()
    {
        "/tmp/it's.txt",
        "/tmp/a\\\\b",
        "/tmp/a b\\",
        "/tmp/x'#y'##z.txt",
        "/tmp/$(touch ran) 'it'.txt",
        "/tmp/\\'",
    };

    [Theory]
    [MemberData(nameof(Awkward))]
    public void Fish_reads_a_dropped_path_back_as_it_is(string path)
    {
        string word = ShellWords.Quoted(path, ShellKind.Fish);
        Assert.True(FishReads(word) == path,
            $"fish reads {word} as {FishReads(word) ?? "an unfinished quote"}, not the dropped file's path {path}: a backslash or quote in it is lost or ends the quote");
    }

    [Theory]
    [MemberData(nameof(Awkward))]
    public void Nushell_reads_a_dropped_path_back_as_it_is(string path)
    {
        string word = ShellWords.Quoted(path, ShellKind.Nushell);
        Assert.True(NushellReads(word) == path,
            $"Nushell reads {word} as {NushellReads(word) ?? "a malformed line"}, not the dropped file's path {path}");
    }

    [Fact]
    public void Fish_escapes_each_backslash_and_quote_in_single_quotes_and_Nushell_takes_a_raw_string()
    {
        Assert.Equal("'/tmp/it\\'s a\\\\b\\\\'", ShellWords.Quoted("/tmp/it's a\\b\\", ShellKind.Fish));
        Assert.Equal("r#'/tmp/it's a\\b'#", ShellWords.Quoted("/tmp/it's a\\b", ShellKind.Nushell));
        Assert.True(ShellWords.Quoted("/tmp/x'#y", ShellKind.Nushell) == "r##'/tmp/x'#y'##", "A quote followed by # in the name ended Nushell's raw string inside it");
        Assert.True(ShellWords.Quoted("10kb", ShellKind.Nushell) == "r#'10kb'#", "A name Nushell reads as a file size was left bare");
    }

    [Fact]
    public void A_path_with_nothing_a_shell_reads_in_it_is_written_bare()
    {
        Assert.Equal("C:\\Users\\me\\Program.cs", ShellWords.Quoted("C:\\Users\\me\\Program.cs", ShellKind.PowerShell));
        Assert.Equal("C:\\Users\\me\\Program.cs", ShellWords.Quoted("C:\\Users\\me\\Program.cs", ShellKind.Cmd));
        Assert.Equal("/Users/me/my-app_1.0+b@x,y=z%.cs", ShellWords.Quoted("/Users/me/my-app_1.0+b@x,y=z%.cs", ShellKind.Posix));
        Assert.Equal("/Users/me/my-app_1.0+b@x,y=z.cs", ShellWords.Quoted("/Users/me/my-app_1.0+b@x,y=z.cs", ShellKind.Fish));
        Assert.Equal("/Users/me/my-app_1.0.cs", ShellWords.Quoted("/Users/me/my-app_1.0.cs", ShellKind.Nushell));
    }

    [Theory]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe", ShellKind.PowerShell)]
    [InlineData("C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", ShellKind.PowerShell)]
    [InlineData("/usr/local/bin/pwsh", ShellKind.PowerShell)]
    [InlineData("cmd.exe", ShellKind.Cmd)]
    [InlineData("C:\\WINDOWS\\system32\\CMD.EXE", ShellKind.Cmd)]
    [InlineData("C:\\Program Files\\Git\\bin\\bash.exe", ShellKind.Posix)]
    [InlineData("/bin/zsh", ShellKind.Posix)]
    [InlineData("/opt/homebrew/bin/fish", ShellKind.Fish)]
    [InlineData("/home/me/.cargo/bin/nu", ShellKind.Nushell)]
    [InlineData("C:\\Program Files\\nu\\bin\\nu.exe", ShellKind.Nushell)]
    public void A_shell_is_known_by_its_files_name(string program, ShellKind kind) => Assert.Equal(kind, ShellWords.KindOf(program));

    /// <summary>A word as fish reads it: in single quotes, a backslash escapes a backslash or a quote and is kept before
    /// anything else; null where the quote is never closed or the word goes on after it.</summary>
    private static string? FishReads(string word)
    {
        if (!word.StartsWith('\''))
        {
            return word;
        }
        System.Text.StringBuilder read = new();
        for (int i = 1; i < word.Length; i++)
        {
            if (word[i] == '\\' && i + 1 < word.Length && word[i + 1] is '\\' or '\'')
            {
                read.Append(word[++i]);
            }
            else if (word[i] == '\'')
            {
                return i == word.Length - 1 ? read.ToString() : null;
            }
            else
            {
                read.Append(word[i]);
            }
        }
        return null;
    }

    /// <summary>A word as Nushell reads it: a raw string ends at the first quote followed by as many #s as began it;
    /// null where it never ends or the word goes on after it.</summary>
    private static string? NushellReads(string word)
    {
        if (!word.StartsWith("r#", StringComparison.Ordinal))
        {
            return word.Contains('\'', StringComparison.Ordinal) ? null : word;
        }
        int hashes = word.IndexOf('\'', StringComparison.Ordinal) - 1;
        string end = "'" + new string('#', hashes);
        int closing = word.IndexOf(end, hashes + 2, StringComparison.Ordinal);
        return closing >= 0 && closing + end.Length == word.Length ? word[(hashes + 2)..closing] : null;
    }
}
