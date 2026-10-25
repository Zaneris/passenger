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
/// the editor; the one nearest after the caret is selected, and the count says which of how many. While a table lies
/// over the editor, its cells are searched instead (read-only: replacing is the Text view's).</summary>
public partial class FindBar : UserControl
{
    private CodeEditor? editor;
    private TextDocument? document;
    private SearchResult result = SearchResult.None;

    /// <summary>The text <see cref="result"/> was found in, which an edit leaves behind until it is searched again.</summary>
    private TextSnapshot? searched;

    /// <summary>Whether a search off the UI thread is out, and whether the text changed again since it began.</summary>
    private bool finding;
    private bool findAgain;
    private int current = -1;
    private CellGrid? grid;
    private TableSearch cells = TableSearch.None;
    private CancellationTokenSource? searching;

    public FindBar()
    {
        InitializeComponent();
        IsVisible = false;
        Query.TextChanged += (_, _) => Search(jump: true);
        MatchCase.IsCheckedChanged += (_, _) => Search(jump: true);
        WholeWords.IsCheckedChanged += (_, _) => Search(jump: true);
        UseRegex.IsCheckedChanged += (_, _) => Search(jump: true);
        NextButton.Click += (_, _) => Next();
        PreviousButton.Click += (_, _) => Previous();
        CloseButton.Click += (_, _) => Close();
        ReplaceButton.Click += (_, _) => ReplaceCurrent();
        ReplaceAllButton.Click += (_, _) => ReplaceAll();
        // On the way down, before the text box takes Enter and Escape for itself.
        Query.AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Tunnel);
        ReplaceWith.AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>What the count beside the arrows says: "2/5", "0 results", a bad regex's error.</summary>
    public string CountText => Count.Text ?? string.Empty;

    public SearchQuery CurrentQuery => new(Query.Text ?? string.Empty, MatchCase.IsChecked == true, WholeWords.IsChecked == true, UseRegex.IsChecked == true);

    /// <summary>Follows whichever document the editor shows, or the table over it when there is one; called when a tab is
    /// activated and when a table file switches between its table and its text.</summary>
    public void Attach(CodeEditor shown, CellGrid? table = null)
    {
        editor = shown;
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
        if (document is not null)
        {
            document.Changed -= OnDocumentChanged;
        }
        document = null;
        editor = null;
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
        ReplaceWith.IsEnabled = ReplaceButton.IsEnabled = ReplaceAllButton.IsEnabled = grid is null;
        ToolTip.SetTip(ReplaceRow, grid is null ? null : "Show the file's Text to replace in it");
    }

    /// <summary>Opens the bar (with the replace row if <paramref name="replace"/>), its query the
    /// selection when that is on one line, or the word under the caret.</summary>
    public void Open(bool replace)
    {
        if (editor is null)
        {
            return;
        }
        IsVisible = true;
        ReplaceRow.IsVisible = replace;
        // A table's chosen cell seeds nothing: the query typed last is what is looked for again.
        string? seed = grid is null ? Seed(editor) : null;
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
    private static string? Seed(CodeEditor editor)
    {
        TextSnapshot text = editor.Document.Snapshot;
        Selection primary = editor.Selections.Primary;
        string? seed = null;
        if (!primary.IsEmpty && text.GetLineFromOffset(primary.Start) == text.GetLineFromOffset(primary.End))
        {
            seed = text.GetText(primary.Range);
        }
        else if (primary.IsEmpty)
        {
            TextRange word = TextNavigation.WordAt(text, primary.Caret);
            if (word.Length > 0 && TextNavigation.IsWordChar(text[word.Start]))
            {
                seed = text.GetText(word);
            }
        }
        return seed;
    }

    public void Close()
    {
        IsVisible = false;
        searching?.Cancel();
        editor?.SetHighlights([], -1);
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
        Found(TextSearch.FindAll(text, CurrentQuery), text, jump);
    }

    /// <summary>Shows what a search of a text found, the match at or after the caret the current one.</summary>
    private void Found(SearchResult found, TextSnapshot text, bool jump)
    {
        result = found;
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

    private void ReplaceCurrent()
    {
        if (editor is null || grid is not null)
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

    private void ReplaceAll()
    {
        if (editor is null || grid is not null)
        {
            return;
        }
        IReadOnlyList<TextEdit> edits = TextSearch.FindAll(editor.Document.Snapshot, CurrentQuery, int.MaxValue).Replacements(ReplaceWith.Text ?? string.Empty);
        editor.ApplyEdits(edits);
        Count.Text = $"Replaced {edits.Count}";
    }

    private void Show()
    {
        editor?.SetHighlights(result.Matches, current);
        Count.Text = Counted(result.Problem, result.Matches.Count, result.Truncated);
    }

    private string Counted(string? problem, int count, bool truncated) =>
        problem ?? (string.IsNullOrEmpty(Query.Text) ? string.Empty
            : count == 0 ? "0 results"
            : $"{current + 1}/{count}{(truncated ? "+" : string.Empty)}");

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
        if (finding)
        {
            findAgain = true;
            return;
        }
        finding = true;
        IdeErrors.Observe(FindAgainAsync());
    }

    /// <summary>Searches the text as it stands off the UI thread, again while it changed meanwhile, one search at a time;
    /// a search lands only for the text and query still shown, and not when one on the UI thread found them first.</summary>
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
                SearchResult found = await BackgroundWork.Run(() => TextSearch.FindAll(text, query));
                if (!findAgain && editor == shown && IsVisible && grid is null && ReferenceEquals(shown.Document.Snapshot, text)
                    && !ReferenceEquals(searched, text) && query == CurrentQuery)
                {
                    Found(found, text, jump: false);
                }
            }
            while (findAgain);
        }
        finally
        {
            finding = false;
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
            case Key.Enter when sender == ReplaceWith && grid is null:
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
