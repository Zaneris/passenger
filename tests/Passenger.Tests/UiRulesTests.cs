using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using System.Buffers.Binary;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Editor;
using Syntax = Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Tests;

/// <summary>Rules the whole window keeps, each decided once in a shared piece and held here across what the tests open:
/// a menu's current value checked and still enabled (MenuChoice); every tool window's tabs one look (ToolTabs,
/// Shell.axaml's panelTab); every popup's width from one scale, its title one size and its rows one height
/// (PopupFrame); a menu opening against its widget; the scrollbars, thin and arrowless; the open editor tab's ×; the
/// focus ring; every list's rows made once (ReusedRows); every Settings page a message names one the dialog lists
/// (SettingsDialog.PlaceOfPage); every window's icon the icon file's (AppIcon, which Shell.axaml's Window style gives);
/// and the window at half a 1366-wide screen with nothing cut, a stripe putting what has no room behind More. Each rule
/// that a look could break is held under both looks in one window, the look switched in place as Settings switches
/// it.</summary>
public sealed class UiRulesTests : IClassFixture<UiRulesTests.Project>, IAsyncLifetime
{
    private readonly Project project;

    public UiRulesTests(Project project)
    {
        this.project = project;
    }

    /// <summary>The project as written for the test, on whichever thread it runs (a window test's, or one of its own).</summary>
    public ValueTask InitializeAsync() => new(project.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The looks a window's rules are held under, in the order a window wears them: Passenger's and Harbour's, whose
    /// shapes are every other theme's (tools/themes.py gives each one or the other's), the rest's colours held by the rules
    /// of tokens alone (<see cref="EveryLook"/>) and their grounds' steps checked as tools/themes.py writes them.</summary>
    private static readonly SourcedTheme?[] Looks = [null, SourcedTheme.Repository("Harbour")];

    [Gate]
    [AvaloniaFact]
    public void A_menus_current_value_is_checked_with_Rider_s_tick_and_can_still_be_chosen()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Hello"), "the project's run configuration");
            window.OpenFile(project.PathOf("Notes.txt"));
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                Current(window, "BranchButton", "main", "the branch menu");
                Current(window, "RunConfigButton", "Hello", "the run configuration menu");
                Current(window, "LineSeparatorButton", "LF - Unix and macOS (\\n)", "the line separator menu");
                Current(window, "EncodingButton", "UTF-8", "the encoding menu");
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A menu item's submenu chevron is every chevron's, under each look: the IDE's own shape, as tall as a
    /// widget's, its middle on its item's middle.</summary>
    [AvaloniaFact]
    public void A_submenus_chevron_is_every_chevrons_shape_and_size_on_its_items_middle()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Notes.txt"));
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                PathIcon widget = Named<Button>(window, "BranchButton").GetVisualDescendants().OfType<PathIcon>().Single(i => i.Classes.Contains(StyleClasses.Chevron));
                window.EditorArea.CodeEditor.Focus();
                Ui.Run(window, AppActions.ShowContextMenu);
                Ui.WaitFor(() => window.OpenContextMenu is not null, "the editor's menu");
                Ui.Settle();
                MenuItem[] parents = [.. window.OpenContextMenu!.Items.OfType<MenuItem>().Where(i => i.ItemCount > 0)];
                Assert.NotEmpty(parents);
                foreach (MenuItem parent in parents)
                {
                    Avalonia.Controls.Shapes.Path chevron = parent.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "PART_ChevronPath");
                    Rect at = Placed(window, chevron);
                    Rect row = Placed(window, parent);
                    Assert.True(ReferenceEquals(chevron.Data, window.FindResource(IconKeys.ChevronRight)), $"{Header(parent)}'s chevron is a shape of its own, not the IDE's chevron");
                    Assert.True(Math.Abs(at.Height - widget.Bounds.Height) <= 0.5,
                        $"{Header(parent)}'s chevron is {at.Height:0.#} tall, where every other chevron (the branch widget's) is {widget.Bounds.Height:0.#}");
                    Assert.True(Math.Abs(at.Center.Y - row.Center.Y) <= 0.5,
                        $"{Header(parent)}'s chevron's middle is at {at.Center.Y:0.#}, off its item's middle at {row.Center.Y:0.#}");
                }
                window.OpenContextMenu.Close();
                Ui.Settle();
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A menu's words that are no command (the heading over the branch menu's branches) stand at the caption's step
    /// in the muted colour under each look, greyed for the keys and the pointer but never drawn as a greyed command; a value
    /// among several (a tool window's view mode and place) is ticked and still offered, never greyed; and every greyed item
    /// of these menus says why (ListMenus).</summary>
    [AvaloniaFact]
    public void A_menus_heading_is_muted_words_its_values_are_ticked_and_every_greyed_item_says_why()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Notes.txt"));
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                Ui.Click(Named<Button>(window, "BranchButton"));
                Ui.WaitFor(() => window.OpenContextMenu is not null, "the branch menu");
                ContextMenu branches = window.OpenContextMenu!;
                MenuItem[] items = [.. branches.Items.OfType<MenuItem>()];
                Ui.GreyedSayWhy(items, "The branch menu");
                MenuItem local = items.Single(i => Header(i) == "Local");
                ContentPresenter words = local.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_HeaderPresenter");
                Color? drawn = (words.Foreground as ISolidColorBrush)?.Color;
                Assert.True(!local.IsEffectivelyEnabled && local.InputGesture is null && words.FontSize == TypeRamp.Caption && drawn == Brush(window, TokenKeys.TextMuted),
                    $"The branch menu's Local heading is {(local.IsEffectivelyEnabled ? "a command the keys stop on" : "passed over")}, at {words.FontSize} in {drawn}, "
                    + $"where a heading is words the keys pass over, at the caption's {TypeRamp.Caption} in the muted {Brush(window, TokenKeys.TextMuted)}, never a greyed command");
                branches.Close();
                Ui.Settle();

                Control pane = window.ExplorerView.GetVisualAncestors().OfType<Control>().First(a => a.GetType().Name == "ToolPane");
                Ui.Click(pane.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Options"));
                MenuFlyout options = window.DockHost.OpenOptions ?? throw new Xunit.Sdk.XunitException("The Explorer's Options opened no menu");
                MenuItem[] made = [.. options.Items.OfType<MenuItem>()];
                Ui.GreyedSayWhy(made, "The Explorer's Options");
                string place = Passenger.App.Docking.ToolWindow.Name(window.DockHost.Windows.Single(w => w.Id == ToolWindowIds.Explorer).Anchor);
                foreach ((string sub, string current) in (ReadOnlySpan<(string, string)>)[("View Mode", "Dock"), ("Move to", place)])
                {
                    MenuItem parent = made.Single(i => Header(i) == sub);
                    Ui.Click(parent);
                    MenuItem[] values = [.. parent.Items.OfType<MenuItem>()];
                    Ui.GreyedSayWhy(values, sub);
                    MenuItem[] ticked = [.. values.Where(v => v.IsChecked)];
                    Assert.True(ticked is [{ IsEnabled: true }] && Header(ticked[0]) == current && values.All(v => v.IsEnabled && v.ToggleType == MenuItemToggleType.Radio),
                        $"{sub} offers [{string.Join(", ", values.Select(v => $"{Header(v)}{(v.IsChecked ? " ticked" : "")}{(v.IsEnabled ? "" : " greyed")}"))}], "
                        + $"where {current}, the one in force, is ticked and still offered as every menu's value is (MenuChoice)");
                }
                options.Hide();
                Ui.Settle();
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A right-click menu opens with its frame's corner at the pointer, however it is made, under each look: the
    /// editor's, the Explorer's, an editor tab's and the console's, each opened for the first time, and the editor's by the
    /// window's far corner, where it opens up and to the left with its far corner there, its shadow round it either way.</summary>
    [AvaloniaFact]
    public void A_right_click_menu_opens_with_its_frames_corner_at_the_pointer()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Notes.txt"));
            Assert.True(window.Perform(AppActions.ShowBuild), "The Build window did not show");
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
                AtPointer(editor, new Point(120, 40), () => window.OpenContextMenu, "The editor");
                AtPointer(editor, new Point(editor.Bounds.Width - 20, editor.Bounds.Height - 20), () => window.OpenContextMenu, "The editor, by the window's far corner,", far: true);
                ListBox rows = window.ExplorerView.Rows();
                AtPointer(rows.ContainerFromIndex(1)!, new Point(60, ListRow.Height / 2), () => window.ExplorerView.OpenMenu, "The Explorer's row");
                Control tab = window.EditorArea.ActiveGroup.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Text == "Notes.txt" && t.Classes.Contains(StyleClasses.TabName)).FindAncestorOfType<Border>()!;
                AtPointer(tab, new Point(12, tab.Bounds.Height / 2), () => tab.ContextMenu is { IsOpen: true } open ? open : null, "The editor tab");
                Passenger.App.ToolWindows.OutputView console = window.BottomPanel.Build;
                AtPointer(console.Editor, new Point(60, 12), () => console.Menu, "The Build console");
            });
        }
        finally
        {
            window.Close();
        }

        void AtPointer(Control control, Point at, Func<ContextMenu?> open, string what, bool far = false)
        {
            Point pointer = control.TranslatePoint(at, window)!.Value;
            Ui.RightClick(control, at);
            ContextMenu menu = open() ?? throw new Xunit.Sdk.XunitException($"{what} opened no menu on a right click");
            Rect drawn = Placed(window, menu.GetVisualDescendants().OfType<Border>().First(b => b.BoxShadow.Count > 0));
            Point corner = far ? drawn.BottomRight : drawn.TopLeft;
            Assert.True(Math.Abs(corner.X - pointer.X) <= 1.5 && Math.Abs(corner.Y - pointer.Y) <= 1.5,
                $"{what}'s menu opened with its frame at {drawn}, its {(far ? "far" : "near")} corner off the pointer at {pointer}, by the room round it for its shadow");
            menu.Close();
            Ui.Settle();
        }
    }

    /// <summary>A widget's menu opens beside the widget that opened it and never over it, wherever the widget stands, by
    /// the window's edges too, where the menu opens the other way: every tool window's Options, and the toolbar's and the
    /// status bar's widgets.</summary>
    [AvaloniaFact]
    public void A_widgets_menu_never_covers_the_widget_that_opened_it()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Notes.txt"));
            Ui.Settle();
            List<string> covered = [];
            void Beside(Button widget, string what, Action close)
            {
                Ui.Click(widget);
                Ui.WaitFor(() => OpenFrame(window) is not null, $"{what}'s menu");
                Rect drawn = Placed(window, OpenFrame(window)!);
                Rect at = Placed(window, widget);
                if (drawn.Intersects(at))
                {
                    covered.Add($"{what}'s menu at {drawn} over it at {at}");
                }
                close();
                Ui.Settle();
            }
            Passenger.App.Docking.DockHost dock = window.DockHost;
            foreach (Passenger.App.Docking.ToolWindow tool in dock.Windows.ToList())
            {
                dock.Show(tool.Id);
                Ui.Settle();
                Button options = tool.Content.GetVisualAncestors().OfType<DockPanel>().First(p => p.Classes.Contains(StyleClasses.ToolPane))
                    .GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(StyleClasses.ToolHeader))
                    .GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Options");
                Beside(options, $"{tool.Title}'s Options", () => dock.OpenOptions?.Hide());
            }
            foreach (string name in (string[])["MenuButton", "ProjectButton", "BranchButton", "RunConfigButton", "GitBranchButton", "LineSeparatorButton", "EncodingButton"])
            {
                Button widget = Named<Button>(window, name);
                if (widget.IsEffectivelyVisible && widget.IsHitTestVisible)
                {
                    Beside(widget, name, () => Ui.Press(window, "ESCAPE"));
                }
            }
            Assert.True(covered.Count == 0, $"A menu covers the widget that opened it, so a second click there lands on the menu: {string.Join("; ", covered)}");
        }
        finally
        {
            window.Close();
        }

        // The frame of the menu open over the window: a context menu's, or a menu flyout's (Options, the main menu).
        static Border? OpenFrame(MainWindow window) =>
            (window.OpenContextMenu is { IsOpen: true } menu ? menu : window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().FirstOrDefault(p => p.IsEffectivelyVisible) as Control)
                ?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.BoxShadow.Count > 0);
    }

    [Gate]
    [AvaloniaFact]
    public void The_open_editor_tab_shows_its_close_button_as_Rider_s_does_and_the_others_only_under_the_pointer()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            window.OpenFile(project.PathOf("Notes.txt"));
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                Border[] tabs = [.. window.EditorArea.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.Tab) && b.IsEffectivelyVisible)];
                Assert.Equal(2, tabs.Length);
                foreach (Border tab in tabs)
                {
                    bool open = tab.Classes.Contains(StyleClasses.Active);
                    Button close = tab.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains(StyleClasses.TabClose));
                    Assert.True(close.Opacity == (open ? 1 : 0),
                        $"The {(open ? "open" : "other")} tab's × has an opacity of {close.Opacity}, where Rider shows it on the open tab alone until the pointer is over another");
                }
            });
        }
        finally
        {
            window.Close();
        }
    }

    [Gate]
    [AvaloniaFact]
    public void Every_tool_windows_tabs_have_one_look_the_open_one_underlined_under_a_larger_title()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            UnderEachLook(window, () => ToolWindowTabs(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void ToolWindowTabs(MainWindow window)
    {
        Color mark = Brush(window, TokenKeys.ChosenMark);
        (string Where, double Height, double Size, Thickness Padding, Thickness Border)? first = null;
        int seen = 0;
        foreach (string action in (string[])[AppActions.ShowProblems, AppActions.ShowGit, AppActions.ShowDebug, AppActions.ShowTerminal, AppActions.ShowLocalHistory, AppActions.ShowAspire])
        {
            Assert.True(window.Perform(action), $"{action} did not run");
            Ui.Settle();
            if (action == AppActions.ShowTerminal)
            {
                Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
            }
            Assert.False(ToolWindowParts(window).OfType<Border>().Any(b => b.Classes.Contains(StyleClasses.Tab)),
                $"{action}'s window has an editor tab's look among its tabs, where every tool window's tab is a panelTab");
            // Its title larger than every word in it (14 over the window's 13), under a header of one height.
            foreach (TextBlock title in ToolWindowParts(window).OfType<TextBlock>().Where(t => t.Classes.Contains(StyleClasses.ToolTitle)))
            {
                Control header = title.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains(StyleClasses.ToolHeader));
                Assert.True(title.FontSize == 14 && title.FontWeight == FontWeight.SemiBold && Math.Abs(header.Bounds.Height - 38) < 0.5,
                    $"{action}'s window has its title '{title.Text}' in {title.FontSize} pt {title.FontWeight} under a header {header.Bounds.Height:0.#} tall, "
                    + "where every tool window's title is 14 pt semibold under a header 38 tall");
            }
            // Every row a person picks from is one height.
            foreach (ListBoxItem row in ToolWindowParts(window).OfType<ListBoxItem>().Where(r => r.Bounds.Height > 0))
            {
                Assert.True(Math.Abs(row.Bounds.Height - ListRow.Height) < 0.5,
                    $"A row of {action}'s window is {row.Bounds.Height:0.#} px tall, where every list's row is {ListRow.Height}");
            }
            // Read while the window is shown: one hidden leaves the tree, and its tabs their styles with it.
            foreach (ToggleButton tab in ToolWindowParts(window).OfType<ToggleButton>().Where(t => t.Classes.Contains(StyleClasses.PanelTab)))
            {
                seen++;
                string where = $"{action}'s tab '{Words(tab)}'";
                first ??= (where, tab.Bounds.Height, tab.FontSize, tab.Padding, tab.BorderThickness);
                Assert.True(tab.Bounds.Height == first.Value.Height && tab.FontSize == first.Value.Size && tab.Padding == first.Value.Padding
                    && tab.BorderThickness == first.Value.Border && tab.CornerRadius == default,
                    $"{where} is {tab.Bounds.Height:0.#} px tall in {tab.FontSize} pt with padding {tab.Padding} and a border of {tab.BorderThickness}, "
                    + $"where {first.Value.Where} is {first.Value.Height:0.#} px in {first.Value.Size} pt: tool windows' tabs differ");
                ChosenMark underline = tab.GetVisualDescendants().OfType<ChosenMark>().Single();
                Assert.True(underline.IsEffectivelyVisible == (tab.IsChecked == true) && underline.Side == Dock.Bottom
                    && underline.Fill is ISolidColorBrush ink && ink.Color == mark && (tab.IsChecked != true || Math.Abs(underline.Bar.Width - tab.Bounds.Width) < 0.5),
                    $"{where} is {(tab.IsChecked == true ? "open" : "closed")} and its chosen mark {(underline.IsEffectivelyVisible ? "shows" : "is hidden")} "
                    + $"along its {underline.Side} {underline.Bar.Width:0.#} wide, where the open tab alone is underlined across its width in the chosen mark");
            }
        }
        Assert.True(seen >= 5, $"Only {seen} tool window tabs were found in Problems, Git, Debug, the Terminal and Aspire");
    }

    /// <summary>A press on the open tab or the chosen segment leaves it chosen, as a press on another chooses that one
    /// alone: the Debug window's Debugger and Console tabs, and Find in Files' scopes.</summary>
    [AvaloniaFact]
    public void A_press_on_the_open_tab_or_the_chosen_segment_leaves_it_chosen_and_one_on_another_chooses_it_alone()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            Assert.True(window.Perform(AppActions.ShowDebug), $"{AppActions.ShowDebug} did not run");
            Ui.Settle();
            Chooses([.. ToolWindowParts(window).OfType<ToggleButton>().Where(t => t.Classes.Contains(StyleClasses.PanelTab) && TabName(t) is "Debugger" or "Console")],
                "the Debug window's tabs");
            Passenger.App.Navigation.FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
            Ui.Settle();
            Chooses([.. popup.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains(StyleClasses.Segment) && t.IsEffectivelyVisible)], "Find in Files' scopes");
        }
        finally
        {
            window.Close();
        }

        static void Chooses(ToggleButton[] row, string what)
        {
            Assert.True(row.Length >= 2 && row.Count(t => t.IsChecked == true) == 1, $"{what} are [{string.Join(", ", row.Select(TabName))}], {row.Count(t => t.IsChecked == true)} of them chosen");
            ToggleButton chosen = row.Single(t => t.IsChecked == true);
            Ui.Click(chosen);
            Assert.True(chosen.IsChecked == true && row.Count(t => t.IsChecked == true) == 1,
                $"A press on {what}' chosen '{TabName(chosen)}' let it go, leaving {row.Count(t => t.IsChecked == true)} chosen, where it stays chosen");
            ToggleButton other = row.First(t => t != chosen);
            Ui.Click(other);
            Assert.True(other.IsChecked == true && row.Count(t => t.IsChecked == true) == 1,
                $"A press on {what}' '{TabName(other)}' left {string.Join(", ", row.Where(t => t.IsChecked == true).Select(TabName))} chosen, where it alone is");
        }
    }

    /// <summary>A tool window's tab is ToolTabs' own, and a window's tabs are a ToolTabs.Strip, which keeps one open and
    /// says each press: the tabs' class given anywhere else fails here, naming its line, as does a tab ToolTabs.Tab makes
    /// for anything but a list's row (a session's tab, which its list makes and its model keeps one open).</summary>
    [Fact]
    public void Every_tool_windows_tab_is_ToolTabs_own_and_a_windows_tabs_are_a_strip()
    {
        Regex byHand = new(@"\bStyleClasses\.PanelTab\b|Classes=""[^""]*\bpanelTab\b");
        Regex lone = new(@"\bToolTabs\.Tab\(");
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/ToolTabs.cs", StringComparison.Ordinal))
            .SelectMany(f => byHand.Matches(f.Text).Concat(lone.Matches(f.Text).Where(m => !MemberAfter(f.Text, m.Index).Contains(".Showing<", StringComparison.Ordinal)))
                .Select(m => $"{f.Path}:{LineOf(f.Text, m.Index)}: {f.Text.Split('\n')[LineOf(f.Text, m.Index) - 1].Trim()}"))];
        Assert.True(found.Length == 0, $"{found.Length} tool window tabs are made by hand, so each keeps one open its own way or lets the open one go: "
            + $"make a window's tabs a ToolTabs.Strip:\n{string.Join("\n", found)}");
        Assert.Matches(byHand, "            ToggleButton button = new() { Classes = { StyleClasses.PanelTab } };");
        Assert.Matches(lone, "        consoleTab = ToolTabs.Tab(\"Console\", true);");
    }

    /// <summary>A few choices side by side, or one thing turned on or off by its words, are Segments, so each looks, keeps
    /// one chosen and says each press one way: the segments' class given anywhere else fails here, naming its line.</summary>
    [Fact]
    public void Every_choice_side_by_side_is_a_Segments()
    {
        Regex byHand = new(@"\bStyleClasses\.Segment\b|Classes=""[^""]*\bsegment\b");
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/Segments.cs", StringComparison.Ordinal))
            .SelectMany(f => byHand.Matches(f.Text).Select(m => $"{f.Path}:{LineOf(f.Text, m.Index)}"))];
        Assert.True(found.Length == 0, $"{found.Length} segments are made by hand, so each looks and keeps its choice its own way: make each row a "
            + $"Segments, a lone one Segments.Toggle:\n{string.Join("\n", found)}");
        Assert.Matches(byHand, "            ToggleButton tab = new() { Classes = { StyleClasses.Segment, StyleClasses.Caption }, Content = name };");
    }

    /// <summary>A search's option (Match case, Words, Regex, Recursively) is OptionToggles', so the find bar and Find in
    /// Files share its look, its tip and its key: the options' class given anywhere else fails here, naming its line.</summary>
    [Fact]
    public void Every_search_option_is_OptionToggles_own_with_its_key()
    {
        Regex byHand = new(@"\bStyleClasses\.Option\b|Classes=""[^""]*\boption\b");
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/OptionToggles.cs", StringComparison.Ordinal))
            .SelectMany(f => byHand.Matches(f.Text).Select(m => $"{f.Path}:{LineOf(f.Text, m.Index)}"))];
        Assert.True(found.Length == 0, $"{found.Length} search options are made by hand, so their looks, tips and keys drift from the find bar's and "
            + $"Find in Files': make each through OptionToggles:\n{string.Join("\n", found)}");
        Assert.Matches(byHand, "<ToggleButton Name=\"MatchCase\" Classes=\"option\" ToolTip.Tip=\"Match case\">");
        Assert.DoesNotMatch(byHand, "        OptionRow row = new(StyleClasses.OptionRow);");
    }

    /// <summary>The rest of the member a place in a C# file stands in, to its closing brace at a member's indent.</summary>
    private static string MemberAfter(string text, int at)
    {
        int end = text.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        return end < 0 ? text[at..] : text[at..end];
    }

    /// <summary>Every choice the window shows (a list's or a tree's chosen row, the open editor tab, a tool window's open
    /// tab, a stripe's open window's button, a toolbar's toggle that is on: Problems' severities, Mute Breakpoints) is shown by
    /// the one chosen mark beside its ground, in the chosen mark's colour on the side its kind takes, and nothing unchosen
    /// shows one, under each look dark and light; every row, tab, stripe button and toolbar toggle holds exactly one mark, so
    /// none draws its choice another way, by a ground alone a person with low vision may not tell from the page.</summary>
    [AvaloniaFact]
    public void Every_chosen_row_tab_stripe_button_and_toolbar_toggle_shows_the_chosen_mark_beside_its_ground_and_nothing_else_does()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            window.OpenFile(project.PathOf("Notes.txt"));
            Assert.True(window.Perform(AppActions.ShowProblems), "Problems did not show");
            Ui.Settle();
            Ui.Click(Toggle(window, "Show Warnings"));
            window.ExplorerView.Choose(window.ExplorerView.Tree.Rows.First(r => r.Name == "Notes.txt"));
            Ui.Settle();
            UnderEachVariant(window, variant => ChoicesMarked(window, variant, ["row", "stripe button", "tool window tab", "editor tab", "toolbar toggle"], ["toolbar toggle"]));

            Assert.True(window.Perform(AppActions.ShowDebug), "Debug did not show");
            Ui.Settle();
            ToggleButton mute = Toggle(window, "Mute Breakpoints");
            Ui.Click(mute);
            Assert.True(mute.IsChecked == true, "Mute Breakpoints did not turn on");
            UnderEachVariant(window, variant => ChoicesMarked(window, variant, ["toolbar toggle"], []));
            Ui.Click(mute);
            Assert.True(mute.IsChecked == false, "Mute Breakpoints did not turn off");
            UnderEachVariant(window, variant => ChoicesMarked(window, variant, [], ["toolbar toggle"]));
        }
        finally
        {
            window.RequestedThemeVariant = null;
            window.Close();
        }
    }

    /// <summary>A toolbar's toggle shown in the window, by the name a screen reader says for it.</summary>
    private static ToggleButton Toggle(MainWindow window, string name) =>
        window.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(t => t.IsEffectivelyVisible && AutomationProperties.GetName(t) == name)
        ?? throw new Xunit.Sdk.XunitException($"No toggle named {name} shows");

    /// <summary>Holds the window to a check under each look, dark and light.</summary>
    private static void UnderEachVariant(MainWindow window, Action<ThemeVariant> check)
    {
        foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Dark, ThemeVariant.Light])
        {
            window.RequestedThemeVariant = variant;
            UnderEachLook(window, () => check(variant));
        }
    }

    /// <summary>Holds every choice shown to the chosen mark, failing where a kind <paramref name="chosen"/> names was seen
    /// nowhere chosen, or one <paramref name="unchosen"/> names nowhere unchosen, so it went unchecked.</summary>
    private static void ChoicesMarked(MainWindow window, ThemeVariant variant, string[] chosen, string[] unchosen)
    {
        Color ink = Brush(window, TokenKeys.ChosenMark);
        ILookup<Control?, ChosenMark> marks = window.GetVisualDescendants().OfType<ChosenMark>()
            .ToLookup(m => m.TemplatedParent as Control ?? m.GetVisualParent()?.GetVisualParent() as Control);
        List<string> wrong = [];
        HashSet<string> seen = [];
        HashSet<string> passed = [];
        foreach (Control host in window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0))
        {
            (string What, bool Chosen, Dock Side)? kind = host switch
            {
                ListBoxItem row => ("row", row.IsSelected, Dock.Left),
                ToggleButton stripe when stripe.Classes.Contains(StyleClasses.Stripe) => ("stripe button", stripe.IsChecked == true, Dock.Left),
                ToggleButton tab when tab.Classes.Contains(StyleClasses.PanelTab) || tab.Classes.Contains(StyleClasses.Segment) => ("tool window tab", tab.IsChecked == true, Dock.Bottom),
                ToggleButton toggle when toggle.Classes.Contains(StyleClasses.Tool) || toggle.Classes.Contains(StyleClasses.Outcome) => ("toolbar toggle", toggle.IsChecked == true, Dock.Bottom),
                Border tab when tab.Classes.Contains(StyleClasses.Tab) => ("editor tab", tab.Classes.Contains(StyleClasses.Active), Dock.Bottom),
                Border row when row.Classes.Contains(StyleClasses.Chosen) => ("row laid out by hand", true, Dock.Left),
                _ => null,
            };
            if (kind is not { } k)
            {
                continue;
            }
            string where = $"{variant}: a {k.What} ({Words(host)})";
            ChosenMark[] own = [.. marks[host]];
            if (own.Length != 1)
            {
                wrong.Add($"{where} holds {own.Length} chosen marks, so its choice is drawn another way");
                continue;
            }
            ChosenMark mark = own[0];
            if (!k.Chosen)
            {
                passed.Add(k.What);
            }
            if (mark.IsEffectivelyVisible != k.Chosen)
            {
                wrong.Add($"{where} is {(k.Chosen ? "chosen and shows no" : "not chosen and shows its")} chosen mark");
            }
            else if (k.Chosen)
            {
                seen.Add(k.What);
                Rect bar = mark.Bar;
                if (mark.Side != k.Side || mark.Fill is not ISolidColorBrush { Color: Color fill } || fill != ink || Math.Min(bar.Width, bar.Height) < ChosenMark.Breadth
                    || !new Rect(mark.Bounds.Size).Contains(bar))
                {
                    wrong.Add($"{where} shows its mark along its {mark.Side} as {bar} in {mark.Fill}, where a {k.What}'s is {ChosenMark.Breadth} broad along its {k.Side} "
                        + $"in the chosen mark's {ink}");
                }
            }
        }
        foreach (ChosenMark stray in marks.Where(g => g.Key is null).SelectMany(g => g).Where(m => m.IsEffectivelyVisible))
        {
            wrong.Add($"{variant}: a chosen mark shows in {stray.GetVisualParent()?.GetType().Name} with no row, tab, stripe button or toggle it marks");
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} choices are drawn otherwise than by the chosen mark beside their ground, so a person may not tell what is chosen:\n"
            + string.Join("\n", wrong));
        Assert.True(chosen.All(seen.Contains), $"{variant}: only {string.Join(", ", seen)} were seen chosen of {string.Join(", ", chosen)}, so the rest went unchecked");
        Assert.True(unchosen.All(passed.Contains), $"{variant}: only {string.Join(", ", passed)} were seen unchosen of {string.Join(", ", unchosen)}, so the rest went unchecked");
    }

    /// <summary>The grounds a choice stands on (a chosen row's, the open tab's and the open window's stripe button's) are named
    /// by the theme's styles alone, which draw the chosen mark beside them: a row or tab chosen by hand takes the chosen
    /// class, never its ground bound by hand without its mark.</summary>
    [Fact]
    public void No_list_or_tab_draws_its_choice_by_a_ground_of_its_own()
    {
        Regex ground = new(@"\bTokenKeys\.(?:RowSelected|Tab_Active|Stripe_Selected)\b|Passenger\.(?:RowSelected|Tab\.Active|Stripe\.Selected)\b");
        string[] found = [.. from file in UiFiles(theme: false)
                             from line in file.Text.Split('\n').Select((text, at) => (Text: text, At: at + 1))
                             where ground.IsMatch(line.Text)
                             select $"{file.Path}:{line.At}: {line.Text.Trim()}"];
        Assert.True(found.Length == 0, $"{found.Length} places draw a choice by its ground alone, with no chosen mark beside it: give the row the chosen class "
            + $"(ChosenMark.Hold), or the list its rows through the theme's template:\n{string.Join("\n", found)}");
    }

    /// <summary>The two windows that list a run's services, Containers and Aspire, have one layout: their tabs are of one
    /// vocabulary in one order, Log first and shown at first, and their toolbars' service actions are one group in one order
    /// right after their Refresh, as their rows' menus begin with them.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_Containers_and_Aspire_windows_share_their_tabs_order_and_their_toolbars_service_group()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            string[] services = [.. Passenger.App.ToolWindows.RunWindows.ServiceActions.Select(service => service.Action)];
            foreach ((string action, Control view, string refresh) in (ReadOnlySpan<(string, Control, string)>)[
                (AppActions.ShowContainers, window.ContainersWindow, AppActions.ContainersRefresh), (AppActions.ShowAspire, window.AspireWindow, AppActions.AspireRefresh)])
            {
                Assert.True(window.Perform(action), $"{action} did not run");
                Ui.Settle();
                // Every tab the window has, those its chosen row does not show too.
                ToggleButton[] tabs = [.. view.GetVisualDescendants().OfType<ToggleButton>().Where(tab => tab.Classes.Contains(StyleClasses.PanelTab)
                    && Passenger.App.ToolWindows.RunWindows.Tabs.Contains(TabName(tab)))];
                string[] names = [.. tabs.Select(TabName)];
                Assert.True(names.Length >= 3, $"{action}'s window has only the tabs [{string.Join(", ", names)}]");
                Assert.Equal([.. Passenger.App.ToolWindows.RunWindows.Tabs.Where(names.Contains)], names);
                Assert.True(names[0] == "Log" && tabs[0].IsChecked == true, $"{action}'s window opens on {names.FirstOrDefault(n => tabs[Array.IndexOf(names, n)].IsChecked == true)}, where both open on Log");
                string[] toolbar = [.. view.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains(StyleClasses.Tool) && ActionButtons.GetAction(button) is not null)
                    .Select(button => ActionButtons.GetAction(button)!)];
                int at = Array.IndexOf(toolbar, refresh) + 1;
                Assert.True(at > 0 && toolbar.Skip(at).Take(services.Length).SequenceEqual(services),
                    $"{action}'s toolbar is [{string.Join(", ", toolbar)}], where both have the service actions [{string.Join(", ", services)}] right after their Refresh");
                foreach (string service in services)
                {
                    Button button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains(StyleClasses.Tool) && ActionButtons.GetAction(b) == service);
                    Assert.Equal(window.Actions[service]!.Title, ActionTips.Of(button) ?? ToolTip.GetTip(button) as string);
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Gate]
    [AvaloniaFact]
    public void Every_popup_takes_a_width_of_the_scale_one_title_size_and_rows_of_one_height()
    {
        MainWindow window = Open(1400, 900);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            UnderEachLook(window, () => Popups(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Popups(MainWindow window)
    {
        List<(string What, Border Frame)> seen = [];
        void Popup(string what, Action open)
        {
            open();
            Ui.Settle();
            Ui.WaitFor(() => window.IsPopupOpen, what);
            Border frame = window.Popup!.GetSelfAndVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(StyleClasses.Popup));
            HoldsToPopupRules(what, frame);
            seen.Add((what, frame));
            Ui.Press(window, "ESCAPE");
        }
        Popup("Find Action", () => window.Perform(AppActions.FindAction));
        Popup("Go to File", () => window.Perform(AppActions.GoToFile));
        Popup("Go to Symbol", () => window.Perform(AppActions.GoToSymbol));
        Popup("Recent Files", () => window.ShowRecentFiles());
        Popup("Switcher", () => window.Perform(AppActions.Switcher));
        Popup("Recent Locations", () => window.Perform(AppActions.RecentLocations));
        Popup("Find in Files", () => window.ShowFindInFiles(false));
        Popup("Stash Changes", () => window.Perform(AppActions.Stash));
        Popup("IDE Internal Errors", () => window.ShowErrors());

        TextBlock[] titles = [.. seen.SelectMany(s => s.Frame.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains(StyleClasses.PaneTitle)))];
        Assert.True(titles.Length >= seen.Count - 2, $"Only {titles.Length} of {seen.Count} popups have a title of the popups' look");
        Assert.True(titles.All(t => t.FontSize == titles[0].FontSize && t.FontWeight == titles[0].FontWeight),
            "Popups' titles differ in size or weight: " + string.Join(", ", titles.Select(t => $"'{t.Text}' {t.FontSize} {t.FontWeight}")));
    }

    /// <summary>A list's rows as every list's are, <see cref="ListRow.Height"/> tall: a dialog's check rows
    /// (<see cref="CheckRows"/>) are held to it here.</summary>
    internal static void HoldsToRowHeight(string what, Visual within)
    {
        foreach (ListBoxItem row in within.GetVisualDescendants().OfType<ListBoxItem>().Where(r => r.IsEffectivelyVisible))
        {
            Assert.True(Math.Abs(row.Bounds.Height - ListRow.Height) < 0.5,
                $"A row of {what} is {row.Bounds.Height:0.#} px tall, where every list's row is {ListRow.Height}");
        }
    }

    /// <summary>A popup's frame as every popup's is: a width of the popups' scale (<see cref="PopupFrame"/>), the frame's
    /// room (none where its rows reach its sides and its parts set their own), its rows <see cref="ListRow.Height"/> tall,
    /// and no word cut nor anything drawn over another in it (LayoutFaults); a popup that lives elsewhere (the debugger's
    /// hover card) is held to it here too.</summary>
    internal static void HoldsToPopupRules(string what, Border frame)
    {
        Assert.True(frame.Classes.Contains(StyleClasses.Popup), $"{what} is not framed as the popups are (Border.popup)");
        List<string> faults = LayoutFaults.Of((Visual?)TopLevel.GetTopLevel(frame) ?? frame, frame, what);
        Assert.True(faults.Count == 0, string.Join("\n", faults));
        Assert.True(PopupFrame.Widths.Any(w => Math.Abs(frame.Bounds.Width - w) < 0.5),
            $"{what} is {frame.Bounds.Width:0.#} px wide, not one of the popups' widths ({string.Join(", ", PopupFrame.Widths)})");
        Thickness[] rooms = [PopupFrame.Inset, PopupFrame.CardInset, ListRow.PopupList, default];
        Assert.True(rooms.Contains(frame.Padding),
            $"{what} keeps {frame.Padding} round what it holds, where a popup's frame keeps PopupFrame.Inset (a card's CardInset, a list's ListRow.PopupList)");
        foreach (ListBoxItem row in frame.GetVisualDescendants().OfType<ListBoxItem>().Where(r => r.IsEffectivelyVisible))
        {
            Assert.True(Math.Abs(row.Bounds.Height - ListRow.Height) < 0.5,
                $"A row of {what} is {row.Bounds.Height:0.#} px tall, where every popup's row is {ListRow.Height}");
        }
    }

    /// <summary>The popups' host frames a popup and keeps it inside the window whatever it is handed: a bare frame, a frame
    /// of its own Border type, a popup of its own template with no width set, the colour picker, or a panel with no frame,
    /// opened by the window's foot and right edge or centred under its top.</summary>
    [AvaloniaFact]
    public void A_popup_is_framed_and_kept_inside_the_window_however_it_is_built()
    {
        MainWindow window = Open(900, 600);
        try
        {
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                List<string> wrong = [];
                Check("A bare Border.popup", () => new Border { Classes = { StyleClasses.Popup }, Padding = new Thickness(10), Child = Words() });
                Check("A popup of its own Border type", () => new OwnFrame { Classes = { StyleClasses.Popup }, Padding = new Thickness(10), Child = Words() });
                Check("A UserControl with no width", () => new UserControl { Content = new Border { Classes = { StyleClasses.Popup }, Padding = new Thickness(10), Child = Words() } });
                Check("The colour picker", () => new Passenger.App.Xaml.ColourPicker(new Passenger.Xaml.Features.Argb(255, 200, 40, 40)));
                Check("A panel with no frame", () => new StackPanel { Children = { Words() } });
                Assert.True(wrong.Count == 0, string.Join("\n", wrong));

                void Check(string what, Func<Control> make)
                {
                    Color ground = Brush(window, TokenKeys.Popup);
                    foreach (bool atEdge in (bool[])[true, false])
                    {
                        string where = atEdge ? "opened by the window's foot and right edge" : "centred under the window's top";
                        Control popup = make();
                        window.Popups.Show(popup, atEdge ? window.Popups.PointIn(window, new Point(window.Bounds.Width - 10, window.Bounds.Height - 10)) : null);
                        Ui.Settle();
                        Border? frame = PopupFrame.FrameOf(popup);
                        Rect placed = frame is null ? default : Placed(window, frame);
                        Rect inside = new Rect(window.Bounds.Size).Deflate(4);
                        if (frame is null || frame.Background is not ISolidColorBrush { Color: Color drawn } || drawn != ground)
                        {
                            wrong.Add($"{what} {where} shows on {(frame?.Background as ISolidColorBrush)?.Color.ToString() ?? "no ground"}, where every popup stands in the popups' frame on {ground}");
                        }
                        else if (frame.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains(StyleClasses.Popup)))
                        {
                            wrong.Add($"{what} {where} shows a frame inside its frame, drawing two edges and two shadows");
                        }
                        else if (placed.Width < 200 || placed.Height < 20 || !inside.Contains(placed))
                        {
                            wrong.Add($"{what} {where} is drawn at {placed} in a window of {window.Bounds.Size}, cut or outside it, where a popup is kept whole inside the window");
                        }
                        Assert.True(window.Popup == popup, $"{what} {where} is not the popup the window says is open ({window.Popup?.GetType().Name ?? "none"})");
                        window.Popups.Close();
                        Ui.Settle();
                    }
                }
            });
        }
        finally
        {
            window.Close();
        }

        static TextBlock Words() => new() { Text = "A popup's words, long enough to need room of their own by the edge" };
    }

    /// <summary>A popup's frame of a type of its own, as the colour picker and the code assist popups are.</summary>
    private sealed class OwnFrame : Border;

    /// <summary>The popups made apart from the searches hold to the popups' rules as the searches do (HoldsToPopupRules):
    /// Rename's box, a list of fixes, a change's lines, the colour picker, and Godot's resource picker, items form and keep
    /// preview, each at a width of the scale with the frame's room, a list sized by what it holds at the narrowest width
    /// that holds it.</summary>
    [AvaloniaFact]
    public void Every_popup_made_apart_from_the_searches_takes_a_width_of_the_scale_and_the_frames_room()
    {
        MainWindow window = Open(1400, 900);
        try
        {
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                Check("Rename's box", () => new Passenger.App.CSharp.RenameBox("Greeter"));
                Check("A short list of fixes", () => new Passenger.App.CSharp.ChoicePopup("Fixes", [new("Add using System;", string.Empty, 1), new("Fully qualify", string.Empty, 2)]), PopupFrame.Narrow);
                Check("A list of fixes with long words", () => new Passenger.App.CSharp.ChoicePopup("Fixes",
                    [new($"Generate the method '{new string('x', 60)}' in the class it is called on", "the class", 1)]), PopupFrame.Wide);
                Check("A change's lines", () => new Passenger.App.Vcs.ChangePopup("var greeting = \"Hello\";\n", false, () => { }, () => { }, () => { }, () => { }, _ => { }));
                Check("The colour picker", () => new Passenger.App.Xaml.ColourPicker(new Passenger.Xaml.Features.Argb(255, 200, 40, 40)));
                Check("Godot's resource picker", () => new Passenger.App.Godot.GodotResourcePicker(project.Folder, ["Texture2D"], null));
                Check("Godot's items form", () => new Passenger.App.Godot.GodotItemsEditor("waves", Passenger.Workspace.GodotType.Array,
                    new Passenger.App.Godot.GodotArray([1L, 2L]) { Element = Passenger.App.Godot.GodotElement.Of(Passenger.Workspace.GodotType.Int) },
                    new Passenger.App.Godot.GodotHint(Passenger.App.Godot.GodotEditing.Items) { Element = Passenger.Workspace.GodotType.Int },
                    new SilentTuner(), value => Task.FromResult(value), () => { }, (_, _) => new Border()));
                Check("Godot's keep preview", () => new Passenger.App.Godot.GodotKeepPreview("Keep in the scene", [], ["position: the node is made by a script"]));
            });
        }
        finally
        {
            window.Close();
        }

        void Check(string what, Func<Control> make, double? width = null)
        {
            Control popup = make();
            window.Popups.Show(popup, window.Popups.PointIn(window, new Point(100, 100)));
            Ui.Settle();
            Border frame = PopupFrame.FrameOf(popup) ?? throw new Xunit.Sdk.XunitException($"{what} shows in no popup's frame");
            HoldsToPopupRules(what, frame);
            Assert.True(width is null || Math.Abs(frame.Bounds.Width - width.Value) < 0.5,
                $"{what} is {frame.Bounds.Width:0.#} px wide, where the narrowest of the popups' widths holding it is {width}");
            window.Popups.Close();
            Ui.Settle();
        }
    }

    /// <summary>A Godot form's tuner that hears nothing: the form is shown, not used.</summary>
    private sealed class SilentTuner : Passenger.App.Godot.IGodotTuner
    {
        public void Settled(long element)
        {
        }

        public void Keep(Passenger.App.Godot.GodotTuned tuned, Control from)
        {
        }

        public void ShowPopup(Control popup, Control from)
        {
        }

        public void ClosePopup()
        {
        }

        public void Say(string words)
        {
        }
    }

    /// <summary>A popup's frame takes its room and width from PopupFrame (Inset, CardInset, a width of its scale, Fitting, a
    /// FittingFrame) or a list popup's ListRow.PopupList, never a number of its own: a frame whose own Padding, Width,
    /// MinWidth or MaxWidth names neither fails here, naming its line, as does an option of a popup's made of its own.</summary>
    [Fact]
    public void Every_popups_frame_takes_its_room_and_width_from_PopupFrame()
    {
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/PopupFrame.cs", StringComparison.Ordinal) && !f.Path.EndsWith("/PopupHost.cs", StringComparison.Ordinal))
            .SelectMany(f => OwnRoom(f.Path, f.Text))];
        Assert.True(found.Length == 0, $"{found.Length} popups give their frame a room or width of its own, or make an option of their own, so they stand "
            + $"apart from every other popup: take PopupFrame's (Inset, CardInset, a width, Fitting or a FittingFrame, Option) or ListRow.PopupList:\n{string.Join("\n", found)}");
        Assert.Single(OwnRoom("a.cs", "Border popup = new()\n{\n    Width = 320,\n    Padding = PopupFrame.Inset,\n    Classes = { StyleClasses.Popup },\n    Child = new StackPanel { Width = 40 },\n};"));
        Assert.Single(OwnRoom("a.cs", "    {\n        Classes.Add(StyleClasses.Popup);\n        Width = PopupFrame.Narrow;\n        Padding = new Thickness(Gaps.Normal);\n        x = new TextBox { MinWidth = 220 };\n    }"));
        Assert.Empty(OwnRoom("a.cs", "Content = new Border { Classes = { StyleClasses.Popup }, Padding = PopupFrame.Inset, Child = content };"));
        Assert.Single(OwnRoom("a.cs", "option = new CheckBox { Content = shown, Focusable = false };"));

        static IEnumerable<string> OwnRoom(string path, string text)
        {
            Regex sized = new(@"^\s*(Padding|Width|MinWidth|MaxWidth)\s*=\s*(.+?)\s*[,;]?\s*$");
            if (path.EndsWith(".axaml", StringComparison.Ordinal))
            {
                foreach (Match tag in Regex.Matches(text, @"<[\w:]+[^>]*\bClasses=""popup""[^>]*>"))
                {
                    foreach (Match own in Regex.Matches(tag.Value, @"\b(?:Padding|Width|MinWidth|MaxWidth)=""([^""]*)""").Where(a => !Named(a.Groups[1].Value)))
                    {
                        yield return $"{path}:{LineOf(text, tag.Index)}: {own.Value}";
                    }
                }
                yield break;
            }
            foreach (Match popup in Regex.Matches(text, @"\bStyleClasses\.Popup\b"))
            {
                (int start, int end) = FrameBlock(text, popup.Index);
                int depth = 0;
                int line = start + 1;
                int lineDepth = 0;
                for (int i = start + 1; i <= end; i++)
                {
                    char c = i < end ? text[i] : '\n';
                    if (c == '\n')
                    {
                        // Each member the line gives at the block's own depth, one-line initializers' too.
                        foreach (string member in AtDepth(text[line..i], lineDepth))
                        {
                            if (sized.Match(member) is { Success: true } own && !Named(own.Groups[2].Value))
                            {
                                yield return $"{path}:{LineOf(text, line)}: {member.Trim()}";
                            }
                        }
                        line = i + 1;
                        lineDepth = depth;
                    }
                    depth += c is '{' or '(' or '[' ? 1 : c is '}' or ')' or ']' ? -1 : 0;
                }
            }
            foreach (Match option in Regex.Matches(text, @"(?:\bnew\s+CheckBox\b|\bCheckBox\s+\w+\s*=\s*new\s*\(\s*\))[^;]*\bFocusable\s*=\s*false"))
            {
                yield return $"{path}:{LineOf(text, option.Index)}: an option's check box of its own";
            }
        }

        // A line's pieces, split at its commas, that stand at the block's own depth (the line starting at depth).
        static IEnumerable<string> AtDepth(string line, int depth)
        {
            int at = depth;
            int from = 0;
            for (int i = 0; i <= line.Length; i++)
            {
                char c = i < line.Length ? line[i] : ',';
                if (c == ',' && at == 0)
                {
                    yield return line[from..i];
                    from = i + 1;
                }
                at += c is '{' or '(' or '[' ? 1 : c is '}' or ')' or ']' ? -1 : 0;
                if (at < 0)
                {
                    yield break;
                }
            }
        }

        static bool Named(string value) => value.Contains("PopupFrame.", StringComparison.Ordinal) || value.Contains("ListRow.PopupList", StringComparison.Ordinal);

        // The braces whose members a frame's class is given among: an object's initializer, or the body that adds the class to itself.
        static (int Start, int End) FrameBlock(string text, int at)
        {
            int start = Enclosing(text, at);
            if (Regex.IsMatch(text[..start], @"\bClasses\s*=\s*$"))
            {
                start = Enclosing(text, start);
            }
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                depth += text[i] == '{' ? 1 : text[i] == '}' ? -1 : 0;
                if (depth == 0)
                {
                    return (start, i);
                }
            }
            return (start, text.Length);
        }

        static int Enclosing(string text, int at)
        {
            int depth = 0;
            for (int i = at - 1; i >= 0; i--)
            {
                depth += text[i] == '}' ? 1 : text[i] == '{' ? -1 : 0;
                if (depth < 0)
                {
                    return i;
                }
            }
            return 0;
        }
    }

    [Gate]
    [AvaloniaFact]
    public void Every_scrollbar_is_Rider_s_thin_and_arrowless_and_a_sideways_one_shows_only_for_what_is_wider()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            window.Perform(AppActions.ShowProblems);
            Ui.Settle();
            UnderEachLook(window, () => Scrollbars(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Scrollbars(MainWindow window)
    {
        ScrollBar[] bars = [.. window.GetVisualDescendants().OfType<ScrollBar>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0)];
        Assert.Contains(bars, b => b.Orientation == Orientation.Vertical && b.FindAncestorOfType<Passenger.Editor.CodeEditor>() is not null);
        foreach (ScrollBar bar in bars)
        {
            string where = bar.FindAncestorOfType<Passenger.Editor.CodeEditor>() is not null ? "The editor's" : $"A {bar.Parent?.GetType().Name}'s";
            Assert.False(bar.GetVisualDescendants().OfType<RepeatButton>().Any(b => b.Name is "PART_LineUpButton" or "PART_LineDownButton"),
                $"{where} {bar.Orientation} scrollbar has arrows, where Rider's has none");
            double across = bar.Orientation == Orientation.Vertical ? bar.Bounds.Width : bar.Bounds.Height;
            Assert.True(across <= 14, $"{where} {bar.Orientation} scrollbar is {across:0.#} px across, where Rider's are thin");
            Thumb thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            double thumbAcross = bar.Orientation == Orientation.Vertical ? thumb.Bounds.Width : thumb.Bounds.Height;
            Assert.True(thumb.CornerRadius.TopLeft > 0 && thumbAcross < across, $"{where} {bar.Orientation} scrollbar's thumb is not Rider's thin rounded one");
            if (bar.Orientation == Orientation.Horizontal && bar.TemplatedParent is ScrollViewer viewer)
            {
                Assert.True(viewer.Extent.Width > viewer.Viewport.Width, $"{where} sideways scrollbar shows for what is no wider than its view");
                Rect shown = Placed(window, Presenter(viewer));
                Assert.True(shown.Bottom <= Placed(window, bar).Top + 0.5, $"{where} sideways scrollbar lies over the last of what it scrolls, where it takes a row of its own");
            }
            if (bar.Orientation == Orientation.Vertical && bar.TemplatedParent is ScrollViewer lines && lines.TemplatedParent is TextBox)
            {
                Rect shown = Placed(window, Presenter(lines));
                Assert.True(shown.Right <= Placed(window, bar).Left + 0.5, "A text box's upright scrollbar lies over its lines' ends, where it takes a column of its own");
            }
        }
    }

    private static ScrollContentPresenter Presenter(ScrollViewer viewer) =>
        viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().First(p => p.TemplatedParent == viewer);

    [Gate]
    [AvaloniaFact]
    public void At_half_a_1366_wide_screen_the_window_cuts_nothing_and_a_stripe_too_short_puts_the_rest_behind_More()
    {
        MainWindow window = Open(683, 768);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            UnderEachLook(window, () => HalfScreen(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void HalfScreen(MainWindow window)
    {
        // Each look from half a 1366-wide screen, as the last left it shorter and narrower.
        window.Width = 683;
        window.Height = 768;
        Ui.Settle();
        Assert.True(Math.Abs(window.Bounds.Width - 683) < 0.5, $"The window is {window.Bounds.Width:0.#} wide at half a 1366-wide screen: its least width is wider");
        Control end = Named<Control>(window, "ToolbarEnd");
        Control start = Named<Control>(window, "ToolbarStart");
        foreach (Button button in end.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible))
        {
            Rect at = Placed(window, button);
            Assert.True(at.Right <= window.Bounds.Width + 0.5, $"The toolbar's {ToolTip.GetTip(button)} is cut off at the window's right edge");
        }
        Assert.True(Placed(window, start).Right <= Placed(window, end).Left + 0.5,
            "The toolbar's project and branch widgets run under its run widget, where Rider's give way, ending in an ellipsis");
        TextBlock project = Named<TextBlock>(window, "ProjectName");
        Assert.True(project.TextTrimming == TextTrimming.CharacterEllipsis, "The project's name is cut rather than ending in an ellipsis");
        StripeButtonsFit(window);
        Assert.True(window.EditorArea.Bounds.Width > 200, $"The editor keeps {window.EditorArea.Bounds.Width:0.#} px beside the tool windows");

        Assert.True(window.Perform(AppActions.ShowNuGet), "NuGet did not show");
        Ui.Settle();
        CheckBox prerelease = window.DockHost.GetVisualDescendants().OfType<CheckBox>().First(c => c.Content as string == "Prerelease" && c.IsEffectivelyVisible);
        Rect nuget = Placed(window, prerelease.FindAncestorOfType<Passenger.App.ToolWindows.NuGetView>()!);
        foreach (Control part in ((Panel)prerelease.Parent!).Children.Where(c => c.IsVisible))
        {
            Rect at = Placed(window, part);
            Assert.True(at.Right <= nuget.Right + 0.5 && at.Width > 0, $"NuGet's {part.GetType().Name} is cut off at its window's right edge ({at.Right:0.#} past {nuget.Right:0.#})");
        }

        foreach (string action in (string[])[AppActions.ShowTests, AppActions.ShowFind, AppActions.ShowDebug, AppActions.ShowProfiler, AppActions.ShowLocalHistory])
        {
            Assert.True(window.Perform(action), $"{action} did not run");
            Ui.Settle();
            foreach (TextBlock empty in window.DockHost.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains(StyleClasses.EmptyText) && t.IsEffectivelyVisible))
            {
                Rect text = Placed(window, empty);
                Rect room = Placed(window, (Visual)empty.Parent!);
                Assert.True(text.Left >= room.Left - 0.5 && text.Right <= room.Right + 0.5,
                    $"After {action}, '{empty.Text}' runs past its pane ({text.Left:0.#} to {text.Right:0.#} in {room.Left:0.#} to {room.Right:0.#})");
            }
        }

        window.Height = 500;
        window.Width = MainWindow.SmallestWidth;
        Ui.Settle();
        StripeButtonsFit(window);
        ToggleButton more = window.DockHost.GetVisualDescendants().OfType<ToggleButton>().First(b => ToolTip.GetTip(b) as string == "More Tool Windows" && b.IsEffectivelyVisible);
        string[] hidden = [.. window.DockHost.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Tag is string && !b.IsVisible).Select(b => (string)b.Tag!)];
        Assert.NotEmpty(hidden);
        Ui.Click(more);
        ContextMenu menu = window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault(m => m.IsOpen)
            ?? throw new Xunit.Sdk.XunitException("More opened no menu of the tool windows it holds");
        Assert.Equal(hidden.Length, menu.Items.Count);
        menu.Close();
        Ui.Settle();
    }

    /// <summary>A window opens inside the working area of a screen smaller than its default size: centred when new, by
    /// the frame the platform reports, whether its content is the title bar or the system's frame stays round it; one
    /// placed past the area's edge is brought onto it.</summary>
    [Gate]
    [AvaloniaFact]
    public void A_window_opens_inside_a_small_screens_working_area_centred_and_one_placed_past_its_edge_is_brought_onto_it()
    {
        PixelRect area = new(0, 0, 3000, 1660);
        MainWindow window = new() { Autosaves = false };
        MainWindow placed = new() { Autosaves = false, Width = 2400, Height = 1400, Position = new PixelPoint(2900, 1500) };
        LargeWindow framed = new() { Width = 2400, Height = 1400 };
        try
        {
            window.FitWithin(area, 2, keepPlace: false);
            window.Show();
            placed.FitWithin(area, 2, keepPlace: true);
            placed.Show();
            framed.FitWithin(area, 2, keepPlace: false);
            framed.Show();
            Ui.Settle();
            foreach ((MainWindow shown, string what) in (IEnumerable<(MainWindow, string)>)[(window, "A new window"), (placed, "A window placed past the screen's edge")])
            {
                PixelRect at = Frame(shown);
                Assert.True(area.Contains(at), $"{what} opened at {at}, {shown.ClientSize} large, past the screen's working area {area}");
                Assert.True(shown.ClientSize.Width >= MainWindow.SmallestWidth && shown.ClientSize.Height >= MainWindow.SmallestHeight,
                    $"{what} opened {shown.ClientSize} large, under the window's smallest");
            }
            foreach ((Window shown, string what) in (IEnumerable<(Window, string)>)[(window, "A new window"), (framed, "A new window in the system's frame")])
            {
                PixelRect centred = Frame(shown);
                Assert.True(area.Contains(centred) && Math.Abs(centred.Center.X - area.Center.X) <= 2 && Math.Abs(centred.Center.Y - area.Center.Y) <= 2,
                    $"{what} opened at {centred}, not centred on the screen's working area {area}");
            }
        }
        finally
        {
            window.Close();
            placed.Close();
            framed.Close();
        }

        static PixelRect Frame(Window shown) => new(shown.Position, PixelSize.FromSize(shown.FrameSize ?? shown.ClientSize, 2));
    }

    /// <summary>Every window the IDE opens is an IdeWindow, fitted by one rule to the working area of its owner's screen as it
    /// shows: a dialog larger than the screen, whether it is sized by hand or by its content, opens no larger than the area
    /// and wholly on it, centred over its owner where it can be, its title bar and its buttons in reach.</summary>
    [AvaloniaFact]
    public void Every_window_the_IDE_opens_fits_its_owners_screen_and_a_dialog_larger_than_it_opens_wholly_on_it()
    {
        string[] unfitted = [.. typeof(MainWindow).Assembly.GetTypes()
            .Where(t => typeof(Window).IsAssignableFrom(t) && !typeof(IdeWindow).IsAssignableFrom(t)).Select(t => t.Name)];
        Assert.True(unfitted.Length == 0,
            $"{string.Join(", ", unfitted)} {(unfitted.Length == 1 ? "is a window" : "are windows")} the shared fit never reaches (IdeWindow): on a small screen its title bar or its buttons open out of reach");

        MainWindow window = Open(1200, 800);
        Screen screen = window.Screens.ScreenFromWindow(window) ?? throw new Xunit.Sdk.XunitException("The window stands on no screen");
        PixelRect area = screen.WorkingArea;
        Size room = area.Size.ToSize(screen.Scaling);
        LargeWindow large = new() { Width = room.Width * 2, Height = room.Height * 2, MinWidth = 320, MinHeight = 200 };
        ConfirmDialog tall = new("A long question", string.Join("\n\n", Enumerable.Repeat("A paragraph that goes on long enough to wrap.", 120)), "Go On");
        try
        {
            large.Show(window);
            _ = tall.ShowDialog<bool>(window);
            Ui.Settle();
            foreach ((Window shown, string what) in (IEnumerable<(Window, string)>)[(large, "A window asking for twice the screen"), (tall, "A question taller than the screen")])
            {
                PixelRect at = new(shown.Position, PixelSize.FromSize(shown.FrameSize ?? shown.ClientSize, screen.Scaling));
                Assert.True(area.Contains(at), $"{what} opened at {at}, past the screen's working area {area}");
                Assert.True(shown.ClientSize.Width >= shown.MinWidth && shown.ClientSize.Height >= shown.MinHeight, $"{what} opened {shown.ClientSize}, under its least");
            }
            Button answer = tall.GetVisualDescendants().OfType<Button>().First(b => b.IsDefault);
            Rect button = new(answer.TranslatePoint(default, tall)!.Value, answer.Bounds.Size);
            Assert.True(button.Bottom <= tall.ClientSize.Height && button.Height > 0,
                $"The question's {answer.Content} stands at {button}, below its window's {tall.ClientSize.Height:0.#} units: out of reach");
            Assert.Same(answer, tall.InputHitTest(button.Center) is Visual hit ? hit.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault() : null);
        }
        finally
        {
            tall.Close();
            large.Close();
            window.Close();
        }
    }

    /// <summary>A window of the IDE's that asks for whatever size it is given.</summary>
    private sealed class LargeWindow : IdeWindow
    {
        public LargeWindow()
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Content = new Border();
        }
    }

    /// <summary>Every window Passenger opens (the main window, a dialog, editors in a window, a tool window in its own)
    /// wears the one icon, AppIcon's, made from the icon file, whose own pictures at the sizes Windows draws a window's icon
    /// (16, 24 and 32 pixels) it takes as they are; and the file reads as one picture large enough for Linux's.</summary>
    [AvaloniaFact]
    public void Every_window_wears_the_icon_files_own_pictures_at_the_sizes_Windows_draws_never_one_scaled_down()
    {
        int[] sizes = PicturesIn(AppIcon.File);
        Assert.True(new[] { 16, 24, 32 }.All(sizes.Contains),
            $"The windows' icon holds pictures {string.Join(", ", sizes)} pixels wide, not 16, 24 and 32: Windows scales a larger one down to its title bar's 16 and taskbar's 24, blurred and fringed");
        using (Stream file = AssetLoader.Open(AppIcon.File))
        {
            using Avalonia.Media.Imaging.Bitmap picture = new(file);
            Assert.True(picture.PixelSize.Width >= 128,
                $"The icon file reads as a picture {picture.PixelSize} large, smaller than the 128 pixels Linux takes a window's icon at");
        }

        List<Window> opened = [];
        using IDisposable watching = Window.WindowOpenedEvent.AddClassHandler<Window>((shown, _) => opened.Add(shown));
        MainWindow window = Open(1200, 800);
        try
        {
            WearsTheIcon(window);
            Assert.True(window.Perform(AppActions.About), $"About could not run: {window.Status}");
            Ui.WaitFor(() => window.OwnedWindows.OfType<AboutDialog>().Any(w => w.IsVisible), "the About dialog");
            AboutDialog about = window.OwnedWindows.OfType<AboutDialog>().Single(w => w.IsVisible);
            WearsTheIcon(about);
            about.Close();
            window.OpenFile(project.PathOf("Notes.txt"));
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            Assert.True(window.Perform(AppActions.OpenInNewWindow), $"Open Tab in New Window could not run: {window.Status}");
            window.DockHost.Show(ToolWindowIds.Debug);
            window.DockHost.SetMode(window.DockHost[ToolWindowIds.Debug], Passenger.App.Docking.ToolMode.Windowed);
            Ui.Settle();
            Window[] apart = [.. opened.Where(w => w != window && w != about && w.IsVisible)];
            WearsTheIcon(apart.FirstOrDefault(w => w.GetVisualDescendants().OfType<Passenger.App.Editors.EditorGroup>().Any())
                ?? throw new Xunit.Sdk.XunitException("Open Tab in New Window opened no window of editors"));
            WearsTheIcon(apart.FirstOrDefault(w => w.GetVisualDescendants().Contains(window.DebugWindow))
                ?? throw new Xunit.Sdk.XunitException("The Debug window, set to a window of its own, opened none"));
        }
        finally
        {
            window.DockHost.SetMode(window.DockHost[ToolWindowIds.Debug], Passenger.App.Docking.ToolMode.Docked);
            foreach (Window other in opened.Where(w => w != window && w.IsVisible))
            {
                other.Close();
            }
            window.Close();
        }

        static void WearsTheIcon(Window shown) =>
            Assert.True(ReferenceEquals(AppIcon.Window, shown.Icon),
                $"The {shown.GetType().Name} \"{shown.Title}\" wears {(shown.Icon is null ? "no icon" : "an icon of its own")}, not the icon file's every window wears: Windows draws a picture scaled down in its title bar and taskbar");
    }

    /// <summary>The widths of the pictures an icon file holds, read from its directory: an entry of 16 bytes each, its first
    /// byte its width, 256 written as 0.</summary>
    private static int[] PicturesIn(Uri iconFile)
    {
        using Stream file = AssetLoader.Open(iconFile);
        using MemoryStream read = new();
        file.CopyTo(read);
        byte[] bytes = read.ToArray();
        Assert.True(bytes.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(bytes) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) == 1,
            $"{iconFile} is not an icon file, so Windows takes it as one picture and scales it");
        return [.. Enumerable.Range(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4))).Select(i => bytes[6 + 16 * i] is 0 ? 256 : (int)bytes[6 + 16 * i])];
    }

    /// <summary>Every corner Passenger rounds is one of the look's radii (Shell.axaml's kindBadge, tag and box on them), so
    /// either look's reach it.</summary>
    [Gate]
    [AvaloniaFact]
    public void Every_rounded_corner_is_one_of_the_looks_radii()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            Assert.True(window.Perform(AppActions.ShowGit), "Git did not show");
            Ui.WaitFor(() => window.DockHost.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.EndsWith("v1.0", StringComparison.Ordinal) == true && t.IsEffectivelyVisible), "the Git log's v1.0 tag");
            UnderEachLook(window, () =>
            {
                Radii(window, "the window with the Git log");
                SettingsDialog settings = window.ShowSettings();
                try
                {
                    settings.Page = SettingsDialog.KeymapPage;
                    Ui.Settle();
                    Radii(settings, "Settings' Keymap page");
                    settings.Page = SettingsDialog.McpPage;
                    Ui.Settle();
                    Radii(settings, "Settings' MCP Server page");
                }
                finally
                {
                    settings.Close();
                    Ui.Settle();
                }
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every control's own corners, and its template's parts', are one of the look's radii too: a style giving a
    /// button or a toggle a radius of its own (a tab's × and pin once had 3) is drawn by its template, which the rule over
    /// the window's Borders passes by. Held over a pinned editor tab, the Git log, the Unit Tests window's filters and NuGet's
    /// search, under each look.</summary>
    [AvaloniaFact]
    public void Every_controls_corner_in_its_template_is_one_of_the_looks_radii()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Notes.txt"));
            Assert.True(window.Perform(AppActions.PinTab), "Pin Tab did not run");
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                foreach (string shown in (string[])[AppActions.ShowGit, AppActions.ShowTests, AppActions.ShowNuGet])
                {
                    Assert.True(window.Perform(shown), $"{shown} did not show");
                    Ui.Settle();
                    Radii(window, $"the window with {shown}", templates: true);
                }
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Holds every Border Passenger made in a window (not a control template's) to the look's radii; with
    /// <paramref name="templates"/>, every control's own corners and its template's Borders and presenters instead.</summary>
    private static void Radii(Window window, string what, bool templates = false)
    {
        HashSet<double> radii = [0];
        foreach (string key in (string[])["ControlRadius", "ToolRadius", "PopupRadius", "CardRadius", "BadgeRadius", "ChipRadius", "TabRadius"])
        {
            if (window.TryFindResource($"Passenger.{key}", window.ActualThemeVariant, out object? found) && found is CornerRadius corners)
            {
                radii.UnionWith([corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft]);
            }
        }
        if (templates)
        {
            // The scrollbar's thumb is the look's own shape, rounder than its width allows a control's radius.
            if (window.TryFindResource(TokenKeys.ScrollThumbRadius, window.ActualThemeVariant, out object? thumb) && thumb is CornerRadius thumbs)
            {
                radii.Add(thumbs.TopLeft);
            }
            foreach (Visual drawn in window.GetVisualDescendants().Where(v => v.IsEffectivelyVisible))
            {
                (CornerRadius r, string kind) = drawn switch
                {
                    TemplatedControl control => (control.CornerRadius, control.GetType().Name),
                    Border { TemplatedParent: Control parent } part => (part.CornerRadius, $"{parent.GetType().Name}'s {part.Name ?? "Border"}"),
                    ContentPresenter { TemplatedParent: Control parent } part => (part.CornerRadius, $"{parent.GetType().Name}'s {part.Name ?? "presenter"}"),
                    _ => (default, string.Empty),
                };
                if (drawn is Control shown && !((double[])[r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft]).All(radii.Contains))
                {
                    Assert.Fail($"In {what}, a {string.Join(".", [kind, .. shown.Classes.Where(c => !c.StartsWith(':'))])} named '{AutomationProperties.GetName(shown)}' "
                        + $"has corners of {r}, none of the look's radii ({string.Join(", ", radii.Order())})");
                }
            }
            return;
        }
        foreach (Border border in window.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.TemplatedParent is null))
        {
            CornerRadius r = border.CornerRadius;
            Assert.True(((double[])[r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft]).All(radii.Contains),
                $"In {what}, a {string.Join(".", ["Border", .. border.Classes.Where(c => !c.StartsWith(':'))])} round '{(border.Child as TextBlock)?.Text ?? border.Child?.GetType().Name}' "
                + $"has corners of {r}, none of the look's radii ({string.Join(", ", radii.Order())})");
        }
    }

    /// <summary>The Debug window's toolbar down its left keeps every button inside the window; a window too short for them
    /// all puts the rest behind More, which lists them.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_debug_windows_toolbar_fits_inside_it_and_puts_what_has_no_room_behind_More()
    {
        MainWindow window = Open(1400, 880);
        try
        {
            Assert.True(window.Perform(AppActions.ShowDebug), "Debug did not show");
            Ui.Settle();
            Passenger.App.ToolWindows.DebugView debug = window.DockHost.GetVisualDescendants().OfType<Passenger.App.ToolWindows.DebugView>().First(v => v.IsEffectivelyVisible);
            // The window's own side bar, not its console's.
            Passenger.App.ToolWindows.ToolColumn tools = debug.GetVisualDescendants().OfType<Passenger.App.ToolWindows.ToolColumn>().Single(c => c.Parent?.Parent == debug);
            // A button with no room is put above the column, which clips it away.
            static bool Drawn(Control button) => button.IsVisible && button.Bounds.Bottom > 0;
            void Fits()
            {
                Rect room = Placed(window, debug);
                foreach (Button button in tools.Children.OfType<Button>().Where(Drawn))
                {
                    Rect at = Placed(window, button);
                    Assert.True(at.Bottom <= room.Bottom + 0.5, $"The Debug window's {ToolTip.GetTip(button)} reaches {at.Bottom:0.#}, past the window's foot at {room.Bottom:0.#}");
                }
            }
            Fits();
            Assert.Equal(tools.Overflowing.Count > 0, Drawn(tools.More));

            // The bottom tool windows dragged short, as a small screen leaves them.
            window.DockHost.Apply(window.DockHost.Capture() with { BottomHeight = 150 });
            Ui.Settle();
            Fits();
            Assert.NotEmpty(tools.Overflowing);
            Assert.True(Drawn(tools.More), "The Debug window's toolbar has buttons with no room and no More to reach them");
            // Each by its name, the keys its tip names in the menu's column of keys.
            (string Name, bool Keyed)[] cut = [.. tools.Overflowing.Select(b => (AutomationProperties.GetName(b)!, ToolTip.GetTip(b) is string tip && tip.EndsWith(')')))];
            Ui.Click(tools.More);
            ContextMenu menu = window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault(m => m.IsOpen)
                ?? throw new Xunit.Sdk.XunitException("More opened no menu of the buttons it holds");
            Assert.Equal(cut, menu.Items.OfType<MenuItem>().Select(i => ((string)i.Header!, i.InputGesture is not null)));
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every tool window's toolbar buttons are made one way (ToolButtons): each icon of the one size every icon has,
    /// and none taking the keys from the list or editor it acts on; only a glyph's (ToolButtons.Glyph: the frame's own Options
    /// and Hide in a header, a row's ×) is smaller, and one inside a field (a list's Sort and Filter) a row icon's.</summary>
    [Gate]
    [AvaloniaFact]
    public void Every_tool_windows_toolbar_buttons_have_the_one_icon_size_and_never_take_the_keys()
    {
        MainWindow window = Open(1400, 880);
        try
        {
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            int seen = 0;
            foreach (Passenger.App.Docking.ToolWindow tool in window.DockHost.Windows.ToList())
            {
                window.DockHost.Show(tool.Id);
                Ui.Settle();
                foreach (Button button in ToolWindowParts(window).OfType<Button>().Where(b => b.Classes.Contains(StyleClasses.Tool) && b.Bounds.Width > 0))
                {
                    if (button.GetVisualDescendants().OfType<PathIcon>().FirstOrDefault() is not { } icon)
                    {
                        continue;
                    }
                    seen++;
                    double size = icon.Classes.Contains(StyleClasses.Small) ? RowIcon.GlyphSize : button.Classes.Contains(StyleClasses.InField) ? RowIcon.Size : 16;
                    string what = $"{tool.Title}'s {ToolTip.GetTip(button)}";
                    Assert.False(button.Focusable, $"{what} takes the keys when clicked, where a toolbar's button leaves them with what it acts on");
                    Assert.True(icon.Bounds.Width == size && icon.Bounds.Height == size,
                        $"{what} has an icon {icon.Bounds.Width:0.#} by {icon.Bounds.Height:0.#}, where every toolbar icon is {size}");
                }
            }
            Assert.True(seen >= 20, $"Only {seen} toolbar buttons were found in the tool windows");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every button a person meets in the window and its tool windows has words a screen reader says: its own, or a
    /// name given where it was made (<see cref="ToolButtons.Tip"/>), never only an icon. Its peer's name is what each
    /// system's accessibility reads, so an icon's button left with a tip alone is heard as a nameless button.</summary>
    [AvaloniaFact]
    public void No_button_in_the_window_or_a_tool_window_goes_unnamed()
    {
        MainWindow window = Open(1400, 880);
        try
        {
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            window.Perform(AppActions.Find);
            Ui.Settle();
            List<string> unnamed = [];
            HashSet<Button> seen = [];
            foreach (Passenger.App.Docking.ToolWindow tool in window.DockHost.Windows.ToList())
            {
                window.DockHost.Show(tool.Id);
                Ui.Settle();
                foreach (Button button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0).Where(seen.Add))
                {
                    if (string.IsNullOrWhiteSpace(Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(button).GetName()))
                    {
                        unnamed.Add($"{tool.Title}'s {button.GetType().Name} {button.Name} tip \"{ToolTip.GetTip(button)}\" in {button.GetVisualParent()?.GetType().Name}");
                    }
                }
            }
            Assert.True(seen.Count >= 40, $"Only {seen.Count} buttons were found in the window and its tool windows");
            Assert.True(unnamed.Count == 0, $"{unnamed.Count} buttons are nameless to a screen reader: give each ToolButtons.Tip or AutomationProperties.Name:\n{string.Join("\n", unnamed)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every field a person meets in the window, its tool windows and every page of Settings has a name a screen
    /// reader says (its own, its label's, a search's), never a placeholder alone, and stands as tall as the buttons and
    /// drop-downs beside it: the one field look, but a field of several lines, a value typed in its row (ValueField) and
    /// a table's cell.</summary>
    [AvaloniaFact]
    public void Every_field_in_the_window_its_tool_windows_and_Settings_is_named_and_a_controls_height()
    {
        MainWindow window = Open(1400, 880);
        SettingsDialog? settings = null;
        try
        {
            double height = (double)window.FindResource(TokenKeys.ControlHeight)!;
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            window.Perform(AppActions.Find);
            Ui.Settle();
            List<string> faults = [];
            HashSet<TextBox> seen = [];
            void Check(string where, Visual within)
            {
                foreach (TextBox box in within.GetVisualDescendants().OfType<TextBox>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0 && b.TemplatedParent is null).Where(seen.Add))
                {
                    string shown = $"{where}'s {box.GetType().Name} {box.Name} \"{box.PlaceholderText}\" in {box.GetVisualParent()?.GetType().Name}";
                    if (string.IsNullOrWhiteSpace(Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(box).GetName()))
                    {
                        faults.Add($"{shown}: nameless to a screen reader");
                    }
                    if (!box.AcceptsReturn && box is not ValueField && !box.Classes.Contains(StyleClasses.Cell) && Math.Abs(box.Bounds.Height - height) > 0.5)
                    {
                        faults.Add($"{shown}: {box.Bounds.Height:0.#} tall");
                    }
                }
            }
            foreach (Passenger.App.Docking.ToolWindow tool in window.DockHost.Windows.ToList())
            {
                window.DockHost.Show(tool.Id);
                Ui.Settle();
                Check(tool.Title, window);
            }
            settings = window.ShowSettings();
            Ui.Settle();
            foreach (string page in settings.Pages.ToList())
            {
                settings.Page = page;
                Ui.Settle();
                Check($"Settings › {page}", settings);
            }
            Assert.True(seen.Count >= 20, $"Only {seen.Count} fields were found in the window, its tool windows and Settings");
            Assert.True(faults.Count == 0, $"{faults.Count} fields are nameless or stand at a height of their own: give each a name (its label through "
                + $"FormRows or AutomationProperties.LabeledBy, a search through SearchBox) and leave its height the base TextBox's:\n{string.Join("\n", faults)}");
        }
        finally
        {
            settings?.Close();
            Ui.Settle();
            window.Close();
        }
    }

    /// <summary>Every button that runs one of the window's actions, the toolbar's and every tool window's, names in its tip
    /// (a link offering one, in its words) the keys the keymap in force gives that action (<see cref="ActionTips"/>), and
    /// every tool window's Hide names Hide Tool Window's keys or none: under every keymap Settings offers, each chosen in
    /// turn, so no tip names a key that does not run what it says.</summary>
    [AvaloniaFact]
    public void Every_action_buttons_tip_names_the_keys_the_keymap_in_force_gives_its_action()
    {
        MainWindow window = Open(1400, 880);
        try
        {
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            foreach (Passenger.Editor.KeymapPreset preset in SettingsDialog.KeymapChoices)
            {
                window.ApplySettings(window.Settings with { Keymap = preset });
                Ui.Settle();
                Passenger.Editor.Keymap keymap = AppActions.Keymap();
                string? KeysOf(string action) => keymap.ShortcutsFor(AppActions.KeysIn(action)).FirstOrDefault()?.Display();
                HashSet<Button> seen = [];
                int hides = 0;
                foreach (Passenger.App.Docking.ToolWindow tool in window.DockHost.Windows.ToList())
                {
                    window.DockHost.Show(tool.Id);
                    Ui.Settle();
                    foreach (Button button in window.GetVisualDescendants().OfType<Button>().Where(seen.Add))
                    {
                        string tip = ToolTip.GetTip(button) as string ?? string.Empty;
                        if (button.Name == AppActions.HideToolWindow)
                        {
                            hides++;
                            string? hide = KeysOf(AppActions.HideToolWindow);
                            Assert.True(tip == ActionTips.Named("Hide", hide),
                                $"Under {preset}'s keymap, {tool.Title}'s Hide says \"{tip}\", where the keys that hide it are {hide ?? "none"}");
                        }
                        else if ((ActionButtons.GetAction(button) ?? ActionTips.ActionOf(button)) is { } action && KeysOf(action) is { } keys)
                        {
                            // A link offering an action names its keys in its own words (an empty state's).
                            string said = button.Classes.Contains(StyleClasses.Link) ? button.Content as string ?? tip : tip;
                            Assert.True(said.Contains($"({keys})", StringComparison.Ordinal),
                                $"Under {preset}'s keymap, {tool.Title}'s button for {action} says \"{said}\", never the {keys} that runs it");
                        }
                    }
                }
                Assert.True(hides >= 4, $"Only {hides} tool windows' Hide buttons were found under {preset}'s keymap");
                Button run = Named<Button>(window, "RunButton");
                Assert.True(seen.Contains(run) && ToolTip.GetTip(run) is string runTip && runTip.Contains($"({KeysOf(AppActions.RunProgram)})", StringComparison.Ordinal),
                    $"The toolbar's Run says \"{ToolTip.GetTip(run)}\" under {preset}'s keymap");
            }
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>Every tool window's Refresh, Expand All, Collapse All and New Session is one of the window's actions, as Find
    /// Action and the keymap reach it: a button doing one of those that runs no action fails here, naming its window.</summary>
    [AvaloniaFact]
    public void Every_tool_windows_refresh_expand_collapse_and_new_session_button_is_one_of_the_windows_actions()
    {
        string[] kinds = ["Refresh", "Read the .http files again", "Expand All", "Collapse All", "New Session"];
        MainWindow window = Open(1400, 880);
        try
        {
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            List<string> bare = [];
            HashSet<string> seen = [];
            foreach (Passenger.App.Docking.ToolWindow tool in window.DockHost.Windows.ToList())
            {
                window.DockHost.Show(tool.Id);
                Ui.Settle();
                foreach (Button button in ToolWindowParts(window).OfType<Button>().Where(b => b.Classes.Contains(StyleClasses.Tool)))
                {
                    if (ToolTip.GetTip(button) is not string tip || kinds.FirstOrDefault(k => tip.StartsWith(k, StringComparison.Ordinal)) is not { } kind)
                    {
                        continue;
                    }
                    seen.Add(kind);
                    if (button.Name is not { } name || window.Actions[name] is null || Passenger.App.ActionButtons.GetAction(button) != name)
                    {
                        bare.Add($"{tool.Title}'s {tip}");
                    }
                }
            }
            Assert.True(bare.Count == 0, $"These buttons run no action, so Find Action, the keymap and agents cannot reach them: {string.Join(", ", bare)}");
            Assert.True(seen.Count == kinds.Length, $"Only {string.Join(", ", seen)} were found among the tool windows' buttons");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Words the window shows that trail off end in the one ellipsis, "…", however they are written: a status,
    /// a console's line, a note. An agent's words (Mcp/) and code shown as code (GDScript's varargs, a YAML document's end)
    /// are not the window's words; three dots inside words ("do...while") are not their end.</summary>
    [Fact]
    public void No_words_the_window_shows_trail_off_in_three_dots()
    {
        Regex trailing = new(@"[A-Za-z ]\.\.\.(?:\\n)?""");
        string[] found = [.. from file in UiFiles()
            where !file.Path.Contains("/Mcp/", StringComparison.Ordinal)
            from line in file.Text.Split('\n').Select((text, i) => (Text: text, Number: i + 1))
            where !line.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && trailing.IsMatch(line.Text)
            select $"{file.Path}:{line.Number}: {line.Text.Trim()}"];
        Assert.True(found.Length == 0, $"Words that trail off in three dots, where every one ends in the one ellipsis, '…':\n{string.Join("\n", found)}");
    }

    /// <summary>A label that asks more before it acts ends in one ellipsis everywhere, "…": every action's title, as the
    /// menus, Find Action and the keymap list it and as a menu opened where it runs words it, macOS's application menu, a
    /// row's menu in the Explorer, and every button and menu item the tool windows show.</summary>
    [Gate]
    [AvaloniaFact]
    public void Every_label_that_asks_more_ends_in_the_one_ellipsis_never_three_dots()
    {
        MainWindow window = Open(1400, 880);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            List<string> dotted = [];
            void Check(string where, string? words)
            {
                if (words?.Contains("...", StringComparison.Ordinal) == true)
                {
                    dotted.Add($"{where}: '{words}'");
                }
            }
            ActionContext here = window.Actions.Context();
            foreach (IdeAction action in window.Actions.All)
            {
                Check(action.Id, action.Title);
                Check($"{action.Id} in the editor", action.TitleWhere(here));
            }
            foreach (NativeMenuItem item in MainWindow.ApplicationMenu is { } application ? Native(application) : [])
            {
                Check("the application menu", item.Header);
            }
            // A project's row: its menu has the most of the Explorer's.
            window.ExplorerView.Reveal(Passenger.Core.PathNames.Canonical(project.PathOf("Hello.csproj")));
            Ui.Settle();
            window.ExplorerView.Rows().RaiseEvent(new ContextRequestedEventArgs());
            Ui.Settle();
            ContextMenu menu = window.ExplorerView.OpenMenu ?? throw new Xunit.Sdk.XunitException("No menu opened on the project's row");
            foreach (MenuItem item in Items(menu.Items.OfType<MenuItem>()))
            {
                Check("the Explorer's menu", item.Header as string);
            }
            menu.Close();
            Ui.Settle();
            foreach (Passenger.App.Docking.ToolWindow tool in window.DockHost.Windows.ToList())
            {
                window.DockHost.Show(tool.Id);
                Ui.Settle();
                foreach (Visual part in ToolWindowParts(window))
                {
                    Check($"{tool.Title}'s window", part switch
                    {
                        Button { Content: string words } => words,
                        MenuItem { Header: string words } => words,
                        Control control => ToolTip.GetTip(control) as string,
                        _ => null,
                    });
                }
            }
            Assert.True(dotted.Count == 0, $"Labels with three dots, where every label has the one ellipsis, '…': {string.Join("; ", dotted)}");
        }
        finally
        {
            window.Close();
        }

        static IEnumerable<NativeMenuItem> Native(NativeMenu menu) =>
            menu.Items.OfType<NativeMenuItem>().SelectMany(i => (IEnumerable<NativeMenuItem>)[i, .. i.Menu is { } sub ? Native(sub) : []]);

        static IEnumerable<MenuItem> Items(IEnumerable<MenuItem> items) =>
            items.SelectMany(i => (IEnumerable<MenuItem>)[i, .. Items(i.Items.OfType<MenuItem>())]);
    }

    [Gate]
    [AvaloniaFact]
    public void At_the_smallest_width_the_status_bars_path_never_draws_under_its_widgets()
    {
        MainWindow window = Open(MainWindow.SmallestWidth, MainWindow.SmallestHeight);
        try
        {
            window.OpenFile(project.PathOf(Project.Deep));
            Ui.Settle();
            Rect widgets = Placed(window, (Visual)Named<Button>(window, "CaretButton").Parent!);
            Button[] crumbs = [.. Named<Control>(window, "BreadcrumbHost").GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)];
            Assert.NotEmpty(crumbs);
            foreach (Button crumb in crumbs)
            {
                Rect seen = Visible(window, crumb);
                Assert.True(seen.Width <= 0 || seen.Right <= widgets.Left + 0.5,
                    $"The status bar's path shows {crumb.Content} under its widgets ({seen.Left:0.#} to {seen.Right:0.#}, the widgets from {widgets.Left:0.#})");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Narrow panes cut no word mid-letter and draw nothing over anything, each held where it comes from: a tool
    /// window's header gives its tabs' room up before its title's and never lays them under its buttons, a tab's name ends in
    /// an ellipsis, the Git log's files end theirs, a sideways scrollbar lies under the last row rather than over it and a
    /// text box's upright one beside its lines.</summary>
    [Gate]
    [AvaloniaFact]
    public void In_narrow_panes_words_with_no_room_end_in_an_ellipsis_and_nothing_draws_over_anything()
    {
        MainWindow window = Open(MainWindow.SmallestWidth, 768);
        try
        {
            window.OpenFile(project.PathOf(Project.Deep));
            Ui.Settle();
            UnderEachLook(window, () => NarrowPanes(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void NarrowPanes(MainWindow window)
    {
        // Two programs debugged, each a tab in the Debug window's header, wider together than its room.
        System.Collections.ObjectModel.ObservableCollection<Passenger.App.ToolWindows.DebugTab> sessions = window.BottomPanel.Debug.Sessions;
        // The second comes once the first is shown, as a second program's start brings it.
        sessions.Add(new Passenger.App.ToolWindows.DebugTab("Greenhouse.AppHost: apiservice", new Passenger.App.ToolWindows.OutputView(toolbar: false)));
        try
        {
            Assert.True(window.Perform(AppActions.ShowDebug), "Debug did not show");
            Ui.Settle();
            sessions.Add(new Passenger.App.ToolWindows.DebugTab("Greenhouse.AppHost: sensors", new Passenger.App.ToolWindows.OutputView(toolbar: false)));
            Ui.Settle();
            Visual debug = PaneOf(window.DebugWindow);
            FitsItsRoom(window, debug, "The Debug window");
            ToggleButton[] tabs = [.. debug.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains(StyleClasses.PanelTab) && Words(t).Contains("Greenhouse", StringComparison.Ordinal))];
            Assert.True(tabs.Length == 2 && tabs.All(t => Visible(window, t).Width >= 40),
                $"The Debug window shows {tabs.Count(t => Visible(window, t).Width >= 40)} of its two programs' tabs in its header's room");
            Rect room = Placed(window, (Visual)tabs[0].GetVisualAncestors().OfType<ContentControl>().First(c => c.ClipToBounds));
            Assert.True(Placed(window, tabs[1]).Right >= room.Right - 1,
                $"The Debug window's tabs end at {Placed(window, tabs[1]).Right:0.#}, their names cut, though the header has room to {room.Right:0.#}");
        }
        finally
        {
            sessions.Clear();
        }

        // The Git log's one commit chosen beside a short, narrow log: its files, one deep in folders, and its details, which scroll.
        window.DockHost.Apply(window.DockHost.Capture() with { BottomHeight = 300 });
        Assert.True(window.Perform(AppActions.ShowGit), "Git did not show");
        Passenger.App.ToolWindows.GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Count == 1, "the log's commit");
        Ui.Click(log.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "First" && t.IsEffectivelyVisible).FindAncestorOfType<ListBoxItem>()!);
        Ui.Landed(() => log.Files.Count >= 4 && log.Details.Contains("tests@passenger.invalid", StringComparison.Ordinal), "the commit's files and details");
        FitsItsRoom(window, PaneOf(log), "The Git window");
        TextBlock? deep = log.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text?.EndsWith("Processing", StringComparison.Ordinal) == true);
        Assert.True(deep is not null && deep.TextTrimming == TextTrimming.CharacterEllipsis,
            $"The Git log's file row {(deep is null ? "of RefundCalculator.cs is not shown" : "cuts its folder rather than ending it in an ellipsis")}");
        Scrollbars(window);
        window.DockHost.RestoreDefault();
        Ui.Settle();
    }

    /// <summary>At the window's least width every tool window, shown in turn where its layout docks it, cuts no word and
    /// draws nothing over anything, in it or round it (the toolbar, the stripes, the status bar), at display scales 1 and
    /// 2, dark and light, under each look; each finding is fixed where it comes from, never in the one window.</summary>
    [AvaloniaFact]
    public void At_the_least_width_every_tool_window_cuts_no_word_and_lays_nothing_over_anything()
    {
        MainWindow window = Open(MainWindow.SmallestWidth, MainWindow.SmallestHeight + 268);
        List<string> found = [];
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                foreach (ThemeChoice variant in (ThemeChoice[])[ThemeChoice.Dark, ThemeChoice.Light])
                {
                    window.ApplySettings(window.Settings with { Theme = variant });
                    foreach (double scale in (double[])[1, 2])
                    {
                        window.SetRenderScaling(scale);
                        Ui.Settle();
                        foreach (Passenger.App.Docking.ToolWindow tool in window.DockHost.Windows.ToList())
                        {
                            window.DockHost.Show(tool.Id);
                            Ui.Settle();
                            found.AddRange(LayoutFaults.Of(window, window, $"Under {window.Settings.Look?.Name ?? CarriedThemes.Own}, {variant}, at scale {scale}, with {tool.Title} shown, the window"));
                        }
                    }
                }
            });
        }
        finally
        {
            window.SetRenderScaling(1);
            window.ApplySettings(new AppSettings());
            window.Close();
        }
        Assert.True(found.Count == 0, $"At the window's least width:\n{string.Join("\n", found.Distinct().Take(60))}");
    }

    /// <summary>The layout faults' check finds what it is for, so its silence means something: words wider than their room
    /// with no ellipsis, laid past a row narrower than them, past their part's room or past the window's edge by a row
    /// wider than it; a fixed-width field past a scroller that scrolls no way sideways, a field squeezed to too little
    /// room to type in, and two buttons in one cell, are each found; words ending in an ellipsis, and a field's own
    /// options inside it, are not.</summary>
    [AvaloniaFact]
    public void The_layout_faults_check_finds_words_cut_and_controls_drawn_over_each_other()
    {
        TextBlock cut = new() { Text = "Words far wider than their room", Width = 60 };
        TextBlock trimmed = new() { Text = "Words far wider than their room", Width = 60, TextTrimming = TextTrimming.CharacterEllipsis };
        Border held = new()
        {
            Width = 60, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "Words wider than their row" } } },
        };
        ShrinkingRow parts = new()
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                new Border
                {
                    Width = 60,
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "Words wider than their part" } } } } },
                },
            },
        };
        ScrollViewer narrow = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Content = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left } };
        TextBox squeezed = new() { Width = 40, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Left };
        StackPanel wider = new()
        {
            Orientation = Orientation.Horizontal, Width = 800, HorizontalAlignment = HorizontalAlignment.Left,
            Children = { new TextBlock { Text = "Words past the window", Margin = new Thickness(500, 0, 0, 0) } },
        };
        Grid laidOver = new() { Children = { new Button { Content = "First" }, new Button { Content = "Second" } } };
        Window window = new() { Width = 400, Height = 300, Content = new StackPanel { Children = { cut, trimmed, held, parts, narrow, squeezed, wider, laidOver } } };
        window.Show();
        try
        {
            Ui.Settle();
            List<string> found = LayoutFaults.Of(window, window, "The test window");
            Assert.True(found.Count(f => f.Contains("cuts", StringComparison.Ordinal)) == 1 && found.Count(f => f.Contains("past the side", StringComparison.Ordinal)) == 2
                && found.Any(f => f.Contains("their part", StringComparison.Ordinal)) && found.Count(f => f.Contains("past the edge", StringComparison.Ordinal)) == 1
                && found.Count(f => f.Contains("to type in", StringComparison.Ordinal)) == 1 && found.Count(f => f.Contains("past the window's edge", StringComparison.Ordinal)) == 1
                && found.Any(f => f.Contains("one over the other", StringComparison.Ordinal)),
                $"The check found {found.Count} faults where one cut word, words laid past their row, words laid past their part's room, a field past its scroller's edge and two buttons laid over each other stand:\n{string.Join("\n", found)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A row of parts lays itself out again as a part grows, however the row was measured: a status widget's counts
    /// (Git's incoming and outgoing), then a widget's name (a run configuration's), growing in a full ShrinkingRow, whose
    /// last widget gives way for them, in one measured with no bound, and in a tool window's bar (ToolFlow) alike, are each
    /// given the room they now ask, the name shown whole and no word drawn past its room (LayoutFaults), where a
    /// ShrinkingRow held the counts' widget to its old width, unheard, and drew them past it.</summary>
    [AvaloniaFact]
    public void A_row_lays_itself_out_again_as_a_part_grows_whether_full_or_measured_with_no_bound()
    {
        List<(string What, Panel Row, TextBlock Name, TextBlock Count, Control? Last)> rows = [];
        StackPanel shown = new() { Spacing = Gaps.Wide };
        foreach (string what in (string[])["A full row", "A row measured with no bound", "A tool window's bar", "A tool window's bar measured with no bound"])
        {
            bool flows = what.StartsWith("A tool window's bar", StringComparison.Ordinal);
            bool bounded = !what.EndsWith("no bound", StringComparison.Ordinal);
            TextBlock name = new() { Text = "Debug", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Button widget = new()
            {
                Classes = { StyleClasses.Widget }, MinWidth = 52,
                Content = new DockPanel { Children = { new PathIcon { Classes = { StyleClasses.Chevron }, Data = Icon(IconKeys.ChevronDown), [DockPanel.DockProperty] = Dock.Right }, name } },
            };
            TextBlock count = new() { Text = "9", Classes = { StyleClasses.Status } };
            Button counts = new()
            {
                Classes = { StyleClasses.StatusItem },
                // As the status bar's widgets, which never give way.
                [ShrinkingRow.KeepsWidthProperty] = true,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = Gaps.Normal,
                    Children = { Counted(new TextBlock { Text = "2", Classes = { StyleClasses.Status } }), Counted(count) },
                },
            };
            Button? last = flows ? null : new()
            {
                Classes = { StyleClasses.Widget }, MinWidth = 76,
                Content = new TextBlock { Text = "feature/a-branch-whose-name-is-long-enough-to-give-way-for-the-others", TextTrimming = TextTrimming.CharacterEllipsis },
            };
            Panel row = flows ? new Passenger.App.ToolWindows.ToolFlow { Children = { widget, counts } } : new ShrinkingRow { Children = { widget, counts, last! } };
            rows.Add((what, row, name, count, last));
            shown.Children.Add(bounded ? new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = row } : new StackPanel { Orientation = Orientation.Horizontal, Children = { row } });
        }
        Window window = new() { Width = 900, Height = 400, Content = shown };
        window.Show();
        try
        {
            Ui.Settle();
            // The full row held 40 px short of what its parts ask, so its last widget has given way; the bar given room to grow in.
            foreach (Panel row in rows.Select(r => r.Row).Where(r => r.Parent is Border))
            {
                double asked = row.Children.Sum(c => c.DesiredSize.Width) + ((row as Passenger.App.ToolWindows.ToolFlow)?.Spacing ?? 0) * (row.Children.Count - 1);
                ((Border)row.Parent!).Width = asked + (row is ShrinkingRow ? -40 : 160);
            }
            Ui.Settle();
            // The counts grow first, alone: a name growing is heard by chance where its ellipsis moves its width.
            List<string> wrong = [];
            double[] before = [.. rows.Select(r => r.Last?.Bounds.Width ?? 0)];
            foreach ((_, _, _, TextBlock count, _) in rows)
            {
                count.Text = "1000";
            }
            Ui.Settle();
            Grew("its counts");
            before = [.. rows.Select(r => r.Last?.Bounds.Width ?? 0)];
            foreach ((_, _, TextBlock name, _, _) in rows)
            {
                name.Text = "Debug with every test";
            }
            Ui.Settle();
            Grew("its widget's name");
            foreach ((string what, _, TextBlock name, _, _) in rows.Where(r => r.Name.TextLayout.TextLines.Any(l => l.HasCollapsed)))
            {
                wrong.Add($"{what} cuts the widget's grown name to {name.Bounds.Width:0.#} px of its {name.TextLayout.WidthIncludingTrailingWhitespace:0.#}, where the row gives it the room it now asks");
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));

            // What growing did to each row: the full row's last widget gave way for it, and no word stands past its room.
            void Grew(string what)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    (string row, Panel panel, _, _, Control? last) = rows[i];
                    if (last is not null && panel.Parent is Border && last.Bounds.Width > before[i] - 1)
                    {
                        wrong.Add($"{row}, {what} grown, left its last widget {last.Bounds.Width:0.#} px wide, as before, where it gives way for them");
                    }
                    wrong.AddRange(LayoutFaults.Of(window, panel, $"{row}, {what} grown,"));
                }
            }
        }
        finally
        {
            window.Close();
        }

        // A count after its icon, as the status bar's Git counts stand.
        static StackPanel Counted(TextBlock count) => new()
        {
            Orientation = Orientation.Horizontal, Spacing = Gaps.Tight,
            Children = { new PathIcon { Width = 11, Height = 11, Data = Icon(IconKeys.ArrowDown) }, count },
        };

        static Geometry Icon(string key) => (Geometry)Application.Current!.FindResource(key)!;
    }

    /// <summary>A field's placeholder too long for it ends in an ellipsis, where the field's scroller, which only follows
    /// its caret, would have cut it mid-letter; and the layout check finds a word cut inside a scroller the person can't
    /// scroll to it: a placeholder laid out wider than its field, as every one was, words wider than a scroller that
    /// scrolls no way sideways, and words wider than a menu's scroller, which has no sideways bar. A field's own typed
    /// text, which its caret scrolls to, and words in a scroller with its bar, are not cuts.</summary>
    [AvaloniaFact]
    public void A_fields_long_placeholder_ends_in_an_ellipsis_and_the_check_finds_one_cut_inside_its_scroller()
    {
        const string Long = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        TextBox field = new() { Width = 160, PlaceholderText = Long, HorizontalAlignment = HorizontalAlignment.Left };
        TextBox typed = new() { Width = 160, Text = Long, HorizontalAlignment = HorizontalAlignment.Left };
        TextBox scrolledPlaceholder = new() { Width = 160, PlaceholderText = Long, HorizontalAlignment = HorizontalAlignment.Left };
        // The field's scroller scrolling sideways while its placeholder shows, as every field's did.
        scrolledPlaceholder.Styles.Add(new Style(x => x.OfType<TextBox>().Class(":empty").Template().OfType<ScrollViewer>().Name("PART_ScrollViewer"))
        {
            Setters = { new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden) },
        });
        ScrollViewer barred = new()
        {
            Width = 160, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalAlignment = HorizontalAlignment.Left,
            Content = new TextBlock { Text = "Words in a scroller with its bar, wider than it" },
        };
        ScrollViewer unscrolled = new()
        {
            Width = 160, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalAlignment = HorizontalAlignment.Left,
            Content = new TextBlock { Text = "Words in a scroller that scrolls no way sideways", Width = 400, HorizontalAlignment = HorizontalAlignment.Left },
        };
        ScrollViewer menus = new()
        {
            Width = 160, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalAlignment = HorizontalAlignment.Left,
            Theme = (ControlTheme)Application.Current!.FindResource("FluentMenuScrollViewer")!,
            Content = new TextBlock { Text = "Words in a menu's scroller, which has no sideways bar" },
        };
        Window window = new() { Width = 400, Height = 400, Content = new StackPanel { Spacing = 8, Children = { field, typed, scrolledPlaceholder, barred, unscrolled, menus } } };
        window.Show();
        try
        {
            Ui.Settle();
            TextBlock placeholder = field.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "PART_Placeholder");
            Assert.True(placeholder.TextLayout.TextLines.Any(l => l.HasCollapsed) && placeholder.Bounds.Width <= field.Bounds.Width,
                $"A field {field.Bounds.Width:0} px wide lays its placeholder out {placeholder.Bounds.Width:0} px wide, untrimmed: it is cut mid-letter at the field's edge, where words with no room end in an ellipsis");
            List<string> found = LayoutFaults.Of(window, window, "The test window");
            string[] cut = [.. found.Where(f => f.Contains("scroller", StringComparison.Ordinal))];
            Assert.True(cut.Length == 3 && cut.Any(f => f.Contains(Long, StringComparison.Ordinal)) && cut.Any(f => f.Contains("scrolls no way", StringComparison.Ordinal))
                && cut.Any(f => f.Contains("no sideways bar", StringComparison.Ordinal)) && found.Count == cut.Length,
                $"The check found {found.Count} faults where a placeholder cut inside its field's scroller, words cut by a scroller that scrolls no way sideways and words cut by a menu's scroller stand, and nothing else:\n{string.Join("\n", found)}");

            // Typed into, the field scrolls again to keep its caret, at the end of the words, in view.
            Ui.WaitFor(() => field.Focus() && field.IsFocused, "the field to take the keys");
            window.KeyTextInput(Long);
            Ui.Settle();
            ScrollViewer scroller = field.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "PART_ScrollViewer");
            Assert.True(field.Text == Long && scroller.Extent.Width > scroller.Viewport.Width && scroller.Offset.X >= scroller.Extent.Width - scroller.Viewport.Width - 1,
                $"Typed into, a field {scroller.Viewport.Width:0} px wide holding words {scroller.Extent.Width:0} px wide stands scrolled {scroller.Offset.X:0} px: its caret at their end is out of sight");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A row of a list of what runs (RunRows) narrower than the parts at its right gives them way from the last,
    /// their words ending in an ellipsis, where they ran past the list's edge, which scrolls no way sideways.</summary>
    [AvaloniaFact]
    public void A_narrow_run_row_gives_way_at_its_right_rather_than_cutting_its_words()
    {
        Passenger.App.ToolWindows.RunRow drawn = new(0, "master")
        {
            State = Passenger.App.ToolWindows.RunState.Stopped, Health = "healthy", Links = [new("58370→5200", null), new("58371→4433/udp", null)], Trailing = "exited(2)",
        };
        ListBox list = new() { Width = 150, ItemsSource = new System.Collections.ObjectModel.ObservableCollection<Passenger.App.ToolWindows.RunItem<Passenger.App.ToolWindows.RunRow>> { new("master", drawn) } };
        Passenger.App.ToolWindows.RunRows.Use<Passenger.App.ToolWindows.RunRow>(list, row => row, _ => { }, _ => { }, "port");
        Window window = new() { Width = 400, Height = 200, Content = list };
        window.Show();
        try
        {
            Ui.Settle();
            Assert.True(list.GetVisualDescendants().OfType<Passenger.App.ToolWindows.RunRowView>().Any(), "The list draws no run row");
            List<string> found = LayoutFaults.Of(window, window, "A run row 150 px wide");
            Assert.True(found.Count == 0, string.Join("\n", found));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The tool window a view is shown in, its header and all.</summary>
    private static Visual PaneOf(Visual view) =>
        view.GetVisualAncestors().OfType<DockPanel>().FirstOrDefault(p => p.Classes.Contains(StyleClasses.ToolPane))
            ?? throw new Xunit.Sdk.XunitException($"The {view.GetType().Name} is in no tool window");

    /// <summary>A part of a window kept as every part is however narrow: words wider than their room end in an ellipsis
    /// rather than being cut, and nothing a person reads or presses lies over another, each as far as what clips it shows
    /// it.</summary>
    internal static void FitsItsRoom(Visual window, Visual part, string what)
    {
        List<string> found = LayoutFaults.Of(window, part, what);
        Assert.True(found.Count == 0, string.Join("\n", found));
    }

    /// <summary>A greyed button shows its tooltip under the pointer, which says why it is greyed: Live Templates' Delete,
    /// greyed for a built-in template, says how one is turned off instead.</summary>
    [AvaloniaFact]
    public void A_greyed_button_shows_its_tooltip_under_the_pointer()
    {
        MainWindow window = Open(1200, 800);
        SettingsDialog? dialog = null;
        try
        {
            dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Query = "live templates";
            Ui.Settle();
            Assert.True(dialog.Templates.Choose("cw"), "The Live Templates page does not list cw");
            Ui.Settle();
            Button delete = dialog.Templates.DeleteButton;
            Assert.False(delete.IsEnabled, "Delete is not greyed for cw, a built-in template");
            Rest(dialog, delete, "A built-in template is turned off by its check box, so it can be turned on again");
        }
        finally
        {
            dialog?.Close();
            window.Close();
        }
    }

    /// <summary>A greyed check box shows its tooltip under the pointer: in NuGet's Sources, a feed of the machine's
    /// nuget.config, which NuGet never writes, has its check box greyed, its tip saying what it is and why it is greyed, a
    /// screen reader naming it all the same. On Linux alone, where NuGet takes the machine's folder from the run
    /// (NUGET_COMMON_APPLICATION_DATA).</summary>
    [AvaloniaFact]
    public void A_greyed_check_box_shows_its_tooltip_under_the_pointer()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "NuGet reads the machine's nuget.config from the system's own folder here, which a test does not write");
        string machine = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("NUGET_COMMON_APPLICATION_DATA")!, "NuGet", "Config")).FullName;
        // Its feed turned off, so a restore beside the test uses none of it.
        File.WriteAllText(Path.Combine(machine, "Team.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="team" value="https://team.invalid/v3/index.json" />
              </packageSources>
              <disabledPackageSources>
                <add key="team" value="true" />
              </disabledPackageSources>
            </configuration>
            """);
        MainWindow window = Open(1200, 800);
        try
        {
            window.Perform(AppActions.ShowNuGet);
            // Read as files: the run's own nuget.config above the folder clears the machine's feed from those in effect.
            Ui.Landed(() => window.NuGet.PackageSources.Files.Count > 0, "the package sources read");
            Ui.Click(((Panel)window.NuGet.Tabs).Children.OfType<ToggleButton>().Single(t => (string?)t.Content == "Sources"), settle: false);
            Ui.WaitFor(() => window.NuGet.ShowsSources && window.NuGet.Sources.Files.Any(f => f.Place == Passenger.Packages.ConfigPlace.Machine),
                () => $"the Sources tab with the machine's nuget.config, where it lists [{string.Join(", ", window.NuGet.Sources.Files.Select(f => $"{f.Path} ({f.Place})"))}]");
            Passenger.Packages.ConfigFile file = window.NuGet.Sources.Files.Single(f => f.Place == Passenger.Packages.ConfigPlace.Machine);
            Ui.Click(Ui.Row(window.NuGet.Sources.GetVisualDescendants().OfType<ListBox>().First(l => l.Items.Contains(file)), file));
            CheckBox? toggle = null;
            Ui.WaitFor(() => (toggle = window.NuGet.Sources.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Tag as string == "team")?.Children.OfType<CheckBox>().FirstOrDefault()) is not null,
                "the machine's feed team, listed with its check box");
            Assert.False(toggle!.IsEnabled, "The check box of a feed of the machine's nuget.config, which NuGet never writes, is not greyed");
            Rest(window, toggle, $"Enabled: {Passenger.App.ToolWindows.NuGetSourcesView.MachineWide}");
            Assert.Equal("Enabled", AutomationProperties.GetName(toggle));
        }
        finally
        {
            window.Close();
            File.Delete(Path.Combine(machine, "Team.config"));
        }
    }

    /// <summary>Every control of a diff a person presses that shows no words of its own says what it does under the pointer
    /// and to a screen reader (<see cref="ToolButtons.Tip"/>): the arrow of each change that rolls it back, the box that
    /// takes it into the commit, the toolbar's and Find's. In the Commit window's diff, the pointer resting on the first
    /// change's arrow and its box shows their tips; a bare icon button put among them is found by the rule.</summary>
    [AvaloniaFact]
    public void Every_icon_control_in_a_diff_says_what_it_does_under_the_pointer_and_to_a_screen_reader()
    {
        string folder = TestFolders.Create("diff-tips-");
        string notes = Path.Combine(Passenger.Core.PathNames.Canonical(folder), "Notes.txt");
        TestFolders.Prepare(() =>
        {
            File.WriteAllText(notes, "one\ntwo\nthree\nfour\nfive\n");
            TestGit.Init(folder);
            File.WriteAllText(notes, "ONE\ntwo\nthree\nfour\nFIVE\n");
        }, folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            // Open, so its diff can roll its changes back.
            window.OpenFile(notes);
            window.Perform(AppActions.ShowCommit);
            Ui.Landed(() => window.CommitView.Rows.OfType<Passenger.App.ToolWindows.CommitRow>().Any(), "the changed file");
            Passenger.App.Vcs.IDiffHost host = Ui.Landed(window.ShowDiffAsync(notes), "the diff")!;
            Passenger.App.Vcs.DiffView view = OpenDiffs.ViewOf(host);
            view.ShowFind(replace: false);
            Ui.Settle();
            Button[] icons = [.. view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.TemplatedParent is null && !Worded(b))];
            Assert.True(icons.Length >= 8, $"Only {icons.Length} controls of no words were found in the diff: {string.Join(", ", icons.Select(b => ToolTip.GetTip(b)))}");
            List<string> untold = Untold(view);
            Assert.True(untold.Count == 0, $"{untold.Count} controls of the diff say nothing of what they do: give each ToolButtons.Tip:\n{string.Join("\n", untold)}");

            Button arrow = icons.Single(b => ToolTip.GetTip(b) as string == "Roll back change 1 to HEAD");
            CheckBox box = icons.OfType<CheckBox>().Single(b => ToolTip.GetTip(b) as string == "Include change 1 in commit");
            Assert.Equal("Roll back change 1 to HEAD", Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(arrow).GetName());
            Assert.Equal("Include change 1 in commit", Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(box).GetName());
            TopLevel top = TopLevel.GetTopLevel(view)!;
            Rest(top, arrow, "Roll back change 1 to HEAD");
            Rest(top, box, "Include change 1 in commit");

            Button bare = new() { Content = new PathIcon() };
            Panel boxes = (Panel)box.GetVisualParent()!;
            boxes.Children.Add(bare);
            Ui.Settle();
            try
            {
                Assert.True(Untold(view).Count == 1, "The rule finds no bare icon button put among the diff's");
            }
            finally
            {
                boxes.Children.Remove(bare);
            }
            host.Close();
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Each control a person presses under <paramref name="within"/> that shows no words of its own (an icon, a bare
    /// check box) and lacks a tip or the name a screen reader says, which <see cref="ToolButtons.Tip"/> gives both.</summary>
    internal static List<string> Untold(Visual within)
    {
        List<string> untold = [];
        foreach (Button button in within.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.TemplatedParent is null && !Worded(b)))
        {
            string? tip = ToolTip.GetTip(button) as string;
            string? name = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(button).GetName();
            if (string.IsNullOrWhiteSpace(tip) || string.IsNullOrWhiteSpace(name))
            {
                untold.Add($"{button.GetType().Name} {button.Name} in {button.GetVisualParent()?.GetType().Name}: tip \"{tip}\", name \"{name}\"");
            }
        }
        return untold;
    }

    /// <summary>Whether a button shows words of its own, which name it.</summary>
    private static bool Worded(Button button) =>
        button.Content is string { Length: > 0 } || button.GetVisualDescendants().OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text));

    /// <summary>Rests the pointer on a control and waits for the tooltip it opens to say what is given.</summary>
    private static void Rest(TopLevel top, Control control, string tip)
    {
        top.MouseMove(control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value);
        Ui.WaitFor(() => Shown(top) == tip, () => $"\"{tip}\" shown with the pointer resting on the {(control.IsEffectivelyEnabled ? string.Empty : "greyed ")}{control.GetType().Name}, where it shows {Shown(top) ?? "no tooltip"}",
            TimeSpan.FromSeconds(5));

        static string? Shown(TopLevel top) => top.GetVisualDescendants().OfType<ToolTip>().FirstOrDefault(t => t.IsEffectivelyVisible)?.Content as string;
    }

    /// <summary>The status bar's words are a link while they have a place to go (an internal error's, IDE Internal Errors):
    /// the hand, a key's reach, and the link's colour underlined under the pointer, from the shared style in every look;
    /// words that go nowhere stay the bar's muted words under the pointer, and the keys pass them by.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_status_bars_words_are_a_link_under_the_pointer_only_while_they_have_a_place_to_go()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            UnderEachLook(window, () =>
            {
                Button said = Named<Button>(window, "StatusMessage");
                TextBlock words = Named<TextBlock>(window, "StatusText");
                Ui.Press(window, Ui.Shortcut(AppActions.ApplyHotReloadChanges));
                Hover(window, said);
                Assert.True(window.StatusPlace is null && !said.Focusable && said.Cursor is null && Colour(words) == Brush(window, TokenKeys.TextMuted) && !Underlined(words),
                    $"\"{window.Status}\", which goes nowhere, is drawn in {Colour(words)} {(Underlined(words) ? "underlined" : "")} under the pointer with the cursor {said.Cursor}, "
                    + "where the status bar's words that go nowhere stay its muted words");

                Avalonia.Threading.Dispatcher.UIThread.Post(() => throw new InvalidOperationException("A status bar link's own error"));
                try
                {
                    Ui.Settle();
                    Assert.True(window.Status == "IDE error: A status bar link's own error" && window.StatusPlace is not null, $"The status bar says \"{window.Status}\" with no place, not the IDE error going to IDE Internal Errors");
                    window.MouseMove(new Point(1, 1));
                    Hover(window, said);
                    Assert.True(said.Focusable && said.Cursor?.ToString() == "Hand" && Colour(words) == Brush(window, TokenKeys.Link) && Underlined(words),
                        $"\"{window.Status}\", which goes to IDE Internal Errors, is drawn in {Colour(words)} {(Underlined(words) ? "underlined" : "not underlined")} under the pointer "
                        + $"with the cursor {said.Cursor}, {(said.Focusable ? "" : "out of the keys' reach, ")}not a link in the link's colour ({Brush(window, TokenKeys.Link)}) underlined, with the hand");
                    Ui.Click(said);
                    Assert.True(window.Popup is ErrorsPopup, $"A click on \"{window.Status}\" showed {window.Popup?.GetType().Name ?? "nothing"}, not IDE Internal Errors");
                    Ui.Press(window, "ESCAPE");
                    window.MouseMove(new Point(1, 1));
                }
                finally
                {
                    // Thrown on purpose: the check that fails a test over a caught error is not to see it.
                    IdeErrors.Clear();
                }
            });
        }
        finally
        {
            window.Close();
        }

        static void Hover(MainWindow window, Control control)
        {
            window.MouseMove(control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value);
            Ui.Settle();
        }

        static Color? Colour(TextBlock words) => (words.Foreground as ISolidColorBrush)?.Color;

        static bool Underlined(TextBlock words) => words.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
    }

    /// <summary>What of a control is drawn: its place cut to each ancestor that clips what it holds.</summary>
    private static Rect Visible(Visual window, Visual control)
    {
        Rect seen = Placed(window, control);
        for (Visual? up = control.GetVisualParent(); up is not null && up != window; up = up.GetVisualParent())
        {
            if (up.ClipToBounds)
            {
                seen = seen.Intersect(Placed(window, up));
            }
        }
        return seen;
    }

    [Gate]
    [AvaloniaFact]
    public void Focus_is_Rider_s_rounded_ring_centred_on_what_has_the_keys_a_check_box_s_round_its_box_alone()
    {
        string other = TestFolders.Create("ui-rules-other-");
        MainWindow window = Open(1200, 800);
        try
        {
            UnderEachLook(window, () => FocusRings(window, other));
        }
        finally
        {
            window.Close();
            TestFolders.Delete(other);
        }
    }

    private static void FocusRings(MainWindow window, string other)
    {
        window.Open(other);
        Ui.Settle();
        Window dialog = window.Asking ?? throw new Xunit.Sdk.XunitException("Opening another folder over one open did not ask where");
        bool sawBox = false;
        bool sawButton = false;
        for (int i = 0; i < 8 && !(sawBox && sawButton); i++)
        {
            Ui.Press(dialog, "TAB");
            if (dialog.FocusManager?.GetFocusedElement() is not Control focused || AdornerLayer.GetAdornerLayer(focused) is not { } layer)
            {
                continue;
            }
            FocusRing ring = layer.Children.OfType<FocusRing>().SingleOrDefault(r => r.IsVisible)
                ?? throw new Xunit.Sdk.XunitException($"{focused.GetType().Name} has the keys from Tab and no focus ring round it");
            (Rect edge, double radius) = ring.Surrounds();
            Point centre = ring.TranslatePoint(edge.Center, dialog)!.Value;
            if (focused is CheckBox box)
            {
                Control square = box.GetVisualDescendants().OfType<Control>().First(c => c.Name == "NormalRectangle");
                Point boxCentre = square.TranslatePoint(new Point(square.Bounds.Width / 2, square.Bounds.Height / 2), dialog)!.Value;
                Assert.True(Math.Abs(edge.Width - square.Bounds.Width) < 0.5 && Off(centre, boxCentre) < 0.5,
                    $"The check box's focus ring surrounds {edge.Width:0.#} px centred {Off(centre, boxCentre):0.#} px off its box, where Rider's rings the box alone");
                sawBox = true;
            }
            else if (focused is Button button)
            {
                Point buttonCentre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), dialog)!.Value;
                Assert.True(Math.Abs(edge.Width - button.Bounds.Width) < 0.5 && Off(centre, buttonCentre) < 0.5 && radius == button.CornerRadius.TopLeft && radius > 0,
                    $"'{button.Content}''s focus ring is not centred on it with its corners, as Rider's rounded ring is");
                sawButton = true;
            }
        }
        Assert.True(sawBox && sawButton, "Tab never reached the dialog's check box and a button");
        dialog.Close();
        Ui.Settle();
    }

    /// <summary>Every control Tab reaches in Settings, on every page, shows it has the keys: the ring round it, or its edge
    /// in the accent as a field's (a text box, a drop-down, a number box), so a person moving by the keys sees where
    /// they are.</summary>
    [AvaloniaFact]
    public void Every_control_Tab_reaches_shows_the_ring_or_the_accent_edge_while_it_has_the_keys()
    {
        MainWindow window = Open(1200, 800);
        SettingsDialog? settings = null;
        try
        {
            Color[] marks = [Brush(window, TokenKeys.Accent), Brush(window, TokenKeys.Focus)];
            settings = window.ShowSettings();
            Ui.Settle();
            HashSet<string> kinds = new(StringComparer.Ordinal);
            List<string> unmarked = [];
            foreach (string page in settings.Pages.ToList())
            {
                settings.Page = page;
                Ui.Settle();
                TextBox search = settings.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible);
                search.Focus();
                Ui.Settle();
                for (int i = 0; i < 80; i++)
                {
                    Ui.Press(settings, "TAB");
                    if (settings.FocusManager?.GetFocusedElement() is not Control focused || focused == search)
                    {
                        break;
                    }
                    kinds.Add(focused.GetType().Name);
                    if (!Ringed(focused) && !AccentEdged(focused, marks))
                    {
                        unmarked.Add($"{page}: {focused.GetType().Name} '{focused.GetSelfAndVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text))?.Text}'");
                    }
                }
            }
            Assert.True(unmarked.Count == 0, $"These controls have the keys from Tab with nothing drawn to say so, neither the ring nor the accent edge:\n{string.Join("\n", unmarked.Distinct())}");
            Assert.True(new[] { "ComboBox", "CheckBox", "Button", "TextBox" }.All(kinds.Contains), $"Tab reached only {string.Join(", ", kinds)} in Settings");
        }
        finally
        {
            settings?.Close();
            Ui.Settle();
            window.Close();
        }

        static bool Ringed(Control focused) => AdornerLayer.GetAdornerLayer(focused) is { } layer
            && layer.Children.OfType<FocusRing>().Any(r => r.IsVisible && AdornerLayer.GetAdornedElement(r) == focused);

        // A part of another control (a number box's text) shows its edge on that control.
        static bool AccentEdged(Control focused, Color[] marks)
        {
            Control owner = focused.TemplatedParent as Control ?? focused;
            return owner.GetSelfAndVisualDescendants().Any(v => v.IsEffectivelyVisible && v switch
            {
                Border { BorderBrush: ISolidColorBrush edge } border => marks.Contains(edge.Color) && border.BorderThickness != default,
                ContentPresenter { BorderBrush: ISolidColorBrush edge } presenter => marks.Contains(edge.Color) && presenter.BorderThickness != default,
                _ => false,
            });
        }
    }

    [Gate]
    [AvaloniaFact]
    public void The_windows_areas_are_told_apart_by_shade_with_a_line_only_where_two_tool_windows_meet()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Notes.txt"));
            Assert.True(window.Perform(AppActions.ShowProblems), "Problems did not show");
            Assert.True(window.Perform(AppActions.ShowStructure), "Structure did not show");
            Ui.Settle();
            UnderEachLook(window, () => Areas(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Areas(MainWindow window)
    {
        Color frame = Brush(window, TokenKeys.Frame);
        Color panel = Brush(window, TokenKeys.Panel);
        Color editor = Brush(window, TokenKeys.Editor_Background);
        Color line = Brush(window, TokenKeys.Divider);

        // The frame one ground with no rule round it; the tool windows a step lighter; the editor a step from both.
        foreach (Border part in window.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible
            && b.Classes.Any(c => c is StyleClasses.TitleBar or StyleClasses.StripeBar or StyleClasses.TabStrip or StyleClasses.StatusBar)))
        {
            string what = part.Classes.First(c => c is StyleClasses.TitleBar or StyleClasses.StripeBar or StyleClasses.TabStrip or StyleClasses.StatusBar);
            Assert.True(part.BorderThickness == default, $"The {what} has a rule of {part.BorderThickness} along it, where the window's areas are told apart by shade");
            Assert.True(what == "titleBar" || (part.Background is ISolidColorBrush ground && ground.Color == frame),
                $"The {what} is drawn on {(part.Background as ISolidColorBrush)?.Color}, not the frame's ground {frame}, as the stripes, the tabs' strip and the status bar are");
        }
        // The title bar's words and icons in its own colours (white on Passenger's blue), whatever the window's are.
        Border bar = Named<Border>(window, "Toolbar");
        Color[] own = [.. ((string[])["Text", "Icon", "Run", "Stop", "HotReload", "BadgeText"]).Select(k => Brush(window, $"Passenger.TitleBar.{k}"))];
        foreach (TextBlock words in bar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
        {
            Assert.True(words.Foreground is ISolidColorBrush ink && own.Contains(ink.Color),
                $"The title bar's '{words.Text}' is drawn {(words.Foreground as ISolidColorBrush)?.Color}, not in the title bar's own colours");
        }
        foreach (PathIcon icon in bar.GetVisualDescendants().OfType<PathIcon>().Where(i => i.IsEffectivelyVisible))
        {
            string where = icon.FindAncestorOfType<Button>() is { } button ? ToolTip.GetTip(button) as string ?? button.Name ?? "a button" : "the bar";
            Assert.True(icon.Foreground is ISolidColorBrush ink && own.Contains(ink.Color),
                $"An icon on the title bar ({where}) is drawn {(icon.Foreground as ISolidColorBrush)?.Color}, not in the title bar's own colours");
        }
        Assert.True(Step(frame, panel) >= MinimumStep && Step(panel, editor) >= MinimumStep && Step(frame, editor) >= MinimumStep,
            $"The frame ({frame}), the tool windows ({panel}) and the editor ({editor}) are {Step(frame, panel):0.#}, {Step(panel, editor):0.#} and "
            + $"{Step(frame, editor):0.#} apart in lightness, where each must be at least {MinimumStep} apart for a person to tell the areas apart without a rule");

        Rect explorer = Placed(window, Pane(window.ExplorerView));
        Rect structure = Placed(window, Pane(window.DockHost.GetVisualDescendants().OfType<Passenger.App.ToolWindows.StructureView>().First(v => v.IsEffectivelyVisible)));
        Rect problems = Placed(window, Pane(window.DockHost.GetVisualDescendants().OfType<Passenger.App.ToolWindows.ProblemsView>().First(v => v.IsEffectivelyVisible)));
        Rect code = Placed(window, window.EditorArea.CodeEditor);
        Rect status = Placed(window, window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(StyleClasses.StatusBar)));
        using Picture picture = new(window);
        // Where two grounds meet, nothing but the two.
        picture.Meets("the left stripe and the Explorer", new Point(explorer.Left, explorer.Top + (explorer.Height * 0.8)), across: true, frame, panel);
        picture.Meets("the Explorer and the editor", new Point(explorer.Right, code.Top + (code.Height * 0.7)), across: true, panel, editor);
        // The caret's line, the file's first, lit across the editor.
        picture.Meets("the tabs' strip and the editor", new Point(code.Right - 60, code.Top), across: false, frame, Brush(window, TokenKeys.Editor_CurrentLine));
        picture.Meets("the editor and Problems", new Point(code.Left + 6, problems.Top), across: false, editor, panel);
        picture.Meets("Problems and the status bar", new Point(problems.Left + (problems.Width * 0.6), status.Top), across: false, panel, frame);
        // Where two tool windows of one ground meet, a line shows the edge.
        picture.Seam("the Explorer and Structure", new Point(explorer.Left + (explorer.Width / 2), structure.Top), panel, line);
        picture.Seam("Structure and Problems", new Point(structure.Left + (structure.Width / 2), problems.Top), panel, line);
    }

    [Gate]
    [AvaloniaFact]
    public void A_tooltip_and_a_drop_downs_list_are_the_themes_popups_and_a_field_stands_on_the_fields_ground()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            UnderEachLook(window, () => PopupGrounds(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void PopupGrounds(MainWindow window)
    {
        SettingsDialog? settings = null;
        try
        {
            Color popup = Brush(window, TokenKeys.Popup);
            Color edge = Brush(window, TokenKeys.PopupBorder);
            Color text = Brush(window, TokenKeys.Text);
            Color field = Brush(window, TokenKeys.Field);
            Color chosenRow = Brush(window, TokenKeys.RowSelected);
            ToggleButton stripe = window.DockHost.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Classes.Contains(StyleClasses.Stripe) && b.IsEffectivelyVisible);
            ToolTip.SetIsOpen(stripe, true);
            Ui.Settle();
            ToolTip tip = window.GetVisualDescendants().OfType<ToolTip>().FirstOrDefault(t => t.IsEffectivelyVisible)
                ?? throw new Xunit.Sdk.XunitException($"The stripe button's tooltip '{ToolTip.GetTip(stripe)}' did not open");
            Assert.True(tip.Background is ISolidColorBrush tipGround && tipGround.Color == popup && tip.BorderBrush is ISolidColorBrush tipEdge && tipEdge.Color == edge
                && tip.Foreground is ISolidColorBrush tipWords && tipWords.Color == text,
                $"A tooltip is drawn on {(tip.Background as ISolidColorBrush)?.Color} with an edge of {(tip.BorderBrush as ISolidColorBrush)?.Color}, "
                + $"where it is the popup's ground ({popup}) and edge ({edge}) with the window's words, not Fluent's greys");
            ToolTip.SetIsOpen(stripe, false);

            settings = window.ShowSettings();
            Ui.Settle();
            ComboBox combo = settings.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
            Border ground = combo.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Background");
            Assert.True(ground.Background is ISolidColorBrush comboGround && comboGround.Color == field,
                $"A drop-down is drawn on {(ground.Background as ISolidColorBrush)?.Color}, where a field's ground is {field}");
            TextBox search = settings.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible);
            combo.Focus();
            Ui.Settle();
            Border searchGround = search.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
            Assert.True(searchGround.Background is ISolidColorBrush searchColour && searchColour.Color == field,
                $"A text field without the keys is drawn on {(searchGround.Background as ISolidColorBrush)?.Color}, where a field's ground is {field}");

            combo.IsDropDownOpen = true;
            Ui.Settle();
            Border list = combo.GetVisualDescendants().OfType<Popup>().First().Child as Border ?? throw new Xunit.Sdk.XunitException("The drop-down's list has no frame");
            Assert.True(list.Background is ISolidColorBrush listGround && listGround.Color == popup && list.BorderBrush is ISolidColorBrush listEdge && listEdge.Color == edge,
                $"A drop-down's list is drawn on {(list.Background as ISolidColorBrush)?.Color} with an edge of {(list.BorderBrush as ISolidColorBrush)?.Color}, "
                + $"where it is the popup's ground ({popup}) and edge ({edge}), as a menu is");
            ComboBoxItem chosen = (ComboBoxItem)combo.ContainerFromIndex(combo.SelectedIndex)!;
            ContentPresenter band = chosen.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            object? radius = window.TryFindResource(TokenKeys.ControlRadius, window.ActualThemeVariant, out object? found) ? found : null;
            Assert.True(band.Background is ISolidColorBrush row && row.Color == chosenRow && radius is CornerRadius rounded && chosen.CornerRadius == rounded
                && Math.Abs(chosen.Bounds.Height - ListRow.Height) < 0.5 && chosen.Margin.Left > 0,
                $"A drop-down's chosen item is drawn on {(band.Background as ISolidColorBrush)?.Color} with corners of {chosen.CornerRadius}, {chosen.Bounds.Height:0.#} px tall, "
                + $"where it is a list's chosen row ({chosenRow}) on a band inset from the frame with the controls' corners, {ListRow.Height} tall, as a menu's item is");
            combo.IsDropDownOpen = false;
            Ui.Settle();
        }
        finally
        {
            settings?.Close();
            Ui.Settle();
        }
    }

    /// <summary>A toolbar button that cannot run is dimmed in the title bar's own icon colour, whatever colour its action
    /// takes while it can: a disabled Stop reads as unavailable just as a disabled Settings does, under Passenger's own theme,
    /// every theme it carries and every theme of its themes repository, Dark and Light.</summary>
    [AvaloniaFact]
    public void A_disabled_Stop_is_drawn_as_every_disabled_toolbar_button_under_every_look_dark_and_light()
    {
        MainWindow window = Open(1200, 800);
        Button stop = Named<Button>(window, "StopButton");
        Button settings = Named<Button>(window, "SettingsButton");
        try
        {
            Assert.False(stop.IsEnabled, "Stop can run with nothing running");
            settings.IsEnabled = false;
            List<string> unlike = [];
            foreach (string look in (string[])[CarriedThemes.Own, .. CarriedThemes.BuiltInNames, .. CarriedThemes.ThemeNames])
            {
                using IDisposable worn = CarriedThemes.Wearing(look);
                foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Dark, ThemeVariant.Light])
                {
                    window.RequestedThemeVariant = variant;
                    Ui.Settle();
                    (Color, double) drawn = Drawn(stop);
                    if (drawn != Drawn(settings))
                    {
                        unlike.Add($"{look} {variant}: Stop is {drawn}, a disabled Settings {Drawn(settings)}");
                    }
                }
            }
            Assert.True(unlike.Count == 0, $"A disabled Stop is not drawn as a disabled toolbar button is, so it reads as one that runs:\n{string.Join("\n", unlike)}");
        }
        finally
        {
            settings.IsEnabled = true;
            window.RequestedThemeVariant = null;
            window.Close();
        }

        static (Color, double) Drawn(Button button) => button.Content is PathIcon { Foreground: ISolidColorBrush ink } icon ? (ink.Color, icon.Opacity) : default;
    }

    /// <summary>Passenger's own theme, every theme it carries, every theme of its themes repository and the VS Code colour
    /// themes the tests carry as Passenger makes them, for a rule held of its tokens alone, which costs no window.</summary>
    public static TheoryData<string> EveryLook => [CarriedThemes.Own, .. CarriedThemes.BuiltInNames, .. CarriedThemes.ThemeNames, .. CarriedThemes.VsCodeNames];

    [Gate]
    [AvaloniaTheory]
    [MemberData(nameof(EveryLook))]
    public void Text_icons_and_focus_rings_meet_WCAG_AA_on_what_they_are_drawn_on_dark_and_light(string look)
    {
        using IDisposable worn = CarriedThemes.Wearing(look);
        // A VS Code theme's edges round its fields are its author's (input.border), so read:Edge holds Passenger's own alone.
        bool own = !look.StartsWith(CarriedThemes.VsCodePrefix, StringComparison.Ordinal);
        List<string> below = [];
        foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Dark, ThemeVariant.Light])
        {
            foreach (ReadOn pair in ReadOnPairs.Where(p => own || !p.Edge))
            {
                // The fourth to sixth brace colours are held where a theme gives them.
                if (ThemeFiles.Optional.ContainsKey(pair.Fore["Passenger.".Length..]) && !Application.Current!.TryGetResource(pair.Fore, variant, out _))
                {
                    continue;
                }
                Color[] under = Colours(pair.Under, variant);
                Color[] grounds = Colours(pair.Ground, variant);
                double worst = (from f in Colours(pair.Fore, variant) from g in grounds from u in under select Ratio(f, Over(g, u))).Min();
                if (worst < pair.Least)
                {
                    below.Add($"{variant}: {pair.Fore} on {pair.Ground} is {worst:0.00}:1, under {pair.Least}:1");
                }
            }
        }
        Assert.True(below.Count == 0, $"In {look}, {below.Count} pairs fall below WCAG AA, so what they draw is hard to read or to find (text 4.5:1, icons, focus rings, the caret and a field's edge 3:1):\n" + string.Join("\n", below));
    }

    /// <summary>Every colour Tokens.axaml gives says on Dark's what it is read on, is a ground another is read on, or says
    /// why it needs neither (read:None), each ground it names a colour Tokens.axaml gives; Light's say nothing of their own,
    /// as Dark's hold for both. So a colour added there is held to what it is drawn on from its first build.</summary>
    [Fact]
    public void Every_colour_says_what_it_is_read_on_or_why_it_needs_nothing()
    {
        XDocument tokens = TokensSource();
        (string Key, XElement Colour)[] dark = [.. SourceColours(tokens, "Dark")];
        HashSet<string> keys = [.. dark.Select(c => c.Key)];
        HashSet<string> grounds = [.. ReadOnPairs.SelectMany(p => (string[])[p.Ground, p.Under])];
        List<string> wrong = [];
        foreach ((string key, XElement colour) in dark)
        {
            XAttribute[] said = [.. colour.Attributes().Where(a => a.Name.Namespace == ReadNamespace)];
            wrong.AddRange(said.Where(a => a.Name.LocalName is not ("Text" or "Mark" or "Edge" or "None")).Select(a => $"{key} says read:{a.Name.LocalName}, which no rule reads"));
            if (said.Length == 0 && !grounds.Contains(key))
            {
                wrong.Add($"{key} says neither what it is read on (read:Text, read:Mark, read:Edge) nor why it needs nothing (read:None), and nothing is read on it");
            }
        }
        wrong.AddRange(grounds.Where(g => !keys.Contains(g)).Order(StringComparer.Ordinal).Select(g => $"{g} is named as a ground but is no colour Tokens.axaml gives"));
        wrong.AddRange(SourceColours(tokens, "Light").Where(c => c.Colour.Attributes().Any(a => a.Name.Namespace == ReadNamespace)).Select(c => $"{c.Key} says in Light what it is read on, which Dark's says for both"));
        Assert.True(wrong.Count == 0, $"{wrong.Count} colours are held to no contrast, or to one that cannot be read, so they may be unreadable unseen:\n" + string.Join("\n", wrong));
    }

    /// <summary>Passenger's themes repository and the source Passenger carries read whole: each theme an index lists reads
    /// with no problem, named as its file, giving every token Tokens.axaml gives in Dark and Light, and each code colour
    /// scheme every colour of the code but the editor's ground, and no other; the source Passenger carries lists the themes
    /// it names (<see cref="ThemeSource.Carried"/>), Passenger's own theme being Tokens.axaml.</summary>
    [Gate]
    [AvaloniaFact]
    public void Every_theme_of_the_repository_reads_whole_and_gives_every_colour_in_Dark_and_Light_and_the_code_colour_scheme_only_the_codes()
    {
        ResourceDictionary passenger = Dictionary("Tokens.axaml");
        string[] keys = Keys(passenger, ThemeVariant.Dark);
        string[] shapes = [.. passenger.Keys.Select(k => k.ToString()!).Order(StringComparer.Ordinal)];
        Assert.Equal(keys, Keys(passenger, ThemeVariant.Light));
        ThemeRead<ThemeIndex> builtIn = BuiltInThemes.Index;
        Assert.True(builtIn.Read is { CodeColours: [] } && builtIn.Problems.Count == 0, $"The built-in source does not read whole: {builtIn.Refused}");
        Assert.Equal([.. CarriedThemes.BuiltInNames.Select(n => $"{n}.json")], builtIn.Read.Themes);
        ThemeRead<ThemeIndex> index = CarriedThemes.Index;
        Assert.True(index.Read is not null && index.Problems.Count == 0, $"The themes repository's index does not read whole: {index.Refused}{string.Join("\n", index.Problems)}");
        Assert.Equal([.. ThemeSource.FormerlyCarried.Select(n => $"{n}.json")], index.Read.Themes);
        Assert.Equal([$"{ThemeSource.FormerScheme}.json"], index.Read.CodeColours);
        List<(string, ResourceDictionary)> files = [("Tokens.axaml", passenger)];
        foreach (string look in (string[])[.. CarriedThemes.BuiltInNames, .. CarriedThemes.ThemeNames])
        {
            ThemeRead<ThemeFile> read = CarriedThemes.ReadTheme(look);
            Assert.True(read.Read is not null && read.Problems.Count == 0 && read.Read.Name == look,
                $"{look}.json does not read whole as {look}, so choosing {look} would fail: {read.Refused}{string.Join("\n", read.Problems)}");
            // A theme laid over a VS Code theme gives its colours through its base.
            ResourceDictionary theme = ThemeResources.Dictionary(CarriedThemes.Theme(look)!);
            files.Add(($"{look}.json", theme));
            // A theme over a VS Code theme gives its base's colours for both; any may add ThemeFiles.Optional's tokens.
            string[] Named(IEnumerable<object> held) => [.. held.Select(k => k.ToString()!).Where(k => k.StartsWith("Passenger.", StringComparison.Ordinal) && !ThemeFiles.Optional.ContainsKey(k["Passenger.".Length..]))];
            string[] Given(ThemeVariant variant) => [.. Named(theme.Keys).Where(k => !shapes.Contains(k))
                .Concat(theme.ThemeDictionaries.TryGetValue(variant, out IThemeVariantProvider? held) ? Named(((ResourceDictionary)held).Keys) : []).Distinct().Order(StringComparer.Ordinal)];
            Assert.True(keys.SequenceEqual(Given(ThemeVariant.Dark)) && keys.SequenceEqual(Given(ThemeVariant.Light)),
                $"{look} does not give every colour Tokens.axaml gives, in Dark and Light, and no other: a colour it lacks shows Passenger's own under it");
            Assert.True(shapes.SequenceEqual(Named(theme.Keys).Where(k => !keys.Contains(k)).Order(StringComparer.Ordinal)), $"{look} does not give every shape Tokens.axaml gives");
        }
        foreach (string choice in index.Read.CodeColours.Select(Path.GetFileNameWithoutExtension).OfType<string>())
        {
            ThemeRead<ThemeFile> read = CarriedThemes.ReadScheme(choice);
            Assert.True(read.Read is not null && read.Problems.Count == 0 && read.Read.Name == choice,
                $"{choice}.json does not read whole as {choice}, so choosing it would fail: {read.Refused}{string.Join("\n", read.Problems)}");
            ResourceDictionary scheme = ThemeResources.Dictionary(read.Read);
            files.Add(($"{choice}.json", scheme));
            // The code's colours, which the scheme gives in the theme's place; the editor's ground, gutter and cards stay the theme's.
            string[] code = [.. keys.Where(k => (k.StartsWith("Passenger.Editor.", StringComparison.Ordinal) && k is not (TokenKeys.Editor_Background or TokenKeys.Editor_Gutter or TokenKeys.Editor_PopupBorder))
                || k.StartsWith("Passenger.Syntax.", StringComparison.Ordinal) || k.StartsWith("Passenger.SyntaxStyle.", StringComparison.Ordinal) || k.StartsWith("Passenger.Diff.", StringComparison.Ordinal))];
            Assert.Equal(code, Keys(scheme, ThemeVariant.Dark));
            Assert.Equal(code, Keys(scheme, ThemeVariant.Light));
            Assert.Empty(scheme.Keys);
        }
        // A theme is tokens alone: Fluent's own keys are FluentKeys.axaml's, once for every theme.
        foreach ((string file, ResourceDictionary theme) in files)
        {
            string[] others = [.. theme.Keys.Concat(theme.ThemeDictionaries.Values.SelectMany(v => ((ResourceDictionary)v).Keys)).Select(k => k.ToString()!)
                .Where(k => !k.StartsWith("Passenger.", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal)];
            Assert.True(others.Length == 0, $"{file} gives {string.Join(", ", others)}, which are not Passenger's tokens: name Fluent's keys once for every theme in FluentKeys.axaml");
        }
    }

    /// <summary>Every colour, brush and corner Fluent gives is the look's own token in either look, Dark and Light
    /// (FluentKeys.axaml), so a control left to Fluent never shows another theme's colours or Fluent's greys. A key is left
    /// to Fluent only by the reasons below; an Avalonia update that adds one fails here until it is mapped.</summary>
    [Gate]
    [AvaloniaFact]
    public void Every_colour_and_corner_Fluent_gives_is_the_looks_own_token_dark_and_light()
    {
        Application app = Application.Current!;
        FluentTheme fluent = app.Styles.OfType<FluentTheme>().Single();
        HashSet<string> keys = new(StringComparer.Ordinal);
        KeysOf(fluent.Resources, keys);
        foreach (Styles child in ((IEnumerable<IStyle>)fluent).OfType<Styles>())
        {
            KeysOf(child.Resources, keys);
        }
        string source = UiSource();
        List<string> unmapped = [];
        List<string> wrong = [];
        List<string> mapped = [];
        foreach (string key in keys.Order(StringComparer.Ordinal))
        {
            object? dark = fluent.TryGetResource(key, ThemeVariant.Dark, out object? d) ? d : null;
            object? light = fluent.TryGetResource(key, ThemeVariant.Light, out object? l) ? l : null;
            if (!Painted(dark) && !Painted(light))
            {
                continue;
            }
            if (Clear(dark) && Clear(light))
            {
                continue;
            }
            if (LeftToFluent(key, dark ?? light, source) is { } why)
            {
                Assert.True(!why.StartsWith('!'), $"{key} is left to Fluent, but {why[1..]}");
                continue;
            }
            if (!FluentKeys.Tokens.TryGetValue(key, out string? token))
            {
                unmapped.Add(key);
                continue;
            }
            mapped.Add(key);
        }
        Assert.True(unmapped.Count == 0, $"Fluent gives {unmapped.Count} keys Passenger's controls can reach that no theme answers for, each showing Fluent's own colour "
            + $"under every look: name each in FluentKeys.axaml as the token it is, or say in this test why no control of Passenger's reads it:\n{string.Join("\n", unmapped)}");
        foreach (string look in (string[])[CarriedThemes.Own, .. CarriedThemes.BuiltInNames, .. CarriedThemes.ThemeNames])
        {
            using IDisposable worn = CarriedThemes.Wearing(look);
            foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Dark, ThemeVariant.Light])
            {
                foreach ((string key, string token) in FluentKeys.Tokens)
                {
                    bool has = app.TryGetResource(token, variant, out object? own);
                    if (!has || !app.TryGetResource(key, variant, out object? found) || !ReferenceEquals(found, own))
                    {
                        wrong.Add($"{look} {variant}: {key} is not {token}{(has ? "" : ", which the look lacks")}");
                    }
                }
            }
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} of Fluent's keys are not the look's own token:\n{string.Join("\n", wrong)}");
        string[] stale = [.. FluentKeys.Tokens.Keys.Where(k => !keys.Contains(k)).Order(StringComparer.Ordinal)];
        Assert.True(stale.Length == 0, $"FluentKeys.axaml names {string.Join(", ", stale)}, which Fluent no longer gives");
    }

    /// <summary>Why a key Fluent gives is left to it, or null when a control of Passenger's can reach it; a reason that
    /// does not hold starts with '!'.</summary>
    private static string? LeftToFluent(string key, object? value, string source)
    {
        // Fluent's palette colours, read only into its own brushes: nothing of Passenger's asks for a colour by name.
        if (value is Color)
        {
            return Regex.IsMatch(source, $@"\b{key}\b") ? $"!Passenger's source reads {key}, a colour Fluent's palette gives" : "a palette colour";
        }
        // ControlThemes.axaml's scrollbar is Passenger's whole, so Fluent's scrollbar keys are never read.
        if (key.StartsWith("ScrollBar", StringComparison.Ordinal))
        {
            Application.Current!.TryGetResource(typeof(ScrollBar), null, out object? ours);
            FluentTheme fluent = Application.Current.Styles.OfType<FluentTheme>().Single();
            fluent.TryGetResource(typeof(ScrollBar), null, out object? theirs);
            return ReferenceEquals(ours, theirs) ? "!the scrollbar's control theme is Fluent's, not ControlThemes.axaml's" : "Passenger's own scrollbar";
        }
        // Fluent's Button.accent alone reads these; Passenger's emphasised button is Button.primary (Shell.axaml).
        if (key.StartsWith("AccentButton", StringComparison.Ordinal))
        {
            return Regex.IsMatch(source, "\"accent\"") ? "!a control of Passenger's takes Fluent's accent class" : "Fluent's accent button";
        }
        // Controls Passenger never shows: none of its windows names one.
        foreach ((string prefix, string[] controls) in Unshown)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                string? shown = controls.FirstOrDefault(c => Regex.IsMatch(source, $@"\b{c}\b"));
                return shown is null ? $"no {controls[0]}" : $"!Passenger shows a {shown}, whose {prefix} keys must be named in FluentKeys.axaml";
            }
        }
        return null;
    }

    private static readonly (string Prefix, string[] Controls)[] Unshown =
    [
        ("AutoCompleteBox", ["AutoCompleteBox"]),
        ("Calendar", ["Calendar", "CalendarDatePicker"]),
        ("DatePicker", ["DatePicker", "CalendarDatePicker"]),
        ("TimePicker", ["TimePicker"]),
        ("DateTime", ["DatePicker", "TimePicker"]),
        ("ToggleSwitch", ["ToggleSwitch"]),
        ("Slider", ["Slider"]),
        ("TreeView", ["TreeView", "TreeViewItem"]),
        ("TabItem", ["TabItem", "TabControl"]),
        ("TabControl", ["TabControl"]),
        ("TabbedPage", ["TabbedPage"]),
        ("SplitButton", ["SplitButton", "ToggleSplitButton"]),
        ("SplitView", ["SplitView"]),
        ("HyperlinkButton", ["HyperlinkButton"]),
        ("HyperlinkVisited", ["HyperlinkButton"]),
        ("Expander", ["Expander"]),
        ("GroupBox", ["GroupBox"]),
        ("NotificationCard", ["NotificationCard", "WindowNotificationManager"]),
        ("PipsPager", ["PipsPager"]),
        ("TableView", ["TableView"]),
        ("RefreshVisualizer", ["RefreshContainer", "RefreshVisualizer"]),
        ("NavigationBar", ["NavigationPage", "DrawerPage"]),
        ("NavigationControl", ["NavigationPage", "DrawerPage"]),
        ("Flyout", ["Flyout", "FlyoutPresenter"]),
    ];

    private static bool Painted(object? value) => value is Color or IBrush or CornerRadius;

    private static bool Clear(object? value) => value is ISolidColorBrush { Color.A: 0 } or Color { A: 0 };

    private static void KeysOf(IResourceProvider provider, HashSet<string> keys)
    {
        if (provider is ResourceInclude include)
        {
            KeysOf(include.Loaded, keys);
            return;
        }
        if (provider is IResourceDictionary dictionary)
        {
            keys.UnionWith(dictionary.Keys.OfType<string>());
            foreach (IThemeVariantProvider variant in dictionary.ThemeDictionaries.Values)
            {
                KeysOf(variant, keys);
            }
            foreach (IResourceProvider merged in dictionary.MergedDictionaries)
            {
                KeysOf(merged, keys);
            }
        }
    }

    /// <summary>No colour and no rounded corner is written outside the theme files (Theme/): each is a token of the look in
    /// force, so a look draws the whole window. Where a colour is the content's, not the window's, it is allowed below,
    /// saying why.</summary>
    [Gate]
    [Fact]
    public void No_colour_or_rounded_corner_is_written_outside_the_theme_files()
    {
        Regex colour = new(@"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b|\bColor\.(?:FromRgb|FromArgb|FromUInt32|FromHsl|FromHsv|Parse)\b|\bColors\.\w+|\bBrushes\.(?!Transparent\b)\w+|new SolidColorBrush\(\s*(?:Color|Colors)\.");
        Regex corners = new(@"new CornerRadius\(\s*([0-9.,\s]+)\)|CornerRadius\s*=\s*([0-9.]+)\b|CornerRadius=""([0-9.,\s]+)""");
        List<string> found = [];
        foreach ((string path, string text) in UiFiles())
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (Match match in colour.Matches(line))
                {
                    if (!ContentsColour(path, match.Value))
                    {
                        found.Add($"{path}:{i + 1}: {match.Value}");
                    }
                }
                foreach (Match match in corners.Matches(line))
                {
                    string numbers = string.Concat(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
                    if (numbers.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Any(n => double.Parse(n, System.Globalization.CultureInfo.InvariantCulture) != 0))
                    {
                        found.Add($"{path}:{i + 1}: {match.Value}");
                    }
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} colours or rounded corners are written outside Theme/, so one look's would show under another: give each a token "
            + $"(Tokens.axaml and every built-in theme's file, Dark and Light) or a radius resource, and read that:\n{string.Join("\n", found)}");
    }

    /// <summary>No margin, padding, spacing or border is given a number of its own outside the theme files: each is a step
    /// of the one scale (Gaps) or a line of Hairlines, so like things stand alike in every window, and Theme/'s styles
    /// write only the steps' numbers. A number that is content's own drawing, not the window's spacing, is allowed below,
    /// saying why.</summary>
    [Fact]
    public void No_margin_padding_spacing_or_border_is_a_number_of_its_own_but_a_step_of_the_one_scale()
    {
        Regex number = new(@"(?<![\w.])\d+(?:\.\d+)?(?![\w.])");
        Regex thickness = new(@"new Thickness\(|\bThickness\s+\w+\s*(?:=>|=)\s*new\(|\b(?:Margin|Padding|BorderThickness)\s*=\s*new\(|Thickness\.Parse\(");
        Regex spacing = new(@"\b(?:Spacing|ItemSpacing|LineSpacing|RowSpacing|ColumnSpacing)\s*=\s*");
        Regex named = new(@"const double (\w*(?:Gap|Gutter|Inset|Pad|Padding|Margin|Spacing|Indent)\w*)\s*=\s*([^;]+);");
        Regex attribute = new(@"\b(?:Margin|Padding|BorderThickness|Spacing|ItemSpacing|LineSpacing|RowSpacing|ColumnSpacing)=""([^""{][^""]*)""");
        Regex setter = new(@"Property=""(Margin|Padding|Spacing|ItemSpacing|LineSpacing|RowSpacing|ColumnSpacing)""\s+Value=""([^""{][^""]*)""");
        Regex selector = new(@"<Style Selector=""([^""]*)""");
        double[] steps = [.. typeof(Gaps).GetFields().Where(f => f.IsLiteral).Select(f => (double)f.GetRawConstantValue()!)];
        // Halving (a mark centred in its column) names no room of its own.
        bool HasNumber(string written) => number.Matches(Regex.Replace(written, @"/\s*2(?![\d.])", "/ half"))
            .Any(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) != 0);
        List<string> found = [];
        foreach ((string path, string text) in UiFiles(theme: true))
        {
            if (path.Contains("/Theme/", StringComparison.Ordinal))
            {
                if (path.EndsWith(".axaml", StringComparison.Ordinal))
                {
                    string[] themeLines = text.Split('\n');
                    string styled = string.Empty;
                    for (int i = 0; i < themeLines.Length; i++)
                    {
                        if (selector.Match(themeLines[i]) is { Success: true } style)
                        {
                            styled = style.Groups[1].Value;
                        }
                        // A rendered Markdown document's own type and spacing, as its page draws it.
                        if (styled.Contains(".md", StringComparison.Ordinal) || styled.Contains("MarkdownBlocks", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        foreach (Match match in setter.Matches(themeLines[i]))
                        {
                            if (!number.Matches(match.Groups[2].Value).Select(m => Math.Abs(double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)))
                                .Any(n => n != 0 && !steps.Contains(n)))
                            {
                                continue;
                            }
                            found.Add($"{path}:{i + 1}: {match.Value} (each number one of Gaps' steps: {string.Join(", ", steps.Distinct())})");
                        }
                    }
                }
                continue;
            }
            List<(int At, string What)> own = [];
            if (path.EndsWith(".axaml", StringComparison.Ordinal))
            {
                foreach (Match match in attribute.Matches(text))
                {
                    if (HasNumber(match.Groups[1].Value))
                    {
                        own.Add((match.Index, match.Value));
                    }
                }
                foreach (Match match in setter.Matches(text))
                {
                    if (HasNumber(match.Groups[2].Value))
                    {
                        own.Add((match.Index, match.Value));
                    }
                }
            }
            else
            {
                string code = string.Join('\n', text.Split('\n').Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));
                foreach (Match match in thickness.Matches(code))
                {
                    string written = Bracketed(code, match.Index + match.Length - 1);
                    if (HasNumber(written))
                    {
                        own.Add((match.Index, match.Value + written + ")"));
                    }
                }
                foreach (Match match in spacing.Matches(code))
                {
                    string written = Expression(code, match.Index + match.Length);
                    if (HasNumber(written))
                    {
                        own.Add((match.Index, match.Value + written));
                    }
                }
                foreach (Match match in named.Matches(code))
                {
                    if (HasNumber(match.Groups[2].Value))
                    {
                        own.Add((match.Index, match.Value));
                    }
                }
            }
            foreach ((int at, string what) in own.Where(o => !ContentsSpacing(path, o.What)).OrderBy(o => o.At))
            {
                found.Add($"{path}:{LineOf(text, at)}: {what}");
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} margins, paddings, spacings or borders are given numbers of their own, so they stand otherwise than their "
            + $"like elsewhere: give each a step of Gaps (Gaps.Normal, new Thickness(Gaps.Wide, 0)) or a line of Hairlines, through the shared piece it "
            + $"belongs to where there is one:\n{string.Join("\n", found)}");
    }

    /// <summary>No word is given a size of its own outside the theme files: each takes a step of the one type ramp
    /// (TypeRamp) through the class that says its role (caption, code, badgeText, a heading's), or the body's, so like words
    /// stand alike in every window; Theme/'s styles write only the ramp's sizes, and the body's resource is its Body.</summary>
    [Fact]
    public void No_font_size_is_a_number_of_its_own_but_a_step_of_the_type_ramp()
    {
        Regex number = new(@"(?<![\w.])\d+(?:\.\d+)?(?![\w.])");
        Regex assigned = new(@"(?<!\w)FontSize\s*=(?!=)");
        Regex attribute = new(@"(?<![\w.])FontSize=""([^""{][^""]*)""");
        Regex setter = new(@"Property=""(?:TextElement\.)?FontSize""\s+Value=""([^""{][^""]*)""");
        Regex selector = new(@"<Style Selector=""([^""]*)""");
        Regex body = new(@"<x:Double x:Key=""Passenger\.BodySize"">([^<]*)</x:Double>");
        double[] steps = [.. typeof(TypeRamp).GetFields().Where(f => f.IsLiteral).Select(f => (double)f.GetRawConstantValue()!).Distinct().Order()];
        bool HasNumber(string written) => number.IsMatch(written);
        List<string> found = [];
        bool bodyGiven = false;
        foreach ((string path, string text) in UiFiles(theme: true))
        {
            string[] lines = text.Split('\n');
            if (path.Contains("/Theme/", StringComparison.Ordinal))
            {
                if (!path.EndsWith(".axaml", StringComparison.Ordinal))
                {
                    continue;
                }
                string styled = string.Empty;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (selector.Match(lines[i]) is { Success: true } style)
                    {
                        styled = style.Groups[1].Value;
                    }
                    if (body.Match(lines[i]) is { Success: true } bodySize)
                    {
                        bodyGiven = true;
                        if (double.Parse(bodySize.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) != TypeRamp.Body)
                        {
                            found.Add($"{path}:{i + 1}: {bodySize.Value} (the body is TypeRamp.Body, {TypeRamp.Body})");
                        }
                    }
                    // A rendered Markdown document's own type, as its page draws it.
                    if (styled.Contains(".md", StringComparison.Ordinal) || styled.Contains("MarkdownBlocks", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    foreach (Match match in setter.Matches(lines[i]))
                    {
                        if (!double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double size) || !steps.Contains(size))
                        {
                            found.Add($"{path}:{i + 1}: {match.Value} (each size one of TypeRamp's steps: {string.Join(", ", steps)})");
                        }
                    }
                }
                continue;
            }
            List<(int At, string What)> own = [];
            if (path.EndsWith(".axaml", StringComparison.Ordinal))
            {
                foreach (Match match in attribute.Matches(text).Concat(setter.Matches(text)))
                {
                    if (HasNumber(match.Groups[1].Value))
                    {
                        own.Add((match.Index, match.Value));
                    }
                }
            }
            else
            {
                string code = string.Join('\n', lines.Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));
                foreach (Match match in assigned.Matches(code))
                {
                    string written = Expression(code, match.Index + match.Length);
                    if (HasNumber(written))
                    {
                        own.Add((match.Index, match.Value + written));
                    }
                }
            }
            foreach ((int at, string what) in own.OrderBy(o => o.At))
            {
                found.Add($"{path}:{LineOf(text, at)}: {what.Trim()}");
            }
        }
        Assert.True(bodyGiven, "Shell.axaml names no Passenger.BodySize, so the window's words take Fluent's size, which no step of the ramp says");
        Assert.True(found.Count == 0, $"{found.Count} words are given a size of their own, so they stand otherwise than their like elsewhere: give each the "
            + $"class of its role on the type ramp (StyleClasses.Caption, Code, BadgeText, PaneTitle, PageTitle, HeroTitle; MutedText.Caption), or TypeRamp's "
            + $"step where a control draws its own text, and in Theme/ one of TypeRamp's sizes:\n{string.Join("\n", found)}");
    }

    /// <summary>No word is made bold or semibold outside the theme files but a run inside running text (a match, a
    /// Markdown span): a heading takes its class (paneTitle, pageTitle, heroTitle, toolTitle, DialogFooter's title) and a
    /// name standing out in its row <c>strong</c>, so every heading and every row's emphasis is one weight.</summary>
    [Fact]
    public void No_font_weight_is_given_outside_the_theme_but_to_a_run_inside_running_text()
    {
        Regex assigned = new(@"(?<!\w)FontWeight\s*=(?!=)");
        Regex attribute = new(@"<(\w+)\b[^<>]*?\bFontWeight=""[^""]*""");
        Regex run = new(@"\bRun\s*(?:\(|\{|\w+\s*=)|(?<!\w)run\.FontWeight");
        List<string> found = [];
        foreach ((string path, string text) in UiFiles())
        {
            if (path.EndsWith(".axaml", StringComparison.Ordinal))
            {
                found.AddRange(attribute.Matches(text).Where(m => m.Groups[1].Value != "Run").Select(m => $"{path}:{LineOf(text, m.Index)}: {m.Value}"));
                continue;
            }
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && assigned.IsMatch(lines[i]) && !run.IsMatch(lines[i]))
                {
                    found.Add($"{path}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} words are given a weight of their own, so one heading or row stands bolder or lighter than its like: "
            + $"give a heading its class (StyleClasses.PaneTitle, PageTitle, HeroTitle; a dialog's through DialogFooter.Headed or Question) and a "
            + $"name standing out in its row StyleClasses.Strong (Classes.Set where a row is shown again):\n{string.Join("\n", found)}");
    }

    /// <summary>The text between the bracket at <paramref name="open"/> and the one closing it.</summary>
    private static string Bracketed(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return text[(open + 1)..i];
            }
        }
        return text[(open + 1)..];
    }

    /// <summary>The expression starting at <paramref name="start"/>, up to the comma, semicolon or bracket that ends it.</summary>
    private static string Expression(string text, int start)
    {
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}' when depth == 0:
                    return text[start..i];
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' or ';' when depth == 0:
                    return text[start..i];
            }
        }
        return text[start..];
    }

    /// <summary>A number that is content's own drawing, not the window's spacing, so no step of the scale stands for it.</summary>
    private static bool ContentsSpacing(string path, string what) => path switch
    {
        // The code editor's own rows and gutter, drawn to the code's metrics.
        _ when path.StartsWith("src/Passenger.Editor/", StringComparison.Ordinal) => true,
        // The terminal's cells, round which it draws to the program's grid.
        _ when path.StartsWith("src/Passenger.Terminal/", StringComparison.Ordinal) => true,
        // The longest pause between a trace's events, a time, not a space.
        _ when path.EndsWith("Profiler/Snapshot.cs", StringComparison.Ordinal) && what.Contains("LongestGap", StringComparison.Ordinal) => true,
        // A flame graph's bars and a frame chart's columns, drawn to the trace's own scale.
        _ when path.EndsWith("Profiling/FlameGraph.cs", StringComparison.Ordinal) || path.EndsWith("Profiling/FrameChart.cs", StringComparison.Ordinal) => true,
        // The Aspire graph's nodes and edges, laid out as a drawing of the resources.
        _ when path.EndsWith("Aspire/AspireGraph.cs", StringComparison.Ordinal) => true,
        // The room a popup's shadow is drawn in round its frame, as far as the shadow's blur reaches.
        _ when path.EndsWith("App/PopupHost.cs", StringComparison.Ordinal) && what.EndsWith("(32, 24, 32, 40)", StringComparison.Ordinal) => true,
        _ => false,
    };

    /// <summary>Every theme key and icon the code names (TokenKeys and IconKeys, a literal still shaped like one in C#, a
    /// resource in XAML) is one the app's resources give, dark and light: a misspelt or missing one finds nothing, drawn as
    /// nothing (Transparent, no icon) with no error. A literal of that shape naming something else (a project's assembly, a
    /// diagnostic's source) is passed over; a key made at run time ($"Icon.{kind}") is its own code's to check.</summary>
    [Gate]
    [AvaloniaFact]
    public void Every_theme_key_and_icon_the_code_names_is_one_the_resources_give_dark_and_light()
    {
        Application app = Application.Current!;
        Regex literal = new(@"""((?:Passenger|Icon)(?:\.[A-Z][A-Za-z0-9]*)+)""");
        Regex resource = new(@"\{(?:DynamicResource|StaticResource)\s+((?:Passenger|Icon)\.[\w.]+)\}");
        string root = Passenger.Tests.Debugging.Helpers.Debuggees.Root;
        List<string> missing = [];
        int named = 0;
        foreach (System.Reflection.FieldInfo key in typeof(TokenKeys).GetFields().Concat(typeof(IconKeys).GetFields()))
        {
            named++;
            string value = (string)key.GetRawConstantValue()!;
            if (!app.TryGetResource(value, ThemeVariant.Dark, out _) || !app.TryGetResource(value, ThemeVariant.Light, out _))
            {
                missing.Add($"{key.DeclaringType!.Name}.{key.Name}: {value}");
            }
        }
        foreach ((string path, string text) in UiFiles(theme: true))
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (Match match in (path.EndsWith(".cs", StringComparison.Ordinal) ? literal : resource).Matches(line))
                {
                    string key = match.Groups[1].Value;
                    // An assembly or a type of it, or a diagnostic's source, shaped like a key but none.
                    if (Assembly(key) || line.Contains("DiagnosticSource", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    named++;
                    if (!app.TryGetResource(key, ThemeVariant.Dark, out _) || !app.TryGetResource(key, ThemeVariant.Light, out _))
                    {
                        missing.Add($"{path}:{i + 1}: {key}");
                    }
                }
            }
        }
        Assert.True(named > 500, $"Only {named} keys were found named in the code: the search no longer reads it");
        Assert.True(missing.Count == 0, $"{missing.Count} keys the code names are no resource, dark or light, so what reads them draws nothing: give each in "
            + $"the theme (Tokens.axaml and every look, or Icons.axaml) or name the one that is there:\n{string.Join("\n", missing)}");

        bool Assembly(string name) => Enumerable.Range(2, name.Count(c => c == '.')).Any(parts =>
            Directory.Exists(Path.Combine(root, "src", string.Join('.', name.Split('.').Take(parts)))));
    }

    /// <summary>Every style class the code names is one a style takes: StyleClasses holds each class the theme's selectors
    /// name, C# names a class from there (or takes it by a parameter marked [IdentifierOf] StyleClasses), and XAML's
    /// Classes name only those. A class written as text in C#, or one in XAML no style takes, fails here naming its line:
    /// misspelt or unstyled, it changes nothing, with no error.</summary>
    [Fact]
    public void Every_style_class_the_code_names_is_one_a_style_takes()
    {
        string root = Passenger.Tests.Debugging.Helpers.Debuggees.Root;
        HashSet<string> styled = new(StringComparer.Ordinal);
        foreach (string theme in (string[])["Shell.axaml", "ControlThemes.axaml"])
        {
            foreach (Match selector in Regex.Matches(File.ReadAllText(Path.Combine(root, "src", "Passenger.App", "Theme", theme)), @"<Style\s[^>]*Selector=""([^""]*)"""))
            {
                styled.UnionWith(Regex.Matches(Regex.Replace(selector.Groups[1].Value, @"\[[^\]]*\]", string.Empty), @"\.([A-Za-z_][\w-]*)").Select(m => m.Groups[1].Value));
            }
        }
        string[] named = [.. typeof(StyleClasses).GetFields().Select(f => (string)f.GetRawConstantValue()!)];
        Assert.True(styled.Count > 100, $"Only {styled.Count} classes were found styled: the search no longer reads the theme's selectors");
        Assert.True(styled.SetEquals(named), $"StyleClasses is not the classes the styles take: it lacks {string.Join(", ", styled.Except(named))} "
            + $"and holds {string.Join(", ", named.Except(styled))}");

        Regex given = new(@"\bClasses\s*=\s*\{([^}]*)\}|\bClasses\.(?:Add|Contains|Set|Remove|Insert)\(\s*([^,)]*)|\bClasses:\s*\[([^\]]*)\]");
        Regex written = new(@"\bClasses=""([^""{]*)""|\bClasses\.([A-Za-z_][\w-]*)=");
        List<string> found = [];
        int checkedCount = 0;
        foreach ((string path, string text) in UiFiles(theme: true))
        {
            bool code = path.EndsWith(".cs", StringComparison.Ordinal);
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith(code ? "//" : "<!--", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (Match match in (code ? given : written).Matches(line))
                {
                    string classes = string.Concat(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
                    foreach (string one in code ? classes.Split(',') : classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string shown = one.Trim();
                        checkedCount++;
                        if (code && shown.Contains('"', StringComparison.Ordinal))
                        {
                            found.Add($"{path}:{i + 1}: {shown} is written as text");
                        }
                        else if (code && Regex.IsMatch(shown, @"^\w+$")
                            && !Regex.IsMatch(text, $@"\[IdentifierOf\(typeof\(StyleClasses\)\)\]\s*string\??\s+{Regex.Escape(shown)}\b"))
                        {
                            found.Add($"{path}:{i + 1}: {shown} is taken from somewhere other than StyleClasses");
                        }
                        else if (!code && !styled.Contains(shown))
                        {
                            found.Add($"{path}:{i + 1}: {shown} is no class a style takes");
                        }
                    }
                }
            }
        }
        Assert.True(checkedCount > 500, $"Only {checkedCount} classes were found named in the code: the search no longer reads it");
        Assert.True(found.Count == 0, $"{found.Count} classes the code names may be none a style takes, so a control given one looks as if it had none: "
            + $"name each from StyleClasses (a parameter that takes one marked [IdentifierOf(typeof(StyleClasses))]), and in XAML write only a class "
            + $"Shell.axaml or ControlThemes.axaml styles:\n{string.Join("\n", found)}");
    }

    /// <summary>A message that sends a person to Settings names a page the dialog lists, as the dialog names it: through
    /// SettingsDialog.PlaceOfPage, given one of the dialog's page constants. Words written after "Settings |" by hand, or
    /// a page the dialog does not list, fail here, naming the line.</summary>
    [AvaloniaFact]
    public void Every_message_that_sends_a_person_to_Settings_names_a_page_the_dialog_lists()
    {
        MainWindow window = Open(1400, 880);
        IReadOnlyList<string> pages;
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            pages = dialog.Pages;
            dialog.Close();
        }
        finally
        {
            window.Close();
        }
        Regex byHand = new(@"\bSettings \|(?!\|)");
        Regex placed = new(@"\bPlaceOfPage\(\s*(?:SettingsDialog\.)?([^()]*?)\s*\)");
        List<string> found = [];
        int named = 0;
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) || lines[i].Contains("static string PlaceOfPage(string page)", StringComparison.Ordinal))
                {
                    continue;
                }
                if (byHand.IsMatch(lines[i]))
                {
                    found.Add($"{path}:{i + 1}: a Settings page named by hand");
                }
                foreach (Match place in placed.Matches(lines[i]))
                {
                    named++;
                    // A declared setting names the page its row is on, which the dialog lists as it makes the row.
                    if (place.Groups[1].Value == "Row.Page" && path.EndsWith("Setting.cs", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string? page = typeof(SettingsDialog).GetField(place.Groups[1].Value, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        is { IsLiteral: true } constant ? constant.GetRawConstantValue() as string : null;
                    if (page is null || !pages.Contains(page))
                    {
                        found.Add($"{path}:{i + 1}: {place.Value} names {(page is null ? "none of the dialog's page constants" : $"'{page}', a page the dialog does not list")}");
                    }
                }
            }
        }
        Assert.True(named >= 4, $"Only {named} messages were found naming a Settings page: the search no longer reads them");
        Assert.True(found.Count == 0, $"{found.Count} messages send a person to a place in Settings that may not be there: name the page with "
            + $"SettingsDialog.PlaceOfPage and the dialog's constant for it, one of {string.Join(", ", pages)}:\n{string.Join("\n", found)}");
    }

    /// <summary>No editor of a Settings row, nothing it holds and no note under it is given a width of its own, a field by
    /// a style either: each narrows with the page by its most width, one of the row's (OptionRow.EditorWidth, a number
    /// box's OptionRow.NumberWidth), a field and what follows it through NarrowingField, a drop-down at the form's most the
    /// theme gives (FormDropDown). The theme explorer is passed over: it lays itself under the theme picker's row at the
    /// widths that row took.</summary>
    [AvaloniaFact]
    public void No_settings_editor_or_note_is_given_a_width_of_its_own_each_narrowing_by_the_rows_most()
    {
        MainWindow window = Open(1400, 880);
        SettingsDialog? settings = null;
        try
        {
            settings = window.ShowSettings();
            Ui.Settle();
            double[] mosts = [OptionRow.EditorWidth, OptionRow.NumberWidth];
            double dropDown = (double)window.FindResource(TokenKeys.DropDownWidth)!;
            List<string> held = [];
            int rows = 0;
            foreach (string page in settings.Pages.ToList())
            {
                settings.Page = page;
                Ui.Settle();
                foreach (OptionRow row in settings.GetVisualDescendants().OfType<OptionRow>())
                {
                    rows++;
                    IEnumerable<Control> parts = row.Editor.GetSelfAndLogicalDescendants().OfType<Control>()
                        .Where(c => !c.GetSelfAndLogicalAncestors().OfType<ThemeExplorer>().Any());
                    foreach (Control part in row.Note is null ? parts : parts.Append(row.Note))
                    {
                        string what = $"{page}, {row.Label.Text}: a {part.GetType().Name}";
                        // A field held to a width by a style (as every form's drop-down was) stands past a page narrower than it.
                        if (double.IsFinite(part.Width) && (part is ComboBox or TextBox or NumericUpDown
                            || Avalonia.Diagnostics.AvaloniaObjectExtensions.GetDiagnostic(part, Layoutable.WidthProperty).Priority == Avalonia.Data.BindingPriority.LocalValue))
                        {
                            held.Add($"{what} given a Width of {part.Width:0}");
                        }
                        if (double.IsFinite(part.MaxWidth) && !mosts.Contains(part.MaxWidth) && !(part is FormDropDown && part.MaxWidth == dropDown))
                        {
                            held.Add($"{what} given a MaxWidth of {part.MaxWidth:0}");
                        }
                        if (part is NarrowingField { Most: var most } && double.IsFinite(most) && !mosts.Contains(most) && most != dropDown)
                        {
                            held.Add($"{what} whose field is given a most of {most:0}");
                        }
                    }
                }
            }
            Assert.True(rows >= 100, $"Only {rows} rows of Settings were found: the search no longer reads them");
            Assert.True(held.Count == 0, $"These parts of Settings' rows hold widths of their own, so they stand at widths their neighbours don't and "
                + $"never narrow with the page: give each a MaxWidth of OptionRow.EditorWidth (a number box OptionRow.NumberWidth), or a field "
                + $"and its button a NarrowingField:\n{string.Join("\n", held.Distinct().Take(60))}");
        }
        finally
        {
            settings?.Close();
            Ui.Settle();
            window.Close();
        }
    }

    /// <summary>Every list's rows, a drop-down's choices and a strip's tabs are made once by ReusedRows and handed each item
    /// shown there, so scrolling builds no row again: rows from an item template (ItemTemplate, a data template of any
    /// kind) or a container's template of its own fail here, naming the line.</summary>
    [Gate]
    [Fact]
    public void Every_lists_rows_are_made_once_by_ReusedRows_never_by_an_item_template()
    {
        Regex made = new(@"\b(?:FuncDataTemplate|FuncTreeDataTemplate|IDataTemplate|IRecyclingDataTemplate|ITreeDataTemplate|DataTemplates)\b(?:<(?:[^<>]|<[^<>]*>)+>)?|<(?:DataTemplate|TreeDataTemplate)\b|\bFuncControlTemplate<(?:ListBoxItem|ComboBoxItem|TreeViewItem)>");
        Regex given = new(@"\b(?:SelectionBox)?ItemTemplate\s*=\s*""?[\w.{} ]*(?:<(?:[^<>]|<[^<>]*>)+>)?|<\w+\.(?:SelectionBox)?ItemTemplate>");
        // The theme's own row template with its chosen mark (ChosenMark.Rows) shows the item as the theme's does.
        Regex container = new(@"<Style Selector=""[^""]*(?:ListBoxItem|ComboBoxItem)""[^>]*>(?:(?!</Style>).)*?Property=""(?:Content)?Template""(?!\s+Value=""\{x:Static app:ChosenMark\.)", RegexOptions.Singleline);
        List<string> found = [];
        foreach ((string path, string text) in UiFiles(theme: true).Where(f => !f.Path.EndsWith("/ReusedRows.cs", StringComparison.Ordinal)))
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }
                Match template = made.Match(lines[i]) is { Success: true } kind ? kind : given.Match(lines[i]);
                if (template.Success)
                {
                    found.Add($"{path}:{i + 1}: {template.Value.Trim()}");
                }
            }
            foreach (Match match in container.Matches(text))
            {
                found.Add($"{path}:{text.AsSpan(0, match.Index).Count('\n') + 1}: a ListBoxItem's Template");
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} lists make their rows from an item template, building a row for every item scrolled into view: "
            + $"give each a row made once through ReusedRows.Use, showing its DataContext (Showing):\n{string.Join("\n", found)}");
    }

    /// <summary>A list hears what its chosen row does by the keys and a double click only through ListKeys.Act (TreeKeys
    /// for a tree, which takes it), ahead of its rows, which take Enter and Space for their choice before a handler of the
    /// list hears them, so Enter does what a double click does in every list: a list's key or double click heard any other
    /// way (its KeyDown or DoubleTapped, a handler added for them, an override, or a XAML attribute) fails here, naming its
    /// line.</summary>
    [Fact]
    public void Every_lists_keys_and_double_click_are_heard_through_ListKeys()
    {
        Regex declared = new(@"\b(?:ListBox|TreeList)\??\s+(\w+)\b(?!\s*\()|\b(\w+)\s*=\s*new\s+(?:ListBox|TreeList)\b|<(?:ListBox|app:TreeList)\b[^>]*\bName=""(\w+)""");
        Regex subclass = new(@"\bclass\s+\w+\s*:\s*(?:ListBox|TreeList)\b");
        Regex heard = new(@"(?:\b([\w.]+)\.)?(?:(?:KeyDown|KeyUp|DoubleTapped)\s*[+-]=|AddHandler\(\s*(?:[\w.]+\.)?(?:KeyDownEvent|KeyUpEvent|DoubleTappedEvent)\b|override\s+void\s+(?:OnKeyDown|OnKeyUp|OnDoubleTapped)\()");
        Regex attribute = new(@"<(?:ListBox|app:TreeList)\b[^>]*\b(?:KeyDown|KeyUp|DoubleTapped)=""");
        (string Path, string Text)[] files = [.. UiFiles()];
        Dictionary<string, HashSet<string>> lists = files.ToDictionary(f => f.Path,
            f => declared.Matches(f.Text).SelectMany(m => m.Groups.Values.Skip(1)).Where(g => g.Success).Select(g => g.Value).ToHashSet(StringComparer.Ordinal));
        HashSet<string> anywhere = [.. lists.Values.SelectMany(n => n)];
        List<string> found = [];
        foreach ((string path, string text) in files.Where(f => !f.Path.EndsWith("/ListKeys.cs", StringComparison.Ordinal)))
        {
            bool isList = subclass.IsMatch(text);
            foreach (Match match in heard.Matches(text))
            {
                string line = text.Split('\n')[LineOf(text, match.Index) - 1].TrimStart();
                string? on = match.Groups[1].Success ? match.Groups[1].Value : null;
                bool list = on is null or "this" ? isList : on.Contains('.') ? anywhere.Contains(on[(on.LastIndexOf('.') + 1)..]) : lists[path].Contains(on);
                if (list && !line.StartsWith("//", StringComparison.Ordinal))
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}: {match.Value.Trim()}");
                }
            }
            found.AddRange(attribute.Matches(text).Select(m => $"{path}:{LineOf(text, m.Index)}: {m.Value.Trim()}"));
        }
        Assert.True(anywhere.Count > 30 && files.Sum(f => Regex.Count(f.Text, @"\b(?:ListKeys\.Act|TreeKeys\.Attach<)")) > 40,
            $"The rule found {anywhere.Count} lists declared and few taking ListKeys, so it no longer reads how lists are written");
        Assert.True(found.Count == 0, $"{found.Count} lists hear their keys or double click by a handler of their own, which the rows' own Enter and Space go "
            + $"ahead of, or which does other than Enter does: give each its acts through ListKeys.Act (TreeKeys.Attach for a tree):\n{string.Join("\n", found)}");
    }

    /// <summary>A menu's item is made by the shared pieces (ListMenus, MenuChoice, and the window's action menus in
    /// MainWindow.Menu), so every item shows only keys the keymap gives its action or its list's row does it by, and greys
    /// saying why: a MenuItem made anywhere else, or keys set beside an item by hand, fails here, naming its line. The
    /// terminal's menu stays its own: its keys are the terminal's, which no keymap gives, in a project without the App's.</summary>
    [Fact]
    public void Every_menu_item_is_made_by_the_shared_menu_pieces_and_shows_no_keys_written_by_hand()
    {
        string[] pieces = ["/ToolWindows/ListMenus.cs", "/MenuChoice.cs", "/MainWindow.Menu.cs"];
        string[] apart = ["src/Passenger.Terminal/TerminalView.cs"];
        Regex made = new(@"\bnew\s+MenuItem\b|\bMenuItem\s+\w+\s*=\s*new\s*\(|\bMenuItem\s+\w+\([^)]*\)\s*=>\s*new\s*\(");
        // The keymap's keys for an action are shown by ActionRegistry.ShowKeys, a list act's by ListMenus.
        Regex keys = new(@"\bInputGesture\s*=");
        string[] found = [.. UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && !pieces.Any(p => f.Path.EndsWith(p, StringComparison.Ordinal)) && !apart.Contains(f.Path))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line.TrimStart())))
            .Where(l => !l.Text.StartsWith("//", StringComparison.Ordinal) && (made.IsMatch(l.Text) || (keys.IsMatch(l.Text) && !l.Path.EndsWith("/ActionRegistry.cs", StringComparison.Ordinal))))
            .Select(l => $"{l.Path}:{l.Line}: {l.Text}")];
        Assert.True(found.Length == 0, $"{found.Length} menu items are made, or given keys, by hand, so their keys are written beside them rather than taken from "
            + $"the keymap or the list's acts, and one greyed may say nothing of why: make each through ListMenus (Item, Keyed, Open, Source, Edit, Remove, "
            + $"Toggle, Sub, Heading, Notice) or MenuChoice:\n{string.Join("\n", found)}");
        Assert.Matches(made, "            MenuItem item = new() { Header = name };");
        Assert.Matches(made, "        static MenuItem Heading(string text) => new() { Header = text, IsEnabled = false };");
        Assert.Matches(made, "menu.Items.Add(new MenuItem { Header = \"Local\", IsEnabled = false });");
        Assert.DoesNotMatch(made, "        MenuItem item = ListMenus.Item(header, run);");
    }

    /// <summary>Every key the window names in words (a tip, a placeholder, a status, a label, a note) is the keymap's for an
    /// action (ActionTips) or KeyWords' for a key no action owns, so a person on another keymap or system reads their own
    /// keys: a key written as words by hand (a modifier's symbol, a modifier's name before a + or held for something, a
    /// function key in brackets, a key pressed or held by its name) and a key turned into words anywhere but KeyWords fail
    /// here, naming the line. The formatter itself (Keymap.cs) is apart.</summary>
    [Fact]
    public void Every_key_named_in_words_is_the_keymaps_or_KeyWords()
    {
        Regex byHand = new(@"[⌘⌥⇧⌃⏎⎋⇥⌫⌦↩]|\b(?:Ctrl|Control|Alt|Option|Shift|Cmd|Command|Win|Meta)\+|\((?:Shift\+)?F\d{1,2}\)"
            + @"|\b(?:Ctrl|Alt|Shift|Cmd|Option)\s+for\s"
            + @"|\b(?:[Pp]ress|[Hh]old|by|or|and|with)\s+(?:Escape|Esc|Enter|Return|Tab|Backspace|Space|Ctrl|Alt|Shift|Cmd)\b(?!\s*(?:key|bar)s?\b)"
            + @"|\b(?:Escape|Esc|Enter|Return|Tab|Backspace)\s+to\s+(?!the\b)[a-z]");
        Regex formatted = new(@"\.Display\(\)|\bKeystroke\.Shown\(");
        string[] apart = ["/KeyWords.cs", "src/Passenger.Editor/Keymap.cs"];
        string[] found = [.. UiFiles().Where(f => !apart.Any(a => f.Path.EndsWith(a, StringComparison.Ordinal)))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line.Trim())))
            .Where(l => !l.Text.StartsWith("//", StringComparison.Ordinal) && !l.Text.StartsWith("<!--", StringComparison.Ordinal)
                && (byHand.IsMatch(l.Text) || formatted.IsMatch(l.Text)))
            .Select(l => $"{l.Path}:{l.Line}: {l.Text}")];
        Assert.True(found.Length == 0, $"{found.Length} keys are named in words by hand, so a person on another keymap or system reads keys that "
            + $"are not theirs: name an action's keys through ActionTips and any other key through KeyWords:\n{string.Join("\n", found)}");
        Assert.Matches(byHand, "    private static string Keys(string key) => OperatingSystem.IsMacOS() ? $\"⌘{key}\" : $\"Ctrl+{key}\";");
        Assert.Matches(byHand, "        ToolTip.SetTip(maskOn, \"File mask (Alt+K)\");");
        Assert.Matches(byHand, "        Button jump = ToolButtons.Of(IconKeys.Edit, \"Jump to Source (F4)\", AskForSource);");
        Assert.Matches(byHand, "        ToolTip.SetTip(label, \"Drag left or right to change it: Shift for faster, Alt for finer\");");
        Assert.Matches(byHand, "            parts.Say($\"Select Element: point at a control in {live.Name} and click it, or press Escape there\");");
        Assert.Matches(byHand, "        Message?.Invoke(this, \"Smart Step Into: choose a call with the arrow keys or Tab, Enter to step into it, Escape to leave\");");
        Assert.Matches(byHand, "            \"A plain click on a file's row opens it, the keys staying in the Explorer. Off, a double-click or Enter opens it. \"");
        Assert.Matches(formatted, "            string keys = AppActions.Keymap().ShortcutsFor(AppActions.HighlightUsages).FirstOrDefault()?.Display() ?? \"the action\";");
        Assert.DoesNotMatch(byHand, "        ToolTip.SetTip(maskOn, ActionTips.Named(\"File mask\", KeyWords.Of(MaskKey)));");
        Assert.DoesNotMatch(byHand, "        new(D.EditorPage, \"Show whitespaces\", \"whitespace spaces tabs dots arrows invisible characters appearance\"));");
        Assert.DoesNotMatch(byHand, "        new(D.EditorPage, \"Vim mode's Ctrl keys\", \"vim keys ctrl control conflict keymap\",");
    }

    /// <summary>A peer of Passenger's tells a screen reader of a change only through PeerChanges.Tell, which tells a pattern's
    /// property only while the peer gives that pattern, as macOS's bridge asks the pattern as it hears the change and a
    /// refusal there ends Passenger: a RaisePropertyChangedEvent written anywhere else fails here, naming its line, as does a
    /// pattern's property of Avalonia's that PeerChanges cannot tell the pattern of.</summary>
    [Fact]
    public void Every_peers_change_is_told_through_PeerChanges_which_tells_a_patterns_only_while_the_peer_gives_it()
    {
        List<string> found = [];
        int told = 0;
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            told += Regex.Count(text, @"\bPeerChanges\.Tell\(");
            if (!path.EndsWith("/PeerChanges.cs", StringComparison.Ordinal))
            {
                found.AddRange(Regex.Matches(text, @"\bRaisePropertyChangedEvent\b").Select(m => $"{path}:{LineOf(text, m.Index)}"));
            }
        }
        List<string> unknown = [];
        foreach (Type identifiers in typeof(AutomationProperty).Assembly.GetExportedTypes().Where(t => t.Namespace == "Avalonia.Automation" && t.Name.EndsWith("PatternIdentifiers", StringComparison.Ordinal)))
        {
            string provider = $"Avalonia.Automation.Provider.I{identifiers.Name[..^"PatternIdentifiers".Length]}Provider";
            foreach (PropertyInfo property in identifiers.GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(AutomationProperty)))
            {
                if (PeerChanges.PatternOf((AutomationProperty)property.GetValue(null)!)?.FullName != provider)
                {
                    unknown.Add($"{identifiers.Name}.{property.Name}");
                }
            }
        }
        Assert.True(told >= 3, $"The rule found {told} changes told through PeerChanges, so it no longer reads how peers tell one");
        Assert.True(found.Count == 0, $"{found.Count} changes are told to a reader past PeerChanges.Tell, which may tell a pattern's change while the peer "
            + $"refuses the pattern, so macOS's bridge asks it and ends Passenger:\n{string.Join("\n", found)}");
        Assert.True(unknown.Count == 0, $"PeerChanges tells these properties as of no pattern, so a change of one is told while the peer may refuse its pattern: {string.Join(", ", unknown)}");
    }

    /// <summary>A row a list reuses draws its icons with RowIcon, one control where a PathIcon is five that the list styles
    /// again whole each time it brings the row back into view: a PathIcon made in a row's factory (the body that hands back
    /// the row Showing its item) or in a row's own class fails here, naming its line; a button's glyph is the button's.</summary>
    [Gate]
    [Fact]
    public void A_reused_rows_icons_are_RowIcons_never_PathIcons()
    {
        Regex rowClass = new(@"\bclass \w*Row\w*\s*:\s*(?:StackPanel|DockPanel|Grid|Panel|ShrinkingRow)\b");
        List<string> found = [];
        int rows = 0;
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            HashSet<int> seen = [];
            IEnumerable<int> starts = Regex.Matches(text, @"\.Showing<").Select(m => Enclosing(text, m.Index))
                .Concat(rowClass.Matches(text).Select(m => text.IndexOf('{', m.Index)));
            foreach (int open in starts.Where(o => o >= 0 && seen.Add(o)))
            {
                rows++;
                int close = Closing(text, open);
                foreach (Match icon in Regex.Matches(text[open..close], @"\bPathIcon\b"))
                {
                    int at = open + icon.Index;
                    int lineStart = text.LastIndexOf('\n', at) + 1;
                    int lineEnd = text.IndexOf('\n', at);
                    if (!text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].Contains(".Content", StringComparison.Ordinal))
                    {
                        found.Add($"{path}:{text.AsSpan(0, at).Count('\n') + 1}");
                    }
                }
            }
        }
        Assert.True(rows > 40, $"Only {rows} rows a list reuses were found: the search no longer reads them");
        Assert.True(found.Count == 0, $"{found.Count} PathIcons are made in rows a list reuses, each five controls styled again as its row comes back into view: "
            + $"draw each with RowIcon:\n{string.Join("\n", found.Distinct())}");

        // The opening brace of the innermost block holding a place, or -1.
        static int Enclosing(string text, int at)
        {
            int depth = 0;
            for (int i = at - 1; i >= 0; i--)
            {
                if (text[i] == '}')
                {
                    depth++;
                }
                else if (text[i] == '{' && depth-- == 0)
                {
                    return i;
                }
            }
            return -1;
        }

        static int Closing(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
                if (depth == 0)
                {
                    return i;
                }
            }
            return text.Length;
        }
    }

    /// <summary>Every tag, symbol kind's badge, solution's initials and filter chip is made by Tags or FilterChip, so a tag's
    /// words stand at one step and colour, a badge at one size, and a filter clears one way everywhere: their classes named
    /// anywhere else fail here, naming the line. The title bar's project badge, made in its XAML, wears the initials' class.</summary>
    [Fact]
    public void Every_tag_badge_and_chip_is_made_by_Tags_or_FilterChip()
    {
        Regex byHand = new(@"\bStyleClasses\.(?:Tag|KindBadge|Initials|Chip)\b|Classes=""[^""]*\b(?:tag|kindBadge|initials|chip)\b");
        string[] owners = ["/Tags.cs", "/FilterChip.cs"];
        string[] found = [.. UiFiles().Where(f => !owners.Any(o => f.Path.EndsWith(o, StringComparison.Ordinal)))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text) && !(l.Path.EndsWith("/MainWindow.axaml", StringComparison.Ordinal) && l.Text.Contains("Name=\"ProjectBadge\"", StringComparison.Ordinal)))
            .Select(l => $"{l.Path}:{l.Line}: {l.Text.Trim()}")];
        Assert.True(found.Length == 0, $"{found.Length} tags, badges or chips are made by hand, so their words' size, colour and room drift from every "
            + $"other's: make each with Tags (Of, Method, Kind, Initials) or a FilterChip:\n{string.Join("\n", found)}");
        Assert.Matches(byHand, "        return new Border { Classes = { StyleClasses.Tag }, Child = text };");
        Assert.Matches(byHand, "                <Border Classes=\"kindBadge\" Width=\"16\">");
        Assert.DoesNotMatch(byHand, "        Border tag = Tags.Of(words);");
    }

    /// <summary>A link stands at the size of the words beside it, by the type ramp's class, and takes the keys as every other
    /// does (its shortcut, where it has one, still its own): a link given a font size or kept off the keys fails here,
    /// naming its line.</summary>
    [Fact]
    public void Every_link_takes_a_step_of_the_ramp_and_the_keys()
    {
        Regex link = new(@"\bStyleClasses\.Link\b|Classes=""[^""]*\blink\b");
        Regex own = new(@"\bFontSize\b|\bFocusable\s*=\s*false\b|\bFocusable=""False""");
        string[] found = [.. UiFiles().SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => link.IsMatch(l.Text) && own.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}: {l.Text.Trim()}")];
        Assert.True(found.Length == 0, $"{found.Length} links take a size of their own or are kept off the keys, so they stand or answer apart from "
            + $"every other: give each the ramp's class of the words it sits in, and leave it the keys:\n{string.Join("\n", found)}");
        Assert.Matches(link, "        Button open = new() { Classes = { StyleClasses.Link }, Focusable = false };");
    }

    /// <summary>What a list or a pane says while it has nothing to show is an EmptyState, so its words stand, wrap and link
    /// to what would fill it one way everywhere: the empty words' class given anywhere else fails here, naming its line.</summary>
    [Fact]
    public void Every_empty_state_is_an_EmptyState()
    {
        Regex byHand = new(@"\bStyleClasses\.EmptyText\b|Classes=""[^""]*\bemptyText\b");
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/EmptyState.cs", StringComparison.Ordinal))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}")];
        Assert.True(found.Length == 0, $"{found.Length} empty states are made by hand, so their words, room and offer drift from every other's: "
            + $"make each an EmptyState:\n{string.Join("\n", found)}");
        Assert.Matches(byHand, "        placeholder = new TextBlock { Classes = { StyleClasses.EmptyText } };");
        Assert.Matches(byHand, "                <TextBlock Name=\"Hint\" Classes=\"emptyText\" Text=\"Nothing open\" />");
    }

    /// <summary>Words to read and copy are SelectableWords, laid out whole at the width they measured at every display
    /// scale: a SelectableTextBlock made or derived from anywhere else fails here, naming its line (#2046).</summary>
    [Fact]
    public void Every_selectable_text_is_SelectableWords()
    {
        Regex byHand = new(@"\bnew SelectableTextBlock\b|:\s*SelectableTextBlock\b|\bSelectableTextBlock\s+\w+\s*=\s*new\(|<SelectableTextBlock\b");
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/SelectableWords.cs", StringComparison.Ordinal))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}: {l.Text.Trim()}")];
        Assert.True(found.Length == 0, $"{found.Length} selectable texts are Avalonia's own, which at a display scale where their width falls on a pixel "
            + $"wrap their last word out of sight: make each a SelectableWords:\n{string.Join("\n", found)}");
        Assert.Matches(byHand, "        text = new SelectableTextBlock");
        Assert.Matches(byHand, "public sealed class MarkdownText : SelectableTextBlock");
        Assert.Matches(byHand, "        SelectableTextBlock words = new()");
        Assert.DoesNotMatch(byHand, "    <Style Selector=\"SelectableTextBlock.mdText\">");
    }

    /// <summary>A tree row's chevron is a RowExpander, one column wide in every tree, turned while its row is open and kept
    /// for a row with nothing under it: a chevron of a row's own (RowIcon.Chevron or a chevron's key given to a RowIcon,
    /// swapped Right for Down) fails here, naming its line. A fold of a form's (FormFold) is no tree's row.</summary>
    [Fact]
    public void Every_tree_rows_chevron_is_a_RowExpander()
    {
        Regex byHand = new(@"\bRowIcon\.Chevron\(\)|\bDataKey\s*=[^;]*\bIconKeys\.Chevron(?:Down|Right)\b");
        string[] owners = ["/RowIcon.cs", "/RowExpander.cs", "/RowGlyphs.cs", "/FormFold.cs"];
        string[] found = [.. UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && !owners.Any(o => f.Path.EndsWith(o, StringComparison.Ordinal)))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}: {l.Text.Trim()}")];
        Assert.True(found.Length == 0, $"{found.Length} tree rows draw a chevron of their own, so it stands, turns and takes a press otherwise than "
            + $"every other tree's: give each row a RowExpander:\n{string.Join("\n", found)}");
        Assert.Matches(byHand, "            chevron.DataKey = row.IsExpanded ? IconKeys.ChevronDown : IconKeys.ChevronRight;");
        Assert.Matches(byHand, "        RowIcon chevron = RowIcon.Chevron();");
    }

    /// <summary>A RowIcon is one of two sizes, never one of its own: a row's or a tab's icon beside words (RowIcon.Size, its
    /// own) or a glyph (RowIcon.Glyph: a chevron, a tab's close), so like icons stand alike in every list. A RowIcon given a
    /// width or height outside RowIcon.cs fails here, naming its line.</summary>
    [Fact]
    public void A_rows_icon_is_a_RowIcons_size_never_one_of_its_own()
    {
        Regex made = new(@"(?:\bRowIcon\s+(\w+)\s*=\s*new\(\)|\b(\w+)\s*=\s*new RowIcon\b)\s*\{");
        Regex named = new(@"\bRowIcon\s+(\w+)\s*[=;]");
        Regex sized = new(@"(?<![\w.])(?:Width|Height)\s*=(?!=)");
        List<string> found = [];
        int icons = 0;
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && !f.Path.EndsWith("/RowIcon.cs", StringComparison.Ordinal)))
        {
            foreach (Match match in made.Matches(text))
            {
                icons++;
                int open = match.Index + match.Length - 1;
                if (sized.IsMatch(Braced(text, open)))
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}");
                }
            }
            foreach (string name in named.Matches(text).Select(m => m.Groups[1].Value).Distinct())
            {
                foreach (Match set in Regex.Matches(text, $@"\b{name}\.(?:Width|Height)\s*=(?!=)"))
                {
                    // A test's state mark, each sized to its glyph's weight in the column of the marks.
                    if (!(path.EndsWith("ToolWindows/TestsView.cs", StringComparison.Ordinal) && name == "state"))
                    {
                        found.Add($"{path}:{LineOf(text, set.Index)}");
                    }
                }
            }
        }
        Assert.True(icons > 20, $"Only {icons} RowIcons were found made: the search no longer reads them");
        Assert.True(found.Count == 0, $"{found.Count} RowIcons are given a size of their own, so like icons stand larger or smaller from list to list: "
            + $"leave each its own size (RowIcon.Size), or make a glyph with RowIcon.Glyph:\n{string.Join("\n", found.Distinct())}");

        static string Braced(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
                if (depth == 0)
                {
                    return text[open..i];
                }
            }
            return text[open..];
        }
    }

    /// <summary>A row, or a part of one, that is there but off (can't be chosen, a feed not used, a file cut, a style that
    /// does not apply) is drawn faint one way, ListRow.Dim: an opacity of a number of its own outside the theme (but none or
    /// whole, a part hidden or shown) fails here, naming its line.</summary>
    [Fact]
    public void A_faint_row_is_dimmed_by_ListRow_never_an_opacity_of_its_own()
    {
        Regex assigned = new(@"(?<!\w)Opacity\s*=(?![=>])");
        Regex attribute = new(@"(?<![\w.])Opacity=""([^""{][^""]*)""");
        Regex number = new(@"(?<![\w.])\d+(?:\.\d+)?(?![\w.])");
        bool OwnNumber(string written) => number.Matches(written).Any(m => m.Value is not ("0" or "1"));
        List<string> found = [];
        foreach ((string path, string text) in UiFiles().Where(f => !f.Path.EndsWith("/ListRow.cs", StringComparison.Ordinal)))
        {
            if (path.EndsWith(".axaml", StringComparison.Ordinal))
            {
                found.AddRange(attribute.Matches(text).Where(m => OwnNumber(m.Groups[1].Value)).Select(m => $"{path}:{LineOf(text, m.Index)}: {m.Value}"));
                continue;
            }
            string code = string.Join('\n', text.Split('\n').Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));
            foreach (Match match in assigned.Matches(code))
            {
                string written = Expression(code, match.Index + match.Length);
                if (OwnNumber(written))
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}: {match.Value}{written.Trim()}");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} controls are made faint by an opacity of their own, so an off row stands fainter or bolder "
            + $"from list to list: dim each with ListRow.Dim (or name the faintness once where it is no row's):\n{string.Join("\n", found)}");
    }

    /// <summary>A row's detail at its end (a path, a kind, keys, when) is ListRow.DetailText, muted at the caption's size
    /// and cut where the row has no room: the detail's margin given to words made by hand fails here, naming its line.</summary>
    [Fact]
    public void Every_rows_detail_at_its_end_is_ListRows_detail()
    {
        Regex byHand = new(@"\bListRow\.Detail\b(?!Text)");
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/ListRow.cs", StringComparison.Ordinal))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}")];
        Assert.True(found.Length == 0, $"{found.Length} rows make their detail by hand, so its size, colour and cut drift from every other row's: "
            + $"make each with ListRow.DetailText:\n{string.Join("\n", found)}");
    }

    /// <summary>No font family is written by name outside the theme files (WindowFonts), but the code font Passenger carries
    /// (CodeEditor.CodeFont, CodeFonts' fallback, carried through FallbackFonts): the window's words take the typeface Settings
    /// puts in force and code the code font, so a family named anywhere else draws in a font no setting reaches.</summary>
    [Fact]
    public void No_font_family_is_named_outside_the_theme_files_but_the_carried_code_font()
    {
        Regex named = new(@"\bnew\s+(?:FontFamily|Typeface)\s*\(\s*""|\b(?:FontFamily|Typeface)\s+\w+\s*=\s*new\s*\(\s*""|\bFontFamily\s*=\s*""|\bFontFamily\.Parse\s*\(\s*""|\bFontFamily=""(?!\{)");
        string[] found = [.. UiFiles().SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && named.IsMatch(l.Text)
                && !(l.Path.EndsWith("/CodeEditor.cs", StringComparison.Ordinal) && l.Text.Contains("CodeFont = FallbackFonts.Carry(new FontFamily(", StringComparison.Ordinal)))
            .Select(l => $"{l.Path}:{l.Line}: {l.Text.Trim()}")];
        Assert.True(found.Length == 0, $"{found.Length} font families are named outside Theme/, drawing in a font no setting reaches: take the window's "
            + $"(TextElement.GetFontFamily, the font a control inherits) or the code font in force (CodeFonts):\n{string.Join("\n", found)}");
    }

    /// <summary>Muted words are made one way, by the muted class (MutedText, or the class on a TextBlock of its own), so a
    /// change to how muted words look is made once: a TextBlock's foreground bound by hand to the muted colour fails here.</summary>
    [Gate]
    [Fact]
    public void Muted_words_take_the_muted_class_never_the_colour_bound_by_hand()
    {
        Regex byHand = new(@"Bind\(TextBlock\.ForegroundProperty, \w+\.GetResourceObservable\(TokenKeys\.TextMuted\)\)");
        string[] found = [.. UiFiles().SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}")];
        Assert.True(found.Length == 0, $"{found.Length} TextBlocks bind the muted colour by hand: give each the muted class (MutedText.Of, or Classes.Add(StyleClasses.Muted)):\n"
            + string.Join("\n", found));
    }

    /// <summary>Words saying what is wrong are coloured one way, by the error class, so a change to how they look is made
    /// once: a TextBlock's foreground bound by hand to the error colour fails here.</summary>
    [Fact]
    public void Error_words_take_the_error_class_never_the_colour_bound_by_hand()
    {
        Regex byHand = new(@"Bind\(\w+\.ForegroundProperty, \w+\.GetResourceObservable\(TokenKeys\.Error\)\)");
        string[] found = [.. UiFiles().SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}")];
        Assert.True(found.Length == 0, $"{found.Length} TextBlocks bind the error colour by hand: give each the error class (Classes.Add(StyleClasses.Error)):\n"
            + string.Join("\n", found));
    }

    /// <summary>A dialog's OK and Cancel are made one way, by DialogFooter (Default, Cancel, OkCancel, Popup), so each
    /// stands in the platform's order and Cancel takes Escape where it should: a button worded OK or Cancel made anywhere
    /// else fails here, naming its line.</summary>
    [Fact]
    public void A_dialogs_OK_and_Cancel_are_made_by_DialogFooter()
    {
        Regex byHand = new(@"\bContent\s*=\s*""(?:OK|Cancel)""");
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/DialogFooter.cs", StringComparison.Ordinal))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}")];
        Assert.True(found.Length == 0, $"{found.Length} OK or Cancel buttons are made by hand, so their order and keys drift from every other dialog's: "
            + $"make each with DialogFooter (Default, Cancel, OkCancel, or Popup in a popup):\n{string.Join("\n", found)}");
    }

    /// <summary>A form's drop-down is a FormDropDown, which narrows with its room up to the form's most, so none stands past
    /// a form narrower than it and every form's end where the others' do: the form's class given to a drop-down by hand,
    /// which held it to a width of its own, or a plain drop-down made in a dialog's file but a table's cell, fails here,
    /// naming its line.</summary>
    [Fact]
    public void Every_forms_drop_down_is_a_FormDropDown_narrowing_with_its_room()
    {
        Regex byHand = new(@"\bStyleClasses\.Form\b");
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/FormDropDown.cs", StringComparison.Ordinal))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}")];
        Assert.True(found.Length == 0, $"{found.Length} drop-downs are given a form's class by hand, so they keep no form's width: make each a FormDropDown:\n{string.Join("\n", found)}");

        // A request line's method stands as wide as its words before the address it begins, no form's field.
        string[] apart = ["Endpoints/InteractiveTestDialog.cs:Method"];
        Regex plain = new(@"\b(?:(\w+)\s*=\s*new\s+ComboBox\b|ComboBox\s+(\w+)\s*=\s*new\s*\(\s*\))\s*\{?");
        List<string> plainOnes = [];
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith("Dialog.cs", StringComparison.Ordinal) || f.Path.EndsWith("Dialogs.cs", StringComparison.Ordinal)))
        {
            foreach (Match match in plain.Matches(text))
            {
                string name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                string own = match.Value.EndsWith('{') ? Whole(text, match.Index + match.Length) : string.Empty;
                if (!own.Contains("StyleClasses.Cell", StringComparison.Ordinal) && !apart.Any(a => path.EndsWith(a.Split(':')[0], StringComparison.Ordinal) && a.EndsWith(":" + name, StringComparison.Ordinal)))
                {
                    plainOnes.Add($"{path}:{LineOf(text, match.Index)}: {name}");
                }
            }
        }
        Assert.True(plainOnes.Count == 0, $"{plainOnes.Count} drop-downs of a dialog's form are plain ones, stretched to the dialog's edge or held to a width of their own, "
            + $"where every form's drop-down ends where the others' do: make each a FormDropDown:\n{string.Join("\n", plainOnes)}");
        Assert.Matches(plain, "        From = new ComboBox { Name = \"ScriptFrom\" };");
        Assert.Matches(plain, "    private readonly ComboBox stepBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };");
        Assert.DoesNotMatch(plain, "    private readonly ComboBox stepBox = new FormDropDown();");
    }

    /// <summary>Every search field is a SearchBox, its magnifier, name and words at the dense step one way in every tool
    /// window, dialog and page, and every other field takes the one field look of the base TextBox: the search class given
    /// anywhere but SearchBox and the find bar's XAML fails here, naming its line.</summary>
    [Fact]
    public void Every_search_field_is_a_SearchBox_and_every_other_field_the_one_field()
    {
        Regex byHand = new(@"\bStyleClasses\.Search\b|Classes=""[^""]*\bsearch\b");
        string[] owners = ["/SearchBox.cs", "/Editors/FindBar.axaml"];
        string[] found = [.. UiFiles().Where(f => !owners.Any(o => f.Path.EndsWith(o, StringComparison.Ordinal)))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}: {l.Text.Trim()}")];
        Assert.True(found.Length == 0, $"{found.Length} fields are given the search class by hand, so a search stands without its magnifier or name, "
            + $"or a form's field at a search's size: make a search a SearchBox (SearchBox.InPopup in a popup that is a search), and leave any other "
            + $"field the base TextBox's:\n{string.Join("\n", found)}");
        Assert.Matches(byHand, "        search = new TextBox { Classes = { StyleClasses.Search }, PlaceholderText = \"Search packages\" };");
        Assert.Matches(byHand, "            <TextBox Name=\"Query\" Classes=\"search\" />");
    }

    /// <summary>Every tool window's drop-down is a ToolDropDown: a bar's, a popup's scope, a value chosen in a row, each at
    /// the dense step, one height and one least width, named where words stand before it. One given the compact class by
    /// hand, or a plain drop-down made in a tool window's or a pane's file (but a table's cell, a form's FormDropDown and
    /// Settings' theme picker, which narrows as a Settings row does), fails here, naming its line.</summary>
    [Fact]
    public void Every_tool_windows_drop_down_is_a_ToolDropDown()
    {
        Regex made = new(@"\b(?:(\w+)\s*=\s*new\s+ComboBox\b|ComboBox\s+(\w+)\s*(?:\{\s*get;\s*\}\s*)?=\s*new\s*\(\s*\))\s*\{?");
        string[] apart = ["/Settings/ThemePicker.cs"];
        List<string> found = [];
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            bool form = path.EndsWith("Dialog.cs", StringComparison.Ordinal) || path.EndsWith("Dialogs.cs", StringComparison.Ordinal) || apart.Any(a => path.EndsWith(a, StringComparison.Ordinal));
            foreach (Match match in made.Matches(text))
            {
                string name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                string own = match.Value.EndsWith('{') ? Whole(text, match.Index + match.Length) : string.Empty;
                if (own.Contains("StyleClasses.Compact", StringComparison.Ordinal) || !form && !own.Contains("StyleClasses.Cell", StringComparison.Ordinal))
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}: {name}");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} tool windows' drop-downs are made by hand, so they stand at a size, height or width of their own "
            + $"beside the search field and buttons of their bar: make each a ToolDropDown (ToolDropDown.Labelled for words before it):\n{string.Join("\n", found)}");
        Assert.Matches(made, "        chooser = new ComboBox { Classes = { StyleClasses.Compact }, MinWidth = 130 };");
        Assert.Matches(made, "    public ComboBox OrderBox { get; } = new() { Width = 120 };");
        Assert.DoesNotMatch(made, "        ComboBox chooser = new ToolDropDown { Name = name };");
    }

    /// <summary>A value typed over in its row is a ValueField, one height and one look in the debugger's values, the Live
    /// Visual Tree's and a Godot scene's properties: the in-row class given anywhere but ValueField fails here, naming its
    /// line, as does code shown through the code font alone, which stands at the body's size, a third size of code beside
    /// the editor's and a pane's: code takes the code step (StyleClasses.Code), and InCodeFont only a run in running text or
    /// an element given its step (a badge's letters, TypeRamp.Code).</summary>
    [Fact]
    public void A_value_typed_in_its_row_is_a_ValueField_and_code_shown_takes_the_code_step()
    {
        Regex inRow = new(@"\bStyleClasses\.InRow\b");
        string[] typed = [.. UiFiles().Where(f => !f.Path.EndsWith("/ValueField.cs", StringComparison.Ordinal))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => inRow.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}: {l.Text.Trim()}")];
        Assert.True(typed.Length == 0, $"{typed.Length} values are typed over in a row of their own making: make each a ValueField:\n{string.Join("\n", typed)}");

        Regex codeFont = new(@"\.InCodeFont\(\)|\bFontFamily\s*=\s*[\w.]*\bCodeFont\b");
        Regex stepped = new(@"\bStyleClasses\.BadgeText\b|\bTypeRamp\.\w+|^\s*run\.InCodeFont\(\)");
        string[] alone = [.. UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => codeFont.IsMatch(l.Text) && !stepped.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}: {l.Text.Trim()}")];
        Assert.True(alone.Length == 0, $"{alone.Length} places show code in the code font at the body's size, a third size of code: give each "
            + $"StyleClasses.Code:\n{string.Join("\n", alone)}");
        Assert.Matches(codeFont, "        TextBlock code = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis }.InCodeFont();");
        Assert.Matches(codeFont, "        FontFamily = Passenger.Editor.CodeEditor.CodeFont,");
        Assert.Matches(stepped, "        TextBlock text = new TextBlock { Classes = { StyleClasses.BadgeText }, Text = method }.InCodeFont();");
    }

    /// <summary>A dialog opens at one of the widths <see cref="DialogWidths"/> gives a kind of dialog, so a form is one width
    /// wherever it opens: a dialog's width written as a number of its own, or one handed to a refactoring's frame, fails
    /// here, naming its line.</summary>
    [Fact]
    public void Every_dialog_opens_at_one_of_the_widths_of_its_kind()
    {
        Regex dialog = new(@"\bclass\s+\w+Dialog\b[^{]*:\s*(?:IdeWindow|RefactoringDialog)\b");
        Regex own = new(@"^\s+Width\s*=\s*\d+(?:\.\d+)?\s*;|(?:^\s+|""\s*,\s*)\d{3,4}\)\s*$", RegexOptions.Multiline);
        string[] found = [.. UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && dialog.IsMatch(f.Text))
            .SelectMany(f => own.Matches(f.Text).Select(m => $"{f.Path}:{LineOf(f.Text, m.Index)}: {m.Value.Trim()}"))];
        Assert.True(found.Length == 0, $"{found.Length} dialogs open at a width of their own, so like dialogs open at unlike widths: give each the width of its kind "
            + $"from DialogWidths (Question, Form, Wide, Workspace):\n{string.Join("\n", found)}");
        Assert.Matches(own, "        Width = 600;");
        Assert.Matches(own, "            720)");
        Assert.Matches(own, "            \"Specify a new name\", 720)");
        Assert.DoesNotMatch(own, "        Width = DialogWidths.Form;");
    }

    /// <summary>Work under way in a dialog is drawn as one line (Shell.axaml's ProgressBar), so a progress bar given a
    /// height of its own fails here, naming its line.</summary>
    [Fact]
    public void Every_progress_bar_is_the_one_line_of_the_theme()
    {
        Regex bar = new(@"\bnew\s+ProgressBar\b\s*\{|\bProgressBar\s+\w+\s*=\s*new\s*\(\s*\)\s*\{");
        string[] found = [.. UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(f => bar.Matches(f.Text).Where(m => Regex.IsMatch(Initializer(f.Text, m.Index + m.Length), @"\b(?:Min)?Height\s*="))
                .Select(m => $"{f.Path}:{LineOf(f.Text, m.Index)}"))];
        Assert.True(found.Length == 0, $"{found.Length} progress bars are given a height of their own, so work under way is drawn unlike in each dialog: "
            + $"leave the height to Shell.axaml's ProgressBar:\n{string.Join("\n", found)}");
    }

    /// <summary>A folder's or file's field and its Browse… button are a BrowseRow, which makes the button, so every one
    /// stands, reads and spaces one way: a Browse… button made anywhere else fails here, naming its line.</summary>
    [Fact]
    public void Every_Browse_button_is_a_BrowseRows_own()
    {
        Regex byHand = new(@"\bContent\s*=\s*""Browse(?:…|\.\.\.)""");
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/BrowseRow.cs", StringComparison.Ordinal))
            .SelectMany(f => byHand.Matches(f.Text).Select(m => $"{f.Path}:{LineOf(f.Text, m.Index)}"))];
        Assert.True(found.Length == 0, $"{found.Length} Browse… buttons are made by hand beside their fields: make each row with BrowseRow.Of(field, pick):\n{string.Join("\n", found)}");
    }

    /// <summary>A button that copies words for the person to paste elsewhere, then says Copied, is a CopyButton (Copy
    /// Command beside a refusal, each agent's setup in Settings), so every one copies and says so one way: a button worded
    /// Copy, Copy and a name, or Copied by hand fails here, naming its line.</summary>
    [Fact]
    public void Every_button_that_copies_then_says_Copied_is_a_CopyButton()
    {
        Regex byHand = new(@"\bContent\s*=\s*[^;,}]*""Cop(?:y(?: [A-Z]\w*)?|ied)""");
        string[] found = [.. UiFiles().Where(f => !f.Path.EndsWith("/CopyButton.cs", StringComparison.Ordinal))
            .SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => byHand.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}")];
        Assert.True(found.Length == 0, $"{found.Length} buttons copy and say so by hand, so their words and clipboard drift from every other copy's: "
            + $"make each a CopyButton:\n{string.Join("\n", found)}");
        Assert.Matches(byHand, "        other.Content = other == copy ? \"Copied\" : \"Copy\";");
        Assert.Matches(byHand, "    Button copyCommand = new() { Content = \"Copy Command\", IsVisible = false };");
        Assert.DoesNotMatch(byHand, "        Button copy = new() { Content = \"Copy and Close\" };");
    }

    /// <summary>A tool window's two panes, and an editor and its file's own pane (a preview), are a PaneSplit, so a side
    /// dock's width or a narrow window's editors stand them one over the other: panes side by side over a divider of their
    /// own stay that way however narrow, the Git window's log a line of letters. The dock's areas, an editor's splits and a
    /// dialog's panes are no two panes of one thing.</summary>
    [Fact]
    public void Every_tool_windows_two_panes_are_a_PaneSplit()
    {
        string[] found = [.. UiFiles()
            .Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && !f.Path.EndsWith("/PaneSplit.cs", StringComparison.Ordinal) && !f.Path.EndsWith("/HairlineSplitter.cs", StringComparison.Ordinal)
                && !f.Path.EndsWith("Dialog.cs", StringComparison.Ordinal) && !f.Path.Contains("/Docking/", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(f.Text, @"HairlineDivider\(GridResizeDirection\.Columns").Select(m => $"{f.Path}:{LineOf(f.Text, m.Index)}"))];
        Assert.True(found.Length == 0, $"{found.Length} tool windows' panes stand side by side however narrow the window: make each a PaneSplit:\n{string.Join("\n", found)}");
    }

    /// <summary>Every tool window's bars are the shared pieces, ToolBars.Top along its top and ToolBars.Side down its left,
    /// so each is one height, one width and one rule, and a banner over what it speaks of is Banner: a bar built by hand (a
    /// fixed height over a rule or docked at the top, a strip down the left beside one, a ToolColumn of its own) fails here,
    /// naming its line.</summary>
    [Gate]
    [Fact]
    public void Every_tool_windows_bars_are_the_shared_top_and_side_bars()
    {
        Regex made = new(@"\bnew(?:\s+[\w.]+)?\s*(?:\(\))?\s*\{");
        Regex column = new(@"\bToolColumn\s+\w+\s*=\s*new\b|\bnew\s+ToolColumn\b");
        List<string> found = [];
        foreach ((string path, string text) in UiFiles())
        {
            // A dialog has no tool bars: its bands over a list's columns are its own.
            if (!path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith("/ToolBars.cs", StringComparison.Ordinal) || path.EndsWith("Dialog.cs", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match match in made.Matches(text))
            {
                string own = Initializer(text, match.Index + match.Length);
                Match width = Regex.Match(own, @"\bWidth\s*=\s*(\d+)\b");
                bool narrow = !width.Success || int.Parse(width.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) <= 48;
                // Over a rule with a height or docked with no padding (a table's band has some), or docked with a height of its own.
                bool docked = own.Contains("Dock.Top", StringComparison.Ordinal);
                bool high = Regex.IsMatch(own, @"\bHeight\s*=\s*\d");
                bool top = (own.Contains("Thickness(0, 0, 0, 1)", StringComparison.Ordinal) && (high || (docked && !own.Contains("Padding", StringComparison.Ordinal))))
                    || (docked && high);
                bool side = own.Contains("Dock.Left", StringComparison.Ordinal) && narrow
                    && (own.Contains("Thickness(0, 0, 1, 0)", StringComparison.Ordinal) || (width.Success && own.Contains("Child", StringComparison.Ordinal)));
                if (top || side)
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}: a {(top ? "top" : "side")} bar built by hand");
                }
            }
            foreach (Match match in column.Matches(text).Where(_ => !path.EndsWith("/ToolColumn.cs", StringComparison.Ordinal)))
            {
                found.Add($"{path}:{LineOf(text, match.Index)}: a ToolColumn of its own");
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} tool window bars are built by hand, so their heights, widths and rules drift apart: make each with "
            + $"ToolBars.Top or ToolBars.Side (ToolBars.OverEditor over an editor, Banner for a line of words on the banner's tint):\n{string.Join("\n", found)}");
    }

    /// <summary>Every icon button is made by ToolButtons (the title bar's and the find bar's declared in XAML and given their
    /// look there, a glyph's small one inside a chip, a field or a tab), so each is one size, one radius and named and greyed
    /// one way: the tool button's, a counted filter's, the small button's and the in-field button's classes named elsewhere
    /// (a tab's × its tabs' own), and a control greyed with its tip of why written beside it by hand, fail here, naming the
    /// line.</summary>
    [Fact]
    public void Every_icon_button_is_made_and_greyed_with_why_by_ToolButtons()
    {
        (Regex Named, string[] Owners)[] owned =
        [
            (new(@"\bStyleClasses\.(?:Tool|Outcome)\b|Classes=""[^""]*\b(?:tool|outcome)\b"), ["/ToolButtons.cs"]),
            (new(@"\bnew\s+Button\b[^;]*\bStyleClasses\.Small\b|\bButton\s+\w+\s*=\s*new\(\)\s*\{[^;]*\bStyleClasses\.Small\b|<Button\b[^>]*\bClasses=""[^""]*\bsmall\b"), ["/ToolButtons.cs"]),
            (new(@"\bStyleClasses\.TabClose\b|Classes=""[^""]*\btabClose\b"), ["/ToolWindows/ToolTabs.cs", "/Editors/EditorGroup.cs"]),
            (new(@"\bStyleClasses\.InField\b|Classes=""[^""]*\binField\b"), ["/ToolWindows/FilterMenu.cs"]),
        ];
        // A menu's item is greyed by its own piece, ListMenus.Grey.
        string[] greyers = ["/ToolButtons.cs", "/ToolWindows/ListMenus.cs"];
        Regex greyed = new(@"\b([\w.]+)\.IsEnabled\s*=\s*[^;]*\bis\s+null\b");
        List<string> found = [];
        foreach ((string path, string text) in UiFiles())
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                foreach ((Regex named, string[] owners) in owned)
                {
                    if (named.IsMatch(lines[i]) && !owners.Any(o => path.EndsWith(o, StringComparison.Ordinal)))
                    {
                        found.Add($"{path}:{i + 1}: {lines[i].Trim()}");
                    }
                }
                if (greyed.Match(lines[i]) is { Success: true } grey && !greyers.Any(o => path.EndsWith(o, StringComparison.Ordinal))
                    && lines.Skip(Math.Max(0, i - 3)).Take(7).Any(l => l.Contains($"ToolTip.SetTip({grey.Groups[1].Value},", StringComparison.Ordinal)))
                {
                    found.Add($"{path}:{i + 1}: greyed with its tip of why by hand: {lines[i].Trim()}");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} icon buttons are made or greyed by hand, so their size, radius, keys and tips drift from every other's: "
            + $"make each with ToolButtons (Of, Glyph, Small, Toggle, Counted, or Give for one declared elsewhere) and grey it with ToolButtons.Grey:\n{string.Join("\n", found)}");
        Assert.Matches(owned[0].Named, "            Button = new Button { Classes = { StyleClasses.Tool }, Content = glyph };");
        Assert.Matches(owned[0].Named, "                    <Button Name=\"PreviousButton\" Classes=\"tool\" ToolTip.Tip=\"Previous occurrence\">");
        Assert.DoesNotMatch(owned[0].Named, "                    <Border Classes=\"toolbarRule\" />");
        Assert.Matches(owned[1].Named, "        Button set = new() { Classes = { StyleClasses.Small }, Content = plus };");
        Assert.DoesNotMatch(owned[1].Named, "        PathIcon glyph = new() { Classes = { StyleClasses.Small } };");
        Assert.Matches(greyed, "        SaveAsProfileButton.IsEnabled = why is null;");
    }

    /// <summary>A style class is given only to a control a style of it takes: Shell.axaml's or ControlThemes.axaml's selector
    /// naming the class on the type the control is styled as (its own, or the one its StyleKeyOverride names), on a base of
    /// it by <c>:is()</c>, or on any control. A class borrowed from another kind of control (a drop-down's given to a
    /// button), or given to a control of its own kind styled as itself (a Border's class on a Border of Passenger's that
    /// says no StyleKeyOverride), styles nothing there, the control looking as if it had none: each fails here, naming its
    /// line. C# is read by its syntax, the control's type taken from what makes it, the declared type of what holds it, a
    /// cast or the class whose own it is.</summary>
    [Fact]
    public void Every_style_class_is_given_to_a_control_of_a_type_its_styles_name()
    {
        Dictionary<string, HashSet<string>> styledOn = ClassesStyledOn();
        Dictionary<string, Type[]> types = StyledTypes();
        List<(string Path, string Text, Microsoft.CodeAnalysis.SyntaxNode? Root)> files = [.. UiFiles().Select(f => (f.Path, f.Text, f.Path.EndsWith(".cs", StringComparison.Ordinal)
            ? Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(f.Text, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)
            : null))];
        // Each of Passenger's controls styled as another, by the type its StyleKeyOverride names; any other as itself.
        Dictionary<string, string> styledAs = new(StringComparer.Ordinal);
        foreach (Syntax.PropertyDeclarationSyntax key in files.SelectMany(f => f.Root?.DescendantNodes().OfType<Syntax.PropertyDeclarationSyntax>() ?? []))
        {
            if (key.Identifier.ValueText == "StyleKeyOverride" && key.ExpressionBody?.Expression is Syntax.TypeOfExpressionSyntax typeOf
                && key.Parent is Syntax.ClassDeclarationSyntax owner && TypeName(typeOf.Type) is { } styled)
            {
                styledAs[owner.Identifier.ValueText] = styled;
            }
        }
        // A control of Passenger's is styled as the nearest of its own kinds that says, else as itself.
        string StyledAs(Type type)
        {
            for (Type? kind = type; kind is not null && kind.Assembly.GetName().Name?.StartsWith("Passenger", StringComparison.Ordinal) == true; kind = kind.BaseType)
            {
                if (styledAs.TryGetValue(kind.Name, out string? key))
                {
                    return key;
                }
            }
            return type.Name.Split('`')[0];
        }
        bool Takes(string on, Type[] given) => on.StartsWith(":is ", StringComparison.Ordinal)
            ? types.TryGetValue(on[4..], out Type[]? bases) && bases.Any(t => given.Any(t.IsAssignableFrom))
            : given.Any(g => StyledAs(g) == on);
        Dictionary<string, string> classOf = typeof(StyleClasses).GetFields().ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);
        List<string> found = [];
        int checkedCount = 0;
        void Check(string path, int line, string? type, string style)
        {
            // A control held as any control may be of any kind.
            if (type is null or nameof(Control) or nameof(Visual) or nameof(StyledElement) || !types.TryGetValue(type, out Type[]? given))
            {
                return;
            }
            checkedCount++;
            HashSet<string> on = styledOn.GetValueOrDefault(style) ?? [];
            if (!on.Contains("*") && !on.Any(s => Takes(s, given)))
            {
                found.Add($"{path}:{line}: {style} given to a {type}, where a style takes it on {(on.Count == 0 ? "nothing" : string.Join(", ", on.Order(StringComparer.Ordinal)))}");
            }
        }
        foreach ((string path, string text, Microsoft.CodeAnalysis.SyntaxNode? root) in files)
        {
            if (root is null)
            {
                foreach (Match element in Regex.Matches(text, @"<(?:\w+:)?(\w+)\b[^>]*>"))
                {
                    foreach (Match given in Regex.Matches(element.Value, @"\sClasses=""([^""{]*)""|\sClasses\.([\w-]+)="))
                    {
                        foreach (string style in given.Groups[1].Success ? given.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [given.Groups[2].Value])
                        {
                            Check(path, LineOf(text, element.Index), element.Groups[1].Value, style);
                        }
                    }
                }
                continue;
            }
            Dictionary<string, HashSet<string>> declared = DeclaredTypes(root);
            foreach (Microsoft.CodeAnalysis.SyntaxNode node in root.DescendantNodes())
            {
                (string? type, Syntax.ExpressionSyntax? named) = node switch
                {
                    Syntax.AssignmentExpressionSyntax
                    {
                        Left: Syntax.IdentifierNameSyntax { Identifier.ValueText: "Classes" },
                        Right: Syntax.InitializerExpressionSyntax,
                        Parent: Syntax.InitializerExpressionSyntax { Parent: Syntax.BaseObjectCreationExpressionSyntax made },
                    } => (MadeType(made), null),
                    Syntax.InvocationExpressionSyntax { Expression: Syntax.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" or "Set" or "Insert" } call } invocation
                        when ClassesOwner(call.Expression, out Syntax.ExpressionSyntax? owner)
                        => (OwnerType(owner, node, declared), invocation.ArgumentList.Arguments.ElementAtOrDefault(call.Name.Identifier.ValueText == "Insert" ? 1 : 0)?.Expression),
                    _ => (null, null),
                };
                IEnumerable<Syntax.ExpressionSyntax> styles = named is not null ? [named]
                    : node is Syntax.AssignmentExpressionSyntax { Right: Syntax.InitializerExpressionSyntax list } ? list.Expressions : [];
                foreach (Syntax.ExpressionSyntax style in styles)
                {
                    if (style is Syntax.MemberAccessExpressionSyntax { Expression: Syntax.IdentifierNameSyntax { Identifier.ValueText: "StyleClasses" }, Name.Identifier.ValueText: string constant }
                        && classOf.TryGetValue(constant, out string? value))
                    {
                        Check(path, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, type, value);
                    }
                }
            }
        }
        Assert.True(checkedCount > 500, $"Only {checkedCount} classes given to a control of a known type were found: the search no longer reads the code");
        Assert.True(found.Count == 0, $"{found.Count} classes are given to a control no style of theirs takes, so it looks as if it had none: give each "
            + $"a class its own kind of control is styled by, or none:\n{string.Join("\n", found)}");
    }

    /// <summary>The types each style class is styled on by Shell.axaml's and ControlThemes.axaml's selectors, by name (":is "
    /// before a type whose kinds all take it, "*" for a class any control takes); a control theme's <c>^</c> is its target
    /// type.</summary>
    private static Dictionary<string, HashSet<string>> ClassesStyledOn()
    {
        Dictionary<string, HashSet<string>> on = new(StringComparer.Ordinal);
        foreach (string theme in (string[])["Shell.axaml", "ControlThemes.axaml"])
        {
            string target = "*";
            string text = File.ReadAllText(Path.Combine(Passenger.Tests.Debugging.Helpers.Debuggees.Root, "src", "Passenger.App", "Theme", theme));
            foreach (Match tag in Regex.Matches(text, @"<ControlTheme\b[^>]*\bTargetType=""(?:\w+:)?(\w+)""|<Style\s[^>]*Selector=""([^""]*)"""))
            {
                if (tag.Groups[1].Success)
                {
                    target = tag.Groups[1].Value;
                    continue;
                }
                string selector = Regex.Replace(tag.Groups[2].Value, @"\[[^\]]*\]|:not\([^)]*\)", string.Empty).Replace("^", target, StringComparison.Ordinal);
                foreach (string part in Regex.Split(selector, @"\s*,\s*|\s*/template/\s*|\s*>\s*|\s+"))
                {
                    Match compound = Regex.Match(part, @"^(?::is\((?:\w+\|)?(\w+)\)|(?:\w+\|)?(\w+))?(.*)$");
                    string type = compound.Groups[1].Success ? $":is {compound.Groups[1].Value}" : compound.Groups[2].Success ? compound.Groups[2].Value : "*";
                    foreach (Match style in Regex.Matches(compound.Groups[3].Value, @"\.([A-Za-z_][\w-]*)"))
                    {
                        if (!on.TryGetValue(style.Groups[1].Value, out HashSet<string>? types))
                        {
                            on[style.Groups[1].Value] = types = new HashSet<string>(StringComparer.Ordinal);
                        }
                        types.Add(type);
                    }
                }
            }
        }
        return on;
    }

    /// <summary>Every styled element type of Avalonia's and Passenger's assemblies loaded, by its name.</summary>
    private static Dictionary<string, Type[]> StyledTypes()
    {
        IEnumerable<Type> Of(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException partly)
            {
                return partly.Types.OfType<Type>();
            }
        }
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } name && (name.StartsWith("Avalonia", StringComparison.Ordinal) || name.StartsWith("Passenger", StringComparison.Ordinal)))
            .SelectMany(Of).Where(t => typeof(StyledElement).IsAssignableFrom(t))
            .GroupBy(t => t.Name.Split('`')[0], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>The types a file declares each name of (a local, a field, a property, a parameter, a pattern's), by the
    /// type's own name.</summary>
    private static Dictionary<string, HashSet<string>> DeclaredTypes(Microsoft.CodeAnalysis.SyntaxNode root)
    {
        Dictionary<string, HashSet<string>> declared = new(StringComparer.Ordinal);
        void Add(string name, Syntax.TypeSyntax? type)
        {
            if (TypeName(type) is { } named)
            {
                if (!declared.TryGetValue(name, out HashSet<string>? types))
                {
                    declared[name] = types = new HashSet<string>(StringComparer.Ordinal);
                }
                types.Add(named);
            }
        }
        foreach (Microsoft.CodeAnalysis.SyntaxNode node in root.DescendantNodes())
        {
            switch (node)
            {
                case Syntax.VariableDeclarationSyntax variables:
                    foreach (Syntax.VariableDeclaratorSyntax each in variables.Variables)
                    {
                        Add(each.Identifier.ValueText, variables.Type);
                    }
                    break;
                case Syntax.ParameterSyntax parameter:
                    Add(parameter.Identifier.ValueText, parameter.Type);
                    break;
                case Syntax.PropertyDeclarationSyntax property:
                    Add(property.Identifier.ValueText, property.Type);
                    break;
                case Syntax.ForEachStatementSyntax each:
                    Add(each.Identifier.ValueText, each.Type);
                    break;
                case Syntax.DeclarationPatternSyntax { Designation: Syntax.SingleVariableDesignationSyntax single } pattern:
                    Add(single.Identifier.ValueText, pattern.Type);
                    break;
            }
        }
        return declared;
    }

    /// <summary>A type's own name, its namespace, generic arguments and nullability left off; null for a var, a tuple or an array.</summary>
    private static string? TypeName(Syntax.TypeSyntax? type) => type switch
    {
        Syntax.NullableTypeSyntax nullable => TypeName(nullable.ElementType),
        Syntax.QualifiedNameSyntax qualified => TypeName(qualified.Right),
        Syntax.AliasQualifiedNameSyntax alias => TypeName(alias.Name),
        Syntax.GenericNameSyntax generic => generic.Identifier.ValueText,
        Syntax.IdentifierNameSyntax { IsVar: false } name => name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The type an object creation makes: its own, or for <c>new()</c> the type of the variable or property it is
    /// given to.</summary>
    private static string? MadeType(Syntax.BaseObjectCreationExpressionSyntax made) => made switch
    {
        Syntax.ObjectCreationExpressionSyntax explicitly => TypeName(explicitly.Type),
        { Parent: Syntax.EqualsValueClauseSyntax { Parent: Syntax.VariableDeclaratorSyntax { Parent: Syntax.VariableDeclarationSyntax variables } } } => TypeName(variables.Type),
        { Parent: Syntax.EqualsValueClauseSyntax { Parent: Syntax.PropertyDeclarationSyntax property } } => TypeName(property.Type),
        _ => null,
    };

    /// <summary>Whether an expression is a control's Classes, <paramref name="owner"/> the control (null for the class's
    /// own, <c>Classes</c> alone).</summary>
    private static bool ClassesOwner(Syntax.ExpressionSyntax expression, out Syntax.ExpressionSyntax? owner)
    {
        owner = (expression as Syntax.MemberAccessExpressionSyntax)?.Expression;
        return expression is Syntax.IdentifierNameSyntax { Identifier.ValueText: "Classes" }
            or Syntax.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Classes" };
    }

    /// <summary>The type of a control whose Classes are named: the class's own for <c>this</c>, a cast's, or the one type
    /// the file declares its name of.</summary>
    private static string? OwnerType(Syntax.ExpressionSyntax? owner, Microsoft.CodeAnalysis.SyntaxNode at, Dictionary<string, HashSet<string>> declared)
    {
        while (owner is Syntax.ParenthesizedExpressionSyntax inner)
        {
            owner = inner.Expression;
        }
        return owner switch
        {
            null or Syntax.ThisExpressionSyntax => at.Ancestors().OfType<Syntax.ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText,
            Syntax.CastExpressionSyntax cast => TypeName(cast.Type),
            Syntax.IdentifierNameSyntax name when declared.TryGetValue(name.Identifier.ValueText, out HashSet<string>? types) && types.Count == 1 => types.Single(),
            _ => null,
        };
    }

    /// <summary>A bar's top, its rules and a banner are the shared pieces (ToolBars.Top, ToolBars.Rule, Banner): a panel docked
    /// at a tool window's top holding its icon buttons, with neither a bar's height nor its rule; a line a pixel wide and
    /// some high between a bar's groups; and a Border docked at the top on the banner's tint, each built by hand, fail here,
    /// naming the line.</summary>
    [Fact]
    public void Every_bars_top_rule_and_banner_is_the_shared_piece()
    {
        Regex border = new(@"(?:\bnew\s+Border\b|\bBorder\s+\w+\s*=\s*new\(\))\s*\{");
        List<string> found = [];
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            if (path.EndsWith("/ToolBars.cs", StringComparison.Ordinal) || path.EndsWith("/Banner.cs", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match match in border.Matches(text))
            {
                string own = Initializer(text, match.Index + match.Length);
                if (Regex.IsMatch(own, @"\bWidth\s*=\s*(?:1\b|Hairlines\.Width\b)") && Regex.IsMatch(own, @"\bHeight\s*="))
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}: a bar's rule built by hand");
                }
            }
            foreach (Match banner in Regex.Matches(text, @"\b(\w+)\.Bind\(Border\.BackgroundProperty, \1\.GetResourceObservable\(TokenKeys\.Banner\)\)"))
            {
                string name = Regex.Escape(banner.Groups[1].Value);
                if (Regex.IsMatch(text, $@"\b{name}\s*=\s*new\b[^;]*\bDock\.Top\b|SetDock\({name}, Dock\.Top\)"))
                {
                    found.Add($"{path}:{LineOf(text, banner.Index)}: a banner built by hand");
                }
            }
            if (path.EndsWith("Dialog.cs", StringComparison.Ordinal))
            {
                continue;
            }
            Microsoft.CodeAnalysis.SyntaxNode root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text, cancellationToken: TestContext.Current.CancellationToken)
                .GetRoot(TestContext.Current.CancellationToken);
            HashSet<string> buttons = [.. root.DescendantNodes().OfType<Syntax.VariableDeclaratorSyntax>()
                .Where(v => v.Initializer?.Value.ToString().StartsWith("ToolButtons.", StringComparison.Ordinal) == true).Select(v => v.Identifier.ValueText)];
            bool Button(Syntax.ExpressionSyntax held) => held.ToString().StartsWith("ToolButtons.", StringComparison.Ordinal)
                || held is Syntax.IdentifierNameSyntax { Identifier.ValueText: string named } && buttons.Contains(named);
            foreach (Syntax.VariableDeclaratorSyntax panel in root.DescendantNodes().OfType<Syntax.VariableDeclaratorSyntax>())
            {
                string name = panel.Identifier.ValueText;
                Syntax.InitializerExpressionSyntax? own = (panel.Initializer?.Value as Syntax.BaseObjectCreationExpressionSyntax)?.Initializer;
                bool top = own?.ToString().Contains("Dock.Top", StringComparison.Ordinal) == true
                    || root.DescendantNodes().OfType<Syntax.InvocationExpressionSyntax>().Any(i => i.ToString() == $"DockPanel.SetDock({name}, Dock.Top)");
                bool holds = own?.Expressions.OfType<Syntax.AssignmentExpressionSyntax>()
                        .Any(a => a.Left.ToString() == "Children" && a.Right is Syntax.InitializerExpressionSyntax children && children.Expressions.Any(Button)) == true
                    || root.DescendantNodes().OfType<Syntax.InvocationExpressionSyntax>()
                        .Any(i => i.Expression.ToString() == $"{name}.Children.Add" && i.ArgumentList.Arguments.Count == 1 && Button(i.ArgumentList.Arguments[0].Expression));
                if (top && holds)
                {
                    found.Add($"{path}:{panel.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: a top bar of {name} built by hand, with neither a bar's height nor its rule");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} bars, rules or banners are built by hand, so their heights, rules and room drift from every other's: "
            + $"make each with ToolBars.Top, ToolBars.Rule or Banner (Banner.Wrapping for words read whole):\n{string.Join("\n", found)}");
    }

    /// <summary>A table's column titles are one piece, ColumnTitles, so every table's are one size and height over one rule:
    /// a grid of titles or a title docked at a table's top over a rule of its own fails here, naming its line.</summary>
    [Gate]
    [Fact]
    public void Every_tables_column_titles_are_ColumnTitles()
    {
        Regex made = new(@"\bnew(?:\s+Border)?\s*(?:\(\))?\s*\{");
        List<string> found = [];
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && !f.Path.EndsWith("/ColumnTitles.cs", StringComparison.Ordinal)))
        {
            foreach (Match match in made.Matches(text))
            {
                string own = Initializer(text, match.Index + match.Length);
                if (!own.Contains("Thickness(0, 0, 0, 1)", StringComparison.Ordinal) || Regex.Match(own, @"\bChild\s*=\s*(\w+)") is not { Success: true } child)
                {
                    continue;
                }
                // What it holds is titles: a Grid or a TextBlock, made there or declared before it.
                string held = child.Groups[1].Value;
                bool titles = Regex.IsMatch(own, @"\bChild\s*=\s*new\s+(?:Grid|TextBlock)\b")
                    || Regex.IsMatch(text, $@"\b(?:Grid|TextBlock)\s+{Regex.Escape(held)}\s*=");
                // Docked at the top there, or by its name after.
                Match named = Regex.Match(text[..match.Index], @"(\w+)\s*=\s*$");
                bool docked = own.Contains("Dock.Top", StringComparison.Ordinal)
                    || (named.Success && Regex.IsMatch(text, $@"(?:SetDock|Docked)\(\s*{Regex.Escape(named.Groups[1].Value)}\s*,\s*Dock\.Top\)"));
                if (titles && docked)
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} tables' column titles are built by hand over a rule, so their sizes, heights and rules drift apart: "
            + $"make each with ColumnTitles.Of:\n{string.Join("\n", found)}");
    }

    /// <summary>A form's rows of a label and the field it names are FormRows, so a screen reader hears each field by its
    /// label and every form's labels stand, wrap and align one way: a grid of an Auto column and a star column given words
    /// for its first column (a TextBlock added to it, or a helper handed the grid and the words), or a label of a width of
    /// its own docked at its field's left, fails here, naming its line; DialogButtonsTests.Check fails a label a dialog
    /// shows beside or over a field that does not name it.</summary>
    [Fact]
    public void Every_forms_rows_of_a_label_and_its_field_are_FormRows()
    {
        Regex labelGrid = new(@"\bGrid\s+(\w+)\s*=\s*new(?:\s+Grid)?\s*(?:\(\))?\s*\{[^;]*?\bColumnDefinitions\s*=\s*new\s+ColumnDefinitions\(\s*""Auto,\s*\*""\s*\)");
        List<string> found = [];
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && !f.Path.EndsWith("/FormRows.cs", StringComparison.Ordinal)))
        {
            foreach (Match match in labelGrid.Matches(text))
            {
                string grid = Regex.Escape(match.Groups[1].Value);
                if (Regex.IsMatch(text, $@"\b{grid}\.Children\.Add\(\s*new\s+TextBlock\b") || Regex.IsMatch(text, $@"\b\w+\(\s*{grid}\s*,[^;\n]*""[^""\n]+"""))
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}");
                }
            }
        }
        // A label of a width of its own docked beside its field (a refactoring's rows were), or one stacked over it.
        Regex docked = new(@"\bTextBlock\s+(\w+)\s*=\s*new(?:\s+TextBlock)?\s*(?:\(\))?\s*\{[^;]*?\bText\s*=\s*\w+[^;]*?\bWidth\s*=\s*\w+");
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && !f.Path.EndsWith("/FormRows.cs", StringComparison.Ordinal)))
        {
            foreach (Match match in docked.Matches(text))
            {
                if (Regex.IsMatch(text, $@"DockPanel\.SetDock\(\s*{Regex.Escape(match.Groups[1].Value)}\s*,\s*Dock\.Left\s*\)"))
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}: a label of a width of its own docked beside its field");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} forms lay out their labels beside their fields by hand, so a screen reader hears none of those fields by its label "
            + $"and their labels drift from every other form's: make each row with FormRows.Of, its form marked by FormRows.Scope:\n{string.Join("\n", found)}");
    }

    /// <summary>A form's label is its words alone, with no colon after them, as every Settings row's and run configuration's
    /// is: a label ending in a colon handed to a form's row (FormRows.Of, a dialog's Row) fails here, naming its line, as
    /// DialogButtonsTests.Check fails one a dialog shows.</summary>
    [Fact]
    public void No_forms_label_ends_in_a_colon()
    {
        Regex colon = new(@"\b(?:FormRows\.Of|Row)\(\s*(?:[^""(),;\n]*\?\s*)?""[^""\n]*:""");
        string[] found = [.. UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(f => colon.Matches(f.Text).Select(m => $"{f.Path}:{LineOf(f.Text, m.Index)}: {m.Value}"))];
        Assert.True(found.Length == 0, $"{found.Length} form labels end in a colon, where every form's labels are their words alone:\n{string.Join("\n", found)}");
        Assert.Matches(colon, "                Row(\"Name:\", NameBox),");
        Assert.Matches(colon, "        Options = Row(toFile ? \"Target file name:\" : \"Target namespace:\", TargetBox);");
        Assert.DoesNotMatch(colon, "                Row(\"Name\", NameBox),");
    }

    /// <summary>A table's rows edited in place are ColumnTitles' ruled rows, so every such table's rows are one height over
    /// one rule, in the titles' columns: a rule under a row bound by hand to the separator's colour fails here, naming its line.</summary>
    [Fact]
    public void Every_editable_tables_rows_are_ColumnTitles_ruled_rows()
    {
        Regex ruled = new(@"\.Bind\(\s*(?:Border\.)?BorderBrushProperty\s*,\s*\w+\.GetResourceObservable\(\s*TokenKeys\.Separator\s*\)\s*\)");
        string[] found = [.. UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && !f.Path.EndsWith("/ColumnTitles.cs", StringComparison.Ordinal))
            .SelectMany(f => ruled.Matches(f.Text).Select(m => $"{f.Path}:{LineOf(f.Text, m.Index)}"))];
        Assert.True(found.Length == 0, $"{found.Length} table rows are ruled by hand, so their heights, columns and rules drift from every other table's: make each "
            + $"with its titles' ColumnTitles.Ruled (an EditableTable's Ruled):\n{string.Join("\n", found)}");
    }

    /// <summary>Words are shown as written, a name's underscores too, since Passenger gives no access keys: every kind of
    /// control that holds words (a button's, a check box's, a menu item's, a tab's, a label's, a group's header), made with
    /// a file's names under the window's theme, draws them whole, and no code makes access-key text of its own; a kind that
    /// takes an underscore for an access key's mark fails here, naming what it drew.</summary>
    [AvaloniaFact]
    public void Every_control_draws_a_names_underscores_never_taking_one_for_an_access_keys_mark()
    {
        const string name = "test_spinner.gd __init__.py";
        StackPanel panel = new();
        Window window = new() { Width = 900, Height = 900, Content = new ScrollViewer { Content = panel } };
        List<(string Kind, Control Made)> made = [];
        foreach (Type kind in typeof(Control).Assembly.GetExportedTypes().Where(Holds).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            Control control = (Control)Activator.CreateInstance(kind)!;
            switch (control)
            {
                case HeaderedContentControl headed:
                    (headed.Header, headed.Content) = (name, name);
                    break;
                case ContentControl content:
                    content.Content = name;
                    break;
                case HeaderedItemsControl headed:
                    headed.Header = name;
                    break;
                case HeaderedSelectingItemsControl headed:
                    headed.Header = name;
                    break;
            }
            panel.Children.Add(control switch
            {
                MenuItem item => new Menu { Items = { item } },
                TabItem tab => new TabControl { Items = { tab } },
                _ => control,
            });
            made.Add((kind.Name, control));
        }
        window.Show();
        try
        {
            Ui.Settle();
            List<string> taken = [];
            List<string> shown = [];
            foreach ((string kind, Control control) in made)
            {
                TextBlock[] words = [.. control.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == name)];
                if (words.Length > 0)
                {
                    shown.Add(kind);
                }
                taken.AddRange(words.Where(t => t is AccessText || Ui.Drawn(t) != name).Select(t => $"{kind} draws '{Ui.Drawn(t)}'"));
            }
            Assert.True(taken.Count == 0, $"{taken.Count} kinds of control take a name's underscore for an access key's mark, so the name loses it: "
                + $"Shell.axaml's ContentPresenter style shows words as written:\n{string.Join("\n", taken)}");
            string[] unseen = [.. ((string[])["Button", "CheckBox", "Label", "MenuItem", "RadioButton", "SplitButton", "TabItem", "ToggleButton"]).Except(shown)];
            Assert.True(unseen.Length == 0, $"{string.Join(", ", unseen)} showed none of the words given them: the rule no longer reads them");
        }
        finally
        {
            window.Close();
        }

        Regex own = new(@"\bnew\s+AccessText\b|<AccessText\b|\bRecognizesAccessKey\b");
        string[] found = [.. UiFiles(theme: true).SelectMany(f => f.Text.Split('\n').Select((line, i) => (f.Path, Line: i + 1, Text: line)))
            .Where(l => own.IsMatch(l.Text) && !(l.Path.EndsWith("Theme/Shell.axaml", StringComparison.Ordinal) && l.Text.Contains("Property=\"RecognizesAccessKey\" Value=\"False\"", StringComparison.Ordinal)))
            .Select(l => $"{l.Path}:{l.Line}")];
        Assert.True(found.Length == 0, $"{found.Length} places make access-key text, which takes a name's underscore for an access key's mark: show words in a TextBlock:\n{string.Join("\n", found)}");

        // A public kind of control that holds words of its own, made alone (a window and a list's rows aside).
        static bool Holds(Type kind) => !kind.IsAbstract && kind.GetConstructor(Type.EmptyTypes) is not null
            && (typeof(ContentControl).IsAssignableFrom(kind) || typeof(HeaderedItemsControl).IsAssignableFrom(kind) || typeof(HeaderedSelectingItemsControl).IsAssignableFrom(kind))
            && !typeof(TopLevel).IsAssignableFrom(kind) && !typeof(ListBoxItem).IsAssignableFrom(kind) && !typeof(TreeViewItem).IsAssignableFrom(kind);
    }

    /// <summary>A native menu's item takes its words through MainWindow.NativeWords, which doubles their underscores, since
    /// the platform's menu (macOS's bar, a Linux desktop's global menu) takes a lone one for an access key's mark: a
    /// NativeMenuItem made or given words otherwise (a literal with no underscore aside) fails here, naming its line.</summary>
    [Fact]
    public void Every_native_menu_items_words_come_through_NativeWords()
    {
        Regex made = new(@"\bnew\s+NativeMenuItem\s*\(|\bNativeMenuItem\s+\w+\s*=\s*new\s*\(|\bNativeMenuItem\s+\w+\(.*\)\s*=>\s*new\s*\(");
        Regex given = new(@"\b(\w+)\.Header\s*=\s*");
        List<string> found = [];
        int items = 0;
        foreach ((string path, string text) in UiFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && f.Text.Contains("NativeMenuItem", StringComparison.Ordinal)))
        {
            foreach (Match match in made.Matches(text))
            {
                items++;
                if (!Words(text, match.Index + match.Length))
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}");
                }
            }
            foreach (Match match in given.Matches(text).Where(m => Native(text, m.Groups[1].Value, m.Index)))
            {
                items++;
                if (!Words(text, match.Index + match.Length))
                {
                    found.Add($"{path}:{LineOf(text, match.Index)}");
                }
            }
        }
        Assert.True(items >= 8, $"Only {items} native menu items were found: the search no longer reads them");
        Assert.True(found.Count == 0, $"{found.Count} native menu items are given words a name's underscore could lose itself in, the platform's menu taking it "
            + $"for an access key's mark: give each through MainWindow.NativeWords:\n{string.Join("\n", found)}");

        // Whether a variable is a NativeMenuItem where its words are given: its nearest declaration before says so.
        static bool Native(string text, string variable, int at) =>
            Regex.Matches(text[..at], $@"\b([\w.]+)\s+{Regex.Escape(variable)}\s*(?:=|\bin\b)").LastOrDefault() is { } declared && declared.Groups[1].Value == "NativeMenuItem";

        // Words given through NativeWords, or a literal with no underscore.
        static bool Words(string text, int at) => text.AsSpan(at).StartsWith("NativeWords(", StringComparison.Ordinal)
            || Regex.Match(text[at..], @"^""([^""\\]*)""") is { Success: true } literal && !literal.Groups[1].Value.Contains('_', StringComparison.Ordinal);
    }

    /// <summary>An object initializer's own settings, from after its brace to its end, less the initializers inside it.</summary>
    private static string Initializer(string text, int from)
    {
        System.Text.StringBuilder own = new();
        int depth = 1;
        for (int i = from; i < text.Length && depth > 0; i++)
        {
            depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 1 && text[i] != '}')
            {
                own.Append(text[i]);
            }
        }
        return own.ToString();
    }

    /// <summary>An object initializer's whole text from just after its opening brace, what its own initializers set
    /// inside it too (a class list's).</summary>
    private static string Whole(string text, int from)
    {
        int depth = 1;
        int at = from;
        for (; at < text.Length && depth > 0; at++)
        {
            depth += text[at] switch { '{' => 1, '}' => -1, _ => 0 };
        }
        return text[from..Math.Max(from, at - 1)];
    }

    private static int LineOf(string text, int at) => text.AsSpan(0, at).Count('\n') + 1;

    /// <summary>A colour that is the content's own, not the window's, so no look has a token for it.</summary>
    private static bool ContentsColour(string path, string match) => (path, match) switch
    {
        // The colour being chosen in the colour picker, and the shades of it its square and slider show.
        (_, _) when path.EndsWith("Xaml/ColourPicker.cs", StringComparison.Ordinal) => true,
        // A colour written in the XAML being edited, which the editor shows beside it.
        (_, _) when path.EndsWith("Xaml/XamlService.cs", StringComparison.Ordinal) => true,
        // A named colour's swatch in XAML completion: the colour the name means.
        (_, "Color.FromArgb" or "new SolidColorBrush(Color.") when path.EndsWith("CSharp/CodeAssist.cs", StringComparison.Ordinal) => true,
        // A colour written in the CSS or HTML being edited, as its server reads it.
        (_, "Color.FromArgb") when path.EndsWith("Web/WebDocument.cs", StringComparison.Ordinal) => true,
        // A Godot Color's swatch beside its row in the debugger's values: the colour the program holds.
        (_, "Color.FromArgb") when path.EndsWith("Godot/GodotValues.cs", StringComparison.Ordinal) => true,
        // A colour a running Godot game's node holds, shown as its swatch in the Live Visual Tree's properties.
        (_, "Color.FromArgb" or "new SolidColorBrush(Color.") when path.EndsWith("Godot/GodotValueCells.cs", StringComparison.Ordinal) => true,
        // The ground of a web page setting none of its own: a browser's white, in every look.
        (_, "Brushes.White") when path.EndsWith("Editors/HtmlPreview.cs", StringComparison.Ordinal) => true,
        // A colour the program in the terminal names: xterm's 24-bit colours, its 256-colour cube and grey ramp.
        (_, "new SolidColorBrush(Color.") when path.EndsWith("TerminalView.cs", StringComparison.Ordinal) => true,
        // A colour a highlighter's look names of its own: a program's 24-bit or 256-colour text in a console.
        (_, "Color.FromUInt32") when path.EndsWith("Editor/CodeEditor.Render.cs", StringComparison.Ordinal) => true,
        _ => false,
    };

    private static string UiSource() => string.Join("\n", UiFiles().Select(f => f.Text));

    /// <summary>The C# and XAML of the projects that draw the window, less the theme files unless <paramref name="theme"/>.</summary>
    private static IEnumerable<(string Path, string Text)> UiFiles(bool theme = false)
    {
        string root = Passenger.Tests.Debugging.Helpers.Debuggees.Root;
        foreach (string project in (string[])["Passenger.App", "Passenger.Editor", "Passenger.Terminal", "Passenger.Profiler"])
        {
            string folder = Path.Combine(root, "src", project);
            foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if ((file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal))
                    && (theme || !relative.Contains("/Theme/", StringComparison.Ordinal)) && !relative.Contains("/obj/", StringComparison.Ordinal) && !relative.Contains("/bin/", StringComparison.Ordinal))
                {
                    yield return (relative, File.ReadAllText(file));
                }
            }
        }
    }

    /// <summary>The least step in lightness (CIELAB's L*) between two grounds side by side that tells them apart.</summary>
    private const double MinimumStep = 2;

    private static double Step(Color one, Color other) => Math.Abs(Lightness(one) - Lightness(other));

    private static double Lightness(Color colour)
    {
        double y = Luminance(colour);
        return y > 216.0 / 24389 ? (116 * Math.Cbrt(y)) - 16 : y * 24389 / 27;
    }

    private static double Luminance(Color colour)
    {
        static double Channel(byte value) => value / 255.0 <= 0.04045 ? value / 255.0 / 12.92 : Math.Pow(((value / 255.0) + 0.055) / 1.055, 2.4);
        return (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));
    }

    private static double Ratio(Color fore, Color ground)
    {
        double a = Luminance(Over(fore, ground));
        double b = Luminance(ground);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static Color Over(Color fore, Color ground)
    {
        double a = fore.A / 255.0;
        byte Mix(byte f, byte g) => (byte)Math.Round((f * a) + (g * (1 - a)));
        return Color.FromRgb(Mix(fore.R, ground.R), Mix(fore.G, ground.G), Mix(fore.B, ground.B));
    }

    /// <summary>A token's colours in a theme variant, its opacity in their alpha: one, or a gradient's every stop.</summary>
    private static Color[] Colours(string key, ThemeVariant variant)
    {
        if (!Application.Current!.TryGetResource(key, variant, out object? found))
        {
            throw new Xunit.Sdk.XunitException($"The theme has no {key} in {variant}");
        }
        return found switch
        {
            ISolidColorBrush solid => [Color.FromArgb((byte)Math.Round(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B)],
            IGradientBrush gradient => [.. gradient.GradientStops.Select(s => s.Color)],
            _ => throw new Xunit.Sdk.XunitException($"{key} is a {found?.GetType().Name}, not a brush"),
        };
    }

    private static readonly XNamespace ReadNamespace = "urn:passenger:contrast";
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>What each colour is read on, as Tokens.axaml declares it on its Dark dictionary's colours: read:Text at WCAG
    /// AA's 4.5:1, read:Mark (an icon, a focus ring, the caret) and read:Edge (a control's edge) at 3:1, a ground laid over
    /// another named <c>Ground/Under</c> and otherwise over Panel. The fourth to sixth braces, which a theme may give, are
    /// read as the first is.</summary>
    private static readonly ReadOn[] ReadOnPairs = DeclaredReadOn();

    /// <summary>A colour, the ground it is read on and what that ground is laid over, the least contrast asked, and whether
    /// it is a control's edge.</summary>
    private sealed record ReadOn(string Fore, string Ground, string Under, double Least, bool Edge);

    /// <summary>Tokens.axaml as written, its colours' declarations being attributes the compiler passes over.</summary>
    private static XDocument TokensSource() =>
        XDocument.Load(Path.Combine(Passenger.Tests.Debugging.Helpers.Debuggees.Root, "src", "Passenger.App", "Theme", "Tokens.axaml"));

    /// <summary>The colours of one variant's dictionary in Tokens.axaml, each by its key.</summary>
    private static IEnumerable<(string Key, XElement Colour)> SourceColours(XDocument tokens, string variant) =>
        from dictionary in tokens.Descendants()
        where dictionary.Name.LocalName == "ResourceDictionary" && (string?)dictionary.Attribute(XamlNamespace + "Key") == variant
        from colour in dictionary.Elements()
        where colour.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush"
        select ((string)colour.Attribute(XamlNamespace + "Key")!, colour);

    private static ReadOn[] DeclaredReadOn()
    {
        List<ReadOn> pairs = [];
        foreach ((string key, XElement colour) in SourceColours(TokensSource(), "Dark"))
        {
            foreach ((string kind, double least) in (ReadOnlySpan<(string, double)>)[("Text", 4.5), ("Mark", 3), ("Edge", 3)])
            {
                foreach (string ground in ((string?)colour.Attribute(ReadNamespace + kind) ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] laid = ground.Split('/');
                    pairs.Add(new ReadOn(key, "Passenger." + laid[0], "Passenger." + (laid.Length > 1 ? laid[1] : "Panel"), least, kind == "Edge"));
                }
            }
        }
        foreach (string optional in ThemeFiles.Optional.Where(o => o.Value == ThemeTokenKind.Colour).Select(o => o.Key))
        {
            Assert.True(optional.StartsWith("Syntax.Brace", StringComparison.Ordinal), $"{optional} is a colour a theme may give that no rule says what it is read on");
            pairs.AddRange(pairs.Where(p => p.Fore == TokenKeys.Syntax_Brace1).ToArray().Select(p => p with { Fore = "Passenger." + optional }));
        }
        return [.. pairs];
    }

    private static ResourceDictionary Dictionary(string file) =>
        (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri($"avares://Passenger/Theme/{file}"));

    private static string[] Keys(ResourceDictionary dictionary, ThemeVariant variant) =>
        [.. ((ResourceDictionary)dictionary.ThemeDictionaries[variant]).Keys.Select(k => k.ToString()!).Where(k => k.StartsWith("Passenger.", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    /// <summary>The tool window's pane a view is shown in.</summary>
    private static Control Pane(Control view) => view.GetVisualAncestors().OfType<Control>().First(a => a.Classes.Contains(StyleClasses.ToolPane));

    /// <summary>The window as the compositor last drew it, read pixel by pixel.</summary>
    private sealed class Picture : IDisposable
    {
        private readonly Avalonia.Media.Imaging.WriteableBitmap frame;
        private readonly double scale;

        public Picture(TopLevel window)
        {
            frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window has drawn no frame.");
            scale = window.RenderScaling;
        }

        public void Dispose() => frame.Dispose();

        public Color At(Point at)
        {
            byte[] pixel = new byte[4];
            System.Runtime.InteropServices.GCHandle pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                PixelPoint point = PixelPoint.FromPoint(at, scale);
                frame.CopyPixels(new PixelRect(point.X, point.Y, 1, 1), pinned.AddrOfPinnedObject(), 4, 4);
            }
            finally
            {
                pinned.Free();
            }
            return frame.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromRgb(pixel[0], pixel[1], pixel[2]) : Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        }

        /// <summary>Two grounds meet at an edge, read across it (left to right, or top to bottom): the first before it, the
        /// second after it, and nothing else between, no rule.</summary>
        public void Meets(string what, Point edge, bool across, Color before, Color after)
        {
            Point Off(double by) => across ? edge + new Point(by, 0) : edge + new Point(0, by);
            Color[] seen = [.. Enumerable.Range(-4, 9).Select(by => At(Off(by)))];
            Assert.True(Near(seen[0], before) && Near(seen[^1], after) && seen.All(c => Near(c, before) || Near(c, after)),
                $"Where {what} meet, the window draws {string.Join(" ", seen)} across the edge, where it would draw {before} then {after} and no rule between");
        }

        /// <summary>Two panes of one ground meet at an edge, read top to bottom: the ground, a line of the divider's colour,
        /// the ground again.</summary>
        public void Seam(string what, Point edge, Color ground, Color line)
        {
            Color[] seen = [.. Enumerable.Range(-4, 9).Select(by => At(edge + new Point(0, by)))];
            Assert.True(Near(seen[0], ground) && Near(seen[^1], ground) && seen.Any(c => Near(c, line)),
                $"Where {what} meet, both on {ground}, the window draws {string.Join(" ", seen)} across the edge, where a line of {line} would show it");
        }

        private static bool Near(Color one, Color other) => Math.Abs(one.R - other.R) <= 3 && Math.Abs(one.G - other.G) <= 3 && Math.Abs(one.B - other.B) <= 3;
    }

    /// <summary>Each stripe button shown lies wholly inside its stripe, above the status bar.</summary>
    private static void StripeButtonsFit(MainWindow window)
    {
        Rect dock = Placed(window, window.DockHost);
        foreach (ToggleButton button in window.DockHost.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Classes.Contains(StyleClasses.Stripe) && b.IsEffectivelyVisible))
        {
            Rect at = Placed(window, button);
            Assert.True(at.Top >= dock.Top - 0.5 && at.Bottom <= dock.Bottom + 0.5,
                $"The stripe's {ToolTip.GetTip(button)} button is cut off at {at.Bottom:0.#}, past the stripe's foot at {dock.Bottom:0.#}");
        }
    }

    /// <summary>Opens a status bar or toolbar widget's menu with a click and holds it to the rule: the current value
    /// checked and enabled, the others not checked.</summary>
    private static void Current(MainWindow window, string widget, string current, string what)
    {
        Button button = Named<Button>(window, widget);
        Ui.Click(button);
        Ui.WaitFor(() => window.OpenContextMenu is not null, what);
        ContextMenu menu = window.OpenContextMenu!;
        MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
        MenuItem[] checkedOnes = [.. items.Where(i => i.IsChecked)];
        Assert.True(checkedOnes.Length == 1 && Header(checkedOnes[0]) == current,
            $"In {what}, [{string.Join(", ", checkedOnes.Select(Header))}] are checked, where {current}, the one in force, alone is");
        Assert.True(checkedOnes[0].IsEnabled, $"In {what}, {current}, the one in force, is greyed where Rider checks it");
        Assert.True(checkedOnes[0].ToggleType is MenuItemToggleType.Radio or MenuItemToggleType.CheckBox, $"In {what}, {current} shows no tick");
        DropsFrom(button, menu, what);
        menu.Close();
        Ui.Settle();
    }

    /// <summary>A widget's menu opens against it, its frame's edge meeting the widget's and one of its sides in line with
    /// the widget's, wherever the room for its shadow puts the menu's popup (Passenger.MenuShadowRoom).</summary>
    private static void DropsFrom(Control widget, ContextMenu menu, string what)
    {
        Border frame = menu.GetVisualDescendants().OfType<Border>().First(b => b.BoxShadow.Count > 0);
        PixelPoint w = widget.PointToScreen(default);
        PixelPoint f = frame.PointToScreen(default);
        double scale = TopLevel.GetTopLevel(widget)!.RenderScaling;
        Rect at = new(w.X / scale, w.Y / scale, widget.Bounds.Width, widget.Bounds.Height);
        Rect drawn = new(f.X / scale, f.Y / scale, frame.Bounds.Width, frame.Bounds.Height);
        bool meets = Math.Abs(drawn.Top - at.Bottom) <= 2 || Math.Abs(drawn.Bottom - at.Top) <= 2;
        bool lined = Math.Abs(drawn.Left - at.Left) <= 2 || Math.Abs(drawn.Right - at.Right) <= 2;
        Assert.True(meets && lined, $"{what}'s frame is drawn at {drawn}, not against its widget at {at}: the menu opens away from what opened it");
    }

    private static string? Header(MenuItem item) => item.Header switch
    {
        string words => words.Replace("__", "_", StringComparison.Ordinal),
        Control control => string.Concat(control.GetSelfAndLogicalDescendants().OfType<TextBlock>().Take(1).Select(t => t.Text)),
        _ => null,
    };

    private static IEnumerable<Visual> ToolWindowParts(MainWindow window) =>
        window.DockHost.GetVisualDescendants().Where(v => v.IsEffectivelyVisible && !window.EditorArea.IsVisualAncestorOf(v) && v != window.EditorArea);

    private static string Words(Control host) => string.Join(" ", host.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    /// <summary>A tab's name, read from what it shows whether it is shown now or not (a hidden one has no template applied).</summary>
    private static string TabName(ToggleButton tab) => tab.Content is TextBlock { Text: { } text } ? text : tab.Content as string ?? string.Empty;

    private static Color Brush(MainWindow window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : default;

    private static T Named<T>(MainWindow window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new Xunit.Sdk.XunitException($"The window has no {name}");

    private static double Off(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static Rect Placed(Visual window, Visual control) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    /// <summary>A window on the class's project, with no C# language server: the rules are the window's, whatever C# says.</summary>
    private MainWindow Open(double width, double height)
    {
        MainWindow window = Ui.OpenFolder(project.Folder, width: width, height: height, languageServer: false, autosaves: false);
        // The rules hold every tool window, whatever this folder's projects bring.
        Ui.ServeEveryWindow(window);
        return window;
    }

    /// <summary>Holds a window to a check under each look, switched in place as Settings switches it (the window's settings
    /// put in force, so it is restyled where it stands), a failure naming the look; Passenger's own put back after it.</summary>
    private static void UnderEachLook(MainWindow window, Action check)
    {
        try
        {
            foreach (SourcedTheme? look in Looks)
            {
                window.ApplySettings(window.Settings with { Look = look });
                Ui.Settle();
                try
                {
                    check();
                }
                catch (Xunit.Sdk.XunitException failed)
                {
                    throw new Xunit.Sdk.XunitException($"Under the {look?.Name ?? CarriedThemes.Own} look: {failed.Message}", failed);
                }
            }
        }
        finally
        {
            Themes.Apply(Application.Current!, null, null);
        }
    }

    /// <summary>A small project in a Git repository (main, a branch other, the tag v1.0), each test starting on it as
    /// written: a file long and wide enough to scroll both ways, a text file, and a file deep enough that its path fills the
    /// status bar at the window's smallest.</summary>
    public sealed class Project : SharedProject
    {
        public const string Deep = "Source/Features/Payments/Processing/RefundCalculator.cs";

        public Project()
            : base("ui-rules-")
        {
            File.WriteAllText(PathOf("Hello.csproj"),
                TestProjects.Console());
            File.WriteAllText(PathOf("Program.cs"),
                string.Concat(Enumerable.Range(1, 120).Select(i => $"// Line {i} of a file long enough to scroll, and wide enough to scroll sideways too: {new string('x', 160)}\n")));
            File.WriteAllText(PathOf("Notes.txt"), "one\ntwo\n");
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(Deep))!);
            File.WriteAllText(PathOf(Deep), "class RefundCalculator\n{\n}\n");
        }

        /// <summary>A repository with a branch and a tag beside main; never restored, since no window of the class starts a
        /// language server or builds, and the rules are the window's.</summary>
        public override async ValueTask InitializeAsync()
        {
            await TestFolders.PrepareAsync(async () =>
            {
                await TestGit.InitAsync(Folder);
                await TestGit.RunAsync(Folder, "branch", "other");
                await TestGit.RunAsync(Folder, "tag", "v1.0");
            }, Folder);
            await PrepareAsync(restore: false);
        }
    }
}
