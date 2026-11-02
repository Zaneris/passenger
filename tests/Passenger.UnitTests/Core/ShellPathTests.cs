using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The lines that put a .NET installation on the PATH of the shells a person starts, and the startup file each of
/// their login shells reads them from: DOTNET_ROOT naming it, from $HOME where it is under it, and the PATH with it first.</summary>
public class ShellPathTests
{
    private static readonly string Home = OperatingSystem.IsWindows() ? @"C:\Users\ana" : "/home/ana";
    private static readonly string Installed = Path.Combine(Home, ".dotnet");

    [Fact]
    public void Zsh_takes_them_in_its_zshrc_beside_ZDOTDIR_when_it_is_set()
    {
        (string file, string lines) = ShellPath.Of("zsh", Home, Installed, macOS: true, zdotdir: null);
        Assert.Equal(Path.Combine(Home, ".zshrc"), file);
        Assert.Equal("# The .NET SDK Passenger installed\nexport DOTNET_ROOT=\"$HOME/.dotnet\"\nexport PATH=\"$DOTNET_ROOT:$PATH\"\n", lines);
        Assert.Equal(Path.Combine(Home, "zsh", ".zshrc"), ShellPath.Of("zsh", Home, Installed, macOS: true, zdotdir: Path.Combine(Home, "zsh")).File);
    }

    [Fact]
    public void Bash_takes_them_in_its_login_profile_on_macOS_and_its_bashrc_elsewhere()
    {
        Assert.Equal(Path.Combine(Home, ".bash_profile"), ShellPath.Of("bash", Home, Installed, macOS: true, zdotdir: null).File);
        Assert.Equal(Path.Combine(Home, ".bashrc"), ShellPath.Of("bash", Home, Installed, macOS: false, zdotdir: null).File);
        Assert.Equal(Path.Combine(Home, ".profile"), ShellPath.Of("dash", Home, Installed, macOS: false, zdotdir: null).File);
    }

    [Fact]
    public void Fish_takes_its_own_words_in_a_file_of_its_conf_d()
    {
        (string file, string lines) = ShellPath.Of("fish", Home, Installed, macOS: false, zdotdir: null);
        Assert.Equal(Path.Combine(Home, ".config", "fish", "conf.d", "dotnet.fish"), file);
        Assert.Equal("# The .NET SDK Passenger installed\nset -gx DOTNET_ROOT \"$HOME/.dotnet\"\nset -gx PATH \"$DOTNET_ROOT\" $PATH\n", lines);
    }

    [Fact]
    public void An_installation_outside_home_is_named_whole_and_quoted()
    {
        string elsewhere = OperatingSystem.IsWindows() ? @"D:\tools\dot net" : "/opt/dot net";
        Assert.Contains($"export DOTNET_ROOT=\"{elsewhere}\"\n", ShellPath.Of("zsh", Home, elsewhere, macOS: false, zdotdir: null).Lines, StringComparison.Ordinal);
    }
}
