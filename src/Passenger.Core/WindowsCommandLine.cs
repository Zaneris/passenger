using System.Text;

namespace Passenger.Core;

/// <summary>Words as one Windows command line, which a program reading its command line by the C runtime's rules splits
/// back into the same words: each pasted as .NET pastes <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.</summary>
public static class WindowsCommandLine
{
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
}
