using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A program's last lines as its end is told: the exception .NET reports as it ends the program named whatever
/// number of frames follows it, its inner ones with it, and what the program last said otherwise.</summary>
public class LastLinesTests
{
    [Fact]
    public void A_programs_last_lines_name_the_exception_that_ended_it_with_its_inner_ones() => Cases.All(
        ("An exception that ended the program is named above however many frames its trace has", An_exception_that_ended_the_program_is_named_above_however_many_frames_its_trace_has),
        ("An exceptions inner ones are named with it and the end of an inner trace is one of its frames", An_exceptions_inner_ones_are_named_with_it_and_the_end_of_an_inner_trace_is_one_of_its_frames),
        ("A line after a trace is what the program last said", A_line_after_a_trace_is_what_the_program_last_said),
        ("A program that said nothing has no last line", A_program_that_said_nothing_has_no_last_line));

    private void An_exception_that_ended_the_program_is_named_above_however_many_frames_its_trace_has()
    {
        LastLines lines = new(5);
        lines.Heard("Initializing application in design mode");
        lines.Heard("Unhandled exception. System.InvalidOperationException: The calling thread cannot access this object");
        foreach (string frame in (string[])["Avalonia.Threading.Dispatcher.VerifyAccess()", "Avalonia.Rendering.UiThreadRenderTimer..ctor(Int32 fps)",
            "Avalonia.AppBuilder.SetupUnsafe()", "Avalonia.AppBuilder.Setup()", "Avalonia.AppBuilder.SetupWithoutStarting()",
            "Avalonia.Designer.HostApp.Program.Main(String[] args)"])
        {
            lines.Heard("   at " + frame);
        }

        Assert.Equal("Unhandled exception. System.InvalidOperationException: The calling thread cannot access this object", lines.Last);
        Assert.Equal(6, lines.Kept.Count);
        Assert.Equal("Unhandled exception. System.InvalidOperationException: The calling thread cannot access this object", lines.Kept[0]);
        Assert.Equal("   at Avalonia.Designer.HostApp.Program.Main(String[] args)", lines.Kept[^1]);
    }

    private void An_exceptions_inner_ones_are_named_with_it_and_the_end_of_an_inner_trace_is_one_of_its_frames()
    {
        LastLines lines = new(3);
        lines.Heard("Unhandled exception. System.TypeInitializationException: The type initializer for 'SkiaSharp.SkiaApi' threw an exception.");
        lines.Heard(" ---> System.DllNotFoundException: Unable to load shared library 'libSkiaSharp'");
        lines.Heard("   at SkiaSharp.SkiaApi..cctor()");
        lines.Heard("   --- End of inner exception stack trace ---");
        lines.Heard("   at Avalonia.Skia.SkiaPlatform.Initialize()");
        lines.Heard("   at Avalonia.AppBuilder.Setup()");

        Assert.Equal("Unhandled exception. System.TypeInitializationException: The type initializer for 'SkiaSharp.SkiaApi' threw an exception. "
            + "---> System.DllNotFoundException: Unable to load shared library 'libSkiaSharp'", lines.Last);
        Assert.Equal(5, lines.Kept.Count);
    }

    private void A_line_after_a_trace_is_what_the_program_last_said()
    {
        LastLines lines = new(5);
        lines.Heard("Unhandled exception. System.Exception: handled and logged");
        lines.Heard("   at Gallery.App.Initialize()");
        lines.Heard("the previewer's last words");
        lines.Heard("   ");

        Assert.Equal("the previewer's last words", lines.Last);
        Assert.Equal(["Unhandled exception. System.Exception: handled and logged", "   at Gallery.App.Initialize()", "the previewer's last words"], lines.Kept);
    }

    private void A_program_that_said_nothing_has_no_last_line()
    {
        LastLines lines = new(5);
        lines.Heard("");

        Assert.Null(lines.Last);
        Assert.Empty(lines.Kept);
    }
}
