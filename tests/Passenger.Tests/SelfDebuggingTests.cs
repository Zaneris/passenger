using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Passenger debugging itself from its window, as its own developer does: opened on its repository, it builds
/// Passenger and launches it under its debugger on a state folder of its own, and stops it in its UI code as a key is
/// pressed there. The inner Passenger is a screenshot's, its steps its keys, since a test cannot click a window of
/// another process; it opens a project of its own, so it has its own language server under it.</summary>
[Collection(Alone.Name)]
public sealed class SelfDebuggingTests : IDisposable
{
    private readonly string folder = TestFolders.Create("inner-");
    private readonly string state = TestFolders.Create("outer-");

    public SelfDebuggingTests()
    {
        File.WriteAllText(Path.Combine(folder, "Sum.csproj"),
            TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(1 + 2);\n");
        TestFolders.Prepare(() => Restore.Run(folder), folder, state);
    }

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(state);
    }

    [AvaloniaFact]
    public void Passenger_opened_on_its_repository_debugs_itself_on_a_state_of_its_own_stopping_stepping_and_evaluating_in_its_ui_code()
    {
        string repository = Debugging.Helpers.Debuggees.Root;
        // Debugging asks nothing of the outer window's C# language server; the inner Passenger starts its own.
        MainWindow window = new() { Width = 1400, Height = 900, StateFolder = state, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(repository);
        // Passenger's whole solution, which a small machine evaluates in longer than a key may settle in.
        Ui.PumpUntil(() => BackgroundWork.Pending == 0, "Passenger's solution to load", TimeSpan.FromMinutes(5));
        Ui.Settle();
        string app = PathNames.Canonical(Path.Combine(repository, "src", "Passenger.App", "Passenger.App.csproj"));
        Assert.True(window.Configuration?.Project == app, $"Opened on its repository, Passenger would debug {window.Configuration?.Name}, not itself");

        // Where the status bar is told why a key's action cannot run: F10 with nothing being debugged, in the inner Passenger.
        string actions = PathNames.Canonical(Path.Combine(repository, "src", "Passenger.App", "MainWindow.Actions.cs"));
        int line = Array.FindIndex(File.ReadAllLines(actions), l => l.Trim() == "status.Text = why;");
        Assert.True(line >= 0, "MainWindow.Actions.cs no longer tells the status bar why an action cannot run");
        window.OpenFile(actions);
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(line, 8));
        Ui.Run(window, AppActions.ToggleBreakpoint);

        string picture = Path.Combine(folder, "inner.png");
        Task<string?> launched = window.DebugAsync($"\"{folder}\" --screenshot \"{picture}\" --key F10 --key F10 --wait 600000");
        Ui.Launched(window, launched, "Passenger to build and start under its debugger", TimeSpan.FromMinutes(5));
        DebugSession session = window.Debugger.Session!;
        Ui.WaitFor(() => session.State == DebugState.Paused || !window.Debugger.IsDebugging, "the inner Passenger to stop at the breakpoint in its key's action", TimeSpan.FromMinutes(3));
        Assert.True(session.State == DebugState.Paused, $"The inner Passenger ended without stopping: {window.Status}\n{Console(window)}");
        Ui.WaitFor(() => Value(window, "why") is not null, "the stop's frames and locals");
        DapStackFrame top = window.BottomPanel.Debug.Frames[0];
        Assert.True(top.Source?.Path == actions && top.Line == line + 1, $"The inner Passenger stopped at {top.Source?.Path}:{top.Line}, not at the breakpoint");
        Assert.Contains(new LineMark(line, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Equal("\"app.debug.stepOver\"", Value(window, "action"));
        Assert.Equal("\"Nothing is being debugged\"", Value(window, "why"));

        Ui.Run(window, AppActions.StepOver);
        Ui.WaitFor(() => session.State == DebugState.Paused && editor.LineMarks.Contains(new LineMark(line + 1, LineMarkKind.ExecutionPoint)), "the step to the next line");

        Ui.Run(window, AppActions.Evaluate);
        window.KeyTextInput("StatusText.Text");
        Ui.Press(window, "Enter");
        Ui.WaitFor(() => Value(window, "StatusText.Text") is { Length: > 0 }, "the inner window's status bar text evaluated");
        Assert.Equal("\"Nothing is being debugged\"", Value(window, "StatusText.Text"));

        int stops = 0;
        session.StateChanged += (_, _) => stops += session.State == DebugState.Paused ? 1 : 0;
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => stops > 0 || !window.Debugger.IsDebugging, "the inner Passenger to take its next key", TimeSpan.FromMinutes(2));
        Assert.True(session.State == DebugState.Paused, $"The inner Passenger did not stop at its next key: {window.Status}\n{Console(window)}");

        // Its state is its own, in the sandbox under the outer's.
        string sandbox = Path.Combine(state, "sandbox");
        string opened = $"Opened {PathNames.Canonical(folder)}";
        Assert.Contains(opened, Ui.Shared(Path.Combine(sandbox, "logs", "passenger.log")), StringComparison.Ordinal);
        Assert.DoesNotContain(opened, Ui.IdeLog(), StringComparison.Ordinal);
        int inner = session.ProcessStarted.Result;
        IReadOnlyList<int> children = Processes.Descendants(inner);
        int[] servers = [.. Directory.GetFiles(Path.Combine(sandbox, "logs", "roslyn"), "server-*.log").Select(ServerProcess)];
        Assert.True(servers.Length > 0 && servers.All(children.Contains),
            $"The inner Passenger's language servers ({string.Join(", ", servers)}) are not among what it started ({string.Join(", ", children)})");

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        Assert.True(Processes.Gone(inner), $"Stop left the inner Passenger running: {Processes.Describe(inner)}");
        foreach (int child in children)
        {
            Assert.True(Processes.Gone(child), $"Stop left what the inner Passenger started running: {Processes.Describe(child)}");
        }
        window.Close();
    }

    private static string? Value(MainWindow window, string name) =>
        window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name)?.Value;

    private static string Console(MainWindow window) => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();

    /// <summary>The process a language server's log is named for: server-DATE-TIME-PROCESS.log.</summary>
    private static int ServerProcess(string log) =>
        int.Parse(Path.GetFileNameWithoutExtension(log).Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
}
