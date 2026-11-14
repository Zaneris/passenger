using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>dotnet format on the solution, a project or a folder chosen in the Explorer: saves all open files first
/// (format rewrites what is on disk), runs dotnet format, and says whether it found anything to fix.</summary>
public partial class MainWindow
{
    /// <summary>dotnet format on the solution, project or folder the person chose: files saved, format run, outcome
    /// said. A folder holds its own .sln or project, or runs within its owning project or solution.</summary>
    private async Task DotnetFormatAsync(ExplorerNode row)
    {
        UiThread.Check();
        if (row.Path is null || work.Begin(AppActions.DotnetFormat, "Running dotnet format") is not { } formatting)
        {
            return;
        }
        using (formatting)
        {
            if (await Editors.SaveAllLandedAsync(formatting, formatting.Stopping) is { } unsaved)
            {
                Told(formatting, false, unsaved);
                return;
            }
            (IReadOnlyList<string> arguments, string? folder) = FormatCommandFor(row);
            ChildResult result;
            try
            {
                result = await BackgroundWork.Run(() =>
                    ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(arguments, folder), cancel: formatting.Stopping));
            }
            catch (OperationCanceledException) when (formatting.IsStopped)
            {
                Told(formatting, false, "dotnet format was stopped");
                return;
            }
            string said = result.Succeeded
                ? $"dotnet format finished on {row.Name}"
                : $"dotnet format failed on {row.Name}: {result.Said}";
            Told(formatting, result.Succeeded, said);
        }
    }

    /// <summary>The dotnet format command arguments and working folder for an Explorer row.</summary>
    private (IReadOnlyList<string> Arguments, string? Folder) FormatCommandFor(ExplorerNode row)
    {
        if (row.Kind == NodeKind.Folder && row.Path is { } folderPath)
        {
            if (ProjectModel.Owning(session.Projects, folderPath) is { } project)
            {
                return (["format", project.Path, "--include", Path.GetRelativePath(project.Folder, folderPath)], project.Folder);
            }
            if (Solution is { } solution)
            {
                string slnFolder = Path.GetDirectoryName(solution.Path)!;
                return (["format", solution.Path, "--include", Path.GetRelativePath(slnFolder, folderPath)], slnFolder);
            }
            return (["format", folderPath], folderPath);
        }
        string target = row.Path!;
        string? working = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
        return (["format", target], working);
    }
}
