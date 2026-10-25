using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Passenger.Inspector;

/// <summary>Where Passenger's debugger stops the app for the inspector, a function breakpoint on each method, by the same
/// names in an Avalonia app and a WPF one: on <see cref="Changed"/> one per property breakpoint (<see cref="Wire.Changed"/>),
/// its condition <c>breakpoint == id</c> and the person's own after it; on <see cref="BindingFailed"/> one while the
/// framework's Binding Errors breakpoint is on (<see cref="Wire.Failed"/>). The person's conditions read the parameters,
/// typed as the framework's own (InspectedObject and InspectedProperty, each project's aliases). Non-user code, so Just My
/// Code shows the code below that made the change or the binding fail as where the app stopped.</summary>
public static class Breaks
{
    /// <summary>A watched property changed: <c>newValue</c>, <c>oldValue</c>, <c>control</c>, <c>property</c>, and
    /// <c>change</c>, what the IDE says of the stop, with <c>setAt</c>, where the style that set it is written (a
    /// <see cref="SourcePlace"/> as the protocol writes one; null in a WPF app, whose value source names no style's place).</summary>
    [DebuggerNonUserCode]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Changed(int breakpoint, InspectedObject control, InspectedProperty property, object? oldValue, object? newValue, string change, string? setAt)
    {
        // Held for the debugger, which reads them where the program stops.
        GC.KeepAlive(control);
        GC.KeepAlive(property);
        GC.KeepAlive(oldValue);
        GC.KeepAlive(newValue);
        GC.KeepAlive(change);
        GC.KeepAlive(setAt);
        _ = breakpoint;
    }

    /// <summary>A binding failed, called on the thread that evaluated it before the framework goes on, so the stack is the
    /// one that failed (the window's XAML being loaded, the code that set its DataContext or raised PropertyChanged).</summary>
    [DebuggerNonUserCode]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BindingFailed(BindingError bindingError) => GC.KeepAlive(bindingError);
}

/// <summary>A binding that failed, as the app stops at it: Passenger's <c>$bindingError</c> (in Variables, and in the
/// breakpoint's condition and log message). The control and the property it sets, its path and the part of the path that
/// failed, the control's DataContext, the framework's message, and where the control is written in XAML. Fields, so the
/// debugger reads each with no code run in the stopped app, even once an evaluation has run past its time.</summary>
[DebuggerDisplay("{Description,nq}")]
public sealed class BindingError
{
    public readonly InspectedObject? Control;

    public readonly InspectedProperty? Property;

    /// <summary>The binding's path as its XAML writes it.</summary>
    public readonly string Path;

    /// <summary>The part of the path the framework could not read (Avalonia's "error point", or "(source)" when it had no
    /// source), when it says which.</summary>
    public readonly string? FailedPart;

    /// <summary>The control's DataContext as the binding failed.</summary>
    public readonly object? DataContext;

    public readonly string Message;

    /// <summary>The control's element in its XAML, "MainWindow.axaml:8:20", when the app's build recorded it: its XAML
    /// compiler's line.</summary>
    public readonly string? Xaml;

    /// <summary>"TextBlock#Caption.Text to Nmae: Could not find a matching property accessor for 'Nmae' on ...".</summary>
    public readonly string Description;

    /// <summary>The failure as the protocol writes it (<see cref="BindingFailure"/>), which Passenger reads at the stop.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public readonly string Said;

    internal BindingError(InspectedObject? control, InspectedProperty? property, object? dataContext, BindingFailure failure, string said)
    {
        Control = control;
        Property = property;
        Path = failure.Path;
        FailedPart = failure.ErrorPoint;
        DataContext = dataContext;
        Message = failure.Message;
        Xaml = failure.Place is { } place ? $"{System.IO.Path.GetFileName(place.File)}:{place.Line}:{place.Column}" : null;
        Description = failure.Description;
        Said = said;
    }
}
