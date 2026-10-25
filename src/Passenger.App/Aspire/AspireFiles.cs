using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

/// <summary>What a C# file is to Aspire's code help and run marks: nothing, which they cost nothing; a service's, a file of
/// a project an AppHost references; or an AppHost's, a file of an AppHost project or a single-file AppHost.</summary>
public enum AspireRole
{
    None,
    Service,
    AppHost,
}

/// <summary>The folder's AppHosts and what each C# file is to them, for one session (<see cref="ProjectSession.AspireFiles"/>).
/// Single-file AppHosts are found among the files of the folder's one walk (<see cref="FileIndex.Walked"/>): a C# file's
/// head is read as a walk first lists it, and again only as the folder watcher says it changed. A file's role comes from the
/// projects as MSBuild evaluated them, kept until the run configurations are made again (an evaluation landed, a project
/// unloaded, a single-file AppHost found or gone), so asking it as a key is typed costs a lookup.</summary>
public sealed class AspireFiles(ProjectSession session)
{
    /// <summary>The most single-file AppHosts a folder offers, so a folder of generated ones cannot flood the configurations.</summary>
    private const int AppHostLimit = 200;

    /// <summary>Each C# file a walk listed or the watcher named, and whether it is a single-file AppHost: touched only by the
    /// reads, which run one at a time on a worker.</summary>
    private readonly Dictionary<string, bool> read = new(PathNames.Comparer);

    /// <summary>Whether the next walk reads every C# file it lists again, the watcher having lost changes; the reads' alone too.</summary>
    private bool again;

    private readonly TaskCompletionSource firstWalk = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task reading = Task.CompletedTask;

    private IReadOnlyList<RunConfiguration>? rolesOf;
    private readonly Dictionary<string, (AspireRole Role, string? AppHost)> roles = new(PathNames.Comparer);
    private HashSet<string> hosts = new(PathNames.Comparer);
    private HashSet<string> services = new(PathNames.Comparer);
    private HashSet<string> singles = new(PathNames.Comparer);

    /// <summary>Done once the folder's first walk has been read for its single-file AppHosts, and after it each later read:
    /// what an agent's open waits for before it says what the workspace runs.</summary>
    public Task Read => firstWalk.Task.IsCompleted ? reading : firstWalk.Task;

    /// <summary>A walk of the folder landed: the C# files it lists that were never read are read, those it no longer lists
    /// forgotten, and the run configurations take the single-file AppHosts when they changed.</summary>
    public void Walked(string root, IReadOnlyList<string> files)
    {
        UiThread.Check();
        Then(walk: true, () =>
        {
            HashSet<string> listed = new(PathNames.Comparer);
            foreach (string relative in files)
            {
                if (IsSource(root, relative))
                {
                    string path = Path.Join(root, relative);
                    listed.Add(path);
                    if (again || !read.ContainsKey(path))
                    {
                        read[path] = IsAppHost(path) ?? read.GetValueOrDefault(path);
                    }
                }
            }
            again = false;
            foreach (string gone in read.Keys.Where(path => !listed.Contains(path)).ToList())
            {
                read.Remove(gone);
            }
        });
    }

    /// <summary>Files the folder watcher saw change: each C# file among them read again, as it may have gained or lost the
    /// AppHost SDK's line; with <paramref name="every"/> (the system lost changes) every one, as the walk that follows lists it.</summary>
    public void Changed(IEnumerable<string> paths, bool every = false)
    {
        UiThread.Check();
        if (session.Folder is not { } root)
        {
            return;
        }
        string[] sources = [.. paths.Where(path => IsSource(root, Path.GetRelativePath(root, path)))];
        if (sources.Length == 0 && !every)
        {
            return;
        }
        Then(walk: false, () =>
        {
            // Kept until the walk reads each again, so the AppHosts already found stay offered meanwhile.
            again |= every;
            foreach (string path in sources)
            {
                if (IsAppHost(path) is { } appHost)
                {
                    read[path] = appHost;
                }
            }
        });
    }

    /// <summary>The C# file's role, as its project and the folder's AppHosts make it now.</summary>
    public AspireRole RoleOf(string path) => Of(path).Role;

    /// <summary>The AppHost a C# file is a source of, as a run configuration names it: its AppHost project's file, or the
    /// single-file AppHost itself; null for any other file.</summary>
    public string? AppHostOf(string path) => Of(path).AppHost;

    private (AspireRole Role, string? AppHost) Of(string path)
    {
        if (!ReferenceEquals(rolesOf, session.Configurations.All))
        {
            rolesOf = session.Configurations.All;
            roles.Clear();
            ProjectModel[] appHosts = [.. session.Projects.Where(p => p.IsAspireHost)];
            hosts = new(appHosts.Select(p => p.Path), PathNames.Comparer);
            services = new(appHosts.SelectMany(p => p.ProjectReferences), PathNames.Comparer);
            singles = new(session.Configurations.SingleFileAppHosts, PathNames.Comparer);
        }
        if (roles.TryGetValue(path, out (AspireRole, string?) known))
        {
            return known;
        }
        (AspireRole, string?) role = singles.Contains(path) ? (AspireRole.AppHost, path)
            : session.ProjectOf(path) is not { } owner ? (AspireRole.None, (string?)null)
            : hosts.Contains(owner.Path) ? (AspireRole.AppHost, owner.Path)
            : services.Contains(owner.Path) ? (AspireRole.Service, (string?)null)
            : (AspireRole.None, (string?)null);
        roles[path] = role;
        return role;
    }

    /// <summary>Reads after the reads before it, off the UI thread, then gives the configurations what changed.</summary>
    private void Then(bool walk, Action work)
    {
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
                IReadOnlyList<string> found = await BackgroundWork.Run(() =>
                {
                    work();
                    return (IReadOnlyList<string>)[.. read.Where(file => file.Value).Select(file => file.Key).Order(StringComparer.Ordinal).Take(AppHostLimit)];
                });
                if (!session.IsEnded && !found.SequenceEqual(session.Configurations.SingleFileAppHosts, PathNames.Comparer))
                {
                    session.Configurations.ConfigureAppHosts(found);
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

    /// <summary>Whether a file, by its path from the folder, is a C# file a single-file AppHost may be: not under a hidden
    /// folder or one every walk passes over.</summary>
    private static bool IsSource(string root, string relative)
    {
        if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative))
        {
            return false;
        }
        string[] names = relative.Split(Path.DirectorySeparatorChar);
        return names[..^1].All(name => !name.StartsWith('.')) && !IgnoredFolders.AnyOf(root, names.AsSpan(..^1));
    }

    /// <summary>Whether a file is a single-file AppHost; null when it could not be read, which keeps what was read before.</summary>
    private static bool? IsAppHost(string path)
    {
        try
        {
            return File.Exists(path) && AspireAppHosts.IsSingleFile(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
