using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Navigation;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A list's keys on a row a click gave them, as a person presses them: the row takes Enter and Space for its own
/// choice before the list hears them, so each list hears its keys ahead of its rows (ListKeys).</summary>
public sealed class ListKeysTests : IDisposable
{
    private readonly string folder = TestFolders.Create("list-keys-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The keys stay where the person works and act on the row chosen: Enter, F4, Space and Delete on a
    /// bookmark's, a commit's and a breakpoint's row, a popup from an Explorer row giving it the keys back, each tool
    /// window shown with the keys inside, Escape giving the editor the keys, the menu key opening a row's menu under
    /// it, and the status bar's last message and the terminal's last command opening as text.</summary>
    [AvaloniaFact]
    public void The_keys_stay_where_the_person_is_working_and_a_lists_keys_act_on_its_row()
    {
        Flow.StepApart<ListKeysTests>("enter and F4 on a bookmark's row go to its line and Delete takes it out", t => t.Enter_and_F4_on_a_bookmarks_row_go_to_its_line_and_Delete_takes_it_out());
        Flow.StepApart<ListKeysTests>("space on a commit row flips its box and F4 opens its file", t => t.Space_on_a_commit_row_flips_its_box_and_F4_opens_its_file());
        Flow.StepApart<ListKeysTests>("the Breakpoints window opens with the keys on its chosen breakpoint and Delete takes each out in turn", t => t.The_Breakpoints_window_opens_with_the_keys_on_its_chosen_breakpoint_and_Delete_takes_each_out_in_turn());
        Flow.StepApart<KeysPlaceTests>("a popup opened from an Explorer row gives the row the keys back as it closes", t => t.A_popup_opened_from_an_Explorer_row_gives_the_row_the_keys_back_as_it_closes());
        Flow.StepApart<KeysPlaceTests>("each tool window's keys show it with the keys inside it", t => t.Each_tool_windows_keys_show_it_with_the_keys_inside_it());
        Flow.StepApart<KeysPlaceTests>("escape in a tool window gives the editor the keys with the window still shown", t => t.Escape_in_a_tool_window_gives_the_editor_the_keys_with_the_window_still_shown());
        Flow.StepApart<KeysPlaceTests>("the menu key opens a row's menu under the chosen row wherever the pointer is", t => t.The_menu_key_opens_a_rows_menu_under_the_chosen_row_wherever_the_pointer_is());
        Flow.StepApart<ReadAsTextTests>("the status bar's last message opens as text in every keymap and Escape gives its bar the keys back", t => t.The_status_bars_last_message_opens_as_text_in_every_keymap_and_Escape_gives_its_bar_the_keys_back());
        Flow.StepApart<ReadAsTextTests>("the terminal's last command and its output open as text and Escape gives the terminal its keys back", t => t.The_terminals_last_command_and_its_output_open_as_text_and_Escape_gives_the_terminal_its_keys_back());
    }

    /// <summary>In the Bookmarks window, Enter on a bookmark's row goes to its line as a double click does, F4 too, and
    /// Delete takes the bookmark out.</summary>
    private void Enter_and_F4_on_a_bookmarks_row_go_to_its_line_and_Delete_takes_it_out()
    {
        TestFolders.Write(folder, "Alpha.txt", "first\nsecond\nthird\n");
        TestFolders.Write(folder, "Beta.txt", "one\ntwo\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile("Alpha.txt");
            Ui.Settle();
            window.EditorArea.CodeEditor.GoTo(new Passenger.Text.TextPosition(2, 0));
            Ui.Run(window, AppActions.ToggleBookmark);
            window.OpenFile("Beta.txt");
            Ui.Settle();
            Ui.Run(window, AppActions.ToggleBookmark);
            Assert.Equal(2, window.Bookmarks.All.Count);
            Bookmark alpha = window.Bookmarks.All.Single(b => b.Name == "Alpha.txt");
            Bookmark beta = window.Bookmarks.All.Single(b => b.Name == "Beta.txt");

            ListBox list = Bookmarks(window);
            Ui.Click(Ui.Row(list, alpha));
            Ui.Press(window, "ENTER");
            Assert.True(window.EditorArea.Active?.Name == "Alpha.txt" && window.EditorArea.CodeEditor.CaretPosition.Line == 2,
                $"Enter on Alpha.txt's bookmark left {window.EditorArea.Active?.Name} open at line {window.EditorArea.CodeEditor.CaretPosition.Line + 1}, not Alpha.txt at line 3");

            list = Bookmarks(window);
            Ui.Click(Ui.Row(list, beta));
            Ui.Press(window, "F4");
            Assert.True(window.EditorArea.Active?.Name == "Beta.txt", $"F4 on Beta.txt's bookmark left {window.EditorArea.Active?.Name} open");

            list = Bookmarks(window);
            Ui.Click(Ui.Row(list, alpha));
            Ui.Press(window, "DELETE");
            Assert.True(window.Bookmarks.All.SequenceEqual([beta]), $"Delete on Alpha.txt's bookmark left [{string.Join(", ", window.Bookmarks.All.Select(b => b.Name))}]");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>In the Commit window, Space on a changed file's row flips its box, and F4 opens the file (Enter's diff is
    /// DiffTabsTests').</summary>
    private void Space_on_a_commit_row_flips_its_box_and_F4_opens_its_file()
    {
        TestFolders.Write(folder, "Notes.txt", "notes\n");
        TestFolders.Write(folder, "Other.txt", "other\n");
        TestGit.Init(folder);
        TestFolders.Write(folder, "Notes.txt", "notes, edited\n");
        TestFolders.Write(folder, "Other.txt", "other, edited\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.Perform(AppActions.ShowCommit);
            Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Count() == 2, "the changed files");
            CommitRow notes = window.CommitView.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Notes.txt");
            ListBox list = window.CommitView.GetVisualDescendants().OfType<ListBox>().First(l => l.Items.Contains(notes));
            bool was = notes.IsChosen;
            Ui.Click(Ui.Row(list, notes));
            Ui.Press(window, "SPACE");
            Assert.True(notes.IsChosen != was, $"Space on Notes.txt's row left its box {(was ? "ticked" : "clear")}");
            Ui.Press(window, "SPACE");
            Assert.True(notes.IsChosen == was, "Space again on Notes.txt's row did not flip its box back");

            Ui.Press(window, "F4");
            Assert.True(window.EditorArea.Active is { Diff: null } open && PathNames.Same(open.Path, Path.Combine(PathNames.Canonical(folder), "Notes.txt")),
                $"F4 on Notes.txt's row left {window.EditorArea.Active?.Path} ({(window.EditorArea.Active?.Diff is null ? "a file" : "a diff")}) in front, not the file");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The Breakpoints window opens with the keys on its chosen breakpoint's row; an exception breakpoint added by
    /// its type's name takes them on its own row, and Delete takes each out in turn, the keys going on to the next.</summary>
    private void The_Breakpoints_window_opens_with_the_keys_on_its_chosen_breakpoint_and_Delete_takes_each_out_in_turn()
    {
        TestFolders.Write(folder, "Program.cs", "int one = 1;\nint two = 2;\nSystem.Console.WriteLine(one + two);\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile("Program.cs");
            Ui.Settle();
            string path = window.EditorArea.Active!.Path;
            foreach (int line in (int[])[0, 1])
            {
                window.EditorArea.CodeEditor.GoTo(new Passenger.Text.TextPosition(line, 0));
                Ui.Run(window, AppActions.ToggleBreakpoint);
            }
            Assert.Equal(2, window.Debugger.Breakpoints.In(path).Count);
            LineBreakpoint first = window.Debugger.Breakpoints.In(path)[0];

            Ui.Run(window, AppActions.ViewBreakpoints);
            BreakpointsWindow breakpoints = Assert.IsType<BreakpointsWindow>(window.Popup);
            Ui.Settle();
            Assert.True(breakpoints.Chosen == first && KeyedRow(breakpoints) == first,
                $"The Breakpoints window opened with {breakpoints.Chosen?.Location ?? "nothing"} chosen and the keys on {Keys(breakpoints)}, not on {first.Location}'s row");

            Ui.Click(breakpoints.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Add Breakpoint"));
            Ui.Choose(breakpoints.AddMenu, BreakpointsWindow.AddExceptions);
            Assert.True(TopLevel.GetTopLevel(breakpoints)?.FocusManager?.GetFocusedElement() is TextBox, $"Add CLR Exception Breakpoints opened with the keys on {Keys(breakpoints)}, not in its search");
            window.KeyTextInput("Passenger.NoSuchException");
            Ui.Press(window, "ENTER");
            ExceptionBreakpoint added = Assert.IsType<ExceptionBreakpoint>(breakpoints.Chosen);
            Assert.True(added.Type == "Passenger.NoSuchException" && KeyedRow(breakpoints) == added,
                $"Enter on a type's name added {added.Type} with the keys on {Keys(breakpoints)}, not on Passenger.NoSuchException's row");

            Ui.Press(window, "DELETE");
            Assert.True(!breakpoints.Listed.Contains(added) && KeyedRow(breakpoints) == first,
                $"Delete on Passenger.NoSuchException's row left [{string.Join(", ", breakpoints.Listed.Select(b => b.Location))}], the keys on {Keys(breakpoints)}, not on {first.Location}'s row");
            Ui.Press(window, "DELETE");
            Ui.Press(window, "DELETE");
            Assert.True(window.Debugger.Breakpoints.In(path).Count == 0, $"Delete twice more left {window.Debugger.Breakpoints.In(path).Count} of the two line breakpoints, the keys on {Keys(breakpoints)}");
        }
        finally
        {
            window.Close();
        }

        static Breakpoint? KeyedRow(Control within) => (TopLevel.GetTopLevel(within)?.FocusManager?.GetFocusedElement() as ListBoxItem)?.Tag as Breakpoint;

        static string Keys(Control within) => TopLevel.GetTopLevel(within)?.FocusManager?.GetFocusedElement() switch
        {
            null => "nothing",
            ListBoxItem { Tag: Breakpoint row } => $"{row.Location}'s row",
            object other => other.GetType().Name,
        };
    }

    /// <summary>The Bookmarks window, shown, its list holding the keys' row.</summary>
    private static ListBox Bookmarks(MainWindow window)
    {
        Ui.Run(window, AppActions.ShowBookmarks);
        return (window.FocusManager?.GetFocusedElement() as Control)?.FindAncestorOfType<ListBox>()
            ?? throw new Xunit.Sdk.XunitException("Showing the Bookmarks window gave none of its rows the keys");
    }
}
