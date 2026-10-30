using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Terminal;

namespace Passenger.Tests;

/// <summary>The Terminal window as programs use it: a real shell in a pseudo-terminal, read as xterm is (scroll regions, the
/// alternate screen, colours, wide characters, answers to a program's questions, the scrollback), keys sent as xterm sends
/// them, typed commands and keys taken ahead of the IDE; floods of output and stuck pastes never hold the window, pastes
/// stay text, accents and emoji take the columns the shell counts, lines reflow as it resizes, and a place or a selection
/// holds as output streams. A run configuration that uses the terminal runs its program in a tab of its own, which reads
/// what is typed there and draws as it likes, and which Stop ends with all it started. What a program writes or reads
/// comes from the TerminalCases debuggee, the same bytes on every system, so Windows checks what ConPTY makes of them.</summary>
public sealed class TerminalTests
{
    [AvaloniaFact]
    public void What_a_full_screen_program_draws_lands_where_it_puts_it_and_the_shells_screen_comes_back_after()
    {
        (MainWindow window, TerminalSession shell) = Open(TestFolders.Create("terminal-"));
        TerminalScreen screen = shell.Screen;
        // Room for the program's twelve rows and the line it leaves the cursor on: the terminal given the frame.
        window.Perform(AppActions.MaximizeToolWindow);
        WaitForScreen(window, shell, s => s.Rows >= 13, "the terminal to take the frame");
        Run(window, "echo before");
        WaitForLine(shell, "before", "the line before the program");

        string columns = screen.Columns.ToString(CultureInfo.InvariantCulture);
        RunCase(window, "draw", columns);
        WaitForScreen(window, shell, s => s.IsAlternate && s.Lines[11].Cells[0].Rune == '漢', "the program to draw its screen");
        Assert.Equal(["1111111111", "22", "     33333", "!"], screen.Lines[..4].Select(l => l.Text()));
        Assert.True(screen.Lines[4..9].Select(l => l.Text()).SequenceEqual(["top", "b", "c", "d", "bottom"]),
            $"a line fed at the foot of a scroll region over rows 6 to 8 moved rows outside it:\n{Screen(shell)}");
        Cell[] colours = screen.Lines[9].Cells;
        Assert.Equal((1, CellFlags.Bold), (colours[0].Style.Fore, colours[0].Style.Flags));
        Assert.Equal(208, colours[1].Style.Fore);
        Assert.Equal(CellStyle.FromRgb(10, 20, 30), colours[2].Style.Fore);
        Assert.Equal(CellStyle.FromRgb(40, 50, 60), colours[3].Style.Fore);
        Assert.Equal((CellStyle.Default, 4), (colours[4].Style.Fore, colours[4].Style.Back));
        Assert.Equal(CellStyle.Plain, colours[5].Style);
        Assert.True(screen.Lines[10].Text().EndsWith("abcd", StringComparison.Ordinal) && screen.Lines[10].Wrapped,
            $"abcd at the right edge reads \"{screen.Lines[10].Text()}\"");
        Assert.True((screen.Lines[11].Cells[0].Width, screen.Lines[11].Cells[1].Width) == (2, 0),
            "the wide character at the edge was split across the rows rather than taken whole to the next");

        Ui.Press(window, "ENTER");
        WaitForScreen(window, shell, s => !s.IsAlternate && s.CursorColumn > 0 && s.Lines[s.CursorRow].Text().Trim().Length > 0, "the shell's screen back with its prompt");
        Assert.Contains("before", Lines(screen));
        Assert.Contains(Lines(screen), l => l.EndsWith($"draw {columns}", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(screen), l => l is "1111111111" or "     33333" or "bottom");
        window.Close();
    }

    [AvaloniaFact]
    public void A_programs_questions_about_the_terminal_are_answered()
    {
        (MainWindow window, TerminalSession shell) = Open(TestFolders.Create("terminal-"));
        // Where the cursor is, as a program asks before drawing, and what the terminal is, each answer read back.
        RunCase(window, "ask");
        WaitForLine(shell, "at 5;10 is ?62;22", "the program's answers");
        window.Close();
    }

    [AvaloniaFact]
    public void Keys_reach_a_program_as_xterm_sends_them_in_either_cursor_mode()
    {
        (MainWindow window, TerminalSession shell) = Open(TestFolders.Create("terminal-"));
        RunCase(window, "keys");
        WaitForLine(shell, "keys?", "the program to wait for its keys");
        foreach (string key in (string[])["UP", "control RIGHT", "DELETE", "shift TAB", "ENTER"])
        {
            Ui.Press(window, key);
        }
        WaitForLine(shell, "^[[A^[[1;5C^[[3~^[[Z", "the keys as the program read them");
        Ui.Press(window, "control C");
        WaitForScreen(window, shell, s => s.CursorColumn > 0, "the prompt after the program");

        RunCase(window, "keys", "application");
        WaitForScreen(window, shell, s => s.ApplicationCursorKeys && s.Lines.Count(l => l.Text() == "keys?") == 2, "the program to wait in the application cursor mode");
        Ui.Press(window, "UP");
        Ui.Press(window, "ENTER");
        WaitForLine(shell, "^[OA", "Up as the program read it in the application cursor mode");
        Ui.Press(window, "control C");
        window.Close();
    }

    /// <summary>A program that asks for the mouse's moves reads each move into another cell as xterm reports it: under 1002
    /// (drags) a drag's press, its moves with the button held and its release, and nothing of a move with no button; under
    /// 1003 (every move) a move with no button too.</summary>
    [AvaloniaFact]
    public void A_program_asking_for_drags_or_every_move_reads_each_move_into_another_cell()
    {
        (MainWindow window, TerminalSession shell) = Open(TestFolders.Create("terminal-"));
        TerminalView view = window.TerminalPanel.View;
        RunCase(window, "mouse", "1002");
        WaitForScreen(window, shell, s => s.MouseTracking == MouseTracking.Drag && s.SgrMouse && Lines(s).Contains("mouse?"), "the program to ask for drags");
        // On the screen's second row: a move with no button over cells 2 to 4 (from 0), then a drag from 2 to 6.
        Hover(window, view.TopLine + 1, 2, 4);
        Drag(window, view.TopLine + 1, 2, view.TopLine + 1, 6);
        WaitForLine(shell, "^[[<0;7;2m", "the drag's release as the program read it");
        Assert.Equal(["^[[<0;3;2M", "^[[<32;4;2M", "^[[<32;5;2M", "^[[<32;6;2M", "^[[<32;7;2M", "^[[<0;7;2m"], Reports(shell.Screen, 1));
        Ui.Press(window, "control C");
        WaitForScreen(window, shell, s => s.MouseTracking == MouseTracking.None && !s.SgrMouse, "the program to give the mouse back as it ends");

        RunCase(window, "mouse", "1003");
        WaitForScreen(window, shell, s => s.MouseTracking == MouseTracking.Any && Lines(s).Count(l => l == "mouse?") == 2, "the program to ask for every move");
        Hover(window, view.TopLine + 1, 2, 4);
        WaitForLine(shell, "^[[<35;5;2M", "the last move as the program read it");
        Assert.Equal(["^[[<35;3;2M", "^[[<35;4;2M", "^[[<35;5;2M"], Reports(shell.Screen, 2));
        Ui.Press(window, "control C");
        window.Close();
    }

    [AvaloniaFact]
    public void Output_past_the_screen_goes_to_the_scrollback_and_clear_buffer_leaves_only_the_prompt_at_the_top()
    {
        (MainWindow window, TerminalSession shell) = Open(TestFolders.Create("terminal-"));
        TerminalScreen screen = shell.Screen;
        RunCase(window, "long", (screen.Columns + 4).ToString(CultureInfo.InvariantCulture));
        WaitForLine(shell, "200", "the program's lines");
        Assert.True(screen.Scrollback.Count > 0 && screen.LineAt(screen.FirstLine).Text() != "1",
            $"the program's first lines left nothing in the scrollback: it holds {screen.Scrollback.Count}");
        Assert.Contains(new string('0', screen.Columns + 4) + "7", Lines(screen));

        Ui.ChooseFromMenu(window.TerminalPanel.View, "Clear Buffer");
        Assert.Empty(screen.Scrollback);
        Assert.True(screen.CursorRow == 0 && screen.Lines[0].Text().Length > 0 && screen.Lines.Skip(1).All(l => l.Text().Length == 0),
            $"after Clear Buffer the screen is not the prompt alone at its top:\n{Screen(shell)}");
        Run(window, "echo after");
        WaitForLine(shell, "after", "a command run after the clear");
        window.Close();
    }

    [AvaloniaFact]
    public void The_terminal_window_runs_a_shell_that_takes_typed_commands_and_hides_when_it_exits()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            MainWindow window = Ui.OpenFolder(folder);
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            Ui.Run(window, AppActions.ShowTerminal);
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
            TerminalSession shell = window.TerminalPanel.Active!;
            Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the terminal");

            window.KeyTextInput(OperatingSystem.IsWindows() ? "echo %CD%" : "pwd");
            Ui.Press(window, "ENTER");
            string where = PathNames.Canonical(folder);
            Ui.WaitFor(() => Screen(shell).Contains(where, StringComparison.Ordinal), $"the folder on the screen:\n{Screen(shell)}");

            Assert.True(shell.Screen.Columns > 80, $"{shell.Screen.Columns} columns");

            Ui.Press(window, "shift ESCAPE");
            Assert.False(window.DockHost.IsShown(ToolWindowIds.Terminal));
            Ui.Run(window, AppActions.ShowTerminal);
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Terminal));

            Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the terminal focused again");
            window.KeyTextInput("exit");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.TerminalPanel.Tabs.Count == 0, "the tab to close");
            Assert.False(window.DockHost.IsShown(ToolWindowIds.Terminal));
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void Every_tool_windows_shortcut_opens_it_while_the_terminal_has_the_keys()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession _) = Open(folder);
            // Run, Git and Bookmarks by their one-key shortcuts (Alt+4, Alt+9 and Ctrl+`; Cmd+4, Cmd+9 and Ctrl+` on macOS).
            foreach ((string action, string shown) in new[] { (AppActions.ShowRun, ToolWindowIds.Run), (AppActions.ShowGit, ToolWindowIds.Git), (AppActions.ShowBookmarks, ToolWindowIds.Bookmarks) })
            {
                window.ShowToolWindow(ToolWindowIds.Terminal);
                window.TerminalPanel.FocusView();
                Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the terminal");
                Passenger.Editor.Keystroke keys = window.Actions.Keymap.ShortcutsFor(action).First(s => s.Second is null).First;
                Ui.Press(window, keys.Notation());
                Assert.True(window.DockHost.IsShown(shown), $"{keys.Display()}, pressed in the terminal, went to the shell rather than opening the {shown} window");
            }
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_paste_cannot_end_its_own_bracket_and_run_the_line_after_it()
    {
        // zsh's line editor brackets pastes (as the Mac's own shell, it is always there on a Mac).
        Assert.SkipUnless(File.Exists("/bin/zsh"), "zsh, whose line editor asks for bracketed pastes, is not installed here");
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder, "/bin/zsh", "-f");
            Ui.WaitFor(() => shell.Screen.BracketedPaste, "zsh to ask for bracketed pastes");

            // A clipboard that ends the bracket itself, then a line to run, as a web page can put there.
            Copy(window, "echo a\x1b[201~\rtouch hacked\n");
            Ui.Press(window, PasteKey);
            Ui.WaitFor(() => shell.Screen.ScreenText().Contains("touch hacked", StringComparison.Ordinal), $"the paste in zsh's line:\n{shell.Screen.ScreenText()}");

            // The paste is text in the line being edited, which Ctrl+C throws away; anything run would have run by the answer.
            Ui.Press(window, "control C");
            window.KeyTextInput("echo checked-$((6*7))");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => shell.Screen.ScreenText().Contains("checked-42", StringComparison.Ordinal), $"zsh's answer:\n{shell.Screen.ScreenText()}");
            Assert.False(File.Exists(Path.Combine(folder, "hacked")), "the paste ended its own bracket and ran the line after it");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_file_dropped_on_the_terminal_is_typed_as_its_path_quoted_for_the_shell()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            // A space and a quote in its name, which the shell would split or end its word at, unquoted.
            string dropped = Path.Combine(folder, "it's here.txt");
            File.WriteAllText(dropped, "dropped-42\n");
            (MainWindow window, TerminalSession shell) = Open(folder);
            window.KeyTextInput(OperatingSystem.IsWindows() ? "type " : "cat ");
            Ui.Drop(window, window.TerminalPanel.View, dropped);
            Ui.Press(window, "ENTER");
            WaitForLine(shell, "dropped-42", "the dropped file read by the shell from its path, quoted as one word");
            Assert.True(window.EditorArea.Documents.Count == 0, "The file dropped on the terminal opened in the editor too");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A file whose name holds a command, dropped on the terminal's own shell (PowerShell on Windows, sh elsewhere),
    /// is read by the shell as its name: its name is echoed as it is, and the command never runs.</summary>
    [AvaloniaFact]
    public void A_file_dropped_on_the_terminal_whose_name_holds_a_command_is_read_as_its_name_and_nothing_runs()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            string name = OperatingSystem.IsWindows() ? "$(New-Item ran) it's.txt" : "$(touch ran) it's.txt";
            string dropped = Path.Combine(folder, name);
            File.WriteAllText(dropped, "dropped\n");
            (string shellProgram, string[] arguments) = OperatingSystem.IsWindows() ? TerminalSession.DefaultShell() : ("/bin/sh", []);
            (MainWindow window, TerminalSession shell) = Open(folder, shellProgram, arguments);
            window.KeyTextInput(OperatingSystem.IsWindows() ? "Split-Path -Leaf " : "basename ");
            Ui.Drop(window, window.TerminalPanel.View, dropped);
            Ui.Press(window, "ENTER");
            WaitForLine(shell, name, "the dropped file's name, read by the shell from its path as it is");
            Assert.False(File.Exists(Path.Combine(folder, "ran")), $"The command in the dropped file's name ran in {Path.GetFileName(shellProgram)}");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>What an agent's --mcp-attach and a relay's session hand a Passenger they start, which it takes out of its
    /// environment as it starts, reaches no shell of its Terminal: each set as a parent's variables are, in the process's
    /// own environment, before the start's check takes them.</summary>
    [AvaloniaFact]
    public void What_Passenger_takes_out_of_its_environment_as_it_starts_reaches_no_shell_in_its_terminal()
    {
        string folder = TestFolders.Create("terminal-");
        string session = Path.Combine(folder, "relay-session.json");
        string[] takenOut = [Passenger.App.Mcp.McpProxy.StartedForVariable, Passenger.App.Mcp.McpProxy.SessionVariable];
        try
        {
            File.WriteAllText(session, """{"tools":"","carried":[],"begun":""}""");
            OwnEnvironment.Set(takenOut[0], "agent");
            OwnEnvironment.Set(takenOut[1], session);
            Passenger.App.Mcp.McpProxy.CheckVariable();
            Assert.False(File.Exists(session), "The start's check did not take the relay's session, so the test checks nothing");

            (MainWindow window, TerminalSession shell) = Open(folder);
            // Only these variables listed, so nothing else of the machine's environment reaches the screen.
            Run(window, OperatingSystem.IsWindows()
                ? $"set {takenOut[0]}& set {takenOut[1]}& set TERM_PROGRAM& echo listed-929"
                : $"env | grep -e '^{takenOut[0]}=' -e '^{takenOut[1]}=' -e '^TERM_PROGRAM='; echo listed-929");
            WaitForLine(shell, "listed-929", "the shell's environment listed");
            List<string> listed = Lines(shell.Screen);
            Assert.True(listed.Contains("TERM_PROGRAM=Passenger"), $"The shell's environment was not listed:\n{Screen(shell)}");
            foreach (string variable in takenOut)
            {
                Assert.False(listed.Any(line => line.StartsWith(variable + "=", StringComparison.Ordinal)),
                    $"{variable}, taken out as Passenger started, reached the Terminal's shell and every program run there:\n{Screen(shell)}");
            }
            window.Close();
        }
        finally
        {
            foreach (string variable in takenOut)
            {
                OwnEnvironment.Set(variable, null);
            }
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The variables of a tmux pane or screen window Passenger was started in, held as a parent's are, in the
    /// process's own environment, reach no shell of its Terminal nor a program a run starts there, which would take itself
    /// for that pane (its tmux commands acting on the person's own session); what the run configuration gives one of them
    /// reaches its program.</summary>
    [AvaloniaFact]
    public void A_tmux_or_screen_Passenger_was_started_in_reaches_no_shell_or_run_in_its_terminal()
    {
        string folder = TestFolders.Create("terminal-outer-");
        Dictionary<string, string> outer = new()
        {
            ["TMUX"] = "/tmp/tmux-501/default,4242,0",
            ["TMUX_PANE"] = "%3",
            ["STY"] = "4242.ttys001.host",
            ["WINDOW"] = "0",
        };
        try
        {
            foreach ((string variable, string value) in outer)
            {
                OwnEnvironment.Set(variable, value);
            }
            string project = TestFolders.Write(folder, "Listed/Listed.csproj", TestProjects.Console());
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            TerminalSession shell = OpenTerminal(window, OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
            // Only these variables listed, so nothing else of the machine's environment reaches the screen.
            Run(window, OperatingSystem.IsWindows()
                ? "set TMUX& set STY& set WINDOW& set TERM_PROGRAM& echo listed-933"
                : "env | grep -e '^TMUX' -e '^STY=' -e '^WINDOW=' -e '^TERM_PROGRAM='; echo listed-933");
            WaitForLine(shell, "listed-933", "the shell's environment listed");
            List<string> listed = Lines(shell.Screen);
            Assert.True(listed.Contains("TERM_PROGRAM=Passenger"), $"The shell's environment was not listed:\n{Screen(shell)}");
            foreach (string variable in outer.Keys)
            {
                Assert.False(listed.Any(line => line.StartsWith(variable + "=", StringComparison.Ordinal)),
                    $"{variable}, the outer tmux's or screen's, reached the Terminal's shell, which takes itself for its pane:\n{Screen(shell)}");
            }

            window.SetConfigurations([new RunConfiguration
            {
                Name = "Outer",
                Project = PathNames.Canonical(project),
                Program = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows()
                    ? "/c \"set TMUX& set STY& set WINDOW& echo run-933\""
                    : "-c \"env | grep -e '^TMUX' -e '^STY=' -e '^WINDOW='; echo run-933\"",
                WorkingDirectory = folder,
                Environment = { ["WINDOW"] = "given" },
                BuildBeforeLaunch = false,
                Console = ConsoleKind.Terminal,
            }], "Outer");
            Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(1));
            TerminalSession program = Assert.IsType<TerminalRun>(window.RunInTerminal).Session;
            WaitForLine(program, "run-933", "the program's environment listed");
            listed = Lines(program.Screen);
            Assert.True(listed.Contains("WINDOW=given"), $"What the run configuration gives WINDOW did not reach its program:\n{Screen(program)}");
            foreach (string variable in outer.Keys)
            {
                Assert.False(listed.Any(line => line.StartsWith(variable + "=", StringComparison.Ordinal) && line != "WINDOW=given"),
                    $"{variable}, the outer tmux's or screen's, reached a program run in the Terminal:\n{Screen(program)}");
            }
            window.Close();
        }
        finally
        {
            foreach (string variable in outer.Keys)
            {
                OwnEnvironment.Set(variable, null);
            }
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_decomposed_accent_shows_on_its_letter_in_one_cell_and_copies_with_it()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder);
            // Résumé.pdf as macOS's file system spells it, each é an e and a combining acute (CC 81), as ls prints it.
            string name = "Re\u0301sume\u0301.pdf";
            RunCase(window, "accent");
            long line = WaitForLine(shell, text => text.StartsWith('R') && text.EndsWith(".pdf", StringComparison.Ordinal), "the name printed");
            TerminalLine row = shell.Screen.LineAt(line);
            Assert.True(row.Text() == name, $"Résumé.pdf printed as \"{row.Text()}\": its accents were dropped");
            Assert.True(row.TextOf(1) == "e\u0301" && row.Cells[2].Rune == 's' && row.Cells[9].Rune == 'f',
                $"Résumé.pdf took other columns than its ten letters, so text after an accent drifts from where the shell counts it: [{string.Join("|", Enumerable.Range(0, 12).Select(row.TextOf))}]");

            Drag(window, line, 0, line, 9);
            Ui.Press(window, CopyKey);
            Assert.Equal(name, Ui.Clipboard(window));
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void An_emoji_takes_the_two_columns_the_shell_counts_so_the_text_after_it_stays_in_place()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder);
            // A rocket (U+1F680), then a check mark (U+2705) and a lightning bolt (U+26A1), each two columns in zsh's count.
            RunCase(window, "emoji");
            long rocket = WaitForLine(shell, "\U0001F680x", "the rocket printed");
            Assert.True(shell.Screen.LineAt(rocket).Cells[2].Rune == 'x',
                $"x printed after a rocket stands in column {Array.FindIndex(shell.Screen.LineAt(rocket).Cells, c => c.Rune == 'x')}, not 2: the rocket overlaps it");
            TerminalLine marks = shell.Screen.LineAt(rocket + 1);
            Assert.True(marks.Text() == "\u2705\u26A1y" && marks.Cells[4].Rune == 'y',
                $"y printed after a check mark and a lightning bolt stands in column {Array.FindIndex(marks.Cells, c => c.Rune == 'y')}, not 4");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_long_line_reads_whole_after_the_terminal_narrows_and_widens_again_and_the_prompt_keeps_its_cursor()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder);
            TerminalScreen screen = shell.Screen;
            int wide = screen.Columns;
            Assert.True(wide >= 100, $"the terminal has {wide} columns, too few for a line of 100");
            // The numbers 10 to 59 in a row: a line of 100 characters.
            string hundred = string.Concat(Enumerable.Range(10, 50));
            RunCase(window, "hundred");
            WaitForLine(shell, hundred, "the line of 100 printed");
            WaitForScreen(window, shell, s => s.CursorColumn > 0 && s.Lines[s.CursorRow].Text() != hundred, "the prompt after the program");
            window.KeyTextInput("echo kept");
            Ui.WaitFor(() => screen.Lines[screen.CursorRow].Text().EndsWith("echo kept", StringComparison.Ordinal), "the command typed");
            string prompt = screen.Lines[screen.CursorRow].Text();

            // Moved to the right side, as a drag of its stripe button moves it, the terminal is a side panel's width.
            DockHost dock = window.DockHost;
            dock.Move(dock[ToolWindowIds.Terminal], ToolAnchor.RightTop);
            Ui.WaitFor(() => screen.Columns < wide, "the terminal to narrow on the right side");
            Assert.True(screen.Columns < 100, $"on the right side the terminal has {screen.Columns} columns, room for the whole line");
            Assert.True(Lines(screen).Contains(hundred), $"at {screen.Columns} columns the line of 100 no longer reads whole, its rows joined:\n{Screen(shell)}");

            dock.Move(dock[ToolWindowIds.Terminal], ToolAnchor.BottomLeft);
            Ui.WaitFor(() => screen.Columns == wide, "the terminal to widen back at the bottom");
            Ui.Settle();
            Assert.True(Enumerable.Range(0, screen.Rows).Any(r => screen.Lines[r].Text() == hundred),
                $"widened again to {wide} columns, the line of 100 is not one row of the screen: the narrower width cut it\n{Screen(shell)}");
            Assert.True(screen.Lines[screen.CursorRow].Text() == prompt && screen.CursorColumn == prompt.Length,
                $"the cursor is at row {screen.CursorRow}, column {screen.CursorColumn}, not after the command typed ({prompt}):\n{Screen(shell)}");
            Ui.Press(window, "ENTER");
            WaitForLine(shell, "kept", "the command typed before the resizes to run");

            dock.Move(dock[ToolWindowIds.Terminal], ToolAnchor.RightTop);
            Ui.WaitFor(() => screen.Columns < wide, "the terminal to narrow again");
            RunCase(window, "size");
            WaitForLine(shell, $"{screen.Rows} {screen.Columns}", $"the program to count the terminal's {screen.Rows} rows and {screen.Columns} columns");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void Scrolled_back_the_terminal_holds_its_place_as_output_arrives()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder);
            TerminalView view = window.TerminalPanel.View;
            RunCase(window, "lines", "1", "300");
            WaitForLine(shell, "300", "the program's lines");
            // Output that comes once the person is reading back: the program waits for a file the test makes.
            RunCase(window, "wait", "go", "1001", "1100");
            Ui.WaitFor(() => shell.Screen.Lines[shell.Screen.CursorRow].Text().Length == 0, "the loop to start waiting");

            Point middle = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
            for (int notch = 0; notch < 7; notch++)
            {
                window.MouseWheel(middle, new Vector(0, 1));
            }
            Ui.Settle();
            Assert.True(view.ScrolledBack == 21, $"seven notches of the wheel scrolled {view.ScrolledBack} lines back, not 21");
            string top = shell.Screen.LineAt(view.TopLine).Text();

            File.WriteAllText(Path.Combine(folder, "go"), string.Empty);
            WaitForLine(shell, "1100", "the hundred lines that came while scrolled back");
            Ui.Settle();
            Assert.True(view.ScrolledBack > 0, "the output scrolled the view back to the bottom while the person read back");
            Assert.True(shell.Screen.LineAt(view.TopLine).Text() == top,
                $"scrolled back to \"{top}\", the view moved to \"{shell.Screen.LineAt(view.TopLine).Text()}\" as 100 lines arrived");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_selection_stays_on_its_text_as_output_streams_past_the_scrollbacks_limit()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder);
            TerminalScreen screen = shell.Screen;
            string text = $"{TerminalScreen.ScrollbackLimit + 100}";
            RunCase(window, "lines", "1", text);
            WaitForLine(shell, text, "the program's lines");
            Assert.True(screen.Scrollback.Count == TerminalScreen.ScrollbackLimit, $"the scrollback holds {screen.Scrollback.Count} lines, short of its limit");
            RunCase(window, "wait", "go", "20001", "20100");
            Ui.WaitFor(() => screen.Lines[screen.CursorRow].Text().Length == 0, "the loop to start waiting");

            long chosen = WaitForLine(shell, text, "the program's last line");
            Drag(window, chosen, 0, chosen, text.Length - 1);
            Assert.Equal(text, window.TerminalPanel.View.SelectedText);
            File.WriteAllText(Path.Combine(folder, "go"), string.Empty);
            WaitForLine(shell, "20100", "the hundred lines streamed past the limit");
            Ui.Press(window, CopyKey);
            Assert.True(Ui.Clipboard(window) == text,
                $"the selection made on \"{text}\" copied \"{Ui.Clipboard(window)}\" once 100 more lines had dropped the oldest");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Plain text lands where a terminal puts it a character at a time, however it arrives: at a row's last column
    /// it waits and wraps with the next character (with wrapping off, writes on over that column), in insert mode it pushes
    /// the row's rest right (beside a wide character too), it takes the line-drawing set and the colour set before it, a
    /// repeat repeats its last character, and a run, an escape or a character's bytes split between writes read whole.</summary>
    [AvaloniaFact]
    public void Plain_text_lands_as_written_at_a_rows_end_in_insert_mode_and_split_between_writes()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder);
            TerminalScreen screen = shell.Screen;
            window.Perform(AppActions.MaximizeToolWindow);
            WaitForScreen(window, shell, s => s.Rows >= 18, "the terminal to take the frame");
            int width = screen.Columns;
            RunCase(window, "runs", width.ToString(CultureInfo.InvariantCulture));
            WaitForScreen(window, shell, s => s.IsAlternate && s.Lines[17].Text() == "abbbb", "the program to write its rows");

            string digits = string.Concat(Enumerable.Range(0, width).Select(i => (char)('0' + (i % 10))));
            (string Text, bool Wrapped, string What)[] rows =
            [
                (new string(' ', width - 4) + "wxyz", true, "text written up to the last column"),
                ("!", false, "the character after it, on the next row"),
                (new string(' ', width - 3) + "125", false, "text past the last column with wrapping off, each character over the last"),
                ("abXYcdef", false, "text inserted inside a row"),
                (" Q字ab", false, "a character inserted over a wide character's second half"),
                ("┌──┐ ok", false, "the line-drawing set's corners and lines"),
                ("split-run", false, "a run split between two writes"),
                ("café!", false, "a character's two bytes split between writes"),
                ("red plain", false, "text after a colour whose escape was split between writes"),
                (new string('-', width), true, "the first row of a run two rows and five long"),
                (new string('-', width), true, "the second row of that run"),
                ("-----", false, "the end of that run"),
                (new string(' ', width - 2) + "ab", true, "a write ending at the last column"),
                ("cd", false, "the next write, wrapped to the next row"),
                ("<<" + digits[..^2], false, "text inserted at a full row's start, its last two columns pushed off"),
                (new string(' ', width - 2) + "XY", true, "text inserted to the row's end"),
                ("Z", false, "the rest of it, inserted on the next row"),
                ("abbbb", false, "a repeat of the last character written"),
            ];
            for (int row = 0; row < rows.Length; row++)
            {
                TerminalLine line = screen.Lines[row];
                Assert.True(line.Text() == rows[row].Text && line.Wrapped == rows[row].Wrapped,
                    $"{rows[row].What} reads \"{line.Text()}\"{(line.Wrapped ? ", wrapped" : string.Empty)} on row {row + 1}, not \"{rows[row].Text}\"{(rows[row].Wrapped ? ", wrapped" : string.Empty)}:\n{Screen(shell)}");
            }
            Cell[] colours = screen.Lines[8].Cells;
            Assert.True(colours[..3].All(c => c.Style == new CellStyle(1, CellStyle.Default, CellFlags.Bold)) && colours[4].Style == CellStyle.Plain,
                $"\"red\" is not bold red with the rest of its row plain: {colours[0].Style}, {colours[4].Style}");

            Ui.Press(window, "ENTER");
            WaitForScreen(window, shell, s => !s.IsAlternate && s.CursorColumn > 0, "the shell's screen back with its prompt");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A log longer than the scrollback holds costs the window's thread nothing for each line past it: the line a
    /// full scrollback lets go is the next row. 30,000 lines allocate on the UI thread under 64 bytes a line more than
    /// 12,000, where a new row's cells alone take 16 bytes a column; the terminal is hidden meanwhile, so no frame counts.</summary>
    [AvaloniaFact]
    public void A_log_three_times_the_scrollback_costs_the_window_no_more_for_each_line_than_one_just_past_it()
    {
        long few = UiBytesForLog(12_000);
        long many = UiBytesForLog(30_000);
        long each = (many - few) / 18_000;
        TestContext.Current.TestOutputHelper?.WriteLine($"The log allocated {few / 1024:N0} KB on the UI thread for 12,000 lines, {many / 1024:N0} KB for 30,000: {each} bytes for each line more.");
        Assert.True(each < 64,
            $"30,000 lines of a log allocated {many / 1024:N0} KB on the UI thread, 12,000 {few / 1024:N0} KB: {each} bytes for each line more, a row made new for each");
    }

    /// <summary>What a log of some lines allocates on the UI thread from its start to its last line, the terminal hidden.</summary>
    private static long UiBytesForLog(int lines)
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder);
            RunCase(window, "log", "go", lines.ToString(CultureInfo.InvariantCulture));
            Ui.WaitFor(() => shell.Screen.Lines[shell.Screen.CursorRow].Text().Length == 0, "the program to start waiting");
            Ui.Press(window, "shift ESCAPE");
            Assert.False(window.DockHost.IsShown(ToolWindowIds.Terminal), "the terminal is still shown after Shift+Escape");
            Ui.Settle();
            long before = GC.GetAllocatedBytesForCurrentThread();
            File.WriteAllText(Path.Combine(folder, "go"), string.Empty);
            Ui.WaitFor(() => Done(shell.Screen), $"the log's {lines} lines");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            string last = $"line {lines:D7} of the build";
            Assert.True(Lines(shell.Screen).Any(l => l.StartsWith(last, StringComparison.Ordinal)), $"the log's last line, {last}, is not in the terminal:\n{Screen(shell)}");
            window.Close();
            return allocated;
        }
        finally
        {
            TestFolders.Delete(folder);
        }

        // Read from the cells, so the looks while the log comes allocate nothing of their own.
        static bool Done(TerminalScreen screen)
        {
            foreach (TerminalLine line in screen.Lines)
            {
                Cell[] cells = line.Cells;
                if (cells.Length > 4 && cells[0].Rune == 'd' && cells[1].Rune == 'o' && cells[2].Rune == 'n' && cells[3].Rune == 'e' && cells[4].Rune == 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>A log in colour streaming up a full screen costs a frame on the UI thread what a frame with one row of an
    /// empty screen written again costs, and what drawing each other row as it was shaped records, under 2 KB a row where
    /// shaping one of these takes tens; so does a full-screen program drawing its whole screen again with one row changed.</summary>
    [AvaloniaFact]
    public void A_log_in_colour_streaming_up_the_screen_costs_a_frame_what_one_changed_row_does()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder);
            window.Perform(AppActions.MaximizeToolWindow);
            WaitForScreen(window, shell, s => s.Rows >= 24 && s.Columns >= 80, "the terminal to take the frame");
            int rows = shell.Screen.Rows;
            long row = UiBytesForLines(window, shell, "row", rows);
            long log = UiBytesForLines(window, shell, "scroll", rows);
            long redrawn = UiBytesForLines(window, shell, "redraw", rows);
            TestContext.Current.TestOutputHelper?.WriteLine($"A frame allocated {row / 1024.0:N1} KB on the UI thread for one row of an empty screen written again, "
                + $"{log / 1024.0:N1} KB for a line streaming up a full screen of {rows} rows, {redrawn / 1024.0:N1} KB for that screen drawn again with one row changed.");
            long most = row + (rows * 2048);
            Assert.True(log < most,
                $"Each frame of a log in colour streaming up {rows} rows allocated {log / 1024.0:N1} KB on the UI thread, where one with a row of an empty screen written again allocated {row / 1024.0:N1} KB: every row was shaped again");
            Assert.True(redrawn < most,
                $"Each frame of a screen of {rows} rows in colour drawn again with one row changed allocated {redrawn / 1024.0:N1} KB on the UI thread, where one with a row of an empty screen written again allocated {row / 1024.0:N1} KB: every row written was shaped again");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>What a frame of the colours case allocates on the UI thread, the least of twenty, each drawing a line
    /// the program brought once the test made its file.</summary>
    private static long UiBytesForLines(MainWindow window, TerminalSession shell, string mode, int rows)
    {
        string folder = window.Folder!;
        int at = mode == "scroll" ? rows - 1 : 0;
        int number = mode == "scroll" ? rows : 1;
        RunCase(window, "colours", mode, rows.ToString(CultureInfo.InvariantCulture));
        WaitForScreen(window, shell, s => s.IsAlternate && Numbered(s.Lines[at], number), $"the {mode} case's first lines");
        string[] next = [.. Enumerable.Range(1, 23).Select(i => Path.Combine(folder, $"next-{i}"))];
        long least = long.MaxValue;
        for (int step = 0; step < next.Length; step++)
        {
            Ui.Settle();
            long before = GC.GetAllocatedBytesForCurrentThread();
            File.WriteAllBytes(next[step], []);
            number++;
            Ui.PumpUntil(() => Numbered(shell.Screen.Lines[at], number), "the case's next line");
            Ui.Draw();
            // The first lines are the first of everything they draw.
            if (step >= 3)
            {
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }
        File.WriteAllBytes(Path.Combine(folder, "done"), []);
        WaitForScreen(window, shell, s => !s.IsAlternate, "the shell's screen back after the case");
        foreach (string made in next)
        {
            File.Delete(made);
        }
        File.Delete(Path.Combine(folder, "done"));
        return least;
    }

    /// <summary>Whether a row begins with a line's four-digit number, read from its cells, so a look allocates nothing.</summary>
    private static bool Numbered(TerminalLine line, int number)
    {
        Cell[] cells = line.Cells;
        for (int i = 3; i >= 0; i--, number /= 10)
        {
            if (cells[i].Rune != '0' + (number % 10))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A run configuration that uses the terminal runs its program in a tab of the Terminal window named for it, a
    /// real terminal: the program clears the screen and writes in colour where it puts the cursor, reads a key and then a line
    /// typed into the view, and its tab, kept once it ends, says so with its exit code; the toolbar's Stop shows while it
    /// runs.</summary>
    [AvaloniaFact]
    public void A_program_run_in_the_terminal_reads_a_key_and_a_line_typed_there_and_draws_in_colour_where_it_puts_the_cursor()
    {
        MainWindow window = RunWindow(TestFolders.Create("terminal-run-"), string.Empty);
        Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(3));
        TerminalRun run = Assert.IsType<TerminalRun>(window.RunInTerminal);
        TerminalSession program = run.Session;
        Assert.Equal("Keys", window.TerminalPanel.ActiveTab?.Name);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Terminal), "The program runs in the terminal, which is not shown");
        Assert.Equal("Running Keys", window.Status);
        Assert.True(window.FindControl<Button>("StopButton")!.IsEnabled, "The toolbar's Stop could not be clicked while the program ran in the terminal");

        WaitForScreen(window, program, s => s.Lines.Any(l => l.Text() == "key?"), "the program to ask for a key");
        TerminalScreen screen = program.Screen;
        Assert.True(screen.Lines[2].Text() == "    red at the third row's fifth column",
            $"The program's line is not where it put the cursor, on a cleared screen:\n{Screen(program)}");
        Assert.True((screen.Lines[2].Cells[4].Style.Fore, screen.Lines[2].Cells[8].Style.Fore) == (9, CellStyle.Default),
            $"\"red\" is not in bright red with the rest of the line in the terminal's own colour: {screen.Lines[2].Cells[4].Style.Fore}, {screen.Lines[2].Cells[8].Style.Fore}");

        Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the program");
        window.KeyTextInput("q");
        // A key typed before ReadKey takes the terminal is echoed by the terminal's own line discipline, as anywhere.
        WaitForLine(program, line => line is "key? key q" or "key? qkey q", "the key as the program read it");
        window.KeyTextInput("hello");
        Ui.Press(window, "ENTER");
        WaitForLine(program, "line hello", "the line as the program read it");

        Assert.Equal(3, Ui.Await(run.Ended, "the program's end"));
        WaitForLine(program, "Process finished with exit code 3.", "its tab saying how it ended");
        Assert.Equal("Keys exited with code 3", window.Status);
        Assert.False(window.FindControl<Button>("StopButton")!.IsEnabled, "The toolbar's Stop could still be clicked once the program ended");
        Assert.True(window.TerminalPanel.Tabs is [{ Name: "Keys", HasEnded: true }], "The ended program's tab was not kept to say how it ended");
        window.Close();
    }

    /// <summary>Stop ends a program running in the terminal and the program it started, and its tab says it was ended.</summary>
    [AvaloniaFact]
    public void Stop_ends_a_program_running_in_the_terminal_and_what_it_started()
    {
        MainWindow window = RunWindow(TestFolders.Create("terminal-stop-"), "child");
        Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(3));
        TerminalRun run = window.RunInTerminal!;
        long at = WaitForLine(run.Session, l => l.StartsWith("child ", StringComparison.Ordinal), "the id of the program's child");
        int child = int.Parse(run.Session.Screen.LineAt(at).Text()["child ".Length..], CultureInfo.InvariantCulture);
        WaitForLine(run.Session, "key?", "the program waiting for a key");
        Assert.True(Processes.IsRunning(child), "The program's child is not running");

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => run.Ended.IsCompleted, "the program's end");
        Assert.True(Processes.Gone(run.ProcessId), $"Stop left the program running: {Processes.Describe(run.ProcessId)}");
        Assert.True(Processes.Gone(child), $"Stop left the program's child running: {Processes.Describe(child)}");
        // SIGKILL's, as a shell gives it, on macOS and Linux; Windows' is what TerminateProcess was given.
        WaitForLine(run.Session, l => OperatingSystem.IsWindows() ? l.StartsWith("Process finished with exit code ", StringComparison.Ordinal) : l == "Process finished with exit code 137.",
            "its tab saying it was ended");
        Assert.False(window.IsRunning || window.FindControl<Button>("StopButton")!.IsEnabled, "The window says the stopped program still runs");
        window.Close();
    }

    /// <summary>A variable a run in the terminal takes away never reaches its program, though Passenger's environment holds
    /// it as a parent's variables are held, in the process's own: a git hook's, which every run is started without, and one
    /// the run configuration gives no value. What the configuration sets reaches it.</summary>
    [AvaloniaFact]
    public void A_variable_a_run_in_the_terminal_takes_away_never_reaches_its_program()
    {
        string folder = TestFolders.Create("terminal-unset-");
        // GIT_PREFIX: of a git hook's variables, the one git itself never reads, should anything else started meanwhile get it.
        string[] takenAway = ["GIT_PREFIX", "PASSENGER_RUN_TAKEN_AWAY"];
        try
        {
            foreach (string variable in takenAway)
            {
                OwnEnvironment.Set(variable, "inherited");
            }
            string project = TestFolders.Write(folder, "Listed/Listed.csproj", TestProjects.Console());
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            // Only these variables listed, so nothing else of the machine's environment reaches the screen.
            window.SetConfigurations([new RunConfiguration
            {
                Name = "Variables",
                Project = PathNames.Canonical(project),
                Program = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows()
                    ? "/c \"set GIT_PREFIX& set PASSENGER_RUN_& echo listed-932\""
                    : "-c \"env | grep -e '^GIT_PREFIX=' -e '^PASSENGER_RUN_'; echo listed-932\"",
                WorkingDirectory = folder,
                Environment = { [takenAway[1]] = string.Empty, ["PASSENGER_RUN_GIVEN"] = "given" },
                BuildBeforeLaunch = false,
                Console = ConsoleKind.Terminal,
            }], "Variables");
            Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(1));
            TerminalSession program = Assert.IsType<TerminalRun>(window.RunInTerminal).Session;
            WaitForLine(program, "listed-932", "the program's environment listed");
            List<string> listed = Lines(program.Screen);
            Assert.True(listed.Contains("PASSENGER_RUN_GIVEN=given"), $"The program's environment was not listed:\n{Screen(program)}");
            foreach (string variable in takenAway)
            {
                Assert.False(listed.Any(line => line.StartsWith(variable + "=", StringComparison.Ordinal)),
                    $"{variable}, which the run takes away, reached the program it runs in the terminal:\n{Screen(program)}");
            }
            window.Close();
        }
        finally
        {
            foreach (string variable in takenAway)
            {
                OwnEnvironment.Set(variable, null);
            }
            TestFolders.Delete(folder);
        }
    }

    /// <summary>On macOS a run in the terminal that takes a variable away (the outer tmux's) still hands its program the
    /// loader's variables, which the loader takes out of a protected program's environment (env's, sh's): one Passenger
    /// inherited, held as a parent's are in the process's own environment, and one the run configuration gives.</summary>
    [AvaloniaFact]
    public void A_run_in_the_terminal_that_takes_a_variable_away_keeps_the_loaders_variables_on_macOS()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Only macOS's loader takes its variables out of what a program it guards runs");
        string folder = TestFolders.Create("terminal-loader-");
        // Folders searched before the loader's own, each empty, so every library loads as it would without them.
        string inherited = $"{Path.Combine(folder, "inherited")}:/usr/local/lib:/usr/lib";
        string given = Path.Combine(folder, "given");
        string carried = TerminalSession.KeptPrefix + "DYLD_LIBRARY_PATH";
        try
        {
            OwnEnvironment.Set("TMUX", "/tmp/tmux-501/default,4242,0");
            OwnEnvironment.Set("DYLD_FALLBACK_LIBRARY_PATH", inherited);
            string project = TestFolders.Write(folder, "Listed/Listed.csproj", TestProjects.Console());
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            window.SetConfigurations([new RunConfiguration
            {
                Name = "Loader",
                Project = PathNames.Canonical(project),
                // .NET's host keeps the loader's variables, where the system's own programs lose them.
                Program = Dotnet.Host(),
                Arguments = $"\"{Debugging.Helpers.Debuggees.Program("TerminalCases")}\" variables DYLD_FALLBACK_LIBRARY_PATH DYLD_LIBRARY_PATH TMUX {carried}",
                WorkingDirectory = folder,
                Environment = { ["DYLD_LIBRARY_PATH"] = given },
                BuildBeforeLaunch = false,
                Console = ConsoleKind.Terminal,
            }], "Loader");
            Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(1));
            TerminalSession program = Assert.IsType<TerminalRun>(window.RunInTerminal).Session;
            WaitForLine(program, "listed", "the program's variables listed");
            List<string> listed = Lines(program.Screen);
            Assert.False(listed.Any(line => line.StartsWith("TMUX=", StringComparison.Ordinal)),
                $"The outer tmux's TMUX reached the program, so its run took nothing away through env:\n{Screen(program)}");
            Assert.True(listed.Contains($"DYLD_FALLBACK_LIBRARY_PATH={inherited}"),
                $"DYLD_FALLBACK_LIBRARY_PATH, which Passenger inherited, never reached the program run in the terminal:\n{Screen(program)}");
            Assert.True(listed.Contains($"DYLD_LIBRARY_PATH={given}"),
                $"DYLD_LIBRARY_PATH, which the run configuration gives, never reached the program run in the terminal:\n{Screen(program)}");
            Assert.False(listed.Any(line => line.StartsWith(carried + "=", StringComparison.Ordinal)),
                $"What carried DYLD_LIBRARY_PATH past env reached the program too:\n{Screen(program)}");
            window.Close();
        }
        finally
        {
            OwnEnvironment.Set("TMUX", null);
            OwnEnvironment.Set("DYLD_FALLBACK_LIBRARY_PATH", null);
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The terminal's links: an address is a link a click opens (a drag from it selects instead) and so does
    /// Cmd+click (Ctrl+click elsewhere), wrapped across rows or not; a file's path is text until Cmd or Ctrl is held over
    /// it, and Cmd+click opens it at its line.</summary>
    [AvaloniaFact]
    public void An_address_in_the_terminal_opens_with_a_click_or_cmd_click_and_a_files_path_with_cmd_click()
    {
        string folder = TestFolders.Create("terminal-");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "first\nsecond\nthird\n");
        (MainWindow window, TerminalSession shell) = Open(folder);
        TerminalView view = window.TerminalPanel.View;
        RawInputModifiers command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        Run(window, "echo see https://example.com/terminal/docs. now");
        long line = WaitForLine(shell, "see https://example.com/terminal/docs. now", "the address");
        Point at = view.TranslatePoint(view.PointOf(line, 10), window)!.Value;
        window.MouseMove(at);
        Ui.Settle();
        Assert.Equal((line, 4, line, 36, false), view.HoveredLink is { } link ? (link.FromLine, link.FromColumn, link.ToLine, link.ToColumn, link.Hidden) : default);

        Drag(window, line, 12, line, 22);
        Assert.Equal("example.com", view.SelectedText);
        Assert.Empty(window.Browsed);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
        Assert.Equal(["https://example.com/terminal/docs"], window.Browsed.Select(u => u.AbsoluteUri));
        window.MouseDown(at, MouseButton.Left, command);
        window.MouseUp(at, MouseButton.Left, command);
        Ui.Settle();
        Assert.Equal(2, window.Browsed.Count);

        string longer = "https://example.com/" + new string('w', shell.Screen.Columns);
        Run(window, $"echo {longer}");
        long first = WaitForLine(shell, longer[..shell.Screen.Columns], "the long address's first row");
        Point wrapped = view.TranslatePoint(view.PointOf(first + 1, 3), window)!.Value;
        window.MouseMove(wrapped, command);
        Ui.Settle();
        Assert.Equal((first, 0, first + 1, 19), view.HoveredLink is { } both ? (both.FromLine, both.FromColumn, both.ToLine, both.ToColumn) : default);
        window.MouseDown(wrapped, MouseButton.Left, command);
        window.MouseUp(wrapped, MouseButton.Left, command);
        Ui.Settle();
        Assert.Equal(longer, window.Browsed[^1].AbsoluteUri);

        Run(window, "echo written to notes.txt:2 just now");
        long named = WaitForLine(shell, "written to notes.txt:2 just now", "the file's name");
        Point file = view.TranslatePoint(view.PointOf(named, 13), window)!.Value;
        window.MouseMove(file);
        Ui.Settle();
        Assert.Equal((named, 11, named, 21, true), view.HoveredLink is { } path ? (path.FromLine, path.FromColumn, path.ToLine, path.ToColumn, path.Hidden) : default);
        window.MouseDown(file, MouseButton.Left);
        window.MouseUp(file, MouseButton.Left);
        Ui.Settle();
        Assert.Null(window.EditorArea.Active);
        window.MouseMove(file, command);
        window.MouseDown(file, MouseButton.Left, command);
        window.MouseUp(file, MouseButton.Left, command);
        Ui.Settle();
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, "notes.txt")), window.EditorArea.Active?.Path);
        Assert.Equal(1, window.EditorArea.CodeEditor.CaretPosition.Line);
        Assert.Equal(3, window.Browsed.Count);
        window.Close();
    }

    /// <summary>A window over a folder of its own holding TerminalConsoleApp, restored, its configuration Keys running the
    /// program in the terminal with these arguments; without the C# language server, which a run asks nothing of.</summary>
    private static MainWindow RunWindow(string folder, string arguments)
    {
        string project = PathNames.Canonical(Debugging.Helpers.Debuggees.CopyTo("TerminalConsoleApp", folder));
        Restore.Run(folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.SetConfigurations([new RunConfiguration { Name = "Keys", Project = project, Arguments = arguments, Console = ConsoleKind.Terminal }], "Keys");
        return window;
    }

    /// <summary>Waits for the terminal's screen to show something, failing with what it shows instead, its size, its
    /// cursor and what has the keys.</summary>
    internal static void WaitForScreen(MainWindow window, TerminalSession shell, Func<TerminalScreen, bool> shows, string what)
    {
        try
        {
            Ui.WaitFor(() => shows(shell.Screen), what);
        }
        catch (TimeoutException waited)
        {
            TerminalScreen screen = shell.Screen;
            throw new TimeoutException($"{waited.Message}. The terminal, {screen.Columns} by {screen.Rows} with its cursor at row {screen.CursorRow} "
                + $"column {screen.CursorColumn}, {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"} having the keys, shows:\n{Screen(shell)}", waited);
        }
    }

    /// <summary>Runs a case of the TerminalCases debuggee through the terminal's shell, so its program writes the same
    /// bytes on every system: typed as a short launcher in the folder, so the command line wraps alike whatever the
    /// checkout's path.</summary>
    private static void RunCase(MainWindow window, params string[] words)
    {
        string launcher = Path.Combine(window.Folder!, OperatingSystem.IsWindows() ? "cases.cmd" : "cases");
        if (!File.Exists(launcher))
        {
            string program = Debugging.Helpers.Debuggees.Program("TerminalCases");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(launcher, $"@\"{Dotnet.Host()}\" \"{program}\" %*\r\n");
            }
            else
            {
                File.WriteAllText(launcher, $"#!/bin/sh\nexec \"{Dotnet.Host()}\" \"{program}\" \"$@\"\n");
                File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        Run(window, $"{(OperatingSystem.IsWindows() ? "cases" : "./cases")} {string.Join(' ', words)}");
    }

    /// <summary>Types a command into the terminal and presses Enter.</summary>
    private static void Run(MainWindow window, string command)
    {
        window.KeyTextInput(command);
        Ui.Press(window, "ENTER");
    }

    /// <summary>Waits for a line of the scrollback or screen to read just so, and gives its number.</summary>
    private static long WaitForLine(TerminalSession shell, string text, string what) => WaitForLine(shell, line => line == text, what);

    /// <summary>Waits for a line of the scrollback or screen whose text passes a test, and gives the newest one's number; a
    /// wait that runs out says what the terminal shows instead.</summary>
    private static long WaitForLine(TerminalSession shell, Func<string, bool> test, string what)
    {
        long found = -1;
        try
        {
            Ui.WaitFor(Shown, what, TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException($"{waited.Message}. The terminal shows:\n{Screen(shell)}", waited);
        }
        return found;

        bool Shown()
        {
            TerminalScreen screen = shell.Screen;
            for (long line = screen.ScreenTop + screen.Rows - 1; line >= screen.FirstLine; line--)
            {
                if (test(screen.LineAt(line).Text()))
                {
                    found = line;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>The scrollback's and screen's text, rows joined where the text wrapped, as a copy of them reads.</summary>
    private static List<string> Lines(TerminalScreen screen)
    {
        List<string> lines = [];
        System.Text.StringBuilder line = new();
        for (long at = screen.FirstLine; at < screen.ScreenTop + screen.Rows; at++)
        {
            TerminalLine row = screen.LineAt(at);
            line.Append(row.Text());
            if (!row.Wrapped)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
        }
        return lines;
    }

    /// <summary>A drag with the mouse from one cell to another, as a person chooses text.</summary>
    private static void Drag(MainWindow window, long fromLine, int fromColumn, long toLine, int toColumn)
    {
        TerminalView view = window.TerminalPanel.View;
        Point from = view.TranslatePoint(view.PointOf(fromLine, fromColumn), window)!.Value;
        Point to = view.TranslatePoint(view.PointOf(toLine, toColumn), window)!.Value;
        Ui.Drag(window, from, to);
    }

    /// <summary>The pointer moved with no button held along a line from one cell to another, a quarter of a cell at a time.</summary>
    private static void Hover(MainWindow window, long line, int fromColumn, int toColumn)
    {
        TerminalView view = window.TerminalPanel.View;
        Point from = view.TranslatePoint(view.PointOf(line, fromColumn), window)!.Value;
        Point to = view.TranslatePoint(view.PointOf(line, toColumn), window)!.Value;
        int steps = Math.Max(1, Math.Abs(toColumn - fromColumn) * 4);
        for (int step = 0; step <= steps; step++)
        {
            window.MouseMove(from + ((to - from) * ((double)step / steps)), RawInputModifiers.None);
        }
        Ui.Settle();
    }

    /// <summary>The mouse reports the TerminalCases program read in its given run (from 1), in order: the lines after that
    /// run's "mouse?".</summary>
    private static List<string> Reports(TerminalScreen screen, int run)
    {
        List<string> lines = Lines(screen);
        int asked = lines.Select((line, at) => (line, at)).Where(l => l.line == "mouse?").Select(l => l.at).ElementAt(run - 1);
        return [.. lines.Skip(asked + 1).TakeWhile(l => l.StartsWith("^[[<", StringComparison.Ordinal))];
    }

    /// <summary>The window over a folder, its Terminal open on the system's plain shell (cmd.exe on Windows, sh elsewhere).</summary>
    private static (MainWindow Window, TerminalSession Shell) Open(string folder) => Open(folder, OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");

    /// <summary>The window over a folder, its Terminal open on a shell of the test's and the keys going to it.</summary>
    internal static (MainWindow Window, TerminalSession Shell) Open(string folder, string shell, params string[] arguments)
    {
        MainWindow window = Ui.OpenFolder(folder);
        return (window, OpenTerminal(window, shell, arguments));
    }

    /// <summary>The window's Terminal open on a shell of the test's, the keys going to it.</summary>
    private static TerminalSession OpenTerminal(MainWindow window, string shell, params string[] arguments)
    {
        window.TerminalPanel.Shell = (shell, arguments);
        Ui.Run(window, AppActions.ShowTerminal);
        Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
        Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the terminal");
        Ui.WaitFor(() => window.TerminalPanel.Active!.Screen.CursorColumn > 0, "the shell's prompt");
        return window.TerminalPanel.Active!;
    }

    /// <summary>The terminal's Paste: Cmd+V on the Mac, Ctrl+Shift+V elsewhere.</summary>
    internal static string PasteKey => OperatingSystem.IsMacOS() ? "meta V" : "control shift V";

    /// <summary>The terminal's Copy: Cmd+C on the Mac, Ctrl+Shift+C elsewhere.</summary>
    private static string CopyKey => OperatingSystem.IsMacOS() ? "meta C" : "control shift C";

    internal static void Copy(TopLevel window, string text) => Ui.Await(window.Clipboard!.SetTextAsync(text), "the clipboard to take the text");

    /// <summary>The number of the last of seq's lines in the terminal, its scrollback included; -1 before the first.</summary>
    internal static long LastLine(TerminalSession session)
    {
        long last = -1;
        foreach (string line in Screen(session).Split('\n'))
        {
            string[] words = line.Split(' ', 3);
            if (words.Length == 3 && words[0] == "line" && long.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out long number))
            {
                last = Math.Max(last, number);
            }
        }
        return last;
    }

    internal static string Screen(TerminalSession session) =>
        string.Join('\n', session.Screen.Scrollback.Select(l => l.Text()).Concat(session.Screen.Lines.Select(l => l.Text())));
}

/// <summary>The terminal never holds the window: a flood of output leaves it taking keys and Ctrl+C stops it, a paste into
/// a program that reads nothing returns before the program reads it, and the shell echoes what is typed before the busy
/// pool would get to it. The pool is taken from everything beside it, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class TerminalResponsivenessTests
{
    [AvaloniaFact]
    public void A_flood_of_output_leaves_the_window_taking_keys_and_Ctrl_C_stops_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The flood is sh's seq, stopped by a Unix pseudo-terminal's own Ctrl+C, which ConPTY has none of");
        string folder = TestFolders.Create("terminal-");
        MainWindow? window = null;
        try
        {
            // 500 MB of numbered lines, made as read: many times what the window takes before Ctrl+C, but not endless.
            string lines = $"seq -f 'line %07.0f {new string('x', 86)}'";
            (window, TerminalSession shell) = TerminalTests.Open(folder, "/bin/sh");
            // The lines' glyphs drawn once first, whose first layout holds a slow machine's window for a second anyway.
            window.KeyTextInput($"{lines} 0 2");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => shell.Screen.ScreenText().Contains("line 0000002", StringComparison.Ordinal), "the first lines on the screen");
            Ui.Settle();
            window.KeyTextInput("clear");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => !shell.Screen.ScreenText().Contains("xxxxxxxx", StringComparison.Ordinal), "the screen cleared");
            window.KeyTextInput($"{lines} 0 4999999");
            using (InputWait waits = new())
            {
                // Pressed and waited for without settling, which waits for all the IDE's background work, seq pouring on.
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                Ui.PumpUntil(() => shell.Screen.ScreenText().Contains("xxxxxxxx", StringComparison.Ordinal), "seq's lines to come onto the screen");
                // Looks at the window's thread, each posted once the last is answered, as keys come: twenty answered as seq pours on.
                Ui.Pumped(() => waits.Answered >= 20, TimeSpan.FromSeconds(20));
                waits.Stop();
                Assert.True(waits.Answered >= 20, $"the window answered {waits.Answered} of {waits.Posted} looks at its thread while seq's output poured in");
            }

            // The window falls behind a moment: seq waits for it, unread, so Ctrl+C finds it running.
            long atCtrlC = -1;
            window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
                {
                    atCtrlC = TerminalTests.LastLine(shell);
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            Ui.Stall(TimeSpan.FromSeconds(2), "the window behind on seq's output, as a heavy frame holds it");
            Ui.Press(window, "control C");
            window.KeyTextInput("echo after-$((6*7))");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => shell.Screen.ScreenText().Contains("after-42", StringComparison.Ordinal),
                $"the shell's answer after Ctrl+C:\n{shell.Screen.ScreenText()}", TimeSpan.FromSeconds(30));
            long last = TerminalTests.LastLine(shell);
            Assert.True(last < 4_999_999,
                $"seq ran to the end of its 500 MB, line {atCtrlC} on the screen when Ctrl+C was pressed: Ctrl+C never stopped it");
            Assert.True(last - atCtrlC < 40_000,
                $"{(last - atCtrlC) / 10_000.0:F1} MB of seq's output came after Ctrl+C: it was read into memory while the window was behind");
        }
        finally
        {
            // seq's lines outlast a test failing part-way, and end with the window's shells.
            window?.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_paste_into_a_program_that_reads_nothing_leaves_the_window_taking_keys()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "A Unix pseudo-terminal's input queue holds a kilobyte or so, past which a write waits for the program; ConPTY's has no such limit to hold");
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = TerminalTests.Open(folder, "/bin/sh");
            // A loop that reads none of its input until the test lets it end.
            string go = Path.Combine(folder, "go");
            window.KeyTextInput($"while [ ! -e '{go}' ]; do sleep 0.1; done");
            Ui.Press(window, "ENTER");
            // The cursor past the command: macOS's bash may write a line of its own after it (child setpgid … not permitted).
            TerminalTests.WaitForScreen(window, shell, s => AboveCursor(s, "sleep 0.1"), "the loop to start");

            // Far more than the pseudo-terminal holds, while the loop reads none of it.
            TerminalTests.Copy(window, string.Concat(Enumerable.Range(0, 2000).Select(i => $"echo pasted {i}\n")));
            bool pasted = false;
            bool letGo = false;
            // A paste that waits for the loop to read it would never return: the loop is let go, the test failing, not hanging.
            Task giveUp = Waiting.Apart(() =>
            {
                if (!Waiting.Until(() => Volatile.Read(ref pasted), TimeSpan.FromSeconds(30)))
                {
                    Volatile.Write(ref letGo, true);
                    File.WriteAllText(go, string.Empty);
                }
            });
            Ui.Press(window, TerminalTests.PasteKey);
            Volatile.Write(ref pasted, true);
            Assert.False(Volatile.Read(ref letGo), "the paste held the window until the program that reads nothing was let go");
            File.WriteAllText(go, string.Empty);
            Ui.Await(giveUp, "the paste's give-up to stand down");
            // What the pseudo-terminal took it echoes, so the paste did go.
            Ui.WaitFor(() => TerminalTests.Screen(shell).Contains("echo pasted 0", StringComparison.Ordinal), "the start of the paste echoed", TimeSpan.FromSeconds(10));

            Ui.Press(window, "shift ESCAPE");
            Assert.False(window.DockHost.IsShown(ToolWindowIds.Terminal), "the terminal is still shown after Shift+Escape");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A terminal ended while its program writes on and has read none of a paste ends without holding the window:
    /// Close Solution comes back, and the shell and its program end.</summary>
    [AvaloniaFact]
    public void A_terminal_whose_program_read_none_of_a_paste_ends_without_holding_the_window()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "A Unix pseudo-terminal's input queue holds a kilobyte or so, past which a write waits for the program; ConPTY's has no such limit to hold");
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = TerminalTests.Open(folder, "/bin/sh");
            int shellId = shell.ProcessId!.Value;
            // A loop that reads none of its input until the test lets it go, then yes, which reads none either and writes on.
            string go = Path.Combine(folder, "go");
            window.KeyTextInput($"while [ ! -e '{go}' ]; do sleep 0.1; done; yes tick");
            Ui.Press(window, "ENTER");
            TerminalTests.WaitForScreen(window, shell, s => AboveCursor(s, "yes tick"), "the loop to start");
            TerminalTests.Copy(window, string.Concat(Enumerable.Range(0, 2000).Select(i => $"echo pasted {i}\n")));
            Ui.Press(window, TerminalTests.PasteKey);
            Ui.WaitFor(() => TerminalTests.Screen(shell).Contains("echo pasted 0", StringComparison.Ordinal), "the start of the paste echoed", TimeSpan.FromSeconds(10));
            File.WriteAllText(go, string.Empty);
            int[] started = [];
            Ui.WaitFor(() => (started = [.. Processes.Descendants(shellId)]).Length > 0 && shell.Screen.Lines.Count(l => l.Text() == "tick") > 2,
                "yes to write on");

            Assert.Null(window.CloseSolution());
            Ui.WaitFor(() => !Processes.IsRunning(shellId) && !started.Any(Processes.IsRunning),
                () => $"the shell and what it ran to end: {string.Join(", ", started.Prepend(shellId).Where(Processes.IsRunning).Select(Processes.Describe))}");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Whether a line above the cursor, on the screen or in the scrollback, holds the text.</summary>
    private static bool AboveCursor(TerminalScreen screen, string text)
    {
        for (long line = screen.FirstLine; line < screen.ScreenTop + screen.CursorRow; line++)
        {
            if (screen.LineAt(line).Text().Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    [AvaloniaFact]
    public void The_shell_echoes_what_is_typed_while_every_thread_of_the_pool_is_busy()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shell held to its answer is sh, a Unix one");
        string folder = TestFolders.Create("terminal-");
        using ManualResetEventSlim release = new();
        try
        {
            (MainWindow window, TerminalSession shell) = TerminalTests.Open(folder, "/bin/sh");
            // The pool's threads held, as builds and debuggers hold them, and more jobs waiting than it adds threads for soon.
            ThreadPool.GetMinThreads(out int threads, out _);
            int jobs = (threads * 4) + 32;
            int taken = 0;
            for (int i = 0; i < jobs; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    Interlocked.Increment(ref taken);
                    release.Wait(TimeSpan.FromSeconds(60));
                }, null);
            }
            Assert.True(Waiting.Until(() => Volatile.Read(ref taken) >= threads, TimeSpan.FromSeconds(10)), $"The pool took {Volatile.Read(ref taken)} of the {threads} jobs meant to hold its threads");
            window.KeyTextInput("echo typed");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            // Frames pumped by hand: settling waits for background work, which the busy pool would hold.
            int takenAtEcho = -1;
            Ui.Pumped(() => TerminalTests.Screen(shell).Contains("\ntyped", StringComparison.Ordinal) && (takenAtEcho = Volatile.Read(ref taken)) >= 0, TimeSpan.FromSeconds(60));
            release.Set();
            Assert.True(takenAtEcho >= 0, $"with the pool's {threads} threads busy, the shell never answered a command typed:\n{TerminalTests.Screen(shell)}");
            // The pool takes its jobs in order, so terminal work queued on it would run only after all of the test's.
            Assert.True(takenAtEcho < jobs,
                $"the shell's answer to a command typed came only once the pool had taken all {jobs} jobs queued before it: the terminal waited for the pool");
            window.Close();
        }
        finally
        {
            release.Set();
            TestFolders.Delete(folder);
        }
    }
}
