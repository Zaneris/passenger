using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Endpoints;

/// <summary>A table of keys and values: each row turned on or off by its check box, removed by its ×, and an empty row
/// last that becomes a new one as soon as something is typed in it. Changing a row raises <see cref="Changed"/>;
/// <see cref="Set"/> replaces the rows without raising it.</summary>
public sealed class KeyValueGrid : Border
{
    /// <summary>One row's controls.</summary>
    public sealed record Row(CheckBox On, TextBox Key, TextBox Value, Button Remove, Border View)
    {
        public bool IsEmpty => string.IsNullOrEmpty(Key.Text) && string.IsNullOrEmpty(Value.Text);
    }

    private const string Columns = "32,*,*,32";

    private readonly StackPanel table = new();
    private readonly List<Row> rows = [];
    private readonly string keyHint;
    private readonly string valueHint;
    private bool setting;

    public KeyValueGrid(string keyHint, string valueHint)
    {
        this.keyHint = keyHint;
        this.valueHint = valueHint;
        Classes.Add("framed");
        ClipToBounds = true;
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions(Columns) };
        string[] titles = ["", "Key", "Value"];
        for (int i = 0; i < titles.Length; i++)
        {
            TextBlock title = new() { Text = titles[i], Classes = { "muted" }, Margin = new Thickness(8, 5) };
            Grid.SetColumn(title, i);
            header.Children.Add(title);
        }
        Border headerFrame = new() { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerFrame.Bind(BorderBrushProperty, headerFrame.GetResourceObservable(TokenKeys.Separator));
        DockPanel.SetDock(headerFrame, Dock.Top);
        Child = new DockPanel { Children = { headerFrame, new ScrollViewer { Content = table } } };
        AddEmpty();
    }

    /// <summary>A row was typed in, turned on or off, added or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>Every row, the empty last one included.</summary>
    public IReadOnlyList<Row> Rows => rows;

    /// <summary>The rows with a key or value, in order, each with whether it is on.</summary>
    public IReadOnlyList<(bool On, string Key, string Value)> Pairs =>
        [.. rows.Where(r => !r.IsEmpty).Select(r => (r.On.IsChecked == true, r.Key.Text?.Trim() ?? string.Empty, r.Value.Text?.Trim() ?? string.Empty))];

    /// <summary>The rows on, as sent.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Enabled =>
        [.. Pairs.Where(p => p.On).Select(p => new KeyValuePair<string, string>(p.Key, p.Value))];

    /// <summary>Replaces the rows, an empty one after them, without raising <see cref="Changed"/>.</summary>
    public void Set(IEnumerable<(bool On, string Key, string Value)> pairs)
    {
        setting = true;
        try
        {
            rows.Clear();
            table.Children.Clear();
            foreach ((bool on, string key, string value) in pairs)
            {
                Add(on, key, value);
            }
            AddEmpty();
        }
        finally
        {
            setting = false;
        }
    }

    private void AddEmpty() => Add(true, string.Empty, string.Empty);

    private Row Add(bool on, string key, string value)
    {
        CheckBox check = new() { IsChecked = on, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        TextBox keyBox = new() { Classes = { "cell" }, Text = key, FontFamily = CSharp.Fonts.Mono, FontSize = 12 };
        TextBox valueBox = new() { Classes = { "cell" }, Text = value, FontFamily = CSharp.Fonts.Mono, FontSize = 12 };
        Button remove = ToolButtons.Of(IconKeys.Close, "Remove");
        remove.VerticalAlignment = VerticalAlignment.Center;
        ((PathIcon)remove.Content!).Classes.Add("small");
        Grid cells = new() { ColumnDefinitions = new ColumnDefinitions(Columns) };
        Control[] parts = [check, keyBox, valueBox, remove];
        for (int i = 0; i < parts.Length; i++)
        {
            Grid.SetColumn(parts[i], i);
            cells.Children.Add(parts[i]);
        }
        Border view = new() { Child = cells, BorderThickness = new Thickness(0, 0, 0, 1) };
        view.Bind(BorderBrushProperty, view.GetResourceObservable(TokenKeys.Separator));
        Row row = new(check, keyBox, valueBox, remove, view);
        rows.Add(row);
        table.Children.Add(view);
        Mark(row);
        keyBox.TextChanged += (_, _) => Typed(row);
        valueBox.TextChanged += (_, _) => Typed(row);
        check.IsCheckedChanged += (_, _) => Raise();
        remove.Click += (_, _) =>
        {
            rows.Remove(row);
            table.Children.Remove(view);
            Raise();
        };
        return row;
    }

    /// <summary>Typing in the empty last row makes it a row of its own, and another empty one follows it.</summary>
    private void Typed(Row row)
    {
        if (setting)
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
        Raise();
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

    private void Raise()
    {
        if (!setting)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
