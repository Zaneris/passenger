using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Debugging from the window, as a person does it: F9 on a line, Alt+F5, the stop shown where
/// it is with its values, then stepping, resuming and stopping with the keys Rider's keymap gives
/// them. The tests on Sum as written share it (<see cref="Sum"/>): restored once, and the build the last test left, so each
/// one's Debug finds nothing to compile; those that end a program that would run for ever share <see cref="Sleeper"/>
/// likewise; a test that changes the program takes a folder of its own (<see cref="Fresh"/>). A window starts no C#
/// language server unless the test's edits go into the program debugged (Hot Reload works them out in the server), the
/// debugger and the file's own parse answering the rest. Debugged, and the console's answers timed, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class DebuggerTests : IClassFixture<DebuggerTests.Sum>, IClassFixture<DebuggerTests.Sleeper>, IClassFixture<RestoredFolders>, IDisposable
{
    private readonly Sum sum;
    private readonly Sleeper sleeper;
    private readonly RestoredFolders folders;

    /// <summary>The test's own folder, once it has taken one.</summary>
    private string? fresh;

    public DebuggerTests(Sum sum, Sleeper sleeper, RestoredFolders folders)
    {
        this.sum = sum;
        this.sleeper = sleeper;
        this.folders = folders;
        sum.Reset();
        sleeper.Reset();
    }

    public void Dispose()
    {
        if (fresh is not null)
        {
            TestFolders.Delete(fresh);
        }
    }

    /// <summary>The folder the test debugs: its own once it has taken one, else the class's Sum.</summary>
    private string Folder => fresh ?? sum.Folder;

    /// <summary>The folder's Program.cs, as the breakpoints name it.</summary>
    private string Program => Path.Combine(PathNames.Canonical(Folder), "Program.cs");

    /// <summary>A folder of the test's own, Sum as written and restored, for a test that changes the program or leaves a file
    /// of its own there: taken the first time it is asked for, and removed as the test ends.</summary>
    private string Fresh() => fresh ??= folders.Take("debug-", made =>
    {
        Sum.WriteTo(made);
        return made;
    });

    /// <summary>F9 and Debug stop the program at the line with its locals, in a Debug window that shares a side pane, its
    /// next hit on Resume and the step after it; the menus of Variables and Frames copy a value, watch it and copy the stack.</summary>
    [AvaloniaFact]
    public void A_breakpoint_set_with_F9_stops_the_program_there_with_its_locals_and_the_variables_and_frames_menus_copy_them()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Assert.Contains(new LineMark(3, LineMarkKind.Breakpoint), editor.LineMarks);

        StartDebugging(window);
        WaitDebugging(window, () => window.Debugger.Session?.State == DebugState.Paused, "the program to stop at the breakpoint");
        Ui.WaitFor(() => Value(window, "i") == "1", "the locals at the first stop");
        Assert.Equal("0", Value(window, "total"));
        Assert.Contains(new LineMark(3, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Contains(new LineMark(3, LineMarkKind.BreakpointBound), editor.LineMarks);
        DapStackFrame top = window.BottomPanel.Debug.Frames[0];
        Assert.Contains("<Main>$", top.Name, StringComparison.Ordinal);
        Assert.Equal(4, top.Line);

        Passenger.App.Docking.DockHost dock = window.DockHost;
        dock.Move(dock[ToolWindowIds.Debug], Passenger.App.Docking.ToolAnchor.RightTop);
        Ui.Settle();
        Passenger.App.ToolWindows.DebugView debug = window.DebugWindow;
        Point origin = debug.TranslatePoint(default, window)!.Value;
        ListBox[] lists = [.. debug.GetVisualDescendants().OfType<ListBox>().Where(l => l.IsEffectivelyVisible)];
        Assert.Equal(2, lists.Length);
        foreach (ListBox list in lists)
        {
            Point at = list.TranslatePoint(default, window)!.Value;
            Assert.True(list.Bounds.Width >= debug.Bounds.Width / 4 && at.X + list.Bounds.Width <= origin.X + debug.Bounds.Width + 0.5,
                $"A list of the {debug.Bounds.Width:0.#} px Debug window is {list.Bounds.Width:0.#} px wide, ending at {at.X + list.Bounds.Width:0.#} where the window ends at {origin.X + debug.Bounds.Width:0.#}");
        }
        dock.Move(dock[ToolWindowIds.Debug], Passenger.App.Docking.ToolAnchor.BottomLeft);
        Ui.Settle();
        Assert.Contains(new LineAnnotation(0, "total: 0", LineAnnotationKind.InlineValue), editor.LineAnnotations);
        Assert.Contains(new LineAnnotation(1, "i: 1", LineAnnotationKind.InlineValue), editor.LineAnnotations);
        Assert.Contains(new LineAnnotation(3, "total: 0", LineAnnotationKind.InlineValue), editor.LineAnnotations);
        Assert.DoesNotContain(editor.LineAnnotations, a => a.Line > 3);

        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => Value(window, "i") == "2", "the second stop");
        Assert.Equal("1", Value(window, "total"));
        Assert.Contains(new LineAnnotation(1, "i: 2", LineAnnotationKind.InlineValue), editor.LineAnnotations);

        Ui.Run(window, AppActions.StepOver);
        Ui.WaitFor(() => editor.LineMarks.Any(m => m.Kind == LineMarkKind.ExecutionPoint && m.Line != 3), "the step");
        Ui.WaitFor(() => Value(window, "total") == "3", "the values after the step");
        Assert.True(window.Debugger.LastStopTimes is { Asked: "step over", ToStop: { } stepped, Frames: var framed, Locals: var listed }
            && stepped + listed < TimeSpan.FromSeconds(1).Scaled() && framed <= listed,
            $"The step's stop took too long to show, or was not timed: {window.Debugger.LastStopTimes}");
        Assert.Contains("Debugging Sum: step over stopped", Ui.IdeLog(), StringComparison.Ordinal);

        Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
        window.DebugWindow.VariablesList.SelectedItem = view.Tree.Rows.First(r => r.Depth == 0 && r.Name == "i");
        Ui.ChooseFromMenu(window.DebugWindow.VariablesList, "Copy Value");
        Ui.WaitFor(() => Ui.Clipboard(window) == "2", "the value on the clipboard");
        Ui.ChooseFromMenu(window.DebugWindow.VariablesList, "Add to Watches");
        Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Role == VariableRole.Watch && r.Name == "i" && r.Value == "2"), "the watch");
        ListBox frames = window.DebugWindow.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, view.Frames));
        Ui.ChooseFromMenu(frames, "Copy Stack");
        Ui.WaitFor(() => Ui.Clipboard(window)?.Contains("<Main>$", StringComparison.Ordinal) == true, "the stack on the clipboard");
        Assert.Contains($"Program.cs:{view.Frames[0].Line}", Ui.Clipboard(window), StringComparison.Ordinal);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        Assert.DoesNotContain(editor.LineMarks, m => m.Kind == LineMarkKind.ExecutionPoint);
        Assert.Contains(new LineMark(3, LineMarkKind.Breakpoint), editor.LineMarks);
        Assert.Empty(editor.LineAnnotations);
        window.Close();
    }

    /// <summary>The Debug window floated off into a window of its own still answers the IDE's keys pressed there: Step
    /// Over with its Variables clicked steps the program, Find Action opens over it, its evaluate box keeps what is
    /// typed and deleted there, Enter evaluating it, and Stop ends the session.</summary>
    [AvaloniaFact]
    public void Step_Over_pressed_in_the_Debug_window_floated_off_steps_the_program_and_its_evaluate_box_keeps_its_keys()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => window.Debugger.Session?.State == DebugState.Paused, "the program to stop at the breakpoint");
        Ui.WaitFor(() => Value(window, "i") == "1", "the locals at the first stop");

        Passenger.App.Docking.DockHost dock = window.DockHost;
        dock.SetMode(dock[ToolWindowIds.Debug], Passenger.App.Docking.ToolMode.Floating);
        Ui.Settle();
        Window floating = Assert.Single(window.OwnedWindows);
        Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
        Assert.Contains(window.DebugWindow, floating.GetVisualDescendants());
        ListBox variables = window.DebugWindow.VariablesList;
        Point row = variables.TranslatePoint(new Point(variables.Bounds.Width / 2, 12), floating)!.Value;
        floating.MouseDown(row, MouseButton.Left);
        floating.MouseUp(row, MouseButton.Left);
        Ui.Settle();
        Assert.True(variables.IsKeyboardFocusWithin, "A click on the floating Debug window's Variables left the keys elsewhere");

        Ui.Run(floating, AppActions.FindAction);
        Control popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
        Assert.True(floating.GetVisualDescendants().Contains(popup), "Find Action, pressed in the floating Debug window, opened over the main window");
        Ui.Press(floating, "ESCAPE");
        Assert.False(window.IsPopupOpen);
        Assert.True(variables.IsKeyboardFocusWithin, "Find Action, closed, left the floating Debug window's keys away from its Variables");

        Ui.Run(floating, AppActions.StepOver);
        WaitDebugging(window, () => editor.LineMarks.Any(m => m.Kind == LineMarkKind.ExecutionPoint && m.Line != 3),
            "Step Over, pressed in the floating Debug window, to step the program");
        Ui.WaitFor(() => Value(window, "total") == "1", "the values after the step");

        Ui.Run(floating, AppActions.Evaluate);
        TextBox evaluate = window.DebugWindow.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsKeyboardFocusWithin);
        floating.KeyTextInput("total + 100");
        Ui.Press(floating, "BACK_SPACE");
        Assert.Equal("total + 10", evaluate.Text);
        Ui.Press(floating, "ENTER");
        Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Name == "total + 10" && r.Value == "11"), "the expression typed in the floating Debug window evaluated");

        Ui.Run(floating, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "Stop, pressed in the floating Debug window, to end the session");
        window.Close();
    }

    [AvaloniaFact]
    public void Dragging_the_execution_arrow_past_the_loop_goes_on_from_there_and_skips_it()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => Value(window, "i") == "1", "the stop in the loop");

        // From the arrow beside line 4 to line 6, the Console.WriteLine after the loop.
        Point from = Arrow(window, editor, 3);
        Point to = Arrow(window, editor, 5);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(5, LineMarkKind.ExecutionPoint)), "the arrow to stand at the line after the loop");
        Assert.Contains(new LineMark(3, LineMarkKind.BreakpointBound), editor.LineMarks);

        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => window.BottomPanel.Debug.Console.Document.Snapshot.ToString().Contains("total 0", StringComparison.Ordinal), "the loop skipped");
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the program's end");
        window.Close();
    }

    [AvaloniaFact]
    public void A_breakpoint_keeps_to_its_code_as_lines_are_added_and_removed_above_it()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);

        editor.GoTo(new TextPosition(0, 0));
        editor.Focus();
        Ui.Press(window, "Enter");
        Ui.Press(window, "Enter");
        Assert.Equal(5, Assert.Single(window.Debugger.Breakpoints.In(Program)).Line);
        Assert.Contains(new LineMark(5, LineMarkKind.Breakpoint), editor.LineMarks);

        Ui.Press(window, "Back");
        Assert.Equal(4, Assert.Single(window.Debugger.Breakpoints.In(Program)).Line);

        editor.GoTo(new TextPosition(4, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Assert.Empty(window.Debugger.Breakpoints.In(Program));
        Assert.Empty(editor.LineMarks);
        window.Close();
    }

    [AvaloniaFact]
    public void A_line_typed_above_a_bound_breakpoint_while_paused_leaves_it_stopping_at_its_statement()
    {
        MainWindow window = Open(hotReload: true);
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => Value(window, "i") == "1", "the first stop");

        editor.GoTo(new TextPosition(0, 0));
        editor.Focus();
        Ui.Press(window, "Enter");
        Assert.Contains(new LineMark(4, LineMarkKind.BreakpointBound), editor.LineMarks);
        Assert.Contains(new LineMark(4, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Contains(new LineAnnotation(2, "i: 1", LineAnnotationKind.InlineValue), editor.LineAnnotations);

        editor.GoTo(new TextPosition(0, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(0, LineMarkKind.BreakpointInvalid)), "the new line's breakpoint refused");
        Assert.Contains("written after the program being debugged was built", window.Debugger.Breakpoints.In(Program)[0].Message, StringComparison.Ordinal);

        // Hot Reload applied the moved lines as it went on, so the debugger names the line as it now is.
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => editor.LineMarks.Any(m => m.Kind == LineMarkKind.ExecutionPoint), "the next stop");
        Ui.WaitFor(() => Value(window, "i") == "2", "the loop's next turn at the breakpoint's statement, not the line after it");
        Assert.Equal(5, window.BottomPanel.Debug.Frames[0].Line);
        Assert.Contains(new LineMark(4, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Contains(new LineMark(4, LineMarkKind.BreakpointBound), editor.LineMarks);
        Assert.Contains(new LineAnnotation(2, "i: 2", LineAnnotationKind.InlineValue), editor.LineAnnotations);
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    /// <summary>A key in a file of the program being debugged carries the build's lines through it by what it changed:
    /// an Enter near the top of a file of 20,000 lines allocates on the UI thread about what one in a short file does,
    /// each with a breakpoint below; the breakpoint goes down with its code, and the program, let go, stops there,
    /// shown at its line now.</summary>
    [AvaloniaFact]
    public void A_key_near_the_top_of_a_long_debugged_file_costs_what_it_does_in_a_short_one_and_its_breakpoint_and_stop_go_with_their_code()
    {
        Write("Program.cs", "int total = 0;\nfor (int i = 1; i <= 3; i++)\n{\n    total += i;\n}\nConsole.WriteLine($\"total {total} {Long.Last()} {Short.Last()}\");\n");
        Write("Long.cs", "static class Long\n{\n" + string.Concat(Enumerable.Range(0, 20_000).Select(i => $"    public const int Value{i} = {i};\n"))
            + "\n    public static int Last()\n    {\n        return Value0;\n    }\n}\n");
        Write("Short.cs", "static class Short\n{\n    public const int One = 1;\n\n    public static int Last()\n    {\n        return One;\n    }\n}\n");
        string longFile = Path.Combine(PathNames.Canonical(Folder), "Long.cs");
        string shortFile = Path.Combine(PathNames.Canonical(Folder), "Short.cs");
        MainWindow window = Open();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => window.Debugger.Session?.State == DebugState.Paused, "the stop in the loop");
        int longReturn = Breakpoint(longFile, "return Value0;");
        int shortReturn = Breakpoint(shortFile, "return One;");
        Ui.Settle();

        const int Keys = 5;
        long inShort = Enters(shortFile);
        long inLong = Enters(longFile);
        TestContext.Current.TestOutputHelper?.WriteLine($"An Enter in the short file allocated {inShort / 1024} KB on the UI thread, near the top of 20,000 lines {inLong / 1024} KB");
        Assert.True(inLong <= inShort + (32 * 1024),
            $"An Enter near the top of a debugged file of 20,000 lines allocated {inLong / 1024} KB on the UI thread, against {inShort / 1024} KB in a short one: the build's lines were carried through it one by one");
        Assert.Equal(longReturn + Keys, Assert.Single(window.Debugger.Breakpoints.In(longFile)).Line);
        Assert.Equal(shortReturn + Keys, Assert.Single(window.Debugger.Breakpoints.In(shortFile)).Line);

        window.OpenFile(Program);
        Ui.Settle();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.Run(window, AppActions.Resume);
        WaitDebugging(window, () => window.EditorArea.Active?.Path == longFile
            && window.EditorArea.CodeEditor.LineMarks.Contains(new LineMark(longReturn + Keys, LineMarkKind.ExecutionPoint)), "the stop at Long's return, where its line is now");
        Assert.Contains(new LineMark(longReturn + Keys, LineMarkKind.BreakpointBound), window.EditorArea.CodeEditor.LineMarks);
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();

        // A breakpoint on a statement of a file, bound: its line.
        int Breakpoint(string file, string statement)
        {
            window.OpenFile(file);
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            int line = editor.Document.Snapshot.GetLineFromOffset(editor.Document.Snapshot.ToString().IndexOf(statement, StringComparison.Ordinal));
            editor.GoTo(new TextPosition(line, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(line, LineMarkKind.BreakpointBound)), $"the breakpoint at {Path.GetFileName(file)}'s {statement} bound");
            return line;
        }

        // The least an Enter at the top of a file allocated on the UI thread, of a few.
        long Enters(string file)
        {
            window.OpenFile(file);
            Ui.Settle();
            CodeEditor shown = window.EditorArea.CodeEditor;
            shown.GoTo(new TextPosition(0, 0));
            shown.Focus();
            Ui.Settle();
            long least = long.MaxValue;
            for (int i = 0; i < Keys; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                Ui.Settle();
            }
            return least;
        }
    }

    /// <summary>Roslyn's compiler ends a line at each break in the comment that the editor does not, so the PDB's lines run four
    /// ahead of the editor's below it: a breakpoint stops at its own line, the arrow, the frame's row and an agent's place say
    /// it, a line typed above while paused moves them with their code, and Run to Cursor and Set Next Statement go to the
    /// lines asked.</summary>
    [AvaloniaFact]
    public void Below_line_breaks_only_the_compiler_counts_a_breakpoint_its_stop_run_to_cursor_and_set_next_statement_keep_to_the_editors_lines()
    {
        Write("Program.cs", $"int total = 0;\n/* Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd */\nfor (int i = 1; i <= 3; i++)\n{{\n    total += i;\n}}\n"
            + "total *= 2;\nConsole.WriteLine($\"total {total}\");\n");
        MainWindow window = Open(hotReload: true);
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(4, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => Value(window, "i") == "1", "the stop at the breakpoint on total += i");
        Assert.Equal("0", Value(window, "total"));
        Assert.Contains(new LineMark(4, LineMarkKind.BreakpointBound), editor.LineMarks);
        Assert.Contains(new LineMark(4, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Contains(new LineAnnotation(4, "total: 0", LineAnnotationKind.InlineValue), editor.LineAnnotations);
        // The debugger names the compiler's line 9; the frame's row, and the stack copied from it, the editor's 5.
        Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
        Assert.Equal(9, view.Frames[0].Line);
        ListBox frames = window.DebugWindow.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, view.Frames));
        Ui.ChooseFromMenu(frames, "Copy Stack");
        Ui.WaitFor(() => Ui.Clipboard(window)?.Contains("<Main>$", StringComparison.Ordinal) == true, "the stack on the clipboard");
        Assert.Contains("Program.cs:5", Ui.Clipboard(window), StringComparison.Ordinal);
        Passenger.App.Mcp.DebugSnapshot told = (Passenger.App.Mcp.DebugSnapshot)Ui.Await(new Passenger.App.Mcp.PassengerTools(window).GetDebugState(), "the stop as an agent is told it");
        Assert.StartsWith("Program.cs:5:", told.Location, StringComparison.Ordinal);
        Assert.EndsWith("at Program.cs:5", told.Frames[0], StringComparison.Ordinal);

        editor.GoTo(new TextPosition(0, 0));
        editor.Focus();
        Ui.Press(window, "Enter");
        Assert.Contains(new LineMark(5, LineMarkKind.BreakpointBound), editor.LineMarks);
        Assert.Contains(new LineMark(5, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Ui.Run(window, AppActions.Resume);
        WaitDebugging(window, () => Value(window, "i") == "2", "the loop's next turn at the breakpoint's statement");
        Assert.Equal(10, view.Frames[0].Line);
        Assert.Contains(new LineMark(5, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Contains(new LineMark(5, LineMarkKind.BreakpointBound), editor.LineMarks);

        editor.GoTo(new TextPosition(5, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.GoTo(new TextPosition(7, 0));
        Ui.Run(window, AppActions.RunToCursor);
        WaitDebugging(window, () => Value(window, "total") == "6" && editor.LineMarks.Contains(new LineMark(7, LineMarkKind.ExecutionPoint)), "Run to Cursor's stop at total *= 2");

        editor.GoTo(new TextPosition(8, 0));
        Ui.Run(window, AppActions.SetNextStatement);
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(8, LineMarkKind.ExecutionPoint)), "the arrow at the WriteLine Set Next Statement chose");
        Ui.Run(window, AppActions.Resume);
        WaitDebugging(window, () => view.Console.Document.Snapshot.ToString().Contains("total 6", StringComparison.Ordinal), "the program to print the total it had, not doubled");
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the program's end");
        window.Close();
    }

    [AvaloniaFact]
    public void A_hit_count_the_debugger_cannot_read_is_said_as_it_is_typed_and_the_breakpoint_refused()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(5, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.EditBreakpoint);
        BreakpointPopup popup = Assert.IsType<BreakpointPopup>(window.Popup);
        Ui.Settle();
        Ui.Press(window, "Tab");
        window.KeyTextInput("3 times");
        Ui.Settle();
        const string said = "The hit count \"3 times\" is not one the debugger can read";
        Assert.Contains(popup.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith(said, StringComparison.Ordinal) == true);
        Ui.Press(window, "Enter");
        LineBreakpoint breakpoint = window.Debugger.Breakpoints.In(Program)[0];
        Assert.Equal("3 times", breakpoint.HitCondition);

        StartDebugging(window);
        WaitDebugging(window, () => Value(window, "total") == "6", "the stop after the loop");
        Assert.Contains(new LineMark(3, LineMarkKind.BreakpointInvalid, Conditional: true), editor.LineMarks);
        Assert.StartsWith(said, breakpoint.Message, StringComparison.Ordinal);
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    [AvaloniaFact]
    public void A_configuration_is_run_debugged_and_profiled_with_the_same_arguments_variable_and_working_folder()
    {
        Write("Program.cs", "Console.WriteLine($\"seen [{string.Join(\"|\", args)}] {Environment.GetEnvironmentVariable(\"PASSENGER_SEEN\")} in {Environment.CurrentDirectory}\");\n");
        Write("Properties/launchSettings.json", """
            { "profiles": { "Seen": { "commandName": "Project", "commandLineArgs": "first \"second word\"", "workingDirectory": "work dir",
              "environmentVariables": { "PASSENGER_SEEN": "yes, seen" } } } }
            """);
        string work = Path.GetDirectoryName(Write("work dir/.keep", string.Empty))!;
        string settings = TestFolders.Create("settings-");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(Fresh());
        Ui.Settle();
        try
        {
            Assert.Equal("Sum: Seen", window.Configuration?.Name);
            string seen = $"seen [first|second word] yes, seen in {PathNames.Canonical(work)}";

            Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end");
            string ran = window.BottomPanel.Run.Document.Snapshot.ToString();
            Assert.True(ran.Contains(seen, StringComparison.Ordinal), $"Run did not give the program what Debug and Profile give it, {seen}:\n{ran}");

            Ui.Launched(window, window.DebugAsync(), "the debug session to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugged program to end");
            string debugged = window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.True(debugged.Contains(seen, StringComparison.Ordinal), $"Debug did not give the program what Run and Profile give it, {seen}:\n{debugged}");

            Ui.Launched(window, window.ProfileAsync(), "the profiled run to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.ProfilerView.Snapshot is not null, "the profiled program to end", TimeSpan.FromMinutes(2));
            string profiled = window.BottomPanel.Run.Document.Snapshot.ToString();
            Assert.True(profiled.Contains(seen, StringComparison.Ordinal), $"Profile did not give the program what Run and Debug give it, {seen}:\n{profiled}");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(settings);
        }
    }

    /// <summary>One program of calls stepped through: a step over a call lists what it returned at the top of Variables, which
    /// a watch names and the next step's calls replace; then Smart Step Into marks the next line's calls in the order they
    /// run, and Enter steps into the one chosen. Resumed, the program ends in an exception the runtime throws, which stops it
    /// at the line that threw it, named there and in the status bar, and is listed in Variables as $exception, which opens
    /// to its members and a watch reads.</summary>
    [AvaloniaFact]
    public void A_step_over_a_call_lists_what_it_returned_smart_step_into_steps_into_the_call_chosen_and_an_exception_stop_names_the_exception_at_its_line()
    {
        Write("Program.cs", "int total = Add(2, 3);\nConsole.WriteLine(Describe(total));\nint nested = Outer(Inner());\nConsole.WriteLine(nested);\nConsole.WriteLine(\"done\");\n"
            + "int[] values = [1, 2];\nint index = 5;\nConsole.WriteLine(values[index]);\n\n"
            + "static int Add(int a, int b) => a + b;\nstatic string Describe(int value) => $\"total {value}\";\n"
            + "static int Inner() => 20;\nstatic int Outer(int value)\n{\n    return value + 1;\n}\n");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(0, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => window.Debugger.Session?.State == DebugState.Paused && Row(window, "total") is not null, "the stop before the call");
        Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
        Assert.DoesNotContain(view.Tree.Rows, r => r.IsReturnValue);

        Ui.Run(window, AppActions.StepOver);
        Ui.WaitFor(() => Value(window, "Add() returned") == "5", "what the call returned, in Variables");
        VariableNode returned = view.Tree.Rows[0];
        Assert.Equal(("Add() returned", "int", "$ReturnValue"), (returned.Name, returned.Type, returned.Expression));
        Assert.True(returned.IsReturnValue, "The call's value was not shown as one");

        window.DebugWindow.VariablesList.SelectedItem = returned;
        Ui.ChooseFromMenu(window.DebugWindow.VariablesList, "Add to Watches");
        Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Role == VariableRole.Watch && r.Name == "$ReturnValue" && r.Value == "5"), "the watch of the value returned");
        Ui.Run(window, AppActions.Evaluate);
        window.KeyTextInput("$returnvalue * 2");
        Ui.Press(window, OperatingSystem.IsMacOS() ? "meta shift Enter" : "control shift Enter");
        Watched(view, "$returnvalue * 2", "10", "a watch computed from the value returned");

        Ui.Run(window, AppActions.StepOver);
        Shows(view, "Describe() returned", "\"total 5\"", "the next step's value");
        Assert.DoesNotContain(view.Tree.Rows, r => r.Name == "Add() returned");
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(2, LineMarkKind.ExecutionPoint)), "the step to the line of calls");

        // The keys go to the editor, back from the evaluate box.
        editor.Focus();
        Ui.Settle();
        Ui.Run(window, AppActions.SmartStepInto);
        Ui.WaitFor(() => editor.IsChoosing, "the line's calls to be offered");
        Assert.Equal(["Inner", "Outer"], editor.Choices.Select(r => editor.Document.Snapshot.GetText(r.Start, r.Length)));
        Assert.Equal(0, editor.Chosen);
        Ui.Press(window, "Escape");
        Assert.False(editor.IsChoosing, "Escape did not leave Smart Step Into");
        Assert.Equal(DebugState.Paused, window.Debugger.Session?.State);
        Assert.Contains(new LineMark(2, LineMarkKind.ExecutionPoint), editor.LineMarks);

        Ui.Run(window, AppActions.SmartStepInto);
        Ui.WaitFor(() => editor.IsChoosing, "the line's calls offered again");
        Ui.Press(window, "Right");
        Assert.Equal(1, editor.Chosen);
        Ui.Press(window, "Tab");
        Assert.Equal(0, editor.Chosen);
        Ui.Press(window, "shift Tab");
        Assert.Equal(1, editor.Chosen);
        Ui.Press(window, "Enter");
        Ui.WaitFor(() => Value(window, "value") == "20", "the stop in Outer, given what Inner returned");
        Assert.False(editor.IsChoosing);
        Assert.Contains(new LineMark(13, LineMarkKind.ExecutionPoint), editor.LineMarks);

        Ui.Run(window, AppActions.Resume);
        WaitDebugging(window, () => editor.LineAnnotations.Any(a => a.Kind == LineAnnotationKind.Exception), "the exception at its line");
        LineAnnotation thrown = Assert.Single(editor.LineAnnotations, a => a.Kind == LineAnnotationKind.Exception);
        Assert.Equal(7, thrown.Line);
        Assert.StartsWith("IndexOutOfRangeException", thrown.Text, StringComparison.Ordinal);
        Assert.Contains(new LineAnnotation(7, "index: 5", LineAnnotationKind.InlineValue), editor.LineAnnotations);
        Assert.Contains("IndexOutOfRangeException", window.Status, StringComparison.Ordinal);

        WaitDebugging(window, () => Row(window, "$exception") is not null, "the exception in Variables");
        VariableNode exception = Row(window, "$exception")!;
        Assert.True(exception.IsException);
        Assert.Equal("System.IndexOutOfRangeException", exception.Type);
        Assert.True(exception.HasChildren, "The exception does not open to its members");
        Ui.Run(window, AppActions.Evaluate);
        window.KeyTextInput("$exception.Message");
        Ui.Press(window, OperatingSystem.IsMacOS() ? "meta shift Enter" : "control shift Enter");
        Watched(view, "$exception.Message", "\"Index was outside the bounds of the array.\"", "the watch of the exception's message");
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    [AvaloniaFact]
    public void A_condition_typed_into_a_breakpoints_popup_decides_where_it_stops()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.EditBreakpoint);
        Assert.IsType<BreakpointPopup>(window.Popup);
        Ui.Settle();
        window.KeyTextInput("i == 2");
        Ui.Press(window, "Enter");
        Assert.Null(window.Popup);
        LineBreakpoint breakpoint = Assert.Single(window.Debugger.Breakpoints.In(Program));
        Assert.Equal("i == 2", breakpoint.Condition);
        Assert.Contains(new LineMark(3, LineMarkKind.Breakpoint, Conditional: true), editor.LineMarks);

        StartDebugging(window);
        WaitDebugging(window, () => window.Debugger.Session?.State == DebugState.Paused && Value(window, "i") is not null, "the stop the condition allows");
        Assert.Equal("2", Value(window, "i"));
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    [AvaloniaFact]
    public void A_tracepoint_set_with_shift_click_logs_its_message_and_the_program_runs_on()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Point gutter = Gutter(window, editor, 3);
        window.MouseDown(gutter, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(gutter, MouseButton.Left, RawInputModifiers.Shift);
        Ui.Settle();
        Assert.Contains(new LineMark(3, LineMarkKind.Breakpoint, Tracepoint: true), editor.LineMarks);

        // The log message is two fields on from the condition.
        window.MouseDown(gutter, MouseButton.Right);
        window.MouseUp(gutter, MouseButton.Right);
        Ui.Settle();
        Assert.IsType<BreakpointPopup>(window.Popup);
        Ui.Press(window, "Tab");
        Ui.Press(window, "Tab");
        window.KeyTextInput("i is {i}, total {total}");
        Ui.Press(window, "Enter");
        Assert.Equal("i is {i}, total {total}", Assert.Single(window.Debugger.Breakpoints.In(Program)).LogMessage);

        StartDebugging(window);
        WaitDebugging(window, () => window.Debugger.Session?.State == DebugState.Ended, "the program to run to its end without stopping");
        string console = window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
        Assert.Contains("i is 1, total 0\ni is 2, total 1\ni is 3, total 3\n", console, StringComparison.Ordinal);
        Assert.Contains("total 6", console, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void The_breakpoints_window_lists_every_breakpoint_and_turns_one_off()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.GoTo(new TextPosition(5, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);

        Ui.Run(window, AppActions.ViewBreakpoints);
        BreakpointsWindow breakpoints = Assert.IsType<BreakpointsWindow>(window.Popup);
        Assert.Equal(["Program.cs:4", "Program.cs:6", ExceptionBreakpoint.AnyException, BindingErrorBreakpoint.AnyBindingError], breakpoints.Listed.Select(b => b.Location));
        CheckBox first = breakpoints.GetVisualDescendants().OfType<CheckBox>().First(c => c.Content is null);
        first.IsChecked = false;
        Assert.False(window.Debugger.Breakpoints.In(Program)[0].Enabled);
        Assert.Contains(new LineMark(3, LineMarkKind.BreakpointDisabled), editor.LineMarks);
        Ui.Press(window, "Escape");
        Assert.Null(window.Popup);
        window.Close();
    }

    [AvaloniaFact]
    public void An_exception_breakpoint_added_by_its_type_stops_where_that_type_is_thrown_as_its_condition_allows_and_at_no_other_type()
    {
        Write("Program.cs", "for (int turn = 1; turn <= 3; turn++)\n{\n    try\n    {\n        throw new ArgumentException($\"argument {turn}\");\n    }\n"
            + "    catch (ArgumentException)\n    {\n    }\n    try\n    {\n        throw new InvalidOperationException($\"operation {turn}\");\n    }\n"
            + "    catch (InvalidOperationException)\n    {\n    }\n}\nConsole.WriteLine(\"done\");\n");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        BreakpointsWindow breakpoints = OpenBreakpoints(window);
        ExceptionBreakpoint operation = AddException(window, breakpoints, "InvalidOperationEx", "System.InvalidOperationException");
        // A new breakpoint on a type stops wherever it is thrown, however it is handled.
        Assert.Equal(ExceptionScenarios.All, operation.Scenarios);
        Ui.Click(breakpoints.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText?.StartsWith("A C# condition", StringComparison.Ordinal) == true));
        window.KeyTextInput("turn == 2");
        Ui.Click(Button(breakpoints, "Done"));
        Assert.Null(window.Popup);
        Assert.Equal("turn == 2", operation.Condition);

        // The argument exceptions and the first turn's pass: the stop is the second turn's, at the line that threw it.
        StartDebugging(window);
        WaitDebugging(window, () => editor.LineAnnotations.Any(a => a.Kind == LineAnnotationKind.Exception), "the stop at the exception breakpoint");
        LineAnnotation thrown = Assert.Single(editor.LineAnnotations, a => a.Kind == LineAnnotationKind.Exception);
        Assert.Equal((11, "InvalidOperationException: operation 2"), (thrown.Line, thrown.Text));
        Assert.Equal("2", Value(window, "turn"));

        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Ended, "the program to run to its end");
        Assert.Contains("done", window.BottomPanel.Debug.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void An_exception_breakpoints_handler_options_stop_it_where_the_users_code_leaves_the_exception_or_where_it_handles_it()
    {
        // The first is handled where it is thrown; List.Sort catches the comparer's and throws its own, which the program handles.
        Write("Program.cs", "try\n{\n    throw new InvalidOperationException(\"caught by the program\");\n}\ncatch (InvalidOperationException)\n{\n}\n"
            + "List<int> numbers = [3, 1, 2];\ntry\n{\n    numbers.Sort((_, _) => throw new InvalidOperationException(\"left to List.Sort\"));\n}\n"
            + "catch (InvalidOperationException)\n{\n}\nConsole.WriteLine(\"done\");\n");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        BreakpointsWindow breakpoints = OpenBreakpoints(window);
        // "Any exception" off, so only the breakpoint on the type stops the program.
        Ui.Click(Row(breakpoints, ExceptionBreakpoint.AnyException).GetVisualDescendants().OfType<CheckBox>().First());
        Assert.False(window.Debugger.Breakpoints.Exceptions[0].Enabled);
        ExceptionBreakpoint operation = AddException(window, breakpoints, "InvalidOperationEx", "System.InvalidOperationException");
        // User-unhandled: handled outside the user's code, or not at all.
        Ui.Click(Scenario(breakpoints, "Exception handler:", "User code"));
        Ui.Click(Button(breakpoints, "Done"));
        Assert.Equal(ExceptionScenarios.Thrown | ExceptionScenarios.HandledInExternalCode | ExceptionScenarios.Unhandled, operation.Scenarios);

        StartDebugging(window);
        WaitDebugging(window, () => editor.LineAnnotations.Any(a => a.Kind == LineAnnotationKind.Exception) || window.Debugger.Session?.State == DebugState.Ended,
            "the stop where List.Sort catches the comparer's exception");
        Assert.Equal((10, "InvalidOperationException: left to List.Sort"), Thrown(editor));
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Ended, "the program to run to its end past the exceptions it handles");

        breakpoints = OpenBreakpoints(window);
        Ui.Click(Row(breakpoints, "System.InvalidOperationException"));
        Ui.Click(Scenario(breakpoints, "Exception handler:", "User code"));
        Ui.Click(Scenario(breakpoints, "Exception handler:", "External code"));
        Ui.Click(Scenario(breakpoints, "Exception handler:", "Unhandled"));
        Ui.Click(Button(breakpoints, "Done"));
        Assert.Equal(ExceptionScenarios.Thrown | ExceptionScenarios.HandledInUserCode, operation.Scenarios);
        StartDebugging(window);
        WaitDebugging(window, () => editor.LineAnnotations.Any(a => a.Kind == LineAnnotationKind.Exception) || window.Debugger.Session?.State == DebugState.Ended,
            "the stop at the exception the program catches");
        Assert.Equal((2, "InvalidOperationException: caught by the program"), Thrown(editor));
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => Thrown(editor) is (10, string text) && text.Contains("Failed to compare", StringComparison.Ordinal) || window.Debugger.Session?.State == DebugState.Ended,
            "the stop at List.Sort's exception the program catches");
        Assert.True(Thrown(editor) is (10, string said) && said.StartsWith("InvalidOperationException: Failed to compare", StringComparison.Ordinal),
            $"The second stop was not at List.Sort's own exception but {Thrown(editor)}, the session {window.Debugger.Session?.State}");
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Ended, "the program to run to its end");
        window.Close();
    }

    [AvaloniaFact]
    public void Mute_breakpoints_lets_a_run_pass_a_line_breakpoint_and_an_exception_breakpoint_and_unmuting_stops_it_again()
    {
        Write("Program.cs", "for (int turn = 1; turn <= 2; turn++)\n{\n    Console.WriteLine($\"turn {turn}\");\n    try\n    {\n"
            + "        throw new InvalidOperationException($\"turn {turn}\");\n    }\n    catch (InvalidOperationException)\n    {\n    }\n"
            + "    Console.WriteLine($\"passed {turn}\");\n    Console.ReadLine();\n}\n");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        BreakpointsWindow breakpoints = OpenBreakpoints(window);
        AddException(window, breakpoints, "InvalidOperationEx", "System.InvalidOperationException");
        Ui.Click(Button(breakpoints, "Done"));

        Assert.False(MuteItem(window).IsChecked);
        MuteItem(window).Command!.Execute(null);
        Ui.Settle();
        Assert.True(window.Debugger.Breakpoints.Muted);
        Assert.True(MuteItem(window).IsChecked, "Run > Mute Breakpoints shows no check while the breakpoints are muted");
        Assert.Contains(new LineMark(2, LineMarkKind.Breakpoint, Muted: true), editor.LineMarks);
        Assert.True(window.BottomPanel.Debug.ShowsMuted, "The Debug window's Mute Breakpoints does not show pressed while they are muted");
        Assert.Single(window.Debugger.Breakpoints.In(Program));
        Assert.Equal(2, window.Debugger.Breakpoints.Exceptions.Count);

        StartDebugging(window);
        Passenger.App.ToolWindows.OutputView console = window.BottomPanel.Debug.Console;
        Ui.WaitFor(() => console.Document.Snapshot.ToString().Contains("passed 1", StringComparison.Ordinal) || window.Debugger.Session?.State is DebugState.Paused or DebugState.Ended,
            "the first turn to run past both breakpoints");
        Assert.True(window.Debugger.Session?.State == DebugState.Running, $"Muted, the program still stopped: {window.Status}");

        Ui.Run(window, AppActions.MuteBreakpoints);
        Assert.False(window.BottomPanel.Debug.ShowsMuted);
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(2, LineMarkKind.BreakpointBound)), "the breakpoint bound again once unmuted");
        _ = window.Debugger.Session!.InputAsync("\n");
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Value(window, "turn") is not null, "the stop at the line breakpoint");
        Assert.Equal("2", Value(window, "turn"));
        Assert.Contains(new LineMark(2, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => editor.LineAnnotations.Any(a => a.Kind == LineAnnotationKind.Exception), "the stop at the exception breakpoint");
        Assert.Equal((5, "InvalidOperationException: turn 2"), Thrown(editor));
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    [AvaloniaFact]
    public void The_breakpoints_window_adds_and_removes_exception_breakpoints_and_the_folder_keeps_them()
    {
        string settings = TestFolders.Create("settings-");
        try
        {
            MainWindow first = Keeping(settings);
            BreakpointsWindow breakpoints = OpenBreakpoints(first);
            AddException(first, breakpoints, "InvalidOperationEx", "System.InvalidOperationException");
            Ui.Click(Scenario(breakpoints, "Exception handler:", "User code"));
            AddException(first, breakpoints, "ArgumentNull", "System.ArgumentNullException");
            Ui.Click(Row(breakpoints, ExceptionBreakpoint.AnyException).GetVisualDescendants().OfType<CheckBox>().First());
            Assert.Equal([ExceptionBreakpoint.AnyException, "System.InvalidOperationException", "System.ArgumentNullException", BindingErrorBreakpoint.AnyBindingError],
                breakpoints.Listed.Select(b => b.Location));
            Assert.False(first.Debugger.Breakpoints.BindingErrors.Enabled, "XAML Binding Errors was on in a folder that never turned it on");
            Assert.Contains(breakpoints.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "XAML Binding Errors");
            Ui.Click(Row(breakpoints, BindingErrorBreakpoint.AnyBindingError).GetVisualDescendants().OfType<CheckBox>().First());
            Ui.Click(Row(breakpoints, BindingErrorBreakpoint.AnyBindingError));
            Ui.Click(breakpoints.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText?.StartsWith("A C# condition", StringComparison.Ordinal) == true));
            first.KeyTextInput("$bindingError.Path == \"Name\"");
            Ui.Click(Button(breakpoints, "Done"));
            first.Close();

            MainWindow second = Keeping(settings);
            IReadOnlyList<ExceptionBreakpoint> kept = second.Debugger.Breakpoints.Exceptions;
            Assert.Equal([null, "System.InvalidOperationException", "System.ArgumentNullException"], kept.Select(e => e.Type));
            Assert.False(kept[0].Enabled);
            Assert.Equal(ExceptionScenarios.Thrown | ExceptionScenarios.HandledInExternalCode | ExceptionScenarios.Unhandled, kept[1].Scenarios);
            Assert.Equal(ExceptionScenarios.All, kept[2].Scenarios);
            BindingErrorBreakpoint bindings = second.Debugger.Breakpoints.BindingErrors;
            Assert.Equal((true, "$bindingError.Path == \"Name\""), (bindings.Enabled, bindings.Condition));

            breakpoints = OpenBreakpoints(second);
            Ui.Click(Row(breakpoints, "System.ArgumentNullException"));
            Ui.Press(second, "Delete");
            Ui.Click(Row(breakpoints, ExceptionBreakpoint.AnyException));
            Ui.Press(second, "Delete");
            Ui.Click(Row(breakpoints, BindingErrorBreakpoint.AnyBindingError));
            Ui.Press(second, "Delete");
            Assert.Equal([ExceptionBreakpoint.AnyException, "System.InvalidOperationException", BindingErrorBreakpoint.AnyBindingError], breakpoints.Listed.Select(b => b.Location));
            Ui.Press(second, "Escape");
            second.Close();

            MainWindow third = Keeping(settings);
            Assert.Equal([null, "System.InvalidOperationException"], third.Debugger.Breakpoints.Exceptions.Select(e => e.Type));
            Assert.True(third.Debugger.Breakpoints.BindingErrors.Enabled, "XAML Binding Errors was not kept on with the folder");
            third.Close();
        }
        finally
        {
            TestFolders.Delete(settings);
        }
    }

    /// <summary>The window a person uses, keeping its folders' state in a store of its own, over Sum, on the server the last
    /// window on it left.</summary>
    private MainWindow Keeping(string settings)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(sum.Folder);
        Ui.Settle();
        window.OpenFile(Program);
        Ui.Settle();
        return window;
    }

    /// <summary>Run > Mute Breakpoints as the menu bar has it when it opens.</summary>
    private static NativeMenuItem MuteItem(MainWindow window) =>
        Ui.Submenu(window.MenuBar, "Run").Items.OfType<NativeMenuItem>().First(i => i.Header == "Mute Breakpoints");

    private static BreakpointsWindow OpenBreakpoints(MainWindow window)
    {
        Ui.Run(window, AppActions.ViewBreakpoints);
        return Assert.IsType<BreakpointsWindow>(window.Popup);
    }

    /// <summary>Adds an exception breakpoint as a person does: + in the Breakpoints window, part of the type's name typed, and
    /// Enter on the type it offers first, which is then the one chosen.</summary>
    private static ExceptionBreakpoint AddException(MainWindow window, BreakpointsWindow breakpoints, string typed, string type)
    {
        Ui.Click(breakpoints.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Add CLR Exception Breakpoints"));
        window.KeyTextInput(typed);
        Ui.WaitFor(() => breakpoints.Offered.FirstOrDefault() == type, $"{type} offered first for \"{typed}\", not [{string.Join(", ", breakpoints.Offered.Take(5))}]");
        Ui.Press(window, "Enter");
        ExceptionBreakpoint added = Assert.IsType<ExceptionBreakpoint>(breakpoints.Chosen);
        Assert.Equal(type, added.Type);
        return added;
    }

    /// <summary>A breakpoint's row in the Breakpoints window, by how it is named there.</summary>
    private static ListBoxItem Row(BreakpointsWindow breakpoints, string location) =>
        breakpoints.GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.Tag as Breakpoint)?.Location == location);

    /// <summary>One of the "Suspend If" boxes for the chosen exception breakpoint: its row's label and its
    /// own.</summary>
    private static CheckBox Scenario(BreakpointsWindow breakpoints, string row, string box)
    {
        TextBlock label = breakpoints.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == row && t.IsEffectivelyVisible);
        Grid grid = (Grid)label.Parent!;
        StackPanel boxes = grid.Children.OfType<StackPanel>().First(p => Grid.GetRow(p) == Grid.GetRow(label));
        return boxes.Children.OfType<CheckBox>().First(c => (string?)c.Content == box);
    }

    private static Button Button(Control within, string content) =>
        within.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == content && b.IsEffectivelyVisible);

    /// <summary>Waits for a top-level row of Variables to show a value; a failure says what the rows showed instead.</summary>
    private static void Shows(Passenger.App.ToolWindows.DebugModel view, string name, string value, string what)
    {
        try
        {
            Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Depth == 0 && r.Name == name && r.Value == value), what, TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            string shown = string.Join("; ", view.Tree.Rows.Where(r => r.Depth == 0).Select(r => $"{r.Name} = {r.Value}"));
            throw new TimeoutException($"{name} never showed {value}: the rows were {(shown.Length == 0 ? "none" : shown)}");
        }
    }

    /// <summary>Waits for a watch to show a value; a failure says what the watches showed instead.</summary>
    private static void Watched(Passenger.App.ToolWindows.DebugModel view, string expression, string value, string what)
    {
        try
        {
            Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Role == VariableRole.Watch && r.Name == expression && r.Value == value), what, TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            string shown = string.Join("; ", view.Tree.Rows.Where(r => r.Role == VariableRole.Watch).Select(r => $"{r.Name} = {r.Value}"));
            throw new TimeoutException($"{expression} never showed {value} as a watch: the watches were {(shown.Length == 0 ? "none" : shown)}");
        }
    }

    /// <summary>The exception the stop shown names, and the line it shows it at.</summary>
    private static (int Line, string Text)? Thrown(CodeEditor editor) =>
        editor.LineAnnotations.FirstOrDefault(a => a.Kind == LineAnnotationKind.Exception) is { Text: not null } thrown ? (thrown.Line, thrown.Text) : null;

    /// <summary>A stop where a local holds a string of megabytes shows the start of it in its row and in a hover, and
    /// Copy Value still has all of it.</summary>
    [AvaloniaFact]
    public void A_string_of_megabytes_shows_its_start_in_variables_and_a_hover_without_holding_the_window()
    {
        Write("Program.cs", "string big = new string('x', 4_000_000);\nint n = big.Length;\nConsole.WriteLine(n);\n");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => window.Debugger.Session?.State == DebugState.Paused, "the stop");
        long before = GC.GetAllocatedBytesForCurrentThread();
        WaitDebugging(window, () => Row(window, "big") is not null, "the string's row");
        Ui.Settle();
        long shown = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(shown < 16 << 20, $"Showing the stop's values allocated {shown >> 20} MB on the UI thread: the 8 MB string was laid out whole");
        Assert.Equal(4_000_002, Row(window, "big")!.Value.Length);

        Point at = editor.TranslatePoint(editor.PointOf(editor.Document.Snapshot.ToString().IndexOf("big.Length", StringComparison.Ordinal) + 1)
            + new Point(1, editor.LineHeight / 2), window)!.Value;
        before = GC.GetAllocatedBytesForCurrentThread();
        window.MouseMove(at, RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the hover card on big");
        Ui.Settle();
        long hovered = GC.GetAllocatedBytesForCurrentThread() - before;
        string card = window.Assist.HoverText!;
        Assert.True(card.StartsWith("big = {string} \"xxx", StringComparison.Ordinal) && card.Length < 2000,
            $"The hover card says {card.Length} characters, beginning \"{card[..Math.Min(40, card.Length)]}\"");
        Assert.True(hovered < 16 << 20, $"Showing the hover card allocated {hovered >> 20} MB on the UI thread: the string was laid out whole");
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    /// <summary>An expression evaluated at a stop runs once, when it is asked: its result is that stop's, gone once the
    /// program goes on, never evaluated again at a later stop. At the next stop, a value set in Variables is the one
    /// the program goes on with.</summary>
    [AvaloniaFact]
    public void An_expression_evaluated_at_a_stop_runs_once_with_the_stop_and_a_value_set_in_variables_is_the_one_the_program_goes_on_with()
    {
        MainWindow window = Open();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => Value(window, "total") == "0", "the first stop");
        Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
        Ui.Run(window, AppActions.Evaluate);
        window.KeyTextInput("total += 100");
        Ui.Press(window, "Enter");
        Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Role == VariableRole.Result && r.Name == "total += 100" && r.Value == "100"), "the expression's result");
        Ui.WaitFor(() => Value(window, "total") == "100", "the value the expression set");

        // Two steps: the loop's own add once (101), and then nothing, the expression never run again.
        StepOver();
        Assert.True(Value(window, "total") == "101", $"After a step total was {Value(window, "total")}: the expression evaluated at the stop before ran again");
        Assert.DoesNotContain(view.Tree.Rows, r => r.Role == VariableRole.Result);
        StepOver();
        Assert.True(Value(window, "total") == "101", $"After a second step total was {Value(window, "total")}: the expression ran again");

        Ui.Run(window, AppActions.Resume);
        WaitDebugging(window, () => Value(window, "i") == "2" && Value(window, "total") == "101", "the loop's next turn");
        ListBox list = window.DebugWindow.VariablesList;
        VariableNode total = view.Tree.Rows.First(r => r.Name == "total");
        Control row = list.ContainerFromItem(total)!;
        Ui.Click(row, settle: false);
        Ui.Press(window, "F2");
        Assert.True(total.IsEditing);
        window.KeyTextInput("100");
        Ui.Press(window, "Enter");
        Ui.WaitFor(() => Value(window, "total") == "100", "the value set");
        Ui.Run(window, AppActions.StepOver);
        Ui.WaitFor(() => Value(window, "total") == "102", "the program to add to the value set, not the old one");
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();

        void StepOver()
        {
            StopTimes? before = window.Debugger.LastStopTimes;
            Ui.Run(window, AppActions.StepOver);
            WaitDebugging(window, () => !ReferenceEquals(window.Debugger.LastStopTimes, before), "the step's stop and its locals");
        }
    }

    /// <summary>An element listed under a range (an array longer than a hundred, a row of a two-dimensional one) takes a
    /// value set in its row as any element does: the range's row is no step of the expression that reaches it.</summary>
    [AvaloniaFact]
    public void A_value_set_on_an_element_listed_under_a_range_is_the_one_the_program_goes_on_with()
    {
        Write("Program.cs", "int[] numbers = new int[250];\nint[,] grid = new int[2, 2];\nConsole.WriteLine($\"set {numbers[150]} {grid[1, 0]}\");\n");
        MainWindow window = Open();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => Row(window, "grid") is not null, "the stop before the line that writes them");

        TreeKeysCheck.Press(window, window.BottomPanel.Debug.Tree.Rows, Row(window, "numbers")!, "Variables");
        SetElement(window, "numbers", "[100..199]", "[150]", "7");
        SetElement(window, "grid", "[1, ...]", "[1, 0]", "5");
        Assert.True(window.Status?.StartsWith("Could not set", StringComparison.Ordinal) != true, $"Set Value was refused: {window.Status}");
        Ui.Run(window, AppActions.Resume);
        WaitDebugging(window, () => window.BottomPanel.Debug.Console.Document.Snapshot.ToString().Contains("set ", StringComparison.Ordinal),
            "the program to write the values");
        Assert.Contains("set 7 5", window.BottomPanel.Debug.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();

        static void SetElement(MainWindow window, string local, string range, string element, string value)
        {
            Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
            VariableNode Child(VariableNode parent, string name)
            {
                Task opened = view.Tree.Expand(parent);
                Ui.WaitFor(() => opened.IsCompleted, $"{parent.Name}'s rows");
                return parent.Children?.FirstOrDefault(c => c.Name == name)
                    ?? throw new InvalidOperationException($"{parent.Name} has no row {name}: {string.Join(", ", (parent.Children ?? []).Select(c => c.Name))}");
            }
            VariableNode top = Row(window, local)!;
            VariableNode row = Child(Child(top, range), element);
            ListBox list = window.DebugWindow.VariablesList;
            Ui.Click(Ui.Row(list, row), settle: false);
            Ui.Press(window, "F2");
            Assert.True(row.IsEditing, $"F2 on {local}'s {element} did not open its value for editing");
            window.KeyTextInput(value);
            Ui.Press(window, "Enter");
            Ui.WaitFor(() => Row(window, local) is { } again && again != top, "the variables shown again once the value was set");
        }
    }

    [AvaloniaFact]
    public void An_evaluation_that_could_not_be_stopped_leaves_the_program_paused_and_a_step_goes_on_once_it_returns()
    {
        // A getter that reads input the program has not been given: an evaluation of it cannot be stopped, and runs on.
        Write("Program.cs", "Counted counted = new();\nint total = 0;\nfor (int i = 1; i <= 3; i++)\n{\n    total += i;\n}\nConsole.WriteLine($\"total {total} {counted.Count}\");\n\n"
            + "static class Waits\n{\n    public static string Input => Console.ReadLine() ?? \"none\";\n}\n\n"
            + "sealed class Counted\n{\n    public int Count => 1;\n}\n");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(4, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => Value(window, "i") == "1", "the stop in the loop");
        Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;

        Evaluate(window, "Waits.Input");
        Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Name == "Waits.Input" && r.Value.Contains("could not be stopped", StringComparison.Ordinal)),
            "the debugger to give up on the evaluation");
        Assert.True(window.Debugger.Session?.State == DebugState.Paused, "The program was left running while the IDE showed it paused");
        Assert.Contains(new LineMark(4, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.NotEmpty(view.Frames);
        // The locals read after it are the stop's: the evaluation's frames, left on its thread above them, are not.
        Assert.True(Value(window, "i") == "1", $"After the evaluation Variables showed {string.Join(", ", view.Tree.Rows.Select(r => $"{r.Name} = {r.Value}"))}: the evaluation's frames were taken for the stop's");
        // No getter runs while it does, though what needs no code run in the program evaluates.
        Assert.True(Member(window, "counted", "Count").StartsWith("Implicit evaluation is disabled", StringComparison.Ordinal),
            "A getter ran while an evaluation that could not be stopped still ran");
        Evaluate(window, "i + 1");
        Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Name == "i + 1"), "the sum evaluated");
        Assert.Equal("2", view.Tree.Rows.First(r => r.Name == "i + 1").Value);

        Ui.Run(window, AppActions.StepOver);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Running || window.Status?.StartsWith("Could not", StringComparison.Ordinal) == true,
            "the step to be taken");
        Assert.True(window.Debugger.Session?.State == DebugState.Running, $"The step was refused: {window.Status}");
        _ = window.Debugger.Session!.InputAsync("typed\n");
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && editor.LineMarks.Contains(new LineMark(5, LineMarkKind.ExecutionPoint)),
            "the step to end at the next line");
        Ui.WaitFor(() => Value(window, "total") == "1", "the stop's locals");
        // The program has gone on since, so getters run again.
        Assert.Equal("1", Member(window, "counted", "Count"));
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();

        static void Evaluate(MainWindow window, string expression)
        {
            Ui.Run(window, AppActions.Evaluate);
            window.KeyTextInput(expression);
            Ui.Press(window, "Enter");
        }

        static string Member(MainWindow window, string local, string member)
        {
            VariableNode row = Row(window, local) ?? throw new InvalidOperationException($"Variables shows no {local}");
            Task opened = window.BottomPanel.Debug.Tree.Expand(row);
            Ui.WaitFor(() => opened.IsCompleted, $"{local}'s members");
            return row.Children?.FirstOrDefault(c => c.Name == member)?.Value
                ?? throw new InvalidOperationException($"{local} has no member {member}: {string.Join(", ", (row.Children ?? []).Select(c => c.Name))}");
        }
    }

    /// <summary>Continue pressed while a stop's values are still being read (a watch, a result and a ToString that each
    /// run 1.5 s in the program) shows the program running at once and sets it going at once: its other thread ticks
    /// again within 100 ms, and the evaluation under way ends in the program, which goes on from it to the next
    /// stop.</summary>
    [AvaloniaFact]
    public void Continue_pressed_while_a_stops_values_are_read_shows_the_program_running_at_once_and_sets_it_going()
    {
        string ticks = Path.Combine(Fresh(), "ticks.txt");
        Write("Program.cs", "Slow slow = new();\n"
            + $"new Thread(() =>\n{{\n    for (long n = 1; ; n++)\n    {{\n        try\n        {{\n            File.WriteAllText(@\"{ticks}\", n.ToString());\n        }}\n        catch (IOException)\n        {{\n        }}\n        Thread.Sleep(5);\n    }}\n}}) {{ IsBackground = true }}.Start();\n"
            + "int total = 0;\nfor (int i = 1; i <= 3; i++)\n{\n    total += i;\n}\nConsole.WriteLine($\"total {total}\");\n\n"
            + "sealed class Slow\n{\n    public int Getter => Spin(1500);\n\n    public override string ToString() => $\"slow {Spin(1500)}\";\n\n"
            + "    static int Spin(int ms)\n    {\n        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();\n"
            + "        while (clock.ElapsedMilliseconds < ms)\n        {\n        }\n        return ms;\n    }\n}\n");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(18, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => Value(window, "i") == "1", "the first stop's locals");
        Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
        Ui.Run(window, AppActions.Evaluate);
        window.KeyTextInput("slow.Getter");
        Ui.Press(window, OperatingSystem.IsMacOS() ? "meta shift Enter" : "control shift Enter");
        Watched(view, "slow.Getter", "1500", "the slow watch");
        Ui.Run(window, AppActions.Evaluate);
        window.KeyTextInput("slow.Getter + 1");
        Ui.Press(window, "Enter");
        Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Role == VariableRole.Result && r.Value == "1501"), "the slow result");

        Ui.Run(window, AppActions.Resume);
        WaitDebugging(window, () => window.Debugger.Stop is not null, "the second stop's frames");
        DebugSession session = window.Debugger.Session!;
        long before = Ticks();
        Stopwatch pressed = Stopwatch.StartNew();
        Assert.True(window.Perform(AppActions.Resume), $"Resume was refused at the stop: {window.Status}");
        Assert.True(session.State == DebugState.Running && !editor.LineMarks.Any(m => m.Kind == LineMarkKind.ExecutionPoint)
            && window.Status?.EndsWith(": running", StringComparison.Ordinal) == true,
            $"Continue left the window showing the stop: the session was {session.State} and the status bar said \"{window.Status}\"");
        Ui.PumpUntil(() => Ticks() > before, "the program to run on", TimeSpan.FromSeconds(30));
        TimeSpan ran = pressed.Elapsed;
        Assert.True(ran < TimeSpan.FromMilliseconds(100).Scaled(),
            $"The program ran on {ran.TotalMilliseconds:0} ms after Continue was pressed: the debugger waited for the stop's evaluations");
        WaitDebugging(window, () => Value(window, "i") == "3", "the third stop's locals, once the evaluation left running had ended");
        Assert.Equal("3", Value(window, "total"));
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();

        long Ticks() => long.TryParse(Ui.Shared(ticks), out long n) ? n : 0;
    }

    [AvaloniaFact]
    public void An_exception_the_debugger_cannot_describe_still_shows_where_the_program_stopped()
    {
        // Its Source outlasts the debugger's wait for code it runs, so the description reading it is given up.
        Write("Program.cs", "int index = 5;\nthrow new Slow();\n\nsealed class Slow : Exception\n{\n"
            + "    public override string? Source { get { Thread.Sleep(8000); return \"late\"; } set { } }\n}\n");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        StartDebugging(window);
        WaitDebugging(window, () => window.BottomPanel.Debug.Frames.Count > 0, "the stop's frames, though its exception is not described");
        Assert.Equal(2, window.BottomPanel.Debug.Frames[0].Line);
        Assert.Contains(new LineMark(1, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Ui.WaitFor(() => Value(window, "index") == "5", "the stop's locals");
        Ui.WaitFor(() => window.Status?.Contains("could not describe", StringComparison.Ordinal) == true, "why the exception is not named in the status bar");
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    [AvaloniaFact]
    public void A_value_from_the_stop_before_a_step_opens_to_an_error_never_to_another_values_members()
    {
        Write("Program.cs", "static class Program\n{\n    static void Main()\n    {\n        Inner inner = new();\n        Visited visited = new();\n"
            + "        Visit(visited);\n        Console.WriteLine(Console.ReadLine());\n    }\n\n    static void Visit(Visited visited)\n    {\n"
            + "        Console.WriteLine(visited.VisitedField);\n    }\n}\n\nsealed class Inner\n{\n    public int InnerField = 1;\n}\n\n"
            + "sealed class Visited\n{\n    public string VisitedField = \"v\";\n}\n");
        MainWindow window = Open();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(6, 8));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => Row(window, "inner") is not null, "the stop before the call");
        Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
        VariableNode stale = Row(window, "inner")!;
        Assert.True(stale.HasChildren);

        Ui.Run(window, AppActions.StepInto);
        Ui.WaitFor(() => Row(window, "inner") is null && Row(window, "visited") is not null, "the stop in the method called");
        // The row the last stop showed, opened as a click on it would: what it named is gone with that stop.
        Task opened = view.Tree.Expand(stale);
        Ui.WaitFor(() => opened.IsCompleted, "the row's members to be asked for");
        Assert.DoesNotContain(stale.Children ?? [], c => c.Name == "VisitedField");
        VariableNode refused = Assert.Single(stale.Children ?? []);
        Assert.Equal(VariableRole.Error, refused.Role);
        Assert.Contains("earlier stop", refused.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(view.Tree.Rows, r => r.Parent == stale);

        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Running && view.Tree.Rows.Count == 0 && view.Frames.Count == 0,
            "the last stop's frames and values to be cleared while the program runs");
        Assert.Null(view.SelectedFrame);
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    /// <summary>The middle of a line's number in the gutter, in the window's coordinates.</summary>
    private static Point Gutter(MainWindow window, CodeEditor editor, int line)
    {
        Point bottom = editor.GutterPoint(line);
        return editor.TranslatePoint(new Point(bottom.X / 2, bottom.Y - 8), window)!.Value;
    }

    /// <summary>Where a line's execution arrow is drawn, right of its number, in the window's coordinates.</summary>
    private static Point Arrow(MainWindow window, CodeEditor editor, int line) =>
        editor.TranslatePoint(editor.GutterColumnPoint(GutterColumn.Marks, line), window)!.Value;

    /// <summary>A top-level row of the Variables view, by name.</summary>
    private static string? Value(MainWindow window, string name) => Row(window, name)?.Value;

    private static VariableNode? Row(MainWindow window, string name) =>
        window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name);

    /// <summary>What is typed into the Debug console is the program's input; and Pause stops the program where it runs on in
    /// its loop, shown there.</summary>
    [AvaloniaFact]
    public void What_is_typed_into_the_debug_console_is_the_programs_input_and_pause_stops_it_where_it_is_in_its_loop()
    {
        Write("Program.cs", "Console.Write(\"Name? \");\nstring? name = Console.ReadLine();\nConsole.WriteLine($\"read: {name}\");\n"
            + "long turns = 0;\nConsole.WriteLine(\"looping\");\nwhile (true)\n{\n    turns++;\n}\n");
        MainWindow window = Open();
        try
        {
            StartDebugging(window);
            Passenger.App.ToolWindows.OutputView console = window.BottomPanel.Debug.Console;
            Ui.WaitFor(() => console.Document.Snapshot.ToString().Contains("Name? ", StringComparison.Ordinal), "the program to ask");
            Ui.TypeToDebugged(window, "Ada");
            WaitDebugging(window, () => console.Document.Snapshot.ToString().Contains("looping", StringComparison.Ordinal), "the program to read the line and go on to its loop");
            Assert.Contains("Name? Ada\nread: Ada\n", console.Document.Snapshot.ToString(), StringComparison.Ordinal);

            window.Perform(AppActions.Pause);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.BottomPanel.Debug.Frames.Count > 0, "the pause to be shown");
            Assert.InRange(window.BottomPanel.Debug.Frames[0].Line, 6, 9);
            Assert.Contains(window.EditorArea.CodeEditor.LineMarks, m => m.Kind == LineMarkKind.ExecutionPoint && m.Line is >= 5 and <= 8);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Debugging_a_program_that_prints_100000_coloured_lines_leaves_the_console_free_of_colour_codes_and_the_window_responsive()
    {
        Write("Program.cs", "for (int i = 0; i < 100_000; i++)\n{\n    Console.WriteLine($\"\\u001b[3{i % 7 + 1}mline {i}\\u001b[0m\");\n}\nConsole.WriteLine(\"printed\");\n");
        MainWindow window = Open();
        Passenger.App.ToolWindows.OutputView console = window.BottomPanel.Debug.Console;
        string Text() => console.Document.Snapshot.ToString();
        try
        {
            // A key's wait, as the system would deliver one: a job at input's priority, posted from another thread now and then.
            Avalonia.Threading.Dispatcher dispatcher = Avalonia.Threading.Dispatcher.UIThread;
            List<double> waits = [];
            using Timer keys = new(_ =>
            {
                long posted = Stopwatch.GetTimestamp();
                dispatcher.Post(() =>
                {
                    lock (waits)
                    {
                        waits.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
                    }
                }, Avalonia.Threading.DispatcherPriority.Input);
            }, null, 0, 10);
            double Worst()
            {
                lock (waits)
                {
                    double worst = waits.Count == 0 ? 0 : waits.Max();
                    waits.Clear();
                    return worst;
                }
            }
            Ui.Hold(TimeSpan.FromSeconds(1), "a second of the window at rest, against which the keys' waits while the program writes are held");
            double atRest = Worst();

            // When the console's text grew, from the debugger's start to the program's last line.
            List<long> grew = [];
            console.Document.Changed += (_, change) =>
            {
                if (change.Edits.Any(e => e.Text.Length > 0))
                {
                    grew.Add(Stopwatch.GetTimestamp());
                }
            };
            window.Perform(AppActions.Debug);
            Ui.PumpUntil(() => Text().Contains("printed", StringComparison.Ordinal), "the program to print its last line", TimeSpan.FromMinutes(2));
            double whilePrinting = Worst();
            double span = Stopwatch.GetElapsedTime(grew[0], grew[^1]).TotalMilliseconds;

            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the program to end");
            string text = Text();
            Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[0m", text, StringComparison.Ordinal);
            Assert.Contains("\nline 99999\nprinted\n", text, StringComparison.Ordinal);
            Assert.True(text.Length <= Passenger.App.ToolWindows.OutputView.DefaultLimitKb * 1024, $"The console kept {text.Length} characters");
            // A batch a frame at most, however many writes the program made and however many events the debugger sent.
            Assert.True(grew.Count <= span / 16 + 10, $"The console took the output in {grew.Count} pieces over {span:0} ms, more than one a frame");
            Assert.True(whilePrinting < Math.Max(500 * TestRun.TimeScale, 20 * atRest),
                $"A key waited up to {whilePrinting:0} ms while the program printed, against {atRest:0} ms at rest");
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_debugger_that_dies_with_an_exception_leaves_the_window_up_with_its_words_in_the_console_and_the_log()
    {
        // A folder in the engine log's place kills the adapter as it starts, its stack trace on standard error.
        string folderForLog = TestFolders.Create("engine-log-");
        string? engineLog = Environment.GetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable);
        Environment.SetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable, folderForLog);
        try
        {
            MainWindow window = Open();
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => Ui.IdeLog().Contains("Passenger's debugger (process", StringComparison.Ordinal),
                "the debugger's end in the IDE's log");

            Assert.True(window.IsVisible, "The IDE went down with its debugger");
            Assert.False(window.Debugger.IsDebugging);
            Assert.Contains("Could not debug", window.Status, StringComparison.Ordinal);
            // Every line of the stack trace, to its last frame: a line that failed on the wrong thread would end the reading.
            string console = window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.Contains("Unhandled exception. System.UnauthorizedAccessException", console, StringComparison.Ordinal);
            Assert.Contains("at Program.<Main>", console, StringComparison.Ordinal);
            string log = Ui.IdeLog();
            Assert.Contains("UnauthorizedAccessException", log, StringComparison.Ordinal);
            Assert.Contains("at Program.<Main>", log, StringComparison.Ordinal);
            string debuggerLog = Ui.Shared(Path.Combine(IdeErrors.LogFolder, "dap.log"));
            Assert.Contains("Unhandled exception. System.UnauthorizedAccessException", debuggerLog, StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable, engineLog);
        }
    }

    [AvaloniaFact]
    public void A_debugger_killed_while_its_program_is_paused_takes_the_program_with_it()
    {
        string started = Path.Combine(TestFolders.Create("debuggee-"), "program.pid");
        MainWindow window = OpenSleeper(started, languageServer: false);
        int debuggee = 0;
        int adapter = 0;
        try
        {
            window.EditorArea.CodeEditor.GoTo(new TextPosition(1, 0));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            StartDebugging(window);
            WaitDebugging(window, () => window.Debugger.Session?.State == DebugState.Paused, "the program to stop at the breakpoint");
            debuggee = Running(window, started);
            adapter = window.Debugger.Session!.AdapterProcessId;

            // The debugger alone, as a crash ends it: what it started is left to whatever ends it next.
            using (Process debugger = Process.GetProcessById(adapter))
            {
                debugger.Kill();
            }
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end with its debugger");
            Assert.True(Processes.Gone(debuggee), "The debugger died and the IDE left the program it launched running, held at its breakpoint, until the IDE closed");
            Assert.Contains("the debugger itself stopped", window.Status, StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            Processes.Kill(debuggee);
            Processes.Kill(adapter);
        }
    }

    [AvaloniaFact]
    public void Closing_the_window_while_debugging_leaves_no_program_debugger_or_language_server_running()
    {
        string started = Path.Combine(TestFolders.Create("debuggee-"), "program.pid");
        // A server of the window's own, which its close is to end, not one it would leave for the next window.
        MainWindow window = OpenSleeper(started, languageServer: true);
        int debuggee = 0;
        int adapter = 0;
        int server = window.CSharpService?.Session?.Server.ProcessId ?? throw new InvalidOperationException("The language server did not start");
        try
        {
            StartDebugging(window);
            debuggee = Running(window, started);
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Running }, "the session to say the program runs");
            adapter = window.Debugger.Session!.AdapterProcessId;
            window.Close();

            // The IDE's process waits for these before it ends, a few seconds at most.
            Assert.True(window.Ended?.Wait(MainWindow.EndLimit + TimeSpan.FromSeconds(5)), "The window closed without ending what it started within its limit, so the IDE's process would not end");
            Assert.True(Processes.Gone(debuggee), "After the window closed, the program being debugged went on running");
            Assert.True(Processes.Gone(adapter), "After the window closed, the debugger went on running");
            Assert.True(Processes.Gone(server), "After the window closed, the language server went on running until the IDE's own end");
        }
        finally
        {
            Processes.Kill(debuggee);
            Processes.Kill(adapter);
        }
    }

    /// <summary>Run as a window test is, on a dispatcher that goes when its run ends, and looked at after: whatever the
    /// window left waiting on that dispatcher is never come back to, and a request it waited on that failed later would be
    /// an error for the finalizer to report into whichever test ran then.</summary>
    [Fact]
    public async Task Stop_and_the_windows_close_on_a_program_the_debugger_cannot_end_leave_no_failed_request_once_the_windows_loop_is_gone()
    {
        string started = Path.Combine(TestFolders.Create("debuggee-"), "program.pid");
        List<DapException> unheard = [];
        EventHandler<UnobservedTaskExceptionEventArgs> finalized = (_, e) =>
        {
            lock (unheard)
            {
                unheard.AddRange(e.Exception.InnerExceptions.OfType<DapException>());
            }
        };
        TaskScheduler.UnobservedTaskException += finalized;
        int debuggee = 0;
        int adapter = 0;
        try
        {
            // A failure comes back as the run's answer: thrown, it would end this test on the window's thread (below).
            (int Program, int Debugger, Task? Ended, Exception? Failed) run = await HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DebuggerTests).Assembly).Dispatch(() =>
            {
                // As every window test starts and ends (TestsLeaveNothingBehind), its window closed however the run ends.
                BackgroundWork.Forget();
                OpenWindows.Watch();
                try
                {
                    // No language server: nothing it starts on this dispatcher is left for a later window to take up.
                    MainWindow window = OpenSleeper(started, languageServer: false);
                    StartDebugging(window);
                    int program = Running(window, started);
                    Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Running }, "the session to say the program runs");
                    int debugger = window.Debugger.Session!.AdapterProcessId;
                    // Stopped where it stands, it answers neither Stop nor the close, which kills both after its limit.
                    Processes.Suspend(program, "The program");
                    window.Perform(AppActions.Stop);
                    window.Close();
                    return (program, debugger, window.Ended, (Exception?)null);
                }
                catch (Exception failed)
                {
                    OpenWindows.CloseAll();
                    return (0, 0, null, failed);
                }
            }, TestContext.Current.CancellationToken);
            // Off the window's thread, where the session ends a run: a test ending there holds it in the runner's wait for the next.
            await Task.Yield();
            if (run.Failed is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(run.Failed);
            }
            (debuggee, adapter) = (run.Program, run.Debugger);
            Task ended = run.Ended ?? throw new InvalidOperationException("The window closed with no end to wait for");

            Assert.True(await Waiting.Within(ended, MainWindow.EndLimit + TimeSpan.FromSeconds(5).Scaled(), TestContext.Current.CancellationToken, scaled: false),
                "The window closed without ending what it started within its limit, so the IDE's process would not end");
            Assert.True(Processes.Gone(adapter), "After the window closed, the debugger went on running");
            Assert.True(Processes.Gone(debuggee), "After the window closed, the program being debugged went on running");
            // A task nobody came back for is heard of when the collector finalizes it.
            for (int i = 0; i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Waiting.For(TimeSpan.FromMilliseconds(100), "for a task nobody came back for to be finalized and heard of, were there one");
            }
            lock (unheard)
            {
                Assert.True(unheard.Count == 0, "A request to the debugger failed once the window's loop was gone, with nothing left to hear it, so the IDE "
                    + $"would report it as an error into whatever ran next: {string.Join("; ", unheard.Select(e => e.Message))}");
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= finalized;
            Processes.Kill(debuggee);
            Processes.Kill(adapter);
        }
    }

    [AvaloniaFact]
    public void Debug_tests_with_a_test_host_the_debugger_cannot_attach_to_ends_the_session_saying_why()
    {
        // What VSTest hands the IDE to attach to, here a host with no .NET in it.
        using Process host = Debugging.Helpers.Debuggees.NotDotnet();
        try
        {
            MainWindow window = Open();
            Task<bool> attached = window.Tests.AttachDebugger!(host.Id);
            Ui.WaitFor(() => attached.IsCompleted, "the attach to give up", TimeSpan.FromSeconds(30));

            Assert.False(attached.Result, "The IDE told VSTest the debugger had hold of a test host it could not attach to");
            Assert.False(window.Debugger.IsDebugging, "The session went on after its attach failed, saying the tests run");
            Assert.Contains($"Could not debug Tests: Could not attach to process {host.Id}", window.Status, StringComparison.Ordinal);
            Assert.Contains("not a .NET program", window.Status, StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            Processes.Kill(host.Id);
        }
    }

    /// <summary>A program started outside Passenger, as a terminal starts one, waiting for a line: Attach to Process
    /// lists it, its process id typed narrows the list to the programs that show it (its own, and any whose id or
    /// command line has it inside), its row chosen and Enter attaches; Detach lets it go still waiting, and Reattach to
    /// Process takes hold of it again; then the line it reads brings it to the breakpoint, and Detach leaves it running
    /// on past the breakpoint to its end, reading and writing as it would have undebugged.</summary>
    [AvaloniaFact]
    public void Attach_to_process_stops_a_program_started_outside_passenger_at_a_breakpoint_it_then_reaches_and_detach_leaves_it_running_to_its_end()
    {
        string source = PathNames.Canonical(Debugging.Helpers.Debuggees.Source("ReadsInputConsoleApp", "Program.cs"));
        int line = Debugging.Helpers.Debuggees.Line("ReadsInputConsoleApp", "Console.WriteLine") - 1;
        ProcessStartInfo start = ChildProcesses.Command(Debugging.Helpers.Debuggees.Apphost("ReadsInputConsoleApp"), []);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        using Process program = ChildProcesses.Start(start);
        Task<string> output = program.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            // Running, as a program a person attaches to is: its runtime has opened what a debugger attaches through.
            Ui.WaitFor(() => DotnetProcesses.List().Any(p => p.Id == program.Id), "the program's runtime to start");
            MainWindow window = new() { Width = 1200, Height = 800 };
            window.Show();
            window.OpenFile(source);
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(line, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);

            AttachToProcessDialog dialog = OpenAttachToProcess(window);
            DotnetProcess listed = Assert.Single(dialog.Shown, p => p.Id == program.Id);
            Assert.Equal(OperatingSystem.IsWindows() ? "ReadsInputConsoleApp.exe" : "ReadsInputConsoleApp", listed.Name);
            Assert.Contains("ReadsInputConsoleApp", listed.CommandLine, StringComparison.Ordinal);
            Assert.True(listed.IsMine && listed.User.Length > 0, $"The program the person started was listed as {(listed.IsMine ? "theirs" : "another user's")}, user '{listed.User}'");
            ChooseById(dialog, program.Id);
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Running }, "the debugger to attach to the program");
            Assert.Empty(window.OwnedWindows.OfType<AttachToProcessDialog>());
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Debug), "Attaching left the Debug window closed");
            Assert.Equal("ReadsInputConsoleApp: running", window.Status);

            Assert.True(window.Perform(AppActions.Detach), "Detach could not run on the program attached to");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let go of the program");
            Assert.Equal("Detached from ReadsInputConsoleApp, which runs on", window.Status);
            Assert.False(program.HasExited, "Detach ended the program it let go of");
            Ui.Run(window, AppActions.ReattachToProcess);
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Running }, "Reattach to Process to attach to the program again");

            program.StandardInput.WriteLine("hello");
            program.StandardInput.Flush();
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Value(window, "line") is not null, "the program to stop at the breakpoint");
            Assert.Equal("\"hello\"", Value(window, "line"));
            Assert.Contains(new LineMark(line, LineMarkKind.ExecutionPoint), editor.LineMarks);

            Assert.True(window.Perform(AppActions.Detach), "Detach could not run on the paused program");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugger to let go of the paused program");
            Assert.True(program.WaitForExit(TimeSpan.FromSeconds(30)), "The program let go of at its breakpoint never ran on to its end");
            if (program.ExitCode != 0 && OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
            {
                // Its copy with CET's shadow stacks off, let go the same way, says whether they are what ended it.
                string unshadowed = LetGoAtBreakpoint(window, "ReadsInputConsoleAppNoCet");
                Assert.Fail($"The program let go of at its breakpoint ended with exit code 0x{program.ExitCode:X8}; its copy built with CetCompat=false, let go the same way, {unshadowed}");
            }
            Assert.Equal(0, program.ExitCode);
            Assert.Equal("read: hello", output.Result.Trim());
            Assert.DoesNotContain(editor.LineMarks, m => m.Kind == LineMarkKind.ExecutionPoint);
            window.Close();
        }
        finally
        {
            Processes.Kill(program.Id);
        }
    }

    /// <summary>Attach to Process lists the .NET programs running and nothing else: not a program with no .NET in it, not
    /// Passenger itself, and not a process that took the number of a .NET program which crashed and left its debugger
    /// pipes behind; what the search finds of none says so.</summary>
    [AvaloniaFact]
    public void Attach_to_process_lists_dotnet_programs_alone()
    {
        using Process other = Debugging.Helpers.Debuggees.NotDotnet();
        ProcessStartInfo start = ChildProcesses.Command(Debugging.Helpers.Debuggees.Apphost("LoopingConsoleApp"), ["sleep"]);
        start.RedirectStandardOutput = true;
        using Process looping = ChildProcesses.Start(start);
        // What a .NET program that crashed would leave in the temporary folder, under a number the other has since.
        string left = Path.Combine(Path.GetTempPath(), $"clr-debug-pipe-{other.Id}-1-in");
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(left, string.Empty);
        }
        try
        {
            Assert.Equal("looping", looping.StandardOutput.ReadLine());
            MainWindow window = new() { Width = 1200, Height = 800 };
            window.Show();
            Ui.Settle();
            AttachToProcessDialog dialog = OpenAttachToProcess(window);
            Assert.Contains(dialog.Shown, p => p.Id == looping.Id && p.Program == "LoopingConsoleApp");
            Assert.DoesNotContain(dialog.Shown, p => p.Id == other.Id);
            Assert.DoesNotContain(dialog.Shown, p => p.Id == Environment.ProcessId);

            // Other programs may show its id, so the search's word for none is asked with words no program shows.
            dialog.KeyTextInput(other.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Ui.Settle();
            Assert.DoesNotContain(dialog.Shown, p => p.Id == other.Id);
            string nothing = $"no-program-{Guid.NewGuid():N}";
            dialog.Search.SelectAll();
            dialog.KeyTextInput(nothing);
            Ui.Settle();
            Assert.Empty(dialog.Shown);
            Assert.Equal($"No .NET process matches '{nothing}'", dialog.Placeholder);
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Ui.Settle();
            Assert.Empty(window.OwnedWindows.OfType<AttachToProcessDialog>());
            Assert.False(window.Debugger.IsDebugging, "Cancelling Attach to Process attached to something");
            window.Close();
        }
        finally
        {
            File.Delete(left);
            Processes.Kill(looping.Id);
            Processes.Kill(other.Id);
        }
    }

    /// <summary>A program attached to ran before Passenger did: closing the window lets go of it, as Detach does, and it
    /// runs on, where a program the debugger started ends with the window.</summary>
    [AvaloniaFact]
    public void Closing_the_window_while_attached_lets_go_of_the_program_which_runs_on()
    {
        ProcessStartInfo start = ChildProcesses.Command(Debugging.Helpers.Debuggees.Apphost("LoopingConsoleApp"), ["sleep"]);
        start.RedirectStandardOutput = true;
        using Process looping = ChildProcesses.Start(start);
        int adapter = 0;
        try
        {
            Assert.Equal("looping", looping.StandardOutput.ReadLine());
            MainWindow window = new() { Width = 1200, Height = 800 };
            window.Show();
            Ui.Settle();
            AttachToProcessDialog dialog = OpenAttachToProcess(window);
            dialog.Choose(looping.Id);
            dialog.Attach();
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Running }, "the debugger to attach to the program");
            adapter = window.Debugger.Session!.AdapterProcessId;
            window.Close();

            Assert.True(window.Ended?.Wait(MainWindow.EndLimit + TimeSpan.FromSeconds(5)), "The window closed without letting go of what it attached to within its limit");
            Assert.True(Processes.Gone(adapter), "After the window closed, the debugger went on running");
            Assert.False(looping.WaitForExit(TimeSpan.FromSeconds(2)), "Closing the window ended a program it had only attached to");
        }
        finally
        {
            Processes.Kill(looping.Id);
            Processes.Kill(adapter);
        }
    }

    /// <summary>A debuggee built from ReadsInputConsoleApp's Program.cs, started outside Passenger, attached to through
    /// Attach to Process, brought by a line to the breakpoint the window has on its source and let go there with
    /// Detach: how it ended, in words for a failure message.</summary>
    private static string LetGoAtBreakpoint(MainWindow window, string debuggee)
    {
        ProcessStartInfo start = ChildProcesses.Command(Debugging.Helpers.Debuggees.Apphost(debuggee), []);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        using Process program = ChildProcesses.Start(start);
        Task<string> output = program.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            Ui.WaitFor(() => DotnetProcesses.List().Any(p => p.Id == program.Id), $"{debuggee}'s runtime to start");
            AttachToProcessDialog dialog = OpenAttachToProcess(window);
            ChooseById(dialog, program.Id);
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Running }, $"the debugger to attach to {debuggee}");
            program.StandardInput.WriteLine("hello");
            program.StandardInput.Flush();
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused, $"{debuggee} to stop at the breakpoint");
            Assert.True(window.Perform(AppActions.Detach), $"Detach could not run on {debuggee}");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, $"the debugger to let go of {debuggee}");
            return program.WaitForExit(TimeSpan.FromSeconds(30))
                ? $"ended with exit code 0x{program.ExitCode:X8}, having written '{output.Result.Trim()}'"
                : "never ran on to its end";
        }
        finally
        {
            Processes.Kill(program.Id);
        }
    }

    /// <summary>A program's process id typed into Attach to Process's search, as a person finds it: the rows left are those
    /// that show it, its own among them and any other whose id or command line has it inside (10328 for 328), so its own is
    /// chosen by its id, as a person clicks it.</summary>
    private static void ChooseById(AttachToProcessDialog dialog, int processId)
    {
        dialog.KeyTextInput(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Ui.Settle();
        Assert.Contains(processId, dialog.Shown.Select(p => p.Id));
        dialog.Choose(processId);
        Assert.Equal(processId, dialog.Chosen?.Id);
    }

    /// <summary>Attach to Process from its keys, once it has listed the programs.</summary>
    private static AttachToProcessDialog OpenAttachToProcess(MainWindow window)
    {
        Ui.Run(window, AppActions.AttachToProcess);
        AttachToProcessDialog dialog = Assert.Single(window.OwnedWindows.OfType<AttachToProcessDialog>());
        Ui.WaitFor(() => dialog.Listed.IsCompleted, "Attach to Process to list the .NET programs");
        Assert.Same(dialog.Search, dialog.FocusManager?.GetFocusedElement());
        return dialog;
    }

    /// <summary>A configuration that uses the terminal is debugged there: the program starts in a tab of the Terminal
    /// window held until the debugger has hold of it, so a breakpoint on Main's first line stops it before any of it
    /// runs; let go, it reads the key and the line typed into its tab, and its end is told there and in the status bar
    /// with its exit code. Debugged again, starting a child, Stop ends it with what it started, and its tab says it was
    /// ended.</summary>
    [AvaloniaFact]
    public void Debugged_in_the_terminal_a_program_stops_on_Mains_first_line_reads_what_is_typed_there_and_stop_ends_it_and_what_it_started()
    {
        Write("Program.cs", File.ReadAllText(Debugging.Helpers.Debuggees.Source("TerminalConsoleApp", "Program.cs")));
        MainWindow window = InTerminal(string.Empty);
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(0, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.Focus();
        Ui.Run(window, AppActions.Debug);
        Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Paused or DebugState.Ended }, "the debugged program to stop at the breakpoint");
        DebugSession going = window.Debugger.Session!;
        Assert.True(going.State == DebugState.Paused, $"Debugged in the terminal, the program ended without stopping at the breakpoint on Main's first line: {window.Status}");
        Ui.WaitFor(() => window.BottomPanel.Debug.Frames.Count > 0, "the stop's frames");
        Assert.True(window.BottomPanel.Debug.Frames[0].Line == 1, $"The program stopped at line {window.BottomPanel.Debug.Frames[0].Line}, not on Main's first line");
        Passenger.App.Run.TerminalRun program = Assert.IsType<Passenger.App.Run.TerminalRun>(going.Terminal);
        Assert.True(program.IsRunning && window.TerminalPanel.Tabs.Any(t => t.Run == program && t.Name == "Sum in the terminal"),
            "The program debugged is not running in a tab of the Terminal window named for its configuration");
        Assert.DoesNotContain("started", TerminalText(program), StringComparison.Ordinal);

        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => TerminalText(program).Contains("key?", StringComparison.Ordinal), "the program to ask for a key in its tab");
        window.ShowToolWindow(ToolWindowIds.Terminal);
        window.TerminalPanel.FocusView();
        Ui.WaitFor(() => window.TerminalPanel.View.IsFocused && window.TerminalPanel.ActiveTab?.Run == program, "the keys to go to the program's tab");
        window.KeyTextInput("q");
        window.KeyTextInput("hello");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => going.State == DebugState.Ended && program.Ended.IsCompleted, "the program's end");
        string shown = TerminalText(program);
        Assert.True(shown.Contains("key? key q", StringComparison.Ordinal) && shown.Contains("line hello", StringComparison.Ordinal)
            && shown.Contains("Process finished with exit code 3.", StringComparison.Ordinal), $"The program's tab shows:\n{shown}");
        Assert.Equal(3, going.ExitCode);
        Ui.WaitFor(() => window.Status == "Debugging Sum in the terminal ended with exit code 3", $"the status bar to say how it ended, not \"{window.Status}\"");

        editor.GoTo(new TextPosition(0, 0));
        editor.Focus();
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Assert.Empty(window.Debugger.Breakpoints.In(Program));
        window.SetConfigurations([new Passenger.App.Run.RunConfiguration
        {
            Name = "Sum in the terminal",
            Project = Path.Combine(PathNames.Canonical(Folder), "Sum.csproj"),
            Arguments = "child",
            Console = Passenger.App.Run.ConsoleKind.Terminal,
        }], "Sum in the terminal");
        StartDebugging(window);
        WaitDebugging(window, () => window.Debugger.Session is { } next && next != going && next.Terminal is { } started && TerminalText(started).Contains("key?", StringComparison.Ordinal),
            "the program to start its child and ask for a key");
        Passenger.App.Run.TerminalRun run = window.Debugger.Session!.Terminal!;
        string said = TerminalText(run).Split('\n').First(l => l.StartsWith("child ", StringComparison.Ordinal));
        int child = int.Parse(said["child ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Processes.IsRunning(child), "The program's child is not running");

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging && run.Ended.IsCompleted, "the session to end");
        Assert.True(Processes.Gone(run.ProcessId), $"Stop left the program running: {Processes.Describe(run.ProcessId)}");
        Assert.True(Processes.Gone(child), $"Stop left the program's child running: {Processes.Describe(child)}");
        Assert.Contains("Process finished with exit code ", TerminalText(run), StringComparison.Ordinal);
        Assert.False(window.FindControl<Avalonia.Controls.Button>("StopButton")!.IsEnabled, "The toolbar's Stop could still be clicked once the session ended");
        window.Close();
    }

    /// <summary>A breakpoint set while the debugger still waits for the program's runtime (here held at its start, a
    /// diagnostic port of the test's own), the first in a file of its own, reaches the program once it runs and stops
    /// it.</summary>
    [AvaloniaFact]
    public void A_breakpoint_set_while_debugging_starts_reaches_the_program_and_stops_it()
    {
        Write("Program.cs", "Console.ReadLine();\nOther.Run();\n");
        string other = Write("Other.cs", "static class Other\n{\n    public static void Run()\n    {\n        Console.WriteLine(\"other ran\");\n    }\n}\n");
        using Debugging.Helpers.StartHold hold = new();
        MainWindow window = Open();
        window.SetConfigurations([new Passenger.App.Run.RunConfiguration
        {
            Name = "Sum held",
            Project = Path.Combine(PathNames.Canonical(Folder), "Sum.csproj"),
            Environment = new Dictionary<string, string>(hold.Environment),
        }], "Sum held");
        Task<int> held = hold.Held(TimeSpan.FromMinutes(3));
        int program = 0;
        int adapter = 0;
        try
        {
            // Not settled: the start holds the window's background work for as long as the debugger waits for the program.
            Assert.True(window.Perform(AppActions.Debug), $"Debug did not start: {Ui.Said(window)}");
            Ui.PumpUntil(() => held.IsCompleted, "the build, then the program's runtime to come to the hold", TimeSpan.FromMinutes(3));
            program = held.Result;
            DebugSession starting = window.Debugger.Session ?? throw new InvalidOperationException($"No session while the program was held: {window.Status}");
            adapter = starting.AdapterProcessId;
            Assert.Equal(DebugState.Starting, starting.State);
            window.OpenFile(other);
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(4, 8));
            Assert.True(window.Perform(AppActions.ToggleBreakpoint), $"Toggle Breakpoint was refused: {window.Status}");

            Task letGo = hold.LetGo();
            Ui.PumpUntil(() => letGo.IsCompleted && starting.State != DebugState.Starting, "the program to run", TimeSpan.FromSeconds(60));
            WaitDebugging(window, () => editor.LineMarks.Contains(new LineMark(4, LineMarkKind.BreakpointBound)),
                "the breakpoint set while the debugger started to be bound in the program");
            _ = starting.InputAsync("go\n");
            WaitDebugging(window, () => starting.State == DebugState.Paused && editor.LineMarks.Contains(new LineMark(4, LineMarkKind.ExecutionPoint)),
                "the program to stop at the breakpoint set while it started");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
            window.Close();
        }
        finally
        {
            Processes.Kill(program);
            Processes.Kill(adapter);
        }
    }

    /// <summary>A debug session is Stop's from its first moment: one whose program's runtime is slow to start (here
    /// held at its start, a diagnostic port of the test's own) shows Stop on the toolbar and in the Debug window while
    /// the debugger waits, and Stop ends it at once, the program and the debugger with it, the Debug window and the
    /// status bar saying it was stopped.</summary>
    [AvaloniaFact]
    public void Stop_ends_a_debug_session_whose_program_is_still_starting_and_the_program_with_it()
    {
        using Debugging.Helpers.StartHold hold = new();
        MainWindow window = Open();
        window.SetConfigurations([new Passenger.App.Run.RunConfiguration
        {
            Name = "Sum held",
            Project = Path.Combine(PathNames.Canonical(Folder), "Sum.csproj"),
            Environment = new Dictionary<string, string>(hold.Environment),
        }], "Sum held");
        Task<int> held = hold.Held(TimeSpan.FromMinutes(3));
        int program = 0;
        int adapter = 0;
        try
        {
            // Not settled: the start holds the window's background work for as long as the debugger waits for the program.
            Assert.True(window.Perform(AppActions.Debug), $"Debug did not start: {Ui.Said(window)}");
            Ui.PumpUntil(() => held.IsCompleted, "the build, then the program's runtime to come to the hold", TimeSpan.FromMinutes(3));
            program = held.Result;
            Assert.True(window.Debugger.Session is not null, $"The debugger waited for the program to start with no session for Stop to end: the status bar said \"{window.Status}\"");
            DebugSession starting = window.Debugger.Session!;
            adapter = starting.AdapterProcessId;
            Assert.Equal(DebugState.Starting, starting.State);
            Assert.True(window.FindControl<Button>("StopButton")!.IsEnabled, "The toolbar's Stop could not be clicked while the debugger waited for the program to start");
            Button stop = window.DebugWindow.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Stop", StringComparison.Ordinal));
            Assert.True(stop.IsEnabled, "The Debug window's Stop was greyed while the debugger waited for the program to start");

            // Not settled: without Stop the start would hold the background work for good.
            Ui.Click(stop, settle: false);
            try
            {
                Ui.PumpUntil(() => window.Debugger.Session is null && !Processes.IsRunning(program), "Stop to end the starting session and its program", TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException waited)
            {
                throw new TimeoutException($"{waited.Message}. {Debugged(window)}", waited);
            }
            Assert.True(Processes.Gone(adapter), $"Stop left the debugger running: {Processes.Describe(adapter)}");
            Assert.Equal("Debugging Sum held was stopped as it started", window.Status);
            Assert.Contains("Debugging Sum held was stopped as it started", window.BottomPanel.Debug.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
            Assert.False(window.FindControl<Button>("StopButton")!.IsEnabled, "The toolbar's Stop could still be clicked once the session ended");
            Assert.False(stop.IsEnabled, "The Debug window's Stop stayed once the session ended");
            window.Close();
        }
        finally
        {
            Processes.Kill(program);
            Processes.Kill(adapter);
        }
    }

    /// <summary>The Run window's Rerun ends only what Run started: a program debugged meanwhile, paused at a breakpoint, stays
    /// paused in its session while Run's program is stopped and run again.</summary>
    [AvaloniaFact]
    public void The_run_windows_rerun_runs_runs_program_again_and_leaves_a_debugged_program_paused()
    {
        string runs = Path.Combine(PathNames.Canonical(Fresh()), "runs.pids");
        Write("Program.cs", "if (Environment.GetEnvironmentVariable(\"PASSENGER_PIDS\") is { } file)\n{\n" +
            "    File.AppendAllText(file, $\"{Environment.ProcessId}\\n\");\n    Thread.Sleep(Timeout.Infinite);\n}\nConsole.WriteLine(\"debugged\");\n");
        MainWindow window = Open();
        string project = Path.Combine(PathNames.Canonical(Folder), "Sum.csproj");
        List<Passenger.App.Run.RunConfiguration> configurations =
        [
            new() { Name = "Debugged", Project = project },
            new() { Name = "Ran", Project = project, Environment = new Dictionary<string, string> { ["PASSENGER_PIDS"] = runs } },
        ];
        window.SetConfigurations(configurations, "Debugged");
        window.EditorArea.CodeEditor.GoTo(new TextPosition(5, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        StartDebugging(window);
        WaitDebugging(window, () => window.Debugger.Session?.State == DebugState.Paused, "the debugged program to stop at the breakpoint");
        DebugSession debugged = window.Debugger.Session!;
        int program = debugged.ProcessStarted.Result;
        try
        {
            window.SetConfigurations(configurations, "Ran");
            Assert.True(window.Perform(AppActions.RunProgram), $"Run did not start: {Ui.Said(window)}");
            Ui.WaitFor(() => Runs().Length == 1, "Run's program to start", TimeSpan.FromMinutes(2));
            int first = Runs()[0];

            Button rerun = window.BottomPanel.Run.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Rerun", StringComparison.Ordinal));
            Ui.Click(rerun);
            Ui.WaitFor(() => Runs().Length == 2, "Run's program to run again", TimeSpan.FromMinutes(2));
            Assert.True(Processes.Gone(first), $"Rerun left Run's first program going: {Processes.Describe(first)}");
            Assert.True(window.IsRunning, "The program Rerun started again is not running");
            Assert.True(window.Debugger.Session == debugged && debugged.State == DebugState.Paused,
                $"Rerun of Run's program ended the debug session beside it: {Debugged(window)}");
            Assert.True(Processes.IsRunning(program), $"Rerun of Run's program ended the program being debugged ({program})");

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, "Stop to end the debugged program and Run's");
        }
        finally
        {
            Processes.Kill(program);
            foreach (int ran in Runs())
            {
                Processes.Kill(ran);
            }
        }
        window.Close();

        int[] Runs() => File.Exists(runs) ? [.. Ui.Shared(runs).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)] : [];
    }

    /// <summary>The window over the folder, its configuration "Sum in the terminal" debugging Sum in the terminal with these
    /// arguments, and chosen.</summary>
    private MainWindow InTerminal(string arguments)
    {
        MainWindow window = Open();
        window.SetConfigurations([new Passenger.App.Run.RunConfiguration
        {
            Name = "Sum in the terminal",
            Project = Path.Combine(PathNames.Canonical(Folder), "Sum.csproj"),
            Arguments = arguments,
            Console = Passenger.App.Run.ConsoleKind.Terminal,
        }], "Sum in the terminal");
        return window;
    }

    /// <summary>A program's tab of the Terminal window as it reads, scrollback and screen.</summary>
    private static string TerminalText(Passenger.App.Run.TerminalRun run) =>
        string.Join('\n', run.Session.Screen.Scrollback.Select(l => l.Text()).Concat(run.Session.Screen.Lines.Select(l => l.Text())));

    /// <summary>A window on the folder with its Program.cs open: on Sum, the server the last window on it left, never saving
    /// what the test types, so the build stays the one the files were built into; on the test's own folder, with no C#
    /// language server unless the test applies its edits to the program (<paramref name="hotReload"/>), which the server
    /// works out, since the debugger and the file's own parse answer the rest.</summary>
    private MainWindow Open(bool hotReload = false)
    {
        if (fresh is null)
        {
            return sum.Open(Program, languageServer: hotReload);
        }
        MainWindow window = Ui.OpenFolder(fresh, languageServer: hotReload);
        window.OpenFile(Program);
        Ui.Settle();
        return window;
    }

    /// <summary>Debug, as Alt+F5 starts it, its build and the debugger's start landed: one that was refused fails here with
    /// the status bar's words, not in a wait for a program that never ran.</summary>
    private static void StartDebugging(MainWindow window)
    {
        Ui.Run(window, AppActions.Debug);
        Assert.True(window.Debugger.Session is not null, $"Debug did not start the program: {Ui.Said(window)}");
    }

    /// <summary>Waits as <see cref="Ui.WaitFor"/> does for what a program being debugged does; one that never comes says
    /// how the debugging stood.</summary>
    private static void WaitDebugging(MainWindow window, Func<bool> condition, string what)
    {
        try
        {
            Ui.WaitFor(condition, what);
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException($"{waited.Message}. {Debugged(window)}", waited);
        }
    }

    /// <summary>The id a program being debugged wrote to a file as it began, saying how the debugging stood when none came.</summary>
    private static int Running(MainWindow window, string file)
    {
        try
        {
            return Processes.WrittenId(file, "the program to run under the debugger");
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException($"{waited.Message}. {Debugged(window)}", waited);
        }
    }

    /// <summary>The debugging as the window shows it: the session's state, the status bar and the end of the Debug console.</summary>
    private static string Debugged(MainWindow window)
    {
        string console = window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
        return $"The session is {window.Debugger.Session?.State.ToString() ?? "not started"}, the status bar says \"{window.Status}\" "
            + $"and the console ends \"{console[Math.Max(0, console.Length - 400)..]}\"";
    }

    /// <summary>A file written into the test's own folder, which it takes for a program of its own.</summary>
    private string Write(string relative, string text) => TestFolders.Write(PathNames.Canonical(Fresh()), relative, text);

    /// <summary>A window on <see cref="Sleeper"/> with its Program.cs open, which never saves what the test types, a
    /// configuration of its own (named apart from the project's generated one) giving the program the file to write its id
    /// to; with a C# language server of its own that ends with the window (<paramref name="languageServer"/>), or none.</summary>
    private MainWindow OpenSleeper(string started, bool languageServer)
    {
        string project = PathNames.Canonical(sleeper.Folder);
        MainWindow window = Ui.OpenFolder(sleeper.Folder, autosaves: false, languageServer: languageServer);
        window.OpenFile(Path.Combine(project, "Program.cs"));
        Ui.Settle();
        window.SetConfigurations([new Passenger.App.Run.RunConfiguration { Name = "Sleeper writing its id", Project = Path.Combine(project, "Sleeper.csproj"), Arguments = $"\"{started}\"" }], "Sleeper writing its id");
        return window;
    }

    /// <summary>A program that writes its id to the file its argument names, says so on its second line, where a breakpoint
    /// stops it, and sleeps for ever: the tests that end such a program share it, built once.</summary>
    public sealed class Sleeper : SharedProject
    {
        public Sleeper()
            : base("debug-sleeper-")
        {
            File.WriteAllText(PathOf("Sleeper.csproj"),
                TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
            File.WriteAllText(PathOf("Program.cs"), "File.WriteAllText(args[0], Environment.ProcessId.ToString());\nConsole.WriteLine(\"stopped here\");\nThread.Sleep(Timeout.Infinite);\n");
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
            Prepare();
        }
    }

    /// <summary>Sum as written, a loop adding one to three and the total printed, which the tests that leave it so share.</summary>
    public sealed class Sum : SharedProject
    {
        public Sum()
            : base("debug-")
        {
            WriteTo(Folder);
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
            Prepare();
        }

        public static void WriteTo(string folder)
        {
            File.WriteAllText(Path.Combine(folder, "Sum.csproj"),
                TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
            File.WriteAllText(Path.Combine(folder, "Program.cs"),
                "int total = 0;\nfor (int i = 1; i <= 3; i++)\n{\n    total += i;\n}\nConsole.WriteLine($\"total {total}\");\n");
        }
    }
}
