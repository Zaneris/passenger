using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Navigation;

/// <summary>What Find in Files' scope offers and where it starts: the scope kept (or asked for from the Solution view),
/// the solution's projects, the directories and file masks used lately, the latest first, and the named scopes.</summary>
public sealed record FindChoices(SearchScope Start, IReadOnlyList<ProjectModel> Projects, IReadOnlyList<string> Directories,
    IReadOnlyList<string> Scopes, IReadOnlyList<string> Masks);

/// <summary>Find in Files (and Replace in Files): the query with Match case, Words and Regex (Alt+C, Alt+O, Alt+X), the
/// scope (Solution, Project, Directory with Recursively, or a named Scope: Alt+U, Alt+P, Alt+D, Alt+S, Alt+Y) and the
/// file mask (Alt+K), the matches as they are found with each one's file and line, the chosen one's file previewed
/// below; Enter or a double-click goes to it, Escape closes. In Replace mode a replacement and Replace All.</summary>
public sealed class FindInFilesPopup : UserControl, IAnswers<SearchMatch?>
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);

    /// <summary>The scopes for a solution, with the letter of each one's Alt key.</summary>
    private static readonly (SearchScopeKind Kind, string Name, Key Key)[] Kinds =
    [
        (SearchScopeKind.Solution, "Solution", Key.U),
        (SearchScopeKind.Project, "Project", Key.P),
        (SearchScopeKind.Directory, "Directory", Key.D),
        (SearchScopeKind.Scope, "Scope", Key.S),
    ];

    private readonly Func<SearchQuery, SearchScope, CancellationToken, Task<IReadOnlyList<SearchMatch>>> search;
    private readonly Func<string, TextSnapshot?> textOf;
    private readonly TextBox query;
    private readonly TextBox? replacement;
    private readonly ToggleButton matchCase;
    private readonly ToggleButton words;
    private readonly ToggleButton regex;
    private readonly Dictionary<SearchScopeKind, ToggleButton> tabs = [];
    private readonly Dictionary<SearchScopeKind, Control> details = [];
    private readonly IReadOnlyList<ProjectModel> projects;
    private readonly ComboBox projectBox;
    private readonly ComboBox directoryBox;
    private readonly ToggleButton recursive;
    private readonly ComboBox scopeBox;
    private readonly CheckBox maskOn;
    private readonly ComboBox maskBox;
    private readonly ListBox results;
    private readonly TextBlock status;
    private readonly CodeEditor preview;
    private readonly Debounce typing;
    private SearchScopeKind kind;
    private CancellationTokenSource? asking;
    private IReadOnlyList<SearchMatch> matches = [];

    /// <summary>The query and scope the matches shown were found for.</summary>
    private (SearchQuery Query, SearchScope Scope)? shownFor;

    private Task? searching;

    /// <param name="ligatures">Settings' Enable ligatures, which the matched lines and the preview follow.</param>
    public FindInFilesPopup(Func<SearchQuery, SearchScope, CancellationToken, Task<IReadOnlyList<SearchMatch>>> search, Func<string, TextSnapshot?> textOf,
        bool replace, string? initial, FindChoices choices, bool ligatures)
    {
        this.search = search;
        this.textOf = textOf;
        projects = choices.Projects;
        Width = PopupFrame.Widest;
        query = new TextBox { Classes = { "search" }, PlaceholderText = "Text to find", Text = initial ?? string.Empty };
        matchCase = Option(OptionGlyph(IconKeys.MatchCase), "Match case (Alt+C)");
        words = Option(OptionGlyph(IconKeys.WholeWord), "Words (Alt+O)");
        regex = Option(new TextBlock { Text = ".*", FontFamily = CSharp.Fonts.Mono, FontWeight = FontWeight.Bold, FontSize = 12 }, "Regex (Alt+X)");
        DockPanel find = new() { Margin = new Thickness(10, 0, 10, 6) };
        StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 0, 0, 0), Children = { matchCase, words, regex } };
        DockPanel.SetDock(toggles, Dock.Right);
        find.Children.Add(toggles);
        find.Children.Add(query);

        maskOn = new CheckBox { Content = "File mask:", IsChecked = choices.Start.Mask is not null, FontSize = 12, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(maskOn, "File mask (Alt+K)");
        maskBox = Field(choices.Masks.Count > 0 ? choices.Masks : ["*.cs"], editable: true);
        maskBox.Width = 150;
        maskBox.Text = choices.Start.Mask ?? choices.Masks.FirstOrDefault() ?? "*.cs";
        maskBox.IsEnabled = maskOn.IsChecked == true;
        StackPanel mask = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children = { maskOn, maskBox } };
        DockPanel header = new() { Margin = new Thickness(12, 8, 10, 6) };
        DockPanel.SetDock(mask, Dock.Right);
        header.Children.Add(mask);
        header.Children.Add(new TextBlock { Classes = { "popupTitle" }, Text = replace ? "Replace in Files" : "Find in Files", VerticalAlignment = VerticalAlignment.Center });
        StackPanel top = new() { Children = { header, find } };
        if (replace)
        {
            replacement = new TextBox { Classes = { "search" }, PlaceholderText = "Replacement" };
            Button all = new() { Content = "Replace All", Classes = { "primary" }, Margin = new Thickness(6, 0, 0, 0) };
            all.Click += (_, _) => RequestReplace();
            DockPanel row = new() { Margin = new Thickness(10, 0, 10, 6) };
            DockPanel.SetDock(all, Dock.Right);
            row.Children.Add(all);
            row.Children.Add(replacement);
            top.Children.Add(row);
        }

        projectBox = Field(choices.Projects, editable: false);
        projectBox.ItemTemplate = new FuncDataTemplate<ProjectModel>((project, _) => new TextBlock { Text = project?.Name });
        projectBox.SelectedItem = choices.Projects.FirstOrDefault(p => p.Path == choices.Start.Project) ?? choices.Projects.FirstOrDefault();
        directoryBox = Field(choices.Directories, editable: true);
        directoryBox.Text = choices.Start.Directory ?? string.Empty;
        recursive = new ToggleButton { Classes = { "option" }, Content = Glyph(IconKeys.Structure), IsChecked = choices.Start.Recursive };
        ToolTip.SetTip(recursive, "Recursively (Alt+Y)");
        Button browse = ToolButtons.Of(IconKeys.FolderOpen, "Choose a directory", () => IdeErrors.Observe(BrowseAsync()));
        browse.Margin = new Thickness(4, 0, 2, 0);
        DockPanel directory = new() { Children = { recursive, browse, directoryBox } };
        DockPanel.SetDock(recursive, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        scopeBox = Field(choices.Scopes, editable: false);
        scopeBox.SelectedItem = choices.Scopes.FirstOrDefault(s => s == choices.Start.Named) ?? choices.Scopes.FirstOrDefault();
        details[SearchScopeKind.Solution] = new Panel();
        details[SearchScopeKind.Project] = projectBox;
        details[SearchScopeKind.Directory] = directory;
        details[SearchScopeKind.Scope] = scopeBox;
        StackPanel kinds = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 8, 0) };
        foreach ((SearchScopeKind each, string name, Key key) in Kinds)
        {
            ToggleButton tab = new() { Classes = { "segment" }, Content = name, FontSize = 12 };
            ToolTip.SetTip(tab, $"{name} (Alt+{key})");
            tab.Click += (_, _) => ScopeKind = each;
            tabs[each] = tab;
            kinds.Children.Add(tab);
        }
        Panel shown = new();
        foreach (Control detail in details.Values)
        {
            shown.Children.Add(detail);
        }
        DockPanel scope = new() { Margin = new Thickness(10, 0, 10, 6), Children = { kinds, shown } };
        DockPanel.SetDock(kinds, Dock.Left);
        top.Children.Add(scope);
        Show(choices.Start.Kind);

        status = new TextBlock { Classes = { "muted" }, FontSize = 12, Margin = new Thickness(12, 0, 12, 6) };
        top.Children.Add(status);
        results = new ListBox
        {
            Classes = { "tree" },
            Height = ListRow.Height * 10,
            ItemTemplate = new FuncDataTemplate<SearchMatch>((match, _) => match is null ? new TextBlock() : Row(match, ligatures), supportsRecycling: false),
        };
        results.SelectionChanged += (_, _) => ShowPreview();
        results.DoubleTapped += (_, _) => Choose();
        ToolWindows.ListMenus.Attach(results, () => results.SelectedItem is not SearchMatch chosen ? [] :
        [
            ToolWindows.ListMenus.Item("Go to", "Enter", Choose),
            ToolWindows.ListMenus.Item("Copy Path", null, () => IdeErrors.Observe(ToolWindows.ListMenus.CopyAsync(this, chosen.Path))),
            ToolWindows.ListMenus.Item("Copy Line", null, () => IdeErrors.Observe(ToolWindows.ListMenus.CopyAsync(this, chosen.LineText.Trim()))),
        ]);
        preview = new CodeEditor { IsReadOnly = true, ShowErrorStripe = false, Height = 180, Focusable = false, Ligatures = ligatures };
        // Clear of the frame's rounded foot, which the editor's square corners would cover.
        Border previewFrame = new() { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 0, 0, 8), Child = preview };
        previewFrame.Bind(Border.BorderBrushProperty, previewFrame.GetResourceObservable(TokenKeys.Divider));
        Content = new Border
        {
            Classes = { "popup" },
            Child = new StackPanel { Children = { top, results, previewFrame } },
        };
        typing = new Debounce(Pause, () => searching = Refresh());
        query.TextChanged += (_, _) => Retype();
        foreach (ToggleButton option in new[] { matchCase, words, regex })
        {
            option.IsCheckedChanged += (_, _) => Retype();
        }
        recursive.IsCheckedChanged += (_, _) => Rescope();
        maskOn.IsCheckedChanged += (_, _) =>
        {
            maskBox.IsEnabled = maskOn.IsChecked == true;
            Rescope();
        };
        projectBox.SelectionChanged += (_, _) => Rescope();
        scopeBox.SelectionChanged += (_, _) => Rescope();
        directoryBox.PropertyChanged += (_, e) => Retyped(e, ComboBox.TextProperty);
        maskBox.PropertyChanged += (_, e) => Retyped(e, ComboBox.TextProperty);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(() =>
        {
            query.Focus();
            query.SelectAll();
        }, DispatcherPriority.Loaded);
        if (query.Text is { Length: > 0 })
        {
            searching = Refresh();
        }
    }

    /// <summary>A match to go to, or null when dismissed.</summary>
    public event EventHandler<SearchMatch?>? Done;

    /// <summary>Replace All pressed: the query, its replacement, and the files it matched in.</summary>
    public event EventHandler<(SearchQuery Query, string Replacement, IReadOnlyList<SearchMatch> Matches)>? ReplaceRequested;

    /// <summary>The scope or the mask changed, for the window to keep.</summary>
    public event EventHandler<SearchScope>? ScopeChanged;

    public IReadOnlyList<SearchMatch> Matches => matches;

    public string Status => status.Text ?? string.Empty;

    public SearchQuery Query => new(query.Text ?? string.Empty, matchCase.IsChecked == true, words.IsChecked == true, regex.IsChecked == true);

    /// <summary>Where the popup looks as it stands: the scope chosen, what each scope names, and the mask while it is on.</summary>
    public SearchScope Scope => new(kind, (projectBox.SelectedItem as ProjectModel)?.Path, directoryBox.Text?.Trim(), recursive.IsChecked == true,
        scopeBox.SelectedItem as string, maskOn.IsChecked == true ? maskBox.Text ?? string.Empty : null);

    /// <summary>The scope chosen, as its tab (or its Alt key) chooses it.</summary>
    public SearchScopeKind ScopeKind
    {
        get => kind;
        set
        {
            bool changed = value != kind;
            Show(value);
            if (changed)
            {
                Rescope();
            }
        }
    }

    /// <summary>The project the Project scope searches, by its file.</summary>
    public string? Project
    {
        get => (projectBox.SelectedItem as ProjectModel)?.Path;
        set => projectBox.SelectedItem = projects.FirstOrDefault(p => p.Path == value);
    }

    /// <summary>The directory the Directory scope searches.</summary>
    public string? Directory
    {
        get => directoryBox.Text;
        set => directoryBox.Text = value;
    }

    /// <summary>Whether the Directory scope searches the directory's subdirectories too.</summary>
    public bool Recursive
    {
        get => recursive.IsChecked == true;
        set => recursive.IsChecked = value;
    }

    /// <summary>The named scope the Scope scope searches.</summary>
    public string? NamedScope
    {
        get => scopeBox.SelectedItem as string;
        set => scopeBox.SelectedItem = value;
    }

    /// <summary>The file mask while it is on, else null; set to null turns it off.</summary>
    public string? FileMask
    {
        get => maskOn.IsChecked == true ? maskBox.Text ?? string.Empty : null;
        set
        {
            if (value is not null)
            {
                maskBox.Text = value;
            }
            maskOn.IsChecked = value is not null;
        }
    }

    /// <summary>The matches' number: a thousand and more past the limit.</summary>
    private static string Count(int found) =>
        found >= FileSearch.MostMatches ? $"{FileSearch.MostMatches}+ matches" : Plural.Of(found, "match", "matches");

    public string Text
    {
        get => query.Text ?? string.Empty;
        set => query.Text = value;
    }

    public string Replacement
    {
        get => replacement?.Text ?? string.Empty;
        set
        {
            if (replacement is not null)
            {
                replacement.Text = value;
            }
        }
    }

    public bool MatchCase
    {
        get => matchCase.IsChecked == true;
        set => matchCase.IsChecked = value;
    }

    public bool Words
    {
        get => words.IsChecked == true;
        set => words.IsChecked = value;
    }

    public bool Regex
    {
        get => regex.IsChecked == true;
        set => regex.IsChecked = value;
    }

    /// <summary>Searches now, as a pause in typing does.</summary>
    public Task SearchAsync()
    {
        typing.Stop();
        searching = Refresh();
        return searching;
    }

    /// <summary>The search under way, if any.</summary>
    public Task Searching => searching ?? Task.CompletedTask;

    /// <summary>Replace All, on the matches of the query and scope as they stand: those shown may be the last search's
    /// for a moment (a key or the scope changed within the pause before the next), so they are searched again
    /// first.</summary>
    public void RequestReplace() => IdeErrors.Observe(ReplaceAsync());

    private async Task ReplaceAsync()
    {
        if (shownFor != (Query, Scope))
        {
            await SearchAsync();
        }
        if (matches.Count > 0 && shownFor == (Query, Scope))
        {
            ReplaceRequested?.Invoke(this, (Query, Replacement, matches));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        typing.Stop();
        asking?.Cancel();
    }

    /// <summary>A change to what is asked: the older query's matches go at once, so none is replaced for what is no longer
    /// asked, and a query that finds nothing says why now (an empty one nothing) rather than that it searches.</summary>
    private void Retype()
    {
        asking?.Cancel();
        SearchQuery asked = Query;
        Show([], asked.Text.Length == 0 ? string.Empty : FileSearch.Problem(asked) ?? Scope.Problem() ?? "Searching…", null);
        typing.Restart();
    }

    private void Retyped(AvaloniaPropertyChangedEventArgs change, AvaloniaProperty text)
    {
        if (change.Property == text)
        {
            Rescope();
        }
    }

    private void Rescope()
    {
        ScopeChanged?.Invoke(this, Scope);
        Retype();
    }

    /// <summary>Shows a scope's tab chosen and its own fields: the project, the directory, the named scope.</summary>
    private void Show(SearchScopeKind shown)
    {
        kind = shown;
        foreach ((SearchScopeKind each, ToggleButton tab) in tabs)
        {
            tab.IsChecked = each == shown;
        }
        foreach ((SearchScopeKind each, Control detail) in details)
        {
            detail.IsVisible = each == shown;
        }
    }

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        IReadOnlyList<IStorageFolder> picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Directory",
            SuggestedStartLocation = System.IO.Directory.Exists(Directory) ? await storage.TryGetFolderFromPathAsync(Directory!) : null,
        });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
        {
            Directory = path;
        }
    }

    private async Task Refresh()
    {
        asking?.Cancel();
        CancellationTokenSource cancel = asking = new();
        SearchQuery asked = Query;
        SearchScope where = Scope;
        if (asked.Text.Length == 0)
        {
            Show([], string.Empty, (asked, where));
            return;
        }
        if ((FileSearch.Problem(asked) ?? where.Problem()) is { } problem)
        {
            Show([], problem, (asked, where));
            return;
        }
        Show([], "Searching…", null);
        try
        {
            IReadOnlyList<SearchMatch> found = await search(asked, where, cancel.Token);
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            int files = found.Select(m => m.Path).Distinct().Count();
            Show(found, found.Count == 0 ? "Nothing found" : $"{Count(found.Count)} in {Plural.Of(files, "file")}", (asked, where));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Show(IReadOnlyList<SearchMatch> found, string said, (SearchQuery Query, SearchScope Scope)? of)
    {
        matches = found;
        shownFor = of;
        results.ItemsSource = found;
        results.SelectedIndex = found.Count > 0 ? 0 : -1;
        status.Text = said;
        ShowPreview();
    }

    private void ShowPreview()
    {
        if (results.SelectedItem is not SearchMatch match || textOf(match.Path) is not { } text)
        {
            preview.Show(new TextDocument(), SelectionSet.Single(0), default);
            return;
        }
        TextDocument shown = new(text.ToString());
        preview.Show(shown, SelectionSet.Single(0), default);
        (preview.Highlighter as IDisposable)?.Dispose();
        preview.Highlighter = Languages.Highlighters.For(Languages.Language.ForPath(match.Path), shown);
        if (match.Line < shown.Snapshot.LineCount)
        {
            int at = shown.Snapshot.GetLineStart(match.Line) + match.Column;
            preview.SetHighlights([new TextRange(at, match.Length)], 0);
            preview.GoTo(new TextPosition(match.Line, match.Column));
        }
    }

    private void Choose()
    {
        if (results.SelectedItem is SearchMatch match)
        {
            Done?.Invoke(this, match);
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.C or Key.O or Key.X)
        {
            ToggleButton option = e.Key == Key.C ? matchCase : e.Key == Key.O ? words : regex;
            option.IsChecked = option.IsChecked != true;
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Alt && Kinds.FirstOrDefault(k => k.Key == e.Key) is { Name: not null } chosen)
        {
            ScopeKind = chosen.Kind;
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.K or Key.Y)
        {
            ToggleButton toggled = e.Key == Key.K ? maskOn : recursive;
            toggled.IsChecked = toggled.IsChecked != true;
            if (toggled == maskOn && maskOn.IsChecked == true)
            {
                maskBox.Focus();
            }
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                Done?.Invoke(this, null);
                e.Handled = true;
                break;
            case Key.Enter when replacement?.IsFocused != true && !InField():
                Choose();
                e.Handled = true;
                break;
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown when !InField() && ListKeys.Move(results, e):
                e.Handled = true;
                break;
        }
    }

    /// <summary>Whether the keys are in a scope's or the mask's field, whose own Enter and arrows choose in its list.</summary>
    private bool InField() => new[] { projectBox, directoryBox, scopeBox, maskBox }.Any(f => f.IsKeyboardFocusWithin);

    /// <summary>A compact drop-down field of the popup's scope and mask, typed into when editable.</summary>
    private static ComboBox Field(System.Collections.IEnumerable items, bool editable) => new()
    {
        Classes = { "compact" },
        ItemsSource = items,
        IsEditable = editable,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static PathIcon Glyph(string icon)
    {
        PathIcon glyph = new() { Classes = { "small" } };
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(icon));
        return glyph;
    }

    private static PathIcon OptionGlyph(string icon)
    {
        PathIcon glyph = new();
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(icon));
        return glyph;
    }

    private static ToggleButton Option(Control label, string tip)
    {
        ToggleButton button = new() { Classes = { "option" }, Content = label };
        ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>A match's line, its leading space trimmed and the match itself bold, and where it is at the right.</summary>
    private static Control Row(SearchMatch match, bool ligatures)
    {
        string line = match.LineText;
        int indent = line.Length - line.TrimStart().Length;
        int start = Math.Clamp(match.Column - indent, 0, Math.Max(0, line.Length - indent));
        string trimmed = line[indent..];
        int length = Math.Min(match.Length, trimmed.Length - start);
        TextBlock text = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = CSharp.Fonts.Mono,
            FontSize = 12,
            FontFeatures = CSharp.Fonts.Features(ligatures),
        };
        text.Inlines = [];
        text.Inlines.Add(new Avalonia.Controls.Documents.Run(trimmed[..start]));
        text.Inlines.Add(new Avalonia.Controls.Documents.Run(trimmed.Substring(start, Math.Max(0, length))) { FontWeight = FontWeight.Bold });
        text.Inlines.Add(new Avalonia.Controls.Documents.Run(trimmed[Math.Min(trimmed.Length, start + Math.Max(0, length))..]));
        TextBlock where = new() { Text = $"{match.Name} {match.Line + 1}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), FontSize = 12 };
        where.Classes.Add("muted");
        DockPanel row = new() { Height = ListRow.Height, Margin = new Thickness(8, 0) };
        DockPanel.SetDock(where, Dock.Right);
        row.Children.Add(where);
        row.Children.Add(text);
        ToolTip.SetTip(row, match.Path);
        return row;
    }
}
