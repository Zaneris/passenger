using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A profiled frame's name split into its type and method as Go to Source looks them up by name: a constructor's
/// before its double dot, so it is looked for by its type, a generic type's without the arguments the runtime writes.</summary>
public sealed class FrameNameTests
{
    [Theory]
    [InlineData("Ns.Type..ctor(Int32)", "Ns.Type", ".ctor")]
    [InlineData("Ns.Outer+Inner..cctor()", "Ns.Outer+Inner", ".cctor")]
    [InlineData("Ns.Type.Run(String, Int32)", "Ns.Type", "Run")]
    [InlineData("Box`1[System.__Canon].Churn()", "Box`1", "Churn")]
    [InlineData("Box`1[System.Collections.Generic.List`1[System.Int32]]..ctor()", "Box`1", ".ctor")]
    [InlineData("Worker+<CrunchAsync>d__0.MoveNext()", "Worker+<CrunchAsync>d__0", "MoveNext")]
    public void A_frames_name_splits_into_its_type_and_method(string frame, string type, string method)
    {
        Assert.Equal(new FrameName(type, method), FrameName.Of(frame));
    }

    [Fact]
    public void A_constructor_is_said_to_be_one_and_a_method_named_ctor_is_not()
    {
        Assert.True(FrameName.Of("Ns.Type..ctor(Int32)")!.Value.IsConstructor);
        Assert.False(FrameName.Of("Ns.Type.ctor()")!.Value.IsConstructor);
    }

    [Theory]
    [InlineData("Main()")]
    [InlineData("[native]")]
    public void A_frame_of_no_type_has_no_split(string frame)
    {
        Assert.Null(FrameName.Of(frame));
    }
}
