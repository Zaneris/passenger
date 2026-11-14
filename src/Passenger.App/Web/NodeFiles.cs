using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>The open folder's Node packages (<see cref="NodeProjects"/>): its package.json files found among those the
/// index's walk lists, never by a walk of their own, read again off the window's thread as one, a lockfile or pnpm's
/// workspace file comes, goes or is written; the run configurations take their scripts. A UI-thread object of its
/// <see cref="ProjectSession"/>.</summary>
public sealed class NodeFiles(ProjectSession session)
{
    /// <summary>The most package.json files read, so a folder of thousands of fixtures cannot flood the configurations.</summary>
    private const int ManifestLimit = 500;

    /// <summary>The package.json files the index lists, outside hidden folders, by their full paths; touched only by the reads.</summary>
    private readonly SortedSet<string> manifests = new(StringComparer.Ordinal);

    private readonly TaskCompletionSource firstWalk = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task reading = Task.CompletedTask;

    /// <summary>The packages as last read, in their manifests' order.</summary>
    public IReadOnlyList<NodePackage> Packages { get; private set; } = [];

    /// <summary>The packages read changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Done once the folder's first walk has been read for its packages, and after it each later read.</summary>
    public Task Read => firstWalk.Task.IsCompleted ? reading : firstWalk.Task;

    /// <summary>A walk of the folder landed: its package.json files are those it lists, picked out off the window's thread.</summary>
    public void Walked(string root, IReadOnlyList<string> files)
    {
        UiThread.Check();
        ReadAgain(walk: true, () =>
        {
            manifests.Clear();
            foreach (string relative in files)
            {
                if (IsManifest(relative))
                {
                    manifests.Add(Path.Join(root, relative));
                }
            }
        });
    }

    /// <summary>Files the index put in and took out after a walk: the packages are read again where one concerns them.</summary>
    public void CameAndWent(string root, IReadOnlyList<string> came, IReadOnlyList<string> gone)
    {
        UiThread.Check();
        if (!came.Concat(gone).Any(NodeProjects.Concerns))
        {
            return;
        }
        ReadAgain(walk: false, () =>
        {
            foreach (string relative in gone)
            {
                manifests.Remove(Path.Join(root, relative));
            }
            foreach (string relative in came.Where(IsManifest))
            {
                manifests.Add(Path.Join(root, relative));
            }
        });
    }

    /// <summary>Files the folder watcher saw written: the packages are read again where one concerns them, or every change
    /// was lost.</summary>
    public void FilesChanged(IEnumerable<string> paths, bool every)
    {
        UiThread.Check();
        if (every || paths.Any(NodeProjects.Concerns))
        {
            ReadAgain(walk: false, null);
        }
    }

    private static bool IsManifest(string relative) =>
        Path.GetFileName(relative) == NodeProjects.ManifestName && !IgnoredFolders.AnyHidden(Path.GetDirectoryName(relative.AsSpan()));

    /// <summary>Reads the packages after the reads before it, off the window's thread, the files listed changed first as
    /// <paramref name="change"/> says (the reads alone touch them, one at a time), and tells what changed.</summary>
    private void ReadAgain(bool walk, Action? change)
    {
        if (session.Folder is not { } top)
        {
            return;
        }
        Task previous = reading;
        reading = ReadAsync();

        async Task ReadAsync()
        {
            try
            {
                await previous;
                if (session.IsEnded)
                {
                    return;
                }
                IReadOnlyList<NodePackage> found = await BackgroundWork.Run(() =>
                {
                    change?.Invoke();
                    return NodeProjects.Read(top, [.. manifests.Take(ManifestLimit)]);
                });
                if (!session.IsEnded && !Same(found, Packages))
                {
                    Packages = found;
                    session.Configurations.ConfigurePackages(found);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception error)
            {
                IdeErrors.Report(error);
            }
            finally
            {
                if (walk)
                {
                    firstWalk.TrySetResult();
                }
            }
        }
    }

    /// <summary>Whether two reads found the same packages, each as its files say it.</summary>
    private static bool Same(IReadOnlyList<NodePackage> one, IReadOnlyList<NodePackage> other) =>
        one.Select(Said).SequenceEqual(other.Select(Said), StringComparer.Ordinal);

    private static string Said(NodePackage package) =>
        $"{package.Manifest}\n{package.Name}\n{package.Manager}\n{package.ManagerSource}\n{package.Engines}\n{package.Root}\n"
        + string.Join('\n', package.Scripts.Select(s => $"{s.Name}\0{s.Command}"));
}
