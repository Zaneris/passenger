using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>A value typed over in its row, every list's alike (the debugger's Set Value, a property's in the Live Visual
/// Tree and a Godot scene, a Godot value's): inside the row's height (<see cref="Tall"/>), its words code at the pane's step
/// (Shell.axaml's <c>TextBox.inRow</c> and <c>code</c>). As wide as what it holds and at least <see cref="Wanted"/> where
/// its row has the room; in a narrower row (a <see cref="ShrinkingRow"/>) it gives way first, down to a few letters,
/// before the row's name does, its text scrolling within it.</summary>
public sealed class ValueField : TextBox
{
    /// <summary>Its height: a list's row inside its hairlines.</summary>
    public static readonly double Tall = ListRow.Height - 2 * Hairlines.Width;

    /// <summary>The least it is wide where its row has the room.</summary>
    private const double Wanted = 240;

    public ValueField()
    {
        Classes.Add(StyleClasses.InRow);
        Classes.Add(StyleClasses.Code);
        SetValue(ShrinkingRow.LeastProperty, 80);
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override Size MeasureOverride(Size availableSize)
    {
        Size size = base.MeasureOverride(availableSize);
        return size.WithWidth(Math.Max(size.Width, Math.Min(Wanted, availableSize.Width)));
    }
}
