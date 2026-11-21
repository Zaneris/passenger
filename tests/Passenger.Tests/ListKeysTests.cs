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

    /// <summary>In the Bookmarks window, Enter on a bookmark's row goes to its line as a double click does, F4 too, and
    /// Delete takes the bookmark out.</summary>
    [AvaloniaFact]
    public void Enter_and_F4_on_a_bookmarks_row_go_to_its_line_and_Delete_takes_it_out()
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
    [AvaloniaFact]
    public void Space_on_a_commit_row_flips_its_box_and_F4_opens_its_file()
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
    [AvaloniaFact]
    public void The_Breakpoints_window_opens_with_the_keys_on_its_chosen_breakpoint_and_Delete_takes_each_out_in_turn()
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

            Ui.Click(breakpoints.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Add CLR Exception Breakpoints"));
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
