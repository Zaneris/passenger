using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WpfTreeApp;

/// <summary>What a person does in the app comes as lines on its standard input, which the Debug window's console sends:
/// "click X,Y" (or "move X,Y") in the main window's units, as Windows sends a pointer's messages to the window, so WPF takes
/// them as it takes a real mouse's while the person's own pointer stays where it is; "rebind" gives the window a new view
/// model, binding (and failing) again; "nudge" has the Count button set its own Margin (SetCurrentValue) over its Style's.</summary>
public partial class App : Application
{
    private const int MouseMove = 0x0200;
    private const int LeftDown = 0x0201;
    private const int LeftUp = 0x0202;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Said on the app's output, so a test that points at the app sees whether WPF had the pointer's press at all.
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent,
            new System.Windows.Input.MouseButtonEventHandler((sender, down) => Console.WriteLine(
                string.Create(CultureInfo.InvariantCulture, $"Pointer down at {down.GetPosition((IInputElement)sender)} on {down.OriginalSource?.GetType().Name}"))),
            handledEventsToo: true);
        Thread input = new(Input) { IsBackground = true, Name = "Input" };
        input.Start();
    }

    private void Input()
    {
        while (Console.ReadLine() is { } line)
        {
            string[] words = line.Trim().Split(' ', 2);
            Dispatcher.BeginInvoke(() => Do(words));
        }
    }

    private void Do(string[] words)
    {
        if (MainWindow is not { } window)
        {
            return;
        }
        if (words is ["rebind"])
        {
            window.DataContext = new MainViewModel();
            Console.WriteLine("Bound again");
            return;
        }
        if (words is ["nudge"] && window is WpfTreeApp.MainWindow main)
        {
            main.Count.SetCurrentValue(FrameworkElement.MarginProperty, new Thickness(3));
            Console.WriteLine("Nudged");
            return;
        }
        if (words is not [string doing, string at] || at.Split(',') is not [string x, string y])
        {
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int px = (int)Math.Round(double.Parse(x, CultureInfo.InvariantCulture) * dpi.DpiScaleX);
        int py = (int)Math.Round(double.Parse(y, CultureInfo.InvariantCulture) * dpi.DpiScaleY);
        IntPtr where = (IntPtr)((py << 16) | (px & 0xFFFF));
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (!hooked)
        {
            hooked = true;
            HwndSource.FromHwnd(handle)?.AddHook(LetGo);
        }
        // WPF takes a pointer's messages only in a window under the pointer or holding its capture: held until Release comes.
        SetCapture(handle);
        PostMessage(handle, MouseMove, IntPtr.Zero, where);
        if (doing == "click")
        {
            PostMessage(handle, LeftDown, 1, where);
            PostMessage(handle, LeftUp, IntPtr.Zero, where);
        }
        PostMessage(handle, Release, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine($"Posted a {doing} at {px},{py} pixels");
    }

    private bool hooked;

    /// <summary>A message of the app's own, after the pointer's: the capture taken for them is let go.</summary>
    private const int Release = 0x8001;

    private static IntPtr LetGo(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Release)
        {
            ReleaseCapture();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
}
