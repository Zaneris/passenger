using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A profiled frame's name split into its type and method as Go to Source looks them up by name: a constructor's
/// before its double dot, so it is looked for by its type, a generic type's without the arguments the runtime writes.</summary>
public sealed class FrameNameTests
{
    [Fact]
    public void A_frames_name_splits_into_its_type_and_method_as_Go_to_Source_looks_them_up() => Cases.All(
        ("a frame's name splits into its type and method", () => Cases.Each(
        [
            ("Ns.Outer+Inner..cctor()", "Ns.Outer+Inner", ".cctor"),
            ("Ns.Type.Run(String, Int32)", "Ns.Type", "Run"),
            ("Box`1[System.Collections.Generic.List`1[System.Int32]]..ctor()", "Box`1", ".ctor"),
            ("Worker+<CrunchAsync>d__0.MoveNext()", "Worker+<CrunchAsync>d__0", "MoveNext"),
        ], row => Assert.Equal(new FrameName(row.Item2, row.Item3), FrameName.Of(row.Item1)))),
        ("a constructor is said to be one and a method named ctor is not", () =>
        {
            Assert.True(FrameName.Of("Ns.Type..ctor(Int32)")!.Value.IsConstructor);
            Assert.False(FrameName.Of("Ns.Type.ctor()")!.Value.IsConstructor);
        }),
        ("a frame of no type has no split", () => Assert.Null(FrameName.Of("[native]"))));
}
