using System.Text;

namespace Passenger.Core;

/// <summary>Words as one Windows command line, which a program reading its command line by the C runtime's rules splits
/// back into the same words: each pasted as .NET pastes <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>; and
/// a batch file's run through cmd.exe (<see cref="ThroughCmd"/>), which reads its line by rules of its own.</summary>
public static class WindowsCommandLine
{
    /// <summary>What cmd.exe expands a variable's substring of no length to: nothing, which keeps the <c>%</c> before it
    /// from beginning the name of a variable.</summary>
    private const string NoVariable = "%cd:~,%";

    /// <summary>The ASCII characters other than letters and digits a word goes through cmd.exe with unquoted: none is an
    /// operator, a separator or an escape of cmd's.</summary>
    private const string PlainToCmd = "#$*+-./:?@\\_";

    /// <summary>Windows' own cmd.exe, which alone runs a batch file.</summary>
    public static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>Whether a program is a batch file (.cmd or .bat), which Windows starts only through cmd.exe.</summary>
    public static bool IsBatch(string program) =>
        program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>The words apart by spaces, each as it is where it is not empty and holds no white space or double quote,
    /// else in double quotes, a quote in it escaped and the backslashes before a quote or the closing one doubled.</summary>
    public static string Of(IEnumerable<string> words)
    {
        StringBuilder line = new();
        foreach (string word in words)
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }
            if (word.Length > 0 && !word.Any(c => char.IsWhiteSpace(c) || c == '"'))
            {
                line.Append(word);
                continue;
            }
            line.Append('"');
            int backslashes = 0;
            foreach (char c in word)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                line.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                line.Append(c);
            }
            line.Append('\\', backslashes * 2).Append('"');
        }
        return line.ToString();
    }

    /// <summary>cmd.exe's arguments running a batch file with these words, each reaching it, and a program it hands them
    /// on to with <c>%*</c>, as given: a word holding anything cmd reads as its own is quoted, a quote in it doubled (so
    /// cmd's quoting stays where it was at every read of the line) and a <c>%</c> kept from naming a variable; extensions
    /// on and delayed expansion off, whatever the registry says. An <see cref="ArgumentException"/> for a word holding a
    /// line break or a NUL, which no command line of cmd's carries.</summary>
    public static string ThroughCmd(string program, IEnumerable<string> words)
    {
        StringBuilder line = new("/e:ON /v:OFF /d /s /c \"\"");
        AppendPercentsKept(line, program);
        line.Append('"');
        foreach (string word in words)
        {
            if (word.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            {
                throw new ArgumentException($"{Path.GetFileName(program)} runs through cmd.exe, whose command line carries no line break or NUL in a word", nameof(words));
            }
            line.Append(' ');
            bool quoted = word.Length == 0 || word[^1] == '\\' || word.Any(c => char.IsControl(c) || (char.IsAscii(c) && !char.IsAsciiLetterOrDigit(c) && !PlainToCmd.Contains(c)));
            if (!quoted)
            {
                line.Append(word);
                continue;
            }
            line.Append('"');
            int backslashes = 0;
            foreach (char c in word)
            {
                if (c == '\\')
                {
                    backslashes++;
                    line.Append('\\');
                    continue;
                }
                if (c == '"')
                {
                    line.Append('\\', backslashes).Append('"');
                }
                backslashes = 0;
                AppendPercentsKept(line, c.ToString());
            }
            line.Append('\\', backslashes).Append('"');
        }
        return line.Append('"').ToString();
    }

    /// <summary>Text as it goes through cmd.exe's line, each <c>%</c> in it followed by a variable's empty substring, which
    /// cmd reads in its place, so nothing between two of them is taken for a variable's name.</summary>
    private static void AppendPercentsKept(StringBuilder line, string text)
    {
        foreach (char c in text)
        {
            line.Append(c);
            if (c == '%')
            {
                line.Append(NoVariable);
            }
        }
    }
}
