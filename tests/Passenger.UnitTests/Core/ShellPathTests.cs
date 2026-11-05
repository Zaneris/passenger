using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The lines that put a .NET installation on the PATH of the shells a person starts, and the startup file each of
/// their login shells reads them from: DOTNET_ROOT naming it, from $HOME where it is under it and quoted so the shell reads
/// it as written, and the PATH with it first; a startup file that is a link kept one, the file it names taking them.</summary>
public class ShellPathTests
{
    private static readonly string Home = OperatingSystem.IsWindows() ? @"C:\Users\ana" : "/home/ana";
    private static readonly string Installed = Path.Combine(Home, ".dotnet");

    /// <summary>A home holding none of the startup files.</summary>
    private static readonly Func<string, bool> NoneThere = _ => false;

    [Fact]
    public void Zsh_takes_them_in_its_zshrc_beside_ZDOTDIR_when_it_is_set()
    {
        (string file, string lines) = ShellPath.Of("zsh", Home, Installed, macOS: true, zdotdir: null, NoneThere);
        Assert.Equal(Path.Combine(Home, ".zshrc"), file);
        Assert.Equal("# The .NET SDK Passenger installed\nexport DOTNET_ROOT=\"$HOME/.dotnet\"\nexport PATH=\"$DOTNET_ROOT:$PATH\"\n", lines);
        Assert.Equal(Path.Combine(Home, "zsh", ".zshrc"), ShellPath.Of("zsh", Home, Installed, macOS: true, zdotdir: Path.Combine(Home, "zsh"), NoneThere).File);
    }

    [Fact]
    public void Bash_on_macOS_takes_them_in_the_first_login_file_it_reads_that_is_there_and_elsewhere_in_its_bashrc()
    {
        string Chosen(params string[] there) =>
            Path.GetFileName(ShellPath.Of("bash", Home, Installed, macOS: true, zdotdir: null, f => there.Any(t => f == Path.Combine(Home, t))).File);
        Assert.Equal(".bash_profile", Chosen());
        Assert.True(Chosen(".profile") == ".profile", "With ~/.profile alone the lines went to a new ~/.bash_profile, so a login shell stops reading ~/.profile");
        Assert.Equal(".bash_login", Chosen(".bash_login", ".profile"));
        Assert.Equal(".bash_profile", Chosen(".bash_profile", ".bash_login", ".profile"));
        Assert.Equal(Path.Combine(Home, ".bashrc"), ShellPath.Of("bash", Home, Installed, macOS: false, zdotdir: null, f => f.EndsWith(".profile", StringComparison.Ordinal)).File);
        Assert.Equal(Path.Combine(Home, ".profile"), ShellPath.Of("dash", Home, Installed, macOS: false, zdotdir: null, NoneThere).File);
    }

    [Fact]
    public void Fish_takes_its_own_words_in_a_file_of_its_conf_d()
    {
        (string file, string lines) = ShellPath.Of("fish", Home, Installed, macOS: false, zdotdir: null, NoneThere);
        Assert.Equal(Path.Combine(Home, ".config", "fish", "conf.d", "dotnet.fish"), file);
        Assert.Equal("# The .NET SDK Passenger installed\nset -gx DOTNET_ROOT \"$HOME/.dotnet\"\nset -gx PATH \"$DOTNET_ROOT\" $PATH\n", lines);
    }

    [Fact]
    public void An_installation_outside_home_is_named_whole_and_quoted()
    {
        Assert.Contains("export DOTNET_ROOT=\"/opt/dot net\"\n", ShellPath.Of("zsh", Home, "/opt/dot net", macOS: false, zdotdir: null, NoneThere).Lines, StringComparison.Ordinal);
    }

    /// <summary>A $, a backtick, a backslash and a quote, which a shell reads inside double quotes, each escaped; fish's
    /// backtick left as it is, as fish reads none there; $HOME the one expansion meant.</summary>
    [Fact]
    public void What_a_shell_reads_inside_quotes_is_escaped_so_it_takes_the_path_as_written()
    {
        string odd = "/opt/a$b`c\\d\"e";
        Assert.Contains(@"export DOTNET_ROOT=""/opt/a\$b\`c\\d\""e""", ShellPath.Of("bash", Home, odd, macOS: false, zdotdir: null, NoneThere).Lines, StringComparison.Ordinal);
        Assert.Contains(@"set -gx DOTNET_ROOT ""/opt/a\$b`c\\d\""e""", ShellPath.Of("fish", Home, odd, macOS: false, zdotdir: null, NoneThere).Lines, StringComparison.Ordinal);
        Assert.Contains(@"export DOTNET_ROOT=""$HOME/dot\$net\`s""", ShellPath.Of("zsh", Home, Path.Combine(Home, "dot$net`s"), macOS: true, zdotdir: null, NoneThere).Lines,
            StringComparison.Ordinal);
    }

    /// <summary>A .zshrc linked into a dotfiles folder, as a dotfiles manager links one (by a relative path), stays that link,
    /// the file it names taking the lines after what it held.</summary>
    [Fact]
    public void A_startup_file_that_is_a_link_stays_one_and_the_file_it_names_takes_the_lines()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows takes the change in the user's Path, never in a startup file");
        string home = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
        try
        {
            string kept = Path.Combine(Directory.CreateDirectory(Path.Combine(home, "dotfiles")).FullName, "zshrc");
            File.WriteAllText(kept, "alias ll='ls -l'\n");
            string link = Path.Combine(home, ".zshrc");
            File.CreateSymbolicLink(link, Path.Combine("dotfiles", "zshrc"));
            (string file, string lines) = ShellPath.Of("zsh", home, Path.Combine(home, ".dotnet"), macOS: OperatingSystem.IsMacOS(), zdotdir: null, File.Exists);
            new ShellPath(Path.Combine(home, ".dotnet"), file, lines, string.Empty).Make();
            Assert.True(new FileInfo(link).LinkTarget == Path.Combine("dotfiles", "zshrc"),
                $"~/.zshrc is no longer the link into the dotfiles folder (it links to {new FileInfo(link).LinkTarget ?? "nothing"}), so their later changes never reach the shell");
            Assert.Equal("alias ll='ls -l'\n\n" + lines, File.ReadAllText(kept));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
