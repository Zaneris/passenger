using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

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

    /// <summary>Why a change from <paramref name="was"/> to <paramref name="now"/> gives this setting a value Settings
    /// refuses, naming the setting; null where it leaves the value be or the new one is allowed.</summary>
    public virtual string? Refused(AppSettings was, AppSettings now) => null;
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
            Classes = { StyleClasses.Form },
        };
        box.SelectionChanged += (_, _) => change(s => set(s, choices[Math.Max(0, box.SelectedIndex)].Value));
        return box;
    }

    public override AppSettings Clamp(AppSettings settings) => Enum.IsDefined(get(settings)) ? settings : set(settings, fallback);
}

/// <summary>A program to start and its arguments, written as one command line (<see cref="Run.CommandLine"/>), or null for
/// the one Passenger finds, <paramref name="found"/>'s, which an empty box shows; a line naming no program (quotes round
/// nothing) is refused, saying why under the box.</summary>
public sealed class ProgramSetting(Func<AppSettings, string?> get, Func<AppSettings, string?, AppSettings> set, Func<string> found, SettingRow row) : Setting(row)
{
    /// <summary>Why a line naming no program is refused.</summary>
    private const string NamesNone = "names no program to start: the program's path comes first, its arguments after it";

    public override Control Editor(AppSettings now, Action<Func<AppSettings, AppSettings>> change)
    {
        TextBox line = new() { Classes = { StyleClasses.Search }, Text = get(now) ?? string.Empty, PlaceholderText = found(), Width = 320 };
        TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable(TokenKeys.Error));
        Said();
        line.TextChanged += (_, _) =>
        {
            change(s => set(s, string.IsNullOrWhiteSpace(line.Text) ? null : line.Text.Trim()));
            Said();
        };
        return new StackPanel { Spacing = 4, Children = { line, problem } };

        void Said()
        {
            problem.IsVisible = Run.CommandLine.NamesNoProgram(line.Text);
            problem.Text = problem.IsVisible ? $"This {NamesNone}" : null;
        }
    }

    public override AppSettings Clamp(AppSettings settings) => string.IsNullOrWhiteSpace(get(settings)) ? set(settings, null) : settings;

    public override string? Refused(AppSettings was, AppSettings now) => get(now) is { } line && line != get(was) && Run.CommandLine.NamesNoProgram(line)
        ? $"{Row!.Label} {line.Trim()} ({SettingsDialog.PlaceOfPage(Row.Page)}) {NamesNone}"
        : null;
}

/// <summary>A font family to show code in, by name (<paramref name="property"/>'s), chosen from a drop-down of the carried
/// font and the installed ones whose characters take one width (<see cref="CodeFonts"/>), each named in its own face, the
/// list filled once they are found off the UI thread; null for the first choice, <paramref name="none"/>'s (the carried
/// font where it names none). A name not installed or not monospaced is refused, saying why.</summary>
public sealed class FontSetting(string property, Func<AppSettings, string?> get, Func<AppSettings, string?, AppSettings> set, string? none, SettingRow row)
    : Setting(row)
{
    /// <summary>The AppSettings property it reads.</summary>
    public string Property => property;

    /// <summary>The names it takes, the carried font's first.</summary>
    public static async Task<IReadOnlyList<string>> ChoicesAsync() => [CodeFonts.Carried, .. await CodeFonts.InstalledAsync()];

    public override Control Editor(AppSettings now, Action<Func<AppSettings, AppSettings>> change)
    {
        string? chosen = Trimmed(get(now));
        ComboBox box = new() { Classes = { StyleClasses.Form }, MaxDropDownHeight = 360 };
        ReusedRows.Use(box, () =>
        {
            TextBlock shown = new() { VerticalAlignment = VerticalAlignment.Center };
            return shown.Showing<Choice>(choice =>
            {
                shown.Text = choice.Shown;
                shown.FontFamily = choice.Face;
            });
        });
        bool filling = false;
        Fill(null);
        box.SelectionChanged += (_, _) =>
        {
            if (!filling && box.SelectedItem is Choice choice)
            {
                chosen = choice.Name;
                change(s => set(s, choice.Name));
            }
        };
        IdeErrors.Observe(FillAsync());
        return box;

        async Task FillAsync() => Fill(await CodeFonts.InstalledAsync());

        // Before the installed fonts are found, the list holds the first choice and the one in force.
        void Fill(IReadOnlyList<string>? installed)
        {
            List<Choice> choices = [none is null ? new(null, CodeFonts.Carried, CodeEditor.CodeFont) : new(null, none, FontFamily.Default)];
            if (none is not null)
            {
                choices.Add(new(CodeFonts.Carried, CodeFonts.Carried, CodeEditor.CodeFont));
            }
            choices.AddRange((installed ?? []).Select(name => new Choice(name, name, new FontFamily(name))));
            int at = choices.FindIndex(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase));
            if (at < 0 && chosen is not null)
            {
                string shown = installed is null ? chosen : $"{chosen} ({(CodeFonts.IsInstalled(chosen) ? "not monospaced" : "not installed")})";
                choices.Insert(1, new(chosen, shown, CodeFonts.Named(chosen) ?? FontFamily.Default));
                at = 1;
            }
            filling = true;
            box.ItemsSource = choices;
            box.SelectedIndex = Math.Max(0, at);
            filling = false;
        }
    }

    public override AppSettings Clamp(AppSettings settings) => set(settings, Trimmed(get(settings)));

    public override string? Refused(AppSettings was, AppSettings now) =>
        Trimmed(get(now)) is { } name && !string.Equals(name, Trimmed(get(was)), StringComparison.OrdinalIgnoreCase) && CodeFonts.WhyNot(name) is { } why
            ? $"{Row!.Label} {name} ({SettingsDialog.PlaceOfPage(Row.Page)}) {why}"
            : null;

    private static string? Trimmed(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>A row of the list: the name it sets (null for the first), the words shown, and the face they are shown in.</summary>
    private sealed record Choice(string? Name, string Shown, FontFamily Face)
    {
        public override string ToString() => Shown;
    }
}
