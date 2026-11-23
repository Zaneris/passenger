using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>An interactive rebase as planned: the history it makes, as the Interactive Rebase dialog shows it before it
/// runs, git's list of steps that makes it, and why a plan git could not run is refused.</summary>
public class RebasePlanTests
{
    private static readonly RebasePlan Four = RebasePlan.Of("base0000", [Commit("a"), Commit("b"), Commit("c"), Commit("d")]);

    [Fact]
    public void A_plan_makes_the_history_its_preview_shows_by_steps_git_runs_without_asking() => Cases.All(
        ("commits before the first change are kept and a moved one is out of the order the rest keep", Commits_before_the_first_change_are_kept_and_a_moved_one_is_out_of_the_order_the_rest_keep),
        ("squashed and fixed up commits fold into the commit under them joining only a squashs message", Squashed_and_fixed_up_commits_fold_into_the_commit_under_them_joining_only_a_squashs_message),
        ("a commit under another message is picked as its twin after a drop and folds come by fixup", A_commit_under_another_message_is_picked_as_its_twin_after_a_drop_and_folds_come_by_fixup),
        ("a fold with nothing under it an empty message and a plan changing nothing are refused", A_fold_with_nothing_under_it_an_empty_message_and_a_plan_changing_nothing_are_refused));

    private void Commits_before_the_first_change_are_kept_and_a_moved_one_is_out_of_the_order_the_rest_keep()
    {
        // d dragged down under c: a and b stand where they stood, untouched.
        RebasePlan plan = Four with { Commits = [Four.Commits[0], Four.Commits[1], Four.Commits[3], Four.Commits[2]] };
        IReadOnlyList<RebasedCommit> made = plan.Outcome();
        Assert.Equal(["a", "b", "d", "c"], made.Select(c => c.Subject));
        Assert.Equal([true, true, false, false], made.Select(c => c.Kept));
        Assert.Equal([false, false, true, false], made.Select(c => c.Moved));
        Assert.All(made, c => Assert.False(c.Reworded));
        Assert.Null(plan.WhyNot);
    }

    private void Squashed_and_fixed_up_commits_fold_into_the_commit_under_them_joining_only_a_squashs_message()
    {
        RebasePlan plan = Four with { Commits = [Four.Commits[0], With(Four.Commits[1], RebaseCommand.Squash), With(Four.Commits[2], RebaseCommand.Drop), With(Four.Commits[3], RebaseCommand.Fixup)] };
        RebasedCommit made = Assert.Single(plan.Outcome());
        Assert.Equal(["a", "b", "d"], made.From.Select(c => c.Commit.Subject));
        Assert.Equal("a\n\nbody of a\n\nb\n\nbody of b", made.Message);
        Assert.True(made.Reworded);
        Assert.False(made.Kept);
        Assert.Equal(["c"], plan.Dropped.Select(c => c.Commit.Subject));
        Assert.Equal(0, plan.HeadOf(3));
        Assert.Equal(-1, plan.HeadOf(2));
    }

    private void A_commit_under_another_message_is_picked_as_its_twin_after_a_drop_and_folds_come_by_fixup()
    {
        RebasePlan plan = Four with
        {
            Commits = [With(Four.Commits[0], RebaseCommand.Reword) with { Written = "A anew" }, With(Four.Commits[1], RebaseCommand.Squash), With(Four.Commits[2], RebaseCommand.Edit), With(Four.Commits[3], RebaseCommand.Drop)],
        };
        Assert.Equal([("a", "A anew")], plan.Rewritten.Select(r => (r.Head.Commit.Subject, r.Message)));
        Assert.Equal("drop a a\npick twin a\nfixup b b\nedit c c\ndrop d d\n", plan.Todo(new Dictionary<string, string> { ["a"] = "twin" }));
        Assert.Equal([("A anew", false, false), ("c", true, false)], plan.Outcome().Select(c => (c.Subject, c.Stops, c.Kept)));
    }

    private void A_fold_with_nothing_under_it_an_empty_message_and_a_plan_changing_nothing_are_refused()
    {
        Assert.StartsWith("Nothing changes yet", Four.WhyNot);
        Assert.StartsWith("Nothing changes yet", (Four with { Commits = [With(Four.Commits[0], RebaseCommand.Reword), .. Four.Commits.Skip(1)] }).WhyNot);
        Assert.Contains("cannot be squashed", (Four with { Commits = [With(Four.Commits[0], RebaseCommand.Drop), With(Four.Commits[1], RebaseCommand.Squash), .. Four.Commits.Skip(2)] }).WhyNot);
        Assert.Contains("needs a message", (Four with { Commits = [Four.Commits[0] with { Written = " \n" }, .. Four.Commits.Skip(1)] }).WhyNot);
    }

    private static (CommitInfo, string) Commit(string name) =>
        (new CommitInfo(name, [], "Author", "author@example.com", DateTimeOffset.UnixEpoch, [], name), $"{name}\n\nbody of {name}\n");

    private static PlannedCommit With(PlannedCommit commit, RebaseCommand command) => commit with { Command = command };
}
