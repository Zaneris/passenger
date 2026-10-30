using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>A [Flags] enum's value named by its members, as Variables shows it: each member and the value read as their
/// underlying type's bits, so a negative member takes part as its bits.</summary>
public class FlagsTests
{
    private static readonly (string Name, object? Literal)[] Access = [("None", 0), ("Read", 1), ("Write", 2), ("Run", 4), ("All", -1)];

    [Fact]
    public void A_combination_of_a_flags_enum_with_a_negative_member_is_named_by_its_members()
    {
        Assert.Equal("Read | Write", ManagedDebugger.FlagsOf(Access, "3"));
        Assert.Equal("Read | Write | Run", ManagedDebugger.FlagsOf(Access, "7"));
    }

    [Fact]
    public void A_value_its_members_leave_bits_of_is_shown_as_it_is()
    {
        Assert.Equal("9", ManagedDebugger.FlagsOf(Access, "9"));
        Assert.Equal("-2", ManagedDebugger.FlagsOf(Access, "-2"));
    }

    [Fact]
    public void A_negative_value_is_named_by_the_members_its_bits_hold()
    {
        (string Name, object? Literal)[] signed = [("Low", (sbyte)1), ("High", (sbyte)-128)];
        Assert.Equal("Low | High", ManagedDebugger.FlagsOf(signed, "-127"));
        (string Name, object? Literal)[] wide = [("Low", 1L), ("Top", long.MinValue)];
        Assert.Equal("Low | Top", ManagedDebugger.FlagsOf(wide, (long.MinValue + 1).ToString(System.Globalization.CultureInfo.CurrentCulture)));
    }
}
