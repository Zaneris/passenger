using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Passenger.App.Tables;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>Find and replace in the open file. Matches are found as the query is typed and drawn in
/// the editor; the one nearest after the caret is selected, and the count says which of how many. A regex too slow for
/// the UI thread's moment is searched again off it, the count saying so meanwhile. While a table lies over the editor,
/// its cells are searched instead (read-only: replacing is the Text view's); a read-only file's replace row is greyed.
/// Its options are Find in Files' (<see cref="OptionToggles"/>), their keys heard while it has the keys.</summary>
public partial class FindBar : UserControl
{
    /// <summary>The bar's room round its rows.</summary>
    public static readonly Thickness BarPadding = new(Gaps.Normal, Gaps.Small);

    /// <summary>The room the query's option toggles take over its right end, and a step clear of them.</summary>
    private const double OptionsRoom = 128;

    /// <summary>The query's room: its words where every search field's start, clear at its right of the toggles over it.</summary>
    public static readonly Thickness QueryPadding = new(FieldInsets.Padding.Left, FieldInsets.Padding.Top, OptionsRoom, FieldInsets.Padding.Bottom);

    /// <summary>The toggles a tight step in from the field's right edge.</summary>
    public static readonly Thickness OptionsMargin = new(0, 0, Gaps.Tight, 0);

    /// <summary>The query's and the replacement's width where the bar has room for it.</summary>
    public const double FieldWidth = 380;

    /// <summary>The room a field keeps to type in, a few words', however narrow the bar: what follows it takes a row below.</summary>
    public const double TypingRoom = 120;

    public static readonly Thickness CountMargin = new(0, 0, Gaps.Normal, 0);

    /// <summary>A row's parts laid out by the shared row that wraps (ToolFlow): its field first, <see cref="FieldWidth"/> wide
    /// where there is room and down to its typing room where not, what follows it after it, or on a row below.</summary>
    private static void Flowing(Panel row, Control field, double typing, params Control[] after)
    {
        row.Children.Clear();
        ToolWindows.ToolFlow.SetFills(field, new ToolWindows.ToolLayout.Fills(typing, FieldWidth));
        ToolWindows.ToolFlow flow = new() { Spacing = Gaps.Normal, Children = { field } };
        flow.Children.AddRange(after);
        row.Children.Add(flow);
    }

    private const string Searching = "Searching…";

    private CodeEditor? editor;

    /// <summary>An editor beside the one searched whose own matches are marked too (a diff's other side), or null.</summary>
    private CodeEditor? beside;
    private TextDocument? document;
    private SearchResult result = SearchResult.None;

    /// <summary>The text <see cref="result"/> was found in, which an edit leaves behind until it is searched again.</summary>
    private TextSnapshot? searched;

    /// <summary>Whether a search off the UI thread is out, and whether the text changed again since it began.</summary>
    private bool finding;
    private bool findAgain;

    /// <summary>Whether the search off the UI thread selects the match it lands on: one typed, which stopped on it.</summary>
    private bool jumpApart;
    private int current = -1;
    private CellGrid? grid;

    /// <summary>Why the file shown takes no replacement (it is read-only), or null.</summary>
    private string? unchangeable;
    private TableSearch cells = TableSearch.None;
    private CancellationTokenSource? searching;

    /// <summary>The stretch In Selection keeps the search inside, carried through the text's changes, while it is on.</summary>
    private AnchoredRange? scope;

    /// <summary>Whether In Selection is being turned off by the bar itself (another file, the bar closed), searching nothing.</summary>
    private bool unscoping;

    /// <summary>What Replace All came to, said in the count's place while the text is the one it left (<see cref="toldOf"/>)
    /// and the search the one it ran, so the search its own edits bring does not wipe it; null when it stands no longer.</summary>
    private string? told;
    private TextSnapshot? toldOf;

    public FindBar()
    {
        InitializeComponent();
        // The bar is one the keys go through, from the query to its buttons.
        ToolButtons.Give(PreviousButton, IconKeys.ArrowUp, "Previous occurrence", keys: true);
        ToolButtons.Give(NextButton, IconKeys.ArrowDown, "Next occurrence", keys: true);
        ToolButtons.Give(CloseButton, IconKeys.Close, "Close", keys: true, glyph: true);
        // In a narrow pane (a console, a diff) each field keeps room to type in, what follows it taking a row below.
        Flowing(QueryRow, QueryRoom, QueryPadding.Left + TypingRoom + QueryPadding.Right, CountRow, ToolWindows.ToolFlow.Spring(), CloseButton);
        Flowing(ReplaceRow, ReplaceWith, FieldInsets.Padding.Left + TypingRoom + FieldInsets.Padding.Right, ReplaceButtons);
        IsVisible = false;
        OptionToggles.Give(MatchCase, OptionToggles.MatchCase);
        OptionToggles.Give(WholeWords, OptionToggles.Words);
        OptionToggles.Give(UseRegex, OptionToggles.Regex);
        OptionToggles.Give(InSelection, OptionToggles.InSelection);
        // The arrows do what Find Previous and Find Next do, so their tips name those actions' keys.
        ActionTips.Set(PreviousButton, AppActions.FindPrevious, "Previous occurrence");
        ActionTips.Set(NextButton, AppActions.FindNext, "Next occurrence");
        // On the way down, so an option's key never types into the field it is pressed in.
        AddHandler(KeyDownEvent, (_, e) => OptionToggles.Hear(e, (OptionToggles.MatchCase, MatchCase), (OptionToggles.Words, WholeWords), (OptionToggles.Regex, UseRegex),
            (OptionToggles.InSelection, InSelection)), RoutingStrategies.Tunnel);
        Query.TextChanged += (_, _) => SearchAnew();
        MatchCase.IsCheckedChanged += (_, _) => SearchAnew();
        WholeWords.IsCheckedChanged += (_, _) => SearchAnew();
        UseRegex.IsCheckedChanged += (_, _) => SearchAnew();
        InSelection.IsCheckedChanged += (_, _) => Scope(InSelection.IsChecked == true);
        NextButton.Click += (_, _) => Next();
        PreviousButton.Click += (_, _) => Previous();
        CloseButton.Click += (_, _) => Close();
        ReplaceButton.Click += (_, _) => ReplaceCurrent();
        ReplaceAllButton.Click += (_, _) => ReplaceAll();
        // On the way down, before the text box takes Enter and Escape for itself.
        Query.AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Tunnel);
        ReplaceBoxes.Take(ReplaceWith);
        ReplaceWith.AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>What the count beside the arrows says: "2/5", "0 results", a bad regex's error, what Replace All came to.</summary>
    public string CountText => Count.Text ?? string.Empty;

    public SearchQuery CurrentQuery => new(Query.Text ?? string.Empty, MatchCase.IsChecked == true, WholeWords.IsChecked == true, UseRegex.IsChecked == true);

    /// <summary>Follows whichever document the editor shows, or the table over it when there is one; called when a tab is
    /// activated and when a table file switches between its table and its text. A file shown read-only says why
    /// (<paramref name="readOnly"/>) on its replace row, greyed.</summary>
    public void Attach(CodeEditor shown, CellGrid? table = null, string? readOnly = null) => Attach(shown, null, table, readOnly);

    /// <summary>Follows an editor as <see cref="Attach(CodeEditor, CellGrid?, string?)"/> does, the query's matches in
    /// <paramref name="next"/> (a diff's other side) marked too, unsearched.</summary>
    public void Attach(CodeEditor shown, CodeEditor? next, CellGrid? table = null, string? readOnly = null)
    {
        beside?.SetHighlights([], -1);
        beside = next;
        if (document != shown.Document)
        {
            Unscope();
        }
        editor = shown;
        unchangeable = readOnly;
        Follow(table);
        if (document is not null)
        {
            document.Changed -= OnDocumentChanged;
        }
        document = shown.Document;
        document.Changed += OnDocumentChanged;
        if (IsVisible)
        {
            Search(jump: false);
        }
    }

    /// <summary>Hides the bar and lets go of the editor and its text, as its group's last tab closes: nothing the file
    /// holds keeps the bar, or the group around it, alive.</summary>
    public void Detach()
    {
        IsVisible = false;
        Unscope();
        if (document is not null)
        {
            document.Changed -= OnDocumentChanged;
        }
        document = null;
        editor = null;
        beside?.SetHighlights([], -1);
        beside = null;
        Untell();
        Follow(null);
        result = SearchResult.None;
        searched = null;
        current = -1;
    }

    private void Follow(CellGrid? table)
    {
        if (grid is not null)
        {
            grid.TableChanged -= OnTableChanged;
            grid.SetMatches([], -1);
        }
        searching?.Cancel();
        grid = table;
        cells = TableSearch.None;
        if (grid is not null)
        {
            grid.TableChanged += OnTableChanged;
        }
        ReplaceWith.IsEnabled = ReplaceButton.IsEnabled = ReplaceAllButton.IsEnabled = Replaces;
        ToolTip.SetTip(ReplaceRow, unchangeable ?? (grid is null ? null : "Show the file's Text to replace in it"));
    }

    /// <summary>Opens the bar (with the replace row if <paramref name="replace"/>), its query the
    /// selection when that is on one line, or the word under the caret. Refused to an agent's action, as its search would
    /// move the person's selection and its box take their keys (<see cref="AgentAsks.Popup"/>).</summary>
    public void Open(bool replace) => Open(replace, seeded: true);

    /// <summary>Incremental Search: the bar opened with nothing in it, its matches found from the caret on as the query is
    /// typed.</summary>
    public void OpenEmpty()
    {
        Query.Text = string.Empty;
        Open(replace: false, seeded: false);
    }

    /// <summary>Find in Selection: In Selection turned over, the bar opened (keeping its query) where it was closed.</summary>
    public void ToggleInSelection()
    {
        if (editor is null || grid is not null)
        {
            return;
        }
        if (!IsVisible)
        {
            Open(replace: false, seeded: false);
        }
        InSelection.IsChecked = InSelection.IsChecked != true;
    }

    /// <summary>Find Word at Caret and its previous: the selection on one line, or the word at the caret, looked for again
    /// from where it stands, the bar shown with it as its query and the editor keeping the keys, so Find Next and Find
    /// Previous go on with it.</summary>
    public void FindAtCaret(bool forward)
    {
        if (editor is null || grid is not null || Seeding(editor) is not { } at)
        {
            return;
        }
        AgentAsks.Popup();
        IsVisible = true;
        // The copy at the caret chosen, the one the search lands on and the step goes on from.
        editor.Select(at);
        string seed = editor.Document.Snapshot.GetText(at);
        if (seed != Query.Text)
        {
            Query.Text = seed;
        }
        Step(forward);
    }

    private void Open(bool replace, bool seeded)
    {
        if (editor is null)
        {
            return;
        }
        AgentAsks.Popup();
        IsVisible = true;
        ReplaceRow.IsVisible = replace;
        // A table's chosen cell seeds nothing: the query typed last is what is looked for again.
        string? seed = grid is null && seeded ? Seed(editor) : null;
        if (seed is not null && seed != Query.Text)
        {
            Query.Text = seed;
        }
        else
        {
            Search(jump: false);
        }
        // A control shown this moment is not laid out yet and refuses the focus; it takes it once it is.
        Dispatcher.UIThread.Post(() =>
        {
            Query.Focus();
            Query.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The selection when it is on one line, or the word under the caret.</summary>
    private static string? Seed(CodeEditor editor) => Seeding(editor) is { } range ? editor.Document.Snapshot.GetText(range) : null;

    /// <summary>Where <see cref="Seed"/> stands in the text, or null for neither.</summary>
    private static TextRange? Seeding(CodeEditor editor)
    {
        TextSnapshot text = editor.Document.Snapshot;
        Selection primary = editor.Selections.Primary;
        if (!primary.IsEmpty && text.GetLineFromOffset(primary.Start) == text.GetLineFromOffset(primary.End))
        {
            return primary.Range;
        }
        if (primary.IsEmpty)
        {
            TextRange word = TextNavigation.WordAt(text, primary.Caret);
            if (word.Length > 0 && WordCharacters.IsWord(text[word.Start]))
            {
                return word;
            }
        }
        return null;
    }

    public void Close()
    {
        IsVisible = false;
        Unscope();
        Untell();
        searching?.Cancel();
        editor?.SetHighlights([], -1);
        beside?.SetHighlights([], -1);
        if (grid is not null)
        {
            grid.SetMatches([], -1);
            grid.Focus();
        }
        else
        {
            editor?.Focus();
        }
    }

    public void Next() => Step(forward: true);

    public void Previous() => Step(forward: false);

    /// <summary>In Selection turned on or off: on, the stretch it keeps the search inside is the selection as it stands, or
    /// the caret's line where nothing is chosen.</summary>
    private void Scope(bool on)
    {
        scope?.Remove();
        scope = null;
        if (on && editor is not null && document is not null)
        {
            Selection primary = editor.Selections.Primary;
            TextSnapshot text = document.Snapshot;
            LineSpan line = text.GetLine(text.GetLineFromOffset(primary.Caret));
            scope = document.MarkAt(primary.IsEmpty ? new TextRange(line.Start, line.Length) : primary.Range);
        }
        if (!unscoping)
        {
            SearchAnew();
        }
    }

    /// <summary>In Selection turned off by the bar itself, as it closes or follows another file, searching nothing.</summary>
    private void Unscope()
    {
        scope?.Remove();
        scope = null;
        // Matches kept from while it was on are the stretch's alone: what acts on a match next searches again.
        searched = null;
        unscoping = true;
        InSelection.IsChecked = false;
        unscoping = false;
    }

    /// <summary>A search's matches, those inside In Selection's stretch alone while it is on.</summary>
    private SearchResult Scoped(SearchResult found) => scope is null ? found : found.Within(scope.Range);

    /// <summary>The search changed (its query or an option): what Replace All came to no longer stands.</summary>
    private void SearchAnew()
    {
        Untell();
        Search(jump: true);
    }

    private void Search(bool jump)
    {
        if (editor is null || !IsVisible)
        {
            return;
        }
        if (grid is not null)
        {
            editor.SetHighlights([], -1);
            IdeErrors.Observe(SearchCells(grid, jump));
            return;
        }
        TextSnapshot text = editor.Document.Snapshot;
        SearchResult found = TextSearch.FindAll(text, CurrentQuery, TextSearch.AtOnce);
        Found(found, text, jump);
        if (found.Stopped)
        {
            jumpApart |= jump;
            SearchApart();
        }
    }

    /// <summary>Shows what a search of a text found, the match at or after the caret the current one.</summary>
    private void Found(SearchResult found, TextSnapshot text, bool jump)
    {
        result = Scoped(found);
        searched = text;
        current = -1;
        if (editor is not null && result.Matches.Count > 0)
        {
            Selection primary = editor.Selections.Primary;
            current = result.NextFrom(primary.Start);
            if (jump)
            {
                editor.Select(result.Matches[current]);
            }
        }
        Show();
    }

    /// <summary>Searches the table's cells off the UI thread, a newer search or table ending this one's; the match at or
    /// after the chosen cell is the current one.</summary>
    private async Task SearchCells(CellGrid shown, bool jump)
    {
        searching?.Cancel();
        CancellationTokenSource cancel = searching = new CancellationTokenSource();
        SearchQuery query = CurrentQuery;
        TableSearch found = shown.Table is { } table && !query.IsEmpty
            ? await BackgroundWork.Run(() => TableSearch.Find(table, query, cancel.Token))
            : TableSearch.None;
        if (cancel.IsCancellationRequested || shown != grid || !IsVisible)
        {
            return;
        }
        cells = found;
        current = cells.NextFrom(shown.Caret, including: true);
        if (jump && current >= 0)
        {
            shown.Choose(cells.Matches[current], cells.Matches[current]);
        }
        ShowCells();
    }

    private void StepCells(CellGrid shown, bool forward)
    {
        if (cells.Matches.Count == 0)
        {
            return;
        }
        current = forward ? cells.NextFrom(shown.Caret, including: false) : cells.PreviousFrom(shown.Caret);
        shown.Choose(cells.Matches[current], cells.Matches[current]);
        ShowCells();
    }

    private void ShowCells()
    {
        grid?.SetMatches(cells.Matches, current);
        Count.Text = Counted(cells.Problem, cells.Matches.Count, cells.Truncated);
    }

    private void OnTableChanged(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            Search(jump: false);
        }
    }

    private void Step(bool forward)
    {
        Untell();
        if (grid is not null)
        {
            StepCells(grid, forward);
            return;
        }
        FindNow();
        if (editor is null || result.Matches.Count == 0)
        {
            return;
        }
        Selection primary = editor.Selections.Primary;
        current = forward ? result.NextFrom(primary.End) : result.PreviousFrom(primary.Start);
        // An empty match (a line's start) the caret already stands on is passed, or Next would stay on it.
        if (forward && primary.IsEmpty && result.Matches[current] is { Length: 0 } here && here.Start == primary.End)
        {
            current = (current + 1) % result.Matches.Count;
        }
        editor.Select(result.Matches[current]);
        Show();
    }

    /// <summary>Whether the text shown takes a replacement: neither a table's cells nor a read-only file's.</summary>
    private bool Replaces => grid is null && unchangeable is null;

    private void ReplaceCurrent()
    {
        if (editor is null || !Replaces)
        {
            return;
        }
        FindNow();
        if (current < 0 || current >= result.Matches.Count)
        {
            return;
        }
        TextRange match = result.Matches[current];
        Selection primary = editor.Selections.Primary;
        if (primary.Start != match.Start || primary.End != match.End)
        {
            editor.Select(match);
            return;
        }
        // Found again for the text as it stands (FindNow), so its matches and groups are this text's.
        string replacement = result.Replacement(current, ReplaceWith.Text ?? string.Empty);
        int after = match.Start + replacement.Length;
        editor.ApplyEdits([new TextEdit(match.Start, match.Length, replacement)], SelectionSet.Single(after));
        Step(forward: true);
    }

    /// <summary>Replaces every match, never only some: a regex too slow for the UI thread's moment is searched off it, and
    /// its matches replaced once found if the text is still the one searched.</summary>
    private void ReplaceAll() => ReplaceAll(CurrentQuery, ReplaceWith.Text ?? string.Empty);

    private void ReplaceAll(SearchQuery query, string replacement)
    {
        if (editor is null || !Replaces)
        {
            return;
        }
        Untell();
        TextSnapshot text = editor.Document.Snapshot;
        SearchResult found = Scoped(TextSearch.FindAll(text, query, TextSearch.AtOnce, int.MaxValue));
        if (found.Stopped)
        {
            IdeErrors.Observe(ReplaceAllApartAsync(editor, text, query, replacement));
            return;
        }
        (IReadOnlyList<TextEdit> edits, string? why) = Replacing(found, replacement);
        Replace(editor, edits, why);
    }

    /// <summary>Every match's replacement as an edit, or why none is made: the text it makes would be too long, said
    /// before any is made.</summary>
    private static (IReadOnlyList<TextEdit> Edits, string? Why) Replacing(SearchResult found, string replacement) =>
        Refusals.TooLongReplaced(found, replacement) is { } why ? ([], why) : (found.Replacements(replacement), null);

    private async Task ReplaceAllApartAsync(CodeEditor shown, TextSnapshot text, SearchQuery query, string replacement)
    {
        Count.Text = Searching;
        TextRange? within = scope?.Range;
        // Each match's groups are found again where it stood, as long as the search took: off the UI thread too.
        (SearchResult found, (IReadOnlyList<TextEdit> edits, string? why)) = await BackgroundWork.Run(() =>
        {
            SearchResult all = TextSearch.FindAll(text, query, TextSearch.Apart, int.MaxValue);
            SearchResult searched = within is { } stretch ? all.Within(stretch) : all;
            return (searched, searched.Stopped ? ([], null) : Replacing(searched, replacement));
        });
        if (editor != shown || !IsVisible || !Replaces)
        {
            return;
        }
        if (!ReferenceEquals(shown.Document.Snapshot, text))
        {
            // Edited while it was searched: the text as it stands is replaced in.
            ReplaceAll(query, replacement);
            return;
        }
        if (found.Stopped)
        {
            Tell($"{found.Problem} Nothing was replaced.", shown, Urgency.Assertive);
            return;
        }
        Replace(shown, edits, why);
    }

    private void Replace(CodeEditor shown, IReadOnlyList<TextEdit> edits, string? why)
    {
        if (why is not null)
        {
            Tell($"Nothing was replaced: {why}.", shown, Urgency.Assertive);
            return;
        }
        shown.ApplyEdits(edits);
        Tell($"Replaced {Numbers.Said(edits.Count)}", shown, Urgency.Polite);
    }

    /// <summary>Says what Replace All came to in the count's place, where it stays until the search or the text changes,
    /// and tells a screen reader, a refusal at once.</summary>
    private void Tell(string said, CodeEditor shown, Urgency urgency)
    {
        told = said;
        toldOf = shown.Document.Snapshot;
        Count.Text = said;
        Announcer.Of(TopLevel.GetTopLevel(this))?.Tell(said, urgency);
    }

    /// <summary>What Replace All came to while it stands, or null once the text it left is no longer the one shown.</summary>
    private string? Told()
    {
        if (told is not null && !ReferenceEquals(editor?.Document.Snapshot, toldOf))
        {
            Untell();
        }
        return told;
    }

    private void Untell()
    {
        told = null;
        toldOf = null;
    }

    private void Show()
    {
        editor?.SetHighlights(result.Matches, current);
        beside?.SetHighlights(TextSearch.FindAll(beside.Document.Snapshot, CurrentQuery, TextSearch.AtOnce).Matches, -1);
        ShowCount();
    }

    /// <summary>The matches marked in the editor beside the one searched, as last shown.</summary>
    public int MarkedBeside => beside?.Highlights.Count ?? 0;

    /// <summary>The count, what Replace All came to while it stands, or that a regex too slow for the UI thread is being
    /// searched off it.</summary>
    private void ShowCount() =>
        Count.Text = Told() ?? (result.Stopped && finding ? Searching : Counted(result.Problem, result.Matches.Count, result.Truncated));

    private string Counted(string? problem, int count, bool truncated) =>
        problem ?? (string.IsNullOrEmpty(Query.Text) ? string.Empty
            : count == 0 ? "0 results"
            : $"{current + 1}/{Numbers.Said(count)}{(truncated ? "+" : string.Empty)}");

    /// <summary>The text changed under an open bar: its matches move with the edit (the editor's highlights are carried by
    /// the text), and the text as it stands is searched off the UI thread, since a search reads the whole file and a key
    /// should cost what it changed. A table's are searched as its own change says.</summary>
    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (!IsVisible || editor is null)
        {
            return;
        }
        if (grid is not null)
        {
            Search(jump: false);
            return;
        }
        SearchApart();
    }

    /// <summary>Starts the search off the UI thread, or has the one out search again once it lands.</summary>
    private void SearchApart()
    {
        if (finding)
        {
            findAgain = true;
            return;
        }
        finding = true;
        if (result.Stopped)
        {
            ShowCount();
        }
        IdeErrors.Observe(FindAgainAsync());
    }

    /// <summary>Searches the text as it stands off the UI thread, again while it changed meanwhile, one search at a time;
    /// a search lands only for the text and query still shown, and not when one on the UI thread found them all first.</summary>
    private async Task FindAgainAsync()
    {
        try
        {
            do
            {
                findAgain = false;
                if (editor is not { } shown || !IsVisible || grid is not null)
                {
                    return;
                }
                TextSnapshot text = shown.Document.Snapshot;
                SearchQuery query = CurrentQuery;
                SearchResult found = await BackgroundWork.Run(() => TextSearch.FindAll(text, query, TextSearch.Apart));
                if (!findAgain && editor == shown && IsVisible && grid is null && ReferenceEquals(shown.Document.Snapshot, text)
                    && !(ReferenceEquals(searched, text) && !result.Stopped) && query == CurrentQuery)
                {
                    Found(found, text, jump: jumpApart);
                    jumpApart = false;
                }
            }
            while (findAgain);
        }
        finally
        {
            finding = false;
            jumpApart = false;
            if (result.Stopped && IsVisible && grid is null)
            {
                // No longer searching: a regex that stopped off the UI thread too says so.
                ShowCount();
            }
        }
    }

    /// <summary>Searches the text again on the UI thread unless the matches are its own: what acts on a match (the next one,
    /// a replace) acts on the text as it stands, even while a search off it is still out.</summary>
    private void FindNow()
    {
        if (editor is not null && !ReferenceEquals(searched, editor.Document.Snapshot))
        {
            Search(jump: false);
        }
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when sender == ReplaceWith && ReplaceBoxes.Breaks(e):
                // The Replace box's line break, which its own handler puts in.
                break;
            case Key.Enter when sender == ReplaceWith && Replaces:
                ReplaceCurrent();
                e.Handled = true;
                break;
            case Key.Enter:
                Step(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }
}
