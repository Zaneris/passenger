using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>How Update Project brings the upstream's commits in, read from what `git config -z --get-regexp` writes: the
/// branch's own rebase setting before pull.rebase, pull.ff for a merge, and a merge, said as not configured, where neither
/// rebase setting is there.</summary>
public class UpdatePlanTests
{
    [Fact]
    public void Nothing_set_merges_and_says_so()
    {
        Assert.Equal(new UpdatePlan(Rebase: false, RebaseMerges: false, FastForward: null, Configured: false), UpdatePlan.Of(string.Empty, "main"));
    }

    [Fact]
    public void Pull_rebase_rebases_and_the_branchs_own_setting_comes_first()
    {
        UpdatePlan rebases = new(Rebase: true, RebaseMerges: false, FastForward: null, Configured: true);
        Assert.Equal(rebases, UpdatePlan.Of("pull.rebase\ntrue\0", "main"));
        Assert.Equal(rebases, UpdatePlan.Of("pull.rebase\0", "main"));
        Assert.Equal(rebases, UpdatePlan.Of("pull.rebase\ninteractive\0", "main"));
        Assert.Equal(rebases with { Rebase = false }, UpdatePlan.Of("pull.rebase\nYes\0branch.main.rebase\nfalse\0", "main"));
        Assert.Equal(rebases with { RebaseMerges = true }, UpdatePlan.Of("pull.rebase\nfalse\0branch.Feature.rebase\nmerges\0", "Feature"));
        Assert.Equal(rebases with { Rebase = false, Configured = false }, UpdatePlan.Of("branch.Feature.rebase\ntrue\0", "feature"));
    }

    [Fact]
    public void Pull_ff_says_how_a_merge_may_fast_forward()
    {
        Assert.Equal("--ff-only", UpdatePlan.Of("pull.ff\nonly\0", "main").FastForward);
        Assert.Equal("--no-ff", UpdatePlan.Of("pull.ff\nfalse\0", "main").FastForward);
        Assert.Equal("--ff", UpdatePlan.Of("pull.ff\ntrue\0", "main").FastForward);
        Assert.Null(UpdatePlan.Of("pull.ff\nsometimes\0", "main").FastForward);
    }
}
