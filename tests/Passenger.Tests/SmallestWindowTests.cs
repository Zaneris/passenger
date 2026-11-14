using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;

namespace Passenger.Tests;

/// <summary>At the smallest window (MainWindow.SmallestWidth wide and SmallestHeight tall) everything shown can be read
/// and reached: the Welcome page scrolls to each of its tiles, Find in Files stands inside the window, the toolbar's long
/// names give way with an ellipsis and never draw over each other or the window's buttons, what the status bar says
/// stands whole in front of the file's path, the Commit window sharing its column draws nothing over its form, the empty
/// editors' words stand inside them, editor tabs down a side leave the code its room, a tool window dropped on the editors
/// is a tab there, and a stripe with no icon gives its room to the editors but while a window is dragged.</summary>
public sealed class SmallestWindowTests
{
    [AvaloniaFact]
    public void At_the_smallest_window_every_Welcome_tile_scrolls_into_view_uncovered_and_takes_a_click()
    {
        string settings = TestFolders.Create("smallest-welcome-");
        MainWindow window = new() { Width = MainWindow.SmallestWidth, Height = MainWindow.SmallestHeight, StateFolder = settings };
        try
        {
            window.Show();
            window.Start(LaunchOptions.Empty);
            Ui.Settle();
            Button[] tiles = [.. window.Welcome.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains(StyleClasses.WelcomeAction))];
            Assert.Equal(4, tiles.Length);
            TextBox search = window.Welcome.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains(StyleClasses.WelcomeSearch));
            foreach (Button tile in tiles)
            {
                tile.BringIntoView();
                Ui.Settle();
                Rect at = Placed(window, tile);
                Visual? hit = window.InputHitTest(at.Center) as Visual;
                Assert.True(hit is not null && hit.GetSelfAndVisualAncestors().Contains(tile),
                    $"The {Words(tile)} tile at {at} is covered by {hit?.GetType().Name ?? "nothing"} at the smallest window: a click on it misses");
                Assert.False(Placed(window, search).Intersects(at), $"The recent search box at {Placed(window, search)} draws over the {Words(tile)} tile at {at}");
            }
        }
        finally
        {
            window.Close();
            TestFolders.Delete(settings);
        }
    }

    [AvaloniaFact]
    public void At_the_smallest_window_Find_in_Files_stands_inside_the_window_its_results_and_preview_giving_way()
    {
        string folder = TestFolders.Create("smallest-find-");
        TestFolders.Write(folder, "Greeter.cs", string.Concat(Enumerable.Range(1, 40).Select(i => $"// Greet line {i}\n")));
        MainWindow window = Ui.OpenFolder(folder, width: MainWindow.SmallestWidth, height: MainWindow.SmallestHeight, languageServer: false, autosaves: false);
        try
        {
            window.OpenFile(Path.Combine(folder, "Greeter.cs"));
            Ui.Settle();
            Ui.Run(window, AppActions.FindInFiles);
            Ui.WaitFor(() => window.GetVisualDescendants().OfType<Passenger.App.Navigation.FindInFilesPopup>().Any(p => p.IsEffectivelyVisible), "Find in Files");
            Passenger.App.Navigation.FindInFilesPopup popup = window.GetVisualDescendants().OfType<Passenger.App.Navigation.FindInFilesPopup>().Single();
            window.KeyTextInput("Greet");
            Ui.WaitFor(() => popup.Matches.Count == 40, () => $"Find in Files' 40 matches ({popup.Status})");
            Border frame = popup.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(StyleClasses.Popup));
            Rect at = Placed(window, frame);
            Assert.True(at.Top >= 0 && at.Bottom <= window.ClientSize.Height,
                $"Find in Files' frame stands from {at.Top:0.#} to {at.Bottom:0.#} in a window {window.ClientSize.Height:0.#} tall: its preview hangs past the window's foot");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void At_the_smallest_widths_the_toolbars_long_names_end_in_an_ellipsis_and_nothing_on_it_draws_over_anything()
    {
        string folder = TestFolders.Create("smallest-toolbar-");
        File.WriteAllText(Path.Combine(folder, "Contoso.Inventory.Management.Service.Api.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(1);\n");
        TestGit.Init(folder);
        TestGit.Run(folder, "checkout", "-q", "-b", "feature/JIRA-12345-rework-the-inventory-reconciliation-pipeline");
        MainWindow window = Ui.OpenFolder(folder, width: 720, height: MainWindow.SmallestHeight, languageServer: false, autosaves: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Count > 0, "the project's run configuration");
            foreach (double width in (double[])[720, MainWindow.SmallestWidth])
            {
                window.Width = width;
                Ui.Settle();
                // The window's own buttons too (Windows' and Linux's), whose clicks a part drawn over them would take.
                Control[] parts = [.. ((Panel)Named(window, "ToolbarStart")).Children.Concat(((Panel)Named(window, "ToolbarEnd")).Children)
                    .Concat(((Panel)Named(window, "WindowButtons")).Children)
                    .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && !ShrinkingRow.HasGone(c))];
                foreach (Control one in parts)
                {
                    foreach (Control other in parts.Where(o => o != one && Placed(window, o).Left > Placed(window, one).Left))
                    {
                        Assert.True(Placed(window, one).Right <= Placed(window, other).Left + 0.5,
                            $"At {width} wide the toolbar's {Name(one)} ({Placed(window, one).Left:0.#} to {Placed(window, one).Right:0.#}) draws over its {Name(other)} from {Placed(window, other).Left:0.#}");
                    }
                }
                foreach (string name in (string[])["ProjectName", "BranchName", "RunConfigName"])
                {
                    TextBlock words = (TextBlock)Named(window, name);
                    Assert.True(words.IsEffectivelyVisible && words.Bounds.Width >= 14,
                        $"At {width} wide the toolbar's {name} has {words.Bounds.Width:0.#} units: '{words.Text}' vanishes with no ellipsis");
                }
            }
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void At_the_smallest_width_a_refusal_stands_whole_in_front_of_a_deep_files_path()
    {
        string folder = TestFolders.Create("smallest-status-");
        string deep = Path.Combine(folder, "Source", "Features", "Payments", "Processing", "RefundCalculator.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
        File.WriteAllText(deep, "class RefundCalculator\n{\n}\n");
        MainWindow window = Ui.OpenFolder(folder, width: MainWindow.SmallestWidth, height: MainWindow.SmallestHeight, languageServer: false, autosaves: false);
        try
        {
            window.OpenFile(deep);
            Ui.Settle();
            Assert.NotEmpty(window.Breadcrumbs);
            Ui.Run(window, AppActions.Build);
            Ui.WaitFor(() => window.Status is { Length: > 0 }, "Build's refusal in the status bar");
            TextBlock said = (TextBlock)Named(window, "StatusText");
            Assert.True(said.IsEffectivelyVisible && said.TextLayout.TextLines.All(l => !l.HasCollapsed) && said.Bounds.Width > 0,
                $"The status bar cuts '{window.Status}' to {said.Bounds.Width:0.#} units behind the file's path: it is read only by hovering");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>At the smallest window, the Commit window sharing its column with Bookmarks: in a folder of no repository it
    /// shows its words and Create Git Repository whole, taking a click, and nothing of a form no repository can use; in a
    /// repository with nothing to commit its words are cut where the form begins, never drawn over it, and the form,
    /// taller than the window leaves it, scrolls to its Commit button.</summary>
    [AvaloniaFact]
    public void At_the_smallest_window_the_commit_windows_empty_state_never_draws_over_its_form_which_scrolls_to_commit()
    {
        string plain = TestFolders.Create("smallest-commit-plain-");
        string repository = TestFolders.Create("smallest-commit-git-");
        foreach (string folder in (string[])[plain, repository])
        {
            File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(1);\n");
        }
        TestGit.Init(repository);
        try
        {
            MainWindow window = Ui.OpenFolder(plain, width: MainWindow.SmallestWidth, height: MainWindow.SmallestHeight, languageServer: false, autosaves: false);
            CommitView commit = Shown(window);
            Assert.Null(window.Vcs.Repository);
            Assert.False(Form(commit).IsEffectivelyVisible, "In a folder of no repository the Commit window shows Amend, Sign and the message, none of which it can use");
            EmptyState empty = commit.GetVisualDescendants().OfType<EmptyState>().Single();
            foreach (Control shown in empty.GetVisualChildren().OfType<Control>().Where(c => c.IsEffectivelyVisible))
            {
                Assert.True(Drawn(window, shown) == Placed(window, shown),
                    $"The Commit window's '{Words(shown)}' at {Placed(window, shown)} is cut to {Drawn(window, shown)} in a window with room for it");
            }
            Button create = empty.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == VcsController.CreateRepository);
            Visual? hit = window.InputHitTest(Placed(window, create).Center) as Visual;
            Assert.True(hit is not null && hit.GetSelfAndVisualAncestors().Contains(create),
                $"Create Git Repository at {Placed(window, create)} is covered by {hit?.GetType().Name ?? "nothing"}: a click on it misses");
            window.Close();

            window = Ui.OpenFolder(repository, width: MainWindow.SmallestWidth, height: MainWindow.SmallestHeight, languageServer: false, autosaves: false);
            commit = Shown(window);
            Assert.True(window.Vcs.Repository is not null && commit.Rows.Count == 0, $"A repository with nothing to commit lists {commit.Rows.Count} rows");
            ScrollViewer form = Form(commit);
            Assert.True(form.Extent.Height > form.Viewport.Height + 1,
                $"The form ({form.Extent.Height:0.#} tall) fits the {form.Viewport.Height:0.#} the Commit window leaves it: the test no longer reaches a short window");
            empty = commit.GetVisualDescendants().OfType<EmptyState>().Single();
            Assert.Equal("No changes", empty.Text);
            foreach (Control shown in empty.GetVisualChildren().OfType<Control>().Where(c => c.IsEffectivelyVisible))
            {
                Assert.False(Drawn(window, shown).Intersects(Drawn(window, form)),
                    $"The Commit window's '{Words(shown)}' draws at {Drawn(window, shown)}, over its form at {Drawn(window, form)}");
            }
            Button commitButton = form.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Commit");
            commitButton.BringIntoView();
            Ui.Settle();
            Assert.True(commitButton.Bounds.Height > 0 && Drawn(window, commitButton) == Placed(window, commitButton),
                $"The Commit button at {Placed(window, commitButton)}, scrolled to, is shown only as {Drawn(window, commitButton)}");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(plain);
            TestFolders.Delete(repository);
        }

        static CommitView Shown(MainWindow window)
        {
            window.Perform(AppActions.ShowCommit);
            window.Perform(AppActions.ShowBookmarks);
            Ui.Settle();
            return window.CommitView;
        }

        static ScrollViewer Form(CommitView commit) =>
            commit.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Amend").FindLogicalAncestorOfType<ScrollViewer>()!;
    }

    /// <summary>At the smallest width with no file open, the empty editors' hint wraps inside them and a folder's long name
    /// ends in an ellipsis there, where both ran past the editors' edge, cut mid-word; at a wide window the hint is one line.</summary>
    [AvaloniaFact]
    public void At_the_smallest_width_the_empty_editors_hint_wraps_inside_them_and_a_long_folder_name_ends_in_an_ellipsis()
    {
        string folder = TestFolders.Create("smallest-hint-Contoso.Inventory.Management.Service.Api-");
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(1);\n");
        MainWindow window = Ui.OpenFolder(folder, width: MainWindow.SmallestWidth, height: MainWindow.SmallestHeight, languageServer: false, autosaves: false);
        try
        {
            Rect editors = Placed(window, window.EditorArea);
            TextBlock hint = window.EditorArea.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("Open a file from the Explorer", StringComparison.Ordinal) == true);
            TextBlock title = window.EditorArea.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Path.GetFileName(folder));
            foreach (TextBlock words in (TextBlock[])[title, hint])
            {
                Rect at = Placed(window, words);
                Assert.True(words.IsEffectivelyVisible && at.Left >= editors.Left && at.Right <= editors.Right && words.TextLayout.TextLines.All(l => l.Width <= at.Width + 0.5),
                    $"'{words.Text}' stands at {at}, {words.TextLayout.Width:0.#} wide, past the editors at {editors}: it is cut at their edge");
            }
            Assert.True(hint.TextLayout.TextLines.Count > 1 && hint.TextLayout.TextLines.All(l => !l.HasCollapsed),
                $"The hint is {hint.TextLayout.TextLines.Count} line(s) in editors {editors.Width:0.#} wide: it is cut, never wrapped");
            Assert.True(title.TextLayout.TextLines is [{ HasCollapsed: true }], $"The folder's name '{title.Text}', wider than the editors, does not end in an ellipsis");

            window.Width = 1400;
            Ui.Settle();
            Assert.True(hint.TextLayout.TextLines.Count == 1, $"At a wide window the hint is {hint.TextLayout.TextLines.Count} lines where one holds it");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Editor tabs down a side take at most a third of a narrow group, the code the rest, where their column kept its
    /// 220 units at the smallest width with the Explorer open and left the code its line numbers alone; at a wide window
    /// the column is 220 wide.</summary>
    [AvaloniaFact]
    public void At_the_smallest_width_editor_tabs_down_a_side_leave_the_code_two_thirds_of_its_group()
    {
        string folder = TestFolders.Create("smallest-side-tabs-");
        foreach (string name in (string[])["One.cs", "Two.cs"])
        {
            File.WriteAllText(Path.Combine(folder, name), $"class {Path.GetFileNameWithoutExtension(name)} {{ }}\n");
        }
        MainWindow window = Ui.OpenFolder(folder, width: MainWindow.SmallestWidth, height: MainWindow.SmallestHeight, languageServer: false, autosaves: false);
        try
        {
            window.ApplySettings(window.Settings with { EditorTabs = EditorTabsPlace.Left });
            window.OpenFile(Path.Combine(folder, "One.cs"));
            window.OpenFile(Path.Combine(folder, "Two.cs"));
            Ui.Settle();
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Explorer), "The Explorer is not open beside the editors: the test no longer reaches a narrow group");
            EditorGroup group = window.EditorArea.ActiveGroup;
            double across = group.Bounds.Width;
            // Within a unit, as the layout rounds to whole pixels.
            Assert.True(group.TabStrip.Bounds.Width <= (across / 3) + 1 && Placed(window, group.Editor).Width >= (across * 2 / 3) - 1,
                $"In a group {across:0.#} wide the tabs down its side take {group.TabStrip.Bounds.Width:0.#} and leave the code {Placed(window, group.Editor).Width:0.#}");

            window.Width = 1400;
            Ui.Settle();
            Assert.True(Math.Abs(group.TabStrip.Bounds.Width - 220) < 0.5, $"At a wide window the tabs down the side are {group.TabStrip.Bounds.Width:0.#} wide, not 220");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>At the smallest width with the Explorer open, a tool window dropped on the right half of the editors opens
    /// among them as a tab, as the overlay says over them, where the frame's right quarter reached into the editors and
    /// docked it on the right; one dropped on the Explorer docks on the left beside it, where the Explorer's right part took
    /// no drop.</summary>
    [AvaloniaFact]
    public void At_the_smallest_width_a_tool_window_dropped_on_the_editors_right_half_is_an_editor_tab_and_on_the_Explorer_docks_left()
    {
        string folder = TestFolders.Create("smallest-drop-");
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Ui.OpenFolder(folder, width: MainWindow.SmallestWidth, height: MainWindow.SmallestHeight, languageServer: false, autosaves: false);
        try
        {
            window.OpenFile(Path.Combine(folder, "One.cs"));
            Ui.Settle();
            DockHost dock = window.DockHost;
            Assert.True(dock.IsShown(ToolWindowIds.Explorer), "The Explorer is not open beside the editors: the test no longer reaches narrow editors");
            Rect editors = Placed(window, window.EditorArea);
            Point onEditors = new(editors.Left + (editors.Width * 0.7), editors.Top + (editors.Height * 0.4));
            Ui.Drag(window, Ui.Centre(StripeIcon(window, ToolWindowIds.Tests), window), onEditors);
            Assert.True(dock[ToolWindowIds.Tests] is { Mode: ToolMode.Editor, IsShown: true },
                $"Unit Tests dropped at {onEditors} on the editors at {editors} stands {dock[ToolWindowIds.Tests].Mode} at {dock[ToolWindowIds.Tests].Anchor}, not among them as a tab");

            Point onExplorer = new(editors.Left - 30, editors.Top + 60);
            Ui.Drag(window, Ui.Centre(StripeIcon(window, ToolWindowIds.Problems), window), onExplorer);
            Assert.True(dock[ToolWindowIds.Problems] is { Mode: ToolMode.Docked, Anchor: ToolAnchor.LeftTop },
                $"Problems dropped at {onExplorer}, on the Explorer, stands {dock[ToolWindowIds.Problems].Mode} at {dock[ToolWindowIds.Problems].Anchor}, not on the left");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Under Cubicle at the smallest width, the left stripe, each window of its places named by a titled tab, folds
    /// away and gives its room to the editors, where it kept 40 units holding nothing; a window dragged by its titled tab
    /// shows it again as a place to drop on, and dropped there its icon keeps it shown.</summary>
    [AvaloniaFact]
    public void Under_Cubicle_at_the_smallest_width_the_empty_left_stripe_folds_away_and_shows_while_a_window_is_dragged()
    {
        string folder = TestFolders.Create("smallest-stripe-");
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Ui.OpenFolder(folder, width: MainWindow.SmallestWidth, height: MainWindow.SmallestHeight, languageServer: false, autosaves: false);
        try
        {
            window.ApplySettings(window.Settings with { Look = SourcedTheme.BuiltIn("Cubicle") });
            window.OpenFile(Path.Combine(folder, "One.cs"));
            Ui.Settle();
            DockHost dock = window.DockHost;
            Border left = StripeBars(window)[0];
            Assert.True(dock.ColumnDefinitions[0].ActualWidth == 0 && !left.IsEffectivelyVisible && Placed(window, window.EditorArea).Left < 1,
                $"Cubicle's left stripe, holding no icon, takes {dock.ColumnDefinitions[0].ActualWidth:0.#} units, the editors starting at {Placed(window, window.EditorArea).Left:0.#}");

            ToolWindow build = dock[ToolWindowIds.Build];
            Border tab = dock.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(StyleClasses.Tab) && Equals(b.Tag, build.Id) && b.IsEffectivelyVisible)
                ?? throw new Xunit.Sdk.XunitException("Cubicle shows no titled tab for Build");
            Point from = Ui.Centre(tab, window);
            window.MouseDown(from, MouseButton.Left);
            for (int step = 1; step <= 4; step++)
            {
                window.MouseMove(from + new Point(0, -20 * step), RawInputModifiers.LeftMouseButton);
            }
            Ui.Settle();
            Assert.True(dock.ColumnDefinitions[0].ActualWidth == 40 && left.IsEffectivelyVisible,
                $"With Build dragged, the left stripe takes {dock.ColumnDefinitions[0].ActualWidth:0.#} units: it is no place to drop it");
            Point onStripe = new(Ui.Centre(left, window).X, Placed(window, left).Top + 60);
            window.MouseMove(onStripe, RawInputModifiers.LeftMouseButton);
            window.MouseUp(onStripe, MouseButton.Left);
            Ui.Settle();
            Assert.True(build.Anchor == ToolAnchor.LeftTop && StripeIcon(window, ToolWindowIds.Build).IsEffectivelyVisible && dock.ColumnDefinitions[0].ActualWidth == 40,
                $"Build dropped on the left stripe stands at {build.Anchor}, the stripe {dock.ColumnDefinitions[0].ActualWidth:0.#} wide");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    private static ToggleButton StripeIcon(MainWindow window, string id) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(b => Equals(b.Tag, id))
            ?? throw new Xunit.Sdk.XunitException($"No stripe shows {id}'s icon");

    /// <summary>The stripes, the left one first.</summary>
    private static Border[] StripeBars(MainWindow window) =>
        [.. window.DockHost.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.StripeBar)).OrderBy(b => Grid.GetColumn(b))];

    private static Control Named(MainWindow window, string name) =>
        window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name) ?? throw new Xunit.Sdk.XunitException($"The window has no {name}");

    private static string Name(Control control) => control.Name ?? control.GetType().Name;

    private static string Words(Control control) => string.Join(" ", control.GetSelfAndVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Take(1));

    private static Rect Placed(Visual window, Visual control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    /// <summary>What of the control is drawn: its place cut by every visual above it that clips.</summary>
    private static Rect Drawn(Visual window, Visual control) =>
        control.GetSelfAndVisualAncestors().TakeWhile(v => v != window).Where(v => v.ClipToBounds)
            .Aggregate(Placed(window, control), (drawn, clip) => drawn.Intersect(Placed(window, clip)));
}
