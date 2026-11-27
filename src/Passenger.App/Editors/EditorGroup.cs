using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App.State;
using Passenger.App.Tables;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>A tab of an editor group: a file, whether it is the one its group shows, and whether it is pinned.</summary>
public sealed class EditorTab(OpenDocument document) : Observable
{
    private bool isActive;
    private bool isPinned;

    public OpenDocument Document { get; } = document;

    public bool IsActive
    {
        get => isActive;
        set => Set(ref isActive, value);
    }

    /// <summary>Pin Tab: it stands left of the unpinned tabs, and Close Others, Close All and closing by side leave it
    /// open.</summary>
    public bool IsPinned
    {
        get => isPinned;
        set => Set(ref isPinned, value);
    }
}

/// <summary>One editor of a split: its tabs (the pinned ones first), its navigation bar, its find bar and its editor,
/// which shows the active tab's file. A file may be open in two groups at once (Split Right on it), each with its own
/// carets and scroll over the one text.</summary>
public sealed class EditorGroup : DockPanel, IOwnKeys
{
    private readonly EditorArea area;
    private readonly ObservableCollection<EditorTab> tabs = [];

    /// <summary>The tabs this group has shown, the latest first, each once and only while it is on the strip: what the
    /// group goes back to as its tab shown closes.</summary>
    private readonly List<EditorTab> shownOrder = [];
    private readonly Dictionary<OpenDocument, (SelectionSet Selections, Vector Scroll)> views = [];
    private readonly Border tabBar;

    /// <summary>A Markdown file's layouts on the tab strip, shown while the group shows one.</summary>
    private readonly StackPanel markdownModes;
    private readonly ToggleButton markdownEditor;
    private readonly ToggleButton markdownSplit;
    private readonly ToggleButton markdownOnly;
    private readonly ToggleButton markdownInline;

    /// <summary>Editor and Preview's mark, turned a quarter while the preview stands under the editor.</summary>
    private readonly PathIcon splitMark = new();

    private const string EditorOnly = "Editor Only";
    private const string EditorAndPreview = "Editor and Preview";
    private const string PreviewOnly = "Preview Only";
    private const string InlineEditing = "Inline Markdown Editing";

    private readonly ItemsControl strip;
    private readonly ScrollViewer stripView;

    /// <summary>At the strip's end, shown while tabs are out of its sight: the list of them.</summary>
    private readonly Button hiddenTabs;

    /// <summary>The editor's place under the theme's picture and a pane of its file's own beside it, or under it in a narrow
    /// group: the group's own child, before which the bars over the body go. The picture is the editor's alone, so a preview
    /// beside it shows its file on the preview's own ground.</summary>
    private readonly ToolWindows.PaneSplit body;

    /// <summary>The editor and what shows over it in its stead: a diff, an asset, a table, a Godot project's page.</summary>
    private readonly Panel editorPlace;
    private readonly Banner banner = new();
    private readonly Button bannerLink;

    /// <summary>The banner's way to the merge window, while the file shown is in conflict.</summary>
    private readonly Button mergeLink;
    private EditorTab? active;
    private EditorTabsPlace tabsPlace;

    /// <summary>The table over the editor for a CSV or workbook, made as the group first shows one.</summary>
    private TableViewer? tables;

    /// <summary>The Godot project page over a project.godot's editor, made as the group first shows one.</summary>
    private Godot.GodotPageViewer? godotPage;

    /// <summary>This group's view of the active asset (its bitmap or face belongs to the document, not this control), of
    /// release notes the app carries, rendered, or of a diff's tab, in the editor's place.</summary>
    private Control? preview;

    public EditorGroup(EditorArea area)
    {
        this.area = area;
        Editor = new CodeEditor { IsVisible = false };
        Gutter = new EditorGutter(Editor);
        Find = new FindBar { [DockProperty] = Dock.Top };
        strip = new ItemsControl
        {
            ItemsSource = tabs,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal }),
        };
        ReusedRows.Use(strip, Tab);
        PreviewToggle = ToolButtons.Toggle(IconKeys.Preview, "Preview");
        PreviewToggle.Classes.Add(StyleClasses.TabStripTool);
        PreviewToggle.IsVisible = false;
        PreviewToggle[DockProperty] = Dock.Right;
        PreviewToggle.Click += (_, _) =>
        {
            area.Ask(this, PreviewAction ?? AppActions.XamlPreview);
            // It says what is in force once its action ran, or was refused.
            FollowPreviewToggle();
        };
        splitMark.Bind(PathIcon.DataProperty, splitMark.GetResourceObservable(IconKeys.Diff));
        markdownEditor = MarkdownButton(ToolButtons.Toggle(IconKeys.DocumentText, EditorOnly), PaneView.Editor);
        markdownSplit = MarkdownButton(ToolButtons.Give(new ToggleButton(), splitMark, EditorAndPreview), PaneView.Split);
        markdownOnly = MarkdownButton(ToolButtons.Toggle(IconKeys.Preview, PreviewOnly), PaneView.Preview);
        markdownInline = MarkdownButton(ToolButtons.Toggle(IconKeys.Edit, InlineEditing), PaneView.Inline);
        markdownInline.Name = "MarkdownInlineEditing";
        markdownModes = new StackPanel
        {
            Orientation = Orientation.Horizontal, IsVisible = false, [DockProperty] = Dock.Right,
            Children = { markdownEditor, markdownSplit, markdownOnly, markdownInline },
        };
        stripView = TabStrips.ScrollingAcross(strip);
        // A narrower strip may leave the active tab out of sight.
        stripView.SizeChanged += (_, _) => RevealActive();
        hiddenTabs = ToolButtons.Of(IconKeys.ChevronDown, "Hidden Tabs", () => area.HiddenTabsAsked(this));
        hiddenTabs.Classes.Add(StyleClasses.TabStripTool);
        hiddenTabs.Name = "HiddenTabs";
        hiddenTabs.IsVisible = false;
        hiddenTabs[DockProperty] = Dock.Right;
        // Tabs come, go, widen or scroll: the list's button shows while some are out of sight.
        stripView.ScrollChanged += (_, _) => hiddenTabs.IsVisible = HiddenTabs.Count > 0;
        tabBar = new Border
        {
            Classes = { StyleClasses.TabStrip },
            [DockProperty] = Dock.Top,
            Child = new Panel
            {
                Children =
                {
                    new DockPanel { Children = { hiddenTabs, markdownModes, PreviewToggle, stripView } },
                    new ThemeBackdrop(BackgroundPlaces.Frame),
                },
            },
        };
        bannerLink = banner.Link(null, () => active?.Document.NoticeAction?.Run());
        bannerLink.Name = "BannerLink";
        bannerLink.IsVisible = false;
        mergeLink = banner.Link(Merge, () =>
        {
            if (active?.Document is { InConflict: true } conflicted)
            {
                area.MergeAsked(conflicted);
            }
        });
        mergeLink.Name = "MergeLink";
        mergeLink.IsVisible = false;
        Navigation = new NavigationBar(area, this);
        Children.Add(tabBar);
        Children.Add(Navigation.Bar);
        Children.Add(banner);
        Children.Add(Find);
        editorPlace = new Panel { Children = { Editor } };
        // The picture over the editor's place, whatever it holds, in a panel of their own; never over the pane beside it.
        body = new ToolWindows.PaneSplit(new Panel { Children = { editorPlace, new ThemeBackdrop(BackgroundPlaces.Editor) } }, null, "*,1,*");
        body.DirectionChanged += (_, _) =>
        {
            splitMark.RenderTransform = body.IsStacked ? new RotateTransform(90) : null;
            FollowSplitWords();
        };
        Children.Add(body);
        // The group the person works in is the area's active one; while a table lies over the editor, the table takes its focus.
        Editor.GotFocus += (_, _) =>
        {
            area.SetActiveGroup(this);
            if (tables is { ShowsTable: true, View.IsVisible: true })
            {
                tables.View.Focus();
            }
            else if (godotPage is { ShowsPage: true })
            {
                godotPage.Body.Focus();
            }
        };
        Editor.WentTo += (_, offset) =>
        {
            tables?.Reveal(offset);
            godotPage?.Reveal();
            if (previewAlone)
            {
                Markdown?.Reveal(offset);
            }
        };
        Editor.SelectionChanged += (_, _) =>
        {
            if (active is not null && Editor.Document == active.Document.Text)
            {
                active.Document.Selections = Editor.Selections;
            }
        };
        AddHandler(PointerPressedEvent, (_, _) => area.SetActiveGroup(this), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) => markdownTableEditing?.KeyDown(e), RoutingStrategies.Tunnel);
    }

    bool IOwnKeys.TakesKey(KeyEventArgs e) => markdownTableEditing?.TakesKey(e) == true;

    /// <summary>A layout's button on the tab strip of a file with a pane of its own (a Markdown file, a form's), asking for its
    /// kind's action as its keys do, pressed while its layout is in force.</summary>
    private ToggleButton MarkdownButton(ToggleButton button, PaneView view)
    {
        button.Classes.Add(StyleClasses.TabStripTool);
        button.Click += (_, _) =>
        {
            area.Ask(this, PaneActions(view).Action);
            FollowPreviewToggle();
        };
        return button;
    }

    /// <summary>Whether the file shown is a form's, whose layouts are its design's rather than a Markdown preview's.</summary>
    private bool ShowsForm => active?.Document is { } shown && InEditor(shown) && area.Designs(shown);

    /// <summary>A layout's action and title for the kind of file shown: a form's code and design, else a Markdown file's.</summary>
    private (string Action, string Title) PaneActions(PaneView view) => (ShowsForm, view) switch
    {
        (true, PaneView.Editor) => (AppActions.FormCodeOnly, CodeOnly),
        (true, PaneView.Split) => (AppActions.FormCodeAndDesign, CodeAndDesign),
        (true, _) => (AppActions.FormDesignOnly, DesignOnly),
        (false, PaneView.Editor) => (AppActions.MarkdownEditorOnly, EditorOnly),
        (false, PaneView.Split) => (AppActions.MarkdownEditorAndPreview, EditorAndPreview),
        (false, PaneView.Inline) => (AppActions.MarkdownInlineEditing, InlineEditing),
        _ => (AppActions.MarkdownPreviewOnly, PreviewOnly),
    };

    private const string CodeOnly = "Code Only";
    private const string CodeAndDesign = "Code and Design";
    private const string DesignOnly = "Design Only";

    /// <summary>The layout buttons' words: what a press does, which for both shown is to turn them.</summary>
    private void FollowSplitWords()
    {
        bool form = ShowsForm;
        foreach ((ToggleButton button, PaneView view) in (ReadOnlySpan<(ToggleButton, PaneView)>)[
            (markdownEditor, PaneView.Editor), (markdownOnly, PaneView.Preview), (markdownInline, PaneView.Inline)])
        {
            (string action, string title) = PaneActions(view);
            ActionTips.Set(button, action, title);
        }
        PaneView shown = form ? area.FormLayout.View : area.MarkdownLayout.View;
        (string split, string both) = PaneActions(PaneView.Split);
        ActionTips.Set(markdownSplit, split, shown != PaneView.Split ? both
            : form ? SideUnder ? "Design Beside the Code" : "Design Under the Code"
            : SideUnder ? "Preview Beside the Editor" : "Preview Under the Editor");
    }

    /// <summary>The way Editor and Preview pressed again stands the preview: under the editor (true) or beside it (false),
    /// null as the group's shape stands it.</summary>
    internal bool? TurnedSplit => body.Turned;

    /// <summary>Whether this is the group the person works in; the others mark their file quieter.</summary>
    public bool IsCurrent
    {
        get => isCurrent;
        set
        {
            isCurrent = value;
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool isCurrent = true;

    private event EventHandler? CurrentChanged;

    public CodeEditor Editor { get; }

    /// <summary>The bar over the editor naming where its caret is in the file's outline.</summary>
    public NavigationBar Navigation { get; }

    /// <summary>The columns of the editor's gutter, fed from the file it shows.</summary>
    public EditorGutter Gutter { get; }

    /// <summary>A pane for the file the editor shows (a XAML file's preview, a Markdown file's), half the group at first
    /// and resized by the hairline between them: beside the editor, or under it where the group is narrow and tall as
    /// a tool window's two panes stand (<see cref="ToolWindows.PaneSplit"/>), so each keeps a width to read; null for none.
    /// Another file's pane takes the place the last one was given.</summary>
    public Control? Side
    {
        get => body.Second;
        set => body.Second = value;
    }

    /// <summary>Whether the pane of the file's own stands under the editor rather than beside it.</summary>
    public bool SideUnder => body.IsStacked;

    public FindBar Find { get; }

    /// <summary>The tab strip's preview toggle for SVG, HTML and XAML files.</summary>
    public ToggleButton PreviewToggle { get; }

    /// <summary>The group's own preview of its Markdown file, made as it first shows one.</summary>
    private MarkdownPreview? markdown;
    private MarkdownTableEditing? markdownTableEditing;

    /// <summary>Whether the Markdown file's preview stands alone in its editor's place.</summary>
    private bool previewAlone;

    /// <summary>The group's own picture of its SVG file, made as it first shows one.</summary>
    private SvgPreview? svg;

    /// <summary>The group's own results of a SQL console, made as it first shows one.</summary>
    private Database.SqlConsoleView? consoleView;

    /// <summary>The group's own page of its HTML file, made as it first shows one.</summary>
    private HtmlPreview? html;

    /// <summary>The page beside the editor while it shows one of an HTML file, else null.</summary>
    public HtmlPreview? Html => Side as HtmlPreview;

    /// <summary>The preview beside the editor while it shows one of a Markdown file, else null.</summary>
    public MarkdownPreview? Markdown => Side as MarkdownPreview;

    /// <summary>The picture beside the editor while it shows one of an SVG file, else null.</summary>
    public SvgPreview? Svg => Side as SvgPreview;

    /// <summary>A Markdown, SVG or HTML file shown in the editor has its preview beside it while the area shows that kind's
    /// previews, and its toggle on the strip either way, a Windows Forms form's file its design as the form layout says, and a
    /// SQL console its results under it; any other file none.</summary>
    internal void FollowSidePreview()
    {
        OpenDocument? document = active?.Document is { } shown && InEditor(shown) ? shown : null;
        FollowPreviewToggle();
        bool isMarkdown = document?.Language == Languages.Language.Markdown;
        bool isForm = document is not null && area.Designs(document);
        PaneLayout layout = isForm ? area.FormLayout : area.MarkdownLayout;
        bool previewOnly = (isMarkdown || isForm) && layout.View == PaneView.Preview;
        bool inline = isMarkdown && layout.View == PaneView.Inline;
        bool moveKeys = previewOnly ? Editor.IsKeyboardFocusWithin
            : (Markdown?.IsKeyboardFocusWithin == true || Side is WinForms.FormDesignPane { IsKeyboardFocusWithin: true })
                && (layout.View == PaneView.Editor || inline);
        Editor.PresentsMarkdown = inline;
        if (inline && markdownTableEditing is null)
        {
            markdownTableEditing = new MarkdownTableEditing(Editor);
            Children.Insert(Children.IndexOf(body), markdownTableEditing.Toolbar);
        }
        markdownTableEditing?.Show(inline ? document?.Text : null);
        Database.SqlConsole? console = document is not null && Database.SqlConsole.Speaks(document.Language) ? area.ConsoleOf?.Invoke(document) : null;
        body.SetPresentation(isMarkdown || isForm ? layout.Stacked : console is not null ? true : null, previewOnly);
        if (isMarkdown && layout.View is PaneView.Split or PaneView.Preview)
        {
            markdown ??= new MarkdownPreview(Editor, area);
            Side = markdown;
            markdown.Show(document);
            if (previewAlone && !previewOnly)
            {
                markdown.EditorShown();
            }
        }
        else
        {
            if (Side is MarkdownPreview)
            {
                Side = null;
            }
            markdown?.Show(null);
            if (!area.ShowsMarkdownPreview)
            {
                markdown?.Forget();
            }
        }
        if (isForm && layout.View != PaneView.Editor && area.DesignOf?.Invoke(document!) is { } design)
        {
            Side = design;
        }
        else if (Side is WinForms.FormDesignPane)
        {
            Side = null;
        }
        previewAlone = previewOnly;
        if (moveKeys)
        {
            FocusShown();
        }
        if (document is not null && EditorArea.IsSvg(document) && area.ShowsSvgPreview)
        {
            svg ??= new SvgPreview();
            Side = svg;
            svg.Show(document);
        }
        else
        {
            if (Side is SvgPreview)
            {
                Side = null;
            }
            svg?.Show(null);
        }
        if (document?.Language == Languages.Language.Html && area.ShowsHtmlPreview)
        {
            html ??= new HtmlPreview(area);
            Side = html;
            html.Show(document);
        }
        else
        {
            if (Side is HtmlPreview)
            {
                Side = null;
            }
            html?.Show(null);
        }
        if (console is not null && area.MakeConsoleView is { } make)
        {
            consoleView ??= make();
            Side = consoleView;
            consoleView.Show(console);
        }
        else
        {
            if (Side is Database.SqlConsoleView)
            {
                Side = null;
            }
            consoleView?.Show(null);
        }
    }

    /// <summary>The results of the console the group shows, under its SQL, while it shows one.</summary>
    public Database.SqlConsoleView? Console => Side as Database.SqlConsoleView;

    /// <summary>The action the strip's preview toggle runs for the file shown, null while it shows none.</summary>
    public string? PreviewAction { get; private set; }

    /// <summary>The strip's preview toggle shows for an SVG, an HTML file or a XAML file the previewer draws, pressed while
    /// that kind's preview is on, its tip naming the action it runs; a Markdown file's layouts show for one, the layout in
    /// force pressed.</summary>
    internal void FollowPreviewToggle()
    {
        OpenDocument? document = active is { } shown && InEditor(shown.Document) ? shown.Document : null;
        bool form = document is not null && area.Designs(document);
        PaneView view = form ? area.FormLayout.View : area.MarkdownLayout.View;
        markdownModes.IsVisible = form || document?.Language == Languages.Language.Markdown;
        markdownEditor.IsChecked = view == PaneView.Editor;
        markdownSplit.IsChecked = view == PaneView.Split;
        markdownOnly.IsChecked = view == PaneView.Preview;
        markdownInline.IsVisible = !form && markdownModes.IsVisible;
        markdownInline.IsChecked = view == PaneView.Inline;
        FollowSplitWords();
        (string Action, string Title, bool On)? toggle = document is null ? null
            : EditorArea.IsSvg(document) ? (AppActions.SvgPreview, "SVG Preview", area.ShowsSvgPreview)
            : document.Language == Languages.Language.Html ? (AppActions.HtmlPreview, "HTML Preview", area.ShowsHtmlPreview)
            : area.HasXamlPreview(document) ? (AppActions.XamlPreview, "XAML Preview", area.ShowsXamlPreview)
            : null;
        PreviewAction = toggle?.Action;
        PreviewToggle.IsVisible = toggle is not null;
        PreviewToggle.IsChecked = toggle?.On == true;
        if (toggle is { } shownToggle)
        {
            ActionTips.Set(PreviewToggle, shownToggle.Action, shownToggle.Title);
        }
    }

    public IReadOnlyList<EditorTab> Tabs => tabs;
    public OpenDocument? Active => active?.Document;
    public bool Has(OpenDocument document) => tabs.Any(t => t.Document == document);

    /// <summary>The tabs out of the strip's sight, wholly or in part, in its order.</summary>
    public IReadOnlyList<EditorTab> HiddenTabs
    {
        get
        {
            bool down = tabsPlace != EditorTabsPlace.Top;
            double from = down ? stripView.Offset.Y : stripView.Offset.X;
            double to = from + (down ? stripView.Viewport.Height : stripView.Viewport.Width);
            return [.. tabs.Where(tab => strip.ContainerFromItem(tab) is not Control shown
                || (down ? shown.Bounds.Y : shown.Bounds.X) < from - 0.5 || (down ? shown.Bounds.Bottom : shown.Bounds.Right) > to + 0.5)];
        }
    }

    /// <summary>Where its tabs stand: a row across its top that scrolls, or a column down its left or right side, each tab
    /// as wide as the column.</summary>
    public EditorTabsPlace TabsPlace
    {
        get => tabsPlace;
        set
        {
            if (value == tabsPlace)
            {
                return;
            }
            tabsPlace = value;
            bool down = value != EditorTabsPlace.Top;
            tabBar[DockProperty] = value switch { EditorTabsPlace.Left => Dock.Left, EditorTabsPlace.Right => Dock.Right, _ => Dock.Top };
            tabBar.Classes.Set(StyleClasses.SideTabs, down);
            strip.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = down ? Orientation.Vertical : Orientation.Horizontal });
            stripView.HorizontalScrollBarVisibility = down ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
            stripView.VerticalScrollBarVisibility = down ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            hiddenTabs[DockProperty] = down ? Dock.Bottom : Dock.Right;
            PreviewToggle[DockProperty] = down ? Dock.Top : Dock.Right;
            markdownModes[DockProperty] = down ? Dock.Top : Dock.Right;
            tabBar.Styles.Clear();
            if (down)
            {
                tabBar.Styles.Add(SideShadows(value));
            }
            // Every tab made again for the new direction.
            ReusedRows.Use(strip, Tab);
            RevealActive();
        }
    }

    /// <summary>The most of a group's width its tabs down a side take, so a narrow group (a small window's, a split's) keeps
    /// the rest for the code, the tabs' names ending sooner in their ellipsis.</summary>
    private const double SideTabsShare = 1.0 / 3;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Set before the column is measured, so it is laid out once at its width.
        double most = tabsPlace == EditorTabsPlace.Top ? double.PositiveInfinity : availableSize.Width * SideTabsShare;
        if (!tabBar.MaxWidth.Equals(most))
        {
            tabBar.MaxWidth = most;
        }
        return base.MeasureOverride(availableSize);
    }

    /// <summary>The theme's open tab down a side: a line its shadow draws inside the tab along its top drawn along the
    /// side toward the editor instead, so the tab still reads as joined to it; a shadow cast outside it stays as it is.</summary>
    private Styles SideShadows(EditorTabsPlace place)
    {
        Style active = new(x => x.OfType<Border>().Class(StyleClasses.Tab).Class(StyleClasses.Active));
        active.Setters.Add(new Setter(Border.BoxShadowProperty, tabBar.GetResourceObservable(TokenKeys.TabShadow, shadow => shadow is BoxShadows shadows ? Turned(shadows) : shadow).ToBinding()));
        // A split the person is not working in draws its open tab plainly, as across the top.
        Style elsewhere = new(x => x.OfType<Border>().Class(StyleClasses.Tab).Class(StyleClasses.Active).Class(StyleClasses.Elsewhere));
        elsewhere.Setters.Add(new Setter(Border.BoxShadowProperty, new BoxShadows(default)));
        return [active, elsewhere];

        BoxShadows Turned(BoxShadows shadows)
        {
            BoxShadow[] each = [.. Enumerable.Range(0, shadows.Count).Select(i => shadows[i] is { IsInset: true } inset
                ? inset with { OffsetX = place == EditorTabsPlace.Left ? -inset.OffsetY : inset.OffsetY, OffsetY = place == EditorTabsPlace.Left ? inset.OffsetX : -inset.OffsetX }
                : shadows[i])];
            return each.Length == 0 ? shadows : new BoxShadows(each[0], each[1..]);
        }
    }

    /// <summary>The button at the strip's end that lists the tabs out of its sight, shown while there are some.</summary>
    public Button HiddenTabsButton => hiddenTabs;

    /// <summary>A tab gone to from the list of those hidden, as a click on it goes: this group the one worked in, its file
    /// shown and scrolled into the strip's sight, with the keys.</summary>
    public void GoTo(OpenDocument document)
    {
        area.SetActiveGroup(this);
        Show(document);
        area.Activated(this);
        FocusShown();
    }

    /// <summary>The strip of tabs, where a dragged tab drops to go into this group at a place.</summary>
    public Control TabStrip => tabBar;

    /// <summary>Adds a file's tab after the active one (or at an index), without showing it: among the pinned tabs if it
    /// is pinned, else after them.</summary>
    public void Add(OpenDocument document, int? index = null, bool pinned = false)
    {
        if (Has(document))
        {
            return;
        }
        int at = index ?? (active is null ? tabs.Count : tabs.IndexOf(active) + 1);
        int fence = tabs.Count(t => t.IsPinned);
        tabs.Insert(pinned ? Math.Clamp(at, 0, fence) : Math.Clamp(at, fence, tabs.Count), new EditorTab(document) { IsPinned = pinned });
        document.Text.Changed += OnTabTextChanged;
    }

    public bool IsPinned(OpenDocument document) => tabs.FirstOrDefault(t => t.Document == document)?.IsPinned == true;

    /// <summary>Pins a tab, which goes to the end of the pinned ones, or unpins it, which goes to just after them.</summary>
    public void Pin(OpenDocument document, bool pinned)
    {
        if (tabs.FirstOrDefault(t => t.Document == document) is not { } tab || tab.IsPinned == pinned)
        {
            return;
        }
        tab.IsPinned = pinned;
        tabs.Move(tabs.IndexOf(tab), tabs.Count(t => t.IsPinned && t != tab));
        // A pinned diff is one the person keeps, so it no longer follows the Commit window's choice.
        if (pinned)
        {
            document.Diff?.Keep();
        }
    }

    /// <summary>A hidden tab's kept carets follow what changes its text meanwhile (a reload, a rename), as the shown one's do;
    /// release notes shown are drawn again from their new text (a build that carries others).</summary>
    private void OnTabTextChanged(object? sender, DocumentChangedEventArgs e)
    {
        EditorTab? tab = null;
        for (int i = 0; i < tabs.Count && tab is null; i++)
        {
            tab = tabs[i].Document.Text == sender ? tabs[i] : null;
        }
        if (tab is null)
        {
            return;
        }
        if (tab == active && tab.Document.IsReleaseNotes && preview is not null)
        {
            ClearPreview();
            preview = PreviewOf(tab.Document);
            editorPlace.Children.Add(preview);
        }
        else if (tab != active && views.TryGetValue(tab.Document, out (SelectionSet Selections, Vector Scroll) view))
        {
            views[tab.Document] = (TextEditing.MapThrough(view.Selections, e.Edits).Clamp(e.After.Length), view.Scroll);
        }
    }

    /// <summary>What shows in the editor's place for a tab that is no text to edit: a diff, an asset's preview, or release
    /// notes rendered.</summary>
    private Control PreviewOf(OpenDocument document) =>
        document.DatabaseTab is { } database ? database.Make() : document.Tool is { } tool ? tool.Make() : document.Diff is { } diff ? diff.Make() : document.Asset is { } asset ? new AssetPreviewControl(asset) : new ReleaseNotesView(document, area.Browse);

    /// <summary>The diff this group shows while it shows a diff's tab, else null.</summary>
    public Vcs.DiffView? Diff => preview as Vcs.DiffView;

    /// <summary>The active tab scrolled into the strip's sight once it is laid out, however many tabs crowd the strip.</summary>
    private void RevealActive() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (active is not null && strip.ContainerFromItem(active) is { } tab)
        {
            tab.BringIntoView();
        }
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>The width Vim's gq wraps a file at where textwidth is not set: its .editorconfig's max_line_length, 0 for none.</summary>
    private static int LineLengthOf(OpenDocument document) => EditorConfig.Number(document.Conventions["max_line_length"]) ?? 0;

    /// <summary>The editor refusing changes while the file shown is read-only, Vim mode saying why in Refusals' words.</summary>
    private void FollowReadOnly(OpenDocument document)
    {
        Editor.IsReadOnly = document.IsReadOnly;
        Editor.ReadOnlyWhy = document.ReadOnlyWhy;
        markdownTableEditing?.FollowReadOnly();
    }

    /// <summary>Whether the editor shows the file, rather than a view of its own in the editor's place.</summary>
    private static bool InEditor(OpenDocument document) => document.Asset is null && !document.IsReleaseNotes && document.Diff is null && document.Tool is null && document.DatabaseTab is null;

    /// <summary>Gives the keys to what shows the file: a diff's side that takes them, its preview or rendered notes, which its keys
    /// move through, else the editor; given them as by Tab (<paramref name="method"/>), it draws the ring round it. False
    /// where it could not take them.</summary>
    public bool FocusShown(NavigationMethod method = NavigationMethod.Unspecified) => preview switch
    {
        not null when active?.Document.DatabaseTable is not null && preview.GetVisualDescendants().OfType<CellGrid>().FirstOrDefault() is { } cells => cells.Focus(method),
        Vcs.DiffView diff => diff.Keyed.Focus(method),
        ToolTabView tool => tool.Window.TakeKeys(method),
        not null => preview.Focus(method),
        _ when Markdown is { } page && area.MarkdownLayout.View == PaneView.Preview => page.Focus(method),
        _ when Side is WinForms.FormDesignPane design && area.FormLayout.View == PaneView.Preview => design.Focus(method),
        _ => Editor.Focus(method),
    };

    /// <summary>Shows a file's text, its asset preview or rendered release notes, or a diff, keeping the carets and scroll of the
    /// text it replaces. A preview detaches the hidden text editor so editing commands cannot change the previous file; a view
    /// in the editor's place that had the keys (a diff's side, a preview) gives them to what shows the file now.</summary>
    public void Show(OpenDocument document)
    {
        Add(document);
        if (active?.Document == document && (Editor.IsVisible || preview is not null))
        {
            return;
        }
        bool keys = preview?.IsKeyboardFocusWithin == true;
        MarkdownPreview? page = Markdown is { IsKeyboardFocusWithin: true } held ? held : null;
        ShowIn(document);
        // A Markdown preview that had the keys keeps them while it stays, showing the next file.
        if (keys || page is { IsKeyboardFocusWithin: false })
        {
            FocusShown();
        }
    }

    private void ShowIn(OpenDocument document)
    {
        Keep();
        if (active is not null)
        {
            active.IsActive = false;
            active.Document.PropertyChanged -= OnDocumentChanged;
        }
        active = tabs.First(t => t.Document == document);
        active.IsActive = true;
        shownOrder.Remove(active);
        shownOrder.Insert(0, active);
        RevealActive();
        document.PropertyChanged += OnDocumentChanged;
        ClearPreview();
        if (!InEditor(document))
        {
            Find.Detach();
            Editor.Detach();
            Gutter.Clear();
            Editor.IsReadOnly = true;
            Editor.IsVisible = false;
            Side = null;
            tables?.Hide();
            godotPage?.Hide();
            preview = PreviewOf(document);
            editorPlace.Children.Add(preview);
            ShowBanner(document);
            tabBar.IsVisible = true;
            Navigation.Show(null);
            FollowSidePreview();
            return;
        }
        (SelectionSet selections, Vector scroll) = views.TryGetValue(document, out (SelectionSet Selections, Vector Scroll) view) ? view : (document.Selections, document.Scroll);
        ThemeLanguages.Follow(Editor, document.Language.Id);
        Editor.LineCommentPrefix = document.Language.LineComment;
        Editor.BlockComment = document.Language.BlockComment;
        Editor.Markup = document.Language.Markup;
        Editor.Indent = document.Indentation.Options;
        Editor.LineLength = LineLengthOf(document);
        Editor.ReindentsPaste = document.Language.ReindentsPaste;
        Editor.ShowWhitespaces = document.ShowWhitespaces;
        // Its decorations are the file's, which every editor showing it reads.
        Editor.Show(document.Text, selections, scroll, document.SoftWrap, document.Decorations);
        Editor.Highlighter = document.Highlighter;
        AutomationProperties.SetName(Editor, document.Title);
        Gutter.Show(document);
        FollowReadOnly(document);
        ShowBanner(document);
        Editor.IsVisible = true;
        tabBar.IsVisible = true;
        Navigation.Show(document);
        ShowTable(document);
        ShowGodotPage(document);
        AttachFind();
        FollowSidePreview();
    }

    /// <summary>The find bar on the file shown: its text, or its table while one lies over it; a read-only file's replace row
    /// greyed, saying why.</summary>
    private void AttachFind() =>
        Find.Attach(Editor, Table, Active?.ReadOnlyWhy);

    /// <summary>The table of the file shown, while it is a CSV, a workbook or a resource, null otherwise.</summary>
    public CellGrid? Table => tables is { ShowsTable: true } ? tables.View : null;

    /// <summary>The bar and grid of a table file over the editor, or none for any other file.</summary>
    private void ShowTable(OpenDocument document)
    {
        if (TableFile.For(document) is not { } file)
        {
            tables?.Hide();
            return;
        }
        if (tables is null)
        {
            tables = new TableViewer();
            Children.Insert(Children.IndexOf(body), tables.Bar);
            // Over the editor in its place, so a XAML-like pane beside it is never covered.
            editorPlace.Children.Add(tables.Body);
            tables.Switched += (_, _) =>
            {
                AttachFind();
                if (tables.ShowsTable)
                {
                    tables.View.Focus();
                }
                else
                {
                    Editor.Focus();
                }
            };
            tables.View.GotFocus += (_, _) => area.SetActiveGroup(this);
            tables.Refused += (_, why) => area.Refuse(why);
        }
        tables.Show(file);
    }

    /// <summary>The page of a Godot project over its project.godot's editor (its bar whichever shows), or none for any other file.</summary>
    private void ShowGodotPage(OpenDocument document)
    {
        if (area.GodotPages?.For(document) is not { } file)
        {
            godotPage?.Hide();
            return;
        }
        if (godotPage is null)
        {
            godotPage = new Godot.GodotPageViewer();
            Children.Insert(Children.IndexOf(body), godotPage.Bar);
            editorPlace.Children.Add(godotPage.Body);
            godotPage.Switched += (_, _) =>
            {
                if (godotPage.ShowsPage)
                {
                    godotPage.Body.Focus();
                }
                else
                {
                    Editor.Focus();
                }
            };
            godotPage.Body.GotFocus += (_, _) => area.SetActiveGroup(this);
        }
        godotPage.Show(file);
    }

    /// <summary>The Godot project page this group shows, null while it shows a project.godot's text or another file.</summary>
    public Godot.GodotPageViewer? GodotPage => godotPage is { ShowsPage: true } ? godotPage : null;

    /// <summary>What the banner over a file Git has in conflict says, before its way to the merge window.</summary>
    public const string Conflicted = "This file has merge conflicts";

    private const string Merge = "Merge…";

    /// <summary>The banner over the file: where a read-only file comes from, that Git has it in conflict, what its notice
    /// says, and what its next save does to how it is stored.</summary>
    private void ShowBanner(OpenDocument document)
    {
        // A diff's own bar says what it is, and a tool window is no file.
        bool file = document is { Diff: null, Tool: null, DatabaseTab: null };
        bool conflicted = file && document.InConflict;
        string[] said = [.. new[] { document.IsReadOnly && file ? document.Origin : null, conflicted ? Conflicted : null, document.Notice, document.EncodingWarning }.OfType<string>()];
        banner.Say(said.Length > 0 ? string.Join("  ·  ", said) : null);
        bannerLink.IsVisible = document.Notice is not null && document.NoticeAction is not null;
        bannerLink.Content = document.NoticeAction?.Text;
        mergeLink.IsVisible = conflicted;
    }

    /// <summary>What the banner over the file shown says, null while it shows none.</summary>
    public string? BannerText => banner.Said;

    /// <summary>The link the banner offers after what it says, null while it offers none.</summary>
    public string? BannerLink => banner.IsVisible && bannerLink.IsVisible ? bannerLink.Content as string : null;

    /// <summary>The banner's link, for a click on it.</summary>
    public Control BannerLinkControl => bannerLink;

    /// <summary>The banner's Merge… over a file in conflict, shown while it is.</summary>
    public Button MergeLink => mergeLink;

    /// <summary>Takes a file's tab away. Where it was the tab shown, the group shows the one it showed before (the latest
    /// first), its neighbour where it showed no other, or nothing when it was the last.</summary>
    public void Remove(OpenDocument document)
    {
        if (tabs.FirstOrDefault(t => t.Document == document) is not { } tab)
        {
            return;
        }
        int index = tabs.IndexOf(tab);
        views.Remove(document);
        if (tables is not null && TableFile.For(document) is { } closed)
        {
            tables.Forget(closed);
        }
        tabs.Remove(tab);
        shownOrder.Remove(tab);
        document.Text.Changed -= OnTabTextChanged;
        markdown?.Forget(document);
        if (tab == active)
        {
            bool keys = preview?.IsKeyboardFocusWithin == true || Markdown?.IsKeyboardFocusWithin == true;
            ClearPreview();
            document.PropertyChanged -= OnDocumentChanged;
            active = null;
            if (tabs.Count > 0)
            {
                Show((shownOrder.Count > 0 ? shownOrder[0] : tabs[Math.Min(index, tabs.Count - 1)]).Document);
                if (keys)
                {
                    // The view in the editor's place that had the keys has gone: what shows the next tab takes them.
                    FocusShown();
                }
            }
            else
            {
                // A split left empty goes, and nothing of the file it showed may keep it.
                Find.Detach();
                Editor.Detach();
                Gutter.Clear();
                Editor.IsVisible = false;
                tabBar.IsVisible = false;
                banner.IsVisible = false;
                Navigation.Show(null);
                tables?.Hide();
                godotPage?.Hide();
                Side = null;
                FollowSidePreview();
            }
        }
    }

    /// <summary>Moves a tab along the strip, a pinned one among the pinned and another after them.</summary>
    public void Reorder(OpenDocument document, int index)
    {
        if (tabs.FirstOrDefault(t => t.Document == document) is { } tab)
        {
            int from = tabs.IndexOf(tab);
            int to = Math.Clamp(index > from ? index - 1 : index, 0, tabs.Count - 1);
            int fence = tabs.Count(t => t.IsPinned);
            tabs.Move(from, tab.IsPinned ? Math.Min(to, fence - 1) : Math.Max(to, fence));
        }
    }

    /// <summary>Where along the strip a point (in the strip's coordinates) falls, for a dropped tab.</summary>
    public int IndexAt(Point at)
    {
        int index = 0;
        bool down = tabsPlace != EditorTabsPlace.Top;
        foreach (Control tab in tabBar.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.Tab)))
        {
            Point centre = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), tabBar) ?? default;
            if (down ? at.Y > centre.Y : at.X > centre.X)
            {
                index++;
            }
        }
        return index;
    }

    /// <summary>Where a tab's carets are in this group: the editor's for the one shown, else as it was left.</summary>
    public SelectionSet SelectionsOf(OpenDocument document) =>
        active?.Document == document && InEditor(document) ? Editor.Selections : views.TryGetValue(document, out (SelectionSet Selections, Vector Scroll) view) ? view.Selections : document.Selections;

    /// <summary>Where a tab's editor was scrolled in this group: the editor's for the one shown, else as it was left.</summary>
    public Vector ScrollOf(OpenDocument document) =>
        active?.Document == document && InEditor(document) ? Editor.ScrollOffset : views.TryGetValue(document, out (SelectionSet Selections, Vector Scroll) view) ? view.Scroll : document.Scroll;

    /// <summary>Puts a tab's carets (and scroll) where they were kept, for when it is shown.</summary>
    public void Remember(OpenDocument document, SelectionSet selections, Vector scroll = default) => views[document] = (selections, scroll);

    /// <summary>The carets and scroll of the text shown, kept for when its tab is shown again; an asset has no editor state.</summary>
    public void Keep()
    {
        if (active is not null && InEditor(active.Document))
        {
            views[active.Document] = (Editor.Selections, Editor.ScrollOffset);
            active.Document.Selections = Editor.Selections;
            active.Document.Scroll = Editor.ScrollOffset;
        }
    }

    public void Cycle(int step)
    {
        if (active is null || tabs.Count < 2)
        {
            return;
        }
        Show(tabs[(tabs.IndexOf(active) + step + tabs.Count) % tabs.Count].Document);
    }

    private void OnDocumentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (active?.Document is not { } document || sender != document)
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(OpenDocument.Asset):
                // The decode landed: the picture takes the stand-in's place, and its focus.
                bool focused = preview?.IsKeyboardFocusWithin == true;
                ClearPreview();
                if (document.Asset is { } asset)
                {
                    preview = new AssetPreviewControl(asset);
                    editorPlace.Children.Add(preview);
                    if (focused)
                    {
                        preview.Focus();
                    }
                }
                break;
            case nameof(OpenDocument.Diff):
                bool keys = preview?.IsKeyboardFocusWithin == true;
                ClearPreview();
                preview = PreviewOf(document);
                editorPlace.Children.Add(preview);
                if (keys)
                {
                    FocusShown();
                }
                break;
            case nameof(OpenDocument.Highlighter):
                Editor.Highlighter = document.Highlighter;
                break;
            case nameof(OpenDocument.Notice) or nameof(OpenDocument.NoticeAction) or nameof(OpenDocument.EncodingWarning) or nameof(OpenDocument.InConflict):
                ShowBanner(document);
                break;
            case nameof(OpenDocument.IsReadOnly):
                FollowReadOnly(document);
                ShowBanner(document);
                break;
            case nameof(OpenDocument.Path):
                ShowTable(document);
                FollowReadOnly(document);
                ShowBanner(document);
                AttachFind();
                Editor.LineCommentPrefix = document.Language.LineComment;
                Editor.BlockComment = document.Language.BlockComment;
                Editor.Markup = document.Language.Markup;
                Editor.ReindentsPaste = document.Language.ReindentsPaste;
                FollowSidePreview();
                break;
            case nameof(OpenDocument.Indentation):
                Editor.Indent = document.Indentation.Options;
                Editor.LineLength = LineLengthOf(document);
                break;
            case nameof(OpenDocument.SoftWrap):
                Editor.SoftWrap = document.SoftWrap;
                break;
            case nameof(OpenDocument.ShowWhitespaces):
                Editor.ShowWhitespaces = document.ShowWhitespaces;
                break;
        }
    }

    /// <summary>Removes this group's view and its glyph runs, leaving the shared asset to its owning document.</summary>
    private void ClearPreview()
    {
        if (preview is not null)
        {
            editorPlace.Children.Remove(preview);
            (preview as IDisposable)?.Dispose();
            preview = null;
        }
    }

    /// <summary>A tab for whichever file the strip hands it: its icon, its name, a pin while pinned, and its × or, while
    /// changed, a dot; its menu, close, pin and presses act on the file it shows. It follows its file only while on the
    /// strip, so a tab closed or moved to another group is held by nothing it showed.</summary>
    private Control Tab()
    {
        RowIcon icon = new();
        TextBlock name = new() { Classes = { StyleClasses.TabName }, VerticalAlignment = VerticalAlignment.Center };
        Ellipse modified = new() { Classes = { StyleClasses.Modified }, Width = 7, Height = 7 };
        Button close = ToolButtons.Small(IconKeys.Close, "Close");
        close.Classes.Add(StyleClasses.TabClose);
        Button pin = ToolButtons.Small(IconKeys.Pin, "Unpin Tab");
        Border production = Tags.Of("production", TokenKeys.Error);
        Border readOnly = Tags.Of("read-only");
        Panel ends = new() { Width = 16, Height = 16, Children = { modified, close } };
        bool down = tabsPlace != EditorTabsPlace.Top;
        Border border = new() { Classes = { StyleClasses.Tab } };
        // Down a side, the name takes the column's width left over, its close at the column's edge.
        ChosenMark.Hold(border, down
            ? new DockPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    Docked(icon, Dock.Left, 0), Docked(ends, Dock.Right, 6), Docked(pin, Dock.Right, 6), Docked(production, Dock.Right, 6),
                    Docked(readOnly, Dock.Right, 6), Docked(name, Dock.Left, 6),
                },
            }
            : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Gaps.Small,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, name, production, readOnly, pin, ends },
            });
        OpenDocument? Shown() => (border.DataContext as EditorTab)?.Document;
        close.Click += (_, e) =>
        {
            if (Shown() is { } document)
            {
                area.CloseTab(this, document);
            }
            e.Handled = true;
        };
        pin.Click += (_, e) =>
        {
            if (Shown() is { } document)
            {
                Pin(document, false);
            }
            e.Handled = true;
        };
        void Paint(EditorTab tab)
        {
            OpenDocument document = tab.Document;
            name.Text = document.Title;
            icon.DataKey = document.IconKey;
            icon.ForegroundKey = document.IconBrushKey;
            ToolTip.SetTip(border, document.Tool is not null || document.DatabaseTab is not null ? document.Title
                : document.Follows ? $"{document.Path}\n\nFollows the file chosen in the Commit window; pin the tab or edit in it to keep it" : document.Path);
            border.Classes.Set(StyleClasses.Follows, document.Follows);
            border.Classes.Set(StyleClasses.Modified, document.IsModified || document.HasPending);
            Database.DatabaseMark mark = area.MarkOf?.Invoke(document) ?? Database.DatabaseMark.None;
            production.IsVisible = mark == Database.DatabaseMark.Production;
            readOnly.IsVisible = mark == Database.DatabaseMark.ReadOnly;
            border.Classes.Set(StyleClasses.Active, tab.IsActive);
            border.Classes.Set(StyleClasses.Elsewhere, !IsCurrent);
            pin.IsVisible = tab.IsPinned;
        }
        void OnCurrentChanged(object? sender, EventArgs e) => border.Classes.Set(StyleClasses.Elsewhere, !IsCurrent);
        void OnFileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (border.DataContext is not EditorTab tab)
            {
                return;
            }
            if (e.PropertyName is nameof(OpenDocument.IsModified) or nameof(OpenDocument.HasPending))
            {
                border.Classes.Set(StyleClasses.Modified, tab.Document.IsModified || tab.Document.HasPending);
            }
            else if (e.PropertyName is nameof(OpenDocument.Path) or nameof(OpenDocument.Diff) or nameof(OpenDocument.Follows) or nameof(OpenDocument.Title))
            {
                Paint(tab);
            }
        }
        bool onStrip = false;
        OpenDocument? followed = null;
        void Follow()
        {
            OpenDocument? following = onStrip ? Shown() : null;
            if (following == followed)
            {
                return;
            }
            if (followed is not null)
            {
                followed.PropertyChanged -= OnFileChanged;
            }
            followed = following;
            if (following is not null)
            {
                following.PropertyChanged += OnFileChanged;
            }
        }
        border.DataContextChanged += (_, _) => Follow();
        border.AttachedToVisualTree += (_, _) =>
        {
            onStrip = true;
            CurrentChanged += OnCurrentChanged;
            Follow();
            border.Classes.Set(StyleClasses.Elsewhere, !IsCurrent);
        };
        border.DetachedFromVisualTree += (_, _) =>
        {
            onStrip = false;
            CurrentChanged -= OnCurrentChanged;
            Follow();
        };
        ContextMenu menu = new();
        menu.Opening += (_, _) =>
        {
            if (Shown() is { } document)
            {
                area.OpenTabMenu(this, document, menu);
            }
        };
        border.ContextMenu = menu;
        border.PointerPressed += (_, e) =>
        {
            if (Shown() is not { } document)
            {
                return;
            }
            PointerPointProperties pressed = e.GetCurrentPoint(this).Properties;
            if (pressed.IsMiddleButtonPressed)
            {
                area.CloseTab(this, document);
                e.Handled = true;
            }
            // Each second press of a run of clicks, so double-clicks in quick turn each toggle.
            else if (pressed.IsLeftButtonPressed && e.ClickCount % 2 == 0)
            {
                area.RequestMaximize();
                e.Handled = true;
            }
            else if (pressed.IsLeftButtonPressed)
            {
                area.SetActiveGroup(this);
                Show(document);
                area.Activated(this);
                area.TabDragOf(this)?.Begin((this, document), e, border);
                e.Handled = true;
            }
        };
        border.PointerMoved += (_, e) => area.TabDragOf(this)?.Continue(e);
        border.PointerReleased += (_, e) => area.TabDragOf(this)?.End(e);
        if (!down)
        {
            return border.Showing<EditorTab>(Paint);
        }
        // Each tab of a column as high as the strip is across the top, its margins inside it as there.
        Panel slot = new() { Children = { border } };
        slot.Bind(HeightProperty, slot.GetResourceObservable(TokenKeys.TabStripHeight));
        return slot.Showing<EditorTab>(Paint);

        static Control Docked(Control part, Dock side, double apart)
        {
            part[DockProperty] = side;
            part.Margin = new Thickness(apart, 0, 0, 0);
            return part;
        }
    }
}
