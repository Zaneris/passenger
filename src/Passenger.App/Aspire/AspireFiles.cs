using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.CSharp;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Roslyn.Extension;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

/// <summary>What a C# file is to Aspire's code help and run marks: nothing, which they cost nothing; a service's, a file of
/// a project an AppHost runs (<see cref="AspireFiles.AppHostsRunning"/>); or an AppHost's, a file of an AppHost project or a
/// single-file AppHost.</summary>
public enum AspireRole
{
    None,
    Service,
    AppHost,
}

/// <summary>The folder's AppHosts and what each C# file is to them, for one session (<see cref="ProjectSession.AspireFiles"/>).
/// Single-file AppHosts, and those of another language an aspire.config.json names, are found among the files of the
/// folder's one walk (<see cref="FileIndex.Walked"/>): a C# file's head, or the config, is read as a walk first lists it, and
/// again only as the folder watcher says it changed; each C# AppHost's sources are
/// read for the services they name by path (<see cref="AspireServices"/>) as it comes and as the watcher says one changed. A
/// file's role comes from the projects as MSBuild evaluated them and those services, kept until the run configurations are
/// made again (an evaluation landed, a project unloaded, a single-file AppHost found or gone) or the services named change,
/// so asking it as a key is typed costs a lookup.</summary>
public sealed class AspireFiles
{
    private readonly ProjectSession session;

    public AspireFiles(ProjectSession session)
    {
        this.session = session;
        session.Configurations.Changed += (_, _) => FollowAppHosts();
        arrivals = new StandingStill(ReadCame);
    }

    /// <summary>The C# files another program brought in, read once their folders have stood still.</summary>
    private readonly StandingStill arrivals;

    /// <summary>The reads of C# files that came put off until let go, for a test of what the folder does meanwhile.</summary>
    public IDisposable HoldArrivals() => arrivals.HoldOpen();

    /// <summary>The most single-file AppHosts a folder offers, so a folder of generated ones cannot flood the configurations.</summary>
    private const int AppHostLimit = 200;

    /// <summary>Each C# file read, by its path from the folder as the index holds it, the index's own string, so the record
    /// costs no path of its own: touched only by the reads, which run one at a time on a worker.</summary>
    private HashSet<string> read = new(PathNames.Comparer);

    /// <summary>The files read that make an AppHost, by their paths: a single-file AppHost, or an aspire.config.json naming
    /// one the CLI runs (<see cref="cliHosts"/>); touched only by the reads.</summary>
    private readonly HashSet<string> appHosts = new(PathNames.Comparer);

    /// <summary>The AppHost each aspire.config.json among <see cref="appHosts"/> names, by the file; touched only by the reads.</summary>
    private readonly Dictionary<string, string> cliHosts = new(PathNames.Comparer);

    /// <summary>The aspire.config.json naming each AppHost of another language, there or not yet, by the AppHost's file, so its
    /// coming or going reads the config again; touched only by the reads.</summary>
    private readonly Dictionary<string, string> namingConfigs = new(PathNames.Comparer);

    /// <summary>The AppHost files of <see cref="namingConfigs"/> as the last read left them; on the UI thread.</summary>
    private IReadOnlySet<string> namedHosts = new HashSet<string>();

    /// <summary>Whether the next walk reads every C# file it lists again, the watcher having lost changes; the reads' alone too.</summary>
    private bool again;

    /// <summary>The C# files changed, come or gone since the last read of the AppHosts' services; touched only by the reads.</summary>
    private readonly HashSet<string> touched = new(PathNames.Comparer);

    private readonly TaskCompletionSource firstWalk = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task reading = Task.CompletedTask;

    private IReadOnlyList<RunConfiguration>? rolesOf;
    private readonly Dictionary<string, (AspireRole Role, string? AppHost)> roles = new(PathNames.Comparer);
    private HashSet<string> hosts = new(PathNames.Comparer);
    private HashSet<string> services = new(PathNames.Comparer);
    private HashSet<string> singles = new(PathNames.Comparer);

    /// <summary>The AppHost projects the reads read the sources of, each with how it compiles them; given on the UI thread.</summary>
    private IReadOnlyList<(string Path, CSharpParseOptions Parsing)> projectHosts = [];

    /// <summary>The services each AppHost's sources name by path, by the AppHost as a run configuration names it; touched only
    /// by the reads.</summary>
    private readonly Dictionary<string, NamedServices> namedBy = new(PathNames.Comparer);

    /// <summary>The services named as the last read left them, by each AppHost; on the UI thread.</summary>
    private IReadOnlyDictionary<string, NamedServices> named = new Dictionary<string, NamedServices>();

    /// <summary>The services each AppHost's sources name by path as the last read left them, by the AppHost as a run
    /// configuration names it: what the folder lends its trust to beside it (<see cref="ProjectSession.Reached"/>).</summary>
    public IReadOnlyDictionary<string, NamedServices> Named => named;

    /// <summary>The services the AppHosts' sources name changed: what a service's file is, and what configures it, with them.</summary>
    public event EventHandler? ServicesChanged;

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
            HashSet<string> hostsListed = new(PathNames.Comparer);
            foreach (string relative in files)
            {
                if (IsSource(root, relative))
                {
                    listed.Add(relative);
                    string path = Path.Join(root, relative);
                    if (again || !read.Contains(relative))
                    {
                        Found(path, IsAppHost(path));
                        touched.Add(path);
                    }
                    if (appHosts.Contains(path))
                    {
                        hostsListed.Add(path);
                    }
                }
            }
            again = false;
            touched.UnionWith(read.Where(relative => !listed.Contains(relative)).Select(relative => Path.Join(root, relative)));
            read = listed;
            appHosts.IntersectWith(hostsListed);
            foreach (string gone in cliHosts.Keys.Where(file => !appHosts.Contains(file)).ToArray())
            {
                cliHosts.Remove(gone);
            }
            foreach (KeyValuePair<string, string> gone in namingConfigs.Where(n => !listed.Contains(PathNames.Below(n.Value, root).ToString())).ToArray())
            {
                namingConfigs.Remove(gone.Key);
            }
        });
    }

    /// <summary>Files the index put in and took out after a walk, by their paths from the folder: the C# files gone
    /// forgotten, those come read once their folders have stood still (<see cref="StandingStill"/>).</summary>
    public void CameAndWent(string root, IReadOnlyList<string> came, IReadOnlyList<string> gone)
    {
        UiThread.Check();
        List<string> sources = [.. gone.Where(relative => IsSource(root, relative))];
        string[] named = [.. came.Concat(gone).Select(relative => Path.Join(root, relative)).Where(namedHosts.Contains)];
        if (sources.Count > 0 || named.Length > 0)
        {
            Then(walk: false, () =>
            {
                foreach (string relative in sources)
                {
                    read.Remove(relative);
                    appHosts.Remove(Path.Join(root, relative));
                    cliHosts.Remove(Path.Join(root, relative));
                    touched.Add(Path.Join(root, relative));
                    foreach (string config in namingConfigs.Where(n => PathNames.Same(n.Value, Path.Join(root, relative))).Select(n => n.Key).ToArray())
                    {
                        namingConfigs.Remove(config);
                    }
                }
                ReadNaming(named);
            });
        }
        arrivals.Came(came.Where(relative => IsSource(root, relative)).Select(relative => Path.Join(root, relative)));
    }

    /// <summary>C# files that came, their folders standing still: each still there read, as one written is.</summary>
    private void ReadCame(IReadOnlyList<string> came)
    {
        if (session.Folder is not { } root)
        {
            return;
        }
        Then(walk: false, () =>
        {
            foreach (string path in came)
            {
                string relative = PathNames.Below(path, root).ToString();
                if (!File.Exists(path) || IsAppHost(path) is not { } appHost)
                {
                    continue;
                }
                read.Add(relative);
                Found(path, appHost);
                touched.Add(path);
            }
        });
    }

    /// <summary>Files the folder watcher saw change: each C# file among them read again, as it may have gained or lost the
    /// AppHost SDK's line, but one that <paramref name="came"/>, read once its folder has stood still, as any change there
    /// puts that read off; with <paramref name="every"/> (the system lost changes) every one, as the walk that follows lists it.</summary>
    public void Changed(IReadOnlyCollection<string> paths, IReadOnlySet<string> came, bool every = false)
    {
        UiThread.Check();
        if (session.Folder is not { } root)
        {
            return;
        }
        arrivals.Heard(paths);
        string[] sources = [.. paths.Where(path => !came.Contains(path) && IsSource(root, PathNames.Below(path, root)))];
        arrivals.Came(paths.Where(path => came.Contains(path) && IsSource(root, PathNames.Below(path, root))));
        string[] named = [.. paths.Where(namedHosts.Contains)];
        if (sources.Length == 0 && named.Length == 0 && !every)
        {
            return;
        }
        Then(walk: false, () =>
        {
            // Kept until the walk reads each again, so the AppHosts already found stay offered meanwhile.
            again |= every;
            foreach (string path in sources)
            {
                touched.Add(path);
                if (IsAppHost(path) is { } appHost)
                {
                    read.Add(PathNames.Below(path, root).ToString());
                    Found(path, appHost);
                }
            }
            ReadNaming(named);
        });
    }

    /// <summary>Reads again the aspire.config.json naming each AppHost file that came, went or changed, which makes an AppHost
    /// only while the file is there.</summary>
    private void ReadNaming(IEnumerable<string> hosts)
    {
        foreach (string host in hosts)
        {
            if (namingConfigs.GetValueOrDefault(host) is { } config && File.Exists(config))
            {
                touched.Add(config);
                Found(config, IsAppHost(config));
            }
        }
    }

    /// <summary>The C# file's role, as its project and the folder's AppHosts make it now.</summary>
    public AspireRole RoleOf(string path) => Of(path).Role;

    /// <summary>The AppHost a C# file is a source of, as a run configuration names it: its AppHost project's file, or the
    /// single-file AppHost itself; null for any other file.</summary>
    public string? AppHostOf(string path) => Of(path).AppHost;

    /// <summary>The AppHosts that run a project as a service, by their files as run configurations name them: those that
    /// reference it, as an AppHost SDK's does, and those whose sources name it by path (<see cref="AspireServices"/>).</summary>
    public IReadOnlyList<string> AppHostsRunning(string project) =>
        [.. session.Projects.Where(p => p.IsAspireHost && p.ProjectReferences.Any(r => PathNames.Same(r, project))).Select(p => p.Path)
            .Concat(named.Where(n => n.Value.All.Contains(project, PathNames.Comparer)).Select(n => n.Key)).Distinct(PathNames.Comparer)];

    /// <summary>The run configurations were made again: the AppHost projects' sources are read for their services where
    /// the AppHosts are others than those read.</summary>
    private void FollowAppHosts()
    {
        (string Path, CSharpParseOptions Parsing)[] now = [.. session.Projects.Where(p => p.IsAspireHost).Select(p => (p.Path, CompilationSymbols.Of(p)))];
        if (!now.Select(h => h.Path).SequenceEqual(projectHosts.Select(h => h.Path), PathNames.Comparer))
        {
            projectHosts = now;
            Then(walk: false, () => { });
        }
    }

    private (AspireRole Role, string? AppHost) Of(string path)
    {
        if (!ReferenceEquals(rolesOf, session.Configurations.All))
        {
            rolesOf = session.Configurations.All;
            roles.Clear();
            ProjectModel[] appHosts = [.. session.Projects.Where(p => p.IsAspireHost)];
            hosts = new(appHosts.Select(p => p.Path), PathNames.Comparer);
            services = new(appHosts.SelectMany(p => p.ProjectReferences).Concat(named.Values.SelectMany(s => s.All)), PathNames.Comparer);
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
        IReadOnlyList<(string Path, CSharpParseOptions Parsing)> given = projectHosts;
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
                (IReadOnlyList<string> found, IReadOnlyDictionary<string, NamedServices>? services, HashSet<string> awaited) = await BackgroundWork.Run(() =>
                {
                    work();
                    IReadOnlyList<string> offered = [.. appHosts.Select(file => cliHosts.GetValueOrDefault(file) ?? file).Distinct(PathNames.Comparer)
                        .Order(StringComparer.Ordinal).Take(AppHostLimit)];
                    // Only a C# AppHost's source names services by path for the reads to follow.
                    return (offered, ReadNamed(given, [.. offered.Where(host => !AspireAppHosts.RunsThroughCli(host))])
                        ? new Dictionary<string, NamedServices>(namedBy, PathNames.Comparer) : null, new HashSet<string>(namingConfigs.Keys, PathNames.Comparer));
                });
                if (session.IsEnded)
                {
                    return;
                }
                namedHosts = awaited;
                if (services is not null)
                {
                    named = services;
                    rolesOf = null;
                }
                if (!found.SequenceEqual(session.Configurations.SingleFileAppHosts, PathNames.Comparer))
                {
                    session.Configurations.ConfigureAppHosts(found);
                }
                if (services is not null)
                {
                    ServicesChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Reads the services the AppHosts' sources name (<see cref="namedBy"/>): each never read, or whose sources a read
    /// since the last saw change, came or go; those gone forgotten. Whether what they name changed.</summary>
    private bool ReadNamed(IReadOnlyList<(string Path, CSharpParseOptions Parsing)> projects, IReadOnlyList<string> singleFiles)
    {
        bool changed = false;
        HashSet<string> current = new(PathNames.Comparer);
        foreach ((string host, bool single, CSharpParseOptions parsing) in projects.Select(p => (p.Path, false, p.Parsing))
            .Concat(singleFiles.Select(s => (s, true, CSharpParsing.Default))))
        {
            current.Add(host);
            string folder = Path.GetDirectoryName(host)!;
            if (namedBy.ContainsKey(host) && !touched.Any(path => single ? PathNames.Same(path, host) : PathNames.Same(Path.GetDirectoryName(path)!, folder)))
            {
                continue;
            }
            IReadOnlyList<string> sources;
            try
            {
                sources = single ? [host] : AspireOrchestration.AppHostSources(host);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                sources = [];
            }
            NamedServices found = NamedIn(sources, folder, parsing);
            if (!namedBy.TryGetValue(host, out NamedServices? before) || !before.Equals(found))
            {
                namedBy[host] = found;
                changed = true;
            }
        }
        foreach (string gone in namedBy.Keys.Where(host => !current.Contains(host)).ToList())
        {
            namedBy.Remove(gone);
            changed = true;
        }
        touched.Clear();
        return changed;
    }

    /// <summary>The projects an AppHost's sources name by a path written in them (<see cref="AspireServices"/>), from its folder,
    /// those only sources an agent's call wrote name apart (<see cref="Mcp.AgentReach.WroteFor"/>).</summary>
    private static NamedServices NamedIn(IReadOnlyList<string> sources, string folder, CSharpParseOptions parsing)
    {
        List<string> persons = [];
        List<string> agents = [];
        foreach (string source in sources)
        {
            string text;
            try
            {
                text = File.ReadAllText(source);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (!AspireServices.Calls.Any(call => text.Contains(call.Method, StringComparison.Ordinal)))
            {
                continue;
            }
            (Mcp.AgentReach.WroteFor(PathNames.Canonical(source)) ? agents : persons).AddRange(AspireServices.In(CSharpParsing.Parse(text, parsing).GetRoot())
                .Select(service => service.Type is null && AspireServices.Written(service.Path) is { } path ? AspireServices.ProjectOf(path, folder) : null)
                .OfType<string>().Select(PathNames.Canonical));
        }
        string[] person = [.. persons.Distinct(PathNames.Comparer)];
        return new NamedServices(person, [.. agents.Distinct(PathNames.Comparer).Except(person, PathNames.Comparer)]);
    }

    /// <summary>Whether a file, by its path from the folder, is a C# file a single-file AppHost may be: not under a hidden
    /// folder or one every walk passes over.</summary>
    private static bool IsSource(string root, ReadOnlySpan<char> relative)
    {
        if (!(relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(relative).Equals(AspireAppHosts.ConfigFile, StringComparison.OrdinalIgnoreCase))
            || Path.IsPathRooted(relative))
        {
            return false;
        }
        ReadOnlySpan<char> folders = relative[..Math.Max(0, relative.LastIndexOfAny(PathNames.Separators))];
        return !IgnoredFolders.AnyHidden(folders) && !IgnoredFolders.AnyIn(root, folders);
    }

    /// <summary>Takes what a read found of a file: an AppHost or not; null, unread, keeps what was found before.</summary>
    private void Found(string path, bool? appHost)
    {
        if (appHost == true)
        {
            appHosts.Add(path);
        }
        else if (appHost == false)
        {
            appHosts.Remove(path);
            cliHosts.Remove(path);
        }
    }

    /// <summary>Whether a file makes an AppHost: a single-file AppHost, or an aspire.config.json naming one the CLI runs, which
    /// it keeps (<see cref="cliHosts"/>); null when it could not be read, which keeps what was read before.</summary>
    private bool? IsAppHost(string path)
    {
        try
        {
            if (Path.GetFileName(path).Equals(AspireAppHosts.ConfigFile, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string before in namingConfigs.Where(n => PathNames.Same(n.Value, path)).Select(n => n.Key).ToArray())
                {
                    namingConfigs.Remove(before);
                }
                if (File.Exists(path) && AspireAppHosts.WrittenIn(path) is { } named)
                {
                    namingConfigs[named] = path;
                    if (File.Exists(named))
                    {
                        cliHosts[path] = named;
                        return true;
                    }
                }
                return false;
            }
            return File.Exists(path) && AspireAppHosts.IsSingleFile(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The .NET projects an AppHost's sources name by path (<see cref="AspireServices"/>): those the person's sources name,
/// and those only sources an agent's call wrote name (<see cref="Mcp.AgentReach.WroteFor"/>), which lend nothing new.</summary>
public sealed record NamedServices(IReadOnlyList<string> Persons, IReadOnlyList<string> Agents)
{
    /// <summary>Every project its sources name, whoever wrote them.</summary>
    public IEnumerable<string> All => Persons.Concat(Agents);

    public bool Equals(NamedServices? other) => other is not null && Persons.SequenceEqual(other.Persons, PathNames.Comparer)
        && Agents.SequenceEqual(other.Agents, PathNames.Comparer);

    public override int GetHashCode() => HashCode.Combine(Persons.Count, Agents.Count);
}
