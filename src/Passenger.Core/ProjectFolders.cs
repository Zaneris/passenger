namespace Passenger.Core;

/// <summary>Which folders under a project's are its own: those its SDK's wildcards enter, passing over its build output
/// (bin, obj), its dependencies (node_modules) and hidden folders, and of those, for what the project itself holds, none
/// holding a project of its own, loaded or not. Each name is read as its folder reads names (<see cref="FolderNames"/>), as
/// the build writes into an Obj where case is ignored. Framework-free, since the project host compiles it in.</summary>
public static class ProjectFolders
{
    /// <summary>The names of a project's build output and dependencies, passed over wherever they are under it.</summary>
    public static FolderNames Passed { get; } = new(["bin", "obj", "node_modules"]);

    /// <summary>Whether a project's wildcards enter a folder under its own: not its build output, its dependencies or a
    /// hidden folder.</summary>
    public static bool Enters(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder.AsSpan())) is not ['.', ..] && !Passed.Is(folder);

    /// <summary>Whether a folder under a project's is the project's own: one its wildcards enter (<see cref="Enters"/>)
    /// that holds no project of its own.</summary>
    public static bool IsOwn(string folder) => Enters(folder) && !HoldsProject(folder);

    /// <summary>A project's own folders: its folder and every one under it that is its own (<see cref="IsOwn"/>), through
    /// <see cref="PathNames.FoldersUnder"/>.</summary>
    public static IEnumerable<string> Under(string projectFolder) => PathNames.FoldersUnder(projectFolder, IsOwn);

    /// <summary>Whether a file, there now or not, lies in a project's own folders: under its folder, each folder between
    /// its own (<see cref="IsOwn"/>).</summary>
    public static bool Holds(string projectFolder, string path)
    {
        if (!PathNames.Within(path, projectFolder))
        {
            return false;
        }
        string top = Path.TrimEndingDirectorySeparator(projectFolder);
        for (string? at = Path.GetDirectoryName(path); at is not null && at.Length > top.Length; at = Path.GetDirectoryName(at))
        {
            if (!IsOwn(at))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether a file is the project WPF's markup compilation writes beside a project's own for the length of a
    /// build whose XAML uses the project's own types, and deletes as it ends (Name_random_wpftmp.csproj): never a project
    /// of the folder, nor a name the project's wildcards take in.</summary>
    public static bool IsMarkupProject(ReadOnlySpan<char> path) =>
        PathNames.HasExtension(path, MarkupLanguages) && Path.GetFileNameWithoutExtension(path).EndsWith(MarkupProject, StringComparison.Ordinal);

    /// <summary>The project files of WPF's languages, C# and Visual Basic, whose extension that project takes.</summary>
    private static readonly string[] MarkupLanguages = [".csproj", ".vbproj"];

    /// <summary>The end of the name WPF's markup compilation gives its project, before the extension.</summary>
    private const string MarkupProject = "_wpftmp";

    /// <summary>Whether a folder holds a project file (<c>*.csproj</c>, <c>*.fsproj</c> and every other <c>*.*proj</c>),
    /// which makes it and the folders under it that project's own, whether a solution names it or not; false for one not
    /// there or that cannot be read.</summary>
    public static bool HoldsProject(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.*proj").Any();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
