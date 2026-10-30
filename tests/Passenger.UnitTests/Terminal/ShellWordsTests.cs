using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>A path dropped on the terminal, written as one word the shell it runs takes as it is: PowerShell's single
/// quotes, cmd's double quotes with each % outside them, sh's single quotes; bare where nothing in it means anything to
/// that shell.</summary>
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

    [Fact]
    public void A_path_with_nothing_a_shell_reads_in_it_is_written_bare()
    {
        Assert.Equal("C:\\Users\\me\\Program.cs", ShellWords.Quoted("C:\\Users\\me\\Program.cs", ShellKind.PowerShell));
        Assert.Equal("C:\\Users\\me\\Program.cs", ShellWords.Quoted("C:\\Users\\me\\Program.cs", ShellKind.Cmd));
        Assert.Equal("/Users/me/my-app_1.0+b@x,y=z%.cs", ShellWords.Quoted("/Users/me/my-app_1.0+b@x,y=z%.cs", ShellKind.Posix));
    }

    [Theory]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe", ShellKind.PowerShell)]
    [InlineData("C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", ShellKind.PowerShell)]
    [InlineData("/usr/local/bin/pwsh", ShellKind.PowerShell)]
    [InlineData("cmd.exe", ShellKind.Cmd)]
    [InlineData("C:\\WINDOWS\\system32\\CMD.EXE", ShellKind.Cmd)]
    [InlineData("C:\\Program Files\\Git\\bin\\bash.exe", ShellKind.Posix)]
    [InlineData("/bin/zsh", ShellKind.Posix)]
    public void A_shell_is_known_by_its_files_name(string program, ShellKind kind) => Assert.Equal(kind, ShellWords.KindOf(program));
}
