using Passenger.App.Testing;

namespace Passenger.UnitTests.Godot;

/// <summary>What GUT's test classes pass on: each class's tests found in the set its scripts are read with, beside the names
/// and paths that say it is a test class.</summary>
public sealed class GutBasesTests
{
    [Fact]
    public void A_class_has_the_tests_entered_for_it_in_the_order_of_their_names()
    {
        HashSet<string> bases = ["SpinTester", "res://spin.gd", GutBases.Entry("SpinTester", "test_turns"), GutBases.Entry("SpinTester", "test_adds"), GutBases.Entry("res://spin.gd", "test_adds")];
        Assert.Equal(["test_adds", "test_turns"], GutBases.TestsOf(bases, "SpinTester"));
        Assert.Equal(["test_adds"], GutBases.TestsOf(bases, "res://spin.gd"));
    }

    [Fact]
    public void A_class_whose_name_begins_anothers_has_none_of_its_tests()
    {
        HashSet<string> bases = [GutBases.Entry("SpinTesterMore", "test_adds")];
        Assert.Empty(GutBases.TestsOf(bases, "SpinTester"));
    }

    [Fact]
    public void A_class_known_to_have_no_tests_and_a_project_of_no_bases_answer_none()
    {
        Assert.Empty(GutBases.TestsOf(new HashSet<string> { "SpinTester" }, "SpinTester"));
        Assert.Empty(GutBases.TestsOf(null, "SpinTester"));
    }
}
