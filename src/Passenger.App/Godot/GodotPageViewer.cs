using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>An editor group's Godot project page: a bar over the editor of a project.godot (its Project and Text) and the
/// page laid over the editor while it shows: the Godot found, the main scene, the autoloads, the input map, the export
/// presets and their templates, the plugins, Godot's script editor and the layers, each row leading to its line or its file;
/// a plugin enabled or disabled from its box, and the main scene, the input map's actions and events, an autoload's kind
/// and the layers' names changed from their rows (<see cref="GodotPageEdits"/>).</summary>
public sealed class GodotPageViewer
{
    /// <summary>The words of the segment that shows the page.</summary>
    public const string PageChoice = "Project";

    /// <summary>The words of the segment that shows the text.</summary>
    public const string TextChoice = "Text";

    private readonly ToggleButton page = new() { Classes = { StyleClasses.Segment }, Content = PageChoice, FontSize = 12, Name = "ShowGodotPage" };
    private readonly ToggleButton text = new() { Classes = { StyleClasses.Segment }, Content = TextChoice, FontSize = 12, Name = "ShowGodotText" };
    private readonly TextBlock said = new() { Classes = { StyleClasses.Muted }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel content = new() { Classes = { StyleClasses.NotesPage } };
    private GodotPageFile? file;
    private GodotPageFacts? painted;

    public GodotPageViewer()
    {
        Bar = ToolWindows.ToolBars.OverEditor(new DockPanel
        {
            Margin = new Thickness(8, 0),
            Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Right, Child = said, Margin = new Thickness(12, 0, 0, 0) },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { page, text } },
            },
        });
        Bar.IsVisible = false;
        page.Click += (_, _) => Choose(true);
        text.Click += (_, _) => Choose(false);
        Body = new ScrollViewer
        {
            Classes = { StyleClasses.Notes },
            Focusable = true,
            IsVisible = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
        };
    }

    /// <summary>The bar over the editor, shown for a project.godot whether its page or its text shows.</summary>
    public Border Bar { get; }

    /// <summary>The page, laid over the editor while it shows.</summary>
    public ScrollViewer Body { get; }

    /// <summary>The file shown, null while the group shows no project.godot.</summary>
    public GodotPageFile? File => file;

    /// <summary>Whether the page, not the editor under it, is what the group shows.</summary>
    public bool ShowsPage => file is { ShowsPage: true };

    /// <summary>Raised as the page or the text is chosen from the bar, for the group to move the focus.</summary>
    public event EventHandler? Switched;

    public void Show(GodotPageFile shown)
    {
        if (shown != file)
        {
            Hide();
            file = shown;
            file.Changed += OnChanged;
        }
        Paint();
        file.Follow();
    }

    /// <summary>Shows no page: the bar and the page hidden.</summary>
    public void Hide()
    {
        if (file is null)
        {
            return;
        }
        file.Changed -= OnChanged;
        file = null;
        Bar.IsVisible = false;
        Body.IsVisible = false;
    }

    /// <summary>The text's caret taken to a place in it: the text shows, as the place asked for is a line of it.</summary>
    public void Reveal()
    {
        if (file is { ShowsPage: true } shown)
        {
            shown.Show(false);
            Switched?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Paint();

    private void Choose(bool showPage)
    {
        file?.Show(showPage);
        Paint();
        Switched?.Invoke(this, EventArgs.Empty);
    }

    private void Paint()
    {
        if (file is null)
        {
            return;
        }
        Bar.IsVisible = true;
        Body.IsVisible = file.ShowsPage;
        page.IsChecked = file.ShowsPage;
        text.IsChecked = !file.ShowsPage;
        said.Text = file.IsReading ? "Reading…" : string.Empty;
        if (file.Facts is { } facts && !ReferenceEquals(facts, painted))
        {
            painted = facts;
            Render(facts);
        }
        else if (file.Facts is null && painted is not null)
        {
            painted = null;
            content.Children.Clear();
        }
    }

    private void Render(GodotPageFacts facts)
    {
        GodotProjectPages pages = file!.Pages;
        string project = Path.Combine(facts.Folder, GodotProject.FileName);
        string presets = Path.Combine(facts.Folder, GodotExports.FileName);
        Vector scrolled = Body.Offset;
        // A row's box or link taken away with the rows leaves the keys on the page, not on nothing.
        bool keys = Body.IsKeyboardFocusWithin;
        content.Children.Clear();
        content.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Classes = { StyleClasses.NotesTitle }, Text = facts.Name ?? Path.GetFileName(facts.Folder), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Classes = { StyleClasses.NotesSubtitle }, Text = $"Godot project in {PathNames.Shown(facts.Folder, null)}", TextWrapping = TextWrapping.Wrap },
            },
        });

        Section("Godot", [Row(Words(facts.Godot), null, null)]);

        // A main scene project.godot names by its uid is shown by its res:// path.
        Section("Main Scene", [facts.MainScene is ({ } res, int line, { } scene)
            ? Row(Link(facts.Scenes.FirstOrDefault(s => s.Uid == res)?.Res ?? res, () => pages.Open(scene)), Muted($"{GodotProject.FileName}:{line + 1}"),
                Link("Setting", () => pages.GoTo(project, line)))
            : facts.MainScene is ({ } missing, int at, null)
                ? Row(Words(missing), Muted($"not there · {GodotProject.FileName}:{at + 1}"), Link("Setting", () => pages.GoTo(project, at)))
                : Empty($"{GodotProject.FileName} names no main scene"), .. MainSceneChoice(facts)]);

        Section("Autoloads", facts.Autoloads.Count == 0 ? [Empty("No autoloads")]
            : [.. facts.Autoloads.Select(a => Row(Link(a.Name, () => pages.GoTo(project, a.Line)), Autoload(facts.Folder, a),
                a.File is { } loaded ? Link(a.Res!, () => pages.Open(loaded)) : Muted(a.Res ?? string.Empty)))]);

        Section("Input Map", [.. facts.Actions.Count == 0 ? [Empty("No input actions of the project's own")] : facts.Actions.SelectMany(a => Action(facts.Folder, project, a)),
            .. ActionMaking(facts)]);

        Section("Export Presets", facts.Presets.Count == 0 ? [Empty($"No {GodotExports.FileName}: Godot's editor makes it (Project, Export)")]
            : [.. facts.Presets.Select(p => Row(Link(p.Preset.Name, () => pages.GoTo(presets, p.Preset.Line)),
                Muted($"{p.Preset.Platform}{(p.Preset.ExportPath is { } path ? $" · {path}" : string.Empty)} · {Templates(p)}"),
                Link(ExportWords, () => pages.Export(facts.Folder, p.Preset.Name))))]);

        Section("Plugins", facts.Plugins.Count == 0 ? [Empty("No plugins under addons/")]
            : [.. facts.Plugins.Select(p => Row(Plugin(facts.Folder, p), Muted(string.Join(" · ", new[] { p.Version, p.Author }.OfType<string>())),
                Link("plugin.cfg", () => pages.Open(p.File))))]);

        Section("Script Editor", [Row(Words(facts.HandOff), null, facts.HandedOff ? null : Link(pages.TitleOf(AppActions.GodotScriptEditor), () => pages.Run(AppActions.GodotScriptEditor)))]);

        Section("Layers", [.. facts.Layers.Count == 0 ? [Empty($"{GodotProject.FileName} names no layers")]
            : facts.Layers.Select(l => Row(Link(l.Name, () => pages.GoTo(project, l.Line)), Muted($"{l.Kind} layer {l.Number}"),
                Link(ClearLayerWords, () => Ask(facts.Folder, GodotPageEdits.NameLayer(l.Written, l.Number, string.Empty))))),
            LayerNaming(facts)]);
        Body.Offset = scrolled;
        if (keys)
        {
            Body.Focus();
        }
    }

    /// <summary>The words of a preset's link to its export.</summary>
    public const string ExportWords = "Export…";

    /// <summary>Whether a preset's templates are there, in words: those of each kind it lacks named.</summary>
    private static string Templates(GodotPagePreset preset) => !preset.Checked ? "templates not looked for: no Godot found runs the project" : (preset.MissingRelease, preset.MissingDebug) switch
    {
        ([], []) => "templates installed",
        ([], var debug) => $"debug templates missing ({Plural.Listed(debug)})",
        (var release, []) => $"release templates missing ({Plural.Listed(release)})",
        (var release, var debug) => $"templates missing ({Plural.Listed([.. release.Union(debug)])})",
    };

    private void Section(string title, IReadOnlyList<Control> rows)
    {
        StackPanel list = new();
        list.Children.AddRange(rows);
        content.Children.Add(new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { new TextBlock { Classes = { StyleClasses.PaneTitle }, Text = title }, new Border { Classes = { StyleClasses.Framed }, Child = list } },
        });
    }

    /// <summary>A row: what it is (a link to its line or file, or words), what it says beside, and a link at its end.</summary>
    private static Control Row(Control name, Control? detail, Control? end)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = ListRow.Height, Margin = new Thickness(8, 0) };
        name.MinWidth = 180;
        name.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(name);
        if (detail is not null)
        {
            detail[Grid.ColumnProperty] = 1;
            detail.Margin = new Thickness(12, 0, 0, 0);
            detail.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(detail);
        }
        if (end is not null)
        {
            end[Grid.ColumnProperty] = 2;
            end.Margin = new Thickness(12, 0, 0, 0);
            end.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(end);
        }
        return row;
    }

    private static Button Link(string words, Action follow)
    {
        Button link = new() { Classes = { StyleClasses.Link }, Content = words };
        link.Click += (_, _) => follow();
        return link;
    }

    private static TextBlock Words(string words) => new() { Text = words, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Muted(string words)
    {
        TextBlock muted = new() { Classes = { StyleClasses.Muted }, Text = words, TextTrimming = TextTrimming.CharacterEllipsis };
        // Words cut short at the row's end are read whole over it.
        ToolTip.SetTip(muted, words);
        return muted;
    }

    private static Control Empty(string words) => new TextBlock { Classes = { StyleClasses.EmptyText }, Text = words, Margin = new Thickness(8, 4), HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>What an autoload is (a global the scripts name, or a node), and a link that asks to make it the other.</summary>
    private Control Autoload(string folder, GodotPageAutoload autoload)
    {
        StackPanel said = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        said.Children.Add(Muted(autoload.Global ? "global" : "node"));
        said.Children.Add(Link(autoload.Global ? MakeNodeWords : MakeGlobalWords, () => Ask(folder, GodotPageEdits.Autoload(autoload, !autoload.Global))));
        return said;
    }

    /// <summary>A change of project.godot asked of the window for the project shown.</summary>
    private void Ask(string folder, GodotPageEdit edit)
    {
        if (file is { } shown)
        {
            IdeErrors.Observe(shown.Pages.Edit(folder, edit));
        }
    }

    /// <summary>The row that makes a scene chosen among the project's the main scene: none where the project has no scene.</summary>
    private IEnumerable<Control> MainSceneChoice(GodotPageFacts facts)
    {
        if (facts.Scenes.Count == 0)
        {
            yield break;
        }
        string? main = facts.MainScene?.Res;
        ComboBox scenes = Choice(MainSceneName, facts.Scenes, facts.Scenes.FirstOrDefault(s => s.Res == main || s.Uid == main), s => s.Res);
        bool byUid = main?.StartsWith(GodotUids.Scheme, StringComparison.Ordinal) == true;
        yield return Row(scenes, Muted("the scene the game starts in"), Link(MakeMainWords, () =>
        {
            if (scenes.SelectedItem is GodotPageScene scene)
            {
                Ask(facts.Folder, GodotPageEdits.MainScene(scene, byUid));
            }
        }));
    }

    /// <summary>An input action's row, leading to its line and taking it out, and a row for each of its events taking it out.</summary>
    private IEnumerable<Control> Action(string folder, string project, GodotPageAction action)
    {
        GodotProjectPages pages = file!.Pages;
        yield return Row(Link(action.Name, () => pages.GoTo(project, action.Line)), Muted(action.Events.Count == 0 ? "no events" : Plural.Of(action.Events.Count, "event")),
            Link(RemoveActionWords, () => Ask(folder, GodotPageEdits.RemoveAction(action))));
        for (int i = 0; i < action.Events.Count; i++)
        {
            int at = i;
            yield return Row(new TextBlock { Text = action.Events[i], Margin = new Thickness(16, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }, null,
                Link(RemoveEventWords, () => Ask(folder, GodotPageEdits.RemoveEvent(action, at))));
        }
    }

    /// <summary>The rows that add an action by the name typed, and an event chosen to an action chosen.</summary>
    private IEnumerable<Control> ActionMaking(GodotPageFacts facts)
    {
        TextBox named = new() { Name = NewActionName, PlaceholderText = "New action's name" };
        yield return Row(named, null, Link(AddActionWords, () => Ask(facts.Folder, GodotPageEdits.AddAction(named.Text ?? string.Empty))));
        if (facts.Actions.Count == 0)
        {
            yield break;
        }
        ComboBox actions = Choice(EventActionName, facts.Actions, null, a => a.Name);
        ComboBox events = Choice(NewEventName, GodotInputEvents.Givable, null, e => e);
        events.MinWidth = 180;
        yield return Row(actions, events, Link(AddEventWords, () =>
        {
            if (actions.SelectedItem is GodotPageAction action && events.SelectedItem is string given)
            {
                Ask(facts.Folder, GodotPageEdits.AddEvent(action, given));
            }
        }));
    }

    /// <summary>The row that names a layer chosen by kind and number, its numbers as many as the kind has.</summary>
    private Control LayerNaming(GodotPageFacts facts)
    {
        ComboBox kinds = Choice(LayerKindName, GodotDefinitions.LayerKinds, null, GodotDefinitions.LayersShown);
        ComboBox numbers = Choice(LayerNumberName, Numbers(GodotDefinitions.LayerKinds[0]), null, n => n);
        kinds.SelectionChanged += (_, _) =>
        {
            if (kinds.SelectedItem is string kind && (numbers.ItemsSource as IReadOnlyList<string>)?.Count != GodotDefinitions.LayerCount(kind))
            {
                numbers.ItemsSource = Numbers(kind);
                numbers.SelectedIndex = 0;
            }
        };
        TextBox named = new() { Name = LayerNameName, PlaceholderText = "Layer's name", MinWidth = 180 };
        return Row(kinds, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { numbers, named } }, Link(NameLayerWords, () =>
        {
            if (kinds.SelectedItem is string kind && numbers.SelectedItem is string number)
            {
                Ask(facts.Folder, GodotPageEdits.NameLayer(kind, int.Parse(number, System.Globalization.CultureInfo.InvariantCulture), named.Text ?? string.Empty));
            }
        }));

        static IReadOnlyList<string> Numbers(string kind) => [.. Enumerable.Range(1, GodotDefinitions.LayerCount(kind)).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture))];
    }

    /// <summary>A list to choose from, of rows made once, the item given (else the first) chosen.</summary>
    private static ComboBox Choice<T>(string name, IReadOnlyList<T> items, T? chosen, Func<T, string> shown)
        where T : class
    {
        ComboBox box = new() { Classes = { StyleClasses.Compact }, Name = name, MaxDropDownHeight = 320 };
        ReusedRows.Use(box, () =>
        {
            TextBlock row = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            return row.Showing<T>(item => row.Text = shown(item));
        });
        box.ItemsSource = items;
        box.SelectedItem = chosen ?? items.FirstOrDefault();
        return box;
    }

    /// <summary>The names of the page's lists and boxes that choose and name what a change writes.</summary>
    public const string MainSceneName = "GodotMainScene", NewActionName = "GodotNewAction", EventActionName = "GodotEventAction", NewEventName = "GodotNewEvent",
        LayerKindName = "GodotLayerKind", LayerNumberName = "GodotLayerNumber", LayerNameName = "GodotLayerName";

    /// <summary>The words of the main scene's link that makes the scene chosen the main scene.</summary>
    public const string MakeMainWords = "Make Main Scene…";

    /// <summary>The words of the links that add an input action and an event to one, and take each out.</summary>
    public const string AddActionWords = "Add Action…", RemoveActionWords = "Remove Action…", AddEventWords = "Add Event…", RemoveEventWords = "Remove Event…";

    /// <summary>The words of the links that name a layer and take a layer's name out.</summary>
    public const string NameLayerWords = "Name Layer…", ClearLayerWords = "Clear Name…";

    /// <summary>The words of an autoload's link that makes it a global the project's scripts name.</summary>
    public const string MakeGlobalWords = "Make Global…";

    /// <summary>The words of an autoload's link that makes it a node alone.</summary>
    public const string MakeNodeWords = "Make Node…";

    /// <summary>A plugin's box, checked while the project enables it; a click asks to enable or disable it, the box left as
    /// the file says until it is written.</summary>
    private Control Plugin(string folder, GodotPlugin plugin)
    {
        CheckBox box = new() { Content = plugin.Name, IsChecked = plugin.Enabled };
        box.Click += (_, _) =>
        {
            box.IsChecked = plugin.Enabled;
            if (file is { } shown)
            {
                IdeErrors.Observe(shown.Pages.SetPlugin(folder, plugin, !plugin.Enabled));
            }
        };
        return box;
    }
}
