using System.Security.Cryptography;
using System.Text;
using Passenger.Core;

namespace Passenger.Terminal;

/// <summary>A folder a shell finds programs in before any other, named by a variable too (DOTNET_ROOT for a .NET
/// installation): given in the shell's environment, and given again once its startup files have run, as they may put other
/// folders first (a macOS login shell's path_helper puts the system's own first) or name another. zsh takes it again
/// before its first prompt through Passenger's .zshenv in <see cref="ZshStartup"/>, which reads the person's own first,
/// bash through PROMPT_COMMAND; any other shell has the environment alone.</summary>
public sealed record FirstOnPath(string Folder, string Variable, string ZshStartup)
{
    /// <summary>The folder, for what gives it again after the startup files.</summary>
    [IdentifierHome]
    public const string FolderVariable = "PASSENGER_FIRST_ON_PATH";

    /// <summary>What gives it again, run once at the first prompt.</summary>
    [IdentifierHome]
    public const string AgainVariable = "PASSENGER_FIRST_AGAIN";

    /// <summary>The person's own ZDOTDIR, while Passenger's names zsh's first startup file.</summary>
    [IdentifierHome]
    public const string ZdotdirVariable = "PASSENGER_ZDOTDIR";

    /// <summary>The folder put first on the PATH and named by the variable, every other place of it on the PATH left out,
    /// in sh's words (bash's and zsh's too); bash's PROMPT_COMMAND then loses what ran it, or goes where nothing else is in it.</summary>
    private string Again => $$"""
        if [ -n "${{{FolderVariable}}+x}" ]; then
        __passenger_rest=${PATH:+$PATH:}
        PATH=${{FolderVariable}}
        while [ -n "$__passenger_rest" ]; do
        __passenger_at=${__passenger_rest%%:*}
        __passenger_rest=${__passenger_rest#*:}
        [ "$__passenger_at" = "${{FolderVariable}}" ] || PATH=$PATH:$__passenger_at
        done
        export PATH {{Variable}}="${{FolderVariable}}"
        unset __passenger_rest __passenger_at {{FolderVariable}}
        fi
        if [ -n "${BASH_VERSION-}" ]; then
        PROMPT_COMMAND=${PROMPT_COMMAND#'{{BashPrompt}}'}
        [ -n "$PROMPT_COMMAND" ] || unset PROMPT_COMMAND
        fi
        unset {{AgainVariable}}

        """;

    /// <summary>What bash's PROMPT_COMMAND begins with, which runs <see cref="Again"/>.</summary>
    private const string BashPrompt = $"eval \"${AgainVariable}\";";

    /// <summary>Passenger's .zshenv: zsh's startup files are then the person's own, from their ZDOTDIR or the home, and
    /// before the first prompt <see cref="Again"/> runs once.</summary>
    public static readonly string ZshEnv = $$"""
        # Passenger's terminal: the person's startup files, then the folder Passenger keeps first on the PATH, at the first prompt.
        if [ -n "${{{ZdotdirVariable}}+x}" ]; then ZDOTDIR=${{ZdotdirVariable}}; else unset ZDOTDIR; fi
        unset {{ZdotdirVariable}}
        if [ -f "${ZDOTDIR:-$HOME}/.zshenv" ]; then . "${ZDOTDIR:-$HOME}/.zshenv"; fi
        __passenger_first() {
        precmd_functions=(${precmd_functions:#__passenger_first})
        eval "${{AgainVariable}}"
        unfunction __passenger_first
        }
        precmd_functions+=(__passenger_first)

        """;

    /// <summary>The folder holding <see cref="ZshEnv"/> under <paramref name="cache"/>, named for what it says, written there
    /// once where it is not: a Passenger of another version writes its own beside it. Off the window's thread.</summary>
    public static string ZshFolder(string cache)
    {
        byte[] text = Encoding.UTF8.GetBytes(ZshEnv);
        string folder = Path.Combine(cache, $"zsh-{Convert.ToHexStringLower(SHA256.HashData(text))[..12]}");
        string file = Path.Combine(folder, ".zshenv");
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(folder);
            AtomicFile.Write(file, text);
        }
        return folder;
    }

    /// <summary>Sets it over the environment a shell (the program named) starts with, given what the process has
    /// (<paramref name="inherited"/>): the folder first on the PATH, once, and the variable naming it; for zsh and bash, what
    /// gives both again after their startup files.</summary>
    public void Give(string shell, IDictionary<string, string> environment, IReadOnlyDictionary<string, string> inherited)
    {
        StringComparison names = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Windows names it as it pleases (Path), and two of one name in an environment are one too many.
        string pathName = inherited.Keys.FirstOrDefault(name => name.Equals("PATH", names)) ?? "PATH";
        string path = environment.TryGetValue(pathName, out string? given) ? given : inherited.GetValueOrDefault(pathName, string.Empty);
        string[] rest = path.Length == 0 ? [] : [.. path.Split(Path.PathSeparator).Where(at => !PathNames.Same(at, Folder))];
        environment[pathName] = string.Join(Path.PathSeparator, [Folder, .. rest]);
        environment[Variable] = Folder;
        string name = Path.GetFileNameWithoutExtension(shell);
        if (name is not ("zsh" or "bash") || OperatingSystem.IsWindows())
        {
            return;
        }
        environment[FolderVariable] = Folder;
        environment[AgainVariable] = Again;
        if (name == "zsh")
        {
            string? own = environment.TryGetValue("ZDOTDIR", out string? set) ? set : inherited.GetValueOrDefault("ZDOTDIR");
            if (own is { Length: > 0 })
            {
                environment[ZdotdirVariable] = own;
            }
            environment["ZDOTDIR"] = ZshStartup;
        }
        else
        {
            string? prompt = environment.TryGetValue("PROMPT_COMMAND", out string? set) ? set : inherited.GetValueOrDefault("PROMPT_COMMAND");
            environment["PROMPT_COMMAND"] = BashPrompt + prompt;
        }
    }
}
