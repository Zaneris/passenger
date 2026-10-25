using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Settings;

/// <summary>Where a setting's own row stands in Settings and what finds it: its page, its label, the words a search
/// matches beside those, and a note under its editor where what it does needs saying.</summary>
public sealed record SettingRow(string Page, string Label, string Words, string? Note = null);

/// <summary>A setting of one value, declared once in <see cref="Declared"/>: how it is read and written, what it allows (a
/// number's range, an enum's values) and its own row in Settings, where it has one. Its editor and its clamp come from
/// here; a row that edits two settings, a radio group or one that follows another setting is the dialog's, built round
/// the editor made here where one fits.</summary>
public abstract class Setting
{
    protected Setting(SettingRow? row)
    {
        Row = row;
        Declared.All.Add(this);
    }

    public SettingRow? Row { get; }

    /// <summary>The control that edits it, showing its value in <paramref name="now"/>; each edit goes through
    /// <paramref name="change"/>.</summary>
    public abstract Control Editor(AppSettings now, Action<Func<AppSettings, AppSettings>> change);

    /// <summary>The settings with this one brought into what it allows.</summary>
    public abstract AppSettings Clamp(AppSettings settings);
}

/// <summary>A setting on or off, edited by a check box.</summary>
public sealed class ToggleSetting(Func<AppSettings, bool> get, Func<AppSettings, bool, AppSettings> set, SettingRow? row = null) : Setting(row)
{
    public override Control Editor(AppSettings now, Action<Func<AppSettings, AppSettings>> change)
    {
        CheckBox box = new() { IsChecked = get(now) };
        box.IsCheckedChanged += (_, _) => change(s => set(s, box.IsChecked == true));
        return box;
    }

    public override AppSettings Clamp(AppSettings settings) => settings;
}

/// <summary>A number held between <paramref name="min"/> and <paramref name="max"/>, edited by a spin box in steps of
/// <paramref name="step"/>, its unit after it where it has one.</summary>
public sealed class NumberSetting(Func<AppSettings, double> get, Func<AppSettings, double, AppSettings> set, double min, double max, double step,
    SettingRow? row = null, string? unit = null) : Setting(row)
{
    public override Control Editor(AppSettings now, Action<Func<AppSettings, AppSettings>> change) => unit is null ? Box(now, change) : new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children = { Box(now, change), new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center } },
    };

    /// <summary>The spin box alone, for a row the dialog builds round it.</summary>
    public NumericUpDown Box(AppSettings now, Action<Func<AppSettings, AppSettings>> change)
    {
        NumericUpDown number = new()
        {
            Value = (decimal)get(now), Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, Width = 140, FormatString = step < 1 ? "0.0" : "0",
        };
        number.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } next)
            {
                change(s => set(s, (double)next));
            }
        };
        return number;
    }

    public override AppSettings Clamp(AppSettings settings) => set(settings, Math.Clamp(get(settings), min, max));
}

/// <summary>One of an enum's values, edited by a drop-down of the names Settings gives them, in <paramref name="choices"/>'
/// order (the enum's own names where it gives none); a value this build does not know goes back to its default.</summary>
public sealed class ChoiceSetting<T> : Setting where T : struct, Enum
{
    private readonly Func<AppSettings, T> get;
    private readonly Func<AppSettings, T, AppSettings> set;
    private readonly IReadOnlyList<(T Value, string Name)> choices;
    private readonly T fallback;

    public ChoiceSetting(Func<AppSettings, T> get, Func<AppSettings, T, AppSettings> set, IReadOnlyList<(T Value, string Name)>? choices = null, SettingRow? row = null)
        : base(row)
    {
        this.get = get;
        this.set = set;
        this.choices = choices ?? [.. Enum.GetValues<T>().Select(v => (v, v.ToString()))];
        fallback = get(new AppSettings());
    }

    public override Control Editor(AppSettings now, Action<Func<AppSettings, AppSettings>> change) => Box(now, change);

    /// <summary>The drop-down alone, for a row the dialog builds round it.</summary>
    public ComboBox Box(AppSettings now, Action<Func<AppSettings, AppSettings>> change)
    {
        T value = get(now);
        ComboBox box = new()
        {
            ItemsSource = choices.Select(c => c.Name).ToArray(),
            SelectedIndex = choices.Select(c => c.Value).ToList().IndexOf(value),
            Classes = { "form" },
        };
        box.SelectionChanged += (_, _) => change(s => set(s, choices[Math.Max(0, box.SelectedIndex)].Value));
        return box;
    }

    public override AppSettings Clamp(AppSettings settings) => Enum.IsDefined(get(settings)) ? settings : set(settings, fallback);
}
