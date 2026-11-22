using Passenger.App.Testing;

namespace Passenger.UnitTests.Godot;

/// <summary>What GUT's test classes pass on: each class's tests found in the set its scripts are read with, beside the names
/// and paths that say it is a test class.</summary>
public sealed class GutBasesTests
{
    /// <summary>A class has the GUT tests entered for it, once, in the order of their names.</summary>
    [Fact]
    public void A_classs_gut_tests_are_those_entered_for_it() => Cases.All(
        (nameof(A_class_has_the_tests_entered_for_it_in_the_order_of_their_names), A_class_has_the_tests_entered_for_it_in_the_order_of_their_names),
        (nameof(A_class_whose_name_begins_anothers_has_none_of_its_tests), A_class_whose_name_begins_anothers_has_none_of_its_tests));

    private void A_class_has_the_tests_entered_for_it_in_the_order_of_their_names()
    {
        HashSet<string> bases = ["SpinTester", "res://spin.gd", GutBases.Entry("SpinTester", "test_turns", "/spin.gd"), GutBases.Entry("SpinTester", "test_adds", "/base.gd"), GutBases.Entry("res://spin.gd", "test_adds", "/base.gd")];
        Assert.Equal([("test_adds", "/base.gd"), ("test_turns", "/spin.gd")], GutBases.TestsOf(bases, "SpinTester"));
        Assert.Equal([("test_adds", "/base.gd")], GutBases.TestsOf(bases, "res://spin.gd"));
    }

    private void A_class_whose_name_begins_anothers_has_none_of_its_tests()
    {
        HashSet<string> bases = [GutBases.Entry("SpinTesterMore", "test_adds", "/spin.gd")];
        Assert.Empty(GutBases.TestsOf(bases, "SpinTester"));
    }

}
