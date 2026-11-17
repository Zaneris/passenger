using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Terminal;

namespace Passenger.Tests;

/// <summary>What the system asks of motion and carets, stood in for (<see cref="SystemMotion.StandIn(MotionAsked)"/>), is
/// what every window does: a caret the system keeps steady stays drawn in the editor and the terminal, and a field's never
/// blinks; one blinking at a rate blinks at it; with less motion a touch's fling stops where the finger leaves it, the
/// working ring stands still and the editor lands a fling and a page in the frame they come in; and a window coming in
/// front reads the system again, as it reads the system's own on each system. Settings' caret width draws the editor's and
/// terminal's carets that broad, and its wait for a shortcut's second key holds the first that long. Blinks and waits are
/// counted over time, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class SystemMotionTests
{
    private static readonly TimeSpan ThreeBlinks = TimeSpan.FromMilliseconds(3 * 530 + 100);

    private static MotionAsked Blinking(int shownMs, int hiddenMs) => new(false, new CaretBlink(TimeSpan.FromMilliseconds(shownMs), TimeSpan.FromMilliseconds(hiddenMs)));

    private static readonly MotionAsked Steady = new(false, CaretBlink.Steady);

    private static readonly MotionAsked Still = new(true, CaretBlink.Default);

    [AvaloniaFact]
    public void With_the_system_keeping_carets_steady_the_editors_caret_stays_drawn_across_what_would_be_its_blinks()
    {
        EditorRig rig = new("class One { }\n");
        long blinks = rig.Editor.Blinks;
        Ui.Hold(ThreeBlinks, "three of Passenger's own blinks");
        Assert.True(rig.Editor.Blinks > blinks, "The editor's caret never blinked at Passenger's own rate, so a steady one proves nothing");

        using (SystemMotion.StandIn(Steady))
        {
            Ui.Settle();
            blinks = rig.Editor.Blinks;
            Ui.Hold(ThreeBlinks, "what would be three blinks");
            Ui.Draw();
            Assert.True(rig.Editor.Blinks == blinks, $"With the system keeping carets steady the editor's caret blinked {rig.Editor.Blinks - blinks} times");
            Assert.True(CaretDrawn(rig.Window, rig.Editor), "With the system keeping carets steady the editor's caret was not drawn");
        }
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void The_editors_caret_blinks_at_the_rate_the_system_names()
    {
        EditorRig rig = new("class One { }\n");
        using (SystemMotion.StandIn(Blinking(5000, 5000)))
        {
            Ui.Settle();
            long blinks = rig.Editor.Blinks;
            Ui.Hold(ThreeBlinks, "three of Passenger's own blinks, inside one of the system's");
            Assert.True(rig.Editor.Blinks == blinks, $"With the system's caret shown five seconds at a time the editor's blinked {rig.Editor.Blinks - blinks} times in {ThreeBlinks.TotalSeconds} s");
        }
        using (SystemMotion.StandIn(Blinking(100, 100)))
        {
            Ui.Settle();
            long blinks = rig.Editor.Blinks;
            Ui.Hold(TimeSpan.FromMilliseconds(1200), "twelve of the system's blinks, two of Passenger's own");
            Assert.True(rig.Editor.Blinks - blinks >= 4, $"With the system's caret blinking every tenth of a second the editor's blinked {rig.Editor.Blinks - blinks} times in 1.2 s");
        }
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void With_the_system_keeping_carets_steady_the_terminals_cursor_stays_drawn_though_its_setting_blinks_it()
    {
        string folder = TestFolders.Create("motion-");
        try
        {
            (MainWindow window, TerminalSession shell) = TerminalTests.Open(folder, OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
            window.ApplySettings(window.Settings with { TerminalCursorBlinking = true, TerminalCursorStyle = CursorShape.Block });
            Ui.Settle();
            TerminalView view = window.TerminalPanel.View;
            long blinks = view.Blinks;
            Ui.Hold(ThreeBlinks, "three of Passenger's own blinks");
            Assert.True(view.Blinks > blinks, "The terminal's cursor never blinked with its setting on, so a steady one proves nothing");

            using (SystemMotion.StandIn(Steady))
            {
                Ui.Settle();
                blinks = view.Blinks;
                Ui.Hold(ThreeBlinks, "what would be three blinks");
                Ui.Draw();
                Assert.True(view.Blinks == blinks, $"With the system keeping carets steady the terminal's cursor blinked {view.Blinks - blinks} times");
                Assert.True(Ui.Pixel(window, CursorCell(window, view, shell)) != TerminalBackground(window, view),
                    "With the system keeping carets steady the terminal's cursor was not drawn");
            }
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_fields_caret_blinks_as_the_system_asks_and_not_at_all_where_it_keeps_carets_steady()
    {
        TextBox field = new() { Width = 200 };
        Window window = new() { Width = 300, Height = 100, Content = field };
        window.Show();
        field.Focus();
        Ui.Settle();
        using (SystemMotion.StandIn(Steady))
        {
            Ui.Settle();
            Assert.True(field.CaretBlinkInterval == TimeSpan.Zero, $"With the system keeping carets steady a field's caret blinks every {field.CaretBlinkInterval.TotalMilliseconds} ms");
        }
        using (SystemMotion.StandIn(Blinking(300, 300)))
        {
            Ui.Settle();
            Assert.True(field.CaretBlinkInterval == TimeSpan.FromMilliseconds(300), $"With the system's caret blinking every 300 ms a field's blinks every {field.CaretBlinkInterval.TotalMilliseconds} ms");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void With_less_motion_a_touchs_fling_stops_where_the_finger_lets_go()
    {
        double Fling()
        {
            StackPanel rows = new();
            for (int i = 0; i < 400; i++)
            {
                rows.Children.Add(new TextBlock { Text = $"Row {i}", Height = 24 });
            }
            ScrollViewer scroller = new() { Content = rows };
            Window window = new() { Width = 400, Height = 600, Content = scroller };
            window.Show();
            Ui.Settle();
            Ui.Fling(window, new Point(200, 500), new Point(200, 140), moves: 6);
            double let = scroller.Offset.Y;
            for (int frame = 0; frame < 30; frame++)
            {
                Ui.Hold(TimeSpan.FromMilliseconds(16), "a frame of a coast");
            }
            double coast = scroller.Offset.Y - let;
            Assert.True(let > 0, "The finger's drag never scrolled the list");
            window.Close();
            return coast;
        }

        double full = Fling();
        Assert.True(full > 0, "With full motion a fling never coasted, so one that stops proves nothing");
        using (SystemMotion.StandIn(Still))
        {
            double still = Fling();
            Assert.True(still == 0, $"With less motion asked a fling coasted {still:0} px after the finger let go");
        }
    }

    [AvaloniaFact]
    public void With_less_motion_the_working_ring_stands_still()
    {
        WorkingRing ring = new() { Width = 16, Height = 16 };
        Window window = new() { Width = 100, Height = 100, Content = ring };
        window.Show();
        Ui.Settle();
        Assert.True(ring.Turning, "The working ring never turned with full motion");
        using (SystemMotion.StandIn(Still))
        {
            Ui.Settle();
            Assert.False(ring.Turning, "With less motion asked the working ring still turned");
        }
        Ui.Settle();
        Assert.True(ring.Turning, "Once full motion was asked again the working ring stood still");
        window.Close();
    }

    [AvaloniaFact]
    public void With_less_motion_the_editor_lands_a_fling_and_a_page_in_the_frame_they_come_in()
    {
        using IDisposable still = SystemMotion.StandIn(Still);
        EditorRig rig = new(string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"int line{i} = {i};")));
        CodeEditor editor = rig.Editor;
        Ui.Press(rig.Window, "PAGEDOWN");
        Ui.Draw();
        Vector paged = editor.ScrollOffset;
        Ui.Hold(TimeSpan.FromMilliseconds(300), "frames after the page");
        Ui.Draw();
        Assert.True(paged.Y > 0 && editor.ScrollOffset == paged, $"A page down stood at {paged.Y:0} in its frame and at {editor.ScrollOffset.Y:0} after");

        Point over = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), rig.Window)!.Value;
        for (int notch = 0; notch < 8; notch++)
        {
            rig.Window.MouseWheel(over, new Vector(0, -3));
        }
        Ui.Draw();
        Vector flung = editor.ScrollOffset;
        Ui.Hold(TimeSpan.FromMilliseconds(300), "frames after the fling");
        Ui.Draw();
        Assert.True(flung.Y > paged.Y && editor.ScrollOffset == flung, $"A fling from {paged.Y:0} stood at {flung.Y:0} in its frame and at {editor.ScrollOffset.Y:0} after");
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void A_window_coming_in_front_reads_the_system_again()
    {
        MotionAsked system = MotionAsked.Default;
        using IDisposable asked = SystemMotion.StandIn(() => system);
        string folder = TestFolders.Create("motion-");
        try
        {
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            Assert.False(SystemMotion.InForce.ReducesMotion);
            system = new MotionAsked(true, CaretBlink.Steady);
            Ui.Settle();
            Assert.False(SystemMotion.InForce.ReducesMotion, "The system was read again with no window coming in front");
            window.Activate();
            Ui.Settle();
            Assert.True(SystemMotion.InForce == system, $"A window come in front left {SystemMotion.InForce} in force where the system asked {system}");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void Settings_caret_width_draws_the_editors_caret_and_the_terminals_line_cursor_that_broad()
    {
        string folder = TestFolders.Create("motion-");
        try
        {
            using IDisposable steady = SystemMotion.StandIn(Steady);
            File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
            (MainWindow window, TerminalSession shell) = TerminalTests.Open(folder, OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
            window.ApplySettings(window.Settings with { TerminalCursorStyle = CursorShape.Bar });
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.EditorPage;
            Ui.Settle();
            SettingsTests.Editor<NumericUpDown>(dialog, "Caret width").Value = 6;
            dialog.Ok();
            Ui.Settle();
            Assert.Equal(6, window.Settings.CaretWidth);

            TerminalView view = window.TerminalPanel.View;
            Point cell = CursorCell(window, view, shell);
            Color cursor = ((ISolidColorBrush)view.FindResource(window.ActualThemeVariant, TokenKeys.Terminal_Cursor)!).Color;
            int line = Broad(x => Ui.Pixel(window, new Point(x, cell.Y)) == cursor, cell.X - (view.PointOf(0, 1).X - view.PointOf(0, 0).X) / 2 - 1);
            Assert.True(line == 6, $"The terminal's line cursor was drawn {line} broad where Settings asks 6 (its keys {(view.IsFocused ? "in it" : "elsewhere")}, its width {view.CursorWidth})");

            window.OpenFile(Path.Combine(folder, "One.cs"));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            // At the line's end, so no glyph stands beside the caret.
            Ui.Press(window, "END");
            Point caret = editor.TranslatePoint(editor.PointOf(editor.Selections.Primary.Caret) + new Point(0, editor.LineHeight / 2), window)!.Value;
            Color colour = ((ISolidColorBrush)editor.FindResource(window.ActualThemeVariant, TokenKeys.Editor_Caret)!).Color;
            int drawn = Broad(x => Ui.Pixel(window, new Point(x, caret.Y)) == colour, caret.X - 2);
            Assert.True(drawn == 6, $"The editor's caret was drawn {drawn} broad where Settings asks 6");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_longer_wait_for_a_shortcuts_second_key_holds_its_first_past_two_seconds()
    {
        string folder = TestFolders.Create("motion-");
        try
        {
            File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            window.OpenFile(Path.Combine(folder, "One.cs"));
            window.ApplySettings(window.Settings with { ChordWaitSeconds = 8 });
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            string first = Ui.Shortcut(EditorActions.Comment);
            Ui.Press(window, first);
            Ui.Hold(TimeSpan.FromSeconds(3), "past the two seconds a first key waits by default");
            Assert.StartsWith("Prefix key pressed: ", window.Status);
            Ui.Type(window, "C", "c");
            Ui.Settle();
            Assert.StartsWith("//", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void The_systems_own_answer_is_read_whatever_stands_in_for_it()
    {
        MotionAsked read = Ui.Await(SystemMotion.ReadSystemAsync(), "the system's motion and caret settings");
        TestContext.Current.TestOutputHelper?.WriteLine($"{Environment.OSVersion}: the system asks for {read}");
        Assert.True(!read.Blink.Blinks || read.Blink.Shown >= TimeSpan.FromMilliseconds(50), $"The system's caret was read as blinking every {read.Blink.Shown.TotalMilliseconds} ms");
    }

    /// <summary>Whether the editor's caret is drawn where it stands, in the caret's colour.</summary>
    private static bool CaretDrawn(TopLevel window, CodeEditor editor)
    {
        Point at = editor.TranslatePoint(editor.PointOf(editor.Selections.Primary.Caret) + new Point(1, editor.LineHeight / 2), window)!.Value;
        Color caret = ((ISolidColorBrush)editor.FindResource(window.ActualThemeVariant, TokenKeys.Editor_Caret)!).Color;
        return Ui.Pixel(window, at) == caret;
    }

    /// <summary>The middle of the cell the terminal's cursor stands in, in the window.</summary>
    private static Point CursorCell(TopLevel window, TerminalView view, TerminalSession shell) =>
        view.TranslatePoint(view.PointOf(shell.Screen.ScreenTop + shell.Screen.CursorRow, shell.Screen.CursorColumn), window)!.Value;

    private static Color TerminalBackground(TopLevel window, TerminalView view) =>
        ((ISolidColorBrush)view.FindResource(window.ActualThemeVariant, TokenKeys.Terminal_Background)!).Color;

    /// <summary>How many whole units in a row from <paramref name="from"/> on hold what <paramref name="drawn"/> asks, after
    /// any that do not.</summary>
    private static int Broad(Func<double, bool> drawn, double from)
    {
        double x = Math.Floor(from) + 0.5;
        for (int i = 0; i < 40 && !drawn(x); i++)
        {
            x++;
        }
        int broad = 0;
        while (broad < 40 && drawn(x + broad))
        {
            broad++;
        }
        return broad;
    }
}
