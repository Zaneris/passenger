using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>A form's drop-down (Settings' options, New Project's, a run configuration's), every one in a column as wide
/// as the others where there is room, Shell.axaml's <c>ComboBox.form</c> most (Passenger.DropDownWidth), so their edges
/// line up, and as wide as the room where there is less, at its left, never past the form's edge: it asks the room it
/// is given, up to that most, whatever panel holds it.</summary>
public class FormDropDown : ComboBox
{
    public FormDropDown() => Classes.Add(StyleClasses.Form);

    /// <summary>Styled as the drop-down it is.</summary>
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override Size MeasureOverride(Size availableSize)
    {
        Size own = base.MeasureOverride(availableSize);
        // The room comes held to the most already; with no bound to it, the most itself.
        double room = double.IsFinite(availableSize.Width) ? availableSize.Width : MaxWidth;
        return double.IsFinite(room) ? own.WithWidth(Math.Max(own.Width, room)) : own;
    }
}
