using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Navigation;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Editor tabs in a window of their own: Open Tab in New Window (Shift+F4, or the tab's menu) or a tab let go
/// outside the editors takes it into a window with its own tabs and splits, over the same open file as the main
/// window's; the IDE's keys work there, a tab drags back, its last tab closed closes it, and the folder brings it back
/// where it stood.</summary>
public sealed class EditorWindowTests : IDisposable
{
    private readonly string folder = TestFolders.Create("editor-windows-");
    private readonly List<Window> opened = [];
    private readonly IDisposable watching;

    public EditorWindowTests()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n}\n");
        File.WriteAllText(Path.Combine(folder, "Two.cs"), "class Two\n{\n}\n");
        File.WriteAllText(Path.Combine(folder, "Three.cs"), "class Three\n{\n    int x;\n}\n");
        watching = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => opened.Add(window));
    }

    public void Dispose()
    {
        watching.Dispose();
        TestFolders.Delete(folder);
    }

    [AvaloniaFact]
    public void Open_tab_in_new_window_takes_the_tab_to_a_window_over_the_same_file_where_the_ides_keys_work_and_its_last_tab_closes_it()
    {
        MainWindow window = Open(1200, 800, "Two.cs", "One.cs");
        EditorArea area = window.EditorArea;
        Assert.True(window.Perform(AppActions.SplitRight));
        Ui.Settle();
        EditorGroup left = area.Groups[0];

        Ui.Press(window, "shift F4");
        Window editors = EditorWindow("Shift+F4");
        EditorGroup there = Assert.Single(GroupsIn(editors));
        Assert.Equal(["One.cs"], Names(there));
        Assert.Same(left, Assert.Single(GroupsIn(window)));
        Assert.Equal(["Two.cs", "One.cs"], Names(left));
        Assert.Equal($"{Path.GetFileName(folder)} – One.cs", editors.Title);

        editors.KeyTextInput("// ");
        Ui.Settle();
        Assert.Same(left.Active, there.Active);
        Assert.StartsWith("// class One", left.Editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(["Two.cs", "One.cs"], area.Documents.Select(d => d.Name));

        Ui.Run(editors, AppActions.Save);
        Assert.Equal("// class One\n{\n}\n", File.ReadAllText(Path.Combine(folder, "One.cs")));
        Assert.False(there.Active!.IsModified, "Saved in the editor window, the file still shows as changed");

        Ui.Run(editors, AppActions.FindAction);
        ActionPopup popup = Assert.IsType<ActionPopup>(window.Popup);
        Assert.True(editors.GetVisualDescendants().Contains(popup), "Find Action, pressed in the editor window, opened over the main window");
        popup.Query = "split right";
        Ui.Settle();
        Ui.Press(editors, "ENTER");
        Assert.False(window.IsPopupOpen);
        Assert.Equal(2, GroupsIn(editors).Count);
        Assert.Single(GroupsIn(window));

        Ui.Run(editors, AppActions.CloseTab);
        Assert.True(editors.IsVisible);
        // Pressed alone: the key's release goes to whatever is in front once the window has gone.
        Keystroke close = AppActions.Keymap().ShortcutsFor(AppActions.CloseTab)[0].First;
        editors.KeyPress(close.Key, Ui.Raw(close.Modifiers), PhysicalKey.None, null);
        Ui.Settle();
        Assert.False(editors.IsVisible, "The editor window stayed open with its last tab closed");
        Assert.Equal(["Two.cs", "One.cs"], Names(left));
        Assert.Equal(["Two.cs", "One.cs"], area.Documents.Select(d => d.Name));
        window.Close();
    }

    [AvaloniaFact]
    public void Closing_an_editor_window_closes_its_tabs_saving_each_as_closing_a_tab_does()
    {
        MainWindow window = Open(1200, 800, "One.cs", "Two.cs");
        Ui.Press(window, "shift F4");
        Window editors = EditorWindow("Shift+F4");
        editors.KeyTextInput("// ");
        Ui.Settle();

        editors.Close();
        Ui.Settle();
        Assert.False(editors.IsVisible, "The editor window stayed open when closed");
        Assert.Equal("// class Two\n{\n}\n", File.ReadAllText(Path.Combine(folder, "Two.cs")));
        Assert.Equal(["One.cs"], window.EditorArea.Documents.Select(d => d.Name));
        Assert.Equal(["One.cs"], Names(Assert.Single(GroupsIn(window))));
        Assert.Equal("One.cs", window.EditorArea.Active?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void A_tab_dragged_out_of_the_main_window_opens_in_a_window_under_the_pointer_and_dragged_back_onto_the_strip_joins_it_again()
    {
        // Small enough that a window the size of its editors fits on the screen beside it.
        MainWindow window = Open(900, 600, "One.cs", "Two.cs");
        EditorGroup first = Assert.Single(GroupsIn(window));
        Point from = Ui.Centre(Tab(first, "Two.cs"), window);
        Point outside = new(window.Bounds.Width + 60, 200);
        Ui.Drag(window, from, outside);

        Window editors = EditorWindow("A tab dragged out of the main window");
        EditorGroup moved = Assert.Single(GroupsIn(editors));
        Assert.Equal(["Two.cs"], Names(moved));
        Assert.Equal(["One.cs"], Names(first));
        PixelPoint dropped = window.PointToScreen(outside);
        Control tab = Tab(moved, "Two.cs");
        PixelRect onScreen = new(tab.PointToScreen(default), PixelSize.FromSize(tab.Bounds.Size, 1));
        Assert.True(onScreen.Contains(dropped), $"The tab was let go at {dropped} on the screen, and its window put it at {onScreen}");

        Point back = Ui.Centre(tab, editors);
        Point strip = editors.PointToClient(first.TabStrip.PointToScreen(new Point(first.TabStrip.Bounds.Width - 30, first.TabStrip.Bounds.Height / 2)));
        Ui.Drag(editors, back, strip);
        Assert.False(editors.IsVisible, "The editor window stayed open with its one tab dragged back out of it");
        Assert.Equal(["One.cs", "Two.cs"], Names(first));
        Assert.Same(first, Assert.Single(window.EditorArea.Groups));
        Assert.Equal("Two.cs", window.EditorArea.Active?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void An_editor_window_comes_back_where_it_stood_with_its_splits_and_tabs_when_the_folder_opens_again()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow first = new() { Width = 1200, Height = 800, StateFolder = settings };
        first.Show();
        first.KeepLayout();
        first.OpenFolder(folder);
        Ui.Settle();
        first.OpenFile(Path.Combine(folder, "One.cs"));
        first.OpenFile(Path.Combine(folder, "Two.cs"));
        Ui.Settle();

        ChooseFromTabMenu(Tab(first.EditorArea.ActiveGroup, "Two.cs"), "Open Tab in New Window");
        Window editors = EditorWindow("Open Tab in New Window on the tab's menu");
        Assert.True(first.Perform(AppActions.SplitDown));
        first.OpenFile(Path.Combine(folder, "Three.cs"));
        first.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
        editors.Position = new PixelPoint(640, 120);
        (editors.Width, editors.Height) = (700, 520);
        Ui.Settle();
        Assert.Equal([["Two.cs"], ["Two.cs", "Three.cs"]], GroupsIn(editors).Select(Names));
        first.Close();
        Assert.False(editors.IsVisible, "The editor window outlived the main window");

        MainWindow second = new() { Width = 1200, Height = 800, StateFolder = settings };
        second.Show();
        second.KeepLayout();
        second.OpenFolder(folder);
        Ui.Settle();
        Window again = EditorWindow("The folder opened again");
        Assert.NotSame(editors, again);
        Assert.Equal(new PixelPoint(640, 120), again.Position);
        Assert.Equal(new Size(700, 520), again.Bounds.Size);
        IReadOnlyList<EditorGroup> groups = GroupsIn(again);
        Assert.Equal([["Two.cs"], ["Two.cs", "Three.cs"]], groups.Select(Names));
        Assert.Equal("Three.cs", groups[1].Active?.Name);
        Assert.Equal(new TextPosition(2, 4), groups[1].Editor.CaretPosition);
        Assert.True(groups[1].TranslatePoint(default, again)!.Value.Y > groups[0].TranslatePoint(default, again)!.Value.Y,
            "The window's Split Down came back as a split beside, not below");
        Assert.Equal(["One.cs"], Names(Assert.Single(GroupsIn(second))));
        Assert.Equal("One.cs", second.EditorArea.Active?.Name);
        Assert.Equal(["One.cs", "Three.cs", "Two.cs"], second.EditorArea.Documents.Select(d => d.Name).Order());
        Assert.Same(groups[0].Active, groups[1].Tabs[0].Document);

        second.OpenFolder(TestFolders.Create("other-"));
        Assert.False(again.IsVisible, "The editor window stayed open over another folder");
        second.OpenFolder(folder);
        Ui.Settle();
        Assert.Equal([["Two.cs"], ["Two.cs", "Three.cs"]], GroupsIn(EditorWindow("The folder opened a second time")).Select(Names));
        second.Close();
    }

    private MainWindow Open(double width, double height, params string[] files)
    {
        MainWindow window = Ui.OpenFolder(folder, width: width, height: height);
        foreach (string file in files)
        {
            window.OpenFile(Path.Combine(folder, file));
        }
        Ui.Settle();
        return window;
    }

    /// <summary>The editor window open last, failing as what should have opened it if none is.</summary>
    private Window EditorWindow(string what)
    {
        Ui.Settle();
        return opened.LastOrDefault(w => w is not MainWindow && w.IsVisible && GroupsIn(w).Count > 0)
            ?? throw new Xunit.Sdk.XunitException($"{what} opened no window of its own with editors in it");
    }

    private static IReadOnlyList<EditorGroup> GroupsIn(Window window) => [.. window.GetVisualDescendants().OfType<EditorGroup>()];

    private static string[] Names(EditorGroup group) => [.. group.Tabs.Select(t => t.Document.Name)];

    private static Control Tab(EditorGroup group, string name) =>
        group.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == name && t.Classes.Contains("tabName")).FindAncestorOfType<Border>()!;

    /// <summary>Chooses an item of a tab's menu, whose header carries the item's shortcut after its title.</summary>
    private static void ChooseFromTabMenu(Control tab, string title)
    {
        ContextMenu menu = tab.ContextMenu!;
        tab.RaiseEvent(new ContextRequestedEventArgs());
        Ui.Settle();
        MenuItem chosen = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header is string header && header.StartsWith(title, StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"The tab's menu has no {title}: [{string.Join(", ", menu.Items.OfType<MenuItem>().Select(i => i.Header))}]");
        Ui.Click(chosen);
        menu.Close();
        Ui.Settle();
    }
}
