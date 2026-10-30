namespace Passenger.Terminal;

/// <summary>A shell by how it reads a quoted word.</summary>
public enum ShellKind
{
    /// <summary>sh and the shells that quote as it does (bash, zsh): nothing in single quotes is expanded.</summary>
    Posix,

    /// <summary>PowerShell (pwsh, powershell): nothing in single quotes is expanded, each single quote in them doubled.</summary>
    PowerShell,

    /// <summary>Windows' cmd.exe, which expands %NAME% inside double quotes too.</summary>
    Cmd,

    /// <summary>fish: nothing in single quotes is expanded, but a backslash there escapes a backslash or a single quote.</summary>
    Fish,

    /// <summary>Nushell (nu): its single quotes take no escapes, so a single quote goes only in a raw string, r#'…'#.</summary>
    Nushell,
}

/// <summary>A path written for a shell as one word it takes as it is, nothing in it expanded or run: bare where nothing
/// in it means anything to that shell, else quoted as the shell reads quotes.</summary>
public static class ShellWords
{
    /// <summary>The quotes PowerShell reads as a single quote: the ASCII one and the typographic ones.</summary>
    private const string PowerShellQuotes = "'‘’‚‛";

    /// <summary>The kind of shell a program is, by its file's name; one not known otherwise quotes as sh does.</summary>
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
            "fish" => ShellKind.Fish,
            "nu" => ShellKind.Nushell,
            _ => ShellKind.Posix,
        };
    }

    /// <summary>A path as one word the shell takes as it is.</summary>
    public static string Quoted(string path, ShellKind shell)
    {
        if (IsBare(path, shell))
        {
            return path;
        }
        return shell switch
        {
            ShellKind.PowerShell => $"'{string.Concat(path.Select(c => PowerShellQuotes.Contains(c) ? $"{c}{c}" : c.ToString()))}'",
            ShellKind.Cmd => string.Join("^%", path.Split('%').Select(CmdQuoted)),
            ShellKind.Fish => $"'{path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'",
            ShellKind.Nushell => NushellRaw(path),
            _ => $"'{path.Replace("'", "'\\''", StringComparison.Ordinal)}'",
        };
    }

    /// <summary>Whether the shell takes the path as it is unquoted. fish 2 expands a word from its %, and Nushell reads a
    /// bare word that is not a path as a value of its own (10kb a file size), so only a path from the root goes bare there.</summary>
    private static bool IsBare(string path, ShellKind shell)
    {
        string marks = shell switch
        {
            ShellKind.Posix => "/._-+:@%,=",
            ShellKind.Fish => "/._-+:@,=",
            ShellKind.Nushell => "/._-",
            _ => "/\\._-:",
        };
        return path.Length > 0 && (shell != ShellKind.Nushell || path[0] == '/') && path.All(c => char.IsLetterOrDigit(c) || marks.Contains(c));
    }

    /// <summary>A path in Nushell's raw string, with one more # than any run of them after a single quote in it, so
    /// nothing in it ends the string.</summary>
    private static string NushellRaw(string path)
    {
        string hashes = "#";
        while (path.Contains($"'{hashes}", StringComparison.Ordinal))
        {
            hashes += "#";
        }
        return $"r{hashes}'{path}'{hashes}";
    }

    /// <summary>A piece of a path between its %s in cmd's double quotes, each % going between two behind a caret (so no
    /// variable's name lies between two of them); the backslashes before its closing quote doubled, so a program reading
    /// its command line as Windows' runtime does takes that quote as one.</summary>
    private static string CmdQuoted(string piece) => $"\"{piece}{new string('\\', piece.Length - piece.TrimEnd('\\').Length)}\"";
}
