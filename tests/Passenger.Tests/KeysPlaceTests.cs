using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;

namespace Passenger.Tests;

/// <summary>The keys go where the person is: a popup closing gives them back to what had them as it opened, a tool
/// window's keys give it the keys, Escape in one gives them back to the editor, and the menu key opens a row's menu at
/// the row.</summary>
public sealed class KeysPlaceTests : IDisposable
{
    private readonly string folder = TestFolders.Create("keys-place-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Rename and Find Action opened from an Explorer row and closed by Escape give the row the keys back, so the
    /// next Down chooses the next row rather than moving the editor's caret.</summary>
    [AvaloniaFact]
    public void A_popup_opened_from_an_Explorer_row_gives_the_row_the_keys_back_as_it_closes()
    {
        foreach (string name in (string[])["Alpha.txt", "Beta.txt", "Gamma.txt", "Delta.txt"])
        {
            TestFolders.Write(folder, name, $"{name}\nsecond line\n");
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("Alpha.txt");
        Ui.Settle();
        string text = window.EditorArea.Active!.Text.Snapshot.ToString();
        Ui.Run(window, AppActions.FocusExplorer);
        ExplorerNode first = window.ExplorerView.Selected ?? throw new Xunit.Sdk.XunitException("The Explorer chose no row as it took the keys");
        Assert.True(RowHasKeys(window, first), $"The Explorer's action left the keys on {Focused(window)}, not its chosen row {first.Name}");

        foreach (string action in (string[])[AppActions.Rename, AppActions.FindAction])
        {
            ExplorerNode was = window.ExplorerView.Selected!;
            Ui.Run(window, action);
            Assert.True(window.IsPopupOpen, $"{window.Actions[action]!.Title} on the Explorer's row {was.Name} opened no popup");
            Ui.Press(window, "ESCAPE");
            Assert.False(window.IsPopupOpen, $"Escape left {window.Actions[action]!.Title}'s popup open");
            Assert.True(RowHasKeys(window, was),
                $"{window.Actions[action]!.Title} closed by Escape gave the keys to {Focused(window)}, not back to the Explorer's row {was.Name} that had them");
            Ui.Press(window, "DOWN");
            ExplorerNode next = window.ExplorerView.Selected!;
            Assert.True(next != was && RowHasKeys(window, next),
                $"Down after {window.Actions[action]!.Title} closed did not choose the Explorer's next row: {next.Name} is chosen, {Focused(window)} has the keys");
            Assert.True(window.EditorArea.Active!.Text.Snapshot.ToString() == text, $"Down after {window.Actions[action]!.Title} closed went into the editor and changed Alpha.txt");
        }
        window.Close();
    }

    /// <summary>A Shift range chosen down the Explorer goes on from its end once the keys come back: Find Action closed by
    /// Escape, and Escape to the editor then the Explorer's keys, give them to the row that had them, not the range's start,
    /// so the next Shift+Down takes in one more row and Down chooses the row after the range.</summary>
    [AvaloniaFact]
    public void The_keys_given_back_to_the_Explorer_go_on_from_the_end_of_a_Shift_range()
    {
        string[] names = [.. "abcdefghijkl".Select(c => $"{c}.txt")];
        foreach (string name in names)
        {
            TestFolders.Write(folder, name, $"{name}\n");
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("l.txt");
        Ui.Settle();
        string text = window.EditorArea.Active!.Text.Snapshot.ToString();
        foreach ((string way, Action leave, Action back) in new (string, Action, Action)[]
        {
            ("Find Action closed by Escape", () => Ui.Run(window, AppActions.FindAction), () => Ui.Press(window, "ESCAPE")),
            ("Escape then the Explorer's keys", () => Ui.Press(window, "ESCAPE"), () => Ui.Run(window, AppActions.FocusExplorer)),
        })
        {
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.Single(r => r.Name == "c.txt"));
            for (int i = 0; i < 4; i++)
            {
                Ui.Press(window, "shift DOWN");
            }
            Assert.Equal(names[2..7], Chosen(window));
            leave();
            Assert.False(RowHasKeys(window, window.ExplorerView.Tree.Rows.Single(r => r.Name == "g.txt")), $"{way}: the keys never left the Explorer");
            back();
            Assert.True(RowHasKeys(window, window.ExplorerView.Tree.Rows.Single(r => r.Name == "g.txt")),
                $"{way} gave the keys to {Focused(window)}, not back to g.txt, the row that had them at the Shift range's end");
            Ui.Press(window, "shift DOWN");
            Assert.True(Chosen(window).SequenceEqual(names[2..8]), $"{way}, Shift+Down chose {string.Join(", ", Chosen(window))}, not c.txt to h.txt");
            Ui.Press(window, "DOWN");
            Assert.True(Chosen(window).SequenceEqual(["i.txt"]), $"{way}, Shift+Down then Down chose {string.Join(", ", Chosen(window))}, not i.txt");
        }
        Assert.Equal(text, window.EditorArea.Active!.Text.Snapshot.ToString());
        window.Close();

        static string[] Chosen(MainWindow window) => [.. window.ExplorerView.SelectedNodes.Select(n => n.Name).Order(StringComparer.Ordinal)];
    }

    /// <summary>Each tool window's keys show it and give it the keys, its search or its list's chosen row first, so what
    /// is typed next goes to it rather than into the open file.</summary>
    [AvaloniaFact]
    public void Each_tool_windows_keys_show_it_with_the_keys_inside_it()
    {
        TestFolders.Write(folder, "Notes.txt", "one\ntwo\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("Notes.txt");
        Ui.Settle();
        List<string> outside = [];
        foreach (Passenger.App.Docking.ToolWindow tool in window.DockHost.Windows.Where(w => w.Action is not null).ToList())
        {
            window.EditorArea.CodeEditor.Focus();
            Ui.Settle();
            if (AppActions.Keymap().ShortcutsFor(tool.Action!).Count > 0)
            {
                Ui.Run(window, tool.Action!);
            }
            else
            {
                Assert.True(window.Perform(tool.Action!), $"{tool.Title}'s action did not run");
                Ui.Settle();
            }
            TopLevel shownIn = TopLevel.GetTopLevel(tool.Content) ?? window;
            object? focused = shownIn.FocusManager?.GetFocusedElement();
            if (!tool.IsShown || focused is not Visual inside || (inside != tool.Content && !tool.Content.IsVisualAncestorOf(inside)))
            {
                outside.Add($"{tool.Title} ({(tool.IsShown ? "shown" : "not shown")}, the keys on {Describe(focused)})");
            }
        }
        Assert.True(outside.Count == 0, $"These tool windows' keys left the keys outside them, so what is typed next goes elsewhere: {string.Join("; ", outside)}");
        Assert.Equal("one\ntwo\n", window.EditorArea.Active!.Text.Snapshot.ToString());
        window.Close();
    }

    /// <summary>Escape in a tool window with nothing of its own to cancel gives the editor the keys, the window still shown;
    /// a search with words in it takes Escape for itself first.</summary>
    [AvaloniaFact]
    public void Escape_in_a_tool_window_gives_the_editor_the_keys_with_the_window_still_shown()
    {
        TestFolders.Write(folder, "Notes.txt", "one\ntwo\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("Notes.txt");
        Ui.Settle();
        Ui.Run(window, AppActions.FocusExplorer);
        Assert.True(RowHasKeys(window, window.ExplorerView.Selected!), $"The Explorer's action left the keys on {Focused(window)}");
        Ui.Press(window, "ESCAPE");
        Assert.True(window.EditorArea.CodeEditor.IsFocused, $"Escape in the Explorer left the keys on {Focused(window)}, not the editor");
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Explorer), "Escape in the Explorer hid it, where only Shift+Escape hides a tool window");

        Ui.Run(window, AppActions.ShowTests);
        TextBox search = Assert.IsType<TextBox>(window.FocusManager?.GetFocusedElement());
        search.Text = "Sums";
        Ui.Press(window, "ESCAPE");
        Assert.True(search.Text == string.Empty && search.IsFocused, "Escape in the Unit Tests' search with words in it did not empty it first, keeping the keys");
        Ui.Press(window, "ESCAPE");
        Assert.True(window.EditorArea.CodeEditor.IsFocused, $"Escape in the Unit Tests' empty search with no test to go to left the keys on {Focused(window)}, not the editor");
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Tests), "Escape in the Unit Tests window hid it");
        window.Close();
    }

    /// <summary>The menu key (Shift+F10) opens a row's menu under the chosen row, in the Explorer and in a tool window's
    /// list, wherever the pointer is, so a person at the keys finds it where they are.</summary>
    [AvaloniaFact]
    public void The_menu_key_opens_a_rows_menu_under_the_chosen_row_wherever_the_pointer_is()
    {
        foreach (string name in (string[])["Alpha.txt", "Beta.txt", "Gamma.txt"])
        {
            TestFolders.Write(folder, name, $"{name}\nsecond line\n");
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("Beta.txt");
        Ui.Settle();
        Ui.Run(window, AppActions.ToggleBookmark);
        // Over the toolbar, far from every row.
        window.MouseMove(new Point(window.Bounds.Width - 300, 20), Avalonia.Input.RawInputModifiers.None);

        Ui.Run(window, AppActions.FocusExplorer);
        Ui.Press(window, "DOWN");
        Control explorerRow = window.FocusManager?.GetFocusedElement() as Control ?? throw new Xunit.Sdk.XunitException("No Explorer row has the keys");
        Ui.Run(window, AppActions.ShowContextMenu);
        ContextMenu explorerMenu = window.ExplorerView.OpenMenu ?? throw new Xunit.Sdk.XunitException("Shift+F10 on an Explorer row opened no menu");
        Assert.True(Under(explorerMenu, explorerRow, out string explorerOff), $"Shift+F10 opened the Explorer row's menu {explorerOff}");
        explorerMenu.Close();
        Ui.Settle();

        Ui.Run(window, AppActions.ShowBookmarks);
        Control bookmarkRow = window.FocusManager?.GetFocusedElement() as Control ?? throw new Xunit.Sdk.XunitException("No Bookmarks row has the keys");
        ListBox bookmarks = bookmarkRow.FindAncestorOfType<ListBox>() ?? throw new Xunit.Sdk.XunitException($"The Bookmarks window's keys went to {Describe(bookmarkRow)}, not a row");
        Ui.Run(window, AppActions.ShowContextMenu);
        ContextMenu bookmarkMenu = bookmarks.ContextMenu is { IsOpen: true } open ? open : throw new Xunit.Sdk.XunitException("Shift+F10 on a bookmark opened no menu");
        Assert.True(Under(bookmarkMenu, bookmarkRow, out string bookmarkOff), $"Shift+F10 opened a bookmark's menu {bookmarkOff}");
        bookmarkMenu.Close();
        Ui.Settle();
        window.Close();

        static bool Under(ContextMenu menu, Control row, out string off)
        {
            PixelPoint menuAt = menu.PointToScreen(default);
            PixelPoint rowAt = row.PointToScreen(new Point(0, row.Bounds.Height));
            off = $"at {menuAt}, where the chosen row's bottom left is at {rowAt}";
            return Math.Abs(menuAt.X - rowAt.X) <= 40 && Math.Abs(menuAt.Y - rowAt.Y) <= 40;
        }
    }

    private static string Describe(object? focused) => focused switch
    {
        null => "nothing",
        Control { Name: { Length: > 0 } name } control => $"{control.GetType().Name} {name}",
        Control control => $"{control.GetType().Name} in {control.FindAncestorOfType<ListBox>()?.GetType().Name ?? control.GetVisualParent()?.GetType().Name}",
        _ => focused.GetType().Name,
    };

    /// <summary>Whether the Explorer's container of a row has the keys.</summary>
    private static bool RowHasKeys(MainWindow window, ExplorerNode row)
    {
        ListBox rows = window.ExplorerView.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Rows");
        return window.FocusManager?.GetFocusedElement() is Control focused && focused == rows.ContainerFromItem(row);
    }

    private static string Focused(MainWindow window) => window.FocusManager?.GetFocusedElement() switch
    {
        null => "nothing",
        Control { DataContext: ExplorerNode node } => $"the Explorer's row {node.Name}",
        object other => other.GetType().Name,
    };
}
