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
/// names give way with an ellipsis, shown whole in a tip, and never draw over each other or the window's buttons, what
/// the status bar says stands whole in front of the file's path, the Commit window sharing its column draws nothing over
/// its form, the empty editors' words stand inside them, editor tabs down a side leave the code its room, a Markdown
/// file's preview stands under its file, a tool window dropped on the editors is a tab there, and a stripe with no icon
/// gives its room to the editors but while a window is dragged.</summary>
public sealed class SmallestWindowTests
{
    /// <summary>At the smallest window the Welcome tiles, Find in Files, the toolbar, the status bar, a refusal and the
    /// Commit window's form stand inside it, uncovered and reachable, and words cut short show whole in a tip, as one
    /// flow, each step the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void At_the_smallest_window_what_a_person_must_reach_stands_inside_it_and_takes_a_click()
    {
        Flow.StepApart<SmallestWindowTests>("at the smallest window every Welcome tile scrolls into view uncovered and takes a click", t => t.At_the_smallest_window_every_Welcome_tile_scrolls_into_view_uncovered_and_takes_a_click());
        Flow.StepApart<SmallestWindowTests>("at the smallest window Find in Files stands inside the window its results and preview giving way", t => t.At_the_smallest_window_Find_in_Files_stands_inside_the_window_its_results_and_preview_giving_way());
        Flow.StepApart<SmallestWindowTests>("at the smallest widths the toolbars long names end in an ellipsis and nothing on it draws over anything", t => t.At_the_smallest_widths_the_toolbars_long_names_end_in_an_ellipsis_and_nothing_on_it_draws_over_anything());
        Flow.StepApart<SmallestWindowTests>("at the smallest width a choices count keeps the status bars widgets inside the window", t => t.At_the_smallest_width_a_choices_count_keeps_the_status_bars_widgets_inside_the_window());
        Flow.StepApart<SmallestWindowTests>("at the smallest width a refusal stands whole in front of a deep files path", t => t.At_the_smallest_width_a_refusal_stands_whole_in_front_of_a_deep_files_path());
        Flow.StepApart<SmallestWindowTests>("at the smallest window the commit windows empty state never draws over its form which scrolls to commit", t => t.At_the_smallest_window_the_commit_windows_empty_state_never_draws_over_its_form_which_scrolls_to_commit());
        Flow.StepApart<SmallestWindowTests>("a path cut short shows whole in a tip only while cut short and a control over it keeps a tip of its own", t => t.A_path_cut_short_shows_whole_in_a_tip_only_while_cut_short_and_a_control_over_it_keeps_a_tip_of_its_own());
    }

    /// <summary>A field's path note cut short shows its whole path in a tip, wrapping inside it after a separator, never
    /// inside a part, and none once its path is short enough to show whole; one under a control whose tip is no words (a
    /// card of its own) shows that tip as it is.</summary>
    private void A_path_cut_short_shows_whole_in_a_tip_only_while_cut_short_and_a_control_over_it_keeps_a_tip_of_its_own()
    {
        string path = Path.Combine("Workspace", "Contoso.Inventory.Management", "Reconciliation", "Service.Api", "Output");
        TextBlock note = FormRows.Note(path, path: true);
        TextBlock carded = FormRows.Note(path, path: true);
        TextBlock card = new() { Text = "A card of its own" };
        Border holder = new() { Child = carded };
        ToolTip.SetTip(holder, card);
        Button elsewhere = new() { Content = "Elsewhere" };
        Window window = new() { Width = 220, Height = 200, Content = new StackPanel { Children = { note, holder, elsewhere } } };
        try
        {
            window.Show();
            Ui.Settle();
            Assert.True(TrimmedTip.IsCutShort(note) && TrimmedTip.IsCutShort(carded), "The notes show the long path whole, so nothing here is cut short");
            Ui.Hover(note);
            Ui.WaitFor(() => ToolTip.GetIsOpen(note), "the whole path's tip");
            TextBlock shown = Ui.TipWords(note);
            string said = shown.Text ?? string.Empty;
            Assert.True(said == TrimmedTip.Parted(path) && !shown.TextLayout.TextLines.Any(line => line.HasCollapsed), $"The tip of the cut path says '{said}', not the whole path");
            string[] lines = [.. shown.TextLayout.TextLines.Select(line => said[line.FirstTextSourceIndex..Math.Min(said.Length, line.FirstTextSourceIndex + line.Length)])];
            Assert.True(lines.Length > 1 && lines[..^1].All(line => line.EndsWith(TrimmedTip.PartBreak)),
                $"The tip's path wraps inside a part rather than after a separator: {string.Join(" | ", lines)}");

            Ui.Hover(carded);
            Ui.WaitFor(() => ToolTip.GetIsOpen(holder), "the card over the cut path");
            Assert.Same(card, ToolTip.GetTip(holder));
            Assert.Null(ToolTip.GetTip(carded));

            Ui.Hover(elsewhere);
            Ui.WaitFor(() => !ToolTip.GetIsOpen(note) && !ToolTip.GetIsOpen(holder), "the tips to close");
            Assert.Null(ToolTip.GetTip(note));
            note.Text = "Output";
            Ui.Settle();
            Assert.False(TrimmedTip.IsCutShort(note), "A note of one short folder is cut short");
            Ui.Hover(note, new Point(6, note.Bounds.Height / 2));
            Assert.Null(ToolTip.GetTip(note));
        }
        finally
        {
            window.Close();
        }
    }

    private void At_the_smallest_window_every_Welcome_tile_scrolls_into_view_uncovered_and_takes_a_click()
    {
        string settings = TestFolders.Create("smallest-welcome-");
        MainWindow window = new() { Width = MainWindow.SmallestWidth, Height = MainWindow.SmallestHeight, StateFolder = settings };
        try
        {
            window.Show();
            window.Start(LaunchOptions.Empty);
            Ui.Settle();
            Button[] tiles = [.. window.Welcome.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains(StyleClasses.WelcomeAction))];
            Assert.Equal(5, tiles.Length);
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

    private void At_the_smallest_window_Find_in_Files_stands_inside_the_window_its_results_and_preview_giving_way()
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

    private void At_the_smallest_widths_the_toolbars_long_names_end_in_an_ellipsis_and_nothing_on_it_draws_over_anything()
    {
        string folder = TestFolders.Create($"smallest-toolbar-{new string('W', 56)}-");
        string fileName = $"{new string('W', 72)}.cs";
        string file = Path.Combine(folder, fileName);
        File.WriteAllText(Path.Combine(folder, "Contoso.Inventory.Management.Service.Api.csproj"), TestProjects.Console());
        File.WriteAllText(file, "System.Console.WriteLine(1);\n");
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

            window.Width = 850;
            Ui.Settle();
            TextBlock project = (TextBlock)Named(window, "ProjectName");
            Button projectButton = (Button)Named(window, "ProjectButton");
            Assert.True(TrimmedTip.IsCutShort(project), $"The toolbar shows the project's name '{project.Text}' whole at 850 wide, so nothing here is cut short");
            string description = Assert.IsType<string>(ToolTip.GetTip(projectButton));
            Ui.Hover(projectButton);
            Ui.WaitFor(() => ToolTip.GetIsOpen(projectButton), "the project's tip");
            Assert.Equal($"{TrimmedTip.Parted(project.Text!)}\n\n{description}", Ui.TipWords(projectButton).Text);

            Ui.WaitFor(() => window.ExplorerView.Locate(file), "the long file to be shown in the Explorer");
            Ui.Settle();
            TextBlock label = window.ExplorerView.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == fileName && text.IsEffectivelyVisible);
            Assert.True(TrimmedTip.IsCutShort(label), "The Explorer shows the long file's name whole, so nothing here is cut short");
            Ui.Hover(label);
            Ui.WaitFor(() => ToolTip.GetIsOpen(label) && !ToolTip.GetIsOpen(projectButton), "the Explorer name's tip to replace the project's");
            Assert.Equal(TrimmedTip.Parted(fileName), Ui.TipWords(label).Text);
            Assert.Equal(description, ToolTip.GetTip(projectButton));
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>At the smallest width, with a choice's count after the caret's place (1:20 (14 chars)) and Git's changes
    /// beside it, the status bar's widgets and words all stand inside the window, nothing over another, the path and what
    /// the bar says giving way first (LayoutFaults); where the last widgets ran past the window's edge.</summary>
    private void At_the_smallest_width_a_choices_count_keeps_the_status_bars_widgets_inside_the_window()
    {
        (string folder, string notes) = Changed("smallest-status-choice-");
        MainWindow window = Ui.OpenFolder(folder, width: MainWindow.SmallestWidth, height: MainWindow.SmallestHeight, languageServer: false, autosaves: false);
        try
        {
            window.OpenFile(notes);
            Ui.Settle();
            window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single(new Passenger.Text.Selection(5, 19));
            Ui.WaitFor(() => Named(window, "GitChangesButton").IsEffectivelyVisible, "Git's changed file in the status bar");
            List<string> faults = LayoutFaults.Of(window, Named(window, "StatusBarFrame"), $"At {MainWindow.SmallestWidth} wide with 14 characters chosen, the status bar");
            Assert.True(faults.Count == 0, string.Join("\n", faults));
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A folder in Git whose Notes.txt is changed since its commit.</summary>
    private static (string Folder, string Notes) Changed(string prefix)
    {
        string folder = TestFolders.Create(prefix);
        string notes = Path.Combine(folder, "Notes.txt");
        File.WriteAllText(notes, "find the words here\nand the words here again\n");
        TestGit.Init(folder);
        File.WriteAllText(notes, "find the words here\nand the words over there again\nline three\n");
        return (folder, notes);
    }

    private void At_the_smallest_width_a_refusal_stands_whole_in_front_of_a_deep_files_path()
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
    private void At_the_smallest_window_the_commit_windows_empty_state_never_draws_over_its_form_which_scrolls_to_commit()
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
            Button create = empty.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == ActionTitles.CreateRepository.English);
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
