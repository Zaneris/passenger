using Avalonia.Media;
using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>A language's answers to the editor's commands in the file shown: what typing opens, completion, the hover
/// card, Go to Declaration, Find Usages, Rename and Reformat Code. Each answers for the files that are its own and
/// passes (false, or null) on any other, or where it leaves the answer to the next on the ladder
/// (<see cref="LanguageAssists"/>).</summary>
internal abstract class LanguageAssist
{
    protected static readonly Task<bool> Passed = Task.FromResult(false);

    protected static readonly Task<bool> Answered = Task.FromResult(true);

    protected static readonly Task<Declared?> NotDeclared = Task.FromResult<Declared?>(null);

    /// <summary>Opens completion or Parameter Info for what was just typed in the file shown, as its language has it.</summary>
    public virtual bool Typed(string text) => false;

    /// <summary>The completion list at the caret.</summary>
    public virtual Task<bool> CompleteAsync(string? trigger, bool incomplete) => Passed;

    /// <summary>One of the language's own items (<see cref="CodeCompletion.Completion.Own"/>) put in place of what was
    /// typed from <paramref name="start"/>.</summary>
    public virtual void Commit(CompletionItem item, int start)
    {
    }

    /// <summary>What the card beside the list says of one of the language's own items; null for none.</summary>
    public virtual string? Describe(CompletionItem item) => null;

    /// <summary>The colour badge of one of the language's own items; null for none.</summary>
    public virtual IBrush? Swatch(CompletionItem item) => null;

    /// <summary>What the hover card says of an offset of a file, added to <paramref name="card"/>, the window's and an
    /// agent's alike; an abandoned one throws <see cref="OperationCanceledException"/>, so the card is not shown.</summary>
    public virtual Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel) => Passed;

    /// <summary>Go to Declaration from an offset of the file shown.</summary>
    public virtual Task<bool> DefinitionAsync(int offset) => Passed;

    /// <summary>Where Go to Declaration goes from an offset of a file, as <see cref="DefinitionAsync"/> goes from the file
    /// shown, for an agent; it answers for the same files, and passes (null) where that passes.</summary>
    public virtual Task<Declared?> DeclaredAsync(OpenDocument document, int offset) => NotDeclared;

    /// <summary>Find Usages from an offset.</summary>
    public virtual Task<bool> UsagesAsync(int offset) => Passed;

    /// <summary>Rename at the caret: its box, or why not.</summary>
    public virtual bool Rename(int caret) => false;

    /// <summary>Reformat Code of a file, the window's and an agent's alike, only <paramref name="within"/> when given.</summary>
    public virtual Task<CodeActions.Reformatted?> FormatAsync(OpenDocument document, TextRange? within) =>
        Task.FromResult<CodeActions.Reformatted?>(null);
}

/// <summary>The languages' own answers, asked in one order by every command: an .http file's routes, an MSBuild file's,
/// XAML's, a compose file's, a settings file's configuration, a web language's, then C#'s, Razor's and any file
/// left; the first that answers is the command's.</summary>
internal sealed class LanguageAssists(HttpAssist http, MSBuildAssist msbuild, XamlAssist xaml, ComposeAssist compose, SettingsAssist settings, WebAssist web, CSharpAssist csharp)
{
    private readonly IReadOnlyList<LanguageAssist> ladder = [http, msbuild, xaml, compose, settings, web, csharp];

    public HttpAssist Http => http;

    public MSBuildAssist MSBuild => msbuild;

    public XamlAssist Xaml => xaml;

    public ComposeAssist Compose => compose;

    public SettingsAssist Settings => settings;

    public WebAssist Web => web;

    public CSharpAssist CSharp => csharp;

    public void Typed(string text)
    {
        foreach (LanguageAssist language in ladder)
        {
            if (language.Typed(text))
            {
                return;
            }
        }
    }

    public async Task CompleteAsync(string? trigger, bool incomplete)
    {
        foreach (LanguageAssist language in ladder)
        {
            if (await language.CompleteAsync(trigger, incomplete))
            {
                return;
            }
        }
    }

    public async Task HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        foreach (LanguageAssist language in ladder)
        {
            if (await language.HoverAsync(card, document, offset, cancel))
            {
                return;
            }
        }
    }

    public async Task DefinitionAsync(int offset)
    {
        foreach (LanguageAssist language in ladder)
        {
            if (await language.DefinitionAsync(offset))
            {
                return;
            }
        }
    }

    /// <summary>Where Go to Declaration goes from an offset of a file, for an agent; null where no language answers.</summary>
    public async Task<Declared?> DeclaredAsync(OpenDocument document, int offset)
    {
        foreach (LanguageAssist language in ladder)
        {
            if (await language.DeclaredAsync(document, offset) is { } declared)
            {
                return declared;
            }
        }
        return null;
    }

    public async Task UsagesAsync(int offset)
    {
        foreach (LanguageAssist language in ladder)
        {
            if (await language.UsagesAsync(offset))
            {
                return;
            }
        }
    }

    public void Rename(int caret)
    {
        foreach (LanguageAssist language in ladder)
        {
            if (language.Rename(caret))
            {
                return;
            }
        }
    }

    /// <summary>Reformat Code by a language's own formatter; null where none formats the file, for its language server.</summary>
    public async Task<CodeActions.Reformatted?> FormatAsync(OpenDocument document, TextRange? within)
    {
        foreach (LanguageAssist language in ladder)
        {
            if (await language.FormatAsync(document, within) is { } done)
            {
                return done;
            }
        }
        return null;
    }
}

/// <summary>What a hover card is given, part by part: the window's card shows each, an agent reads them as text.</summary>
internal interface IHoverCard
{
    /// <summary>Code, as written.</summary>
    void AddCode(string code);

    /// <summary>Words.</summary>
    void AddText(string text);

    /// <summary>What a language server says of a place.</summary>
    void AddHover(Hover hover);
}

/// <summary>Where Go to Declaration goes, the window's and an agent's alike: its places as a language server names them,
/// each with the words its choice shows (<see cref="Names"/>, none for one shown by its line of code); else a page that
/// declares it (an image's registry page), or the words for there being none.</summary>
internal sealed record Declared(IReadOnlyList<LspLocation> Places, string None)
{
    /// <summary>The words each place's choice shows, in order; null where each is shown by its line of code.</summary>
    public IReadOnlyList<string?>? Names { get; init; }

    /// <summary>What the places to choose from are called.</summary>
    public string Title { get; init; } = "Declarations";

    /// <summary>A page that declares it in place of a file, which <see cref="None"/> names.</summary>
    public Uri? Page { get; init; }

    /// <summary>Places in the editor's lines, as a language server names them (<see cref="ServerPlaces.ServerPositionOf"/>).</summary>
    public static IReadOnlyList<LspLocation> Served(IEnumerable<(string Path, TextPosition Position)> places, Func<string, TextSnapshot?> texts) =>
        [.. places.Select(p =>
        {
            LspPosition at = ServerPlaces.ServerPositionOf(texts(p.Path), p.Position);
            return new LspLocation(DocumentUris.FromPath(p.Path), new LspRange(at, at));
        })];
}
