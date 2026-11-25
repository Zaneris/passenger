using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Passenger.App.LiveTree;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.WinForms;

/// <summary>A row of a component's Properties or Events: the property or the event it lists.</summary>
public sealed record FormPropertyRow(DesignProperty? Property, DesignEvent? Event)
{
    public string Name => Property?.Name ?? Event!.Name;

    /// <summary>The name as the property grid shows it: in parentheses where its type says, as the component's own (Name).</summary>
    public string Shown => Property is { Parenthesized: true } ? $"({Name})" : Name;

    /// <summary>Whether it is the component's name, which a rename of its field changes.</summary>
    public bool Naming => Property is { Name: "Name", Parenthesized: true };
}

/// <summary>The properties or the events of the component chosen in a form's design, under the Structure window's outline of
/// its components, as Windows Forms' property grid lists them: its name and type over a row for each by name, what the code
/// sets in bold, its category under it. A value is typed in its row (Enter or F2, Enter writing it as its type converter
/// reads it) or chosen from its list where it offers one; Reset in a row's menu puts it back to its default. An event's
/// row hooks a handler, written into the form's code where it declares none, and goes to it (Enter or a double click),
/// or one typed by name; Remove Handler unhooks it. Each change asked of the window (<see cref="SetRequested"/> and the
/// rest) as an edit of the design.</summary>
public sealed class FormProperties : DockPanel
{
    private const string PropertiesKey = "properties";
    private const string EventsKey = "events";

    private readonly TextBlock heading = new() { Classes = { StyleClasses.PaneTitle }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Segments<string> kinds = new();
    private readonly ListBox list = new() { Classes = { StyleClasses.Tree } };
    private readonly EmptyState placeholder = new();
    private string? typing;
    private bool showing;
    private event Action? Refreshed;

    public FormProperties()
    {
        kinds.Add(PropertiesKey, "Properties");
        kinds.Add(EventsKey, "Events");
        kinds.Choice = PropertiesKey;
        kinds.Chosen += (_, _) => Fill();
        kinds.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        DockPanel top = new() { Margin = new Thickness(Gaps.Normal, Gaps.Small, Gaps.Normal, Gaps.Tight), Children = { kinds, heading } };
        SetDock(kinds, Avalonia.Controls.Dock.Right);
        SetDock(top, Avalonia.Controls.Dock.Top);
        Children.Add(top);
        list.ItemsSource = Rows;
        ReusedRows.Use(list, Row);
        ListKeys.Act(list, new ListActs { Open = Open, Edit = Type });
        ListMenus.Attach(list, () => list.SelectedItem is not FormPropertyRow chosen ? [] : chosen.Property is { } property
            ?
            [
                ListMenus.Item("Reset", () => ResetRequested?.Invoke(this, property.Name), property.Set && !property.ReadOnly ? null : Refusals.PropertyAtDefault),
                ListMenus.Item("Copy Value", () => IdeErrors.Observe(ListMenus.CopyAsync(this, property.Text))),
            ]
            :
            [
                ListMenus.Item("Go to Handler", () => HookRequested?.Invoke(this, (chosen.Event!.Name, null))),
                ListMenus.Item("Remove Handler", () => UnhookRequested?.Invoke(this, chosen.Event!.Name), chosen.Event!.Handler is null ? Refusals.NoHandler : null),
            ]);
        Children.Add(new Panel { Children = { list, placeholder } });
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Panel));
        Show(null);
    }

    /// <summary>A property given the value its text says.</summary>
    public event EventHandler<(string Property, string Text)>? SetRequested;

    /// <summary>A property put back to its default.</summary>
    public event EventHandler<string>? ResetRequested;

    /// <summary>An event's handler hooked (the one named, else the one hooked or the designer's name for it) and gone to.</summary>
    public event EventHandler<(string Event, string? Handler)>? HookRequested;

    public event EventHandler<string>? UnhookRequested;

    /// <summary>The component renamed: its field, through a rename of the code.</summary>
    public event EventHandler<string>? RenameRequested;

    public ObservableCollection<FormPropertyRow> Rows { get; } = [];

    /// <summary>The design shown, and its component inspected; none for none.</summary>
    public FormDesign? Shown { get; private set; }

    /// <summary>Whether the rows are the events, rather than the properties.</summary>
    public bool ShowsEvents
    {
        get => kinds.Choice == EventsKey;
        set
        {
            kinds.Choice = value ? EventsKey : PropertiesKey;
            Fill();
        }
    }

    /// <summary>What the pane says in place of rows; null while it lists some.</summary>
    public string? Said => placeholder.IsVisible ? placeholder.Text : null;

    /// <summary>The row chosen, or null.</summary>
    public FormPropertyRow? Chosen => list.SelectedItem as FormPropertyRow;

    /// <summary>The box a row's value is typed in, while one is.</summary>
    public TextBox? Typing => list.GetLogicalDescendants().OfType<TextBox>()
        .FirstOrDefault(t => t.IsEffectivelyVisible && (t.DataContext as FormPropertyRow)?.Name == typing);

    /// <summary>Chooses a property's or an event's row by its name, scrolled into view.</summary>
    public void Choose(string name)
    {
        if (Rows.FirstOrDefault(r => r.Name == name) is { } row)
        {
            list.SelectedItem = row;
            list.ScrollIntoView(row);
        }
    }

    /// <summary>Shows the inspected component of a design, or none.</summary>
    public void Show(FormDesign? design)
    {
        if (Shown is { } before)
        {
            before.Changed -= DesignChanged;
        }
        Shown = design;
        if (design is not null)
        {
            design.Changed += DesignChanged;
        }
        Fill();
    }

    private void DesignChanged(object? sender, EventArgs e) => Fill();

    /// <summary>The chosen row opened: an event's handler hooked and gone to; a property's value typed.</summary>
    private void Open()
    {
        if (Chosen is { Event: { } raised })
        {
            HookRequested?.Invoke(this, (raised.Name, null));
            return;
        }
        Type();
    }

    /// <summary>The chosen row's value opened to type: Enter writes it, Escape leaves it.</summary>
    private void Type()
    {
        if (Chosen is { } row && (row.Event is not null || row.Property is { ReadOnly: false }))
        {
            typing = row.Name;
            Refreshed?.Invoke();
        }
    }

    private void Typed(FormPropertyRow row, string text)
    {
        typing = null;
        Refreshed?.Invoke();
        if (row.Naming)
        {
            RenameRequested?.Invoke(this, text.Trim());
        }
        else if (row.Event is { } raised)
        {
            HookRequested?.Invoke(this, (raised.Name, text.Trim().Length == 0 ? null : text.Trim()));
        }
        else
        {
            SetRequested?.Invoke(this, (row.Name, text));
        }
    }

    private void Fill()
    {
        FormDesign? design = Shown;
        DesignedBox? box = design?.Inspected is { } field ? design.Components.FirstOrDefault(c => c.Field == field) : null;
        heading.Text = box is null ? null : $"{(box.Field.Length == 0 ? design!.Files.Class[(design.Files.Class.LastIndexOfAny(['.', '+']) + 1)..] : box.Field)}: {box.Type}";
        List<FormPropertyRow> now = design is null ? [] : ShowsEvents
            ? [.. design.Events.Select(e => new FormPropertyRow(null, e))]
            : [.. design.Properties.Select(p => new FormPropertyRow(p, null)).OrderBy(r => r.Property!.Parenthesized ? 0 : 1)];
        string? chosen = Chosen?.Name;
        if (Rows.Count == now.Count && Rows.Select(r => r.Name).SequenceEqual(now.Select(r => r.Name)))
        {
            for (int i = 0; i < now.Count; i++)
            {
                if (Rows[i] != now[i])
                {
                    Rows[i] = now[i];
                }
            }
            Refreshed?.Invoke();
        }
        else
        {
            typing = null;
            Rows.Clear();
            foreach (FormPropertyRow row in now)
            {
                Rows.Add(row);
            }
        }
        if (chosen is not null && Chosen?.Name != chosen && Rows.FirstOrDefault(r => r.Name == chosen) is { } again)
        {
            list.SelectedItem = again;
        }
        placeholder.Text = design is null || design.Inspected is null ? Refusals.NoComponentChosen
            : Rows.Count == 0 ? (design.Running ? "Reading its properties…" : design.Status) : null;
        placeholder.IsVisible = placeholder.Text is not null;
        list.IsVisible = !placeholder.IsVisible;
    }

    /// <summary>A row: its name (bold where the code sets it), its value's words, the list it offers or the box it is typed
    /// in, and its category under them; an event's handler as its value.</summary>
    private Control Row()
    {
        PropertyRow grid = new();
        ToolDropDown choices = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 320, IsVisible = false };
        ReusedRows.Use(choices, () =>
        {
            TextBlock choice = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            return choice.Showing<string>(c => choice.Text = c);
        });
        grid.InValue(choices);
        choices.SelectionChanged += (_, _) =>
        {
            if (!showing && grid.DataContext is FormPropertyRow { Property: { } property } && choices.SelectedItem is string picked && picked != property.Text)
            {
                SetRequested?.Invoke(this, (property.Name, picked));
            }
        };
        TextBox box = grid.Box;
        box.KeyDown += (_, e) =>
        {
            if (grid.DataContext is not FormPropertyRow shown || e.Key is not (Key.Enter or Key.Escape))
            {
                return;
            }
            e.Handled = true;
            if (e.Key == Key.Enter)
            {
                Typed(shown, box.Text ?? string.Empty);
            }
            else
            {
                typing = null;
                Refreshed?.Invoke();
            }
            ListKeys.Hold(list);
        };
        Refreshed += () =>
        {
            if (grid.DataContext is FormPropertyRow shown)
            {
                Show(shown);
            }
        };
        return grid.Showing<FormPropertyRow>(Show);

        void Show(FormPropertyRow row)
        {
            showing = true;
            grid.Label.Text = row.Shown;
            grid.Label.Classes.Set(StyleClasses.Strong, row.Property?.Set == true || row.Event?.Handler is not null);
            string text = row.Property?.Text ?? row.Event?.Handler ?? string.Empty;
            grid.Value.Text = text;
            ToolTip.SetTip(grid.Label, row.Property?.Description ?? row.Event?.Description);
            ToolTip.SetTip(grid.Value, text.Length > 0 ? text : null);
            grid.Source.Text = row.Property is { } property ? $"{property.Category} · {property.Type}" : row.Event!.Category;
            bool typed = typing == row.Name;
            bool listed = !typed && row.Property is { ReadOnly: false, Choices.Count: > 0, Exclusive: true };
            if (listed)
            {
                choices.ItemsSource = row.Property!.Choices;
                choices.SelectedItem = row.Property.Choices!.FirstOrDefault(c => c == row.Property.Text);
            }
            choices.IsVisible = listed;
            if (typed && !box.IsVisible)
            {
                box.Text = text;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.SelectAll();
                });
            }
            box.IsVisible = typed;
            grid.Value.IsVisible = !typed && !listed;
            grid.SplitFor(editor: listed || typed);
            grid.Value.Classes.Set(StyleClasses.Muted, row.Property?.ReadOnly == true);
            showing = false;
        }
    }
}
