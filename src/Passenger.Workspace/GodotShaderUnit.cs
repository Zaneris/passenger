namespace Passenger.Workspace;

/// <summary>What Godot's front end said of a shader: an error or a warning, the file it names (empty for the shader itself,
/// else an include's res:// path), its line (from one) and Godot's words.</summary>
public sealed record GodotShaderMessage(bool Error, string File, int Line, string Text, bool Note = false);

/// <summary>A mark a compile's messages put on a file's text: where, whether an error, its words, and whether a note of
/// Passenger's own reading, which says nothing is wrong with the shader.</summary>
public sealed record GodotShaderMark(int Start, int End, bool Error, string Message, bool Note = false);

/// <summary>A shader as Godot is given it to compile: its res:// path (a scene's own Shader by its file's path and the
/// section's id, <c>res://level.tscn::Shader_x</c>, as Godot names it), its code, and the text of every file its
/// <c>#include</c>s reach, by the res:// path Godot resolves each to; those it names that no file answers are
/// <see cref="Missing"/>. Pure.</summary>
public sealed record GodotShaderUnit(string Res, string Code, IReadOnlyDictionary<string, string> Includes, IReadOnlyList<string> Missing)
{
    /// <summary>What Godot's preprocessor says of an include no file answers.</summary>
    public const string NoInclude = "Shader include file does not exist:";

    /// <summary>How deep includes are followed, as Godot's preprocessor follows them.</summary>
    private const int Deepest = 25;

    /// <summary>The shader at <paramref name="res"/> with its code, its includes read through <paramref name="textOf"/>
    /// (a file's text by res:// path, null for none), each once.</summary>
    public static GodotShaderUnit Of(string res, string code, Func<string, string?> textOf)
    {
        Dictionary<string, string> includes = new(StringComparer.Ordinal);
        List<string> missing = [];
        Follow(res, code, 0);
        return new GodotShaderUnit(res, code, includes, missing);

        void Follow(string from, string text, int depth)
        {
            if (depth > Deepest)
            {
                return;
            }
            foreach (GodotShaderInclude include in GodotShaderCode.Read(text).Includes)
            {
                string path = Resolved(from, include.Path);
                if (includes.ContainsKey(path) || missing.Contains(path))
                {
                    continue;
                }
                if (textOf(path) is { } read)
                {
                    includes[path] = read;
                    Follow(path, read, depth + 1);
                }
                else
                {
                    missing.Add(path);
                }
            }
        }
    }

    /// <summary>The res:// path an include written in a file resolves to, as Godot resolves it: simplified, and a relative
    /// one taken from the folder of the file (a scene's own Shader's, its scene's).</summary>
    public static string Resolved(string from, string written)
    {
        string path = written.Replace('\\', '/');
        if (!path.StartsWith("res://", StringComparison.Ordinal) && !path.StartsWith("user://", StringComparison.Ordinal))
        {
            string file = from.Split("::")[0];
            int slash = file.LastIndexOf('/');
            string folder = slash >= "res://".Length ? file[..slash] : "res:/";
            path = $"{folder}/{path}";
        }
        int scheme = path.IndexOf("://", StringComparison.Ordinal) + 3;
        List<string> parts = [];
        foreach (string part in path[scheme..].Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }
            if (part == ".." && parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else if (part != "..")
            {
                parts.Add(part);
            }
        }
        return path[..scheme] + string.Join('/', parts);
    }

    /// <summary>The marks a compile's messages put on one of the unit's texts: the shader's own (<paramref name="include"/>
    /// null) or an include's. A message on a line of the text is marked there, on the name it quotes where the line writes
    /// it; one in a file the text includes is marked on the <c>#include</c> leading to it, naming its place; an include no
    /// file answers is marked on its path with Godot's preprocessor's words, which its message then says again.</summary>
    public IReadOnlyList<GodotShaderMark> MarksIn(string? include, IReadOnlyList<GodotShaderMessage> messages)
    {
        string res = include ?? Res;
        string text = include is null ? Code : Includes.GetValueOrDefault(include) ?? string.Empty;
        GodotShaderCode code = GodotShaderCode.Read(text);
        List<GodotShaderMark> marks = [];
        foreach (GodotShaderInclude written in code.Includes)
        {
            if (Missing.Contains(Resolved(res, written.Path)))
            {
                marks.Add(new GodotShaderMark(written.Start, written.End, true, $"{NoInclude} {Resolved(res, written.Path)}"));
            }
        }
        foreach (GodotShaderMessage message in messages)
        {
            if (message.Text.StartsWith(NoInclude, StringComparison.Ordinal) && marks.Any(m => m.Message == message.Text))
            {
                continue;
            }
            bool here = message.File.Length == 0 ? include is null : message.File == include;
            if (here)
            {
                (int start, int end) = OnLine(text, message.Line, message.Text);
                marks.Add(new GodotShaderMark(start, end, message.Error, message.Text, message.Note));
            }
            else if (message.File.Length > 0 && code.Includes.FirstOrDefault(i => Reaches(Resolved(res, i.Path), message.File, 0)) is { } leading)
            {
                marks.Add(new GodotShaderMark(leading.Start, leading.End, message.Error, $"{message.File}:{message.Line}: {message.Text}", message.Note));
            }
        }
        return marks;
    }

    /// <summary>Whether a file of the unit is, or includes through its own includes, another.</summary>
    private bool Reaches(string from, string target, int depth)
    {
        if (from == target)
        {
            return true;
        }
        if (depth > Deepest || !Includes.TryGetValue(from, out string? text))
        {
            return false;
        }
        return GodotShaderCode.Read(text).Includes.Any(i => Reaches(Resolved(from, i.Path), target, depth + 1));
    }

    /// <summary>Where a message on a line (from one, lines ended by '\n' as Godot's compiler counts them) is marked: the name
    /// it quotes ('speedd') where the line writes it as a word, else the line's text but its white space.</summary>
    internal static (int Start, int End) OnLine(string text, int line, string message)
    {
        int start = 0;
        for (int at = 1; at < Math.Max(1, line); at++)
        {
            int next = text.IndexOf('\n', start);
            if (next < 0)
            {
                break;
            }
            start = next + 1;
        }
        int end = text.IndexOf('\n', start) is var n and >= 0 ? n : text.Length;
        string row = text[start..end];
        int open = message.IndexOf('\'');
        int close = open >= 0 ? message.IndexOf('\'', open + 1) : -1;
        if (close > open + 1)
        {
            string quoted = message[(open + 1)..close];
            for (int at = row.IndexOf(quoted, StringComparison.Ordinal); at >= 0; at = row.IndexOf(quoted, at + 1, StringComparison.Ordinal))
            {
                bool before = at == 0 || !(char.IsLetterOrDigit(row[at - 1]) || row[at - 1] == '_');
                bool after = at + quoted.Length >= row.Length || !(char.IsLetterOrDigit(row[at + quoted.Length]) || row[at + quoted.Length] == '_');
                if (before && after)
                {
                    return (start + at, start + at + quoted.Length);
                }
            }
        }
        string trimmed = row.TrimEnd();
        int first = trimmed.Length - trimmed.TrimStart().Length;
        return trimmed.Length == first ? (start, start) : (start + first, start + trimmed.Length);
    }
}
