using System.Diagnostics;
using System.Windows;
using System.Windows.Diagnostics;
using Passenger.Core;

namespace Passenger.Inspector.Wpf;

/// <summary>Breakpoints in a WPF app's XAML: WPF reads a page's BAML as the app runs, so no code of the app's stands where an
/// element is made, but under a debugger it tells of each element as it joins the window's tree and records where its XAML
/// made it (<see cref="VisualDiagnostics"/>). Each such element, its attributes set, is handed to <see cref="Breaks.Loaded"/> with its place,
/// where the debugger's function breakpoint for a XAML breakpoint stops on the place it is on. Begun as the app starts, before
/// its first page loads, and only while a debugger is attached; non-user code, so the stack below shows what loaded it.</summary>
[DebuggerNonUserCode]
internal static class XamlStops
{
    /// <summary>Hears the elements joining the app's windows from now on, where a debugger is attached; whether it does.</summary>
    public static bool Begin()
    {
        if (!Debugger.IsAttached)
        {
            return false;
        }
        try
        {
            VisualDiagnostics.VisualTreeChanged += OnChanged;
            return true;
        }
        catch (InvalidOperationException refused)
        {
            // WPF tells of its tree only to a debugger it sees: XAML breakpoints then bind but never stop.
            InspectorTrace.Step($"WPF told of no elements joining its tree: {refused.Message}");
            return false;
        }
    }

    /// <summary>An element joining its parent: handed over once its XAML has set its attributes, which for an element WPF
    /// adds to its parent before setting them (top-down, as a panel's children are) is as it is initialized.</summary>
    private static void OnChanged(object? sender, VisualTreeChangeEventArgs e)
    {
        if (e.ChangeType != VisualTreeChangeType.Add || e.Child is not DependencyObject element || Sources.Place(element) is not { } place)
        {
            return;
        }
        if (element is FrameworkElement { IsInitialized: false } pending)
        {
            EventHandler? initialized = null;
            initialized = (_, _) =>
            {
                pending.Initialized -= initialized;
                Breaks.Loaded(PathNames.Canonical(place.File), place.Line, place.Column, pending);
            };
            pending.Initialized += initialized;
            return;
        }
        Breaks.Loaded(PathNames.Canonical(place.File), place.Line, place.Column, element);
    }
}
