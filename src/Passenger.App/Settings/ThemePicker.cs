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
/// without the source; Add from File reads a file of the person's (a Passenger theme, a VS Code theme's JSON or a .vsix),
/// says why one is refused and changes nothing then.</summary>
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
        // As wide as a form's other drop-downs where there is room, narrower where not.
        NarrowingField choosing = new(Box, AddFile);
        choosing.Bind(NarrowingField.MostProperty, this.GetResourceObservable(TokenKeys.DropDownWidth));
        Children.Add(choosing);
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
    public ComboBox Box { get; } = new() { MaxDropDownHeight = 360 };

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

    /// <summary>A file of the person's added as a theme or scheme and chosen, or why not, said under the drop-down: a
    /// Passenger theme file, a VS Code colour theme's JSON (the files it includes beside it), or a <c>.vsix</c>, each colour
    /// theme it contributes added and the first chosen; as a code colour scheme, a VS Code theme gives its token and
    /// semantic colours alone.</summary>
    public async Task<string?> AddFileAsync(string path)
    {
        IReadOnlyDictionary<string, ThemeTokenKind> kinds = ThemeResources.Kinds;
        bool code = setting.Code;
        (List<string>? names, IReadOnlyList<string> problems, string? why) = await BackgroundWork.Run(async () =>
        {
            List<Added> added = [];
            List<string> said = [];
            string? refused = path.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase) ? FromVsix(path, added, said)
                : await FromJsonAsync(path, code, kinds, added, said);
            refused ??= added.Select(a => KeptThemes.KeepBase(ThemeSource.Added, code, a.Name, a.Base)
                ?? KeptThemes.Keep(ThemeSource.Added, code, a.Name, a.Bytes, a.Beside)).FirstOrDefault(w => w is not null);
            return (refused is null ? added.Select(a => a.Name).ToList() : null, (IReadOnlyList<string>)said, refused);
        });
        if (names is not [string first, ..])
        {
            Say(why);
            return why;
        }
        Themes.Reload();
        Say(problems.Count == 0 ? null : string.Join("\n", problems));
        change(s => setting.Set(s, new SourcedTheme(ThemeSource.Added.Name, first)));
        Fill();
        return null;
    }

    /// <summary>A theme added from a file: its name, its file as kept, the files kept beside it, and its VS Code base's.</summary>
    private sealed record Added(string Name, byte[] Bytes, Dictionary<string, byte[]> Beside, IReadOnlyDictionary<string, byte[]> Base);

    /// <summary>Every colour theme a .vsix contributes, each a theme over it kept with its files; why not where none reads.</summary>
    private static string? FromVsix(string path, List<Added> added, List<string> said)
    {
        byte[] bytes;
        try
        {
            long length = new FileInfo(path).Length;
            if (length > VsCodeThemes.MostVsixBytes)
            {
                return $"{path} is {length} bytes, more than the {VsCodeThemes.MostVsixBytes} a .vsix may hold";
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{path} could not be read: {error.Message}";
        }
        if (VsCodeThemes.Vsix(bytes, out string? unzipped) is not { } files)
        {
            return $"{path} is refused: {unzipped}";
        }
        ThemeRead<VsCodeExtension> manifest = VsCodeThemes.ReadManifest(VsCodeThemes.Manifest, files(VsCodeThemes.Manifest).Bytes);
        if (manifest.Read is not { } extension)
        {
            return $"{path} is refused: {manifest.Refused}";
        }
        if (extension.Themes.Count == 0)
        {
            return $"{path} contributes no colour theme";
        }
        foreach (VsCodeContribution theme in extension.Themes)
        {
            (VsCodeTaken? taken, string? why) = VsCodeThemes.FromVsix(bytes, theme.Label);
            if (taken is null)
            {
                said.Add($"{theme.Label} in {path} is refused: {why}");
                continue;
            }
            said.AddRange(taken.Problems);
            added.Add(new Added(theme.Label, Wrapper(theme.Label, new Dictionary<string, string>
            {
                ["id"] = extension.Id, ["theme"] = theme.Label, ["version"] = extension.Version,
            }), [], taken.Files));
        }
        return added.Count > 0 ? null : string.Join("\n", said);
    }

    /// <summary>A Passenger theme file with the pictures and VS Code theme it names beside it, or a VS Code theme's JSON with
    /// the files it includes, kept as a theme over it; why not where it cannot be read.</summary>
    private static async Task<string?> FromJsonAsync(string path, bool code, IReadOnlyDictionary<string, ThemeTokenKind> kinds, List<Added> added, List<string> said)
    {
        (byte[]? found, string? unread) = ThemeSourceReads.FromFile(path);
        if (found is null)
        {
            return unread;
        }
        if (VsCodeThemes.IsVsCode(found))
        {
            Dictionary<string, byte[]> read = new(StringComparer.Ordinal);
            ThemeRead<VsCodeTheme> vs = VsCodeThemes.ReadTheme(Path.GetFileName(path), null, null, file =>
            {
                (byte[]? bytes, string? why) = ThemeSourceReads.BesideFile(path, file);
                if (bytes is not null)
                {
                    read[$"{KeptThemes.BaseFolder}/{file}"] = bytes;
                }
                return (bytes, why);
            });
            if (vs.Read is not { } theme)
            {
                return $"{path} is refused: {vs.Refused}";
            }
            said.AddRange(vs.Problems);
            added.Add(new Added(theme.Name, Wrapper(theme.Name, new Dictionary<string, string> { ["file"] = $"{KeptThemes.BaseFolder}/{Path.GetFileName(path)}" }), read,
                new Dictionary<string, byte[]>()));
            return null;
        }
        ThemeRead<ThemeFile> own = code ? ThemeFiles.ReadCodeColours(path, found, kinds) : ThemeFiles.ReadTheme(path, found, kinds);
        if (own.Read is not { } whole)
        {
            return own.Refused;
        }
        (Dictionary<string, byte[]> beside, string? unseen) = await ThemeImages.ReadAsync(whole, image => Task.FromResult(ThemeSourceReads.BesideFile(path, image)));
        (Dictionary<string, byte[]> based, string? unbased) = await VsCodeBases.BesideAsync(whole, file => Task.FromResult(ThemeSourceReads.BesideFile(path, file)));
        foreach ((string file, byte[] bytes) in based)
        {
            beside[file] = bytes;
        }
        IReadOnlyDictionary<string, byte[]> fetched = new Dictionary<string, byte[]>();
        if (whole.Base is { Id: not null } wanted)
        {
            (VsCodeTaken? taken, string? why) = await VsCodeBases.FetchAsync(wanted, CancellationToken.None);
            if (taken is null)
            {
                return why;
            }
            fetched = taken.Files;
        }
        said.AddRange(own.Problems);
        added.Add(new Added(whole.Name, found, beside, fetched));
        return unseen ?? unbased;
    }

    /// <summary>A theme file laid over a VS Code theme and giving nothing of its own.</summary>
    private static byte[] Wrapper(string name, Dictionary<string, string> vscode) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["format"] = ThemeFiles.Format, ["name"] = name, ["vscode"] = vscode });

    private async Task PickFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = setting.Code ? "Add Code Colour Scheme" : "Add Theme",
            FileTypeFilter = [new FilePickerFileType("Passenger and VS Code themes") { Patterns = ["*.json", "*.vsix"] }],
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
        if (choice is { Value: { } theme, Listed: { Bytes: not null } listed } && theme.From is { } source && !KeptThemes.Holds(source, setting.Code, theme.Name))
        {
            string? why = await ThemeListing.KeepAsync(listed, theme.Name);
            if (why is not null)
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
        foreach (ThemeSource source in Sources().Append(ThemeSource.Added).Append(ThemeSource.OpenVsx))
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
