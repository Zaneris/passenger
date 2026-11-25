using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
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

/// <summary>How much of a file's change git's index holds, which a commit of the index takes: none of it, part of it (the
/// working tree changed it further), or all of it.</summary>
public enum StagedState
{
    None,
    Part,
    Whole,
}

/// <summary>A changed file of the working tree: its path (absolute), how it changed, where it came from if renamed, what
/// changed in it where it is a submodule, and its mode where that changed since the last commit.</summary>
public sealed record FileChange(string Path, ChangeKind Kind, string? From = null, SubmoduleChange Submodule = SubmoduleChange.None,
    ModeChange? Mode = null)
{
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>How much of the change the index holds (git's staged changes).</summary>
    public StagedState Staged { get; init; }

    /// <summary>Whether the index holds the file's move alone, the last commit's content under its new name, as a move
    /// Passenger made keeps the file's history: nothing staged of what changed in it.</summary>
    public bool MovedOnlyInIndex { get; init; }

    /// <summary>Whether the index holds some of what changed in the file, beyond a move alone.</summary>
    public bool HoldsStaged => Staged != StagedState.None && !MovedOnlyInIndex && Kind is not (ChangeKind.Conflicted or ChangeKind.Unversioned);

    /// <summary>In a folder not trusted, the filter git would store the file through, which runs only once the folder is
    /// trusted (<see cref="UntrustedGit.FilteredAsync"/>); null where it has none, or the folder is trusted.</summary>
    public string? Filter { get; init; }

    /// <summary>Why git neither stores nor writes out the file here (a commit, a stash, a rollback): in a folder not
    /// trusted, it is stored through a filter, which runs only once the folder is trusted; null where it is not.</summary>
    public string? Unfiltered => Filter is { } filter ? UntrustedGit.Unfiltered(Name, filter) : null;

    /// <summary>Why the change cannot be committed here, or null when it can: a submodule whose own files changed while its
    /// commit did not, which only a commit in the submodule takes; or a file stored through a filter, in a folder not
    /// trusted, where it would go in as the disk has it (its deletion stores nothing).</summary>
    public string? Uncommittable => IsSubmoduleContent ? $"{Name} is a submodule: its own {OwnChanges} are committed in it, not here"
        : Kind is ChangeKind.Deleted ? null
        : Unfiltered;

    private bool IsSubmoduleContent => Submodule.HasFlag(SubmoduleChange.Submodule) && !Submodule.HasFlag(SubmoduleChange.Commit) && Kind is ChangeKind.Modified;

    private string OwnChanges => (Submodule.HasFlag(SubmoduleChange.Modified), Submodule.HasFlag(SubmoduleChange.Untracked)) switch
    {
        (true, true) => "modified and untracked files",
        (true, false) => "modified files",
        _ => "untracked files",
    };
}

/// <summary>A file's mode in the last commit and in the working tree, as git writes them (100644, 100755 executable,
/// 120000 a symbolic link).</summary>
public sealed record ModeChange(string Before, string After)
{
    /// <summary>The change as git's diff says it.</summary>
    public string Unified => $"old mode {Before}\nnew mode {After}";

    /// <summary>The change in a person's words.</summary>
    public string Said => (Before, After) switch
    {
        ("100644", "100755") => "File mode changed from 100644 to 100755: now executable",
        ("100755", "100644") => "File mode changed from 100755 to 100644: no longer executable",
        _ => $"File mode changed from {Before} to {After}",
    };
}

/// <summary>What changed in a submodule as its repository's status says (its "sub" field): the commit it is at, which a
/// commit here records, and the files in its own working tree, which only a commit in it takes.</summary>
[Flags]
public enum SubmoduleChange
{
    /// <summary>Not a submodule.</summary>
    None = 0,

    /// <summary>A submodule.</summary>
    Submodule = 1,

    /// <summary>Its commit is not the one the last commit records, in the index or checked out.</summary>
    Commit = 2,

    /// <summary>Files it tracks changed.</summary>
    Modified = 4,

    /// <summary>Files it does not track are in it.</summary>
    Untracked = 8,
}

/// <summary>A commit as the log lists it: its hash and parents, who made it and when, the branches and tags at it,
/// and its subject.</summary>
public sealed record CommitInfo(string Hash, IReadOnlyList<string> Parents, string Author, string Email, DateTimeOffset Time, IReadOnlyList<string> Refs, string Subject)
{
    public string Short => GitHashes.Short(Hash);
}

/// <summary>What Show History is of: a file, followed through its renames; a folder; or a file's lines, from 0 as the last
/// commit numbers them (Show History for Selection). <see cref="From"/> is where the last commit has a file renamed or
/// moved since, not committed yet, whose history is read from there.</summary>
public sealed record HistoryScope(string Path, bool IsFolder = false, int FirstLine = -1, int LastLine = -1, string? From = null)
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
/// for a local branch the remote and the remote's branch it tracks (its upstream), if any, and the folder of the working
/// tree it is checked out in, if any (this one's for the current branch).</summary>
public sealed record BranchInfo(GitRef Ref, string Hash, bool IsCurrent, string? UpstreamRemote = null, string? UpstreamBranch = null, string? Worktree = null)
{
    /// <summary>The working tree another than this one has it checked out in, where git checks it out nowhere else.</summary>
    public string? CheckedOutElsewhere => IsCurrent ? null : Worktree;

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

    public string Short => GitHashes.Short(Hash);
}

/// <summary>What one status read found, together: the branch checked out (null on a detached head), the commit HEAD
/// names (null before the first), the changed and new files, and those of them in conflict; and the upstream the branch
/// tracks as git shortens it (origin/main; null where it tracks none), with whether the two name one commit (null where
/// the upstream is gone from the remote's branches).</summary>
public sealed record GitStatus(string? Branch, string? Head, IReadOnlyList<FileChange> Changes, IReadOnlyList<GitConflict> Conflicts,
    string? Upstream = null, bool? Even = null);

/// <summary>A file in conflict and which of git's three versions of it there are: the one both sides came from (stage
/// 1), ours (stage 2, HEAD's side) and theirs (stage 3, the side brought in); a side without one deleted the file.
/// <see cref="IsBinary"/> when any of them is binary (<see cref="FileText.LooksBinary"/>).</summary>
public sealed record GitConflict(string Path, bool HasBase, bool HasOurs, bool HasTheirs, bool IsBinary = false)
{
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Whether both sides have the file as text, so there is something to merge line by line; else it is kept
    /// or deleted whole.</summary>
    public bool CanMerge => HasOurs && HasTheirs && !IsBinary;
}

/// <summary>A stash: its place in the list (stash@{Index}) when the list was read, its commit, what it says, and when it
/// was made.</summary>
public sealed record StashInfo(int Index, string Commit, string Message, DateTimeOffset Time);

/// <summary>What looking for a folder's working tree found: the tree, or none; and why there is none where git cannot say:
/// it refused the one it found (a repository another user owns, as on a FAT or network drive, until safe.directory names
/// it), in its words, it was not run in a folder not trusted (<see cref="UntrustedGit"/>), or it is not installed
/// (<paramref name="Missing"/>), which leaves a repository there unread. In a folder not trusted, the tree is the folder
/// the repository's .git is in, and <paramref name="WorkTreeNamed"/> the one its settings name elsewhere, unused.</summary>
public sealed record GitLookup(Git? Tree, string? Refused, bool Missing = false, string? WorkTreeNamed = null);

/// <summary>What `git` says, and whether it said it without failing; and why a question it asked of the person (a login,
/// a key's passphrase) went unanswered, where one did.</summary>
public sealed record GitResult(int Code, string Output, string Error, string? Unanswered = null)
{
    public bool Succeeded => Code == 0;

    /// <summary>What to tell the person when it failed: why its question went unanswered where one did, which is why git
    /// failed, else git's own words.</summary>
    public string Message => !Succeeded && Unanswered is { } why ? why : (Error.Trim().Length > 0 ? Error : Output).Trim();
}

/// <summary>A Git working tree, through the `git` executable: its status, branches, a file as the last commit has it,
/// and committing, pushing and switching. Every call runs git in the tree's root, its output read as UTF-8, never
/// through a shell.</summary>
public sealed partial class Git
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

    /// <summary>Told, and awaited, before an operation writes out the working tree's files (a checkout, an update, a stash,
    /// a merge, a reset, a resolution): the files it may write or delete, by their full paths as the disk names them, each
    /// still as it stands.</summary>
    public Func<IReadOnlyList<string>, Task>? Writing { get; set; }

    /// <summary>Runs an operation that writes out the working tree's files, <see cref="Writing"/> told first of those
    /// <paramref name="writes"/> lists from the tree's top, listed only where something is told; one git could not list is
    /// told of nothing.</summary>
    private async Task<GitResult> WritingOutAsync(Func<Task<IReadOnlyList<string>?>> writes, Func<Task<GitResult>> operation)
    {
        if (Writing is { } writing && await writes() is { Count: > 0 } relative)
        {
            DiskNames names = new(Root);
            await writing([.. relative.Distinct(StringComparer.Ordinal).Select(names.Full)]);
        }
        return await operation();
    }

    /// <summary>The files several lists name, those git could not list left out.</summary>
    private static IReadOnlyList<string> AllOf(params IReadOnlyList<string>?[] lists) => [.. lists.OfType<IReadOnlyList<string>>().SelectMany(l => l).Distinct(StringComparer.Ordinal)];

    /// <summary>The working tree a folder is in; none when it is in none, with git's reason when git refused the one it
    /// found, or why git is not there to look.</summary>
    public static async Task<GitLookup> OpenAsync(string folder)
    {
        // What the lookup finds, its gits one after another, is what the window shows next: a screenshot waits for it whole.
        using GitHold held = new();
        GitResult top = await RunAsync(folder, ["rev-parse", "--show-toplevel", "--absolute-git-dir"], answers: [NotThere]);
        if (top.Code == Absent)
        {
            return new GitLookup(null, top.Error, Missing: true);
        }
        if (top.Code == Withheld)
        {
            return new GitLookup(null, top.Error);
        }
        if (top.Succeeded && Places(top.Output) is ({ } root, { } gitDir))
        {
            return new GitLookup(new Git(PathNames.Canonical(root), PathNames.Canonical(gitDir)), null, WorkTreeNamed: await WorkTreeNamedAsync(folder));
        }
        // Git names the setting that would let it in only when it refuses a repository it found, in any language it speaks.
        return new GitLookup(null, top.Code == NotThere && top.Error.Contains("safe.directory", StringComparison.Ordinal)
            ? $"Git refuses this folder's repository: {OneLine(top.Error)}"
            : null);
    }

    /// <summary>In a folder not trusted, the working tree the repository's settings name where it is not the folder its .git
    /// is in (<see cref="UntrustedGit.WorkTreeNamedAsync"/>); null where trusted, or they name none, or git cannot say.</summary>
    private static async Task<string?> WorkTreeNamedAsync(string folder)
    {
        if (WorkspaceTrust.Trusts(folder) || await Programs.GitAsync() is not { } git)
        {
            return null;
        }
        try
        {
            return await UntrustedGit.WorkTreeNamedAsync(git, folder);
        }
        catch (Exception unread) when (ChildFailures.Of(unread))
        {
            return null;
        }
    }

    /// <summary>The working tree's top and git's folder as rev-parse writes them, a line each: read whole but for the line
    /// break (and the carriage return before it on Windows), as a folder's name may begin or end with a space. Null where
    /// git wrote other than two lines.</summary>
    internal static (string Top, string GitDir)? Places(string output)
    {
        string[] lines = output.Split('\n');
        if (lines is not [{ Length: > 0 } top, { Length: > 0 } gitDir, ""])
        {
            return null;
        }
        return (Unbroken(top), Unbroken(gitDir));

        static string Unbroken(string line) => OperatingSystem.IsWindows() && line.EndsWith('\r') ? line[..^1] : line;
    }

    /// <summary>What git said on one line: each of its lines trimmed, "fatal: " left off, and a line it ends without a
    /// stop given one where a sentence follows.</summary>
    private static string OneLine(string said)
    {
        StringBuilder line = new();
        foreach (string part in said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string words = part.StartsWith("fatal: ", StringComparison.Ordinal) ? part[7..] : part;
            if (line.Length > 0)
            {
                line.Append(char.IsUpper(words[0]) && line[^1] is not ('.' or ':' or ';' or ',' or '!' or '?') ? ". " : " ");
            }
            line.Append(words);
        }
        return line.ToString();
    }

    /// <summary>Makes a folder a working tree (Create Git Repository).</summary>
    public static Task<GitResult> InitAsync(string folder) => RunAsync(folder, ["init"]);

    /// <summary>Clones a repository into a folder: each line git says of its progress
    /// goes to <paramref name="progress"/> as it is said, on another thread, and what it asks of the person (a login) to
    /// <paramref name="asker"/>. Canceled, git is ended with all it started
    /// and the task is canceled; what it had made of the folder is the caller's to remove.</summary>
    public static async Task<GitResult> CloneAsync(string url, string destination, Action<string> progress, CancellationToken cancel, GitAsker? asker = null)
    {
        // The clone opens next: a screenshot waits for it.
        using GitHold held = new();
        string parent = System.IO.Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        // After "--" a URL that begins with a dash is a URL, never an option such as --upload-pack.
        (ProcessStartInfo? start, GitResult? refused) = await StartAsync(parent, ["clone", "--progress", "--", url, destination]);
        if (start is null)
        {
            return refused ?? NotInstalled;
        }
        await using Prompting? prompting = Asking(start, asker, held, cancel);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process process;
        try
        {
            process = await ChildProcesses.StartAsync(start);
        }
        catch (System.ComponentModel.Win32Exception unstarted)
        {
            return StartFailure(unstarted);
        }
        using (process)
        {
            Task<string> output = ChildProcesses.Output(process).ReadToEndAsync(CancellationToken.None);
            Task<string> said = LinesAsync(ChildProcesses.Errors(process), progress);
            using (cancel.Register(() => ChildProcesses.Kill(process)))
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            string errors = await said;
            if (prompting is not null)
            {
                Prompting.Heard(errors);
            }
            cancel.ThrowIfCancellationRequested();
            return new GitResult(process.ExitCode, await output, errors, prompting?.Refused);
        }
    }

    /// <summary>What git writes to its errors, each line handed on as it ends, and the lines it finished, whole: progress
    /// writes its line again and again after a carriage return, which ends a line handed on but not one kept.</summary>
    private static async Task<string> LinesAsync(TextReader errors, Action<string> line)
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
    /// left out, renames with where they came from, however many files moved), in one git so the three agree.</summary>
    public async Task<GitStatus> StatusAsync()
    {
        (GitResult ran, PooledBytes? output) = await RunAsync(Root,
            [.. Unlimited, "status", "--porcelain=v2", "-z", "--branch", "--no-ahead-behind", "--untracked-files=all"], PooledAsync);
        StatusRead read;
        using (output)
        {
            if (!ran.Succeeded || output is null)
            {
                throw new InvalidOperationException((ran with { Output = output is null ? string.Empty : Encoding.UTF8.GetString(output.Written) }).Message);
            }
            read = StatusOf(output.Written, new DiskNames(Root));
        }
        List<GitConflict> conflicts = read.Conflicts;
        if (conflicts.Count > 0)
        {
            await BinaryBlobsAsync(read.Stages.SelectMany(s => s));
            for (int i = 0; i < conflicts.Count; i++)
            {
                conflicts[i] = conflicts[i] with { IsBinary = read.Stages[i].Any(blob => binaryBlobs.GetValueOrDefault(blob)) };
            }
        }
        await MarkFilteredAsync(read.Changes);
        return new GitStatus(read.Branch, read.Head, read.Changes, conflicts, read.Upstream, read.Even);
    }

    /// <summary>Marks each change git would store or write out through a filter, in a folder not trusted, with the filter's
    /// name: a file there, not a submodule (<see cref="FilteredAsync"/>). Where git cannot say, none is marked, and what
    /// would store or write it asks again.</summary>
    private async Task MarkFilteredAsync(List<FileChange> changes)
    {
        if (WorkspaceTrust.Trusts(Root))
        {
            return;
        }
        List<int> stored = [.. Enumerable.Range(0, changes.Count).Where(at => changes[at] is
            { Kind: ChangeKind.Modified or ChangeKind.Added or ChangeKind.Renamed or ChangeKind.Deleted or ChangeKind.Unversioned, Submodule: SubmoduleChange.None })];
        if (await FilteredAsync([.. stored.Select(at => Relative(changes[at].Path))]) is not { Count: > 0 } filtered)
        {
            return;
        }
        foreach (int at in stored)
        {
            if (filtered.TryGetValue(Relative(changes[at].Path), out string? filter))
            {
                changes[at] = changes[at] with { Filter = filter };
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, string> NoneFiltered = new Dictionary<string, string>();

    /// <summary>Of <paramref name="relative"/> (from the tree's top), each file git would store through a filter, with the
    /// filter's name, in a folder not trusted, where none runs (<see cref="UntrustedGit.FilteredAsync"/>); none where the
    /// folder is trusted, and null where git could not say.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> FilteredAsync(IReadOnlyList<string> relative)
    {
        if (relative.Count == 0 || WorkspaceTrust.Trusts(Root))
        {
            return NoneFiltered;
        }
        // What git answers is what the window shows next: a screenshot waits for it.
        using GitHold held = new();
        if (await Programs.GitAsync() is not { } git || await UntrustedGit.DriversAsync(git, Root) is not { } drivers)
        {
            return null;
        }
        return await FilteredByAsync(git, drivers, relative, []);
    }

    /// <summary><see cref="FilteredAsync(IReadOnlyList{string})"/> through <paramref name="drivers"/>, as the working tree's
    /// attributes have them and, where a .gitattributes is among the files, as each of <paramref name="sources"/> has them
    /// too, whose files git writes out under its own.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> FilteredByAsync(string git, IReadOnlySet<string> drivers, IReadOnlyList<string> relative,
        IReadOnlyList<string> sources)
    {
        if (relative.Count == 0 || drivers.Count == 0)
        {
            return NoneFiltered;
        }
        bool composed = relative.Any(name => !Ascii.IsValid(name)) && await ComposesAsync();
        string[] named = [.. relative.Select(name => GitForm(name, composed))];
        bool attributed = relative.Any(name => name == ".gitattributes" || name.EndsWith("/.gitattributes", StringComparison.Ordinal));
        Dictionary<string, string> filtered = new(StringComparer.Ordinal);
        foreach (string? source in (string?[])[null, .. attributed ? sources : []])
        {
            if (await UntrustedGit.FilteredAsync(git, Root, named, drivers, source) is not { } found)
            {
                return null;
            }
            foreach ((int at, string filter) in found)
            {
                filtered.TryAdd(relative[at], filter);
            }
        }
        return filtered;
    }

    /// <summary>Why git cannot store or write out files here, in a folder not trusted: the first that <paramref name="paths"/>
    /// names (from the tree's top, asked only where a configuration gives a filter a program) git stores through a
    /// filter, which runs only once the folder is trusted, or git could not say which those are; null where none is, or
    /// the folder is trusted. Where a .gitattributes is among them, the files are asked of <paramref name="sources"/>' too.</summary>
    private async Task<GitResult?> UnfilteredAsync(Func<Task<IReadOnlyList<string>?>> paths, params IReadOnlyList<string> sources)
    {
        if (WorkspaceTrust.Trusts(Root))
        {
            return null;
        }
        using GitHold held = new();
        if (await Programs.GitAsync() is not { } git)
        {
            return null;
        }
        IReadOnlySet<string>? drivers = await UntrustedGit.DriversAsync(git, Root);
        if (drivers is { Count: 0 })
        {
            return null;
        }
        IReadOnlyList<string>? relative = drivers is null ? null : await paths();
        IReadOnlyDictionary<string, string>? filtered = relative is null ? null : await FilteredByAsync(git, drivers!, relative, sources);
        return filtered is null || relative is null ? Unsaid
            : relative.FirstOrDefault(filtered.ContainsKey) is { } first ? new GitResult(1, string.Empty, UntrustedGit.Unfiltered(System.IO.Path.GetFileName(first), filtered[first]))
            : null;
    }

    /// <summary><see cref="UnfilteredAsync(Func{Task{IReadOnlyList{string}?}}, IReadOnlyList{string})"/> of files named.</summary>
    private Task<GitResult?> UnfilteredAsync(IReadOnlyList<string> relative) => UnfilteredAsync(() => Task.FromResult<IReadOnlyList<string>?>(relative));

    /// <summary>Whether git here may store or write out a file as it keeps it where a filter would: the folder is not trusted
    /// and a configuration gives a filter a program, or git could not say; what would resolve the revisions an operation
    /// moves between is asked only then.</summary>
    private async Task<bool> MayFilterAsync() =>
        !WorkspaceTrust.Trusts(Root) && await Programs.GitAsync() is { } git && await UntrustedGit.DriversAsync(git, Root) is not { Count: 0 };

    private static readonly GitResult Unsaid =
        new(1, string.Empty, "The folder is not trusted, and git could not say which of the files it stores through a filter, which runs only once the folder is trusted");

    /// <summary>The files (from the tree's top) that differ between two revisions (or a range, "A...B", alone), or between
    /// one and the working tree; null where git could not say.</summary>
    private async Task<IReadOnlyList<string>?> DifferingAsync(params string[] revisions)
    {
        GitResult diff = await RunAsync(Root, ["diff", "--name-only", "--no-renames", "-z", "--end-of-options", .. revisions, "--"], answers: [NotThere]);
        return diff.Succeeded ? diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
    }

    /// <summary>Every file of a revision's tree (from its top); null where it names none.</summary>
    private async Task<IReadOnlyList<string>?> FilesOfAsync(string revision)
    {
        GitResult files = await RunAsync(Root, ["ls-tree", "-r", "-z", "--name-only", "--full-tree", "--end-of-options", revision], answers: [NotThere]);
        return files.Succeeded ? files.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
    }

    /// <summary>The commit a revision names (a branch, a tag, a hash), or null where it names none.</summary>
    private async Task<string?> CommitOfAsync(string revision)
    {
        if (revision.StartsWith('-'))
        {
            return null;
        }
        GitResult named = await RunAsync(Root, ["rev-parse", "-q", "--verify", $"{revision}^{{commit}}"], answers: [1, NotThere]);
        return named.Succeeded && named.Output.Trim() is { Length: > 0 } hash ? hash : null;
    }

    /// <summary>The files a move of the working tree from HEAD to <paramref name="target"/> writes out (a checkout, a
    /// reset): those that differ between the two, or every file of the target where HEAD names no commit yet; null where
    /// git could not say.</summary>
    private async Task<IReadOnlyList<string>?> SwitchedAsync(string target) =>
        await DifferingAsync("HEAD", target) ?? (await CommitOfAsync("HEAD") is null ? await FilesOfAsync(target) : null);

    /// <summary>Why a move of the working tree to a revision (a branch, a tag, a commit) cannot be made here: in a folder
    /// not trusted, a file it writes out is stored through a filter, which runs only once the folder is trusted
    /// (<see cref="UnfilteredAsync(Func{Task{IReadOnlyList{string}?}}, IReadOnlyList{string})"/>); null where it can, or
    /// the revision names no commit, which git refuses itself.</summary>
    public async Task<string?> WhyNotSwitchedAsync(string target)
    {
        // What git answers is what the window shows next (an action greyed): a screenshot waits for it.
        using GitHold held = new();
        return (await UnfilteredSwitchAsync(target))?.Message;
    }

    private async Task<GitResult?> UnfilteredSwitchAsync(string target) => await MayFilterAsync() && await CommitOfAsync(target) is { } commit
        ? await UnfilteredAsync(() => SwitchedAsync(commit), commit)
        : null;

    /// <summary>What <c>git switch</c> checks out for a branch's name: the local branch, else each remote's branch of that
    /// name, one of which it would make a local branch tracking.</summary>
    private async Task<IReadOnlyList<string>> BranchesNamedAsync(string branch)
    {
        if (await CommitOfAsync($"refs/heads/{branch}") is { } local)
        {
            return [local];
        }
        GitResult remotes = await RunAsync(Root, ["for-each-ref", "--format=%(refname)", "refs/remotes"]);
        return remotes.Succeeded
            ? [.. remotes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(name => name.EndsWith($"/{branch}", StringComparison.Ordinal))]
            : [];
    }

    /// <summary>Why a branch cannot be checked out by its name here (<see cref="WhyNotSwitchedAsync"/>, of the local branch,
    /// or of each remote's of that name it would be made from); null where it can.</summary>
    public async Task<string?> WhyNotCheckedOutAsync(string branch)
    {
        // What git answers is what the window shows next (an action greyed): a screenshot waits for it.
        using GitHold held = new();
        return (await UnfilteredCheckoutAsync(branch))?.Message;
    }

    private async Task<GitResult?> UnfilteredCheckoutAsync(string branch)
    {
        if (!await MayFilterAsync())
        {
            return null;
        }
        foreach (string target in await BranchesNamedAsync(branch))
        {
            if (await UnfilteredSwitchAsync(target) is { } refused)
            {
                return refused;
            }
        }
        return null;
    }

    /// <summary>The files a checkout of a branch by its name writes out (<see cref="SwitchedAsync"/>): to the local branch,
    /// else to whichever remote's branch of that name it makes one from.</summary>
    private async Task<IReadOnlyList<string>?> CheckedOutAsync(string branch)
    {
        List<IReadOnlyList<string>?> each = [];
        foreach (string target in await BranchesNamedAsync(branch))
        {
            each.Add(await SwitchedAsync(target));
        }
        return AllOf([.. each]);
    }

    /// <summary>Why the uncommitted changes cannot be stashed here: in a folder not trusted, git would store a file among
    /// them, and write it out again from HEAD, through a filter, which runs only once the folder is trusted; null where they
    /// can be.</summary>
    public async Task<string?> WhyNotStashedAsync()
    {
        // What git answers is what the window shows next (an action greyed): a screenshot waits for it.
        using GitHold held = new();
        return (await UnfilteredStashAsync())?.Message;
    }

    private Task<GitResult?> UnfilteredStashAsync() => UnfilteredAsync(StashedAsync);

    /// <summary>The files a stash writes out: those with uncommitted changes, put back as the head has them, and those git
    /// does not track, taken away; null where git could not say.</summary>
    private async Task<IReadOnlyList<string>?> StashedAsync()
    {
        GitResult untracked = await RunAsync(Root, ["ls-files", "-z", "--others", "--exclude-standard"]);
        IReadOnlyList<string>? tracked = await DifferingAsync("HEAD") ?? (await CommitOfAsync("HEAD") is null ? [] : null);
        return tracked is null || !untracked.Succeeded ? null : [.. tracked, .. untracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>Why a stash cannot be applied here: in a folder not trusted, a file it writes out is stored through a filter,
    /// which runs only once the folder is trusted; null where it can be, or the stash is no longer listed.</summary>
    public async Task<string?> WhyNotUnstashedAsync(StashInfo stash)
    {
        // What git answers is what the window shows next (an action greyed): a screenshot waits for it.
        using GitHold held = new();
        return (await UnfilteredUnstashAsync(stash))?.Message;
    }

    private Task<GitResult?> UnfilteredUnstashAsync(StashInfo stash) => UnfilteredAsync(() => UnstashedAsync(stash.Commit), stash.Commit);

    /// <summary>The files a stash's commit writes out as it is applied: those it changed, and the new files it holds; null
    /// where git could not say.</summary>
    private async Task<IReadOnlyList<string>?> UnstashedAsync(string stash)
    {
        // A stash's commit holds the files as they were, its first parent the head it was made on, its third the new files.
        IReadOnlyList<string>? changed = await DifferingAsync($"{stash}^1", stash);
        IReadOnlyList<string>? added = await CommitOfAsync($"{stash}^3") is { } untracked ? await FilesOfAsync(untracked) : [];
        return changed is null || added is null ? null : [.. changed, .. added];
    }

    /// <summary>The setting that has a status look for renames however many files were added and deleted (a limit of 0 is
    /// none), for that read alone: past its limit git lists the moves it would otherwise find as deletions and additions.</summary>
    private static readonly string[] Unlimited = ["-c", "status.renameLimit=0"];

    /// <summary>A status as read from git's bytes: the branch, HEAD's commit, the files, the conflicts and, for each
    /// conflict, the blobs of the stages git has a version at; the upstream and whether HEAD is where it is.</summary>
    internal sealed record StatusRead(string? Branch, string? Head, List<FileChange> Changes, List<GitConflict> Conflicts, List<string[]> Stages)
    {
        public string? Upstream { get; init; }

        public bool? Even { get; init; }
    }

    /// <summary>A status from what <c>git status --porcelain=v2 -z --branch</c> wrote, read where it lies: each entry's
    /// fields found in place and only its paths, a branch, HEAD and a conflict's blobs made strings, so a status of
    /// thousands of files costs those files' paths.</summary>
    internal static StatusRead StatusOf(ReadOnlySpan<byte> output, DiskNames names)
    {
        string? branch = null;
        string? head = null;
        string? upstream = null;
        bool? even = null;
        List<FileChange> changes = [];
        List<GitConflict> conflicts = [];
        List<string[]> stages = [];
        Span<Range> fields = stackalloc Range[11];
        while (!output.IsEmpty)
        {
            ReadOnlySpan<byte> entry = Entry(ref output);
            if (entry.Length < 2)
            {
                continue;
            }
            switch (entry[0])
            {
                case (byte)'#' when entry.StartsWith("# branch.oid "u8):
                    head = entry[13..].SequenceEqual("(initial)"u8) ? null : Text(entry[13..]);
                    break;
                case (byte)'#' when entry.StartsWith("# branch.head "u8):
                    branch = entry[14..].SequenceEqual("(detached)"u8) ? null : Text(entry[14..]);
                    break;
                case (byte)'#' when entry.StartsWith("# branch.upstream "u8):
                    upstream = Text(entry[18..]);
                    break;
                // "+0 -0" at the upstream's commit, "+? -?" apart (--no-ahead-behind counts none), no line once it is gone.
                case (byte)'#' when entry.StartsWith("# branch.ab "u8):
                    even = entry[12..].SequenceEqual("+0 -0"u8);
                    break;
                // "1 XY sub mH mI mW hH hI path"
                case (byte)'1' when Spaced(entry, fields[..9]):
                    ReadOnlySpan<byte> xy = entry[fields[1]];
                    changes.Add(new FileChange(names.Full(entry[fields[8]]), Kind(xy), Submodule: Submodule(xy, entry[fields[2]]),
                        Mode: Mode(entry[fields[3]], entry[fields[5]])) { Staged = StagedOf(xy) });
                    break;
                // "2 XY sub mH mI mW hH hI Xscore path", then where it came from; a move alone where hI and mI are hH and mH.
                case (byte)'2' when Spaced(entry, fields[..10]):
                    changes.Add(new FileChange(names.Full(entry[fields[9]]), ChangeKind.Renamed, names.Full(Entry(ref output)),
                        Mode: Mode(entry[fields[3]], entry[fields[5]]))
                    {
                        Staged = StagedOf(entry[fields[1]]),
                        MovedOnlyInIndex = entry[fields[6]].SequenceEqual(entry[fields[7]]) && entry[fields[3]].SequenceEqual(entry[fields[4]]),
                    });
                    break;
                // "u XY sub m1 m2 m3 mW h1 h2 h3 path": a stage git has no version at has the mode 000000.
                case (byte)'u' when Spaced(entry, fields):
                    string path = names.Full(entry[fields[10]]);
                    changes.Add(new FileChange(path, ChangeKind.Conflicted));
                    conflicts.Add(new GitConflict(path, Staged(entry[fields[3]]), Staged(entry[fields[4]]), Staged(entry[fields[5]])));
                    List<string> blobs = [];
                    for (int stage = 3; stage <= 5; stage++)
                    {
                        if (Staged(entry[fields[stage]]))
                        {
                            blobs.Add(Text(entry[fields[stage + 4]]));
                        }
                    }
                    stages.Add([.. blobs]);
                    break;
                // A save under way as git read the folder: the saved file's change, which the read after the save lists.
                case (byte)'?' when entry.EndsWith(SavingSuffix):
                    break;
                case (byte)'?':
                    changes.Add(new FileChange(names.Full(entry[2..]), ChangeKind.Unversioned));
                    break;
            }
        }
        return new StatusRead(branch, head, changes, conflicts, stages) { Upstream = upstream, Even = even };

        static ChangeKind Kind(ReadOnlySpan<byte> xy) =>
            xy.Contains((byte)'D') ? ChangeKind.Deleted : xy[0] == (byte)'A' ? ChangeKind.Added : ChangeKind.Modified;

        // X is the index's change from the last commit, Y the working tree's from the index; '.' where none.
        static StagedState StagedOf(ReadOnlySpan<byte> xy) => xy[0] == (byte)'.' ? StagedState.None : xy[1] == (byte)'.' ? StagedState.Whole : StagedState.Part;

        // "N..." for a file; "S" then C, M and U (or '.') for a submodule's commit, tracked and untracked changes. A
        // commit the index holds that the last commit does not is a change of its commit too.
        static SubmoduleChange Submodule(ReadOnlySpan<byte> xy, ReadOnlySpan<byte> sub) => sub is [(byte)'S', var commit, var modified, var untracked]
            ? SubmoduleChange.Submodule
                | (commit == (byte)'C' || xy[0] != (byte)'.' ? SubmoduleChange.Commit : SubmoduleChange.None)
                | (modified == (byte)'M' ? SubmoduleChange.Modified : SubmoduleChange.None)
                | (untracked == (byte)'U' ? SubmoduleChange.Untracked : SubmoduleChange.None)
            : SubmoduleChange.None;

        static bool Staged(ReadOnlySpan<byte> mode) => mode.ContainsAnyExcept((byte)'0');

        // A side's 000000 is a file added or deleted, not a mode changed.
        static ModeChange? Mode(ReadOnlySpan<byte> head, ReadOnlySpan<byte> worktree) =>
            !head.SequenceEqual(worktree) && Staged(head) && Staged(worktree) ? new ModeChange(Text(head), Text(worktree)) : null;
    }

    /// <summary>A save's temporary file's name ends, as git writes it.</summary>
    private static readonly byte[] SavingSuffix = Encoding.UTF8.GetBytes(FileText.SaveSuffix);

    /// <summary>The bytes up to the next NUL, which ends each of a -z output's entries; <paramref name="output"/> is moved past.</summary>
    private static ReadOnlySpan<byte> Entry(ref ReadOnlySpan<byte> output)
    {
        int end = output.IndexOf((byte)0);
        ReadOnlySpan<byte> entry = end < 0 ? output : output[..end];
        output = end < 0 ? [] : output[(end + 1)..];
        return entry;
    }

    /// <summary>An entry's fields between spaces, as many as <paramref name="fields"/> holds, the last running to the entry's
    /// end (a path may hold spaces); false where the entry has fewer.</summary>
    private static bool Spaced(ReadOnlySpan<byte> entry, Span<Range> fields)
    {
        int start = 0;
        for (int i = 0; i < fields.Length - 1; i++)
        {
            int at = entry[start..].IndexOf((byte)' ');
            if (at < 0)
            {
                return false;
            }
            fields[i] = new Range(start, start + at);
            start += at + 1;
        }
        fields[^1] = new Range(start, entry.Length);
        return true;
    }

    /// <summary>The folders git's status shows whole by default (absolute, as the disk names them): each holds untracked
    /// files and none git tracks, ignored ones left out.</summary>
    public async Task<IReadOnlyList<string>> UntrackedFoldersAsync()
    {
        GitResult listed = await RunAsync(Root, ["ls-files", "-z", "--others", "--exclude-standard", "--directory", "--no-empty-directory"]);
        if (!listed.Succeeded)
        {
            throw new InvalidOperationException(listed.Message);
        }
        DiskNames names = new(Root);
        return [.. listed.Output.Split('\0').Where(e => e.Length > 1 && e[^1] == '/').Select(e => names.Full(e[..^1]))];
    }

    /// <summary>What .gitignore and git's other excludes leave out of the repository under <paramref name="folder"/>, a
    /// folder of the working tree: each file or folder they leave out that git does not track, a folder named whole,
    /// absolute as the disk names them.</summary>
    public async Task<IReadOnlyList<string>> LeftOutUnderAsync(string folder)
    {
        GitResult listed = await RunAsync(folder, ["ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory"]);
        if (!listed.Succeeded)
        {
            throw new InvalidOperationException(listed.Message);
        }
        DiskNames names = new(folder);
        return [.. listed.Output.Split('\0').Where(e => e.Length > 0).Select(e => names.Full(e.TrimEnd('/')))];
    }

    /// <summary>Whether each blob a conflict's stages name is binary, by the blob's hash, which names those bytes for good.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> binaryBlobs = new(StringComparer.Ordinal);

    /// <summary>Reads, by one git, the first bytes of each of these blobs not read before (<see cref="BlobProbe"/>), and
    /// keeps whether it is binary; those it could not read are read again by the next status.</summary>
    private async Task BinaryBlobsAsync(IEnumerable<string> blobs)
    {
        string[] unread = [.. blobs.Distinct(StringComparer.Ordinal).Where(b => !binaryBlobs.ContainsKey(b))];
        if (unread.Length == 0)
        {
            return;
        }
        (_, Dictionary<string, bool>? probed) = await RunAsync(Root, ["cat-file", "--batch"], output => BlobProbe.ReadAsync(output, unread, FileText.LooksBinary),
            Encoding.ASCII.GetBytes(string.Concat(unread.Select(b => $"{b}\n"))));
        foreach ((string blob, bool binary) in probed ?? [])
        {
            binaryBlobs[blob] = binary;
        }
    }

    /// <summary>What the tree is in the middle of (a merge, a rebase, a cherry-pick or a revert stopped for its
    /// conflicts, or a rebase at a step of its list that stops it), read from git's own files, and the names its two
    /// sides go by. <paramref name="branch"/> and <paramref name="head"/> are the status's, ours outside a rebase.</summary>
    public async Task<MergeState> MergeStateAsync(string? branch, string? head)
    {
        string? message = Read("MERGE_MSG");
        string ours = branch ?? GitHashes.Short(head) ?? "HEAD";
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
            // A pick stopped for conflicts leaves its message in MERGE_MSG, which an edit, a break or an exec never writes.
            RebaseStop? stop = message is null && folder == "rebase-merge" ? RebaseStop.Of(Read(System.IO.Path.Combine(folder, "done"))) : null;
            (string? stopped, string? subject) = applying is null ? (null, null) : await PersonsCommitAsync(applying);
            return new MergeState(MergeOperation.Rebase, onto is null ? ours : await NameAsync(onto), theirs, GitHashes.Short(stopped), null, stop, subject);
        }
        if (Read("CHERRY_PICK_HEAD") is { } picked)
        {
            return new MergeState(MergeOperation.CherryPick, ours, null, GitHashes.Short(picked), Message(message));
        }
        if (Read("REVERT_HEAD") is { } reverted)
        {
            return new MergeState(MergeOperation.Revert, ours, null, GitHashes.Short(reverted), Message(message));
        }
        return new MergeState(MergeOperation.None, ours, null, null, null);

        // Git's message for the merge less its comment lines (the conflicts), which a commit from a file would keep.
        static string? Message(string? written) => written is null ? null
            : string.Join('\n', written.Split('\n').Where(l => !l.StartsWith('#'))).Trim() is { Length: > 0 } kept ? kept : null;
    }

    /// <summary>The folder Git keeps what every working tree of the repository shares in (its refs, its objects): a linked
    /// worktree's Git folder names it in its commondir file, and any other's is its Git folder itself.</summary>
    public string CommonDir => commonDir ??= Read("commondir") is { Length: > 0 } common ? PathNames.Canonical(System.IO.Path.GetFullPath(common, GitDir)) : GitDir;

    private string? commonDir;

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
            ?? GitHashes.Short(hash);
        named = (hash, name);
        return name;
    }

    private (string Hash, string Name)? named;

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
        (GitResult ran, byte[]? bytes) = await RunAsync(folder, arguments, WholeAsync, answers: [NotThere]);
        return ran.Succeeded ? bytes : null;
    }

    /// <summary>Accept Yours or Accept Theirs over conflicted files: each takes that side's version (ours, or theirs with
    /// <paramref name="theirs"/>, in git's own terms), or is deleted where that side deleted it, and is marked resolved.</summary>
    public async Task<GitResult> AcceptAsync(IReadOnlyList<GitConflict> conflicts, bool theirs)
    {
        string[] kept = [.. conflicts.Where(c => theirs ? c.HasTheirs : c.HasOurs).Select(c => Relative(c.Path))];
        string[] deleted = [.. conflicts.Where(c => !(theirs ? c.HasTheirs : c.HasOurs)).Select(c => Relative(c.Path))];
        if (await UnfilteredAsync(kept) is { } unfiltered)
        {
            return unfiltered;
        }
        return await WritingOutAsync(() => Task.FromResult<IReadOnlyList<string>?>([.. kept, .. deleted]), async () =>
        {
            if (kept.Length > 0)
            {
                GitResult taken = await OnFilesAsync(["checkout", theirs ? "--theirs" : "--ours"], kept);
                if (!taken.Succeeded)
                {
                    return taken;
                }
                GitResult added = await OnFilesAsync(["add"], kept);
                if (!added.Succeeded)
                {
                    return added;
                }
            }
            return deleted.Length > 0 ? await OnFilesAsync(["rm", "-q", "-f"], deleted) : new GitResult(0, string.Empty, string.Empty);
        });
    }

    /// <summary>Marks a conflicted file resolved with what is on disk now (the merge's result written there).</summary>
    public async Task<GitResult> MarkResolvedAsync(string path) =>
        await UnfilteredAsync([Relative(path)]) ?? await RunAsync(Root, ["add", "--", Literal(Relative(path))]);

    /// <summary>Abort Merge, Abort Rebase and the like: the tree back as it was before the operation began.</summary>
    public Task<GitResult> AbortAsync(MergeOperation operation)
    {
        string command = operation.Command() ?? throw new ArgumentException("Nothing is under way to abort", nameof(operation));
        return WritingOutAsync(() => AbortedAsync(operation), () => RunAsync(Root, [command, "--abort"]));
    }

    /// <summary>The files an abort writes out: those that differ from the commit it puts the tree back to (a rebase's branch
    /// as it was, a sequence of picks' first head, else HEAD), and those its autostash puts back.</summary>
    private async Task<IReadOnlyList<string>?> AbortedAsync(MergeOperation operation)
    {
        string? back = operation switch
        {
            MergeOperation.Rebase => RebaseRead("orig-head"),
            MergeOperation.CherryPick or MergeOperation.Revert => Read(System.IO.Path.Combine("sequencer", "head")),
            _ => null,
        };
        return AllOf(await DifferingAsync(back ?? "HEAD"), await AutostashedAsync());
    }

    /// <summary>Continue Rebase: the resolved commit made with the message it had, and the rest replayed.</summary>
    public Task<GitResult> ContinueRebaseAsync(Signer? signer = null) =>
        WritingOutAsync(ContinuedAsync, () => RunSignedAsync(["rebase", "--continue"], signer, NoEditor));

    /// <summary>The files a rebase going on writes out: any its branch's own commits changed, which those still to replay
    /// are among, and those its autostash puts back at its end.</summary>
    private async Task<IReadOnlyList<string>?> ContinuedAsync() =>
        AllOf(RebaseRead("onto") is { } onto && RebaseRead("orig-head") is { } head ? await DifferingAsync($"{onto}...{head}") : null, await AutostashedAsync());

    /// <summary>The files the autostash of the merge or rebase under way puts back as it ends (<see cref="UnstashedAsync"/>):
    /// none without one.</summary>
    private async Task<IReadOnlyList<string>?> AutostashedAsync() =>
        (RebaseRead("autostash") ?? Read("MERGE_AUTOSTASH")) is { } stash ? await UnstashedAsync(stash) : [];

    /// <summary>A file of the rebase under way, from whichever of git's two folders for one it keeps it in; null with none.</summary>
    private string? RebaseRead(string name) => Read(System.IO.Path.Combine("rebase-merge", name)) ?? Read(System.IO.Path.Combine("rebase-apply", name));

    /// <summary>The environment of a git that makes commits with the messages they had, opening no editor.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoEditor = new Dictionary<string, string> { [GitVariables.Editor] = "true" };

    /// <summary>A git that may make commits, each signed as the person's config has it through <paramref name="signer"/>'s
    /// own gpg, a key's passphrase asked of its asker.</summary>
    private Task<GitResult> RunSignedAsync(IReadOnlyList<string> arguments, Signer? signer, IReadOnlyDictionary<string, string>? environment = null)
    {
        Dictionary<string, string> given = new(environment ?? new Dictionary<string, string>());
        foreach ((string name, string value) in signer?.Signing.Environment ?? new Dictionary<string, string>())
        {
            given[name] = value;
        }
        return RunAsync(Root, [.. signer?.Signing.Before ?? [], .. arguments], environment: given, asker: signer?.Asker);
    }

    /// <summary>The branches, remotes and tags the log shows and the commit each names, as a hash of git's bytes for them
    /// read off the caller's thread: with HEAD's commit, what changes when the history does (a commit, a checkout, a
    /// fetch), and not when a file is saved or stashed.</summary>
    public Task<UInt128> RefsAsync() => BackgroundWork.Run(async () =>
    {
        using GitOutput refs = await OutputOfAsync(Root, ["for-each-ref", "--format=%(objectname) %(refname)", "refs/heads", "refs/remotes", "refs/tags"]);
        return Hash(refs.Bytes);
    });

    private static UInt128 Hash(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt128LittleEndian(hash);
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
        GitResult refs = await RunAsync(Root, ["for-each-ref", BranchFormat, "refs/heads", "refs/remotes", "refs/tags"]);
        GitResult remotes = await RunAsync(Root, ["remote"]);
        string[] named = remotes.Succeeded ? remotes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        return (refs.Succeeded ? BranchesOf(refs.Output) : [], named);
    }

    /// <summary>The local branches, the one committed to last first, each as <see cref="BranchTreeAsync"/> reads it. Read and
    /// parsed off the caller's thread.</summary>
    public Task<IReadOnlyList<BranchInfo>> LocalBranchesAsync() => BackgroundWork.Run<IReadOnlyList<BranchInfo>>(async () =>
    {
        GitResult refs = await RunAsync(Root, ["for-each-ref", "--sort=-committerdate", BranchFormat, "refs/heads"]);
        return refs.Succeeded ? BranchesOf(refs.Output) : [];
    });

    /// <summary>What a branch or tag is read as, in the fields <see cref="BranchesOf"/> takes.</summary>
    private const string BranchFormat =
        "--format=%(HEAD)%1f%(refname)%1f%(objectname)%1f%(*objectname)%1f%(upstream:remotename)%1f%(upstream:remoteref)%1f%(refname:short)%1f%(worktreepath)";

    /// <summary>The branches and tags for-each-ref wrote in <see cref="BranchFormat"/>, a line each, in its order.</summary>
    private static List<BranchInfo> BranchesOf(string output)
    {
        List<BranchInfo> found = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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
                    field[4].Length > 0 ? field[4] : null, field[5].Length > 0 ? field[5] : null, field.Length > 7 && field[7].Length > 0 ? field[7] : null));
            }
        }
        return found;
    }

    /// <summary>The history of every branch, remote and tag and of HEAD, newest first with a branch's commits together, at
    /// most this many; or, given <paramref name="revisions"/>, theirs alone (a branch the log is filtered to,
    /// "main..feature" for the commits of feature main lacks). Not --all, which takes in the stash's own commits (and
    /// notes'). Read and parsed off the caller's thread.</summary>
    public Task<IReadOnlyList<CommitInfo>> LogAsync(int most = LogLength, IReadOnlyList<string>? revisions = null)
    {
        string[] arguments = ["log", "--topo-order", $"--max-count={most}", CommitFormat, .. LogRevisions(revisions), "--"];
        return BackgroundWork.Run<IReadOnlyList<CommitInfo>>(async () =>
        {
            using GitOutput log = await OutputOfAsync(Root, arguments);
            // A branch with no commits yet has no history to read.
            return log.Succeeded ? Commits(log.Bytes) : [];
        });
    }

    /// <summary>How many commits the log reads unless asked for more.</summary>
    public const int LogLength = 2000;

    /// <summary>What the log is of: every branch, remote and tag and HEAD, or the revisions given.</summary>
    private static string[] LogRevisions(IReadOnlyList<string>? revisions) =>
        // An unborn HEAD (a new repository, an orphan branch) is left out rather than failing the whole log.
        revisions is null ? ["--branches", "--remotes", "--tags", "--ignore-missing", "HEAD"] : ["--end-of-options", .. revisions];

    /// <summary>How many commits <see cref="LogAsync"/> reads to be sure of reaching a commit: those of its log that are
    /// not in the commit's history, every one of which may come before it, and the commit. Null where git does not have
    /// the commit.</summary>
    public async Task<int?> LogDepthAsync(string hash, IReadOnlyList<string>? revisions = null)
    {
        GitResult above = await RunAsync(Root, ["rev-list", "--count", .. LogRevisions(revisions), $"^{hash}", "--"], answers: [NotThere]);
        return above.Succeeded && int.TryParse(above.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture, out int count) ? count + 1 : null;
    }

    /// <summary>The whole hash of the commit a hash or its first characters (at least 4) name, as a log shows it; null where
    /// git has no such commit, or the characters name more than one object, or are no hash's.</summary>
    public async Task<string?> CommitNamedAsync(string hash)
    {
        if (hash.Length is < 4 or > 64 || !hash.All(char.IsAsciiHexDigit))
        {
            return null;
        }
        GitResult named = await RunAsync(Root, ["rev-parse", "-q", "--verify", $"{hash}^{{commit}}"], answers: [1, NotThere]);
        return named.Succeeded ? named.Output.Trim() : null;
    }

    /// <summary>The commit HEAD names, as the log reads it, or null before the first commit.</summary>
    public async Task<CommitInfo?> HeadAsync()
    {
        GitResult log = await RunAsync(Root, ["log", "-1", CommitFormat, "HEAD", "--"], answers: [NotThere]);
        return log.Succeeded ? Commits(Encoding.UTF8.GetBytes(log.Output)).FirstOrDefault() : null;
    }

    private const string CommitFormat = "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s%x1e";

    /// <summary>A history's commits: each begins a record, and what follows its last field is what it did there.</summary>
    private const string HistoryFormat = "--format=%x1e%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s%x1f";

    /// <summary>What ends each record of <see cref="CommitFormat"/> and <see cref="HistoryFormat"/>, and each field.</summary>
    private const byte RecordEnd = 0x1e;
    private const byte FieldEnd = 0x1f;

    /// <summary>A log's commits from git's bytes, each record's fields found in place and only those kept made strings.</summary>
    private static List<CommitInfo> Commits(ReadOnlySpan<byte> output)
    {
        List<CommitInfo> commits = [];
        Hashes hashes = new();
        Span<Range> fields = stackalloc Range[8];
        while (!output.IsEmpty)
        {
            // A log writes a line break after each record's end.
            ReadOnlySpan<byte> record = Record(ref output).Trim((byte)'\n');
            if (Fields(record, fields) >= 7)
            {
                commits.Add(Commit(record, fields, hashes));
            }
        }
        return commits;
    }

    /// <summary>The bytes up to the next record's end, which <paramref name="output"/> is moved past.</summary>
    private static ReadOnlySpan<byte> Record(ref ReadOnlySpan<byte> output)
    {
        int end = output.IndexOf(RecordEnd);
        ReadOnlySpan<byte> record = end < 0 ? output : output[..end];
        output = end < 0 ? [] : output[(end + 1)..];
        return record;
    }

    /// <summary>A record's fields between 0x1F bytes, at most as many as <paramref name="fields"/> holds, the last
    /// running to the record's end; how many there are.</summary>
    private static int Fields(ReadOnlySpan<byte> record, Span<Range> fields)
    {
        int count = 0;
        int start = 0;
        while (count < fields.Length - 1 && record[start..].IndexOf(FieldEnd) is int at and >= 0)
        {
            fields[count++] = new Range(start, start + at);
            start += at + 1;
        }
        fields[count++] = new Range(start, record.Length);
        return count;
    }

    private static CommitInfo Commit(ReadOnlySpan<byte> record, ReadOnlySpan<Range> field, Hashes hashes) => new(
        hashes.Of(record[field[0]]),
        Parts(record[field[1]], " "u8, hashes),
        Text(record[field[2]]),
        Text(record[field[3]]),
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(record[field[4]], System.Globalization.CultureInfo.InvariantCulture)),
        Parts(record[field[5]], ", "u8, null),
        Text(record[field[6]]));

    private static string Text(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);

    /// <summary>A field's parts between separators, empty ones passed over: hashes, each made once by
    /// <paramref name="hashes"/>, or with none the branches and tags at a commit, HEAD's arrow taken off the one it names.</summary>
    private static string[] Parts(ReadOnlySpan<byte> field, ReadOnlySpan<byte> separator, Hashes? hashes)
    {
        int count = 0;
        for (ReadOnlySpan<byte> rest = field; !rest.IsEmpty;)
        {
            int at = rest.IndexOf(separator);
            count += at == 0 ? 0 : 1;
            rest = at < 0 ? [] : rest[(at + separator.Length)..];
        }
        if (count == 0)
        {
            return [];
        }
        string[] parts = new string[count];
        count = 0;
        for (ReadOnlySpan<byte> rest = field; !rest.IsEmpty;)
        {
            int at = rest.IndexOf(separator);
            ReadOnlySpan<byte> part = at < 0 ? rest : rest[..at];
            if (!part.IsEmpty)
            {
                parts[count++] = hashes is not null ? hashes.Of(part) : Text(part.StartsWith("HEAD -> "u8) ? part[8..] : part);
            }
            rest = at < 0 ? [] : rest[(at + separator.Length)..];
        }
        return parts;
    }

    /// <summary>Each hash a log names made a string once, however many of its commits name it (as their own or a parent).</summary>
    private sealed class Hashes
    {
        private readonly Dictionary<string, string> made = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> lookup;

        public Hashes() => lookup = made.GetAlternateLookup<ReadOnlySpan<char>>();

        public string Of(ReadOnlySpan<byte> hash)
        {
            // Never more characters than bytes; a SHA-256 hash is 64.
            Span<char> chars = stackalloc char[64];
            if (hash.Length > chars.Length)
            {
                return Text(hash);
            }
            ReadOnlySpan<char> text = chars[..Encoding.UTF8.GetChars(hash, chars)];
            if (!lookup.TryGetValue(text, out string? known))
            {
                known = new string(text);
                made.Add(known, known);
            }
            return known;
        }
    }

    /// <summary>Show History's commits from HEAD, newest first, at most this many, each with what it did there: a file's
    /// followed through its renames (--follow), a folder's, or those that changed a file's lines (-L), renames followed
    /// too. Empty where git has none: a file it does not track, lines past the end of the last commit's.</summary>
    public async Task<IReadOnlyList<HistoryCommit>> HistoryAsync(HistoryScope scope, int most = 2000)
    {
        string relative = Relative(scope.From ?? scope.Path);
        string count = $"--max-count={most}";
        if (scope.IsLines)
        {
            // -L takes no pathspec: its file is whatever follows the range. The prefixes are set, whatever diff.noprefix says.
            return await HistoriesAsync(["-c", "core.quotePath=false", "log", $"-L{scope.FirstLine + 1},{scope.LastLine + 1}:{relative}",
                "--no-color", "--src-prefix=a/", "--dst-prefix=b/", HistoryFormat, count, "HEAD"], PatchFile);
        }
        // Literal: a name with brackets or a star is that file alone, never a pattern.
        return await HistoriesAsync(["--literal-pathspecs", "log", .. scope.IsFolder ? Array.Empty<string>() : ["--follow"],
            "--name-status", "-z", "-M", HistoryFormat, count, "HEAD", "--", relative], NameStatus);
    }

    /// <summary>A history's commits as git lists them, read and parsed off the caller's thread; none when git fails.</summary>
    private Task<IReadOnlyList<HistoryCommit>> HistoriesAsync(string[] arguments, Func<string, List<FileChange>> changes) =>
        BackgroundWork.Run<IReadOnlyList<HistoryCommit>>(async () =>
        {
            using GitOutput log = await OutputOfAsync(Root, arguments);
            return log.Succeeded ? Histories(log.Bytes, changes) : [];
        });

    private static List<HistoryCommit> Histories(ReadOnlySpan<byte> output, Func<string, List<FileChange>> changes)
    {
        List<HistoryCommit> commits = [];
        Hashes hashes = new();
        Span<Range> fields = stackalloc Range[8];
        while (!output.IsEmpty)
        {
            ReadOnlySpan<byte> record = Record(ref output);
            if (Fields(record, fields) == 8)
            {
                commits.Add(new HistoryCommit(Commit(record, fields, hashes), changes(Text(record[fields[7]]))));
            }
        }
        return commits;
    }

    /// <summary>Files as --name-status -z lists them: a status, then the path, or a rename's or copy's two.</summary>
    private List<FileChange> NameStatus(string output)
    {
        DiskNames names = new(Root);
        string Full(string relative) => names.Full(relative);
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

    /// <summary>The files a commit changed against its first parent (all of them for the first commit, and for a merge
    /// those the branches it merged brought in), renames found.</summary>
    public async Task<IReadOnlyList<FileChange>> CommitFilesAsync(CommitInfo commit)
    {
        // Of a merge's one commit diff-tree prints nothing: it is compared with its first parent by name.
        string[] commits = commit.Parents.Count > 1 ? [commit.Parents[0], commit.Hash] : [commit.Hash];
        GitResult files = await RunAsync(Root, ["diff-tree", "--no-commit-id", "--name-status", "-r", "-z", "-M", "--root", "--end-of-options", .. commits]);
        return files.Succeeded ? NameStatus(files.Output) : [];
    }

    /// <summary>Git blame for a file, line by line, over the bytes given (the editor's text, unsaved changes and all, in
    /// the file's encoding as a save would write it), so a line typed since is one not committed yet; null for a file Git
    /// does not track. Git takes them through the file's clean filter, as it would the file on disk.</summary>
    public async Task<IReadOnlyList<BlameLine>?> BlameAsync(string path, byte[] contents)
    {
        (GitResult blamed, byte[]? output) = await RunAsync(Root, ["blame", "--porcelain", "--contents", "-", "--", Relative(path)], WholeAsync, contents);
        if (!blamed.Succeeded || output is null)
        {
            return null;
        }
        Dictionary<string, (string Author, DateTimeOffset Time, string Summary)> commits = [];
        List<BlameLine> lines = [];
        string? hash = null;
        (string Author, long Time, string Summary) reading = (string.Empty, 0, string.Empty);
        // A line's own text (after its tab) is in the file's encoding, and passed over; git's own fields are UTF-8.
        foreach (string line in Encoding.UTF8.GetString(output).Split('\n'))
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
            if (GitHashes.IsWhole(words[0]))
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

    /// <summary>A file's bytes at a revision (a hash, a branch, HEAD), as <see cref="BytesAtAsync(string, IReadOnlyList{string})"/>
    /// reads them; null when it had no such file.</summary>
    public async Task<byte[]?> BytesAtAsync(string revision, string path) => (await BytesAtAsync(revision, [path]))[path];

    /// <summary>Files' bytes at a revision as checking each out would write it (its filters and line breaks: smudged by
    /// LFS or git-crypt, CRLF under core.autocrlf), or as git keeps it where a filter cannot run here (git-lfs not
    /// installed); each null where the revision has no such file. Two gits read them however many there are.</summary>
    public async Task<IReadOnlyDictionary<string, byte[]?>> BytesAtAsync(string revision, IReadOnlyList<string> paths)
    {
        Dictionary<string, byte[]?> read = [];
        List<(string Path, string Name)> batched = [];
        foreach (string path in paths.Distinct())
        {
            string name = Relative(path);
            if (!IsInside(path))
            {
                // A path outside the tree ends the whole batch ("outside repository").
                read[path] = null;
            }
            else if (name.AsSpan().IndexOfAny('\n', '\r') >= 0)
            {
                // The batch takes a name per line.
                read[path] = await FilteredAsync($"{revision}:{name}", null);
            }
            else
            {
                batched.Add((path, name));
            }
        }
        if (batched.Count == 0)
        {
            return read;
        }
        bool composed = batched.Any(b => !Ascii.IsValid(b.Name)) && await ComposesAsync();
        batched = [.. batched.Select(b => (b.Path, GitForm(b.Name, composed)))];
        // Each blob by its name first: a filtered batch takes an object and a path, parted at the first space.
        (_, byte[]? checks) = await RunAsync(Root, ["cat-file", "--batch-check"], WholeAsync,
            Encoding.UTF8.GetBytes(string.Concat(batched.Select(b => $"{revision}:{b.Name}\n"))));
        string[] found = Encoding.UTF8.GetString(checks ?? []).Split('\n');
        List<(string Path, string Name, string Blob)> blobs = [];
        for (int i = 0; i < batched.Count; i++)
        {
            // "<object> blob <size>", or "<name> missing" (a submodule's commit is no file's bytes).
            string[] header = i < found.Length ? found[i].Split(' ') : [];
            read[batched[i].Path] = null;
            if (header is [{ } blob, "blob", _] && blob.All(char.IsAsciiHexDigit))
            {
                blobs.Add((batched[i].Path, batched[i].Name, blob));
            }
        }
        await FilterAsync(blobs, read);
        return read;
    }

    /// <summary>Blobs' bytes as checking each out at its path would write them, by one git: its answer for each object
    /// gives the size it has in git rather than the size filtered, so each is followed by an object no repository has,
    /// named at random, whose "missing" ends it. One the batch did not give (a required filter that failed ends it) is
    /// read on its own.</summary>
    private async Task FilterAsync(List<(string Path, string Name, string Blob)> blobs, Dictionary<string, byte[]?> read)
    {
        List<(string Path, string Name, string Blob, string End)> asked = [];
        StringBuilder input = new();
        foreach ((string path, string name, string blob) in blobs)
        {
            // The path is what follows the first run of spaces, so one that begins with a space is read on its own.
            if (name.Length > 0 && !char.IsWhiteSpace(name[0]))
            {
                string end = $"x{Guid.NewGuid():N}";
                asked.Add((path, name, blob, end));
                input.Append(System.Globalization.CultureInfo.InvariantCulture, $"{blob} {name}\n{end} -\n");
            }
        }
        byte[] output = asked.Count == 0 ? [] : (await RunAsync(Root, ["cat-file", "--batch", "--filters"], WholeAsync, Encoding.UTF8.GetBytes(input.ToString()))).Read ?? [];
        int at = 0;
        HashSet<string> given = new(StringComparer.Ordinal);
        foreach ((string path, _, string blob, string end) in asked)
        {
            // "<object> blob <size>", the filtered bytes and a line break, then "<end> missing".
            int line = Array.IndexOf(output, (byte)'\n', at);
            if (line < 0 || !output.AsSpan(at, line - at).StartsWith(Encoding.ASCII.GetBytes($"{blob} blob ")))
            {
                break;
            }
            byte[] ending = Encoding.ASCII.GetBytes($"\n{end} missing\n");
            int ends = output.AsSpan(line + 1).IndexOf(ending);
            if (ends < 0)
            {
                break;
            }
            read[path] = output.AsSpan(line + 1, ends).ToArray();
            given.Add(path);
            at = line + 1 + ends + ending.Length;
        }
        foreach ((string path, string name, string blob) in blobs)
        {
            if (!given.Contains(path))
            {
                read[path] = await FilteredAsync(blob, name);
            }
        }
    }

    /// <summary>One object's bytes as checking it out at its path would write them (the path in the name, or given), else
    /// as git keeps them where its filter fails; null where there is no such object.</summary>
    private async Task<byte[]?> FilteredAsync(string name, string? path) =>
        await BytesAsync(Root, ["cat-file", "--filters", .. path is null ? Array.Empty<string>() : [$"--path={path}"], name])
            ?? await BytesAsync(Root, ["cat-file", "blob", name]);

    /// <summary>A file's bytes at a revision as <see cref="BytesAtAsync(string, IReadOnlyList{string})"/> reads them, at the
    /// first of <paramref name="paths"/> it has (a file renamed since, where it was), taken as git writes them so a binary
    /// file's are only counted (<see cref="StoredBytes"/>); null where it has none of them.</summary>
    public Task<StoredBytes?> StoredAtAsync(string revision, IReadOnlyList<string> paths) => BackgroundWork.Run(async () =>
        await StoredAsync(revision, paths, StoredRead.ReadAsync) is (true, { } read) ? read : null);

    /// <summary>How the file at <paramref name="disk"/> differs from a revision's where either is binary, both read as they
    /// come and never held whole: the revision's at the first of <paramref name="paths"/> it has, as checking it out would
    /// write it; null where neither is.</summary>
    public Task<BinaryChange?> BinaryChangeAsync(string revision, IReadOnlyList<string> paths, string disk) => BackgroundWork.Run(async () =>
        await StoredAsync(revision, paths, stored => DiskChangeAsync(stored, disk)) is (true, var change) ? change : await DiskChangeAsync(null, disk));

    /// <summary>How the file at <paramref name="disk"/> differs from a revision's bytes read already (null where it has no
    /// such file) where either is binary, the disk's read as they come; null where neither is.</summary>
    public static Task<BinaryChange?> BinaryChangeAsync(byte[]? stored, string disk) =>
        BackgroundWork.Run(() => DiskChangeAsync(stored is null ? null : new MemoryStream(stored, writable: false), disk));

    private static async Task<BinaryChange?> DiskChangeAsync(Stream? stored, string disk)
    {
        await using Stream? now = File.Exists(disk) ? FileText.OpenRead(disk) : null;
        return await StoredRead.CompareAsync(stored, now);
    }

    /// <summary>Hands <paramref name="read"/> a revision's file at the first of <paramref name="paths"/> it has, as git
    /// writes it checked out there (or as git keeps it where its filter cannot run here), with its answer; false where the
    /// revision has none of them. A blob is named by one git cat-file --batch-check, so a file it lacks fails no git.</summary>
    private async Task<(bool Found, T Answer)> StoredAsync<T>(string revision, IReadOnlyList<string> paths, Func<Stream, Task<T>> read)
    {
        foreach (string path in paths.Distinct().Where(IsInside))
        {
            string name = Relative(path);
            // The batch takes a name per line.
            string[][] reads = name.AsSpan().IndexOfAny('\n', '\r') >= 0
                ? [["cat-file", "--filters", $"{revision}:{name}"], ["cat-file", "blob", $"{revision}:{name}"]]
                : await BlobAtAsync(revision, name) is { } blob ? [["cat-file", "--filters", $"--path={name}", blob], ["cat-file", "blob", blob]] : [];
            foreach (string[] arguments in reads)
            {
                // An answer may be null (a reader's no), which is an answer all the same.
                if (await RunAsync(Root, arguments, read, answers: [NotThere]) is ({ Succeeded: true }, var answer))
                {
                    return (true, answer!);
                }
            }
        }
        return (false, default!);
    }

    /// <summary>The blob a revision has at a name from the tree's top, or null where it has no file there (a submodule's
    /// commit is no file's).</summary>
    private async Task<string?> BlobAtAsync(string revision, string name)
    {
        bool composed = !Ascii.IsValid(name) && await ComposesAsync();
        (_, byte[]? check) = await RunAsync(Root, ["cat-file", "--batch-check"], WholeAsync, Encoding.UTF8.GetBytes($"{revision}:{GitForm(name, composed)}\n"));
        return Encoding.UTF8.GetString(check ?? []).TrimEnd('\n').Split(' ') is [{ } blob, "blob", _] && blob.All(char.IsAsciiHexDigit) ? blob : null;
    }

    /// <summary>Whether git stores a file as a revision has it whichever of LF and CRLF ends its lines (core.autocrlf, a
    /// text or eol attribute), asked of git rather than of its settings: the revision's text (<paramref name="version"/>, as
    /// checking it out writes it) with each, hashed as git would store it at <paramref name="path"/>, against the blob the
    /// revision has at the first of <paramref name="paths"/> it has. False where either is stored otherwise, the text has
    /// no line break to ask by, a filter's program would take it in (one is never run for this), or git cannot say.</summary>
    public Task<bool> BreaksStoredAlikeAsync(string revision, IReadOnlyList<string> paths, string path, FileText version) => BackgroundWork.Run(async () =>
    {
        if (version.CrBreaks || !version.Text.Contains('\n', StringComparison.Ordinal) || !IsInside(path))
        {
            return false;
        }
        string name = GitForm(Relative(path), await ComposesAsync());
        Task<string?> blob = BlobOfAsync();
        // A text made only to ask is handed to no filter's program, which may keep what it is given.
        GitResult attributes = await RunAsync(Root, ["check-attr", "-z", "--stdin", "filter"], $"{name}\0");
        if (await blob is not { } stored || !attributes.Succeeded || attributes.Output.Split('\0') is not [_, _, "unspecified" or "unset" or "set", ..])
        {
            return false;
        }
        string?[] hashes = await Task.WhenAll(HashAsync(LineEndings.In(version.Text, "\n")), HashAsync(LineEndings.In(version.Text, "\r\n")));
        return hashes.All(hash => hash == stored);

        async Task<string?> BlobOfAsync()
        {
            foreach (string at in paths.Distinct().Where(IsInside))
            {
                string relative = Relative(at);
                // The batch takes a name per line.
                if (relative.AsSpan().IndexOfAny('\n', '\r') >= 0)
                {
                    return null;
                }
                if (await BlobAtAsync(revision, relative) is { } found)
                {
                    return found;
                }
            }
            return null;
        }

        // A path on git's command line, as hash-object takes none on stdin beside the bytes: --path picks their conversions.
        async Task<string?> HashAsync(string text) =>
            await RunAsync(Root, ["hash-object", "--stdin", $"--path={name}"], TextAsync, FileText.Encoded(text, version.Encoding, version.HasBom)) is ({ Succeeded: true }, { } hash)
                ? hash.Trim()
                : null;
    });

    /// <summary>Whether a path is in the working tree, where git has a last commit's text for it.</summary>
    public bool IsInside(string path) => PathNames.Within(path, Root);

    /// <summary>What the index has at moved paths (files, or folders' files) moved to where they now are, in order, as git
    /// mv moves them with the files: each keeps its staged content under its new name, so the status shows the move as a
    /// rename and the change the file has beyond that as its own. One git reads the index and one writes it, however many
    /// moved; a move into or out of the tree, of a path the index does not have, or of one in conflict, is left alone.</summary>
    public async Task<GitResult> MoveInIndexAsync(IReadOnlyList<(string From, string To)> moves) =>
        await MoveInIndexAsync(moves, environment: null) is { Ran: var ran } ? ran : Done;

    /// <summary><see cref="MoveInIndexAsync(IReadOnlyList{ValueTuple{string, string}})"/> in the index the environment names
    /// (GIT_INDEX_FILE), with whether any move took an entry.</summary>
    private async Task<(GitResult Ran, bool Moved)> MoveInIndexAsync(IReadOnlyList<(string From, string To)> moves, IReadOnlyDictionary<string, string>? environment)
    {
        List<(string From, string To)> inside = [.. moves.Where(m => IsInside(m.From) && IsInside(m.To)).Select(m => (Relative(m.From), Relative(m.To)))];
        if (inside.Count == 0)
        {
            return (Done, false);
        }
        bool composed = inside.Any(m => !Ascii.IsValid(m.From) || !Ascii.IsValid(m.To)) && await ComposesAsync();
        inside = [.. inside.Select(m => (GitForm(m.From, composed), GitForm(m.To, composed)))];
        string plan;
        using (GitOutput staged = await OutputOfAsync(Root, ["ls-files", "--stage", "-z"], environment))
        {
            if (!staged.Succeeded)
            {
                return (new GitResult(1, string.Empty, "git could not read the index"), false);
            }
            plan = IndexMoves.Plan(staged.Bytes, inside);
        }
        return plan.Length == 0 ? (Done, false) : (await RunAsync(Root, ["update-index", "-z", "--index-info"], plan, environment), true);
    }

    /// <summary>What a call that had nothing to do gives.</summary>
    private static readonly GitResult Done = new(0, string.Empty, string.Empty);

    /// <summary>Update Project's first half: the remote the branch's upstream is on fetched (origin where it has none),
    /// which changes no file of the tree. Canceled, git is ended with all it started and the task is canceled; what git
    /// asks of the person on the way (a login, a key's passphrase), here and wherever a remote is reached, goes to
    /// <paramref name="asker"/>, and with none git asks nobody.</summary>
    public Task<GitResult> FetchUpstreamAsync(CancellationToken cancel = default, GitAsker? asker = null) => RunAsync(Root, ["fetch"], cancel: cancel, asker: asker);

    /// <summary>How the person's git config has Update Project bring the upstream's commits in, for this branch.</summary>
    public async Task<UpdatePlan> UpdatePlanAsync(string branch)
    {
        GitResult set = await RunAsync(Root, ["config", "-z", "--get-regexp", @"^(pull\.(rebase|ff)|branch\..*\.rebase)$"], answers: [1]);
        return UpdatePlan.Of(set.Succeeded ? set.Output : string.Empty, branch);
    }

    /// <summary>The diff, merge and filter drivers git's configuration declares for the repository, each by its kind and
    /// name (<c>[diff "csharp-own"]</c>); null where git could not say.</summary>
    public async Task<IReadOnlySet<(string Kind, string Name)>?> DriversAsync()
    {
        GitResult set = await RunAsync(Root, ["config", "-z", "--name-only", "--get-regexp", $"^({GitAttributeNames.Diff}|{GitAttributeNames.Merge}|{GitAttributeNames.Filter})\\."], answers: [1]);
        if (!set.Succeeded && set.Code != 1)
        {
            return null;
        }
        HashSet<(string, string)> drivers = [];
        foreach (string key in set.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int first = key.IndexOf('.', StringComparison.Ordinal);
            int last = key.LastIndexOf('.');
            if (first > 0 && last > first)
            {
                drivers.Add((key[..first], key[(first + 1)..last]));
            }
        }
        return drivers;
    }

    /// <summary>Update Project's second half, once the upstream is fetched: the branch merged with it or rebased onto it as
    /// <paramref name="plan"/> says, its uncommitted changes to tracked files set aside first and put back after
    /// (--autostash); a merge or rebase stopped for conflicts puts them back once it is finished or aborted.</summary>
    public Task<GitResult> IntegrateAsync(UpdatePlan plan, Signer? signer = null) => WritingOutAsync(() => IntegratedAsync(plan), () => plan.Rebase
        ? RunSignedAsync(["rebase", "--autostash", .. plan.RebaseMerges ? new[] { "--rebase-merges" } : []], signer, NoEditor)
        : RunSignedAsync(["merge", "--no-edit", "--autostash", .. plan.FastForward is { } fastForward ? new[] { fastForward } : [], "@{upstream}"], signer));

    /// <summary>The files an update writes out: those with uncommitted changes, set aside and put back, and those the merge
    /// with the upstream, or the rebase onto it, writes.</summary>
    private async Task<IReadOnlyList<string>?> IntegratedAsync(UpdatePlan plan) =>
        AllOf(await DifferingAsync("HEAD"), plan.Rebase ? await RebasedAsync("@{upstream}", "HEAD") : await MergedAsync("@{upstream}"));

    /// <summary>How many commits the branch has that its upstream lacks, and the upstream has that it lacks; null where it
    /// tracks none.</summary>
    public async Task<(int Ahead, int Behind)?> DivergenceAsync()
    {
        GitResult counted = await RunAsync(Root, ["rev-list", "--left-right", "--count", "HEAD...@{upstream}", "--"], answers: [NotThere]);
        string[] counts = counted.Output.Split('\t', StringSplitOptions.TrimEntries);
        return counted.Succeeded && counts.Length == 2
            && int.TryParse(counts[0], System.Globalization.CultureInfo.InvariantCulture, out int ahead)
            && int.TryParse(counts[1], System.Globalization.CultureInfo.InvariantCulture, out int behind)
            ? (ahead, behind) : null;
    }

    /// <summary>Whether a file git tracks has changes not committed, staged or not.</summary>
    public async Task<bool> HasTrackedChangesAsync() =>
        (await RunAsync(Root, ["status", "--porcelain", "--untracked-files=no"])).Output.Trim().Length > 0;

    /// <summary>Every remote fetched, its branches gone from it pruned; canceled, as <see cref="FetchUpstreamAsync"/>.</summary>
    public Task<GitResult> FetchAsync(CancellationToken cancel = default, GitAsker? asker = null) => RunAsync(Root, ["fetch", "--all", "--prune"], cancel: cancel, asker: asker);

    /// <summary>The last commit's message, or null before the first commit.</summary>
    public async Task<string?> LastMessageAsync()
    {
        GitResult log = await RunAsync(Root, ["log", "-1", "--format=%B"], answers: [NotThere]);
        return log.Succeeded ? log.Output.TrimEnd() : null;
    }

    /// <summary>Commits these files' changes (new ones added first) and nothing else the index holds, with a message,
    /// amending the last commit when asked (with no files, its message alone), signed and signed off as
    /// <paramref name="signing"/> says, a key's passphrase asked of <paramref name="asker"/>. Finishing a merge (or a
    /// cherry-pick or a revert stopped for conflicts), which git commits only whole, the files
    /// <paramref name="excluded"/> are taken out of it and kept as changes.</summary>
    public async Task<GitResult> CommitAsync(IReadOnlyList<string> paths, string message, bool amend = false, bool merging = false, IReadOnlyList<string>? excluded = null,
        CommitSigning? signing = null, GitAsker? asker = null, IReadOnlyDictionary<string, PartialCommit>? partial = null)
    {
        if (await UnfilteredAsync([.. paths.Select(Relative).Distinct().Where(StillThere)]) is { } unfiltered)
        {
            return unfiltered;
        }
        if (partial is { Count: > 0 })
        {
            return merging ? new GitResult(1, string.Empty, "A merge commits whole files: choose whole files while it is under way")
                : await CommitPartialAsync(paths, message, amend, signing ?? CommitSigning.AsConfigured, asker, partial);
        }
        signing ??= CommitSigning.AsConfigured;
        string[] relative = [.. paths.Select(Relative).Distinct()];
        string[] there = [.. relative.Where(StillThere)];
        // What the index stages of the files before they are added, put back if the commit is not made; none before a first commit.
        Dictionary<string, StagedEntry>? before = !merging && relative.Length > 0 && await StagedAsync() is ({ Succeeded: true }, var staged) ? staged : null;
        // No paths is an amend of the message alone: "add -A --" with none would add the whole tree.
        if (there.Length > 0)
        {
            GitResult add = await OnFilesAsync(["add", "-A"], there);
            if (!add.Succeeded)
            {
                return add;
            }
        }
        // A rename staged already leaves its old name in HEAD alone, which "add" refuses as matching no file.
        string[] gone = [.. relative.Except(there)];
        if (gone.Length > 0)
        {
            GitResult removed = await OnFilesAsync(["rm", "--cached", "-r", "-q", "--ignore-unmatch"], gone);
            if (!removed.Succeeded)
            {
                return await PutBackAsync(removed, relative, before);
            }
        }
        string[] left = merging ? [.. (excluded ?? []).Select(Relative).Distinct().Except(relative)] : [];
        if (left.Length > 0)
        {
            GitResult unstaged = await OnFilesAsync(["reset", "-q"], left);
            if (!unstaged.Succeeded)
            {
                return unstaged;
            }
        }
        using CommitFile file = CommitFile.Message(message);
        string[] commit = [.. signing.Before, "commit", "-F", file.Path, .. amend ? new[] { "--amend" } : [], .. signing.After];
        // --only with no paths amends the message alone, leaving out what else the index holds (an IDE rename, a git add).
        return merging ? await RunAsync(Root, commit, environment: signing.Environment, asker: asker)
            : relative.Length > 0 ? await PutBackAsync(await OnFilesAsync([.. commit, "--only"], relative, asker, signing.Environment), relative, before)
            : await RunAsync(Root, [.. commit, .. amend ? new[] { "--only" } : []], environment: signing.Environment, asker: asker);
    }

    /// <summary>Commits what the index holds, as git commit with no files named does (amending the last commit when asked),
    /// signed and signed off as <paramref name="signing"/> says, a key's passphrase asked of <paramref name="asker"/>.</summary>
    public async Task<GitResult> CommitIndexAsync(string message, bool amend, CommitSigning? signing = null, GitAsker? asker = null)
    {
        signing ??= CommitSigning.AsConfigured;
        using CommitFile file = CommitFile.Message(message);
        return await RunAsync(Root, [.. signing.Before, "commit", "-F", file.Path, .. amend ? new[] { "--amend" } : [], .. signing.After],
            environment: signing.Environment, asker: asker);
    }

    /// <summary>Stages files whole, as git add does: each as the disk has it now, one gone taken out of the index (a rename's
    /// old name among them).</summary>
    public async Task<GitResult> StageAsync(IReadOnlyList<string> paths)
    {
        string[] relative = [.. paths.Select(Relative).Distinct()];
        string[] there = [.. relative.Where(StillThere)];
        if (await UnfilteredAsync(there) is { } unfiltered)
        {
            return unfiltered;
        }
        if (there.Length > 0 && await OnFilesAsync(["add", "-A"], there) is { Succeeded: false } unadded)
        {
            return unadded;
        }
        string[] gone = [.. relative.Except(there)];
        return gone.Length > 0 ? await OnFilesAsync(["rm", "--cached", "-r", "-q", "--ignore-unmatch"], gone) : Done;
    }

    /// <summary>Unstages files whole: the index given back what the last commit has at each, none where it has none; a moved
    /// file (<paramref name="moves"/>, where it was and is) whose move the index holds keeps it there, its new name holding
    /// what the last commit has at its old one, as a move in the Explorer leaves the index.</summary>
    public async Task<GitResult> UnstageAsync(IReadOnlyList<string> paths, IReadOnlyList<(string From, string To)> moves)
    {
        string[] relative = [.. paths.Select(Relative).Distinct()];
        if (await HeadAsync() is null)
        {
            string[] every = [.. relative, .. moves.SelectMany(m => new[] { Relative(m.From), Relative(m.To) })];
            return every.Length == 0 ? Done : await OnFilesAsync(["rm", "--cached", "-r", "-q", "--ignore-unmatch"], [.. every.Distinct()]);
        }
        Dictionary<string, (string Mode, string Blob)> committed = moves.Count == 0 ? [] : await HeadFilesAsync();
        Dictionary<string, (string Mode, string Blob)> held = moves.Count == 0 ? [] : await StageZeroAsync();
        bool composed = moves.Any(m => !Ascii.IsValid(Relative(m.From)) || !Ascii.IsValid(Relative(m.To))) && await ComposesAsync();
        StringBuilder kept = new();
        List<string> reset = [.. relative];
        foreach ((string from, string to) in moves)
        {
            string was = GitForm(Relative(from), composed);
            string now = GitForm(Relative(to), composed);
            if (held.ContainsKey(now) && !held.ContainsKey(was) && committed.TryGetValue(was, out (string Mode, string Blob) entry))
            {
                kept.Append(System.Globalization.CultureInfo.InvariantCulture, $"{entry.Mode} {entry.Blob}\t{now}\0");
            }
            else
            {
                reset.AddRange([Relative(from), Relative(to)]);
            }
        }
        GitResult unstaged = reset.Count == 0 ? Done : await OnFilesAsync(["reset", "-q", "HEAD"], [.. reset.Distinct()]);
        return !unstaged.Succeeded || kept.Length == 0 ? unstaged : await RunAsync(Root, ["update-index", "-z", "--index-info"], kept.ToString());
    }

    /// <summary>The last commit's files, each by its name as git writes it with its mode and blob; none where git could not
    /// read them.</summary>
    private async Task<Dictionary<string, (string Mode, string Blob)>> HeadFilesAsync()
    {
        Dictionary<string, (string Mode, string Blob)> files = new(StringComparer.Ordinal);
        GitResult tree = await RunAsync(Root, ["ls-tree", "-r", "-z", "--full-tree", "--end-of-options", "HEAD"]);
        foreach (string entry in tree.Succeeded ? tree.Output.Split('\0') : [])
        {
            // "<mode> <type> <object>\t<name>": a submodule's commit is no file's blob.
            if (entry.IndexOf('\t') is > 0 and int tab && entry[..tab].Split(' ') is [{ } mode, "blob", { } blob])
            {
                files[entry[(tab + 1)..]] = (mode, blob);
            }
        }
        return files;
    }

    /// <summary>Stages some of a file's changes: what the index holds of it takes in <paramref name="chosen"/>'s changes as
    /// git keeps text (the file's conversions run on them), what it held besides kept (<see cref="PartialCommit.Restaged"/>).
    /// Refused where the file changed since they were chosen, the index has no text of it, or what it holds can't be kept
    /// beside them.</summary>
    public async Task<GitResult> StageChangesAsync(string path, PartialCommit chosen)
    {
        string relative = Relative(path);
        if (await UnfilteredAsync([relative]) is { } unfiltered)
        {
            return unfiltered;
        }
        FileText? disk = await BackgroundWork.Run(() => File.Exists(path) ? FileText.Load(path) : null);
        byte[]? before = await BytesAtAsync("HEAD", chosen.From ?? path);
        FileText? head = before is null ? null : FileText.FromBytes(before, disk?.Encoding);
        if (disk is null || head is null || disk.Text != chosen.Diff.Text.ToString() || head.Text != chosen.Diff.Head)
        {
            return new GitResult(1, string.Empty, $"{System.IO.Path.GetFileName(path)} changed since its changes were chosen: stage them again");
        }
        bool composed = await ComposesAsync();
        string name = GitForm(relative, composed);
        if (await IndexEntryOfAsync(name) is not { Mode: "100644" or "100755" } entry)
        {
            return new GitResult(1, string.Empty, $"Git's index has no text of {System.IO.Path.GetFileName(path)}: stage it whole");
        }
        byte[] bytes = FileText.Encoded(chosen.Content(), disk.Encoding, disk.HasBom, disk.CrBreaks);
        (GitResult Ran, string? Read) blob = await RunAsync(Root, ["hash-object", "-w", "--stdin", $"--path={name}"], TextAsync, bytes);
        if (!blob.Ran.Succeeded)
        {
            return blob.Ran;
        }
        (GitResult listed, Dictionary<string, StagedEntry> staged) = await StagedAsync();
        if (!listed.Succeeded)
        {
            return listed;
        }
        string from = chosen.From is { } moved ? GitForm(Relative(moved), composed) : name;
        (string? kept, bool left, GitResult? refused) = await RestagedAsync(name, from, (entry.Mode, blob.Read!.Trim()), staged);
        if (refused is not null || left)
        {
            return refused ?? new GitResult(1, string.Empty, $"Git's index holds {System.IO.Path.GetFileName(path)} as no text: stage it whole");
        }
        return await RunAsync(Root, ["update-index", "-z", "--index-info"], kept ?? $"{entry.Mode} {blob.Read!.Trim()}\t{name}\0");
    }

    /// <summary>Unstages some of a file's changes: what the index holds of it gives back the last commit's lines wherever
    /// it changed those of <paramref name="changes"/> (each by its lines in the last commit, <see cref="DiffHunk.OldStart"/>
    /// and <see cref="DiffHunk.OldCount"/>), the rest it holds kept (<see cref="PartialCommit.Unstaged"/>).</summary>
    public async Task<GitResult> UnstageChangesAsync(string path, string? from, IReadOnlyList<DiffHunk> changes)
    {
        bool composed = await ComposesAsync();
        string name = GitForm(Relative(path), composed);
        string was = from is null ? name : GitForm(Relative(from), composed);
        if (await IndexEntryOfAsync(name) is not { Mode: "100644" or "100755" } entry)
        {
            return new GitResult(1, string.Empty, $"Git's index has no text of {System.IO.Path.GetFileName(path)}: unstage it whole");
        }
        byte[]? kept = await BlobAtAsync("HEAD", was) is { } head && await BytesAsync(Root, ["cat-file", "blob", head]) is { } headBytes
            && await BytesAsync(Root, ["cat-file", "blob", entry.Blob]) is { } heldBytes
            ? await BackgroundWork.Run(() => PartialCommit.Unstaged(headBytes, heldBytes, changes)) : null;
        if (kept is null)
        {
            return new GitResult(1, string.Empty, $"Passenger can't take part of what Git's index holds of {System.IO.Path.GetFileName(path)} out: unstage it whole");
        }
        (GitResult Ran, string? Read) written = await RunAsync(Root, ["hash-object", "-w", "--stdin", "--no-filters"], TextAsync, kept);
        return !written.Ran.Succeeded ? written.Ran
            : await RunAsync(Root, ["update-index", "-z", "--index-info"], $"{entry.Mode} {written.Read!.Trim()}\t{name}\0");
    }

    /// <summary>What the index holds at each file, its mode and blob, or null where it holds none (or it is in conflict), for
    /// putting them back as they were (<see cref="PutIndexEntriesAsync"/>): one git reads them however many there are.</summary>
    public async Task<IReadOnlyList<(string Mode, string Blob)?>> IndexEntriesAsync(IReadOnlyList<string> paths)
    {
        bool composed = paths.Any(p => !Ascii.IsValid(Relative(p))) && await ComposesAsync();
        Dictionary<string, (string Mode, string Blob)> held = await StageZeroAsync();
        return [.. paths.Select(p => held.TryGetValue(GitForm(Relative(p), composed), out (string Mode, string Blob) entry) ? entry : ((string, string)?)null)];
    }

    /// <summary>Puts the index's entry at each file as its entry has it, taking it out where that is null, by one git.</summary>
    public async Task<GitResult> PutIndexEntriesAsync(IReadOnlyList<(string Path, (string Mode, string Blob)? Entry)> entries)
    {
        bool composed = entries.Any(e => !Ascii.IsValid(Relative(e.Path))) && await ComposesAsync();
        StringBuilder lines = new();
        foreach ((string path, (string Mode, string Blob)? entry) in entries)
        {
            string name = GitForm(Relative(path), composed);
            // A mode of zeros takes the entry out.
            lines.Append(entry is { } put ? $"{put.Mode} {put.Blob}\t{name}\0" : $"0 {new string('0', 40)}\t{name}\0");
        }
        return lines.Length == 0 ? Done : await RunAsync(Root, ["update-index", "-z", "--index-info"], lines.ToString());
    }

    /// <summary>The index's stage-0 entry at a name as git writes it, or null where it has none.</summary>
    private async Task<(string Mode, string Blob)?> IndexEntryOfAsync(string name) =>
        (await StageZeroAsync()).TryGetValue(name, out (string Mode, string Blob) entry) ? entry : null;

    /// <summary>The index's stage-0 entries by their names as git writes them; none where git could not read it.</summary>
    private async Task<Dictionary<string, (string Mode, string Blob)>> StageZeroAsync()
    {
        Dictionary<string, (string Mode, string Blob)> held = new(StringComparer.Ordinal);
        GitResult entries = await RunAsync(Root, ["ls-files", "--stage", "-z"]);
        if (!entries.Succeeded)
        {
            return held;
        }
        foreach (string entry in entries.Output.Split('\0'))
        {
            // "<mode> <blob> <stage>\t<name>"
            if (entry.IndexOf('\t') is > 0 and int tab && entry[..tab].Split(' ') is [{ } mode, { } blob, "0"])
            {
                held[entry[(tab + 1)..]] = (mode, blob);
            }
        }
        return held;
    }

    /// <summary>Where a commit of whole files failed (a hook refused it, its signing was refused), the index's entries for
    /// them as they were before they were added: as the last commit has them, but those staged otherwise
    /// (<paramref name="before"/>, read first), which take what was staged; a commit made, or one with nothing read
    /// before it, as it came.</summary>
    private async Task<GitResult> PutBackAsync(GitResult committed, IReadOnlyCollection<string> relative, IReadOnlyDictionary<string, StagedEntry>? before)
    {
        if (committed.Succeeded || before is null)
        {
            return committed;
        }
        GitResult put = await OnFilesAsync(["reset", "-q", "HEAD"], relative);
        bool composed = await ComposesAsync();
        string[] names = [.. relative.Select(r => GitForm(r, composed))];
        StringBuilder staged = new();
        foreach ((string name, StagedEntry entry) in before)
        {
            if (entry.Status != 'U' && names.Any(n => n.Length == 0 || name == n || name.StartsWith(n + "/", StringComparison.Ordinal)))
            {
                // A mode of zeros takes out what the last commit has and the index did not.
                staged.Append(System.Globalization.CultureInfo.InvariantCulture, $"{entry.Index.Mode} {entry.Index.Blob}\t{name}\0");
            }
        }
        if (put.Succeeded && staged.Length > 0)
        {
            put = await RunAsync(Root, ["update-index", "-z", "--index-info"], staged.ToString());
        }
        return put.Succeeded ? committed : committed with { Error = $"{committed.Error.TrimEnd()}\n{put.Message}" };
    }

    /// <summary>The files of <paramref name="moves"/> (each where it came from and where it is) that git would find no
    /// longer moved once committed as the disk has them, their content changed past what its rename search finds alike (a
    /// half): the last commit's files against the working tree's, as a look at such a commit later compares them, within
    /// the person's limits.</summary>
    public Task<IReadOnlyList<(string From, string To)>> LostMovesAsync(IReadOnlyList<(string From, string To)> moves) =>
        BackgroundWork.Run<IReadOnlyList<(string From, string To)>>(async () =>
        {
            if (moves.Count == 0)
            {
                return [];
            }
            GitResult compared = await RunAsync(Root, ["diff", "HEAD", "-z", "--name-status", "--find-renames", "--no-ext-diff"]);
            if (!compared.Succeeded)
            {
                return [];
            }
            // "R<score>\0<from>\0<to>\0" for each rename found, "<letter>\0<path>\0" for the rest (a copy names two).
            HashSet<(string, string)> found = [];
            DiskNames names = new(Root);
            string[] fields = compared.Output.Split('\0');
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].Length > 0 && fields[i][0] is 'R' or 'C' && i + 2 < fields.Length)
                {
                    if (fields[i][0] == 'R')
                    {
                        found.Add((names.Full(fields[i + 1]), names.Full(fields[i + 2])));
                    }
                    i += 2;
                }
                else if (fields[i].Length > 0)
                {
                    i++;
                }
            }
            return [.. moves.Where(m => !found.Contains((m.From, m.To)))];
        });

    /// <summary>Commits files' moves alone, before what changed in them, so git finds each a rename however much changed:
    /// through an index of its own from the last commit, where each file's entry goes from where it was to where it is,
    /// with the last commit's content; the real index stays as it was and the files' own changes are left to commit
    /// after. Signed and signed off as <paramref name="signing"/> says.</summary>
    public async Task<GitResult> CommitMovesAsync(IReadOnlyList<(string From, string To)> moves, string message, CommitSigning? signing = null, GitAsker? asker = null)
    {
        signing ??= CommitSigning.AsConfigured;
        using PrivateIndex index = await PrivateIndexAsync("moves", signing);
        if (!index.Read.Succeeded)
        {
            return index.Read;
        }
        (GitResult moved, bool any) = await MoveInIndexAsync(moves, index.Environment);
        if (!moved.Succeeded || !any)
        {
            return moved.Succeeded ? new GitResult(1, string.Empty, "The last commit has none of the moved files where they were") : moved;
        }
        using CommitFile file = CommitFile.Message(message);
        return await RunAsync(Root, [.. signing.Before, "commit", "-F", file.Path, .. signing.After], environment: index.Environment, asker: asker);
    }

    /// <summary>A commit's index of its own in the git folder, named for <paramref name="kind"/>, read from the last commit,
    /// and the environment that points git at it beside <paramref name="signing"/>'s; its read's failure is in
    /// <see cref="PrivateIndex.Read"/>, the index deleted as it is disposed whatever came of it.</summary>
    private async Task<PrivateIndex> PrivateIndexAsync(string kind, CommitSigning signing)
    {
        CommitFile index = CommitFile.Index(GitDir, kind);
        try
        {
            Dictionary<string, string> environment = new(signing.Environment ?? new Dictionary<string, string>()) { [GitVariables.IndexFile] = index.Path };
            // The real index's stats go along, or the commit reads every file again; -i asks nothing of a file staged and changed since.
            GitResult read = await RunAsync(Root, ["read-tree", "-m", "-i", $"--index-output={index.Path}", "HEAD"]);
            return new PrivateIndex(index, environment, read);
        }
        catch
        {
            index.Dispose();
            throw;
        }
    }

    /// <summary>A commit's own index (<see cref="PrivateIndexAsync"/>): the environment git runs with to use it, and how
    /// its read from the last commit went.</summary>
    private sealed record PrivateIndex(CommitFile File, IReadOnlyDictionary<string, string> Environment, GitResult Read) : IDisposable
    {
        public void Dispose() => File.Dispose();
    }

    /// <summary>A private index keeps excluded changes out of the commit and leaves the real index untouched on failure;
    /// once made, a file committed in part keeps staged what the index held of it beside what went in (<see cref="RestagedAsync"/>).</summary>
    private async Task<GitResult> CommitPartialAsync(IReadOnlyList<string> paths, string message, bool amend, CommitSigning signing, GitAsker? asker,
        IReadOnlyDictionary<string, PartialCommit> partial)
    {
        Dictionary<string, byte[]> contents = new(PathNames.Comparer);
        foreach ((string path, PartialCommit selection) in partial)
        {
            FileText? disk = await BackgroundWork.Run(() => File.Exists(path) ? FileText.Load(path) : null);
            byte[]? before = await BytesAtAsync("HEAD", selection.From ?? path);
            FileText? head = before is null ? null : FileText.FromBytes(before, disk?.Encoding);
            if (!paths.Contains(path, PathNames.Comparer) || disk is null || head is null
                || disk.Text != selection.Diff.Text.ToString() || head.Text != selection.Diff.Head)
            {
                return new GitResult(1, string.Empty, $"{System.IO.Path.GetFileName(path)} changed since its changes were selected; review its diff again");
            }
            contents[path] = FileText.Encoded(selection.Content(), disk.Encoding, disk.HasBom, disk.CrBreaks);
        }
        using PrivateIndex index = await PrivateIndexAsync("commit", signing);
        if (!index.Read.Succeeded)
        {
            return index.Read;
        }
        string[] relative = [.. paths.Select(Relative).Distinct()];
        GitResult added = await OnFilesAsync(["add", "-A"], relative, environment: index.Environment);
        if (!added.Succeeded)
        {
            return added;
        }
        GitResult entries = await RunAsync(Root, ["ls-files", "--stage", "-z"], environment: index.Environment);
        if (!entries.Succeeded)
        {
            return entries;
        }
        bool composed = await ComposesAsync();
        (GitResult listed, Dictionary<string, StagedEntry> staged) = await StagedAsync();
        if (!listed.Succeeded)
        {
            return listed;
        }
        StringBuilder restaged = new();
        HashSet<string> left = new(PathNames.Comparer);
        foreach ((string path, byte[] bytes) in contents)
        {
            string name = GitForm(Relative(path), composed);
            string? entry = entries.Output.Split('\0').FirstOrDefault(e => e.IndexOf('\t') is > 0 and int tab && e[(tab + 1)..] == name);
            if (entry is null || !entry.StartsWith("100", StringComparison.Ordinal))
            {
                return new GitResult(1, string.Empty, "Only regular text files support partial commits");
            }
            // A path on git's command line, as hash-object takes none on stdin beside the bytes: --path picks their filters.
            (GitResult Ran, string? Read) blob = await RunAsync(Root, ["hash-object", "-w", "--stdin", $"--path={name}"], TextAsync, bytes);
            if (!blob.Ran.Succeeded)
            {
                return blob.Ran;
            }
            GitResult updated = await RunAsync(Root, ["update-index", "-z", "--index-info"],
                $"{entry[..6]} {blob.Read!.Trim()}\t{name}\0", index.Environment);
            if (!updated.Succeeded)
            {
                return updated;
            }
            string from = partial[path].From is { } moved ? GitForm(Relative(moved), composed) : name;
            switch (await RestagedAsync(name, from, (entry[..6], blob.Read!.Trim()), staged))
            {
                case { Refused: { } refused }:
                    return refused;
                case { Left: true }:
                    left.Add(Relative(path));
                    break;
                case { Entry: { } kept }:
                    restaged.Append(kept);
                    break;
            }
        }
        using CommitFile file = CommitFile.Message(message);
        GitResult committed = await RunAsync(Root, [.. signing.Before, "commit", "-F", file.Path, .. amend ? new[] { "--amend" } : [], .. signing.After],
            environment: index.Environment, asker: asker);
        if (committed.Succeeded)
        {
            // An empty list of paths would reset the whole index.
            string[] reset = [.. relative.Where(r => !left.Contains(r))];
            GitResult indexed = reset.Length > 0 ? await OnFilesAsync(["reset", "-q", "HEAD"], reset) : Done;
            if (indexed.Succeeded && restaged.Length > 0)
            {
                indexed = await RunAsync(Root, ["update-index", "-z", "--index-info"], restaged.ToString());
            }
            if (!indexed.Succeeded)
            {
                return committed with { Output = committed.Output + "\n" + indexed.Message };
            }
        }
        return committed;
    }

    /// <summary>A path's entry in the last commit and in the index where the two differ, each a mode and a blob (zeros
    /// where it has none), with git's letter for how they differ: A, D, M, T or U (in conflict).</summary>
    private sealed record StagedEntry(char Status, (string Mode, string Blob) Head, (string Mode, string Blob) Index);

    /// <summary>The index's entries that differ from the last commit's, by name as git writes it.</summary>
    private async Task<(GitResult Ran, Dictionary<string, StagedEntry> Staged)> StagedAsync()
    {
        GitResult listed = await RunAsync(Root, ["diff-index", "--cached", "-z", "--no-renames", "HEAD"]);
        Dictionary<string, StagedEntry> staged = new(StringComparer.Ordinal);
        string[] fields = listed.Succeeded ? listed.Output.Split('\0') : [];
        // ":<mode> <mode> <blob> <blob> <letter>", then its path.
        for (int i = 0; i + 1 < fields.Length; i += 2)
        {
            if (fields[i].TrimStart(':').Split(' ') is [{ } headMode, { } indexMode, { } headBlob, { } indexBlob, [char status, ..]])
            {
                staged[fields[i + 1]] = new StagedEntry(status, (headMode, headBlob), (indexMode, indexBlob));
            }
        }
        return (listed, staged);
    }

    /// <summary>What the index is to hold at a file a partial commit takes some changes of (<paramref name="name"/>, moved
    /// from <paramref name="from"/>), once <paramref name="committed"/> is: nothing to do but take the commit's where
    /// nothing else was staged there; the entry that keeps what was staged with the commit's changes taken in
    /// (<see cref="PartialCommit.Restaged"/>); left as it stands where a deletion, a link or a submodule is staged
    /// there; or refused, before any commit, where the staged text can't be kept beside the committed one.</summary>
    private async Task<(string? Entry, bool Left, GitResult? Refused)> RestagedAsync(string name, string from, (string Mode, string Blob) committed,
        IReadOnlyDictionary<string, StagedEntry> staged)
    {
        StagedEntry? here = staged.GetValueOrDefault(name);
        StagedEntry? there = from == name ? null : staged.GetValueOrDefault(from);
        if (here is { Status: 'D' } || here is { Status: not 'U', Index.Mode: not ("100644" or "100755") })
        {
            return (null, true, null);
        }
        // What is staged of the file: at its name, else at the name it had while the index has not moved it.
        (string Mode, string Blob)? index = here is { Status: 'A' or 'M' or 'T' } ? here.Index
            : here is null && there is { Status: 'M' or 'T', Index.Mode: "100644" or "100755" } ? there.Index : null;
        if (index is not { } held)
        {
            return (null, false, null);
        }
        (string Mode, string Blob)? last = from == name ? here!.Head : there is { Status: not 'U' and not 'A' } ? there.Head : null;
        string? head = last?.Blob ?? await BlobAtAsync("HEAD", from);
        string mode = last is { } before && before.Mode != held.Mode ? held.Mode : committed.Mode;
        string blob = committed.Blob;
        if (held.Blob != head && held.Blob != committed.Blob)
        {
            byte[]? merged = head is not null && await BytesAsync(Root, ["cat-file", "blob", head]) is { } headBytes
                && await BytesAsync(Root, ["cat-file", "blob", held.Blob]) is { } heldBytes
                && await BytesAsync(Root, ["cat-file", "blob", committed.Blob]) is { } committedBytes
                ? await BackgroundWork.Run(() => PartialCommit.Restaged(headBytes, heldBytes, committedBytes)) : null;
            if (merged is null)
            {
                return (null, false, new GitResult(1, string.Empty,
                    $"{System.IO.Path.GetFileName(name)} has staged changes Passenger can't keep beside the ones chosen: commit it whole, or unstage it first"));
            }
            (GitResult Ran, string? Read) written = await RunAsync(Root, ["hash-object", "-w", "--stdin", "--no-filters"], TextAsync, merged);
            if (!written.Ran.Succeeded)
            {
                return (null, false, written.Ran);
            }
            blob = written.Read!.Trim();
        }
        return mode == committed.Mode && blob == committed.Blob ? (null, false, null) : ($"{mode} {blob}\t{name}\0", false, null);
    }

    /// <summary>Whether a path is on the disk: a file, a folder or a link, a broken one too, which git tracks as itself.</summary>
    private bool StillThere(string relative)
    {
        string full = Full(relative);
        return System.IO.Path.Exists(full) || new FileInfo(full).LinkTarget is not null;
    }

    /// <summary>Whether the branch checked out tracks a remote's branch (its upstream), which a push goes to.</summary>
    public async Task<bool> TracksAsync() => await UpstreamAsync() is not null;

    /// <summary>The branch the one checked out tracks, as the remote's branches are named (origin/main), or null.</summary>
    public async Task<string?> UpstreamAsync()
    {
        GitResult upstream = await RunAsync(Root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], answers: [NotThere]);
        return upstream.Succeeded && upstream.Output.Trim() is { Length: > 0 } name ? name : null;
    }

    /// <summary>The remotes' names.</summary>
    public async Task<IReadOnlyList<string>> RemotesAsync()
    {
        GitResult remotes = await RunAsync(Root, ["remote"]);
        return remotes.Succeeded ? remotes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    }

    /// <summary>Pushes the branch to the one it tracks; or, given <paramref name="remote"/>, to that remote under its own
    /// name, which it tracks from then on. Canceled, as <see cref="FetchUpstreamAsync"/>.</summary>
    public async Task<GitResult> PushAsync(string? remote = null, CancellationToken cancel = default, GitAsker? asker = null)
    {
        if (remote is null)
        {
            return await RunAsync(Root, ["push"], cancel: cancel, asker: asker);
        }
        return await BranchAsync() is { } branch
            ? await RunAsync(Root, ["push", "-u", "--end-of-options", remote, $"refs/heads/{branch}:refs/heads/{branch}"], cancel: cancel, asker: asker)
            : new GitResult(1, string.Empty, "A detached head has no branch to push");
    }

    /// <summary>Checks a branch out. What follows --end-of-options (here and wherever a name or a hash comes from outside) is
    /// a name, never an option, whoever gave it: an agent's "--orphan=x" is a branch git does not have, not an order to
    /// empty the working tree.</summary>
    public async Task<GitResult> CheckoutAsync(string branch) =>
        await UnfilteredCheckoutAsync(branch) ?? await WritingOutAsync(() => CheckedOutAsync(branch), () => RunAsync(Root, ["switch", "--end-of-options", branch]));

    /// <summary>A remote's branch checked out as a local branch of the same name that tracks it.</summary>
    public async Task<GitResult> CheckoutTrackingAsync(string remoteBranch) =>
        await UnfilteredSwitchAsync(remoteBranch) ?? await WritingOutAsync(() => SwitchedAsync(remoteBranch), () => RunAsync(Root, ["switch", "--track", "--end-of-options", remoteBranch]));

    /// <summary>Another branch merged into the current one, git's own message for the merge commit. In a folder not trusted,
    /// refused where it writes out a file git stores through a filter: one the branch changed since the two went apart.</summary>
    public async Task<GitResult> MergeAsync(string branch, Signer? signer = null) =>
        await UnfilteredMergeAsync(branch) ?? await WritingOutAsync(() => MergedAsync(branch), () => RunSignedAsync(["merge", "--no-edit", "--end-of-options", branch], signer));

    private async Task<GitResult?> UnfilteredMergeAsync(string branch) => await MayFilterAsync() && await CommitOfAsync(branch) is { } commit
        ? await UnfilteredAsync(() => MergedAsync(commit), commit)
        : null;

    /// <summary>The files a merge of a branch writes out: those it changed since the two went apart; null where git could not
    /// say.</summary>
    private Task<IReadOnlyList<string>?> MergedAsync(string branch) => DifferingAsync($"HEAD...{branch}");

    /// <summary>The current branch rebased onto another; or, given <paramref name="branch"/>, that branch checked out and
    /// rebased onto <paramref name="onto"/> (Checkout and Rebase onto Current, "git rebase HEAD branch"). In a folder not
    /// trusted, refused where it writes out a file git stores through a filter: one that differs between the head, the
    /// branch and <paramref name="onto"/>, or one a commit it replays changed.</summary>
    public async Task<GitResult> RebaseAsync(string onto, string? branch = null, Signer? signer = null) =>
        await UnfilteredRebaseAsync(onto, branch)
        ?? await WritingOutAsync(() => RebasedAsync(onto, branch ?? "HEAD"),
            () => RunSignedAsync(["rebase", "--end-of-options", onto, .. branch is null ? Array.Empty<string>() : [branch]], signer, NoEditor));

    private async Task<GitResult?> UnfilteredRebaseAsync(string onto, string? branch)
    {
        if (!await MayFilterAsync() || await CommitOfAsync(onto) is not { } upon || await CommitOfAsync(branch ?? "HEAD") is not { } rebased)
        {
            return null;
        }
        return await UnfilteredAsync(() => RebasedAsync(upon, rebased), upon, rebased);
    }

    /// <summary>The files a rebase of <paramref name="rebased"/> onto <paramref name="upon"/> writes out: those that differ
    /// between the head and either, and those a commit it replays changed; null where git could not say.</summary>
    private async Task<IReadOnlyList<string>?> RebasedAsync(string upon, string rebased)
    {
        IReadOnlyList<string>?[] differing = [await DifferingAsync("HEAD", rebased), await DifferingAsync("HEAD", upon), await DifferingAsync($"{upon}...{rebased}")];
        return differing.Any(paths => paths is null) ? null : AllOf(differing);
    }

    public Task<GitResult> RenameBranchAsync(string from, string to) => RunAsync(Root, ["branch", "-m", "--end-of-options", from, to]);

    /// <summary>A local branch deleted; one not merged into HEAD (or its upstream) only when <paramref name="force"/>.</summary>
    public Task<GitResult> DeleteBranchAsync(string name, bool force) => RunAsync(Root, ["branch", force ? "-D" : "-d", "--end-of-options", name]);

    /// <summary>A remote's branch deleted on the remote, and its remote-tracking branch with it. Canceled, as
    /// <see cref="FetchUpstreamAsync"/>.</summary>
    public Task<GitResult> DeleteRemoteBranchAsync(string remote, string branch, CancellationToken cancel = default, GitAsker? asker = null) =>
        RunAsync(Root, ["push", "--end-of-options", remote, $":refs/heads/{branch}"], cancel: cancel, asker: asker);

    public Task<GitResult> DeleteTagAsync(string name) => RunAsync(Root, ["tag", "-d", "--end-of-options", name]);

    /// <summary>Whether every commit of a branch is in HEAD's history.</summary>
    public async Task<bool> IsMergedAsync(string branch) => (await RunAsync(Root, ["merge-base", "--is-ancestor", branch, "HEAD"], answers: [1])).Succeeded;

    /// <summary>A local branch that is not checked out brought up to date with the remote's branch it tracks: forward only,
    /// refused where the two went apart. Canceled, as <see cref="FetchUpstreamAsync"/>.</summary>
    public Task<GitResult> FetchIntoAsync(string remote, string remoteBranch, string local, CancellationToken cancel = default, GitAsker? asker = null) =>
        RunAsync(Root, ["fetch", "--end-of-options", remote, $"{remoteBranch}:refs/heads/{local}"], cancel: cancel, asker: asker);

    /// <summary>The files that differ between a revision and the working tree (Show Diff with Working Tree), renames
    /// found, each by its path now, in their paths' order: those git does not track too, ignored ones left out, each added,
    /// or modified where the revision has it otherwise, which git's diff, reading only what git tracks, calls deleted.</summary>
    public async Task<IReadOnlyList<FileChange>> ChangesSinceAsync(string revision)
    {
        GitResult files = await RunAsync(Root, ["diff", "--name-status", "-z", "-M", "--end-of-options", revision, "--"]);
        if (!files.Succeeded)
        {
            return [];
        }
        List<FileChange> changes = NameStatus(files.Output);
        GitResult untracked = await RunAsync(Root, ["ls-files", "-z", "--others", "--exclude-standard"]);
        if (!untracked.Succeeded || untracked.Output.Length == 0)
        {
            return changes;
        }
        DiskNames names = new(Root);
        // Where the revision has a path no tracked file of the working tree now holds: a deletion, or a rename's source.
        HashSet<string> gone = new(changes.Select(c => c.Kind switch { ChangeKind.Deleted => c.Path, ChangeKind.Renamed => c.From, _ => null }).OfType<string>(), PathNames.Comparer);
        HashSet<string> untrackedFiles = new(untracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(names.Full), PathNames.Comparer);
        changes.RemoveAll(c => c.Kind == ChangeKind.Deleted && untrackedFiles.Contains(c.Path));
        HashSet<string> same = await AsStoredAsync(revision, [.. untrackedFiles.Where(gone.Contains)]);
        changes.AddRange(untrackedFiles.Where(path => !same.Contains(path)).Select(path => new FileChange(path, gone.Contains(path) ? ChangeKind.Modified : ChangeKind.Added)));
        changes.Sort((a, b) => PathNames.Comparer.Compare(a.Path, b.Path));
        return changes;
    }

    /// <summary>The files of <paramref name="paths"/> (untracked here) whose bytes, as git would store them, are those
    /// <paramref name="revision"/> has at the same path; each given on git's input, a name holding a line break never same.</summary>
    private async Task<HashSet<string>> AsStoredAsync(string revision, IReadOnlyList<string> paths)
    {
        string[] asked = [.. paths.Where(p => p.AsSpan().IndexOfAny('\n', '\r') < 0)];
        if (asked.Length == 0)
        {
            return [];
        }
        string[] relative = [.. asked.Select(Relative)];
        GitResult hashed = await RunAsync(Root, ["hash-object", "--stdin-paths"], string.Join('\n', relative) + "\n");
        GitResult stored = await RunAsync(Root, ["cat-file", "--batch-check=%(objectname)"], string.Join('\n', relative.Select(r => $"{revision}:{r}")) + "\n");
        if (!hashed.Succeeded || !stored.Succeeded)
        {
            return [];
        }
        string[] mine = hashed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] theirs = stored.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        HashSet<string> same = new(PathNames.Comparer);
        for (int i = 0; i < asked.Length && i < mine.Length && i < theirs.Length; i++)
        {
            if (mine[i].Trim() == theirs[i].Trim())
            {
                same.Add(asked[i]);
            }
        }
        return same;
    }

    /// <summary>The stashes, the newest first.</summary>
    public async Task<IReadOnlyList<StashInfo>> StashesAsync()
    {
        GitResult list = await RunAsync(Root, ["stash", "list", "--format=%gd%x1f%H%x1f%gs%x1f%ct"]);
        if (!list.Succeeded)
        {
            return [];
        }
        List<StashInfo> stashes = [];
        foreach (string line in list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\x1f');
            if (parts.Length == 4 && parts[0].StartsWith("stash@{", StringComparison.Ordinal) && int.TryParse(parts[0][7..^1], out int index)
                && long.TryParse(parts[3], out long seconds))
            {
                stashes.Add(new StashInfo(index, parts[1], parts[2], DateTimeOffset.FromUnixTimeSeconds(seconds)));
            }
        }
        return stashes;
    }

    /// <summary>Puts the uncommitted changes away in a stash, new files too unless <paramref name="untracked"/> is false;
    /// the files go back to the head's. With nothing to put away, git makes no stash and exits 0 all the same: that is a
    /// failure here, in git's words.</summary>
    public async Task<GitResult> StashAsync(string message, bool untracked = true)
    {
        string? before = await StashTopAsync();
        if (await UnfilteredStashAsync() is { } unfiltered)
        {
            return unfiltered;
        }
        GitResult stashed = await WritingOutAsync(StashedAsync, () => RunAsync(Root, ["stash", "push", .. untracked ? new[] { "--include-untracked" } : [],
            .. message.Length > 0 ? new[] { "-m", message } : []]));
        if (!stashed.Succeeded || await StashTopAsync() != before)
        {
            return stashed;
        }
        return new GitResult(1, string.Empty, stashed.Message.Length > 0 ? stashed.Message : "No local changes to save");
    }

    /// <summary>The newest stash's commit, or null with none.</summary>
    private async Task<string?> StashTopAsync()
    {
        GitResult top = await RunAsync(Root, ["rev-parse", "-q", "--verify", "refs/stash"], answers: [1]);
        return top.Succeeded ? top.Output.Trim() : null;
    }

    /// <summary>The stash of this one's commit where the list has it now, another program having perhaps stashed or
    /// dropped one since it was read; null once it has left the list.</summary>
    public async Task<StashInfo?> StashNowAsync(StashInfo stash) => (await StashesAsync()).FirstOrDefault(s => s.Commit == stash.Commit);

    /// <summary>Applies a stash to the files, and with <paramref name="pop"/> drops it once it applied: the stash of its
    /// commit, wherever the list has it now, one no longer listed refused saying so.</summary>
    public async Task<GitResult> UnstashAsync(StashInfo stash, bool pop) => await StashNowAsync(stash) is { } now
        ? await UnfilteredUnstashAsync(now) ?? await WritingOutAsync(() => UnstashedAsync(now.Commit), () => RunAsync(Root, ["stash", pop ? "pop" : "apply", pop ? Place(now) : now.Commit]))
        : Gone(stash);

    /// <summary>Throws a stash away: the one of its commit, wherever the list has it now, one no longer listed refused
    /// saying so.</summary>
    public async Task<GitResult> DropStashAsync(StashInfo stash) => await StashNowAsync(stash) is { } now
        ? await RunAsync(Root, ["stash", "drop", Place(now)])
        : Gone(stash);

    private static string Place(StashInfo stash) => $"stash@{{{stash.Index}}}";

    private static GitResult Gone(StashInfo stash) =>
        new(1, string.Empty, $"The stash \"{stash.Message}\" is no longer in the list: it was popped or dropped since the list was read");

    /// <summary>A new branch from the head, checked out. -c takes the next word as the name, whatever it is, and git
    /// refuses a name that starts with a dash.</summary>
    public Task<GitResult> NewBranchAsync(string name) => RunAsync(Root, ["switch", "-c", name]);

    /// <summary>A branch made at a commit and checked out.</summary>
    public async Task<GitResult> NewBranchAtAsync(string name, string hash) =>
        await UnfilteredSwitchAsync(hash) ?? await WritingOutAsync(() => SwitchedAsync(hash), () => RunAsync(Root, ["switch", "-c", name, "--end-of-options", hash]));

    /// <summary>A commit checked out on its own, with no branch (a detached head).</summary>
    public async Task<GitResult> CheckoutRevisionAsync(string hash) =>
        await UnfilteredSwitchAsync(hash) ?? await WritingOutAsync(() => SwitchedAsync(hash), () => RunAsync(Root, ["switch", "--detach", "--end-of-options", hash]));

    public async Task<GitResult> CherryPickAsync(string hash, Signer? signer = null) =>
        await UnfilteredPickAsync(hash) ?? await WritingOutAsync(() => PickedAsync(hash), () => RunSignedAsync(["cherry-pick", "--end-of-options", hash], signer));

    /// <summary>A commit undone by a new one, with git's own message.</summary>
    public async Task<GitResult> RevertAsync(string hash, Signer? signer = null) =>
        await UnfilteredPickAsync(hash) ?? await WritingOutAsync(() => PickedAsync(hash), () => RunSignedAsync(["revert", "--no-edit", "--end-of-options", hash], signer));

    /// <summary>Why a commit's changes cannot be applied or undone here (a cherry-pick, a revert): in a folder not trusted, a
    /// file it changed is stored through a filter, which runs only once the folder is trusted.</summary>
    private async Task<GitResult?> UnfilteredPickAsync(string hash) => await MayFilterAsync() && await CommitOfAsync(hash) is { } commit
        ? await UnfilteredAsync(() => PickedAsync(commit), commit)
        : null;

    /// <summary>The files a commit's changes applied or undone write out: those it changed, or every file of a first commit;
    /// null where git could not say.</summary>
    private async Task<IReadOnlyList<string>?> PickedAsync(string commit) => await DifferingAsync($"{commit}^", commit) ?? await FilesOfAsync(commit);

    /// <summary>The branch moved to a commit: soft keeps the changes staged, mixed keeps them in the files, hard drops them,
    /// in a folder not trusted refused where it writes out a file git stores through a filter.</summary>
    public async Task<GitResult> ResetAsync(string hash, ResetMode mode) =>
        await UnfilteredResetAsync(hash, mode) ?? await WritingOutAsync(() => mode is ResetMode.Hard ? DifferingAsync(hash) : Task.FromResult<IReadOnlyList<string>?>([]),
            () => RunAsync(Root, ["reset", $"--{mode.ToString().ToLowerInvariant()}", "--end-of-options", hash]));

    private async Task<GitResult?> UnfilteredResetAsync(string hash, ResetMode mode) => mode is ResetMode.Hard && await MayFilterAsync() && await CommitOfAsync(hash) is { } commit
        ? await UnfilteredAsync(() => DifferingAsync(commit), commit)
        : null;

    /// <summary>Puts files back as the last commit has them (Rollback): one added to the index but in no commit kept on
    /// disk, unversioned; a submodule whose commit alone changed is checked out at the one recorded (in a folder not trusted
    /// only recorded so in the index), one whose own files changed left to its repository; an unversioned file, of which
    /// git has nothing, passed over. A file another program holds is tried again (<see cref="Attempts"/>); one never let go
    /// of, and a submodule left, are named in the result.</summary>
    public async Task<GitResult> RollbackAsync(IReadOnlyList<FileChange> changes)
    {
        FileChange[] ownChanges = [.. changes.Where(c => c.Submodule.HasFlag(SubmoduleChange.Submodule) && c.Kind is ChangeKind.Modified
            && (c.Submodule.HasFlag(SubmoduleChange.Modified) || !c.Submodule.HasFlag(SubmoduleChange.Commit)))];
        changes = [.. changes.Except(ownChanges)];
        FileChange[] restoring = [.. changes.Where(c => c.Kind is not (ChangeKind.Unversioned or ChangeKind.Added))];
        string[] tracked = [.. restoring.SelectMany(c => c.From is { } from ? new[] { c.Path, from } : [c.Path]).Select(Relative)];
        string[] staged = [.. changes.Where(c => c.Kind is ChangeKind.Added).Select(c => Relative(c.Path))];
        if (await UnfilteredAsync(tracked) is { } unfiltered)
        {
            return unfiltered;
        }
        FileChange[] submodules = [.. restoring.Where(c => c.Submodule.HasFlag(SubmoduleChange.Submodule))];
        // Untrusted, git checks no submodule out (UntrustedGit): what the last commit records goes back in the index alone.
        bool checksOut = WorkspaceTrust.Trusts(Root);
        GitResult restored = tracked.Length == 0 ? new GitResult(0, string.Empty, string.Empty)
            : await WritingOutAsync(() => Task.FromResult<IReadOnlyList<string>?>(tracked), () => RestoreAsync(tracked, submodules.Length > 0 && checksOut));
        // -f takes out a file changed again since it was added too, which --cached leaves on disk as it is now.
        GitResult unadded = staged.Length == 0 ? restored : await OnFilesAsync(["rm", "--cached", "-q", "-f", "--ignore-unmatch"], staged);
        restored = restored.Succeeded ? unadded : restored;
        List<string> kept =
        [
            .. ownChanges.Select(c => $"{c.Name} was left as it is: a submodule's own changes are rolled back in it"),
            .. checksOut ? [] : submodules.Select(c => $"{c.Name} was left checked out as it is: a submodule is checked out at the commit recorded only in a trusted folder"),
        ];
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

    /// <summary>The files as the last commit has them, in the index and on disk, and with <paramref name="submodules"/> each
    /// submodule among them checked out at the commit it records. A failure is tried again whatever git said, its words
    /// being in the person's language: restoring twice is the same as once.</summary>
    private async Task<GitResult> RestoreAsync(string[] relative, bool submodules)
    {
        for (int attempt = 1; ; attempt++)
        {
            GitResult restored = await OnFilesAsync(["restore", .. submodules ? new[] { "--recurse-submodules" } : [], "--source=HEAD", "--staged", "--worktree"], relative);
            if (restored.Succeeded || attempt == Attempts)
            {
                return restored;
            }
            await Task.Delay(Retry);
        }
    }

    /// <summary>A path from the tree's top as git names it.</summary>
    public string Relative(string path) => RelativeIn(Root, path);

    /// <summary>A path from <paramref name="root"/> as git names it: its separators made '/' where the system's is a
    /// backslash, and elsewhere as the disk names it, a backslash being a letter of a file's name there (a\b.txt is not
    /// a/b.txt).</summary>
    internal static string RelativeIn(string root, string path) => PathNames.Route(path, root);

    /// <summary>The .gitignore line that matches the file or folder at <paramref name="path"/> and nothing else: its path
    /// from the tree's top anchored there by a leading '/' (so a '#' or '!' it starts with is no comment or negation), a
    /// folder's ending in '/', and each character a pattern reads as more than itself escaped (a wildcard, a bracket, a
    /// backslash, a space it ends with). Null for a name holding a line break, which no line of the file can.</summary>
    public string? IgnoreLine(string path, bool folder)
    {
        string relative = Relative(path).TrimEnd('/');
        if (relative.AsSpan().IndexOfAny('\n', '\r') >= 0)
        {
            return null;
        }
        int spaces = relative.Length - relative.TrimEnd(' ').Length;
        StringBuilder line = new("/");
        for (int i = 0; i < relative.Length; i++)
        {
            char c = relative[i];
            if (c is '\\' or '[' or ']' or '*' or '?' || (c == ' ' && i >= relative.Length - spaces))
            {
                line.Append('\\');
            }
            line.Append(c);
        }
        return folder ? line.Append('/').ToString() : line.ToString();
    }

    /// <summary>A file's name as git takes it for that file alone, never a pattern: "[ab].cs" is not also "a.cs", nor is
    /// a name starting ":" pathspec magic. (The environment's GIT_LITERAL_PATHSPECS would also stop a stash's own magic.)</summary>
    private static string Literal(string relative) => $":(literal){relative}";

    /// <summary>Runs git over files named on its input, each a literal pathspec ended by a NUL (composed where git takes
    /// names composed), never on its command line, which holds only so many (32,767 characters on Windows).</summary>
    private async Task<GitResult> OnFilesAsync(IReadOnlyList<string> arguments, IReadOnlyCollection<string> relative, GitAsker? asker = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        bool composed = relative.Any(name => !Ascii.IsValid(name)) && await ComposesAsync();
        StringBuilder names = new();
        foreach (string name in relative)
        {
            names.Append(Literal(GitForm(name, composed))).Append('\0');
        }
        return await RunAsync(Root, [.. arguments, "--pathspec-from-file=-", "--pathspec-file-nul"], names.ToString(), environment, asker: asker);
    }

    private string Full(string relative) => new DiskNames(Root).Full(relative);

    /// <summary>Paths git names, as the disk names them. On macOS git gives each name composed (core.precomposeunicode),
    /// where the disk may hold it decomposed, as the Finder writes it, and the volume takes either: so a name with a
    /// letter beyond ASCII is looked for in its folder, each folder listed once.</summary>
    internal sealed class DiskNames(string root)
    {
        private readonly Dictionary<string, Dictionary<string, string>> folders = new(StringComparer.Ordinal);

        public string Full(string relative)
        {
            if (!OperatingSystem.IsMacOS() || Ascii.IsValid(relative))
            {
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
            }
            string at = root;
            foreach (string name in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                at = System.IO.Path.Combine(at, !Ascii.IsValid(name) && Listed(at).TryGetValue(name.Normalize(NormalizationForm.FormC), out string? stored) ? stored : name);
            }
            return at;
        }

        /// <summary><see cref="Full(string)"/> of a path as git wrote it: one of ASCII alone, as most are, joined to the root
        /// from where it lies, with no string of it made first.</summary>
        public string Full(ReadOnlySpan<byte> relative)
        {
            const int Longest = 1024;
            if (relative.Length > Longest || !Ascii.IsValid(relative))
            {
                return Full(Encoding.UTF8.GetString(relative));
            }
            if (Joined(relative) is { } joined)
            {
                return joined;
            }
            Span<char> chars = stackalloc char[Longest];
            Ascii.ToUtf16(relative, chars, out int written);
            ReadOnlySpan<char> name = chars[..written];
            return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(name) ? name.ToString() : System.IO.Path.Join(root, name));
        }

        /// <summary>A path of plain names below the root, as git writes them, joined to it in the one string the full path
        /// comes to, the system's separators in place of '/'; null for any other, which <see cref="System.IO.Path.GetFullPath(string)"/>
        /// would change.</summary>
        private string? Joined(ReadOnlySpan<byte> relative)
        {
            char separator = System.IO.Path.DirectorySeparatorChar;
            int between = root.EndsWith(separator) ? 0 : 1;
            int length = root.Length + between + relative.Length;
            if (relative.IsEmpty || length > 2048 || !PlainNames(relative))
            {
                return null;
            }
            Span<char> chars = stackalloc char[length];
            root.CopyTo(chars);
            if (between == 1)
            {
                chars[root.Length] = separator;
            }
            Span<char> name = chars[(root.Length + between)..];
            Ascii.ToUtf16(relative, name, out _);
            name.Replace('/', separator);
            return new string(chars);
        }

        /// <summary>Whether a path is names between '/' that a full path keeps as they are: none empty, '.' or '..', and on
        /// Windows none ending in a dot or a space or holding a '\' or ':', which its full path would trim or take apart.</summary>
        private static bool PlainNames(ReadOnlySpan<byte> relative)
        {
            foreach (Range part in relative.Split((byte)'/'))
            {
                ReadOnlySpan<byte> name = relative[part];
                if (name.IsEmpty || name.SequenceEqual("."u8) || name.SequenceEqual(".."u8)
                    || OperatingSystem.IsWindows() && (name[^1] is (byte)'.' or (byte)' ' || name.IndexOfAny((byte)'\\', (byte)':') >= 0))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>A folder's names by their composed form; none where it cannot be listed (a file deleted with it).</summary>
        private Dictionary<string, string> Listed(string folder)
        {
            if (!folders.TryGetValue(folder, out Dictionary<string, string>? names))
            {
                names = new(StringComparer.Ordinal);
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
                    {
                        string name = System.IO.Path.GetFileName(entry);
                        names.TryAdd(name.Normalize(NormalizationForm.FormC), name);
                    }
                }
                catch (Exception unlisted) when (unlisted is IOException or UnauthorizedAccessException)
                {
                }
                folders[folder] = names;
            }
            return names;
        }
    }

    /// <summary>Whether git takes names composed (core.precomposeunicode, which git init sets on macOS): it composes those
    /// on its command line itself, but not those it reads from its input, which are given it composed. Asked once.</summary>
    private Task<bool> ComposesAsync() => composes ??= OperatingSystem.IsMacOS() ? ComposedAsync() : Task.FromResult(false);

    private Task<bool>? composes;

    private async Task<bool> ComposedAsync() =>
        (await RunAsync(Root, ["config", "--type=bool", "core.precomposeunicode"], answers: [1])).Output.Trim() == "true";

    /// <summary>A name as git takes it from its input: composed where it composes names.</summary>
    private static string GitForm(string relative, bool composed) => composed ? relative.Normalize(NormalizationForm.FormC) : relative;

    /// <summary>Runs git to its end (<see cref="RunAsync{T}"/>), its input and its output text in UTF-8, its output read whole.</summary>
    private static async Task<GitResult> RunAsync(string folder, IReadOnlyList<string> arguments, string? input = null, IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyCollection<int>? answers = null, CancellationToken cancel = default, GitAsker? asker = null)
    {
        (GitResult ran, string? output) = await RunAsync(folder, arguments, TextAsync, input is null ? default : Encoding.UTF8.GetBytes(input), environment, answers,
            cancel, asker);
        return ran with { Output = output ?? string.Empty };
    }

    /// <summary>git's commands that write a work tree's files, each with its words that leave the files as they are (a stash's
    /// list, an apply's check or one into the index alone).</summary>
    private static readonly Dictionary<string, string[]> WorkTreeWriters = new(StringComparer.Ordinal)
    {
        ["checkout"] = [], ["switch"] = [], ["restore"] = ["--staged"], ["reset"] = [], ["merge"] = [], ["rebase"] = [], ["pull"] = [],
        ["stash"] = ["list", "show"], ["cherry-pick"] = [], ["revert"] = [], ["am"] = [], ["apply"] = ["--cached", "--check", "--stat", "--numstat", "--summary"],
        ["rm"] = ["--cached"], ["mv"] = [], ["clean"] = ["-n", "--dry-run"], ["read-tree"] = [], ["checkout-index"] = [],
    };

    /// <summary>How many of Passenger's git commands writing a work tree run now, and when the last one ended (a
    /// <see cref="Stopwatch"/> timestamp).</summary>
    private static int writingWorkTree;
    private static long wroteWorkTree;

    /// <summary>Whether a git command of Passenger's that writes a work tree's files (a checkout, a pull, a stash, a reset)
    /// runs now or has ended since a <see cref="Stopwatch"/> timestamp: what changed meanwhile may be its.</summary>
    public static bool WroteWorkTreeSince(long since) => Volatile.Read(ref writingWorkTree) > 0 || Volatile.Read(ref wroteWorkTree) >= since;

    /// <summary>Whether a git command line writes the work tree's files: its command is one that does, and none of its
    /// words leaves them as they are.</summary>
    private static bool WritesWorkTree(IReadOnlyList<string> arguments) =>
        CommandAt(arguments) is >= 0 and int at && WorkTreeWriters.TryGetValue(arguments[at], out string[]? leaving)
        && !arguments.Skip(at + 1).Any(leaving.Contains);

    /// <summary>Where a git command line's command is: the first word past the options before it (-c and -C take a
    /// value); -1 where it has none.</summary>
    private static int CommandAt(IReadOnlyList<string> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is "-c" or "-C")
            {
                i++;
            }
            else if (!arguments[i].StartsWith('-'))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Runs git to its end, the one way every call but a clone runs it: <paramref name="input"/> written to it, what
    /// it writes handed to <paramref name="read"/> as it comes (<see cref="ChildProcesses.RunAsync{T}"/>), and what it asks
    /// of the person going to <paramref name="asker"/>; canceled, it is ended with all it started (a fetch's upload-pack, a
    /// push's ssh) and the task is canceled. A start or a read that fails is a failure in the system's words, with no
    /// answer (<see cref="StartFailure"/>, <see cref="ReadFailure"/>), never thrown.</summary>
    private static async Task<(GitResult Result, T? Read)> RunAsync<T>(string folder, IReadOnlyList<string> arguments, Func<Stream, Task<T>> read,
        ReadOnlyMemory<byte> input = default, IReadOnlyDictionary<string, string>? environment = null, IReadOnlyCollection<int>? answers = null,
        CancellationToken cancel = default, GitAsker? asker = null)
    {
        // What git answers is what the window shows next (the branch, the bars, the log, a blame): a screenshot waits for it.
        using GitHold held = new();
        bool writes = WritesWorkTree(arguments);
        if (writes)
        {
            Interlocked.Increment(ref writingWorkTree);
        }
        try
        {
            return await RunStartedAsync(folder, arguments, read, input, environment, answers, cancel, asker, held);
        }
        finally
        {
            if (writes)
            {
                Volatile.Write(ref wroteWorkTree, Stopwatch.GetTimestamp());
                Interlocked.Decrement(ref writingWorkTree);
            }
        }
    }

    private static async Task<(GitResult Result, T? Read)> RunStartedAsync<T>(string folder, IReadOnlyList<string> arguments, Func<Stream, Task<T>> read,
        ReadOnlyMemory<byte> input, IReadOnlyDictionary<string, string>? environment, IReadOnlyCollection<int>? answers, CancellationToken cancel, GitAsker? asker,
        GitHold held)
    {
        // A folder gone (deleted under a window, or with it as it closes) starts no git, which could only fail there.
        if (!Directory.Exists(folder))
        {
            return (Gone(folder), default);
        }
        (ProcessStartInfo? start, GitResult? refused) = await StartAsync(folder, arguments);
        if (start is null)
        {
            return (refused ?? NotInstalled, default);
        }
        ChildProcesses.WithVariables(start, environment);
        await using Prompting? prompting = Asking(start, asker, held, cancel);
        try
        {
            ChildRead<T> ran = await ChildProcesses.RunAsync(start, read, input, answers, cancel, prompting is null ? null : prompting.Runs);
            if (prompting is not null)
            {
                Prompting.Heard(ran.Error);
            }
            return (new GitResult(ran.ExitCode, string.Empty, ran.Error, prompting?.Refused), ran.Output);
        }
        catch (System.ComponentModel.Win32Exception unstarted)
        {
            return (Directory.Exists(folder) ? StartFailure(unstarted) : Gone(folder), default);
        }
        catch (IOException unread)
        {
            return (ReadFailure(unread), default);
        }
    }

    /// <summary>What git writes, as text in UTF-8, or in the encoding a byte order mark it begins with names.</summary>
    private static async Task<string> TextAsync(Stream output)
    {
        using StreamReader reader = new(output, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    /// <summary>What git writes, whole, as bytes: for output that counts its lengths in bytes (cat-file's objects) and a
    /// file's text in its own encoding (blame's).</summary>
    private static async Task<byte[]> WholeAsync(Stream output)
    {
        using PooledBytes bytes = new();
        await bytes.ReadToEndAsync(output);
        return bytes.Written.ToArray();
    }

    /// <summary>Git started to ask <paramref name="asker"/> what it asks of the person through a socket of its own, the
    /// operation's work let go while they answer; none where there is no asker, or no socket to be had.</summary>
    private static Prompting? Asking(ProcessStartInfo start, GitAsker? asker, GitHold held, CancellationToken cancel)
    {
        if (asker is null || Prompting.Open(asker, held.Apart, cancel) is not { } prompting)
        {
            return null;
        }
        ChildProcesses.WithVariables(start, prompting.Variables);
        return prompting;
    }

    /// <summary>What git writes, read into a buffer of the pool's as it comes, and whether git succeeded; nothing where it
    /// did not run, or what it wrote could not be read.</summary>
    private static async Task<GitOutput> OutputOfAsync(string folder, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        (GitResult ran, PooledBytes? bytes) = await RunAsync(folder, arguments, PooledAsync, environment: environment);
        return new GitOutput(ran.Succeeded, bytes);
    }

    /// <summary>What git writes, read into a buffer of the pool's as it comes, which its taker disposes.</summary>
    private static async Task<PooledBytes> PooledAsync(Stream output)
    {
        PooledBytes bytes = new();
        await bytes.ReadToEndAsync(output);
        return bytes;
    }

    /// <summary>Whether a git command succeeded and what it wrote, its bytes in a buffer of the pool's until disposed.</summary>
    private sealed class GitOutput(bool succeeded, PooledBytes? bytes) : IDisposable
    {
        public bool Succeeded => succeeded;

        public ReadOnlySpan<byte> Bytes => bytes is null ? [] : bytes.Written;

        public void Dispose() => bytes?.Dispose();
    }

    /// <summary>What a call gives in place of git's exit code when git is not there to run, as a shell does.</summary>
    private const int Absent = 127;

    /// <summary>What a call gives in place of git's exit code when git is there but could not be started (its command
    /// line too long, its file not one the system runs), as a shell does.</summary>
    private const int Unstarted = 126;

    /// <summary>What a call gives in place of git's exit code when what git wrote could not be read: none git exits with.</summary>
    private const int Unread = -1;

    /// <summary>What a call gives in place of git's exit code when git was not run in a folder not trusted, as it could not
    /// be kept from running the programs the repository names: none git exits with.</summary>
    private const int Withheld = -2;

    /// <summary>Why a call has no answer from git where what it wrote could not be read, in the system's words.</summary>
    internal static GitResult ReadFailure(IOException failed) => new(Unread, string.Empty, $"git's output could not be read: {failed.Message}");

    /// <summary>Why git did not start, in the system's words: as git not installed only where the system found no file to
    /// run (ENOENT, or on Windows no such file or path), else as git that could not be started.</summary>
    internal static GitResult StartFailure(System.ComponentModel.Win32Exception failed) =>
        failed.NativeErrorCode == 2 || (OperatingSystem.IsWindows() && failed.NativeErrorCode == 3)
            ? new GitResult(Absent, string.Empty, $"git is not installed: {failed.Message}")
            : new GitResult(Unstarted, string.Empty, $"git could not be started: {failed.Message}");

    /// <summary>Why a call has no answer from git where the folder it was asked of is not there: git was not run, never
    /// that git is not installed, though a start there fails on macOS and Linux as one of a program not found does.</summary>
    internal static GitResult Gone(string folder) => new(Unstarted, string.Empty, $"git was not run in {folder}, which is not there");

    private static GitResult NotInstalled =>
        new(Absent, string.Empty, OperatingSystem.IsMacOS() ? "git is not installed: xcode-select --install installs it" : "git is not installed");

    /// <summary>Why no git can be run here, in the words a call would fail with: it is not installed; null where one is.</summary>
    public static async Task<string?> MissingAsync() => await Programs.GitAsync() is null ? NotInstalled.Error : null;

    /// <summary>What git exits with when what it is asked for is not there (a file at a revision, an upstream, a first
    /// commit, a working tree): the answer no to a question, which the log leaves out.</summary>
    private const int NotThere = 128;

    /// <summary>How git is started for every call; else why not: it is not installed, or in a folder the person has not
    /// trusted it could not be kept to the folder's files and from running the programs the repository names
    /// (<see cref="UntrustedGit"/>). A clone or an init reads no repository's configuration where it starts, and makes
    /// its own of the person's: it is fitted to no folder's trust.</summary>
    private static async Task<(ProcessStartInfo? Start, GitResult? Unstarted)> StartAsync(string folder, IReadOnlyList<string> arguments)
    {
        if (await Programs.GitAsync() is not { } git)
        {
            return (null, NotInstalled);
        }
        ProcessStartInfo start = ChildProcesses.Command(git, arguments, folder);
        bool makes = CommandAt(arguments) is >= 0 and int at && arguments[at] is "clone" or "init";
        if (!makes && !WorkspaceTrust.Trusts(folder) && await UntrustedGit.FitAsync(start, git, folder) is { } why)
        {
            return (null, Directory.Exists(folder) ? new GitResult(Withheld, string.Empty, why) : Gone(folder));
        }
        // Git never stops to ask on a terminal: it has none (ChildProcesses), and this keeps it from looking for one.
        start.Environment[GitVariables.TerminalPrompt] = "0";
        // A background status or diff never takes the index's lock, so a stash, commit or checkout then never fails on it.
        start.Environment[GitVariables.OptionalLocks] = "0";
        MsysGpg.FitGit(start);
        return (start, null);
    }
}

/// <summary>How Update Project brings the upstream's commits in, as the person's git config says (branch.NAME.rebase,
/// else pull.rebase; and pull.ff): rebased onto them, with <see cref="RebaseMerges"/> keeping the branch's merges, or
/// merged, <see cref="FastForward"/> the merge's --ff-only, --no-ff or --ff; merged where neither rebase setting is there
/// (<see cref="Configured"/> false).</summary>
public sealed record UpdatePlan(bool Rebase, bool RebaseMerges, string? FastForward, bool Configured)
{
    /// <summary>The plan `git config -z --get-regexp` wrote for <paramref name="branch"/>: each setting its name, a line
    /// break and its value, ended by a NUL; a name alone (no value) is true. A rebase setting git does not know merges.</summary>
    public static UpdatePlan Of(string config, string branch)
    {
        Dictionary<string, string> set = new(StringComparer.Ordinal);
        foreach (string entry in config.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int line = entry.IndexOf('\n', StringComparison.Ordinal);
            set[line < 0 ? entry : entry[..line]] = line < 0 ? "true" : entry[(line + 1)..];
        }
        string? rebase = set.GetValueOrDefault($"branch.{branch}.rebase") ?? set.GetValueOrDefault("pull.rebase");
        string? fastForward = set.GetValueOrDefault("pull.ff") is not { } ff ? null
            : Is(ff, "only") ? "--ff-only"
            : Truth(ff) is { } on ? (on ? "--ff" : "--no-ff") : null;
        bool merges = rebase is not null && Is(rebase, "merges", "m");
        bool rebases = rebase is not null && (merges || Truth(rebase) == true || Is(rebase, "interactive", "i"));
        return new UpdatePlan(rebases, merges, fastForward, rebase is not null);

        // As git reads a boolean: null where the value is none.
        static bool? Truth(string value) => Is(value, "true", "yes", "on", "1") ? true : Is(value, "false", "no", "off", "0", "") ? false : null;

        static bool Is(string value, params string[] words) => words.Any(word => word.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>How Reset Current Branch to Here treats the changes after the commit.</summary>
public enum ResetMode
{
    Soft,
    Mixed,
    Hard,
}
