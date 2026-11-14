using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;

namespace Passenger.Tests;

/// <summary>At the smallest window (MainWindow.SmallestWidth wide and SmallestHeight tall) everything shown can be read
/// and reached: the Welcome page scrolls to each of its tiles, Find in Files stands inside the window, the toolbar's long
/// names give way with an ellipsis and never draw over each other or the window's buttons, what the status bar says
/// stands whole in front of the file's path, and the Commit window sharing its column draws nothing over its form.</summary>
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
