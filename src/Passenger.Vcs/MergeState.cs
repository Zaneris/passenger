namespace Passenger.Vcs;

/// <summary>What git is in the middle of, stopped for its conflicts.</summary>
public enum MergeOperation
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
}

/// <summary>What the working tree is in the middle of and the names git's two sides go by: <see cref="Ours"/> is HEAD's
/// side (the branch checked out; during a rebase the branch rebased onto), <see cref="Theirs"/> the side brought in
/// (the merged branch; the rebased one), <see cref="Commit"/> the commit a rebase, a cherry-pick or a revert stopped at,
/// and <see cref="Message"/> the commit message git wrote for finishing it.</summary>
public sealed record MergeState(MergeOperation Operation, string Ours, string? Theirs, string? Commit, string? Message)
{
    public static MergeState Idle { get; } = new(MergeOperation.None, "HEAD", null, null, null);

    /// <summary>Whether a commit finishes the operation, which git makes only of the whole index.</summary>
    public bool CommitsWhole => Operation is MergeOperation.Merge or MergeOperation.CherryPick or MergeOperation.Revert;

    /// <summary>The words the Conflicts dialog and the merge window use for the sides.
    /// <paramref name="stash"/> names the stash an unstash with conflicts applied, which git keeps no record of.</summary>
    public ConflictSides Sides(string? stash) => Operation switch
    {
        MergeOperation.Merge => new($"Merging branch {Theirs} into branch {Ours}", Ours, Theirs, $"Your version, branch {Ours}", $"Changes from branch {Theirs}", Swapped: false),
        // Rebasing, "yours" is the branch being rebased, which git calls theirs.
        MergeOperation.Rebase => new($"Rebasing branch {Theirs} onto branch {Ours}", Theirs, Ours,
            Commit is null ? $"Rebasing {Theirs}" : $"Rebasing {Commit} from {Theirs}", $"Already rebased commits and commits from {Ours}", Swapped: true),
        MergeOperation.CherryPick => new($"Conflicts during cherry-picking commit {Commit}", Ours, null, "Local Changes", $"Changes from cherry-pick {Commit}", Swapped: false),
        MergeOperation.Revert => new($"Conflicts during revert of commit {Commit}", Ours, null, "Local Changes", $"Changes from {Commit}", Swapped: false),
        _ when stash is not null => new($"Conflicts during unstashing {stash}", Ours, null, "Local changes", "Changes from stash", Swapped: false),
        _ => new("The following files have conflicts:", Ours, null, "Your version", "Their version", Swapped: false),
    };
}

/// <summary>A conflict's two sides as the person sees them: what is under way, the branch each column of the Conflicts
/// dialog names (Yours, Theirs), the merge window's titles over its left and right sides, and whether yours is git's
/// "theirs" (a rebase).</summary>
public sealed record ConflictSides(string Description, string? Yours, string? Theirs, string LeftTitle, string RightTitle, bool Swapped)
{
    public string YoursColumn => Yours is null ? "Yours" : $"Yours ({Yours})";

    public string TheirsColumn => Theirs is null ? "Theirs" : $"Theirs ({Theirs})";

    /// <summary>Whether yours has the file, git's ours or theirs as the sides are swapped.</summary>
    public bool YoursHas(GitConflict conflict) => Swapped ? conflict.HasTheirs : conflict.HasOurs;

    public bool TheirsHas(GitConflict conflict) => Swapped ? conflict.HasOurs : conflict.HasTheirs;
}
