using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Terminal;

namespace Passenger.Tests;

/// <summary>The Terminal window as programs use it: a real shell in a pseudo-terminal, read as xterm is (scroll regions, the
/// alternate screen, colours, wide characters, answers to a program's questions, the scrollback), keys sent as xterm sends
/// them, typed commands and keys taken ahead of the IDE; floods of output and stuck pastes never hold the window, pastes
/// stay text, accents and emoji take the columns the shell counts, lines reflow as it resizes, a place or a selection
/// holds as output streams, and no program but a terminal's own holds its pseudo-terminal. A run configuration that uses
/// the terminal runs its program in a tab of its own, which reads what is typed there and draws as it likes, and which
/// Stop ends with all it started. What a program writes or reads comes from the TerminalCases debuggee, the same bytes on
/// every system, so Windows checks what ConPTY makes of them.</summary>
[AboutMacOS]
public sealed class TerminalTests
{
    /// <summary>A shell in the Terminal starts in the window's folder, takes commands and hides as it exits; a full-
    /// screen program's screen gives the shell's back, a program's questions are answered, an emoji takes two columns
    /// and an input method's text goes in as committed; the scrollback holds output, its place, a selection and a long
    /// line through a resize, a long log costing per line and frame what a short one does, Clear Buffer leaves the
    /// prompt, and the cursor takes the shape set.</summary>
    [AvaloniaFact]
    public void A_shell_in_the_terminal_takes_commands_draws_what_programs_write_and_keeps_its_scrollback()
    {
        Flow.StepApart<TerminalTests>("the terminal window runs a shell that takes typed commands and hides when it exits", t => t.The_terminal_window_runs_a_shell_that_takes_typed_commands_and_hides_when_it_exits());
        Flow.StepApart<TerminalTests>("a terminal shown as the window is made starts its shell in the folder the window starts on", t => t.A_terminal_shown_as_the_window_is_made_starts_its_shell_in_the_folder_the_window_starts_on());
        Flow.StepApart<TerminalTests>("what a full screen program draws lands where it puts it and the shell's screen comes back after", t => t.What_a_full_screen_program_draws_lands_where_it_puts_it_and_the_shells_screen_comes_back_after());
        Flow.StepApart<TerminalTests>("a program's questions about the terminal are answered", t => t.A_programs_questions_about_the_terminal_are_answered());
        Flow.StepApart<TerminalTests>("an emoji takes the two columns the shell counts so the text after it stays in place", t => t.An_emoji_takes_the_two_columns_the_shell_counts_so_the_text_after_it_stays_in_place());
        Flow.StepApart<TerminalTests>("text an input method composes shows at the cursor and reaches the program only as it is committed", t => t.Text_an_input_method_composes_shows_at_the_cursor_and_reaches_the_program_only_as_it_is_committed());
        Flow.StepApart<TerminalTests>("output past the screen goes to the scrollback and clear buffer leaves only the prompt at the top", t => t.Output_past_the_screen_goes_to_the_scrollback_and_clear_buffer_leaves_only_the_prompt_at_the_top());
        Flow.StepApart<TerminalTests>("scrolled back the terminal holds its place as output arrives", t => t.Scrolled_back_the_terminal_holds_its_place_as_output_arrives());
        Flow.StepApart<TerminalTests>("a selection stays on its text as output streams past the scrollback's limit", t => t.A_selection_stays_on_its_text_as_output_streams_past_the_scrollbacks_limit());
        Flow.StepApart<TerminalTests>("a long line reads whole after the terminal narrows and widens again and the prompt keeps its cursor", t => t.A_long_line_reads_whole_after_the_terminal_narrows_and_widens_again_and_the_prompt_keeps_its_cursor());
        Flow.StepApart<TerminalTests>("a log three times the scrollback costs the window no more for each line than one just past it", t => t.A_log_three_times_the_scrollback_costs_the_window_no_more_for_each_line_than_one_just_past_it());
        Flow.StepApart<TerminalTests>("a log in colour streaming up the screen costs a frame what one changed row does", t => t.A_log_in_colour_streaming_up_the_screen_costs_a_frame_what_one_changed_row_does());
        Flow.StepApart<TerminalTests>("copy on selection copies a finished drag only when enabled", t => t.Copy_on_selection_copies_a_finished_drag_only_when_enabled());
        Flow.StepApart<TerminalTests>("the terminal draws the configured cursor shape", t => t.The_terminal_draws_the_configured_cursor_shape(CursorShape.Bar, false, true, false));
    }

    /// <summary>Keys reach a program in the terminal as xterm sends them in either cursor mode, Enter with each
    /// modifier and Ctrl+J as it asked for them whatever the keymap binds, while every tool window's shortcut still
    /// opens it; and the terminal takes the keys from the file only when the person shows it, a window opening on a
    /// kept layout with the terminal open giving the file it opens the keys.</summary>
    [AvaloniaFact]
    public void The_keys_reach_the_program_in_the_terminal_and_the_ide_only_as_meant()
    {
        Flow.StepApart<TerminalTests>("keys reach a program as xterm sends them in either cursor mode", t => t.Keys_reach_a_program_as_xterm_sends_them_in_either_cursor_mode());
        Flow.StepApart<TerminalTests>("enter with each modifier and Ctrl J reach a program as it asked for them in every keymap", t => t.Enter_with_each_modifier_and_Ctrl_J_reach_a_program_as_it_asked_for_them_in_every_keymap(KeymapPreset.IntelliJ));
        Flow.StepApart<TerminalTests>("every tool window's shortcut opens it while the terminal has the keys", t => t.Every_tool_windows_shortcut_opens_it_while_the_terminal_has_the_keys());
        Flow.StepApart<TerminalTests>("a window opening on a kept layout with the terminal open gives the file it opens the keys", t => t.A_window_opening_on_a_kept_layout_with_the_terminal_open_gives_the_file_it_opens_the_keys());
        Flow.StepApart<TerminalTests>("the terminal shown by its stripe button takes the keys from the file", t => t.The_terminal_shown_by_its_stripe_button_takes_the_keys_from_the_file());
    }

    /// <summary>Nothing reaches a terminal's shell to run that the person did not mean: a dropped file whose name holds
    /// a command is read as its name, and what Passenger took out of its environment and a tmux or screen it was
    /// started in reach no shell or run.</summary>
    [AvaloniaFact]
    public void Nothing_reaches_a_terminals_shell_to_run_that_the_person_did_not_type()
    {
        Flow.StepApart<TerminalTests>("a file dropped on the terminal whose name holds a command is read as its name and nothing runs", t => t.A_file_dropped_on_the_terminal_whose_name_holds_a_command_is_read_as_its_name_and_nothing_runs());
        Flow.StepApart<TerminalTests>("what Passenger takes out of its environment as it starts reaches no shell in its terminal", t => t.What_Passenger_takes_out_of_its_environment_as_it_starts_reaches_no_shell_in_its_terminal());
        Flow.StepApart<TerminalTests>("a tmux or screen Passenger was started in reaches no shell or run in its terminal", t => t.A_tmux_or_screen_Passenger_was_started_in_reaches_no_shell_or_run_in_its_terminal());
    }

    /// <summary>A program run in the terminal reads a key and a line typed there and draws in colour where it puts the
    /// cursor, one needing a terminal in the Run window is offered one, Stop ends it with what it started, a variable
    /// the run takes away never reaches it, and a line it writes as it ends at once is on its tab every run.</summary>
    [AvaloniaFact]
    public void A_program_run_in_the_terminal_reads_what_is_typed_shows_all_it_wrote_and_ends_with_what_it_started()
    {
        Flow.StepApart<TerminalTests>("a program run in the terminal reads a key and a line typed there and draws in colour where it puts the cursor", t => t.A_program_run_in_the_terminal_reads_a_key_and_a_line_typed_there_and_draws_in_colour_where_it_puts_the_cursor());
        Flow.StepApart<TerminalTests>("a program needing a terminal in the Run window is offered one and reads a key there", t => t.A_program_needing_a_terminal_in_the_Run_window_is_offered_one_and_reads_a_key_there());
        Flow.StepApart<TerminalTests>("stop ends a program running in the terminal and what it started", t => t.Stop_ends_a_program_running_in_the_terminal_and_what_it_started());
        Flow.StepApart<TerminalTests>("a variable a run in the terminal takes away never reaches its program", t => t.A_variable_a_run_in_the_terminal_takes_away_never_reaches_its_program());
        Flow.StepApart<TerminalTests>("a program that writes a line and ends at once has it on its tab every run", t => t.A_program_that_writes_a_line_and_ends_at_once_has_it_on_its_tab_every_run());
    }

    /// <summary>On a Unix pseudo-terminal a program's reset gives the cursor back the shape the person set, and nothing
    /// run in one terminal or started by the IDE can type into another.</summary>
    [AvaloniaFact]
    public void On_a_Unix_terminal_a_programs_reset_restores_the_cursor_and_nothing_types_into_another_terminal()
    {
        Flow.StepApart<TerminalTests>("a program's reset gives back the cursor shape the person set", t => t.Programs_override_the_configured_cursor_and_reset_restores_it("\\033[0 q"));
        Flow.StepApart<TerminalTests>("nothing run in one terminal or started by the IDE can type into another terminal", t => t.Nothing_run_in_one_terminal_or_started_by_the_IDE_can_type_into_another_terminal());
    }


    /// <summary>An address or a file's path in the terminal opens with a click, and words a program links (OSC 8) are a
    /// link a click opens: a web address in the browser, a file only on this machine, the disk asked only on the click,
    /// what it opens told from its address alone, and a path naming another machine never asked of the disk, in the
    /// terminal or a console.</summary>
    [AvaloniaFact]
    public void Links_in_the_terminal_open_what_they_name_and_never_ask_another_machine_of_the_disk()
    {
        Flow.StepApart<TerminalTests>("an address in the terminal opens with a click or cmd click and a file's path with cmd click", t => t.An_address_in_the_terminal_opens_with_a_click_or_cmd_click_and_a_files_path_with_cmd_click(false));
        Flow.StepApart<TerminalTests>("words a program links to a web address are a link a click opens", t => t.Words_a_program_links_to_a_web_address_are_a_link_a_click_opens());
        Flow.StepApart<TerminalTests>("words a program links to a file are a link only on this machine and a click asks the disk", t => t.Words_a_program_links_to_a_file_are_a_link_only_on_this_machine_and_a_click_asks_the_disk());
        Flow.StepApart<TerminalTests>("what a program's link opens is told from its address alone", t => t.What_a_programs_link_opens_is_told_from_its_address_alone());
        Flow.StepApart<TerminalTests>("a path naming another machine in the terminal or a console is never asked of the disk", t => t.A_path_naming_another_machine_in_the_terminal_or_a_console_is_never_asked_of_the_disk());
    }

    private void The_terminal_draws_the_configured_cursor_shape(CursorShape style, bool centre, bool left, bool bottom)
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder, OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
            window.ApplySettings(window.Settings with { TerminalCursorStyle = style });
            Ui.Settle();
            TerminalView view = window.TerminalPanel.View;
            long row = shell.Screen.ScreenTop + shell.Screen.CursorRow;
            int column = shell.Screen.CursorColumn;
            Point cell = view.PointOf(row, column);
            double width = view.PointOf(row, column + 1).X - cell.X;
            double height = view.PointOf(row + 1, column).Y - cell.Y;
            Color background = ((ISolidColorBrush)view.FindResource(window.ActualThemeVariant, TokenKeys.Terminal_Background)!).Color;
            bool Drawn(Point at) => Ui.Pixel(window, view.TranslatePoint(at, window)!.Value) != background;
            Assert.Equal(centre, Drawn(cell));
            Assert.Equal(left, Drawn(cell + new Point(1 - width / 2, 0)));
            Assert.Equal(bottom, Drawn(cell + new Point(0, height / 2 - 1)));
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private void Copy_on_selection_copies_a_finished_drag_only_when_enabled()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder, OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
            Run(window, "echo selection-marker");
            long line = WaitForLine(shell, "selection-marker", "the text to select");
            Copy(window, "clipboard kept");
            Drag(window, line, 0, line, 8);
            Assert.Equal("clipboard kept", Ui.Clipboard(window));
            SettingsDialog dialog = SettingsTests.TerminalDialog(window);
            Assert.Contains("Copy on selection", dialog.Shown);
            CheckBox copy = SettingsTests.Editor<CheckBox>(dialog, "Copy on selection");
            Ui.Click(copy);
            dialog.Ok();
            Ui.Settle();
            TerminalView view = window.TerminalPanel.View;
            Ui.Click(view, view.PointOf(line, 13));
            Assert.Equal("clipboard kept", Ui.Clipboard(window));
            Drag(window, line, 0, line, 8);
            Assert.Equal("selection", Ui.Clipboard(window));
            Copy(window, "waiting for release");
            Ui.Click(view, view.PointOf(line, 13));
            Point from = view.TranslatePoint(view.PointOf(line, 0), window)!.Value;
            Point to = view.TranslatePoint(view.PointOf(line, 8), window)!.Value;
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            Ui.Settle();
            Assert.Equal("waiting for release", Ui.Clipboard(window));
            window.MouseUp(to, MouseButton.Left);
            Ui.Settle();
            Assert.Equal("selection", Ui.Clipboard(window));
            Ui.DoubleClick(view, view.PointOf(line, 3));
            Assert.Equal("selection-marker", Ui.Clipboard(window));
            if (!OperatingSystem.IsWindows())
            {
                Run(window, "printf '\\033[?1000h\\033[?1006h'; read x; printf '\\033[?1000l\\033[?1006l'");
                WaitForScreen(window, shell, s => s.MouseTracking == MouseTracking.Press, "the program to request mouse reports");
                Copy(window, "program owns the mouse");
                Drag(window, line, 0, line, 8);
                Assert.Equal("program owns the mouse", Ui.Clipboard(window));
                Ui.Click(view, view.PointOf(line, 13));
                window.MouseDown(from, MouseButton.Left, RawInputModifiers.Shift);
                window.MouseMove(to, RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
                window.MouseUp(to, MouseButton.Left, RawInputModifiers.Shift);
                Ui.Settle();
                Assert.Equal("selection", Ui.Clipboard(window));
                Ui.Press(window, "ENTER");
                WaitForScreen(window, shell, s => s.MouseTracking == MouseTracking.None, "the program to release the mouse");
            }
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private void Programs_override_the_configured_cursor_and_reset_restores_it(string reset)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The program's cursor requests are written by sh's printf, which cmd.exe has none of");
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder, "/bin/sh");
            SettingsDialog dialog = SettingsTests.TerminalDialog(window);
            Assert.Contains("Cursor style", dialog.Shown);
            ComboBox style = SettingsTests.Editor<ComboBox>(dialog, "Cursor style");
            style.SelectedIndex = 2;
            dialog.Ok();
            Ui.Settle();
            window.TerminalPanel.FocusView();
            Ui.Settle();
            TerminalView view = window.TerminalPanel.View;
            Color background = ((ISolidColorBrush)view.FindResource(window.ActualThemeVariant, TokenKeys.Terminal_Background)!).Color;
            Assert.Equal(background, Ui.Pixel(window, CursorCentre(window)));
            Run(window, $"printf '\\033[2 q'; read x; printf '{reset}'");
            WaitForScreen(window, shell, s => s.CursorColumn == 0 && s.RequestedCursorShape == CursorShape.Block, "the program waiting with its block cursor");
            Assert.NotEqual(background, Ui.Pixel(window, CursorCentre(window)));
            Ui.Press(window, "ENTER");
            WaitForScreen(window, shell, s => s.CursorColumn > 0, "the prompt after the program resets the terminal");
            Assert.Equal(background, Ui.Pixel(window, CursorCentre(window)));
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private void What_a_full_screen_program_draws_lands_where_it_puts_it_and_the_shells_screen_comes_back_after()
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

    private void A_programs_questions_about_the_terminal_are_answered()
    {
        (MainWindow window, TerminalSession shell) = Open(TestFolders.Create("terminal-"));
        // Where the cursor is, as a program asks before drawing, and what the terminal is, each answer read back.
        RunCase(window, "ask");
        WaitForLine(shell, "at 5;10 is ?62;22", "the program's answers");
        window.Close();
    }

    private void Keys_reach_a_program_as_xterm_sends_them_in_either_cursor_mode()
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
        WaitForPromptBelow(window, shell, "^[[A^[[1;5C^[[3~^[[Z");

        RunCase(window, "keys", "application");
        WaitForScreen(window, shell, s => s.ApplicationCursorKeys && s.Lines.Count(l => l.Text() == "keys?") == 2, "the program to wait in the application cursor mode");
        Ui.Press(window, "UP");
        Ui.Press(window, "ENTER");
        WaitForLine(shell, "^[OA", "Up as the program read it in the application cursor mode");
        Ui.Press(window, "control C");
        window.Close();
    }

    /// <summary>Enter held with each modifier, Enter alone and Ctrl+J reach a program in every keymap, no IDE shortcut
    /// taking one from the terminal: as xterm sends them (Ctrl+J a line feed), a line feed after each Return in new line
    /// mode, until the program asks for the kitty keyboard protocol, then each as that protocol's own key, Enter alone
    /// still a carriage return. The program asks for disambiguation and alternate keys, as OpenCode's library does, and is
    /// told disambiguation alone is in force; its flags go when it pops them.</summary>
    private void Enter_with_each_modifier_and_Ctrl_J_reach_a_program_as_it_asked_for_them_in_every_keymap(KeymapPreset keymap)
    {
        (MainWindow window, TerminalSession shell) = Open(TestFolders.Create("terminal-"));
        try
        {
            window.ApplySettings(window.Settings with { Keymap = keymap });
            Ui.Settle();
            Assert.True(window.TerminalPanel.View.IsFocused, $"Choosing the {keymap} keymap took the keys from the terminal");
            string[] keys = ["shift ENTER", "control ENTER", "alt ENTER", "control shift ENTER", "alt shift ENTER", "control J", "ENTER"];
            // Option on the Mac types rather than sends ESC, so xterm's Alt+Enter is Enter there.
            string alt = OperatingSystem.IsMacOS() ? "" : "^[";

            RunCase(window, "keyboard");
            WaitForLine(shell, "keyboard?", "the program to wait for its keys");
            Press(window, keys);
            string legacy = $"^M^M{alt}^M^M{alt}^M^J^M";
            WaitForScreen(window, shell, s => Lines(s).Contains(legacy), $"the keys as xterm sends them, {legacy}, read by a program that never asked for more");
            WaitForPromptBelow(window, shell, legacy);

            RunCase(window, "keyboard", "newline");
            WaitForScreen(window, shell, s => Lines(s).Count(l => l == "keyboard?") == 2, "the program to wait for its keys in new line mode");
            Press(window, keys);
            string newLine = $"^M^J^M^J{alt}^M^J^M^J{alt}^M^J^J^M^J";
            WaitForScreen(window, shell, s => Lines(s).Contains(newLine), $"the keys as xterm sends them in new line mode, each Return a carriage return and a line feed, {newLine}");
            WaitForPromptBelow(window, shell, newLine);

            RunCase(window, "keyboard", "5");
            WaitForLine(shell, "flags 1", "the program told disambiguation alone is in force");
            WaitForScreen(window, shell, s => s.KeyboardFlags == TerminalKeys.Disambiguate && Lines(s).Count(l => l == "keyboard?") == 3,
                "the program to wait for its keys under the kitty keyboard protocol");
            Press(window, keys);
            string kitty = "^[[13;2u^[[13;5u^[[13;3u^[[13;6u^[[13;4u^[[106;5u^M";
            WaitForScreen(window, shell, s => Lines(s).Contains(kitty), $"the keys as the kitty keyboard protocol sends them, {kitty}");
            WaitForScreen(window, shell, s => s.KeyboardFlags == 0, "the program's flags to go as it pops them");
            WaitForPromptBelow(window, shell, kitty);
            Run(window, "echo after");
            WaitForLine(shell, "after", "a command Enter runs once the program has ended");
        }
        finally
        {
            // The keymap in force is the process's; the next test starts on the platform's own.
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VisualStudio });
            window.Close();
        }

        static void Press(MainWindow window, string[] keys)
        {
            foreach (string key in keys)
            {
                Ui.Press(window, key);
            }
            window.KeyTextInput("q");
        }
    }

    private void Output_past_the_screen_goes_to_the_scrollback_and_clear_buffer_leaves_only_the_prompt_at_the_top()
    {
        (MainWindow window, TerminalSession shell) = Open(TestFolders.Create("terminal-"));
        TerminalScreen screen = shell.Screen;
        RunCase(window, "long", (screen.Columns + 4).ToString(CultureInfo.InvariantCulture));
        WaitForLine(shell, "200", "the program's lines");
        // Cleared before the shell's prompt came, the screen would be left empty, the prompt coming after it.
        WaitForPromptBelow(window, shell, "200");
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

    private void A_terminal_shown_as_the_window_is_made_starts_its_shell_in_the_folder_the_window_starts_on()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            MainWindow window = new() { Width = 1200, Height = 800 };
            // Shown before any folder is open, as a theme's layout or the person's kept one shows it.
            window.ShowToolWindow(ToolWindowIds.Terminal);
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            Assert.True(Screenshot.Prepare(window, LaunchOptions.Parse([folder]), []), "The window never settled");
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
            TerminalSession shell = window.TerminalPanel.Active!;
            // A layout's Terminal takes no keys as it shows: a click in it gives them.
            Ui.Click(window.TerminalPanel.View);
            Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the terminal clicked");

            Run(window, OperatingSystem.IsWindows() ? "echo %CD%" : "pwd");
            string where = PathNames.Canonical(folder);
            Ui.WaitFor(() => Screen(shell).Contains(where, StringComparison.Ordinal),
                () => $"the shell shown as the window was made started outside the folder it opened on:\n{Screen(shell)}");
            Assert.True(window.TerminalPanel.Tabs.Count == 1, $"{window.TerminalPanel.Tabs.Count} shells were started for the one window shown");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The person's window on the layout they left with the Terminal open, starting as it shows on a folder and a
    /// file, gives the file's editor the keys, and what is typed goes into the file.</summary>
    private void A_window_opening_on_a_kept_layout_with_the_terminal_open_gives_the_file_it_opens_the_keys()
    {
        string state = TestFolders.Create("terminal-state-");
        try
        {
            MainWindow first = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false };
            first.Show();
            first.KeepLayout();
            first.DockHost.Show(ToolWindowIds.Terminal);
            first.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            Ui.Settle();
            first.Close();
            Ui.Settle();

            MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false };
            window.KeepLayout();
            OpensWithTheKeysInTheFile(window, startsAsItShows: true);
        }
        finally
        {
            TestFolders.Delete(state);
        }
    }

    /// <summary>Opens a window whose layout shows the Terminal on a folder and a file in it, started after it shows or as it
    /// shows, and finds the file's editor with the keys once the Terminal's shell has started, and typed text in the file.</summary>
    private static void OpensWithTheKeysInTheFile(MainWindow window, bool startsAsItShows)
    {
        string folder = TestFolders.Create("terminal-keys-");
        try
        {
            string notes = Path.Combine(folder, "notes.txt");
            File.WriteAllText(notes, "written\n");
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Terminal), "The layout did not show the Terminal as the window was made");
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            LaunchOptions launch = LaunchOptions.Parse([folder, "--file", notes]);
            if (startsAsItShows)
            {
                window.Opened += (_, _) => window.Start(launch);
                window.Show();
                Ui.Settle();
            }
            else
            {
                Assert.True(Screenshot.Prepare(window, launch, []), "The window never settled");
            }
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true } shell && shell.Screen.CursorColumn > 0, "the Terminal's shell to prompt");
            Ui.Settle();
            string how = startsAsItShows ? "started as it showed" : "started after it showed";
            Assert.True(window.EditorArea.Active?.Path == PathNames.Canonical(notes) && window.EditorArea.CodeEditor.IsKeyboardFocusWithin && !window.TerminalPanel.View.IsFocused,
                $"The window {how} on notes.txt left the keys {(window.TerminalPanel.View.IsFocused ? "in the Terminal its layout showed" : "away from the file")}, the file shown {window.EditorArea.Active?.Path}");

            window.KeyTextInput("typed-into-the-file");
            Ui.Settle();
            Assert.True(window.EditorArea.CodeEditor.Document.Snapshot.ToString().Contains("typed-into-the-file", StringComparison.Ordinal)
                && !Screen(window.TerminalPanel.Active!).Contains("typed-into-the-file", StringComparison.Ordinal),
                $"What was typed in the window {how} went to the shell rather than the file:\n{Screen(window.TerminalPanel.Active!)}");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The Terminal shown by a click on its stripe's button takes the keys from the file, as its action gives them,
    /// so what is typed next goes to its shell.</summary>
    private void The_terminal_shown_by_its_stripe_button_takes_the_keys_from_the_file()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            string notes = Path.Combine(folder, "notes.txt");
            File.WriteAllText(notes, "written\n");
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            window.OpenFile(notes);
            Ui.Settle();
            Assert.True(window.EditorArea.CodeEditor.IsKeyboardFocusWithin, "The file opened without the keys");

            Ui.Click(window.DockHost.GetVisualDescendants().OfType<ToggleButton>().First(b => Equals(b.Tag, ToolWindowIds.Terminal)));
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true } shell && shell.Screen.CursorColumn > 0, "the Terminal's shell to prompt");
            Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the Terminal its stripe's button showed");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private void The_terminal_window_runs_a_shell_that_takes_typed_commands_and_hides_when_it_exits()
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

    private void Every_tool_windows_shortcut_opens_it_while_the_terminal_has_the_keys()
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

    /// <summary>A file whose name holds a command, dropped on the terminal's own shell (PowerShell on Windows, sh elsewhere),
    /// is read by the shell as its name: its name is echoed as it is, and the command never runs.</summary>
    private void A_file_dropped_on_the_terminal_whose_name_holds_a_command_is_read_as_its_name_and_nothing_runs()
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
    private void What_Passenger_takes_out_of_its_environment_as_it_starts_reaches_no_shell_in_its_terminal()
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
            // Read again with neither given, so later tests in this process run as a start no agent made.
            Passenger.App.Mcp.McpProxy.CheckVariable();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The variables of a tmux pane or screen window Passenger was started in, held as a parent's are, in the
    /// process's own environment, reach no shell of its Terminal nor a program a run starts there, which would take itself
    /// for that pane (its tmux commands acting on the person's own session); what the run configuration gives one of them
    /// reaches its program.</summary>
    private void A_tmux_or_screen_Passenger_was_started_in_reaches_no_shell_or_run_in_its_terminal()
    {
        string folder = TestFolders.Create("terminal-outer-");
        Dictionary<string, string> outer = new()
        {
            ["TMUX"] = "/tmp/tmux-501/default,4242,0",
            ["TMUX_PANE"] = "%3",
            ["STY"] = "4242.ttys001.host",
            ["WINDOW"] = "0",
        };
        MainWindow? window = null;
        try
        {
            foreach ((string variable, string value) in outer)
            {
                OwnEnvironment.Set(variable, value);
            }
            string project = TestFolders.Write(folder, "Listed/Listed.csproj", TestProjects.Console());
            window = Ui.OpenFolder(folder, languageServer: false);
            TerminalSession shell = OpenTerminal(window, OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", []);
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
        }
        finally
        {
            // The Terminal's shell works in the folder until its window closes, which a test failing part-way leaves open.
            window?.Close();
            foreach (string variable in outer.Keys)
            {
                OwnEnvironment.Set(variable, null);
            }
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A terminal's pseudo-terminal is its own: neither the shell of a terminal opened after it nor a program the
    /// IDE starts after it holds it, so nothing run there can type a command into it. Each writes a command into every
    /// descriptor it holds that is a terminal (through bash, which takes a descriptor above 9 where dash takes none); a
    /// command typed into each terminal afterwards, which its shell reads after all written to it before, shows that
    /// neither ran.</summary>
    private void Nothing_run_in_one_terminal_or_started_by_the_IDE_can_type_into_another_terminal()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "What types into the descriptors it holds is bash, writing to Unix ones");
        string folder = TestFolders.Create("terminal-own-");
        MainWindow? window = null;
        try
        {
            (window, TerminalSession first) = Open(folder, "/bin/bash", "--norc", "--noprofile");
            window.Perform(AppActions.NewTerminalSession);
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true } active && active != first, "the second terminal's shell to start");
            TerminalSession second = window.TerminalPanel.Active!;
            Ui.WaitFor(() => window.TerminalPanel.View.IsFocused && second.Screen.CursorColumn > 0, "the second terminal's prompt");

            Run(window, TypingIntoHeld("typed-from-the-second-terminal"));
            long said = WaitForLine(second, line => line.StartsWith(HeldWord, StringComparison.Ordinal), "the second terminal's shell to type into what it holds");
            string heldBySecond = second.Screen.LineAt(said).Text().Trim();
            ChildResult started = Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(
                ChildProcesses.Command("/bin/bash", ["-c", TypingIntoHeld("typed-from-a-program-the-IDE-started")], folder), limit: TimeSpan.FromSeconds(30))),
                "a program the IDE starts to type into what it holds", TimeSpan.FromSeconds(40));
            Assert.True(started.Succeeded, $"The program the IDE started failed: {started.Output}{started.Error}");

            foreach ((TerminalSession shell, string which) in new[] { (first, "first"), (second, "second") })
            {
                Passenger.App.ToolWindows.TerminalTab tab = window.TerminalPanel.Tabs.Single(t => t.Session == shell);
                Ui.Click(window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.DataContext == tab));
                Ui.WaitFor(() => window.TerminalPanel.Active == shell && window.TerminalPanel.View.IsFocused, $"the keys to go to the {which} terminal");
                Run(window, $"touch sentinel-{which}");
                Ui.WaitFor(() => File.Exists(Path.Combine(folder, $"sentinel-{which}")), $"the {which} terminal to run what was typed into it");
            }
            Assert.False(File.Exists(Path.Combine(folder, "typed-from-the-second-terminal")),
                $"The second terminal's shell held the first's pseudo-terminal, and what it wrote there ran in the first ({heldBySecond}):\n{Screen(first)}");
            Assert.False(File.Exists(Path.Combine(folder, "typed-from-a-program-the-IDE-started")),
                $"A program the IDE started held a terminal's pseudo-terminal, and what it wrote there ran in that terminal ({started.Output.Trim()}):\n"
                + $"{Screen(first)}\n---\n{Screen(second)}");
        }
        finally
        {
            window?.Close();
            TestFolders.Delete(folder);
        }

        // Builtins alone, opening nothing: a terminal above 2 is the shell's own (an interactive bash's 255) or another's.
        static string TypingIntoHeld(string file) =>
            $"n=3; held=; while [ $n -lt 1024 ]; do if [ -t $n ]; then held=\"$held $n\"; printf 'touch {file}\\r' >&$n; fi; n=$((n+1)); done; "
            + $"echo; echo \"{HeldWord[..9]}\"\"{HeldWord[9..]}$held\"";
    }

    /// <summary>What a shell typing into the terminals it holds says before their descriptors: split in two where it is typed,
    /// so the typed line never reads as what it says, and on a line of its own, past what it wrote to its own terminal.</summary>
    private const string HeldWord = "terminals-held:";

    private void An_emoji_takes_the_two_columns_the_shell_counts_so_the_text_after_it_stays_in_place()
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

    /// <summary>Text an input method composes (kana here, as Chinese comes by Pinyin) is drawn at the cursor, underlined, two
    /// columns a character, the input method's window opening at its cursor in it; none of it reaches the program, nor a
    /// key the input method takes (Enter, a candidate's number on the keypad), until it is committed, and the program then
    /// reads the committed text alone, under the kitty keyboard protocol as an agent's prompt asks, in the columns the
    /// composition took; a composition left as the keys go elsewhere is dropped.</summary>
    private void Text_an_input_method_composes_shows_at_the_cursor_and_reaches_the_program_only_as_it_is_committed()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder);
            TerminalView view = window.TerminalPanel.View;
            TextInputMethodClientRequestedEventArgs asked = new() { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            view.RaiseEvent(asked);
            TextInputMethodClient client = asked.Client ?? throw new InvalidOperationException("The terminal gave the input method no client, so it composes nothing there");
            Assert.True(client.SupportsPreedit && client.TextViewVisual == view, "The terminal does not draw what the input method composes");

            RunCase(window, "typed", "1");
            WaitForScreen(window, shell, s => s.KeyboardFlags == TerminalKeys.Disambiguate && Lines(s).Contains("typed?"),
                "the program to wait for what is typed under the kitty keyboard protocol");
            TerminalScreen screen = shell.Screen;
            long line = screen.ScreenTop + screen.CursorRow;
            int column = screen.CursorColumn;
            Point CellAt(int at)
            {
                Point centre = view.PointOf(line, at);
                return new Point(centre.X - ((view.PointOf(line, at + 1).X - centre.X) / 2), centre.Y - ((view.PointOf(line + 1, at).Y - centre.Y) / 2));
            }
            void InputMethodAt(int at, string when)
            {
                Point expected = CellAt(at);
                Point told = client.CursorRectangle.TopLeft;
                Assert.True(Math.Abs(told.X - expected.X) < 0.01 && Math.Abs(told.Y - expected.Y) < 0.01,
                    $"{when}, the input method's window would open at {told}, not at column {at} of the cursor's row, {expected}");
            }
            InputMethodAt(column, "Before a composition");

            client.SetPreeditText("にほん");
            Ui.Settle();
            Color foreground = ((ISolidColorBrush)view.FindResource(window.ActualThemeVariant, TokenKeys.Terminal_Foreground)!).Color;
            double underline = view.PointOf(line + 1, 0).Y - view.PointOf(line, 0).Y;
            Color[] under = Ui.Pixels(window, [.. Enumerable.Range(column, 7).Select(at => view.TranslatePoint(view.PointOf(line, at) + new Point(0, (underline / 2) - 1.5), window)!.Value)]);
            Assert.True(under[..6].All(c => c == foreground) && under[6] != foreground,
                $"The composition is not drawn underlined over the six columns its three kana take from the cursor: [{string.Join(", ", under)}], the text {foreground}");
            InputMethodAt(column + 6, "Past three kana");

            foreach (PhysicalKey key in (PhysicalKey[])[PhysicalKey.Enter, PhysicalKey.NumPad1])
            {
                window.KeyPress(Key.ImeProcessed, RawInputModifiers.None, key, null);
                window.KeyRelease(Key.ImeProcessed, RawInputModifiers.None, key, null);
            }
            client.SetPreeditText("日本", 1);
            Ui.Settle();
            InputMethodAt(column + 2, "At the second of two Han characters");
            client.SetPreeditText(null);
            window.KeyTextInput("日本");
            Ui.Settle();
            InputMethodAt(column, "Once the composition was committed, before the program moved the cursor");
            client.SetPreeditText("ご");
            Ui.Settle();
            Ui.Click(window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible));
            Ui.Click(view, view.PointOf(line, column + 10));
            Assert.True(view.IsFocused && Ui.Pixel(window, view.TranslatePoint(view.PointOf(line, column) + new Point(0, (underline / 2) - 1.5), window)!.Value) != foreground,
                "A composition left as the keys went elsewhere is still drawn once they came back");
            window.KeyTextInput("q");
            WaitForLine(shell, "U+65E5 U+672C", "the committed text alone as the program read it");
            TerminalLine read = screen.LineAt(WaitForLine(shell, "日本|", "the committed text as the program wrote it back"));
            Assert.True(read.Cells[0].Width == 2 && read.Cells[2].Rune == '本' && read.Cells[4].Rune == '|',
                $"日本| took other columns than the composition did: [{string.Join("|", Enumerable.Range(0, 6).Select(read.TextOf))}]");
            WaitForScreen(window, shell, s => s.KeyboardFlags == 0, "the program's flags to go as it pops them");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private void A_long_line_reads_whole_after_the_terminal_narrows_and_widens_again_and_the_prompt_keeps_its_cursor()
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

    private void Scrolled_back_the_terminal_holds_its_place_as_output_arrives()
    {
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = Open(folder);
            TerminalView view = window.TerminalPanel.View;
            RunCase(window, "lines", "1", "300");
            WaitForLine(shell, "300", "the program's lines");
            WaitForPromptBelow(window, shell, "300");
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

    private void A_selection_stays_on_its_text_as_output_streams_past_the_scrollbacks_limit()
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
            WaitForPromptBelow(window, shell, text);
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

    /// <summary>A log longer than the scrollback holds costs the window's thread nothing for each line past it: the line a
    /// full scrollback lets go is the next row. 30,000 lines allocate on the UI thread under 64 bytes a line more than
    /// 12,000, where a new row's cells alone take 16 bytes a column; the terminal is hidden meanwhile, so no frame counts.</summary>
    private void A_log_three_times_the_scrollback_costs_the_window_no_more_for_each_line_than_one_just_past_it()
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
    private void A_log_in_colour_streaming_up_the_screen_costs_a_frame_what_one_changed_row_does()
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
    private void A_program_run_in_the_terminal_reads_a_key_and_a_line_typed_there_and_draws_in_colour_where_it_puts_the_cursor()
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
        Assert.Equal("Keys ended with exit code 3", window.Status);
        Assert.False(window.FindControl<Button>("StopButton")!.IsEnabled, "The toolbar's Stop could still be clicked once the program ended");
        Assert.True(window.TerminalPanel.Tabs is [{ Name: "Keys", HasEnded: true }], "The ended program's tab was not kept to say how it ended");
        window.Close();
    }

    /// <summary>A program needing a terminal that is run in the Run window has the Run window say so, read from the exception
    /// .NET writes there (Console.ReadKey refusing the window's pipe, on Windows Console.Clear before it), with Run in the
    /// Terminal, which has its configuration use the terminal, kept with the folder, and runs it again in a tab of the
    /// Terminal window, where it reads a key typed there.</summary>
    private void A_program_needing_a_terminal_in_the_Run_window_is_offered_one_and_reads_a_key_there()
    {
        string folder = TestFolders.Create("terminal-need-");
        string settings = TestFolders.Create("settings-");
        try
        {
            string project = PathNames.Canonical(Debugging.Helpers.Debuggees.CopyTo("TerminalConsoleApp", folder));
            Restore.Run(folder);
            MainWindow window = Keeping(folder, settings);
            try
            {
                window.SetConfigurations([new RunConfiguration { Name = "Keys", Project = project }], "Keys");
                Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(3));
                Assert.Null(window.RunInTerminal);
                OutputView console = window.BottomPanel.Run;
                Ui.WaitFor(() => window.RunEnd.IsCompleted, () => $"the program to end in the Run window; it shows:\n{console.Document.Snapshot}");
                string call = OperatingSystem.IsWindows() ? "Console.Clear" : "Console.ReadKey";
                Assert.True(console.Offered == $"Keys needs a terminal for {call}: the Run window gives it pipes",
                    $"The Run window does not offer a terminal, saying {console.Offered ?? "nothing"}, where it shows:\n{console.Document.Snapshot}");

                Ui.ClickButton(console, "Run in the Terminal");
                Ui.WaitFor(() => window.RunInTerminal is { IsRunning: true }, () => $"the program to run in the terminal: {Ui.Said(window)}", TimeSpan.FromMinutes(2));
                TerminalSession program = window.RunInTerminal!.Session;
                Assert.Equal("Keys", window.TerminalPanel.ActiveTab?.Name);
                WaitForScreen(window, program, s => s.Lines.Any(l => l.Text() == "key?"), "the program to ask for a key in the terminal");
                Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the program");
                window.KeyTextInput("q");
                WaitForLine(program, line => line is "key? key q" or "key? qkey q", "the key as the program read it");
                Assert.Equal(ConsoleKind.Terminal, window.Configuration?.Console);
                Ui.Run(window, AppActions.Stop);
                Ui.WaitFor(() => window.RunInTerminal is { IsRunning: false }, "the program to end once stopped");
            }
            finally
            {
                window.Close();
            }
            MainWindow again = Keeping(folder, settings);
            try
            {
                Assert.True(again.Configuration is { Name: "Keys", Console: ConsoleKind.Terminal }, "Run in the Terminal was not kept with the configuration once the folder opened again");
            }
            finally
            {
                again.Close();
            }
        }
        finally
        {
            TestFolders.Delete(folder);
            TestFolders.Delete(settings);
        }
    }

    /// <summary>A window keeping its state in <paramref name="settings"/> open on a folder, without the C# language server,
    /// which a run asks nothing of.</summary>
    private static MainWindow Keeping(string folder, string settings)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>Stop ends a program running in the terminal and the program it started, and its tab says it was ended.</summary>
    private void Stop_ends_a_program_running_in_the_terminal_and_what_it_started()
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
        WaitForLine(run.Session, l => OperatingSystem.IsWindows() ? l.StartsWith("Process finished with exit code ", StringComparison.Ordinal) : l == "Process finished with exit code 137 (SIGKILL).",
            "its tab saying it was ended");
        Assert.False(window.IsRunning || window.FindControl<Button>("StopButton")!.IsEnabled, "The window says the stopped program still runs");
        window.Close();
    }

    /// <summary>A variable a run in the terminal takes away never reaches its program, though Passenger's environment holds
    /// it as a parent's variables are held, in the process's own: a git hook's, which every run is started without, and one
    /// the run configuration gives no value. What the configuration sets reaches it.</summary>
    private void A_variable_a_run_in_the_terminal_takes_away_never_reaches_its_program()
    {
        string folder = TestFolders.Create("terminal-unset-");
        // GIT_PREFIX: of a git hook's variables, the one git itself never reads, should anything else started meanwhile get it.
        string[] takenAway = [GitVariables.Prefix, "PASSENGER_RUN_TAKEN_AWAY"];
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

    /// <summary>A program run in the terminal that writes a line and ends at once, as echo does within a millisecond of its
    /// start, has that line on its tab above how it ended, every time: 300 runs, each in a tab of its own.</summary>
    private void A_program_that_writes_a_line_and_ends_at_once_has_it_on_its_tab_every_run()
    {
        const int Runs = 300;
        const string Said = "said-at-once";
        string folder = TestFolders.Create("terminal-at-once-");
        try
        {
            string project = TestFolders.Write(folder, "Says/Says.csproj", TestProjects.Console());
            TestFolders.Write(folder, "Says/Program.cs", "return 0;\n");
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            window.SetConfigurations([new RunConfiguration
            {
                Name = "Echo",
                Project = PathNames.Canonical(project),
                Program = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/echo",
                Arguments = OperatingSystem.IsWindows() ? $"/c echo {Said}" : Said,
                WorkingDirectory = folder,
                BuildBeforeLaunch = false,
                Console = ConsoleKind.Terminal,
            }], "Echo");
            List<int> lost = [];
            string? firstLost = null;
            for (int run = 0; run < Runs; run++)
            {
                Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(1));
                Assert.True(window.RunInTerminal is not null, $"Run {run} did not run in the terminal: {Ui.Said(window)}");
                TerminalRun started = window.RunInTerminal;
                Assert.Equal(0, Ui.Await(started.Ended, "the program's end"));
                if (!Lines(started.Session.Screen).Contains(Said))
                {
                    lost.Add(run);
                    firstLost ??= Screen(started.Session);
                }
            }
            Assert.True(lost.Count == 0, $"{lost.Count} of {Runs} runs lost the line the program wrote (runs {string.Join(", ", lost)}); "
                + $"the first's tab shows only:\n{firstLost}");
            window.Close();
        }
        finally
        {
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
    private void An_address_in_the_terminal_opens_with_a_click_or_cmd_click_and_a_files_path_with_cmd_click(bool copyOnSelection)
    {
        string folder = TestFolders.Create("terminal-");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "first\nsecond\nthird\n");
        (MainWindow window, TerminalSession shell) = Open(folder);
        window.ApplySettings(window.Settings with { TerminalCopyOnSelection = copyOnSelection });
        Copy(window, "clipboard kept");
        TerminalView view = window.TerminalPanel.View;
        RawInputModifiers command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        Run(window, "echo see https://example.com/terminal/docs. now");
        long line = WaitForLine(shell, "see https://example.com/terminal/docs. now", "the address");
        // A full screen scrolls as the shell's prompt comes: the pointer goes to a line once the prompt is below it.
        WaitForPromptBelow(window, shell, "see https://example.com/terminal/docs. now");
        Point at = view.TranslatePoint(view.PointOf(line, 10), window)!.Value;
        window.MouseMove(at);
        Ui.Settle();
        Assert.Equal((line, 4, line, 36, false), view.HoveredLink is { } link ? (link.FromLine, link.FromColumn, link.ToLine, link.ToColumn, link.Hidden) : default);

        Drag(window, line, 12, line, 22);
        Assert.Equal("example.com", view.SelectedText);
        Assert.Equal(copyOnSelection ? "example.com" : "clipboard kept", Ui.Clipboard(window));
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
        WaitForPromptBelow(window, shell, longer[..shell.Screen.Columns]);
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
        WaitForPromptBelow(window, shell, "written to notes.txt:2 just now");
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
        Assert.Equal(copyOnSelection ? "example.com" : "clipboard kept", Ui.Clipboard(window));
        window.TerminalPanel.FocusView();
        Ui.Settle();
        Run(window, "echo https://example.com/double-click");
        long doubleClick = WaitForLine(shell, "https://example.com/double-click", "the address to double-click");
        WaitForPromptBelow(window, shell, "https://example.com/double-click");
        Ui.DoubleClick(view, view.PointOf(doubleClick, 13));
        Assert.Equal(copyOnSelection ? "example.com" : "clipboard kept", Ui.Clipboard(window));
        window.Close();
    }

    /// <summary>Words a program links (OSC 8, as Spectre.Console's link markup writes them) are a link a click opens: each
    /// run of words linked to a web address, its link ended by ST or by BEL, an id among its parameters passed over; words
    /// linked to an address a click opens nothing for, and the words between, stay text.</summary>
    private void Words_a_program_links_to_a_web_address_are_a_link_a_click_opens()
    {
        (MainWindow window, TerminalSession shell) = Open(TestFolders.Create("terminal-"));
        TerminalView view = window.TerminalPanel.View;
        const string Written = "see the docs or this not mail end";
        RunCase(window, "links");
        long line = WaitForLine(shell, Written, "the program's linked words");
        WaitForPromptBelow(window, shell, Written);
        TerminalLink? Over(int column)
        {
            window.MouseMove(view.TranslatePoint(view.PointOf(line, column), window)!.Value);
            Ui.Settle();
            return view.HoveredLink;
        }
        void Click(int column)
        {
            Point at = view.TranslatePoint(view.PointOf(line, column), window)!.Value;
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Ui.Settle();
        }

        Assert.Equal((line, 4, line, 11, false), Over(6) is { } docs ? (docs.FromLine, docs.FromColumn, docs.ToLine, docs.ToColumn, docs.Hidden) : default);
        Click(6);
        Assert.Equal(["https://example.com/spectre"], window.Browsed.Select(u => u.AbsoluteUri));
        Assert.Equal((line, 16, line, 19), Over(17) is { } other ? (other.FromLine, other.FromColumn, other.ToLine, other.ToColumn) : default);
        Click(17);
        Assert.Equal("https://example.com/other", window.Browsed[^1].AbsoluteUri);
        Assert.True(Over(26) is null, "Words linked to a mail address are a link, which a click would hand the system to open");
        Assert.True(Over(13) is null, "The words between two links are a link");
        Click(13);
        Assert.Equal(2, window.Browsed.Count);
        window.Close();
    }

    /// <summary>Words a program links to a file address are a link only for a path on this machine, which a click opens
    /// as the disk has it then: another host's file, a share's path by four slashes or by escaped backslashes are text, so
    /// drawing them reaches no other machine; a file here opens in the editor, and one that is not there is said in the
    /// status bar, nothing opened.</summary>
    private void Words_a_program_links_to_a_file_are_a_link_only_on_this_machine_and_a_click_asks_the_disk()
    {
        string folder = TestFolders.Create("terminal-");
        string notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(notes, "first\nsecond\n");
        string missing = Path.Combine(folder, "lost.txt");
        (MainWindow window, TerminalSession shell) = Open(folder);
        TerminalView view = window.TerminalPanel.View;
        const string Written = "far host unc shape esc path here notes gone lost";
        RunCase(window, "file-links", notes, missing);
        long line = WaitForLine(shell, Written, "the program's linked words");
        WaitForPromptBelow(window, shell, Written);
        TerminalLink? Over(int column)
        {
            window.MouseMove(view.TranslatePoint(view.PointOf(line, column), window)!.Value);
            Ui.Settle();
            return view.HoveredLink;
        }
        void Click(int column)
        {
            Point at = view.TranslatePoint(view.PointOf(line, column), window)!.Value;
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Ui.Settle();
        }

        Assert.True(Over(5) is null, "Words linked to another host's file are a link, so drawing them asks that host for it");
        Assert.True(Over(14) is null, "Words linked to a share's path by four slashes are a link, so drawing them asks the share for it");
        Assert.True(Over(24) is null, "Words linked to a share's path by escaped backslashes are a link, so drawing them asks the share for it");
        Assert.Equal((line, 33, line, 37), Over(34) is { } here ? (here.FromLine, here.FromColumn, here.ToLine, here.ToColumn) : default);
        Assert.Equal((line, 44, line, 47), Over(45) is { } gone ? (gone.FromLine, gone.FromColumn, gone.ToLine, gone.ToColumn) : default);
        Click(45);
        Assert.Equal($"The program's link names {missing}, which is not there", window.Status);
        Assert.Null(window.EditorArea.Active);
        Click(34);
        Assert.Equal(PathNames.Canonical(notes), window.EditorArea.Active?.Path);
        window.Close();
    }

    /// <summary>What a program's link opens is told from its address alone, the disk asked nothing, so a link to a file
    /// that is not there is one as much as a link to one that is; an address of another host, of a share or a device, or
    /// of a scheme a click opens nothing for is none.</summary>
    private void What_a_programs_link_opens_is_told_from_its_address_alone()
    {
        string missing = Path.Combine(TestRun.Root, "no-such-folder", "no-such-file.txt");
        Assert.Equal(new ProgramFileLink(missing), MainWindow.ProgramLink(new Uri(missing).AbsoluteUri));
        Assert.Equal(new WebLink(new Uri("https://example.com/a")), MainWindow.ProgramLink("https://example.com/a"));
        string here = OperatingSystem.IsWindows() ? @"C:\work\x.txt" : "/work/x.txt";
        Assert.Equal(new ProgramFileLink(here), MainWindow.ProgramLink(OperatingSystem.IsWindows() ? "file://localhost/C:/work/x.txt" : "file://localhost/work/x.txt"));
        foreach (string none in (string[])["file://otherhost/share/x", "file:////otherhost/share/y", "file:///%5C%5Cotherhost%5Cshare%5Cz", "file:///%2F%2Fotherhost/z",
            "file://127.0.0.2/x", "file:///%5C%5C%3F%5CC:%5Cx", "file:///%5C%5C.%5Cpipe%5Cx", "file:///net/otherhost/x", "mailto:someone@example.com", "x-apple.systempreferences:", "not an address"])
        {
            Assert.True(MainWindow.ProgramLink(none) is null, $"{none} is a link, which drawing it or a click on it would take to another host, a share or a device");
        }
    }

    /// <summary>What TerminalCases' found-paths case writes: paths naming another machine as program output writes them,
    /// then a frame in a file of the folder's.</summary>
    private static readonly string[] FoundPathLines =
    [
        @"   at Shop.Main() in \\otherhost\share\Program.cs:line 5",
        "   at Shop.Main() in /_///otherhost/share/Program.cs:line 6",
        "//otherhost/share/Program.cs(3,1): error CS1002: ; expected",
        "/net/otherhost/share/Program.cs(4,1): error CS1002: ; expected",
        @"/net/otherhost/share/Program.cs:12 and \\?\C:\share\Device.cs:4",
        "   at: void Main._Process(double) (res:////otherhost/share/Main.cs:19)",
        "see file://otherhost/share/x.cs or file:////otherhost/share/y.cs",
        "   at Shop.Main() in src/Program.cs:line 2",
    ];

    /// <summary>A path naming another machine in a program's output (a share's by either slash, a device's, one under an
    /// automount root), as a stack frame, a compiler's message, a Godot error's place or a file address writes it, is no
    /// link and never asked of the disk as the terminal or the Run window shows it, so showing a line reaches no other
    /// machine and mounts nothing; a frame in a file of the folder's still links it once the disk, asked off the window's
    /// thread, has answered, and a click opens it at its line.</summary>
    private void A_path_naming_another_machine_in_the_terminal_or_a_console_is_never_asked_of_the_disk()
    {
        string folder = TestFolders.Create("terminal-");
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        string program = Path.Combine(folder, "src", "Program.cs");
        File.WriteAllText(program, "first\nsecond\n");
        (MainWindow window, TerminalSession shell) = Open(folder);
        TerminalView view = window.TerminalPanel.View;
        RunCase(window, "found-paths");
        long line = WaitForLine(shell, FoundPathLines[^1], "the frame in the folder's file");
        WaitForPromptBelow(window, shell, FoundPathLines[^1]);
        Ui.Settle();
        Held(window.TerminalLinks!, "the terminal");

        Point at = view.TranslatePoint(view.PointOf(line, FoundPathLines[^1].IndexOf("src/", StringComparison.Ordinal) + 2), window)!.Value;
        window.MouseMove(at);
        Ui.Settle();
        Assert.True(view.HoveredLink is { Hidden: false }, "The pointer rests on the frame in the folder's file, but it is no link");
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
        Assert.Equal(PathNames.Canonical(program), window.EditorArea.Active?.Path);
        Assert.Equal(1, window.EditorArea.CodeEditor.CaretPosition.Line);

        Ui.Run(window, AppActions.ShowRun);
        OutputView run = window.BottomPanel.Run;
        run.Append(string.Concat(FoundPathLines.Select(said => said + "\n")));
        Ui.Settle();
        Held(run.Links, "the Run window");
        Assert.True(window.Perform(AppActions.CloseAllTabs));
        Ui.Settle();
        Assert.Null(window.EditorArea.Active);
        Ui.ClickLink(run, FoundPathLines[^1]);
        Assert.Equal(PathNames.Canonical(program), window.EditorArea.Active?.Path);

        string other = Path.Combine(folder, "src", "Other.cs");
        File.WriteAllText(other, "first\n");
        const string Later = "   at Shop.Later() in src/Other.cs:line 1";
        int resolved = PathNames.ResolvedOnThisThread;
        Assert.Empty(run.Links.In(Later));
        Assert.True(resolved == PathNames.ResolvedOnThisThread, "Reading a line's links asked the disk on the window's thread, which a slow disk would hold");
        Assert.Contains(Ui.LinksIn(run.Links, Later), l => l.Target is FileLink { Path: var linked } && PathNames.Same(linked, PathNames.Canonical(other)));
        window.Close();

        void Held(ConsoleLinks links, string where)
        {
            foreach (string said in FoundPathLines[..^1])
            {
                Assert.True(Ui.LinksIn(links, said) is not { Length: > 0 } found || found.All(l => l.Target is WebLink),
                    $"{where} links {said}, which names another machine");
            }
            Assert.Contains(Ui.LinksIn(links, FoundPathLines[^1]), l => l.Target is FileLink { Path: var linked, Position.Line: 1 } && PathNames.Same(linked, PathNames.Canonical(program)));
            string[] reached = [.. links.Asked.Where(p => p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)
                || p.StartsWith("/net/", StringComparison.OrdinalIgnoreCase))];
            Assert.True(reached.Length == 0, $"Showing {where}'s lines asked the disk about {string.Join(", ", reached)}, which reaches another machine or mounts it");
            Assert.Contains(links.Asked, p => PathNames.Same(PathNames.Canonical(p), PathNames.Canonical(program)));
        }
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
    internal static void WaitForScreen(MainWindow window, TerminalSession shell, Func<TerminalScreen, bool> shows, string what, TimeSpan? limit = null)
    {
        try
        {
            Ui.WaitFor(() => shows(shell.Screen), what, limit);
        }
        catch (TimeoutException waited)
        {
            TerminalScreen screen = shell.Screen;
            throw new TimeoutException($"{waited.Message}. The terminal, {screen.Columns} by {screen.Rows} with its cursor at row {screen.CursorRow} "
                + $"column {screen.CursorColumn}, {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"} having the keys, shows:\n{Screen(shell)}", waited);
        }
    }

    /// <summary>Waits for the shell's prompt on a row below a program's last line, so the program has ended and its terminal
    /// modes are the shell's again: a line typed sooner comes while the program still puts them back, unechoed and its
    /// Enter a bare carriage return, or echoed ahead of the prompt, which comes with the line again a row lower once the
    /// program has gone.</summary>
    private static void WaitForPromptBelow(MainWindow window, TerminalSession shell, string last)
    {
        WaitForScreen(window, shell, PromptBelow, $"the shell's prompt below the program's last line, {last}");

        bool PromptBelow(TerminalScreen screen)
        {
            if (screen.CursorColumn == 0)
            {
                return false;
            }
            for (long line = screen.ScreenTop + screen.CursorRow - 1; line >= screen.FirstLine; line--)
            {
                if (screen.LineAt(line).Text() == last)
                {
                    return true;
                }
            }
            return false;
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

    private static Point CursorCentre(MainWindow window)
    {
        TerminalView view = window.TerminalPanel.View;
        TerminalScreen screen = view.Session!.Screen;
        return view.TranslatePoint(view.PointOf(screen.ScreenTop + screen.CursorRow, screen.CursorColumn), window)!.Value;
    }

    /// <summary>A drag with the mouse from one cell to another, as a person chooses text.</summary>
    private static void Drag(MainWindow window, long fromLine, int fromColumn, long toLine, int toColumn)
    {
        TerminalView view = window.TerminalPanel.View;
        Point from = view.TranslatePoint(view.PointOf(fromLine, fromColumn), window)!.Value;
        Point to = view.TranslatePoint(view.PointOf(toLine, toColumn), window)!.Value;
        Ui.Drag(window, from, to);
    }

    /// <summary>The window over a folder, its Terminal open on the system's plain shell (cmd.exe on Windows, sh elsewhere).</summary>
    private static (MainWindow Window, TerminalSession Shell) Open(string folder) => Open(folder, OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");

    /// <summary>The window over a folder, its Terminal open on a shell of the test's and the keys going to it.</summary>
    internal static (MainWindow Window, TerminalSession Shell) Open(string folder, string shell, params string[] arguments) =>
        Open(folder, shell, arguments, answered: null);

    /// <summary>The window over a folder, its Terminal open on a shell of the test's and the keys going to it, each answer
    /// its screen gives the shell (a cursor position report) added to <paramref name="answered"/> from the shell's start.</summary>
    internal static (MainWindow Window, TerminalSession Shell) Open(string folder, string shell, string[] arguments, List<string>? answered)
    {
        MainWindow window = Ui.OpenFolder(folder);
        return (window, OpenTerminal(window, shell, arguments, answered));
    }

    /// <summary>The window's Terminal open on a shell of the test's, the keys going to it, its screen's answers to the shell
    /// added to <paramref name="answered"/> where given, heard from the session's first moment.</summary>
    private static TerminalSession OpenTerminal(MainWindow window, string shell, string[] arguments, List<string>? answered = null)
    {
        window.TerminalPanel.Shell = (shell, arguments);
        Ui.Run(window, AppActions.ShowTerminal, settle: answered is null);
        if (answered is not null)
        {
            Ui.PumpUntil(() => window.TerminalPanel.Active is not null, "the terminal's session");
            window.TerminalPanel.Active!.Screen.Reply += answered.Add;
        }
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
    /// <summary>A Unix pseudo-terminal never holds the window: a flood of output leaves it taking keys and Ctrl+C stops
    /// it, a paste a program reads none of neither holds the window nor its end, and the shell echoes what is typed
    /// while every thread of the pool is busy.</summary>
    [AvaloniaFact]
    public void The_terminal_never_holds_the_window_whatever_its_programs_write_or_leave_unread()
    {
        Flow.StepApart<TerminalResponsivenessTests>("a flood of output leaves the window taking keys and Ctrl C stops it", t => t.A_flood_of_output_leaves_the_window_taking_keys_and_Ctrl_C_stops_it());
        Flow.StepApart<TerminalResponsivenessTests>("a paste into a program that reads nothing leaves the window taking keys", t => t.A_paste_into_a_program_that_reads_nothing_leaves_the_window_taking_keys());
        Flow.StepApart<TerminalResponsivenessTests>("a terminal whose program read none of a paste ends without holding the window", t => t.A_terminal_whose_program_read_none_of_a_paste_ends_without_holding_the_window());
        Flow.StepApart<TerminalResponsivenessTests>("the shell echoes what is typed while every thread of the pool is busy", t => t.The_shell_echoes_what_is_typed_while_every_thread_of_the_pool_is_busy());
    }

    private void A_flood_of_output_leaves_the_window_taking_keys_and_Ctrl_C_stops_it()
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

    private void A_paste_into_a_program_that_reads_nothing_leaves_the_window_taking_keys()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "A Unix pseudo-terminal's input queue holds a kilobyte or so, past which a write waits for the program; ConPTY's has no such limit to hold");
        string folder = TestFolders.Create("terminal-");
        try
        {
            (MainWindow window, TerminalSession shell) = TerminalTests.Open(folder, "/bin/sh");
            // A loop that reads none of its input until the test lets it end.
            string go = Path.Combine(folder, "go");
            RunReadingNone(window, shell, $"while [ ! -e '{go}' ]; do sleep 0.1; done");

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
            Ui.Await(giveUp, "the paste's give-up to stand down");
            // What the pseudo-terminal took it echoes, so the paste did go: seen before the loop ends, whose shell then overruns the scrollback.
            TerminalTests.WaitForScreen(window, shell, _ => PasteEchoed(shell), "the start of the paste echoed", TimeSpan.FromSeconds(10));
            File.WriteAllText(go, string.Empty);

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
    private void A_terminal_whose_program_read_none_of_a_paste_ends_without_holding_the_window()
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
            RunReadingNone(window, shell, $"while [ ! -e '{go}' ]; do sleep 0.1; done; yes tick");
            TerminalTests.Copy(window, string.Concat(Enumerable.Range(0, 2000).Select(i => $"echo pasted {i}\n")));
            Ui.Press(window, TerminalTests.PasteKey);
            TerminalTests.WaitForScreen(window, shell, _ => PasteEchoed(shell), "the start of the paste echoed", TimeSpan.FromSeconds(10));
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

    /// <summary>Types a command line that reads none of its input and waits for the shell's word that it runs, written only
    /// once its line editor has given the terminal back with echo on: the editor writes the line's end before that, and a
    /// paste then lands unechoed, held by the pseudo-terminal until a program reads it.</summary>
    private static void RunReadingNone(MainWindow window, TerminalSession shell, string command)
    {
        window.KeyTextInput($"echo started-$((6*7)); {command}");
        Ui.Press(window, "ENTER");
        TerminalTests.WaitForScreen(window, shell, _ => TerminalTests.Screen(shell).Contains("started-42", StringComparison.Ordinal), "the loop to start");
    }

    private static bool PasteEchoed(TerminalSession shell) => TerminalTests.Screen(shell).Contains("echo pasted 0", StringComparison.Ordinal);

    private void The_shell_echoes_what_is_typed_while_every_thread_of_the_pool_is_busy()
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
