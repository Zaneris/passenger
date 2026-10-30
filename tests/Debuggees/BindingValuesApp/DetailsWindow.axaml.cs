using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace BindingValuesApp;

/// <summary>A second window, which "details" shows and "close details" closes.</summary>
public partial class DetailsWindow : Window
{
    public DetailsWindow()
    {
        InitializeComponent();
        DataContext = new Details();
    }
}

public sealed class Details
{
    public string Note => "Kept apart";
}

/// <summary>The details window shown, and once closed, whether the GC collected it: said on the console after the GC has
/// had its tries, the app's UI thread running between them.</summary>
public static class DetailsShown
{
    private static DetailsWindow? shown;
    private static WeakReference? closed;
    private static int tries;

    public static void Open()
    {
        shown = new DetailsWindow();
        shown.Show();
        Console.WriteLine("Details shown");
    }

    public static void Close()
    {
        closed = Closing();
        Dispatcher.UIThread.Post(Collect, DispatcherPriority.Background);
    }

    /// <summary>Closed apart, so nothing of this frame holds the window after.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Closing()
    {
        DetailsWindow window = shown!;
        shown = null;
        window.Close();
        return new WeakReference(window);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        if (!closed!.IsAlive || ++tries == 20)
        {
            Console.WriteLine($"Details collected: {!closed.IsAlive}");
            return;
        }
        DispatcherTimer.RunOnce(Collect, TimeSpan.FromMilliseconds(100));
    }
}
