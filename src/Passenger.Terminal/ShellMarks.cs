using Passenger.Core;

namespace Passenger.Terminal;

/// <summary>Shell integration's marks (OSC 133) for each shell Passenger's terminal starts, set up through the shell's own
/// startup after the person's: A as each prompt begins, C as a command runs, D with its exit status as it ends, which the
/// screen reads (<see cref="TerminalScreen.Command"/>). zsh through Passenger's .zshenv (<see cref="FirstOnPath.ZshEnv"/>),
/// its hooks put first at the first prompt; bash through PROMPT_COMMAND, its runs through PS0 from bash 4.4; fish through
/// its events after its configuration (-C); PowerShell by wrapping the prompt its profile left (-NoExit -Command), a run
/// told by its history; cmd through PROMPT, which marks prompts alone. A shell given a command of its own to run is left
/// as it is.</summary>
public static class ShellMarks
{
    /// <summary>What sets bash's marks up at its first prompt, after its startup files.</summary>
    [IdentifierHome]
    public const string BashVariable = "PASSENGER_SHELL_MARKS";

    /// <summary>bash's marks, run once at the first prompt: the status taken first at every prompt, before anything else
    /// in PROMPT_COMMAND changes it, then given back to what follows there (through &amp;&amp;, so an ERR trap runs no second
    /// time); PS0 saying a command runs where bash has it (4.4 on).</summary>
    private static readonly string BashMarks = $$"""
        {{BashTakeOut(BashVariable)}}__passenger_marks() { printf '\033]133;D;%s\007\033]133;A\007' "$1"; return "$1"; }
        PROMPT_COMMAND='__passenger_marks "$?" && :'"${PROMPT_COMMAND:+;$PROMPT_COMMAND}"
        if [ "${BASH_VERSINFO[0]}" -gt 4 ] || { [ "${BASH_VERSINFO[0]}" -eq 4 ] && [ "${BASH_VERSINFO[1]}" -ge 4 ]; }; then PS0='\e]133;C\a'"${PS0-}"; fi
        printf '\033]133;A\007'
        unset {{BashVariable}}

        """;

    /// <summary>What ends <see cref="BashMarks"/> where Passenger's environment gave bash no PROMPT_COMMAND: Passenger's own
    /// put it in the shell's environment, which a child bash, lacking its function, would take.</summary>
    private const string BashUnexported = "export -n PROMPT_COMMAND\n";

    /// <summary>What ends <see cref="BashMarks"/> where Passenger's environment gave bash a PROMPT_COMMAND, which its children
    /// take as they would elsewhere, Passenger's function with it.</summary>
    private const string BashExported = "export -f __passenger_marks\n";

    /// <summary>bash's PROMPT_COMMAND running a variable's text first, before what it held (<paramref name="after"/>), a
    /// separator between only where it held anything, so a startup file's own added after a separator of its own leaves a
    /// command line bash reads.</summary>
    internal static string BashFirst(string variable, string? after) => $"eval \"${variable}\"{(string.IsNullOrEmpty(after) ? "" : $";{after}")}";

    /// <summary>bash's words taking <see cref="BashFirst"/>'s out of the start of PROMPT_COMMAND, with what separates it from
    /// the rest.</summary>
    internal static string BashTakeOut(string variable) => $$"""
        PROMPT_COMMAND=${PROMPT_COMMAND#'eval "${{variable}}"'}
        PROMPT_COMMAND=${PROMPT_COMMAND#"${PROMPT_COMMAND%%[![:space:];]*}"}

        """;

    /// <summary>zsh's hooks, defined in Passenger's .zshenv: the status taken by the first of the precmd functions. Every
    /// array is read with a default, as the person's startup files may have turned nounset on.</summary>
    internal const string ZshHooks = """
        __passenger_marks_precmd() {
        __passenger_status=$?
        [ -n "${__passenger_ran-}" ] && printf '\033]133;D;%s\007' "$__passenger_status"
        __passenger_ran=
        printf '\033]133;A\007'
        }
        __passenger_marks_preexec() {
        __passenger_ran=1
        printf '\033]133;C\007'
        }
        precmd_functions=(__passenger_marks_precmd ${precmd_functions-})
        preexec_functions+=(__passenger_marks_preexec)

        """;

    /// <summary>What puts zsh's hooks back first at its first prompt, where the person's startup files may have moved them.</summary>
    internal const string ZshFirst = """
        precmd_functions=(__passenger_marks_precmd ${${precmd_functions-}:#__passenger_marks_precmd})
        (( ${preexec_functions[(I)__passenger_marks_preexec]-0} )) || preexec_functions+=(__passenger_marks_preexec)

        """;

    private const string FishMarks =
        "function __passenger_marks_prompt --on-event fish_prompt; printf '\\e]133;A\\a'; end; "
        + "function __passenger_marks_preexec --on-event fish_preexec; printf '\\e]133;C\\a'; end; "
        + "function __passenger_marks_postexec --on-event fish_postexec; printf '\\e]133;D;%s\\a' $status; end";

    /// <summary>PowerShell's prompt wrapped: a command line its history gained since the last prompt ran, failed where $?
    /// said so (its exit code, else 1); $? and $LASTEXITCODE given back to the prompt it wraps.</summary>
    internal const string PowerShellMarks =
        "$global:__PassengerPrompt = $function:prompt; $global:__PassengerSeen = (Get-History -Count 1).Id; "
        + "function global:prompt { $ok = $?; $code = $global:LASTEXITCODE; $esc = [char]27; $bel = [char]7; "
        + "$last = (Get-History -Count 1).Id; $marks = ''; "
        + "if ($last -ne $global:__PassengerSeen) { $global:__PassengerSeen = $last; $s = if ($ok) { 0 } elseif ($code) { $code } else { 1 }; "
        + "$marks = $esc + ']133;C' + $bel + $esc + ']133;D;' + $s + $bel }; "
        + "$global:LASTEXITCODE = $code; if (-not $ok) { Write-Error 'failed' -ErrorAction Ignore }; "
        + "$marks + $esc + ']133;A' + $bel + (& $global:__PassengerPrompt) }";

    /// <summary>cmd's prompt mark, before its prompt.</summary>
    private const string CmdMark = "$E]133;A$E\\";

    /// <summary>The program and arguments a shell starts with, and its environment, set up to mark its commands where Passenger
    /// knows how (<paramref name="zshStartup"/> the folder of Passenger's .zshenv); as given for any other program.</summary>
    public static (string App, string[] Arguments) Give(string app, string[] arguments, IDictionary<string, string> environment,
        IReadOnlyDictionary<string, string> inherited, string zshStartup)
    {
        switch (ShellKinds.Of(app))
        {
            case ShellKind.Zsh when !OperatingSystem.IsWindows() && !Runs(arguments, "-c", "-f", "--no-rcs"):
                ZshStartup(environment, inherited, zshStartup);
                return (app, arguments);
            case ShellKind.Bash when !Runs(arguments, "-c"):
                string? prompt = environment.TryGetValue("PROMPT_COMMAND", out string? set) ? set : inherited.GetValueOrDefault("PROMPT_COMMAND");
                environment["PROMPT_COMMAND"] = BashFirst(BashVariable, prompt);
                environment[BashVariable] = BashMarks + (string.IsNullOrEmpty(prompt) ? BashUnexported : BashExported);
                return (app, arguments);
            case ShellKind.Fish when !Runs(arguments, "-c", "--command"):
                return (app, [.. arguments, "-C", FishMarks]);
            case ShellKind.PowerShell:
                return (app, PowerShellArguments(arguments));
            case ShellKind.Cmd when !Runs(arguments, "/c", "/k"):
                string own = environment.TryGetValue("PROMPT", out string? given) ? given : inherited.GetValueOrDefault("PROMPT") ?? "$P$G";
                environment["PROMPT"] = CmdMark + own;
                return (app, arguments);
            default:
                return (app, arguments);
        }
    }

    /// <summary>zsh's first startup file Passenger's .zshenv, which reads the person's own from their ZDOTDIR or home.</summary>
    internal static void ZshStartup(IDictionary<string, string> environment, IReadOnlyDictionary<string, string> inherited, string zshStartup)
    {
        string? own = environment.TryGetValue("ZDOTDIR", out string? set) ? set : inherited.GetValueOrDefault("ZDOTDIR");
        if (own == zshStartup)
        {
            return;
        }
        if (own is { Length: > 0 })
        {
            environment[FirstOnPath.ZdotdirVariable] = own;
        }
        environment["ZDOTDIR"] = zshStartup;
    }

    /// <summary>PowerShell's arguments with its marks set up after its profile: added to the command Passenger's own
    /// arguments run (<see cref="TerminalSession.DefaultShell"/>), else as a command of their own; none where the person's
    /// run a command or a file.</summary>
    private static string[] PowerShellArguments(string[] arguments)
    {
        int command = Array.FindIndex(arguments, a => a.Equals("-Command", StringComparison.OrdinalIgnoreCase));
        if (command == arguments.Length - 2 && arguments.Any(a => a.Equals("-NoExit", StringComparison.OrdinalIgnoreCase)))
        {
            return [.. arguments[..^1], $"{arguments[^1]}; {PowerShellMarks}"];
        }
        return command >= 0 || Runs(arguments, "-c", "-File", "-f", "-EncodedCommand", "-e", "-ec", "-CommandWithArgs", "-cwa")
            ? arguments
            : [.. arguments, "-NoExit", "-Command", PowerShellMarks];
    }

    /// <summary>Whether the arguments name any of these options, which run a command or a file in place of the shell's
    /// prompt, or keep its startup files from it, whatever their letter case.</summary>
    private static bool Runs(string[] arguments, params string[] options) =>
        arguments.Any(a => options.Any(o => a.Equals(o, StringComparison.OrdinalIgnoreCase)));
}
