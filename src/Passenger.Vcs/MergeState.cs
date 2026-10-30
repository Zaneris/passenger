namespace Passenger.Vcs;

/// <summary>What git is in the middle of, stopped for its conflicts (a rebase also at a step of its list).</summary>
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
/// <see cref="Message"/> the commit message git wrote for finishing it, and <see cref="Stop"/> the step of a rebase's
/// list it stopped at, where it stopped for no conflicts.</summary>
public sealed record MergeState(MergeOperation Operation, string Ours, string? Theirs, string? Commit, string? Message, RebaseStop? Stop = null)
{
    public static MergeState Idle { get; } = new(MergeOperation.None, "HEAD", null, null, null);

    /// <summary>Whether a commit finishes the operation, which git makes only of the whole index.</summary>
    public bool CommitsWhole => Operation is MergeOperation.Merge or MergeOperation.CherryPick or MergeOperation.Revert;

    /// <summary>What is under way in a person's words, its conflicts resolved or not; null with nothing under way.</summary>
    public string? UnderWay => Operation switch
    {
        MergeOperation.Merge => $"Merging {Theirs} into {Ours}",
        MergeOperation.Rebase => Stop switch
        {
            { Step: RebaseStep.Edit } => $"Rebasing {Theirs} onto {Ours}, stopped to edit {Commit}",
            { Step: RebaseStep.Break } => $"Rebasing {Theirs} onto {Ours}, stopped at a break",
            { Step: RebaseStep.Exec } exec => $"Rebasing {Theirs} onto {Ours}, stopped after exec {exec.Command}",
            _ => Commit is null ? $"Rebasing {Theirs} onto {Ours}" : $"Rebasing {Theirs} onto {Ours}, stopped at {Commit}",
        },
        MergeOperation.CherryPick => $"Cherry-picking {Commit}",
        MergeOperation.Revert => $"Reverting {Commit}",
        _ => null,
    };

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

/// <summary>The steps of a rebase's list that stop it with no conflicts, handing the tree to the person.</summary>
public enum RebaseStep
{
    Edit,
    Break,
    Exec,
}

/// <summary>A step of an interactive rebase's list that stopped it with the tree handed to the person, who may commit
/// there (amend the commit to edit, or add commits of their own) before it goes on: an edit, a break, or an exec that
/// failed or left changes, <see cref="Command"/> its command.</summary>
public sealed record RebaseStop(RebaseStep Step, string? Command = null)
{
    /// <summary>The stop the last step a rebase did makes, read from its done list (git-rebase-todo's lines as it did
    /// them, a step's word or its letter first); null for any other step, which stops only for conflicts.</summary>
    public static RebaseStop? Of(string? done)
    {
        string? last = done?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        if (last is null)
        {
            return null;
        }
        int space = last.IndexOfAny([' ', '\t']);
        string word = space < 0 ? last : last[..space];
        return word switch
        {
            "edit" or "e" => new RebaseStop(RebaseStep.Edit),
            "break" or "b" => new RebaseStop(RebaseStep.Break),
            "exec" or "x" when space > 0 => new RebaseStop(RebaseStep.Exec, last[(space + 1)..].Trim()),
            _ => null,
        };
    }
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
