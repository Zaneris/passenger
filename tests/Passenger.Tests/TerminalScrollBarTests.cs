using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Terminal;

namespace Passenger.Tests;

/// <summary>A real shell's retained output can be browsed with the shared overlay without losing selection, focus or
/// the place being read as output arrives. Alternate-screen programs keep their own wheel/keyboard behavior.</summary>
[Collection(Alone.Name)]
public sealed class TerminalScrollBarTests : IDisposable
{
    private readonly string folder = TestFolders.Create("terminal-scrollbar-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_shells_overlay_browses_scrollback_and_follows_typing_without_covering_cells_or_stealing_the_keys(bool light)
    {
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.TerminalPanel.Shell = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ["/D", "/Q"])
            : ("/bin/sh", []);
        window.Show();
        window.OpenFolder(folder);
        window.ShowToolWindow("terminal");
        Ui.Settle();
        try
        {
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the test shell to start");
            TerminalSession shell = window.TerminalPanel.Active!;
            TerminalView view = window.TerminalPanel.View;
            ScrollBar bar = Assert.Single(view.GetVisualChildren().OfType<ScrollBar>());
            Assert.False(bar.IsVisible);
            int columns = shell.Screen.Columns;
            view.Focus();
            window.KeyTextInput(OperatingSystem.IsWindows()
                ? "for /L %i in (1,1,240) do @echo terminal-%i"
                : "i=1; while [ $i -le 240 ]; do echo terminal-$i; i=$((i+1)); done");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => shell.Screen.Scrollback.Count > 200 && Enumerable.Range(0, shell.Screen.Rows)
                .Any(row => shell.Screen.Lines[row].Text() == "terminal-240"), "the shell's 240 lines to reach scrollback");
            Ui.Settle();
            Assert.True(bar.IsVisible);
            ScrollBarTests.Check(bar);
            Assert.Equal(columns, shell.Screen.Columns);
            Assert.Equal(shell.Screen.ScreenTop, view.TopLine);
            Assert.Equal(shell.Screen.Rows, bar.ViewportSize);
            Assert.Equal(view.Bounds.Width, bar.Bounds.Right, 1);

            view.Select(shell.Screen.ScreenTop + 1, 0, shell.Screen.ScreenTop + 2, 8);
            string selected = view.SelectedText;
            Assert.NotEmpty(selected);
            Thumb thumb = Assert.Single(bar.GetVisualDescendants().OfType<Thumb>());
            Point at = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseMove(at - new Vector(0, 70));
            window.MouseUp(at - new Vector(0, 70), MouseButton.Left);
            Ui.Settle();
            Assert.True(view.ScrolledBack > 0);
            Assert.Equal(selected, view.SelectedText);
            Assert.True(view.IsFocused, "The overlay stole the shell's keys");
            long held = view.TopLine;
            shell.Say("\r\noutput while reading history\r\n");
            Ui.Settle();
            Assert.Equal(held, view.TopLine);
            Assert.Equal(selected, view.SelectedText);
            Assert.Equal(held - shell.Screen.FirstLine, bar.Value);
            Point overBar = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, 25), window)!.Value;
            window.MouseWheel(overBar, new Vector(0, 1));
            Ui.Settle();
            Assert.Equal(held - 3, view.TopLine);

            string theme = light ? "light" : "dark";
            foreach (double width in new double[] { 1200, 683, 640, 1200 })
            {
                window.Width = width;
                Ui.Settle();
                Assert.True(bar.IsVisible);
                ScrollBarTests.Check(bar);
                Assert.Equal(view.Bounds.Width, bar.Bounds.Right, 1);
                Assert.True(view.IsFocused);
                UiAudit.Capture(window, $"terminal-scrollbar-{width:0}-{theme}");
            }

            view.ScrollLines(20);
            Assert.True(view.ScrolledBack > 0);
            window.KeyTextInput("echo terminal-after-scroll");
            Ui.Settle();
            Assert.Equal(0, view.ScrolledBack);
            Assert.Equal(bar.Maximum, bar.Value);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => Enumerable.Range(0, shell.Screen.Rows).Any(row => shell.Screen.Lines[row].Text() == "terminal-after-scroll"),
                "the keys after browsing history to reach the shell");

            // A held place that ages out clamps to the oldest retained line. The bar describes the bounded ring,
            // rather than accumulating an ever larger absolute range over the life of the shell.
            view.ScrollLines(int.MaxValue);
            shell.Say(string.Concat(Enumerable.Range(0, TerminalScreen.ScrollbackLimit + 20).Select(i => $"retained-{i}\r\n")));
            Ui.Settle();
            Assert.Equal(TerminalScreen.ScrollbackLimit, shell.Screen.Scrollback.Count);
            Assert.True(shell.Screen.FirstLine > 0);
            Assert.Equal(shell.Screen.FirstLine, view.TopLine);
            Assert.Equal(0, bar.Value);
            Assert.Equal(TerminalScreen.ScrollbackLimit, bar.Maximum);
            long historyPlace = view.TopLine;

            // Exercise the same VT alternate-screen transitions a full-screen terminal program sends.
            shell.Say("\x1b[?1049h\x1b[2J\x1b[HFull-screen program");
            Ui.Settle();
            Assert.True(shell.Screen.IsAlternate);
            Assert.False(bar.IsVisible);
            Assert.Equal(shell.Screen.ScreenTop, view.TopLine);
            Assert.Equal("Full-screen program", shell.Screen.LineAt(view.TopLine).Text());
            UiAudit.Capture(window, $"terminal-scrollbar-alternate-{theme}");
            Point inProgram = view.TranslatePoint(new Point(70, 60), window)!.Value;
            window.MouseWheel(inProgram, new Vector(0, 1));
            Ui.Press(window, "shift PAGE_UP");
            Ui.Settle();
            Assert.Equal(shell.Screen.ScreenTop, view.TopLine);
            Assert.False(bar.IsVisible);
            shell.Say("\x1b[?1049l");
            Ui.Settle();
            Assert.False(shell.Screen.IsAlternate);
            Assert.True(bar.IsVisible);
            Assert.Equal(Math.Max(historyPlace, shell.Screen.FirstLine), view.TopLine);

            // Typing belongs to the active screen, and keeps the existing behavior of following new output again.
            shell.Say("\x1b[?1049h\x1b[2J\x1b[HFull-screen program");
            Ui.Press(window, "control C");
            window.KeyTextInput("echo terminal-alt-input");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => Enumerable.Range(0, shell.Screen.Rows).Any(row => shell.Screen.Lines[row].Text() == "terminal-alt-input"),
                "typing on the alternate screen to reach the shell");
            Assert.Equal(shell.Screen.ScreenTop, view.TopLine);
            Assert.False(bar.IsVisible);
            shell.Say("\x1b[?1049l");
            Ui.Settle();
            Assert.Equal(0, view.ScrolledBack);
            view.ClearBuffer();
            Ui.Settle();
            Assert.False(bar.IsVisible);
            Assert.Equal(0, bar.Maximum);
            UiAudit.Capture(window, $"terminal-scrollbar-cleared-{theme}");
        }
        finally
        {
            window.Close();
        }
    }
}
