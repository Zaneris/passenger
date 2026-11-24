using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>A working tree of a repository as git lists it: its folder, the commit it has checked out and its branch (none
/// where detached), whether it is the repository's main one, and why it is locked or may be pruned (its folder gone),
/// where it is.</summary>
public sealed record WorktreeInfo(string Folder, string? Head, string? Branch, bool IsMain, string? Locked = null, string? Gone = null)
{
    /// <summary>The name it is shown by: its folder's.</summary>
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder));

    /// <summary>Its branch's short name (feature for refs/heads/feature), or null where detached.</summary>
    public string? BranchName => Branch is { } full && full.StartsWith("refs/heads/", StringComparison.Ordinal) ? full["refs/heads/".Length..] : Branch;
}

/// <summary>A new working tree to add: its folder, the branch it checks out, made from <paramref name="From"/> where it
/// is a new one (null for one that is there).</summary>
public sealed record WorktreePlan(string Folder, string Branch, string? From);

public sealed partial class Git
{
    /// <summary>The repository's working trees, the main one first, as git lists them.</summary>
    public async Task<IReadOnlyList<WorktreeInfo>> WorktreesAsync()
    {
        // Lines, not -z, which git 2.36 brought.
        GitResult listed = await RunAsync(Root, ["worktree", "list", "--porcelain"]);
        return listed.Succeeded ? Worktrees.Read(listed.Output) : [];
    }

    /// <summary>A working tree added as planned: an existing branch checked out in a new folder, or a new branch made from
    /// a revision and checked out there.</summary>
    public Task<GitResult> AddWorktreeAsync(WorktreePlan plan) => plan.From is { } from
        ? RunAsync(Root, ["worktree", "add", "-b", plan.Branch, "--end-of-options", plan.Folder, from])
        : RunAsync(Root, ["worktree", "add", "--end-of-options", plan.Folder, plan.Branch]);

    /// <summary>How many files a working tree has changed or new, as git status lists them (a rename once); null where git
    /// could not say.</summary>
    public async Task<int?> WorktreeChangesAsync(string folder)
    {
        GitResult status = await RunAsync(folder, ["status", "--porcelain", "-z", "--untracked-files=normal"]);
        if (!status.Succeeded)
        {
            return null;
        }
        string[] entries = status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int count = 0;
        for (int i = 0; i < entries.Length; i++, count++)
        {
            // A rename's or a copy's old name follows it as an entry of its own.
            if (entries[i].Length > 0 && entries[i][0] is 'R' or 'C')
            {
                i++;
            }
        }
        return count;
    }

    /// <summary>A working tree whose folder is gone forgotten by git: that one alone, where its git removes one gone, else
    /// with every other one gone (prune), as an older git does.</summary>
    public async Task<GitResult> ForgetWorktreeAsync(string folder)
    {
        GitResult removed = await RunAsync(Root, ["worktree", "remove", "--end-of-options", folder], answers: [128]);
        return removed.Succeeded || Directory.Exists(folder) ? removed : await RunAsync(Root, ["worktree", "prune"]);
    }
}

/// <summary>What git says of working trees, read apart from any git.</summary>
public static class Worktrees
{
    /// <summary>The working trees `git worktree list --porcelain` wrote: each a run of lines (worktree, HEAD, branch or
    /// detached, locked, prunable), ended by an empty one; the first is the main one.</summary>
    public static IReadOnlyList<WorktreeInfo> Read(string output)
    {
        List<WorktreeInfo> found = [];
        string? folder = null;
        string? head = null;
        string? branch = null;
        string? locked = null;
        string? gone = null;
        foreach (string line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0)
            {
                if (folder is not null)
                {
                    found.Add(new WorktreeInfo(folder, head, branch, found.Count == 0, locked, gone));
                }
                (folder, head, branch, locked, gone) = (null, null, null, null, null);
                continue;
            }
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string key = space < 0 ? line : line[..space];
            string value = space < 0 ? string.Empty : line[(space + 1)..];
            switch (key)
            {
                case "worktree":
                    folder = value;
                    break;
                case "HEAD":
                    head = value;
                    break;
                case "branch":
                    branch = value;
                    break;
                case "locked":
                    locked = value;
                    break;
                case "prunable":
                    gone = value.Length > 0 ? value : "its folder is gone";
                    break;
            }
        }
        if (folder is not null)
        {
            found.Add(new WorktreeInfo(folder, head, branch, found.Count == 0, locked, gone));
        }
        return found;
    }

    /// <summary>The folder a new working tree for a branch goes in by default: beside the repository's main one, named for
    /// the repository and the branch (passenger-feature-login for feature/login).</summary>
    public static string Beside(string main, string branch)
    {
        string top = Path.TrimEndingDirectorySeparator(main);
        string name = $"{Path.GetFileName(top)}-{FileNames.Safe(branch.Replace('/', '-'))}";
        return Path.Combine(Path.GetDirectoryName(top) ?? top, name);
    }
}
