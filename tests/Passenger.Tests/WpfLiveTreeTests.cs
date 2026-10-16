using System.Diagnostics;
using System.Text.Json;
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
using Passenger.Core;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>A WPF app debugged from the window, as an Avalonia one is in LiveTreeTests and BindingErrorsTests:
/// tests/Debuggees/WpfTreeApp, written into a folder of its own with a project file for the tests' own target, restored from
/// the run's own feed, built and launched by Debug as a person's app is, with Passenger's WPF inspector in it. Its window
/// shows on the screen (WPF has no headless platform), and its standard input stands in for the person's pointer ("click
/// X,Y", typed into the Debug window's console, which the app sends its window as Windows sends a mouse's messages). WPF runs
/// on Windows alone, so elsewhere the tests skip. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class WpfLiveTreeTests : IClassFixture<RestoredFolders>, IDisposable
{
    private const string OnlyOnWindows = "WPF runs on Windows alone";

    /// <summary>The variable that has the WPF inspector in the app write each step of its start to a file.</summary>
    private const string InspectorTrace = "PASSENGER_INSPECTOR_TRACE";

    private readonly string folder = string.Empty;
    private readonly string? traceBefore = Environment.GetEnvironmentVariable(InspectorTrace);

    public WpfLiveTreeTests(RestoredFolders folders)
    {
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
        Environment.SetEnvironmentVariable(InspectorTrace, Trace);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(InspectorTrace, traceBefore);
        if (folder.Length > 0)
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The file the inspector in the app traces its steps to.</summary>
    private string Trace => Path.Combine(folder, "inspector.log");

    private string Xaml => Path.Combine(folder, "MainWindow.xaml");

    private string AppXaml => Path.Combine(folder, "App.xaml");

    [AvaloniaFact]
    public void A_busy_wpf_ui_refuses_input_with_its_actual_reason_and_never_delivers_the_click_after_recovery()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string blocked = Path.Combine(folder, "blocked");
        string release = Path.Combine(folder, "release");
        string code = Path.Combine(folder, "MainWindow.xaml.cs");
        File.WriteAllText(code, File.ReadAllText(code).Replace("InitializeComponent();", $$"""
            InitializeComponent();
            Count.Click += (_, _) =>
            {
                System.IO.File.WriteAllText({{JsonSerializer.Serialize(blocked)}}, "blocked");
                while (!System.IO.File.Exists({{JsonSerializer.Serialize(release)}}))
                    System.Threading.Thread.Sleep(5);
            };
            Delete.Click += (_, _) => Hello.Text = "Delete handled";
            """, StringComparison.Ordinal));
        MainWindow window = Debugged();
        LiveApp app = window.LiveApp!;
        long count = window.LiveTree.Rows.Single(r => r.Element.Name == "Count").Element.Id;
        long delete = window.LiveTree.Rows.Single(r => r.Element.Name == "Delete").Element.Id;
        try
        {
            Task<InputDone> clicked = app.InputAsync(new InputAction(InputKinds.Click) { Element = count });
            Ui.PumpUntil(() => clicked.IsCompleted, "the click that starts the app's busy work to answer", TimeSpan.FromSeconds(4));
            _ = clicked.GetAwaiter().GetResult();
            Assert.True(File.Exists(blocked), "The app's actual click handler did not enter its blocked UI work");
            Stopwatch took = Stopwatch.StartNew();
            Task<InputDone> rejected = app.InputAsync(new InputAction(InputKinds.Click) { Element = delete });
            Ui.PumpUntil(() => rejected.IsCompleted, "the blocked UI input to be refused", TimeSpan.FromSeconds(4));
            LiveAppException refused = Assert.Throws<LiveAppException>(() => rejected.GetAwaiter().GetResult());
            Assert.Contains("UI is busy: input was not sent", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("popup", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(took.Elapsed < TimeSpan.FromSeconds(4), $"A blocked UI held an input request for {took.Elapsed}");
            File.WriteAllText(release, "continue");
            List<Element> recovered = [.. Ui.Await(app.AgentTreeAsync([]), "the recovered app's tree").Roots.SelectMany(Flatten)];
            Assert.Equal("Nobody greeted", recovered.Single(e => e.Name == "Hello").Text);
            Assert.Equal("Clicked once", recovered.Single(e => e.Name == "Counter").Text);
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = delete }), "a fresh explicit click after recovery");
            List<Element> retried = [.. Ui.Await(app.AgentTreeAsync([]), "the retried app's tree").Roots.SelectMany(Flatten)];
            Assert.Equal("Delete handled", retried.Single(e => e.Name == "Hello").Text);
        }
        finally
        {
            File.WriteAllText(release, "continue");
            Stop(window);
            window.Close();
        }

        static IEnumerable<Element> Flatten(Element element) => new[] { element }.Concat(element.Children.SelectMany(Flatten));
    }

    [AvaloniaFact]
    public void A_debugged_wpf_app_shows_its_elements_where_each_value_comes_from_and_goes_to_their_xaml()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = Debugged();
        LiveTreeView tree = window.LiveTree;
        Assert.True(window.DockHost.IsShown("liveTree"), "The Live Visual Tree did not open as the app's window came up");
        Assert.Equal("MainWindow", tree.Rows[0].Element.Type);
        Assert.Contains(tree.Rows, r => r.Element is { Type: "StackPanel", Name: "Panel" });
        Assert.Contains(tree.Rows, r => r.Element is { Type: "TextBlock", Name: "Greeting" });
        Assert.Contains(tree.Rows, r => r.Element is { Type: "Button", Name: "Delete" });
        Assert.True(window.LiveApp is { Kind: LiveAppKind.Wpf } && window.LiveApp.Framework.StartsWith("WPF ", StringComparison.Ordinal),
            $"The app was taken for {window.LiveApp?.Kind}, {window.LiveApp?.Framework}");

        // A value set on the element is local; a button's background comes from its default style, the theme's.
        ElementDetails delete = Choose(window, "Delete");
        PropertyValue deleteBackground = delete.Properties.Single(p => p.Name == "Background");
        Assert.True(deleteBackground.Source == ValueSources.Local, $"Delete's Background came from {deleteBackground.Source} {deleteBackground.Detail}, not its local value");
        ElementDetails count = Choose(window, "Count");
        PropertyValue countBackground = count.Properties.Single(p => p.Name == "Background");
        Assert.True(countBackground.Source == ValueSources.Theme, $"Count's Background came from {countBackground.Source} {countBackground.Detail}, not Button's default style");

        // The implicit Style for Button in App.xaml gives its margin, linked to its setter's line there.
        PropertyValue margin = count.Properties.Single(p => p.Name == "Margin");
        Assert.True(margin.Source == ValueSources.Style, $"Count's Margin came from {margin.Source} {margin.Detail}, not the implicit Style for Button");
        Assert.Equal((AppXaml, Line(AppXaml, "Property=\"Margin\"")), (PathNames.Canonical(margin.Place!.File), margin.Place.Line));
        StyleReport implicitStyle = count.Styles.Single(s => s.Kind == StyleKinds.Style);
        Assert.Equal(("Style TargetType=Button", Line(AppXaml, "<Style TargetType=\"Button\">")), (implicitStyle.Selector, implicitStyle.Place?.Line));
        // Its trigger does not apply while the pointer is elsewhere, and says why.
        StyleReport hover = count.Styles.Single(s => s.Kind == StyleKinds.Trigger && s.Selector == "Trigger IsMouseOver=True");
        Assert.True(hover is { Applies: false, WhyNot: "IsMouseOver is False, not True" }, $"The IsMouseOver trigger applies {hover.Applies}: {hover.WhyNot}");
        Assert.Contains(hover.Setters, s => s.Property == "Foreground" && s.State == SetterStates.Inactive);

        // A keyed Style's setter gives the greeting its weight, and the window's FontSize reaches it by inheritance.
        ElementDetails greeting = Choose(window, "Greeting");
        PropertyValue weight = greeting.Properties.Single(p => p.Name == "FontWeight");
        Assert.True(weight.Source == ValueSources.Style, $"Greeting's FontWeight came from {weight.Source} {weight.Detail}, not its Style");
        Assert.Equal((AppXaml, Line(AppXaml, "Property=\"FontWeight\"")), (PathNames.Canonical(weight.Place!.File), weight.Place.Line));
        PropertyValue size = greeting.Properties.Single(p => p.Name == "FontSize");
        Assert.True(size is { Source: ValueSources.Inherited, Value: "15" } && size.Detail == "From MainWindow (its local value)",
            $"Greeting's FontSize {size.Value} came from {size.Source} {size.Detail}, not the window's local value");
        // Its bounds, in its window and in its parent.
        Assert.True(greeting.Layout.InWindow is { X: >= 12, Y: >= 12, Width: > 0, Height: > 0 }, $"Greeting's box in its window is {greeting.Layout.InWindow}");
        Assert.True(greeting.Layout.Bounds.Width > 0 && greeting.Layout.Margin == new Edges(0, 0, 0, 0), $"Greeting's layout: {greeting.Layout}");

        // Go to source lands on the element in its XAML, from WPF's own record of where it was made.
        Choose(window, "Delete");
        Assert.NotNull(tree.GoToSource);
        Click(tree.GoToSource!);
        Assert.Equal(Xaml, window.EditorArea.Active?.Path);
        Assert.Equal(Line(Xaml, "x:Name=\"Delete\"") - 1, window.EditorArea.CodeEditor.CaretPosition.Line);

        // Paused, the app answers nothing: the tree stays as it last ran and says so, until the app runs again.
        Ui.Run(window, AppActions.Pause);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused, "the app to pause");
        Ui.WaitFor(() => tree.BannerText is not null, "the tree to say the app is paused");
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => tree.BannerText is null, "the tree to be live again once the app runs");

        Stop(window);
        Assert.Empty(tree.Rows);
        window.Close();
    }

    [AvaloniaFact]
    public void A_debugged_wpf_apps_misspelt_binding_shows_in_the_debug_console_linked_to_its_line_and_in_problems()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = Opened();
        Assert.Null(Ui.Await(window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4)));
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

        // WPF's System.Windows.Data Error, its place first and linked: a click opens MainWindow.xaml at the misspelt member.
        Assert.StartsWith($"MainWindow.xaml({Misspelt.Line + 1},{Misspelt.Column + 1}): binding error: TextBlock#Caption.Text to Nmae: ", line, StringComparison.Ordinal);
        Assert.Contains("'Nmae' property not found", line, StringComparison.Ordinal);
        Assert.Contains("System.Windows.Data Error: 40", line, StringComparison.Ordinal);
        Assert.Contains("Did you mean Name?", line, StringComparison.Ordinal);
        ClickLink(window, console, line);
        Assert.Equal((Xaml, Misspelt), (window.EditorArea.Active?.Path, window.EditorArea.CodeEditor.CaretPosition));

        // In Problems' Bindings, once, with its element, property, path and count, at the misspelt member.
        ProblemsView problems = window.BottomPanel.Problems;
        BindingProblem caption = Assert.Single(problems.BindingProblems);
        Assert.Equal(("TextBlock#Caption", "Text", "Nmae", 1), (caption.Failure.Control, caption.Failure.Property, caption.Failure.Path, caption.Failure.Count));
        Assert.Equal((Xaml, Misspelt), (caption.Problem.Path, caption.Problem.Position));
        Assert.Equal(["Name"], caption.Failure.Nearest);
        Assert.Equal(Line(Xaml, "x:Name=\"Caption\""), caption.Failure.Place?.Line);

        // Failing again, it is counted, not listed again.
        Say(window, "rebind");
        Ui.WaitFor(() => problems.BindingProblems.SingleOrDefault()?.Failure.Count == 2, "the caption's failure to be counted twice");
        Assert.Single(problems.BindingProblems);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        window.Close();
    }

    [AvaloniaFact]
    public void Xaml_binding_errors_stops_a_debugged_wpf_app_where_its_binding_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = Opened();
        BreakpointStore store = window.Debugger.Breakpoints;
        store.Update(store.BindingErrors, enabled: true, suspend: true, null, null, null);
        Assert.True(window.Debugger.Breakpoints.BindingErrors.Enabled, "XAML Binding Errors did not turn on");

        // WPF's binding engine fails the caption's binding with none of the app's code on the stack: the stop shows the XAML.
        string engineLog = Path.Combine(folder, "engine.log");
        string? kept = Environment.GetEnvironmentVariable("PASSENGER_ENGINE_LOG");
        Environment.SetEnvironmentVariable("PASSENGER_ENGINE_LOG", engineLog);
        DebugStop stop;
        try
        {
            Assert.Null(Ui.Await(window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4)));
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
            Environment.SetEnvironmentVariable("PASSENGER_ENGINE_LOG", kept);
        }
        string frames = string.Join(" < ", stop.Frames.Take(12).Select(f => $"{f.Name} {Path.GetFileName(f.Source?.Path)}:{f.Line}"));
        Assert.True(stop.Binding is { } binding && (binding.Problem.Path, binding.Problem.Position) == (Xaml, Misspelt),
            $"The stop was not taken for the caption's binding error at MainWindow.xaml {Misspelt}: {stop.Binding?.Problem.Path} {stop.Binding?.Problem.Position} ({stop.Reason}; {frames})");
        Ui.WaitFor(() => window.EditorArea.Active?.Path == Xaml && window.EditorArea.CodeEditor.CaretPosition == Misspelt,
            $"the editor to show the misspelt member, not {window.EditorArea.Active?.Path} {window.EditorArea.CodeEditor.CaretPosition} ({frames})");
        Ui.WaitFor(() => window.Status?.Contains("paused on a binding error at MainWindow.xaml", StringComparison.Ordinal) == true,
            $"the status bar to say the binding error, not \"{window.Status}\"", TimeSpan.FromSeconds(20));
        // $bindingError above the frame's values, as an Avalonia app's is, opening into the failure.
        Assert.Equal("\"Nmae\"", Member(window, "$bindingError", "Path"));
        Assert.Equal("\"Nmae\"", Member(window, "$bindingError", "FailedPart"));
        Assert.Contains("MainViewModel", Member(window, "$bindingError", "DataContext"), StringComparison.Ordinal);
        Assert.Contains("TextBlock", Member(window, "$bindingError", "Control"), StringComparison.Ordinal);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        window.Close();
    }

    [AvaloniaFact]
    public void Select_element_chooses_the_element_clicked_in_a_wpf_app()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = Debugged();
        LiveTreeView tree = window.LiveTree;
        Box count = Choose(window, "Count").Layout.InWindow!;
        Choose(window, "Greeting");

        ClickOn(window, tree.SelectElement);
        Assert.True(tree.SelectElement.IsChecked, "Select Element did not stay on");
        // Just inside the Count button's edge, short of its text: the border its template draws.
        Say(window, $"click {(count.X + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)},{(count.Y + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
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

    /// <summary>The folder opened in a window.</summary>
    private MainWindow Opened()
    {
        MainWindow window = new() { Width = 1400, Height = 900 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>The folder opened in a window and its app launched by Debug, its tree shown.</summary>
    private MainWindow Debugged()
    {
        MainWindow window = Opened();
        Assert.Null(Ui.Await(window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4)));
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

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Settle();
    }

    /// <summary>A click on a control of the window, where it is drawn.</summary>
    private static void ClickOn(MainWindow window, Control control)
    {
        Point centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
        window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
        Ui.Settle();
    }

    /// <summary>A line of the app's input, typed into the Debug window's console.</summary>
    private static void Say(MainWindow window, string line)
    {
        window.ShowToolWindow("debug");
        window.BottomPanel.Debug.ShowTab(console: true);
        Ui.Settle();
        OutputView console = window.BottomPanel.Debug.Console;
        Point inside = console.Editor.TranslatePoint(new Point(console.Editor.Bounds.Width / 2, console.Editor.Bounds.Height / 2), window)!.Value;
        window.MouseDown(inside, Avalonia.Input.MouseButton.Left);
        window.MouseUp(inside, Avalonia.Input.MouseButton.Left);
        window.KeyTextInput(line);
        Ui.Press(window, "ENTER");
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

    /// <summary>A click on a console line's link, at its first character, as a person clicks it.</summary>
    private static void ClickLink(MainWindow window, OutputView console, string line)
    {
        CodeEditor editor = console.Editor;
        int offset = editor.Document.Snapshot.ToString().IndexOf(line, StringComparison.Ordinal);
        Point top = editor.PointOf(offset);
        Point at = editor.TranslatePoint(top + new Vector(2, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(at);
        Ui.Settle();
        Assert.True(editor.HoveredLink is { Target: FileLink }, $"The pointer rests on the line's place but nothing is underlined:\n{line}");
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Ui.Settle();
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
        DebugView view = window.BottomPanel.Debug;
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
