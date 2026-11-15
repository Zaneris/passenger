using Avalonia.Controls;

namespace Passenger.App;

/// <summary>A folder's field with its Browse... button after it, the field filling the row, in reading order for Tab
/// too. A form's row names its <see cref="Field"/>, the row being no field of its own (<see cref="FormRows"/>).</summary>
public sealed class BrowseRow : Grid
{
    private BrowseRow(TextBox field, Button browse)
    {
        Field = field;
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        SetColumn(browse, 1);
        Children.Add(field);
        Children.Add(browse);
    }

    /// <summary>Styled as the Grid it is.</summary>
    protected override Type StyleKeyOverride => typeof(Grid);

    /// <summary>The folder's field.</summary>
    public TextBox Field { get; }

    public static BrowseRow Of(TextBox field, Button browse) => new(field, browse);
}
