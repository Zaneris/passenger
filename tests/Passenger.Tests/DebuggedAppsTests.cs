using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>Two Avalonia apps debugged at once from one window, each with its inspector in it: tests/Debuggees/BindingErrorsApp
/// and LiveTreeApp, written into a folder of their own with project files on the tests' own Avalonia and a solution over
/// both, headless. Each keeps its Live Visual Tree, binding values and failed bindings for as long as its Debug tab is
/// there, the window showing the chosen tab's. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class DebuggedAppsTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public DebuggedAppsTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("apps-", WriteTo));
    }

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Both apps written into a folder, each in a folder of its own, and a solution over them, whose path it gives.</summary>
    internal static string WriteTo(string folder)
    {
        foreach (string app in (string[])["BindingErrorsApp", "LiveTreeApp"])
        {
            AvaloniaApp.Debuggee(Directory.CreateDirectory(Path.Combine(folder, app)).FullName, app);
        }
        string solution = Path.Combine(folder, "Apps.slnx");
        File.WriteAllText(solution, "<Solution>\n  <Project Path=\"BindingErrorsApp/BindingErrorsApp.csproj\" />\n  <Project Path=\"LiveTreeApp/LiveTreeApp.csproj\" />\n</Solution>\n");
        return solution;
    }

    /// <summary>The first app's window, which binds its greeting and misspells its caption's Name.</summary>
    private string Xaml => Path.Combine(folder, "BindingErrorsApp", "MainWindow.axaml");

    [AvaloniaFact]
    public void Two_apps_debugged_at_once_each_keep_their_live_visual_tree_binding_values_and_failed_bindings_as_their_tab_is_chosen()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        Assert.Null(window.OpenFile(Xaml));
        CodeEditor editor = window.EditorArea.CodeEditor;
        int greeting = Line(Xaml, "x:Name=\"Greeting\"");
        try
        {
            Debug(window, "BindingErrorsApp", "Caption");
            ProblemsModel problems = window.BottomPanel.Problems;
            Ui.WaitFor(() => problems.BindingProblems.Any(p => p.Failure.Path == "Nmae"), "the first app's caption to fail in Problems");
            Choose(window, "Greeting");
            Ui.WaitFor(() => Value(editor, greeting) == "Text: \"Hello\"", $"the first app's greeting after its binding, not {Value(editor, greeting)}", TimeSpan.FromMinutes(1));
            LiveApp first = window.Session.LiveApp!;

            Debug(window, "LiveTreeApp", "Delete");
            Assert.DoesNotContain(window.LiveTree.Rows, r => r.Element.Name == "Caption");
            LiveApp second = window.Session.LiveApp!;
            Assert.NotSame(first, second);
            Assert.True(first.State == LiveAppState.Connected,
                $"Debugging a second app cut the first off from its inspector: it is {first.State} ({first.Problem})");
            Assert.True(problems.BindingProblems.Any(p => p.Failure.Path == "Nmae"),
                "Debugging a second app took the first's failed binding out of Problems while the first is still debugged");
            Ui.WaitFor(() => Value(editor, greeting) is null, $"the greeting's value to go with the first app's tab, not to stay as {Value(editor, greeting)}");

            ChooseTab(window, "BindingErrorsApp");
            Ui.WaitFor(() => window.Session.LiveApp == first && window.LiveTree.Rows.Any(r => r.Element.Name == "Caption"),
                $"the Live Visual Tree to show the first app's window again ({window.LiveApp?.Name} {window.LiveApp?.State})", TimeSpan.FromMinutes(1));
            Assert.DoesNotContain(window.LiveTree.Rows, r => r.Element.Name == "Delete");
            Choose(window, "Greeting");
            Ui.WaitFor(() => Value(editor, greeting) == "Text: \"Hello\"", $"the first app's greeting again, not {Value(editor, greeting)}", TimeSpan.FromMinutes(1));

            ChooseTab(window, "LiveTreeApp");
            Ui.WaitFor(() => window.Session.LiveApp == second && window.LiveTree.Rows.Any(r => r.Element.Name == "Delete"),
                "the Live Visual Tree to show the second app's window again", TimeSpan.FromMinutes(1));

            Assert.True(window.Perform(AppActions.StopAll), $"Stop All could not run: {Ui.Said(window)}");
            Ui.WaitFor(() => !window.Debugger.IsDebugging && first.State == LiveAppState.Ended && second.State == LiveAppState.Ended, "both apps to end");
            Assert.Contains(problems.BindingProblems, p => p.Failure.Path == "Nmae");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Debugs a run configuration beside whatever is debugged, and waits for the Live Visual Tree to show its window
    /// by an element of it.</summary>
    internal static void Debug(MainWindow window, string configuration, string element)
    {
        Assert.True(window.SelectConfiguration(configuration), $"No run configuration {configuration}: {string.Join(", ", window.Configurations.Select(c => c.Name))}");
        Ui.Launched(window, window.DebugAsync(), $"{configuration} to build and start under the debugger", TimeSpan.FromMinutes(4));
        Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element.Name == element),
            $"the Live Visual Tree to show {configuration}'s window ({window.LiveApp?.Name} {window.LiveApp?.State} {window.LiveApp?.Problem})", TimeSpan.FromMinutes(2));
    }

    /// <summary>Chooses the element of a name in the tree, as a click on its row does, and waits for its details.</summary>
    private static void Choose(MainWindow window, string name)
    {
        LiveTreeView tree = window.LiveTree;
        tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == name);
        Ui.WaitFor(() => tree.Details?.Name == name, $"{name}'s details");
    }

    /// <summary>A click on a program's tab in the Debug window's header, as a person picks the one shown.</summary>
    private static void ChooseTab(MainWindow window, string name)
    {
        window.ShowToolWindow(ToolWindowIds.Debug);
        Ui.Settle();
        ToggleButton tab = window.DebugWindow.HeaderTools.GetVisualDescendants().OfType<ToggleButton>()
            .First(button => button.GetVisualDescendants().OfType<TextBlock>().First().Text == name);
        Ui.Click(tab);
        Assert.True(window.Debugger.Chosen?.Name == name, $"A click on {name}'s tab chose {window.Debugger.Chosen?.Name}");
    }

    /// <summary>The binding values after a line (from zero), as the editor shows them; null for none.</summary>
    private static string? Value(CodeEditor editor, int line) =>
        editor.LineAnnotations.Where(a => a.Line == line).Select(a => a.Text).FirstOrDefault();

    /// <summary>The line (from zero) of a file holding some text.</summary>
    private static int Line(string file, string text) => Array.FindIndex(File.ReadAllLines(file), l => l.Contains(text, StringComparison.Ordinal));
}
