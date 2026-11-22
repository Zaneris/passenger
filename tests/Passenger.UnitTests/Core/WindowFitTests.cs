using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Every window the IDE opens fits a small screen's working area: a 1920×1080 laptop at 150% with its taskbar,
/// 1280×688 units, its dialogs no larger than the area down to their least and wholly on it, their title bars and buttons
/// in reach wherever their owner stands.</summary>
public class WindowFitTests
{
    /// <summary>The laptop's working area in its pixels, at its scaling.</summary>
    private static readonly WindowFit.Box Laptop = new(0, 0, 1920, 1032);

    private const double Scaling = 1.5;

    /// <summary>A system's title bar and borders round a dialog's client area.</summary>
    private static readonly (double Width, double Height) Frame = (16, 40);

    /// <summary>Large windows as they ask to open, their size and their least: the widest, the one whose least is tallest and
    /// the smallest.</summary>
    private static readonly (string Name, double Width, double Height, double LeastWidth, double LeastHeight)[] Windows =
    [
        ("Merge", 1300, 800, 800, 400),
        ("New Project", 920, 640, 720, 480),
        ("Attach to Process", 900, 520, 560, 300),
    ];

    [Fact]
    public void Every_window_opens_wholly_on_a_small_screen_over_its_owner_and_a_kept_place_moves_only_back_onto_it() => Cases.All(
        ("a window larger than a small screen opens wholly on it over an owner anywhere on it", () => Cases.Each(Windows,
            row => A_window_larger_than_a_small_screen_opens_wholly_on_it_over_an_owner_anywhere_on_it(row.Name, row.Width, row.Height, row.LeastWidth, row.LeastHeight), row => row.Name)),
        ("a window that fits keeps its size and opens centred over its owner", A_window_that_fits_keeps_its_size_and_opens_centred_over_its_owner),
        ("a window whose least is taller than the screen keeps its least with its title bar at the area's top",
            A_window_whose_least_is_taller_than_the_screen_keeps_its_least_with_its_title_bar_at_the_areas_top),
        ("a window kept where it was placed moves only back onto the area and one on it stays",
            A_window_kept_where_it_was_placed_moves_only_back_onto_the_area_and_one_on_it_stays));

    private static void A_window_larger_than_a_small_screen_opens_wholly_on_it_over_an_owner_anywhere_on_it(string name, double width, double height, double leastWidth, double leastHeight)
    {
        // The owner maximized, then a small window of it in each corner and half off the area's edge.
        WindowFit.Box[] owners = [Laptop, new(0, 0, 960, 600), new(960, 432, 960, 600), new(1500, 900, 960, 600), new(-400, -200, 960, 600)];
        foreach (WindowFit.Box owner in owners)
        {
            WindowFit.Fitted fit = WindowFit.Within(Laptop, Scaling, (width, height), (leastWidth, leastHeight), Frame, owner);
            WindowFit.Box frame = fit.Frame(Scaling, Frame.Width, Frame.Height);
            Assert.True(frame.X >= Laptop.X && frame.Y >= Laptop.Y && frame.Right <= Laptop.Right && frame.Bottom <= Laptop.Bottom,
                $"{name} over an owner at {owner} opens at {frame}, past the working area {Laptop}: its title bar or its buttons out of reach");
            Assert.True(fit.Width >= leastWidth && fit.Height >= leastHeight,
                $"{name} opens {fit.Width}×{fit.Height}, under its least {leastWidth}×{leastHeight}");
            Assert.True(fit.Width <= width && fit.Height <= height, $"{name} opens {fit.Width}×{fit.Height}, larger than it asked, {width}×{height}");
        }
    }

    private static void A_window_that_fits_keeps_its_size_and_opens_centred_over_its_owner()
    {
        WindowFit.Box owner = new(300, 200, 1200, 700);
        WindowFit.Fitted fit = WindowFit.Within(Laptop, Scaling, (520, 360), (420, 280), Frame, owner);
        WindowFit.Box frame = fit.Frame(Scaling, Frame.Width, Frame.Height);
        Assert.Equal((520.0, 360.0), (fit.Width, fit.Height));
        Assert.True(Math.Abs(frame.X + (frame.Width / 2) - (owner.X + (owner.Width / 2))) <= 1 && Math.Abs(frame.Y + (frame.Height / 2) - (owner.Y + (owner.Height / 2))) <= 1,
            $"A dialog that fits opened at {frame}, not centred over its owner at {owner}");
    }

    private static void A_window_whose_least_is_taller_than_the_screen_keeps_its_least_with_its_title_bar_at_the_areas_top()
    {
        WindowFit.Box area = new(0, 0, 1024, 600);
        WindowFit.Fitted fit = WindowFit.Within(area, 1, (1300, 800), (800, 700), Frame, area);
        Assert.Equal((1008.0, 700.0), (fit.Width, fit.Height));
        Assert.True(fit.Y == area.Y, $"A window taller than the screen opened at y {fit.Y}, its title bar off the area's top at {area.Y}");
    }

    private static void A_window_kept_where_it_was_placed_moves_only_back_onto_the_area_and_one_on_it_stays()
    {
        // A second screen left of the first, its pixels at negative places.
        WindowFit.Box left = new(-1920, 0, 1920, 1032);
        WindowFit.Fitted past = WindowFit.Within(left, Scaling, (800, 600), (320, 200), (0, 0), left, (-300, 900));
        WindowFit.Box frame = past.Frame(Scaling, 0, 0);
        Assert.True(frame.Right <= left.Right && frame.Bottom <= left.Bottom && frame.X >= left.X,
            $"A window placed past its screen's edge stays at {frame}, off the area {left}");
        WindowFit.Fitted on = WindowFit.Within(left, Scaling, (800, 600), (320, 200), (0, 0), left, (-1800, 100));
        Assert.Equal((-1800, 100), (on.X, on.Y));
    }
}
