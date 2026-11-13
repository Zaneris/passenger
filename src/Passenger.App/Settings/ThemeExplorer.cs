using System.Globalization;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;

namespace Passenger.App.Settings;

/// <summary>Appearance's explorer of VS Code colour themes from Open VSX: closed until a person opens it, so Open VSX is
/// reached only while it is open or a theme is chosen; opened, it lists the themes category's most downloaded first, by
/// words, by rating, newest or by name on request, one row for each colour theme an extension contributes (icon themes
/// and the like left out), with its extension's name, publisher, downloads and rating. Choosing one fetches its extension's
/// <c>.vsix</c>, reads only its manifest and the theme's files, and keeps them as a theme laid over that base
/// (<see cref="ThemeSource.OpenVsx"/>), shown at once before Apply; one that cannot be read says why and changes
/// nothing.</summary>
public sealed class ThemeExplorer : StackPanel
{
    private static readonly string[] Orders = ["Most downloaded", "Best rated", "Newest", "Name"];
    private readonly Func<AppSettings> working;
    private readonly Action<Func<AppSettings, AppSettings>> change;
    private readonly Action chosen;
    private readonly List<ExplorerRow> rows = [];
    private readonly Dictionary<string, byte[]> fetched = new(StringComparer.Ordinal);
    private readonly StackPanel opened = new() { Spacing = 6, IsVisible = false };
    private CancellationTokenSource? searching;
    private CancellationTokenSource? choosing;
    private int found;
    private int total;
    private bool filling;

    public ThemeExplorer(Func<AppSettings> working, Action<Func<AppSettings, AppSettings>> change, Action chosen)
    {
        this.working = working;
        this.change = change;
        this.chosen = chosen;
        Spacing = 6;
        Width = 420;
        AutomationProperties.SetName(SearchBox, "Search VS Code themes");
        AutomationProperties.SetName(OrderBox, "Order of VS Code themes");
        AutomationProperties.SetName(List, "VS Code themes");
        OrderBox.ItemsSource = Orders;
        OrderBox.SelectedIndex = 0;
        ReusedRows.Use(List, () =>
        {
            TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock about = MutedText.Of(string.Empty);
            about.Margin = new Avalonia.Thickness(10, 0, 0, 0);
            DockPanel row = new() { Children = { label, about } };
            DockPanel.SetDock(label, Dock.Left);
            return row.Showing<ExplorerRow>(shown =>
            {
                label.Text = shown.Theme.Label;
                about.Text = shown.About;
            });
        });
        OpenButton.Click += (_, _) => Open();
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                IdeErrors.Observe(SearchAsync(more: false));
            }
        };
        OrderBox.SelectionChanged += (_, _) =>
        {
            if (opened.IsVisible)
            {
                IdeErrors.Observe(SearchAsync(more: false));
            }
        };
        MoreButton.Click += (_, _) => IdeErrors.Observe(SearchAsync(more: true));
        List.SelectionChanged += (_, _) =>
        {
            if (!filling && List.SelectedItem is ExplorerRow row)
            {
                IdeErrors.Observe(ChooseAsync(row));
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            searching?.Cancel();
            choosing?.Cancel();
        };
        opened.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { SearchBox, OrderBox } });
        opened.Children.Add(new Border { Classes = { StyleClasses.Framed }, Child = List });
        opened.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { MoreButton } });
        Children.Add(OpenButton);
        Children.Add(opened);
        Children.Add(Said);
    }

    /// <summary>Opens the explorer, searching Open VSX for its most downloaded colour themes.</summary>
    public Button OpenButton { get; } = new() { Content = "Browse VS Code Themes", Name = "BrowseVsCodeThemes" };

    /// <summary>The words searched for: a theme's or extension's name, its publisher, or words it is described by.</summary>
    public TextBox SearchBox { get; } = new()
    {
        Classes = { StyleClasses.Search }, PlaceholderText = "Name, publisher or words", Width = 236,
    };

    /// <summary>The order the themes are listed in.</summary>
    public ComboBox OrderBox { get; } = new() { Classes = { StyleClasses.Form }, Width = 176 };

    /// <summary>The colour themes found, one row each.</summary>
    public ListBox List { get; } = new() { Classes = { StyleClasses.Tree }, Height = 240 };

    /// <summary>Lists the next page of what the search found.</summary>
    public Button MoreButton { get; } = new() { Content = "Show More", IsVisible = false };

    /// <summary>What the explorer is doing, what it found, or why a search or a theme was refused.</summary>
    public TextBlock Said { get; } = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };

    /// <summary>Whether it is open, and so reaches Open VSX.</summary>
    public bool IsOpen => opened.IsVisible;

    /// <summary>Each row listed, as its theme's label, then its extension and publisher.</summary>
    public IReadOnlyList<string> Listed => [.. rows.Select(r => $"{r.Theme.Label} ({r.Entry.DisplayName}, {r.Entry.Namespace})")];

    /// <summary>The order searched in.</summary>
    public OpenVsxOrder Order
    {
        get => (OpenVsxOrder)Math.Max(0, OrderBox.SelectedIndex);
        set => OrderBox.SelectedIndex = (int)value;
    }

    /// <summary>The search or choice under way, for a test to wait for.</summary>
    public Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>Opens it and searches, as its button does.</summary>
    public void Open()
    {
        OpenButton.IsVisible = false;
        opened.IsVisible = true;
        IdeErrors.Observe(SearchAsync(more: false));
    }

    /// <summary>Chooses the row of a theme's label (and extension, where two share it), as a click on it does.</summary>
    public void Choose(string label, string? extension = null) =>
        List.SelectedItem = rows.First(r => r.Theme.Label == label && (extension is null || r.Entry.Id == extension));

    private Task SearchAsync(bool more)
    {
        Task search = SearchOnceAsync(more);
        Working = search;
        return search;
    }

    private async Task SearchOnceAsync(bool more)
    {
        searching?.Cancel();
        CancellationTokenSource cancel = searching = new CancellationTokenSource();
        string words = SearchBox.Text ?? string.Empty;
        OpenVsxOrder order = Order;
        int offset = more ? found : 0;
        Say("Searching Open VSX…", problem: false);
        MoreButton.IsEnabled = false;
        (List<ExplorerRow>? read, OpenVsxPage? page, string? why) = await BackgroundWork.Run(async () =>
        {
            (OpenVsxPage? listed, string? unread) = await OpenVsx.SearchAsync(words, order, offset, cancel.Token);
            if (listed is null)
            {
                return ((List<ExplorerRow>?)null, (OpenVsxPage?)null, unread);
            }
            // Each extension's manifest names its colour themes; four asked at once.
            using SemaphoreSlim turns = new(4);
            List<ExplorerRow>[] each = await Task.WhenAll(listed.Extensions.Select(async entry =>
            {
                await turns.WaitAsync(cancel.Token);
                try
                {
                    (VsCodeExtension? extension, _) = await OpenVsx.ManifestAsync(entry, cancel.Token);
                    return extension is null ? [] : extension.Themes.Select(t => new ExplorerRow(entry, t)).ToList();
                }
                finally
                {
                    turns.Release();
                }
            }));
            return ([.. each.SelectMany(r => r)], listed, null);
        }, cancel.Token);
        if (cancel.IsCancellationRequested)
        {
            return;
        }
        MoreButton.IsEnabled = true;
        if (read is null || page is null)
        {
            Say($"Open VSX could not be searched: {why}", problem: true);
            return;
        }
        filling = true;
        if (!more)
        {
            rows.Clear();
        }
        rows.AddRange(read);
        List.ItemsSource = rows.ToArray();
        List.SelectedItem = null;
        filling = false;
        found = offset + page.Extensions.Count;
        total = page.Total;
        MoreButton.IsVisible = found < total && page.Extensions.Count > 0;
        Say(rows.Count == 0 ? "Open VSX lists no colour theme for these words" : $"{Plural.Of(rows.Count, "colour theme")} from {Plural.Of(rows.Select(r => r.Entry.Id).Distinct().Count(), "extension")} on Open VSX",
            problem: false);
    }

    private async Task ChooseAsync(ExplorerRow row)
    {
        Task choice = ChooseOnceAsync(row);
        Working = choice;
        await choice;
    }

    private async Task ChooseOnceAsync(ExplorerRow row)
    {
        choosing?.Cancel();
        CancellationTokenSource cancel = choosing = new CancellationTokenSource();
        Say($"Fetching {row.Theme.Label} from Open VSX…", problem: false);
        string key = $"{row.Entry.Id} {row.Entry.Version}";
        byte[]? had = fetched.GetValueOrDefault(key);
        (string? name, byte[]? vsix, string? why) = await BackgroundWork.Run(async () =>
        {
            (byte[]? bytes, string? unread) = had is null ? await OpenVsx.VsixAsync(row.Entry, cancel.Token) : (had, null);
            if (bytes is null)
            {
                return ((string?)null, (byte[]?)null, unread);
            }
            (VsCodeTaken? taken, string? refused) = VsCodeThemes.FromVsix(bytes, row.Theme.Label);
            if (taken is null)
            {
                return (null, bytes, refused);
            }
            string named = KeptName(row);
            string? unkept = KeptThemes.KeepBase(ThemeSource.OpenVsx, false, named, taken.Files)
                ?? KeptThemes.Keep(ThemeSource.OpenVsx, false, named, Wrapper(named, row));
            return (unkept is null ? named : null, bytes, unkept);
        }, cancel.Token);
        if (vsix is not null)
        {
            fetched[key] = vsix;
        }
        if (cancel.IsCancellationRequested)
        {
            return;
        }
        if (name is null)
        {
            Say($"{row.Theme.Label} from {row.Entry.DisplayName} is not used: {why}", problem: true);
            return;
        }
        Themes.Reload();
        change(s => s with { Look = new SourcedTheme(ThemeSource.OpenVsx.Name, name) });
        chosen();
        Say($"{row.Theme.Label} from {row.Entry.DisplayName} {row.Entry.Version} is shown; Apply keeps it as your theme", problem: false);
    }

    /// <summary>The name a theme is kept under: its label, unless another extension's theme of that label is kept, then
    /// with its extension's name after it.</summary>
    private static string KeptName(ExplorerRow row)
    {
        string label = row.Theme.Label;
        byte[]? kept = KeptThemes.Bytes(ThemeSource.OpenVsx, false, label);
        VsCodeBase? held = kept is null ? null : ThemeFiles.ReadTheme(label, kept, new Dictionary<string, ThemeTokenKind>()).Read?.Base;
        return held is null || held.Id == row.Entry.Id ? label : $"{label} ({row.Entry.DisplayName})";
    }

    /// <summary>The theme kept for a choice: format 1, its name, and its base by id and label, its version left to Refresh.</summary>
    private static byte[] Wrapper(string name, ExplorerRow row) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["format"] = ThemeFiles.Format,
        ["name"] = name,
        ["vscode"] = new Dictionary<string, string> { ["id"] = row.Entry.Id, ["theme"] = row.Theme.Label },
    }, new JsonSerializerOptions { WriteIndented = true });

    private void Say(string? words, bool problem)
    {
        Said.Text = words;
        Said.IsVisible = words is not null;
        Said.Classes.Set(StyleClasses.Error, problem);
        Said.Classes.Set(StyleClasses.Muted, !problem);
    }

    /// <summary>A colour theme an extension found contributes, and what its row says of it.</summary>
    private sealed record ExplorerRow(OpenVsxEntry Entry, VsCodeContribution Theme)
    {
        public string About => string.Join(" · ", ((string?[])
        [
            Theme.Dark ? "Dark" : "Light",
            $"{Entry.DisplayName} by {Entry.Namespace}",
            $"{Count(Entry.Downloads)} downloads",
            Entry.Rating is { } rated ? $"rated {rated.ToString("0.0", CultureInfo.InvariantCulture)}" : null,
        ]).OfType<string>());

        public override string ToString() => Theme.Label;

        private static string Count(long many) => many switch
        {
            >= 1_000_000 => (many / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
            >= 1_000 => (many / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K",
            _ => many.ToString(CultureInfo.InvariantCulture),
        };
    }
}
