using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>An assignment's target C# keeps readonly, or reaches through a nullable's Value, in the forms Set Value tries in
/// turn, each writing where the target refers to.</summary>
public class WritableTargetsTests
{
    [Fact]
    public void A_target_CSharp_keeps_readonly_or_reaches_through_a_nullables_value_is_written_where_it_refers_to() => Cases.All(
        ("a readonly target is written through each variable it is reached by the first first", () =>
        {
            Assert.Equal([$"{AsRef}(in value)"], WritableTargets.Of("value", NoneNullable));
            Assert.Equal([$"{AsRef}(in iv).X", $"{AsRef}(in iv.X)"], WritableTargets.Of("iv.X", NoneNullable));
            Assert.Equal([$"{AsRef}(in span)[0].X", $"{AsRef}(in span[0]).X", $"{AsRef}(in span[0].X)"], WritableTargets.Of(" span[0].X ", NoneNullable));
            Assert.Empty(WritableTargets.Of("iv.", NoneNullable));
        }),
        ("a target reached through a nullable's value is written through the value the nullable holds", () =>
        {
            const string Held = "System.Nullable.GetValueRefOrDefaultRef";
            Assert.Equal([$"{AsRef}(in {Held}(in place)).X", $"{AsRef}(in {Held}(in place).X)"], WritableTargets.Of("place.Value.X", receiver => receiver == "place"));
            Assert.Equal([$"{AsRef}(in {Held}(in this.place)).Inner.X", $"{AsRef}(in {Held}(in this.place).Inner).X", $"{AsRef}(in {Held}(in this.place).Inner.X)"],
                WritableTargets.Of("this.place.Value.Inner.X", receiver => receiver == "this.place"));
            Assert.Equal([$"{AsRef}(in place).Value", $"{AsRef}(in place.Value)"], WritableTargets.Of("place.Value", _ => true));
        }));

    private const string AsRef = "System.Runtime.CompilerServices.Unsafe.AsRef";

    private static bool NoneNullable(string receiver) => false;
}
