namespace Passenger.Vcs;

/// <summary>What a stash's reflog subject says: the branch it was made on (null where it was made on a detached head or
/// git wrote another subject) and its message.</summary>
public sealed record StashSaid(string? Branch, string Message)
{
    /// <summary>A stash's subject as git writes it: "On main: message" for one given a message, "WIP on main: 1a2b3c4
    /// subject" for one made without, and "(no branch)" for a detached head's; any other kept whole as its message.</summary>
    public static StashSaid Of(string subject)
    {
        foreach (string lead in (string[])["WIP on ", "On "])
        {
            int colon = subject.IndexOf(": ", StringComparison.Ordinal);
            if (subject.StartsWith(lead, StringComparison.Ordinal) && colon > lead.Length)
            {
                string branch = subject[lead.Length..colon];
                return new StashSaid(branch == "(no branch)" ? null : branch, subject[(colon + 2)..]);
            }
        }
        return new StashSaid(null, subject);
    }
}

public sealed partial class Git
{
    /// <summary>The files a stash holds: those it changed against the head it was made on, renames found, then the new files
    /// it put away (its third parent's), each as unversioned.</summary>
    public async Task<IReadOnlyList<FileChange>> StashFilesAsync(StashInfo stash)
    {
        GitResult files = await RunAsync(Root, ["diff-tree", "--no-commit-id", "--name-status", "-r", "-z", "-M", "--end-of-options", $"{stash.Commit}^1", stash.Commit]);
        List<FileChange> held = files.Succeeded ? NameStatus(files.Output) : [];
        if (await CommitOfAsync($"{stash.Commit}^3") is { } untracked && await FilesOfAsync(untracked) is { } added)
        {
            DiskNames names = new(Root);
            held.AddRange(added.Select(path => new FileChange(names.Full(path), ChangeKind.Unversioned)));
        }
        return held;
    }

    /// <summary>Why files cannot be written out here as a revision has them: in a folder not trusted, one git stores through
    /// a filter, which runs only once the folder is trusted, in git's words; null where each can be.</summary>
    public async Task<string?> WhyNotWrittenOutAsync(IReadOnlyList<string> paths) => (await UnfilteredAsync([.. paths.Select(Relative)]))?.Message;

    /// <summary>A stash's commit put back at the top of the list under its message (git stash store), as a drop taken back
    /// does; refused where git no longer has the commit.</summary>
    public Task<GitResult> StoreStashAsync(string commit, string message) => RunAsync(Root, ["stash", "store", "-m", message, commit]);
}
