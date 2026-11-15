using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A shell known by its program's file name, in any letter case and with or without .exe: zsh and bash told apart
/// from sh for their startup files, though all three quote alike.</summary>
public class ShellKindsTests
{
    [Theory]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe", ShellKind.PowerShell)]
    [InlineData("C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", ShellKind.PowerShell)]
    [InlineData("/usr/local/bin/pwsh", ShellKind.PowerShell)]
    [InlineData("cmd.exe", ShellKind.Cmd)]
    [InlineData("C:\\WINDOWS\\system32\\CMD.EXE", ShellKind.Cmd)]
    [InlineData("C:\\Program Files\\Git\\bin\\bash.exe", ShellKind.Bash)]
    [InlineData("/usr/local/bin/bash", ShellKind.Bash)]
    [InlineData("/bin/zsh", ShellKind.Zsh)]
    [InlineData("/bin/sh", ShellKind.Posix)]
    [InlineData("/bin/dash", ShellKind.Posix)]
    [InlineData("/opt/homebrew/bin/fish", ShellKind.Fish)]
    [InlineData("/home/me/.cargo/bin/nu", ShellKind.Nushell)]
    [InlineData("C:\\Program Files\\nu\\bin\\nu.exe", ShellKind.Nushell)]
    public void A_shell_is_known_by_its_files_name(string program, ShellKind kind) => Assert.Equal(kind, ShellKinds.Of(program));

    [Fact]
    public void Sh_zsh_and_bash_alone_quote_as_sh_does()
    {
        ShellKind[] quoting = [.. Enum.GetValues<ShellKind>().Where(kind => kind.QuotesAsSh())];
        Assert.Equal([ShellKind.Posix, ShellKind.Zsh, ShellKind.Bash], quoting);
    }
}
