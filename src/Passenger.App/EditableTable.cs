using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A table whose rows are edited in place (a request's headers, a run's services, a configuration's projects),
/// one look in every dialog: framed on the list's ground, its column titles over its rows, each row ruled under it
/// (<see cref="Ruled"/>), and where it has no rows a muted line saying why in their place (<see cref="SayInstead"/>).
/// <see cref="Changed"/> is raised for each edit a person makes, never for rows the code sets (<see cref="Setting"/>).</summary>
public abstract class EditableTable : Border
{
    private readonly ColumnTitles titles;
    private readonly TextBlock said = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(Gaps.Normal, Gaps.Small), IsVisible = false };

    /// <param name="columns">The rows' column widths, as their grid has them.</param>
    /// <param name="titled">The columns' titles in order, empty for none.</param>
    /// <param name="scrolls">Whether its rows scroll inside it, for a table given a height of its own.</param>
    protected EditableTable(string columns, IReadOnlyList<string> titled, bool scrolls = false)
    {
        Classes.Add(StyleClasses.Framed);
        ClipToBounds = true;
        titles = ColumnTitles.Of(columns, titled);
        StackPanel body = new() { Children = { Table, said } };
        Child = new DockPanel { Children = { titles, scrolls ? new ScrollViewer { Content = body } : body } };
    }

    /// <summary>Styled as the Border it is, so Border.framed frames it.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>A row was edited: typed in, chosen, turned on or off, added, removed or moved.</summary>
    public event EventHandler? Changed;

    /// <summary>What it says in place of rows, or empty while it says nothing.</summary>
    public string Said => said.IsVisible ? said.Text ?? string.Empty : string.Empty;

    /// <summary>The rows' views, in their order.</summary>
    protected StackPanel Table { get; } = new();

    /// <summary>Whether the code is setting rows, which raises no <see cref="Changed"/>.</summary>
    protected bool IsSetting { get; private set; }

    /// <summary>Says why there are no rows, in their place; null says nothing.</summary>
    protected void SayInstead(string? why)
    {
        said.Text = why;
        said.IsVisible = why is not null;
    }

    /// <summary>Sets rows, or changes one, as the code does: none of it raises <see cref="Changed"/>.</summary>
    protected void Setting(Action set)
    {
        bool was = IsSetting;
        IsSetting = true;
        try
        {
            set();
        }
        finally
        {
            IsSetting = was;
        }
    }

    /// <summary>Raises <see cref="Changed"/> for a person's edit, never while the code sets rows.</summary>
    protected void Tell()
    {
        if (!IsSetting)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A row of the cells given, in the titles' columns, ruled under it.</summary>
    protected Border Ruled(params Control[] cells) => titles.Ruled(cells);
}
