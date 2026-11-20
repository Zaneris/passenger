using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>A folder's or file's field with its Browse… button after it, the field filling the row, in reading order for Tab
/// too. A form's row names its <see cref="Field"/>, the row being no field of its own (<see cref="FormRows"/>).</summary>
public sealed class BrowseRow : Grid
{
    private BrowseRow(TextBox field, Func<Task> pick)
    {
        Field = field;
        Browse = new Button { Content = "Browse…", Margin = new Thickness(Gaps.Normal, 0, 0, 0) };
        Browse.Click += (_, _) => IdeErrors.Observe(pick());
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        SetColumn(Browse, 1);
        Children.Add(field);
        Children.Add(Browse);
    }

    /// <summary>Styled as the Grid it is.</summary>
    protected override Type StyleKeyOverride => typeof(Grid);

    /// <summary>The folder's or file's field.</summary>
    public TextBox Field { get; }

    /// <summary>The Browse… button, which asks <see cref="Of"/>'s pick.</summary>
    public Button Browse { get; }

    /// <param name="pick">Asks the person for the folder or file and puts it in the field.</param>
    public static BrowseRow Of(TextBox field, Func<Task> pick) => new(field, pick);
}
