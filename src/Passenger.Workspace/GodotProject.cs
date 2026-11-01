using System.Text;

namespace Passenger.Workspace;

/// <summary>A Godot project as its project.godot says it: its name, the scene its game starts in, and the assembly Godot
/// loads its C# from (<c>dotnet/project/assembly_name</c>), each null where the file sets none. Read from Godot's
/// ConfigFile text: <c>[section]</c> lines, then <c>key=value</c>, a value a quoted string with escapes or any other of
/// Godot's values (numbers, <c>PackedStringArray(…)</c>, a dictionary over several lines), comments after <c>;</c>.</summary>
public sealed record GodotProject(string Folder, string? Name, string? MainScene, string? AssemblyName)
{
    /// <summary>The file that makes a folder a Godot project.</summary>
    public const string FileName = "project.godot";

    /// <summary>The Godot project of a folder, or null when it has no project.godot or it cannot be read.</summary>
    public static GodotProject? Read(string folder)
    {
        string file = Path.Combine(folder, FileName);
        try
        {
            return File.Exists(file) ? Parse(folder, File.ReadAllText(file)) : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The project a project.godot's text says, of its folder.</summary>
    public static GodotProject Parse(string folder, string text)
    {
        Dictionary<string, string> values = Strings(text);
        return new GodotProject(folder, values.GetValueOrDefault("application/config/name"), values.GetValueOrDefault("application/run/main_scene"),
            values.GetValueOrDefault("dotnet/project/assembly_name"));
    }

    /// <summary>Every value of the text that is a string, by its section and key ("application/config/name"); a key set
    /// twice keeps its last, as Godot reads it.</summary>
    internal static Dictionary<string, string> Strings(string text)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        string section = string.Empty;
        int at = 0;
        while (at < text.Length)
        {
            at = SkipBlank(text, at);
            if (at >= text.Length)
            {
                break;
            }
            if (text[at] == ';' || text[at] == '#')
            {
                at = LineEnd(text, at);
                continue;
            }
            if (text[at] == '[')
            {
                int close = text.IndexOf(']', at);
                int end = LineEnd(text, at);
                section = close > at && close < end ? text[(at + 1)..close].Trim() : section;
                at = end;
                continue;
            }
            int equals = text.IndexOf('=', at);
            int lineEnd = LineEnd(text, at);
            if (equals < 0 || equals > lineEnd)
            {
                at = lineEnd;
                continue;
            }
            string key = text[at..equals].Trim();
            int value = SkipSpaces(text, equals + 1);
            if (value < text.Length && text[value] == '"')
            {
                (string read, int next) = Quoted(text, value);
                found[section.Length > 0 ? $"{section}/{key}" : key] = read;
                at = next;
            }
            else
            {
                at = ValueEnd(text, value);
            }
        }
        return found;
    }

    /// <summary>A quoted string from its opening quote, its escapes read, and where the text goes on after it; one never
    /// closed runs to the text's end.</summary>
    private static (string Value, int Next) Quoted(string text, int quote)
    {
        StringBuilder value = new();
        int at = quote + 1;
        while (at < text.Length && text[at] != '"')
        {
            if (text[at] == '\\' && at + 1 < text.Length)
            {
                at++;
                value.Append(text[at] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => text[at] });
            }
            else
            {
                value.Append(text[at]);
            }
            at++;
        }
        return (value.ToString(), Math.Min(at + 1, text.Length));
    }

    /// <summary>Where a value other than a string ends: its line's end, or past the brackets it opens and the strings in them.</summary>
    private static int ValueEnd(string text, int at)
    {
        int depth = 0;
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '"')
            {
                at = Quoted(text, at).Next;
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == '\n' && depth == 0)
            {
                return at + 1;
            }
            at++;
        }
        return at;
    }

    private static int LineEnd(string text, int at)
    {
        int end = text.IndexOf('\n', at);
        return end < 0 ? text.Length : end + 1;
    }

    private static int SkipBlank(string text, int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at]))
        {
            at++;
        }
        return at;
    }

    private static int SkipSpaces(string text, int at)
    {
        while (at < text.Length && text[at] is ' ' or '\t')
        {
            at++;
        }
        return at;
    }
}
