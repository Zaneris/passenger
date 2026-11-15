using Passenger.Core;

namespace Passenger.Inspector;

/// <summary>When a wait of an inspector's for the app to set itself up (the logger's sink it sets, its Application, its UI
/// thread, its first window) looks again: quickly through the program's first moments and those after each assembly it
/// loads, as an app's setup loads its framework's platform, renderer and XAML, and seldom otherwise, so a program that never
/// makes an Application (a tool using the framework's types, a test harness) wakes the inspector seldom once it has started.
/// Waits until the next look on the waiting thread; an assembly loaded meanwhile ends the wait at once.</summary>
internal sealed class Looking : IDisposable
{
    /// <summary>How long looks come quickly after the program starts or loads an assembly.</summary>
    private static readonly TimeSpan QuickFor = TimeSpan.FromSeconds(2);

    /// <summary>How often it looks outside those moments.</summary>
    private static readonly TimeSpan Seldom = TimeSpan.FromSeconds(1);

    private readonly TimeSpan quick;
    private readonly object gate = new();
    private Deadline quickUntil = Deadline.In(QuickFor);

    /// <summary>Whether an assembly has loaded since the last look.</summary>
    private bool loaded;

    /// <param name="quick">How often it looks in the program's first moments and after an assembly loads.</param>
    public Looking(TimeSpan quick)
    {
        this.quick = quick;
        AppDomain.CurrentDomain.AssemblyLoad += OnLoad;
    }

    /// <summary>Waits until the next look, on the one thread that looks.</summary>
    public void Wait()
    {
        TimeSpan wait = quickUntil.Passed ? Seldom : quick;
        bool woken;
        lock (gate)
        {
            if (!loaded)
            {
                Monitor.Wait(gate, wait);
            }
            woken = loaded;
            loaded = false;
        }
        if (woken)
        {
            quickUntil = Deadline.In(QuickFor);
        }
    }

    /// <summary>Hears no more of the program's loads.</summary>
    public void Dispose() => AppDomain.CurrentDomain.AssemblyLoad -= OnLoad;

    /// <summary>On the thread that loaded an assembly, any of the program's: only marked, so the load goes on at once.</summary>
    private void OnLoad(object? sender, AssemblyLoadEventArgs args)
    {
        lock (gate)
        {
            loaded = true;
            Monitor.Pulse(gate);
        }
    }
}
