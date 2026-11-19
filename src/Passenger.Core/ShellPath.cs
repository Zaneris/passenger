using System.Text;

namespace Passenger.Core;

/// <summary>What puts a folder on the PATH of the shells a person starts outside Passenger: a .NET installation (with
/// DOTNET_ROOT naming it) or the folder of a command (<see cref="ForCommands"/>). On macOS and Linux, lines added to the
/// end of their login shell's startup file (zsh's .zshrc; bash's on macOS the first of .bash_profile, .bash_login and
/// .profile there, as a login shell reads only that one, and .bashrc elsewhere; fish's conf.d; else .profile); on Windows,
/// the folder put on the user's Path (<see cref="UserEnvironment"/>), first for .NET's, which also names it as their
/// DOTNET_ROOT. <see cref="Shown"/> says it in a line before it is made.</summary>
public sealed record ShellPath(string Root, string? File, string Lines, string Shown)
{
    /// <summary>A test's stand-in for the home folder whose startup files take the lines, ZDOTDIR passed over; null for the
    /// person's own.</summary>
    public static string? HomeStandIn { get; set; }

    /// <summary>A test's stand-in for Windows' user environment, its variables by name; null for the person's own.</summary>
    public static Dictionary<string, string>? UserStandIn
    {
        get => UserEnvironment.StandIn;
        set => UserEnvironment.StandIn = value;
    }

    /// <summary>The variable naming the folder on Windows (DOTNET_ROOT for .NET's), null for a command's.</summary>
    private string? Variable { get; init; } = RuntimeVariables.Root;

    /// <summary>The home folder whose startup files take the lines: a test's, else the person's.</summary>
    public static string Home => HomeStandIn ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The folder zsh reads its startup files from where it is not the home: none for a test's home.</summary>
    private static string? Zdotdir => HomeStandIn is null ? Environment.GetEnvironmentVariable("ZDOTDIR") : null;

    /// <summary>The change for <paramref name="root"/>, by the login shell's kind; null where it is made already.</summary>
    public static ShellPath? For(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return UserEnvironment.Holds(root)
                && PathNames.Same(UserEnvironment.Get(RuntimeVariables.Root) ?? string.Empty, root)
                ? null
                : new ShellPath(root, null, root, $"puts {root} first on your user Path and sets your DOTNET_ROOT to it, for the programs you start from now on");
        }
        (string file, string lines) = Of(ShellKinds.Of(ShellEnvironment.LoginShell()), Home, root, OperatingSystem.IsMacOS(), Zdotdir, System.IO.File.Exists);
        return Written(file, lines) ? null
            : new ShellPath(root, file, lines, $"adds {Plural(lines)} to the end of {PathNames.Shown(file, null)}, which put {PathNames.Shown(root, null)} first on the PATH of the shells you start from now on");
    }

    /// <summary>The change putting <paramref name="folder"/>, where <paramref name="command"/> is, on the PATH of the shells
    /// the person starts from now on, at its end on Windows' user Path; null where it is there already: on the PATH this
    /// process has from their shell (<see cref="ShellEnvironment.Ready"/> once it has read it), or in their startup file's
    /// lines.</summary>
    public static ShellPath? ForCommands(string folder, string command)
    {
        if (OperatingSystem.IsWindows())
        {
            return UserEnvironment.Holds(folder) ? null
                : new ShellPath(folder, null, folder, $"adds {folder} to the end of your user Path, for the programs you start from now on") { Variable = null };
        }
        if ((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Any(on => on.Length > 0 && PathNames.Same(on, folder)))
        {
            return null;
        }
        (string file, string lines) = CommandsOf(ShellKinds.Of(ShellEnvironment.LoginShell()), Home, folder, command, OperatingSystem.IsMacOS(),
            Zdotdir, System.IO.File.Exists);
        return Written(file, lines) ? null
            : new ShellPath(folder, file, lines, $"adds {Plural(lines)} to the end of {PathNames.Shown(file, null)}, which put {PathNames.Shown(folder, null)} on the PATH of the shells you start from now on")
            {
                Variable = null,
            };
    }

    /// <summary>Whether the startup file holds the lines already; one that cannot be read is written to as the change makes
    /// it, which says why it cannot be.</summary>
    private static bool Written(string file, string lines)
    {
        try
        {
            return System.IO.File.Exists(file) && System.IO.File.ReadAllText(file).Contains(lines, StringComparison.Ordinal);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The startup file and its lines for a shell of a kind (zsh, bash, fish, any other taking .profile) in
    /// <paramref name="home"/>, bash's on macOS the first of its login files that <paramref name="exists"/>: DOTNET_ROOT
    /// naming <paramref name="root"/> (from $HOME where it is under it), quoted so the shell reads it as written, and the
    /// PATH with it first.</summary>
    public static (string File, string Lines) Of(ShellKind shell, string home, string root, bool macOS, string? zdotdir, Func<string, bool> exists)
    {
        const string Said = "# The .NET SDK Passenger installed";
        return shell == ShellKind.Fish
            ? (Startup(shell, home, macOS, zdotdir, exists, "dotnet"), $"{Said}\nset -gx DOTNET_ROOT \"{Inside(root, home, FishRead)}\"\nset -gx PATH \"$DOTNET_ROOT\" $PATH\n")
            : (Startup(shell, home, macOS, zdotdir, exists, "dotnet"), $"{Said}\nexport DOTNET_ROOT=\"{Inside(root, home, PosixRead)}\"\nexport PATH=\"$DOTNET_ROOT:$PATH\"\n");
    }

    /// <summary>The startup file and its lines putting <paramref name="folder"/>, where <paramref name="command"/> is, first
    /// on the PATH of a shell of a kind, as <see cref="Of"/> chooses the file; fish's its own in conf.d, named after the
    /// command.</summary>
    public static (string File, string Lines) CommandsOf(ShellKind shell, string home, string folder, string command, bool macOS, string? zdotdir, Func<string, bool> exists)
    {
        string said = $"# Where Passenger's {command} command is";
        return shell == ShellKind.Fish
            ? (Startup(shell, home, macOS, zdotdir, exists, command), $"{said}\nset -gx PATH \"{Inside(folder, home, FishRead)}\" $PATH\n")
            : (Startup(shell, home, macOS, zdotdir, exists, command), $"{said}\nexport PATH=\"{Inside(folder, home, PosixRead)}:$PATH\"\n");
    }

    /// <summary>The file a login shell of a kind reads its lines from in a home; fish's a file of conf.d named
    /// <paramref name="fishName"/>.</summary>
    private static string Startup(ShellKind shell, string home, bool macOS, string? zdotdir, Func<string, bool> exists, string fishName) => shell switch
    {
        ShellKind.Fish => Path.Combine(home, ".config", "fish", "conf.d", $"{fishName}.fish"),
        ShellKind.Zsh => Path.Combine(zdotdir is { Length: > 0 } ? zdotdir : home, ".zshrc"),
        ShellKind.Bash when macOS => BashLogin(home, exists),
        ShellKind.Bash => Path.Combine(home, ".bashrc"),
        _ => Path.Combine(home, ".profile"),
    };

    /// <summary>A folder as written inside double quotes (from $HOME where it is under it), which keep spaces, a backslash
    /// before each character a shell still reads inside them, so it takes the folder as written.</summary>
    private static string Inside(string folder, string home, string read)
    {
        string? below = PathNames.IsUnder(folder, home) ? PathNames.Relative(folder, home) : null;
        return (below is not null ? "$HOME/" : string.Empty) + string.Concat((below ?? folder).Select(c => read.Contains(c, StringComparison.Ordinal) ? $"\\{c}" : c.ToString()));
    }

    /// <summary>What a POSIX shell still reads inside double quotes: a backslash, a quote, a $ and a backtick.</summary>
    private const string PosixRead = "\\\"$`";

    /// <summary>What fish still reads inside double quotes, its command substitution taking no backtick.</summary>
    private const string FishRead = "\\\"$";

    /// <summary>The file a bash login shell reads in a home: the first of .bash_profile, .bash_login and .profile there, for it
    /// reads no other, and .bash_profile where none is.</summary>
    private static string BashLogin(string home, Func<string, bool> exists) =>
        ((string[])[".bash_profile", ".bash_login", ".profile"]).Select(name => Path.Combine(home, name)).FirstOrDefault(exists) ?? Path.Combine(home, ".bash_profile");

    /// <summary>Makes the change: the lines added to the end of the startup file (made if it is not there; one that is a link,
    /// into a dotfiles repository, kept one and the file it names written), or the folder put on Windows' user Path (and
    /// named by its variable), programs started since told the environment changed.</summary>
    public void Make()
    {
        if (File is null)
        {
            if (OperatingSystem.IsWindows())
            {
                UserEnvironment.Put(Root, first: Variable is not null);
                if (Variable is { } variable)
                {
                    UserEnvironment.Set(variable, Root);
                }
            }
            return;
        }
        string had = System.IO.File.Exists(File) ? System.IO.File.ReadAllText(File) : string.Empty;
        string written = had.Length == 0 ? Lines : had.EndsWith('\n') ? $"{had}\n{Lines}" : $"{had}\n\n{Lines}";
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        AtomicFile.Write(AtomicFile.Target(File), Encoding.UTF8.GetBytes(written));
    }

    private static string Plural(string lines) => $"{lines.Count(c => c == '\n')} lines";
}
