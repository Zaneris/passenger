using Passenger.Core;

namespace Passenger.Terminal;

/// <summary>A path, or any word, written for a shell as one word it takes as it is, nothing in it expanded or run: bare
/// where nothing in it means anything to that shell, else quoted as the shell reads quotes.</summary>
public static class ShellWords
{
    /// <summary>The quotes PowerShell reads as a single quote: the ASCII one and the typographic ones.</summary>
    private const string PowerShellQuotes = "'‘’‚‛";

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
        string marks = shell.QuotesAsSh() ? "/._-+:@%,=" : shell switch
        {
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
