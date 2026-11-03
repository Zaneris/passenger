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
        list.DoubleTapped += (_, _) => EditText();
        list.KeyDown += (_, e) =>
        {
            if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.F2 && typing is null)
            {
                e.Handled = true;
                EditText();
            }
        };
        Children.Add(list);
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Panel));
    }

    /// <summary>The node shown, or null for none.</summary>
    public GodotSceneValues? Shown { get; private set; }

    /// <summary>The property whose value is being typed as text, or null.</summary>
    private string? typing;

    /// <summary>The chosen row's value opened as text to type (an array's, a dictionary's, a resource's, any value), Enter
    /// writing it as Godot's variant text and Escape leaving it.</summary>
    public void EditText()
    {
        if (list.SelectedItem is GodotSceneProperty row && Shown is not null)
        {
            typing = row.Property.Name;
            Refreshed?.Invoke();
        }
    }

    /// <summary>Chooses a property's row, scrolled into view.</summary>
    public void Choose(string name)
    {
        if (Rows.FirstOrDefault(r => r.Property.Name == name) is { } row)
        {
            list.SelectedItem = row;
            list.ScrollIntoView(row);
        }
    }

    /// <summary>The box a row's value is typed in, while one is: the row's own, not a value editor's.</summary>
    public TextBox? Typing => list.GetLogicalDescendants().OfType<TextBox>()
        .FirstOrDefault(t => t.Tag == TypingBox && t.IsEffectivelyVisible && (t.DataContext as GodotSceneProperty)?.Property.Name == typing);

    /// <summary>What marks a row's box a value is typed in.</summary>
    private static readonly object TypingBox = new();

    /// <summary>The row chosen, or null.</summary>
    public GodotSceneProperty? Chosen => list.SelectedItem as GodotSceneProperty;

    private async Task TypedAsync(string property, string written)
    {
        typing = null;
        Refreshed?.Invoke();
        if (Shown is not { } values)
        {
            return;
        }
        try
        {
            await values.SetTextAsync(property, written);
        }
        catch (LiveAppException refused)
        {
            say($"{property} was not set: {refused.Message}");
        }
    }

    public ObservableCollection<GodotSceneProperty> Rows { get; } = [];

    /// <summary>Shows a node's properties, or none.</summary>
    public void Show(GodotSceneValues? values)
    {
        if (Shown is { } before)
        {
            before.Changed -= ValuesChanged;
        }
        if (values is null || Shown is null || values.File != Shown.File || values.Node != Shown.Node)
        {
            typing = null;
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
        string? chosen = Chosen?.Property.Name;
        if (Rows.Count == now.Count && Rows.Select(r => r.Property.Name).SequenceEqual(now.Select(r => r.Property.Name)))
        {
            for (int i = 0; i < now.Count; i++)
            {
                // A row replaced is made again, closing what it had open: only a row that shows something else is.
                if (!Alike(Rows[i], now[i]))
                {
                    Rows[i] = now[i];
                }
            }
            Refreshed?.Invoke();
        }
        else
        {
            Rows.Clear();
            foreach (GodotSceneProperty row in now)
            {
                Rows.Add(row);
            }
        }
        // The row chosen stays chosen as the rows are read again (a write, the scene read anew).
        if (chosen is not null && Chosen?.Property.Name != chosen && Rows.FirstOrDefault(r => r.Property.Name == chosen) is { } again)
        {
            list.SelectedItem = again;
            list.ScrollIntoView(again);
        }
    }

    /// <summary>Whether two rows of a property show the same: its value's text, where it comes from, and how it is edited.</summary>
    private static bool Alike(GodotSceneProperty one, GodotSceneProperty other) =>
        one.Text == other.Text && one.Source == other.Source && one.Set == other.Set && one.Base == other.Base && one.Rank == other.Rank
        && one.Property with { Value = null } == other.Property with { Value = null };

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
        TextBox box = new TextBox { FontSize = 12, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Tag = TypingBox }.InCodeFont();
        Grid.SetColumn(box, 1);
        box.KeyDown += (_, e) =>
        {
            if (grid.DataContext is not GodotSceneProperty shown || e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Escape))
            {
                return;
            }
            e.Handled = true;
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                IdeErrors.Observe(TypedAsync(shown.Property.Name, box.Text ?? string.Empty));
            }
            else
            {
                typing = null;
                Refreshed?.Invoke();
            }
            list.Focus();
        };
        grid.Children.Add(label);
        grid.Children.Add(text);
        grid.Children.Add(source);
        grid.Children.Add(box);
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
            bool typed = typing == row.Property.Name;
            if (typed && !box.IsVisible)
            {
                box.Text = row.Text;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.SelectAll();
                });
            }
            box.IsVisible = typed;
            if (made is { } shownCell)
            {
                shownCell.Cell.Editor.IsVisible = edits && !typed;
            }
            text.IsVisible = !edits && !typed;
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
