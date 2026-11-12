using Passenger.Core;

namespace Passenger.App.Debugging;

/// <summary>The open folder's files, for a program built elsewhere whose PDB names its sources as that build saw them (an
/// image's <c>/src/Api/Program.cs</c>, a deterministic build's <c>/_/Api/Program.cs</c>), which no path map covers: a
/// document is the folder's file a breakpoint in it binds to, by the debugger's own match (<see cref="SourcePaths.FileOf"/>),
/// so a stop shows where its breakpoint was set. Taken from the folder's index as it stood, its files by name read once;
/// immutable and thread-safe.</summary>
public sealed class WorkspaceSources
{
    private readonly ILookup<string, string> byName;

    /// <summary>The files of a folder, by their paths from it (as <see cref="Workspace.FileIndex.Files"/> gives them).</summary>
    public WorkspaceSources(string folder, IEnumerable<string> files) =>
        byName = files.Select(file => Path.Combine(folder, file)).ToLookup(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder's file a document names, or null for one this disk has as it is, or one no file of the folder is.</summary>
    public string? FileOf(string document)
    {
        string name = Path.GetFileName(document.Replace('\\', '/'));
        return name.Length == 0 || !byName.Contains(name) || File.Exists(document) ? null : SourcePaths.FileOf(document, byName[name]);
    }
}
