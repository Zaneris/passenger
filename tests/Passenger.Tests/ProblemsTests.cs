using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Problems as Rider keeps them without a build: the current file's as they are typed, and with solution-wide
/// analysis on, every file's, a file nobody has open included, grouped by project and file.</summary>
public sealed class ProblemsTests : IDisposable
{
    private readonly string folder = TestFolders.Create("problems-");

    public ProblemsTests()
    {
        Write("Sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        Write("Program.cs", "class Program\n{\n    static void Main()\n    {\n    }\n}\n");
        Write("Helper.cs", "static class Helper\n{\n    public static int Twice(int value) => value * 2;\n}\n");
        Write("Caller.cs", "static class Caller\n{\n    public static int Four() => Helper.Twice(2);\n}\n");
        Write("Bad.cs", "class Bad\n{\n    public int Count = \"none\";\n}\n");
        TestFolders.Prepare(() => Restore.Run(folder), folder);
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Solution_wide_analysis_lists_every_files_errors_and_follows_an_edit_that_breaks_a_file_nobody_has_open()
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("Helper.cs");
        Ui.Settle();
        ProblemsView problems = window.BottomPanel.Problems;
        Assert.Null(problems.SolutionProblems);

        window.Perform(AppActions.ToggleSolutionAnalysis);
        Assert.True(window.Settings.SolutionWideAnalysis);
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true, "the closed file's error");
        Problem bad = problems.SolutionProblems!.First(p => p.Code == "CS0029");
        Assert.EndsWith("Bad.cs", bad.Path, StringComparison.Ordinal);
        Assert.Equal(new TextPosition(2, 23), bad.Position);
        problems.Shown = ProblemsTab.Solution;
        Assert.Equal(["Sample", "Bad.cs"], problems.Rows.Where(r => r.Problem is null).Select(r => r.Label).Take(2));

        // Helper's method renamed by typing: Caller, closed, no longer compiles, and says so without a build.
        string text = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
        int at = text.IndexOf("Twice", StringComparison.Ordinal);
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(new Selection(at, at + "Twice".Length));
        window.KeyTextInput("Thrice");
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Path?.EndsWith("Caller.cs", StringComparison.Ordinal) == true) == true,
            "the broken caller's error", TimeSpan.FromMinutes(1));
        Assert.Empty(problems.FileProblems);

        // The open file's own problems are the File tab's, as they are typed.
        window.KeyTextInput(" x");
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error), "the open file's error");
        Assert.Empty(problems.Problems);

        // A problem's menu copies its message.
        window.Perform(AppActions.ShowProblems);
        problems.Shown = ProblemsTab.File;
        Avalonia.Controls.ListBox rows = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(problems).OfType<Avalonia.Controls.ListBox>().First();
        ProblemRow chosen = problems.Rows.First(r => r.Problem is { Severity: ProblemSeverity.Error });
        rows.SelectedItem = chosen;
        Ui.ChooseFromMenu(rows, "Copy Message");
        Ui.WaitFor(() => Ui.Clipboard(window) == chosen.Problem!.Message, "the message on the clipboard");

        window.Perform(AppActions.ToggleSolutionAnalysis);
        Assert.Null(problems.SolutionProblems);
        Assert.Equal(ProblemsTab.File, problems.Shown);
        window.Close();
    }

    /// <summary>Lines typed above a file's problems move them, and the File tab says where they are now after every key,
    /// each row's place shown on its own line of the list as it was: the list's rows are not made again for every key, as
    /// they were while the file had problems (every row's controls made, styled and laid out again a key).</summary>
    [AvaloniaFact]
    public void Lines_typed_above_a_files_problems_move_their_rows_without_making_the_list_again()
    {
        Write("Several.cs", "class Several\n{\n    int First = \"one\";\n    int Second = \"two\";\n    string Third = 3;\n}\n");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("Several.cs");
        window.Perform(AppActions.ShowProblems);
        Ui.Settle();
        ProblemsView problems = window.BottomPanel.Problems;
        Assert.Equal(ProblemsTab.File, problems.Shown);
        Ui.WaitFor(() => problems.FileProblems.Count(p => p.Severity == ProblemSeverity.Error) == 3, "the file's three errors");
        Avalonia.Controls.ListBox list = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(problems).OfType<Avalonia.Controls.ListBox>().First();
        // The text on each row that says where its problem is, as the list shows it (its rows' boxes are reused anyway).
        Avalonia.Controls.TextBlock[] Shown() => [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<Avalonia.Controls.ListBoxItem>()
            .Select(item => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(item).OfType<Avalonia.Controls.TextBlock>()
                .First(t => t.Text?.StartsWith("Several.cs:", StringComparison.Ordinal) == true))];
        string[] Places() => [.. Shown().Select(t => t.Text ?? string.Empty)];
        Avalonia.Controls.TextBlock[] before = Shown();
        Assert.Equal(3, before.Length);
        Assert.Equal(["Several.cs:3:17", "Several.cs:4:18", "Several.cs:5:20"], Places());

        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(1, 1));
        editor.Focus();
        for (int i = 1; i <= 3; i++)
        {
            Ui.Press(window, "ENTER");
            Assert.Equal([2 + i, 3 + i, 4 + i], problems.FileProblems.Select(p => p.Position.Line));
            Assert.True(Shown().SequenceEqual(before), $"After {i} lines went in above the problems the File tab's rows were made again");
            Assert.Equal([$"Several.cs:{3 + i}:17", $"Several.cs:{4 + i}:18", $"Several.cs:{5 + i}:20"], Places());
        }

        // Once the server's own diagnostics come again, they are where the rows say.
        Ui.Settle();
        Assert.Equal(["Several.cs:6:17", "Several.cs:7:18", "Several.cs:8:20"], Places());
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Long_file_and_problem_categories_remain_reachable_with_real_diagnostics_in_narrow_panes(bool light)
    {
        const string Name = "A very long file name that keeps the current file tab readable while all other problem categories remain reachable.cs";
        Write(Name, "class Another { public int Count = \"none\"; }\n");
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(Name);
        try
        {
            ProblemsView problems = window.BottomPanel.Problems;
            Ui.WaitFor(() => problems.FileProblems.Any(problem => problem.Severity == ProblemSeverity.Error && problem.Message.Contains("string", StringComparison.Ordinal)),
                "the actual long-named file's type error");
            window.Perform(AppActions.ToggleSolutionAnalysis);
            Ui.WaitFor(() => problems.SolutionProblems?.Any(problem => problem.Code == "CS0029") == true, "the actual solution's diagnostics");
            BuildOutcome built = Ui.Await(window.BuildAsync(), "the actual compiler's errors");
            Assert.Equal(BuildStatus.Failed, built.Status);
            Assert.Contains(built.Problems, problem => problem.Code == "CS0029");
            window.ShowToolWindow("problems");
            Ui.Settle();
            ToggleButton[] pages = [.. problems.GetVisualDescendants().OfType<ToggleButton>().Where(button => button.Classes.Contains("panelTab") && button.IsVisible)];
            ProblemsTab[] kinds = [ProblemsTab.File, ProblemsTab.Solution, ProblemsTab.Toolset, ProblemsTab.Build, ProblemsTab.Bindings];
            Assert.Equal(kinds.Length, pages.Length);
            Control strip = problems.GetVisualDescendants().OfType<ScrollViewer>().SingleOrDefault(view => view.Name == "ProblemsCategoryStrip") ?? (Control)problems;
            DockLayout bottom = window.DockHost.Capture();
            window.Width = 640;
            window.DockHost.Apply(bottom with
            {
                LeftWidth = 200,
                Windows = bottom.Windows.ToDictionary(pair => pair.Key, pair => pair.Key == "problems"
                    ? pair.Value with { Anchor = ToolAnchor.LeftTop, Shown = true }
                    : pair.Value.Anchor == ToolAnchor.LeftTop ? pair.Value with { Shown = false } : pair.Value),
            });
            Ui.Settle();
            foreach (string placement in new[] { "side-200", "bottom" })
            {
                if (placement == "bottom")
                {
                    window.DockHost.Apply(bottom);
                    Ui.Settle();
                }
                foreach (double width in new double[] { 640, 683, 1200 })
                {
                    window.Width = width;
                    Ui.Settle();
                    for (int index = 0; index < pages.Length; index++)
                    {
                        ToggleButton page = pages[index];
                        for (int attempt = 0; attempt < 20 && !Inside(page); attempt++)
                        {
                            double left = page.TranslatePoint(default, strip)!.Value.X;
                            double clipped = left < 0 ? left : left + page.Bounds.Width - strip.Bounds.Width;
                            // Fractional wheel input can reveal a tab almost as wide as the strip without overshooting it.
                            double direction = -Math.Clamp(clipped / 50, -3, 3);
                            Point wheel = new(strip.Bounds.Width / 2, 16);
                            window.MouseWheel(strip.TranslatePoint(wheel, window)!.Value, new Vector(direction, 0));
                            Ui.Settle();
                        }
                        bool contained = Inside(page);
                        if (!contained)
                        {
                            UiAudit.Capture(window, $"problems-unreachable-{placement}-{(light ? "light" : "dark")}");
                        }
                        Assert.True(contained, $"The '{page.Content}' category cannot be brought fully into the actual {problems.Bounds.Width}px pane");
                        Point at = page.TranslatePoint(new Point(page.Bounds.Width / 2, page.Bounds.Height / 2), window)!.Value;
                        window.MouseMove(at);
                        Assert.True(page.IsPointerOver);
                        window.MouseDown(at, MouseButton.Left);
                        window.MouseUp(at, MouseButton.Left);
                        Ui.Settle();
                        Assert.Equal(kinds[index], problems.Shown);
                        Assert.Same(page, Assert.Single(pages, button => button.IsChecked == true));
                        Assert.Equal(page.Content, ToolTip.GetTip(page));
                        TextBlock label = Assert.Single(page.GetVisualDescendants().OfType<TextBlock>());
                        Assert.True(label.TextLayout.Width <= label.Bounds.Width + 1, "A long Problems category name must ellipsize beside the other reachable categories");
                        UiAudit.Capture(window, $"problems-tabs-{placement}-{width:0}-{kinds[index]}-{(light ? "light" : "dark")}");
                    }
                }
            }
            window.Width = 640;
            Ui.Settle();
            Assert.True(pages[0].Focus());
            Ui.Settle();
            for (int index = 0; index < pages.Length; index++)
            {
                for (int attempt = 0; attempt < 80 && !pages[index].IsFocused; attempt++)
                {
                    Ui.Press(window, "tab");
                }
                bool focused = pages[index].IsFocused && Inside(pages[index]);
                if (!focused)
                {
                    UiAudit.Capture(window, $"problems-keyboard-unreachable-{kinds[index]}-{(light ? "light" : "dark")}");
                }
                Assert.True(focused, $"Keyboard focus must reveal the complete Problems category '{pages[index].Content}': focused={pages[index].IsFocused}, bounds={pages[index].Bounds}, at={pages[index].TranslatePoint(default, strip)}, strip={strip.Bounds}");
                Ui.Press(window, "space");
                Assert.Equal(kinds[index], problems.Shown);
            }
            Assert.True(pages[0].Focus());
            Ui.Settle();
            Ui.Press(window, "space");
            Ui.Press(window, "tab");
            Assert.True(pages[1].IsFocused);
            foreach (double width in new double[] { 1200, 683, 640, 1200 })
            {
                window.Width = width;
                Ui.Settle();
                Assert.Equal(ProblemsTab.File, problems.Shown);
                Assert.True(pages[1].IsFocused && Inside(pages[1]), "Resizing must preserve the visible inactive category's keyboard focus without switching the shown problems");
                UiAudit.Capture(window, $"problems-focused-category-{width:0}-{(light ? "light" : "dark")}");
            }

            bool Inside(Control page) => page.TranslatePoint(default, strip) is { } at && at.X >= -0.5
                && at.X + page.Bounds.Width <= strip.Bounds.Width + 0.5;
        }
        finally
        {
            window.Close();
        }
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(folder, name), text);
}
