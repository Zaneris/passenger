using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Testing;
using Passenger.App.ToolWindows;

namespace Passenger.Tests;

/// <summary>The Unit Tests window's tally as a run of 1,502 tests counts up: each outcome's count stands whole inside its
/// filter, the filter's padding after it and the bar's gap before what follows, however many digits the counts grow to
/// and however narrow the window.</summary>
public sealed class UnitTestsCountsTests
{
    private const string Project = "/src/Sums.Tests/Sums.Tests.csproj";
    private const string Framework = "net11.0";

    [AvaloniaFact]
    public void A_runs_counts_keep_their_room_and_gaps_as_they_grow_a_digit_at_every_width()
    {
        List<TestItem> found = Found(1502);
        TestTree tree = new();
        tree.Load([new ProjectTests(Project, "Sums.Tests", Code(found), [new TestSource(Framework, found)])]);
        TestsView view = new(tree);
        Window window = new() { Width = 1200, Height = 400, Content = view };
        window.Show();
        List<string> faults = [];
        try
        {
            foreach (double width in (double[])[1200, MainWindow.SmallestWidth, 360])
            {
                window.Width = width;
                tree.Begin(found);
                int passed = 0;
                Dictionary<int, double> widths = [];
                foreach (int next in (int[])[1, 9, 10, 99, 100, 999, 1000, 1057, 1111, 1381, 1502])
                {
                    tree.Apply(found[passed..next].Select(t => new TestOutcome(t, TestState.Passed, TimeSpan.FromMilliseconds(1))));
                    passed = next;
                    Ui.Settle();
                    string what = $"At {width:0} px wide with {passed} of {found.Count} tests passed, the Unit Tests window's bar";
                    faults.AddRange(LayoutFaults.Of(window, Bar(view), what));
                    faults.AddRange(GapsOf(Bar(view), what));
                    double passedWidth = Filters(view).Single(f => AutomationProperties.GetName(f)?.StartsWith("Filter Passed Tests", StringComparison.Ordinal) == true).Bounds.Width;
                    int digits = passed.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
                    if (widths.TryGetValue(digits, out double before) && Math.Abs(before - passedWidth) > 0.5)
                    {
                        faults.Add($"{what} widens the passed filter from {before:0.#} to {passedWidth:0.#} px as its count of {digits} digits counts on to {passed}: what follows it moves at each count");
                    }
                    widths[digits] = passedWidth;
                }
                tree.End();
                Ui.Settle();
            }
        }
        finally
        {
            window.Close();
        }
        Assert.True(faults.Count == 0, string.Join("\n", faults.Distinct().Take(20)));
    }

    /// <summary>Where a count is drawn past the room its filter keeps for it, or the filter stands nearer the next part of
    /// the bar than the bar's gap.</summary>
    private static IEnumerable<string> GapsOf(ToolFlow bar, string what)
    {
        Control[] shown = [.. bar.Children.Where(c => c.IsVisible)];
        for (int i = 0; i < shown.Length; i++)
        {
            if (shown[i] is not ToggleButton { Content: StackPanel } filter)
            {
                continue;
            }
            TextBlock count = Count(filter);
            Rect box = filter.Bounds;
            double end = (count.TranslatePoint(new Point(count.TextLayout.WidthIncludingTrailingWhitespace, 0), bar)?.X ?? 0) + filter.Padding.Right;
            if (end > box.Right + 0.5)
            {
                yield return $"{what} draws the count '{count.Text}' {end - box.Right:0.#} px past its filter's padding of {filter.Padding.Right:0}, over the gap after it";
            }
            if (i + 1 < shown.Length && Math.Abs(shown[i + 1].Bounds.Top - box.Top) < 1 && shown[i + 1].Bounds.Left - box.Right < bar.Spacing - 0.5)
            {
                yield return $"{what} leaves {shown[i + 1].Bounds.Left - box.Right:0.#} px after the count '{count.Text}', where a bar's gap is {bar.Spacing:0}";
            }
        }
    }

    private static ToolFlow Bar(TestsView view) => view.GetVisualDescendants().OfType<ToolFlow>().First();

    /// <summary>The outcome filters shown, each with its count.</summary>
    private static IEnumerable<ToggleButton> Filters(TestsView view) => Bar(view).Children.OfType<ToggleButton>().Where(f => f.IsVisible && f.Content is StackPanel);

    private static TextBlock Count(ToggleButton filter) => ((StackPanel)filter.Content!).Children.OfType<TextBlock>().Single();

    /// <summary>A runner's tests, ten methods to a class.</summary>
    private static List<TestItem> Found(int count) =>
        [.. Enumerable.Range(0, count).Select(at => new TestItem(Project, $"Sums.Tests.C{at / 10}Tests.Adds{at % 10}", $"Sums.Tests.C{at / 10}Tests.Adds{at % 10}")
        {
            Framework = Framework, Path = $"/src/Sums.Tests/C{at / 10}Tests.cs", Line = (at % 10) + 1,
        })];

    /// <summary>The same methods as the code declares them.</summary>
    private static CodeTests Code(List<TestItem> found) => new(
        [.. found.Select(t => t with { Framework = null })],
        found.GroupBy(t => t.Path!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(t => t.FullyQualifiedName[(t.FullyQualifiedName.LastIndexOf('.') + 1)..]).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal));
}
