using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Host;
using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.App.MSBuild;

/// <summary>The code help in a folder's MSBuild files: every open project, .props and .targets given an
/// <see cref="MSBuildDocument"/>, each evaluated through the project it belongs to (itself, the workspace's project whose
/// evaluation imports it, or the one Go to Declaration came from) in the folder's project host, with the open files'
/// unsaved text in place of the disk's. Package ids, versions and advisories come from the folder's packages. A UI-thread
/// object; the host and the feeds are asked off it.</summary>
public sealed class MSBuildService(Func<string?> folder, Func<WorkspaceModel?> workspace) : IDisposable
{
    private readonly Dictionary<OpenDocument, MSBuildDocument> documents = [];

    /// <summary>The project a file outside the workspace's imports was gone into from (an SDK's targets), by path.</summary>
    private readonly Dictionary<string, string> cameFrom = new(StringComparer.Ordinal);

    private Task<IReadOnlyList<string>>? sdks;
    private bool disposed;

    /// <summary>The folder's package feeds (All Feeds), its session's packages'.</summary>
    public Func<Task<IPackageFeed>>? Feed { get; set; }

    /// <summary>What the folder's audit feeds know of vulnerable packages, its session's packages'.</summary>
    public Func<Task<VulnerabilityData>>? Vulnerabilities { get; set; }

    /// <summary>Something to say in the status bar: why a project could not be evaluated, or a feed did not answer.</summary>
    public event EventHandler<string>? Message;

    public MSBuildDocument? For(OpenDocument document) => documents.GetValueOrDefault(document);

    public IReadOnlyCollection<MSBuildDocument> Documents => documents.Values;

    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (disposed || documents.ContainsKey(document) || folder() is null || !MSBuildFiles.Is(document.Path))
        {
            return;
        }
        documents[document] = new MSBuildDocument(this, document);
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out MSBuildDocument? gone))
        {
            gone.Dispose();
        }
    }

    /// <summary>Files changed on disk: each open file whose evaluation read one of them (or that is one) is evaluated again,
    /// and with files come or gone among them (<paramref name="cameOrWent"/>), each whose wildcards walked a folder they are in.</summary>
    public void FilesChanged(IReadOnlyCollection<string> paths, bool cameOrWent)
    {
        UiThread.Check();
        foreach (MSBuildDocument document in documents.Values)
        {
            if (document.Read(paths, cameOrWent))
            {
                document.Again();
            }
        }
    }

    /// <summary>Files the IDE itself made, moved or deleted, which the folder watcher may take for saves: each open
    /// file whose evaluation walked folders is evaluated again.</summary>
    public void FilesCameOrWent()
    {
        UiThread.Check();
        foreach (MSBuildDocument document in documents.Values.Where(d => d.Walks))
        {
            document.Again();
        }
    }

    /// <summary>The workspace's projects were evaluated: an open file that had no project to be evaluated through may now.</summary>
    public void ProjectsEvaluated()
    {
        UiThread.Check();
        foreach (MSBuildDocument document in documents.Values.Where(d => d.Project is null))
        {
            document.Again();
        }
    }

    /// <summary>A project's evaluation landed for one of its files: the project's other open files take it up too, without
    /// asking for it again.</summary>
    internal void Inspected(MSBuildDocument from, MSBuildInsight insight, IReadOnlyDictionary<string, string> texts)
    {
        foreach (MSBuildDocument other in documents.Values.Where(d => d != from && d.Project is { } own && PathNames.Same(own, insight.Project)).ToList())
        {
            IdeErrors.Observe(other.AdoptAsync(insight, texts));
        }
    }

    /// <summary>The project a file is evaluated through: a project itself; else the workspace's project whose evaluation
    /// imported it, the nearest; a Directory.*.props or .targets the nearest project under its folder; else the project Go
    /// to Declaration came from into it; null for none.</summary>
    public string? ProjectFor(string path)
    {
        if (MSBuildFiles.IsProject(path))
        {
            return path;
        }
        IReadOnlyList<ProjectModel> projects = workspace()?.Projects ?? [];
        ProjectModel? reading = projects.Where(p => p.Exists && p.Evaluated?.Inputs.Any(i => PathNames.Same(i.Path, path)) == true)
            .OrderBy(p => p.Path.Length).FirstOrDefault();
        if (reading is not null)
        {
            return reading.Path;
        }
        string name = Path.GetFileName(path);
        if (name.StartsWith("Directory.", StringComparison.OrdinalIgnoreCase)
            && projects.Where(p => p.Exists && PathNames.IsUnder(p.Path, Path.GetDirectoryName(path)!)).MinBy(p => p.Path.Length) is { } under)
        {
            return under.Path;
        }
        return cameFrom.GetValueOrDefault(path);
    }

    /// <summary>A file Go to Declaration is about to open, gone into from a project's file: it is evaluated through that
    /// project when the workspace's evaluations do not import it themselves (an SDK's targets).</summary>
    public void CameFrom(string path, string project)
    {
        if (!cameFrom.ContainsKey(path))
        {
            cameFrom[path] = project;
        }
    }

    /// <summary>The open MSBuild files whose text differs from the disk's, which an evaluation reads in its place.</summary>
    internal Dictionary<string, string> Texts()
    {
        Dictionary<string, string> texts = new(StringComparer.Ordinal);
        foreach (OpenDocument document in documents.Keys.Where(d => d.IsModified))
        {
            texts[document.Path] = document.Text.Snapshot.ToString();
        }
        return texts;
    }

    /// <summary>The global properties a build of the solution gives its projects.</summary>
    internal Dictionary<string, string> Properties() => workspace() is { } open ? ProjectModels.Properties(open) : [];

    /// <summary>The project host for the folder's SDK; null with no folder.</summary>
    internal async Task<ProjectHost?> HostAsync() => folder() is { } open ? await Task.Run(() => ProjectHosts.ForAsync(open)) : null;

    /// <summary>The SDKs the host's .NET SDK carries (its Sdks folder's), read once.</summary>
    internal Task<IReadOnlyList<string>> SdksAsync() => sdks ??= ReadSdksAsync();

    private async Task<IReadOnlyList<string>> ReadSdksAsync()
    {
        try
        {
            if (await HostAsync() is not { } host)
            {
                return [];
            }
            return await Task.Run(() => (IReadOnlyList<string>)[.. Directory.EnumerateDirectories(Path.Combine(host.Sdk, "Sdks")).Select(Path.GetFileName).OfType<string>()
                .Where(n => n.StartsWith("Microsoft.NET.Sdk", StringComparison.Ordinal)).Order(StringComparer.Ordinal)]);
        }
        catch (Exception error) when (error is ProjectHostFailed or IOException or UnauthorizedAccessException)
        {
            sdks = null;
            return [];
        }
    }

    /// <summary>The known vulnerabilities, or none when nothing can say.</summary>
    internal async Task<VulnerabilityData> KnownVulnerabilitiesAsync()
    {
        if (Vulnerabilities is not { } read)
        {
            return VulnerabilityData.None;
        }
        try
        {
            return await read();
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            return VulnerabilityData.None;
        }
    }

    internal void Say(string message) => Message?.Invoke(this, message);

    public void Dispose()
    {
        disposed = true;
        foreach (OpenDocument document in documents.Keys.ToList())
        {
            Detach(document);
        }
    }
}
