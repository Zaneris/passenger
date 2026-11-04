namespace Passenger.Workspace.GodotShader;

/// <summary>Godot's String path methods as core/string/ustring.cpp writes them (MIT): simplify_path, is_absolute_path,
/// get_base_dir and path_join, by which its preprocessor names the file an include asks for. Pure.</summary>
public static class GodotPathStrings
{
    /// <summary>The path with its slashes made '/', '//' made one, '.' dropped and each '..' taking the name before it
    /// (one at the start kept), after its drive or <c>res://</c>.</summary>
    public static string SimplifyPath(string path)
    {
        string s = path;
        string drive = string.Empty;
        int p = s.IndexOf("://", StringComparison.Ordinal);
        bool found = false;
        if (p > 0 && s[..p].All(char.IsAsciiLetterOrDigit))
        {
            found = true;
            drive = s[..(p + 3)];
            s = s[(p + 3)..];
        }
        if (!found)
        {
            if (s.StartsWith("//", StringComparison.Ordinal) || s.StartsWith(@"\\", StringComparison.Ordinal))
            {
                drive = s[..2];
                s = s[2..];
            }
            else if (s.StartsWith('/') || s.StartsWith('\\'))
            {
                drive = s[..1];
                s = s[1..];
            }
            else
            {
                p = s.IndexOf(":/", StringComparison.Ordinal);
                if (p == -1)
                {
                    p = s.IndexOf(@":\", StringComparison.Ordinal);
                }
                int slash = s.IndexOf('/');
                if (p != -1 && p < slash)
                {
                    drive = s[..(p + 2)];
                    s = s[(p + 2)..];
                }
            }
        }
        s = s.Replace('\\', '/');
        while (s.Contains("//", StringComparison.Ordinal))
        {
            s = s.Replace("//", "/", StringComparison.Ordinal);
        }
        List<string> dirs = [.. s.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        for (int i = 0; i < dirs.Count; i++)
        {
            if (dirs[i] == ".")
            {
                dirs.RemoveAt(i);
                i--;
            }
            else if (dirs[i] == ".." && i != 0)
            {
                dirs.RemoveAt(i);
                dirs.RemoveAt(i - 1);
                i -= 2;
            }
        }
        return drive + string.Join('/', dirs);
    }

    public static bool IsAbsolutePath(string path) => path.Length > 1
        ? path[0] is '/' or '\\' || path.Contains(":/", StringComparison.Ordinal) || path.Contains(@":\", StringComparison.Ordinal)
        : path.Length == 1 && path[0] is '/' or '\\';

    /// <summary>The folder a path is in: <c>res://shaders</c> of <c>res://shaders/a.gdshader</c>, <c>res://</c> of
    /// <c>res://a.gdshader</c> (and of a scene's own <c>res://level.tscn::Shader_x</c>).</summary>
    public static string GetBaseDir(string path)
    {
        int end = 0;
        int basepos = path.IndexOf("://", StringComparison.Ordinal);
        if (basepos != -1)
        {
            end = basepos + 3;
        }
        if (end == 0)
        {
            basepos = path.IndexOf(":/", StringComparison.Ordinal);
            if (basepos == -1)
            {
                basepos = path.IndexOf(@":\", StringComparison.Ordinal);
            }
            if (basepos != -1)
            {
                end = basepos + 2;
            }
        }
        if (end == 0 && path.StartsWith('/'))
        {
            end = 1;
        }
        string rest = end != 0 ? path[end..] : path;
        string start = end != 0 ? path[..end] : string.Empty;
        int separator = Math.Max(rest.LastIndexOf('/'), rest.LastIndexOf('\\'));
        return separator == -1 ? start : start + rest[..separator];
    }

    public static string PathJoin(string folder, string file) =>
        folder.Length == 0 ? file : folder[^1] == '/' || file.StartsWith('/') ? folder + file : $"{folder}/{file}";
}
