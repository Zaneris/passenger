namespace Passenger.Host;

/// <summary>File-based programs (<c>dotnet run app.cs</c>) as their C# file's head tells them, one definition for both ends
/// (the app compiles this file too): a C# file named where a project would be is a program, whose project the SDK makes
/// from its directives (<c>#:package</c>, <c>#:include</c> and the rest), the lines before its first token and any
/// <c>#if</c>, after a <c>#!</c> line.</summary>
public static class ProgramFiles
{
    /// <summary>The most lines read for a head: past them, a file's directives are not looked for.</summary>
    private const int HeadLines = 400;

    /// <summary>Whether a project's path is a program's C# file rather than a project file.</summary>
    public static bool IsProgram(string path) => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>The metadata the SDK puts on the reference a program's <c>#:ref</c> makes, naming the C# file referenced: the
    /// program whose project it is.</summary>
    public const string RefMetadata = "FileBasedProgramsFromRefDirective";

    /// <summary>A file's head: its <c>#!</c> line and each <c>#:</c> directive, trimmed, in order; none for a file not
    /// there or that does not read.</summary>
    public static List<string> Head(string path)
    {
        try
        {
            using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return Head(Lines(reader));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> Lines(StreamReader reader)
    {
        for (int read = 0; read < HeadLines && reader.ReadLine() is { } line; read++)
        {
            yield return line;
        }
    }

    /// <summary>The head of a file's lines (<see cref="Head(string)"/>): comments and other preprocessor lines pass, the first
    /// token or <c>#if</c> ends it.</summary>
    public static List<string> Head(IEnumerable<string> lines) => [.. HeadAt(lines).Select(h => h.Text)];

    /// <summary>The head of a file's lines as <see cref="Head(IEnumerable{string})"/> finds it, each line trimmed, with its
    /// index among them.</summary>
    public static List<(int Line, string Text)> HeadAt(IEnumerable<string> lines)
    {
        List<(int Line, string Text)> head = [];
        bool comment = false;
        int index = -1;
        foreach (string line in lines)
        {
            index++;
            string text = line.Trim();
            if (index == 0 && text.StartsWith("#!", StringComparison.Ordinal))
            {
                head.Add((index, text));
                continue;
            }
            if (comment)
            {
                int end = text.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }
                comment = false;
                text = text[(end + 2)..].TrimStart();
            }
            while (text.StartsWith("/*", StringComparison.Ordinal))
            {
                int end = text.IndexOf("*/", 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    comment = true;
                    break;
                }
                text = text[(end + 2)..].TrimStart();
            }
            if (comment || text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }
            if (text.StartsWith("#:", StringComparison.Ordinal))
            {
                head.Add((index, text));
                continue;
            }
            if (!text.StartsWith('#') || text.StartsWith("#if", StringComparison.Ordinal))
            {
                break;
            }
        }
        return head;
    }

    /// <summary>A directive's kind and its value, as the SDK splits them: <c>#:include "my file.cs"</c> is ("include",
    /// "my file.cs"), a quoted value read as a C# string's; null for the <c>#!</c> line.</summary>
    public static (string Kind, string Value)? Parse(string directive)
    {
        if (!directive.StartsWith("#:", StringComparison.Ordinal))
        {
            return null;
        }
        string rest = directive[2..];
        int space = rest.IndexOfAny([' ', '\t']);
        string kind = space < 0 ? rest : rest[..space];
        string value = space < 0 ? string.Empty : rest[(space + 1)..].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        return (kind, value);
    }
}
