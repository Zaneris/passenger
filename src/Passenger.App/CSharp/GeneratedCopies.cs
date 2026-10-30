using System.Security.Cryptography;
using System.Text;
using Passenger.Core;

namespace Passenger.App.CSharp;

/// <summary>Where a copy of a source generator's code goes, and which project's code a copy is, from their names alone.</summary>
internal static class GeneratedCopies
{
    /// <summary>Where a generator's document is copied: under <paramref name="folder"/>, in a folder named after its project
    /// and told apart by the project's file and name and the generator (each target framework of a project its own), at its
    /// hint name, each of whose names is one a file system takes.</summary>
    public static string PlaceOf(string folder, string projectPath, string projectName, string generator, string hint)
    {
        string project = projectPath.Length > 0 ? Path.GetFileNameWithoutExtension(projectPath) : projectName;
        string told = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{projectPath}\n{projectName}\n{generator}")))[..12];
        string[] names = [.. hint.Split('/', '\\').Where(n => n.Length > 0).Select(Named)];
        return Path.Combine([folder, $"{Named(project)}-{told}", .. names.Length > 0 ? names : ["generated.cs"]]);
    }

    /// <summary>The name of the project a copy under <paramref name="folder"/> is of, as its folder names it; null for a
    /// path that is no copy.</summary>
    public static string? ProjectNameOf(string folder, string path)
    {
        if (!PathNames.IsUnder(path, folder))
        {
            return null;
        }
        string first = Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        int told = first.LastIndexOf('-');
        return told > 0 ? first[..told] : null;
    }

    /// <summary>A name made one every file system takes: each character a file name may not hold on one made '_', and "."
    /// and ".." too.</summary>
    private static string Named(string name)
    {
        string made = string.Concat(name.Select(c => c < ' ' || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c));
        return made is "" or "." or ".." ? "_" : made;
    }
}
