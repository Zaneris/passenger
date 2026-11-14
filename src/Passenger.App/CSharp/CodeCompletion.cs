using Avalonia.Controls;
using Passenger.App.Navigation;
using Passenger.App.Web;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Completion as you type: the list a language makes for the caret (its server's, or its own), narrowed as the
/// name is typed, the chosen item's documentation beside it, and the item put in on Enter, Tab or a commit character;
/// with it the live and postfix templates, Surround With and Complete Current Statement.</summary>
internal sealed partial class CodeCompletion
{
    private readonly AssistContext context;
    private readonly PopupPlacement popups;
    private readonly CompletionPopup completion = new();
    private readonly InfoCard itemCard = new();
    private readonly Dictionary<CompletionItem, LiveTemplate> templatesOffered = [];
    private readonly Dictionary<CompletionItem, (PostfixTemplate Template, PostfixTarget Target)> postfixOffered = [];

    /// <summary>The templates and scope the live templates offered were made for: a list asked again where both stand
    /// offers the items made before.</summary>
    private (TemplateSet Set, TemplateScope Scope)? templatesFor;
    private CancellationTokenSource? completing;
    private CancellationTokenSource? describing;
    private Completion? open;

    public CodeCompletion(AssistContext context, PopupPlacement popups)
    {
        this.context = context;
        this.popups = popups;
        popups.Add(completion);
        popups.Add(itemCard);
        completion.List.DoubleTapped += (_, _) => IdeErrors.Observe(Commit());
        completion.SelectedChanged += (_, _) => IdeErrors.Observe(DescribeSelected());
        completion.Swatch = item => open?.Own?.Swatch(item);
    }

    /// <summary>The list closed, however it closed: what a language kept of it goes.</summary>
    public event EventHandler? Closed;

    private CodeEditor Editor => context.Editor;

    public bool IsOpen => completion.IsVisible;

    /// <summary>What the card beside the completion list says about the chosen item while it shows, null otherwise.</summary>
    public string? ItemDocumentation => itemCard.IsVisible ? itemCard.Text : null;

    /// <summary>The completion list's labels, in order, while it shows.</summary>
    public IReadOnlyList<string> Labels => completion.IsVisible ? [.. completion.Rows.Select(r => r.Item.Label)] : [];

    /// <summary>The completion list's rows, in order, each with the detail it shows on its right, while it shows.</summary>
    public IReadOnlyList<(string Label, string Detail)> Details => completion.IsVisible ? [.. completion.Rows.Select(r => (r.Item.Label, r.Detail))] : [];

    /// <summary>What was typed of the name the completion list shows its rows for, while it shows.</summary>
    public string? NarrowedBy => completion.IsVisible ? filtered?.Typed : null;

    /// <summary>The label of the chosen row, while the list shows.</summary>
    public string? Chosen => completion.IsVisible ? completion.Selected?.Item.Label : null;

    /// <summary>The controls showing the completion list's rows on screen, while it shows.</summary>
    public IReadOnlyList<Control> RowControls => completion.IsVisible ? RowsLaidOut : [];

    /// <summary>The controls the completion list has made for its rows, shown or not.</summary>
    public IReadOnlyList<Control> RowsLaidOut => [.. completion.List.GetRealizedContainers().OfType<ListBoxItem>().Select(row => row.Presenter?.Child).OfType<Control>()];

    /// <summary>The keys the open list takes before the editor does (Up, Down, Enter, Tab, Escape), and Tab's template
    /// after its abbreviation.</summary>
    public bool TakeKey(string action)
    {
        if (open is not null && action is EditorActions.Up or EditorActions.Down or EditorActions.PageUp or EditorActions.PageDown
            or EditorActions.NewLine or EditorActions.Indent)
        {
            // From the rows of the name as typed, whose filter landing later would have chosen its first row again.
            FilterNow();
        }
        if (!completion.IsVisible && open is { Coming: true } && action == EditorActions.Escape)
        {
            // A list showing no row while it waits for more is the person's to dismiss too.
            Close();
        }
        if (completion.IsVisible)
        {
            switch (action)
            {
                case EditorActions.Up:
                    completion.Move(-1);
                    return true;
                case EditorActions.Down:
                    completion.Move(1);
                    return true;
                case EditorActions.PageUp:
                    completion.Move(-9);
                    return true;
                case EditorActions.PageDown:
                    completion.Move(9);
                    return true;
                case EditorActions.NewLine or EditorActions.Indent:
                    IdeErrors.Observe(Commit());
                    return true;
                case EditorActions.Escape:
                    Close();
                    return true;
            }
        }
        return action == EditorActions.Indent && !Editor.IsInSnippet && TryExpandTemplate();
    }

    /// <summary>What was just typed narrows the open list, commits its item or closes it; then opens what the language
    /// opens for it.</summary>
    public void Typed(string text)
    {
        if (completion.IsVisible || open is { Coming: true })
        {
            if (text.All(IsWordChar))
            {
                Filter();
                if (open is { IsIncomplete: true })
                {
                    // The server listed only some of what fits: what is typed now narrows it to the rest.
                    IdeErrors.Observe(Start(null, incomplete: true));
                }
                return;
            }
            if (CommitsOn(text) is { } end)
            {
                IdeErrors.Observe(CommitOn(text, end));
                return;
            }
            Close();
        }
        context.Languages.Typed(text);
    }

    /// <summary>Where the name typed so far ends, when what was just typed is one of the chosen item's commit
    /// characters (the server's: a dot, a bracket, a semicolon), so the item goes in before it; null otherwise. Space
    /// is not one, and nothing commits before a letter of the name is typed.</summary>
    private int? CommitsOn(string text)
    {
        if (open is null || text.Length != 1 || text == " ")
        {
            return null;
        }
        int end = Editor.Selections.Primary.Caret - 1;
        if (end <= open.Start || end > Editor.Document.Snapshot.Length || Editor.Document.Snapshot[end] != text[0])
        {
            return null;
        }
        // The item chosen among the rows of the name as typed before the character, its filter landed or not.
        FilterNow(end);
        if (open is null || !completion.IsVisible || completion.Selected is not { } row || templatesOffered.ContainsKey(row.Item) || postfixOffered.ContainsKey(row.Item))
        {
            return null;
        }
        return (row.Item.CommitCharacters ?? open.CommitCharacters).Contains(text) ? end : null;
    }

    /// <summary>The chosen item goes in before the commit character typed, and the character then does what it does
    /// after any name: a dot lists members, a bracket shows the signature.</summary>
    private async Task CommitOn(string text, int end)
    {
        using (BackgroundWork.Scope())
        {
            await CommitCore(end);
            context.Languages.Typed(text);
        }
    }

    /// <summary>Filters once the move's input is done: a typed character moves the caret before the editor says what was
    /// typed, and a commit character must find the list still open.</summary>
    public void CaretMoved()
    {
        if (completion.IsVisible || open is { Coming: true })
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Filter);
        }
    }

    /// <summary>The list for the caret, as the shown file's language makes it.</summary>
    public async Task Start(string? trigger, bool incomplete = false)
    {
        using (BackgroundWork.Scope())
        {
            await context.Languages.CompleteAsync(trigger, incomplete);
        }
    }

    /// <summary>A list asked for anew, any still coming let go: what tells it it has been asked for again since.</summary>
    public CancellationTokenSource Asking()
    {
        completing?.Cancel();
        CancellationTokenSource cancel = new();
        completing = cancel;
        return cancel;
    }

    /// <summary>The list laid out once ahead of its first use, with a row of each kind, as its window opens: its first
    /// layout makes, styles and shapes all its rows' controls, which the window's first list would wait for.</summary>
    public void LayOutAhead()
    {
        if (completion.IsVisible)
        {
            return;
        }
        CompletionItemKind[] kinds = Enum.GetValues<CompletionItemKind>();
        completion.LayOutAhead([.. Enumerable.Range(0, 12).Select(i => Row(new CompletionItem
        {
            Label = new string('m', 4 + i * 3),
            Kind = kinds[i % kinds.Length],
            Detail = i % 2 == 0 ? new string('n', 6 + i) : null,
        }))]);
    }

    /// <summary>A list a language made, shown narrowed to what has been typed.</summary>
    public void Open(Completion list)
    {
        templatesOffered.Clear();
        templatesFor = null;
        postfixOffered.Clear();
        open = list;
        lastAsked = null;
        Filter();
    }

    /// <summary>What a language lists ahead of its server's items, merged by label: where the name they complete starts,
    /// the items, and the text after the caret they replace (null for the server's items' own).</summary>
    public readonly record struct OwnItems(int Start, IReadOnlyList<CompletionItem> Items, string? Tail)
    {
        /// <summary>The characters beyond a name's that narrow the list rather than close it (a path's '/').</summary>
        public string? WordCharacters { get; init; }

        /// <summary>Whether a server's item is the same as one of these by its name alone, its label's call parentheses
        /// (Godot's <c>get_tree()</c>, <c>emit(…)</c>) taken off.</summary>
        public bool ByName { get; init; }
    }

    /// <summary>A label as its name alone: <c>get_tree()</c>, <c>emit(…)</c> as get_tree, emit.</summary>
    private static string NameOf(string label) => label.EndsWith(')') && label.IndexOf('(', StringComparison.Ordinal) is > 0 and int open ? label[..open] : label;

    /// <summary>The language server's list at the caret: a language's own items first (<paramref name="own"/>), unless
    /// <paramref name="first"/> lists its own alone; then the server's, the live templates that may go here (not in a web
    /// file) and the postfix templates.</summary>
    public async Task ServerListAsync(LspDocument lsp, string? trigger, bool incomplete, WebDocument? web,
        Func<TextSnapshot, int, CancellationToken, Task<OwnItems?>>? own = null, Func<CancellationToken, Task<bool>>? first = null)
    {
        CancellationTokenSource cancel = Asking();
        try
        {
            if (first is not null && await first(cancel.Token))
            {
                return;
            }
            int caret = Editor.Selections.Primary.Caret;
            TextSnapshot asked = Editor.Document.Snapshot;
            // The server asked first, so it works out its list while the language finds its own items.
            Task<(CompletionList? List, int Version)> listing = lsp.CompletionAsync(caret, trigger, cancel.Token, incomplete);
            Task<OwnItems?> finding = own is null ? Task.FromResult<OwnItems?>(null) : own(asked, caret, cancel.Token);
            await Task.WhenAll(listing, finding);
            OwnItems? named = await finding;
            (CompletionList? list, _) = await listing;
            IReadOnlyList<CompletionItem> served = Served(list?.Items ?? [], named, lsp);
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            Completion? shown = null;
            if (served.Count > 0 || named is not null)
            {
                shown = Made(lsp, list, [served], named, web, asked, caret, list?.IsIncomplete ?? false, before: null);
            }
            else if (list?.Later is null)
            {
                Close();
                return;
            }
            if (list?.Later is not { } coming)
            {
                return;
            }
            Completion? waiting = open;
            Completion? grown = null;
            try
            {
                // The items of namespaces not imported, which the server lists after all the rest, added as they come.
                CompletionList? rest = await coming;
                IReadOnlyList<CompletionItem> later = Served(rest?.Items ?? [], named, lsp);
                if (!cancel.IsCancellationRequested && ReferenceEquals(open, waiting) && later.Count > 0)
                {
                    grown = Made(lsp, list, [served, later], named, web, asked, caret, list.IsIncomplete || rest!.IsIncomplete, shown);
                }
            }
            finally
            {
                // Nothing more can come: a list with no row goes, as one the server sent none for, or will once its rows land.
                shown?.Coming = false;
                if (grown is null && !cancel.IsCancellationRequested && ReferenceEquals(open, waiting)
                    && (shown is null || (!completion.IsVisible && filtered is { } rowless && ReferenceEquals(rowless.List, shown))))
                {
                    Close();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Typed on: the list is asked for again.
        }
        catch (LanguageServerFailed)
        {
            // No list, as with nothing to offer; typing on asks again.
        }
    }

    /// <summary>The server's items a list offers: those a language's own items leave, and that replace the stretch its name does.</summary>
    private static IReadOnlyList<CompletionItem> Served(IReadOnlyList<CompletionItem> items, OwnItems? named, LspDocument lsp)
    {
        if (named is not { } mine)
        {
            return items;
        }
        HashSet<string> labels = [.. mine.Items.Select(i => i.Label)];
        // A server's item that would replace another stretch than the name's could not go in where the name does.
        return [.. items.Where(i => !labels.Contains(mine.ByName ? NameOf(i.Label) : i.Label) && (i.TextEdit is not { } replaced || lsp.OffsetOf(replaced.Range.Start) == mine.Start))];
    }

    /// <summary>The list opened of the server's items asked at <paramref name="caret"/>, its parts in order, a language's
    /// own first and the templates last; one with items added as they came (<paramref name="before"/>) keeps the choice
    /// made in it.</summary>
    private Completion Made(LspDocument lsp, CompletionList? list, IReadOnlyList<CompletionItem>[] parts, OwnItems? named, WebDocument? web,
        TextSnapshot asked, int caret, bool incomplete, Completion? before)
    {
        IReadOnlyList<CompletionItem> served = parts.FirstOrDefault(p => p.Count > 0) ?? parts[0];
        int start = named?.Start ?? (served.FirstOrDefault(i => i.TextEdit is not null)?.TextEdit is { } edit
            ? lsp.OffsetOf(edit.Range.Start)
            : lsp.IsRazor ? RazorWordStart(caret, served) : web is not null ? WebAssist.WebWordStart(web, Editor.Document.Snapshot, caret) : context.WordStart(caret));
        // The live templates that may go here are listed with the server's items, ahead of a keyword they share.
        TemplateScope scope = web is null && context.Syntax is { } syntax ? TemplateScopes.At(syntax, caret).Scope : TemplateScope.None;
        if (templatesFor != (Templates, scope))
        {
            templatesOffered.Clear();
            foreach (LiveTemplate template in Templates.In(scope))
            {
                templatesOffered[new CompletionItem { Label = template.Abbreviation, Kind = CompletionItemKind.Snippet, Detail = template.Description, SortText = "!" + template.Abbreviation }] = template;
            }
            templatesFor = (Templates, scope);
        }
        if (before is null)
        {
            postfixOffered.Clear();
            foreach ((PostfixTemplate postfix, PostfixTarget target) in Templates.PostfixInCompletion ? Postfixes(Math.Min(start, caret), caret) : [])
            {
                postfixOffered[new CompletionItem { Label = postfix.Name, Kind = CompletionItemKind.Snippet, Detail = postfix.Example, SortText = "~" + postfix.Name }] = (postfix, target);
            }
        }
        IReadOnlyList<CompletionItem> ahead = named?.Items ?? [];
        CompletionItem[] offered = [.. templatesOffered.Keys, .. postfixOffered.Keys];
        int listStart = before?.Start ?? Math.Min(start, Editor.Selections.Primary.Caret);
        string listTail = named is { Tail: { } tail } ? tail : web is not null ? Tail(asked, served, caret) : string.Empty;
        string lead = web is not null && start < caret && caret <= asked.Length ? asked.GetText(start, caret - start) : string.Empty;
        open = new Completion(new Joined([ahead, .. parts, offered]), listStart, list?.ItemDefaults?.CommitCharacters ?? [], incomplete)
        {
            Tail = listTail,
            WordCharacters = named?.WordCharacters ?? string.Empty,
            Lead = lead,
            Before = before,
            Coming = before is null && list?.Later is not null,
        };
        Filter();
        return open;
    }

    /// <summary>A list's parts read as one list in their order, so a list of every type in reach is made without a copy.</summary>
    private sealed class Joined(IReadOnlyList<CompletionItem>[] parts) : IReadOnlyList<CompletionItem>
    {
        public int Count { get; } = parts.Sum(p => p.Count);

        public CompletionItem this[int index]
        {
            get
            {
                foreach (IReadOnlyList<CompletionItem> part in parts)
                {
                    if (index < part.Count)
                    {
                        return part[index];
                    }
                    index -= part.Count;
                }
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public IEnumerator<CompletionItem> GetEnumerator() => parts.SelectMany(p => p).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>What a list is narrowed by: the text typed since it opened, what of it an item with no filter text of its
    /// own is matched by, how alike two scores must be to keep the list's own order, and the list's ranking.</summary>
    private readonly record struct Narrowing(Completion List, string Typed, string Bare, int Alike, RankedList Ranked);

    /// <summary>How many filters were asked for, the last the only one shown when it lands; what the last asked was for,
    /// so the same name asked again (a key's caret move after its text) is not filtered twice; and what the rows shown were
    /// filtered by.</summary>
    private int filters;
    private (Completion List, string Typed, int Filter)? lastAsked;
    private (Completion List, string Typed)? filtered;

    /// <summary>The open list's ranking, made for it once.</summary>
    private RankedList? ranked;

    /// <summary>What the open list is narrowed by with the name typed up to <paramref name="end"/> (the caret when not
    /// given); null, the list closed, once the caret has left the name or what is typed is no name.</summary>
    private Narrowing? NarrowedTo(int? end = null)
    {
        if (open is null)
        {
            return null;
        }
        TextSnapshot text = Editor.Document.Snapshot;
        int caret = end ?? Editor.Selections.Primary.Caret;
        if (caret < open.Start || Editor.Selections.HasMultiple)
        {
            Close();
            return null;
        }
        string typed = text.GetText(open.Start, caret - open.Start);
        // A web server's items may replace what came before the name (TypeScript's the member's dot), there as it asked.
        string lead = open.Lead;
        int skip = 0;
        while (skip < lead.Length && !IsWordChar(lead[skip]))
        {
            skip++;
        }
        bool led = skip > 0 && typed.StartsWith(lead[..skip], StringComparison.Ordinal) && typed[skip..].All(IsWordChar);
        if (!led && !typed.All(IsWordChar))
        {
            Close();
            return null;
        }
        if (!ReferenceEquals(ranked?.List, open))
        {
            ranked = new RankedList(open);
        }
        return new Narrowing(open, typed, led ? typed[skip..] : typed, open.Alike, ranked);
    }

    /// <summary>The list narrowed to what has been typed since it opened: prefix matches first (an exact-case one first of
    /// those), then CamelHumps, then anywhere in the name. Scored and made into rows on the thread pool, since a
    /// statement's list is every type in reach, and shown as it lands unless a later key's filter was asked for since.</summary>
    private void Filter()
    {
        if (NarrowedTo() is not { } narrowing || lastAsked == (narrowing.List, narrowing.Typed, filters))
        {
            return;
        }
        lastAsked = (narrowing.List, narrowing.Typed, ++filters);
        IdeErrors.Observe(FilterAsync(narrowing, filters));
    }

    private async Task FilterAsync(Narrowing narrowing, int asked)
    {
        CompletionRow[] rows = await BackgroundWork.Run(() => Rows(narrowing));
        if (asked == filters && open == narrowing.List)
        {
            ShowRows(narrowing, rows);
        }
    }

    /// <summary>The list shown for the name as typed up to <paramref name="end"/>, filtered here should its filter not have
    /// landed yet: what commits or moves the choice must find the rows of the name as typed.</summary>
    private void FilterNow(int? end = null)
    {
        if (NarrowedTo(end) is not { } narrowing || filtered == (narrowing.List, narrowing.Typed))
        {
            return;
        }
        lastAsked = (narrowing.List, narrowing.Typed, ++filters);
        ShowRows(narrowing, Rows(narrowing));
    }

    private void ShowRows(Narrowing narrowing, CompletionRow[] rows)
    {
        // The choice made among the rows of the name as typed stays on its item as items are added to the list.
        CompletionItem? chosen = filtered is { } before && ReferenceEquals(before.List, narrowing.List.Before) && before.Typed == narrowing.Typed
            ? completion.Selected?.Item
            : null;
        if (rows.Length == 0)
        {
            if (narrowing.List.Coming)
            {
                completion.Show([]);
                completion.IsVisible = false;
                itemCard.IsVisible = false;
                filtered = (narrowing.List, narrowing.Typed);
                return;
            }
            Close();
            return;
        }
        bool opening = !completion.IsVisible;
        // A list the person's Enter would commit from, which an agent's action never opens.
        if (opening && AgentAsks.Refused())
        {
            Close();
            return;
        }
        completion.Show(rows, chosen);
        filtered = (narrowing.List, narrowing.Typed);
        completion.IsVisible = true;
        popups.Place(completion, narrowing.List.Start, above: false);
        if (opening && narrowing.List.DescribedAsItOpens)
        {
            // The first item's documentation beside the list as it opens, which its selection came too early to show.
            IdeErrors.Observe(DescribeSelected());
        }
    }

    /// <summary>The rows a list shows narrowed (<see cref="RankedList.Rows"/>); reads nothing of the window's, so it runs
    /// off the UI thread.</summary>
    private static CompletionRow[] Rows(Narrowing narrowing) => narrowing.Ranked.Rows(narrowing.Typed, narrowing.Bare, narrowing.Alike);

    /// <summary>The most rows the list shows.</summary>
    private const int MostShown = 200;

    /// <summary>A list's ranking (<see cref="CompletionRanking"/>), made from its items the first time it is narrowed, off the
    /// UI thread since a statement's list is every type in reach; and the row each item makes, made once it is first shown.
    /// Its items that match what was typed are the best first, the higher score over the list's
    /// <see cref="Completion.Alike"/>, then the one the server preselects, then by its sort text; where the server says
    /// how well its items fit the place (C#'s), the one that fits better first among those matched the same way. Only the
    /// best <see cref="MostShown"/> kept. An item with no filter text of its own is matched by what was typed less a web
    /// server's lead.</summary>
    private sealed class RankedList(Completion list)
    {
        private readonly Lazy<CompletionRanking> ranking = new(() => new CompletionRanking([.. list.Items.Select(item =>
            new CompletionRanking.Entry(item.FilterText ?? item.Label, item.FilterText is not null, item.Preselect == true, item.SortText ?? item.Label, item.MatchPriority))]));

        private readonly CompletionRow?[] rows = new CompletionRow?[list.Items.Count];

        public Completion List => list;

        public CompletionRow[] Rows(string typed, string bare, int alike)
        {
            int[] shown = ranking.Value.Shown(typed, bare, alike, MostShown);
            CompletionRow[] made = new CompletionRow[shown.Length];
            for (int i = 0; i < shown.Length; i++)
            {
                int at = shown[i];
                made[i] = Volatile.Read(ref rows[at]) ?? Interlocked.CompareExchange(ref rows[at], Row(list.Items[at]), null) ?? rows[at]!;
            }
            return made;
        }
    }

    private async Task Commit()
    {
        using (BackgroundWork.Scope())
        {
            await CommitCore();
        }
    }

    /// <summary>The chosen item in place of the name typed, up to <paramref name="end"/> (the caret when not given).</summary>
    private async Task CommitCore(int? end = null)
    {
        FilterNow(end);
        if (open is null || !completion.IsVisible || completion.Selected is not { } row)
        {
            return;
        }
        if (open.Own is { } language)
        {
            int from = open.Start;
            Close();
            language.Commit(row.Item, from);
            return;
        }
        LspDocument? lsp = context.Current?.Lsp;
        if (lsp is null && !open.Settings)
        {
            return;
        }
        int start = open.Start;
        string tail = open.Tail;
        Close();
        CompletionItem item = row.Item;
        if (templatesOffered.TryGetValue(item, out LiveTemplate? chosen))
        {
            Expand(chosen, start, Editor.Selections.Primary.Caret);
            return;
        }
        if (postfixOffered.TryGetValue(item, out (PostfixTemplate Template, PostfixTarget Target) postfix))
        {
            ExpandPostfix(postfix.Template, postfix.Target, Editor.Selections.Primary.Caret);
            return;
        }
        TextDocument document = Editor.Document;
        IndentOptions indent = Editor.Indent;
        using ChangeTracker asked = new(document);
        int typed = end ?? Editor.Selections.Primary.Caret;
        try
        {
            if (item.Data is not null && item.AdditionalTextEdits is null && lsp is not null)
            {
                item = await lsp.ResolveAsync(item) ?? item;
            }
        }
        catch (LanguageServerFailed)
        {
            // The item goes in as listed, without what resolving would have added (a using it needs).
        }
        // The editor shows another file now: the item was for the one it showed.
        if (Editor.Document != document)
        {
            return;
        }
        if (ClientCommands.CompletionEdit(item) is { } complex)
        {
            CommitWhole(asked, complex, start, typed, indent);
            return;
        }
        start = asked.MapOffset(start);
        int caret = Math.Min(asked.MapOffset(typed), Editor.Document.Snapshot.Length);
        // What the item replaces after the caret, still there (a JSON string's closing quote): it goes with the name typed.
        if (tail.Length > 0 && caret + tail.Length <= Editor.Document.Snapshot.Length && Editor.Document.Snapshot.GetText(caret, tail.Length) == tail)
        {
            caret += tail.Length;
        }
        string insert = item.TextEdit?.NewText ?? item.TextEditText ?? item.InsertText ?? item.Label;
        // Reuse a separator already at the caret so accepting a declaration keyword does not double the space.
        if (end is null && lsp?.LanguageId == LanguageIds.CSharp && item.Kind == CompletionItemKind.Keyword
            && item.InsertTextFormat != InsertTextFormat.Snippet && DeclarationKeyword(insert))
        {
            if (caret < document.Snapshot.Length && document.Snapshot[caret] is ' ' or '\t')
            {
                insert += document.Snapshot[caret++];
            }
            else
            {
                insert += " ";
            }
        }
        TextRange final = new(insert.Length, 0);
        if (item.InsertTextFormat == InsertTextFormat.Snippet)
        {
            // A snippet (Razor's attribute="$0", a component with its required parameters): its places are Tab's to visit.
            Snippet snippet = Snippet.FromProtocol(insert);
            if (item.AdditionalTextEdits is not { Count: > 0 })
            {
                Editor.InsertSnippet(TextRange.FromBounds(start, Math.Max(start, caret)), snippet);
                return;
            }
            insert = snippet.Text;
            final = snippet.End;
        }
        int replaced = Math.Max(start, caret);
        List<TextEdit> edits = [new TextEdit(start, replaced - start, insert)];
        if (item.AdditionalTextEdits is { Count: > 0 } extra && WorkspaceEdits.Carried(asked, extra, indent) is { } placed)
        {
            edits.AddRange(placed.Where(e => e.End <= start || e.Start >= caret));
        }
        string eol = document.LineEnding;
        List<TextEdit> fitted = Indentation.Fit(document.Snapshot, LineEndings.Fit(document.Snapshot, edits, eol), indent);
        int after = ItemEnd(fitted, start, replaced);
        Selection landing = new(after - LineEndings.In(insert[final.Start..], eol).Length, after - LineEndings.In(insert[final.End..], eol).Length);
        int at = Editor.Selections.Primary.Caret;
        // Said, since an item put in with nothing typed is an insertion at the caret, which stays before it; a commit character's caret is past it.
        Editor.ApplyEdits(fitted, at >= start && at <= replaced ? SelectionSet.Single(landing) : null);
        if (item.Command is { } command)
        {
            context.Say(CodeActions.NotRun(item.Label, command));
        }
    }

    /// <summary>A keyword a word always follows: new is left out, as new() and new[] follow it with none.</summary>
    private static bool DeclarationKeyword(string text) => text is
        "public" or "private" or "protected" or "internal" or "static" or "abstract" or "sealed"
        or "virtual" or "override" or "readonly" or "volatile" or "const" or "extern"
        or "unsafe" or "async" or "partial" or "required" or "file" or "ref" or "scoped"
        or "class" or "struct" or "interface" or "enum" or "record" or "delegate" or "event" or "namespace";

    /// <summary>Where the item put in place of <paramref name="start"/> to <paramref name="end"/> ends once the fitted
    /// edits are made: the end of the edit holding it (fitting may widen it or join an edit touching it), moved on by the
    /// edits before it (a using added above), or the item's own end when it changed nothing.</summary>
    private static int ItemEnd(IReadOnlyList<TextEdit> fitted, int start, int end)
    {
        int shift = 0;
        foreach (TextEdit edit in fitted)
        {
            if (edit.Start <= start && edit.End >= end)
            {
                return edit.Start + shift + edit.Text.Length;
            }
            if (edit.Start >= end)
            {
                break;
            }
            shift += edit.Text.Length - edit.Length;
        }
        return end + shift;
    }

    /// <summary>A completion whose code the server writes whole (an override's body, the async an await needs in its
    /// method): its edit in place of the name typed from <paramref name="start"/> to <paramref name="typed"/>, answered for
    /// the text <paramref name="asked"/> began with, and the caret where the server puts it.</summary>
    private void CommitWhole(ChangeTracker asked, ComplexEdit complex, int start, int typed, IndentOptions indent)
    {
        ServerLines lines = ServerLines.Of(asked.Snapshot);
        int from = lines.OffsetOf(complex.Edit.Range.Start);
        int to = lines.OffsetOf(complex.Edit.Range.End);
        string written = complex.Edit.NewText;
        string eol = asked.Document.LineEnding;
        // Roslyn's edit covers the name as it stood when resolved; one that meets the name takes all of what was typed.
        (int first, int last) = from <= typed && to >= start ? (Math.Min(from, start), Math.Max(to, typed)) : (from, to);
        if (LineEndings.Fit(asked.Snapshot, [new TextEdit(first, last - first, written)], eol) is not [TextEdit broken])
        {
            return;
        }
        // The server's caret is an offset in the text its edit leaves, which starts as this edit's does, or after a '\r' it took in.
        int into = complex.Caret is { } caret && caret - from >= 0 && caret - from <= written.Length
            ? Math.Min(LineEndings.In(asked.Snapshot.GetText(broken.Start, first - broken.Start) + written[..(caret - from)], eol).Length, broken.Text.Length)
            : broken.Text.Length;
        if (Indentation.Fit(asked.Snapshot, broken, indent, ref into) is not { } made)
        {
            return;
        }
        if (asked.Map([made]) is not [TextEdit placed])
        {
            context.Say("The completion was not inserted: the code it changes was edited meanwhile");
            return;
        }
        Editor.ApplyEdits([placed], SelectionSet.Single(placed.Start + into));
    }

    public void Close()
    {
        completing?.Cancel();
        describing?.Cancel();
        open = null;
        lastAsked = null;
        completion.IsVisible = false;
        itemCard.IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The documentation beside the completion list: the chosen item's signature and summary, resolved once
    /// the choice rests a moment.</summary>
    private async Task DescribeSelected()
    {
        describing?.Cancel();
        itemCard.IsVisible = false;
        if (completion.IsVisible && completion.Selected is { } chosen && open?.Own is { } language)
        {
            itemCard.Clear();
            if (language.Describe(chosen.Item, itemCard) && !itemCard.IsEmpty)
            {
                itemCard.IsVisible = true;
                popups.PlaceBeside(itemCard, completion);
            }
            return;
        }
        if (!completion.IsVisible || completion.Selected is not { } row || context.Current?.Lsp is not { } lsp)
        {
            return;
        }
        CancellationTokenSource cancel = new();
        describing = cancel;
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancel.Token);
            CompletionItem item = row.Item;
            if (item.Data is not null && item.Documentation is null)
            {
                item = await lsp.ResolveAsync(item, cancel.Token) ?? item;
            }
            if (cancel.IsCancellationRequested || !completion.IsVisible || completion.Selected != row)
            {
                return;
            }
            itemCard.Clear();
            if (item.Detail is { Length: > 0 } detail)
            {
                itemCard.AddCode(detail);
            }
            if (Shapes.Text(item.Documentation) is { Length: > 0 } documentation)
            {
                itemCard.AddMarkdown(documentation);
            }
            if (itemCard.IsEmpty)
            {
                return;
            }
            itemCard.IsVisible = true;
            popups.PlaceBeside(itemCard, completion);
        }
        catch (OperationCanceledException)
        {
            // Another item chosen, or the list closed.
        }
        catch (LanguageServerFailed)
        {
            // No card beside the list.
        }
    }

    /// <summary>A character of the word a completion list narrows by: an identifier's, and in a Razor file a markup
    /// name's too (<c>@onclick</c>, <c>data-enhance-nav</c>, <c>@bind:after</c>).</summary>
    private bool IsWordChar(char c) => AssistContext.IsIdentifierChar(c) || (c is '@' or '-' or ':' && context.InRazor) || (open?.ResourceNames == true && c == '-')
        || (open?.WordCharacters.Contains(c) == true)
        || (context.WebCurrent is { } web && WebAssist.IsWebWordChar(web, c));

    /// <summary>Where a Razor list's word starts when its items say no range: back over an identifier, then over as much of
    /// a markup name before it as some item begins with, so <c>data-en</c> is replaced whole by <c>data-enhance-nav</c>
    /// while C#'s <c>a-b</c> completes only <c>b</c>.</summary>
    private int RazorWordStart(int caret, IReadOnlyList<CompletionItem> items)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        int identifier = context.WordStart(caret);
        int start = identifier;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }
        for (; start < identifier; start++)
        {
            string typed = text.GetText(start, caret - start);
            if (items.Any(i => (i.FilterText ?? i.Label).StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
            {
                return start;
            }
        }
        return identifier;
    }

    /// <summary>A row's badge: the letter and colour of each kind of completion.</summary>
    private static CompletionRow Row(CompletionItem item)
    {
        (string letter, string brush) = item.Kind switch
        {
            CompletionItemKind.Method or CompletionItemKind.Function => ("m", TokenKeys.Syntax_Method),
            CompletionItemKind.Constructor => ("c", TokenKeys.Syntax_Class),
            CompletionItemKind.Field => ("f", TokenKeys.Syntax_Field),
            CompletionItemKind.Variable => ("v", TokenKeys.TextMuted),
            CompletionItemKind.Class => ("C", TokenKeys.Syntax_Class),
            CompletionItemKind.Interface => ("I", TokenKeys.Syntax_Interface),
            CompletionItemKind.Module => ("N", TokenKeys.Syntax_Keyword),
            CompletionItemKind.Property => ("p", TokenKeys.Syntax_Property),
            CompletionItemKind.Enum => ("E", TokenKeys.Syntax_Enum),
            CompletionItemKind.Keyword => ("k", TokenKeys.Syntax_Keyword),
            CompletionItemKind.Snippet => ("s", TokenKeys.TextMuted),
            CompletionItemKind.EnumMember => ("e", TokenKeys.Syntax_EnumMember),
            CompletionItemKind.Constant => ("c", TokenKeys.Syntax_Constant),
            CompletionItemKind.Struct => ("S", TokenKeys.Syntax_Struct),
            CompletionItemKind.Event => ("e", TokenKeys.Syntax_Event),
            CompletionItemKind.Operator => ("o", TokenKeys.TextMuted),
            CompletionItemKind.TypeParameter => ("T", TokenKeys.Syntax_TypeParameter),
            _ => ("·", TokenKeys.TextMuted),
        };
        string detail = item.LabelDetails?.Description ?? item.Detail ?? string.Empty;
        return new CompletionRow(item, letter, brush, detail);
    }

    /// <summary>An open completion list: its items, where the name it completes starts, the characters that commit an
    /// item that names none of its own, and whether the server left items out until more is typed.</summary>
    public sealed record Completion(IReadOnlyList<CompletionItem> Items, int Start, IReadOnlyList<string> CommitCharacters, bool IsIncomplete)
    {
        public bool ResourceNames { get; init; }

        /// <summary>A settings file's keys alone, Passenger's own, offered where its JSON server does not run.</summary>
        public bool Settings { get; init; }

        /// <summary>The language whose own items these are (XAML's, an MSBuild file's), which commits, describes and badges
        /// them; null for a server's list.</summary>
        public LanguageAssist? Own { get; init; }

        /// <summary>The characters beyond a name's that narrow the list rather than close it.</summary>
        public string WordCharacters { get; init; } = string.Empty;

        /// <summary>How alike two scores must be to keep the list's own order among them (an MSBuild list's common
        /// properties first), not the shortest name's.</summary>
        public int Alike { get; init; } = 1;

        /// <summary>Whether the first item's documentation shows beside the list as it opens.</summary>
        public bool DescribedAsItOpens { get; init; }

        /// <summary>The list these items were added to as they came, whose choice stays where it is (C#'s types of
        /// namespaces not imported added to what is in scope); null for a list asked anew.</summary>
        public Completion? Before { get; init; }

        /// <summary>Whether items are still to be added to the list as they come (<see cref="Before"/>): showing no row
        /// meanwhile, it stays open for them.</summary>
        public bool Coming { get; set; }

        /// <summary>The text after the caret a web server's items replace as well (a JSON string's closing quote, the rest
        /// of a CSS name the caret is inside), as it stood when the list was asked for; empty for none.</summary>
        public string Tail { get; init; } = string.Empty;

        /// <summary>The text before the caret a web server's items replace, from <see cref="Start"/>, as it stood when the
        /// list was asked for (TypeScript's member dot, a JSON string's opening quote); empty for none.</summary>
        public string Lead { get; init; } = string.Empty;
    }

    /// <summary>The text after the caret a list's items replace, by the first item's range past the caret.</summary>
    private static string Tail(TextSnapshot asked, IReadOnlyList<CompletionItem> items, int caret)
    {
        if (items.FirstOrDefault(i => i.TextEdit is not null)?.TextEdit is not { } edit)
        {
            return string.Empty;
        }
        int end = Math.Min(ServerLines.Of(asked).OffsetOf(edit.Range.End), asked.Length);
        return end > caret && caret <= asked.Length ? asked.GetText(caret, end - caret) : string.Empty;
    }
}
