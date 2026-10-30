namespace Passenger.Terminal;

/// <summary>A shell by how it reads a quoted word.</summary>
public enum ShellKind
{
    /// <summary>sh and the shells that quote as it does (bash, zsh, fish): nothing in single quotes is expanded.</summary>
    Posix,

    /// <summary>PowerShell (pwsh, powershell): nothing in single quotes is expanded, each single quote in them doubled.</summary>
    PowerShell,

    /// <summary>Windows' cmd.exe, which expands %NAME% inside double quotes too.</summary>
    Cmd,
}

/// <summary>A path written for a shell as one word it takes as it is, nothing in it expanded or run: bare where nothing
/// in it means anything to that shell, else quoted as the shell reads quotes.</summary>
public static class ShellWords
{
    /// <summary>The quotes PowerShell reads as a single quote: the ASCII one and the typographic ones.</summary>
    private const string PowerShellQuotes = "'‘’‚‛";

    /// <summary>The kind of shell a program is, by its file's name; one not known to be PowerShell or cmd quotes as sh does.</summary>
    public static ShellKind KindOf(string program)
    {
        string name = program[(program.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".exe".Length];
        }
        return name.ToLowerInvariant() switch
        {
            "pwsh" or "powershell" => ShellKind.PowerShell,
            "cmd" => ShellKind.Cmd,
            _ => ShellKind.Posix,
        };
    }

    /// <summary>A path as one word the shell takes as it is.</summary>
    public static string Quoted(string path, ShellKind shell)
    {
        if (path.Length > 0 && path.All(c => char.IsLetterOrDigit(c) || (shell == ShellKind.Posix ? "/._-+:@%,=" : "/\\._-:").Contains(c)))
        {
            return path;
        }
        return shell switch
        {
            ShellKind.PowerShell => $"'{string.Concat(path.Select(c => PowerShellQuotes.Contains(c) ? $"{c}{c}" : c.ToString()))}'",
            ShellKind.Cmd => string.Join("^%", path.Split('%').Select(CmdQuoted)),
            _ => $"'{path.Replace("'", "'\\''", StringComparison.Ordinal)}'",
        };
    }

    /// <summary>A piece of a path between its %s in cmd's double quotes, each % going between two behind a caret (so no
    /// variable's name lies between two of them); the backslashes before its closing quote doubled, so a program reading
    /// its command line as Windows' runtime does takes that quote as one.</summary>
    private static string CmdQuoted(string piece) => $"\"{piece}{new string('\\', piece.Length - piece.TrimEnd('\\').Length)}\"";
}
