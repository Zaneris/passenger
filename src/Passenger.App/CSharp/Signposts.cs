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

    /// <summary>Whether any part of a signpost shows.</summary>
    private bool IsOn => settings.Signposts && (settings.SignpostUsages || settings.SignpostInheriting || settings.SignpostAuthors);

    /// <summary>Takes in the settings as they stand: shown again where the signposts' changed, every row gone when off.</summary>
    public void Follow(AppSettings next)
    {
        UiThread.Check();
        bool same = settings.Signposts == next.Signposts && settings.SignpostUsages == next.SignpostUsages
            && settings.SignpostInheriting == next.SignpostInheriting && settings.SignpostAuthors == next.SignpostAuthors;
        settings = next;
        if (same)
        {
            return;
        }
        foreach (OpenDocument document in kept.Keys)
        {
            vcs.WantBlame(document, IsOn && settings.SignpostAuthors);
            if (!IsOn)
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
        Soon();
    }

    /// <summary>The counts asked again soon: the text, the scroll or what is shown changed.</summary>
    public void Soon()
    {
        if (IsOn && kept.Count > 0)
        {
            pause.Restart();
        }
    }

    /// <summary>The words a rest over a signpost's word shows: its authors' newest commit; none for a count.</summary>
    public static string? HoverOf(RowAboveWord word) =>
        word.Data is SignpostWord { Kind: SignpostKind.Authors, Commit: { IsCommitted: true } commit }
            ? $"{commit.Short} {commit.Summary}\n{commit.Author}, {commit.Time.ToLocalTime():f}"
            : null;

    private void Track(OpenDocument document)
    {
        if (document.Language != Language.CSharp || kept.ContainsKey(document))
        {
            return;
        }
        Kept known = new((_, _) => Recount());
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
        known.Declarations = await BackgroundWork.Run(() => SignpostDeclarations.Of(parsed), cancel);
        known.Version = version;
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
            counts = await lsp.SignpostsAsync([.. asked.Select(a => a.Declaration.NameOffset)], settings.SignpostUsages, settings.SignpostInheriting, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return;
        }
        catch (LanguageServerFailed)
        {
            // Cancelled by the server or gone with it: the next pause asks again.
            return;
        }
        if (cancel.IsCancellationRequested || version != document.Text.Version || counts.Count != asked.Count)
        {
            return;
        }
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
        List<RowAbove> rows = [];
        bool said = blame is not null;
        foreach ((SignpostDeclaration declaration, (string, int) key) in Keyed(known.Declarations))
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
            if (words.Count == 0)
            {
                // Held for the words to come, so the code stays where it stands as they do.
                words.Add(new RowAboveWord("…"));
            }
            rows.Add(new RowAbove(declaration.LineCode, words));
        }
        // Until something is known of the file (no server to count, no Git to blame) it has no rows to hold.
        document.Decorations.SetRowsAbove(said ? rows : []);
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
        public int Version { get; set; } = -1;
        public Dictionary<(string, int), Counted> Counts { get; } = [];
    }

    /// <summary>A declaration's counts as the extension gave them, and the <see cref="generation"/> they were counted in.</summary>
    private readonly record struct Counted(int? Usages, int? Inheriting, int Generation);
}
