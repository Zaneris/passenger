using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Containers;

namespace Passenger.App.Database;

/// <summary>An import's columns, one row each of the file's: its name, and the table's column it goes into (chosen among an
/// existing table's, Skip passing it over) or, for a new table, the column's name and type, proposed from the first rows.</summary>
public sealed class ImportColumnsTable : EditableTable
{
    /// <summary>The choice that passes a file's column over.</summary>
    public const string Skip = "Skip";

    private readonly List<(string Name, Control Into, TextBox? Type)> rows = [];

    public ImportColumnsTable()
        : base("*,*,*", ["File column", "Table column", "Type"], scrolls: true)
    {
        MaxHeight = 220;
    }

    /// <summary>Each file column's row: its name, the table's column it goes into (null: skipped) and a new column's type.</summary>
    public IReadOnlyList<ImportColumn> Columns => [.. rows.Select(r => new ImportColumn(r.Name,
        r.Into switch
        {
            ComboBox { SelectedItem: string chosen } when chosen != Skip => chosen,
            TextBox { Text: { } named } when named.Trim().Length > 0 => named.Trim(),
            _ => null,
        }, r.Type?.Text?.Trim() ?? string.Empty))];

    /// <summary>Says why there are no rows (the file not read, or read as none), in their place.</summary>
    public void Say(string why) => Setting(() =>
    {
        rows.Clear();
        Table.Children.Clear();
        SayInstead(why);
    });

    /// <summary>Rows for a new table: each of the file's columns named as it is, its type proposed from its values.</summary>
    public void SetNew(IReadOnlyList<string> names, IReadOnlyList<string> types) => Setting(() =>
    {
        Begin(names);
        for (int i = 0; i < names.Count; i++)
        {
            TextBox name = new() { Classes = { StyleClasses.Cell }, Text = names[i], Name = $"ImportColumnName{i.ToString(CultureInfo.InvariantCulture)}" };
            TextBox type = new() { Classes = { StyleClasses.Cell }, Text = types[i], Name = $"ImportColumnType{i.ToString(CultureInfo.InvariantCulture)}" };
            Add(names[i], name, type);
        }
    });

    /// <summary>Rows for an existing table: each of the file's columns put in the table's column <paramref name="into"/>
    /// says (null: skipped), the table's others offered.</summary>
    public void SetExisting(IReadOnlyList<string> names, IReadOnlyList<string> table, IReadOnlyList<int?> into) => Setting(() =>
    {
        Begin(names);
        for (int i = 0; i < names.Count; i++)
        {
            ComboBox chosen = new()
            {
                Classes = { StyleClasses.Cell },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = (string[])[Skip, .. table],
                SelectedIndex = into[i] is { } at ? at + 1 : 0,
                Name = $"ImportColumnInto{i.ToString(CultureInfo.InvariantCulture)}",
            };
            chosen.SelectionChanged += (_, _) => Tell();
            Add(names[i], chosen, null);
        }
    });

    private void Begin(IReadOnlyList<string> names)
    {
        rows.Clear();
        Table.Children.Clear();
        SayInstead(names.Count == 0 ? "The file holds no columns" : null);
    }

    private void Add(string name, Control into, TextBox? type)
    {
        TextBlock file = new() { Text = ContainerSecrets.InSaid(name), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        if (into is TextBox box)
        {
            box.TextChanged += (_, _) => Tell();
        }
        if (type is not null)
        {
            type.TextChanged += (_, _) => Tell();
        }
        Border view = Ruled(file, into, type ?? (Control)new TextBlock { Classes = { StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0), Text = "as the table has it" });
        rows.Add((name, into, type));
        Table.Children.Add(view);
    }
}
