using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Host;
using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.App.Packages;

public sealed record PackageUpdate(string Id, string Version, IReadOnlyList<string> Projects);

public sealed record PackageUpdateResult(int Updated, IReadOnlyList<string> Skipped, int Remaining, string? Failure)
{
    public string Message => $"Updated {Plural.Of(Updated, "package reference")}" + (Skipped.Count > 0 ? $"; skipped: {string.Join("; ", Skipped)}" : string.Empty)
        + (Failure is not null ? $"; {Failure}; {Remaining} remaining" : string.Empty);
}

public sealed partial class FolderPackages
{
    private CancellationTokenSource? bulkStopping;
    private CommandHistory.Recording? bulkRecording;
    private PackageOutcome? bulkLastChange;
    private readonly Dictionary<string, byte[]?> bulkSnapshots = new(PathNames.Comparer);

    /// <summary>Fresh evaluations use the same solution properties as the session's project model.</summary>
    public Func<ProjectModel, IReadOnlyDictionary<string, string>>? EvaluationProperties { get; set; }

    public async Task<PackageUpdateResult> UpdateAllAsync(IReadOnlyList<PackageUpdate> updates,
        Func<ProjectModel, string, string, CommandHistory.Recording?, CancellationToken, Task<bool>>? before)
    {
        if (IsBusy || Commands is null)
        {
            return new PackageUpdateResult(0, [], updates.Sum(u => u.Projects.Count), "A package change is under way or command history is unavailable");
        }
        IsBusy = true;
        changing = "Updating all NuGet packages…";
        using IDisposable held = BackgroundWork.Scope();
        using Working? working = Work?.Begin(AppActions.NuGetUpdateAll, changing);
        using CancellationTokenSource stopping = working is null ? new() : CancellationTokenSource.CreateLinkedTokenSource(working.Stopping);
        bulkStopping = stopping;
        using CancellationTokenRegistration stop = stopping.Token.Register(manager.Stop);
        int updated = 0;
        int remaining = updates.Sum(u => u.Projects.Count);
        List<string> skipped = [];
        string? failure = null;
        List<string> written = [];
        ChangeBegun?.Invoke(this, changing);
        try
        {
            if (SaveFirst is { } save && await save(working, stopping.Token) is { } unsaved)
            {
                failure = unsaved;
            }
            else
            {
                using CommandHistory.Recording recording = Commands.RecordWrites("Update All NuGet Packages");
                bulkRecording = recording;
                string[] paths = [.. updates.SelectMany(u => u.Projects).Distinct(PathNames.Comparer).OrderBy(p => p, PathNames.Comparer)];
                foreach (string path in paths)
                {
                    stopping.Token.ThrowIfCancellationRequested();
                    ProjectModel? project = Projects.FirstOrDefault(p => PathNames.Same(p.Path, path) && p.Exists);
                    PackageUpdate[] asked = [.. updates.Where(u => u.Projects.Contains(path, PathNames.Comparer))];
                    if (project is null)
                    {
                        skipped.Add($"{Path.GetFileName(path)}: project no longer exists");
                        remaining -= asked.Length;
                        continue;
                    }
                    working?.Say($"Updating NuGet packages in {project.Name}…");
                    if (SaveFirst is { } saveProject && await saveProject(working, stopping.Token) is { } unsavedProject)
                    {
                        failure = $"{project.Name}: {unsavedProject}";
                        break;
                    }
                    stopping.Token.ThrowIfCancellationRequested();
                    if (ChangedBulkFile() is { } changed)
                    {
                        failure = changed;
                        break;
                    }
                    project = await FreshAsync(project, stopping.Token);
                    List<(string Id, string Version)> targets = [];
                    foreach (PackageUpdate target in asked)
                    {
                        InstalledVersion[] at = await VersionsInAsync(project, target.Id);
                        if (at.Length == 0 || at.Any(v => v.Version is null) || InstalledVersion.Lowered(target.Version, at).Count > 0)
                        {
                            skipped.Add($"{project.Name}/{target.Id}: removed, unknown version, or would downgrade a framework");
                            remaining--;
                            continue;
                        }
                        if (InstalledVersion.Move(target.Version, at) == PackageMove.None)
                        {
                            remaining--;
                            continue;
                        }
                        bulkLastChange = null;
                        if (before is not null && !await before(project, target.Id, target.Version, recording, stopping.Token))
                        {
                            if (bulkLastChange is { Done: false } failed)
                            {
                                failure = stopping.IsCancellationRequested ? $"Stopped in {project.Name}" : $"{project.Name}: {failed.Failure("Update failed")}";
                                break;
                            }
                            if (bulkLastChange is { Done: true })
                            {
                                updated++;
                            }
                            skipped.Add($"{project.Name}/{target.Id}: handled or declined by its update check");
                            remaining--;
                            project = await FreshAsync(project, stopping.Token);
                            continue;
                        }
                        targets.Add((target.Id, target.Version));
                    }
                    if (failure is not null)
                    {
                        break;
                    }
                    stopping.Token.ThrowIfCancellationRequested();
                    // A provider check may itself have updated a set, so compare again before writing this project's batch.
                    project = await FreshAsync(project, stopping.Token);
                    List<(string Id, string Version)> eligible = [];
                    foreach ((string id, string version) in targets)
                    {
                        InstalledVersion[] at = await VersionsInAsync(project, id);
                        if (at.Length > 0 && at.All(v => v.Version is not null) && InstalledVersion.Move(version, at) == PackageMove.Update)
                        {
                            eligible.Add((id, version));
                        }
                        else
                        {
                            skipped.Add($"{project.Name}/{id}: already handled or no longer eligible");
                            remaining--;
                        }
                    }
                    if (eligible.Count == 0)
                    {
                        continue;
                    }
                    PackageOutcome outcome = await InstallSetAsync(project, eligible, recording);
                    written.AddRange(outcome.Written);
                    if (!outcome.Done)
                    {
                        failure = stopping.IsCancellationRequested ? $"Stopped in {project.Name}" : $"{project.Name}: {outcome.Failure("Update failed")}";
                        break;
                    }
                    updated += eligible.Count;
                    remaining -= eligible.Count;
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            failure = "Stopped";
        }
        catch (ProjectHostFailed failed)
        {
            failure = $"Could not recheck project references: {failed.Reason}";
        }
        finally
        {
            bulkRecording = null;
            bulkLastChange = null;
            bulkSnapshots.Clear();
            bulkStopping = null;
            IsBusy = false;
            changing = null;
        }
        PackageUpdateResult result = new(updated, skipped, remaining, failure);
        foreach (string line in skipped)
        {
            Line?.Invoke(this, line);
        }
        Line?.Invoke(this, result.Message);
        working?.Tell(failure is null, result.Message);
        Changed?.Invoke(this, new PackageOutcome(failure is null, [.. written.Distinct(PathNames.Comparer)], [result.Message]) { Name = "Update All NuGet Packages" });
        return result;
    }

    private string? ChangedBulkFile() => bulkSnapshots.FirstOrDefault(file => !Same(Bytes(file.Key), file.Value)).Key is { } changed
        ? $"{Path.GetFileName(changed)} changed during Update All; stopped to preserve that edit" : null;

    private async Task<ProjectModel> FreshAsync(ProjectModel project, CancellationToken cancel)
    {
        ProjectHost host = await ProjectHosts.ForAsync(project.Folder, cancel);
        EvaluatedProject? latest = null;
        IReadOnlyDictionary<string, string> properties = EvaluationProperties?.Invoke(project) ?? ProjectModels.Properties(null, project);
        await BackgroundWork.Run(() => host.EvaluateAsync([project.Path], properties,
            answer => latest = answer, cancel), cancel);
        if (latest is not { Complete: true, Problem: null })
        {
            throw new ProjectHostFailed(latest?.Problem ?? "The project could not be evaluated completely");
        }
        return ProjectModel.From(project.Path, latest);
    }

    private static async Task<InstalledVersion[]> VersionsInAsync(ProjectModel project, string id)
    {
        RestoredVersions restored = await BackgroundWork.Run(() => Restored(project));
        return [.. References(project).Where(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Version.IsRanged && restored.Of(r.Version.Framework, id) is { } resolved && r.Version.Range!.Contains(resolved)
                ? r.Version with { Resolved = resolved } : r.Version)];
    }
}
