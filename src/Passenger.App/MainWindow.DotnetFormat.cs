using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>dotnet format, as Settings' C# formatter may ask for it: the folder's own SDK run over the C# files laid out,
/// which it writes on disk for the editors to take back.</summary>
public partial class MainWindow
{
    /// <summary>The most characters of files a run of dotnet format names on its command line; more go to another run.</summary>
    private const int DotnetFormatNames = 16_000;

    /// <summary>dotnet format over C# files of the folder (canonical paths) on the folder's own SDK, for a trusted folder
    /// alone: the project holding them all named, else the solution holding them, else each project with its own, the files
    /// on --include from the open folder; <paramref name="whitespace"/> for its layout alone, else all it fixes. Null once
    /// it ran, else why not.</summary>
    private async Task<string?> DotnetFormatAsync(IReadOnlyList<string> files, bool whitespace, CancellationToken cancel)
    {
        if (session.Folder is not { } folder)
        {
            return Refusals.NoFolder.Shown;
        }
        if (!session.Trusted)
        {
            return Refusals.Untrusted.Shown;
        }
        List<(string Workspace, List<string> Files)> runs = [];
        foreach (IGrouping<ProjectModel?, string> owned in files.GroupBy(f => session.ProjectOf(f)))
        {
            if (owned.Key is not { IsProgram: false } project)
            {
                return Refusals.DotnetFormatsProjects(Path.GetFileName(owned.First())).Shown;
            }
            runs.Add((project.Path, [.. owned]));
        }
        // One load of the solution for files of several of its projects, the workspace's projects being the solution's.
        if (runs.Count > 1 && session.Workspace?.Solution is { } solution)
        {
            runs = [(solution.Path, [.. files])];
        }
        foreach ((string workspace, List<string> laid) in runs)
        {
            if (!WorkspaceTrust.Trusts(workspace))
            {
                return Refusals.Untrusted.Shown;
            }
            foreach (IReadOnlyList<string> named in Named(laid))
            {
                string[] arguments = ["format", .. whitespace ? (string[])["whitespace"] : [], workspace, "--no-restore", "--include",
                    .. named.Select(f => Path.GetRelativePath(folder, f))];
                ChildResult ran;
                try
                {
                    ran = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(arguments, folder), cancel: cancel);
                }
                catch (FileNotFoundException)
                {
                    return Refusals.NoDotnet.Shown;
                }
                if (!ran.Succeeded)
                {
                    return Refusals.DotnetFormatFailed(ran.ExitCode, ChildResult.FirstLineOf(ran.Error) is { Length: > 0 } said ? said : ran.FirstLine).Shown;
                }
            }
        }
        return null;

        // The files of a run in turns of at most DotnetFormatNames characters, so no command line runs past what a system takes.
        static IEnumerable<IReadOnlyList<string>> Named(IReadOnlyList<string> laid)
        {
            List<string> turn = [];
            int length = 0;
            foreach (string file in laid)
            {
                if (turn.Count > 0 && length + file.Length > DotnetFormatNames)
                {
                    yield return turn;
                    (turn, length) = ([], 0);
                }
                turn.Add(file);
                length += file.Length + 1;
            }
            if (turn.Count > 0)
            {
                yield return turn;
            }
        }
    }
}
