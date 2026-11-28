using System.Text;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>Passenger as git's sequence editor for an interactive rebase it runs: git, given <see cref="Marker"/> after
/// the program, hands it the list of steps it made (git-rebase-todo), and it writes over it the list the person
/// planned, from the file <see cref="Variable"/> names, once git's list names the commits the plan was made from
/// (<see cref="Moved"/>). Every helper of git's that Passenger is shares git's environment, so the marker, never the
/// variable alone, tells this one apart.</summary>
public static class RebaseTodo
{
    /// <summary>What a planned list's first line begins with, a comment to git: then the commits the plan was made from,
    /// oldest first.</summary>
    internal const string Planned = "# Passenger planned from:";

    /// <summary>Why a planned rebase is not run where the branch is no longer as it was planned.</summary>
    public const string MovedSince = "The branch moved since the rebase was planned, so it did not run: plan it again";

    /// <summary>The variable naming the file of the planned list of steps.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_REBASE_TODO";

    /// <summary>The word git's sequence editor is given before the list's file.</summary>
    [IdentifierHome]
    public const string Marker = "--rebase-todo";

    /// <summary>What Main asks before anything starts: where git runs this executable as its sequence editor, the planned
    /// list written over git's and the exit code; else null, and Passenger starts as itself.</summary>
    public static int? Write(string[] args)
    {
        if (args is not [Marker, { Length: > 0 } todo] || Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } planned)
        {
            return null;
        }
        try
        {
            byte[] plan = File.ReadAllBytes(planned);
            if (Moved(File.ReadAllText(todo), Encoding.UTF8.GetString(plan)))
            {
                // Git ends the rebase where its editor fails, before it changes anything.
                Console.Error.WriteLine(MovedSince);
                return 1;
            }
            AtomicFile.Write(todo, plan);
            return 0;
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Passenger could not write the rebase's steps: {unwritten.Message}");
            return 1;
        }
    }

    /// <summary>Whether git's own list of steps (<paramref name="listed"/>) picks other commits than those the plan was made
    /// from (<see cref="Planned"/>), or in another order: a commit made on the branch, or one taken away, since it was planned.
    /// Git names a commit by a prefix of its hash.</summary>
    public static bool Moved(string listed, string planned)
    {
        string first = planned.Split('\n', 2)[0].TrimEnd('\r');
        string[] meant = first.StartsWith(Planned, StringComparison.Ordinal)
            ? first[Planned.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        List<string> picked = [];
        foreach (string line in listed.Split('\n'))
        {
            if (line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries) is ["pick" or "p", { } commit, ..])
            {
                picked.Add(commit);
            }
        }
        return meant.Length == 0 || picked.Count != meant.Length || picked.Zip(meant).Any(p => !p.Second.StartsWith(p.First, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The sequence editor git runs <paramref name="program"/> as, through its shell: the program's path as one word
    /// sh takes as it is, then the marker.</summary>
    internal static string Editor(string program) => $"'{program.Replace("'", "'\\''", StringComparison.Ordinal)}' {Marker}";
}

public sealed partial class Git
{
    /// <summary>What Interactively Rebase from Here plans: <paramref name="from"/> and every commit after it up to HEAD,
    /// oldest first with their whole messages, each picked, onto from's parent (none where from is the first commit); or
    /// why there is nothing to plan: from is not in HEAD's history, or a merge is among them, which a rebase of commits one
    /// by one would leave out.</summary>
    public async Task<(RebasePlan? Plan, string? WhyNot)> RebasePlanAsync(string from)
    {
        if (await CommitOfAsync(from) is not { } start)
        {
            return (null, $"{GitHashes.Short(from)} is no commit here");
        }
        if (!(await RunAsync(Root, ["merge-base", "--is-ancestor", start, "HEAD"], answers: [1])).Succeeded)
        {
            return (null, $"{GitHashes.Short(start)} is not in the history of {await BranchAsync() ?? "HEAD"}, which a rebase rewrites");
        }
        string? onto = await CommitOfAsync($"{start}^");
        string range = onto is null ? "HEAD" : $"{onto}..HEAD";
        IReadOnlyList<CommitInfo> commits = await LogAsync(int.MaxValue, [range]);
        if (commits.FirstOrDefault(c => c.Parents.Count > 1) is { } merge)
        {
            return (null, $"The commits from {GitHashes.Short(start)} hold a merge, {merge.Short}, which a rebase of commits one by one leaves out");
        }
        GitResult read = await RunAsync(Root, ["log", "--format=%H%x1f%B%x1e", "--end-of-options", range, "--"]);
        if (!read.Succeeded)
        {
            return (null, read.Message);
        }
        Dictionary<string, string> messages = new(StringComparer.Ordinal);
        foreach (string record in read.Output.Split('\x1e'))
        {
            string[] fields = record.TrimStart('\n').Split('\x1f', 2);
            if (fields is [{ Length: > 0 } hash, { } message])
            {
                messages[hash] = message.TrimEnd();
            }
        }
        return (RebasePlan.Of(onto, [.. commits.Reverse().Select(c => (c, messages.GetValueOrDefault(c.Hash) ?? c.Subject))]), null);
    }

    /// <summary>An interactive rebase as planned, run as git's own with Passenger its sequence editor
    /// (<see cref="RebaseTodo"/>, <paramref name="program"/>): the person's changes kept aside and put back at its end
    /// (--autostash), each commit signed as git's config has it through <paramref name="signer"/>, and every message the
    /// plan's, a commit made under another message picked as a twin of it (<see cref="TwinAsync"/>), so git asks for none.
    /// It runs only on the branch as planned (<see cref="RebaseTodo.MovedSince"/>), and stops at an edit or for conflicts.</summary>
    public async Task<GitResult> InteractiveRebaseAsync(RebasePlan plan, string program, Signer? signer = null)
    {
        if (plan.Commits.MaxBy(c => c.Place) is { } newest && await CommitOfAsync("HEAD") != newest.Commit.Hash)
        {
            return new GitResult(1, string.Empty, RebaseTodo.MovedSince);
        }
        if (await MayFilterAsync() && await UnfilteredAsync(() => InteractivelyRebasedAsync(plan), plan.Base is { } upon ? [upon, "HEAD"] : ["HEAD"]) is { } unfiltered)
        {
            return unfiltered;
        }
        Dictionary<string, string> twins = new(StringComparer.Ordinal);
        foreach ((PlannedCommit head, string message) in plan.Rewritten)
        {
            if (await TwinAsync(head.Commit, message) is not { } twin)
            {
                return new GitResult(1, string.Empty, $"Git made no commit of {head.Commit.Short} under its new message");
            }
            twins[head.Commit.Hash] = twin;
        }
        CommitFile todo;
        try
        {
            todo = CommitFile.Todo(plan.Todo(twins));
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            return new GitResult(1, string.Empty, $"The rebase's steps could not be written: {unwritten.Message}");
        }
        using (todo)
        {
            Dictionary<string, string> environment = new(NoEditor)
            {
                [GitVariables.SequenceEditor] = RebaseTodo.Editor(program),
                [RebaseTodo.Variable] = todo.Path,
            };
            string[] onto = plan.Base is { } on ? ["--end-of-options", on] : ["--root"];
            return await WritingOutAsync(() => InteractivelyRebasedAsync(plan), () => RunSignedAsync(["rebase", "--interactive", "--autostash", .. onto], signer, environment));
        }
    }

    /// <summary>The files an interactive rebase writes out: those its commits changed (every file of HEAD's where the first
    /// commit is among them), and those its autostash puts back.</summary>
    private async Task<IReadOnlyList<string>?> InteractivelyRebasedAsync(RebasePlan plan)
    {
        IReadOnlyList<string>? rebased = plan.Base is { } upon ? await RebasedAsync(upon, "HEAD") : await FilesOfAsync("HEAD");
        IReadOnlyList<string>? kept = await DifferingAsync("HEAD");
        return rebased is null || kept is null ? null : AllOf(rebased, kept);
    }

    /// <summary>What a twin's parent's message begins with, the commit it is the twin of following.</summary>
    private const string TwinOf = "Passenger's twin of ";

    /// <summary>A commit making <paramref name="commit"/>'s change under another message, by its author at its time: its
    /// tree over a parent of its own holding its parent's tree and naming the commit (<see cref="TwinOf"/>), which is in
    /// no branch, so a pick of it makes the commit's change and git never takes it as it is, whatever comes before it;
    /// unsigned, as it is only picked. Null where git made none.</summary>
    private async Task<string?> TwinAsync(CommitInfo commit, string message)
    {
        GitResult author = await RunAsync(Root, ["log", "-1", "--format=%an%x00%ae%x00%ad", "--date=raw", "--end-of-options", commit.Hash, "--"]);
        if (!author.Succeeded || author.Output.TrimEnd('\n', '\r').Split('\0') is not [{ } name, { } email, { Length: > 0 } date])
        {
            return null;
        }
        GitResult before = commit.Parents.Count > 0
            ? await RunAsync(Root, ["rev-parse", "--verify", "--end-of-options", $"{commit.Parents[0]}^{{tree}}"])
            : await RunAsync(Root, ["mktree"], input: string.Empty);
        if (!before.Succeeded)
        {
            return null;
        }
        GitResult parent = await RunAsync(Root, ["commit-tree", "--no-gpg-sign", before.Output.Trim()], input: $"{TwinOf}{commit.Hash}\n");
        if (!parent.Succeeded)
        {
            return null;
        }
        Dictionary<string, string> by = new()
        {
            [GitVariables.AuthorName] = name,
            [GitVariables.AuthorEmail] = email,
            [GitVariables.AuthorDate] = date,
        };
        GitResult twin = await RunAsync(Root, ["commit-tree", "--no-gpg-sign", $"{commit.Hash}^{{tree}}", "-p", parent.Output.Trim()],
            input: message.ReplaceLineEndings("\n").TrimEnd() + "\n", environment: by);
        return twin.Succeeded && twin.Output.Trim() is { Length: > 0 } made ? made : null;
    }

    /// <summary>The commit a rebase stopped at as the person knows it, with its subject: the commit a twin stands for where
    /// Passenger picked one under a new message (<see cref="TwinAsync"/>), else the commit itself, its subject null where
    /// git could not say. The last one read is kept, as every status read while the rebase stands asks again.</summary>
    private async Task<(string Hash, string? Subject)> PersonsCommitAsync(string applying)
    {
        if (stoppedAt is { } last && last.Applying == applying)
        {
            return (last.Hash, last.Subject);
        }
        // The commit and its first parent: a twin's is parentless, its message naming the commit it stands for.
        GitResult read = await RunAsync(Root, ["log", "-2", "--format=%H%x1f%P%x1f%s%x1e", "--end-of-options", applying, "--"]);
        string[][] records = [.. read.Output.Split('\x1e').Select(r => r.TrimStart('\n', '\r').Split('\x1f')).Where(r => r is [{ Length: > 0 }, _, _])];
        if (!read.Succeeded || records.Length == 0)
        {
            return (applying, null);
        }
        (string Hash, string? Subject) person = (applying, records[0][2].TrimEnd('\n', '\r'));
        if (records is [_, [{ } parent, { Length: 0 }, { } stand], ..] && parent == records[0][1].Trim() && stand.StartsWith(TwinOf, StringComparison.Ordinal)
            && stand[TwinOf.Length..].TrimEnd('\n', '\r') is { } twinned && GitHashes.IsWhole(twinned))
        {
            GitResult subject = await RunAsync(Root, ["log", "-1", "--format=%s", "--end-of-options", twinned, "--"]);
            if (!subject.Succeeded)
            {
                return (twinned, null);
            }
            person = (twinned, subject.Output.TrimEnd('\n', '\r'));
        }
        stoppedAt = (applying, person.Hash, person.Subject);
        return person;
    }

    private (string Applying, string Hash, string? Subject)? stoppedAt;

    /// <summary>The name and email a replay's commits are made by, in no repository, so no one's.</summary>
    private static readonly IReadOnlyDictionary<string, string> Replayer = new Dictionary<string, string>
    {
        [GitVariables.AuthorName] = "Passenger",
        [GitVariables.AuthorEmail] = "replay@passenger.invalid",
        [GitVariables.CommitterName] = "Passenger",
        [GitVariables.CommitterEmail] = "replay@passenger.invalid",
    };

    /// <summary>Where a planned rebase will stop for conflicts, foreseen by replaying it with merge-tree into objects of its
    /// own (<see cref="CommitFile.Replay"/>): from the first step git makes anew (<see cref="RebasePlan.FirstReplayed"/>),
    /// each kept step's change merged into what those before it made; one that conflicts stops the rebase, those after it
    /// replayed as if its conflicts were resolved to the commit's own files. Null where git cannot say: one before 2.40,
    /// whose merge-tree takes no merge base, or a replay it could not make.</summary>
    public async Task<IReadOnlyList<ForeseenConflict>?> ConflictsAheadAsync(RebasePlan plan, CancellationToken cancel = default)
    {
        List<ForeseenConflict> stops = [];
        int first = plan.FirstReplayed;
        if (first == plan.Commits.Count)
        {
            return stops;
        }
        CommitFile replay;
        try
        {
            replay = CommitFile.Replay();
        }
        catch (Exception unmade) when (unmade is IOException or UnauthorizedAccessException)
        {
            PassengerLog.Write($"A rebase's conflicts could not be foreseen, its replay's folder unmade: {unmade.Message}");
            return null;
        }
        using (replay)
        {
            Dictionary<string, string> environment = new(Replayer)
            {
                [GitVariables.ObjectDirectory] = replay.Objects,
                [GitVariables.AlternateObjectDirectories] = AlternateObjects(System.IO.Path.Combine(CommonDir, "objects")),
                [GitVariables.IndexFile] = replay.Path,
            };
            string? empty = null;
            string? onto = first > 0 ? plan.Commits[first - 1].Commit.Hash : plan.Base;
            foreach (PlannedCommit step in plan.Commits.Skip(first).Where(s => s.Command is not RebaseCommand.Drop))
            {
                if (step.Commit.Parents.Count == 0 || onto is null)
                {
                    empty ??= await ReplayedAsync(null, null, environment, cancel);
                }
                string? basis = step.Commit.Parents.Count > 0 ? step.Commit.Parents[0] : empty;
                onto ??= empty;
                if (basis is null || onto is null)
                {
                    return null;
                }
                GitResult merged = await RunAsync(Root, ["merge-tree", "--write-tree", "--no-messages", "-z", $"--merge-base={basis}", "--end-of-options", onto, step.Commit.Hash],
                    environment: environment, answers: [1], cancel: cancel);
                string[] fields = merged.Output.Split('\0');
                if (merged.Code is not (0 or 1) || fields[0].Trim() is not { Length: > 0 } tree)
                {
                    return null;
                }
                if (merged.Code == 1)
                {
                    List<(string Entry, string Path)> entries = [.. fields.Skip(1).Where(f => f.Contains('\t')).Select(f => (f[..f.IndexOf('\t')], f[(f.IndexOf('\t') + 1)..]))];
                    stops.Add(new ForeseenConflict(step.Commit.Hash, [.. entries.Select(e => e.Path).Distinct(StringComparer.Ordinal)], Sure: stops.Count == 0));
                    if (await OwnFilesAsync(tree, entries, environment, cancel) is not { } resolved)
                    {
                        return stops;
                    }
                    tree = resolved;
                }
                onto = await ReplayedAsync(tree, onto, environment, cancel);
            }
            return stops;
        }
    }

    /// <summary>A replay's commit of <paramref name="tree"/> (the empty tree where null) over <paramref name="parent"/>, as
    /// git's merge-tree before 2.45 merges only commits; null where git made none.</summary>
    private async Task<string?> ReplayedAsync(string? tree, string? parent, IReadOnlyDictionary<string, string> environment, CancellationToken cancel)
    {
        if (tree is null)
        {
            GitResult made = await RunAsync(Root, ["mktree"], input: string.Empty, environment: environment, cancel: cancel);
            tree = made.Succeeded ? made.Output.Trim() : null;
        }
        if (tree is not { Length: > 0 })
        {
            return null;
        }
        GitResult commit = await RunAsync(Root, ["commit-tree", "--no-gpg-sign", tree, .. parent is null ? Array.Empty<string>() : ["-p", parent]],
            input: "Passenger's replay of a rebase\n", environment: environment, cancel: cancel);
        return commit.Succeeded && commit.Output.Trim() is { Length: > 0 } hash ? hash : null;
    }

    /// <summary>A merge's tree with each file in conflict as the commit merged in has it, gone where that commit has none
    /// (git's stage 3 of each, from merge-tree's <paramref name="entries"/>); null where git could not make it.</summary>
    private async Task<string?> OwnFilesAsync(string tree, IReadOnlyList<(string Entry, string Path)> entries, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancel)
    {
        if (!(await RunAsync(Root, ["read-tree", "--end-of-options", tree], environment: environment, cancel: cancel)).Succeeded)
        {
            return null;
        }
        StringBuilder input = new();
        foreach (string path in entries.Select(e => e.Path).Distinct(StringComparer.Ordinal))
        {
            // Each entry is "mode object stage"; the stage merged in is 3.
            string? theirs = entries.Where(e => e.Path == path).Select(e => e.Entry.Split(' ')).Where(f => f is [_, _, "3"]).Select(f => $"{f[0]} {f[1]}").FirstOrDefault();
            input.Append(theirs ?? $"0 {new string('0', tree.Length)}").Append('\t').Append(path).Append('\0');
        }
        if (!(await RunAsync(Root, ["update-index", "-z", "--index-info"], input: input.ToString(), environment: environment, cancel: cancel)).Succeeded)
        {
            return null;
        }
        GitResult written = await RunAsync(Root, ["write-tree"], environment: environment, cancel: cancel);
        return written.Succeeded && written.Output.Trim() is { Length: > 0 } made ? made : null;
    }

    /// <summary>A folder as git's list of alternate object folders takes it: quoted as C quotes a string where it holds the
    /// list's separator or begins with a quote.</summary>
    private static string AlternateObjects(string folder) => folder.Contains(System.IO.Path.PathSeparator) || folder.StartsWith('"')
        ? $"\"{folder.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
        : folder;

    /// <summary>Skip Commit: the commit a rebase stopped at for its conflicts left out, the tree put back as the commits before
    /// it left it, and the rest replayed.</summary>
    public Task<GitResult> SkipRebaseAsync(Signer? signer = null) =>
        WritingOutAsync(async () => AllOf(await DifferingAsync("HEAD"), await ContinuedAsync()), () => RunSignedAsync(["rebase", "--skip"], signer, NoEditor));
}
