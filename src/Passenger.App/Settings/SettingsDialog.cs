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
        leftPane.Bind(Border.BorderBrushProperty, leftPane.GetResourceObservable("Passenger.Divider"));
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
        ComboBox look = new() { ItemsSource = LookChoices.Select(c => c.Name).ToArray(), SelectedIndex = Array.FindIndex(LookChoices, c => c.Look == Working.Look), Classes = { "form" } };
        look.SelectionChanged += (_, _) => Change(s => s with { Look = LookChoices[Math.Max(0, look.SelectedIndex)].Look });
        Add(Appearance, "Theme", look, "colour color theme look palette " + string.Join(" ", LookChoices.Select(c => c.Name.ToLowerInvariant())));
        ComboBox theme = new() { ItemsSource = new[] { "System", "Dark", "Light" }, SelectedIndex = Working.Theme switch { ThemeChoice.Dark => 1, ThemeChoice.Light => 2, _ => 0 }, Classes = { "form" } };
        theme.SelectionChanged += (_, _) => Change(s => s with { Theme = theme.SelectedIndex switch { 1 => ThemeChoice.Dark, 2 => ThemeChoice.Light, _ => ThemeChoice.System } });
        Add(Appearance, "Dark or light", theme, "colour color theme dark light system sync os appearance mode");
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
            Add(Appearance, "Show main menu as a menu bar", Check(Working.MainMenuBar, v => Change(s => s with { MainMenuBar = v })),
                "main menu bar hamburger file edit view toolbar ribbon menus",
                "File, Edit, View and the rest side by side on the toolbar rather than behind its button. Those the toolbar has no room for go behind the button after them.");
        }

        Add(SystemPage, "Reopen projects on startup", Check(Working.ReopenProjectsOnStartup, v => Change(s => s with { ReopenProjectsOnStartup = v })),
            "reopen last solution project folder startup start launch welcome screen recent");
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
        Add(SystemPage, "Save when leaving the IDE", Check(Working.SaveOnDeactivation, v => Change(s => s with { SaveOnDeactivation = v })),
            "autosave files switching to a different application or the built-in terminal focus frame deactivation");
        NumericUpDown seconds = Number(Working.IdleSeconds, AppSettings.MinIdleSeconds, AppSettings.MaxIdleSeconds, 1, v => Change(s => s with { IdleSeconds = (int)v }));
        seconds.IsEnabled = Working.SaveWhenIdle;
        CheckBox idle = Check(Working.SaveWhenIdle, v =>
        {
            Change(s => s with { SaveWhenIdle = v });
            seconds.IsEnabled = v;
        });
        TextBlock unit = new() { Text = "seconds", VerticalAlignment = VerticalAlignment.Center };
        Add(SystemPage, "Save when idle for", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { idle, seconds, unit } },
            "autosave files idle inactive seconds");

        Add(UpdatesPage, "Check for updates", Check(Working.CheckForUpdates, v => Change(s => s with { CheckForUpdates = v })),
            "updates check new version release newer download github automatic install restart",
            "Passenger asks GitHub for its latest release when it starts and once a day. When a newer one is out, an installed "
            + "Passenger downloads it, checks it against the SHA-256 the release gives, and installs it when it restarts; a "
            + "copy built from source says in the status bar that it is out. Nothing is sent but the requests, which name "
            + "Passenger and its version.");

        ComboBox scheme = new() { ItemsSource = ColourSchemeChoices.Select(c => c.Name).ToArray(), SelectedIndex = Array.FindIndex(ColourSchemeChoices, c => c.Scheme == Working.ColourScheme), Classes = { "form" } };
        scheme.SelectionChanged += (_, _) => Change(s => s with { ColourScheme = ColourSchemeChoices[Math.Max(0, scheme.SelectedIndex)].Scheme });
        Add(EditorPage, "Colour scheme", scheme, "colour color scheme syntax highlighting code colours rider keywords strings comments",
            "The colours the code is drawn in: the theme's own, or Rider's, Dark or Light as the theme is. The editor keeps the theme's background.");
        Add(EditorPage, "Font size", Number(Working.EditorFontSize, AppSettings.MinFontSize, AppSettings.MaxFontSize, 1, v => Change(s => s with { EditorFontSize = v })), "font size text");
        Add(EditorPage, "Line spacing", Number(Working.EditorLineSpacing, AppSettings.MinLineSpacing, AppSettings.MaxLineSpacing, 0.1, v => Change(s => s with { EditorLineSpacing = v })), "font line height");
        Add(EditorPage, "Enable ligatures", Check(Working.Ligatures, v => Change(s => s with { Ligatures = v })), "font arrows");
        Add(EditorPage, "Show line numbers", Check(Working.ShowLineNumbers, v => Change(s => s with { ShowLineNumbers = v })), "gutter appearance");
        Add(EditorPage, "Rainbow braces", Check(Working.RainbowBraces, v => Change(s => s with { RainbowBraces = v })), "brackets parentheses colour color nesting");
        Add(EditorPage, "Solution-wide analysis", Check(Working.SolutionWideAnalysis, v => Change(s => s with { SolutionWideAnalysis = v })),
            "errors problems whole solution analysis inspections without build");
        Add(EditorPage, "Show parameter name hints", Check(Working.ParameterHints, v => Change(s => s with { ParameterHints = v })), "inlay hints parameter names arguments");
        Add(EditorPage, "Show type name hints", Check(Working.TypeHints, v => Change(s => s with { TypeHints = v })), "inlay hints type names var lambda parameters new inferred");
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
        Add(EditorPage, "Use the original line's indent for wrapped fragments", Check(Working.SoftWrapIndent, v => Change(s => s with { SoftWrapIndent = v })),
            "soft wrap wrapped indent indentation fragments");
        Add(EditorPage, "Show whitespaces", Check(Working.ShowWhitespaces, v => Change(s => s with { ShowWhitespaces = v })),
            "whitespace spaces tabs dots arrows invisible characters appearance");
        Add(EditorPage, OperatingSystem.IsMacOS() ? "Change font size with Command+Mouse Wheel" : "Change font size with Ctrl+Mouse Wheel",
            Check(Working.WheelZoom, v => Change(s => s with { WheelZoom = v })), "zoom font size mouse wheel scroll ctrl control command cmd");

        const string Folding = "fold by default folding collapse collapsed open opening region";
        Add(CodeFoldingPage, "File header", Check(Working.FoldFileHeader, v => Change(s => s with { FoldFileHeader = v })),
            $"{Folding} comments licence license copyright top",
            "Fold by default: what folds as a file opens. A file keeps the folds it was closed with, after a restart too.");
        Add(CodeFoldingPage, "Imports", Check(Working.FoldImports, v => Change(s => s with { FoldImports = v })), $"{Folding} usings using directives");
        Add(CodeFoldingPage, "Documentation comments", Check(Working.FoldDocComments, v => Change(s => s with { FoldDocComments = v })),
            $"{Folding} doc xml summary comments");
        Add(CodeFoldingPage, "Method bodies", Check(Working.FoldMethodBodies, v => Change(s => s with { FoldMethodBodies = v })),
            $"{Folding} members bodies accessors properties functions");
        Add(CodeFoldingPage, "Preprocessor regions", Check(Working.FoldPreprocessorRegions, v => Change(s => s with { FoldPreprocessorRegions = v })),
            $"{Folding} #region endregion directives .net");
        Add(CodeFoldingPage, "Line and block comments", Check(Working.FoldComments, v => Change(s => s with { FoldComments = v })),
            $"{Folding} block comments .net");
        Add(CodeFoldingPage, "XML tags", Check(Working.FoldXmlTags, v => Change(s => s with { FoldXmlTags = v })),
            $"{Folding} xml elements tags project csproj props axaml xaml");

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
        ComboBox results = new() { ItemsSource = BuildResultsChoices, SelectedIndex = (int)Working.ShowBuildResults, Classes = { "form" } };
        results.SelectionChanged += (_, _) => Change(s => s with { ShowBuildResults = (BuildResultsMode)Math.Max(0, results.SelectedIndex) });
        Add(BuildPage, "Showing build results after build completed", results,
            "build tool window results after completed finished failed errors warnings always never toolset presentation");

        Add(HotReloadPage, "Hot Reload when debugging (Edit and Continue)", Check(Working.HotReloadWhileDebugging, v => Change(s => s with { HotReloadWhileDebugging = v })),
            "hot reload edit and continue enc debug apply changes paused running alt f10");
        Add(HotReloadPage, "Hot Reload when running (dotnet watch)", Check(Working.HotReload, v => Change(s => s with { HotReload = v })),
            "hot reload dotnet watch run apply changes on save running without debugging");
        Add(HotReloadPage, "Apply changes when a file is saved", Check(Working.HotReloadOnSave, v => Change(s => s with { HotReloadOnSave = v })),
            "hot reload save apply changes debug edit and continue");
        Add(HotReloadPage, "Always restart for a change it cannot apply", Check(Working.RestartOnRudeEdit, v => Change(s => s with { RestartOnRudeEdit = v })),
            "hot reload rude edit restart ask always never dotnet watch");
        NumericUpDown buffer = Number(Working.ConsoleBufferKb, AppSettings.MinConsoleBufferKb, AppSettings.MaxConsoleBufferKb, 64, v => Change(s => s with { ConsoleBufferKb = (int)v }));
        Add(RunPage, "Console buffer size", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { buffer, new TextBlock { Text = "KB", VerticalAlignment = VerticalAlignment.Center } } },
            "console cycle buffer size output limit lines run debug build kb");

        string? onPath = Passenger.Core.Programs.Node();
        TextBox node = new() { Classes = { "search" }, Text = Working.NodeInterpreter ?? string.Empty, PlaceholderText = onPath ?? "Not found on the PATH", Width = 320 };
        node.TextChanged += (_, _) => Change(s => s with { NodeInterpreter = string.IsNullOrWhiteSpace(node.Text) ? null : node.Text.Trim() });
        Add(NodePage, "Node interpreter", node, "node nodejs interpreter javascript typescript html css json language server npm path",
            "The Node.js the JavaScript, TypeScript, HTML, CSS and JSON language servers run on. Empty, it is the first on the PATH, "
            + "as the login shell finds it. Without one, those files keep their colours only.");
        AddCodeQualityTools();
        Add(SchemasPage, "Allow downloading JSON Schemas from remote sources", Check(Working.DownloadJsonSchemas, v => Change(s => s with { DownloadJsonSchemas = v })),
            "json schema schemas remote download web http https schemastore $schema validation completion",
            "A JSON file that names a schema on the web ($schema) has it downloaded to check and complete it. package.json, "
            + "tsconfig.json, jsconfig.json, appsettings.json, launchSettings.json and global.json use the schemas Passenger "
            + "carries from SchemaStore, which are never downloaded.");
        Add(DockerPage, "Complete image tags from Docker Hub", Check(Working.DockerHubTags, v => Change(s => s with { DockerHubTags = v })),
            "docker hub dockerfile containerfile image images tag tags from completion registry remote download web network",
            "In a Dockerfile, completion after FROM image: lists the image's tags, which the Dockerfile language server asks "
            + "Docker Hub for, naming the image. Off, it asks Docker Hub nothing and offers no tags.");
        Add(DockerPage, "Check Dockerfiles with BuildKit", Check(Working.BuildKitChecks, v => Change(s => s with { BuildKitChecks = v })),
            "buildkit buildx build check checks lint rules warnings dockerfile containerfile base image images metadata registry registries network engine",
            "As a Dockerfile opens and is saved, the container engine's BuildKit checks it by its build rules with the engine's own "
            + "Dockerfile frontend, never one a # syntax= line names, which is neither pulled nor run. BuildKit reads each base "
            + "image's metadata from its registry. Off, no check runs and no registry is asked; a compose file's values and errors, "
            + "which Compose works out on this computer, stay.");

        Add(TerminalPage, "Font size", Number(Working.TerminalFontSize, AppSettings.MinFontSize, AppSettings.MaxFontSize, 1, v => Change(s => s with { TerminalFontSize = v })), "font size shell");
        TextBox terminalFont = new() { Classes = { "search" }, Text = Working.TerminalFontFamily ?? string.Empty, PlaceholderText = "JetBrains Mono", Width = 320 };
        terminalFont.TextChanged += (_, _) => Change(s => s with { TerminalFontFamily = terminalFont.Text });
        Add(TerminalPage, "Font family", terminalFont, "font family name typeface shell",
            "An installed monospace font. Blank, unavailable or proportional fonts use bundled JetBrains Mono.");
        Add(TerminalPage, "Copy on selection", Check(Working.TerminalCopyOnSelection, v => Change(s => s with { TerminalCopyOnSelection = v })),
            "copy selection clipboard mouse drag word");
        Add(TerminalPage, "Cursor blinking", Check(Working.TerminalCursorBlinking, v => Change(s => s with { TerminalCursorBlinking = v })),
            "cursor caret blink blinking", "Programs can override this setting until the terminal is reset.");
        ComboBox terminalCursor = new()
        {
            ItemsSource = new[] { "Block", "Line", "Underline" }, Classes = { "form" },
            SelectedIndex = Working.TerminalCursorStyle switch { Passenger.Terminal.CursorShape.Bar => 1, Passenger.Terminal.CursorShape.Underline => 2, _ => 0 },
        };
        terminalCursor.SelectionChanged += (_, _) => Change(s => s with
        {
            TerminalCursorStyle = terminalCursor.SelectedIndex switch { 1 => Passenger.Terminal.CursorShape.Bar, 2 => Passenger.Terminal.CursorShape.Underline, _ => Passenger.Terminal.CursorShape.Block },
        });
        Add(TerminalPage, "Cursor style", terminalCursor, "cursor caret type style shape block line bar underline",
            "Programs can override this setting until the terminal is reset.");
        (string shell, _) = Passenger.Terminal.TerminalSession.DefaultShell();
        TextBox path = new() { Classes = { "search" }, Text = Working.TerminalShell ?? string.Empty, PlaceholderText = shell, Width = 320 };
        path.TextChanged += (_, _) => Change(s => s with { TerminalShell = string.IsNullOrWhiteSpace(path.Text) ? null : path.Text.Trim() });
        Add(TerminalPage, "Shell path", path, "shell zsh bash powershell program");

        AddAgents(agents);

        ComboBox shows = new() { ItemsSource = DiscordChoices, SelectedIndex = (int)Working.DiscordShows, Classes = { "form" }, IsEnabled = Working.ShowInDiscord };
        shows.SelectionChanged += (_, _) => Change(s => s with { DiscordShows = (DiscordShown)Math.Max(0, shows.SelectedIndex) });
        NumericUpDown idleMinutes = Number(Working.DiscordIdleMinutes, AppSettings.MinDiscordIdleMinutes, AppSettings.MaxDiscordIdleMinutes, 1,
            v => Change(s => s with { DiscordIdleMinutes = (int)v }));
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

        Add(DiagnosticsPage, "Trace protocol messages", Check(Working.TraceProtocols, v => Change(s => s with { TraceProtocols = v })),
            "trace log lsp dap json-rpc language server debugger adapter vstest tests messages verbose mcp agent calls",
            "Every message to and from the C# language server, the debugger and the test runner is written to lsp.log, dap.log "
            + "and vstest.log in the logs folder (Help, Show Log), and each agent's MCP call, with what it waited on and for how "
            + "long, to mcp.log.");
        Add(DiagnosticsPage, "Log every program started", Check(Working.LogEveryProgram, v => Change(s => s with { LogEveryProgram = v })),
            "debug log processes programs commands git dotnet exit code time",
            "Each program the IDE runs (git, dotnet, the servers) is written to passenger.log with its exit code and time. "
            + "Otherwise only those that fail or take a second or more are.");

        AddKeymap();
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

    private static NumericUpDown Number(double value, double min, double max, double step, Action<double> changed)
    {
        NumericUpDown number = new() { Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, Width = 140, FormatString = step < 1 ? "0.0" : "0" };
        number.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } next)
            {
                changed((double)next);
            }
        };
        return number;
    }

    private static CheckBox Check(bool value, Action<bool> changed)
    {
        CheckBox box = new() { IsChecked = value };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    private sealed record Option(string Page, string Label, Control Row, string Words, string? Action = null);
}
