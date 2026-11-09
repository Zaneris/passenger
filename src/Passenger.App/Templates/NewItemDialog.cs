using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Core;

namespace Passenger.App.Templates;

/// <summary>Where New Item makes what it makes: the folder, as the window shows it, its project's language (C#, F#, VB;
/// null outside a project), the namespace the folder's code takes, and the short names of the items the project's kind
/// puts first.</summary>
public sealed record ItemTarget(string Folder, string Shown, string? Language, string Namespace, IReadOnlyList<string> First);

/// <summary>What New Item made, by the name given and its template's name: the files dotnet new wrote and the folders it
/// made whole, or a file of Passenger's own templates to write, with its text.</summary>
public sealed record MadeItem(string Name, string Template, IReadOnlyList<string> Files, IReadOnlyList<string> Folders, (string Path, string Text)? Own);

/// <summary>The New Item dialog: the item templates for the folder's project's language and those of none, dotnet new's
/// beside Passenger's own (<see cref="OwnTemplates"/>), down the left under a search, the project's kind's own first; on
/// the right the name, the namespace for a template that takes one, the template's options as New Project shows them
/// (<see cref="TemplateOptions"/>) and the command line. Create runs dotnet new into the folder and closes with what it
/// made (<see cref="Made"/>), or for one of Passenger's own, with the file to write; else says why, nothing left made.</summary>
public sealed class NewItemDialog : IdeWindow
{
    /// <summary>What the dialog asks itself of a template.</summary>
    private static readonly HashSet<string> Asked = new(StringComparer.OrdinalIgnoreCase) { "namespace" };

    private readonly ItemTarget target;
    private readonly TextBox search = new() { Classes = { StyleClasses.Search }, PlaceholderText = "Search", Margin = new Thickness(8, 8, 8, 6) };
    private readonly ListBox list;
    private readonly TextBlock loading = new() { Classes = { StyleClasses.Muted }, Text = "Loading templates…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
    private readonly TextBlock heading = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock description = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox itemName = new() { Classes = { StyleClasses.Search } };
    private readonly TextBox space = new() { Classes = { StyleClasses.Search } };
    private readonly Control nameRow;
    private readonly Control spaceRow;
    private readonly TemplateOptions options = new();
    private readonly TextBlock command = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Control details;
    private readonly ProgressBar bar = new() { IsIndeterminate = true, Height = 4, MinHeight = 4, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock progress = new() { Classes = { StyleClasses.Muted }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel working;
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button create = DialogFooter.Default("Create");

    private IReadOnlyList<ProjectTemplate> entries = [];
    private IReadOnlyList<ProjectTemplate> listed = [];
    private ProjectTemplate? template;
    private TemplateCatalog? catalog;
    private CancellationTokenSource? creating;

    /// <summary>The name the dialog gave last: one that differs is the person's, which a template chosen later keeps.</summary>
    private string nameGiven = string.Empty;

    /// <param name="read">The folder whose SDK's templates are read: the solution's, else the open folder.</param>
    public NewItemDialog(ItemTarget target, string read)
    {
        this.target = target;
        FormRows.Scope(this);
        Title = "New Item";
        Width = 860;
        Height = 600;
        MinWidth = 680;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        create.IsEnabled = false;

        list = new ListBox { Classes = { StyleClasses.Tree }, Padding = new Thickness(8, 0) };
        ReusedRows.Use(list, Item);
        list.SelectionChanged += (_, _) => Choose(list.SelectedItem as ProjectTemplate);
        search.TextChanged += (_, _) => Filter();
        search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        DockPanel leftPanel = new();
        DockPanel.SetDock(search, Dock.Top);
        leftPanel.Children.Add(search);
        leftPanel.Children.Add(new Panel { Children = { list, loading } });
        Border left = new() { Width = 280, BorderThickness = new Thickness(0, 0, 1, 0), Child = leftPanel };
        left.Bind(Border.BorderBrushProperty, left.GetResourceObservable(TokenKeys.Divider));

        nameRow = TemplateOptions.Row("Name:", itemName);
        spaceRow = TemplateOptions.Row("Namespace:", space);
        itemName.TextChanged += (_, _) => ShowCommand();
        space.TextChanged += (_, _) => ShowCommand();
        space.Text = target.Namespace;
        options.Changed += ShowCommand;
        TextBlock where = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = $"It will be made in {target.Shown}" };
        details = new StackPanel { Spacing = 10, Children = { nameRow, spaceRow, TemplateOptions.Row(null, where), options.Panel, options.Folded, TemplateOptions.Row(null, command) } };
        StackPanel right = new()
        {
            Margin = new Thickness(24, 18, 24, 18),
            Spacing = 10,
            Children = { new StackPanel { Children = { heading, description }, Margin = new Thickness(0, 0, 0, 8) }, details },
        };
        problem.Classes.Add(StyleClasses.Error);
        working = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false, Children = { bar, progress } };
        create.Click += (_, _) => IdeErrors.Observe(CreateAsync());
        Closing += (_, _) => creating?.Cancel();
        Action changed = () => IdeErrors.Observe(RefreshAsync(read));
        ProjectTemplates.Changed += changed;
        Closed += (_, _) => ProjectTemplates.Changed -= changed;
        DockPanel.SetDock(left, Dock.Left);
        Content = DialogFooter.Page(new DockPanel { Children = { left, new Border { Child = new ScrollViewer { Content = right } } } },
            DialogFooter.OkCancel(create, Cancel, new Panel { Children = { problem, working } }));
        Choose(null);
        if (ProjectTemplates.Ready(read) is { } ready)
        {
            Show(ready);
        }
        TemplatesRead = RefreshAsync(read);
        Opened += (_, _) =>
        {
            search.Focus();
            Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);
        };
    }

    /// <summary>The read of the templates, done once they are listed or the dialog says why they could not be.</summary>
    public Task TemplatesRead { get; }

    /// <summary>The templates listed, those the search finds, in their order.</summary>
    public IReadOnlyList<ProjectTemplate> Listed => listed;

    /// <summary>The template chosen.</summary>
    public ProjectTemplate? Chosen => template;

    /// <summary>The search over the templates.</summary>
    public string Query
    {
        get => search.Text ?? string.Empty;
        set => search.Text = value;
    }

    public string ItemName
    {
        get => itemName.Text?.Trim() ?? string.Empty;
        set => itemName.Text = value;
    }

    /// <summary>The namespace the template is given, where it takes one.</summary>
    public string Namespace
    {
        get => space.Text?.Trim() ?? string.Empty;
        set => space.Text = value;
    }

    /// <summary>Whether a name is asked, and a namespace: not of a template that takes none.</summary>
    public bool AsksName => nameRow.IsVisible;

    public bool AsksNamespace => spaceRow.IsVisible;

    /// <summary>The labels of the template's options shown now.</summary>
    public IReadOnlyList<string> OptionLabels => options.Labels;

    /// <summary>The control that sets an option, by its label, for a click; null when none is shown.</summary>
    public Control? OptionEditor(string label) => options.Editor(label);

    /// <summary>The link that opens the template's options waiting under More options.</summary>
    public Button MoreOptions => options.MoreOptions;

    /// <summary>The list's row for a template by its short name, for a click on it; null when it is not listed.</summary>
    public Control? Row(string shortName)
    {
        int at = listed.ToList().FindIndex(t => t.ShortNames.Contains(shortName, StringComparer.OrdinalIgnoreCase));
        if (at < 0)
        {
            return null;
        }
        list.ScrollIntoView(at);
        list.UpdateLayout();
        return list.ContainerFromIndex(at);
    }

    /// <summary>What Create runs, as the dialog shows it.</summary>
    public string Command => command.Text ?? string.Empty;

    public Button CreateButton => create;

    /// <summary>Why the last Create was refused or failed, or why the templates could not be read; null when none.</summary>
    public string? Problem => problem.IsVisible ? problem.Text : null;

    /// <summary>What Create made, once it closed the dialog; null until then, and when given up.</summary>
    public MadeItem? Made { get; private set; }

    /// <summary>Create, as its button does: dotnet new run into the folder (or Passenger's own file planned), the dialog
    /// closing with what it made, else saying why, anything dotnet new left taken away.</summary>
    public async Task CreateAsync()
    {
        if (creating is not null || template is null)
        {
            return;
        }
        if (WhyNot() is { } refused)
        {
            Fail(refused);
            return;
        }
        string name = template.TakesName ? ItemName : template.Name;
        if (template.Own is { } own)
        {
            Made = new MadeItem(name, template.Name, [], [], (Path.Combine(target.Folder, name + own.Extension), own.Text(name, Namespace)));
            Close(Made);
            return;
        }
        using CancellationTokenSource cancel = new();
        creating = cancel;
        Fail(null);
        Busy(true);
        string folder = target.Folder;
        progress.Text = $"Creating {name} from {template.Name} (dotnet new {template.ShortName})…";
        HashSet<string> before = await BackgroundWork.Run(() => Entries(folder));
        try
        {
            Task<ChildResult> run = ChildProcesses.RunAsync(ProjectTemplates.NewCommand(Arguments(output: true), folder), limit: TimeSpan.FromMinutes(5), cancel: cancel.Token);
            BackgroundWork.Track(run);
            ChildResult made = await run;
            (List<string> files, List<string> folders) = await BackgroundWork.Run(() => MadeSince(before, Entries(folder)));
            if (!made.Succeeded || files.Count + folders.Count == 0)
            {
                await BackgroundWork.Run(() => TakeAway(files, folders));
                Fail(made.Succeeded ? $"dotnet new {template.ShortName} made nothing in {folder}" : $"{name} could not be created: {made.Said}");
                return;
            }
            creating = null;
            Made = new MadeItem(name, template.Name, files, folders, null);
            Close(Made);
        }
        catch (OperationCanceledException)
        {
            // Stopped: what dotnet new wrote meanwhile goes.
            (List<string> files, List<string> folders) = await BackgroundWork.Run(() => MadeSince(before, Entries(folder)));
            await BackgroundWork.Run(() => TakeAway(files, folders));
        }
        finally
        {
            creating = null;
            Busy(false);
        }
    }

    public void Cancel()
    {
        creating?.Cancel();
        Close(null);
    }

    /// <summary>Why Create would make nothing, or null: a name a file cannot take, or (for C#) no type's name; a namespace
    /// that is none; a file of Passenger's own already there; an option refused.</summary>
    private string? WhyNot()
    {
        if (template is null)
        {
            return "Choose a template";
        }
        if (template.TakesName)
        {
            if (ProjectNames.Problem(ItemName, "Item", namesCode: false) is { } named)
            {
                return named;
            }
            if (template.Own is not null && !Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(ItemName))
            {
                return $"{ItemName} is not a C# name";
            }
        }
        if (spaceRow.IsVisible && (Namespace.Length == 0 || Namespace.Split('.').Any(p => !Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(p))))
        {
            return $"{Namespace} is not a namespace";
        }
        if (template.Own is { } own && Path.Exists(Path.Combine(target.Folder, ItemName + own.Extension)))
        {
            return $"{ItemName + own.Extension} is already there";
        }
        return options.WhyNot();
    }

    /// <summary>dotnet new's arguments: the template, the name (where it takes one), where it goes (none in the line the
    /// dialog shows), the namespace (where it takes one), the language, and the options changed.</summary>
    private List<string> Arguments(bool output)
    {
        TemplateVariant variant = Variant();
        List<string> arguments = [template!.ShortName];
        if (template.TakesName)
        {
            arguments.AddRange(["--name", ItemName]);
        }
        if (output)
        {
            arguments.AddRange(["--output", target.Folder, "--no-update-check"]);
        }
        if (spaceRow.IsVisible)
        {
            arguments.AddRange(["--namespace", Namespace]);
        }
        if (variant.Language is { } language)
        {
            arguments.AddRange(["--language", language]);
        }
        arguments.AddRange(options.Arguments());
        return arguments;
    }

    private TemplateVariant Variant() => template!.Variant(target.Language, null);

    /// <summary>Reads the folder's templates and lists what the read brings, the template chosen kept.</summary>
    private async Task RefreshAsync(string read)
    {
        try
        {
            Show(await BackgroundWork.Run(() => ProjectTemplates.LoadAsync(read)));
        }
        catch (Exception error) when (ProjectTemplates.Unreadable(error))
        {
            if (catalog is null)
            {
                Show(null);
            }
            Fail($"The templates could not be read: {error.Message}");
        }
    }

    /// <summary>The templates of a read for the folder (none given null, Passenger's own alone): the project's language's
    /// and those of none, the project's kind's own first, then Passenger's own, then the rest by name.</summary>
    private void Show(TemplateCatalog? read)
    {
        if (read is not null && (ReferenceEquals(read, catalog) || catalog?.Shape == read.Shape) || creating is not null)
        {
            return;
        }
        catalog = read;
        bool Fits(ProjectTemplate t) => t.Languages.Count == 0 || target.Language is { } language && t.Languages.Contains(language);
        List<ProjectTemplate> theirs = [.. (read?.Items ?? []).Where(Fits)];
        int First(ProjectTemplate t) => target.First.ToList().FindIndex(f => t.ShortNames.Contains(f, StringComparer.OrdinalIgnoreCase)) is var at and >= 0 ? at : int.MaxValue;
        entries = [.. theirs.Where(t => First(t) < int.MaxValue).OrderBy(First), .. OwnTemplates.All.Where(Fits),
            .. theirs.Where(t => First(t) == int.MaxValue).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
        loading.Text = "Nothing matches";
        Filter();
    }

    /// <summary>Lists the templates the search finds, every word in a name, a short name or a tag, the one chosen kept
    /// where it is still listed, else the first.</summary>
    private void Filter()
    {
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        listed = [.. entries.Where(t => words.All(w => Has(t.Name, w) || t.ShortNames.Any(s => Has(s, w)) || t.Classifications.Any(c => Has(c, w))))];
        ProjectTemplate? kept = template is null ? null : listed.FirstOrDefault(t => t.Name == template.Name && t.ShortName == template.ShortName && (t.Own is null) == (template.Own is null));
        list.ItemsSource = listed;
        list.SelectedItem = kept ?? listed.FirstOrDefault();
        loading.IsVisible = listed.Count == 0 && (catalog is not null || words.Length > 0);
        if (listed.Count == 0)
        {
            Choose(null);
        }

        static bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A template chosen: its name and description, the name it offers (unless the person typed one), whether it
    /// takes a namespace, and its options.</summary>
    private void Choose(ProjectTemplate? chosen)
    {
        if (chosen is not null && ReferenceEquals(chosen, template))
        {
            return;
        }
        template = chosen;
        details.IsVisible = chosen is not null;
        create.IsEnabled = chosen is not null && creating is null;
        if (chosen is null)
        {
            heading.Text = "New Item";
            description.Text = string.Empty;
            options.Clear();
            command.Text = string.Empty;
            return;
        }
        TemplateVariant variant = Variant();
        heading.Text = chosen.Name;
        description.Text = variant.Description;
        nameRow.IsVisible = chosen.TakesName;
        spaceRow.IsVisible = chosen.Own is not null || variant.Options.Any(o => Asked.Contains(o.Name));
        if (chosen.TakesName && (ItemName.Length == 0 || ItemName == nameGiven))
        {
            nameGiven = Free(chosen.DefaultName);
            itemName.Text = nameGiven;
        }
        options.Show(variant, Asked);
        ShowCommand();
    }

    /// <summary>A name no file in the folder has before its extension: the template's own, else with the first number
    /// that makes it one.</summary>
    private string Free(string name)
    {
        HashSet<string> taken;
        try
        {
            taken = new(Directory.EnumerateFileSystemEntries(target.Folder).Select(e => Path.GetFileName(e).Split('.')[0]), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            taken = [];
        }
        if (!taken.Contains(name))
        {
            return name;
        }
        int n = 1;
        while (taken.Contains($"{name}{n}"))
        {
            n++;
        }
        return $"{name}{n}";
    }

    private void ShowCommand() =>
        command.Text = template is null ? string.Empty
            : template.Own is { } own ? $"Passenger makes {(template.TakesName ? ItemName : template.Name)}{own.Extension}"
            : $"dotnet new {string.Join(' ', Arguments(output: false).Select(ChildProcesses.Quote))}";

    private void Fail(string? why)
    {
        problem.Text = why;
        problem.IsVisible = why is not null;
    }

    private void Busy(bool busy)
    {
        working.IsVisible = busy;
        problem.IsVisible = !busy && problem.Text is not null;
        create.IsEnabled = !busy && template is not null;
        foreach (Control field in (Control[])[search, list, itemName, space, options.Panel, options.Folded])
        {
            field.IsEnabled = !busy;
        }
    }

    /// <summary>Up and Down in the search choose among the templates found; Escape clears a search before it cancels.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown when ListKeys.Move(list, e):
                e.Handled = true;
                break;
            case Key.Escape when Query.Length > 0:
                Query = string.Empty;
                e.Handled = true;
                break;
        }
    }

    /// <summary>A row for whichever template the list hands its container: its name, and where it comes from at its right
    /// (dotnet new's short name, or Passenger for its own).</summary>
    private static Control Item()
    {
        TextBlock name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        TextBlock from = new() { Classes = { StyleClasses.Muted }, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel both = new();
        DockPanel.SetDock(from, Dock.Right);
        both.Children.Add(from);
        both.Children.Add(name);
        Border item = new() { Padding = new Thickness(5, 6), Child = both };
        return item.Showing<ProjectTemplate>(row =>
        {
            name.Text = row.Name;
            from.Text = row.Own is not null ? "Passenger" : row.ShortName;
            ToolTip.SetTip(item, row.Own is not null ? $"{row.Name}, Passenger's own" : $"{row.Name} (dotnet new {row.ShortName})");
        });
    }

    /// <summary>Every file and folder under a folder, walking only into those a project's wildcards enter (bin, obj,
    /// node_modules and hidden folders listed, not entered).</summary>
    private static HashSet<string> Entries(string folder)
    {
        HashSet<string> entries = new(PathNames.Comparer);
        foreach (string each in PathNames.FoldersUnder(folder, ProjectFolders.Enters))
        {
            try
            {
                entries.UnionWith(Directory.EnumerateFileSystemEntries(each));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Gone or shut: nothing in it is dotnet new's.
            }
        }
        return entries;
    }

    /// <summary>What came since the first listing: the files outside a folder that came, and the topmost folders that came.</summary>
    private static (List<string> Files, List<string> Folders) MadeSince(HashSet<string> before, HashSet<string> after)
    {
        List<string> came = [.. after.Where(e => !before.Contains(e))];
        List<string> folders = [.. came.Where(Directory.Exists).Where(f => !came.Any(o => PathNames.IsUnder(f, o))).Select(PathNames.Canonical)];
        List<string> files = [.. came.Where(File.Exists).Where(f => !came.Any(o => PathNames.IsUnder(f, o))).Select(PathNames.Canonical)];
        return (files, folders);
    }

    /// <summary>Takes away what a Create that failed or was stopped made.</summary>
    private static void TakeAway(List<string> files, List<string> folders)
    {
        foreach (string path in files.Concat(folders))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                IdeErrors.Write($"A new item that could not be made left {path}: {error.Message}");
            }
        }
    }
}
