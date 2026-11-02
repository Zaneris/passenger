using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Passenger.Core;

/// <summary>What puts a .NET installation on the PATH of the shells a person starts outside Passenger: lines added to the
/// end of their login shell's startup file on macOS and Linux (zsh's .zshrc, bash's .bash_profile on macOS and .bashrc
/// elsewhere, fish's conf.d, else .profile), or, on Windows, the folder put first on the user's Path and named by their
/// DOTNET_ROOT. <see cref="Shown"/> says it in a line before it is made.</summary>
public sealed partial record ShellPath(string Root, string? File, string Lines, string Shown)
{
    /// <summary>A test's stand-in for the home folder whose startup files take the lines; null for the person's own.</summary>
    public static string? HomeStandIn { get; set; }

    /// <summary>A test's stand-in for Windows' user environment, its variables by name; null for the person's own.</summary>
    public static Dictionary<string, string>? UserStandIn { get; set; }

    /// <summary>The change for <paramref name="root"/>, by the login shell's name; null where it is made already.</summary>
    public static ShellPath? For(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            string path = UserVariable("Path") ?? string.Empty;
            return path.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(p => PathNames.Same(Environment.ExpandEnvironmentVariables(p), root))
                && PathNames.Same(UserVariable(RuntimeVariables.Root) ?? string.Empty, root)
                ? null
                : new ShellPath(root, null, root, $"puts {root} first on your user Path and sets your DOTNET_ROOT to it, for the programs you start from now on");
        }
        string home = HomeStandIn ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string file, string lines) = Of(Path.GetFileName(ShellEnvironment.LoginShell()), home, root, OperatingSystem.IsMacOS(), Environment.GetEnvironmentVariable("ZDOTDIR"));
        try
        {
            if (System.IO.File.Exists(file) && System.IO.File.ReadAllText(file).Contains(lines, StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            // Unread, it is written to as the change makes it, which says why it cannot be.
        }
        return new ShellPath(root, file, lines, $"adds {Plural(lines)} to the end of {PathNames.Shown(file, null)}, which put {PathNames.Shown(root, null)} first on the PATH of the shells you start from now on");
    }

    /// <summary>The startup file and its lines for a shell by name (zsh, bash, fish, any other taking .profile) in
    /// <paramref name="home"/>: DOTNET_ROOT naming <paramref name="root"/> (from $HOME where it is under it) and the PATH
    /// with it first.</summary>
    public static (string File, string Lines) Of(string shell, string home, string root, bool macOS, string? zdotdir)
    {
        string named = PathNames.IsUnder(root, home) && !PathNames.Same(root, home)
            ? "$HOME/" + Path.GetRelativePath(home, root).Replace('\\', '/')
            : root;
        string quoted = '"' + named.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
        const string Said = "# The .NET SDK Passenger installed";
        return shell switch
        {
            "fish" => (Path.Combine(home, ".config", "fish", "conf.d", "dotnet.fish"),
                $"{Said}\nset -gx DOTNET_ROOT {quoted}\nset -gx PATH \"$DOTNET_ROOT\" $PATH\n"),
            "zsh" => (Path.Combine(zdotdir is { Length: > 0 } ? zdotdir : home, ".zshrc"), Posix(quoted)),
            "bash" => (Path.Combine(home, macOS ? ".bash_profile" : ".bashrc"), Posix(quoted)),
            _ => (Path.Combine(home, ".profile"), Posix(quoted)),
        };

        static string Posix(string quoted) => $"{Said}\nexport DOTNET_ROOT={quoted}\nexport PATH=\"$DOTNET_ROOT:$PATH\"\n";
    }

    /// <summary>Makes the change: the lines added to the end of the startup file (made if it is not there), or Windows' user
    /// variables set, programs started since told the environment changed.</summary>
    public void Make()
    {
        if (File is null)
        {
            if (OperatingSystem.IsWindows())
            {
                string path = UserVariable("Path") ?? string.Empty;
                SetUserVariable("Path", string.Join(';', [Root, .. path.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => !PathNames.Same(Environment.ExpandEnvironmentVariables(p), Root))]));
                SetUserVariable(RuntimeVariables.Root, Root);
            }
            return;
        }
        string had = System.IO.File.Exists(File) ? System.IO.File.ReadAllText(File) : string.Empty;
        string written = had.Length == 0 ? Lines : had.EndsWith('\n') ? $"{had}\n{Lines}" : $"{had}\n\n{Lines}";
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        AtomicFile.Write(File, Encoding.UTF8.GetBytes(written));
    }

    private static string Plural(string lines) => $"{lines.Count(c => c == '\n')} lines";

    private static string? UserVariable(string name)
    {
        if (UserStandIn is { } standIn)
        {
            return standIn.GetValueOrDefault(name);
        }
        return OperatingSystem.IsWindows() ? Registered(name) : null;
    }

    private static void SetUserVariable(string name, string value)
    {
        if (UserStandIn is { } standIn)
        {
            standIn[name] = value;
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            Register(name, value);
        }
    }

    /// <summary>A user variable as the registry holds it, its %names% left unexpanded.</summary>
    [SupportedOSPlatform("windows")]
    private static string? Registered(string name)
    {
        using RegistryKey? environment = Registry.CurrentUser.OpenSubKey("Environment");
        return environment?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>A user variable written as one that may hold %names%, then the change told to every window, as Windows' own
    /// settings tell it, so a program started from Explorer next takes it.</summary>
    [SupportedOSPlatform("windows")]
    private static void Register(string name, string value)
    {
        using (RegistryKey environment = Registry.CurrentUser.CreateSubKey("Environment"))
        {
            environment.SetValue(name, value, RegistryValueKind.ExpandString);
        }
        // A window that does not answer within 5 s is passed over, as Windows' own settings pass it over.
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
    }

    [LibraryImport("user32", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
