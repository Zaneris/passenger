using System.Collections.Concurrent;
using Passenger.App.CSharp;
using Passenger.App.MSBuild;
using Passenger.App.Packages;
using Passenger.App.Web;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Mcp;

/// <summary>What of the disk an agent's tools reach, every path a tool takes asked here: the folder open in the window and
/// the folders its trusted solution and AppHosts reach beside it (<see cref="ProjectSession.Reached"/>), to read and change; and only
/// to read, what Go to Declaration leads to outside them, each read-only in the editor: a package's file in a folder an
/// evaluation names, an MSBuild file the .NET SDK installed, and the copies made to be read (a source generator's code, a
/// library's code the C# language server or Passenger decompiled or took from its symbols, a stylesheet a page links). A
/// path is canonical (<see cref="PathNames.Canonical"/>), so one through a link out of the folder is where it leads.</summary>
public static class AgentReach
{
    /// <summary>The solutions, filters and C# files (an AppHost's sources among them) Passenger wrote for an agent's call, by
    /// canonical path, until written again outside one (the person's save) or opened by the person: what each names beside
    /// the folder reaches no further than before (<see cref="ProjectSession.Reached"/>).</summary>
    private static readonly ConcurrentDictionary<string, byte> Written = new(PathNames.Comparer);

    static AgentReach() => FileText.Writing += path =>
    {
        if (WorkspaceModel.IsSolution(path) || PathNames.HasExtension(path, [".cs"]))
        {
            string canonical = PathNames.Canonical(path);
            if (AgentCall.Current is not null)
            {
                Written[canonical] = 0;
            }
            else
            {
                Written.TryRemove(canonical, out _);
            }
        }
    };

    /// <summary>Whether an agent's call wrote a solution, filter or C# file (a canonical path) since the person last wrote or
    /// opened it.</summary>
    public static bool WroteFor(string path) => Written.ContainsKey(path);

    /// <summary>A solution or filter the person opened (a canonical path): what it names is theirs to reach from now on, as
    /// it would be in a Passenger started anew.</summary>
    public static void Opened(string path) => Written.TryRemove(path, out _);

    /// <summary>Where the C# language server writes what Go to Declaration of a library's symbol opens (its metadata as code,
    /// decompiled, or its source from its PDB or Source Link), in the temporary folder it takes (<see
    /// cref="RoslynServerFolder.Temporary"/>, else Passenger's own), made the person's own (<see cref="UserTemporary.Own"/>);
    /// null where it is not theirs (a link, another user's folder on a shared /tmp), which no agent then reads in.</summary>
    public static string? ServerStubs => Stubs.Value;

    private static readonly Lazy<string?> Stubs = new(() =>
    {
        try
        {
            string temporary = RoslynServerFolder.Temporary(CSharpService.ServerCache) ?? Path.GetTempPath();
            return PathNames.Canonical(UserTemporary.Own(Path.Combine(temporary, "MetadataAsSource")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    });

    /// <summary>Whether an agent may change a file or folder (a canonical path): only the open folder's, and those of the
    /// folders its trusted solution and AppHosts reach (<paramref name="reached"/>).</summary>
    public static bool Changes(string path, string? folder, IReadOnlyList<string> reached) =>
        folder is not null && PathNames.Within(path, folder) || reached.Any(r => PathNames.Within(path, r));

    /// <summary>Whether an agent may read a file or folder (a canonical path): those it may change, and the files outside
    /// them made or installed to be read.</summary>
    public static bool Reads(string path, string? folder, IReadOnlyList<string> reached) => Changes(path, folder, reached) || PackageFolders.Holds(path) || MSBuildFiles.IsInstalled(path)
        || GeneratedCode.IsCopy(path) || RemoteStylesheets.IsCopy(path) || PathNames.IsUnder(path, PathNames.Canonical(LibrarySources.Folder))
        || ServerStubs is { } stubs && PathNames.IsUnder(path, stubs);
}
