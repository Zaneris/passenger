using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Passenger.Core;

namespace Passenger.App.Settings;

/// <summary>A theme or a code colour scheme by its source and name, or null for Passenger's own (the theme's own colours),
/// chosen from what every source lists (<see cref="ThemePicker"/>).</summary>
public sealed class ThemeSetting(bool code, Func<AppSettings, SourcedTheme?> get, Func<AppSettings, SourcedTheme?, AppSettings> set, SettingRow row) : Setting(row)
{
    /// <summary>Whether it chooses the code's colours rather than the theme.</summary>
    public bool Code => code;

    public SourcedTheme? Get(AppSettings settings) => get(settings);

    public AppSettings Set(AppSettings settings, SourcedTheme? theme) => set(settings, theme);

    public override Control Editor(AppSettings now, Action<Func<AppSettings, AppSettings>> change) => new ThemePicker(this, () => now, change);

    public override AppSettings Clamp(AppSettings settings) => settings;
}

/// <summary>A theme's or a code colour scheme's drop-down: Passenger's own first, then each source's by name with the source
/// after it, Passenger's themes repository first, then the person's sources and the files they added. Before a source is
/// read it lists what is kept from it; opened, it reads each source not read yet in this process, saying which it reads and
/// why one could not be. A choice from a source is kept as it is chosen, so the preview and every later start draw it
/// without the source; Add from File reads a file of the person's, says why one is refused and changes nothing then.</summary>
public sealed class ThemePicker : StackPanel
{
    private readonly ThemeSetting setting;
    private readonly Func<AppSettings> working;
    private readonly Action<Func<AppSettings, AppSettings>> change;
    private readonly TextBlock reading = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock problems = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
    private bool filling;
    private string? said;

    public ThemePicker(ThemeSetting setting, Func<AppSettings> working, Action<Func<AppSettings, AppSettings>> change)
    {
        this.setting = setting;
        this.working = working;
        this.change = change;
        Spacing = 4;
        AutomationProperties.SetName(Box, setting.Row?.Label);
        ReusedRows.Use(Box, () =>
        {
            TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
            TextBlock source = new() { Classes = { StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center };
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { name, source } }.Showing<Choice>(choice =>
            {
                name.Text = choice.Name;
                source.Text = choice.Source;
            });
        });
        Box.DropDownOpened += (_, _) => ThemeListing.Look(Sources());
        Box.SelectionChanged += (_, _) =>
        {
            if (!filling && Box.SelectedItem is Choice choice)
            {
                IdeErrors.Observe(ChooseAsync(choice));
            }
        };
        AddFile.Click += (_, _) => IdeErrors.Observe(PickFileAsync());
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Box, AddFile } });
        Children.Add(reading);
        Children.Add(problems);
        AttachedToVisualTree += (_, _) =>
        {
            ThemeListing.Changed += SourcesRead;
            Fill();
        };
        DetachedFromVisualTree += (_, _) => ThemeListing.Changed -= SourcesRead;
        Fill();
    }

    /// <summary>The drop-down.</summary>
    public ComboBox Box { get; } = new() { Classes = { StyleClasses.Form }, MinWidth = 260, MaxDropDownHeight = 360 };

    /// <summary>Add from File…: a theme or code colour scheme file of the person's, kept as added and chosen.</summary>
    public Button AddFile { get; } = new() { Content = "Add from File…" };

    /// <summary>The names listed, each with its source after it where it has one, in order.</summary>
    public IReadOnlyList<string> Listed => [.. (Box.ItemsSource as IEnumerable<Choice> ?? []).Select(c => c.Source.Length == 0 ? c.Name : $"{c.Name} ({c.Source})")];

    /// <summary>What it says of a source being read.</summary>
    public string? Reading => reading.IsVisible ? reading.Text : null;

    /// <summary>Why a source, a file it lists or a file added could not be read; null for nothing.</summary>
    public string? Problems => problems.IsVisible ? problems.Text : null;

    /// <summary>Chooses the listed theme of a name (and source, where two share it), as a click on it does.</summary>
    public void Choose(string name, string? source = null) =>
        Box.SelectedItem = (Box.ItemsSource as IEnumerable<Choice> ?? []).First(c => c.Name == name && (source is null || c.Source == source));

    /// <summary>A file of the person's added as a theme or scheme and chosen, or why not, said under the drop-down.</summary>
    public async Task<string?> AddFileAsync(string path)
    {
        IReadOnlyDictionary<string, ThemeTokenKind> kinds = ThemeResources.Kinds;
        bool code = setting.Code;
        (ThemeRead<ThemeFile>? read, byte[]? bytes, string? why) = await BackgroundWork.Run<(ThemeRead<ThemeFile>?, byte[]?, string?)>(() =>
        {
            (byte[]? found, string? unread) = ThemeSourceReads.FromFile(path);
            return found is null ? (null, null, unread)
                : (code ? ThemeFiles.ReadCodeColours(path, found, kinds) : ThemeFiles.ReadTheme(path, found, kinds), found, null);
        });
        if (read?.Read is not { } file || bytes is null)
        {
            Say(why ?? read?.Refused);
            return why ?? read?.Refused;
        }
        if (await BackgroundWork.Run(() => KeptThemes.Keep(ThemeSource.Added, code, file.Name, bytes)) is { } unkept)
        {
            Say(unkept);
            return unkept;
        }
        Themes.Reload();
        Say(read.Problems.Count == 0 ? null : string.Join("\n", read.Problems));
        change(s => setting.Set(s, new SourcedTheme(ThemeSource.Added.Name, file.Name)));
        Fill();
        return null;
    }

    private async Task PickFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = setting.Code ? "Add Code Colour Scheme" : "Add Theme",
            FileTypeFilter = [new FilePickerFileType("Passenger theme files") { Patterns = ["*.json"] }],
        });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
        {
            await AddFileAsync(path);
        }
    }

    /// <summary>The sources listed: Passenger's themes repository, then the person's.</summary>
    private IEnumerable<ThemeSource> Sources() =>
        [ThemeSource.Repository, .. working().ThemeSources.Select(s => ThemeSource.Of(s, out _)).OfType<ThemeSource>()];

    private async Task ChooseAsync(Choice choice)
    {
        if (choice is { Value: { } theme, Listed: { Bytes: { } bytes } } && theme.From is { } source && !KeptThemes.Holds(source, setting.Code, theme.Name))
        {
            bool code = setting.Code;
            if (await BackgroundWork.Run(() => KeptThemes.Keep(source, code, theme.Name, bytes)) is { } why)
            {
                Say(why);
                Fill();
                return;
            }
        }
        // Changed before the list is made again, which chooses the row the settings name.
        change(s => setting.Set(s, choice.Value));
        Say(null);
    }

    /// <summary>The list made again, as the sources listed changed.</summary>
    public void Refill() => Fill();

    private void SourcesRead(object? sender, EventArgs args) => Fill();

    private void Say(string? why)
    {
        said = why;
        Fill();
    }

    /// <summary>The list as the sources stand: each read one's, else what is kept from it, the chosen one always.</summary>
    private void Fill()
    {
        bool code = setting.Code;
        SourcedTheme? chosen = setting.Get(working());
        List<Choice> choices = [new(null, code ? "Same as theme" : "Passenger", string.Empty, null)];
        List<string> refused = [];
        List<string> underWay = [];
        SourceRead builtIn = BuiltInThemes.Listed(ThemeResources.Kinds);
        Add(ThemeSource.BuiltIn, builtIn.Listed);
        foreach (ThemeSource source in Sources().Append(ThemeSource.Added))
        {
            if (ThemeListing.IsReading(source))
            {
                underWay.Add(source.Shown);
            }
            if (ThemeListing.Read(source) is { } read)
            {
                if (read.Refused is { } why)
                {
                    refused.Add($"{source.Shown} could not be read: {why}");
                }
                refused.AddRange(read.Listed.Where(l => l.Code == code && l.Read.Refused is not null).Select(l => l.Read.Refused!));
                Add(source, read.Listed);
            }
            foreach (ThemeFile kept in ThemeListing.Kept(source, code))
            {
                Add(source, [], kept.Name);
            }
        }
        if (chosen is not null && !choices.Any(c => c.Value == chosen))
        {
            choices.Add(new(chosen, chosen.Name, $"{chosen.From?.Shown ?? chosen.Source}, not kept yet", null));
        }
        filling = true;
        Box.ItemsSource = choices;
        Box.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Value == chosen));
        filling = false;
        reading.Text = underWay.Count == 0 ? null : $"Reading {string.Join(", ", underWay)}…";
        reading.IsVisible = underWay.Count > 0;
        string[] lines = [.. (said is null ? [] : new[] { said }), .. refused];
        problems.Text = lines.Length == 0 ? null : string.Join("\n", lines);
        problems.IsVisible = lines.Length > 0;

        void Add(ThemeSource source, IEnumerable<ListedTheme> listed, string? kept = null)
        {
            foreach (string name in kept is null ? listed.Where(l => l.Code == code).Select(l => l.Read.Read?.Name).OfType<string>() : [kept])
            {
                SourcedTheme value = new(source.Name, name);
                if (!choices.Any(c => c.Value == value))
                {
                    choices.Add(new(value, name, source == ThemeSource.BuiltIn ? string.Empty : source.Shown, listed.FirstOrDefault(l => l.Code == code && l.Read.Read?.Name == name)));
                }
            }
        }
    }

    /// <summary>A row of the list: what it sets (null for Passenger's own), its name, its source as shown, and its file as
    /// read from the source, kept as it is chosen.</summary>
    private sealed record Choice(SourcedTheme? Value, string Name, string Source, ListedTheme? Listed)
    {
        public override string ToString() => Name;
    }
}
