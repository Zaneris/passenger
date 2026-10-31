using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>At the smallest window (MainWindow.SmallestWidth wide and SmallestHeight tall) everything shown can be read
/// and reached: the Welcome page scrolls to each of its tiles, Find in Files stands inside the window, the toolbar's long
/// names give way with an ellipsis and never draw over each other, and what the status bar says stands whole in front of
/// the file's path.</summary>
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
                Control[] parts = [.. ((Panel)Named(window, "ToolbarStart")).Children.Concat(((Panel)Named(window, "ToolbarEnd")).Children)
                    .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0)];
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

    private static Control Named(MainWindow window, string name) =>
        window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name) ?? throw new Xunit.Sdk.XunitException($"The window has no {name}");

    private static string Name(Control control) => control.Name ?? control.GetType().Name;

    private static string Words(Control control) => string.Join(" ", control.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Take(1));

    private static Rect Placed(Visual window, Visual control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
}
