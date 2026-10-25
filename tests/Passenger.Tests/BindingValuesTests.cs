using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>A debugged Avalonia app's binding values in its .axaml file, after each binding and on hover:
/// tests/Debuggees/BindingValuesApp, written into a folder of its own with a project file on the tests' own Avalonia,
/// headless, "rename" typed into the Debug window's console having its code-behind change the customer's name on the UI
/// thread, which it names UI, as an app may (the debugger marks Main's thread whatever its name). Debugged through the
/// window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class BindingValuesTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public BindingValuesTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("bindingvalues-", made => AvaloniaApp.Debuggee(made, "BindingValuesApp")));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private string Xaml => Path.Combine(folder, "MainWindow.axaml");

    private string CodeBehind => Path.Combine(folder, "MainWindow.axaml.cs");

    [AvaloniaFact]
    public void A_debugged_apps_view_shows_its_bindings_values_after_them_and_on_hover_paused_and_running_with_a_control_chosen()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        // Where rename has changed the name, on the app's UI thread.
        window.Debugger.Breakpoints.Toggle(CodeBehind, Line(CodeBehind, "Console.WriteLine"));
        Assert.Null(window.OpenFile(Xaml));
        Ui.Settle();
        Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
        Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element.Name == "Customer"),
            $"the Live Visual Tree to show the app's window ({window.LiveApp?.State} {window.LiveApp?.Problem})", TimeSpan.FromMinutes(2));
        Ui.TypeToDebugged(window, "rename");
        Stopped(window, "the stop after rename");

        Assert.Null(window.OpenFile(Xaml));
        CodeEditor editor = window.EditorArea.CodeEditor;
        int customer = Line(Xaml, "x:Name=\"Customer\"");
        Ui.WaitFor(() => Value(editor, customer) is not null, $"the customer's name after its binding ({window.Status})", TimeSpan.FromMinutes(1));
        Assert.Equal("Text: \"Grace Hopper\"", Value(editor, customer));
        Assert.Equal("Title: \"Binding Values\"", Value(editor, Line(Xaml, "Title=")));
        Assert.Equal("Text: \"3\"", Value(editor, Line(Xaml, "x:Name=\"Orders\"")));
        Assert.Equal("Text: \"VIP\"", Value(editor, Line(Xaml, "{TemplateBinding Content}")));
        Assert.StartsWith("Background: ", Value(editor, Line(Xaml, "{TemplateBinding Background}")), StringComparison.Ordinal);
        // A collection expression's list shows the display Roslyn gives it: its interface's Count.
        Assert.Equal("ItemsSource: Count = 3", Value(editor, Line(Xaml, "ItemsSource=")));
        int broken = Line(Xaml, "x:Name=\"Broken\"");
        LineAnnotation failed = Assert.Single(editor.LineAnnotations, a => a.Line == broken);
        Assert.Equal(LineAnnotationKind.Failed, failed.Kind);
        Assert.StartsWith("Text: binding failed at Nmae: ", failed.Text, StringComparison.Ordinal);
        int row = Line(Xaml, "<TextBlock Text=\"{Binding}\"");
        Assert.Equal("Text: made 3 times, choose one in the Live Visual Tree", Value(editor, row));

        string card = Hover(window, editor, "Customer.Name");
        Assert.Contains("TextBlock#Customer.Text = {string} \"Grace Hopper\"", card, StringComparison.Ordinal);
        Assert.Contains("DataContext = {BindingValuesApp.MainViewModel}", card, StringComparison.Ordinal);
        Assert.Contains("→ Customer = {BindingValuesApp.Customer}", card, StringComparison.Ordinal);
        Assert.Contains("→ Name = {string} \"Grace Hopper\"", card, StringComparison.Ordinal);
        card = Hover(window, editor, "Customer.Orders");
        Assert.Contains("→ Orders = {int} 3", card, StringComparison.Ordinal);
        card = Hover(window, editor, "Nmae");
        Assert.Contains("Binding failed at Nmae: Could not find a matching property accessor for 'Nmae'", card, StringComparison.Ordinal);
        Assert.Contains("TextBlock#Broken.Text = {string} null", card, StringComparison.Ordinal);
        Assert.Contains("→ Nmae: failed here", card, StringComparison.Ordinal);

        LiveTreeView tree = window.LiveTree;
        tree.Tree.SelectedItem = tree.Rows.Where(r => r.Element is { Type: "TextBlock", Name: null, FromTemplate: false }).ElementAt(1);
        Ui.WaitFor(() => Value(editor, row) == "Text: \"Two\"", $"the chosen row's value, not {Value(editor, row)}", TimeSpan.FromMinutes(1));
        card = Hover(window, editor, "{Binding}");
        Assert.Contains("TextBlock.Text = {string} \"Two\"\nDataContext = {string} \"Two\"", card, StringComparison.Ordinal);

        Resume(window);
        Ui.WaitFor(() => Value(editor, row) == "Text: \"Two\"" && Value(editor, customer) == "Text: \"Grace Hopper\"",
            $"the running app's values ({Value(editor, customer)}, {Value(editor, row)})", TimeSpan.FromMinutes(1));
        window.Debugger.Breakpoints.Toggle(CodeBehind, Line(CodeBehind, "Console.WriteLine"));
        Ui.Settle();
        Ui.TypeToDebugged(window, "rename");
        Ui.WaitFor(() => Value(editor, customer) == "Text: \"Ada Lovelace\"", $"the renamed customer's name, not {Value(editor, customer)}", TimeSpan.FromMinutes(1));

        // A stop on another thread, while the UI thread waits where the runtime evaluates nothing.
        string program = Path.Combine(folder, "Program.cs");
        window.Debugger.Breakpoints.Toggle(program, Line(program, "if (line.Trim() != \"rename\")"));
        Ui.Settle();
        Ui.TypeToDebugged(window, "rename");
        Stopped(window, "the stop on the input thread");
        Assert.Null(window.OpenFile(Xaml));
        Ui.WaitFor(() => editor.LineAnnotations.Any(a => a.Text == "binding values: shown at a stop on the app's UI thread, not this one"),
            $"the file to say why it shows no values, not {string.Join(" | ", editor.LineAnnotations.Select(a => a.Text))}", TimeSpan.FromMinutes(1));
        card = Hover(window, editor, "Customer.Name");
        Assert.Contains("This stop is on another thread than the app's UI thread", card, StringComparison.Ordinal);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        Ui.WaitFor(() => editor.LineAnnotations.Count == 0, "the values to go with the app");
        window.Close();
    }

    /// <summary>The binding values after a line (from zero), as the editor shows them; null for none.</summary>
    private static string? Value(CodeEditor editor, int line) =>
        editor.LineAnnotations.Where(a => a.Line == line).Select(a => a.Text).FirstOrDefault();

    /// <summary>The card shown with the pointer resting on some text of the editor's.</summary>
    private static string Hover(MainWindow window, CodeEditor editor, string text)
    {
        Ui.Press(window, "ESCAPE");
        window.MouseMove(new Point(0, 0), RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the last card to go");
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
        Point point = editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, $"the card on {text}");
        return window.Assist.HoverText!;
    }

    private static void Stopped(MainWindow window, string what)
    {
        Ui.WaitFor(() => window.Debugger.Session?.State is DebugState.Paused or DebugState.Ended && (window.Debugger.Stop is { } || window.Debugger.Session.State == DebugState.Ended),
            what, TimeSpan.FromMinutes(1));
        Assert.True(window.Debugger.Stop is not null, $"The program ended before {what}: {window.Status}");
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

    /// <summary>The line (from zero) of a file holding some text, so a test names what it means rather than a number.</summary>
    private static int Line(string file, string text) => Array.FindIndex(File.ReadAllLines(file), l => l.Contains(text, StringComparison.Ordinal));
}
