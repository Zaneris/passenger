namespace Passenger.Core;

/// <summary>Whether Passenger runs a folder's own code: its MSBuild targets, analyzers, generators and restore, its
/// TypeScript, ESLint and Prettier, the programs its Git configuration names, a dotnet its global.json names, Godot's
/// editor on its native extensions, its built assemblies in a previewer. One answer per folder, a folder trusted with
/// every folder under it: the person's, which the App keeps in their state and gives here (<see cref="Trust"/>), and
/// every folder's in a process nobody is asked in (<see cref="TrustEverything"/>). A folder in a Git repository is trusted
/// with the whole repository (<see cref="FolderToTrust"/>), whose build files its builds take in. A trust given stands
/// until the person takes it back (<see cref="TakeBack"/>). Any thread.</summary>
public static class WorkspaceTrust
{
    /// <summary>Why a folder's code does not run, in the one sentence every refusal of it says.</summary>
    public const string Untrusted = "The folder is not trusted: Passenger runs none of its code until it is";

    private static readonly Lock Gate = new();
    private static TrustedFolders folders = TrustedFolders.None;

    /// <summary>Whether a folder (or a file) is trusted: under a folder trusted, or anywhere in a process that trusts
    /// every folder but one held to the person's answer (<see cref="Ask"/>).</summary>
    public static bool Trusts(string folder) => Volatile.Read(ref folders).Trusts(folder);

    /// <summary>The gate every MSBuild question or target run of a folder's project goes through before it starts (the App's
    /// MSBuildQuestions: a workload's question, a listing of devices, an installer), whoever asks and whatever the window
    /// holds of the project (a kept evaluation): one on a path not trusted is refused, <see cref="UntrustedFolderException"/>
    /// saying <see cref="Untrusted"/>.</summary>
    public static void Check(string path)
    {
        if (!Trusts(path))
        {
            throw new UntrustedFolderException(path);
        }
    }

    /// <summary>Whether this process trusts every folder: nobody is here to ask.</summary>
    public static bool TrustsEverything => Volatile.Read(ref folders).Everything;

    /// <summary>The trust changed: a folder was trusted or taken back, or the process came to trust every folder. Raised on
    /// the thread that changed it.</summary>
    public static event EventHandler? Changed;

    /// <summary>Every folder trusted from now on, but one held to the person's answer: a Passenger nobody sees (an agent's
    /// own, a screenshot's) and a test run, where whoever started it chose the folder.</summary>
    public static void TrustEverything() => Change(now => now with { Everything = true });

    /// <summary>Folders trusted from now on, each with every folder under it, a folder in a Git repository trusting the
    /// repository (<see cref="FolderToTrust"/>): the person's answer, and those their state keeps. Canonical paths.</summary>
    public static void Trust(params IEnumerable<string> trusted)
    {
        string[] folders = [.. trusted.Select(FolderToTrust)];
        Change(now => now.With(folders));
    }

    /// <summary>The folder trusting <paramref name="folder"/> trusts: the top of the Git repository holding it
    /// (<see cref="RepositoryOf"/>), as MSBuild takes the build files above a project (Directory.Build.props,
    /// Directory.Packages.props) from anywhere in it, else the folder itself. A canonical path.</summary>
    public static string FolderToTrust(string folder) => RepositoryOf(folder) ?? folder;

    /// <summary>Whether trusting a folder trusts it alone, no Git repository above it holding it (<see cref="FolderToTrust"/>):
    /// what Passenger made for the person (a new solution, a clone) is trusted as it is made only where it does, as only
    /// the person trusts a repository it is in. A canonical path, there or not yet.</summary>
    public static bool TrustsAlone(string folder) => PathNames.Same(FolderToTrust(folder), folder);

    /// <summary>The top of the Git work tree holding a folder, the outermost where one holds another (a submodule in its
    /// superproject, a clone in another's folder): the highest folder of the walk up (<see cref="FoldersUp"/>) with a
    /// <c>.git</c> folder or a <c>.git</c> file naming one (a worktree's, a submodule's), read by Passenger, never asked of
    /// git. Never the home folder, a folder holding it or a disk's root; null where none holds it. A canonical path.</summary>
    public static string? RepositoryOf(string folder)
    {
        if (!Path.IsPathFullyQualified(folder))
        {
            return null;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? canonicalHome = home.Length > 0 ? PathNames.Canonical(home) : null;
        string? top = null;
        foreach (string at in FoldersUp.From(Path.TrimEndingDirectorySeparator(folder)))
        {
            if (Path.GetDirectoryName(at) is null || canonicalHome is not null && PathNames.Within(canonicalHome, at))
            {
                break;
            }
            if (FoldersUp.Found(at, ".git") is { } git && (Directory.Exists(git) || NamesGitFolder(git)))
            {
                top = at;
            }
        }
        return top;
    }

    /// <summary>A folder the person trusted, and every folder trusted under it, no longer trusted from now on: what runs of
    /// its code is the caller's to end. A folder above it still trusted, or one lent, keeps it trusted. A canonical path.</summary>
    public static void TakeBack(string folder) => Change(now => now.Without(folder));

    /// <summary>The folders a trusted workspace whose own folder is <paramref name="open"/> reaches outside it (its solution's
    /// projects beside it), trusted while it is open, in place of those it lent before; none lets them go. Canonical paths;
    /// one no workspace may lend (<see cref="Lends"/>) is refused, whoever asks.</summary>
    public static void Lend(object workspace, string open, IEnumerable<string> reached)
    {
        string[] folders = [.. reached.Where(f => Lends(f, open))];
        Change(now => now.Lending(workspace, folders));
    }

    /// <summary>Whether a workspace whose own folder is <paramref name="open"/> may lend its trust to <paramref name="folder"/>,
    /// a folder of one of its projects outside it: never a disk's root, the home folder or a folder holding it, nor a folder
    /// holding <paramref name="open"/>, each of which would trust what the person never chose. Canonical paths.</summary>
    public static bool Lends(string folder, string open) => WhyNotLent(folder, open) is null;

    /// <summary>Why a workspace whose own folder is <paramref name="open"/> may not lend its trust to <paramref name="folder"/>
    /// (<see cref="Lends"/>); null where it may. Canonical paths.</summary>
    public static Unlent? WhyNotLent(string folder, string open)
    {
        if (!Path.IsPathFullyQualified(folder))
        {
            return Unlent.Relative;
        }
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is null)
        {
            return Unlent.Root;
        }
        if (PathNames.Within(open, folder))
        {
            return Unlent.HoldsOpen;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && PathNames.Within(PathNames.Canonical(home), folder) ? Unlent.HoldsHome : null;
    }

    /// <summary>A folder held to the person's answer while the hold lasts, in a process that trusts every other: a test's
    /// own folder, a screenshot asked to open untrusted. A canonical path.</summary>
    public static IDisposable Ask(string folder)
    {
        Change(now => now with { Asked = [.. now.Asked, folder] });
        return new Asking(folder);
    }

    private static void Change(Func<TrustedFolders, TrustedFolders> change)
    {
        lock (Gate)
        {
            TrustedFolders next = change(folders);
            if (next.Equals(folders))
            {
                return;
            }
            Volatile.Write(ref folders, next);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Whether a <c>.git</c> file names a git folder, as a worktree's and a submodule's do (<c>gitdir: …</c>);
    /// never opened where it only looks like a file (a FIFO, whose open waits for a writer).</summary>
    private static bool NamesGitFolder(string file)
    {
        if (FileKinds.Of(file) is not (null or FileKind.File))
        {
            return false;
        }
        try
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> start = stackalloc byte[GitFolderNamed.Length];
            return stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) == start.Length && start.SequenceEqual(GitFolderNamed);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>How a <c>.git</c> file begins that names a git folder.</summary>
    private static ReadOnlySpan<byte> GitFolderNamed => "gitdir:"u8;

    private static int IndexOf(IReadOnlyList<string> asked, string folder)
    {
        for (int at = 0; at < asked.Count; at++)
        {
            if (ReferenceEquals(asked[at], folder))
            {
                return at;
            }
        }
        return -1;
    }

    private sealed class Asking(string folder) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Change(now => IndexOf(now.Asked, folder) is int at and >= 0 ? now with { Asked = [.. now.Asked.Take(at), .. now.Asked.Skip(at + 1)] } : now);
            }
        }
    }
}

/// <summary>Why a workspace lends a folder none of its trust (<see cref="WorkspaceTrust.WhyNotLent"/>).</summary>
public enum Unlent
{
    /// <summary>The folder is named by no full path.</summary>
    Relative,

    /// <summary>The folder is a disk's root.</summary>
    Root,

    /// <summary>The folder is the open folder or holds it.</summary>
    HoldsOpen,

    /// <summary>The folder is the home folder or holds it.</summary>
    HoldsHome,
}

/// <summary>A run of a folder's code refused at <see cref="WorkspaceTrust.Check"/>: the path is not trusted.</summary>
public sealed class UntrustedFolderException(string path) : InvalidOperationException(WorkspaceTrust.Untrusted)
{
    /// <summary>The path refused.</summary>
    public string Path { get; } = path;
}

/// <summary>Folders trusted, each with every folder under it, and whether every other is too but those held to the
/// person's answer.</summary>
public sealed record TrustedFolders
{
    public static TrustedFolders None { get; } = new();

    /// <summary>Whether every folder is trusted that is not held to the person's answer (<see cref="Asked"/>).</summary>
    public bool Everything { get; init; }

    /// <summary>The folders trusted, canonical.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    /// <summary>The folders whose trust is the person's answer alone, where <see cref="Everything"/> trusts the rest.</summary>
    public IReadOnlyList<string> Asked { get; init; } = [];

    /// <summary>The folders trusted workspaces reach outside their own, each with the workspace that lent it.</summary>
    public IReadOnlyList<(object Workspace, string Folder)> Lent { get; init; } = [];

    /// <summary>These with a workspace's lent folders replaced.</summary>
    public TrustedFolders Lending(object workspace, IReadOnlyList<string> folders) => this with
    {
        Lent = [.. Lent.Where(l => !ReferenceEquals(l.Workspace, workspace)), .. folders.Distinct(PathNames.Comparer).Select(f => (workspace, f))],
    };

    /// <summary>Whether a path is trusted: as given, or as the file system names it (one reached through a link).</summary>
    public bool Trusts(string path)
    {
        if (TrustsAsGiven(path))
        {
            return true;
        }
        string canonical = PathNames.Canonical(path);
        return canonical != path && TrustsAsGiven(canonical);
    }

    /// <summary>These with more folders trusted, those trusted already (or under one) passed over.</summary>
    public TrustedFolders With(IEnumerable<string> trusted)
    {
        List<string> roots = [.. Roots];
        foreach (string folder in trusted)
        {
            if (folder.Length > 0 && Path.IsPathFullyQualified(folder) && !roots.Any(r => PathNames.Within(folder, r)))
            {
                roots.RemoveAll(r => PathNames.IsUnder(r, folder));
                roots.Add(Path.TrimEndingDirectorySeparator(folder));
            }
        }
        return roots.Count == Roots.Count && roots.SequenceEqual(Roots) ? this : this with { Roots = roots };
    }

    /// <summary>These with a folder no longer trusted, nor any trusted under it.</summary>
    public TrustedFolders Without(string folder)
    {
        List<string> roots = [.. Roots.Where(r => !PathNames.Within(r, folder))];
        return roots.Count == Roots.Count ? this : this with { Roots = roots };
    }

    private bool TrustsAsGiven(string path) => Roots.Any(r => PathNames.Within(path, r)) || Lent.Any(l => PathNames.Within(path, l.Folder))
        || Everything && !Asked.Any(a => PathNames.Within(path, a));

    public bool Equals(TrustedFolders? other) => other is not null && Everything == other.Everything
        && Roots.SequenceEqual(other.Roots) && Asked.SequenceEqual(other.Asked)
        && Lent.Count == other.Lent.Count && Lent.Zip(other.Lent).All(p => ReferenceEquals(p.First.Workspace, p.Second.Workspace) && p.First.Folder == p.Second.Folder);

    public override int GetHashCode() => HashCode.Combine(Everything, Roots.Count, Asked.Count);
}
