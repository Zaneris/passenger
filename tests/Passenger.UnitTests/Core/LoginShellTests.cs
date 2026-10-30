using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The login shell the IDE's environment is read from and its terminal runs: SHELL where it names a file, else the
/// system's own, the same one for both.</summary>
public class LoginShellTests
{
    [Fact]
    public void With_SHELL_unset_on_Linux_the_login_shell_is_bash_where_it_is_installed_and_else_sh()
    {
        Assert.Equal("/bin/bash", ShellEnvironment.LoginShell(null, file => file is "/bin/bash" or "/bin/sh", macOS: false));
        Assert.Equal("/bin/bash", ShellEnvironment.LoginShell("", file => file is "/bin/bash" or "/bin/sh", macOS: false));
        Assert.Equal("/bin/sh", ShellEnvironment.LoginShell(null, file => file is "/bin/sh", macOS: false));
    }

    [Fact]
    public void A_SHELL_naming_a_file_is_the_login_shell_and_one_naming_none_is_passed_over()
    {
        Assert.Equal("/usr/bin/fish", ShellEnvironment.LoginShell("/usr/bin/fish", file => file is "/usr/bin/fish" or "/bin/bash", macOS: false));
        Assert.Equal("/bin/bash", ShellEnvironment.LoginShell("/usr/bin/gone", file => file is "/bin/bash", macOS: false));
        Assert.Equal("/bin/zsh", ShellEnvironment.LoginShell("/usr/bin/gone", file => file is "/bin/bash" or "/bin/zsh", macOS: true));
        Assert.Equal("/bin/zsh", ShellEnvironment.LoginShell(null, file => true, macOS: true));
    }
}
