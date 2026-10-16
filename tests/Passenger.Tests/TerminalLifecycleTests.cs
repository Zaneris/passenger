using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Terminal;

namespace Passenger.Tests;

/// <summary>Closing a real terminal program finishes its run, not just its visual tab or PTY handle.</summary>
[Collection(Alone.Name)]
public sealed class TerminalLifecycleTests : IDisposable
{
    private readonly string folder = TestFolders.Create("terminal-lifecycle-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Closing_stopping_and_shutting_down_real_terminal_runs_publish_one_actual_exit()
    {
        string project = PathNames.Canonical(Debugging.Helpers.Debuggees.CopyTo("TerminalConsoleApp", folder));
        Restore.Run(folder);
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            foreach (string end in new[] { "close", "stop-dispose", "close-all", "natural-dispose", "output-child-close" })
            {
                window.SetConfigurations([new RunConfiguration { Name = "Keys", Project = project, Console = ConsoleKind.Terminal, BuildBeforeLaunch = end == "close",
                    Arguments = end == "output-child-close" ? "output-child" : string.Empty, WorkingDirectory = folder }], "Keys");
                Assert.Null(Ui.Await(window.RunAsync(), "the terminal run to start", TimeSpan.FromMinutes(3)));
                TerminalRun run = Assert.IsType<TerminalRun>(window.RunInTerminal);
                TerminalSession session = run.Session;
                int pid = run.ProcessId;
                List<int> exits = [];
                session.Exited += (_, code) => exits.Add(code);
                int? child = null;
                if (end == "output-child-close")
                {
                    child = Processes.WrittenId(Path.Combine(folder, "terminal-child.pid"), "the child inheriting the terminal's output");
                    Ui.WaitFor(() => session.Screen.Scrollback.Count > 100, "sustained output through the inherited PTY handle");
                }
                else
                {
                    Ui.WaitFor(() => session.Screen.Lines.Any(line => line.Text().Contains("key?", StringComparison.Ordinal)), "the actual program to wait for keys");
                }
                Assert.True(Processes.IsRunning(pid));
                if (end is "close" or "output-child-close")
                {
                    Button close = Assert.Single(window.TerminalPanel.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("tabClose"));
                    Point at = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(at);
                    Ui.Settle();
                    window.MouseDown(at, MouseButton.Left);
                    window.MouseUp(at, MouseButton.Left);
                    Assert.Empty(window.TerminalPanel.Tabs);
                }
                else if (end == "close-all")
                {
                    Await(window.TerminalPanel.CloseAllAsync(), "all sessions to close");
                    Assert.Empty(window.TerminalPanel.Tabs);
                }
                else if (end == "stop-dispose")
                {
                    Ui.Run(window, AppActions.Stop);
                    Task first = session.DisposeAsync().AsTask();
                    Task second = session.DisposeAsync().AsTask();
                    Assert.Same(first, second);
                    Await(first, "the stopped session to dispose");
                }
                else
                {
                    window.KeyTextInput("q");
                    Ui.WaitFor(() => session.Screen.Lines.Any(line => line.Text().Contains("line?", StringComparison.Ordinal)), "the program to ask for its line");
                    window.KeyTextInput("finished");
                    Ui.Press(window, "ENTER");
                    Ui.WaitFor(() => run.Ended.IsCompleted, "the natural exit code");
                    Await(session.DisposeAsync().AsTask(), "the ended session to dispose");
                }
                Ui.WaitFor(() => run.Ended.IsCompleted && !session.IsRunning && Processes.Gone(pid), $"{end}: the real process and its run to end", TimeSpan.FromSeconds(10));
                if (child is { } descendant)
                {
                    Ui.WaitFor(() => Processes.Gone(descendant), "the output-writing child to exit too", TimeSpan.FromSeconds(10));
                }
                int actual = Assert.Single(exits);
                Assert.Equal(OperatingSystem.IsWindows() || end == "natural-dispose" ? actual : 137, run.Ended.Result);
                Assert.Equal(end == "natural-dispose", !run.WasStopped);
                if (end == "natural-dispose")
                {
                    Assert.Equal(3, actual);
                }
                else if (OperatingSystem.IsWindows())
                {
                    Assert.NotEqual(0, actual);
                }
                Await(session.DisposeAsync().AsTask(), "repeated disposal");
                Await(window.TerminalPanel.CloseAllAsync(), "repeated shutdown");
                Ui.Settle();
                Assert.Single(exits);
                Assert.False(run.IsRunning || window.IsRunning);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void Await(Task task, string what)
    {
        Ui.WaitFor(() => task.IsCompleted, what, TimeSpan.FromSeconds(30));
        task.GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void Immediate_terminal_shutdown_owns_created_shells_and_failed_start_leaves_no_connection()
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.SetRenderScaling(2);
        window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/D", "/Q"]) : ("/bin/sh", []);
        window.Show();
        window.OpenFolder(folder);
        window.ShowToolWindow("terminal");
        Ui.Settle();
        try
        {
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the initial shell");
            Await(window.TerminalPanel.CloseAllAsync(), "the initial shell to close");
            for (int attempt = 0; attempt < 5; attempt++)
            {
                // No wait between requesting a session and shutdown: a platform may spawn synchronously or later.
                Task<TerminalSession> start = window.TerminalPanel.NewSessionAsync();
                TerminalSession session = Assert.Single(window.TerminalPanel.Tabs).Session;
                int? pid = session.ProcessId;
                List<int> exits = [];
                session.Exited += (_, code) => exits.Add(code);
                Task close = window.TerminalPanel.CloseAllAsync();
                Assert.Same(session, Ui.Await(start, "the requested shell to finish starting"));
                pid ??= session.ProcessId;
                Await(close, "the immediately closed shell to finish");
                Assert.True(pid.HasValue);
                Ui.WaitFor(() => Processes.Gone(pid.Value), "the disposed shell's real process to exit", TimeSpan.FromSeconds(10));
                Assert.False(session.IsRunning);
                Assert.Single(exits);
                Assert.Empty(window.TerminalPanel.Tabs);
            }
            foreach (string missing in OperatingSystem.IsWindows() ? new[] { Path.Combine(folder, "missing-shell") }
                : new[] { Path.Combine(folder, "missing-shell"), "./missing-shell" })
            {
                window.TerminalPanel.Shell = (missing, []);
                TerminalSession failed = Ui.Await(window.TerminalPanel.NewSessionAsync(), "the missing shell's diagnostic");
                Assert.Contains(failed.Screen.Lines, line => line.Text().Contains("could not start a shell", StringComparison.Ordinal));
                Assert.Same(failed, window.TerminalPanel.Active);
                Assert.False(failed.IsRunning);
                Assert.Null(failed.ProcessId);
                window.ShowToolWindow("terminal");
                foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    window.RequestedThemeVariant = theme;
                    Ui.Settle();
                    UiAudit.Capture(window, $"terminal-missing-{(Path.IsPathRooted(missing) ? "absolute" : "relative")}-{theme.ToString().ToLowerInvariant()}");
                }
                Await(window.TerminalPanel.CloseAllAsync(), "the failed shell's tab to close");
                Assert.Empty(window.TerminalPanel.Tabs);
            }
            if (!OperatingSystem.IsWindows())
            {
                string shellFolder = Path.Combine(folder, "shell folder");
                Directory.CreateDirectory(shellFolder);
                string executable = Path.Combine(shellFolder, "real-shell");
                File.WriteAllText(executable, "#!/bin/sh\nprintf 'relative-shell-ready\\n'\nread answer\nexit 2\n");
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                window.TerminalPanel.Shell = ("./real-shell", []);
                TerminalSession relative = Ui.Await(window.TerminalPanel.NewSessionAsync(shellFolder), "a relative executable in a spaced working directory");
                window.ShowToolWindow("terminal");
                Ui.WaitFor(() => relative.Screen.Lines.Any(line => line.Text().Contains("relative-shell-ready", StringComparison.Ordinal)), "the relative shell's real output");
                int pid = Assert.IsType<int>(relative.ProcessId);
                List<int> exits = [];
                relative.Exited += (_, code) => exits.Add(code);
                window.TerminalPanel.FocusView();
                Ui.Settle();
                Assert.Same(window.TerminalPanel.View, window.FocusManager?.GetFocusedElement());
                window.KeyTextInput("finished");
                Ui.Press(window, "enter");
                Ui.WaitFor(() => !relative.IsRunning && Processes.Gone(pid), "the real program's exit, even when its code is also an errno", TimeSpan.FromSeconds(10));
                Assert.Equal(2, Assert.Single(exits));
                Assert.Empty(window.TerminalPanel.Tabs);
            }
        }
        finally
        {
            Await(window.TerminalPanel.CloseAllAsync(), "cleanup after an interrupted assertion");
            window.Close();
        }
    }
}
