namespace Passenger.Core;

/// <summary>Whether Passenger runs a folder's own code: its MSBuild targets, analyzers, generators and restore, its
/// TypeScript, ESLint and Prettier, the programs its Git configuration names, a dotnet its global.json names, Godot's
/// editor on its native extensions, its built assemblies in a previewer. One answer per folder, a folder trusted with
/// every folder under it: the person's, which the App keeps in their state and gives here (<see cref="Trust"/>), and
/// every folder's in a process nobody is asked in (<see cref="TrustEverything"/>). A trust given stands until the person
/// takes it back (<see cref="TakeBack"/>). Any thread.</summary>
public static class WorkspaceTrust
{
    /// <summary>Why a folder's code does not run, in the one sentence every refusal of it says.</summary>
    public const string Untrusted = "The folder is not trusted: Passenger runs none of its code until it is";

    private static readonly Lock Gate = new();
    private static TrustedFolders folders = TrustedFolders.None;

    /// <summary>Whether a folder (or a file) is trusted: under a folder trusted, or anywhere in a process that trusts
    /// every folder but one held to the person's answer (<see cref="Ask"/>).</summary>
    public static bool Trusts(string folder) => Volatile.Read(ref folders).Trusts(folder);

    /// <summary>Whether this process trusts every folder: nobody is here to ask.</summary>
    public static bool TrustsEverything => Volatile.Read(ref folders).Everything;

    /// <summary>The trust changed: a folder was trusted or taken back, or the process came to trust every folder. Raised on
    /// the thread that changed it.</summary>
    public static event EventHandler? Changed;

    /// <summary>Every folder trusted from now on, but one held to the person's answer: a Passenger nobody sees (an agent's
    /// own, a screenshot's) and a test run, where whoever started it chose the folder.</summary>
    public static void TrustEverything() => Change(now => now with { Everything = true });

    /// <summary>Folders trusted from now on, each with every folder under it: the person's answer, and those their state
    /// keeps. Canonical paths.</summary>
    public static void Trust(params IEnumerable<string> trusted) => Change(now => now.With(trusted));

    /// <summary>A folder the person trusted, and every folder trusted under it, no longer trusted from now on: what runs of
    /// its code is the caller's to end. A folder above it still trusted, or one lent, keeps it trusted. A canonical path.</summary>
    public static void TakeBack(string folder) => Change(now => now.Without(folder));

    /// <summary>The folders a trusted workspace reaches outside its own (a solution's projects beside it), trusted while it is
    /// open, in place of those it lent before; none lets them go. Canonical paths.</summary>
    public static void Lend(object workspace, IEnumerable<string> reached)
    {
        string[] folders = [.. reached];
        Change(now => now.Lending(workspace, folders));
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
