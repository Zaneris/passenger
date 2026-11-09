using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>Where a setting's own row stands in Settings and what finds it: its page, its label, the words a search
/// matches beside those, and a note under its editor where what it does needs saying.</summary>
public sealed record SettingRow(string Page, string Label, string Words, string? Note = null)
{
    /// <summary>Whether its row takes effect under the settings as they stand, and why not where not: a row another greys
    /// while it is off (a line length while wrapping is off).</summary>
    public SettingWhen? When { get; init; }
}

/// <summary>What a row takes effect under, and the words said on it while it does not.</summary>
public sealed record SettingWhen(Func<AppSettings, bool> Holds, string Why);

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
/// nothing) is refused, saying why under the box. Where it is given <paramref name="described"/>, what the line in force
/// runs (its version, say) is said under the box as it is typed, once found off the UI thread.</summary>
public sealed class ProgramSetting(Func<AppSettings, string?> get, Func<AppSettings, string?, AppSettings> set, Func<string> found, SettingRow row,
    Func<string?, Task<string>>? described = null) : Setting(row)
{
    /// <summary>Why a line naming no program is refused.</summary>
    private const string NamesNone = "names no program to start: the program's path comes first, its arguments after it";

    public override Control Editor(AppSettings now, Action<Func<AppSettings, AppSettings>> change)
    {
        TextBox line = new() { Classes = { StyleClasses.Search }, Text = get(now) ?? string.Empty, PlaceholderText = found(), Width = 320 };
        TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        problem.Classes.Add(StyleClasses.Error);
        TextBlock runs = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = described is not null };
        int asked = 0;
        Said();
        line.TextChanged += (_, _) =>
        {
            change(s => set(s, string.IsNullOrWhiteSpace(line.Text) ? null : line.Text.Trim()));
            Said();
        };
        return new StackPanel { Spacing = 4, Children = { line, problem, runs } };

        // What the line runs, the last asked winning.
        async Task DescribeAsync(string? typed)
        {
            int mine = ++asked;
            string words = await described!(typed);
            if (mine == asked)
            {
                runs.Text = words;
            }
        }

        void Said()
        {
            problem.IsVisible = Run.CommandLine.NamesNoProgram(line.Text);
            problem.Text = problem.IsVisible ? $"This {NamesNone}" : null;
            if (described is not null && !problem.IsVisible)
            {
                IdeErrors.Observe(DescribeAsync(string.IsNullOrWhiteSpace(line.Text) ? null : line.Text.Trim()));
            }
        }
    }

    public override AppSettings Clamp(AppSettings settings) => string.IsNullOrWhiteSpace(get(settings)) ? set(settings, null) : settings;

    public override string? Refused(AppSettings was, AppSettings now) => get(now) is { } line && line != get(was) && Run.CommandLine.NamesNoProgram(line)
        ? $"{Row!.Label} {line.Trim()} ({SettingsDialog.PlaceOfPage(Row.Page)}) {NamesNone}"
        : null;
}

/// <summary>A font family by name (<paramref name="property"/>'s), chosen from a drop-down of what <paramref name="fonts"/>
/// offers (the carried font, any choices of its own, the installed families it takes), each named in its own face, the
/// list filled once the installed ones are found off the UI thread; null for the first choice, <paramref name="none"/>'s
/// (the carried font where it names none). A name the fonts refuse is refused, saying why.</summary>
public sealed class FontSetting(string property, Func<AppSettings, string?> get, Func<AppSettings, string?, AppSettings> set, string? none,
    FontChoices fonts, SettingRow row) : Setting(row)
{
    /// <summary>The AppSettings property it reads.</summary>
    public string Property => property;

    /// <summary>The names it takes, the carried font's first.</summary>
    public async Task<IReadOnlyList<string>> ChoicesAsync() => [.. Offered, .. await InstalledAsync()];

    /// <summary>The names it offers of its own: the carried font's, then its own choices (the system's UI font).</summary>
    public IReadOnlyList<string> Offered => [fonts.Carried, .. fonts.Own().Select(own => own.Name)];

    /// <summary>The installed families it takes, by name in order.</summary>
    public Task<IReadOnlyList<string>> InstalledAsync() => fonts.InstalledAsync();

    public override Control Editor(AppSettings now, Action<Func<AppSettings, AppSettings>> change)
    {
        string? chosen = Trimmed(get(now));
        ComboBox box = new() { Classes = { StyleClasses.Form, StyleClasses.Faces }, MaxDropDownHeight = 360 };
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

        async Task FillAsync() => Fill(await fonts.InstalledAsync());

        // Before the installed fonts are found, the list holds the first choice, the fonts' own and the one in force.
        void Fill(IReadOnlyList<string>? installed)
        {
            List<Choice> choices = [none is null ? new(null, fonts.Carried, fonts.Face) : new(null, none, FontFamily.Default)];
            if (none is not null)
            {
                choices.Add(new(fonts.Carried, fonts.Carried, fonts.Face));
            }
            choices.AddRange(fonts.Own().Select(own => new Choice(own.Name, own.Shown, own.Face)));
            choices.AddRange((installed ?? []).Select(name => new Choice(name, name, new FontFamily(name))));
            int at = choices.FindIndex(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase));
            if (at < 0 && none is null && string.Equals(chosen, fonts.Carried, StringComparison.OrdinalIgnoreCase))
            {
                at = 0;
            }
            if (at < 0 && chosen is not null)
            {
                string shown = installed is null ? chosen : $"{chosen} ({fonts.Unusable(chosen)})";
                choices.Insert(1, new(chosen, shown, fonts.Named(chosen) ?? FontFamily.Default));
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
        Trimmed(get(now)) is { } name && !string.Equals(name, Trimmed(get(was)), StringComparison.OrdinalIgnoreCase) && fonts.WhyNot(name) is { } why
            ? $"{Row!.Label} {name} ({SettingsDialog.PlaceOfPage(Row.Page)}) {why}"
            : null;

    private static string? Trimmed(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>A row of the list: the name it sets (null for the first), the words shown, and the face they are shown in.</summary>
    private sealed record Choice(string? Name, string Shown, FontFamily Face)
    {
        public override string ToString() => Shown;
    }
}

/// <summary>What a font setting offers and takes: the font Passenger carries, by its name and face, any choices of its
/// own after it, the installed families it takes, and why it refuses a name.</summary>
public abstract class FontChoices
{
    /// <summary>Code's: the carried JetBrains Mono or an installed monospaced family (<see cref="CodeFonts"/>).</summary>
    public static readonly FontChoices Code = new CodeChoices();

    /// <summary>The window's: the carried Inter, the system's own UI font or any installed family (<see cref="WindowFonts"/>).</summary>
    public static readonly FontChoices Window = new WindowChoices();

    /// <summary>The carried font's name.</summary>
    public abstract string Carried { get; }

    /// <summary>The carried font.</summary>
    public abstract FontFamily Face { get; }

    /// <summary>The choices after the carried font and before the installed families: each the name it sets, the words
    /// shown and its face.</summary>
    public virtual IReadOnlyList<(string Name, string Shown, FontFamily Face)> Own() => [];

    /// <summary>The installed families it takes, by name in order.</summary>
    public abstract Task<IReadOnlyList<string>> InstalledAsync();

    /// <summary>The family a name gives, or null for one it refuses.</summary>
    public abstract FontFamily? Named(string name);

    /// <summary>Why a name gives no font, as words after it; null where it gives one.</summary>
    public abstract string? WhyNot(string name);

    /// <summary>A name it refuses, as the list says so after it: "not installed".</summary>
    public abstract string Unusable(string name);

    private sealed class CodeChoices : FontChoices
    {
        public override string Carried => CodeFonts.Carried;

        public override FontFamily Face => CodeEditor.CodeFont;

        public override Task<IReadOnlyList<string>> InstalledAsync() => CodeFonts.InstalledAsync();

        public override FontFamily? Named(string name) => CodeFonts.Named(name);

        public override string? WhyNot(string name) => CodeFonts.WhyNot(name);

        public override string Unusable(string name) => CodeFonts.IsInstalled(name) ? "not monospaced" : "not installed";
    }

    private sealed class WindowChoices : FontChoices
    {
        public override string Carried => WindowFontNames.Carried;

        public override FontFamily Face => WindowFonts.Carried;

        public override IReadOnlyList<(string Name, string Shown, FontFamily Face)> Own() =>
            [(WindowFontNames.SystemUi, WindowFonts.SystemShown(), WindowFonts.Named(WindowFontNames.SystemUi) ?? FontFamily.Default)];

        public override Task<IReadOnlyList<string>> InstalledAsync() => WindowFonts.InstalledAsync();

        public override FontFamily? Named(string name) => WindowFonts.Named(name);

        public override string? WhyNot(string name) => WindowFonts.WhyNot(name);

        public override string Unusable(string name) => "not installed";
    }
}
