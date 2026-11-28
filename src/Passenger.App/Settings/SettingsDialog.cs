using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>Settings: the pages down the left in a tree under a search box, the chosen page's options on the right, OK,
/// Cancel and Apply below. A search keeps the pages with an option whose words (or its pages' names) match, shown with
/// the pages above them, and shows only those options. The options edit a copy, previewing its visual changes until
/// Apply or OK commits them or closing discards them.</summary>
public sealed partial class SettingsDialog : IdeWindow
{
    public const string Appearance = "Appearance";
    public const string GeneralPage = "General";
    public const string AccessibilityPage = "Accessibility";
    public const string UpdatesPage = "Updates";
    public const string ExplorerPage = "Explorer";
    public const string EditorPage = "Editor";
    public const string CodeFoldingPage = "Code Folding";
    public const string LiveTemplatesPage = "Live Templates";
    public const string PostfixPage = "Postfix Completion";
    public const string CodeStylePage = "Code Style";
    public const string XamlCodeStylePage = "XAML";
    public const string GDScriptCodeStylePage = "GDScript";
    public const string SqlCodeStylePage = "SQL";
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
    public const string BuildAndRunPage = "Build and Run";
    public const string LanguagesPage = "Languages and Frameworks";
    public const string WebPage = "Web";
    public const string ToolsPage = "Tools";

    /// <summary>Every page under the one it sits under (none at the top), in the order the tree lists them: the one
    /// declaration of where a page stands, which the tree, a search and every path to a page follow.</summary>
    private static readonly (string Page, string? Parent)[] Tree =
    [
        (Appearance, null), (AccessibilityPage, Appearance),
        (GeneralPage, null), (TrustPage, GeneralPage), (UpdatesPage, GeneralPage),
        (KeymapPage, null),
        (EditorPage, null), (CodeFoldingPage, EditorPage), (LiveTemplatesPage, EditorPage), (PostfixPage, EditorPage),
        (CodeStylePage, EditorPage), (XamlCodeStylePage, CodeStylePage), (GDScriptCodeStylePage, CodeStylePage), (SqlCodeStylePage, CodeStylePage),
        (ExplorerPage, null),
        (VersionControlPage, null),
        (BuildAndRunPage, null), (BuildPage, BuildAndRunPage), (RunPage, BuildAndRunPage), (HotReloadPage, BuildAndRunPage), (DockerPage, BuildAndRunPage),
        (LanguagesPage, null), (GodotPage, LanguagesPage), (WebPage, LanguagesPage),
        (NodePage, WebPage), (EslintPage, WebPage), (PrettierPage, WebPage), (SchemasPage, WebPage), (StylesheetsPage, WebPage),
        (ToolsPage, null), (TerminalPage, ToolsPage), (McpPage, ToolsPage), (DiscordPage, ToolsPage), (DiagnosticsPage, ToolsPage),
    ];

    /// <summary>Every page, in the tree's order.</summary>
    public static IReadOnlyList<string> EveryPage { get; } = [.. Tree.Select(t => t.Page)];

    /// <summary>The page a page sits under; null for one at the top.</summary>
    public static string? ParentOf(string page) => Tree.Single(t => t.Page == page).Parent;

    /// <summary>The pages from the top down to a page, itself last.</summary>
    public static IReadOnlyList<string> PathOf(string page)
    {
        List<string> path = [page];
        for (string? above = ParentOf(page); above is not null; above = ParentOf(above))
        {
            path.Insert(0, above);
        }
        return path;
    }

    /// <summary>A page as a message sends a person to it, by its path in the tree: "Settings | Languages and Frameworks |
    /// Web | Node.js".</summary>
    public static string PlaceOfPage(string page) => $"Settings | {string.Join(" | ", PathOf(page))}";

    /// <summary>An option's row as a message sends a person to it: its page's place, then its label.</summary>
    public static string PlaceOfRow(string page, string label) => $"{PlaceOfPage(page)} | {label}";

    /// <summary>The names of <see cref="BuildResultsMode"/>'s choices, in its order.</summary>
    public static readonly string[] BuildResultsChoices = ["Always", "Only if errors or warnings received", "Only if errors received", "Never"];

    /// <summary>The names of <see cref="OpenProjectIn"/>'s choices, in its order.</summary>
    public static readonly (OpenProjectIn Choice, string Name)[] OpenProjectChoices =
        [(OpenProjectIn.NewWindow, "New window"), (OpenProjectIn.CurrentWindow, "Current window"), (OpenProjectIn.Ask, "Ask")];

    /// <summary>The names of <see cref="DiscordShown"/>'s choices, in its order.</summary>
    public static readonly string[] DiscordChoices = ["Passenger only", "The solution", "The solution and the file"];

    /// <summary>The names of Attributes format's choices, in <see cref="Passenger.Xaml.Features.XamlAttributeStyle"/>'s
    /// order.</summary>
    /// <summary>The names of <see cref="Database.SqlKeywordCase"/>'s choices, in its order.</summary>
    public static readonly string[] SqlKeywordCases = ["Upper case", "Lower case", "As written"];

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
    private readonly TreeList pages;

    /// <summary>The tree's rows as listed: every page's row under each open one, or, while a search keeps some, those it keeps
    /// and the pages above them, open.</summary>
    private readonly TreeRows<PageRow> pageRows = [];

    /// <summary>Each page's row, by its page.</summary>
    private readonly Dictionary<string, PageRow> rowOf = Tree.ToDictionary(t => t.Page, t => new PageRow(t.Page, PathOf(t.Page).Count - 1));

    /// <summary>The pages a search keeps options of (or the Keymap page's own finds), in the tree's order; every page while
    /// there is no search.</summary>
    private string[] kept = [];

    /// <summary>The pages open as the person left them, kept while a search opens those above what it keeps.</summary>
    private HashSet<string>? openBeforeSearch;
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
    /// <param name="questions">The window's first-time questions, whose answers their rows show and change.</param>
    public SettingsDialog(AppSettings current, ActionRegistry actions, Action<AppSettings, AppSettings> apply, Mcp.AgentSetup? agents = null, SettingsPreview? preview = null,
        FolderEditorConfigs? editorConfigs = null, TrustAnswers? trust = null, FirstTimeQuestions? questions = null)
    {
        this.apply = apply;
        this.trust = trust;
        this.questions = questions;
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
        pages = new TreeList { Classes = { StyleClasses.Tree, StyleClasses.Pages }, Padding = new Thickness(PagesInset.Left, 0), ItemsSource = pageRows };
        ReusedRows.Use(pages, PageRowView);
        TreeKeys.Attach<PageRow>(pages, Toggle);
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

    /// <summary>The Live Templates page, made as it is first shown or asked for.</summary>
    public LiveTemplatesPane Templates =>
        templates ??= new LiveTemplatesPane(TemplateChange.Applied(Working.LiveTemplates), list => Change(s => s with { LiveTemplates = TemplateChange.Of(list) }));

    private LiveTemplatesPane? templates;

    /// <summary>The Postfix Completion page, made as it is first shown or asked for.</summary>
    public PostfixTemplatesPane Postfix => postfix ??= new PostfixTemplatesPane(Working, Change);

    private PostfixTemplatesPane? postfix;

    public string Query
    {
        get => search.Text ?? string.Empty;
        set => search.Text = value;
    }

    /// <summary>The pages a search keeps options of, in the tree's order; every page with options of its own while there is
    /// no search.</summary>
    public IReadOnlyList<string> Pages => [.. kept.Where(p => options.Any(o => o.Page == p) || p == KeymapPage)];

    /// <summary>The tree's rows as listed, each page's name indented two spaces a level below the top.</summary>
    public IReadOnlyList<string> Listed => [.. pageRows.Select(r => new string(' ', r.Depth * 2) + r.Page)];

    /// <summary>The page chosen; set, the pages above it open so its row shows.</summary>
    public string? Page
    {
        get => (pages.SelectedItem as PageRow)?.Page;
        set
        {
            if (value is not null && rowOf.ContainsKey(value))
            {
                foreach (string above in PathOf(value).SkipLast(1))
                {
                    rowOf[above].IsExpanded = true;
                }
                ListRows();
            }
            pages.SelectedItem = value is null ? null : rowOf.GetValueOrDefault(value);
        }
    }

    /// <summary>Opens or closes a page's row; one closed over the page chosen takes the choice.</summary>
    private void Toggle(PageRow row)
    {
        if (!row.Expands)
        {
            return;
        }
        bool hid = row.IsExpanded && Page is { } chosen && PathOf(chosen).SkipLast(1).Contains(row.Page);
        row.IsExpanded = !row.IsExpanded;
        ListRows();
        if (hid)
        {
            pages.SelectedItem = row;
        }
        pages.Retell();
    }

    /// <summary>Lists the rows: under each open page those the search keeps (every one without a search), a page opening
    /// only where it has some.</summary>
    private void ListRows()
    {
        HashSet<string> listed = [.. kept.SelectMany(PathOf)];
        IEnumerable<PageRow> Under(string? parent) => Tree.Where(t => t.Parent == parent && listed.Contains(t.Page)).Select(t => rowOf[t.Page]);
        foreach (PageRow row in rowOf.Values)
        {
            row.Expands = Under(row.Page).Any();
        }
        pageRows.Show([.. TreeRows<PageRow>.Shown(Under(null), row => Under(row.Page))]);
    }

    /// <summary>The options the chosen page shows now, by their labels.</summary>
    public IReadOnlyList<string> Shown => [.. options.Where(o => o.Page == Page && o.Kept).Select(o => o.Label)];

    /// <summary>How many options' rows are made: those of the pages shown so far that a search kept.</summary>
    public int RowsMade => options.Count(o => o.Made is not null);

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
            ApplyQuestions();
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
            option.Kept = words.All(w => option.Searched.Contains(w, StringComparison.OrdinalIgnoreCase) || option.Shown.Shown.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || option.Path.Contains(w, StringComparison.OrdinalIgnoreCase))
                && byShortcut(option);
        }
        string? was = Page;
        bool searching = words.Length > 0 || shortcutFilter is not null;
        kept = !searching ? [.. EveryPage] : [.. EveryPage.Where(p => options.Any(o => o.Page == p && o.Kept)
            || (p == KeymapPage && (shortcutFilter is not null || FindsKeymapChoice(words))))];
        // A search opens the pages above what it keeps, the person's own opening coming back once it is cleared.
        if (searching)
        {
            openBeforeSearch ??= [.. rowOf.Values.Where(r => r.IsExpanded).Select(r => r.Page)];
            foreach (string above in kept.SelectMany(p => PathOf(p).SkipLast(1)))
            {
                rowOf[above].IsExpanded = true;
            }
        }
        else if (openBeforeSearch is { } open)
        {
            openBeforeSearch = null;
            foreach (PageRow row in rowOf.Values)
            {
                row.IsExpanded = open.Contains(row.Page);
            }
        }
        // The rows kept stay the same rows, so a page still listed stays chosen, the Keymap page's top, Find Shortcut with it, kept.
        ListRows();
        nothing.IsVisible = kept.Length == 0;
        Page = was is not null && kept.Contains(was) && (!searching || Pages.Contains(was)) ? was : searching ? Pages.FirstOrDefault() : was ?? EveryPage[0];
        ShowPage();
    }

    private void ShowPage()
    {
        body.Children.Clear();
        heading.Text = Page ?? string.Empty;
        pageTop.Child = Page == KeymapPage ? keymapHeader : null;
        // Only the rows the search keeps, each made as it is first shown.
        foreach (Option option in options.Where(o => o.Page == Page && o.Kept))
        {
            body.Children.Add(option.Row);
        }
        // A page with nothing of its own to show (a group of pages, or one the search keeps only for what is under it) lists the pages under it.
        if (Page is { } page && body.Children.Count == 0 && page != KeymapPage)
        {
            foreach (string under in Tree.Where(t => t.Parent == page && kept.Contains(t.Page)).Select(t => t.Page))
            {
                body.Children.Add(PageLink(under));
            }
        }
    }

    /// <summary>A page under the one shown, as that one lists it: its name, going to it, and the first of what it holds.</summary>
    private Control PageLink(string page)
    {
        Button go = new() { Content = page, Classes = { StyleClasses.Link }, HorizontalAlignment = HorizontalAlignment.Left };
        go.Click += (_, _) => Page = page;
        string[] holds = [.. options.Where(o => o.Kept && PathOf(o.Page).Contains(page)).Select(o => o.Label).Distinct().Take(4)];
        TextBlock said = new() { Text = string.Join(", ", holds), TextWrapping = TextWrapping.Wrap, Classes = { StyleClasses.Muted } };
        return new StackPanel { Spacing = Gaps.Tight, Children = { go, said } };
    }

    /// <summary>A page's row in the tree: its chevron where pages sit under it, and its name, indented by its level.</summary>
    private Control PageRowView()
    {
        RowExpander chevron = RowExpander.Of<PageRow>(Toggle);
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        StackPanel row = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { chevron, name } };
        return row.Showing<PageRow>(page =>
        {
            row.Margin = new Thickness(page.Depth * Gaps.Indent, 0, 0, 0);
            chevron.Expands = page.Expands;
            chevron.IsOpen = page.IsExpanded;
            name.Text = page.Page;
        });
    }

    /// <summary>A page's row in the tree.</summary>
    public sealed class PageRow(string page, int depth) : Observable, ITreeRow
    {
        private bool isExpanded;
        private bool expands;

        public string Page { get; } = page;

        public int Depth { get; } = depth;

        /// <summary>Whether pages are listed under it: any at all, or, while a search keeps some, any it keeps.</summary>
        public bool Expands
        {
            get => expands;
            set => Set(ref expands, value);
        }

        public bool IsExpanded
        {
            get => isExpanded;
            set => Set(ref isExpanded, value);
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
        Add(Declared.DisplayLanguage);
        Add(Declared.WindowFontFamily);
        Add(Declared.DisplayScale);
        Add(Declared.EditorTabs);
        Add(Declared.ToolWindowLook);
        Add(Declared.ClosedToolWindows);
        Add(Declared.WideLayout);
        Add(Declared.FindResults);
        Add(Declared.PlainFolderIcons);

        Add(Declared.FollowSystemContrast);
        Add(Declared.StatePalette);
        Add(Declared.SoundCues);
        Add(Declared.SoundOnErrorLine);
        Add(Declared.SoundOnWarningLine);
        Add(Declared.SoundOnBreakpointLine);
        Add(Declared.SoundOnFoldedLine);
        Add(Declared.SoundOnChangedLine);
        Add(Declared.SoundOnBuildEnd);
        Add(Declared.SoundOnTestsEnd);
        Add(Declared.SoundOnDebuggerStop);
        Add(Declared.SoundOnTerminalFailure);
        Add(Declared.SoundOnSaveRefused);

        Add(Declared.ReopenProjectsOnStartup);
        Add(GeneralPage, "Open project in", () =>
        {
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
            return openIn;
        }, "open solution project folder new window current window this window ask confirm where frame");
        Add(Declared.ConfirmBeforeClosing);
        Add(Declared.SaveOnDeactivation);
        Add(GeneralPage, "Save when idle for", () =>
        {
            NumericUpDown seconds = Declared.IdleSeconds.Box(Working, Change);
            seconds.IsEnabled = Working.SaveWhenIdle;
            CheckBox idle = Check(Working.SaveWhenIdle, v =>
            {
                Change(s => s with { SaveWhenIdle = v });
                seconds.IsEnabled = v;
            });
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Normal, Children = { idle, NumberSetting.WithUnit(seconds, "seconds") } };
        }, "autosave files idle inactive seconds");

        AddTrust();
        Add(Declared.CheckForUpdates);

        Add(Declared.AlwaysSelectOpenedFile);
        Add(Declared.OpenFilesWithSingleClick);
        Add(Declared.OpenProjectFileOnDoubleClick);
        Add(Declared.NestFiles);

        Add(Declared.EditorFontFamily);
        Add(Declared.EditorFontSize);
        Add(Declared.EditorLineSpacing);
        Add(Declared.CaretWidth);
        Add(Declared.Ligatures);
        Add(Declared.ShowLineNumbers);
        Add(Declared.ShowNavigationBar);
        Add(Declared.RainbowBraces);
        Add(Declared.VimMode);
        Add(Declared.VimKeysToKeymap, () => Declared.VimKeysToKeymap.Box(Working, Change, stroke => KeymapDoes(actions, stroke)));
        Add(Declared.CloseTags);
        Add(Declared.TabStepsOverClosers);
        Add(Declared.CompletionParentheses);
        Add(Declared.FormatOnType);
        Add(Declared.XamlWorkspace);
        Add(Declared.SolutionWideAnalysis);
        Add(Declared.ParameterHints);
        Add(Declared.TypeHints);
        Add(Declared.Signposts);
        Add(Declared.SignpostUsages);
        Add(Declared.SignpostInheriting);
        Add(Declared.SignpostAuthors);
        Add(Declared.SignpostComplexity);
        Add(Declared.SignpostComplexityMinimum);
        Add(Declared.SignpostComplexityYellowFrom);
        Add(Declared.SignpostComplexityRedFrom);
        Add(EditorPage, "Soft-wrap these files", () =>
        {
            TextBox masks = new() { Text = Working.SoftWrapFiles, PlaceholderText = "No files", IsEnabled = Working.SoftWrap };
            ToolTip.SetTip(masks, "Use * and ? as wildcards and ; to separate patterns");
            masks.TextChanged += (_, _) => Change(s => s with { SoftWrapFiles = masks.Text ?? string.Empty });
            CheckBox wrapFiles = Check(Working.SoftWrap, v =>
            {
                Change(s => s with { SoftWrap = v });
                masks.IsEnabled = v;
            });
            DockPanel.SetDock(wrapFiles, Dock.Left);
            return new DockPanel { HorizontalSpacing = 8, MaxWidth = OptionRow.EditorWidth, Children = { wrapFiles, masks } };
        }, "soft wrap wrapping long lines width markdown text files masks");
        Add(Declared.SoftWrapIndent);
        Add(Declared.ShowWhitespaces);
        Add(Declared.WheelZoom);
        Add(Declared.ReformatOnSave);
        Add(Declared.OptimizeImportsOnSave);
        Add(Declared.CSharpFormatter);
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
        Add(Declared.SqlKeywords);

        AddPage(PostfixPage, () => Postfix, "postfix templates completion expression dot autopopup "
            + string.Join(' ', Languages.CSharp.PostfixTemplates.All.Select(t => $"{t.Name} {t.Description} {t.Example}")));
        AddPage(LiveTemplatesPage, () => Templates, "live templates c# shortcut abbreviation snippet expand tab surround with generation availability mnemonic variables macros "
            + string.Join(' ', CSharp.LiveTemplates.BuiltIn.Select(t => $"{t.Abbreviation} {t.Description}")));

        Add(Declared.OpenDiffsIn);
        Add(Declared.DiffHighlighting);
        Add(Declared.ChangesAtCheckout);

        Lazy<(CheckBox Explicitly, CheckBox Implicitly)> buildWindow = new(() =>
        {
            CheckBox implicitly = Check(Working.OpenBuildWindowOnImplicitBuild, v => Change(s => s with { OpenBuildWindowOnImplicitBuild = v }));
            implicitly.IsEnabled = Working.OpenBuildWindowOnBuild;
            CheckBox explicitly = Check(Working.OpenBuildWindowOnBuild, v =>
            {
                Change(s => s with { OpenBuildWindowOnBuild = v });
                implicitly.IsEnabled = v;
            });
            return (explicitly, implicitly);
        });
        Add(BuildPage, "Tool Window: when build is explicitly invoked, open the build progress view in a tool window", () => buildWindow.Value.Explicitly,
            "build tool window open progress view start explicitly invoked solution rebuild clean toolset presentation");
        Add(BuildPage, "Tool Window: open the build progress view even when the build is invoked implicitly (i.e. Run/Debug)", () => buildWindow.Value.Implicitly,
            "build tool window open progress view start implicitly run debug profile before launch toolset presentation");
        Add(Declared.ShowBuildResults);
        Add(Declared.ContinuousTesting);

        Add(Declared.HotReloadWhileDebugging);
        Add(Declared.HotReload);
        Add(Declared.HotReloadOnSave);
        Add(Declared.XamlHotReloadOnSave);
        Add(Declared.RestartOnRudeEdit);
        Add(Declared.WhenDebuggerStops);
        Add(Declared.ShowElapsedAtStops);
        Add(Declared.GreyCodeAhead);
        Add(Declared.AskBeforeRerun);
        Add(Declared.ConsoleBufferKb);
        Add(Declared.ConsoleFontSize);
        Add(Declared.Browser);

        Add(Declared.WebRuntime);
        Add(NodePage, "Node interpreter", () =>
        {
            TextBox node = new() { Text = Working.NodeInterpreter ?? string.Empty, PlaceholderText = Passenger.Core.Programs.Node() ?? "Not found on the PATH", MaxWidth = OptionRow.EditorWidth };
            node.TextChanged += (_, _) => Change(s => s with { NodeInterpreter = string.IsNullOrWhiteSpace(node.Text) ? null : node.Text.Trim() });
            return node;
        }, "node nodejs interpreter javascript typescript html css json language server npm path",
            "The Node.js the JavaScript, TypeScript, HTML, CSS and JSON language servers run on where they run on Node.js. Empty, "
            + "it is the first on the PATH, as the login shell finds it. Without one, those files keep their colours only.");
        AddCodeQualityTools();
        Add(Declared.DownloadJsonSchemas);
        Add(Declared.DownloadStylesheets);
        // WSL Containers is Windows' alone, so elsewhere the engine is always docker's.
        if (OperatingSystem.IsWindows())
        {
            Add(Declared.ContainerEngine);
        }
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

        Lazy<(CheckBox Discord, ComboBox Shows, Control Idle)> discordRows = new(() =>
        {
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
            return (discord, shows, new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Normal, Children = { whenIdle, NumberSetting.WithUnit(idleMinutes, "minutes") } });
        });
        const string Discord = "discord rich presence activity status playing";
        Add(DiscordPage, "Show activity in Discord", () => discordRows.Value.Discord, $"{Discord} friends share game",
            "Discord shows Playing Passenger with its icon, what is chosen below and how long the solution has been open, to "
            + "whoever can see your activity. Passenger tells the Discord app on this computer alone. Off, it tells Discord nothing.");
        Add(DiscordPage, "Show", () => discordRows.Value.Shows, $"{Discord} solution project file name details visibility privacy",
            "Passenger only: that you are using Passenger, and nothing of your work. The solution: its name too. The solution and "
            + "the file: the name of the file you are editing as well. Whoever can see your Discord activity sees these names.");
        Add(DiscordPage, "Only Passenger when idle for", () => discordRows.Value.Idle, $"{Discord} idle inactive away timeout minutes");

        Add(Declared.TraceProtocols);
        Add(Declared.LogEveryProgram);

        Add(Declared.ChordWaitSeconds);
        AddKeymap();
    }

    /// <summary>A declared setting's own row, on its page, its editor made from the declaration as the page is first shown.</summary>
    private void Add(Setting setting) => Add(setting, () => setting.Editor(Working, Change));

    /// <summary>A declared setting's own row, its editor made by the dialog round what only the dialog knows.</summary>
    private void Add(Setting setting, Control editor) => Add(setting, () => editor);

    /// <summary>A declared setting's own row, its editor made as its page is first shown.</summary>
    private void Add(Setting setting, Func<Control> editor)
    {
        SettingRow row = setting.Row ?? throw new ArgumentException("A setting with no row of its own goes in a row the dialog builds", nameof(setting));
        Add(row.Page, row.Label, () =>
        {
            Control made = editor();
            if (row.When is { } when)
            {
                // Greyed, saying why, while the setting it follows leaves it no effect.
                ToolTip.SetShowOnDisabled(made, true);
                dependents.Add((row.Label.English, made, when));
                Follow(made, when);
            }
            return made;
        }, row.Searched, row.NoteShown, over: row.Over);
    }

    /// <summary>The rows another setting greys, each with what it takes effect under.</summary>
    private readonly List<(string Label, Control Editor, SettingWhen When)> dependents = [];

    private void Follow(Control editor, SettingWhen when)
    {
        ToolButtons.Grey(editor, when.Holds(Working) ? null : when.Why);
    }

    /// <summary>Why a row is greyed as the settings stand; null where it takes effect, or is no row another greys.</summary>
    public string? WhyGreyed(string label) =>
        Made(label) && dependents.FirstOrDefault(d => d.Label == label) is { Editor: { } editor, When: { } when } && !when.Holds(Working) && !editor.IsEnabled ? when.Why : null;

    /// <summary>Makes the row of a label, if not made yet, as its page shows it; whether there is one.</summary>
    private bool Made(string label) => options.FirstOrDefault(o => o.Label == label) is { } option && option.Row is not null;

    /// <summary>What the folder's .editorconfig files set over the rows, followed while the dialog is open.</summary>
    private readonly FolderEditorConfigs? editorConfigs;

    /// <summary>Each row the folder's .editorconfig files may set, by its label, with what says so under it.</summary>
    private readonly List<(string Label, EditorConfigOver Over, TextBlock Said)> saidOver = [];

    /// <summary>Reads of what the .editorconfig files set, the last asked alone landing.</summary>
    private int saidAsked;

    /// <summary>What the folder's .editorconfig files set over a row, by its label, a line to each key as the row shows them;
    /// null where they set nothing over it.</summary>
    public string? SaidOver(string label) => Made(label) && saidOver.FirstOrDefault(s => s.Label == label).Said is { IsVisible: true } said ? said.Text : null;

    /// <summary>What the folder's .editorconfig files set as last read, which a row made since says too.</summary>
    private EditorConfigLines? saidRead;

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
        saidRead = read;
        foreach ((_, EditorConfigOver over, TextBlock said) in saidOver)
        {
            Say(read, over, said);
        }
    }

    /// <summary>What the .editorconfig files set over a row, said under it.</summary>
    private static void Say(EditorConfigLines read, EditorConfigOver over, TextBlock said)
    {
        IReadOnlyList<string> lines = read.Shortly(over);
        said.Text = string.Join('\n', lines);
        said.IsVisible = lines.Count > 0;
    }

    /// <summary>A page that is one control of its own (the live templates' list and editor), the whole of its width, made
    /// as it is first shown, found by a search for any of its words.</summary>
    private void AddPage(string page, Func<Control> content, string keywords) => options.Add(new Option(page, page, content, $"{page} {keywords}"));

    /// <summary>An option's row (<see cref="Add(string, string, Func{Control}, string, string?, string?, EditorConfigOver?, Action{OptionRow}?)"/>)
    /// of an editor already made.</summary>
    private Option Add(string page, Words label, Control editor, string keywords, Words? note = null, string? action = null, EditorConfigOver? over = null) =>
        Add(page, label, () => editor, keywords, note, action, over);

    /// <summary>An option's row: its label, what edits it, and a note under the editor when it needs saying what it
    /// does; a keymap row names its action, and one the folder's .editorconfig files may set the keys it is said over by.
    /// Found by a search from its words alone, its label's in either language, it is made, then given to
    /// <paramref name="made"/>, as its page is first shown.</summary>
    private Option Add(string page, Words label, Func<Control> editor, string keywords, Words? note = null, string? action = null, EditorConfigOver? over = null,
        Action<OptionRow>? made = null)
    {
        // A row an .editorconfig says over is found by the keys that do, as a person reads them in the file.
        string keys = over is null ? string.Empty : $" editorconfig {string.Join(' ', over.Keys.Concat(over.Aliases))}";
        Option option = new(page, label, () =>
        {
            OptionRow row = Row(label, editor(), note, over);
            made?.Invoke(row);
            return row;
        }, $"{page} {label.English} {keywords}{keys}", action);
        options.Add(option);
        return option;
    }

    /// <summary>An option's row as its page first shows it: its label, its editor, its note, and what the folder's
    /// .editorconfig files set over it, as last read.</summary>
    private OptionRow Row(Words label, Control editor, Words? note, EditorConfigOver? over)
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
            [ShownWords.TextProperty] = label,
            Padding = new Thickness(0, 0, Gaps.Group, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLabeledBy(editor, name);
        // The one field of an editor of several parts (a check box before it, its problem under it) is heard by the row's words.
        if (editor is not TextBox && editor.GetLogicalDescendants().OfType<TextBox>().ToList() is [{ } field] && AutomationProperties.GetName(field) is null or "")
        {
            AutomationProperties.SetLabeledBy(field, name);
        }
        TextBlock? said = note is null ? null
            : new TextBlock { [ShownWords.TextProperty] = note, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left, Classes = { StyleClasses.Muted } };
        TextBlock? setOver = null;
        if (over is not null)
        {
            setOver = new TextBlock { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false, Classes = { StyleClasses.SaidOver } };
            saidOver.Add((label.English, over, setOver));
            if (saidRead is { } read)
            {
                Say(read, over, setOver);
            }
        }
        return new OptionRow(name, editor, said, firstLine, setOver);
    }

    private static CheckBox Check(bool value, Action<bool> changed)
    {
        CheckBox box = new() { IsChecked = value };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    /// <summary>An option as a search finds it, by its page, label and words, its row made as its page is first shown
    /// (<see cref="Row"/>); whether the search keeps it.</summary>
    private sealed class Option(string page, Words label, Func<Control> make, string words, string? action = null)
    {
        public string Page { get; } = page;

        /// <summary>The names of its page and those above it, which a search finds it by too.</summary>
        public string Path { get; } = string.Join(' ', PathOf(page));

        /// <summary>Its label's English, by which code and agents name it.</summary>
        public string Label { get; } = label.English;

        /// <summary>Its label as shown, which a search finds it by too.</summary>
        public Words Shown { get; } = label;

        public string Searched { get; set; } = words;

        public string? Action { get; } = action;

        public bool Kept { get; set; } = true;

        /// <summary>The row, once made.</summary>
        public Control? Made { get; private set; }

        public Control Row => Made ??= make();
    }
}
