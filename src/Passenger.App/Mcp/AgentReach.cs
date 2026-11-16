using Passenger.App.CSharp;
using Passenger.App.MSBuild;
using Passenger.App.Packages;
using Passenger.App.Web;
using Passenger.App.Xaml;
using Passenger.Core;

namespace Passenger.App.Mcp;

/// <summary>What of the disk an agent's tools reach, every path a tool takes asked here: the folder open in the window and
/// the folders its trusted solution reaches beside it (<see cref="ProjectSession.Reached"/>), to read and change; and only
/// to read, what Go to Declaration leads to outside them, each read-only in the editor: a package's file in a folder an
/// evaluation names, an MSBuild file the .NET SDK installed, and the copies made to be read (a source generator's code, a
/// library's code the C# language server or Passenger decompiled or took from its symbols, a stylesheet a page links). A
/// path is canonical (<see cref="PathNames.Canonical"/>), so one through a link out of the folder is where it leads.</summary>
public static class AgentReach
{
    /// <summary>Where the C# language server writes what Go to Declaration of a library's symbol opens (its metadata as code,
    /// decompiled, or its source from its PDB or Source Link): the temporary folder it takes from Passenger's environment.</summary>
    public static string ServerStubs => PathNames.Canonical(Path.Combine(Path.GetTempPath(), "MetadataAsSource"));

    /// <summary>Whether an agent may change a file or folder (a canonical path): only the open folder's, and those of the
    /// folders its trusted solution reaches (<paramref name="reached"/>).</summary>
    public static bool Changes(string path, string? folder, IReadOnlyList<string> reached) =>
        folder is not null && PathNames.Within(path, folder) || reached.Any(r => PathNames.Within(path, r));

    /// <summary>Whether an agent may read a file or folder (a canonical path): those it may change, and the files outside
    /// them made or installed to be read.</summary>
    public static bool Reads(string path, string? folder, IReadOnlyList<string> reached) => Changes(path, folder, reached) || PackageFolders.Holds(path) || MSBuildFiles.IsInstalled(path)
        || GeneratedCode.IsCopy(path) || RemoteStylesheets.IsCopy(path) || PathNames.IsUnder(path, PathNames.Canonical(LibrarySources.Folder))
        || PathNames.IsUnder(path, ServerStubs);
}
