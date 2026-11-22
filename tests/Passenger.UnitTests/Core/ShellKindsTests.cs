using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A shell known by its program's file name, in any letter case and with or without .exe: zsh and bash told apart
/// from sh for their startup files, though all three quote alike.</summary>
public class ShellKindsTests
{
    [Fact]
    public void A_shell_is_known_by_its_files_name_and_sh_zsh_and_bash_alone_quote_as_sh_does() => Cases.All(
        ("A shell is known by its files name", A_shell_is_known_by_its_files_name),
        ("Sh zsh and bash alone quote as sh does", Sh_zsh_and_bash_alone_quote_as_sh_does));

    private void A_shell_is_known_by_its_files_name() => Cases.Each<(string program, ShellKind kind)>(
    [
        ("C:\\Program Files\\PowerShell\\7\\pwsh.exe", ShellKind.PowerShell),
        ("/usr/local/bin/pwsh", ShellKind.PowerShell),
        ("C:\\Program Files\\Git\\bin\\bash.exe", ShellKind.Bash),
        ("/bin/zsh", ShellKind.Zsh),
        ("/opt/homebrew/bin/fish", ShellKind.Fish),
        ("C:\\Program Files\\nu\\bin\\nu.exe", ShellKind.Nushell),
    ], row =>
    {
        (string program, ShellKind kind) = row;
        Assert.Equal(kind, ShellKinds.Of(program));
    });

    private void Sh_zsh_and_bash_alone_quote_as_sh_does()
    {
        ShellKind[] quoting = [.. Enum.GetValues<ShellKind>().Where(kind => kind.QuotesAsSh())];
        Assert.Equal([ShellKind.Posix, ShellKind.Zsh, ShellKind.Bash], quoting);
    }
}
