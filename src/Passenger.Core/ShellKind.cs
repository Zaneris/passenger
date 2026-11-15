namespace Passenger.Core;

/// <summary>A shell by its program (<see cref="ShellKinds.Of"/>): how it reads a quoted word, and which startup files it reads.</summary>
public enum ShellKind
{
    /// <summary>sh, and any shell not known otherwise, which quotes as it does: nothing in single quotes is expanded.</summary>
    Posix,

    /// <summary>zsh, which quotes as sh does.</summary>
    Zsh,

    /// <summary>bash, which quotes as sh does.</summary>
    Bash,

    /// <summary>PowerShell (pwsh, powershell): nothing in single quotes is expanded, each single quote in them doubled.</summary>
    PowerShell,

    /// <summary>Windows' cmd.exe, which expands %NAME% inside double quotes too.</summary>
    Cmd,

    /// <summary>fish: nothing in single quotes is expanded, but a backslash there escapes a backslash or a single quote.</summary>
    Fish,

    /// <summary>Nushell (nu): its single quotes take no escapes, so a single quote goes only in a raw string, r#'…'#.</summary>
    Nushell,
}

/// <summary>What kind of shell a program is.</summary>
public static class ShellKinds
{
    /// <summary>The kind of shell a program is, by its file's name in any letter case, an .exe left out; one not known
    /// otherwise is sh's.</summary>
    public static ShellKind Of(string program)
    {
        string name = program[(program.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".exe".Length];
        }
        return name.ToLowerInvariant() switch
        {
            "zsh" => ShellKind.Zsh,
            "bash" => ShellKind.Bash,
            "pwsh" or "powershell" => ShellKind.PowerShell,
            "cmd" => ShellKind.Cmd,
            "fish" => ShellKind.Fish,
            "nu" => ShellKind.Nushell,
            _ => ShellKind.Posix,
        };
    }

    /// <summary>Whether a shell reads quotes as sh does: sh, zsh and bash.</summary>
    public static bool QuotesAsSh(this ShellKind shell) => shell is ShellKind.Posix or ShellKind.Zsh or ShellKind.Bash;
}
