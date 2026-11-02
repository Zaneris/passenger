using Passenger.Inspector;

namespace Passenger.App.LiveTree;

/// <summary>What the Live Visual Tree shows a running program's tree from: an Avalonia or WPF app's inspector
/// (<see cref="LiveApp"/>), or a Godot game's remote debug connection (<see cref="Godot.GodotGame"/>). Its state, name,
/// framework and process, its tree, and an element's details; events on the UI thread.</summary>
public interface ILiveTreeSource
{
    /// <summary>Its run configuration's name.</summary>
    string Name { get; }

    LiveAppState State { get; }

    /// <summary>Why it was refused, once it was.</summary>
    string? Problem { get; }

    int? ProcessId { get; }

    /// <summary>What it runs on, as the window's status line says it once it is up.</summary>
    string Framework { get; }

    event EventHandler? StateChanged;

    /// <summary>Its tree changed: elements came or went.</summary>
    event EventHandler? TreeChanged;

    Task<AppTree> TreeAsync();

    Task<ElementDetails> DetailsAsync(long element);
}

/// <summary>A source whose elements' properties can be set as it runs, from the text the person types.</summary>
public interface ILiveTreeValues
{
    /// <summary>Why a property shown cannot be set, or null when it can.</summary>
    string? WhyNotSet(long element, PropertyValue value);

    /// <summary>Sets a property of an element to the value a text gives, as its type reads it; throws a
    /// <see cref="LiveAppException"/> saying why it could not.</summary>
    Task SetAsync(long element, PropertyValue value, string text);
}

/// <summary>The editors a source gives the Properties page's rows in place of a value's text, where one fits its value (a
/// Godot game's colours, ranges, enums, vectors, check boxes and resources): a cell made once with each row.</summary>
public interface ILiveValueCells
{
    LiveValueCell Make();
}

/// <summary>A row's editor, in the value's column, and its note under the row in place of where the value comes from (a
/// value changed, with Keep), shown for each property the row is handed.</summary>
public abstract class LiveValueCell
{
    public abstract Avalonia.Controls.Control Editor { get; }

    public abstract Avalonia.Controls.Control Note { get; }

    /// <summary>Shows a property of an element: whether the editor takes the place of the value's text, and whether the note
    /// takes the place of its source.</summary>
    public abstract (bool Edits, bool Notes) Show(long element, PropertyValue value);
}
