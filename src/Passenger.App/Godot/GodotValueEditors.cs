using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.LiveTree;
using Passenger.Editor;
using Passenger.Inspector;

namespace Passenger.App.Godot;

/// <summary>A running game's array (a packed one too) as its items, or a dictionary as its keys and values: a row each,
/// typed as its value reads (as the property's element type where it names one, else as the item's own, else as the text
/// reads), taken at Enter or as the field is left; Add makes one more, a row's remove takes it away. An item that is itself
/// an array or a dictionary opens its own form under the rows, and one that is an object its own properties. Each change
/// sends the whole value to the game (a nested item's through the form it is in), and the rows then show what it holds.</summary>
public sealed class GodotItemsEditor : Border
{
    private readonly GodotGame game;
    private readonly string name;
    private readonly GodotType type;
    private readonly GodotHint hint;
    private readonly IGodotTuner tuner;

    /// <summary>Sends a whole value to where it is held, answering what is held then.</summary>
    private readonly Func<object?, Task<object?>> send;

    /// <summary>Tells what holds the value that it has settled in the game.</summary>
    private readonly Action settled;

    // Clear of the scroll bar, which shows over the rows' right.
    private readonly StackPanel rows = new() { Spacing = 2, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock empty;
    private readonly List<TextBox> fields = [];
    private readonly Border slot = new() { IsVisible = false };

    /// <summary>The types an item (a dictionary's value) and a key are read as: Nil where any value is taken.</summary>
    private readonly GodotType itemType;
    private readonly GodotType keyType;

    /// <summary>What the container is typed to hold as the game sent it, which every value sent back keeps.</summary>
    private readonly GodotElement items;
    private readonly GodotElement keys;
    private object? shown;
    private bool sending;

    public GodotItemsEditor(GodotGame game, long element, GodotProperty property, GodotHint hint, IGodotTuner tuner)
        : this(game, property.Name, property.Type, property.Value, hint, tuner, async value =>
        {
            await game.SetValueAsync(element, property.Name, value);
            GodotObjectState state = await game.InspectAsync((ulong)element);
            return state.Properties.FirstOrDefault(p => p.Name == property.Name)?.Value ?? value;
        }, () => tuner.Settled(element))
    {
    }

    /// <summary>The form of a value held somewhere <paramref name="send"/> sends it to: a property's, or an item's of another form.</summary>
    private GodotItemsEditor(GodotGame game, string name, GodotType type, object? value, GodotHint hint, IGodotTuner tuner, Func<object?, Task<object?>> send, Action settled)
    {
        this.game = game;
        this.tuner = tuner;
        this.hint = hint;
        this.send = send;
        this.settled = settled;
        this.name = name;
        this.type = type;
        (items, keys) = value switch
        {
            GodotArray array => (array.Element, default),
            GodotDictionary dictionary => (dictionary.ValueType, dictionary.KeyType),
            _ => (GodotElement.Of(hint.Element), default),
        };
        itemType = hint.Element != GodotType.Nil ? hint.Element : items.Kind == 0 ? GodotType.Nil : items.Type;
        keyType = hint.Key != GodotType.Nil ? hint.Key : keys.Kind == 0 ? GodotType.Nil : keys.Type;
        Classes.Add(StyleClasses.Popup);
        Width = PopupFrame.Narrow;
        Padding = new Thickness(8);
        empty = new TextBlock { Classes = { StyleClasses.EmptyText }, Text = Entries ? "No entries" : "No items" };
        Button add = new() { Classes = { StyleClasses.Compact }, Content = Entries ? "Add Entry" : "Add Item", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => IdeErrors.Observe(AddAsync());
        Add = add;
        string of = itemType == GodotType.Nil && keyType == GodotType.Nil ? GodotVariant.TypeName(type)
            : Entries ? $"Dictionary[{Named(keyType, keys)}, {Named(itemType, items)}]" : $"{type}[{Named(itemType, items)}]";
        Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Classes = { StyleClasses.PopupTitle }, Text = $"{name}: {of}", TextTrimming = TextTrimming.CharacterEllipsis },
                new ScrollViewer { MaxHeight = ListRow.Height * 12, Content = new Panel { Children = { rows, empty } } },
                add,
                slot,
            },
        };
        Show(value);

        static string Named(GodotType type, GodotElement typed) => typed.Name.Length > 0 ? typed.Name : GodotVariant.TypeName(type);
    }

    /// <summary>Whether it shows a dictionary's entries rather than an array's items.</summary>
    public bool Entries => hint.Editing == GodotEditing.Entries;

    /// <summary>Each row's fields as shown, in order: an item's value, or an entry's key then its value.</summary>
    public IReadOnlyList<TextBox> Fields => fields;

    /// <summary>The button that adds an item or an entry.</summary>
    public Button Add { get; }

    /// <summary>The value as the rows show it.</summary>
    public object? Value => shown;

    private IReadOnlyList<object?> Listed => GodotVariant.Items(shown) ?? [];

    private IReadOnlyList<KeyValuePair<object?, object?>> Pairs => shown is GodotDictionary dictionary ? dictionary.Entries : [];

    private void Show(object? value)
    {
        shown = value;
        rows.Children.Clear();
        fields.Clear();
        int count = Entries ? Pairs.Count : Listed.Count;
        for (int i = 0; i < count; i++)
        {
            rows.Children.Add(Entries ? EntryRow(i) : ItemRow(i));
        }
        empty.IsVisible = count == 0;
    }

    private Control ItemRow(int index)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("32,*,Auto") };
        TextBlock at = new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 12, Text = index.ToString(System.Globalization.CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        Control field = Opens(Listed[index], $"{name}[{index}]", held => Replaced(index, held)) is { } opens ? opens
            : Field(Listed[index], text => IdeErrors.Observe(SetItemAsync(index, text)));
        Grid.SetColumn(field, 1);
        Button remove = ToolButtons.Of(IconKeys.Close, $"Remove item {index}", () => IdeErrors.Observe(RemoveAsync(index)));
        Grid.SetColumn(remove, 2);
        row.Children.Add(at);
        row.Children.Add(field);
        row.Children.Add(remove);
        return row;
    }

    private Control EntryRow(int index)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("2*,3*,Auto") };
        KeyValuePair<object?, object?> entry = Pairs[index];
        TextBox key = Field(entry.Key, text => IdeErrors.Observe(SetEntryAsync(index, text, key: true)));
        Control value = Opens(entry.Value, $"{name}[{GodotVariant.Shown(entry.Key, quoted: true)}]", held => ReplacedValue(index, held)) is { } opens ? opens
            : Field(entry.Value, text => IdeErrors.Observe(SetEntryAsync(index, text, key: false)));
        value.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(value, 1);
        Button remove = ToolButtons.Of(IconKeys.Close, $"Remove {GodotVariant.Shown(entry.Key, quoted: true)}", () => IdeErrors.Observe(RemoveAsync(index)));
        Grid.SetColumn(remove, 2);
        row.Children.Add(key);
        row.Children.Add(value);
        row.Children.Add(remove);
        return row;
    }

    /// <summary>The button an item that is a form of its own opens it with, under the rows: an array's or a dictionary's,
    /// whose changes send this whole value with the item <paramref name="within"/> puts in its place; an object's own
    /// properties. Null for an item typed as text.</summary>
    private Button? Opens(object? item, string named, Func<object?, object?> within)
    {
        GodotType of = GodotVariant.TypeOf(item);
        bool nested = of is GodotType.Array or GodotType.Dictionary || of >= GodotType.PackedByteArray;
        if (!nested && item is not GodotObjectId)
        {
            return null;
        }
        TextBlock words = new TextBlock { FontSize = 12, Text = GodotVariant.Shown(item), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        Button button = new()
        {
            Classes = { StyleClasses.Compact },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(4, 2, 8, 2),
            Content = words,
        };
        ToolTip.SetTip(button, item is GodotObjectId ? $"{named}: its own properties, read and set in the game" : $"{named}: its items, set in the game");
        button.Click += (_, _) =>
        {
            Control form = item is GodotObjectId held
                ? new GodotResourceEditor(game, held.Id, named, tuner)
                : new GodotItemsEditor(game, named, of, item, new GodotHint(of == GodotType.Dictionary ? GodotEditing.Entries : GodotEditing.Items) { Element = GodotVariant.ElementOf(of) },
                    tuner, async changed => Pick(await SendWholeAsync(within(changed)), named, item), settled);
            slot.Child = form;
            slot.IsVisible = true;
        };
        return button;
    }

    /// <summary>This value with the item at an index replaced.</summary>
    private object? Replaced(int index, object? held)
    {
        List<object?> changed = [.. Listed];
        changed[index] = held;
        return GodotVariant.Packed(type, changed, items);
    }

    /// <summary>This dictionary with the value of the entry at an index replaced.</summary>
    private object? ReplacedValue(int index, object? held)
    {
        List<KeyValuePair<object?, object?>> entries = [.. Pairs];
        entries[index] = new(entries[index].Key, held);
        return Typed(entries);
    }

    /// <summary>The item a nested form shows, out of the whole value held now: at its index, or under its key, as it was named
    /// (<c>name[2]</c>, <c>name["key"]</c>); the item as sent where it is gone.</summary>
    private object? Pick(object? whole, string named, object? fallback)
    {
        string inside = named[(name.Length + 1)..^1];
        if (whole is GodotDictionary dictionary)
        {
            return dictionary.Entries.FirstOrDefault(e => GodotVariant.Shown(e.Key, quoted: true) == inside) is { Key: not null } found ? found.Value : fallback;
        }
        return int.TryParse(inside, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index)
            && GodotVariant.Items(whole) is { } listed && index < listed.Count ? listed[index] : fallback;
    }

    /// <summary>A field showing a value as it is typed back, which hands what was typed on at Enter or as it is left, where
    /// that differs from what it showed; Escape puts back what it showed.</summary>
    private TextBox Field(object? value, Action<string> taken)
    {
        string was = GodotVariant.Shown(value, quoted: value is string or GodotName or GodotNodePath);
        TextBox field = new TextBox { FontSize = 12, Text = was, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        field.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Take();
            }
            else if (e.Key == Key.Escape && field.Text != was)
            {
                e.Handled = true;
                field.Text = was;
            }
        };
        field.LostFocus += (_, _) => Take();
        fields.Add(field);
        return field;

        void Take()
        {
            if (field.Text is { } text && text != was && !sending)
            {
                was = text;
                taken(text);
            }
        }
    }

    /// <summary>A typed item read as <paramref name="of"/> reads it where that is known and reads it, else as the text
    /// reads (a number, true or false, a literal, a vector's numbers, null), else as the words typed.</summary>
    private static async Task<(object? Value, string? WhyNot)> ReadAsync(GodotType of, bool strict, string text)
    {
        if (of != GodotType.Nil)
        {
            (object? value, string? wanted) = await GodotVariant.ReadAsync(of, text, script: null);
            if (wanted is null || strict)
            {
                return (value, wanted);
            }
        }
        return (GodotVariant.Guessed(text), null);
    }

    private async Task SetItemAsync(int index, string text)
    {
        if (index >= Listed.Count)
        {
            return;
        }
        GodotType of = itemType != GodotType.Nil ? itemType : GodotVariant.TypeOf(Listed[index]);
        (object? read, string? wanted) = await ReadAsync(of, itemType != GodotType.Nil, text);
        if (wanted is not null)
        {
            Refused($"{name}[{index}] takes {wanted}");
            return;
        }
        List<object?> changed = [.. Listed];
        changed[index] = read;
        await SendAsync(GodotVariant.Packed(type, changed, items));
    }

    private async Task SetEntryAsync(int index, string text, bool key)
    {
        if (index >= Pairs.Count)
        {
            return;
        }
        KeyValuePair<object?, object?> entry = Pairs[index];
        GodotType given = key ? keyType : itemType;
        (object? read, string? wanted) = await ReadAsync(given != GodotType.Nil ? given : GodotVariant.TypeOf(key ? entry.Key : entry.Value), given != GodotType.Nil, text);
        if (wanted is not null)
        {
            Refused($"{(key ? "A key of " : string.Empty)}{name}{(key ? string.Empty : $"[{GodotVariant.Shown(entry.Key, quoted: true)}]")} takes {wanted}");
            return;
        }
        if (key && Pairs.Where((_, i) => i != index).Any(p => GodotVariant.Same(p.Key, read)))
        {
            Refused($"{GodotVariant.Shown(read, quoted: true)} is a key of {name} already");
            return;
        }
        List<KeyValuePair<object?, object?>> entries = [.. Pairs];
        entries[index] = key ? new(read, entry.Value) : new(entry.Key, read);
        await SendAsync(Typed(entries));
    }

    /// <summary>One more item, of the element type or the last item's (zero, empty, false), or an entry whose key no other
    /// has, of the type the keys are; an untyped dictionary takes a string where its last key's type gives none.</summary>
    private async Task AddAsync()
    {
        if (Entries)
        {
            List<KeyValuePair<object?, object?>> entries = [.. Pairs];
            List<object?> taken = [.. entries.Select(e => e.Key)];
            GodotType newKey = keyType != GodotType.Nil ? keyType : entries.Count > 0 ? GodotVariant.TypeOf(entries[^1].Key) : GodotType.String;
            GodotType newValue = itemType != GodotType.Nil ? itemType : entries.Count > 0 ? GodotVariant.TypeOf(entries[^1].Value) : GodotType.Nil;
            if (!GodotVariant.TryNewKey(newKey, taken, out object? key) && (keyType != GodotType.Nil || !GodotVariant.TryNewKey(GodotType.String, taken, out key)))
            {
                Refused($"No new {GodotVariant.TypeName(newKey)} key can be made for {name}");
                return;
            }
            entries.Add(new(key, GodotVariant.Default(newValue)));
            await SendAsync(Typed(entries));
            return;
        }
        List<object?> added = [.. Listed];
        GodotType of = itemType != GodotType.Nil ? itemType : added.Count > 0 ? GodotVariant.TypeOf(added[^1]) : GodotType.Nil;
        added.Add(GodotVariant.Default(of));
        await SendAsync(GodotVariant.Packed(type, added, items));
    }

    /// <summary>Entries as a dictionary typed as the one the game sent.</summary>
    private GodotDictionary Typed(IReadOnlyList<KeyValuePair<object?, object?>> entries) => new(entries) { KeyType = keys, ValueType = items };

    private async Task RemoveAsync(int index)
    {
        if (Entries && index < Pairs.Count)
        {
            await SendAsync(Typed([.. Pairs.Where((_, i) => i != index)]));
        }
        else if (!Entries && index < Listed.Count)
        {
            await SendAsync(GodotVariant.Packed(type, [.. Listed.Where((_, i) => i != index)], items));
        }
    }

    /// <summary>Sends the whole value, then shows what the game holds, the properties pane's row read again with it.</summary>
    private async Task SendAsync(object? value)
    {
        try
        {
            await SendWholeAsync(value);
        }
        catch (LiveAppException failed)
        {
            Refused($"{name} was not set: {failed.Message}");
            return;
        }
        settled();
    }

    /// <summary>Sends the whole value where it is held and shows what is held then, which it answers; a nested form's change
    /// comes this way too, so its rows and these show the same.</summary>
    private async Task<object?> SendWholeAsync(object? value)
    {
        sending = true;
        try
        {
            object? held = await send(value);
            Show(held);
            return held;
        }
        finally
        {
            sending = false;
        }
    }

    private void Refused(string why)
    {
        tuner.Say(why);
        Show(shown);
    }
}

/// <summary>A resource a node of a running game holds, as its own properties: read by the resource's id (one loaded from a
/// file through Passenger's helper), each row's editor the one its value takes in the properties pane, a value no editor
/// fits typed and taken at Enter, each set in the game as it changes; what one of its editors opens (a colour's picker,
/// another resource's properties) shows under its rows. A resource shared by several nodes changes in each.</summary>
public sealed class GodotResourceEditor : Border, IGodotTuner
{
    private readonly GodotGame game;
    private readonly IGodotTuner tuner;
    // Clear of the scroll bar, which shows over the rows' right.
    private readonly StackPanel rows = new() { Spacing = 2, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock words = new() { Classes = { StyleClasses.EmptyText } };
    private readonly TextBlock title;
    private readonly Border slot = new() { IsVisible = false };
    private readonly Dictionary<string, (Control Row, GodotValueCell Cell, TextBox Typed, TextBlock Shown)> made = new(StringComparer.Ordinal);
    private ulong? id;

    public GodotResourceEditor(GodotGame game, long element, string property, IGodotTuner tuner)
        : this(game, property, tuner) => IdeErrors.Observe(ReadAsync(element, property));

    /// <summary>An object an array or a dictionary holds, by its id, as its own properties (<paramref name="named"/> where it is held).</summary>
    public GodotResourceEditor(GodotGame game, ulong held, string named, IGodotTuner tuner)
        : this(game, named, tuner) => IdeErrors.Observe(ReadHeldAsync(game.Held(new GodotObjectId(held)), named));

    private GodotResourceEditor(GodotGame game, string property, IGodotTuner tuner)
    {
        this.game = game;
        this.tuner = tuner;
        Classes.Add(StyleClasses.Popup);
        Width = PopupFrame.Medium;
        Padding = new Thickness(8);
        title = new TextBlock { Classes = { StyleClasses.PopupTitle }, Text = property, TextTrimming = TextTrimming.CharacterEllipsis };
        words.Text = $"Reading {property}'s properties…";
        Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                title,
                new ScrollViewer { MaxHeight = ListRow.Height * 14, Content = new Panel { Children = { rows, words } } },
                slot,
            },
        };
    }

    /// <summary>The resource's id once read.</summary>
    public ulong? Resource => id;

    /// <summary>A property's row, once shown.</summary>
    public Control? RowOf(string name) => made.TryGetValue(name, out (Control Row, GodotValueCell Cell, TextBox Typed, TextBlock Shown) row) ? row.Row : null;

    private async Task ReadAsync(long element, string property)
    {
        try
        {
            await ReadHeldAsync(await game.HeldAsync(element, property), property);
        }
        catch (LiveAppException failed)
        {
            words.Text = $"{property}'s properties could not be read: {failed.Message}";
        }
    }

    private async Task ReadHeldAsync((ulong Id, string Class, string Path) held, string property)
    {
        try
        {
            id = held.Id;
            title.Text = $"{property}: {(held.Class.Length > 0 ? held.Class : "Resource")}{(held.Path.Length > 0 ? $" ({held.Path})" : string.Empty)}";
            Show(await game.InspectAsync(held.Id));
        }
        catch (LiveAppException failed)
        {
            words.Text = $"{property}'s properties could not be read: {failed.Message}";
        }
    }

    private void Show(GodotObjectState state)
    {
        List<GodotProperty> shown = [.. state.Properties.Where(p => p.Name is not ("script" or "resource_path" or "resource_name" or "resource_local_to_scene" or "resource_scene_unique_id"))];
        words.IsVisible = shown.Count == 0;
        words.Text = "It has no properties to show";
        if (title.Text is { } named && !named.Contains(':', StringComparison.Ordinal))
        {
            title.Text = $"{named}: {state.Class}";
        }
        foreach (GodotProperty property in shown)
        {
            if (!made.TryGetValue(property.Name, out (Control Row, GodotValueCell Cell, TextBox Typed, TextBlock Shown) row))
            {
                made[property.Name] = row = Row(property.Name);
                rows.Children.Add(row.Row);
            }
            PropertyValue value = new(property.Name, property.Category, false, property.Type.ToString(), GodotVariant.Shown(property.Value), property.Category, null, null);
            (bool edits, bool notes) = row.Cell.Show((long)state.Id, value);
            row.Cell.Editor.IsVisible = edits;
            row.Cell.Note.IsVisible = notes;
            bool typed = !edits && game.WhyNotSet((long)state.Id, property.Name) is null && GodotVariant.Parse(property.Type, GodotVariant.Shown(property.Value)).WhyNot is null;
            row.Typed.IsVisible = typed;
            row.Shown.IsVisible = !edits && !typed;
            if (!row.Typed.IsFocused)
            {
                row.Typed.Text = GodotVariant.Shown(property.Value);
            }
            row.Shown.Text = GodotVariant.Shown(property.Value);
            ToolTip.SetTip(row.Shown, game.WhyNotSet((long)state.Id, property.Name) ?? GodotVariant.Shown(property.Value));
        }
    }

    private (Control Row, GodotValueCell Cell, TextBox Typed, TextBlock Shown) Row(string name)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("2*,3*"), RowDefinitions = new RowDefinitions("Auto,Auto"), MinHeight = ListRow.Height };
        TextBlock label = new() { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        ToolTip.SetTip(label, name);
        GodotValueCell cell = new(game, this);
        TextBox typed = new TextBox { FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        TextBlock shown = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        typed.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && id is { } resource)
            {
                e.Handled = true;
                IdeErrors.Observe(TypedAsync((long)resource, name, typed.Text ?? string.Empty));
            }
        };
        foreach (Control value in new[] { cell.Editor, typed, shown })
        {
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        // What a value changed was, and Keep, under its editor, as the pane's rows note it.
        Grid.SetRow(cell.Note, 1);
        Grid.SetColumn(cell.Note, 1);
        cell.Note.IsVisible = false;
        grid.Children.Add(cell.Note);
        grid.Children.Add(label);
        return (grid, cell, typed, shown);
    }

    private async Task TypedAsync(long resource, string name, string text)
    {
        if (game.InspectedOf(resource)?.Properties.FirstOrDefault(p => p.Name == name) is not { } property)
        {
            return;
        }
        (object? read, string? wanted) = await GodotVariant.ReadAsync(property.Type, text, script: null);
        if (wanted is not null)
        {
            tuner.Say($"{name} takes {wanted}");
            return;
        }
        try
        {
            await game.SetValueAsync(resource, name, read);
        }
        catch (LiveAppException failed)
        {
            tuner.Say($"{name} was not set: {failed.Message}");
            return;
        }
        Settled(resource);
    }

    /// <summary>A value set has settled: the resource's properties are read again, so the rows show what the game holds.</summary>
    public void Settled(long element)
    {
        if (id is { } resource && (long)resource == element)
        {
            IdeErrors.Observe(ReadAgainAsync(element));
        }
    }

    private async Task ReadAgainAsync(long element)
    {
        try
        {
            Show(await game.InspectAsync((ulong)element));
        }
        catch (LiveAppException failed)
        {
            tuner.Say($"The resource's properties could not be read again: {failed.Message}");
        }
    }

    /// <summary>A resource's value kept where the resource is written, as the pane's Keep keeps a node's.</summary>
    public void Keep(GodotTuned tuned, Control from) => tuner.Keep(tuned, from);

    /// <summary>What an editor of a row opens, under the rows.</summary>
    public void ShowPopup(Control popup, Control from)
    {
        slot.Child = popup;
        slot.IsVisible = true;
        Dispatcher.UIThread.Post(() => popup.Focus(), DispatcherPriority.Loaded);
    }

    public void ClosePopup()
    {
        slot.Child = null;
        slot.IsVisible = false;
    }

    public void Say(string words) => tuner.Say(words);
}
