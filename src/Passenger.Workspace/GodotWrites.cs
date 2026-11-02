namespace Passenger.Workspace;

/// <summary>What a Godot project's text files say written as Godot's editor writes it: a setting of project.godot (an input
/// action, an autoload) put last in its section, the section made in its place among the others where there is none, and a
/// group given to a node in its scene's header. Pure: each takes a file's text and gives the text it becomes, its line
/// breaks the file's.</summary>
public static class GodotWrites
{
    /// <summary>An input action's value as Godot's editor writes one it has just made: no events, the default deadzone.</summary>
    public static string ActionValue(string newline) => $"{{{newline}\"deadzone\": 0.2,{newline}\"events\": []{newline}}}";

    /// <summary>An autoload's value: its file's res:// path, a star before it making it a node of the tree as Godot's editor does.</summary>
    public static string AutoloadValue(string res) => $"\"*{res}\"";

    /// <summary>The line breaks a file's text writes: its first one's, else a line feed.</summary>
    public static string NewlineOf(string text) => text.IndexOf('\n', StringComparison.Ordinal) is > 0 and var at && text[at - 1] == '\r' ? "\r\n" : "\n";

    /// <summary>A key of a section set to a value, put after the section's last entry, the section made where the file has
    /// none (before the first that sorts after it, as Godot keeps them); null where the section has the key already.</summary>
    public static string? WithSetting(string text, string section, string key, string value)
    {
        string newline = NewlineOf(text);
        GodotFile file = GodotFile.Parse(text);
        string line = $"{key}={value}";
        if (file.Sections.FirstOrDefault(s => s.Tag == section) is { } found)
        {
            if (found.Properties.Any(p => p.Key == key))
            {
                return null;
            }
            int after = found.Properties.Count > 0 ? found.Properties[^1].Value.End : found.End;
            string lead = found.Properties.Count > 0 ? newline : newline + newline;
            return text[..after] + lead + line + text[after..];
        }
        string made = $"[{section}]{newline}{newline}{line}{newline}";
        if (file.Sections.FirstOrDefault(s => string.CompareOrdinal(s.Tag, section) > 0) is { } next)
        {
            return text[..next.Start] + made + newline + text[next.Start..];
        }
        string body = text.TrimEnd('\r', '\n');
        return body + newline + newline + made;
    }

    /// <summary>A group given to the node a scene's section declares, after those its header gives (a header with none takes a
    /// <c>groups=[…]</c> before the scene it instances, as Godot writes it); null where it has the group already, or the
    /// section is no node's.</summary>
    public static string? WithGroup(string text, int section, string group)
    {
        GodotFile file = GodotFile.Parse(text);
        if (file.Sections.FirstOrDefault(s => s.Start == section) is not { Tag: "node" } node)
        {
            return null;
        }
        string quoted = Quoted(group);
        if (node.Attribute("groups") is { } groups)
        {
            if (GodotDefinitions.StringsIn(text, groups.Value).Any(g => g.Text == group))
            {
                return null;
            }
            int close = groups.Value.End - 1;
            bool empty = text[(groups.Value.Start + 1)..close].Trim().Length == 0;
            return text[..close] + (empty ? quoted : ", " + quoted) + text[close..];
        }
        GodotEntry? instance = node.Attributes.FirstOrDefault(a => a.Key is "instance" or "instance_placeholder");
        int at = instance is not null ? instance.KeyStart - 1 : node.Attributes.Count > 0 ? node.Attributes[^1].Value.End : node.End - 1;
        return text[..at] + $" groups=[{quoted}]" + text[at..];
    }

    /// <summary>The line a node's header becomes once a group is given to it (<see cref="WithGroup"/>); null where it is not given.</summary>
    public static string? GroupLine(string text, int section, string group) =>
        WithGroup(text, section, group) is { } made ? made[section..LineEnd(made, section)] : null;

    private static int LineEnd(string text, int at)
    {
        int end = text.IndexOf('\n', at);
        end = end < 0 ? text.Length : end;
        return end > at && text[end - 1] == '\r' ? end - 1 : end;
    }

    /// <summary>A GDScript with a function made last, two blank lines before it as Godot's style has them, its body
    /// <c>pass</c> indented as the script's first indented line is (a tab where none is).</summary>
    public static string WithFunction(string text, string name, IReadOnlyList<string> parameters)
    {
        string newline = NewlineOf(text);
        string indent = text.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.Length > 0 && l[0] is ' ' or '\t' && l.Trim().Length > 0) is { } line
            ? line[..(line.Length - line.TrimStart().Length)] : "\t";
        string body = text.TrimEnd('\r', '\n');
        string lead = body.Length == 0 ? string.Empty : newline + newline + newline;
        return $"{body}{lead}func {name}({string.Join(", ", parameters)}) -> void:{newline}{indent}pass{newline}";
    }

    /// <summary>A string as Godot's text writes it, its quotes and backslashes escaped.</summary>
    public static string Quoted(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
