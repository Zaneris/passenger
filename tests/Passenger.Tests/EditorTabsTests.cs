using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.Text;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Passenger.Tests;

/// <summary>Rider's Window > Editor Tabs and the tab's own menu: a pinned tab stands left of the others with a pin, stays
/// through Close Other Tabs and Close All Tabs and comes back pinned with the folder; Reopen Closed Tab brings tabs back
/// in the reverse of the order they closed, each where it stood with its caret; Close Tabs to the Left and to the Right
/// leave the pinned ones; Unsplit and Unsplit All join the splits, keeping every tab.</summary>
public sealed class EditorTabsTests : IDisposable
{
    private static readonly string[] Five = ["One.cs", "Two.cs", "Three.cs", "Four.cs", "Five.cs"];

    private readonly string folder = TestFolders.Create("tabs-");
    private readonly string settings = TestFolders.Create("settings-");

    public EditorTabsTests()
    {
        foreach (string file in Five)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            File.WriteAllText(Path.Combine(folder, file), $"class {name}\n{{\n    int first;\n    int second;\n    int third;\n}}\n");
        }
    }

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_active_tabs_close_button_stays_visible_beside_its_unsaved_dot_and_a_pinned_tabs_pin(bool light)
    {
        MainWindow window = Keeping("One.cs", "Two.cs");
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.Autosaves = false;
        EditorGroup group = window.EditorArea.ActiveGroup;
        window.MouseMove(new Point(600, 300));
        Ui.Settle();
        Assert.Equal(1, CloseButton("Two.cs").Opacity);
        Assert.Equal(0, CloseButton("One.cs").Opacity);

        window.OpenFile("One.cs");
        window.KeyTextInput("// unsaved\n");
        Ui.Settle();
        Ellipse dot = Tab(group, "One.cs").GetVisualDescendants().OfType<Ellipse>().Single(e => e.Classes.Contains("modified"));
        Button close = CloseButton("One.cs");
        Assert.True(dot.IsEffectivelyVisible && dot.Opacity == 1, "The active file lost its unsaved indication to its close button");
        Assert.Equal(1, close.Opacity);
        Assert.True(dot.TranslatePoint(new Point(dot.Bounds.Width, 0), close)!.Value.X <= 0, "The unsaved dot overlaps the close button");

        Ui.ChooseFromMenu(Tab(group, "One.cs"), "Pin Tab");
        window.MouseMove(new Point(600, 300));
        Ui.Settle();
        Assert.True(PinShown(group, "One.cs"));
        Assert.Equal(1, CloseButton("One.cs").Opacity);

        // An inactive tab's close button appears under the pointer and closes that file without selecting it.
        close = CloseButton("Two.cs");
        Point at = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        Ui.Settle();
        Assert.Equal(1, close.Opacity);
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Ui.Settle();
        Assert.Equal(["One.cs"], Names(group));
        Assert.Equal("One.cs", window.EditorArea.Active?.Name);

        // The active close control works without first pointing at its tab.
        window.MouseMove(new Point(600, 300));
        close = CloseButton("One.cs");
        at = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Ui.Settle();
        Assert.Empty(group.Tabs);
        window.Close();

        Button CloseButton(string name) => Tab(group, name).GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabClose"));
    }

    [AvaloniaFact]
    public void A_pinned_tab_stands_left_with_a_pin_stays_through_Close_Others_and_Close_All_and_comes_back_pinned_with_the_folder()
    {
        MainWindow first = Keeping("One.cs", "Two.cs", "Three.cs", "Four.cs");
        EditorGroup group = first.EditorArea.ActiveGroup;
        Assert.Equal(["One.cs", "Two.cs", "Three.cs", "Four.cs"], Names(group));

        // Pin Tab from Three.cs's own menu: it goes to the left, with its pin, and the menu offers to unpin it.
        Ui.ChooseFromMenu(Tab(group, "Three.cs"), "Pin Tab");
        Assert.Equal(["Three.cs", "One.cs", "Two.cs", "Four.cs"], Names(group));
        Assert.True(PinShown(group, "Three.cs"), "The pinned tab shows no pin");
        Assert.False(PinShown(group, "One.cs"), "A tab never pinned shows a pin");
        Ui.ChooseFromMenu(Tab(group, "Three.cs"), "Unpin Tab");
        Assert.Equal(["Three.cs", "One.cs", "Two.cs", "Four.cs"], Names(group));
        Assert.False(PinShown(group, "Three.cs"), "The tab unpinned still shows its pin");
        Ui.ChooseFromMenu(Tab(group, "Three.cs"), "Pin Tab");

        // A file opened next opens after the pinned tabs, even with a pinned one shown.
        first.OpenFile("Three.cs");
        first.OpenFile("Five.cs");
        Ui.Settle();
        Assert.Equal(["Three.cs", "Five.cs", "One.cs", "Two.cs", "Four.cs"], Names(group));

        // Window > Editor Tabs > Close Other Tabs on Two.cs, then Close All Tabs: the pinned tab stays through both.
        first.OpenFile("Two.cs");
        FromEditorTabs(first, "Close Other Tabs");
        Assert.Equal(["Three.cs", "Two.cs"], Names(group));
        FromEditorTabs(first, "Close All Tabs");
        Assert.Equal(["Three.cs"], Names(group));
        Assert.Equal(["Three.cs"], first.EditorArea.Documents.Select(d => d.Name));
        Assert.False(MenuItem(first, "Close All Tabs").IsEnabled, "Close All Tabs can still run with only a pinned tab open");
        first.OpenFile("One.cs");
        Ui.Settle();
        first.Close();

        // The folder opened again has it pinned, with its pin, left of the others.
        MainWindow second = Keeping();
        EditorGroup reopened = second.EditorArea.ActiveGroup;
        Assert.Equal(["Three.cs", "One.cs"], Names(reopened));
        Assert.True(reopened.IsPinned(reopened.Tabs[0].Document), "Three.cs came back with the folder but no longer pinned");
        Assert.False(reopened.IsPinned(reopened.Tabs[1].Document));
        Assert.True(PinShown(reopened, "Three.cs"), "Three.cs came back pinned but shows no pin");
        second.Close();
    }

    [AvaloniaFact]
    public void Reopen_Closed_Tab_brings_tabs_back_in_the_reverse_of_the_order_they_closed_each_where_it_stood_with_its_caret()
    {
        MainWindow window = Keeping(Five);
        EditorArea area = window.EditorArea;
        EditorGroup group = area.ActiveGroup;
        Assert.False(MenuItem(window, "Reopen Closed Tab").IsEnabled, "Reopen Closed Tab can run with no tab closed");

        // Two.cs, Four.cs and One.cs closed by their keys, each with its caret on a line of its own.
        CloseAt(window, "Two.cs", new TextPosition(2, 4));
        CloseAt(window, "Four.cs", new TextPosition(3, 8));
        CloseAt(window, "One.cs", new TextPosition(4, 2));
        Assert.Equal(["Three.cs", "Five.cs"], Names(group));

        FromEditorTabs(window, "Reopen Closed Tab");
        Assert.Equal(["One.cs", "Three.cs", "Five.cs"], Names(group));
        Assert.Equal(("One.cs", new TextPosition(4, 2)), (area.Active?.Name, area.CodeEditor.CaretPosition));
        FromEditorTabs(window, "Reopen Closed Tab");
        Assert.Equal(["One.cs", "Three.cs", "Four.cs", "Five.cs"], Names(group));
        Assert.Equal(("Four.cs", new TextPosition(3, 8)), (area.Active?.Name, area.CodeEditor.CaretPosition));
        FromEditorTabs(window, "Reopen Closed Tab");
        Assert.Equal(Five, Names(group));
        Assert.Equal(("Two.cs", new TextPosition(2, 4)), (area.Active?.Name, area.CodeEditor.CaretPosition));
        Assert.False(MenuItem(window, "Reopen Closed Tab").IsEnabled, "Reopen Closed Tab can still run with every closed tab back");

        // A tab closed in a split goes back into that split, though another is worked in.
        EditorGroup left = group;
        window.OpenFile("Four.cs");
        FromEditorTabs(window, "Split Right");
        EditorGroup right = area.ActiveGroup;
        Ui.ChooseFromMenu(Tab(left, "Five.cs"), "Move to Opposite Group");
        Assert.Equal(["Four.cs", "Five.cs"], Names(right));
        CloseAt(window, "Five.cs", new TextPosition(1, 0));
        Assert.Equal(["Four.cs"], Names(right));
        left.Editor.Focus();
        Ui.Settle();
        Assert.Same(left, area.ActiveGroup);
        FromEditorTabs(window, "Reopen Closed Tab");
        Assert.Equal(["Four.cs", "Five.cs"], Names(right));
        Assert.Same(right, area.ActiveGroup);
        Assert.DoesNotContain("Five.cs", Names(left));
        window.Close();
    }

    [AvaloniaFact]
    public void Close_Tabs_to_the_Left_and_to_the_Right_close_those_on_that_side_but_the_pinned_ones()
    {
        MainWindow window = Keeping(Five);
        EditorGroup group = window.EditorArea.ActiveGroup;
        Ui.ChooseFromMenu(Tab(group, "Five.cs"), "Pin Tab");
        Assert.Equal(["Five.cs", "One.cs", "Two.cs", "Three.cs", "Four.cs"], Names(group));

        Ui.ChooseFromMenu(Tab(group, "Three.cs"), "Close Tabs to the Left");
        Assert.Equal(["Five.cs", "Three.cs", "Four.cs"], Names(group));
        Ui.ChooseFromMenu(Tab(group, "Three.cs"), "Close Tabs to the Right");
        Assert.Equal(["Five.cs", "Three.cs"], Names(group));
        Assert.Equal(["Five.cs", "Three.cs"], window.EditorArea.Documents.Select(d => d.Name).Order());

        // Nothing is left on either side but the pinned tab, so neither can run, from its menu or Window > Editor Tabs.
        Assert.Throws<InvalidOperationException>(() => Ui.ChooseFromMenu(Tab(group, "Three.cs"), "Close Tabs to the Left"));
        window.OpenFile("Three.cs");
        Assert.False(MenuItem(window, "Close Tabs to the Right").IsEnabled, "Close Tabs to the Right can run on the last tab");
        window.Close();
    }

    [AvaloniaFact]
    public void Unsplit_joins_a_split_into_the_group_worked_in_and_Unsplit_All_joins_every_split_keeping_each_tab_and_its_caret()
    {
        MainWindow window = Keeping("One.cs", "Two.cs");
        EditorArea area = window.EditorArea;
        Assert.False(MenuItem(window, "Unsplit All").IsEnabled, "Unsplit All can run on an editor that is not split");
        FromEditorTabs(window, "Split Right");
        window.OpenFile("Three.cs");
        FromEditorTabs(window, "Split Down");
        window.OpenFile("Four.cs");
        area.CodeEditor.GoTo(new TextPosition(3, 6));
        Ui.Settle();
        Assert.Equal([["One.cs", "Two.cs"], ["Two.cs", "Three.cs"], ["Three.cs", "Four.cs"]], area.Groups.Select(Names));
        EditorGroup bottom = area.ActiveGroup;

        // Unsplit from the lower right split: the one above it joins it, and the left one stays.
        FromEditorTabs(window, "Unsplit");
        Assert.Equal([["One.cs", "Two.cs"], ["Three.cs", "Four.cs", "Two.cs"]], area.Groups.Select(Names));
        Assert.Same(bottom, area.ActiveGroup);

        // Unsplit All: one group with every tab, the file worked in shown with its caret where it was.
        FromEditorTabs(window, "Unsplit All");
        EditorGroup joined = Assert.Single(area.Groups);
        Assert.Same(bottom, joined);
        Assert.Equal(["Three.cs", "Four.cs", "Two.cs", "One.cs"], Names(joined));
        Assert.Equal(4, area.Documents.Count);
        Assert.Equal(("Four.cs", new TextPosition(3, 6)), (area.Active?.Name, area.CodeEditor.CaretPosition));
        window.Close();
    }

    /// <summary>The window a person uses, keeping its state in the test's own folder, on the folder with these files open.</summary>
    private MainWindow Keeping(params string[] files)
    {
        MainWindow window = new() { Width = 1400, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        foreach (string file in files)
        {
            window.OpenFile(file);
        }
        Ui.Settle();
        return window;
    }

    /// <summary>Shows a file's tab, puts its caret somewhere, and closes it by its keys.</summary>
    private static void CloseAt(MainWindow window, string file, TextPosition caret)
    {
        window.OpenFile(file);
        window.EditorArea.CodeEditor.GoTo(caret);
        Ui.Settle();
        Ui.Run(window, AppActions.CloseTab);
    }

    /// <summary>An item of Window > Editor Tabs as the menu bar shows it once opened.</summary>
    private static NativeMenuItem MenuItem(MainWindow window, string title) =>
        Ui.Submenu(Ui.Submenu(window.MenuBar, "Window"), "Editor Tabs").Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == title)
            ?? throw new Xunit.Sdk.XunitException($"Window > Editor Tabs has no {title}");

    /// <summary>Chooses an item of Window > Editor Tabs, failing if it cannot run.</summary>
    private static void FromEditorTabs(MainWindow window, string title)
    {
        NativeMenuItem item = MenuItem(window, title);
        Assert.True(item.IsEnabled, $"Window > Editor Tabs > {title} cannot run here");
        item.Command!.Execute(null);
        Ui.Settle();
    }

    private static string[] Names(EditorGroup group) => [.. group.Tabs.Select(t => t.Document.Name)];

    private static Control Tab(EditorGroup group, string name) =>
        group.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == name && t.Classes.Contains("tabName")).FindAncestorOfType<Border>()!;

    private static bool PinShown(EditorGroup group, string name) =>
        Tab(group, name).GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabPin")).IsEffectivelyVisible;
}
