using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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

/// <summary>One editor of a split: its tabs (the pinned ones first), its find bar and its editor, which shows the
/// active tab's file. A file may be open in two groups at once (Split Right on it), each with its own carets and scroll
/// over the one text.</summary>
public sealed class EditorGroup : DockPanel
{
    private readonly EditorArea area;
    private readonly ObservableCollection<EditorTab> tabs = [];
    private readonly Dictionary<OpenDocument, (SelectionSet Selections, Vector Scroll)> views = [];
    private readonly Border tabBar;
    private readonly ItemsControl strip;
    private readonly ScrollViewer stripView;
    private readonly Grid body;
    private HairlineDivider? divider;
    private Control? side;
    private readonly Banner banner = new();
    private readonly Button bannerLink;
    private EditorTab? active;

    /// <summary>The table over the editor for a CSV or workbook, made as the group first shows one.</summary>
    private TableViewer? tables;

    /// <summary>This group's view of the active asset (its bitmap or face belongs to the document, not this control) or of
    /// release notes the app carries, rendered, in the editor's place.</summary>
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
            area.PreviewToggled(this);
            // It says what is in force once its action ran, or was refused.
            FollowPreviewToggle();
        };
        stripView = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = strip };
        // A narrower strip may leave the active tab out of sight.
        stripView.SizeChanged += (_, _) => RevealActive();
        tabBar = new Border
        {
            Classes = { StyleClasses.TabStrip },
            [DockProperty] = Dock.Top,
            Child = new DockPanel
            {
                Children =
                {
                    PreviewToggle,
                    stripView,
                },
            },
        };
        bannerLink = banner.Link(null, () => active?.Document.NoticeAction?.Run());
        bannerLink.Name = "BannerLink";
        bannerLink.IsVisible = false;
        Children.Add(tabBar);
        Children.Add(banner);
        Children.Add(Find);
        // The editor beside a pane of its file's own (a XAML file's preview), divided by a hairline that resizes them.
        body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,0,0") };
        body.Children.Add(Editor);
        Children.Add(body);
        // The group the person works in is the area's active one; while a table lies over the editor, the table takes its focus.
        Editor.GotFocus += (_, _) =>
        {
            area.SetActiveGroup(this);
            if (tables is { ShowsTable: true, View.IsVisible: true })
            {
                tables.View.Focus();
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
    }

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

    /// <summary>The columns of the editor's gutter, fed from the file it shows.</summary>
    public EditorGutter Gutter { get; }

    /// <summary>A pane beside the editor for the file it shows (a XAML file's preview), half the group's width at first and
    /// resized by the hairline between them; null for none.</summary>
    public Control? Side
    {
        get => side;
        set
        {
            if (value == side)
            {
                return;
            }
            if (side is not null)
            {
                body.Children.Remove(side);
            }
            if (value is null)
            {
                body.Children.Remove(divider!);
                divider = null;
                body.ColumnDefinitions = new ColumnDefinitions("*,0,0");
            }
            else if (divider is null)
            {
                // A divider only while there is a pane to divide from, so the splits' own are the only ones otherwise.
                divider = new HairlineDivider(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
                body.Children.Add(divider);
                body.ColumnDefinitions = new ColumnDefinitions("*,1,*");
            }
            side = value;
            if (value is not null)
            {
                // Another file's pane takes the place the last one was given.
                Grid.SetColumn(value, 2);
                body.Children.Add(value);
            }
        }
    }
    public FindBar Find { get; }

    /// <summary>The tab strip's toggle of the preview beside the editor, shown while the group shows a Markdown file or a
    /// XAML file the previewer draws.</summary>
    public ToggleButton PreviewToggle { get; }

    /// <summary>The group's own preview of its Markdown file, made as it first shows one.</summary>
    private MarkdownPreview? markdown;

    /// <summary>The preview beside the editor while it shows one of a Markdown file, else null.</summary>
    public MarkdownPreview? Markdown => Side as MarkdownPreview;

    /// <summary>A Markdown file shown in the editor has its preview beside it while the area shows previews, and its toggle
    /// on the strip either way; any other file neither.</summary>
    internal void FollowMarkdown()
    {
        OpenDocument? document = active?.Document;
        bool shows = document is not null && InEditor(document) && document.Language == Languages.Language.Markdown;
        FollowPreviewToggle();
        if (shows && area.ShowsMarkdownPreview)
        {
            markdown ??= new MarkdownPreview(Editor, area);
            Side = markdown;
            markdown.Show(document);
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
    }

    /// <summary>The strip's preview toggle shows for a Markdown file or a XAML file the previewer draws, pressed while
    /// that kind's preview is on, its tip naming the action it runs.</summary>
    internal void FollowPreviewToggle()
    {
        OpenDocument? document = active is { } shown && InEditor(shown.Document) ? shown.Document : null;
        bool markdown = document?.Language == Languages.Language.Markdown;
        bool xaml = !markdown && document is not null && area.HasXamlPreview(document);
        PreviewToggle.IsVisible = markdown || xaml;
        PreviewToggle.IsChecked = markdown ? area.ShowsMarkdownPreview : xaml && area.ShowsXamlPreview;
        if (markdown || xaml)
        {
            ActionTips.Set(PreviewToggle, markdown ? AppActions.MarkdownPreview : AppActions.XamlPreview, markdown ? "Markdown Preview" : "XAML Preview");
        }
    }

    public IReadOnlyList<EditorTab> Tabs => tabs;
    public OpenDocument? Active => active?.Document;
    public bool Has(OpenDocument document) => tabs.Any(t => t.Document == document);

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
    }

    /// <summary>A hidden tab's kept carets follow what changes its text meanwhile (a reload, a rename), as the shown one's do;
    /// release notes shown are drawn again from their new text (a build that carries others).</summary>
    private void OnTabTextChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (tabs.FirstOrDefault(t => t.Document.Text == sender) is not { } tab)
        {
            return;
        }
        if (tab == active && tab.Document.IsReleaseNotes && preview is not null)
        {
            ClearPreview();
            preview = PreviewOf(tab.Document);
            body.Children.Add(preview);
        }
        else if (tab != active && views.TryGetValue(tab.Document, out (SelectionSet Selections, Vector Scroll) view))
        {
            views[tab.Document] = (TextEditing.MapThrough(view.Selections, e.Edits).Clamp(e.After.Length), view.Scroll);
        }
    }

    /// <summary>What shows in the editor's place for a file that is no text to edit: an asset's preview, or release notes rendered.</summary>
    private Control PreviewOf(OpenDocument document) =>
        document.Asset is { } asset ? new AssetPreviewControl(asset) : new ReleaseNotesView(document, area.Browse);

    /// <summary>The active tab scrolled into the strip's sight once it is laid out, however many tabs crowd the strip.</summary>
    private void RevealActive() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (active is not null && strip.ContainerFromItem(active) is { } tab)
        {
            tab.BringIntoView();
        }
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>Whether the editor shows the file, rather than a view of its own in the editor's place.</summary>
    private static bool InEditor(OpenDocument document) => document.Asset is null && !document.IsReleaseNotes;

    /// <summary>Gives the keys to what shows the file: its preview or rendered notes, which its keys move through, else the
    /// editor.</summary>
    public void FocusShown()
    {
        if (preview is not null)
        {
            preview.Focus();
        }
        else
        {
            Editor.Focus();
        }
    }

    /// <summary>Shows a file's text, or its asset preview or rendered release notes, keeping the carets and scroll of the
    /// text it replaces. A preview detaches the hidden text editor so editing commands cannot change the previous file.</summary>
    public void Show(OpenDocument document)
    {
        Add(document);
        if (active?.Document == document && (Editor.IsVisible || preview is not null))
        {
            return;
        }
        Keep();
        if (active is not null)
        {
            active.IsActive = false;
            active.Document.PropertyChanged -= OnDocumentChanged;
        }
        active = tabs.First(t => t.Document == document);
        active.IsActive = true;
        RevealActive();
        document.PropertyChanged += OnDocumentChanged;
        ClearPreview();
        if (document.Asset is not null || document.IsReleaseNotes)
        {
            Find.Detach();
            Editor.Detach();
            Gutter.Clear();
            Editor.IsReadOnly = true;
            Editor.IsVisible = false;
            Side = null;
            tables?.Hide();
            preview = PreviewOf(document);
            body.Children.Add(preview);
            ShowBanner(document);
            tabBar.IsVisible = true;
            FollowMarkdown();
            return;
        }
        (SelectionSet selections, Vector scroll) = views.TryGetValue(document, out (SelectionSet Selections, Vector Scroll) view) ? view : (document.Selections, document.Scroll);
        Editor.LineCommentPrefix = document.Language.LineComment;
        Editor.BlockComment = document.Language.BlockComment;
        Editor.Markup = document.Language.Markup;
        Editor.Indent = document.Indentation.Options;
        Editor.ReindentsPaste = document.Language.ReindentsPaste;
        Editor.ShowWhitespaces = document.ShowWhitespaces;
        // Its decorations are the file's, which every editor showing it reads.
        Editor.Show(document.Text, selections, scroll, document.SoftWrap, document.Decorations);
        Editor.Highlighter = document.Highlighter;
        Gutter.Show(document);
        Editor.ContextLabel = ContextShown(document);
        Editor.IsReadOnly = document.IsReadOnly;
        ShowBanner(document);
        Editor.IsVisible = true;
        tabBar.IsVisible = true;
        ShowTable(document);
        AttachFind();
        FollowMarkdown();
    }

    /// <summary>The find bar on the file shown: its text, or its table while one lies over it; a read-only file's replace row
    /// greyed, saying why.</summary>
    private void AttachFind() =>
        Find.Attach(Editor, Table, Active is { IsReadOnly: true } shown ? Refusals.ReadOnly(shown.Name) : null);

    /// <summary>The table of the file shown, while it is a CSV or a workbook, null otherwise.</summary>
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
            // Over the editor in its own column, so a XAML-like pane beside it is never covered.
            body.Children.Add(tables.Body);
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
        }
        tables.Show(file);
    }

    /// <summary>The banner over the file: where a read-only file comes from, what its notice says, and what its next save
    /// does to how it is stored.</summary>
    private void ShowBanner(OpenDocument document)
    {
        string[] said = [.. new[] { document.IsReadOnly ? document.Origin : null, document.Notice, document.EncodingWarning }.OfType<string>()];
        banner.Say(said.Length > 0 ? string.Join("  ·  ", said) : null);
        bannerLink.IsVisible = document.Notice is not null && document.NoticeAction is not null;
        bannerLink.Content = document.NoticeAction?.Text;
    }

    /// <summary>What the banner over the file shown says, null while it shows none.</summary>
    public string? BannerText => banner.Said;

    /// <summary>The link the banner offers after what it says, null while it offers none.</summary>
    public string? BannerLink => banner.IsVisible && bannerLink.IsVisible ? bannerLink.Content as string : null;

    /// <summary>The banner's link, for a click on it.</summary>
    public Control BannerLinkControl => bannerLink;

    /// <summary>Takes a file's tab away; the group shows its neighbour, or nothing when it was the last.</summary>
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
        document.Text.Changed -= OnTabTextChanged;
        markdown?.Forget(document);
        if (tab == active)
        {
            ClearPreview();
            document.PropertyChanged -= OnDocumentChanged;
            active = null;
            if (tabs.Count > 0)
            {
                Show(tabs[Math.Min(index, tabs.Count - 1)].Document);
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
                tables?.Hide();
                Side = null;
                FollowMarkdown();
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
        foreach (Control tab in tabBar.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.Tab)))
        {
            Point centre = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, 0), tabBar) ?? default;
            if (at.X > centre.X)
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
                    body.Children.Add(preview);
                    if (focused)
                    {
                        preview.Focus();
                    }
                }
                break;
            case nameof(OpenDocument.Highlighter):
                Editor.Highlighter = document.Highlighter;
                break;
            case nameof(OpenDocument.Notice) or nameof(OpenDocument.NoticeAction) or nameof(OpenDocument.EncodingWarning):
                ShowBanner(document);
                break;
            case nameof(OpenDocument.ProjectContexts):
            case nameof(OpenDocument.ProjectContext):
                Editor.ContextLabel = ContextShown(document);
                break;
            case nameof(OpenDocument.Path):
                ShowTable(document);
                Editor.IsReadOnly = document.IsReadOnly;
                ShowBanner(document);
                AttachFind();
                Editor.LineCommentPrefix = document.Language.LineComment;
                Editor.BlockComment = document.Language.BlockComment;
                Editor.Markup = document.Language.Markup;
                Editor.ReindentsPaste = document.Language.ReindentsPaste;
                FollowMarkdown();
                break;
            case nameof(OpenDocument.Indentation):
                Editor.Indent = document.Indentation.Options;
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
            body.Children.Remove(preview);
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
        RowIcon closeGlyph = new() { Width = 10, Height = 10, DataKey = IconKeys.Close };
        Button close = new() { Classes = { StyleClasses.TabClose }, Content = closeGlyph };
        ToolTip.SetTip(close, "Close");
        RowIcon pinGlyph = new() { Width = 10, Height = 10, DataKey = IconKeys.Pin };
        Button pin = new() { Classes = { StyleClasses.TabPin }, Content = pinGlyph };
        ToolTip.SetTip(pin, "Unpin Tab");
        Border border = new()
        {
            Classes = { StyleClasses.Tab },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, name, pin, new Panel { Width = 16, Height = 16, Children = { modified, close } } },
            },
        };
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
            icon.Data = document.Icon;
            icon.Foreground = document.IconBrush;
            ToolTip.SetTip(border, document.Path);
            modified.IsVisible = document.IsModified;
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
            if (e.PropertyName == nameof(OpenDocument.IsModified))
            {
                modified.IsVisible = tab.Document.IsModified;
            }
            else if (e.PropertyName == nameof(OpenDocument.Path))
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
        return border.Showing<EditorTab>(Paint);
    }

    /// <summary>What the editor's context switcher shows for a file in several of the server's projects: the target
    /// framework alone (Roslyn names a multi-targeted project's "Lib (net10.0)") when they are one project's, else the
    /// project's name too; nothing for a file in one.</summary>
    private static string? ContextShown(OpenDocument document)
    {
        if (document.ProjectContexts.Count < 2 || document.ProjectContext is not { } chosen)
        {
            return null;
        }
        return document.ProjectContexts.Select(ProjectContext.ProjectOf).Distinct(StringComparer.Ordinal).Count() == 1
            && ProjectContext.FrameworkOf(chosen) is { } framework ? framework : chosen;
    }
}
