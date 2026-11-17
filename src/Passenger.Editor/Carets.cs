using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Passenger.Core;

namespace Passenger.Editor;

/// <summary>Every caret's blink and width, put in force once for the whole application: its resources hold the blink
/// under <see cref="BlinkKey"/> (and a text box's under <see cref="TextBoxBlinkKey"/>) and the width under
/// <see cref="WidthKey"/>, which every editor and terminal follow (<see cref="FollowBlink"/>, <see cref="FollowWidth"/>)
/// and the text boxes' style takes.</summary>
public static class Carets
{
    /// <summary>The application's resource holding the <see cref="CaretBlink"/> in force.</summary>
    [IdentifierHome]
    public const string BlinkKey = "Passenger.CaretBlink";

    /// <summary>The application's resource holding a text box's blink, the time it stays shown and hidden, zero for none.</summary>
    [IdentifierHome]
    public const string TextBoxBlinkKey = "Passenger.TextBoxCaretBlink";

    /// <summary>The application's resource holding a line caret's width in force.</summary>
    [IdentifierHome]
    public const string WidthKey = "Passenger.CaretWidth";

    /// <summary>A line caret's width where none is set.</summary>
    public const double DefaultWidth = 2;

    /// <summary>Puts a blink in force for every caret following it.</summary>
    public static void UseBlink(Application app, CaretBlink blink)
    {
        app.Resources[BlinkKey] = blink;
        // A text box hides its caret as long as it shows it, so it takes the time shown.
        app.Resources[TextBoxBlinkKey] = blink.Blinks ? blink.Shown : TimeSpan.Zero;
    }

    /// <summary>Puts a line caret's width in force for every caret following it.</summary>
    public static void UseWidth(Application app, double width) => app.Resources[WidthKey] = width;

    /// <summary>Has a control's blink follow the one in force, below any value it is given.</summary>
    public static void FollowBlink(StyledElement element, StyledProperty<CaretBlink> property) =>
        element.Bind(property, element.GetResourceObservable(BlinkKey, found => found as CaretBlink ?? CaretBlink.Default), BindingPriority.Style);

    /// <summary>Has a control's caret width follow the one in force, below any value it is given.</summary>
    public static void FollowWidth(StyledElement element, StyledProperty<double> property) =>
        element.Bind(property, element.GetResourceObservable(WidthKey, found => found is double width ? width : DefaultWidth), BindingPriority.Style);
}
