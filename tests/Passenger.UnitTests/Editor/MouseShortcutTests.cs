using Avalonia.Input;
using Passenger.Editor;

namespace Passenger.UnitTests.Editor;

/// <summary>A keymap's mouse shortcuts as IntelliJ's notation writes them: read back as written, a double-click's too,
/// shown as menus name them, given to an action exactly as a person's keymap lists them, and a gesture shared by a click
/// and a drag as Alt-click's Quick Evaluate shares Alt-drag's column.</summary>
public class MouseShortcutTests
{
    [Theory]
    [InlineData("alt button1")]
    [InlineData("meta shift button1")]
    [InlineData("control button2")]
    [InlineData("button4")]
    [InlineData("alt button1 doubleClick")]
    [InlineData("meta control alt shift button5 doubleClick")]
    public void A_mouse_shortcut_reads_back_as_it_is_written(string written)
    {
        MouseShortcut gesture = MouseShortcut.Parse(written);
        Assert.Equal(written, gesture.Notation());
        Assert.Equal(gesture, MouseShortcut.Parse(gesture.Notation()));
    }

    [Fact]
    public void A_mouse_shortcut_is_shown_by_its_modifiers_button_and_clicks()
    {
        bool mac = OperatingSystem.IsMacOS();
        Assert.Equal(mac ? "⌥Click" : "Alt+Click", MouseShortcut.Parse("alt button1").Display());
        Assert.Equal(mac ? "⇧⌘Click" : "Shift+Win+Click", MouseShortcut.Parse("meta shift button1").Display());
        Assert.Equal(mac ? "⌃Middle Click" : "Ctrl+Middle Click", MouseShortcut.Parse("control button2").Display());
        Assert.Equal(mac ? "⌥Double-Click" : "Alt+Double-Click", MouseShortcut.Parse("alt button1 doubleClick").Display());
        Assert.Equal("Back Button", MouseShortcut.Parse("button4").Display());
        Assert.Equal("Forward Button Double-Click", MouseShortcut.Parse("button5 doubleClick").Display());
    }

    [Fact]
    public void A_click_and_a_drag_share_a_gesture_where_two_clicks_cannot()
    {
        Keymap keymap = new Keymap("shared").Bind(EditorActions.ColumnSelectionDrag, "alt button1").Bind("quick", "alt button1");
        MouseShortcut alt = MouseShortcut.Parse("alt button1");
        Assert.Equal("quick", keymap.ActionFor(alt));
        Assert.Equal(EditorActions.ColumnSelectionDrag, keymap.DragFor(alt));
        Assert.Throws<InvalidOperationException>(() => keymap.Bind("other", "alt button1"));
        Assert.True(keymap.Conflict("other", alt) == "quick", "Another click given Alt-click was not told it takes it from the click that has it");
        Assert.True(keymap.Conflict(EditorActions.AddColumnSelectionDrag, alt) == EditorActions.ColumnSelectionDrag,
            "Another drag given Alt-click was not told it takes it from the drag that has it");
    }

    [Fact]
    public void An_action_set_from_a_persons_keymap_has_exactly_its_listed_gestures_and_takes_one_from_another_click()
    {
        Keymap keymap = new Keymap("set")
            .Bind("declaration", "F12", "meta button1")
            .Bind("split", "meta alt S")
            .Bind(EditorActions.ColumnSelectionDrag, "meta button1");
        MouseShortcut cmd = MouseShortcut.Parse("meta button1");
        keymap.Set("split", [Shortcut.Parse("meta alt S")], [cmd, MouseShortcut.Parse("button2 doubleClick")]);
        Assert.Equal("split", keymap.ActionFor(cmd));
        Assert.True(keymap.MouseShortcutsFor("declaration").Count == 0, "Go to Declaration kept the gesture Split took from it");
        Assert.True(keymap.ShortcutsFor("declaration").SequenceEqual([Shortcut.Parse("F12")]), "Go to Declaration lost its key with its gesture");
        Assert.True(keymap.DragFor(cmd) == EditorActions.ColumnSelectionDrag, "A click taking a gesture took it from the drag beside it too");
        Assert.Equal(["meta button1", "button2 doubleClick"], keymap.MouseShortcutsFor("split").Select(g => g.Notation()));

        keymap.Set("split", [Shortcut.Parse("meta alt S")], []);
        Assert.True(keymap.ActionFor(cmd) is null, "Split's gesture still ran it once its list named none");
        Assert.True(keymap.MouseShortcutsFor("split").Count == 0, "Split kept gestures its list no longer named");
    }

    [Fact]
    public void A_double_click_runs_the_one_clicks_action_only_where_the_keymap_gives_it_none_of_its_own()
    {
        Keymap keymap = new Keymap("clicks").Bind("back", "button4").Bind("open", "alt button1").Bind("split", "alt button1 doubleClick");
        Assert.Equal("back", keymap.ActionForAnyClick(new MouseShortcut(MouseButton.XButton1, KeyModifiers.None, 2)));
        Assert.Equal("split", keymap.ActionForAnyClick(new MouseShortcut(MouseButton.Left, KeyModifiers.Alt, 2)));
        Assert.Equal("open", keymap.ActionForAnyClick(new MouseShortcut(MouseButton.Left, KeyModifiers.Alt, 1)));
    }
}
