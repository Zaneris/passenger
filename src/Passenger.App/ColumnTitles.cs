using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A table's column titles over its rule, one look in every table: muted words a row high
/// (<see cref="ListRow.Height"/>) over the separator's rule, docked at the table's top, each over its column where the
/// column's words start, a number's at its column's right. Its rows take the same columns through <see cref="Row"/>.
/// UiRulesTests fails titles built by hand.</summary>
public sealed class ColumnTitles : Border
{
    /// <summary>Where a tree list's rows start inside it, past its padding and theirs, so titles over a list stand over its
    /// columns.</summary>
    public const double OverList = 10;

    /// <summary>The titles' size, a caption's beside the rows' words.</summary>
    private const double TitleSize = 12;

    private readonly string columns;
    private readonly Grid grid;
    private readonly TextBlock[] titles;

    private ColumnTitles(string columns, IReadOnlyList<string> named, double inset, double cell, bool numbers)
    {
        this.columns = columns;
        grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Height = ListRow.Height, Margin = new Thickness(inset, 0) };
        titles = new TextBlock[named.Count];
        for (int i = 0; i < named.Count; i++)
        {
            bool number = numbers && i > 0;
            TextBlock title = MutedText.Of(named[i]);
            title.FontSize = TitleSize;
            title.Margin = number ? new Thickness(0, 0, cell, 0) : new Thickness(cell, 0);
            title.HorizontalAlignment = number ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            title.TextAlignment = number ? TextAlignment.Right : TextAlignment.Left;
            Grid.SetColumn(title, i);
            grid.Children.Add(title);
            titles[i] = title;
        }
        DockPanel.SetDock(this, Dock.Top);
        BorderThickness = new Thickness(0, 0, 0, 1);
        Child = grid;
        this.Bind(BorderBrushProperty, this.GetResourceObservable(TokenKeys.Separator));
    }

    /// <summary>Styled as the Border it is.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <param name="columns">The rows' column widths, as their grid has them.</param>
    /// <param name="titles">The columns' titles in order, empty for none; columns past them have none.</param>
    /// <param name="inset">Where the rows' grid starts from each side of the table: <see cref="OverList"/> over a tree list.</param>
    /// <param name="cell">Where words start inside a column, as the rows' cells have them.</param>
    /// <param name="numbers">Whether every column after the first holds numbers, its title at its right.</param>
    public static ColumnTitles Of(string columns, IReadOnlyList<string> titles, double inset = 0, double cell = 8, bool numbers = false) =>
        new(columns, titles, inset, cell, numbers);

    /// <summary>A tree list's titles: its first column a name, taking the width the others leave, the rest numbers of the
    /// widths given.</summary>
    public static ColumnTitles OfNumbers(params (string Title, double Width)[] columns) =>
        new(string.Join(",", columns.Select((c, i) => i == 0 ? "*" : c.Width.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            [.. columns.Select(c => c.Title)], OverList, cell: 0, numbers: true);

    /// <summary>The titles' words, in their columns' order.</summary>
    public IReadOnlyList<TextBlock> Titles => titles;

    /// <summary>Each title a button that sorts the table by its column.</summary>
    public ColumnTitles Sorting(Action<int> sort)
    {
        for (int i = 0; i < titles.Length; i++)
        {
            TextBlock title = titles[i];
            int column = i;
            grid.Children.Remove(title);
            Button button = new() { Classes = { StyleClasses.Crumb }, Content = title, Padding = new Thickness(0), HorizontalAlignment = title.HorizontalAlignment,
                VerticalAlignment = VerticalAlignment.Center };
            button.Margin = title.Margin;
            title.Margin = default;
            button.Click += (_, _) => sort(column);
            Grid.SetColumn(button, column);
            grid.Children.Add(button);
        }
        return this;
    }

    /// <summary>A row of the table: its cells in the titles' columns, a row high.</summary>
    public Grid Row(params Control[] cells)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions(columns), Height = ListRow.Height };
        for (int i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            row.Children.Add(cells[i]);
        }
        return row;
    }
}
