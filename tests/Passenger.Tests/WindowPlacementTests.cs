using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Passenger.App;
using Passenger.App.State;

namespace Passenger.Tests;

/// <summary>The person's window opens where their last one closed: at its bounds while normal, maximized as it was, never
/// minimized, wholly on a screen however the screens changed; a second window opens beside the first, and a window nobody
/// sees neither opens at the place kept nor keeps its own. Each start's window keeps its state in the test's own folder,
/// made as the app makes the person's window before it shows. The headless platform maximizes and minimizes no window, so
/// a test moves and sizes one as a platform does.</summary>
public sealed class WindowPlacementTests : IDisposable
{
    private readonly string state = TestFolders.Create("placement-state-");

    public void Dispose() => TestFolders.Delete(state);

    [AvaloniaFact]
    public void A_window_closed_maximized_after_a_move_and_a_resize_opens_maximized_over_those_bounds()
    {
        MainWindow first = Start();
        PixelRect area = Area(first);
        PixelPoint moved = area.TopLeft + new PixelPoint(60, 40);
        first.Position = moved;
        first.Width = 1000;
        first.Height = 640;
        Ui.Settle();
        // As Windows maximizes: the frame moved over the whole working area, its new state told after.
        first.Position = area.TopLeft;
        first.Width = area.Width;
        first.Height = area.Height;
        first.WindowState = WindowState.Maximized;
        Ui.Settle();
        first.Close();
        Ui.Settle();

        MainWindow next = Start();
        try
        {
            Assert.True(next.WindowState == WindowState.Maximized && next.Position == moved && next.ClientSize == new Size(1000, 640),
                $"The window closed maximized from {moved} at 1000×640 opened {next.WindowState} at {next.Position} at {next.ClientSize}");
        }
        finally
        {
            next.Close();
        }
    }

    [AvaloniaFact]
    public void A_window_closed_minimized_opens_normal_at_the_bounds_it_had_before()
    {
        MainWindow first = Start();
        PixelRect area = Area(first);
        PixelPoint moved = area.TopLeft + new PixelPoint(120, 80);
        first.Position = moved;
        first.Width = 900;
        first.Height = 600;
        Ui.Settle();
        first.WindowState = WindowState.Minimized;
        // As Windows reports a minimized window, far off every screen.
        first.Position = new PixelPoint(-32000, -32000);
        Ui.Settle();
        first.Close();
        Ui.Settle();

        MainWindow next = Start();
        try
        {
            Assert.True(next.WindowState == WindowState.Normal && next.Position == moved && next.ClientSize == new Size(900, 600),
                $"The window closed minimized from {moved} at 900×600 opened {next.WindowState} at {next.Position} at {next.ClientSize}");
        }
        finally
        {
            next.Close();
        }
    }

    [AvaloniaFact]
    public void A_place_off_every_screen_and_larger_than_any_opens_wholly_on_a_screen()
    {
        File.WriteAllText(Path.Combine(state, "windowPlacement.json"), """
            { "version": 1, "x": -40000, "y": 50000, "width": 90000, "height": 700, "state": "Minimized" }
            """);

        MainWindow window = Start();
        try
        {
            PixelRect frame = new(window.Position, PixelSize.FromSize(window.ClientSize, window.DesktopScaling));
            Screen[] screens = [.. window.Screens.All];
            Assert.True(screens.Any(s => s.WorkingArea.Contains(frame)) && window.WindowState == WindowState.Normal,
                $"The window kept off every screen opened {window.WindowState} at {frame}, off the screens' working areas [{string.Join(", ", screens.Select(s => s.WorkingArea))}]");
            Assert.Equal(700, window.ClientSize.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_second_window_opens_beside_the_first_and_the_last_closed_is_where_the_next_start_opens()
    {
        MainWindow first = Start();
        PixelPoint placed = Area(first).TopLeft + new PixelPoint(40, 30);
        first.Position = placed;
        Ui.Settle();
        MainWindow second = PassengerApp.NewWindow(first);
        Ui.Settle();
        Assert.True(second.Position != first.Position, $"The second window opened over the first, at {second.Position}");
        PixelPoint secondAt = second.Position;
        first.Close();
        Ui.Settle();
        second.Close();
        Ui.Settle();

        MainWindow next = Start();
        try
        {
            Assert.True(next.Position == secondAt, $"The next start opened at {next.Position}, not where the window closed last stood, {secondAt}");
        }
        finally
        {
            next.Close();
        }
    }

    [AvaloniaFact]
    public void A_window_nobody_sees_neither_opens_at_the_place_kept_nor_keeps_its_own()
    {
        string kept = Path.Combine(state, "windowPlacement.json");
        string written = """{ "version": 1, "x": 30, "y": 20, "width": 800, "height": 500, "state": "Maximized" }""";
        File.WriteAllText(kept, written);

        MainWindow window = Start(unseen: true);
        try
        {
            Assert.True(window.WindowState == WindowState.Normal && window.Position != new PixelPoint(30, 20),
                $"A window nobody sees opened at the place kept, {window.WindowState} at {window.Position}");
            window.Position += new PixelPoint(10, 10);
            Ui.Settle();
        }
        finally
        {
            window.Close();
            Ui.Settle();
        }
        Assert.Equal(written, File.ReadAllText(kept));
    }

    /// <summary>The window a person starts, made as the app makes it: keeping its layout, then placed before it shows.</summary>
    private MainWindow Start(bool unseen = false)
    {
        MainWindow window = new() { StateFolder = state, StartsLanguageServer = false, Unseen = unseen };
        window.KeepLayout();
        window.OpenWherePlaced();
        window.Show();
        Ui.Settle();
        return window;
    }

    private static PixelRect Area(Window window) => (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary
        ?? throw new Xunit.Sdk.XunitException("The headless platform has no screen")).WorkingArea;
}
