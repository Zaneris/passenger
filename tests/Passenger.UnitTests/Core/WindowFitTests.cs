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

    /// <summary>Each large window as it asks to open: its size and its least.</summary>
    public static TheoryData<string, double, double, double, double> Windows() => new()
    {
        { "Settings", 980, 700, 640, 400 },
        { "Diff", 1200, 760, 600, 300 },
        { "Merge", 1300, 800, 800, 400 },
        { "Run Configurations", 980, 660, 700, 440 },
        { "New Project", 920, 640, 720, 480 },
        { "Interactive Test", 1040, 640, 640, 420 },
        { "Attach to Process", 900, 520, 560, 300 },
    };

    [Theory]
    [MemberData(nameof(Windows))]
    public void A_window_larger_than_a_small_screen_opens_wholly_on_it_over_an_owner_anywhere_on_it(string name, double width, double height, double leastWidth, double leastHeight)
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

    [Fact]
    public void A_window_that_fits_keeps_its_size_and_opens_centred_over_its_owner()
    {
        WindowFit.Box owner = new(300, 200, 1200, 700);
        WindowFit.Fitted fit = WindowFit.Within(Laptop, Scaling, (520, 360), (420, 280), Frame, owner);
        WindowFit.Box frame = fit.Frame(Scaling, Frame.Width, Frame.Height);
        Assert.Equal((520.0, 360.0), (fit.Width, fit.Height));
        Assert.True(Math.Abs(frame.X + (frame.Width / 2) - (owner.X + (owner.Width / 2))) <= 1 && Math.Abs(frame.Y + (frame.Height / 2) - (owner.Y + (owner.Height / 2))) <= 1,
            $"A dialog that fits opened at {frame}, not centred over its owner at {owner}");
    }

    [Fact]
    public void A_window_whose_least_is_taller_than_the_screen_keeps_its_least_with_its_title_bar_at_the_areas_top()
    {
        WindowFit.Box area = new(0, 0, 1024, 600);
        WindowFit.Fitted fit = WindowFit.Within(area, 1, (1300, 800), (800, 700), Frame, area);
        Assert.Equal((1008.0, 700.0), (fit.Width, fit.Height));
        Assert.True(fit.Y == area.Y, $"A window taller than the screen opened at y {fit.Y}, its title bar off the area's top at {area.Y}");
    }

    [Fact]
    public void A_window_kept_where_it_was_placed_moves_only_back_onto_the_area_and_one_on_it_stays()
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
