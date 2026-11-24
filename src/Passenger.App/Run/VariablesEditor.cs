using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Passenger.App.Run;

/// <summary>A program's environment variables edited in place, one look wherever a run's variables are given (a
/// configuration's, a part's of a run of several): each a row of its name and its value with a button taking it out, under
/// their titles, and Add variable after them. <see cref="Changed"/> is raised for each edit a person makes, never for rows
/// the code sets.</summary>
public sealed class VariablesEditor : StackPanel
{
    private readonly StackPanel rows = new() { Spacing = Gaps.Tight };
    private bool setting;

    public VariablesEditor()
    {
        Spacing = Gaps.Small;
        AddButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Gaps.Small,
                Children = { Glyph(IconKeys.Add), new TextBlock { Text = "Add variable" } },
            },
        };
        AddButton.Click += (_, _) => Add(string.Empty, string.Empty, focus: true);
        Children.Add(Header());
        Children.Add(rows);
        Children.Add(AddButton);
    }

    /// <summary>A variable was added, typed in or taken out.</summary>
    public event EventHandler? Changed;

    public Button AddButton { get; }

    /// <summary>The rows as they stand: each variable's name and value boxes.</summary>
    public IReadOnlyList<(TextBox Name, TextBox Value)> Rows =>
        [.. rows.Children.OfType<Grid>().Select(g => ((TextBox)g.Children[0], (TextBox)g.Children[1]))];

    /// <summary>The variables the rows give, each named row's value by its name, the last of a name taken.</summary>
    public Dictionary<string, string> Values
    {
        get
        {
            Dictionary<string, string> environment = [];
            foreach ((TextBox name, TextBox value) in Rows)
            {
                if (name.Text?.Trim() is { Length: > 0 } named)
                {
                    environment[named] = value.Text ?? string.Empty;
                }
            }
            return environment;
        }
    }

    /// <summary>Whether the rows give these variables already.</summary>
    public bool Gives(IReadOnlyDictionary<string, string> environment)
    {
        Dictionary<string, string> given = Values;
        return given.Count == environment.Count && given.All(v => environment.TryGetValue(v.Key, out string? value) && value == v.Value);
    }

    /// <summary>The rows of these variables, without raising <see cref="Changed"/>.</summary>
    public void Set(IReadOnlyDictionary<string, string> environment)
    {
        setting = true;
        try
        {
            rows.Children.Clear();
            foreach ((string name, string value) in environment)
            {
                Add(name, value, focus: false);
            }
        }
        finally
        {
            setting = false;
        }
    }

    private void Add(string name, string value, bool focus)
    {
        TextBox nameBox = new() { PlaceholderText = "Name", Text = name };
        TextBox valueBox = new() { PlaceholderText = "Value", Text = value };
        Button remove = ToolButtons.Glyph(IconKeys.Close, "Remove Variable");
        Grid row = new() { ColumnDefinitions = Columns(), ColumnSpacing = Gaps.Normal };
        Grid.SetColumn(valueBox, 1);
        Grid.SetColumn(remove, 2);
        row.Children.Add(nameBox);
        row.Children.Add(valueBox);
        row.Children.Add(remove);
        rows.Children.Add(row);
        nameBox.TextChanged += (_, _) => Tell();
        valueBox.TextChanged += (_, _) => Tell();
        remove.Click += (_, _) =>
        {
            rows.Children.Remove(row);
            Tell();
        };
        if (focus)
        {
            Dispatcher.UIThread.Post(() => nameBox.Focus(), DispatcherPriority.Loaded);
        }
    }

    private void Tell()
    {
        if (!setting)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A variable's name, its value taking more of what the row leaves, and its Remove: the name no wider than a
    /// name needs, both narrowing together with the dialog.</summary>
    private static ColumnDefinitions Columns() =>
        [new ColumnDefinition(2, GridUnitType.Star) { MaxWidth = 240 }, new ColumnDefinition(3, GridUnitType.Star), new ColumnDefinition(GridLength.Auto)];

    private static PathIcon Glyph(string key)
    {
        PathIcon icon = new();
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(key));
        return icon;
    }

    private static Grid Header()
    {
        Grid header = new() { ColumnDefinitions = Columns(), ColumnSpacing = Gaps.Normal };
        TextBlock name = new() { Text = "Name", Classes = { StyleClasses.Muted } };
        TextBlock value = new() { Text = "Value", Classes = { StyleClasses.Muted } };
        Grid.SetColumn(value, 1);
        header.Children.Add(name);
        header.Children.Add(value);
        return header;
    }
}
