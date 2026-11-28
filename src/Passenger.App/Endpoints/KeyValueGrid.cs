using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Editor;

namespace Passenger.App.Endpoints;

/// <summary>A table of keys and values: each row turned on or off by its check box, removed by its ×, and an empty row
/// last that becomes a new one as soon as something is typed in it. Changing a row raises <see cref="EditableTable.Changed"/>;
/// <see cref="Set"/> replaces the rows without raising it.</summary>
public sealed class KeyValueGrid : EditableTable
{
    /// <summary>One row's controls.</summary>
    public sealed record Row(CheckBox On, TextBox Key, TextBox Value, Button Remove, Border View)
    {
        public bool IsEmpty => string.IsNullOrEmpty(Key.Text) && string.IsNullOrEmpty(Value.Text);
    }

    private readonly List<Row> rows = [];
    private readonly string keyHint;
    private readonly string valueHint;

    public KeyValueGrid(string keyHint, string valueHint)
        : base("32,*,*,32", ["", "Key", "Value"], scrolls: true)
    {
        this.keyHint = keyHint;
        this.valueHint = valueHint;
        AddEmpty();
    }

    /// <summary>Every row, the empty last one included.</summary>
    public IReadOnlyList<Row> Rows => rows;

    /// <summary>The rows with a key or value, in order, each with whether it is on.</summary>
    public IReadOnlyList<(bool On, string Key, string Value)> Pairs =>
        [.. rows.Where(r => !r.IsEmpty).Select(r => (r.On.IsChecked == true, r.Key.Text?.Trim() ?? string.Empty, r.Value.Text?.Trim() ?? string.Empty))];

    /// <summary>The rows on, as sent.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Enabled =>
        [.. Pairs.Where(p => p.On).Select(p => new KeyValuePair<string, string>(p.Key, p.Value))];

    /// <summary>Replaces the rows, an empty one after them, without raising <see cref="Changed"/>.</summary>
    public void Set(IEnumerable<(bool On, string Key, string Value)> pairs) => Setting(() =>
    {
        rows.Clear();
        Table.Children.Clear();
        foreach ((bool on, string key, string value) in pairs)
        {
            Add(on, key, value);
        }
        AddEmpty();
    });

    private void AddEmpty() => Add(true, string.Empty, string.Empty);

    private Row Add(bool on, string key, string value)
    {
        CheckBox check = new() { IsChecked = on, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        TextBox keyBox = new TextBox { Classes = { StyleClasses.Cell, StyleClasses.Code }, Text = key };
        TextBox valueBox = new TextBox { Classes = { StyleClasses.Cell, StyleClasses.Code }, Text = value };
        Button remove = ToolButtons.Glyph(IconKeys.Close, "Remove");
        remove.VerticalAlignment = VerticalAlignment.Center;
        Border view = Ruled(check, keyBox, valueBox, remove);
        Row row = new(check, keyBox, valueBox, remove, view);
        rows.Add(row);
        Table.Children.Add(view);
        Mark(row);
        TypedText.Hear(keyBox, () => Typed(row));
        TypedText.Hear(valueBox, () => Typed(row));
        check.IsCheckedChanged += (_, _) => Tell();
        remove.Click += (_, _) =>
        {
            rows.Remove(row);
            Table.Children.Remove(view);
            Tell();
        };
        return row;
    }

    /// <summary>Typing in the empty last row makes it a row of its own, and another empty one follows it.</summary>
    private void Typed(Row row)
    {
        if (IsSetting)
        {
            return;
        }
        if (row == rows[^1] && !row.IsEmpty)
        {
            row.On.IsChecked = true;
            AddEmpty();
        }
        foreach (Row each in rows)
        {
            Mark(each);
        }
        Tell();
    }

    /// <summary>The empty last row has no check box or × until it holds something.</summary>
    private void Mark(Row row)
    {
        bool last = rows.Count > 0 && row == rows[^1] && row.IsEmpty;
        row.On.IsVisible = !last;
        row.Remove.IsVisible = !last;
        row.Key.PlaceholderText = last ? keyHint : null;
        row.Value.PlaceholderText = last ? valueHint : null;
    }
}
