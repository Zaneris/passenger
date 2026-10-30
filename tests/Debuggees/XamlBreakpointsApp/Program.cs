using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;

namespace XamlBreakpointsApp;

public static class Program
{
    /// <summary>The UI's dispatcher, once the app has made it: nothing is posted before.</summary>
    public static Dispatcher? Ui { get; set; }

    /// <summary>Headless, so a test's run puts nothing on the screen. A line on its standard input ("paint", "warn",
    /// "nudge", "rename") has the window's code-behind change something, as something a person did in it would.</summary>
    public static int Main(string[] args)
    {
        Thread input = new(Input) { IsBackground = true, Name = "Input" };
        input.Start();
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .StartWithClassicDesktopLifetime(args);
    }

    private static void Input()
    {
        while (Console.ReadLine() is { } line)
        {
            string said = line.Trim();
            Ui?.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
                {
                    return;
                }
                switch (said)
                {
                    case "paint":
                        window.Paint();
                        break;
                    case "warn":
                        window.Warn();
                        break;
                    case "nudge":
                        window.Nudge();
                        break;
                    case "rename":
                        window.Rename();
                        break;
                }
            });
        }
    }
}
