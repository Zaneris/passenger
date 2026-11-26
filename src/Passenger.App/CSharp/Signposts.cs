using Passenger.App.Editors;
using Passenger.App.Settings;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.CSharp;

/// <summary>What a signpost's word says, which a press on it shows.</summary>
internal enum SignpostKind
{
    Usages,
    Inheriting,
    Authors,
}

/// <summary>A signpost's word as its row holds it: what it says, the name of the declaration under it, and for its authors
/// the newest commit among them.</summary>
internal sealed record SignpostWord(SignpostKind Kind, string Name, BlameLine? Commit = null);

/// <summary>A complexity word and the document version its line offsets belong to.</summary>
internal sealed record ComplexityWord(FunctionComplexity Function, int Version);

/// <summary>Signposts: a row above each C# type and member of a file shown, saying how many places use it, what implements,
/// overrides or derives from it, and who changed it most (its file's one blame, <see cref="VcsController.BlameOf"/>), a word
/// pressed to see them. Once anything is known of a file, a row stands for every declaration its parse finds, so the code
/// never moves again as counts come; those on screen are counted in one request of the extension as typing and scrolling
/// pause, a count kept by its declaration's name until counted again. A UI-thread object of the window's.</summary>
internal sealed class Signposts
{
    /// <summary>How many lines past those on screen are counted, so a short scroll finds them counted.</summary>
    private const int Beyond = 20;

    private readonly EditorArea editors;
    private readonly Func<CSharpService?> csharp;
    private readonly Debounce pause;
    private VcsController vcs;
    private readonly Dictionary<OpenDocument, Kept> kept = [];
    private AppSettings settings;
    private CancellationTokenSource? counting;

    /// <summary>How many times the C# files or the server's projects changed: a count from before a change is asked again.</summary>
    private int generation;

    /// <summary>How many counts in a row the server cancelled or refused as stale, each asked again at the next pause up to
    /// <see cref="AskedAgain"/> times.</summary>
    private int stale;

    /// <summary>How many times in a row a count the server cancelled is asked again before the next change does.</summary>
    private const int AskedAgain = 5;

    public Signposts(EditorArea editors, Func<CSharpService?> csharp, VcsController vcs, AppSettings settings)
    {
        this.editors = editors;
        this.csharp = csharp;
        this.vcs = vcs;
        this.settings = settings;
        pause = new Debounce(TypingPauses.Signposts, ShowAsync);
        editors.Opened += (_, document) => Track(document);
        editors.Closed += (_, document) => Forget(document);
        editors.ActiveChanged += (_, _) => Soon();
        editors.ForEachEditor(editor => editor.ScrollChanged += (_, _) => Soon());
        vcs.Blamed += OnBlamed;
        foreach (OpenDocument document in editors.Documents)
        {
            Track(document);
        }
    }

    /// <summary>Follows the Git of the window's session from now, the files it keeps blamed for their authors.</summary>
    public void Bind(VcsController next)
    {
        UiThread.Check();
        vcs.Blamed -= OnBlamed;
        vcs = next;
        vcs.Blamed += OnBlamed;
        foreach (OpenDocument document in kept.Keys)
        {
            vcs.WantBlame(document, IsOn && settings.SignpostAuthors);
        }
    }

    private void OnBlamed(object? sender, OpenDocument document)
    {
        if (kept.TryGetValue(document, out Kept? known))
        {
            Set(document, known);
        }
    }

    private bool CountsOrAuthors => settings.SignpostUsages || settings.SignpostInheriting || settings.SignpostAuthors;

    /// <summary>Whether any part of a signpost shows.</summary>
    private bool IsOn => settings.Signposts && (CountsOrAuthors || settings.SignpostComplexity);

    /// <summary>Takes in the settings as they stand: shown again where the signposts' changed, every row gone when off.</summary>
    public void Follow(AppSettings next)
    {
        UiThread.Check();
        bool same = settings.Signposts == next.Signposts && settings.SignpostUsages == next.SignpostUsages
            && settings.SignpostInheriting == next.SignpostInheriting && settings.SignpostAuthors == next.SignpostAuthors
            && settings.SignpostComplexity == next.SignpostComplexity
            && settings.SignpostComplexityMinimum == next.SignpostComplexityMinimum
            && settings.SignpostComplexityYellowFrom == next.SignpostComplexityYellowFrom
            && settings.SignpostComplexityRedFrom == next.SignpostComplexityRedFrom;
        bool minimumChanged = settings.SignpostComplexityMinimum != next.SignpostComplexityMinimum;
        settings = next;
        if (same)
        {
            return;
        }
        foreach ((OpenDocument document, Kept known) in kept)
        {
            vcs.WantBlame(document, IsOn && settings.SignpostAuthors);
            if (!settings.Signposts || !settings.SignpostComplexity)
            {
                HideComplexity(document, known);
            }
            else if (minimumChanged && known.Version == document.Text.Version)
            {
                RefreshComplexity(document, known);
            }
            if (IsOn)
            {
                Set(document, known);
            }
            else
            {
                document.Decorations.SetRowsAbove([]);
            }
        }
        if (IsOn)
        {
            Soon();
            return;
        }
        counting?.Cancel();
        pause.Stop();
    }

    /// <summary>The counts asked again soon, every one of them: the server loaded projects, or a C# file changed.</summary>
    public void Recount()
    {
        generation++;
        stale = 0;
        Soon();
    }

    /// <summary>The counts asked again soon: the text, the scroll or what is shown changed, or the server came to answer for a
    /// file (ready, or a file opened in it), which a count asked before it was could not be.</summary>
    public void Soon()
    {
        if (IsOn && kept.Count > 0)
        {
            pause.Restart();
        }
    }

    /// <summary>What a rest over a signpost's word shows.</summary>
    public static string? HoverOf(RowAboveWord word) => word.Data switch
    {
        SignpostWord { Kind: SignpostKind.Authors, Commit: { IsCommitted: true } commit } =>
            $"{commit.Short} {commit.Summary}\n{commit.Author}, {commit.Time.ToLocalTime():f}",
        ComplexityWord complexity => $"Cognitive complexity of {complexity.Function.Name}: {complexity.Function.Score}\nClick to toggle the increments at the ends of lines",
        _ => null,
    };

    /// <summary>Shows one function's increments at the ends of its lines, or hides them if it was already chosen.</summary>
    public void ToggleComplexity(TextDocument text, ComplexityWord word)
    {
        UiThread.Check();
        if (!settings.Signposts || !settings.SignpostComplexity || text.Version != word.Version
            || word.Function.Score < settings.SignpostComplexityMinimum
            || editors.Documents.FirstOrDefault(document => document.Text == text) is not { } document
            || !kept.TryGetValue(document, out Kept? known))
        {
            return;
        }
        FunctionComplexity function = word.Function;
        bool sameFunction = known.ExpandedComplexity is { } expanded
            && expanded.Place.Offset == function.Offset && expanded.Name == function.Name;
        HideComplexity(document, known);
        if (!sameFunction)
        {
            known.ExpandedComplexity = (text.AnchorAt(function.Offset, AnchorGravity.After), function.Name);
            SetComplexityLines(document, function);
        }
    }

    private static void SetComplexityLines(OpenDocument document, FunctionComplexity function) =>
        document.ComplexityAnnotations = [.. function.Lines.Select(line => new LineAnnotation(line.Line, $"+{line.Points}", LineAnnotationLook.Muted))];

    private static void HideComplexity(OpenDocument document, Kept known)
    {
        known.ExpandedComplexity?.Place.Remove();
        known.ExpandedComplexity = null;
        document.ComplexityAnnotations = [];
    }

    /// <summary>Finds the chosen function in the new parse after its name's offset has moved with the text.</summary>
    private void RefreshComplexity(OpenDocument document, Kept known)
    {
        if (known.ExpandedComplexity is not { } expanded)
        {
            return;
        }
        int offset = expanded.Place.Offset;
        if (known.Complexity?.SelectMany(group => group).FirstOrDefault(f => f.Offset == offset && f.Name == expanded.Name) is { } function
            && function.Score >= settings.SignpostComplexityMinimum)
        {
            SetComplexityLines(document, function);
        }
        else
        {
            HideComplexity(document, known);
        }
    }

    private void Track(OpenDocument document)
    {
        if (document.Language != Language.CSharp || kept.ContainsKey(document))
        {
            return;
        }
        Kept known = new((_, _) =>
        {
            document.ComplexityAnnotations = [];
            Recount();
        });
        kept[document] = known;
        document.Text.Changed += known.OnChanged;
        vcs.WantBlame(document, IsOn && settings.SignpostAuthors);
        Soon();
    }

    private void Forget(OpenDocument document)
    {
        if (kept.Remove(document, out Kept? known))
        {
            document.Text.Changed -= known.OnChanged;
            HideComplexity(document, known);
            vcs.WantBlame(document, false);
        }
    }

    /// <summary>The rows of every C# file shown, from their parse as the text stands, then the counts of the declarations on
    /// screen not yet counted for it, in one request each; a later pause cancels this one.</summary>
    private async Task ShowAsync()
    {
        counting?.Cancel();
        if (!IsOn)
        {
            return;
        }
        CancellationTokenSource mine = counting = new CancellationTokenSource();
        foreach ((OpenDocument document, Kept known) in kept.ToList())
        {
            if (Shown(document) is not { } lines || mine.IsCancellationRequested)
            {
                continue;
            }
            await ShowAsync(document, known, lines, mine.Token);
        }
    }

    private async Task ShowAsync(OpenDocument document, Kept known, (int First, int Last) lines, CancellationToken cancel)
    {
        if (document.Syntax is not { } syntax)
        {
            return;
        }
        int version = document.Text.Version;
        ParsedText parsed = await syntax.CurrentAsync();
        if (cancel.IsCancellationRequested || version != document.Text.Version || !ReferenceEquals(parsed.Text, document.Text.Snapshot))
        {
            return;
        }
        bool withComplexity = settings.SignpostComplexity;
        if (!ReferenceEquals(known.Parsed, parsed) || (known.Complexity is not null) != withComplexity)
        {
            IReadOnlyList<SignpostDeclaration> declarations;
            ILookup<int, FunctionComplexity>? complexity;
            try
            {
                (declarations, complexity) = await BackgroundWork.Run(() =>
                    (SignpostDeclarations.Of(parsed), withComplexity ? CognitiveComplexity.Of(parsed, cancel).ToLookup(f => f.RowOffset) : null), cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            if (cancel.IsCancellationRequested || version != document.Text.Version || !kept.ContainsKey(document)
                || !IsOn || withComplexity != settings.SignpostComplexity)
            {
                return;
            }
            known.Parsed = parsed;
            known.Declarations = declarations;
            known.Complexity = complexity;
            known.Version = version;
            RefreshComplexity(document, known);
        }
        Set(document, known);
        if (!(settings.SignpostUsages || settings.SignpostInheriting) || csharp() is not { IsReady: true } service || service.For(document)?.Lsp is not { } lsp)
        {
            return;
        }
        int asking = generation;
        List<(SignpostDeclaration Declaration, (string, int) Key)> asked = [];
        foreach ((SignpostDeclaration declaration, (string, int) key) in Keyed(known.Declarations))
        {
            if (declaration.Line >= lines.First - Beyond && declaration.Line <= lines.Last + Beyond
                && !(known.Counts.TryGetValue(key, out Counted counted) && counted.Generation == asking))
            {
                asked.Add((declaration, key));
            }
        }
        if (asked.Count == 0)
        {
            return;
        }
        IReadOnlyList<Passenger.Roslyn.Extension.SignpostCount> counts;
        LanguageServer.NobodyWaits();
        try
        {
            counts = await lsp.SignpostsAsync([.. asked.Select(a => a.Declaration.NameOffset)], settings.SignpostUsages, settings.SignpostInheriting, cancel: cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return;
        }
        catch (LanguageServerFailed failed)
        {
            // Gone with the server, its start makes it ready again; made stale by a change the server took in, asked again.
            if ((failed.Cancelled || failed.Outdated) && stale++ < AskedAgain)
            {
                Soon();
            }
            return;
        }
        if (cancel.IsCancellationRequested || version != document.Text.Version || !kept.ContainsKey(document) || counts.Count != asked.Count)
        {
            return;
        }
        stale = 0;
        for (int i = 0; i < asked.Count; i++)
        {
            known.Counts[asked[i].Key] = new Counted(counts[i].Usages, counts[i].Inheriting, asking);
        }
        Set(document, known);
    }

    /// <summary>The lines the editors showing a file show between them, or null where none does.</summary>
    private (int First, int Last)? Shown(OpenDocument document)
    {
        (int First, int Last)? shown = null;
        editors.ForEachEditor(editor =>
        {
            if (editor.Document == document.Text && editor.IsVisible)
            {
                (int first, int last) = editor.LinesShown;
                shown = shown is { } before ? (Math.Min(before.First, first), Math.Max(before.Last, last)) : (first, last);
            }
        });
        return shown;
    }

    /// <summary>A file's rows above its declarations, each with what is known of it, for the text as it was parsed.</summary>
    private void Set(OpenDocument document, Kept known)
    {
        if (known.Version != document.Text.Version || !IsOn)
        {
            return;
        }
        IReadOnlyList<BlameLine>? blame = settings.SignpostAuthors ? vcs.BlameOf(document) : null;
        Dictionary<int, List<RowAboveWord>> rows = [];
        bool said = blame is not null;
        foreach ((SignpostDeclaration declaration, (string, int) key) in Keyed(CountsOrAuthors ? known.Declarations : []))
        {
            List<RowAboveWord> words = [];
            known.Counts.TryGetValue(key, out Counted counted);
            if (settings.SignpostUsages && counted.Usages is int usages)
            {
                words.Add(new RowAboveWord(SignpostDeclarations.Usages(usages), new SignpostWord(SignpostKind.Usages, declaration.Name)));
            }
            if (settings.SignpostInheriting && declaration.Heritage != Heritage.None && counted.Inheriting is int inheriting)
            {
                words.Add(new RowAboveWord(SignpostDeclarations.Inheriting(declaration.Heritage, inheriting), new SignpostWord(SignpostKind.Inheriting, declaration.Name)));
            }
            if (blame is not null && BlameAuthors.Of(blame, declaration.FirstLine, declaration.LastLine) is { } authors)
            {
                words.Add(new RowAboveWord(authors.Label, new SignpostWord(SignpostKind.Authors, declaration.Name, authors.Latest)));
            }
            said |= words.Count > 0;
            rows.Add(declaration.LineCode, words);
        }
        if (settings.SignpostComplexity && known.Complexity is { } complexity)
        {
            foreach (IGrouping<int, FunctionComplexity> group in complexity)
            {
                bool shared = group.Skip(1).Any();
                foreach (FunctionComplexity function in group.Where(f => f.Score >= settings.SignpostComplexityMinimum))
                {
                    if (!rows.TryGetValue(group.Key, out List<RowAboveWord>? words))
                    {
                        rows.Add(group.Key, words = []);
                    }
                    words.Add(ComplexityScore(function, known.Version, shared));
                }
            }
        }
        // Keep space for counts that have not arrived once there is something to show.
        document.Decorations.SetRowsAbove([.. rows.Where(row => said || row.Value.Count > 0)
            .Select(row => new RowAbove(row.Key, row.Value.Count > 0 ? row.Value : [new RowAboveWord("…")]))]);
    }

    private RowAboveWord ComplexityScore(FunctionComplexity function, int version, bool shared)
    {
        string dotBrush = function.Score switch
        {
            int score when score < settings.SignpostComplexityYellowFrom => TokenKeys.Vcs_File_Added,
            int score when score < settings.SignpostComplexityRedFrom => TokenKeys.Warning,
            _ => TokenKeys.Error,
        };
        string label = shared ? $"{function.Score} ({function.Name})" : $"{function.Score}";
        return new RowAboveWord(label, new ComplexityWord(function, version), dotBrush);
    }

    /// <summary>The declarations with what their counts are kept by: their name, and which of the declarations of that name
    /// they are, so an edit that adds none keeps each its counts.</summary>
    private static IEnumerable<(SignpostDeclaration Declaration, (string Name, int Nth) Key)> Keyed(IReadOnlyList<SignpostDeclaration> declarations)
    {
        Dictionary<string, int> seen = [];
        foreach (SignpostDeclaration declaration in declarations)
        {
            int nth = seen.GetValueOrDefault(declaration.Name);
            seen[declaration.Name] = nth + 1;
            yield return (declaration, (declaration.Name, nth));
        }
    }

    /// <summary>What is known of one file: its declarations as last parsed and the text's version then, and each
    /// declaration's counts, with when they were counted.</summary>
    private sealed class Kept(EventHandler<DocumentChangedEventArgs> onChanged)
    {
        public EventHandler<DocumentChangedEventArgs> OnChanged { get; } = onChanged;
        public IReadOnlyList<SignpostDeclaration> Declarations { get; set; } = [];
        public ILookup<int, FunctionComplexity>? Complexity { get; set; }
        public (Anchor Place, string Name)? ExpandedComplexity { get; set; }
        public ParsedText? Parsed { get; set; }
        public int Version { get; set; } = -1;
        public Dictionary<(string, int), Counted> Counts { get; } = [];
    }

    /// <summary>A declaration's counts as the extension gave them, and the <see cref="generation"/> they were counted in.</summary>
    private readonly record struct Counted(int? Usages, int? Inheriting, int Generation);
}
