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
