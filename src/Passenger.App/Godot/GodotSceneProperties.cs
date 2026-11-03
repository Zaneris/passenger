using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Passenger.App.LiveTree;
using Passenger.Editor;
using Passenger.Inspector;

namespace Passenger.App.Godot;

/// <summary>The properties of the node chosen in a scene's outline, under the outline: its name and class over a row for
/// each property, what the scene sets first, then its script's exports and its class's properties at their defaults, each
/// with where its value comes from. A value is edited with the Live Visual Tree's own editors (<see cref="GodotValueCells"/>)
/// and written into the scene once it settles (<see cref="GodotSceneValues"/>); one they cannot edit shows its text.</summary>
public sealed class GodotSceneProperties : DockPanel, IGodotTuner
{
    private readonly TextBlock heading = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 6, 8, 4) };
    private readonly ListBox list = new() { Classes = { StyleClasses.Tree } };
    private readonly Action<Control, Control> showPopup;
    private readonly Action closePopup;
    private readonly Action<string> say;
    private GodotValueCells? cells;
    private event Action? Refreshed;

    public GodotSceneProperties(Action<Control, Control> showPopup, Action closePopup, Action<string> say)
    {
        this.showPopup = showPopup;
        this.closePopup = closePopup;
        this.say = say;
        SetDock(heading, Dock.Top);
        Children.Add(heading);
        list.ItemsSource = Rows;
        ReusedRows.Use(list, Row);
        Children.Add(list);
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Panel));
    }

    /// <summary>The node shown, or null for none.</summary>
    public GodotSceneValues? Shown { get; private set; }

    public ObservableCollection<GodotSceneProperty> Rows { get; } = [];

    /// <summary>Shows a node's properties, or none.</summary>
    public void Show(GodotSceneValues? values)
    {
        if (Shown is { } before)
        {
            before.Changed -= ValuesChanged;
        }
        Shown = values;
        cells = values is null ? null : new GodotValueCells(values, this);
        if (values is not null)
        {
            values.Changed += ValuesChanged;
        }
        heading.Text = values is null ? null : values.Type is { } type ? $"{values.Name}: {type}" : values.Name;
        Fill();
    }

    /// <summary>The scene's text changed elsewhere (typed, an Undo): the rows show what it says now.</summary>
    public void ReadAgain() => Shown?.ReadAgain();

    /// <summary>The editor a property's row shows, scrolled into view, for a test to drive; null where its row shows only text.</summary>
    public Control? EditorOf(string name)
    {
        int at = Rows.ToList().FindIndex(r => r.Property.Name == name);
        if (at < 0)
        {
            return null;
        }
        list.ScrollIntoView(at);
        list.UpdateLayout();
        return list.GetLogicalDescendants().OfType<Grid>().FirstOrDefault(g => g.DataContext is GodotSceneProperty row && row.Property.Name == name)?.Tag is GodotValueCell cell
            && cell.Editor.IsVisible ? cell.ShownEditor : null;
    }

    private void ValuesChanged(object? sender, EventArgs e) => Fill();

    private void Fill()
    {
        IReadOnlyList<GodotSceneProperty> now = Shown?.Properties ?? [];
        if (Rows.Count == now.Count && Rows.Select(r => r.Property.Name).SequenceEqual(now.Select(r => r.Property.Name)))
        {
            for (int i = 0; i < now.Count; i++)
            {
                // A row replaced is made again, closing what it had open: only a row that changed is.
                if (!Equals(Rows[i], now[i]))
                {
                    Rows[i] = now[i];
                }
            }
            Refreshed?.Invoke();
            return;
        }
        Rows.Clear();
        foreach (GodotSceneProperty row in now)
        {
            Rows.Add(row);
        }
    }

    /// <summary>A property's row: its name, its value's editor where one fits it (else its text), and where it comes from.</summary>
    private Control Row()
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("8*,12*"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(8, 3) };
        TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        TextBlock text = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        Grid.SetColumn(text, 1);
        TextBlock source = new() { Classes = { StyleClasses.Muted }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(source, 1);
        Grid.SetColumnSpan(source, 2);
        grid.Children.Add(label);
        grid.Children.Add(text);
        grid.Children.Add(source);
        (GodotValueCells From, LiveValueCell Cell)? made = null;
        Refreshed += () =>
        {
            if (grid.DataContext is GodotSceneProperty shown)
            {
                Show(shown);
            }
        };
        return grid.Showing<GodotSceneProperty>(Show);

        void Show(GodotSceneProperty row)
        {
            label.Text = row.Property.Name;
            ToolTip.SetTip(label, $"{row.Property.Name} ({GodotVariant.TypeName(row.Property.Type)}, {row.Property.Category})");
            text.Text = row.Text;
            ToolTip.SetTip(text, row.Text);
            source.Text = row.Source;
            label.FontWeight = row.Set ? FontWeight.SemiBold : FontWeight.Normal;
            bool edits = false;
            if (cells is { } given && Shown is not null)
            {
                if (made is not { } kept || kept.From != given)
                {
                    if (made is { } old)
                    {
                        grid.Children.Remove(old.Cell.Editor);
                    }
                    LiveValueCell cell = given.Make();
                    Grid.SetColumn(cell.Editor, 1);
                    grid.Children.Add(cell.Editor);
                    grid.Tag = cell;
                    made = (given, cell);
                }
                (edits, _) = made.Value.Cell.Show(0, new PropertyValue(row.Property.Name, row.Property.Category, false, GodotVariant.TypeName(row.Property.Type), row.Text, row.Source, null, null));
            }
            if (made is { } shownCell)
            {
                shownCell.Cell.Editor.IsVisible = edits;
            }
            text.IsVisible = !edits;
        }
    }

    void IGodotTuner.Settled(long element) => Shown?.ReadAgain();

    void IGodotTuner.Keep(GodotTuned tuned, Control from)
    {
    }

    void IGodotTuner.ShowPopup(Control popup, Control from) => showPopup(popup, from);

    void IGodotTuner.ClosePopup() => closePopup();

    void IGodotTuner.Say(string words) => say(words);
}
