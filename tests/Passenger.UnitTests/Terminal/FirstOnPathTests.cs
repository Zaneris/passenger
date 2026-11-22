using Passenger.Core;
using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>A folder a shell is to find programs in first (one lending the dotnet of the .NET installation a folder's projects
/// take): put first on the PATH it inherits, once, and its variable given the installation; zsh and bash given what puts
/// both again after their startup files, each keeping what the person had (zsh's ZDOTDIR, bash's PROMPT_COMMAND); any other
/// shell the environment alone.</summary>
public class FirstOnPathTests
{
    private static readonly char Separator = Path.PathSeparator;

    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "dotnet-of-the-folder");

    private static readonly string Installation = Path.Combine(Path.GetTempPath(), "installation-of-the-folder");

    private static readonly FirstOnPath First = new(Folder, RuntimeVariables.Root, Installation, Path.Combine(Path.GetTempPath(), "zsh-startup"));

    private static Dictionary<string, string> Inherited(params (string Name, string Value)[] more)
    {
        Dictionary<string, string> inherited = new() { ["PATH"] = string.Join(Separator, "/usr/bin", Folder, "/bin"), [RuntimeVariables.Root] = "/usr/share/dotnet" };
        foreach ((string name, string value) in more)
        {
            inherited[name] = value;
        }
        return inherited;
    }

    [Fact]
    public void Each_shell_finds_the_installations_folder_first_on_its_path() => Cases.All(
        ("Any shell finds the folder first on the path once and has the installation as its variable", Any_shell_finds_the_folder_first_on_the_path_once_and_has_the_installation_as_its_variable),
        ("A path set for the start is the one the folder goes first on", A_path_set_for_the_start_is_the_one_the_folder_goes_first_on),
        ("Zsh reads passengers zshenv first which is given the persons own zdotdir", Zsh_reads_passengers_zshenv_first_which_is_given_the_persons_own_zdotdir),
        ("Bash runs it again at its first prompt before the prompt command it had", Bash_runs_it_again_at_its_first_prompt_before_the_prompt_command_it_had));

    private void Any_shell_finds_the_folder_first_on_the_path_once_and_has_the_installation_as_its_variable()
    {
        Dictionary<string, string> environment = [];
        First.Give("/bin/sh", environment, Inherited());
        Assert.True(environment.GetValueOrDefault("PATH") == string.Join(Separator, Folder, "/usr/bin", "/bin"),
            $"sh would run another installation's dotnet: PATH {environment.GetValueOrDefault("PATH")}");
        Assert.True(environment.GetValueOrDefault(RuntimeVariables.Root) == Installation,
            $"sh's DOTNET_ROOT is {environment.GetValueOrDefault(RuntimeVariables.Root)}, not the installation {Installation}, so an app it starts finds no runtime");
        Assert.True(environment.Count == 2, $"A shell that reads nothing of Passenger's after its startup files was given more: {string.Join(", ", environment.Keys)}");
    }

    private void A_path_set_for_the_start_is_the_one_the_folder_goes_first_on()
    {
        Dictionary<string, string> environment = new() { ["PATH"] = "/given" };
        First.Give("/bin/sh", environment, Inherited());
        Assert.Equal(string.Join(Separator, Folder, "/given"), environment["PATH"]);
    }

    private void Zsh_reads_passengers_zshenv_first_which_is_given_the_persons_own_zdotdir()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "zsh's startup files are read on macOS and Linux alone");
        Dictionary<string, string> environment = [];
        First.Give("/bin/zsh", environment, Inherited(("ZDOTDIR", "/home/me/.config/zsh")));
        Assert.True(environment.GetValueOrDefault("ZDOTDIR") == First.ZshStartup && environment.GetValueOrDefault(FirstOnPath.ZdotdirVariable) == "/home/me/.config/zsh",
            $"zsh would read the person's startup files from {environment.GetValueOrDefault(FirstOnPath.ZdotdirVariable)}, Passenger's from {environment.GetValueOrDefault("ZDOTDIR")}");
        Assert.True(environment.GetValueOrDefault(FirstOnPath.FolderVariable) == Folder && environment.GetValueOrDefault(FirstOnPath.ValueVariable) == Installation
            && environment.GetValueOrDefault(FirstOnPath.AgainVariable) is { Length: > 0 },
            "zsh's first prompt is given nothing to put the folder first and the installation as DOTNET_ROOT again with");

        Dictionary<string, string> homes = [];
        First.Give("/bin/zsh", homes, Inherited());
        Assert.False(homes.ContainsKey(FirstOnPath.ZdotdirVariable), "With no ZDOTDIR of the person's, zsh would be given an empty one, and read no startup file of theirs");
    }

    private void Bash_runs_it_again_at_its_first_prompt_before_the_prompt_command_it_had()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "bash's startup files are read on macOS and Linux alone");
        Dictionary<string, string> environment = [];
        First.Give("/usr/local/bin/bash", environment, Inherited(("PROMPT_COMMAND", "history -a")));
        Assert.True(environment.GetValueOrDefault("PROMPT_COMMAND") == $"eval \"${FirstOnPath.AgainVariable}\";history -a" && !environment.ContainsKey("ZDOTDIR"),
            $"bash's PROMPT_COMMAND is {environment.GetValueOrDefault("PROMPT_COMMAND")}, ZDOTDIR {environment.GetValueOrDefault("ZDOTDIR")}");
    }
}
