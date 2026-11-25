using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>What a tab of Show Diff with Working Tree compares the working tree with: a branch or tag, by its ref, or a
/// stash, by its commit; its name as it was when the tab opened.</summary>
public sealed record WorkingTreeComparison(string Name, string Revision, StashInfo? Stash = null)
{
    public static WorkingTreeComparison Of(BranchInfo branch) => new(branch.Name, branch.Ref.FullName);

    public static WorkingTreeComparison Of(StashInfo stash) => new($"stash@{{{stash.Index}}}", stash.Commit, stash);

    /// <summary>The revision that holds a listed file as the comparison has it: a stash's files git did not track are in
    /// its third parent.</summary>
    public string RevisionOf(FileChange file) => Stash is not null && file.Kind == ChangeKind.Unversioned ? $"{Revision}^3" : Revision;

    /// <summary>What the tab says above its files.</summary>
    public string Explanation => Stash is { } stash
        ? $"Files that {Name} changes (\"{stash.Message}\"), each to compare with the working tree"
        : $"Files that differ between {Name} and the working tree";
}
