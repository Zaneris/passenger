using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Settings;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The keys IntelliJ IDEA's keymap gives two actions told apart by where they are pressed: one action takes the
/// key, the other takes it where it is (<see cref="AppActions.KeysIn"/>): Generate's in the Explorer is New Item, View
/// Breakpoints' on a breakpoint's line Edit Breakpoint, and on the Mac Replace's Cmd+R in the Run window Rerun.</summary>
public sealed class IntelliJPlaceKeysTests : IDisposable
{
    private static readonly bool Mac = OperatingSystem.IsMacOS();

    private readonly string folder = PathNames.Canonical(TestFolders.Create("intellij-places-"));

    public IntelliJPlaceKeysTests()
    {
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console());
        string runs = Path.Combine(folder, "runs.txt").Replace("\\", "\\\\", StringComparison.Ordinal);
        TestFolders.Write(folder, "Hello/Program.cs", $"System.IO.File.AppendAllText(\"{runs}\", \"run\\n\");\nint total = 1;\ntotal += 2;\nSystem.Console.WriteLine(total);\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Generates_key_in_the_Explorer_opens_New_Item_and_in_the_editor_does_not()
    {
        MainWindow window = Open();
        try
        {
            string generate = Mac ? "meta N" : "alt INSERT";
            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, generate);
            Assert.False(window.OwnedWindows.OfType<NewItemDialog>().Any(d => d.IsVisible), $"{generate} in the editor opened New Item, where it is Generate's");

            window.ExplorerView.Reveal(Path.Combine(folder, "Hello", "Program.cs"));
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == Path.Combine(folder, "Hello", "Program.cs")));
            Ui.Press(window, generate);
            Ui.WaitFor(() => window.OwnedWindows.OfType<NewItemDialog>().Any(d => d.IsVisible), $"{generate} in the Explorer to open New Item");
            window.OwnedWindows.OfType<NewItemDialog>().Single(d => d.IsVisible).Close();
            Ui.Settle();
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void View_Breakpoints_key_on_a_breakpoints_line_edits_it_and_elsewhere_lists_the_breakpoints()
    {
        MainWindow window = Open();
        try
        {
            string viewBreakpoints = Mac ? "meta shift F8" : "control shift F8";
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            editor.GoTo(new TextPosition(2, 0));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Assert.Single(window.Debugger.Breakpoints.In(Path.Combine(folder, "Hello", "Program.cs")));

            Ui.Press(window, viewBreakpoints);
            Assert.True(window.Popup is BreakpointPopup, $"{viewBreakpoints} on the breakpoint's line opened {window.Popup?.GetType().Name ?? "nothing"}, where it edits that breakpoint");
            Ui.Press(window, "ESCAPE");
            editor.Focus();
            editor.GoTo(new TextPosition(1, 0));
            Ui.Press(window, viewBreakpoints);
            Assert.True(window.Popup is BreakpointsWindow, $"{viewBreakpoints} on a line with no breakpoint opened {window.Popup?.GetType().Name ?? "nothing"}, where it lists the breakpoints");
            Ui.Press(window, "ESCAPE");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void On_the_Mac_Cmd_R_in_the_Run_window_reruns_and_in_the_editor_replaces()
    {
        Assert.SkipUnless(Mac, "Cmd+R is IntelliJ IDEA's Rerun in the Run window on the Mac alone; Ctrl+F5 is its own elsewhere");
        string runs = Path.Combine(folder, "runs.txt");
        MainWindow window = Open();
        try
        {
            Ui.Launched(window, window.RunAsync(), "Hello to build and run", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => Runs(runs) == 1 && window.RunEnd.IsCompleted, "Hello's run to end");

            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, "meta R");
            Assert.True(window.EditorArea.FindBar.IsVisible && Runs(runs) == 1, "Cmd+R in the editor ran Hello again or opened no Replace, where it replaces");
            Ui.Press(window, "ESCAPE");

            Assert.True(window.Perform(AppActions.ShowRun));
            Ui.Settle();
            Ui.Press(window, "meta R");
            Ui.WaitFor(() => Runs(runs) == 2 && window.RunEnd.IsCompleted, "Cmd+R in the Run window to run Hello again", TimeSpan.FromMinutes(2));
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }

        static int Runs(string file) => File.Exists(file) ? File.ReadAllLines(file).Length : 0;
    }

    /// <summary>The folder open with IntelliJ IDEA's keymap chosen and Program.cs shown.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.ApplySettings(window.Settings with { Keymap = KeymapPreset.IntelliJ });
        window.OpenFile(Path.Combine(folder, "Hello", "Program.cs"));
        Ui.Settle();
        return window;
    }
}
