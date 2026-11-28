using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using Microsoft.CodeAnalysis.CSharp;

namespace Passenger.App.Navigation;

/// <summary>What Find in Files' scope offers and where it starts: the scope kept (or asked for from the Solution view),
/// the solution's projects, the directories and file masks used lately, the latest first, and the named scopes.</summary>
public sealed record FindChoices(SearchScope Start, IReadOnlyList<ProjectModel> Projects, IReadOnlyList<string> Directories,
    IReadOnlyList<string> Scopes, IReadOnlyList<string> Masks);

/// <summary>Find in Files (and Replace in Files): the query with Match case, Words and Regex (Alt+C, Alt+O, Alt+X), the
/// scope (Solution, Project, Directory with Recursively, or a named Scope: Alt+U, Alt+P, Alt+D, Alt+S, Alt+Y) and the
/// file mask (Alt+K), the lines with matches as they are found, each once with its file and line and every match on it
/// bold, the chosen one's file previewed below; Enter or a double-click goes to its first match, Escape closes. Replace
/// in Files has a replacement and Replace All under the query, a row the open popup takes or gives up in place
/// (<see cref="Replacing"/>).</summary>
public sealed class FindInFilesPopup : UserControl, IAnswers<SearchMatch?>
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);

    /// <summary>The scopes for a solution, with the letter of each one's Alt key.</summary>
    private static readonly (SearchScopeKind Kind, Words Name, Key Key)[] Kinds =
    [
        (SearchScopeKind.Solution, NavigationWords.InSolution, Key.U),
        (SearchScopeKind.Project, NavigationWords.InProject, Key.P),
        (SearchScopeKind.Directory, NavigationWords.InDirectory, Key.D),
        (SearchScopeKind.Scope, NavigationWords.InScope, Key.S),
    ];

    /// <summary>The file mask offered where none was used before.</summary>
    private const string DefaultMask = "*.cs";

    /// <summary>The key that turns the file mask on and off, its field taking the keys as it turns on.</summary>
    private static readonly Keystroke MaskKey = new(Key.K, KeyModifiers.Alt);

    /// <summary>The key that keeps the matches in the Find window (<see cref="FindWindow"/>).</summary>
    private static readonly Keystroke FindWindowKey = new(Key.Enter, Keystroke.Command);

    private readonly Func<SearchQuery, SearchScope, CancellationToken, Task<FilesFound>> search;
    private readonly Func<string, TextSnapshot?> textOf;
    private readonly Func<string, CSharpParseOptions> parseOptions;
    private readonly TextBox query;
    private readonly TextBlock title;
    private readonly TextBox replacement;

    /// <summary>The replacement and Replace All, shown while it is Replace in Files.</summary>
    private readonly DockPanel replaceRow;
    private readonly ToggleButton matchCase;
    private readonly ToggleButton words;
    private readonly ToggleButton regex;
    private readonly Segments<SearchScopeKind> kinds = new() { Margin = new Thickness(0, 0, Gaps.Normal, 0) };
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
    private FilesFound found = FilesFound.None;
    private IReadOnlyList<SearchLine> lines = [];

    /// <summary>The query and scope the matches shown were found for.</summary>
    private (SearchQuery Query, SearchScope Scope)? shownFor;

    private Task? searching;

    /// <param name="parseOptions">What a C# file's preview is parsed with: its project's symbols.</param>
    /// <param name="ligatures">Settings' Enable ligatures, which the matched lines and the preview follow.</param>
    public FindInFilesPopup(Func<SearchQuery, SearchScope, CancellationToken, Task<FilesFound>> search, Func<string, TextSnapshot?> textOf,
        Func<string, CSharpParseOptions> parseOptions, bool replace, string? initial, FindChoices choices, bool ligatures)
    {
        this.search = search;
        this.textOf = textOf;
        this.parseOptions = parseOptions;
        projects = choices.Projects;
        Width = PopupFrame.Widest;
        query = SearchBox.InPopup(NameOf(replace), NavigationWords.TextToFind);
        query.Text = initial ?? string.Empty;
        matchCase = OptionToggles.Of(OptionToggles.MatchCase);
        words = OptionToggles.Of(OptionToggles.Words);
        regex = OptionToggles.Of(OptionToggles.Regex);
        DockPanel find = new() { Margin = new Thickness(Gaps.Wide, 0, Gaps.Wide, Gaps.Small) };
        StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Hair, Margin = new Thickness(Gaps.Small, 0, 0, 0), Children = { matchCase, words, regex } };
        DockPanel.SetDock(toggles, Dock.Right);
        find.Children.Add(toggles);
        find.Children.Add(query);

        maskOn = PopupFrame.Option(NavigationWords.FileMaskOn, choices.Start.Mask is not null, takesKeys: true);
        ShownWords.Set(maskOn, ToolTip.TipProperty, ActionTips.Named(NavigationWords.FileMask, KeyWords.Of(MaskKey)));
        maskBox = Field(choices.Masks.Count > 0 ? choices.Masks : [DefaultMask], editable: true);
        maskBox.Width = 150;
        maskBox.Text = choices.Start.Mask ?? choices.Masks.FirstOrDefault() ?? DefaultMask;
        maskBox.IsEnabled = maskOn.IsChecked == true;
        StackPanel mask = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Small, Children = { maskOn, maskBox } };
        DockPanel header = new() { Margin = new Thickness(Gaps.Wide, Gaps.Normal, Gaps.Wide, Gaps.Small) };
        DockPanel.SetDock(mask, Dock.Right);
        header.Children.Add(mask);
        title = new TextBlock { Classes = { StyleClasses.PaneTitle }, [ShownWords.TextProperty] = NameOf(replace), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        replacement = SearchBox.InPopup(NavigationWords.Replacement, NavigationWords.Replacement);
        Editors.ReplaceBoxes.Take(replacement);
        Button all = new() { [ShownWords.ContentProperty] = NavigationWords.ReplaceAll, Classes = { StyleClasses.Primary }, Margin = new Thickness(Gaps.Small, 0, 0, 0) };
        all.Click += (_, _) => RequestReplace();
        replaceRow = new DockPanel { Margin = new Thickness(Gaps.Wide, 0, Gaps.Wide, Gaps.Small), IsVisible = replace };
        DockPanel.SetDock(all, Dock.Right);
        replaceRow.Children.Add(all);
        replaceRow.Children.Add(replacement);
        StackPanel top = new() { Children = { header, find, replaceRow } };

        projectBox = Field(choices.Projects, editable: false);
        ReusedRows.Use(projectBox, () =>
        {
            ProjectLabel named = new();
            return named.Showing<ProjectModel>(named.Show);
        });
        projectBox.SelectedItem = choices.Projects.FirstOrDefault(p => p.Path == choices.Start.Project) ?? choices.Projects.FirstOrDefault();
        directoryBox = Field(choices.Directories, editable: true);
        directoryBox.Text = choices.Start.Directory ?? string.Empty;
        recursive = OptionToggles.Of(OptionToggles.Recursively);
        recursive.IsChecked = choices.Start.Recursive;
        Button browse = ToolButtons.Of(IconKeys.FolderOpen, NavigationWords.ChooseDirectory, () => IdeErrors.Observe(BrowseAsync()));
        browse.Margin = new Thickness(Gaps.Tight, 0, Gaps.Hair, 0);
        DockPanel directory = new() { Children = { recursive, browse, directoryBox } };
        DockPanel.SetDock(recursive, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        scopeBox = Field(choices.Scopes, editable: false);
        scopeBox.SelectedItem = choices.Scopes.FirstOrDefault(s => s == choices.Start.Named) ?? choices.Scopes.FirstOrDefault();
        details[SearchScopeKind.Solution] = new Panel();
        details[SearchScopeKind.Project] = projectBox;
        details[SearchScopeKind.Directory] = directory;
        details[SearchScopeKind.Scope] = scopeBox;
        foreach ((SearchScopeKind each, Words name, Key key) in Kinds)
        {
            ShownWords.Set(kinds.Add(each, name), ToolTip.TipProperty, ActionTips.Named(name, KeyWords.Of(key, KeyModifiers.Alt)));
        }
        kinds.Chosen += (_, each) => ScopeKind = each;
        Panel shown = new();
        foreach (Control detail in details.Values)
        {
            shown.Children.Add(detail);
        }
        DockPanel scope = new() { Margin = new Thickness(Gaps.Wide, 0, Gaps.Wide, Gaps.Small), Children = { kinds, shown } };
        DockPanel.SetDock(kinds, Dock.Left);
        top.Children.Add(scope);
        Show(choices.Start.Kind);

        status = new TextBlock { Classes = { StyleClasses.Muted, StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center };
        FindWindow = new Button { Classes = { StyleClasses.Link, StyleClasses.Caption }, [ShownWords.ContentProperty] = NavigationWords.OpenInFindWindow, IsEnabled = false, [DockPanel.DockProperty] = Dock.Right };
        ShownWords.Set(FindWindow, ToolTip.TipProperty, ActionTips.Named(NavigationWords.OpenInFindWindow, KeyWords.Of(FindWindowKey)));
        ToolTip.SetShowOnDisabled(FindWindow, true);
        FindWindow.Click += (_, _) => KeepInFindWindow();
        top.Children.Add(new DockPanel { Margin = new Thickness(Gaps.Wide, 0, Gaps.Wide, Gaps.Small), Children = { FindWindow, status } });
        results = new ListBox
        {
            Classes = { StyleClasses.Tree },
            Height = ResultsHeight,
        };
        ReusedRows.Use(results, () => Row(ligatures));
        results.SelectionChanged += (_, _) => ShowPreview();
        ListKeys.Act(results, new ListActs { Open = Choose });
        ToolWindows.ListMenus.Attach(results, () => results.SelectedItem is not SearchLine chosen ? [] :
        [
            ToolWindows.ListMenus.Open(NavigationWords.GoTo, Choose),
            ToolWindows.ListMenus.Item(SharedWords.CopyPath, () => IdeErrors.Observe(ToolWindows.ListMenus.CopyAsync(this, chosen.Path))),
            ToolWindows.ListMenus.Item(NavigationWords.CopyLine, () => IdeErrors.Observe(ToolWindows.ListMenus.CopyAsync(this, chosen.LineText.Trim()))),
        ]);
        preview = new CodeEditor { IsReadOnly = true, ReadOnlyWhy = Refusals.PreviewReadOnly.Shown, ShowErrorStripe = false, Height = PreviewHeight, Focusable = false, Ligatures = ligatures };
        // Clear of the frame's rounded foot, which the editor's square corners would cover.
        Border previewFrame = new() { BorderThickness = Hairlines.Above, Padding = new Thickness(0, 0, 0, Gaps.Normal), Child = preview };
        previewFrame.Bind(Border.BorderBrushProperty, previewFrame.GetResourceObservable(TokenKeys.Divider));
        Content = new Border
        {
            Classes = { StyleClasses.Popup },
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
        Dispatcher.UIThread.Post(TakeKeys, DispatcherPriority.Loaded);
        if (query.Text is { Length: > 0 })
        {
            searching = Refresh();
        }
    }

    /// <summary>A match to go to, or null when dismissed.</summary>
    public event EventHandler<SearchMatch?>? Done;

    /// <summary>Whether it is Replace in Files, its replacement row shown, rather than Find in Files. Turned over while it
    /// is open, the query, its options, the scope, the mask and the matches stay as they are, and the replacement typed
    /// comes back with its row.</summary>
    public bool Replacing
    {
        get => replaceRow.IsVisible;
        set
        {
            replaceRow.IsVisible = value;
            ShownWords.Set(title, TextBlock.TextProperty, NameOf(value));
            ShownWords.Set(query, AutomationProperties.NameProperty, NameOf(value));
        }
    }

    private static Words NameOf(bool replacing) => replacing ? NavigationWords.ReplaceInFiles : NavigationWords.FindInFiles;

    /// <summary>Gives the query the keys, all of it chosen, as the popup does as it opens.</summary>
    public void TakeKeys()
    {
        query.Focus();
        query.SelectAll();
    }

    /// <summary>The link that lists the matches found in the Find window, enabled while there are some.</summary>
    public Button FindWindow { get; }

    /// <summary>The matches found, asked for in the Find window by the link or its keys, with the words searched for.</summary>
    public event EventHandler<(string Text, FilesFound Found)>? FindWindowRequested;

    /// <summary>The matches the search shown found.</summary>
    public FilesFound Found => found;

    private void KeepInFindWindow()
    {
        if (found.Matches.Count > 0)
        {
            FindWindowRequested?.Invoke(this, (Text, found));
        }
    }

    /// <summary>Replace All pressed: the query, its replacement, and the files it matched in.</summary>
    public event EventHandler<(SearchQuery Query, string Replacement, FilesFound Found)>? ReplaceRequested;

    /// <summary>The scope or the mask changed, for the window to keep.</summary>
    public event EventHandler<SearchScope>? ScopeChanged;

    public IReadOnlyList<SearchMatch> Matches => found.Matches;

    /// <summary>The rows: the lines the matches are on, each once.</summary>
    public IReadOnlyList<SearchLine> Lines => lines;

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
    private static Words Count(int found) =>
        found >= FileSearch.MostMatches ? NavigationWords.MostMatches(FileSearch.MostMatches) : NavigationWords.Matches(found);

    public string Text
    {
        get => query.Text ?? string.Empty;
        set => query.Text = value;
    }

    public string Replacement
    {
        get => replacement.Text ?? string.Empty;
        set => replacement.Text = value;
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
        if (found.Matches.Count > 0 && shownFor == (Query, Scope))
        {
            ReplaceRequested?.Invoke(this, (Query, Replacement, found));
        }
    }

    /// <summary>The results' height and the preview's with room for them.</summary>
    private const double ResultsHeight = ListRow.Height * 10;

    private const double PreviewHeight = 180;

    /// <summary>The least each gives way to: three results and three lines of the one chosen.</summary>
    private const double LeastResults = ListRow.Height * 3;

    private const double LeastPreview = 60;

    /// <summary>Short of the room its parts take (a short window, which the popup is fitted inside), the results and the
    /// preview give way together, each down to its least, so the popup stays whole above the window's foot.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        Size laid = base.MeasureOverride(availableSize.WithHeight(double.PositiveInfinity));
        double whole = laid.Height + (ResultsHeight - results.Height) + (PreviewHeight - preview.Height);
        double give = Math.Clamp((whole - availableSize.Height) / (ResultsHeight - LeastResults + PreviewHeight - LeastPreview), 0, 1);
        double resultsHeight = Math.Round(ResultsHeight - (give * (ResultsHeight - LeastResults)));
        double previewHeight = Math.Round(PreviewHeight - (give * (PreviewHeight - LeastPreview)));
        // Set only as they change, so a layout pass that finds the same heights invalidates nothing.
        if (resultsHeight != results.Height || previewHeight != preview.Height)
        {
            (results.Height, preview.Height) = (resultsHeight, previewHeight);
        }
        return base.MeasureOverride(availableSize);
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
        Show(FilesFound.None, asked.Text.Length == 0 ? string.Empty : FileSearch.Problem(asked) ?? Scope.Problem() ?? NavigationWords.Searching, null);
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
        kinds.Choice = shown;
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
            Title = NavigationWords.Directory.Shown,
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
            Show(FilesFound.None, string.Empty, (asked, where));
            return;
        }
        if ((FileSearch.Problem(asked) ?? where.Problem()) is { } problem)
        {
            Show(FilesFound.None, problem, (asked, where));
            return;
        }
        Show(FilesFound.None, NavigationWords.Searching, null);
        try
        {
            FilesFound searched = await search(asked, where, cancel.Token);
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            IReadOnlyList<SearchMatch> each = searched.Matches;
            int files = each.Select(m => m.Path).Distinct().Count();
            Words said = each.Count == 0 ? NavigationWords.NothingFound : NavigationWords.FoundIn(Count(each.Count), files);
            Show(searched, searched.StoppedIn(System.IO.Path.GetFileName) is { } stopped ? NavigationWords.FoundStopped(said, stopped) : said, (asked, where));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Show(FilesFound shown, Words said, (SearchQuery Query, SearchScope Scope)? of)
    {
        found = shown;
        shownFor = of;
        lines = SearchLine.Of(shown.Matches);
        results.ItemsSource = lines;
        results.SelectedIndex = lines.Count > 0 ? 0 : -1;
        ShownWords.Set(status, TextBlock.TextProperty, said);
        FindWindow.IsEnabled = shown.Matches.Count > 0;
        ShowPreview();
    }

    private void ShowPreview()
    {
        if (results.SelectedItem is not SearchLine line || textOf(line.Path) is not { } text)
        {
            preview.Show(new TextDocument(), SelectionSet.Single(0), default);
            return;
        }
        TextDocument shown = new(text.ToString());
        preview.Show(shown, SelectionSet.Single(0), default);
        (preview.Highlighter as IDisposable)?.Dispose();
        preview.Highlighter = Languages.Highlighters.For(Languages.Language.ForPath(line.Path), shown, parseOptions(line.Path));
        if (line.Line < shown.Snapshot.LineCount)
        {
            int start = shown.Snapshot.GetLineStart(line.Line);
            preview.SetHighlights([.. line.Matches.Select(m => new TextRange(start + m.Column, m.Length))], 0);
            preview.GoTo(new TextPosition(line.Line, line.First.Column));
        }
    }

    private void Choose()
    {
        if (results.SelectedItem is SearchLine line)
        {
            Done?.Invoke(this, line.First);
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (OptionToggles.Hear(e, (OptionToggles.MatchCase, matchCase), (OptionToggles.Words, words), (OptionToggles.Regex, regex), (OptionToggles.Recursively, recursive)))
        {
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Alt && Kinds.FirstOrDefault(k => k.Key == e.Key) is { Name: not null } chosen)
        {
            ScopeKind = chosen.Kind;
            e.Handled = true;
            return;
        }
        if (Keystroke.From(e) == MaskKey)
        {
            maskOn.IsChecked = maskOn.IsChecked != true;
            if (maskOn.IsChecked == true)
            {
                maskBox.Focus();
            }
            e.Handled = true;
            return;
        }
        if (Keystroke.From(e) == FindWindowKey)
        {
            KeepInFindWindow();
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                Done?.Invoke(this, null);
                e.Handled = true;
                break;
            case Key.Enter when !replacement.IsFocused && !InField():
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
    private static ComboBox Field(System.Collections.IEnumerable items, bool editable) => new ToolDropDown
    {
        ItemsSource = items,
        IsEditable = editable,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>A line's row for whichever line the list hands its container: its text, every match on it in bold, and
    /// where it is. Its runs are its own, kept from one line to the next and added to for a line with more matches.</summary>
    private static Control Row(bool ligatures)
    {
        List<Avalonia.Controls.Documents.Run> runs = [];
        TextBlock text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { StyleClasses.Code },
            FontFeatures = CSharp.Fonts.Features(ligatures),
            Inlines = [],
        };
        TextBlock where = ListRow.DetailText();
        DockPanel row = new() { Height = ListRow.Height, Margin = ListRow.Inset };
        row.Children.Add(where);
        row.Children.Add(text);
        return row.Showing<SearchLine>(line =>
        {
            string whole = line.LineText;
            int indent = whole.Length - whole.TrimStart().Length;
            string trimmed = whole[indent..];
            int used = 0;
            int at = 0;
            foreach (SearchMatch match in line.Matches)
            {
                int start = Math.Clamp(match.Column - indent, at, trimmed.Length);
                int end = Math.Clamp(match.Column + match.Length - indent, start, trimmed.Length);
                Piece(trimmed[at..start], bold: false);
                Piece(trimmed[start..end], bold: true);
                at = end;
            }
            Piece(trimmed[at..], bold: false);
            // The text shows the first of the runs, as many as the line uses.
            Avalonia.Controls.Documents.InlineCollection inlines = text.Inlines!;
            while (inlines.Count > used)
            {
                inlines.RemoveAt(inlines.Count - 1);
            }
            while (inlines.Count < used)
            {
                inlines.Add(runs[inlines.Count]);
            }
            where.Text = $"{line.Name} {line.Line + 1}";
            ToolTip.SetTip(row, line.Path);

            void Piece(string said, bool bold)
            {
                if (used == runs.Count)
                {
                    runs.Add(new Avalonia.Controls.Documents.Run());
                }
                Avalonia.Controls.Documents.Run run = runs[used++];
                run.Text = said;
                if (bold)
                {
                    run.FontWeight = FontWeight.Bold;
                }
                else
                {
                    run.ClearValue(Avalonia.Controls.Documents.TextElement.FontWeightProperty);
                }
            }
        });
    }
}
