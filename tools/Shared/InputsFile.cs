namespace Passenger.Tools;

/// <summary>The file of what the build hands one of its tools: one <c>name=value</c> a line, both sides trimmed, blank
/// lines passed over. A line that is no name=value, or a value of two parts without its '|', fails naming the file, as
/// the tool's own <paramref name="failure"/>.</summary>
public sealed class InputsFile(string path, Func<string, Exception> failure)
{
    /// <summary>Each line's name and value, in the file's order.</summary>
    public IEnumerable<(string Name, string Value)> Lines()
    {
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                throw Failure($"'{line}' is not name=value");
            }
            yield return (line[..equals].Trim(), line[(equals + 1)..].Trim());
        }
    }

    /// <summary>A value of two parts written first|second, the second holding any further '|'; <paramref name="takes"/>
    /// says what the name takes, for the failure of a value without one.</summary>
    public (string First, string Second) Pair(string name, string value, string takes) =>
        value.Split('|', 2) is [string first, string second] ? (first, second) : throw Failure($"{name} takes {takes}, not '{value}'");

    /// <summary>The tool's failure for what is wrong in the file, naming it.</summary>
    public Exception Failure(string problem) => failure($"{path}: {problem}");
}
