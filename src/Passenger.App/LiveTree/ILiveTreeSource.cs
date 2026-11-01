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
