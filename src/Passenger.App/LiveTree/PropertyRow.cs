using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.LiveTree;

/// <summary>A property's row as every property list draws it, the Live Visual Tree's and a Godot scene's: its name in
/// the first column (<see cref="Named"/>), its value in the code font in the second (<see cref="Value"/>), which a box
/// typed in (<see cref="Box"/>) or a cell's own editor takes the place of while it edits, and under both where the value
/// comes from (<see cref="Source"/>). The columns split the row one way in every list: the name the larger part beside
/// a value's words, the value the larger part beside an editor (<see cref="SplitFor"/>).</summary>
public sealed class PropertyRow : Grid
{
    private static readonly GridLength NameByWords = new(11, GridUnitType.Star);
    private static readonly GridLength ByWords = new(9, GridUnitType.Star);
    private static readonly GridLength NameByEditor = new(8, GridUnitType.Star);
    private static readonly GridLength ByEditor = new(12, GridUnitType.Star);

    public PropertyRow()
    {
        ColumnDefinitions = [new ColumnDefinition(NameByWords), new ColumnDefinition(ByWords)];
        RowDefinitions = new RowDefinitions("Auto,Auto");
        Margin = new Thickness(Gaps.Normal, Gaps.Hair);
        Named.Children.Add(Label);
        Children.Add(Named);
        InValue(Value);
        InValue(Box);
        Under(Source);
    }

    /// <summary>The name's place: its words, after whatever stands before them (a breakpoint's dot, docked left).</summary>
    public DockPanel Named { get; } = new() { Margin = new Thickness(0, 0, Gaps.Normal, 0) };

    /// <summary>The property's name.</summary>
    public TextBlock Label { get; } = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The value's words.</summary>
    public TextBlock Value { get; } = new() { Classes = { StyleClasses.Code }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The box the value is typed over in, hidden until it is.</summary>
    public TextBox Box { get; } = new ValueField { IsVisible = false };

    /// <summary>Where the value comes from, muted under the name and value.</summary>
    public TextBlock Source { get; } = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>Splits the row for a value a cell's own editor shows, which takes the larger part, or for its words.</summary>
    public void SplitFor(bool editor)
    {
        ColumnDefinitions[0].Width = editor ? NameByEditor : NameByWords;
        ColumnDefinitions[1].Width = editor ? ByEditor : ByWords;
    }

    /// <summary>Puts a control in the value's place (an editor of the value's own).</summary>
    public void InValue(Control control)
    {
        SetColumn(control, 1);
        Children.Add(control);
    }

    /// <summary>Puts a control under the name and value, across both (a link to where the value is set, a note).</summary>
    public void Under(Control control)
    {
        SetRow(control, 1);
        SetColumnSpan(control, 2);
        Children.Add(control);
    }
}
