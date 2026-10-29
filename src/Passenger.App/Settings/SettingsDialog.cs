using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>Settings: the pages down the left under a search box, the chosen page's options on the right, OK, Cancel
/// and Apply below. A search keeps the pages with an option whose words match and shows only those options. The options
/// edit a copy, which Apply and OK hand back.</summary>
public sealed partial class SettingsDialog : Window
{
    public const string Appearance = "Appearance";
    public const string SystemPage = "System Settings";
    public const string UpdatesPage = "Updates";
    public const string EditorPage = "Editor";
    public const string CodeFoldingPage = "Code Folding";
    public const string LiveTemplatesPage = "Live Templates";
    public const string PostfixPage = "Postfix Completion";
    public const string XamlCodeStylePage = "Code Style: XAML";
    public const string TerminalPage = "Terminal";
    public const string BuildPage = "Build";
    public const string RunPage = "Run";
    public const string HotReloadPage = "Hot Reload";
    public const string NodePage = "Node.js";
    public const string EslintPage = "ESLint";
    public const string PrettierPage = "Prettier";
    public const string SchemasPage = "Remote JSON Schemas";
    public const string DockerPage = "Docker";
    public const string KeymapPage = "Keymap";
    public const string McpPage = "MCP Server";
    public const string DiscordPage = "Discord";
    public const string DiagnosticsPage = "Diagnostics";

    /// <summary>A page as a message sends a person to it, by its name above as the dialog lists it:
    /// "Settings | Node.js".</summary>
    public static string PlaceOfPage(string page) => $"Settings | {page}";

    /// <summary>The names of <see cref="BuildResultsMode"/>'s choices, in its order.</summary>
    public static readonly string[] BuildResultsChoices = ["Always", "Only if errors or warnings received", "Only if errors received", "Never"];

    /// <summary>The names of <see cref="OpenProjectIn"/>'s choices, in its order.</summary>
    public static readonly (OpenProjectIn Choice, string Name)[] OpenProjectChoices =
        [(OpenProjectIn.NewWindow, "New window"), (OpenProjectIn.CurrentWindow, "Current window"), (OpenProjectIn.Ask, "Ask")];

    /// <summary>The themes, in the order Settings offers them.</summary>
    public static readonly (Look Look, string Name)[] LookChoices = [.. Enum.GetValues<Look>().Select(l => (l, l.ToString()))];

    /// <summary>The code's colour schemes, in the order Settings offers them.</summary>
    public static readonly (ColourScheme Scheme, string Name)[] ColourSchemeChoices = [(ColourScheme.Theme, "Same as theme"), (ColourScheme.Rider, "Rider")];

    /// <summary>The names of <see cref="DiscordShown"/>'s choices, in its order.</summary>
    public static readonly string[] DiscordChoices = ["Passenger only", "The solution", "The solution and the file"];

    private static readonly string[] Order = [Appearance, SystemPage, UpdatesPage, EditorPage, CodeFoldingPage, PostfixPage, XamlCodeStylePage, LiveTemplatesPage,
        BuildPage, RunPage, HotReloadPage, NodePage, EslintPage, PrettierPage, SchemasPage, DockerPage, TerminalPage, McpPage, DiscordPage, DiagnosticsPage, KeymapPage];

    /// <summary>The names of Attributes format's choices, in <see cref="Passenger.Xaml.Features.XamlAttributeStyle"/>'s
    /// order.</summary>
    public static readonly string[] AttributeStyles = ["On single line", "First attribute on single line", "Each attribute on separate lines", "Do not touch"];

    /// <summary>The names of Attributes indenting's choices, in <see
    /// cref="Passenger.Xaml.Features.XamlAttributeIndent"/>'s order.</summary>
    public static readonly string[] AttributeIndents = ["Single indent", "Double indent", "Align by first attribute"];

    /// <summary>The names of an element's inside indenting, in <see
    /// cref="Passenger.Xaml.Features.XamlContentIndent"/>'s order.</summary>
    public static readonly string[] ContentIndents = ["Do not change indenting", "One indent from parent element", "No indent from parent element", "No indent at all"];

    /// <summary>The words a search finds every option of a page by, beside each option's own.</summary>
    internal const string FoldingWords = "fold by default folding collapse collapsed open opening region";
    internal const string XamlWords = "xaml axaml xml code style formatting reformat format layout wpf avalonia";
    internal const string EslintWords = "eslint lint linter linting javascript typescript code quality tools problems inspections";
    internal const string PrettierWords = "prettier format formatter formatting reformat code style javascript typescript css json";
    internal const string McpWords = "mcp model context protocol agents ai claude codex opencode attach socket";

    /// <summary>Puts what the options changed in force: from the settings as they were applied last, to the options'.</summary>
    private readonly Action<AppSettings, AppSettings> apply;

    /// <summary>The settings as the dialog opened on them or last applied them, which what is changed since is told
    /// from, so another window's change meanwhile is not undone.</summary>
    private AppSettings applied;

    /// <summary>The window's actions, which the keymap page lists.</summary>
    private readonly ActionRegistry actions;
    private readonly List<Option> options = [];
    private readonly TextBox search;
    private readonly ListBox pages;
    private readonly TextBlock heading;
    private readonly StackPanel body;
    private readonly TextBlock nothing;

    /// <summary>What a page keeps above its options as they scroll (the Keymap page's toolbar).</summary>
    private readonly Decorator pageTop = new();

    /// <param name="agents">The agents' setup the MCP Server page shows: this copy's, and the window's folder.</param>
    public SettingsDialog(AppSettings current, ActionRegistry actions, Action<AppSettings, AppSettings> apply, Mcp.AgentSetup? agents = null)
    {
        this.apply = apply;
        this.actions = actions;
        Working = current;
        applied = current;
        Title = "Settings";
        Width = 980;
        Height = 700;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddOptions(agents ?? Mcp.AgentSetup.Here(null));
        search = new TextBox { Classes = { "search" }, PlaceholderText = "Search settings", Margin = new Thickness(10, 10, 10, 6) };
        search.TextChanged += (_, _) => Filter();
        pages = new ListBox { Classes = { "tree", "pages" } };
        pages.SelectionChanged += (_, _) => ShowPage();
        heading = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        body = new StackPanel { Spacing = 12 };
        nothing = new TextBlock { Classes = { "muted" }, Text = "Nothing matches", IsVisible = false, Margin = new Thickness(21, 4) };

        Button ok = new() { Content = "OK", IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        Button applyButton = new() { Content = "Apply" };
        ok.Click += (_, _) => Ok();
        cancel.Click += (_, _) => Close(false);
        applyButton.Click += (_, _) => Apply();

        DockPanel left = new() { Width = 240 };
        DockPanel.SetDock(search, Dock.Top);
        left.Children.Add(search);
        left.Children.Add(new Panel { Children = { pages, nothing } });
        Border leftPane = new() { BorderThickness = new Thickness(0, 0, 1, 0), Child = left };
        leftPane.Bind(Border.BorderBrushProperty, leftPane.GetResourceObservable(TokenKeys.Divider));
        Border bottom = DialogFooter.Bar(DialogFooter.Of(ok, cancel, applyButton));
        DockPanel right = new() { Margin = new Thickness(20, 16, 20, 0) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(pageTop, Dock.Top);
        right.Children.Add(heading);
        right.Children.Add(pageTop);
        right.Children.Add(new ScrollViewer { Content = body });
        Border editorPane = new() { Child = right };

        DockPanel root = new();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(leftPane, Dock.Left);
        root.Children.Add(bottom);
        root.Children.Add(leftPane);
        root.Children.Add(editorPane);
        Content = root;
        Filter();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The settings as the options now stand.</summary>
    public AppSettings Working { get; private set; }

    /// <summary>The Live Templates page.</summary>
    public LiveTemplatesPane Templates { get; private set; } = null!;

    /// <summary>The Postfix Completion page.</summary>
    public PostfixTemplatesPane Postfix { get; private set; } = null!;

    public string Query
    {
        get => search.Text ?? string.Empty;
        set => search.Text = value;
    }

    /// <summary>The pages listed, after the search.</summary>
    public IReadOnlyList<string> Pages => pages.ItemsSource as IReadOnlyList<string> ?? [];

    public string? Page
    {
        get => pages.SelectedItem as string;
        set => pages.SelectedItem = value;
    }

    /// <summary>The options the chosen page shows now, by their labels.</summary>
    public IReadOnlyList<string> Shown => [.. options.Where(o => o.Page == Page && o.Row.IsVisible).Select(o => o.Label)];

    /// <summary>OK: the settings applied, and the dialog closed.</summary>
    public void Ok()
    {
        Apply();
        Close(true);
    }

    /// <summary>Apply: what the options changed put in force, the dialog kept open.</summary>
    public void Apply()
    {
        apply(applied, Working);
        applied = Working;
    }

    /// <summary>Changes one setting, as its option does.</summary>
    public void Change(Func<AppSettings, AppSettings> change) => Working = change(Working);

    private void Filter()
    {
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Func<Option, bool> byShortcut = ShortcutFiltered();
        foreach (Option option in options)
        {
            option.Row.IsVisible = words.All(w => option.Words.Contains(w, StringComparison.OrdinalIgnoreCase)) && byShortcut(option);
        }
        string? was = Page;
        string[] listed = [.. Order.Where(p => options.Any(o => o.Page == p && o.Row.IsVisible)
            || (p == KeymapPage && (shortcutFilter is not null || FindsKeymapChoice(words))))];
        // A new list chooses no page for a moment, which would take the Keymap page's top, Find Shortcut with it, away.
        if (!listed.SequenceEqual(Pages))
        {
            pages.ItemsSource = listed;
        }
        nothing.IsVisible = listed.Length == 0;
        pages.SelectedItem = listed.Contains(was) ? was : listed.FirstOrDefault();
        ShowPage();
    }

    private void ShowPage()
    {
        body.Children.Clear();
        heading.Text = Page ?? string.Empty;
        pageTop.Child = Page == KeymapPage ? keymapHeader : null;
        foreach (Option option in options.Where(o => o.Page == Page))
        {
            body.Children.Add(option.Row);
        }
    }

    private void AddOptions(Mcp.AgentSetup agents)
    {
        Add(Declared.Look);
        Add(Declared.Theme);
        if (TitleBars.Offered)
        {
            CheckBox merge = Check(Working.MergeMainMenuWithWindowTitle, v => Change(s => s with { MergeMainMenuWithWindowTitle = v }));
            merge.IsEnabled = TitleBars.NotOnThisDesktop is null;
            Add(Appearance, "Merge main menu with window title", merge,
                "title bar window header toolbar decorations frame caption buttons minimize maximize close system linux",
                TitleBars.NotOnThisDesktop ?? TitleBars.OwnFrame);
        }
        if (!OperatingSystem.IsMacOS())
        {
            Add(Declared.MainMenuBar);
        }

        Add(Declared.ReopenProjectsOnStartup);
        StackPanel openIn = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
        foreach ((OpenProjectIn choice, string name) in OpenProjectChoices)
        {
            RadioButton radio = new() { Content = name, GroupName = "openProjectIn", IsChecked = Working.OpenProjectIn == choice };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    Change(s => s with { OpenProjectIn = choice });
                }
            };
            openIn.Children.Add(radio);
        }
        Add(SystemPage, "Open project in", openIn,
            "open solution project folder new window current window this window ask confirm where frame");
        Add(Declared.SaveOnDeactivation);
        NumericUpDown seconds = Declared.IdleSeconds.Box(Working, Change);
        seconds.IsEnabled = Working.SaveWhenIdle;
        CheckBox idle = Check(Working.SaveWhenIdle, v =>
        {
            Change(s => s with { SaveWhenIdle = v });
            seconds.IsEnabled = v;
        });
        TextBlock unit = new() { Text = "seconds", VerticalAlignment = VerticalAlignment.Center };
        Add(SystemPage, "Save when idle for", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { idle, seconds, unit } },
            "autosave files idle inactive seconds");

        Add(Declared.CheckForUpdates);

        Add(Declared.ColourScheme);
        Add(Declared.EditorFontSize);
        Add(Declared.EditorLineSpacing);
        Add(Declared.Ligatures);
        Add(Declared.ShowLineNumbers);
        Add(Declared.RainbowBraces);
        Add(Declared.SolutionWideAnalysis);
        Add(Declared.ParameterHints);
        Add(Declared.TypeHints);
        TextBox masks = new() { Classes = { "search" }, Text = Working.SoftWrapFiles, PlaceholderText = "No files", Width = 260, IsEnabled = Working.SoftWrap };
        ToolTip.SetTip(masks, "Use * and ? as wildcards and ; to separate patterns");
        masks.TextChanged += (_, _) => Change(s => s with { SoftWrapFiles = masks.Text ?? string.Empty });
        CheckBox wrapFiles = Check(Working.SoftWrap, v =>
        {
            Change(s => s with { SoftWrap = v });
            masks.IsEnabled = v;
        });
        Add(EditorPage, "Soft-wrap these files", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { wrapFiles, masks } },
            "soft wrap wrapping long lines width markdown text files masks");
        Add(Declared.SoftWrapIndent);
        Add(Declared.ShowWhitespaces);
        Add(Declared.WheelZoom);

        Add(Declared.FoldFileHeader);
        Add(Declared.FoldImports);
        Add(Declared.FoldDocComments);
        Add(Declared.FoldMethodBodies);
        Add(Declared.FoldPreprocessorRegions);
        Add(Declared.FoldComments);
        Add(Declared.FoldXmlTags);

        AddXamlCodeStyle();

        Postfix = new PostfixTemplatesPane(Working, Change);
        AddPage(PostfixPage, Postfix, "postfix templates completion expression dot autopopup "
            + string.Join(' ', Languages.CSharp.PostfixTemplates.All.Select(t => $"{t.Name} {t.Description} {t.Example}")));
        Templates = new LiveTemplatesPane(TemplateChange.Applied(Working.LiveTemplates), list => Change(s => s with { LiveTemplates = TemplateChange.Of(list) }));
        AddPage(LiveTemplatesPage, Templates, "live templates c# shortcut abbreviation snippet expand tab surround with generation availability mnemonic variables macros "
            + string.Join(' ', CSharp.LiveTemplates.BuiltIn.Select(t => $"{t.Abbreviation} {t.Description}")));

        CheckBox implicitly = Check(Working.OpenBuildWindowOnImplicitBuild, v => Change(s => s with { OpenBuildWindowOnImplicitBuild = v }));
        implicitly.IsEnabled = Working.OpenBuildWindowOnBuild;
        CheckBox explicitly = Check(Working.OpenBuildWindowOnBuild, v =>
        {
            Change(s => s with { OpenBuildWindowOnBuild = v });
            implicitly.IsEnabled = v;
        });
        Add(BuildPage, "Tool Window: when build is explicitly invoked, open the build progress view in a tool window", explicitly,
            "build tool window open progress view start explicitly invoked solution rebuild clean toolset presentation");
        Add(BuildPage, "Tool Window: open the build progress view even when the build is invoked implicitly (i.e. Run/Debug)", implicitly,
            "build tool window open progress view start implicitly run debug profile before launch toolset presentation");
        Add(Declared.ShowBuildResults);

        Add(Declared.HotReloadWhileDebugging);
        Add(Declared.HotReload);
        Add(Declared.HotReloadOnSave);
        Add(Declared.RestartOnRudeEdit);
        Add(Declared.ConsoleBufferKb);

        string? onPath = Passenger.Core.Programs.Node();
        TextBox node = new() { Classes = { "search" }, Text = Working.NodeInterpreter ?? string.Empty, PlaceholderText = onPath ?? "Not found on the PATH", Width = 320 };
        node.TextChanged += (_, _) => Change(s => s with { NodeInterpreter = string.IsNullOrWhiteSpace(node.Text) ? null : node.Text.Trim() });
        Add(NodePage, "Node interpreter", node, "node nodejs interpreter javascript typescript html css json language server npm path",
            "The Node.js the JavaScript, TypeScript, HTML, CSS and JSON language servers run on. Empty, it is the first on the PATH, "
            + "as the login shell finds it. Without one, those files keep their colours only.");
        AddCodeQualityTools();
        Add(Declared.DownloadJsonSchemas);
        Add(Declared.DockerHubTags);
        Add(Declared.BuildKitChecks);

        Add(Declared.TerminalFontSize);
        (string shell, _) = Passenger.Terminal.TerminalSession.DefaultShell();
        TextBox path = new() { Classes = { "search" }, Text = Working.TerminalShell ?? string.Empty, PlaceholderText = shell, Width = 320 };
        path.TextChanged += (_, _) => Change(s => s with { TerminalShell = string.IsNullOrWhiteSpace(path.Text) ? null : path.Text.Trim() });
        Add(TerminalPage, "Shell path", path, "shell zsh bash powershell program");

        AddAgents(agents);

        ComboBox shows = Declared.DiscordShows.Box(Working, Change);
        shows.IsEnabled = Working.ShowInDiscord;
        NumericUpDown idleMinutes = Declared.DiscordIdleMinutes.Box(Working, Change);
        idleMinutes.IsEnabled = Working.ShowInDiscord && Working.DiscordIdle;
        CheckBox whenIdle = Check(Working.DiscordIdle, v =>
        {
            Change(s => s with { DiscordIdle = v });
            idleMinutes.IsEnabled = Working.ShowInDiscord && v;
        });
        whenIdle.IsEnabled = Working.ShowInDiscord;
        CheckBox discord = Check(Working.ShowInDiscord, v =>
        {
            Change(s => s with { ShowInDiscord = v });
            shows.IsEnabled = v;
            whenIdle.IsEnabled = v;
            idleMinutes.IsEnabled = v && Working.DiscordIdle;
        });
        const string Discord = "discord rich presence activity status playing";
        Add(DiscordPage, "Show activity in Discord", discord, $"{Discord} friends share game",
            "Discord shows Playing Passenger with its icon, what is chosen below and how long the solution has been open, to "
            + "whoever can see your activity. Passenger tells the Discord app on this computer alone. Off, it tells Discord nothing.");
        Add(DiscordPage, "Show", shows, $"{Discord} solution project file name details visibility privacy",
            "Passenger only: that you are using Passenger, and nothing of your work. The solution: its name too. The solution and "
            + "the file: the name of the file you are editing as well. Whoever can see your Discord activity sees these names.");
        Add(DiscordPage, "Only Passenger when idle for", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { whenIdle, idleMinutes, new TextBlock { Text = "minutes", VerticalAlignment = VerticalAlignment.Center } },
        }, $"{Discord} idle inactive away timeout minutes");

        Add(Declared.TraceProtocols);
        Add(Declared.LogEveryProgram);

        AddKeymap();
    }

    /// <summary>A declared setting's own row, on its page, its editor made from the declaration.</summary>
    private void Add(Setting setting)
    {
        SettingRow row = setting.Row ?? throw new ArgumentException("A setting with no row of its own goes in a row the dialog builds", nameof(setting));
        Add(row.Page, row.Label, setting.Editor(Working, Change), row.Words, row.Note);
    }

    /// <summary>A page that is one control of its own (the live templates' list and editor), the whole of its width, found
    /// by a search for any of its words.</summary>
    private void AddPage(string page, Control content, string keywords) => options.Add(new Option(page, page, content, $"{page} {keywords}"));

    /// <summary>An option's row: its label, what edits it, and a note under the editor when it needs saying what it
    /// does; a keymap row names its action.</summary>
    private Control Add(string page, string label, Control editor, string keywords, string? note = null, string? action = null)
    {
        // Beside an editor of several lines the label stands level with its first line.
        double firstLine = editor switch
        {
            TextBox { AcceptsReturn: true } => 5,
            StackPanel { Orientation: Orientation.Vertical, Children: [RadioButton or Border { Classes: ["framed"] }, ..] } => 6,
            _ => -1,
        };
        TextBlock name = new()
        {
            Text = label,
            Width = 220,
            Padding = new Thickness(0, 0, 16, 0),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = firstLine < 0 ? VerticalAlignment.Center : VerticalAlignment.Top,
            Margin = new Thickness(0, Math.Max(0, firstLine), 0, 0),
        };
        Control row = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, editor } };
        if (note is not null)
        {
            TextBlock said = new() { Text = note, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(name.Width, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            said.Classes.Add("muted");
            row = new StackPanel { Spacing = 4, Children = { row, said } };
        }
        options.Add(new Option(page, label, row, $"{page} {label} {keywords}", action));
        return row;
    }

    private static CheckBox Check(bool value, Action<bool> changed)
    {
        CheckBox box = new() { IsChecked = value };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    private sealed record Option(string Page, string Label, Control Row, string Words, string? Action = null);
}
