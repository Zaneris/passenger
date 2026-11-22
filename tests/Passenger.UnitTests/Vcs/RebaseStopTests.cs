using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>Where an interactive rebase stopped, read from its done list's last step: an edit, a break or an exec stops it
/// with the tree handed over, while a pick stops only for conflicts.</summary>
public class RebaseStopTests
{
    [Fact]
    public void A_rebase_stops_at_an_edit_a_break_or_an_exec_and_at_a_pick_only_for_conflicts() => Cases.All(
        ("an edit a break or an exec by its word or letter is a stop of the list", An_edit_a_break_or_an_exec_by_its_word_or_letter_is_a_stop_of_the_list),
        ("a pick or nothing done stops only for conflicts", A_pick_or_nothing_done_stops_only_for_conflicts));

    private void An_edit_a_break_or_an_exec_by_its_word_or_letter_is_a_stop_of_the_list()
    {
        Assert.Equal(new RebaseStop(RebaseStep.Edit), RebaseStop.Of("pick 1a2b3c4d # one\nedit 5e6f7a8b9c # two\n"));
        Assert.Equal(new RebaseStop(RebaseStep.Edit), RebaseStop.Of("e 5e6f7a8b9c two"));
        Assert.Equal(new RebaseStop(RebaseStep.Break), RebaseStop.Of("edit 5e6f7a8b9c # two\nbreak\n"));
        Assert.Equal(new RebaseStop(RebaseStep.Break), RebaseStop.Of("b"));
        Assert.Equal(new RebaseStop(RebaseStep.Exec, "dotnet test --no-build"), RebaseStop.Of("pick 1a2b3c4d # one\nexec dotnet test --no-build\n"));
        Assert.Equal(new RebaseStop(RebaseStep.Exec, "make"), RebaseStop.Of("x  make \n\n"));
    }

    private void A_pick_or_nothing_done_stops_only_for_conflicts()
    {
        Assert.Null(RebaseStop.Of(null));
        Assert.Null(RebaseStop.Of(string.Empty));
        Assert.Null(RebaseStop.Of("edit 5e6f7a8b9c # two\npick 1a2b3c4d # three\n"));
        Assert.Null(RebaseStop.Of("fixup 1a2b3c4d # three\n# a comment\n"));
        Assert.Null(RebaseStop.Of("exec"));
    }

}
