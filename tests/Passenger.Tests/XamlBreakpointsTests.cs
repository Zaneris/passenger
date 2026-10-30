using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>Breakpoints in an Avalonia app's XAML and on its controls' properties, debugged from the window:
/// tests/Debuggees/XamlBreakpointsApp, written into a folder of its own with a project file on the tests' own Avalonia,
/// headless, its standard input ("paint", "warn", "nudge", "rename", typed into the Debug window's console) having its code-behind
/// change its controls as something a person did would. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class XamlBreakpointsTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public XamlBreakpointsTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("xamlbreakpoints-", made => AvaloniaApp.Debuggee(made, "XamlBreakpointsApp")));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private string Xaml => Path.Combine(folder, "MainWindow.axaml");

    private string CodeBehind => Path.Combine(folder, "MainWindow.axaml.cs");

    [AvaloniaFact]
    public void A_breakpoint_on_an_axaml_element_stops_as_its_control_is_made_and_one_on_an_attribute_as_its_property_is_set()
    {
        MainWindow window = Opened();
        Assert.Null(window.OpenFile(Xaml));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        int top = Line(Xaml, "Width=\"480\"");
        int save = Line(Xaml, "x:Name=\"Save\"");
        int row = Line(Xaml, "<local:Row");

        // Toggle Breakpoint on an attribute is the attribute's; on a line's start, the line's: its element.
        editor.GoTo(new TextPosition(top, Column(Xaml, top, "Width") + 1));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.GoTo(new TextPosition(save, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.GoTo(new TextPosition(save, Column(Xaml, save, "Background") + 3));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.GoTo(new TextPosition(row, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        BreakpointStore store = window.Debugger.Breakpoints;
        Assert.Equal([$"MainWindow.axaml:{top + 1} Width", $"MainWindow.axaml:{save + 1}", $"MainWindow.axaml:{save + 1} Background", $"MainWindow.axaml:{row + 1}"],
            store.In(Xaml).Select(b => b.Location));
        TextSnapshot text = editor.Document.Snapshot;
        LineMark background = editor.LineMarks.Single(m => m.Range is not null && text.GetText(m.Range.Value.Start, m.Range.Value.Length).StartsWith("Background", StringComparison.Ordinal));
        Assert.Equal("Background=\"Orange\"", text.GetText(background.Range!.Value.Start, background.Range.Value.Length));

        Ui.Run(window, AppActions.ViewBreakpoints);
        BreakpointsWindow breakpoints = Assert.IsType<BreakpointsWindow>(window.Popup);
        Assert.Equal([$"MainWindow.axaml:{top + 1} Width", $"MainWindow.axaml:{save + 1}", $"MainWindow.axaml:{save + 1} Background", $"MainWindow.axaml:{row + 1}", ExceptionBreakpoint.AnyException,
            BindingErrorBreakpoint.AnyBindingError], breakpoints.Listed.Select(b => b.Location));
        Ui.Click(Row(breakpoints, $"MainWindow.axaml:{row + 1}"));
        Ui.Click(breakpoints.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText?.StartsWith("Stop on hits", StringComparison.Ordinal) == true));
        window.KeyTextInput("==2");
        Ui.Click(breakpoints.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Done" && b.IsEffectivelyVisible));
        Assert.Equal("==2", store.In(Xaml).Single(b => b.Line == row).HitCondition);

        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        // The window's Width, which XamlIl gives no code of its own: the stop is before it is set, its Title already set.
        DebugStop stop = Stopped(window, "the stop where the window's Width is set");
        AssertAt(stop, Xaml, top, Column(Xaml, top, "Width"));
        Assert.Equal("NaN", Member(window, "arg_1", "Width"));
        Assert.Equal("\"XAML Breakpoints\"", Member(window, "arg_1", "Title"));

        Resume(window);
        stop = Stopped(window, "the stop where Save is made");
        AssertAt(stop, Xaml, save, Column(Xaml, save, "Button"));
        Assert.True(stop.Frames.Any(f => f.Source?.Path == CodeBehind && f.Line == Line(CodeBehind, "InitializeComponent();") + 1),
            $"The stack did not show the window's constructor making its XAML: {string.Join(" < ", stop.Frames.Select(f => $"{f.Name} {Path.GetFileName(f.Source?.Path)}:{f.Line}"))}");
        Assert.Contains(window.BottomPanel.Debug.Frames, f => f.Source?.Path == Xaml);

        Resume(window);
        stop = Stopped(window, "the stop where Save's Background is set");
        AssertAt(stop, Xaml, save, Column(Xaml, save, "Background"));

        Resume(window);
        stop = Stopped(window, "the stop where the second row is made");
        AssertAt(stop, Xaml, row, Column(Xaml, row, "local:Row"));
        Assert.Equal("1", Ui.Await(window.Debugger.Session!.EvaluateAsync("XamlBreakpointsApp.Row.Made", stop.Top!.Id, "watch"), "the rows made so far").Result);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        Assert.Equal(4, store.In(Xaml).Count);
        window.Close();
    }

    /// <summary>Two buttons written on one line each set Content: Toggle Breakpoint on the second's is drawn on the second's,
    /// and the program stops where that Content is set, at its column, the first's never taking the breakpoint.</summary>
    [AvaloniaFact]
    public void A_breakpoint_on_the_second_of_two_same_named_attributes_on_a_line_is_drawn_and_stops_on_the_second()
    {
        File.WriteAllText(Xaml, File.ReadAllText(Xaml).Replace("<Button x:Name=\"Delete\" Content=\"Delete\" />",
            "<Button x:Name=\"Delete\" Content=\"Delete\" /><Button x:Name=\"Undo\" Content=\"Undo\" />", StringComparison.Ordinal));
        MainWindow window = Opened();
        Assert.Null(window.OpenFile(Xaml));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        int line = Line(Xaml, "Content=\"Undo\"");
        int second = Column(Xaml, line, "Content=\"Undo\"");
        editor.GoTo(new TextPosition(line, second + 3));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Assert.Equal([$"MainWindow.axaml:{line + 1} Content"], window.Debugger.Breakpoints.In(Xaml).Select(b => b.Location));
        TextSnapshot text = editor.Document.Snapshot;
        LineMark drawn = Assert.Single(editor.LineMarks, m => m.Line == line);
        Assert.True(drawn.Range is { } range && text.GetText(range.Start, range.Length) == "Content=\"Undo\"",
            $"The breakpoint on Undo's Content was drawn on {(drawn.Range is { } on ? text.GetText(on.Start, on.Length) : "its whole line")}");

        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        DebugStop stop = Stopped(window, "the stop where Undo's Content is set");
        AssertAt(stop, Xaml, line, second);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        window.Close();
    }

    /// <summary>At a stop shown at its breakpoint rather than where the thread is (the window's Width, which XamlIl gives no
    /// code of its own), Set Next Statement back to the window's element shows the stop there, where it went, and the
    /// program then makes the window's XAML again from its start, stopping at the Width again.</summary>
    [AvaloniaFact]
    public void Set_next_statement_from_a_stop_shown_short_of_its_point_shows_the_stop_where_it_went()
    {
        MainWindow window = Opened();
        Assert.Null(window.OpenFile(Xaml));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        int top = Line(Xaml, "Width=\"480\"");
        int root = Line(Xaml, "<Window");
        editor.GoTo(new TextPosition(top, Column(Xaml, top, "Width") + 1));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        DebugStop stop = Stopped(window, "the stop where the window's Width is set");
        AssertAt(stop, Xaml, top, Column(Xaml, top, "Width"));

        editor.GoTo(new TextPosition(root, 0));
        Ui.Run(window, AppActions.SetNextStatement);
        Ui.WaitFor(() => window.Debugger.Stop is { Reason: StopReasons.Goto }, () => $"Set Next Statement's stop (status: {window.Status})");
        AssertAt(window.Debugger.Stop!, Xaml, root, Column(Xaml, root, "Window"));
        Assert.Contains(new LineMark(root, LineMarkKind.ExecutionPoint), editor.LineMarks);

        Resume(window);
        stop = Stopped(window, "the stop where the window's Width is set again");
        AssertAt(stop, Xaml, top, Column(Xaml, top, "Width"));

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        window.Close();
    }

    /// <summary>XamlIl counts lines as an XML reader does, ending one at a lone '\r' and at no U+0085, U+2028 or U+2029, so
    /// below a comment holding all four and past a lone '\r' in Save's tag the PDB's lines run two ahead of the editor's: the
    /// breakpoint on Save's Background stops there with the arrow on its line, the frame's row and an agent's place say it,
    /// and a line typed above while paused moves the arrow and the row with their code, as the next stop, Delete's, is.</summary>
    [AvaloniaFact]
    public void Below_line_breaks_only_an_xml_reader_counts_a_xaml_breakpoint_stops_on_its_attribute_at_the_editors_line()
    {
        string xaml = File.ReadAllText(Xaml)
            .Replace("    <StackPanel", $"    <!-- Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd -->\n    <StackPanel", StringComparison.Ordinal)
            .Replace("<Button x:Name=\"Save\" Content=\"Save\"", "<Button x:Name=\"Save\"\r        Content=\"Save\"", StringComparison.Ordinal);
        File.WriteAllText(Xaml, xaml);
        (int save, int background) = EditorPlace(xaml, "Background=\"Orange\"");
        (int delete, int button) = EditorPlace(xaml, "Button x:Name=\"Delete\"");
        MainWindow window = Opened();
        Assert.Null(window.OpenFile(Xaml));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(save, background + 3));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.GoTo(new TextPosition(delete, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Assert.Equal([$"MainWindow.axaml:{save + 1} Background", $"MainWindow.axaml:{delete + 1}"], window.Debugger.Breakpoints.In(Xaml).Select(b => b.Location));

        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        DebugStop stop = Stopped(window, "the stop where Save's Background is set");
        // The debugger names XamlIl's line and column (from one), past both lone '\r's: Background's begins at the tag's.
        int named = xaml.IndexOf("Background=\"Orange\"", StringComparison.Ordinal);
        Assert.Equal((save + 3, named - xaml.LastIndexOf('\r', named)), (stop.Top!.Line, stop.Top.Column));
        Assert.Equal(new TextPosition(save, background), window.Debugger.PlaceOf(stop.Top));
        Assert.Contains(new LineMark(save, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Contains(editor.LineMarks, m => m.Line == save && m.Kind == LineMarkKind.BreakpointBound);
        window.ShowToolWindow(ToolWindowIds.Debug);
        window.BottomPanel.Debug.ShowTab(console: false);
        Ui.WaitFor(() => FrameRows(window).Contains($"MainWindow.axaml:{save + 1}"), $"the frame's row to name Save's line, not {string.Join(", ", FrameRows(window))}");
        Passenger.App.Mcp.DebugSnapshot told = (Passenger.App.Mcp.DebugSnapshot)Ui.Await(new Passenger.App.Mcp.PassengerTools(window).GetDebugState(), "the stop as an agent is told it");
        Assert.Equal($"MainWindow.axaml:{save + 1}:{background + 1}", told.Location);
        Assert.EndsWith($"at MainWindow.axaml:{save + 1}", told.Frames[0], StringComparison.Ordinal);

        editor.GoTo(new TextPosition(0, 0));
        editor.Focus();
        Ui.Press(window, "Enter");
        Assert.Contains(new LineMark(save + 1, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Contains($"MainWindow.axaml:{save + 2}", FrameRows(window));

        Resume(window);
        stop = Stopped(window, "the stop where Delete is made");
        Assert.Equal(delete + 3, stop.Top!.Line);
        Assert.Equal(new TextPosition(delete + 1, button), window.Debugger.PlaceOf(stop.Top));
        Assert.Contains(new LineMark(delete + 1, LineMarkKind.ExecutionPoint), editor.LineMarks);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        window.Close();
    }

    [AvaloniaFact]
    public void Break_when_value_changes_stops_in_the_code_the_style_or_the_binding_that_changed_that_controls_property()
    {
        // XamlIl counts the lone '\r' in the window's tag as a line's end, which the editor reads inside its line.
        string xaml = File.ReadAllText(Xaml).Replace("Title=\"XAML Breakpoints\" Width", "Title=\"XAML Breakpoints\"\r        Width", StringComparison.Ordinal);
        File.WriteAllText(Xaml, xaml);
        int red = EditorPlace(xaml, "<Setter Property=\"Foreground\" Value=\"Red\"").Line;
        MainWindow window = Opened();
        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element.Name == "Save"),
            $"the Live Visual Tree to show the app's window ({window.LiveApp?.State} {window.LiveApp?.Problem})", TimeSpan.FromMinutes(2));
        BreakpointStore store = window.Debugger.Breakpoints;

        PropertyBreakpoint background = Watch(window, "Save", "Background");
        Assert.Equal("Background on Button#Save", background.Location);
        Ui.WaitFor(() => background.Status == BreakpointStatus.Bound, $"the debugger to take the property breakpoint, not to say {background.Message}");
        Ui.TypeToDebugged(window, "paint");
        DebugStop stop = Stopped(window, "the stop where paint sets Save's Background");
        AssertAt(stop, CodeBehind, Line(CodeBehind, "Save.Background = Brushes.Green;"));
        Says(window, "Property Breakpoint Hit: Background on Button#Save is changed (Previous Value: Orange, New Value: Green, its local value)");
        Resume(window);
        stop = Stopped(window, "the stop where paint sets Save's Background again");
        AssertAt(stop, CodeBehind, Line(CodeBehind, "Save.Background = Brushes.Blue;"));
        Resume(window);
        Ui.WaitFor(() => Console(window).Contains("Painted", StringComparison.Ordinal), "paint to finish");

        Ui.Run(window, AppActions.ViewBreakpoints);
        BreakpointsWindow breakpoints = Assert.IsType<BreakpointsWindow>(window.Popup);
        Assert.Equal([background.Location, ExceptionBreakpoint.AnyException, BindingErrorBreakpoint.AnyBindingError], breakpoints.Listed.Select(b => b.Location));
        Assert.Contains(breakpoints.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Avalonia Property Breakpoints");
        Ui.Click(Row(breakpoints, background.Location));
        Ui.Click(breakpoints.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText?.StartsWith("Stop on hits", StringComparison.Ordinal) == true));
        window.KeyTextInput("2");
        Ui.Click(breakpoints.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Done" && b.IsEffectivelyVisible));
        Assert.Equal("2", background.HitCondition);
        Ui.Settle();
        Ui.TypeToDebugged(window, "paint");
        stop = Stopped(window, "the stop at the second change, by the hit count");
        AssertAt(stop, CodeBehind, Line(CodeBehind, "Save.Background = Brushes.Blue;"));
        Resume(window);
        Ui.WaitFor(() => Console(window).Split("Painted").Length == 3, "paint to finish again");

        Watch(window, "Save", "Background");
        Assert.DoesNotContain(background, store.Properties);
        PropertyBreakpoint width = Watch(window, "Save", "Width");
        Ui.Run(window, AppActions.ViewBreakpoints);
        breakpoints = Assert.IsType<BreakpointsWindow>(window.Popup);
        Ui.Click(Row(breakpoints, width.Location));
        Ui.Click(breakpoints.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText?.StartsWith("A C# condition", StringComparison.Ordinal) == true));
        window.KeyTextInput("(double)newValue > 150");
        Ui.Click(breakpoints.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Done" && b.IsEffectivelyVisible));
        Assert.Equal("(double)newValue > 150", width.Condition);
        Ui.Settle();
        Ui.TypeToDebugged(window, "paint");
        stop = Stopped(window, "the stop where Width goes over 150");
        AssertAt(stop, CodeBehind, Line(CodeBehind, "Save.Width = 200;"));
        Assert.DoesNotContain(BreakpointConditions.NotEvaluated(DebugSession.EngineCondition(width)), Console(window), StringComparison.Ordinal);
        Resume(window);

        PropertyBreakpoint foreground = Watch(window, "Save", "Foreground");
        Ui.TypeToDebugged(window, "warn");
        stop = Stopped(window, "the stop where the danger style sets Save's Foreground");
        AssertAt(stop, CodeBehind, Line(CodeBehind, "Save.Classes.Add(\"danger\");"));
        Says(window, $"Property Breakpoint Hit: {foreground.Location} is changed");
        Says(window, "by style Button.danger");
        // Where its setter is written, at the editor's line, which XamlIl's runs one past below the lone '\r'.
        Says(window, $" at MainWindow.axaml:{red + 1})");
        Resume(window);

        Watch(window, "Caption", "Text");
        Ui.TypeToDebugged(window, "rename");
        stop = Stopped(window, "the stop where the caption's binding sets its Text");
        AssertAt(stop, Path.Combine(folder, "MainViewModel.cs"), Line(Path.Combine(folder, "MainViewModel.cs"), "PropertyChanged?.Invoke"));
        Says(window, "binding");
        Resume(window);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        // Each named a control of that run, as a data breakpoint names an object: none outlives it.
        Assert.Empty(store.Properties);
        window.Close();
    }

    /// <summary>Delete's Background, which Fluent's theme gave it, set by the control itself (SetCurrentValue): the property
    /// breakpoint says the control set it, as the property's row says it and as the theme's setter says what overrides it,
    /// never the theme that gave the value before.</summary>
    [AvaloniaFact]
    public void A_property_breakpoint_on_a_value_the_control_sets_itself_says_so_as_its_row_and_the_setter_it_overrides_do()
    {
        MainWindow window = Opened();
        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        LiveTreeView tree = window.LiveTree;
        Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Delete"),
            $"the Live Visual Tree to show the app's window ({window.LiveApp?.State} {window.LiveApp?.Problem})", TimeSpan.FromMinutes(2));

        PropertyBreakpoint background = Watch(window, "Delete", "Background");
        Ui.WaitFor(() => background.Status == BreakpointStatus.Bound, $"the debugger to take the property breakpoint, not to say {background.Message}");
        Ui.TypeToDebugged(window, "nudge");
        DebugStop stop = Stopped(window, "the stop where nudge has Delete set its own Background");
        AssertAt(stop, CodeBehind, Line(CodeBehind, "Delete.SetCurrentValue("));
        Says(window, "New Value: Purple, by the control itself (SetCurrentValue))");
        Resume(window);
        Ui.WaitFor(() => Console(window).Contains("Nudged", StringComparison.Ordinal), "nudge to finish");

        tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Panel");
        Ui.WaitFor(() => tree.Details?.Name == "Panel", "the panel's details");
        tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Delete");
        Ui.WaitFor(() => tree.Details?.Name == "Delete", "Delete's details");
        PropertyValue row = tree.Details!.Properties.Single(p => p.Name == "Background");
        Assert.True(row is { Source: ValueSources.Current, Detail: "Set by the control itself (SetCurrentValue)" },
            $"Delete's Background row said it came from {row.Source} ({row.Detail}), not the control itself");
        SetterReport themes = tree.Details.Styles.Single(s => s.Kind == StyleKinds.Theme).Setters.Single(s => s.Property == "Background");
        Assert.True(themes is { State: SetterStates.Overridden, By: "the control itself (SetCurrentValue)" },
            $"The theme's Background setter on Delete was {themes.State} by {themes.By}, not overridden by the control itself");

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        window.Close();
    }

    private MainWindow Opened() => Ui.OpenFolder(folder, 1400, 900, languageServer: false);

    /// <summary>Break When Value Changes from a property's row of a control chosen in the Live Visual Tree: the property
    /// breakpoint it sets, or null when it took the one there away.</summary>
    private static PropertyBreakpoint Watch(MainWindow window, string element, string property)
    {
        LiveTreeView tree = window.LiveTree;
        // Chosen afresh, so its properties are as they are now (a value set since lists it as local).
        tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Panel");
        Ui.WaitFor(() => tree.Details?.Name == "Panel", "the panel's details");
        tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == element);
        Ui.WaitFor(() => tree.Details?.Name == element, $"{element}'s details");
        tree.ShowPage(LiveTreePage.Properties);
        Ui.Settle();
        tree.PropertyList.SelectedItem = tree.PropertyRows.First(p => p.Name == property);
        Ui.ChooseFromMenu(tree.PropertyList, LiveTreeView.BreakWhenValueChanges);
        return window.Debugger.Breakpoints.FindProperty(window.Debugger.Session!, tree.Details!.Id, property)!;
    }

    /// <summary>Waits for the program to stop and the stop to be read, which it returns.</summary>
    private static DebugStop Stopped(MainWindow window, string what)
    {
        Ui.WaitFor(() => window.Debugger.Session?.State is DebugState.Paused or DebugState.Ended && (window.Debugger.Stop is { } || window.Debugger.Session.State == DebugState.Ended),
            what, TimeSpan.FromMinutes(1));
        return window.Debugger.Stop ?? throw new Xunit.Sdk.XunitException($"The program ended before {what}: {window.Status}");
    }

    /// <summary>Resumes, and waits for the program to leave its stop.</summary>
    private static void Resume(MainWindow window)
    {
        DebugSession session = window.Debugger.Session!;
        bool resumed = false;
        session.StateChanged += (_, _) => resumed |= session.State != DebugState.Paused;
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => resumed, "the program to go on");
    }

    /// <summary>A stop shown where it is meant to be: its frame at a line (from zero) of a file, at a column (from zero).</summary>
    private static void AssertAt(DebugStop stop, string path, int line, int? column = null)
    {
        Assert.True(stop.Top?.Source?.Path == path && stop.Top.Line == line + 1 && (column is null || stop.Top.Column == column + 1),
            $"The stop was shown at {stop.Top?.Source?.Path}:{stop.Top?.Line}:{stop.Top?.Column}, not {path}:{line + 1}{(column is null ? string.Empty : $":{column + 1}")} "
            + $"({stop.Reason}; {string.Join(" < ", stop.Frames.Take(12).Select(f => $"{f.Name} {Path.GetFileName(f.Source?.Path)}:{f.Line}"))})");
    }

    /// <summary>A member of a top-level row of Variables, the row opened to show it.</summary>
    private static string? Member(MainWindow window, string row, string name)
    {
        Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
        Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Depth == 0 && r.Name == row), $"{row} in Variables");
        VariableNode owner = view.Tree.Rows.First(r => r.Depth == 0 && r.Name == row);
        if (!owner.IsExpanded)
        {
            _ = view.Tree.Expand(owner);
        }
        Ui.WaitFor(() => owner.Children?.Any(c => c.Name == name) == true, $"{row}'s {name} in Variables");
        return owner.Children!.First(c => c.Name == name).Value;
    }

    private static string Console(MainWindow window) => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();

    /// <summary>The status bar says something of the stop, once the stop's words are read.</summary>
    private static void Says(MainWindow window, string text) =>
        Ui.WaitFor(() => window.Status?.Contains(text, StringComparison.Ordinal) == true, $"the status bar to say \"{text}\", not \"{window.Status}\"", TimeSpan.FromSeconds(20));

    private static ListBoxItem Row(BreakpointsWindow breakpoints, string location) =>
        breakpoints.GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.Tag as Breakpoint)?.Location == location);

    /// <summary>Where (line and column from zero) some text begins in a text as the editor counts lines, at each '\n' alone.</summary>
    private static (int Line, int Column) EditorPlace(string text, string find)
    {
        int at = text.IndexOf(find, StringComparison.Ordinal);
        return (text.AsSpan(0, at).Count('\n'), at - text.LastIndexOf('\n', at) - 1);
    }

    /// <summary>The places the Debug window's frame rows name, as drawn.</summary>
    private static List<string?> FrameRows(MainWindow window)
    {
        Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
        ListBox frames = window.DebugWindow.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, view.Frames));
        return [.. frames.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)];
    }

    /// <summary>The line (from zero) of a file holding some text, so a test names what it means rather than a number.</summary>
    private static int Line(string file, string text) => Array.FindIndex(File.ReadAllLines(file), l => l.Contains(text, StringComparison.Ordinal));

    /// <summary>Where (from zero) some text begins on a line (from zero) of a file.</summary>
    private static int Column(string file, int line, string text) => File.ReadAllLines(file)[line].IndexOf(text, StringComparison.Ordinal);
}
