using System.Diagnostics;
using System.Reflection;
using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>Which methods' frames the stack leaves out, read from metadata as the debugger reads a program's: those their
/// code hides from debuggers and stack traces alike, as the runtime marks its exception dispatch from .NET 11 on, and no
/// other, so reflection's [DebuggerHidden] calls, which stack traces show, stay between the frames they connect.</summary>
public class HiddenFramesTests
{
    [Theory]
    [InlineData(nameof(Methods.HiddenFromBoth), true)]
    [InlineData(nameof(Methods.HiddenFromDebuggers), false)]
    [InlineData(nameof(Methods.HiddenFromTraces), false)]
    [InlineData(nameof(Methods.Plain), false)]
    public void A_frame_is_left_out_only_where_its_method_is_hidden_from_debuggers_and_stack_traces(string method, bool hidden)
    {
        Assert.Equal(hidden, IsHiddenFrame(typeof(Methods).GetMethod(method)!));
    }

    [Fact]
    public void A_frame_of_a_type_hidden_from_stack_traces_is_left_out_where_its_method_is_hidden_from_debuggers()
    {
        Assert.True(IsHiddenFrame(typeof(HiddenType).GetMethod(nameof(HiddenType.HiddenFromDebuggers))!));
        Assert.False(IsHiddenFrame(typeof(HiddenType).GetMethod(nameof(HiddenType.Plain))!));
    }

    [Fact]
    public void A_reflection_call_hidden_from_debuggers_alone_is_a_frame_of_the_stack()
    {
        Assert.False(IsHiddenFrame(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!),
            "MethodBase.Invoke is [DebuggerHidden] alone, and its frame would go missing between a caller and the method it invoked");
    }

    private static bool IsHiddenFrame(MethodInfo method)
    {
        using ModuleMetadataReader reader = ModuleMetadataReader.TryLoad(method.Module.Assembly.Location)
            ?? throw new InvalidOperationException($"{method.Module.Name} could not be read");
        return reader.IsHiddenFrame(method.MetadataToken);
    }

    public static class Methods
    {
        [DebuggerHidden]
        [StackTraceHidden]
        public static void HiddenFromBoth()
        {
        }

        [DebuggerHidden]
        public static void HiddenFromDebuggers()
        {
        }

        [StackTraceHidden]
        public static void HiddenFromTraces()
        {
        }

        public static void Plain()
        {
        }
    }

    [StackTraceHidden]
    public static class HiddenType
    {
        [DebuggerHidden]
        public static void HiddenFromDebuggers()
        {
        }

        public static void Plain()
        {
        }
    }
}
