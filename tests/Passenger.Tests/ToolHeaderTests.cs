using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Profiler tabs remain reachable while real runtime actions and status retain their own fitted space.</summary>
[Collection(Alone.Name)]
public sealed class ToolHeaderTests : IDisposable
{
    private readonly string folder = TestFolders.Create("tool-header-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Profiler_tabs_and_running_actions_stay_reachable_without_losing_page_or_focus_on_resize(bool light)
    {
        string project = PathNames.Canonical(Path.Combine(folder, "Header.csproj"));
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "while (!System.IO.File.Exists(\"finish\")) { System.Threading.Thread.SpinWait(100_000); }\n");
        Restore.Run(folder);
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("Program.cs");
        Ui.Settle();
        string name = "A configuration with a deliberately long readable name";
        window.SetConfigurations([new RunConfiguration { Name = name, Project = project, WorkingDirectory = folder }], name);
        try
        {
            string? failed = Ui.Await(window.ProfileAsync(), "the real program to start under the profiler", TimeSpan.FromMinutes(3));
            Assert.True(failed is null, failed);
            Ui.WaitFor(() => window.ProfilerView.Status.StartsWith("Profiling ", StringComparison.Ordinal), "the runtime status");
            Control actions = Assert.Single(window.ProfilerView.GetVisualDescendants().OfType<Control>(), control => control.Name == "ProfilerRunTools");
            Control tabsBar = Assert.Single(window.ProfilerView.GetVisualDescendants().OfType<Control>(), control => control.Name == "ProfilerTabsBar");
            ScrollViewer strip = Assert.Single(window.ProfilerView.GetVisualDescendants().OfType<ScrollViewer>(), control => control.Name == "ProfilerPageStrip");
            foreach (double width in new double[] { 1200, 683, 640, 1200 })
            {
                window.Width = width;
                Ui.Settle();
                CheckActions(actions);
                CheckActions(tabsBar);
                Assert.Equal(3, actions.GetVisualDescendants().OfType<Button>().Count(button => button.IsEffectivelyVisible));
                UiAudit.Capture(window, $"profiler-running-{width:0}-{(light ? "light" : "dark")}");
            }
            File.WriteAllText(Path.Combine(folder, "finish"), string.Empty);
            Ui.WaitFor(() => window.ProfilerView.Snapshot is not null, "the actual profile's snapshot", TimeSpan.FromMinutes(2));
            Ui.Settle();
            Assert.False(actions.IsVisible);
            ToggleButton[] pages = [.. strip.GetVisualDescendants().OfType<ToggleButton>().Where(button => button.Classes.Contains("panelTab"))];
            Assert.Equal(8, pages.Length);
            foreach (ToggleButton page in pages)
            {
                window.Width = 640;
                Ui.Settle();
                for (int attempt = 0; attempt < 20 && !Inside(page, strip); attempt++)
                {
                    double direction = page.TranslatePoint(default, strip)!.Value.X < 0 ? 3 : -3;
                    Point at = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
                    window.MouseWheel(at, new Vector(direction, 0));
                    Ui.Settle();
                }
                Assert.True(Inside(page, strip), $"The '{page.Content}' tab could not be brought fully into its narrow strip by scrolling");
                Point hit = page.TranslatePoint(new Point(page.Bounds.Width / 2, page.Bounds.Height / 2), window)!.Value;
                window.MouseMove(hit);
                window.MouseDown(hit, MouseButton.Left);
                window.MouseUp(hit, MouseButton.Left);
                Ui.Settle();
                Assert.True(page.IsChecked == true);
                Assert.Same(page, Assert.Single(pages, button => button.IsChecked == true));
                object? focus = window.FocusManager?.GetFocusedElement();
                foreach (double width in new double[] { 1200, 683, 640, 1200 })
                {
                    window.Width = width;
                    Ui.Settle();
                    Assert.True(page.IsChecked == true && Inside(page, strip), $"'{page.Content}' lost selection or escaped its strip at {width}px");
                    Assert.Same(page, Assert.Single(pages, button => button.IsChecked == true));
                    Assert.Same(focus, window.FocusManager?.GetFocusedElement());
                    CheckActions(tabsBar);
                    UiAudit.Capture(window, $"profiler-page-{page.Content}-{width:0}-{(light ? "light" : "dark")}");
                }
            }
            window.Width = 640;
            Ui.Settle();
            foreach (string navigation in new[] { "shift+tab", "tab" })
            {
                foreach (ToggleButton page in navigation == "tab" ? pages : pages.Reverse())
                {
                    for (int attempt = 0; attempt < 100 && window.FocusManager?.GetFocusedElement() != page; attempt++)
                    {
                        Ui.Press(window, navigation);
                    }
                    Assert.Same(page, window.FocusManager?.GetFocusedElement());
                    Assert.True(Inside(page, strip), $"The keyboard-focused '{page.Content}' tab is clipped out of its strip");
                    Ui.Press(window, "space");
                    Assert.Same(page, Assert.Single(pages, button => button.IsChecked == true));
                }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(folder, "finish"), string.Empty);
            window.Close();
        }
    }

    private static bool Inside(Control child, Control parent) => child.TranslatePoint(default, parent) is { } at
        && at.X >= -0.5 && at.Y >= -0.5 && at.X + child.Bounds.Width <= parent.Bounds.Width + 0.5
        && at.Y + child.Bounds.Height <= parent.Bounds.Height + 0.5;

    private static void CheckActions(Control bar)
    {
        List<Rect> taken = [];
        foreach (Button button in bar.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible
            && !button.GetVisualAncestors().TakeWhile(ancestor => ancestor != bar).OfType<ScrollViewer>().Any()))
        {
            Assert.True(Inside(button, bar), $"'{button.Content ?? ToolTip.GetTip(button)}' escapes the {bar.Name} toolbar");
            Rect bounds = new(button.TranslatePoint(default, bar)!.Value, button.Bounds.Size);
            Assert.DoesNotContain(taken, prior => prior.Intersects(bounds));
            taken.Add(bounds);
        }
    }
}
