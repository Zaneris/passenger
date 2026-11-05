using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.LiveTree;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Xaml.Features;

namespace Passenger.App.Godot;

/// <summary>What the properties pane's editors of a running game lean on: the node's details read again once a value
/// settles, Keep, a popup beside what opened it, and the status bar.</summary>
public interface IGodotTuner
{
    /// <summary>A value set has settled: the node's details are read again, so its rows show what the game holds.</summary>
    void Settled(long element);

    void Keep(GodotTuned tuned, Control from);

    /// <summary>A popup beside the control that opened it.</summary>
    void ShowPopup(Control popup, Control from);

    void ClosePopup();

    void Say(string words);
}

/// <summary>Where a value editor's values come from and go: a running game's nodes (<see cref="GodotGame"/>), or a scene's
/// nodes as its file writes them (<see cref="GodotSceneValues"/>).</summary>
public interface IGodotValues
{
    /// <summary>An element's property as last read, or null.</summary>
    GodotProperty? PropertyOf(long element, string name);

    /// <summary>Why a property shown cannot be set, or null where it can.</summary>
    string? WhyNotSet(long element, string name);

    /// <summary>A value an editor gave: <paramref name="settles"/> once the person lets go (a drag's end, a choice made), where
    /// a value moved before is only shown. Throws a <see cref="LiveAppException"/> saying why it was not set.</summary>
    Task SetValueAsync(long element, string name, object? value, bool settles);

    /// <summary>A value changed and not kept, noted under its row with Keep; null where none is.</summary>
    GodotTuned? TunedOf(long element, string name);
}

/// <summary>The properties pane's editors for Godot values, each fit for its value as its type and hint read
/// (<see cref="GodotHints"/>): a check box, a slider within a range, a choice of names, a colour's swatch that opens the
/// colour picker, axes that scrub, and, a running game's alone, a resource's thumbnail that opens the project's resources
/// and its Properties its own, an array's or a dictionary's words that open its items or entries. A value lands as the
/// pointer moves, and a game's value changed is noted under its row with what it was, and Keep.</summary>
public sealed class GodotValueCells(IGodotValues values, IGodotTuner tuner) : ILiveValueCells
{
    public IGodotValues Values => values;

    public LiveValueCell Make() => new GodotValueCell(values, tuner);
}

/// <summary>One row's editors, each made the first time a property of its kind is shown, and its note.</summary>
internal sealed class GodotValueCell : LiveValueCell
{
    private readonly IGodotValues values;
    private readonly IGodotTuner tuner;
    private readonly Panel editor = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel note = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock changed = new() { Classes = { StyleClasses.Muted }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button keep = new() { Classes = { StyleClasses.Link }, Content = "Keep", FontSize = 11 };

    private CheckBox? check;
    private (DockPanel Panel, ValueSlider Track, ScrubField Number)? range;
    private ComboBox? choice;
    private (Button Button, Border Swatch, TextBlock Hex)? colour;
    private (UniformGrid Panel, List<ScrubField> Fields)? axes;
    private (DockPanel Panel, Button Button, Image Thumbnail, TextBlock Name, Button Properties)? resource;
    private (Button Button, TextBlock Words)? items;

    private long element;
    private GodotProperty? property;
    private GodotHint hint = new(GodotEditing.Text);
    private bool showing;

    /// <summary>The value waiting to be sent behind the one being sent, and to which node's property; whether the details
    /// are read again once it is.</summary>
    private (long Element, string Name, object? Value, bool Settles)? pending;
    private bool sending;
    private bool settle;

    public GodotValueCell(IGodotValues values, IGodotTuner tuner)
    {
        this.values = values;
        this.tuner = tuner;
        Avalonia.Controls.Shapes.Ellipse mark = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        mark.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, mark.GetResourceObservable(TokenKeys.Modified));
        note.Children.Add(mark);
        note.Children.Add(changed);
        note.Children.Add(keep);
        ToolTip.SetTip(keep, "Write this value where it comes from: its node's line in its scene, or its default in the C# that exports it");
        keep.Click += (_, _) =>
        {
            if (property is { } shown && values.TunedOf(element, shown.Name) is { Changed: true } tuned)
            {
                tuner.Keep(tuned, keep);
            }
        };
    }

    public override Control Editor => editor;

    public override Control Note => note;

    public override (bool Edits, bool Notes) Show(long element, PropertyValue value)
    {
        this.element = element;
        property = values.PropertyOf(element, value.Name);
        bool notes = property is not null && values.TunedOf(element, value.Name) is { Changed: true } tuned && Noted(tuned);
        if (property is null)
        {
            return (false, notes);
        }
        hint = GodotHints.Of(property);
        string? whyNot = values.WhyNotSet(element, value.Name);
        // A resource's own properties are read and set by its id, however the node holds it.
        bool ownProperties = values is GodotGame && hint is { Editing: GodotEditing.Resource, PathString: false } && property.Value is string { Length: > 0 } or GodotObjectId;
        if (hint.Editing == GodotEditing.Text || whyNot is not null && !ownProperties || values is not GodotGame && hint.Editing is GodotEditing.Resource or GodotEditing.Items or GodotEditing.Entries)
        {
            return (false, notes);
        }
        showing = true;
        try
        {
            Control shown = hint.Editing switch
            {
                GodotEditing.Check => ShowCheck(property.Value),
                GodotEditing.Range => ShowRange(property.Value, hint.Range!),
                GodotEditing.Choice => ShowChoice(property.Value),
                GodotEditing.Colour => ShowColour(property.Value),
                GodotEditing.Axes => ShowAxes(property.Value),
                GodotEditing.Items or GodotEditing.Entries => ShowItems(property.Value),
                _ => ShowResource(property.Value, whyNot, ownProperties),
            };
            foreach (Control child in editor.Children)
            {
                child.IsVisible = child == shown;
            }
        }
        finally
        {
            showing = false;
        }
        return (true, notes);
    }

    /// <summary>The note's words for a value changed: what it was, Keep beside them.</summary>
    private bool Noted(GodotTuned tuned)
    {
        changed.Text = $"Changed from {Written(tuned.Original)}";
        ToolTip.SetTip(changed, $"{tuned.Name} was {Written(tuned.Original)} before it was set; it is {Written(tuned.Value)} now");
        return true;
    }

    /// <summary>A value as a note says it: a colour by its hex, a number in its shortest form, the rest as the pane shows them.</summary>
    internal static string Written(object? value) => value switch
    {
        GodotMath { Type: GodotType.Color } => ArgbOf(value).Hex,
        double real => GodotText.Number(real),
        GodotMath math => $"({string.Join(", ", math.Values.Select(GodotText.Number))})",
        _ => GodotVariant.Shown(value),
    };

    private T Add<T>(T control) where T : Control
    {
        editor.Children.Add(control);
        return control;
    }

    private Control ShowCheck(object? value)
    {
        if (check is null)
        {
            check = Add(new CheckBox { VerticalAlignment = VerticalAlignment.Center });
            check.IsCheckedChanged += (_, _) =>
            {
                if (!showing)
                {
                    Send(check.IsChecked == true, settles: true);
                }
            };
        }
        check.IsChecked = value is true;
        return check;
    }

    private Control ShowRange(object? value, GodotRange given)
    {
        if (range is null)
        {
            ValueSlider slider = new();
            ScrubField number = new(string.Empty) { Width = 74, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(number, Dock.Right);
            DockPanel panel = Add(new DockPanel { Children = { number, slider } });
            slider.Changing += (_, shown) =>
            {
                number.Show(shown);
                Send(FromShown(shown), settles: false);
            };
            slider.Committed += (_, shown) =>
            {
                number.Show(shown);
                Send(FromShown(shown), settles: true);
            };
            number.Changing += (_, shown) =>
            {
                slider.Show(shown);
                Send(FromShown(Taken(shown)), settles: false);
            };
            number.Committed += (_, shown) =>
            {
                slider.Show(Taken(shown));
                Send(FromShown(Taken(shown)), settles: true);
            };
            range = (panel, slider, number);
        }
        (DockPanel rangePanel, ValueSlider rangeSlider, ScrubField rangeNumber) = range.Value;
        rangeSlider.At = given.At;
        rangeSlider.Fraction = given.Fraction;
        rangeSlider.Step = given.Step;
        rangeNumber.Step = given.Step;
        rangeNumber.ShownStep = given.Step;
        rangeNumber.Integers = property?.Type == GodotType.Int;
        double now = Shown(value);
        rangeSlider.Show(now);
        rangeNumber.Show(now);
        string unit = given.Suffix is { Length: > 0 } suffix ? $" {suffix}" : given.Degrees ? "°" : string.Empty;
        ToolTip.SetTip(rangeSlider, $"From {GodotText.Number(given.Min)}{unit} to {GodotText.Number(given.Max)}{unit}{(given.OrGreater ? ", or more as typed" : string.Empty)}");
        return rangePanel;
    }

    /// <summary>A number typed past an end, taken where the range the row shows now allows it: a row is handed one property
    /// after another.</summary>
    private double Taken(double typed) => hint.Range?.Within(typed) ?? typed;

    /// <summary>A number as the range shows it: an angle in degrees.</summary>
    private double Shown(object? value)
    {
        double number = value switch
        {
            long whole => whole,
            double real => real,
            _ => 0,
        };
        return hint.Range is { Degrees: true } ? Math.Round(number * 180 / Math.PI, 4) : number;
    }

    /// <summary>A number shown as the value the game holds: a whole one for an int, an angle in radians.</summary>
    private object FromShown(double shown)
    {
        double value = hint.Range is { Degrees: true } ? shown * Math.PI / 180 : shown;
        return property?.Type == GodotType.Int ? (object)(long)Math.Round(value) : value;
    }

    private Control ShowChoice(object? value)
    {
        if (choice is null)
        {
            choice = Add(new ComboBox { Classes = { StyleClasses.Compact }, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 320 });
            ReusedRows.Use(choice, () =>
            {
                TextBlock row = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                return row.Showing<GodotChoice>(c => row.Text = c.Name);
            });
            choice.SelectionChanged += (_, _) =>
            {
                if (!showing && choice.SelectedItem is GodotChoice chosen)
                {
                    Send(property?.Type == GodotType.StringName ? new GodotName(chosen.Name) : chosen.Value, settles: true);
                }
            };
        }
        if (!ReferenceEquals(choice.ItemsSource, hint.Choices))
        {
            choice.ItemsSource = hint.Choices;
        }
        string? now = value switch
        {
            GodotName name => name.Name,
            string text => text,
            _ => null,
        };
        choice.SelectedItem = hint.Choices.FirstOrDefault(c => now is not null ? c.Name == now : c.Value is long v && value is long held && v == held);
        return choice;
    }

    private Control ShowColour(object? value)
    {
        if (colour is null)
        {
            Border swatch = new() { Classes = { StyleClasses.Box }, Width = 22, Height = 16, BorderThickness = new Thickness(1) };
            swatch.Bind(Border.BorderBrushProperty, swatch.GetResourceObservable(TokenKeys.FieldBorder));
            TextBlock hex = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
            Button button = Add(new Button
            {
                Classes = { StyleClasses.Compact },
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(4, 2, 8, 2),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { swatch, hex } },
            });
            ToolTip.SetTip(button, values is GodotGame ? "Pick a colour: the game takes each one as the pointer moves" : "Pick a colour: the scene is written once one is chosen");
            button.Click += (_, _) => PickColour(button);
            colour = (button, swatch, hex);
        }
        Argb argb = ArgbOf(value);
        colour.Value.Swatch.Background = new SolidColorBrush(Color.FromArgb(argb.A, argb.R, argb.G, argb.B));
        colour.Value.Hex.Text = argb.Hex;
        return colour.Value.Button;
    }

    /// <summary>The colour picker over the swatch: each colour it moves to sent as it moves, the one chosen kept, and the
    /// colour the game had put back where the picker is left.</summary>
    private void PickColour(Button from)
    {
        if (property is not { } picked)
        {
            return;
        }
        object? before = picked.Value;
        long at = element;
        string name = picked.Name;
        ColourPicker picker = new(ArgbOf(before), alpha: hint.Alpha);
        picker.Changing += (_, moved) => SendTo(at, name, ColourOf(moved, before), settles: false);
        picker.Chosen += (_, chosen) =>
        {
            tuner.ClosePopup();
            SendTo(at, name, chosen is { } taken ? ColourOf(taken, before) : before, settles: true);
        };
        tuner.ShowPopup(picker, from);
    }

    /// <summary>A Godot colour as the picker's eight bits a channel, each clamped (a colour past one is HDR).</summary>
    internal static Argb ArgbOf(object? value)
    {
        IReadOnlyList<double> c = value is GodotMath { Type: GodotType.Color, Values: { Count: 4 } values } ? values : [1, 1, 1, 1];
        return new Argb(Byte(c[3]), Byte(c[0]), Byte(c[1]), Byte(c[2]));

        static byte Byte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0, 1) * 255);
    }

    /// <summary>The picker's colour as Godot's: channels from zero to one, each the single nearest its byte, the alpha the
    /// colour had where the picker sets none.</summary>
    internal static GodotMath ColourOf(Argb chosen, object? before)
    {
        double alpha = before is GodotMath { Type: GodotType.Color, Values: [_, _, _, double a] } && chosen.A == ArgbOf(before).A ? a : chosen.A / 255.0;
        return new GodotMath(GodotType.Color, [Single(chosen.R / 255.0), Single(chosen.G / 255.0), Single(chosen.B / 255.0), Single(alpha)]);

        static double Single(double value) => (double)(float)Math.Round(value, 4);
    }

    private Control ShowAxes(object? value)
    {
        int count = hint.Axes.Count;
        if (axes is not { } made || made.Fields.Count != count)
        {
            if (axes is { } old)
            {
                editor.Children.Remove(old.Panel);
            }
            UniformGrid panel = Add(new UniformGrid { Columns = count, Rows = 1 });
            List<ScrubField> fields = [];
            for (int i = 0; i < count; i++)
            {
                int axis = i;
                ScrubField field = new(hint.Axes[i]) { Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) };
                field.Changing += (_, moved) => Send(WithAxis(axis, moved), settles: false);
                field.Committed += (_, moved) => Send(WithAxis(axis, moved), settles: true);
                fields.Add(field);
                panel.Children.Add(field);
            }
            axes = (panel, fields);
        }
        (UniformGrid axesPanel, List<ScrubField> axesFields) = axes!.Value;
        IReadOnlyList<double> numbers = Numbers(value);
        for (int i = 0; i < axesFields.Count; i++)
        {
            axesFields[i].Integers = hint.Integers;
            axesFields[i].Step = hint.Range?.Step ?? (hint.Integers ? 1 : 0.1);
            axesFields[i].ShownStep = hint.Range?.Step ?? GodotText.InspectorStep;
            axesFields[i].Show(i < numbers.Count ? numbers[i] : 0);
        }
        return axesPanel;
    }

    private static IReadOnlyList<double> Numbers(object? value) => value switch
    {
        GodotMath math => math.Values,
        long whole => [whole],
        double real => [real],
        _ => [],
    };

    /// <summary>The value with one axis moved, from the value as last read and the other axes as shown.</summary>
    private object? WithAxis(int axis, double moved)
    {
        if (property is not { } shown || axes is not { } fields)
        {
            return null;
        }
        if (shown.Type is GodotType.Int or GodotType.Float)
        {
            return shown.Type == GodotType.Int ? (long)Math.Round(moved) : moved;
        }
        double[] values = [.. fields.Fields.Select(f => f.Value)];
        values[axis] = moved;
        return new GodotMath(shown.Type, [.. values.Select(v => hint.Integers ? Math.Round(v) : (double)(float)v)]);
    }

    /// <summary>A resource's thumbnail and name, which opens the project's resources where the node takes another
    /// (<paramref name="whyNot"/> null), and its Properties, which opens its own where it holds one.</summary>
    private Control ShowResource(object? value, string? whyNot, bool ownProperties)
    {
        if (resource is null)
        {
            Image thumbnail = new() { Width = 18, Height = 18, Stretch = Stretch.Uniform };
            TextBlock name = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Button button = new()
            {
                Classes = { StyleClasses.Compact },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(4, 2, 8, 2),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { thumbnail, name } },
            };
            button.Click += (_, _) => PickResource(button);
            Button properties = new() { Classes = { StyleClasses.Link }, Content = "Properties", FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            properties.Click += (_, _) => EditResource(properties);
            DockPanel.SetDock(properties, Dock.Right);
            DockPanel panel = Add(new DockPanel { Children = { properties, button } });
            resource = (panel, button, thumbnail, name, properties);
        }
        string? path = value as string;
        resource.Value.Name.Text = path is { Length: > 0 } ? Path.GetFileName(path) : value is null ? "None" : GodotVariant.Shown(value);
        resource.Value.Button.IsEnabled = whyNot is null;
        ToolTip.SetTip(resource.Value.Button, whyNot ?? (path is { Length: > 0 } ? $"{path}: choose another of the project's" : "Choose one of the project's"));
        resource.Value.Properties.IsVisible = ownProperties;
        ToolTip.SetTip(resource.Value.Properties, "Its own properties, read and set in the game: a resource several nodes hold changes in each");
        IdeErrors.Observe(ThumbnailAsync(resource.Value.Thumbnail, path));
        return resource.Value.Panel;
    }

    /// <summary>The resource's own properties over its row (<see cref="GodotResourceEditor"/>).</summary>
    private void EditResource(Button from)
    {
        if (property is { } shown)
        {
            tuner.ShowPopup(new GodotResourceEditor((GodotGame)values, element, shown.Name, tuner), from);
        }
    }

    /// <summary>An array's or dictionary's words, which open its items or entries to set (<see cref="GodotItemsEditor"/>).</summary>
    private Control ShowItems(object? value)
    {
        if (items is null)
        {
            TextBlock words = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
            Button button = Add(new Button
            {
                Classes = { StyleClasses.Compact },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(4, 2, 8, 2),
                Content = words,
            });
            button.Click += (_, _) =>
            {
                if (property is { } shown)
                {
                    tuner.ShowPopup(new GodotItemsEditor((GodotGame)values, element, shown, hint, tuner), button);
                }
            };
            items = (button, words);
        }
        items.Value.Words.Text = GodotVariant.Shown(value);
        int count = hint.Editing == GodotEditing.Entries ? (value as GodotDictionary)?.Entries.Count ?? 0 : GodotVariant.Items(value)?.Count ?? 0;
        ToolTip.SetTip(items.Value.Button, hint.Editing == GodotEditing.Entries ? $"{Plural.Of(count, "entry", "entries")}: set its keys and values" : $"{Plural.Of(count, "item")}: set them");
        return items.Value.Button;
    }

    private async Task ThumbnailAsync(Image image, string? path)
    {
        image.Tag = path;
        image.Source = null;
        if (path is null || !path.StartsWith("res://", StringComparison.Ordinal) || values is not GodotGame game)
        {
            return;
        }
        Avalonia.Media.Imaging.Bitmap? made = await GodotResources.ThumbnailAsync(game.PathOf(path), 36);
        if (Equals(image.Tag, path))
        {
            image.Source = made;
        }
    }

    /// <summary>The project's resources of the property's classes, with their thumbnails, over the button: the one chosen
    /// sent to the game.</summary>
    private void PickResource(Button from)
    {
        if (property is not { } picked || values is not GodotGame game)
        {
            return;
        }
        long at = element;
        string name = picked.Name;
        GodotResourcePicker list = new(game.Folder, hint.Classes, picked.Value as string);
        list.Chosen += (_, chosen) =>
        {
            tuner.ClosePopup();
            if (chosen is not null)
            {
                IdeErrors.Observe(SetResourceAsync(at, name, chosen));
            }
        };
        tuner.ShowPopup(list, from);
    }

    private async Task SetResourceAsync(long at, string name, string chosen)
    {
        try
        {
            await ((GodotGame)values).SetResourceAsync(at, name, chosen);
        }
        catch (LiveAppException failed)
        {
            tuner.Say($"{name} was not set: {failed.Message}");
            return;
        }
        tuner.Settled(at);
    }

    private void Send(object? value, bool settles)
    {
        if (property is { } shown)
        {
            SendTo(element, shown.Name, value, settles);
        }
    }

    /// <summary>A value sent to the game behind the one being sent, the latest of those waiting the one that goes, so a drag
    /// lands as fast as the game takes it; the details read again once one that settles has gone.</summary>
    private void SendTo(long at, string name, object? value, bool settles)
    {
        pending = (at, name, value, settles || pending is { Settles: true } waiting && waiting.Element == at && waiting.Name == name);
        settle |= settles;
        if (!sending)
        {
            IdeErrors.Observe(SendingAsync());
        }
    }

    private async Task SendingAsync()
    {
        sending = true;
        long last = element;
        try
        {
            while (pending is { } next)
            {
                pending = null;
                last = next.Element;
                await values.SetValueAsync(next.Element, next.Name, next.Value, next.Settles);
            }
        }
        catch (LiveAppException failed)
        {
            pending = null;
            tuner.Say($"The value was not set: {failed.Message}");
        }
        finally
        {
            sending = false;
        }
        if (settle)
        {
            settle = false;
            tuner.Settled(last);
        }
    }

    /// <summary>The number a range's or an axis's value shows, for a test to read.</summary>
    internal string Showing => range is { } r && r.Panel.IsVisible ? GodotText.Number(r.Track.Value) : string.Empty;

    /// <summary>The editor shown, for a test to drive.</summary>
    internal Control? ShownEditor => editor.Children.FirstOrDefault(c => c.IsVisible);

    /// <summary>The axes' fields as shown.</summary>
    internal IReadOnlyList<ScrubField> Axes => axes?.Fields ?? [];
}
