using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>Where JavaScript a .NET app calls through JS interop is declared: the function, class or method an identifier
/// names ("app.greet" names greet) in a script's text, outside its comments; the files a module's path names under the
/// projects that serve it; and the scripts a call with no module may reach, a component's collocated script first.</summary>
public static class JsFunctions
{
    /// <summary>The extensions of the scripts a page loads.</summary>
    private static readonly string[] Scripts = [".js", ".mjs", ".cjs"];

    /// <summary>The name an identifier calls: its last part ("greet" of "app.greet", "Map" of "new.Map").</summary>
    public static string NameOf(string identifier)
    {
        int dot = identifier.LastIndexOf('.');
        return dot < 0 ? identifier : identifier[(dot + 1)..];
    }

    /// <summary>Where a script declares what an identifier names, each the stretch of its name: a function declaration, a class,
    /// a function or arrow function assigned to it or given as an object's member, and a method; none inside a comment.</summary>
    public static IReadOnlyList<(int Start, int Length)> Declared(string script, string identifier)
    {
        string name = NameOf(identifier);
        if (name.Length == 0 || !script.Contains(name, StringComparison.Ordinal))
        {
            return [];
        }
        bool[] comment = Comments(script);
        string escaped = Regex.Escape(name);
        string function = @"(?:async\b\s*)?(?:function\b|\([^()]*\)\s*=>|[A-Za-z_$][\w$]*\s*=>)";
        Regex declared = new(
            $@"\bfunction\b\s*\*?\s*(?<n>{escaped})\s*\("
            + $@"|\bclass\s+(?<n>{escaped})(?![\w$])"
            + $@"|(?<![\w$])(?<n>{escaped})\s*=(?![=>])\s*{function}"
            + $@"|(?<![\w$.])(?<q>['""]?)(?<n>{escaped})\k<q>\s*:\s*{function}"
            + $@"|^[ \t]*(?:static\s+)?(?:async\s+)?\*?\s*(?<n>{escaped})\s*\([^()]*\)\s*\{{",
            RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        List<(int, int)> found = [];
        foreach (Match match in declared.Matches(script))
        {
            Group at = match.Groups["n"];
            if (!comment[at.Index] && !found.Contains((at.Index, at.Length)))
            {
                found.Add((at.Index, at.Length));
            }
        }
        return found;
    }

    /// <summary>Which characters of a script lie in a comment, its strings read so a "//" in one starts none.</summary>
    private static bool[] Comments(string script)
    {
        bool[] inside = new bool[script.Length + 1];
        int i = 0;
        while (i < script.Length)
        {
            char c = script[i];
            if (c is '"' or '\'' or '`')
            {
                i++;
                while (i < script.Length && script[i] != c && (c == '`' || script[i] != '\n'))
                {
                    i += script[i] == '\\' ? 2 : 1;
                }
                i++;
            }
            else if (c == '/' && i + 1 < script.Length && script[i + 1] is '/' or '*')
            {
                int end = script[i + 1] == '/' ? script.IndexOf('\n', i) : script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? script.Length : script[i + 1] == '/' ? end : end + 2;
                Array.Fill(inside, true, i, end - i);
                i = end;
            }
            else
            {
                i++;
            }
        }
        return inside;
    }

    /// <summary>The files a module's path ("./Components/Pages/Map.razor.js", "/js/app.js", "_content/Lib/lib.js") may name,
    /// by the projects that serve it (their project files, the caller's first): each project's web root and its own folder (a
    /// collocated script's place), and under <c>_content/Name/</c> the web root of the project of that name. A path to another
    /// host names none.</summary>
    public static IReadOnlyList<string> ModuleFiles(string module, IReadOnlyList<string> projects)
    {
        string path = module.Split('?', '#')[0].Replace('\\', '/');
        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return [];
        }
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        path = path.TrimStart('/');
        List<string> files = [];
        const string Content = "_content/";
        if (path.StartsWith(Content, StringComparison.Ordinal) && path.IndexOf('/', Content.Length) is int slash and > 0)
        {
            string name = path[Content.Length..slash];
            string rest = path[(slash + 1)..];
            foreach (string project in projects.Where(p => Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                files.Add(Path.Combine(Path.GetDirectoryName(project)!, "wwwroot", Local(rest)));
            }
            return files;
        }
        foreach (string project in projects)
        {
            string folder = Path.GetDirectoryName(project)!;
            files.Add(Path.Combine(folder, "wwwroot", Local(path)));
            files.Add(Path.Combine(folder, Local(path)));
        }
        return [.. files.Distinct(PathNames.Comparer)];
    }

    /// <summary>A path of a URL's in this system's separators.</summary>
    private static string Local(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>A file's collocated script, which a component's or page's calls reach first: Map.razor.js beside Map.razor
    /// or its code-behind Map.razor.cs; null for a file of another kind.</summary>
    public static string? CollocatedOf(string path)
    {
        string markup = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
        return RazorFiles.Is(markup) ? markup + ".js" : null;
    }

    /// <summary>Whether a file is a script a page may load: its extension one of a script's, and not a minified copy, whose
    /// source beside it is what is read.</summary>
    public static bool IsScript(string path) =>
        PathNames.HasExtension(path, Scripts) && !Path.GetFileNameWithoutExtension(path).EndsWith(".min", StringComparison.OrdinalIgnoreCase);
}
