using System.Runtime.InteropServices;
using System.Text;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>What an interactive rebase does with one of the branch's commits: keeps it (pick), keeps it under another
/// message (reword), stops after it for the person to change it (edit), folds it into the commit before it with both
/// messages (squash) or with that commit's alone (fixup), or leaves it out (drop).</summary>
public enum RebaseCommand
{
    Pick,
    Reword,
    Edit,
    Squash,
    Fixup,
    Drop,
}

/// <summary>One of the branch's commits as an interactive rebase plans it: the commit, its whole message, its place among
/// the branch's commits as they stand (from 0, the oldest), what is done with it, and the message the person wrote for
/// the commit it heads (null: the one the plan makes of it, <see cref="RebasePlan.DefaultMessageOf"/>).</summary>
public sealed record PlannedCommit(CommitInfo Commit, string Message, int Place, RebaseCommand Command = RebaseCommand.Pick, string? Written = null)
{
    /// <summary>Whether it folds into the commit before it (squash, fixup).</summary>
    public bool Folds => Command is RebaseCommand.Squash or RebaseCommand.Fixup;
}

/// <summary>A commit an interactive rebase makes, as its plan shows it before it runs: the planned commits it is made of
/// (the first, then those folded into it), its message, whether the rebase stops after it for the person to change it,
/// whether it stands among the others elsewhere than its first commit did, and whether git keeps that commit as it is,
/// as it keeps each before the first change.</summary>
public sealed record RebasedCommit(IReadOnlyList<PlannedCommit> From, string Message, bool Stops, bool Moved, bool Kept)
{
    /// <summary>The message's first line.</summary>
    public string Subject => Message.Split('\n')[0].TrimEnd('\r');

    /// <summary>Whether it is made under another message than its first commit's.</summary>
    public bool Reworded => !RebasePlan.SameMessage(Message, From[0].Message);
}

/// <summary>A planned commit that stops the rebase for conflicts, as foreseen before it runs
/// (<see cref="Git.ConflictsAheadAsync"/>): the commit, its files in conflict as git names them from the working tree's
/// top, and whether it surely stops there, as the first stop does, or is foreseen as if the conflicts before it were
/// resolved to each commit's own files.</summary>
public sealed record ForeseenConflict(string Commit, IReadOnlyList<string> Files, bool Sure);

/// <summary>An interactive rebase as the person plans it (Interactively Rebase from Here): the commit its commits go onto
/// (null where the branch's first commit is among them), and those commits oldest first, as git's list of steps has
/// them. What it makes (<see cref="Outcome"/>) is what the rebase leaves once it has run: every message is the plan's, a
/// commit made under another message than its first commit's picked as a twin of that commit carrying it
/// (<see cref="Rewritten"/>), and every commit folded in by fixup, so git asks for none.</summary>
public sealed record RebasePlan(string? Base, IReadOnlyList<PlannedCommit> Commits)
{
    /// <summary>The branch's commits as they stand, oldest first with their messages, each picked.</summary>
    public static RebasePlan Of(string? onto, IReadOnlyList<(CommitInfo Commit, string Message)> commits) =>
        new(onto, [.. commits.Select((c, at) => new PlannedCommit(c.Commit, c.Message, at))]);

    /// <summary>The commits the rebase makes, oldest first.</summary>
    public IReadOnlyList<RebasedCommit> Outcome()
    {
        List<RebasedCommit> made = [];
        IReadOnlySet<int> inPlace = InPlace();
        bool keeping = true;
        for (int at = 0; at < Commits.Count; at++)
        {
            PlannedCommit head = Commits[at];
            if (head.Command is RebaseCommand.Drop || head.Folds)
            {
                keeping = false;
                continue;
            }
            List<PlannedCommit> from = [head, .. FoldedInto(at)];
            string message = MessageOf(at);
            // Git leaves a commit as it is while it and each before it stand where they stood, unchanged.
            keeping = keeping && head.Place == at && from.Count == 1 && head.Command is not RebaseCommand.Edit && SameMessage(message, head.Message);
            made.Add(new RebasedCommit(from, message, head.Command is RebaseCommand.Edit, !inPlace.Contains(at), keeping));
        }
        return made;
    }

    /// <summary>The commits it leaves out.</summary>
    public IReadOnlyList<PlannedCommit> Dropped => [.. Commits.Where(c => c.Command is RebaseCommand.Drop)];

    /// <summary>Where git begins making each step's change anew: the first step that stands elsewhere than its commit stood,
    /// or is left out. Each step before it leaves its commit's own tree, so none of them can stop for conflicts; the count
    /// of steps where there is none.</summary>
    public int FirstReplayed
    {
        get
        {
            int at = 0;
            while (at < Commits.Count && Commits[at].Place == at && Commits[at].Command is not RebaseCommand.Drop)
            {
                at++;
            }
            return at;
        }
    }

    /// <summary>What the trees the rebase makes depend on: the commits it keeps, in order. Two plans alike in it stop for
    /// the same conflicts, whatever they do with the messages, the folds and the edits.</summary>
    public string ReplayKey => string.Join(' ', Commits.Where(c => c.Command is not RebaseCommand.Drop).Select(c => c.Commit.Hash));

    /// <summary>Where the commit that the step at <paramref name="at"/> is part of is headed: itself where it heads one, the
    /// nearest kept step before it that does where it folds; -1 where it is left out, or folds into none.</summary>
    public int HeadOf(int at)
    {
        if (Commits[at].Command is RebaseCommand.Drop)
        {
            return -1;
        }
        for (int head = at; head >= 0; head--)
        {
            if (Commits[head].Command is not (RebaseCommand.Drop or RebaseCommand.Squash or RebaseCommand.Fixup))
            {
                return head;
            }
        }
        return -1;
    }

    /// <summary>The steps folded into the commit headed at <paramref name="head"/>, in order: those after it up to the next
    /// it does not fold, the dropped ones between left out.</summary>
    public IEnumerable<PlannedCommit> FoldedInto(int head)
    {
        for (int at = head + 1; at < Commits.Count && (Commits[at].Folds || Commits[at].Command is RebaseCommand.Drop); at++)
        {
            if (Commits[at].Folds)
            {
                yield return Commits[at];
            }
        }
    }

    /// <summary>The message the commit headed at <paramref name="head"/> is made with: the one the person wrote for it, else
    /// <see cref="DefaultMessageOf"/>.</summary>
    public string MessageOf(int head) => Commits[head].Written ?? DefaultMessageOf(head);

    /// <summary>The message the commit headed at <paramref name="head"/> is made with unless the person writes another: its
    /// own, and after it, a blank line apart, each squashed commit's, as git joins them (a fixup's left out).</summary>
    public string DefaultMessageOf(int head)
    {
        StringBuilder message = new(Commits[head].Message.TrimEnd());
        foreach (PlannedCommit squashed in FoldedInto(head).Where(c => c.Command is RebaseCommand.Squash))
        {
            message.Append("\n\n").Append(squashed.Message.TrimEnd());
        }
        return message.ToString();
    }

    /// <summary>Whether the rebase changes anything: a commit moved, left out, folded in, edited or made under another
    /// message.</summary>
    public bool Changes => Commits.Select((c, at) => c.Place != at || c.Command is not (RebaseCommand.Pick or RebaseCommand.Reword)).Any(changed => changed)
        || Rewritten.Any();

    /// <summary>Why the rebase cannot run as planned; null where it can.</summary>
    public string? WhyNot
    {
        get
        {
            if (Commits.FirstOrDefault(c => c.Command is not RebaseCommand.Drop) is { Folds: true } first)
            {
                return $"{first.Commit.Short} cannot be {Word(first.Command)}: no commit kept comes before it to fold it into";
            }
            if (Outcome().FirstOrDefault(c => c.Message.Trim().Length == 0) is { } empty)
            {
                return $"The commit made of {empty.From[0].Commit.Short} needs a message";
            }
            return Changes ? null : "Nothing changes yet: move a commit, or choose another action for one";
        }
    }

    /// <summary>The steps that head a commit made under another message than their own, with that message: each is picked
    /// as a twin of its commit carrying the message, so git asks for none.</summary>
    public IEnumerable<(PlannedCommit Head, string Message)> Rewritten
    {
        get
        {
            for (int at = 0; at < Commits.Count; at++)
            {
                if (Commits[at].Command is not (RebaseCommand.Drop or RebaseCommand.Squash or RebaseCommand.Fixup) && MessageOf(at) is { } message
                    && !SameMessage(message, Commits[at].Message))
                {
                    yield return (Commits[at], message);
                }
            }
        }
    }

    /// <summary>Git's list of steps for the plan, oldest first: a head picked (or edited) as it is, or as its twin from
    /// <paramref name="twins"/> (by its commit's hash) after a drop of the commit itself, which git then finds still
    /// listed; each folded commit a fixup, as the head's message is the whole commit's; a dropped one a drop. Its first line,
    /// a comment to git, names the commits it was planned from (<see cref="RebaseTodo.Planned"/>).</summary>
    public string Todo(IReadOnlyDictionary<string, string> twins)
    {
        StringBuilder todo = new();
        todo.Append(RebaseTodo.Planned).Append(string.Concat(Commits.OrderBy(c => c.Place).Select(c => $" {c.Commit.Hash}"))).Append('\n');
        foreach (PlannedCommit step in Commits)
        {
            string hash = step.Commit.Hash;
            if (step.Command is RebaseCommand.Drop)
            {
                Line("drop", hash);
            }
            else if (step.Folds)
            {
                Line("fixup", hash);
            }
            else
            {
                string verb = step.Command is RebaseCommand.Edit ? "edit" : "pick";
                if (twins.TryGetValue(hash, out string? twin))
                {
                    Line("drop", hash);
                    Line(verb, twin);
                }
                else
                {
                    Line(verb, hash);
                }
            }

            void Line(string verb, string commit) => todo.Append(verb).Append(' ').Append(commit).Append(' ').Append(step.Commit.Subject).Append('\n');
        }
        return todo.ToString();
    }

    /// <summary>The commits it moves, by where they stand in the plan: those heading a commit outside the longest run of
    /// heads still in the order they stood in, the fewest moves that make it.</summary>
    private HashSet<int> InPlace()
    {
        List<int> heads = [.. Enumerable.Range(0, Commits.Count).Where(at => Commits[at].Command is not (RebaseCommand.Drop or RebaseCommand.Squash or RebaseCommand.Fixup))];
        // The longest run of heads whose places rise, by patience: each pile's top place and the head on it.
        List<int> tops = [];
        List<int> onTop = [];
        int[] before = new int[heads.Count];
        for (int i = 0; i < heads.Count; i++)
        {
            int place = Commits[heads[i]].Place;
            int pile = Sorted.FirstAtOrAfter<int>(CollectionsMarshal.AsSpan(tops), place);
            before[i] = pile > 0 ? onTop[pile - 1] : -1;
            if (pile == tops.Count)
            {
                tops.Add(place);
                onTop.Add(i);
            }
            else
            {
                tops[pile] = place;
                onTop[pile] = i;
            }
        }
        HashSet<int> kept = [];
        for (int i = onTop.Count > 0 ? onTop[^1] : -1; i >= 0; i = before[i])
        {
            kept.Add(heads[i]);
        }
        return kept;
    }

    private static string Word(RebaseCommand command) => command is RebaseCommand.Squash ? "squashed" : "fixed up";

    /// <summary>Whether two messages say the same: alike but for their line breaks and the white space they end with, as
    /// git keeps a commit's message.</summary>
    public static bool SameMessage(string one, string other) =>
        string.Equals(one.ReplaceLineEndings("\n").TrimEnd(), other.ReplaceLineEndings("\n").TrimEnd(), StringComparison.Ordinal);
}
