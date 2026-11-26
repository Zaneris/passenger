using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Passenger.App.LiveTree;
using Passenger.App.ToolWindows;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Xaml.Features;

namespace Passenger.App.WinForms;

/// <summary>A row of a component's Properties or Events: the property (or a part of one, at a depth under it) or the event it
/// lists, and whether its parts are shown under it.</summary>
public sealed class FormPropertyRow(DesignProperty? property, DesignEvent? raised, int depth, bool expanded) : ITreeRow
{
    public DesignProperty? Property { get; } = property;

    public DesignEvent? Event { get; } = raised;

    /// <summary>The property's dotted name (a part's after its value's), or the event's.</summary>
    public string Name => Property?.Name ?? Event!.Name;

    /// <summary>The name as the property grid shows it: an extender's as its provider names it, a part's alone, in
    /// parentheses where its type says, as the component's own (Name).</summary>
    public string Shown => Property is { Shown: { } named } ? named
        : Property is { Parenthesized: true } ? $"({Name[(Name.LastIndexOf('.') + 1)..]})" : Name[(Name.LastIndexOf('.') + 1)..];

    /// <summary>Whether it is the component's name, which a rename of its field changes.</summary>
    public bool Naming => Property is { Name: "Name", Parenthesized: true };

    public int Depth { get; } = depth;

    public bool Expands => Property?.Parts is { Count: > 0 };

    public bool IsExpanded { get; } = expanded;

    /// <summary>Whether it shows what another row shows: a row made again for the same is the same row.</summary>
    public bool Alike(FormPropertyRow other) => Depth == other.Depth && IsExpanded == other.IsExpanded && Event == other.Event && Same(Property, other.Property);

    private static bool Same(DesignProperty? one, DesignProperty? other) => one is null || other is null ? one == other
        : one with { Choices = null, Flags = null, Parts = null } == other with { Choices = null, Flags = null, Parts = null }
            && (one.Choices ?? []).SequenceEqual(other.Choices ?? []) && (one.Flags ?? []).SequenceEqual(other.Flags ?? [])
            && (one.Parts ?? []).Select(p => (p.Name, p.Text, p.Set)).SequenceEqual((other.Parts ?? []).Select(p => (p.Name, p.Text, p.Set)));
}

/// <summary>The properties or the events of the component chosen in a form's design, under the Structure window's outline of
/// its components, as Windows Forms' property grid lists them: its name and type over a row for each by name, what the code
/// sets in bold, its category under it, a value of parts (a Point, a Font, FlatAppearance) opening to them. A value is typed
/// in its row (Enter, Enter again writing it), chosen from its list, turned on and off flag by flag (Anchor), picked as a
/// colour, a list of words edited a line an item, a collection given an item of a type it takes; Reset in a row's menu puts
/// it back to its default. An event's row hooks a handler, written into the form's code where it declares none, and goes
/// to it, or one typed by name; Remove Handler unhooks it. Each change asked of the window as an edit of the design.</summary>
public sealed class FormProperties : DockPanel
{
    private const string PropertiesKey = "properties";
    private const string EventsKey = "events";

    private readonly TextBlock heading = new() { Classes = { StyleClasses.PaneTitle }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Segments<string> kinds = new();
    private readonly TreeList list = new() { Classes = { StyleClasses.Tree } };
    private readonly EmptyState placeholder = new();
    private readonly Action<Control, Control> showPopup;
    private readonly Action closePopup;

    /// <summary>The values whose parts are shown, by their dotted names, kept as the rows are read again.</summary>
    private readonly HashSet<string> open = new(StringComparer.Ordinal);
    private string? typing;
    private bool showing;
    private event Action? Refreshed;

    /// <param name="showPopup">Shows a popup beside the control that opened it (the colour picker).</param>
    public FormProperties(Action<Control, Control> showPopup, Action closePopup)
    {
        this.showPopup = showPopup;
        this.closePopup = closePopup;
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
        TreeKeys.Attach<FormPropertyRow>(list, Toggle, row =>
        {
            Open(row);
            return true;
        });
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

    /// <summary>A property (its dotted name: a part of one too) given the value its text says.</summary>
    public event EventHandler<(string Property, string Text)>? SetRequested;

    /// <summary>A property put back to its default.</summary>
    public event EventHandler<string>? ResetRequested;

    /// <summary>An event's handler hooked (the one named, else the one hooked or the designer's name for it) and gone to.</summary>
    public event EventHandler<(string Event, string? Handler)>? HookRequested;

    public event EventHandler<string>? UnhookRequested;

    /// <summary>The component renamed: its field, through a rename of the code.</summary>
    public event EventHandler<string>? RenameRequested;

    /// <summary>A list of words given whole, a line an item.</summary>
    public event EventHandler<(string Property, string Lines)>? ItemsRequested;

    /// <summary>An item of a type (its full name with its assembly's) added to a collection.</summary>
    public event EventHandler<(string Property, string Type)>? AddItemRequested;

    /// <summary>The popup a list of words' items are edited in, while it is open.</summary>
    public ItemsPopup? EditingItems { get; private set; }

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

    /// <summary>The editor a row shows in its value's place, for a test to drive: its list, its flags' toggles, its colour's
    /// button; null where it shows words.</summary>
    public Control? EditorOf(string name)
    {
        if (Rows.FirstOrDefault(r => r.Name == name) is not { } row)
        {
            return null;
        }
        list.ScrollIntoView(row);
        list.UpdateLayout();
        return list.GetLogicalDescendants().OfType<PropertyRow>().FirstOrDefault(g => g.DataContext == row)?.Tag is Control[] editors
            ? editors.FirstOrDefault(e => e.IsVisible)
            : null;
    }

    /// <summary>Chooses a property's or an event's row by its (dotted) name, scrolled into view.</summary>
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

    /// <summary>Shows a value's parts under it, or hides them.</summary>
    public void Toggle(FormPropertyRow row)
    {
        if (!row.Expands)
        {
            return;
        }
        if (!open.Remove(row.Name))
        {
            open.Add(row.Name);
        }
        Fill();
    }

    private void DesignChanged(object? sender, EventArgs e) => Fill();

    /// <summary>A row opened: an event's handler hooked and gone to; a property's value typed; a value of parts opened.</summary>
    private void Open(FormPropertyRow row)
    {
        if (row.Event is { } raised)
        {
            HookRequested?.Invoke(this, (raised.Name, null));
        }
        else if (row.Property is { ReadOnly: false })
        {
            typing = row.Name;
            Refreshed?.Invoke();
        }
        else
        {
            Toggle(row);
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
        List<FormPropertyRow> now = [];
        if (design is not null && ShowsEvents)
        {
            now.AddRange(design.Events.Select(e => new FormPropertyRow(null, e, 0, false)));
        }
        else if (design is not null)
        {
            Add(design.Properties.OrderBy(p => p.Parenthesized ? 0 : 1), 0);
        }
        string? chosen = Chosen?.Name;
        if (Rows.Count == now.Count && Rows.Select(r => r.Name).SequenceEqual(now.Select(r => r.Name)))
        {
            for (int i = 0; i < now.Count; i++)
            {
                // A row whose value is being typed keeps its box, and what is typed in it, until Enter or Escape.
                if (!Rows[i].Alike(now[i]) && Rows[i].Name != typing)
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

        void Add(IEnumerable<DesignProperty> properties, int depth)
        {
            foreach (DesignProperty property in properties)
            {
                bool expanded = open.Contains(property.Name) && property.Parts is { Count: > 0 };
                now.Add(new FormPropertyRow(property, null, depth, expanded));
                if (expanded)
                {
                    Add(property.Parts!, depth + 1);
                }
            }
        }
    }

    /// <summary>A row: its chevron where its value has parts, its name (bold where the code sets it), its value's words or
    /// the editor fit for it (a list of choices, the flags' toggles, a colour's swatch, the box it is typed in), and its
    /// category under them; an event's handler as its value.</summary>
    private Control Row()
    {
        PropertyRow grid = new();
        RowExpander chevron = RowExpander.Of<FormPropertyRow>(Toggle);
        DockPanel.SetDock(chevron, Avalonia.Controls.Dock.Left);
        grid.Named.Children.Insert(0, chevron);
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
        WrapPanel flags = new() { IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        grid.InValue(flags);
        Border swatch = new() { Classes = { StyleClasses.Box }, Width = 22, Height = 14, BorderThickness = Hairlines.Round };
        swatch.Bind(Border.BorderBrushProperty, swatch.GetResourceObservable(TokenKeys.FieldBorder));
        TextBlock named = new() { Classes = { StyleClasses.Code }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Button colour = new()
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
            Padding = new Thickness(Gaps.Tight, Gaps.Hair, Gaps.Normal, Gaps.Hair),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Small, Children = { swatch, named } },
        };
        ToolTip.SetTip(colour, "Pick a colour: the form's code is written once one is chosen");
        colour.Click += (_, _) =>
        {
            if (grid.DataContext is FormPropertyRow { Property: { Argb: uint argb } property })
            {
                Pick(colour, property, argb);
            }
        };
        grid.InValue(colour);
        Button items = new() { Content = "Edit Items…", HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
        items.Click += (_, _) =>
        {
            if (grid.DataContext is FormPropertyRow { Property: { Lines: { } lines } property })
            {
                EditItems(items, property, lines);
            }
        };
        grid.InValue(items);
        ToolDropDown adds = new("Add an item") { HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };
        ReusedRows.Use(adds, () =>
        {
            TextBlock kind = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            return kind.Showing<string>(type => kind.Text = ShortName(type));
        });
        adds.SelectionChanged += (_, _) =>
        {
            if (!showing && grid.DataContext is FormPropertyRow { Property: { } property } && adds.SelectedItem is string type)
            {
                showing = true;
                adds.SelectedItem = null;
                showing = false;
                AddItemRequested?.Invoke(this, (property.Name, type));
            }
        };
        grid.InValue(adds);
        grid.Tag = new Control[] { choices, flags, colour, items, adds };
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
            chevron.Expands = row.Expands;
            chevron.IsOpen = row.IsExpanded;
            grid.Named.Margin = new Thickness(row.Depth * Gaps.Indent, 0, Gaps.Normal, 0);
            grid.Label.Text = row.Shown;
            grid.Label.Classes.Set(StyleClasses.Strong, row.Property?.Set == true || row.Event?.Handler is not null);
            string text = row.Property?.Text ?? row.Event?.Handler ?? string.Empty;
            grid.Value.Text = text;
            ToolTip.SetTip(grid.Label, row.Property?.Description ?? row.Event?.Description);
            ToolTip.SetTip(grid.Value, text.Length > 0 ? text : null);
            grid.Source.Text = row.Property is { } property ? $"{property.Category} · {property.Type}" : row.Event!.Category;
            bool typed = typing == row.Name;
            bool editable = !typed && row.Property is { ReadOnly: false };
            bool listed = editable && row.Property is { Flags: null, Argb: null, Choices.Count: > 0, Exclusive: true };
            if (listed)
            {
                choices.ItemsSource = row.Property!.Choices;
                choices.SelectedItem = row.Property.Choices!.FirstOrDefault(c => c == row.Property.Text);
            }
            choices.IsVisible = listed;
            bool flagged = editable && row.Property is { Flags.Count: > 0 and <= 8 };
            if (flagged)
            {
                ShowFlags(flags, row.Property!);
            }
            flags.IsVisible = flagged;
            bool coloured = editable && row.Property is { Argb: not null };
            if (coloured)
            {
                uint argb = row.Property!.Argb!.Value;
                swatch.Background = new SolidColorBrush(Color.FromUInt32(argb));
                named.Text = text;
            }
            colour.IsVisible = coloured;
            bool lined = !typed && row.Property is { Lines: not null };
            items.IsVisible = lined;
            bool adding = !typed && row.Property is { Adds.Count: > 0 };
            if (adding)
            {
                adds.ItemsSource = row.Property!.Adds;
                adds.SelectedItem = null;
            }
            adds.IsVisible = adding;
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
            grid.Value.IsVisible = !typed && !listed && !flagged && !coloured && !lined && !adding;
            grid.SplitFor(editor: listed || typed || flagged || coloured || lined || adding);
            grid.Value.Classes.Set(StyleClasses.Muted, row.Property is { ReadOnly: true });
            showing = false;
        }
    }

    /// <summary>A flags value's toggles, one a flag, each on where the value holds it: a press writes the value with that
    /// flag turned over, None where none is left.</summary>
    private void ShowFlags(WrapPanel flags, DesignProperty property)
    {
        HashSet<string> held = [.. property.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        while (flags.Children.Count < property.Flags!.Count)
        {
            ToggleButton made = Segments.Toggle(string.Empty);
            made.IsCheckedChanged += (_, _) =>
            {
                if (!showing && flags.DataContext is FormPropertyRow { Property: { } now } && made.Content is string flag)
                {
                    HashSet<string> turned = [.. now.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(f => f != "None")];
                    if (made.IsChecked == true)
                    {
                        turned.Add(flag);
                    }
                    else
                    {
                        turned.Remove(flag);
                    }
                    SetRequested?.Invoke(this, (now.Name, turned.Count == 0 ? "None" : string.Join(", ", now.Flags!.Where(turned.Contains))));
                }
            };
            flags.Children.Add(made);
        }
        for (int i = 0; i < flags.Children.Count; i++)
        {
            ToggleButton toggle = (ToggleButton)flags.Children[i];
            toggle.IsVisible = i < property.Flags.Count;
            if (toggle.IsVisible)
            {
                toggle.Content = property.Flags[i];
                toggle.IsChecked = held.Contains(property.Flags[i]);
            }
        }
    }

    /// <summary>A list of words' items edited as text in a popup over its row's button, the lines given written as the list.</summary>
    private void EditItems(Control from, DesignProperty property, IReadOnlyList<string> lines)
    {
        ItemsPopup popup = new(property.Name, lines);
        popup.Chosen += (_, given) =>
        {
            EditingItems = null;
            closePopup();
            if (given is not null)
            {
                ItemsRequested?.Invoke(this, (property.Name, given));
            }
        };
        EditingItems = popup;
        showPopup(popup, from);
    }

    /// <summary>A type's name alone, without its namespace or assembly.</summary>
    private static string ShortName(string type)
    {
        string full = type.Split(',')[0];
        return full[(full.LastIndexOfAny(['.', '+']) + 1)..];
    }

    /// <summary>The colour picker over a colour's swatch: the one chosen written as its red, green and blue (and alpha where
    /// it is not opaque), as the type converter reads a colour.</summary>
    private void Pick(Control from, DesignProperty property, uint argb)
    {
        Argb start = new((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        ColourPicker picker = new(start);
        picker.Chosen += (_, chosen) =>
        {
            closePopup();
            if (chosen is { } taken)
            {
                string text = taken.A == 255
                    ? string.Create(CultureInfo.InvariantCulture, $"{taken.R}{Separator} {taken.G}{Separator} {taken.B}")
                    : string.Create(CultureInfo.InvariantCulture, $"{taken.A}{Separator} {taken.R}{Separator} {taken.G}{Separator} {taken.B}");
                SetRequested?.Invoke(this, (property.Name, text));
            }
        };
        showPopup(picker, from);
    }

    /// <summary>The list separator a colour's channels are written apart with, as its converter reads them in the person's culture.</summary>
    private static string Separator => CultureInfo.CurrentCulture.TextInfo.ListSeparator;
}
