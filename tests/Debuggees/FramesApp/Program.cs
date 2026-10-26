using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;

namespace FramesApp;

public static class Program
{
    /// <summary>Slow layouts asked for by the command line ("--frames N"), after which the app ends by itself.</summary>
    public static int Frames { get; private set; }

    /// <summary>Whether the command line asked for long lists ("--lists"): a panel of a hundred borders and one of a
    /// thousand text blocks in the window.</summary>
    public static bool Lists { get; private set; }

    /// <summary>Headless, so a test's run puts nothing on the screen. Its window's SlowPanel spins in its measure; "slow N"
    /// typed into its console measures it again N times, a frame each, "stall MS" measures it once for MS milliseconds,
    /// "listened" says whether anything listens to Avalonia's layout activities and meters, and "quit" ends it.</summary>
    public static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--frames" && i + 1 < args.Length)
            {
                Frames = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--lists")
            {
                Lists = true;
            }
        }
        Thread input = new(Read) { IsBackground = true, Name = "Input" };
        input.Start();
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Measures the window's SlowPanel again, a frame at a time, then ends the app if it is to end.</summary>
    public static void Slow(int times, bool thenQuit)
    {
        int left = times;
        DispatcherTimer.Run(() =>
        {
            if (left-- > 0 && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
            {
                window.Slow.InvalidateMeasure();
                return true;
            }
            if (thenQuit)
            {
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
            Console.WriteLine($"measured {times} times");
            return false;
        }, TimeSpan.FromMilliseconds(80));
    }

    private static void Read()
    {
        while (Console.ReadLine() is { } line)
        {
            string[] words = line.Trim().Split(' ');
            if (words is ["slow", string count] && int.TryParse(count, CultureInfo.InvariantCulture, out int times))
            {
                Dispatcher.UIThread.Post(() => Slow(times, thenQuit: false));
            }
            else if (words is ["stall", string spin] && int.TryParse(spin, CultureInfo.InvariantCulture, out int ms))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
                    {
                        SlowPanel.StallMs = ms;
                        window.Slow.InvalidateMeasure();
                    }
                });
            }
            else if (words is ["listened"])
            {
                // Listeners take a source or meter by its name, so one of the name Avalonia's has is listened to as Avalonia's is.
                using ActivitySource activities = new("Avalonia.Diagnostic.Source");
                using Meter meter = new("Avalonia.Diagnostic.Meter");
                Histogram<double> measured = meter.CreateHistogram<double>("framesapp.listened");
                Console.WriteLine($"layout listened: activities {activities.HasListeners()}, meters {measured.Enabled}");
            }
            else if (words is ["quit"])
            {
                Dispatcher.UIThread.Post(() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
            }
        }
    }
}
