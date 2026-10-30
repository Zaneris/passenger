using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace LiveTreeApp;

public static class Program
{
    /// <summary>The UI's dispatcher, once the app has made it: input is never posted before.</summary>
    public static Dispatcher? Ui { get; set; }

    /// <summary>Headless, so a test's run puts nothing on the screen; what a person's pointer does in its window comes as
    /// lines on its standard input ("move X,Y" or "click X,Y" in the window's units), and what they type as "type TEXT",
    /// which the Debug window's console sends.</summary>
    public static int Main(string[] args)
    {
        Thread pointer = new(Pointer) { IsBackground = true, Name = "Pointer" };
        pointer.Start();
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .StartWithClassicDesktopLifetime(args);
    }

    private static void Pointer()
    {
        while (Console.ReadLine() is { } line)
        {
            string[] words = line.Trim().Split(' ', 2);
            if (words is ["type", string typed])
            {
                Ui?.Post(() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.KeyTextInput(typed));
                continue;
            }
            if (words.Length != 2 || words[1].Split(',') is not [string x, string y])
            {
                continue;
            }
            Point at = new(double.Parse(x, CultureInfo.InvariantCulture), double.Parse(y, CultureInfo.InvariantCulture));
            Ui?.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
                {
                    return;
                }
                window.MouseMove(at);
                if (words[0] == "click")
                {
                    window.MouseDown(at, MouseButton.Left);
                    window.MouseUp(at, MouseButton.Left);
                }
            });
        }
    }
}
