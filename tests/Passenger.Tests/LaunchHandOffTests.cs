using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A person's second launch, as Godot's editor makes one for a script: Passenger's own executable run with a
/// folder, <c>--file</c> and <c>--caret</c> hands them to the Passenger already running in its state and ends, the
/// running one's window on that folder taking the file at the line, no second Passenger or window made; one for a folder
/// no window has is opened by the running Passenger in a window of its own; with none running, the launch is the
/// Passenger (headless here, as the run's launches are), and the next launch goes to it.</summary>
public sealed class LaunchHandOffTests
{
    private static readonly string Passenger = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Passenger.exe" : "Passenger");

    [AvaloniaFact]
    public void A_second_launch_with_a_file_and_caret_goes_to_the_window_on_its_folder_at_its_line_with_no_second_window()
    {
        string folder = TestFolders.Create("launch-");
        string script = TestFolders.Write(folder, "Player.cs", "one\ntwo\nthree\nfour\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        int windows = PassengerApp.Windows.Count;

        Launched([folder, "--file", script, "--caret", "3:2"], StateStore.DefaultRoot);

        Ui.WaitFor(() => Caret(window) == (PathNames.Canonical(script), new TextPosition(2, 1)),
            () => $"the launch's file in the window on its folder with the caret at 3:2; it shows {window.EditorArea.Active?.Path ?? "nothing"} at {Caret(window).At}");
        Assert.True(PassengerApp.Windows.Count == windows && PassengerApp.Front == window,
            $"The launch was to go to the window on its folder, brought forward, with no other window made; {PassengerApp.Windows.Count} windows are open "
            + $"where {windows} were, the front one on {PassengerApp.Front?.Folder}");
    }

    [AvaloniaFact]
    public void A_launch_counting_its_caret_from_zero_as_Godot_s_editor_fills_it_in_puts_the_caret_on_that_line()
    {
        string folder = TestFolders.Create("launch-");
        string script = TestFolders.Write(folder, "Player.cs", "one\ntwo\nthree\nfour\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);

        Launched([folder, "--file", script, "--caret", "0:0", "--zero-based"], StateStore.DefaultRoot);

        Ui.WaitFor(() => Caret(window) == (PathNames.Canonical(script), new TextPosition(0, 0)),
            () => $"the caret at the file's first line and column, as Godot's 0:0 says; it shows {window.EditorArea.Active?.Path ?? "nothing"} at {Caret(window).At}");
    }

    [AvaloniaFact]
    public void A_launch_for_a_folder_no_window_has_is_opened_by_the_running_Passenger_in_a_window_of_its_own()
    {
        string open = TestFolders.Create("launch-open-");
        TestFolders.Write(open, "notes.txt", "open\n");
        string other = TestFolders.Create("launch-other-");
        string script = TestFolders.Write(other, "Enemy.cs", "one\ntwo\nthree\n");
        MainWindow window = Ui.OpenFolder(open, languageServer: false);

        Launched([other, "--file", script, "--caret", "2:1"], StateStore.DefaultRoot);

        Ui.WaitFor(() => PassengerApp.Windows.Any(w => w != window && w.Folder == PathNames.Canonical(other)),
            () => $"a window of the running Passenger's own on {other}; its windows are on {string.Join(", ", PassengerApp.Windows.Select(w => w.Folder ?? "nothing"))}");
        MainWindow opened = PassengerApp.Windows.Single(w => w.Folder == PathNames.Canonical(other));
        Ui.WaitFor(() => Caret(opened) == (PathNames.Canonical(script), new TextPosition(1, 0)),
            () => $"the launch's file at 2:1 in the window made for its folder; it shows {opened.EditorArea.Active?.Path ?? "nothing"} at {Caret(opened).At}");
        Assert.True(window.Folder == PathNames.Canonical(open) && PassengerApp.Front == opened,
            $"The window on {open} was to keep its folder and the new one to come forward; it is on {window.Folder}, the front one on {PassengerApp.Front?.Folder}");
    }

    [AvaloniaFact]
    public void With_no_Passenger_running_a_launch_is_the_Passenger_and_the_next_launch_goes_to_it()
    {
        string state = TestFolders.Create("launch-state-");
        string folder = TestFolders.Write(TestFolders.Create("launch-"), "Player.cs", "one\ntwo\nthree\n");
        string project = Path.GetDirectoryName(folder)!;
        using Process first = Start([project], state);
        Task<string> said = first.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            string sockets = SocketFolders.For(state);
            Ui.PumpUntil(() => Directory.Exists(sockets) && Directory.EnumerateFiles(sockets, "*.open").Any() || !DotnetProcesses.Runs(first),
                $"the Passenger the first launch started to listen for launches in {sockets}", TimeSpan.FromMinutes(2));
            if (!DotnetProcesses.Runs(first))
            {
                Assert.Fail($"The first launch, with no Passenger running, ended with exit code {first.ExitCode} rather than being the Passenger");
            }

            string log = Path.Combine(state, "logs", "passenger.log");
            Launched([project, "--file", folder, "--caret", "3:1"], state, () => File.Exists(log) ? Ui.Shared(log) : "no log");

            Ui.PumpUntil(() => File.Exists(log) && Ui.Shared(log).Contains("taken by the window on " + PathNames.Canonical(project), StringComparison.Ordinal),
                $"the first launch's Passenger to say in its log that it took the second launch in its window on {project}");
        }
        finally
        {
            first.StandardInput.Close();
            if (!Ui.Pumped(() => !DotnetProcesses.Runs(first), TimeSpan.FromSeconds(30)))
            {
                first.Kill(entireProcessTree: true);
            }
            Ui.AwaitPumped(said, "the first launch's standard error to end");
        }
    }

    /// <summary>The caret's place in the window's file shown, and that file.</summary>
    private static (string? Path, TextPosition At) Caret(MainWindow window)
    {
        if (window.EditorArea.Active is not { } shown)
        {
            return (null, default);
        }
        int caret = window.EditorArea.CodeEditor.Selections.Primary.Caret;
        TextSnapshot text = shown.Text.Snapshot;
        int line = text.GetLineFromOffset(caret);
        return (shown.Path, new TextPosition(line, caret - text.GetLineStart(line)));
    }

    /// <summary>A launch run with its input left open, so one that started a Passenger of its own rather than handing itself
    /// over would never end: fails unless it ended with exit code 0 within the limit, its window's loop run meanwhile.</summary>
    private static void Launched(IReadOnlyList<string> arguments, string state, Func<string>? running = null)
    {
        using Process launch = Start(arguments, state);
        Task<string> error = launch.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            bool ended = Ui.Pumped(() => !DotnetProcesses.Runs(launch), TimeSpan.FromSeconds(60));
            string errors = ended ? Ui.AwaitPumped(error, "the launch's standard error to end") : string.Empty;
            Assert.True(ended && launch.ExitCode == 0, ended ? $"The launch ended with exit code {launch.ExitCode}: {errors}"
                : $"The launch was still running after 60 s: it started a Passenger of its own rather than handing itself to the one running{(running is null ? string.Empty : $"\nThe running one's log:\n{running()}")}");
        }
        finally
        {
            if (DotnetProcesses.Runs(launch))
            {
                launch.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>Passenger's executable started with these arguments in the state given, its input kept and its errors read.</summary>
    private static Process Start(IReadOnlyList<string> arguments, string state)
    {
        ProcessStartInfo start = new(Passenger)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[StateStore.Variable] = state;
        return Process.Start(start)!;
    }
}
