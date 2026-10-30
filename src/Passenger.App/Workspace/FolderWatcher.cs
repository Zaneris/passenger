using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>What changed in a folder while it was watched: the files created, changed, renamed or deleted, whether
/// any of that changed which files there are, whether a project or solution file was among them, and whether Git's
/// own state moved (a commit or checkout made elsewhere, a repository made or removed); and, apart from the files, the
/// restores written (each project's <c>obj/project.assets.json</c>), whatever ran them: the language server, a
/// terminal's dotnet, a build.</summary>
public sealed record FolderChanges(IReadOnlySet<string> Paths, bool Structure, bool Projects, bool Git)
{
    public IReadOnlySet<string> Restored { get; init; } = new HashSet<string>();

    /// <summary>The files and folders among <see cref="Paths"/> that came or went (created, deleted or renamed), each also
    /// as a walk names it, its folder's canonical path and its own name: what Go to File's index puts in or takes out.</summary>
    public IReadOnlySet<string> ComeOrGone { get; init; } = new HashSet<string>();

    /// <summary>Whether the system lost changes (too many at once, as a large checkout makes) or a watch failed: what they
    /// were is not known, so everything shown from the folder is read again (the open files, the Explorer, the solution,
    /// Go to File's index). <see cref="Paths"/> then holds the folder itself, and what its record held that is gone.</summary>
    public bool Lost { get; init; }

    /// <summary>Whether the system said it lost changes, which the folders listed again find, all but a file rewritten in
    /// place where nothing else changed: the open files are each looked at, as few as they are.</summary>
    public bool Overflowed { get; init; }

    /// <summary>Whether a project's launch profiles (its <c>launchSettings.json</c>, a file-based program's <c>.run.json</c>)
    /// were among the files: the run configurations are made of them, and nothing the solution or the Explorer shows is.</summary>
    public bool LaunchProfiles { get; init; }
}

/// <summary>Watches a folder for what other programs change in it (a terminal's <c>git pull</c>, an agent's edits,
/// <c>dotnet new</c>), handing the changes over on the UI thread. Build output, dependencies and Git's folder are passed
/// over, except Git's folder come or gone, its HEAD, index and refs (<see cref="Vcs.GitWatcher.Moves"/>) and a restore's
/// assets; where each folder is watched on its own (Linux), they are never watched at all but for Git's and each obj
/// itself, and a folder made later is watched as it appears, what was written in it before then taken as made with it. A
/// burst's lost changes are found as <see cref="WatchedTree"/> finds them.</summary>
public sealed class FolderWatcher : WatchedTree
{
    /// <summary>How long after a save its file's events are taken for the save's own.</summary>
    private static readonly TimeSpan OwnWriteHeard = TimeSpan.FromSeconds(5);

    private static readonly string[] ProjectExtensions = [".sln", ".slnx", ".slnf", ".csproj", ".fsproj", ".vbproj", ".props", ".targets"];

    /// <summary>The file a restore writes in a project's obj (or, with UseArtifactsOutput, in artifacts/obj/Project).</summary>
    private const string Assets = "project.assets.json";

    private readonly string folder;
    private readonly HashSet<string> paths = new(StringComparer.Ordinal);
    private readonly HashSet<string> restored = new(StringComparer.Ordinal);
    private readonly HashSet<string> comeOrGone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> ownWrites = new(StringComparer.Ordinal);
    private bool structure;
    private bool projects;
    private bool launchProfiles;
    private bool git;
    private int outOfWatches;

    public FolderWatcher(string folder, FolderWatching watching)
        : base(folder, watching, watching.EachFolder)
    {
        this.folder = folder;
        FileText.Writing += OnWriting;
        Begin();
    }

    protected override string Shown => "The folder's watcher";

    /// <summary>A file Passenger is saving: its events in the next moments are its content changing, not a new file.
    /// The saves heard of longer ago than that are let go here, so the watcher keeps only its last moments' saves.</summary>
    private void OnWriting(string path)
    {
        string canonical = PathNames.Canonical(path);
        lock (Gate)
        {
            DateTime now = DateTime.UtcNow;
            foreach ((string written, DateTime at) in ownWrites)
            {
                if (now - at >= OwnWriteHeard)
                {
                    // A dictionary's enumeration survives a removal (since .NET Core 3.0).
                    ownWrites.Remove(written);
                }
            }
            ownWrites[canonical] = now;
        }
    }

    /// <summary>Raised on the UI thread with what changed since the last time.</summary>
    public event EventHandler<FolderChanges>? Changed;

    public override void Dispose()
    {
        FileText.Writing -= OnWriting;
        base.Dispose();
    }

    /// <summary>The person hears once for the folder opened that its watches ran out, not once for each folder.</summary>
    protected override void Unwatched(string at, Exception error)
    {
        if (OutOfWatches(error) is { } advice && Interlocked.Exchange(ref outOfWatches, 1) == 0)
        {
            IdeErrors.Report(new IOException(advice, error));
        }
    }

    /// <summary>Whether a folder is watched where each is watched on its own: the open folder and the kept folders under
    /// it, of Git's store only its top and the folders of its refs, whose files say a commit or checkout happened, and of
    /// build output only the folders a restore writes its assets in (<see cref="HoldsRestores"/>).</summary>
    protected override bool Kept(string path)
    {
        ReadOnlySpan<char> parts = PathNames.Below(path, folder);
        if (parts.IsEmpty)
        {
            return path == folder;
        }
        ReadOnlySpan<char> first = First(parts, out ReadOnlySpan<char> rest);
        return first.SequenceEqual(".git") ? First(parts, out _).Length == parts.Length || Vcs.GitWatcher.HoldsRefs(First(rest, out _))
            : !IgnoredFolders.AnyIn(folder, parts) || HoldsRestores(parts);
    }

    /// <summary>Whether a folder of build output is where a restore writes its assets, or above it to see that made: a
    /// project's obj itself, and UseArtifactsOutput's artifacts, artifacts/obj and artifacts/obj/Project.</summary>
    private bool HoldsRestores(ReadOnlySpan<char> parts)
    {
        bool two = Last(parts, out ReadOnlySpan<char> above, out ReadOnlySpan<char> last);
        bool three = Last(above, out ReadOnlySpan<char> aboveSecond, out ReadOnlySpan<char> second);
        Last(aboveSecond, out ReadOnlySpan<char> aboveThird, out ReadOnlySpan<char> third);
        int output = three && second.SequenceEqual("obj") && third.SequenceEqual("artifacts") ? 3
            : two && last.SequenceEqual("obj") && second.SequenceEqual("artifacts") ? 2
            : last.SequenceEqual("artifacts") || last.SequenceEqual("obj") ? 1
            : 0;
        return output > 0 && !IgnoredFolders.AnyIn(folder, output switch { 3 => aboveThird, 2 => aboveSecond, _ => above });
    }

    /// <summary>Whether a path, by its names from the open folder, is a restore's assets, which the rest of build output's
    /// files are not.</summary>
    private static bool IsRestore(ReadOnlySpan<char> parts)
    {
        if (!Last(parts, out ReadOnlySpan<char> above, out ReadOnlySpan<char> last) || !last.SequenceEqual(Assets))
        {
            return false;
        }
        bool three = Last(above, out ReadOnlySpan<char> aboveSecond, out ReadOnlySpan<char> second);
        Last(aboveSecond, out _, out ReadOnlySpan<char> third);
        return second.SequenceEqual("obj") || three && third.SequenceEqual("obj");
    }

    /// <summary>A path's first name and the names after it.</summary>
    private static ReadOnlySpan<char> First(ReadOnlySpan<char> parts, out ReadOnlySpan<char> rest)
    {
        int at = parts.IndexOfAny(PathNames.Separators);
        rest = at < 0 ? [] : parts[(at + 1)..];
        return at < 0 ? parts : parts[..at];
    }

    /// <summary>A path's last name and the names before it; false when it has one name alone.</summary>
    private static bool Last(ReadOnlySpan<char> parts, out ReadOnlySpan<char> above, out ReadOnlySpan<char> last)
    {
        int at = parts.LastIndexOfAny(PathNames.Separators);
        above = at < 0 ? [] : parts[..at];
        last = at < 0 ? parts : parts[(at + 1)..];
        return at >= 0;
    }

    /// <summary>What the system saw at a path, by its names from the open folder read in place, so the events of what is
    /// passed over (build output, dependencies, Git's objects) cost no string; only those kept are made canonical. Git's
    /// and a restore's are taken apart and count toward no burst.</summary>
    protected override Notice Noticed(string path, bool created)
    {
        if (FileText.IsSaving(path))
        {
            return default;
        }
        ReadOnlySpan<char> parts = PathNames.Below(path, folder);
        bool inGit = First(parts, out ReadOnlySpan<char> rest).SequenceEqual(".git");
        if (inGit)
        {
            // Git's folder itself come or gone is a repository made or removed (git init, a clone, its .git moved away).
            bool moved = rest.IsEmpty ? created : Vcs.GitWatcher.Moves(First(rest, out _));
            return moved ? new Notice(Takes: true, Counts: false, Canonical: null) : default;
        }
        if (IsRestore(parts))
        {
            return new Notice(Takes: true, Counts: false, PathNames.Canonical(path));
        }
        // A passed-over folder itself, come or gone, is its parent changing; what changes in it is never followed.
        if (IgnoredFolders.AnyIn(folder, parts) && !(created && !IgnoredFolders.AnyIn(folder, Above(parts))))
        {
            return default;
        }
        // As a walk names it: its own name in its folder, which a link's canonical path is not.
        string? named = !created || Path.GetDirectoryName(path) is not { } parent ? null : Path.Join(PathNames.Canonical(parent), Path.GetFileName(path.AsSpan()));
        return new Notice(Takes: true, Counts: true, PathNames.Canonical(path), named);
    }

    /// <summary>Takes in a change under the gate: Git's state moved, a restore written, or a kept path changed, with what it
    /// changes (which files there are, the projects, the launch profiles).</summary>
    protected override void Take(string path, bool created, Notice notice)
    {
        if (notice.Canonical is not { } canonical)
        {
            git = true;
            return;
        }
        if (!notice.Counts)
        {
            restored.Add(canonical);
            return;
        }
        paths.Add(canonical);
        // A save moves its temporary file over the file: a change, not a file come or gone.
        bool saved = ownWrites.TryGetValue(canonical, out DateTime at) && DateTime.UtcNow - at < OwnWriteHeard;
        if (created && !saved)
        {
            structure = true;
            comeOrGone.Add(canonical);
            if (notice.Named is { } named)
            {
                comeOrGone.Add(named);
            }
        }
        projects |= IsProjectFile(path);
        launchProfiles |= Path.GetFileName(path.AsSpan()).Equals(Run.LaunchProfile.SettingsFile, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".run.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A path's names but its last.</summary>
    private static ReadOnlySpan<char> Above(ReadOnlySpan<char> parts)
    {
        Last(parts, out ReadOnlySpan<char> above, out _);
        return above;
    }

    /// <summary>Whether a file is a solution, project, props or targets file, which the solution is read again for.</summary>
    private static bool IsProjectFile(string path)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(path.AsSpan());
        foreach (string project in ProjectExtensions)
        {
            if (extension.Equals(project, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    protected override FolderChanges? Gathered(bool lost, bool overflowed)
    {
        FolderChanges changes = new(new HashSet<string>(paths, StringComparer.Ordinal), structure, projects, git)
        {
            Restored = new HashSet<string>(restored, StringComparer.Ordinal),
            ComeOrGone = new HashSet<string>(comeOrGone, StringComparer.Ordinal),
            Lost = lost,
            Overflowed = overflowed,
            LaunchProfiles = launchProfiles,
        };
        paths.Clear();
        restored.Clear();
        comeOrGone.Clear();
        structure = projects = launchProfiles = git = false;
        return changes.Paths.Count > 0 || changes.Restored.Count > 0 || changes.Git || changes.Overflowed ? changes : null;
    }

    /// <summary>On the UI thread, unless the watcher was disposed meanwhile.</summary>
    protected override void Hand(FolderChanges changes) => UiThread.Post(() =>
    {
        if (!IsDisposed)
        {
            Changed?.Invoke(this, changes);
        }
    });
}
