using System.Diagnostics;
using System.Text;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Vcs;

/// <summary>How a file differs from the last commit.</summary>
public enum ChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Unversioned,
    Conflicted,
}

/// <summary>A changed file of the working tree: its path (absolute), how it changed, and where it came from if renamed.</summary>
public sealed record FileChange(string Path, ChangeKind Kind, string? From = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>A commit as the log lists it: its hash and parents, who made it and when, the branches and tags at it,
/// and its subject.</summary>
public sealed record CommitInfo(string Hash, IReadOnlyList<string> Parents, string Author, string Email, DateTimeOffset Time, IReadOnlyList<string> Refs, string Subject)
{
    public string Short => Hash[..Math.Min(8, Hash.Length)];
}

/// <summary>What Show History is of: a file, followed through its renames; a folder; or a file's lines, from 0 as the last
/// commit numbers them (Show History for Selection).</summary>
public sealed record HistoryScope(string Path, bool IsFolder = false, int FirstLine = -1, int LastLine = -1)
{
    public bool IsLines => FirstLine >= 0;
}

/// <summary>A commit of a history and what it did there: the files it changed in the folder, or the file under the name
/// it had then.</summary>
public sealed record HistoryCommit(CommitInfo Commit, IReadOnlyList<FileChange> Changes);

/// <summary>What a branch or tag is, as Compare with Branch or Tag groups them.</summary>
public enum RefKind
{
    Local,
    Remote,
    Tag,
}

/// <summary>A branch or tag: its full name (refs/heads/main), which names it to git whatever else shares its short one,
/// and the short name it is shown by; and, where read, git's own short name for it (heads/main while a tag is named main
/// too), which is what git writes of it in a merge's message and conflict markers.</summary>
public sealed record GitRef(string FullName, string Name, RefKind Kind, string? GitName = null);

/// <summary>A branch or tag as the Git log's branches panel lists it: the commit it names, whether it is checked out, and
/// for a local branch the remote and the remote's branch it tracks (its upstream), if any.</summary>
public sealed record BranchInfo(GitRef Ref, string Hash, bool IsCurrent, string? UpstreamRemote = null, string? UpstreamBranch = null)
{
    public string Name => Ref.Name;

    public RefKind Kind => Ref.Kind;

    /// <summary>The upstream as the remote's branches are named (origin/main), or null.</summary>
    public string? Upstream => UpstreamRemote is null || UpstreamBranch is null ? null
        : $"{UpstreamRemote}/{(UpstreamBranch.StartsWith("refs/heads/", StringComparison.Ordinal) ? UpstreamBranch[11..] : UpstreamBranch)}";
}

/// <summary>A line as Git blame has it: the commit that last changed it (all zeros for a change not committed yet),
/// who made it and when, and its subject.</summary>
public sealed record BlameLine(string Hash, string Author, DateTimeOffset Time, string Summary)
{
    public bool IsCommitted => Hash.Any(c => c != '0');

    public string Short => Hash[..Math.Min(8, Hash.Length)];
}

/// <summary>What one status read found, together: the branch checked out (null on a detached head), the commit HEAD
/// names (null before the first), the changed and new files, and those of them in conflict.</summary>
public sealed record GitStatus(string? Branch, string? Head, IReadOnlyList<FileChange> Changes, IReadOnlyList<GitConflict> Conflicts);

/// <summary>A file in conflict and which of git's three versions of it there are: the one both sides came from (stage
/// 1), ours (stage 2, HEAD's side) and theirs (stage 3, the side brought in); a side without one deleted the file.</summary>
public sealed record GitConflict(string Path, bool HasBase, bool HasOurs, bool HasTheirs)
{
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Whether both sides have the file, so there is something to merge; else it is kept or deleted whole.</summary>
    public bool CanMerge => HasOurs && HasTheirs;
}

/// <summary>A stash: its place in the list (stash@{Index}), what it says, and when it was made.</summary>
public sealed record StashInfo(int Index, string Message, DateTimeOffset Time);

/// <summary>What `git` says, and whether it said it without failing.</summary>
public sealed record GitResult(int Code, string Output, string Error)
{
    public bool Succeeded => Code == 0;

    /// <summary>What to tell the person when it failed: git's own words.</summary>
    public string Message => (Error.Trim().Length > 0 ? Error : Output).Trim();
}

/// <summary>A Git working tree, through the `git` executable: its status, branches, a file as the last commit has it,
/// and committing, pushing and switching. Every call runs git in the tree's root, its output read as UTF-8, never
/// through a shell.</summary>
public sealed class Git
{
    private Git(string root, string gitDir)
    {
        Root = root;
        GitDir = gitDir;
    }

    /// <summary>The working tree's top folder.</summary>
    public string Root { get; }

    /// <summary>Git's own folder for the tree (.git, or a linked worktree's under it), where it keeps a merge's or a
    /// rebase's state.</summary>
    public string GitDir { get; }

    /// <summary>The working tree a folder is in, or null when it is in none (or git is not there).</summary>
    public static async Task<Git?> OpenAsync(string folder)
    {
        GitResult top = await RunAsync(folder, ["rev-parse", "--show-toplevel", "--absolute-git-dir"], answers: [NotThere]);
        string[] lines = top.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return top.Succeeded && lines.Length == 2
            ? new Git(PathNames.Canonical(lines[0]), PathNames.Canonical(lines[1]))
            : null;
    }

    /// <summary>Makes a folder a working tree (Create Git Repository).</summary>
    public static Task<GitResult> InitAsync(string folder) => RunAsync(folder, ["init"]);

    /// <summary>Clones a repository into a folder: each line git says of its progress
    /// goes to <paramref name="progress"/> as it is said, on another thread. Canceled, git is ended with all it started
    /// and the task is canceled; what it had made of the folder is the caller's to remove.</summary>
    public static async Task<GitResult> CloneAsync(string url, string destination, Action<string> progress, CancellationToken cancel)
    {
        // The clone opens next: a screenshot waits for it.
        using IDisposable held = BackgroundWork.Scope();
        string parent = System.IO.Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        // After "--" a URL that begins with a dash is a URL, never an option such as --upload-pack.
        if (Start(parent, ["clone", "--progress", "--", url, destination]) is not { } start)
        {
            return NotInstalled;
        }
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process process;
        try
        {
            process = await ChildProcesses.StartAsync(start);
        }
        catch (System.ComponentModel.Win32Exception missing)
        {
            return new GitResult(127, string.Empty, $"git is not installed: {missing.Message}");
        }
        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> said = LinesAsync(process.StandardError, progress);
            using (cancel.Register(() => ChildProcesses.Kill(process)))
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            string errors = await said;
            cancel.ThrowIfCancellationRequested();
            return new GitResult(process.ExitCode, await output, errors);
        }
    }

    /// <summary>What git writes to its errors, each line handed on as it ends, and the lines it finished, whole: progress
    /// writes its line again and again after a carriage return, which ends a line handed on but not one kept.</summary>
    private static async Task<string> LinesAsync(StreamReader errors, Action<string> line)
    {
        StringBuilder kept = new();
        StringBuilder current = new();
        char[] buffer = new char[1024];
        int read;
        while ((read = await errors.ReadAsync(buffer)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c is not ('\r' or '\n'))
                {
                    current.Append(c);
                    continue;
                }
                if (current.Length > 0)
                {
                    line(current.ToString());
                    if (c == '\n')
                    {
                        kept.AppendLine(current.ToString());
                    }
                }
                current.Clear();
            }
        }
        if (current.Length > 0)
        {
            line(current.ToString());
            kept.AppendLine(current.ToString());
        }
        return kept.ToString();
    }

    /// <summary>The branch, HEAD's commit, and the changed and unversioned files (ignored ones and a save's temporary file
    /// left out, renames with where they came from), in one git so the three agree.</summary>
    public async Task<GitStatus> StatusAsync()
    {
        GitResult status = await RunAsync(Root, ["status", "--porcelain=v2", "-z", "--branch", "--no-ahead-behind", "--untracked-files=all"]);
        if (!status.Succeeded)
        {
            throw new InvalidOperationException(status.Message);
        }
        string? branch = null;
        string? head = null;
        List<FileChange> changes = [];
        List<GitConflict> conflicts = [];
        string[] entries = status.Output.Split('\0');
        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            if (entry.Length < 2)
            {
                continue;
            }
            switch (entry[0])
            {
                case '#' when entry.StartsWith("# branch.oid ", StringComparison.Ordinal):
                    head = entry[13..] is "(initial)" ? null : entry[13..];
                    break;
                case '#' when entry.StartsWith("# branch.head ", StringComparison.Ordinal):
                    branch = entry[14..] is "(detached)" ? null : entry[14..];
                    break;
                case '1':
                    // "1 XY sub mH mI mW hH hI path"
                    string[] ordinary = entry.Split(' ', 9);
                    changes.Add(new FileChange(Full(ordinary[8]), Kind(ordinary[1])));
                    break;
                case '2':
                    // "2 XY sub mH mI mW hH hI Xscore path", then the path it came from as its own entry.
                    string[] renamed = entry.Split(' ', 10);
                    changes.Add(new FileChange(Full(renamed[9]), ChangeKind.Renamed, Full(entries[++i])));
                    break;
                case 'u':
                    // "u XY sub m1 m2 m3 mW h1 h2 h3 path": a stage git has no version at has the mode 000000.
                    string[] unmerged = entry.Split(' ', 11);
                    string path = Full(unmerged[10]);
                    changes.Add(new FileChange(path, ChangeKind.Conflicted));
                    conflicts.Add(new GitConflict(path, Staged(unmerged[3]), Staged(unmerged[4]), Staged(unmerged[5])));
                    break;
                // A save under way as git read the folder: the saved file's change, which the read after the save lists.
                case '?' when FileText.IsSaving(entry):
                    break;
                case '?':
                    changes.Add(new FileChange(Full(entry[2..]), ChangeKind.Unversioned));
                    break;
            }
        }
        return new GitStatus(branch, head, changes, conflicts);

        static ChangeKind Kind(string xy) =>
            xy.Contains('D') ? ChangeKind.Deleted : xy[0] == 'A' ? ChangeKind.Added : ChangeKind.Modified;

        static bool Staged(string mode) => mode.Any(c => c != '0');
    }

    /// <summary>What the tree is in the middle of (a merge, a rebase, a cherry-pick or a revert stopped for its
    /// conflicts), read from git's own files, and the names its two sides go by. <paramref name="branch"/> and
    /// <paramref name="head"/> are the status's, ours outside a rebase.</summary>
    public async Task<MergeState> MergeStateAsync(string? branch, string? head)
    {
        string? message = Read("MERGE_MSG");
        string ours = branch ?? Short(head) ?? "HEAD";
        if (Read("MERGE_HEAD") is { } merging)
        {
            return new MergeState(MergeOperation.Merge, ours, await NameAsync(merging), null, Message(message));
        }
        foreach (string folder in (string[])["rebase-merge", "rebase-apply"])
        {
            if (Read(System.IO.Path.Combine(folder, "head-name")) is not { } rebased)
            {
                continue;
            }
            string theirs = rebased.StartsWith("refs/heads/", StringComparison.Ordinal) ? rebased[11..] : "detached HEAD";
            string? onto = Read(System.IO.Path.Combine(folder, "onto"));
            string? applying = Read("REBASE_HEAD") ?? Read(System.IO.Path.Combine(folder, "stopped-sha"));
            return new MergeState(MergeOperation.Rebase, onto is null ? ours : await NameAsync(onto), theirs, Short(applying), null);
        }
        if (Read("CHERRY_PICK_HEAD") is { } picked)
        {
            return new MergeState(MergeOperation.CherryPick, ours, null, Short(picked), Message(message));
        }
        if (Read("REVERT_HEAD") is { } reverted)
        {
            return new MergeState(MergeOperation.Revert, ours, null, Short(reverted), Message(message));
        }
        return new MergeState(MergeOperation.None, ours, null, null, null);

        // Git's message for the merge less its comment lines (the conflicts), which a commit from a file would keep.
        static string? Message(string? written) => written is null ? null
            : string.Join('\n', written.Split('\n').Where(l => !l.StartsWith('#'))).Trim() is { Length: > 0 } kept ? kept : null;
    }

    /// <summary>A file of git's own folder, trimmed, or null when there is none.</summary>
    private string? Read(string name)
    {
        string path = System.IO.Path.Combine(GitDir, name);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            // Being written as git finishes: the next read sees it.
            return null;
        }
    }

    /// <summary>A commit by the branch at it (a local one first, then a remote's), else its first eight characters; the
    /// last one named is kept, since every status read while a merge is under way asks for it again.</summary>
    private async Task<string> NameAsync(string hash)
    {
        if (named is { } last && last.Hash == hash)
        {
            return last.Name;
        }
        GitResult at = await RunAsync(Root, ["for-each-ref", $"--points-at={hash}", "--format=%(refname)", "refs/heads", "refs/remotes"]);
        string[] refs = at.Succeeded ? at.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
        string name = refs.FirstOrDefault(r => r.StartsWith("refs/heads/", StringComparison.Ordinal))?[11..]
            ?? refs.FirstOrDefault(r => r.StartsWith("refs/remotes/", StringComparison.Ordinal) && !r.EndsWith("/HEAD", StringComparison.Ordinal))?[13..]
            ?? Short(hash)!;
        named = (hash, name);
        return name;
    }

    private (string Hash, string Name)? named;

    private static string? Short(string? hash) => hash is null ? null : hash[..Math.Min(8, hash.Length)];

    /// <summary>A conflicted file's three versions as git keeps them while it is in conflict, each as its bytes would be
    /// checked out (the working tree's filters and line breaks, CRLF under core.autocrlf): the one both sides came from,
    /// ours and theirs, each null where that side has none. The bytes are the caller's to read in the file's encoding.</summary>
    public async Task<(byte[]? Base, byte[]? Ours, byte[]? Theirs)> StagesAsync(string path)
    {
        Task<byte[]?> common = StageAsync(1, path);
        Task<byte[]?> ours = StageAsync(2, path);
        Task<byte[]?> theirs = StageAsync(3, path);
        return (await common, await ours, await theirs);
    }

    /// <summary>One stage of a conflicted file as checking it out would write it, or as git keeps it when a filter it names
    /// cannot run here (git-lfs not installed); null when the stage has none.</summary>
    private async Task<byte[]?> StageAsync(int stage, string path)
    {
        string name = $":{stage}:{Relative(path)}";
        return await BytesAsync(Root, ["cat-file", "--filters", name]) ?? await BytesAsync(Root, ["cat-file", "blob", name]);
    }

    /// <summary>What a git command writes, as bytes, or null when it fails (128 for something not there, its answer).</summary>
    private static async Task<byte[]?> BytesAsync(string folder, IReadOnlyList<string> arguments)
    {
        using IDisposable held = BackgroundWork.Scope();
        if (Start(folder, arguments) is not { } start)
        {
            return null;
        }
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        try
        {
            using Process process = await ChildProcesses.StartAsync(start);
            using MemoryStream output = new();
            Task copied = process.StandardOutput.BaseStream.CopyToAsync(output);
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await copied;
            await error;
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
        catch (Exception failed) when (failed is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    /// <summary>Accept Yours or Accept Theirs over conflicted files: each takes that side's version (ours, or theirs with
    /// <paramref name="theirs"/>, in git's own terms), or is deleted where that side deleted it, and is marked resolved.</summary>
    public async Task<GitResult> AcceptAsync(IReadOnlyList<GitConflict> conflicts, bool theirs)
    {
        string[] kept = [.. conflicts.Where(c => theirs ? c.HasTheirs : c.HasOurs).Select(c => Literal(Relative(c.Path)))];
        string[] deleted = [.. conflicts.Where(c => !(theirs ? c.HasTheirs : c.HasOurs)).Select(c => Literal(Relative(c.Path)))];
        if (kept.Length > 0)
        {
            GitResult taken = await RunAsync(Root, ["checkout", theirs ? "--theirs" : "--ours", "--", .. kept]);
            if (!taken.Succeeded)
            {
                return taken;
            }
            GitResult added = await RunAsync(Root, ["add", "--", .. kept]);
            if (!added.Succeeded)
            {
                return added;
            }
        }
        return deleted.Length > 0 ? await RunAsync(Root, ["rm", "-q", "-f", "--", .. deleted]) : new GitResult(0, string.Empty, string.Empty);
    }

    /// <summary>Marks a conflicted file resolved with what is on disk now (the merge's result written there).</summary>
    public Task<GitResult> MarkResolvedAsync(string path) => RunAsync(Root, ["add", "--", Literal(Relative(path))]);

    /// <summary>Abort Merge, Abort Rebase and the like: the tree back as it was before the operation began.</summary>
    public Task<GitResult> AbortAsync(MergeOperation operation) => RunAsync(Root, [operation switch
    {
        MergeOperation.Merge => "merge",
        MergeOperation.Rebase => "rebase",
        MergeOperation.CherryPick => "cherry-pick",
        MergeOperation.Revert => "revert",
        _ => throw new ArgumentException("Nothing is under way to abort", nameof(operation)),
    }, "--abort"]);

    /// <summary>Continue Rebase: the resolved commit made with the message it had, and the rest replayed.</summary>
    public Task<GitResult> ContinueRebaseAsync() =>
        RunAsync(Root, ["rebase", "--continue"], environment: new Dictionary<string, string> { ["GIT_EDITOR"] = "true" });

    /// <summary>The branches, remotes and tags the log shows and the commit each names: with HEAD's commit, what changes
    /// when the history does (a commit, a checkout, a fetch), and not when a file is saved or stashed.</summary>
    public async Task<string> RefsAsync()
    {
        GitResult refs = await RunAsync(Root, ["for-each-ref", "--format=%(objectname) %(refname)", "refs/heads", "refs/remotes", "refs/tags"]);
        return refs.Output;
    }

    /// <summary>The branch checked out, or null on a detached head.</summary>
    public async Task<string?> BranchAsync()
    {
        GitResult branch = await RunAsync(Root, ["branch", "--show-current"]);
        return branch.Succeeded && branch.Output.Trim() is { Length: > 0 } name ? name : null;
    }

    /// <summary>The local branches, the current one first.</summary>
    public async Task<IReadOnlyList<string>> BranchesAsync()
    {
        GitResult branches = await RunAsync(Root, ["for-each-ref", "--format=%(HEAD)%(refname:short)", "refs/heads"]);
        if (!branches.Succeeded)
        {
            return [];
        }
        List<string> lines = [.. branches.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        return [.. lines.Where(l => l.StartsWith('*')).Select(l => l[1..]), .. lines.Where(l => !l.StartsWith('*')).Select(l => l.TrimStart(' '))];
    }

    /// <summary>The local branches (the current one first), the remote ones (less each remote's HEAD) and the tags, as
    /// Compare with Branch or Tag lists them.</summary>
    public async Task<IReadOnlyList<GitRef>> RefNamesAsync()
    {
        GitResult refs = await RunAsync(Root, ["for-each-ref", "--format=%(HEAD)%(refname)", "refs/heads", "refs/remotes", "refs/tags"]);
        if (!refs.Succeeded)
        {
            return [];
        }
        List<(bool Current, GitRef Ref)> found = [];
        foreach (string line in refs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string full = line[1..];
            GitRef? named = full switch
            {
                _ when full.StartsWith("refs/heads/", StringComparison.Ordinal) => new GitRef(full, full[11..], RefKind.Local),
                _ when full.StartsWith("refs/remotes/", StringComparison.Ordinal) && !full.EndsWith("/HEAD", StringComparison.Ordinal) => new GitRef(full, full[13..], RefKind.Remote),
                _ when full.StartsWith("refs/tags/", StringComparison.Ordinal) => new GitRef(full, full[10..], RefKind.Tag),
                _ => null,
            };
            if (named is not null)
            {
                found.Add((line[0] == '*', named));
            }
        }
        return [.. found.OrderBy(f => f.Ref.Kind).ThenBy(f => !f.Current).Select(f => f.Ref)];
    }

    /// <summary>The branches, remotes' branches (less each remote's HEAD) and tags as the Git log's branches panel lists
    /// them, each with the commit it names (a tag's, peeled), the current branch marked and each local branch's upstream;
    /// and the remotes, which a remote branch's name begins with.</summary>
    public async Task<(IReadOnlyList<BranchInfo> Branches, IReadOnlyList<string> Remotes)> BranchTreeAsync()
    {
        GitResult refs = await RunAsync(Root, ["for-each-ref",
            "--format=%(HEAD)%1f%(refname)%1f%(objectname)%1f%(*objectname)%1f%(upstream:remotename)%1f%(upstream:remoteref)%1f%(refname:short)",
            "refs/heads", "refs/remotes", "refs/tags"]);
        GitResult remotes = await RunAsync(Root, ["remote"]);
        List<BranchInfo> found = [];
        foreach (string line in refs.Succeeded ? refs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [])
        {
            string[] field = line.Split('\x1f');
            if (field.Length < 7)
            {
                continue;
            }
            string full = field[1];
            string? own = field[6].Length > 0 ? field[6] : null;
            GitRef? named = full switch
            {
                _ when full.StartsWith("refs/heads/", StringComparison.Ordinal) => new GitRef(full, full[11..], RefKind.Local, own),
                _ when full.StartsWith("refs/remotes/", StringComparison.Ordinal) && !full.EndsWith("/HEAD", StringComparison.Ordinal) => new GitRef(full, full[13..], RefKind.Remote, own),
                _ when full.StartsWith("refs/tags/", StringComparison.Ordinal) => new GitRef(full, full[10..], RefKind.Tag, own),
                _ => null,
            };
            if (named is not null)
            {
                found.Add(new BranchInfo(named, field[3].Length > 0 ? field[3] : field[2], field[0] == "*",
                    field[4].Length > 0 ? field[4] : null, field[5].Length > 0 ? field[5] : null));
            }
        }
        return (found, remotes.Succeeded ? remotes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : []);
    }

    /// <summary>The history of every branch, remote and tag and of HEAD, newest first with a branch's commits together, at
    /// most this many; or, given <paramref name="revisions"/>, theirs alone (a branch the log is filtered to,
    /// "main..feature" for the commits of feature main lacks). Not --all, which takes in the stash's own commits (and
    /// notes').</summary>
    public async Task<IReadOnlyList<CommitInfo>> LogAsync(int most = 2000, IReadOnlyList<string>? revisions = null)
    {
        // An unborn HEAD (a new repository, an orphan branch) is left out rather than failing the whole log.
        string[] which = revisions is null ? ["--branches", "--remotes", "--tags", "--ignore-missing", "HEAD"] : ["--end-of-options", .. revisions];
        GitResult log = await RunAsync(Root, ["log", "--topo-order", $"--max-count={most}", CommitFormat, .. which, "--"]);
        // A branch with no commits yet has no history to read.
        return log.Succeeded ? Commits(log.Output) : [];
    }

    /// <summary>The commit HEAD names, as the log reads it, or null before the first commit.</summary>
    public async Task<CommitInfo?> HeadAsync()
    {
        GitResult log = await RunAsync(Root, ["log", "-1", CommitFormat, "HEAD", "--"], answers: [NotThere]);
        return log.Succeeded ? Commits(log.Output).FirstOrDefault() : null;
    }

    private const string CommitFormat = "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s%x1e";

    /// <summary>A history's commits: each begins a record, and what follows its last field is what it did there.</summary>
    private const string HistoryFormat = "--format=%x1e%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s%x1f";

    private static List<CommitInfo> Commits(string output)
    {
        List<CommitInfo> commits = [];
        foreach (string record in output.Split('\x1e'))
        {
            string[] field = record.Trim('\n').Split('\x1f');
            if (field.Length >= 7)
            {
                commits.Add(Commit(field));
            }
        }
        return commits;
    }

    private static CommitInfo Commit(string[] field) => new(
        field[0],
        field[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
        field[2],
        field[3],
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(field[4], System.Globalization.CultureInfo.InvariantCulture)),
        [.. field[5].Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(r => r.StartsWith("HEAD -> ", StringComparison.Ordinal) ? r[8..] : r)],
        field[6]);

    /// <summary>Show History's commits from HEAD, newest first, at most this many, each with what it did there: a file's
    /// followed through its renames (--follow), a folder's, or those that changed a file's lines (-L), renames followed
    /// too. Empty where git has none: a file it does not track, lines past the end of the last commit's.</summary>
    public async Task<IReadOnlyList<HistoryCommit>> HistoryAsync(HistoryScope scope, int most = 2000)
    {
        string relative = Relative(scope.Path);
        string count = $"--max-count={most}";
        if (scope.IsLines)
        {
            // -L takes no pathspec: its file is whatever follows the range. The prefixes are set, whatever diff.noprefix says.
            GitResult lines = await RunAsync(Root, ["-c", "core.quotePath=false", "log", $"-L{scope.FirstLine + 1},{scope.LastLine + 1}:{relative}",
                "--no-color", "--src-prefix=a/", "--dst-prefix=b/", HistoryFormat, count, "HEAD"]);
            return lines.Succeeded ? Histories(lines.Output, PatchFile) : [];
        }
        // Literal: a name with brackets or a star is that file alone, never a pattern.
        GitResult log = await RunAsync(Root, ["--literal-pathspecs", "log", .. scope.IsFolder ? Array.Empty<string>() : ["--follow"],
            "--name-status", "-z", "-M", HistoryFormat, count, "HEAD", "--", relative]);
        return log.Succeeded ? Histories(log.Output, NameStatus) : [];
    }

    private static List<HistoryCommit> Histories(string output, Func<string, List<FileChange>> changes)
    {
        List<HistoryCommit> commits = [];
        foreach (string record in output.Split('\x1e'))
        {
            string[] field = record.Split('\x1f', 8);
            if (field.Length == 8)
            {
                commits.Add(new HistoryCommit(Commit(field), changes(field[7])));
            }
        }
        return commits;
    }

    /// <summary>Files as --name-status -z lists them: a status, then the path, or a rename's or copy's two.</summary>
    private List<FileChange> NameStatus(string output)
    {
        List<FileChange> changes = [];
        string[] parts = output.Split('\0');
        for (int i = 0; i + 1 < parts.Length; i++)
        {
            // A log writes a line break between a commit and its files.
            string status = parts[i].TrimStart('\n');
            if (status.Length == 0)
            {
                continue;
            }
            switch (status[0])
            {
                case 'R' or 'C' when i + 2 < parts.Length:
                    changes.Add(new FileChange(Full(parts[i + 2]), ChangeKind.Renamed, Full(parts[i + 1])));
                    i += 2;
                    break;
                case 'A':
                    changes.Add(new FileChange(Full(parts[++i]), ChangeKind.Added));
                    break;
                case 'D':
                    changes.Add(new FileChange(Full(parts[++i]), ChangeKind.Deleted));
                    break;
                default:
                    changes.Add(new FileChange(Full(parts[++i]), ChangeKind.Modified));
                    break;
            }
        }
        return changes;
    }

    /// <summary>The file a line history's commit changed, from its patch's head: "--- a/" the name before (or /dev/null
    /// where the lines began) and "+++ b/" the name it has in the commit.</summary>
    private List<FileChange> PatchFile(string patch)
    {
        string? before = null;
        string? after = null;
        foreach (string line in patch.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                break;
            }
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                before = line[4..];
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                after = line[4..];
            }
        }
        if (Unprefixed(after) is not { } path)
        {
            return [];
        }
        return
        [
            before == "/dev/null" ? new FileChange(Full(path), ChangeKind.Added)
                : Unprefixed(before) is { } from && from != path ? new FileChange(Full(path), ChangeKind.Renamed, Full(from))
                : new FileChange(Full(path), ChangeKind.Modified),
        ];
    }

    /// <summary>A patch head's name without its "a/" or "b/": unquoted where git quoted it (a name with a quote, a backslash
    /// or a control character), less the tab git ends a name with a space with.</summary>
    private static string? Unprefixed(string? name)
    {
        if (name is null)
        {
            return null;
        }
        if (name.EndsWith('\t'))
        {
            name = name[..^1];
        }
        if (name.Length > 1 && name[0] == '"' && name[^1] == '"')
        {
            name = Unquoted(name[1..^1]);
        }
        return name.Length > 2 && name[1] == '/' ? name[2..] : null;
    }

    /// <summary>A name git quoted as C does, its octal escapes being the bytes of its UTF-8.</summary>
    private static string Unquoted(string quoted)
    {
        List<byte> bytes = [];
        for (int i = 0; i < quoted.Length; i++)
        {
            int escape = quoted.IndexOf('\\', i);
            if (escape < 0 || escape + 1 == quoted.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(quoted[i..]));
                break;
            }
            bytes.AddRange(Encoding.UTF8.GetBytes(quoted[i..escape]));
            i = escape + 1;
            char escaped = quoted[i];
            if (escaped is >= '0' and <= '7' && i + 2 < quoted.Length)
            {
                bytes.Add(Convert.ToByte(quoted.Substring(i, 3), 8));
                i += 2;
                continue;
            }
            bytes.Add(escaped switch
            {
                'a' => 7,
                'b' => 8,
                't' => 9,
                'n' => 10,
                'v' => 11,
                'f' => 12,
                'r' => 13,
                _ => (byte)escaped,
            });
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>A commit's whole message.</summary>
    public async Task<string> MessageAsync(string hash)
    {
        GitResult show = await RunAsync(Root, ["show", "-s", "--format=%B", "--end-of-options", hash]);
        return show.Succeeded ? show.Output.TrimEnd() : string.Empty;
    }

    /// <summary>The files a commit changed against its first parent (all of them for the first commit), renames found.</summary>
    public async Task<IReadOnlyList<FileChange>> CommitFilesAsync(string hash)
    {
        GitResult files = await RunAsync(Root, ["diff-tree", "--no-commit-id", "--name-status", "-r", "-z", "-M", "--root", "--end-of-options", hash]);
        return files.Succeeded ? NameStatus(files.Output) : [];
    }

    /// <summary>Git blame for a file, line by line, over the text given (the editor's, unsaved changes and all), so a
    /// line typed since is one not committed yet; null for a file Git does not track.</summary>
    public async Task<IReadOnlyList<BlameLine>?> BlameAsync(string path, string text)
    {
        GitResult blame = await RunAsync(Root, ["blame", "--porcelain", "--contents", "-", "--", Relative(path)], text);
        if (!blame.Succeeded)
        {
            return null;
        }
        Dictionary<string, (string Author, DateTimeOffset Time, string Summary)> commits = [];
        List<BlameLine> lines = [];
        string? hash = null;
        (string Author, long Time, string Summary) reading = (string.Empty, 0, string.Empty);
        foreach (string line in blame.Output.Split('\n'))
        {
            if (line.StartsWith('\t'))
            {
                // The line itself: the commit's fields are all read by now.
                if (hash is not null)
                {
                    if (!commits.TryGetValue(hash, out (string Author, DateTimeOffset Time, string Summary) commit))
                    {
                        commits[hash] = commit = (reading.Author, DateTimeOffset.FromUnixTimeSeconds(reading.Time), reading.Summary);
                    }
                    lines.Add(new BlameLine(hash, commit.Author, commit.Time, commit.Summary));
                }
                continue;
            }
            string[] words = line.Split(' ', 2);
            if (words[0].Length == 40 && words[0].All(char.IsAsciiHexDigit))
            {
                hash = words[0];
                reading = (string.Empty, 0, string.Empty);
                continue;
            }
            string value = words.Length > 1 ? words[1] : string.Empty;
            switch (words[0])
            {
                case "author":
                    reading.Author = value;
                    break;
                case "author-time" when long.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out long seconds):
                    reading.Time = seconds;
                    break;
                case "summary":
                    reading.Summary = value;
                    break;
            }
        }
        return lines;
    }

    /// <summary>A file's text at a revision (a hash, a branch, HEAD), or null when it had no such file.</summary>
    public async Task<string?> TextAtAsync(string revision, string path)
    {
        // Its 128 for a file the revision lacks (untracked, ignored, new) is its answer, asked of every such file opened.
        GitResult show = await RunAsync(Root, ["show", "--end-of-options", $"{revision}:{Relative(path)}"], answers: [NotThere]);
        return show.Succeeded ? show.Output : null;
    }

    /// <summary>Files' texts at a revision, read by one git however many there are (cat-file's batch), each null where
    /// the revision has no such file, as <see cref="TextAtAsync"/> would read them one by one.</summary>
    public async Task<IReadOnlyDictionary<string, string?>> TextsAtAsync(string revision, IReadOnlyList<string> paths)
    {
        Dictionary<string, string?> texts = [];
        List<string> batched = [];
        foreach (string path in paths.Distinct())
        {
            if (!IsInside(path))
            {
                // A path outside the tree ends the whole batch ("outside repository").
                texts[path] = null;
            }
            else if (path.Contains('\n') || path.Contains('\r'))
            {
                // The batch takes a name per line.
                texts[path] = await TextAtAsync(revision, path);
            }
            else
            {
                batched.Add(path);
            }
        }
        if (batched.Count == 0)
        {
            return texts;
        }
        byte[] output = await OutputAsync(Root, ["cat-file", "--batch"], string.Concat(batched.Select(p => $"{revision}:{Relative(p)}\n")));
        int at = 0;
        foreach (string path in batched)
        {
            texts[path] = null;
            int end = Array.IndexOf(output, (byte)'\n', at);
            if (end < 0)
            {
                continue;
            }
            // "<object> <type> <size>", the object's bytes and a line break; or "<name> missing".
            string[] header = Encoding.UTF8.GetString(output, at, end - at).Split(' ');
            at = end + 1;
            if (header.Length == 3 && header[0].All(char.IsAsciiHexDigit) && int.TryParse(header[2], System.Globalization.CultureInfo.InvariantCulture, out int size)
                && size <= output.Length - at)
            {
                if (header[1] == "blob")
                {
                    // Read as RunAsync reads git's output: UTF-8, a byte order mark taken as the file's.
                    using StreamReader reader = new(new MemoryStream(output, at, size, writable: false), Encoding.UTF8);
                    texts[path] = reader.ReadToEnd();
                }
                at += size + 1;
            }
        }
        return texts;
    }

    /// <summary>Whether a path is in the working tree, where git has a last commit's text for it.</summary>
    public bool IsInside(string path) => PathNames.Within(path, Root);

    /// <summary>What the index has at a path (a file, or a folder's files) moved to where the file now is, as git mv
    /// moves it with the file: each keeps its staged content under the new name, so the status shows the move as a
    /// rename and the change the file has beyond that as its own. A path the index does not have, or has in conflict,
    /// is left alone.</summary>
    public async Task<GitResult> MoveInIndexAsync(string from, string to)
    {
        if (!IsInside(from) || !IsInside(to))
        {
            return new GitResult(0, string.Empty, string.Empty);
        }
        string source = Relative(from);
        string target = Relative(to);
        GitResult staged = await RunAsync(Root, ["ls-files", "--stage", "-z", "--", Literal(source)]);
        if (!staged.Succeeded)
        {
            return staged;
        }
        StringBuilder moves = new();
        foreach (string entry in staged.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> <object> <stage>\t<path>"
            int tab = entry.IndexOf('\t', StringComparison.Ordinal);
            string[] fields = tab < 0 ? [] : entry[..tab].Split(' ');
            if (fields.Length != 3)
            {
                continue;
            }
            string path = entry[(tab + 1)..];
            if (fields[2] != "0")
            {
                return new GitResult(0, string.Empty, string.Empty);
            }
            // Mode 0 takes the old path out of the index, as update-index --index-info reads it.
            moves.Append(System.Globalization.CultureInfo.InvariantCulture, $"0 {new string('0', fields[1].Length)}\t{path}\0");
            moves.Append(System.Globalization.CultureInfo.InvariantCulture, $"{fields[0]} {fields[1]}\t{target}{path[source.Length..]}\0");
        }
        return moves.Length == 0 ? staged : await RunAsync(Root, ["update-index", "-z", "--index-info"], moves.ToString());
    }

    /// <summary>Update Project: the branch's upstream merged in (or rebased onto, as the person's pull.rebase
    /// says).</summary>
    public Task<GitResult> PullAsync() => RunAsync(Root, ["pull", "--no-edit"]);

    public Task<GitResult> FetchAsync() => RunAsync(Root, ["fetch", "--all", "--prune"]);

    /// <summary>The last commit's message, or null before the first commit.</summary>
    public async Task<string?> LastMessageAsync()
    {
        GitResult log = await RunAsync(Root, ["log", "-1", "--format=%B"], answers: [NotThere]);
        return log.Succeeded ? log.Output.TrimEnd() : null;
    }

    /// <summary>Commits these files' changes (new ones added first) with a message, amending the last commit when asked.
    /// Finishing a merge (or a cherry-pick or a revert stopped for conflicts), which git commits only whole, the files
    /// <paramref name="excluded"/> are taken out of it and kept as changes.</summary>
    public async Task<GitResult> CommitAsync(IReadOnlyList<string> paths, string message, bool amend = false, bool merging = false, IReadOnlyList<string>? excluded = null)
    {
        string[] relative = [.. paths.Select(Relative).Distinct()];
        // No paths is an amend of the message alone: "add -A --" with none would add the whole tree.
        if (relative.Length > 0)
        {
            GitResult add = await RunAsync(Root, ["add", "-A", "--", .. relative.Select(Literal)]);
            if (!add.Succeeded)
            {
                return add;
            }
        }
        string[] left = merging ? [.. (excluded ?? []).Select(Relative).Distinct().Except(relative)] : [];
        if (left.Length > 0)
        {
            GitResult unstaged = await RunAsync(Root, ["reset", "-q", "--", .. left.Select(Literal)]);
            if (!unstaged.Succeeded)
            {
                return unstaged;
            }
        }
        string file = System.IO.Path.GetTempFileName();
        AtomicFile.Write(file, Encoding.UTF8.GetBytes(message), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            string[] only = relative.Length > 0 && !merging ? ["--only", "--", .. relative.Select(Literal)] : [];
            return await RunAsync(Root, ["commit", "-F", file, .. amend ? new[] { "--amend" } : [], .. only]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Pushes the branch, setting its upstream on the first push.</summary>
    public async Task<GitResult> PushAsync()
    {
        GitResult upstream = await RunAsync(Root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], answers: [NotThere]);
        if (upstream.Succeeded)
        {
            return await RunAsync(Root, ["push"]);
        }
        return await BranchAsync() is { } branch
            ? await RunAsync(Root, ["push", "-u", "origin", branch])
            : new GitResult(1, string.Empty, "A detached head has no branch to push");
    }

    /// <summary>Checks a branch out. What follows --end-of-options (here and wherever a name or a hash comes from outside) is
    /// a name, never an option, whoever gave it: an agent's "--orphan=x" is a branch git does not have, not an order to
    /// empty the working tree.</summary>
    public Task<GitResult> CheckoutAsync(string branch) => RunAsync(Root, ["switch", "--end-of-options", branch]);

    /// <summary>A remote's branch checked out as a local branch of the same name that tracks it.</summary>
    public Task<GitResult> CheckoutTrackingAsync(string remoteBranch) => RunAsync(Root, ["switch", "--track", "--end-of-options", remoteBranch]);

    /// <summary>Another branch merged into the current one, git's own message for the merge commit.</summary>
    public Task<GitResult> MergeAsync(string branch) => RunAsync(Root, ["merge", "--no-edit", "--end-of-options", branch]);

    /// <summary>The current branch rebased onto another; or, given <paramref name="branch"/>, that branch checked out and
    /// rebased onto <paramref name="onto"/> (Checkout and Rebase onto Current, "git rebase HEAD branch").</summary>
    public Task<GitResult> RebaseAsync(string onto, string? branch = null) =>
        RunAsync(Root, ["rebase", "--end-of-options", onto, .. branch is null ? Array.Empty<string>() : [branch]],
            environment: new Dictionary<string, string> { ["GIT_EDITOR"] = "true" });

    public Task<GitResult> RenameBranchAsync(string from, string to) => RunAsync(Root, ["branch", "-m", "--end-of-options", from, to]);

    /// <summary>A local branch deleted; one not merged into HEAD (or its upstream) only when <paramref name="force"/>.</summary>
    public Task<GitResult> DeleteBranchAsync(string name, bool force) => RunAsync(Root, ["branch", force ? "-D" : "-d", "--end-of-options", name]);

    /// <summary>A remote's branch deleted on the remote, and its remote-tracking branch with it.</summary>
    public Task<GitResult> DeleteRemoteBranchAsync(string remote, string branch) => RunAsync(Root, ["push", "--end-of-options", remote, $":refs/heads/{branch}"]);

    public Task<GitResult> DeleteTagAsync(string name) => RunAsync(Root, ["tag", "-d", "--end-of-options", name]);

    /// <summary>Whether every commit of a branch is in HEAD's history.</summary>
    public async Task<bool> IsMergedAsync(string branch) => (await RunAsync(Root, ["merge-base", "--is-ancestor", branch, "HEAD"], answers: [1])).Succeeded;

    /// <summary>A local branch that is not checked out brought up to date with the remote's branch it tracks: forward only,
    /// refused where the two went apart.</summary>
    public Task<GitResult> FetchIntoAsync(string remote, string remoteBranch, string local) =>
        RunAsync(Root, ["fetch", "--end-of-options", remote, $"{remoteBranch}:refs/heads/{local}"]);

    /// <summary>A local branch pushed to the branch it tracks, or to origin under its own name, then tracked, when it tracks
    /// none.</summary>
    public Task<GitResult> PushBranchAsync(BranchInfo branch) => branch.UpstreamRemote is { } remote && branch.UpstreamBranch is { } target
        ? RunAsync(Root, ["push", "--end-of-options", remote, $"refs/heads/{branch.Name}:{target}"])
        : RunAsync(Root, ["push", "-u", "--end-of-options", "origin", $"refs/heads/{branch.Name}:refs/heads/{branch.Name}"]);

    /// <summary>The files that differ between a revision and the working tree (Show Diff with Working Tree), renames
    /// found, each by its path now.</summary>
    public async Task<IReadOnlyList<FileChange>> ChangesSinceAsync(string revision)
    {
        GitResult files = await RunAsync(Root, ["diff", "--name-status", "-z", "-M", "--end-of-options", revision, "--"]);
        return files.Succeeded ? NameStatus(files.Output) : [];
    }

    /// <summary>The stashes, the newest first.</summary>
    public async Task<IReadOnlyList<StashInfo>> StashesAsync()
    {
        GitResult list = await RunAsync(Root, ["stash", "list", "--format=%gd%x1f%gs%x1f%ct"]);
        if (!list.Succeeded)
        {
            return [];
        }
        List<StashInfo> stashes = [];
        foreach (string line in list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\x1f');
            if (parts.Length == 3 && parts[0].StartsWith("stash@{", StringComparison.Ordinal) && int.TryParse(parts[0][7..^1], out int index)
                && long.TryParse(parts[2], out long seconds))
            {
                stashes.Add(new StashInfo(index, parts[1], DateTimeOffset.FromUnixTimeSeconds(seconds)));
            }
        }
        return stashes;
    }

    /// <summary>Puts the uncommitted changes, new files too, away in a stash; the files go back to the head's.</summary>
    public Task<GitResult> StashAsync(string message) =>
        RunAsync(Root, ["stash", "push", "--include-untracked", .. message.Length > 0 ? new[] { "-m", message } : []]);

    /// <summary>Applies a stash to the files, and with <paramref name="pop"/> drops it once it applied.</summary>
    public Task<GitResult> UnstashAsync(int index, bool pop) => RunAsync(Root, ["stash", pop ? "pop" : "apply", $"stash@{{{index}}}"]);

    public Task<GitResult> DropStashAsync(int index) => RunAsync(Root, ["stash", "drop", $"stash@{{{index}}}"]);

    /// <summary>A new branch from the head, checked out. -c takes the next word as the name, whatever it is, and git
    /// refuses a name that starts with a dash.</summary>
    public Task<GitResult> NewBranchAsync(string name) => RunAsync(Root, ["switch", "-c", name]);

    /// <summary>A branch made at a commit and checked out.</summary>
    public Task<GitResult> NewBranchAtAsync(string name, string hash) => RunAsync(Root, ["switch", "-c", name, "--end-of-options", hash]);

    /// <summary>A commit checked out on its own, with no branch (a detached head).</summary>
    public Task<GitResult> CheckoutRevisionAsync(string hash) => RunAsync(Root, ["switch", "--detach", "--end-of-options", hash]);

    public Task<GitResult> CherryPickAsync(string hash) => RunAsync(Root, ["cherry-pick", "--end-of-options", hash]);

    /// <summary>A commit undone by a new one, with git's own message.</summary>
    public Task<GitResult> RevertAsync(string hash) => RunAsync(Root, ["revert", "--no-edit", "--end-of-options", hash]);

    /// <summary>The branch moved to a commit: soft keeps the changes staged, mixed keeps them in the files, hard drops them.</summary>
    public Task<GitResult> ResetAsync(string hash, ResetMode mode) => RunAsync(Root, ["reset", $"--{mode.ToString().ToLowerInvariant()}", "--end-of-options", hash]);

    /// <summary>Puts files back as the last commit has them (Rollback); new files are deleted. A file another
    /// program holds is tried again (<see cref="Attempts"/>), and one it never lets go of is named in the result, the
    /// rest rolled back all the same.</summary>
    public async Task<GitResult> RollbackAsync(IReadOnlyList<FileChange> changes)
    {
        string[] tracked = [.. changes.Where(c => c.Kind is not ChangeKind.Unversioned).SelectMany(c => c.From is { } from ? new[] { c.Path, from } : [c.Path]).Select(Relative)];
        GitResult restored = tracked.Length == 0 ? new GitResult(0, string.Empty, string.Empty) : await RestoreAsync(tracked);
        List<string> kept = [];
        foreach (FileChange added in changes.Where(c => c.Kind is ChangeKind.Unversioned))
        {
            if (await DeleteAsync(added.Path) is { } refused)
            {
                kept.Add(refused);
            }
        }
        if (kept.Count == 0)
        {
            return restored;
        }
        string[] reasons = [.. restored.Succeeded ? [] : new[] { restored.Message }, .. kept];
        return new GitResult(restored.Succeeded ? 1 : restored.Code, restored.Output, string.Join('\n', reasons));
    }

    /// <summary>How often a rollback tries a file, half a second apart: on Windows, another program holding it open (a
    /// virus scanner, an indexer) fails git's restore for that moment with "unable to unlink old".</summary>
    private const int Attempts = 3;

    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(500);

    /// <summary>The files as the last commit has them, in the index and on disk. A failure is tried again whatever git
    /// said, its words being in the person's language: restoring twice is the same as once.</summary>
    private async Task<GitResult> RestoreAsync(string[] relative)
    {
        for (int attempt = 1; ; attempt++)
        {
            GitResult restored = await RunAsync(Root, ["restore", "--source=HEAD", "--staged", "--worktree", "--", .. relative.Select(Literal)]);
            if (restored.Succeeded || attempt == Attempts)
            {
                return restored;
            }
            await Task.Delay(Retry);
        }
    }

    /// <summary>Deletes a new file, tried again as a restore is; why not, when it was never let go of.</summary>
    private static async Task<string?> DeleteAsync(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return null;
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts)
                {
                    return refused.Message;
                }
            }
            await Task.Delay(Retry);
        }
    }

    public string Relative(string path) => System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/');

    /// <summary>A file's name as git takes it for that file alone, never a pattern: "[ab].cs" is not also "a.cs", nor is
    /// a name starting ":" pathspec magic. (The environment's GIT_LITERAL_PATHSPECS would also stop a stash's own magic.)</summary>
    private static string Literal(string relative) => $":(literal){relative}";

    private string Full(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative));

    private static async Task<GitResult> RunAsync(string folder, IReadOnlyList<string> arguments, string? input = null, IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyCollection<int>? answers = null)
    {
        // What git answers is what the window shows next (the branch, the bars, the log, a blame): a screenshot waits for it.
        using IDisposable held = BackgroundWork.Scope();
        if (Start(folder, arguments) is not { } start)
        {
            return NotInstalled;
        }
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }
        try
        {
            ChildResult result = await ChildProcesses.RunAsync(start, input, answers: answers);
            return new GitResult(result.ExitCode, result.Output, result.Error);
        }
        catch (System.ComponentModel.Win32Exception missing)
        {
            return new GitResult(127, string.Empty, $"git is not installed: {missing.Message}");
        }
    }

    /// <summary>What git writes, as bytes, for output that counts its lengths in them (cat-file's objects); nothing when
    /// git is not there or fails to start.</summary>
    private static async Task<byte[]> OutputAsync(string folder, IReadOnlyList<string> arguments, string input)
    {
        using IDisposable held = BackgroundWork.Scope();
        if (Start(folder, arguments) is not { } start)
        {
            return [];
        }
        // Its input stays open for the names written to it.
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        try
        {
            using Process process = await ChildProcesses.StartAsync(start);
            using MemoryStream output = new();
            Task copied = process.StandardOutput.BaseStream.CopyToAsync(output);
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            await process.WaitForExitAsync();
            await copied;
            await error;
            return output.ToArray();
        }
        catch (Exception failed) when (failed is System.ComponentModel.Win32Exception or IOException)
        {
            return [];
        }
    }

    private static GitResult NotInstalled =>
        new(127, string.Empty, OperatingSystem.IsMacOS() ? "git is not installed: xcode-select --install installs it" : "git is not installed");

    /// <summary>What git exits with when what it is asked for is not there (a file at a revision, an upstream, a first
    /// commit, a working tree): the answer no to a question, which the log leaves out.</summary>
    private const int NotThere = 128;

    /// <summary>How git is started for every call, or null when it is not installed.</summary>
    private static ProcessStartInfo? Start(string folder, IReadOnlyList<string> arguments)
    {
        if (Programs.Git() is not { } git)
        {
            return null;
        }
        ProcessStartInfo start = ChildProcesses.Command(git, arguments, folder);
        // Git never stops to ask on a terminal: it has none (ChildProcesses), and this keeps it from looking for one.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // A background status or diff never takes the index's lock, so a stash, commit or checkout then never fails on it.
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        return start;
    }
}

/// <summary>How Reset Current Branch to Here treats the changes after the commit.</summary>
public enum ResetMode
{
    Soft,
    Mixed,
    Hard,
}
