using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Terminal;

namespace Passenger.Tests;

/// <summary>Tool-window tabs share one presentation, while real terminal sessions keep their selected shell, reading
/// position and keys through selecting and closing tabs. The window audit visits every registered tool surface.</summary>
[Collection(Alone.Name)]
public sealed class ToolTabTests : IDisposable
{
    private readonly string folder = TestFolders.Create("tool-tabs-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tool_tabs_share_their_style_and_terminal_tabs_keep_the_selected_shell_and_its_keys(bool light)
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.TerminalPanel.Shell = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ["/D", "/Q"])
            : ("/bin/sh", []);
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("One.cs");
        window.ShowToolWindow("terminal");
        Ui.Settle();
        try
        {
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the first shell");
            TerminalSession first = window.TerminalPanel.Active!;
            Click(window.TerminalPanel.HeaderTools);
            Ui.WaitFor(() => window.TerminalPanel.Tabs.Count == 2 && window.TerminalPanel.Active is { IsRunning: true }, "the second shell");
            TerminalSession second = window.TerminalPanel.Active!;
            int secondPid = second.ProcessId!.Value;
            ToggleButton[] sessions = Tabs(window.TerminalPanel);
            Assert.Equal(2, sessions.Length);
            Click(sessions[0]);
            Assert.Same(first, window.TerminalPanel.Active);
            Assert.True(sessions[0].IsChecked == true && sessions[1].IsChecked == false);
            Assert.True(window.TerminalPanel.View.IsFocused);

            window.KeyTextInput(OperatingSystem.IsWindows()
                ? "for /L %i in (0,1,119) do @echo reading-%i"
                : "i=0; while [ \"$i\" -lt 120 ]; do printf 'reading-%s\\n' \"$i\"; i=$((i+1)); done");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => first.Screen.Lines.Any(line => line.Text() == "reading-119") && first.Screen.CursorColumn > 0,
                "the shell's real history and next prompt");
            window.TerminalPanel.View.ScrollLines(20);
            long held = window.TerminalPanel.View.TopLine;
            Click(sessions[0]);
            Assert.True(sessions[0].IsChecked == true, "The selected tool tab switched itself off");
            Assert.Equal(held, window.TerminalPanel.View.TopLine);
            Assert.True(window.TerminalPanel.View.IsFocused);

            Button closeOther = Assert.Single(sessions[1].GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("tabClose"));
            Click(closeOther);
            Assert.Single(window.TerminalPanel.Tabs);
            Ui.WaitFor(() => !second.IsRunning && Processes.Gone(secondPid), "only the background shell to close", TimeSpan.FromSeconds(10));
            Assert.Same(first, window.TerminalPanel.Active);
            Assert.True(first.IsRunning && window.TerminalPanel.View.IsFocused,
                $"Closing a background tab left the active shell running={first.IsRunning}, with keyboard focus={window.TerminalPanel.View.IsFocused}");
            Assert.Equal(held, window.TerminalPanel.View.TopLine);
            Assert.DoesNotContain(window.TerminalPanel.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("tab"));
            Run("session-keeps-keys");

            Click(window.TerminalPanel.HeaderTools);
            Ui.WaitFor(() => window.TerminalPanel.Tabs.Count == 2 && window.TerminalPanel.Active is { IsRunning: true }, "a third shell after closing the second");
            TerminalSession third = window.TerminalPanel.Active!;
            int thirdPid = third.ProcessId!.Value;
            Assert.Equal(["Local", "Local (3)"], window.TerminalPanel.Tabs.Select(tab => tab.Name));
            sessions = Tabs(window.TerminalPanel);
            Button closeActive = Assert.Single(sessions[1].GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("tabClose"));
            Click(closeActive);
            Assert.Single(window.TerminalPanel.Tabs);
            Ui.WaitFor(() => !third.IsRunning && Processes.Gone(thirdPid), "the active shell to close", TimeSpan.FromSeconds(10));
            Assert.Same(first, window.TerminalPanel.Active);
            Assert.True(Assert.Single(Tabs(window.TerminalPanel)).IsChecked == true, "Closing a session reselected or toggled its removed tab");
            Assert.True(window.TerminalPanel.View.IsFocused);
            Run("session-after-close");

            HashSet<string> withTabs = [];
            foreach (ToolWindow tool in window.DockHost.Windows.ToArray())
            {
                window.ShowToolWindow(tool.Id);
                foreach (double width in new double[] { 1200, 683 })
                {
                    window.Width = width;
                    Ui.Settle();
                    ToggleButton[] visible = [.. window.GetVisualDescendants().OfType<ToggleButton>()
                        .Where(button => button.Classes.Contains("panelTab") && button.IsEffectivelyVisible)];
                    foreach (ToggleButton button in visible)
                    {
                        Check(button);
                    }
                    if (visible.Length > 0)
                    {
                        withTabs.Add(tool.Id);
                    }
                    UiAudit.Capture(window, $"tool-{tool.Id}-{width:0}-{(light ? "light" : "dark")}");
                }
            }
            Assert.Contains("terminal", withTabs);
            Assert.Contains("debug", withTabs);
            Assert.Contains("git", withTabs);
            Assert.Contains("nuget", withTabs);
            Assert.Contains("problems", withTabs);
            Assert.Contains("profiler", withTabs);

            window.ShowToolWindow("terminal");
            Ui.Settle();
            Button closeLast = Assert.Single(Tabs(window.TerminalPanel)).GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("tabClose"));
            Click(closeLast);
            Ui.WaitFor(() => window.TerminalPanel.Tabs.Count == 0 && !window.DockHost.IsShown("terminal"), "the last closed shell to hide its tool window");
        }
        finally
        {
            window.Close();
        }

        void Click(Control control)
        {
            Point at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseMove(at);
            Ui.Settle();
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Ui.Settle();
        }

        void Run(string text)
        {
            window.KeyTextInput($"echo {text}");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.TerminalPanel.Active!.Screen.Lines.Any(line => line.Text() == text), "the selected shell to receive its keys");
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Many_terminal_sessions_and_a_long_run_name_remain_reachable_in_a_narrow_pane(bool light)
    {
        string project = PathNames.Canonical(Debugging.Helpers.Debuggees.CopyTo("TerminalConsoleApp", folder));
        Restore.Run(folder);
        string title = "A terminal run configuration with a deliberately long descriptive name that must keep its close button reachable";
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/D", "/Q"]) : ("/bin/sh", []);
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        window.SetConfigurations([new RunConfiguration { Name = title, Project = project, Console = ConsoleKind.Terminal }], title);
        try
        {
            string? problem = Ui.Await(window.RunAsync(), "the real program in its long-named terminal tab");
            Assert.True(problem is null, problem);
            TerminalRun run = Assert.IsType<TerminalRun>(window.RunInTerminal);
            Ui.WaitFor(() => run.Session.Screen.Lines.Any(line => line.Text().Contains("key?", StringComparison.Ordinal)), "the actual terminal program to wait for keys");
            for (int index = 0; index < 7; index++)
            {
                int before = window.TerminalPanel.Tabs.Count;
                Click(window.TerminalPanel.HeaderTools);
                Ui.WaitFor(() => window.TerminalPanel.Tabs.Count == before + 1 && window.TerminalPanel.Active is { IsRunning: true }, "another real shell");
            }
            ScrollViewer strip = Assert.Single(window.TerminalPanel.GetVisualDescendants().OfType<ScrollViewer>(), control => control.Name == "TerminalSessionStrip");
            ToggleButton longTab = LongTab();
            Reach(longTab);
            Click(longTab);
            foreach (double width in new double[] { 1200, 683, 640, 1200 })
            {
                window.Width = width;
                Ui.Settle();
                CheckLongTab();
                UiAudit.Capture(window, $"terminal-many-{width:0}-{(light ? "light" : "dark")}");
            }
            ToggleButton[] browsingButtons = Tabs(window.TerminalPanel);
            ToggleButton browsing = browsingButtons[^1];
            window.MouseMove(new Point(10, 10));
            Assert.True(browsingButtons[0].Focus());
            Ui.Settle();
            for (int attempt = 0; attempt < browsingButtons.Length * 2 + 2 && window.FocusManager?.GetFocusedElement() != browsing; attempt++)
            {
                Ui.Press(window, "tab");
            }
            Assert.Same(browsing, window.FocusManager?.GetFocusedElement());
            foreach (double width in new double[] { 640, 1200 })
            {
                window.Width = width;
                Ui.Settle();
                Assert.Same(browsing, window.FocusManager?.GetFocusedElement());
                Assert.Same(run.Session, window.TerminalPanel.Active);
                UiAudit.Capture(window, $"terminal-tab-focus-{width:0}-{(light ? "light" : "dark")}");
                Assert.True(Inside(browsing), "Resizing must keep a keyboard-focused inactive session tab visible without changing the running session");
                Button focusedClose = Assert.Single(browsing.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("tabClose"));
                Ui.Press(window, "tab");
                Ui.Settle();
                Assert.Same(focusedClose, window.FocusManager?.GetFocusedElement());
                Assert.Same(run.Session, window.TerminalPanel.Active);
                Assert.False(browsing.IsChecked == true || browsing.IsPointerOver || focusedClose.IsPointerOver);
                Assert.Equal(1, focusedClose.Opacity);
                PathIcon glyph = Assert.Single(focusedClose.GetVisualDescendants().OfType<PathIcon>());
                Point center = glyph.TranslatePoint(new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2), window)!.Value;
                Assert.True(window.TerminalPanel.TryFindResource("Passenger.Terminal.Background", window.ActualThemeVariant, out object? ground));
                Assert.NotEqual(Assert.IsAssignableFrom<ISolidColorBrush>(ground).Color, Ui.Pixel(window, center));
                Border ring = Assert.Single(window.GetVisualDescendants().OfType<Border>(), border => border.Name == "TabCloseFocusRing" && border.IsEffectivelyVisible);
                Assert.Equal(focusedClose.CornerRadius, ring.CornerRadius);
                Assert.Equal(focusedClose.Bounds.Size, ring.Bounds.Size);
                Assert.Equal(new Point(), ring.TranslatePoint(default, focusedClose));
                Assert.False(ring.IsHitTestVisible);
                Control hit = Assert.IsAssignableFrom<Control>(window.InputHitTest(center));
                Assert.True(hit == focusedClose || hit.GetVisualAncestors().Contains(focusedClose), "The close action's focus ring must not intercept its pointer target");
                UiAudit.Capture(window, $"terminal-close-focus-{width:0}-{(light ? "light" : "dark")}");
                Ui.Press(window, "shift+tab");
                Ui.Settle();
                Assert.Same(browsing, window.FocusManager?.GetFocusedElement());
            }
            int closedPid = window.TerminalPanel.Tabs[^1].Session.ProcessId!.Value;
            int sessionsBeforeClose = window.TerminalPanel.Tabs.Count;
            Ui.Press(window, "tab");
            Ui.Press(window, "enter");
            Ui.WaitFor(() => window.TerminalPanel.Tabs.Count == sessionsBeforeClose - 1 && Processes.Gone(closedPid), "the keyboard close action to end only its background shell");
            Assert.Same(run.Session, window.TerminalPanel.Active);
            Assert.True(window.TerminalPanel.View.IsFocused);
            Reach(longTab);
            Click(longTab);
            DockLayout layout = window.DockHost.Capture();
            window.DockHost.Apply(layout with
            {
                LeftWidth = 200,
                Windows = layout.Windows.ToDictionary(pair => pair.Key, pair => pair.Key == "terminal"
                    ? pair.Value with { Anchor = ToolAnchor.LeftTop, Shown = true }
                    : pair.Value with { Shown = false }),
            });
            Ui.Settle();
            // ItemsControl regenerates its tab presenters when the persistent terminal panel is reparented.
            longTab = LongTab();
            UiAudit.Capture(window, $"terminal-many-side-200-{(light ? "light" : "dark")}");
            CheckLongTab();
            ToggleButton[] buttons = Tabs(window.TerminalPanel);
            for (int index = 0; index < buttons.Length; index++)
            {
                ToggleButton button = buttons[index];
                Reach(button);
                Click(button);
                Passenger.App.ToolWindows.TerminalTab tab = window.TerminalPanel.Tabs[index];
                Assert.Same(tab.Session, window.TerminalPanel.Active);
                Assert.True(window.TerminalPanel.View.IsFocused);
                if (tab.Run is null)
                {
                    string answer = $"shell-{index}";
                    window.KeyTextInput($"echo {answer}");
                    Ui.Press(window, "enter");
                    Ui.WaitFor(() => tab.Session.Screen.Lines.Any(line => line.Text() == answer), "only the selected shell to receive its command");
                }
            }
            for (int index = 0; index < buttons.Length; index++)
            {
                Assert.True(buttons[0].Focus());
                Ui.Settle();
                // Each session's close button is also a keyboard stop.
                for (int attempt = 0; attempt < buttons.Length * 2 + 2 && window.FocusManager?.GetFocusedElement() != buttons[index]; attempt++)
                {
                    Ui.Press(window, "tab");
                }
                Assert.Same(buttons[index], window.FocusManager?.GetFocusedElement());
                Assert.True(Inside(buttons[index]), "The keyboard-focused session tab must scroll into view");
                Ui.Press(window, "space");
                Ui.Settle();
                Assert.Same(window.TerminalPanel.Tabs[index].Session, window.TerminalPanel.Active);
                Assert.True(window.TerminalPanel.View.IsFocused);
            }
            TerminalSession selected = window.TerminalPanel.Active!;
            Reach(longTab);
            Button close = Assert.Single(longTab.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("tabClose"));
            Assert.True(Inside(close));
            Click(close);
            Ui.WaitFor(() => run.Ended.IsCompleted && Processes.Gone(run.ProcessId), "closing the long background run's real process");
            Assert.Same(selected, window.TerminalPanel.Active);
            Assert.True(window.TerminalPanel.View.IsFocused);

            void CheckLongTab()
            {
                Assert.Same(run.Session, window.TerminalPanel.Active);
                Assert.True(Inside(longTab), $"Resizing must reveal the complete selected run tab: at {longTab.TranslatePoint(default, strip)}, tab {longTab.Bounds}, strip {strip.Bounds}, viewport {strip.Viewport}, offset {strip.Offset}");
                Assert.Equal(title, ToolTip.GetTip(longTab));
                TextBlock name = Assert.Single(longTab.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == title);
                Assert.True(name.TextLayout.Width <= name.Bounds.Width + 1, "The long run name must ellipsize beside its close button");
                Assert.True(Inside(Assert.Single(longTab.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("tabClose"))));
            }

            ToggleButton LongTab() => Assert.Single(Tabs(window.TerminalPanel), button => button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == title));

            bool Inside(Control control) => control.TranslatePoint(default, strip) is { } at && at.X >= -1 && at.X + control.Bounds.Width <= strip.Bounds.Width + 1;

            void Reach(Control control)
            {
                for (int attempt = 0; attempt < 160 && !Inside(control); attempt++)
                {
                    Point at = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
                    window.MouseWheel(at, new Vector(control.TranslatePoint(default, strip)!.Value.X < 0 ? 0.5 : -0.5, 0));
                    Ui.Settle();
                }
                Assert.True(Inside(control), "Every terminal session must be reachable by scrolling");
            }
        }
        finally
        {
            Task close = window.TerminalPanel.CloseAllAsync();
            Ui.WaitFor(() => close.IsCompleted, "all real terminal sessions to shut down", TimeSpan.FromSeconds(30));
            close.GetAwaiter().GetResult();
            window.Close();
        }

        void Click(Control control)
        {
            Point at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseMove(at);
            Ui.Settle();
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Ui.Settle();
        }
    }

    private static ToggleButton[] Tabs(Control surface) => [.. surface.GetVisualDescendants().OfType<ToggleButton>().Where(button => button.Classes.Contains("panelTab"))];

    public static void Check(ToggleButton button)
    {
        Assert.Equal(24, button.Bounds.Height);
        Assert.Equal(13, button.FontSize);
        Assert.Equal(new CornerRadius(6), button.CornerRadius);
        Assert.Equal(default, button.BorderThickness);
        ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(control => control.Name == "PART_ContentPresenter");
        if (button.IsChecked == true)
        {
            Assert.True(button.TryFindResource("Passenger.Selected", button.ActualThemeVariant, out object? selected));
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(selected).Color, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
        }
    }
}
