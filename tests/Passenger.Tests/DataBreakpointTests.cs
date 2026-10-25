using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Data breakpoints, from the window: Set Data Breakpoint on a field in Variables stops the program at the
/// line that changes it, the breakpoint listed in the Breakpoints window with a line breakpoint's options. The .NET
/// runtime watches memory for a debugger on Windows x64 alone, so there the program is debugged, and elsewhere the
/// action is greyed saying so. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class DataBreakpointTests : IClassFixture<RestoredFolders>, IDisposable
{
    /// <summary>Program.cs's line (from zero) where the program waits to be watched, and the one that writes the field.</summary>
    private const int Ready = 6;
    private const int Writes = 27;

    private const string OnlyOnWindowsX64 = "Data breakpoints are only available on Windows x64, where the .NET runtime watches memory for a debugger";

    private readonly string folder;
    private readonly string program;

    public DataBreakpointTests(RestoredFolders folders)
    {
        folder = folders.Take("data-", made =>
        {
            File.WriteAllText(Path.Combine(made, "Sum.csproj"), Project);
            File.WriteAllText(Path.Combine(made, "Program.cs"), Source);
            return made;
        });
        program = Path.Combine(PathNames.Canonical(folder), "Program.cs");
    }

    private const string Project =
        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
        + "    <ImplicitUsings>enable</ImplicitUsings>\n    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>\n  </PropertyGroup>\n</Project>\n";

    // Garbage before the neighbour, so the compacting collection moves it; the counter, watched, is held where it is. A
    // statement follows the write: the runtime stops only where the thread can be, which a method's epilog is not.
    private const string Source =
        "using System.Runtime.CompilerServices;\n"
        + "\n"
        + "List<byte[]> before = Garbage();\n"
        + "Neighbour neighbour = new();\n"
        + "List<byte[]> after = Garbage();\n"
        + "Counter counter = new();\n"
        + "Console.WriteLine(\"ready\");\n"
        + "nint was = Place.Of(neighbour);\n"
        + "before.Clear();\n"
        + "after.Clear();\n"
        + "GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);\n"
        + "Console.WriteLine($\"collected; the neighbour moved: {Place.Of(neighbour) != was}\");\n"
        + "counter.Set(0);\n"
        + "counter.Set(2);\n"
        + "counter.Set(3);\n"
        + "Console.WriteLine($\"count {counter.Count}\");\n"
        + "\n"
        + "static List<byte[]> Garbage() => [.. Enumerable.Range(0, 4000).Select(_ => new byte[48])];\n"
        + "\n"
        + "sealed class Counter\n"
        + "{\n"
        + "    private int count;\n"
        + "\n"
        + "    public int Count => count;\n"
        + "\n"
        + "    public void Set(int value)\n"
        + "    {\n"
        + "        count = value;\n"
        + "        Console.WriteLine($\"set {value}\");\n"
        + "    }\n"
        + "}\n"
        + "\n"
        + "sealed class Neighbour\n"
        + "{\n"
        + "    public long Payload = 7;\n"
        + "}\n"
        + "\n"
        + "static unsafe class Place\n"
        + "{\n"
        + "    public static nint Of(object value) => *(nint*)Unsafe.AsPointer(ref value);\n"
        + "}\n";

    public void Dispose() => TestFolders.Delete(folder);

    private static bool Watches => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    [AvaloniaFact]
    public void Set_data_breakpoint_on_a_field_stops_at_the_line_that_changes_it_after_a_compacting_collection_and_not_where_it_is_written_unchanged()
    {
        Assert.SkipUnless(Watches, OnlyOnWindowsX64);
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Passenger.App.ToolWindows.DebugModel view = PausedAtReady(window);
        VariableNode count = Member(view, "counter", "count");
        Assert.Equal(DataWatchOffer.Set, Offered(window, view, count).Item.Header as string);
        Ui.ChooseFromMenu(window.DebugWindow.VariablesList, DataWatchOffer.Set);
        DataBreakpoint watched = Assert.Single(window.Debugger.Breakpoints.Data);
        Assert.StartsWith("count on counter", watched.Location, StringComparison.Ordinal);
        Ui.WaitFor(() => watched.Status == BreakpointStatus.Bound, $"the debugger to take the data breakpoint, not to say {watched.Message}");
        Assert.Equal(DataWatchOffer.Remove, Offered(window, view, Member(view, "counter", "count")).Item.Header as string);

        // Set(0) writes the value the field has, which does not stop it; Set(2) changes it, after the collection.
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => window.Debugger.Session?.StopReason == "data breakpoint" || window.Debugger.Session?.State == DebugState.Ended, "the stop at the write");
        Assert.True(window.Debugger.Session?.StopReason == "data breakpoint", $"The program never stopped at the write to count: {window.Status}");
        string console = window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
        Assert.True(console.Contains("collected; the neighbour moved: True", StringComparison.Ordinal),
            $"The collection before the write moved nothing, so it did not test that the watch holds through one: {console}");
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(Writes, LineMarkKind.ExecutionPoint)), "the execution point at the line that wrote the field");
        Ui.WaitFor(() => Value(window, "value") == "2", "the stop at Set(2), past Set(0)'s write of the same value");
        Assert.Contains("Data Breakpoint Hit: count on counter", window.Status, StringComparison.Ordinal);
        Assert.Contains("Previous Value: 0, New Value: 2", window.Status, StringComparison.Ordinal);

        Ui.Run(window, AppActions.StepOver);
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(Writes + 1, LineMarkKind.ExecutionPoint)), "the step to the line after the write");

        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => Value(window, "value") == "3", "the stop at the next write that changes count");
        Assert.Contains("Previous Value: 2, New Value: 3", window.Status, StringComparison.Ordinal);
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        // The object was that session's: the breakpoint goes with it.
        Assert.Empty(window.Debugger.Breakpoints.Data);
        window.Close();
    }

    [AvaloniaFact]
    public void The_breakpoints_window_lists_a_data_breakpoint_and_removing_it_there_lets_the_program_run_past_the_writes()
    {
        Assert.SkipUnless(Watches, OnlyOnWindowsX64);
        MainWindow window = Open();
        Passenger.App.ToolWindows.DebugModel view = PausedAtReady(window);
        window.DebugWindow.VariablesList.SelectedItem = Member(view, "counter", "count");
        Ui.ChooseFromMenu(window.DebugWindow.VariablesList, DataWatchOffer.Set);
        DataBreakpoint watched = Assert.Single(window.Debugger.Breakpoints.Data);

        Ui.Run(window, AppActions.ViewBreakpoints);
        BreakpointsWindow breakpoints = Assert.IsType<BreakpointsWindow>(window.Popup);
        Assert.Equal(["Program.cs:7", watched.Location, ExceptionBreakpoint.AnyException, BindingErrorBreakpoint.AnyBindingError], breakpoints.Listed.Select(b => b.Location));
        Assert.Contains(breakpoints.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Data Breakpoints");
        Ui.Click(Row(breakpoints, watched.Location));
        Assert.Same(watched, breakpoints.Chosen);
        Ui.Press(window, "Delete");
        Assert.Equal(["Program.cs:7", ExceptionBreakpoint.AnyException, BindingErrorBreakpoint.AnyBindingError], breakpoints.Listed.Select(b => b.Location));
        Ui.Press(window, "Escape");
        Assert.Empty(window.Debugger.Breakpoints.Data);

        // Paused until the debugger answers the resume, which no background work waits for: only a state after it counts.
        DebugSession session = window.Debugger.Session!;
        bool resumed = false;
        session.StateChanged += (_, _) => resumed |= session.State != DebugState.Paused;
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => resumed && session.State is DebugState.Ended or DebugState.Paused, "the program to run to its end");
        Assert.True(session.State == DebugState.Ended, $"Removed, the data breakpoint still stopped the program: {window.Status}");
        Assert.Contains("count 3", window.BottomPanel.Debug.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_condition_typed_for_a_data_breakpoint_in_the_breakpoints_window_decides_which_write_stops()
    {
        Assert.SkipUnless(Watches, OnlyOnWindowsX64);
        MainWindow window = Open();
        Passenger.App.ToolWindows.DebugModel view = PausedAtReady(window);
        window.DebugWindow.VariablesList.SelectedItem = Member(view, "counter", "count");
        Ui.ChooseFromMenu(window.DebugWindow.VariablesList, DataWatchOffer.Set);
        DataBreakpoint watched = Assert.Single(window.Debugger.Breakpoints.Data);

        Ui.Run(window, AppActions.ViewBreakpoints);
        BreakpointsWindow breakpoints = Assert.IsType<BreakpointsWindow>(window.Popup);
        Ui.Click(Row(breakpoints, watched.Location));
        Ui.Click(breakpoints.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText?.StartsWith("A C# condition", StringComparison.Ordinal) == true));
        window.KeyTextInput("value == 3");
        Ui.Click(breakpoints.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Done" && b.IsEffectivelyVisible));
        Assert.Equal("value == 3", watched.Condition);

        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => window.Debugger.Session?.StopReason == "data breakpoint" || window.Debugger.Session?.State == DebugState.Ended, "the stop the condition allows");
        Ui.WaitFor(() => Value(window, "value") == "3", "the stop at Set(3), the write of 2 passed by the condition");
        Assert.Contains("Previous Value: 2, New Value: 3", window.Status, StringComparison.Ordinal);
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    [AvaloniaFact]
    public void Set_data_breakpoint_is_greyed_saying_why_on_a_local_on_a_property_with_no_backing_field_and_wherever_the_runtime_cannot_watch()
    {
        MainWindow window = Open();
        Passenger.App.ToolWindows.DebugModel view = PausedAtReady(window);
        string? unavailable = window.Debugger.Session!.DataBreakpointsUnavailable;
        Assert.True(Watches == (unavailable is null), $"Windows x64 alone watches memory for a debugger; this debugger says: {unavailable ?? "it can"}");

        (MenuItem field, string? fieldWhy) = Offered(window, view, Member(view, "counter", "count"));
        (MenuItem local, string? localWhy) = Offered(window, view, view.Tree.Rows.First(r => r.Depth == 0 && r.Name == "counter"));
        (MenuItem property, string? propertyWhy) = Offered(window, view, Member(view, "counter", "Count"));
        Assert.Equal(DataWatchOffer.Set, field.Header as string);
        if (unavailable is not null)
        {
            Assert.False(field.IsEnabled, "Set Data Breakpoint was offered where the runtime cannot watch memory");
            Assert.Equal(unavailable, fieldWhy);
            Assert.Contains("only available on Windows x64", fieldWhy, StringComparison.Ordinal);
            Assert.False(local.IsEnabled);
            Assert.Equal(unavailable, localWhy);
        }
        else
        {
            Assert.True(field.IsEnabled, $"Set Data Breakpoint was greyed on a field: {fieldWhy}");
            Assert.False(local.IsEnabled, "Set Data Breakpoint was offered on a local variable");
            Assert.Equal(DebugController.NotAMember, localWhy);
            Assert.False(property.IsEnabled, "Set Data Breakpoint was offered on a property whose getter is the program's own");
            Assert.Equal("The breakpoint cannot be set. The property doesn't have a backing field.", propertyWhy);
        }
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    /// <summary>The program debugged from its start to the line where it waits to be watched, its Debug window then.</summary>
    private Passenger.App.ToolWindows.DebugModel PausedAtReady(MainWindow window)
    {
        window.EditorArea.CodeEditor.GoTo(new TextPosition(Ready, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.Run(window, AppActions.Debug);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Value(window, "counter") is not null, "the stop before the program is watched");
        return window.BottomPanel.Debug;
    }

    /// <summary>A member of a top-level row of Variables, the row opened to show it.</summary>
    private static VariableNode Member(Passenger.App.ToolWindows.DebugModel view, string row, string name)
    {
        VariableNode owner = view.Tree.Rows.First(r => r.Depth == 0 && r.Name == row);
        if (!owner.IsExpanded)
        {
            _ = view.Tree.Expand(owner);
        }
        Ui.WaitFor(() => owner.Children?.Any(c => c.Name == name) == true, $"{row}'s {name} in Variables");
        return owner.Children!.First(c => c.Name == name);
    }

    /// <summary>The data breakpoint item of a row's menu as it opens on the row, once the debugger has answered, and why
    /// it is greyed when it is.</summary>
    private static (MenuItem Item, string? WhyNot) Offered(MainWindow window, Passenger.App.ToolWindows.DebugModel view, VariableNode node)
    {
        window.DebugWindow.VariablesList.SelectedItem = node;
        ContextMenu menu = window.DebugWindow.VariablesList.ContextMenu ?? throw new InvalidOperationException("The row has no menu");
        window.DebugWindow.VariablesList.RaiseEvent(new ContextRequestedEventArgs());
        Ui.Settle();
        MenuItem item = menu.Items.OfType<MenuItem>().Last();
        menu.Close();
        Ui.Settle();
        return (item, ToolTip.GetTip(item) as string);
    }

    private static string? Value(MainWindow window, string name) =>
        window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name)?.Value;

    private static ListBoxItem Row(BreakpointsWindow breakpoints, string location) =>
        breakpoints.GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.Tag as Breakpoint)?.Location == location);

    /// <summary>A window on the program with its file open, with no C# language server, which debugging asks nothing of.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(program);
        Ui.Settle();
        return window;
    }
}
