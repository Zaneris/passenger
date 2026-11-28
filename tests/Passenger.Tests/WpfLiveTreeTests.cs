using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.ToolWindows;
using Passenger.App.WinForms;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>A WPF app debugged from the window: tests/Debuggees/WpfTreeApp, written into a folder of its own with a
/// project file for the tests' own target, restored from the run's own feed, built and launched by Debug as a person's app
/// is, with Passenger's WPF inspector in it; and a Windows Forms app, tests/Debuggees/FormsTreeApp, the same way with its
/// own. Their windows show on the screen (neither has a headless platform), and their standard input stands in for the
/// person's pointer ("click X,Y", typed into the Debug window's console, which the app sends its window as Windows sends a
/// mouse's messages). Both run on Windows alone, so elsewhere the tests skip. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class WpfLiveTreeTests : IClassFixture<RestoredFolders>, IDisposable
{
    private const string OnlyOnWindows = "WPF runs on Windows alone";

    private readonly RestoredFolders folders;

    private readonly string folder = string.Empty;
    private readonly string? traceBefore = Environment.GetEnvironmentVariable(Wire.TraceVariable);

    public WpfLiveTreeTests(RestoredFolders folders)
    {
        this.folders = folders;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        folder = PathNames.Canonical(folders.Take("wpftree-", made =>
        {
            foreach (string file in Directory.GetFiles(Debuggees.Source("WpfTreeApp")))
            {
                File.Copy(file, Path.Combine(made, Path.GetFileName(file)));
            }
            return WpfApp.Project(made, "WpfTreeApp");
        }));
        // What the inspector did as the app started, quoted by a failure's message; the app launched inherits it.
        Environment.SetEnvironmentVariable(Wire.TraceVariable, Trace);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Wire.TraceVariable, traceBefore);
        if (folder.Length > 0)
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The file the inspector in the app traces its steps to.</summary>
    private string Trace => Path.Combine(folder, "inspector.log");

    private string Xaml => Path.Combine(folder, "MainWindow.xaml");

    private string AppXaml => Path.Combine(folder, "App.xaml");

    /// <summary>A debugged WPF app's live tree: its elements with where each value comes from, going to their XAML; a
    /// misspelt binding in the debug console linked to its line and in Problems; XAML binding errors stopping the app
    /// where its binding fails; and Select Element choosing the element clicked; then a Windows Forms app's controls, going
    /// to their designer code. On Windows only.</summary>
    [AvaloniaFact]
    public void A_debugged_wpf_apps_elements_bindings_and_clicks_reach_their_xaml()
    {
        Flow.StepApart("a debugged WPF app shows its elements, where each value comes from, and goes to their XAML", () => new WpfLiveTreeTests(folders), t => t.A_debugged_wpf_app_shows_its_elements_where_each_value_comes_from_and_goes_to_their_xaml());
        Flow.StepApart("a debugged WPF app's misspelt binding shows in the debug console linked to its line and in Problems", () => new WpfLiveTreeTests(folders), t => t.A_debugged_wpf_apps_misspelt_binding_shows_in_the_debug_console_linked_to_its_line_and_in_problems());
        Flow.StepApart("XAML Binding Errors stops a debugged WPF app where its binding fails", () => new WpfLiveTreeTests(folders), t => t.Xaml_binding_errors_stops_a_debugged_wpf_app_where_its_binding_fails());
        Flow.StepApart("Select Element chooses the element clicked in a WPF app", () => new WpfLiveTreeTests(folders), t => t.Select_element_chooses_the_element_clicked_in_a_wpf_app());
        Flow.StepApart("a breakpoint on a WPF page's element and one on an attribute each stop at its XAML as the element joins the window",
            () => new WpfLiveTreeTests(folders), t => t.A_breakpoint_on_a_wpf_pages_element_and_one_on_an_attribute_each_stop_at_its_xaml_as_the_element_joins_the_window());
        Flow.StepApart("a debugged Windows Forms app shows its controls, where each value comes from in its designer code, and Select Element chooses the one clicked", () => new WpfLiveTreeTests(folders), t => t.A_debugged_windows_forms_app_shows_its_controls_where_each_value_comes_from_in_its_designer_code_and_select_element_chooses_the_one_clicked());
        Flow.StepApart("a debugged Windows Forms app takes an edit of its form's design without being started again", () => new WpfLiveTreeTests(folders), t => t.A_debugged_windows_forms_app_takes_an_edit_of_its_forms_design_without_being_started_again());
    }

    private void A_debugged_wpf_app_shows_its_elements_where_each_value_comes_from_and_goes_to_their_xaml()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = Debugged();
        LiveTreeView tree = window.LiveTree;
        Assert.True(window.DockHost.IsShown(ToolWindowIds.LiveTree), "The Live Visual Tree did not open as the app's window came up");
        Assert.Equal("MainWindow", tree.Rows[0].Element.Type);
        Assert.Contains(tree.Rows, r => r.Element is { Type: "StackPanel", Name: "Panel" });
        Assert.Contains(tree.Rows, r => r.Element is { Type: "TextBlock", Name: "Greeting" });
        Assert.Contains(tree.Rows, r => r.Element is { Type: "Button", Name: "Delete" });
        Assert.True(window.LiveApp is { Kind: LiveAppKind.Wpf } && window.LiveApp.Framework.StartsWith("WPF ", StringComparison.Ordinal),
            $"The app was taken for {window.LiveApp?.Kind}, {window.LiveApp?.Framework}");

        ElementDetails delete = Choose(window, "Delete");
        PropertyValue deleteBackground = delete.Properties.Single(p => p.Name == "Background");
        Assert.True(deleteBackground.Source == ValueSources.Local, $"Delete's Background came from {deleteBackground.Source} {deleteBackground.Detail}, not its local value");
        ElementDetails count = Choose(window, "Count");
        PropertyValue countBackground = count.Properties.Single(p => p.Name == "Background");
        Assert.True(countBackground.Source == ValueSources.Theme, $"Count's Background came from {countBackground.Source} {countBackground.Detail}, not Button's default style");

        PropertyValue margin = count.Properties.Single(p => p.Name == "Margin");
        Assert.True(margin.Source == ValueSources.Style, $"Count's Margin came from {margin.Source} {margin.Detail}, not the implicit Style for Button");
        Assert.Equal((AppXaml, Line(AppXaml, "Property=\"Margin\"")), (PathNames.Canonical(margin.Place!.File), margin.Place.Line));
        StyleReport implicitStyle = count.Styles.Single(s => s.Kind == StyleKinds.Style);
        Assert.Equal(("Style TargetType=Button", Line(AppXaml, "<Style TargetType=\"Button\">")), (implicitStyle.Selector, implicitStyle.Place?.Line));
        StyleReport hover = count.Styles.Single(s => s.Kind == StyleKinds.Trigger && s.Selector == "Trigger IsMouseOver=True");
        Assert.True(hover is { Applies: false, WhyNot: "IsMouseOver is False, not True" }, $"The IsMouseOver trigger applies {hover.Applies}: {hover.WhyNot}");
        Assert.Contains(hover.Setters, s => s.Property == "Foreground" && s.State == SetterStates.Inactive);

        ElementDetails greeting = Choose(window, "Greeting");
        PropertyValue weight = greeting.Properties.Single(p => p.Name == "FontWeight");
        Assert.True(weight.Source == ValueSources.Style, $"Greeting's FontWeight came from {weight.Source} {weight.Detail}, not its Style");
        Assert.Equal((AppXaml, Line(AppXaml, "Property=\"FontWeight\"")), (PathNames.Canonical(weight.Place!.File), weight.Place.Line));
        PropertyValue size = greeting.Properties.Single(p => p.Name == "FontSize");
        Assert.True(size is { Source: ValueSources.Inherited, Value: "15" } && size.Detail == "From MainWindow (its local value)",
            $"Greeting's FontSize {size.Value} came from {size.Source} {size.Detail}, not the window's local value");
        Assert.True(greeting.Layout.InWindow is { X: >= 12, Y: >= 12, Width: > 0, Height: > 0 }, $"Greeting's box in its window is {greeting.Layout.InWindow}");
        Assert.True(greeting.Layout.Bounds.Width > 0 && greeting.Layout.Margin == new Edges(0, 0, 0, 0), $"Greeting's layout: {greeting.Layout}");

        Choose(window, "Delete");
        Assert.NotNull(tree.GoToSource);
        Ui.Click(tree.GoToSource!);
        Assert.Equal(Xaml, window.EditorArea.Active?.Path);
        Assert.Equal(Line(Xaml, "x:Name=\"Delete\"") - 1, window.EditorArea.CodeEditor.CaretPosition.Line);

        Ui.Run(window, AppActions.Pause);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused, "the app to pause");
        Ui.WaitFor(() => tree.BannerText is not null, "the tree to say the app is paused");
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => tree.BannerText is null, "the tree to be live again once the app runs");

        Stop(window);
        Assert.Empty(tree.Rows);
        window.Close();
    }

    private void A_debugged_wpf_apps_misspelt_binding_shows_in_the_debug_console_linked_to_its_line_and_in_problems()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = Opened();
        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        window.BottomPanel.Debug.ShowTab(console: true);
        OutputView console = window.BottomPanel.Debug.Console;
        string line;
        try
        {
            line = WaitForLine(console, "the Debug window's console to say the caption's binding failed");
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException($"{waited.Message}: {Said(window)}", waited);
        }

        Assert.StartsWith($"MainWindow.xaml({Misspelt.Line + 1},{Misspelt.Column + 1}): binding error: TextBlock#Caption.Text to Nmae: ", line, StringComparison.Ordinal);
        Assert.Contains("'Nmae' property not found", line, StringComparison.Ordinal);
        Assert.Contains("System.Windows.Data Error: 40", line, StringComparison.Ordinal);
        Assert.Contains("Did you mean Name?", line, StringComparison.Ordinal);
        Ui.ClickLink(console, line);
        Assert.Equal((Xaml, Misspelt), (window.EditorArea.Active?.Path, window.EditorArea.CodeEditor.CaretPosition));

        ProblemsModel problems = window.BottomPanel.Problems;
        BindingProblem caption = Assert.Single(problems.BindingProblems);
        Assert.Equal(("TextBlock#Caption", "Text", "Nmae", 1), (caption.Failure.Control, caption.Failure.Property, caption.Failure.Path, caption.Failure.Count));
        Assert.Equal((Xaml, Misspelt), (caption.Problem.Path, caption.Problem.Position));
        Assert.Equal(["Name"], caption.Failure.Nearest);
        Assert.Equal(Line(Xaml, "x:Name=\"Caption\""), caption.Failure.Place?.Line);

        Ui.TypeToDebugged(window, "rebind");
        Ui.WaitFor(() => problems.BindingProblems.SingleOrDefault()?.Failure.Count == 2, "the caption's failure to be counted twice");
        Assert.Single(problems.BindingProblems);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        window.Close();
    }

    private void Xaml_binding_errors_stops_a_debugged_wpf_app_where_its_binding_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = Opened();
        BreakpointStore store = window.Debugger.Breakpoints;
        store.Update(store.BindingErrors, enabled: true, suspend: true, null, null, null);
        Assert.True(window.Debugger.Breakpoints.BindingErrors.Enabled, "XAML Binding Errors did not turn on");

        // WPF's binding engine fails the caption's binding with none of the app's code on the stack: the stop shows the XAML.
        string engineLog = Path.Combine(folder, "engine.log");
        string? kept = Environment.GetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable);
        Environment.SetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable, engineLog);
        DebugStop stop;
        try
        {
            Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
            stop = Stopped(window, "the stop where the window's view model makes the caption's binding fail");
        }
        catch (TimeoutException waited)
        {
            string said = File.Exists(engineLog) ? string.Join('\n', Ui.Shared(engineLog).Split('\n')
                .Where(l => l.Contains("unction breakpoint", StringComparison.Ordinal) || l.Contains("Inspector", StringComparison.Ordinal))
                .TakeLast(40)) : "(no engine log)";
            throw new TimeoutException($"{waited.Message}\nThe engine's log on function breakpoints and the inspector:\n{said}", waited);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EngineLogVariable, kept);
        }
        string frames = string.Join(" < ", stop.Frames.Take(12).Select(f => $"{f.Name} {Path.GetFileName(f.Source?.Path)}:{f.Line}"));
        Assert.True(stop.Binding is { } binding && (binding.Problem.Path, binding.Problem.Position) == (Xaml, Misspelt),
            $"The stop was not taken for the caption's binding error at MainWindow.xaml {Misspelt}: {stop.Binding?.Problem.Path} {stop.Binding?.Problem.Position} ({stop.Reason}; {frames})");
        Ui.WaitFor(() => window.EditorArea.Active?.Path == Xaml && window.EditorArea.CodeEditor.CaretPosition == Misspelt,
            $"the editor to show the misspelt member, not {window.EditorArea.Active?.Path} {window.EditorArea.CodeEditor.CaretPosition} ({frames})");
        Ui.WaitFor(() => window.Status?.Contains("paused on a binding error at MainWindow.xaml", StringComparison.Ordinal) == true,
            $"the status bar to say the binding error, not \"{window.Status}\"", TimeSpan.FromSeconds(20));
        Assert.Equal("\"Nmae\"", Member(window, "$bindingError", "Path"));
        Assert.Equal("\"Nmae\"", Member(window, "$bindingError", "FailedPart"));
        Assert.Contains("MainViewModel", Member(window, "$bindingError", "DataContext"), StringComparison.Ordinal);
        Assert.Contains("TextBlock", Member(window, "$bindingError", "Control"), StringComparison.Ordinal);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        window.Close();
    }

    private void Select_element_chooses_the_element_clicked_in_a_wpf_app()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = Debugged();
        LiveTreeView tree = window.LiveTree;
        Box count = Choose(window, "Count").Layout.InWindow!;
        Choose(window, "Greeting");

        Ui.Click(tree.SelectElement);
        Assert.True(tree.SelectElement.IsChecked, "Select Element did not stay on");
        // Just inside the Count button's edge, short of its text: the border its template draws.
        Ui.TypeToDebugged(window, $"click {(count.X + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)},{(count.Y + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        try
        {
            Ui.WaitFor(() => tree.SelectElement.IsChecked == false && tree.Details is { } chosen && chosen.Name != "Greeting" && Under(tree, chosen.Id, "Count"),
                "the element clicked in the app to be chosen in the tree", TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException($"{waited.Message}, not {tree.Details?.Type} of {tree.Details?.TemplatedParent} (Select Element {tree.SelectElement.IsChecked}; "
                + $"Count at {count}): {Said(window)}", waited);
        }
        Assert.Equal("Button#Count", tree.Details!.TemplatedParent);
        // The click was Select Element's: the app never saw it, and its counter says so.
        Assert.Equal("\"Not clicked yet\"", Choose(window, "Counter").Properties.Single(p => p.Name == "Text").Value);

        Stop(window);
        window.Close();
    }

    private void A_debugged_windows_forms_app_shows_its_controls_where_each_value_comes_from_in_its_designer_code_and_select_element_chooses_the_one_clicked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string forms = FormsFolder();
        string designer = Path.Combine(forms, "MainForm.Designer.cs");
        MainWindow window = Ui.OpenFolder(forms, 1400, 900);
        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        LiveTreeView tree = window.LiveTree;
        try
        {
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Count"), "the Live Visual Tree to show the app's form", TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException($"{waited.Message}: {Said(window)}", waited);
        }
        Assert.True(window.LiveApp is { Kind: LiveAppKind.WinForms } && window.LiveApp.Framework.StartsWith("Windows Forms ", StringComparison.Ordinal),
            $"The app was taken for {window.LiveApp?.Kind}, {window.LiveApp?.Framework}");
        Assert.Equal("MainForm", tree.Rows[0].Element.Type);
        Assert.Contains(tree.Rows, r => r.Element is { Type: "ToolStripMenuItem", Name: "HelpItem" });

        ElementDetails count = Choose(window, "Count");
        PropertyValue text = count.Properties.Single(p => p.Name == "Text");
        Assert.True(text is { Source: ValueSources.Local, Value: "\"Count\"", Place: { } set } && PathNames.Same(set.File, designer) && set.Line == Line(designer, "Count.Text = "),
            $"Count's Text {text.Value} came from {text.Source} at {text.Place?.File}:{text.Place?.Line}, not the designer code's line setting it");
        ElementDetails inside = Choose(window, "Inside");
        PropertyValue font = inside.Properties.Single(p => p.Name == "Font");
        Assert.True(font is { Source: ValueSources.Inherited, Detail: "From Panel#Bold (its local value)" }, $"Inside's Font came from {font.Source} {font.Detail}, not its panel's");
        Choose(window, "Count");
        Assert.NotNull(tree.GoToSource);
        Ui.Click(tree.GoToSource!);
        Assert.Equal(designer, window.EditorArea.Active?.Path);
        Assert.Equal(Line(designer, "Count = new Button();") - 1, window.EditorArea.CodeEditor.CaretPosition.Line);

        // A click Select Element takes is the inspector's: the app never sees it, and its counter says so.
        Box at = count.Layout.InWindow!;
        Ui.Click(tree.SelectElement);
        Ui.TypeToDebugged(window, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"click {at.X + 5},{at.Y + 5}"));
        Ui.WaitFor(() => tree.SelectElement.IsChecked == false && tree.Details?.Name == "Count", "the control clicked in the app to be chosen in the tree", TimeSpan.FromSeconds(30));
        Assert.Equal("\"Not clicked yet\"", Choose(window, "Counter").Properties.Single(p => p.Name == "Text").Value);
        Stop(window);
        window.Close();
    }

    /// <summary>Under Debug, Count's text typed in the form's design (MainForm.cs) is written to its designer code and taken by
    /// the running form through its inspector, said as Hot Reload's design changes.</summary>
    private void A_debugged_windows_forms_app_takes_an_edit_of_its_forms_design_without_being_started_again()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string forms = FormsFolder();
        MainWindow window = Ui.OpenFolder(forms, 1400, 900);
        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element.Name == "Count"), () => $"the Live Visual Tree to show the app's form: {Said(window)}", TimeSpan.FromMinutes(2));
        window.OpenFile(Path.Combine(forms, "MainForm.cs"));
        FormDesignPane pane = WinFormsDesignerTests.Drawn(window);
        window.ShowToolWindow(ToolWindowIds.Structure);
        DesignedBox count = WinFormsDesignerTests.Component(pane, "Count");
        Ui.Click(pane.Frame, new Point(count.X + (count.Width / 2), count.Y + (count.Height / 2)));
        Ui.WaitFor(() => pane.Chosen?.Field == "Count" && window.FormProperties?.Rows.Any(r => r.Name == "Text") == true, "Count's properties to be listed");
        HotReloadOutcome? before = window.Debugger.LastApplied;
        string? said = WinFormsDesignerTests.Type(window, window.FormProperties!, "Text", "Counted");
        string designer = Path.Combine(forms, "MainForm.Designer.cs");
        Ui.WaitFor(() => window.Debugger.LastApplied is { } now && now != before && now.Said.English.Contains("Design changes", StringComparison.Ordinal),
            () => $"Hot Reload to say what the design's edit came to in the app, not {window.Debugger.LastApplied?.Result} \"{window.Debugger.LastApplied?.Said}\" "
                + $"(the edit said \"{said}\", its designer code {(File.ReadAllText(designer).Contains("\"Counted\"", StringComparison.Ordinal) ? "written" : "not written")}, "
                + $"the app's edits {(window.LiveApp?.Xaml is { } xaml ? (xaml.Has(designer) ? "waiting" : "none") : "unfollowed")}; {window.Status})");
        Assert.Contains("Design changes applied: MainForm.Designer.cs (MainForm)", window.Debugger.LastApplied!.Said.English, StringComparison.Ordinal);
        Choose(window, "Counter");
        Assert.Equal("\"Counted\"", Choose(window, "Count").Properties.Single(p => p.Name == "Text").Value);
        Stop(window);
        window.Close();
    }

    /// <summary>FormsTreeApp's project, written once for every step that runs it.</summary>
    private string FormsFolder() => PathNames.Canonical(folders.Take("formstree-", made =>
    {
        foreach (string file in Directory.GetFiles(Debuggees.Source("FormsTreeApp")))
        {
            File.Copy(file, Path.Combine(made, Path.GetFileName(file)));
        }
        return WpfApp.Project(made, "FormsTreeApp", forms: true);
    }));

    /// <summary>Under Debug, a breakpoint on the line of a WPF page's element, and one on another element's attribute, each
    /// stop as their element joins the window from the page's BAML, the stop shown at the XAML with the element there to
    /// read (its attribute set) and the app's own frames below; Continue goes on to the next, one on the second of two
    /// elements on a line stopping at that one alone. Built with no XAML places recorded, each says why it cannot stop.</summary>
    private void A_breakpoint_on_a_wpf_pages_element_and_one_on_an_attribute_each_stop_at_its_xaml_as_the_element_joins_the_window()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        // Two elements on one line, which WPF tells apart by the column it records each at.
        File.WriteAllText(Xaml, File.ReadAllText(Xaml).Replace("<TextBlock x:Name=\"Hello\"",
            "<TextBlock x:Name=\"Left\" Text=\"left\" /><TextBlock x:Name=\"Right\" Text=\"right\" />\r\n        <TextBlock x:Name=\"Hello\"", StringComparison.Ordinal));
        MainWindow window = Opened();
        Assert.Null(window.OpenFile(Xaml));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        int count = Line(Xaml, "x:Name=\"Count\"") - 1;
        int hello = Line(Xaml, "x:Name=\"Hello\"") - 1;
        int shared = Line(Xaml, "x:Name=\"Right\"") - 1;
        editor.GoTo(new TextPosition(count, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.GoTo(new TextPosition(shared, File.ReadAllLines(Xaml)[shared].IndexOf("Text=\"right\"", StringComparison.Ordinal) + 1));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.GoTo(new TextPosition(hello, File.ReadAllLines(Xaml)[hello].IndexOf("Text=", StringComparison.Ordinal) + 1));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        BreakpointStore store = window.Debugger.Breakpoints;
        Assert.Equal([$"MainWindow.xaml:{count + 1}", $"MainWindow.xaml:{shared + 1} Text", $"MainWindow.xaml:{hello + 1} Text"], store.In(Xaml).Select(b => b.Location));

        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        DebugStop stop = Stopped(window, "the stop as Count joins the window");
        string frames = string.Join(" < ", stop.Frames.Take(12).Select(f => $"{f.Name} {Path.GetFileName(f.Source?.Path)}:{f.Line}"));
        Assert.True(stop.Top?.Source?.Path == Xaml && stop.Top.Line == count + 1, $"The stop was shown at {stop.Top?.Source?.Path}:{stop.Top?.Line}, not Count's line {count + 1} ({frames})");
        Assert.True(stop.Frames.Count > 1, $"The stop showed no frames of the app's below its XAML: {frames}");
        Assert.Contains("Count", Ui.Await(window.Debugger.Session!.EvaluateAsync("((System.Windows.FrameworkElement)element).Name", stop.Top!.Id, EvaluateContexts.Watch), "the element made").Result,
            StringComparison.Ordinal);
        Ui.WaitFor(() => window.EditorArea.Active?.Path == Xaml && window.EditorArea.CodeEditor.CaretPosition.Line == count, "the editor to show Count's line");

        DebugSession session = window.Debugger.Session!;
        bool resumed = false;
        session.StateChanged += (_, _) => resumed |= session.State != DebugState.Paused;
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => resumed, "the program to go on");
        stop = Stopped(window, "the stop as Right joins the window, and not Left, which starts on its line before it");
        Assert.True(stop.Top?.Source?.Path == Xaml && stop.Top.Line == shared + 1, $"The second stop was shown at {stop.Top?.Source?.Path}:{stop.Top?.Line}, not Right's line {shared + 1}");
        Assert.Equal("\"right\"", Ui.Await(session.EvaluateAsync("((System.Windows.Controls.TextBlock)element).Text", stop.Top!.Id, EvaluateContexts.Watch), "the element stopped at").Result);
        resumed = false;
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => resumed, "the program to go on");
        stop = Stopped(window, "the stop as Hello joins the window, its Text set");
        Assert.True(stop.Top?.Source?.Path == Xaml && stop.Top.Line == hello + 1, $"The third stop was shown at {stop.Top?.Source?.Path}:{stop.Top?.Line}, not Hello's line {hello + 1}");
        Assert.Equal("\"Nobody greeted\"", Ui.Await(session.EvaluateAsync("((System.Windows.Controls.TextBlock)element).Text", stop.Top!.Id, EvaluateContexts.Watch), "Hello's Text").Result);
        Stop(window);

        // Built with no element's place recorded, as a Release build is, each XAML breakpoint says why it cannot stop.
        string project = Directory.GetFiles(folder, "*.csproj").Single();
        string built = File.ReadAllText(project);
        File.WriteAllText(project, built.Replace("</Project>", "  <PropertyGroup>\r\n    <XamlDebuggingInformation>false</XamlDebuggingInformation>\r\n  </PropertyGroup>\r\n</Project>",
            StringComparison.Ordinal));
        try
        {
            Ui.Launched(window, window.DebugAsync(), "the app built with no XAML places to start under the debugger", TimeSpan.FromMinutes(4));
            Ui.WaitFor(() => store.In(Xaml).All(b => b.Status == BreakpointStatus.Invalid && b.Message?.Contains("XamlDebuggingInformation", StringComparison.Ordinal) == true),
                () => $"each XAML breakpoint to say why it cannot stop: {string.Join("; ", store.In(Xaml).Select(b => $"{b.Location} {b.Status} {b.Message}"))}", TimeSpan.FromMinutes(2));
            Stop(window);
        }
        finally
        {
            File.WriteAllText(project, built);
        }
        window.Close();
    }

    /// <summary>Whether a row of the tree is the element of a name or inside it, by the rows above it a level up each.</summary>
    private static bool Under(LiveTreeView tree, long id, string name)
    {
        List<LiveTreeRow> rows = [.. tree.Rows];
        int at = rows.FindIndex(r => r.Element.Id == id);
        if (at < 0)
        {
            return false;
        }
        int depth = rows[at].Depth;
        for (int i = at; i >= 0 && depth >= 0; i--)
        {
            if (rows[i].Depth == depth)
            {
                if (rows[i].Element.Name == name)
                {
                    return true;
                }
                depth--;
            }
        }
        return false;
    }

    /// <summary>Where the caption's binding names Nmae, from zero.</summary>
    private TextPosition Misspelt => new(Line(Xaml, "{Binding Nmae}") - 1, File.ReadAllLines(Xaml)[Line(Xaml, "{Binding Nmae}") - 1].IndexOf("Nmae", StringComparison.Ordinal));

    private MainWindow Opened() => Ui.OpenFolder(folder, 1400, 900);

    /// <summary>The folder opened in a window and its app launched by Debug, its tree shown.</summary>
    private MainWindow Debugged()
    {
        MainWindow window = Opened();
        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        try
        {
            Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element.Name == "Delete"), "the Live Visual Tree to show the app's window", TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException($"{waited.Message}: {Said(window)}", waited);
        }
        return window;
    }

    /// <summary>What the window says of the app and its debugging, the end of its Debug console and of the IDE's log, and what
    /// the inspector in the app traced, for a failure's message.</summary>
    private string Said(MainWindow window)
    {
        window.BottomPanel.Debug.Console.Flush();
        string console = window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
        string log = Ui.IdeLog();
        string trace = File.Exists(Trace) ? Ui.Shared(Trace) : "(nothing)";
        DebugStop? stop = window.Debugger.Stop;
        return $"the app's inspector {window.LiveApp?.Kind} {window.LiveApp?.State} {window.LiveApp?.Problem}; the debugger {window.Debugger.Session?.State}"
            + (stop is null ? string.Empty : $" stopped for {stop.Reason} at {stop.Top?.Name} {Path.GetFileName(stop.Top?.Source?.Path)}:{stop.Top?.Line} ({stop.Exception})")
            + $"; the status bar \"{window.Status}\"; the Debug console ends:\n{console[Math.Max(0, console.Length - 2000)..]}"
            + $"\nThe IDE's log ends:\n{log[Math.Max(0, log.Length - 2000)..]}\nThe inspector traced:\n{trace[Math.Max(0, trace.Length - 3000)..]}";
    }

    /// <summary>Chooses the element of a name in the tree, as a click on its row does, and waits for its details.</summary>
    private static ElementDetails Choose(MainWindow window, string name)
    {
        LiveTreeView tree = window.LiveTree;
        tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == name);
        Ui.WaitFor(() => tree.Details?.Name == name, $"{name}'s details");
        return tree.Details!;
    }

    /// <summary>The console's line saying the caption's binding failed, once it has it.</summary>
    private static string WaitForLine(OutputView console, string what)
    {
        string? line = null;
        Ui.WaitFor(() =>
        {
            console.Flush();
            line = console.Document.Snapshot.ToString().Split('\n').FirstOrDefault(l => l.Contains("binding error", StringComparison.Ordinal));
            return line is not null;
        }, what, TimeSpan.FromMinutes(2));
        return line!;
    }

    /// <summary>Waits for the program to stop and the stop to be read, which it returns.</summary>
    private DebugStop Stopped(MainWindow window, string what)
    {
        try
        {
            Ui.WaitFor(() => window.Debugger.Session?.State is DebugState.Paused or DebugState.Ended && (window.Debugger.Stop is { } || window.Debugger.Session.State == DebugState.Ended),
                what, TimeSpan.FromMinutes(1));
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException($"{waited.Message} (XAML Binding Errors {window.Debugger.Breakpoints.BindingErrors.Status}: "
                + $"{window.Debugger.Breakpoints.BindingErrors.Message}): {Said(window)}", waited);
        }
        return window.Debugger.Stop ?? throw new Xunit.Sdk.XunitException($"The program ended before {what}: {Said(window)}");
    }

    /// <summary>A member of a top-level row of Variables, the row opened to show it.</summary>
    private static string Member(MainWindow window, string row, string name)
    {
        DebugModel view = window.BottomPanel.Debug;
        Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Depth == 0 && r.Name == row),
            $"{row} in Variables, not only {string.Join(", ", view.Tree.Rows.Where(r => r.Depth == 0).Select(r => r.Name))}");
        VariableNode owner = view.Tree.Rows.First(r => r.Depth == 0 && r.Name == row);
        if (!owner.IsExpanded)
        {
            _ = view.Tree.Expand(owner);
        }
        Ui.WaitFor(() => owner.Children?.Any(c => c.Name == name) == true, $"{row}'s {name} in Variables");
        return owner.Children!.First(c => c.Name == name).Value;
    }

    private static void Stop(MainWindow window)
    {
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        Ui.WaitFor(() => window.LiveApp?.State == LiveAppState.Ended, "the app's inspector to end with it");
    }

    /// <summary>The line (from one) of a file holding some text, so a test names what it means rather than a number.</summary>
    private static int Line(string file, string text) => Array.FindIndex(File.ReadAllLines(file), l => l.Contains(text, StringComparison.Ordinal)) + 1;
}
