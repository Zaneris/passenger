using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>Passenger as git's sequence editor for an interactive rebase it runs: git, given <see cref="Marker"/> after
/// the program, hands it the list of steps it made (git-rebase-todo), and it writes over it the list the person
/// planned, from the file <see cref="Variable"/> names. Every helper of git's that Passenger is shares git's environment,
/// so the marker, never the variable alone, tells this one apart.</summary>
public static class RebaseTodo
{
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
            AtomicFile.Write(todo, File.ReadAllBytes(planned));
            return 0;
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Passenger could not write the rebase's steps: {unwritten.Message}");
            return 1;
        }
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
    /// It stops where git does: at an edit, or for conflicts.</summary>
    public async Task<GitResult> InteractiveRebaseAsync(RebasePlan plan, string program, Signer? signer = null)
    {
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

    /// <summary>A commit making <paramref name="commit"/>'s change under another message, by its author at its time: its
    /// tree over a parent of its own holding its parent's tree, which is in no branch, so a pick of it makes the commit's
    /// change and git never takes it as it is, whatever comes before it; unsigned, as it is only picked. Null where git
    /// made none.</summary>
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
        GitResult parent = await RunAsync(Root, ["commit-tree", "--no-gpg-sign", before.Output.Trim()], input: $"{commit.Short}'s parent, for its twin\n");
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

    /// <summary>Skip Commit: the commit a rebase stopped at for its conflicts left out, the tree put back as the commits before
    /// it left it, and the rest replayed.</summary>
    public Task<GitResult> SkipRebaseAsync(Signer? signer = null) =>
        WritingOutAsync(async () => AllOf(await DifferingAsync("HEAD"), await ContinuedAsync()), () => RunSignedAsync(["rebase", "--skip"], signer, NoEditor));
}
