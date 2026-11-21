using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.ToolWindows;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>Settings: the pages down the left under a search box, the chosen page's options on the right, OK, Cancel
/// and Apply below. A search keeps the pages with an option whose words match and shows only those options. The options
/// edit a copy, previewing its visual changes until Apply or OK commits them or closing discards them.</summary>
public sealed partial class SettingsDialog : IdeWindow
{
    public const string Appearance = "Appearance";
    public const string SystemPage = "System Settings";
    public const string UpdatesPage = "Updates";
    public const string ExplorerPage = "Explorer";
    public const string EditorPage = "Editor";
    public const string CodeFoldingPage = "Code Folding";
    public const string LiveTemplatesPage = "Live Templates";
    public const string PostfixPage = "Postfix Completion";
    public const string XamlCodeStylePage = "Code Style: XAML";
    public const string GDScriptCodeStylePage = "Code Style: GDScript";
    public const string TerminalPage = "Terminal";
    public const string VersionControlPage = "Version Control";
    public const string BuildPage = "Build";
    public const string RunPage = "Run";
    public const string HotReloadPage = "Hot Reload";
    public const string NodePage = "Node.js";
    public const string EslintPage = "ESLint";
    public const string PrettierPage = "Prettier";
    public const string SchemasPage = "Remote JSON Schemas";
    public const string StylesheetsPage = "Remote Stylesheets";
    public const string DockerPage = "Docker";
    public const string GodotPage = "Godot";
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

    /// <summary>The names of <see cref="DiscordShown"/>'s choices, in its order.</summary>
    public static readonly string[] DiscordChoices = ["Passenger only", "The solution", "The solution and the file"];

    private static readonly string[] Order = [Appearance, SystemPage, TrustPage, UpdatesPage, ExplorerPage, EditorPage, CodeFoldingPage, PostfixPage, XamlCodeStylePage, GDScriptCodeStylePage, LiveTemplatesPage,
        VersionControlPage, BuildPage, RunPage, HotReloadPage, NodePage, EslintPage, PrettierPage, SchemasPage, StylesheetsPage, DockerPage, GodotPage, TerminalPage, McpPage, DiscordPage, DiagnosticsPage, KeymapPage];

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
    internal const string GDScriptWords = "gdscript gd godot code style formatting reformat format layout style guide";
    internal const string EslintWords = "eslint lint linter linting javascript typescript code quality tools problems inspections";
    internal const string PrettierWords = "prettier format formatter formatting reformat code style javascript typescript css json";
    internal const string McpWords = "mcp model context protocol agents ai claude codex opencode attach socket";

    /// <summary>Puts what the options changed in force: from the settings as they were applied last, to the options'.</summary>
    private readonly Action<AppSettings, AppSettings> apply;
    private readonly SettingsPreview? preview;

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
    private readonly EmptyState nothing;

    /// <summary>The page's room at its right: its gutter, where its scroll bar shows over the page, clear of what it shows.</summary>
    private const double Gutter = Gaps.Page;

    /// <summary>The search field's room in the pages' column, the pages' bands as wide as it below.</summary>
    private static readonly Thickness PagesInset = new(Gaps.Normal);

    /// <summary>What a page keeps above its options as they scroll (the Keymap page's toolbar).</summary>
    private readonly Decorator pageTop = new() { Margin = new Thickness(0, 0, Gutter, 0) };

    /// <summary>OK and Apply, which put the options in force: greyed while an option holds a value Settings refuses.</summary>
    private readonly Button ok = DialogFooter.Default();
    private readonly Button applyButton = new() { Content = "Apply" };

    /// <param name="preview">An optional temporary visual overlay, released when this dialog closes.</param>
    /// <param name="agents">The agents' setup the MCP Server page shows: this copy's, and the window's folder.</param>
    /// <param name="editorConfigs">What the window's folder's .editorconfig files set over the rows, followed while it is open.</param>
    /// <param name="trust">The folders the person answered for, which the Trusted Folders page shows, where the window keeps them.</param>
    public SettingsDialog(AppSettings current, ActionRegistry actions, Action<AppSettings, AppSettings> apply, Mcp.AgentSetup? agents = null, SettingsPreview? preview = null,
        FolderEditorConfigs? editorConfigs = null, TrustAnswers? trust = null)
    {
        this.apply = apply;
        this.trust = trust;
        this.preview = preview;
        this.editorConfigs = editorConfigs;
        Closed += (_, _) => preview?.Dispose();
        this.actions = actions;
        Working = current;
        applied = current;
        Title = "Settings";
        Width = DialogWidths.Workspace;
        Height = 700;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddOptions(agents ?? Mcp.AgentSetup.Here(null, current.TerminalShell));
        search = SearchBox.Of("Search settings");
        search.Margin = new Thickness(PagesInset.Left, PagesInset.Top, PagesInset.Right, Gaps.Small);
        search.TextChanged += (_, _) => Filter();
        pages = new ListBox { Classes = { StyleClasses.Tree, StyleClasses.Pages }, Padding = new Thickness(PagesInset.Left, 0) };
        pages.SelectionChanged += (_, _) => ShowPage();
        heading = new TextBlock { Classes = { StyleClasses.PageTitle }, Margin = new Thickness(0, 0, Gutter, Gaps.Wide) };
        // The scroll bar shows over the scrolled page's right, so the gutter is inside it: the bar stands there, beside the rows.
        body = new StackPanel { Spacing = Gaps.Wide, Margin = new Thickness(0, 0, Gutter, 0) };
        nothing = new EmptyState("Nothing matches") { IsVisible = false };

        ok.Click += (_, _) => Ok();
        applyButton.Click += (_, _) => Apply();

        DockPanel left = new() { Width = 240 };
        DockPanel.SetDock(search, Dock.Top);
        left.Children.Add(search);
        left.Children.Add(new Panel { Children = { pages, nothing } });
        Border leftPane = new() { BorderThickness = Hairlines.After, Child = left };
        leftPane.Bind(Border.BorderBrushProperty, leftPane.GetResourceObservable(TokenKeys.Divider));
        DockPanel right = new() { Margin = new Thickness(Gaps.Page, Gaps.Group, 0, 0) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(pageTop, Dock.Top);
        right.Children.Add(heading);
        right.Children.Add(pageTop);
        right.Children.Add(new ScrollViewer { Content = body });
        DockPanel.SetDock(leftPane, Dock.Left);
        Content = DialogFooter.Page(new DockPanel { Children = { leftPane, right } }, DialogFooter.OkCancel(ok, () => Close(false), more: applyButton));
        Filter();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);
        if (editorConfigs is not null)
        {
            editorConfigs.Changed += EditorConfigsChanged;
            Closed += (_, _) => editorConfigs.Changed -= EditorConfigsChanged;
            IdeErrors.Observe(ShowSaidOverAsync());
        }
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

    /// <summary>Why the options as they stand cannot be put in force, naming the setting whose value Settings refuses;
    /// null when they can.</summary>
    public string? Refused => Declared.All.Select(s => s.Refused(applied, Working)).FirstOrDefault(r => r is not null);

    /// <summary>OK: the settings applied, and the dialog closed; nothing while a value is refused.</summary>
    public void Ok()
    {
        if (Refused is null)
        {
            Apply();
            Close(true);
        }
    }

    /// <summary>Apply: what the options changed put in force, the dialog kept open; nothing while a value is refused.</summary>
    public void Apply()
    {
        if (Refused is null)
        {
            apply(applied, Working);
            applied = Working;
            preview?.Update(applied, Working);
            ApplyTrust();
        }
    }

    /// <summary>Changes one setting, as its option does, OK and Apply greyed while a value it holds is refused.</summary>
    public void Change(Func<AppSettings, AppSettings> change)
    {
        Working = change(Working);
        ok.IsEnabled = applyButton.IsEnabled = Refused is null;
        preview?.Update(applied, Working);
        foreach ((_, Control editor, SettingWhen when) in dependents)
        {
            Follow(editor, when);
        }
    }

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
        AddThemes();
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
        Add(Declared.WindowFontFamily);
        Add(Declared.DisplayScale);
        Add(Declared.EditorTabs);
        Add(Declared.ToolWindowLook);
        Add(Declared.ClosedToolWindows);
        Add(Declared.FindResults);

        Add(Declared.ReopenProjectsOnStartup);
        StackPanel openIn = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Group };
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
        Add(Declared.ConfirmBeforeClosing);
        Add(Declared.SaveOnDeactivation);
        NumericUpDown seconds = Declared.IdleSeconds.Box(Working, Change);
        seconds.IsEnabled = Working.SaveWhenIdle;
        CheckBox idle = Check(Working.SaveWhenIdle, v =>
        {
            Change(s => s with { SaveWhenIdle = v });
            seconds.IsEnabled = v;
        });
        Add(SystemPage, "Save when idle for", new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Normal, Children = { idle, NumberSetting.WithUnit(seconds, "seconds") } },
            "autosave files idle inactive seconds");

        AddTrust();
        Add(Declared.CheckForUpdates);

        Add(Declared.AlwaysSelectOpenedFile);
        Add(Declared.OpenFilesWithSingleClick);
        Add(Declared.NestFiles);

        Add(Declared.EditorFontFamily);
        Add(Declared.EditorFontSize);
        Add(Declared.EditorLineSpacing);
        Add(Declared.CaretWidth);
        Add(Declared.Ligatures);
        Add(Declared.ShowLineNumbers);
        Add(Declared.RainbowBraces);
        Add(Declared.VimMode);
        Add(Declared.VimKeysToKeymap, Declared.VimKeysToKeymap.Box(Working, Change, stroke => KeymapDoes(actions, stroke)));
        Add(Declared.CloseTags);
        Add(Declared.CompletionParentheses);
        Add(Declared.FormatOnType);
        Add(Declared.XamlWorkspace);
        Add(Declared.SolutionWideAnalysis);
        Add(Declared.ParameterHints);
        Add(Declared.TypeHints);
        TextBox masks = new() { Text = Working.SoftWrapFiles, PlaceholderText = "No files", IsEnabled = Working.SoftWrap };
        ToolTip.SetTip(masks, "Use * and ? as wildcards and ; to separate patterns");
        masks.TextChanged += (_, _) => Change(s => s with { SoftWrapFiles = masks.Text ?? string.Empty });
        CheckBox wrapFiles = Check(Working.SoftWrap, v =>
        {
            Change(s => s with { SoftWrap = v });
            masks.IsEnabled = v;
        });
        DockPanel.SetDock(wrapFiles, Dock.Left);
        Add(EditorPage, "Soft-wrap these files", new DockPanel { HorizontalSpacing = 8, MaxWidth = OptionRow.EditorWidth, Children = { wrapFiles, masks } },
            "soft wrap wrapping long lines width markdown text files masks");
        Add(Declared.SoftWrapIndent);
        Add(Declared.ShowWhitespaces);
        Add(Declared.WheelZoom);
        Add(Declared.ReformatOnSave);
        Add(Declared.EnsureFinalLineBreak);

        Add(Declared.FoldFileHeader);
        Add(Declared.FoldImports);
        Add(Declared.FoldDocComments);
        Add(Declared.FoldMethodBodies);
        Add(Declared.FoldPreprocessorRegions);
        Add(Declared.FoldComments);
        Add(Declared.FoldXmlTags);

        AddXamlCodeStyle();
        Add(Declared.GDScriptWrapLongLines);
        Add(Declared.GDScriptLineLength);
        Add(Declared.GDScriptBlankLines);
        Add(Declared.GDScriptTrailingCommas);
        Add(Declared.GDScriptFormatOnSave);

        Postfix = new PostfixTemplatesPane(Working, Change);
        AddPage(PostfixPage, Postfix, "postfix templates completion expression dot autopopup "
            + string.Join(' ', Languages.CSharp.PostfixTemplates.All.Select(t => $"{t.Name} {t.Description} {t.Example}")));
        Templates = new LiveTemplatesPane(TemplateChange.Applied(Working.LiveTemplates), list => Change(s => s with { LiveTemplates = TemplateChange.Of(list) }));
        AddPage(LiveTemplatesPage, Templates, "live templates c# shortcut abbreviation snippet expand tab surround with generation availability mnemonic variables macros "
            + string.Join(' ', CSharp.LiveTemplates.BuiltIn.Select(t => $"{t.Abbreviation} {t.Description}")));

        Add(Declared.OpenDiffsIn);
        Add(Declared.DiffHighlighting);

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
        Add(Declared.XamlHotReloadOnSave);
        Add(Declared.RestartOnRudeEdit);
        Add(Declared.WhenDebuggerStops);
        Add(Declared.AskBeforeRerun);
        Add(Declared.ConsoleBufferKb);
        Add(Declared.Browser);

        string? onPath = Passenger.Core.Programs.Node();
        TextBox node = new() { Text = Working.NodeInterpreter ?? string.Empty, PlaceholderText = onPath ?? "Not found on the PATH", MaxWidth = OptionRow.EditorWidth };
        node.TextChanged += (_, _) => Change(s => s with { NodeInterpreter = string.IsNullOrWhiteSpace(node.Text) ? null : node.Text.Trim() });
        Add(NodePage, "Node interpreter", node, "node nodejs interpreter javascript typescript html css json language server npm path",
            "The Node.js the JavaScript, TypeScript, HTML, CSS and JSON language servers run on. Empty, it is the first on the PATH, "
            + "as the login shell finds it. Without one, those files keep their colours only.");
        AddCodeQualityTools();
        Add(Declared.DownloadJsonSchemas);
        Add(Declared.DownloadStylesheets);
        Add(Declared.DockerHubTags);
        Add(Declared.BuildKitChecks);
        Add(Declared.GodotExecutable);
        Add(Declared.GodotLiveValues);
        Add(Declared.GodotTestsWindowed);
        Add(Declared.GodotDebugPlays);
        Add(Declared.GodotSelectionFollows);

        Add(Declared.TerminalFontSize);
        Add(Declared.TerminalFontFamily);
        Add(Declared.TerminalCopyOnSelection);
        Add(Declared.TerminalCursorBlinking);
        Add(Declared.TerminalCursorStyle);
        Add(Declared.TerminalShell);

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
            Spacing = Gaps.Normal,
            Children = { whenIdle, NumberSetting.WithUnit(idleMinutes, "minutes") },
        }, $"{Discord} idle inactive away timeout minutes");

        Add(Declared.TraceProtocols);
        Add(Declared.LogEveryProgram);

        Add(Declared.ChordWaitSeconds);
        AddKeymap();
    }

    /// <summary>A declared setting's own row, on its page, its editor made from the declaration.</summary>
    private void Add(Setting setting) => Add(setting, setting.Editor(Working, Change));

    /// <summary>A declared setting's own row, its editor made by the dialog round what only the dialog knows.</summary>
    private void Add(Setting setting, Control editor)
    {
        SettingRow row = setting.Row ?? throw new ArgumentException("A setting with no row of its own goes in a row the dialog builds", nameof(setting));
        Add(row.Page, row.Label, editor, row.Words, row.NoteShown, over: row.Over);
        if (row.When is { } when)
        {
            // Greyed, saying why, while the setting it follows leaves it no effect.
            ToolTip.SetShowOnDisabled(editor, true);
            dependents.Add((row.Label, editor, when));
            Follow(editor, when);
        }
    }

    /// <summary>The rows another setting greys, each with what it takes effect under.</summary>
    private readonly List<(string Label, Control Editor, SettingWhen When)> dependents = [];

    private void Follow(Control editor, SettingWhen when)
    {
        bool holds = when.Holds(Working);
        editor.IsEnabled = holds;
        ToolTip.SetTip(editor, holds ? null : when.Why);
    }

    /// <summary>Why a row is greyed as the settings stand; null where it takes effect, or is no row another greys.</summary>
    public string? WhyGreyed(string label) =>
        dependents.FirstOrDefault(d => d.Label == label) is { Editor: { } editor, When: { } when } && !when.Holds(Working) && !editor.IsEnabled ? when.Why : null;

    /// <summary>What the folder's .editorconfig files set over the rows, followed while the dialog is open.</summary>
    private readonly FolderEditorConfigs? editorConfigs;

    /// <summary>Each row the folder's .editorconfig files may set, by its label, with what says so under it.</summary>
    private readonly List<(string Label, EditorConfigOver Over, TextBlock Said)> saidOver = [];

    /// <summary>Reads of what the .editorconfig files set, the last asked alone landing.</summary>
    private int saidAsked;

    /// <summary>What the folder's .editorconfig files set over a row, by its label, a line to each key as the row shows them;
    /// null where they set nothing over it.</summary>
    public string? SaidOver(string label) => saidOver.FirstOrDefault(s => s.Label == label).Said is { IsVisible: true } said ? said.Text : null;

    private void EditorConfigsChanged(object? sender, EventArgs e) => IdeErrors.Observe(ShowSaidOverAsync());

    /// <summary>Says under each row what the folder's .editorconfig files set over it, once read off the window's thread.</summary>
    private async Task ShowSaidOverAsync()
    {
        int mine = ++saidAsked;
        EditorConfigLines read = await editorConfigs!.ReadAsync();
        if (mine != saidAsked)
        {
            return;
        }
        foreach ((_, EditorConfigOver over, TextBlock said) in saidOver)
        {
            IReadOnlyList<string> lines = read.Shortly(over);
            said.Text = string.Join('\n', lines);
            said.IsVisible = lines.Count > 0;
        }
    }

    /// <summary>A page that is one control of its own (the live templates' list and editor), the whole of its width, found
    /// by a search for any of its words.</summary>
    private void AddPage(string page, Control content, string keywords) => options.Add(new Option(page, page, content, $"{page} {keywords}"));

    /// <summary>An option's row: its label, what edits it, and a note under the editor when it needs saying what it
    /// does; a keymap row names its action, and one the folder's .editorconfig files may set the keys it is said over by.</summary>
    private Control Add(string page, string label, Control editor, string keywords, string? note = null, string? action = null, EditorConfigOver? over = null)
    {
        // Beside an editor of several lines the label stands level with its first line.
        double firstLine = editor switch
        {
            TextBox { AcceptsReturn: true } => 5,
            StackPanel { Orientation: Orientation.Vertical, Children: [TextBox, ..] } => 5,
            global::Passenger.App.Settings.ThemeExplorer => 5,
            StackPanel { Orientation: Orientation.Vertical, Children: [RadioButton or Border { Classes: [StyleClasses.Framed] }, ..] } => 6,
            _ => -1,
        };
        TextBlock name = new()
        {
            Text = label,
            Padding = new Thickness(0, 0, Gaps.Group, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLabeledBy(editor, name);
        // The one field of an editor of several parts (a check box before it, its problem under it) is heard by the row's words.
        if (editor is not TextBox && editor.GetLogicalDescendants().OfType<TextBox>().ToList() is [{ } field] && AutomationProperties.GetName(field) is null or "")
        {
            AutomationProperties.SetLabeledBy(field, name);
        }
        TextBlock? said = note is null ? null : new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left, Classes = { StyleClasses.Muted } };
        TextBlock? setOver = null;
        if (over is not null)
        {
            setOver = new TextBlock { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false, Classes = { StyleClasses.SaidOver } };
            saidOver.Add((label, over, setOver));
        }
        OptionRow row = new(name, editor, said, firstLine, setOver);
        // A row an .editorconfig says over is found by the keys that do, as a person reads them in the file.
        string keys = over is null ? string.Empty : $" editorconfig {string.Join(' ', over.Keys.Concat(over.Aliases))}";
        options.Add(new Option(page, label, row, $"{page} {label} {keywords}{keys}", action));
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
