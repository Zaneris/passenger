using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Packages;
using Passenger.App.Welcome;
using Passenger.Core;
using Passenger.Packages;

namespace Passenger.App.ToolWindows;

/// <summary>The Sources tab of the NuGet window: the nuget.config files in effect for the folder, NuGet's order (the
/// solution's, the folders' above it, the user's, the machine's), and for the one chosen its Feeds, each enabled or not,
/// added, edited and deleted in that file; its Properties; and the Summary, the feeds a restore uses. A feed disabled is
/// disabled in the file that names it; one enabled is enabled wherever a file had disabled it, as dotnet nuget enables it.
/// The machine's files NuGet never writes, and neither does the tab.</summary>
public sealed class NuGetSourcesView : Grid
{
    private readonly NuGetView owner;
    private readonly ListBox files;
    private readonly StackPanel details = new() { Spacing = 6, Margin = new Thickness(14, 10) };
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private PackageSourceSet sources = PackageSourceSet.None;
    private bool drawing;

    public NuGetSourcesView(NuGetView owner)
    {
        this.owner = owner;
        ColumnDefinitions = new ColumnDefinitions("2*,1,5*");
        files = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<ConfigFile>((file, _) => file is null ? new TextBlock() : FileRow(file), supportsRecycling: false),
        };
        files.SelectionChanged += (_, _) =>
        {
            if (!drawing)
            {
                Draw();
            }
        };
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable(TokenKeys.Error));
        DockPanel right = new();
        DockPanel.SetDock(problem, Dock.Bottom);
        problem.Margin = new Thickness(14, 0, 14, 8);
        right.Children.Add(problem);
        right.Children.Add(new ScrollViewer { Content = details });
        HairlineDivider divider = new(GridResizeDirection.Columns) { [ColumnProperty] = 1 };
        SetColumn(right, 2);
        Children.Add(files);
        Children.Add(divider);
        Children.Add(right);
    }

    /// <summary>The files in effect, as listed.</summary>
    public IReadOnlyList<ConfigFile> Files => sources.Files;

    /// <summary>The file whose feeds are shown.</summary>
    public ConfigFile? Chosen => files.SelectedItem as ConfigFile;

    /// <summary>Why the last change could not be written, or null.</summary>
    public string? Problem => problem.IsVisible ? problem.Text : null;

    /// <summary>Shows a file's feeds, by its path.</summary>
    public void Choose(string path) => files.SelectedItem = Files.FirstOrDefault(f => Core.PathNames.Same(f.Path, path))
        ?? throw new ArgumentException($"{path} is not among the nuget.config files in effect: {string.Join(", ", Files.Select(f => f.Path))}");

    /// <summary>Takes the sources just read, keeping the file chosen while it is still in effect; at first the folder's
    /// own, else the first that can be written.</summary>
    internal void Show(PackageSourceSet loaded)
    {
        string? was = Chosen?.Path;
        sources = loaded;
        drawing = true;
        try
        {
            files.ItemsSource = loaded.Files;
            files.SelectedItem = loaded.Files.FirstOrDefault(f => was is not null && Core.PathNames.Same(f.Path, was))
                ?? loaded.Files.FirstOrDefault(f => !f.ReadOnly);
        }
        finally
        {
            drawing = false;
        }
        Draw();
    }

    private void Draw()
    {
        details.Children.Clear();
        if (sources.Problem is { } unread)
        {
            details.Children.Add(Error($"NuGet could not read the configuration: {unread}"));
        }
        if (Chosen is { } file)
        {
            DrawFile(file);
        }
        details.Children.Add(Title("Summary", new Thickness(0, 14, 0, 2)));
        if (!sources.Feeds.Any())
        {
            details.Children.Add(MutedText.Wrapped("No feeds: a restore finds no packages"));
        }
        foreach (FeedSource feed in sources.Feeds)
        {
            details.Children.Add(SummaryRow(feed));
        }
        if (sources.Mapped)
        {
            details.Children.Add(MutedText.Wrapped("Package source mapping:"));
            foreach ((string feed, IReadOnlyList<string> patterns) in sources.MappingPatterns)
            {
                details.Children.Add(MutedText.Wrapped($"    {feed}: {string.Join(", ", patterns)}"));
            }
        }
    }

    private void DrawFile(ConfigFile file)
    {
        Button add = ToolButtons.Of(IconKeys.Add, "Add Feed", () => IdeErrors.Observe(AddAsync(file)));
        add.IsEnabled = !file.ReadOnly && file.Problem is null;
        add.Name = "AddFeed";
        DockPanel header = new() { Children = { add, Title("Feeds", default) } };
        DockPanel.SetDock(add, Dock.Right);
        details.Children.Add(header);
        if (file.Problem is { } broken)
        {
            details.Children.Add(Error($"The file does not read: {broken}"));
            return;
        }
        if (file.ReadOnly)
        {
            details.Children.Add(MutedText.Wrapped("A machine-wide file: NuGet reads it and never writes it"));
        }
        if (file.Feeds.Count == 0)
        {
            details.Children.Add(MutedText.Wrapped("No feeds in this file"));
        }
        foreach (ConfigFeed feed in file.Feeds)
        {
            details.Children.Add(FeedRow(file, feed));
        }
        if (file.Clears)
        {
            details.Children.Add(MutedText.Wrapped("<clear /> here: the feeds of the files after this one are not used"));
        }
        details.Children.Add(Title("Properties", new Thickness(0, 14, 0, 2)));
        details.Children.Add(Property("Global packages folder", file.GlobalPackagesFolder));
        details.Children.Add(Property("Repository path", file.RepositoryPath));
    }

    /// <summary>A feed of the file: whether it is enabled, its name and URL, a lock when the file holds credentials for
    /// it, and Edit and Delete Feed. One the file names that a restore does not use is greyed.</summary>
    private Control FeedRow(ConfigFile file, ConfigFeed feed)
    {
        FeedSource? effective = sources.Feeds.FirstOrDefault(f => f.Name.Equals(feed.Name, StringComparison.OrdinalIgnoreCase) && f.DefinedIn is { } from && Core.PathNames.Same(from, file.Path));
        bool enabled = effective?.Enabled ?? !feed.DisabledHere;
        CheckBox toggle = new() { IsChecked = enabled, IsEnabled = !file.ReadOnly, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Margin = new Thickness(0, 0, 4, 0) };
        ToolTip.SetTip(toggle, "Enabled");
        toggle.IsCheckedChanged += (_, _) => IdeErrors.Observe(SetEnabledAsync(file, feed.Name, toggle.IsChecked == true));
        TextBlock name = new() { Text = feed.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock url = new() { Classes = { "muted" }, Text = feed.Value, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0) };
        ToolTip.SetTip(url, feed.Value);
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,150,*,Auto,Auto,Auto"), Height = 28, Tag = feed.Name };
        Add(row, toggle, 0);
        Add(row, name, 1);
        Add(row, url, 2);
        if (feed.Username is not null || feed.HasPassword)
        {
            PathIcon locked = new() { Classes = { "small" }, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            locked.Bind(PathIcon.DataProperty, locked.GetResourceObservable(IconKeys.Lock));
            ToolTip.SetTip(locked, feed.Username is { } user ? $"Credentials for {user} in this file" : "Credentials in this file");
            Add(row, locked, 3);
        }
        Button edit = ToolButtons.Of(IconKeys.Edit, "Edit", () => IdeErrors.Observe(EditAsync(file, feed)));
        Button delete = ToolButtons.Of(IconKeys.Clear, "Delete Feed", () => IdeErrors.Observe(DeleteAsync(file, feed)));
        edit.IsEnabled = delete.IsEnabled = !file.ReadOnly;
        Add(row, edit, 4);
        Add(row, delete, 5);
        if (effective is null)
        {
            row.Opacity = 0.55;
            ToolTip.SetTip(row, "Not used: a nuget.config before this one clears packageSources, or names a feed of this name itself");
        }
        return row;
    }

    private Control SummaryRow(FeedSource feed)
    {
        string from = feed.DefinedIn is { } file ? RecentRow.Home(file) : "the environment";
        Grid line = new() { ColumnDefinitions = new ColumnDefinitions("170,*,Auto"), Opacity = feed.Enabled ? 1 : 0.55 };
        Add(line, new TextBlock { Text = feed.Name, TextTrimming = TextTrimming.CharacterEllipsis }, 0);
        Add(line, new TextBlock { Classes = { "muted" }, Text = feed.Source, TextTrimming = TextTrimming.PathSegmentEllipsis }, 1);
        Add(line, new TextBlock { Classes = { "muted" }, Text = feed.Enabled ? string.Empty : "disabled", Margin = new Thickness(10, 0, 0, 0) }, 2);
        ToolTip.SetTip(line, $"{(feed.Enabled ? "Enabled" : "Disabled")}, from {from}{(feed.Credentials is { } held ? $", with {held}" : string.Empty)}");
        return line;
    }

    private async Task SetEnabledAsync(ConfigFile file, string name, bool enabled)
    {
        if (drawing)
        {
            return;
        }
        // Enabling takes away every writable file's disabling, as dotnet nuget enable does; disabling writes into this file.
        string[] targets = enabled
            ? [.. sources.Files.Where(f => !f.ReadOnly && f.Disables(name)).Select(f => f.Path)]
            : [file.Path];
        await WriteAsync(() =>
        {
            foreach (string target in targets)
            {
                NuGetConfigEditor.SetEnabled(target, name, enabled);
            }
        });
    }

    private async Task AddAsync(ConfigFile file)
    {
        if (await AskAsync(new FeedDialog(file, null)) is { } added)
        {
            await WriteAsync(() => NuGetConfigEditor.Add(file.Path, added));
        }
    }

    private async Task EditAsync(ConfigFile file, ConfigFeed feed)
    {
        if (await AskAsync(new FeedDialog(file, feed)) is { } edited)
        {
            await WriteAsync(() => NuGetConfigEditor.Update(file.Path, feed.Name, edited));
        }
    }

    private async Task DeleteAsync(ConfigFile file, ConfigFeed feed)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }
        ConfirmDialog confirm = new("Deleting Feed", $"Are you sure you want to delete feed {feed.Name}?", "Delete Feed");
        if (await confirm.ShowDialog<bool>(window))
        {
            await WriteAsync(() => NuGetConfigEditor.Remove(file.Path, feed.Name));
        }
    }

    private async Task<FeedEdit?> AskAsync(FeedDialog dialog) =>
        TopLevel.GetTopLevel(this) is Window window && await dialog.ShowDialog<bool>(window) ? dialog.Result : null;

    /// <summary>Writes the change off the window's thread, then reads the sources again; a file that will not take it
    /// says why under the tab.</summary>
    private async Task WriteAsync(Action change)
    {
        problem.IsVisible = false;
        try
        {
            owner.Packages.SaveFirst?.Invoke();
            await BackgroundWork.Run(change);
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException)
        {
            problem.Text = $"The change could not be written: {failed.Message}";
            problem.IsVisible = true;
        }
        await owner.ReloadSourcesAsync();
    }

    private static Control FileRow(ConfigFile file)
    {
        PathIcon icon = new() { Width = 14, Height = 14, Margin = new Thickness(6, 0, 6, 0) };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(IconKeys.DocumentText));
        TextBlock place = new()
        {
            Classes = { "muted" },
            Text = file.Place switch { ConfigPlace.Solution => "solution", ConfigPlace.Folder => "folder", ConfigPlace.User => "user", _ => "machine" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        // Its name, then its folder, which gives way first in a narrow pane so the name stays whole.
        TextBlock folder = new() { Classes = { "muted" }, Text = RecentRow.Home(Path.GetDirectoryName(file.Path) ?? string.Empty), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        ShrinkingRow path = new() { Children = { new TextBlock { Text = Path.GetFileName(file.Path), VerticalAlignment = VerticalAlignment.Center }, folder } };
        Grid line = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = ListRow.Height };
        Add(line, icon, 0);
        Add(line, path, 1);
        Add(line, place, 2);
        ToolTip.SetTip(line, file.Path);
        return line;
    }

    private static void Add(Grid grid, Control control, int column)
    {
        SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static TextBlock Title(string text, Thickness margin) => new() { Classes = { "paneTitle" }, Text = text, Margin = margin, VerticalAlignment = VerticalAlignment.Center };


    private static TextBlock Error(string text)
    {
        TextBlock error = new() { Text = text, TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable(TokenKeys.Error));
        return error;
    }

    private static Control Property(string name, string? value)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("170,*") };
        Add(row, new TextBlock { Text = name }, 0);
        Add(row, value is null ? MutedText.Wrapped("not set") : new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis }, 1);
        return row;
    }
}
