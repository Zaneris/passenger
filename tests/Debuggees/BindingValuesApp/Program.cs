using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;

namespace BindingValuesApp;

public static class Program
{
    /// <summary>The UI's dispatcher, once the app has made it: nothing is posted before.</summary>
    public static Dispatcher? Ui { get; set; }

    /// <summary>Headless, so a test's run puts nothing on the screen. A line "rename" on its standard input has the window's
    /// code-behind change the customer's name on the UI thread, as something a person did in it would; "details" shows a
    /// second window and "close details" closes it (<see cref="DetailsShown"/>).</summary>
    public static int Main(string[] args)
    {
        // Named, as an app may name its UI thread: the debugger still knows it for Main's, where binding values are read.
        Thread.CurrentThread.Name = "UI";
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
            if (line.Trim() is "details" or "close details")
            {
                Action asked = line.Trim() == "details" ? DetailsShown.Open : DetailsShown.Close;
                Ui?.Post(asked);
                continue;
            }
            if (line.Trim() != "rename")
            {
                continue;
            }
            Ui?.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
                {
                    window.Rename();
                }
            });
        }
    }
}
