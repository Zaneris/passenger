using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;

namespace Passenger.App.Settings;

/// <summary>Appearance's themes: the theme and the code colour scheme, each chosen from every source's (<see
/// cref="ThemePicker"/>), and the sources themselves: Passenger's themes repository, listed always, and those the person
/// adds (a folder, a GitHub repository or an address), each read before it is added, one refused saying why and changing
/// nothing; Remove takes one back, and Refresh reads every source again, taking the newer copy of each theme kept from it.</summary>
public sealed partial class SettingsDialog
{
    /// <summary>The theme's drop-down and Add from File.</summary>
    public ThemePicker ThemePicker { get; private set; } = null!;

    /// <summary>The code colour scheme's drop-down and Add from File.</summary>
    public ThemePicker SchemePicker { get; private set; } = null!;

    /// <summary>The theme sources listed: Passenger's themes repository, then the person's.</summary>
    public ListBox ThemeSourceList { get; } = new() { Classes = { StyleClasses.Tree }, Height = 96 };

    /// <summary>Where a source to add is written.</summary>
    public TextBox ThemeSourceBox { get; } = new()
    {
        Classes = { StyleClasses.Search }, PlaceholderText = "A folder, owner/repository or https:// address", Width = 300,
    };

    public Button AddThemeSourceButton { get; } = new() { Content = "Add" };

    public Button RemoveThemeSourceButton { get; } = new() { Content = "Remove", IsEnabled = false };

    public Button RefreshThemesButton { get; } = new() { Content = "Refresh" };

    /// <summary>Why the last source asked for was not added, or what a refresh came to.</summary>
    public TextBlock ThemeSourceWords { get; } = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };

    private void AddThemes()
    {
        ThemePicker = new ThemePicker(Declared.Look, () => Working, Change);
        SchemePicker = new ThemePicker(Declared.ColourScheme, () => Working, Change);
        Add(Declared.Look, ThemePicker);
        Add(Declared.Theme);
        Add(Declared.ColourScheme, SchemePicker);

        void List()
        {
            ThemeSourceList.ItemsSource = (string[])[ThemeSource.Repository.Shown, .. Working.ThemeSources];
            RemoveThemeSourceButton.IsEnabled = ThemeSourceList.SelectedIndex > 0;
        }
        ThemeSourceList.SelectionChanged += (_, _) => RemoveThemeSourceButton.IsEnabled = ThemeSourceList.SelectedIndex > 0;
        RemoveThemeSourceButton.Click += (_, _) =>
        {
            if (ThemeSourceList.SelectedIndex > 0 && ThemeSourceList.SelectedItem is string source)
            {
                Change(s => s with { ThemeSources = [.. s.ThemeSources.Where(t => t != source)] });
                List();
                Sources();
            }
        };
        AddThemeSourceButton.Click += (_, _) => IdeErrors.Observe(AddSourceAsync());
        ThemeSourceBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                IdeErrors.Observe(AddSourceAsync());
            }
        };
        RefreshThemesButton.Click += (_, _) => IdeErrors.Observe(RefreshThemesAsync());
        List();
        StackPanel sources = new()
        {
            Spacing = 6,
            Width = 420,
            Children =
            {
                new Border { Classes = { StyleClasses.Framed }, Child = ThemeSourceList },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ThemeSourceBox, AddThemeSourceButton } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { RemoveThemeSourceButton, RefreshThemesButton } },
                ThemeSourceWords,
            },
        };
        Add(Appearance, "Theme sources", sources, "theme themes colour color scheme source sources repository github folder address url add remove refresh passenger-themes",
            "Where themes and code colour schemes come from. Passenger reads a source only when you look at its themes, choose one or "
            + "refresh. A theme you choose is kept, so it works offline; Refresh takes a newer copy.");

        // Sources read again in the lists, as one is added or removed.
        void Sources()
        {
            ThemePicker.Refill();
            SchemePicker.Refill();
            ThemeListing.Look(Working.ThemeSources.Select(s => ThemeSource.Of(s, out _)).OfType<ThemeSource>());
        }

        async Task AddSourceAsync()
        {
            string words = ThemeSourceBox.Text ?? string.Empty;
            if (await ThemeSourceAdded(words) is { } why)
            {
                SayOfSources(why, problem: true);
                return;
            }
            ThemeSourceBox.Text = string.Empty;
            SayOfSources(null, problem: false);
            List();
            Sources();
        }
    }

    /// <summary>A source added from the words naming it, once read; why not where it names none or cannot be read.</summary>
    private async Task<string?> ThemeSourceAdded(string words)
    {
        if (ThemeSource.Of(words, out string? why) is not { } source)
        {
            return $"This source is not added: {why}";
        }
        if (source.Kind is not (ThemeSourceKind.Folder or ThemeSourceKind.GitHub or ThemeSourceKind.Web))
        {
            return $"{words.Trim()} names Passenger's own themes, listed always";
        }
        if (source.Kind == ThemeSourceKind.Folder && Directory.Exists(source.Name))
        {
            source = new ThemeSource(ThemeSourceKind.Folder, PathNames.Canonical(source.Name));
        }
        if (source == ThemeSource.Repository || Working.ThemeSources.Contains(source.Name, StringComparer.Ordinal))
        {
            return $"{source.Shown} is listed already";
        }
        SourceRead read = await ThemeListing.ReadAsync(source);
        if (read.Refused is { } refused)
        {
            return $"{source.Shown} is not added: {refused}";
        }
        Change(s => s with { ThemeSources = [.. s.ThemeSources, source.Name] });
        return null;
    }

    private async Task RefreshThemesAsync()
    {
        RefreshThemesButton.IsEnabled = false;
        try
        {
            ThemeSource[] sources = [ThemeSource.Repository, .. Working.ThemeSources.Select(s => ThemeSource.Of(s, out _)).OfType<ThemeSource>()];
            int taken = await ThemeListing.RefreshAsync(sources);
            string[] refused = [.. sources.Select(ThemeListing.Read).OfType<SourceRead>().Where(r => r.Refused is not null).Select(r => $"{r.Source.Shown} could not be read: {r.Refused}")];
            SayOfSources(refused.Length > 0 ? string.Join("\n", refused) : taken == 0 ? "Every kept theme is as its source has it" : $"{Plural.Of(taken, "kept theme")} taken anew from {(taken == 1 ? "its source" : "their sources")}",
                problem: refused.Length > 0);
        }
        finally
        {
            RefreshThemesButton.IsEnabled = true;
        }
    }

    private void SayOfSources(string? words, bool problem)
    {
        ThemeSourceWords.Text = words;
        ThemeSourceWords.IsVisible = words is not null;
        ThemeSourceWords.Classes.Set(StyleClasses.Error, problem);
        ThemeSourceWords.Classes.Set(StyleClasses.Muted, !problem);
    }

    /// <summary>A declared setting's own row, its editor made by the dialog.</summary>
    private void Add(Setting setting, Control editor)
    {
        SettingRow row = setting.Row ?? throw new ArgumentException("A setting with no row of its own goes in a row the dialog builds", nameof(setting));
        Add(row.Page, row.Label, editor, row.Words, row.Note);
    }
}
