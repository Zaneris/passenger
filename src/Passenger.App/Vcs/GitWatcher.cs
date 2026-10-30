using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Vcs;

/// <summary>A Git folder outside the open folder watched for Git's own state moving: a linked worktree's or a submodule's,
/// the repository's above a folder opened below its top, and the common folder of a worktree's refs. A change to HEAD, the
/// index or the refs (files, or a reftable repository's tables) is handed over on the UI thread, whatever made it (a
/// terminal's commit, a branch switched by an agent); what else Git writes there (its objects and logs) is passed over, and
/// only the folder itself and the folders of its refs are kept.</summary>
public sealed class GitWatcher : WatchedTree
{
    private bool moved;

    public GitWatcher(string folder, FolderWatching watching)
        : base(folder, watching, watching.EachFolder) => Begin();

    protected override string Shown => $"The watcher of Git's folder {Top}";

    /// <summary>Raised on the UI thread when HEAD, the index or the refs changed.</summary>
    public event EventHandler? Moved;

    /// <summary>Whether a name in Git's folder is that of a folder its refs are kept in: refs, or a reftable repository's
    /// tables (Git 2.45's reftable, which writes nothing else as a commit or a checkout moves a ref).</summary>
    public static bool HoldsRefs(ReadOnlySpan<char> name) => name is "refs" or "reftable";

    /// <summary>Whether a name in Git's folder says Git's state moved when what it names changes: HEAD, the index, packed
    /// refs, or a folder its refs are kept in (<see cref="HoldsRefs"/>).</summary>
    public static bool Moves(ReadOnlySpan<char> name) => name is "HEAD" or "index" or "packed-refs" || HoldsRefs(name);

    protected override bool Kept(string path)
    {
        ReadOnlySpan<char> parts = PathNames.Below(path, Top);
        return parts.IsEmpty ? path == Top : HoldsRefs(First(parts));
    }

    protected override Notice Noticed(string path, bool created) =>
        Moves(First(PathNames.Below(path, Top))) ? new Notice(Takes: true, Counts: false, Canonical: null) : default;

    protected override void Take(string path, bool created, Notice notice) => moved = true;

    protected override FolderChanges? Gathered(bool lost, bool overflowed)
    {
        bool any = moved || lost || overflowed;
        moved = false;
        return any ? new FolderChanges(new HashSet<string>(), Structure: false, Projects: false, Git: true) : null;
    }

    protected override void Hand(FolderChanges changes) => UiThread.Post(() =>
    {
        if (!IsDisposed)
        {
            Moved?.Invoke(this, EventArgs.Empty);
        }
    });

    /// <summary>A path's first name.</summary>
    private static ReadOnlySpan<char> First(ReadOnlySpan<char> parts)
    {
        int at = parts.IndexOfAny(PathNames.Separators);
        return at < 0 ? parts : parts[..at];
    }
}
