using System.Diagnostics;
using System.Reflection;
using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>Which methods' frames the stack leaves out, read from metadata as the debugger reads a program's: those their
/// code hides from debuggers and stack traces alike, as the runtime marks its exception dispatch from .NET 11 on, and no
/// other, so reflection's [DebuggerHidden] calls, which stack traces show, stay between the frames they connect.</summary>
public class HiddenFramesTests
{
    /// <summary>The runtime's contract with debuggers from .NET 11 is read from this process's CoreLib (the unit tests run on
    /// .NET 11 or later): its exception dispatch and its call of the entry point are there, marked hidden.</summary>
    [Fact]
    public void A_frame_is_left_out_only_where_its_method_is_hidden_from_debuggers_and_stack_traces_alike() => Cases.All(
        ("a frame is left out only where its method, or its method and its type, are hidden from debuggers and stack traces", () =>
        {
            Assert.True(IsHiddenFrame(typeof(Methods).GetMethod(nameof(Methods.HiddenFromBoth))!));
            Assert.False(IsHiddenFrame(typeof(Methods).GetMethod(nameof(Methods.HiddenFromDebuggers))!));
            Assert.True(IsHiddenFrame(typeof(HiddenType).GetMethod(nameof(HiddenType.HiddenFromDebuggers))!));
            Assert.False(IsHiddenFrame(typeof(HiddenType).GetMethod(nameof(HiddenType.Plain))!));
        }),
        ("a reflection call hidden from debuggers alone is a frame of the stack", () =>
            Assert.False(IsHiddenFrame(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!),
                "MethodBase.Invoke is [DebuggerHidden] alone, and its frame would go missing between a caller and the method it invoked")),
        ("the runtime's exception dispatch and entry point call are frames left out", () => Cases.Each(
            [("System.Runtime.EH", "DispatchEx"), ("System.Environment", "CallEntryPoint")], row =>
        {
            (string type, string name) = row;
            Assert.True(Environment.Version.Major >= 11, $"The unit tests run on .NET {Environment.Version}, before the runtime marked these hidden");
            MethodInfo? method = typeof(object).Assembly.GetType(type)?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == name);
            Assert.True(method is not null, $".NET {Environment.Version}'s CoreLib has no {type}.{name}, so the frames a stop shows above or below the program's are no longer known");
            Assert.True(IsHiddenFrame(method), $"{type}.{name} is not hidden from debuggers and stack traces alike, so a stop under it would show it on top");
        })));

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
