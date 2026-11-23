using Passenger.App.Editors;
using Passenger.App.MSBuild;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>What every part of the code assistance shares: the editor shown and its file, the language services that
/// answer for it, each asked for every time it is used since a server started again is a new one, and what the parts
/// tell the window (the status bar's words, a place to go, a page to open, usages found, files changed).</summary>
internal sealed class AssistContext(EditorArea editors, Func<CSharpService?> service)
{
    public EditorArea Editors => editors;

    /// <summary>The folder's C# language server, null without one.</summary>
    public CSharpService? CSharp => service();

    /// <summary>The window's JavaScript, TypeScript, HTML, CSS and JSON, for a web file shown.</summary>
    public Func<WebLanguages?>? Web { get; set; }

    /// <summary>The window's XAML, for a XAML file shown.</summary>
    public Func<XamlService?>? Xaml { get; set; }

    /// <summary>The window's MSBuild code help, for a project file shown.</summary>
    public Func<MSBuildService?>? MSBuild { get; set; }

    /// <summary>The stylesheets the window's pages link, for a Razor or HTML file shown.</summary>
    public Func<PageStylesheets?>? Stylesheets { get; set; }

    /// <summary>The scenes of the window's Godot projects, C# or GDScript alone, for a scene shown.</summary>
    public Func<Godot.GodotSceneIndex?>? GodotScenes { get; set; }

    /// <summary>The open shaders' marks from Godot's compiler, which its compiler and what it read of their uniforms come from.</summary>
    public Func<Godot.GodotShaderMarks?>? GodotShaders { get; set; }

    /// <summary>The open GDScripts' own trees and types.</summary>
    public Func<Godot.GodotScriptTypes?>? GodotScriptTypes { get; set; }

    /// <summary>The scenes of the Godot project a file is of and that project's folder; null for a file of none.</summary>
    public (Godot.GodotSceneIndex Index, string Folder)? GodotOf(string path) =>
        GodotScenes?.Invoke() is { } index && index.GameOf(path) is { } folder ? (index, folder) : null;

    /// <summary>The languages' own answers, in the one order each command asks them.</summary>
    public LanguageAssists Languages { get; set; } = null!;

    public event EventHandler<(string Path, TextPosition Position)>? Navigated;

    public event EventHandler<UsagesFound>? UsagesFound;

    public event EventHandler<string>? Message;

    public event EventHandler<Uri>? Browsed;

    public event EventHandler<object>? Followed;

    /// <summary>The keys that follow a link, as a tip shows them; null with none.</summary>
    public Func<string?> FollowKeys { get; set; } = () => null;

    public event EventHandler<string>? Played;

    public CodeEditor Editor => editors.CodeEditor;

    public int Caret => Editor.Selections.Primary.Caret;

    /// <summary>The shown file's share of its language server: C#'s or Razor's, else a web language's.</summary>
    public IServerDocument? Current => editors.Active is { } active ? ServerOf(active) : null;

    /// <summary>A file's share of its language server: C#'s or Razor's, else a web language's.</summary>
    public IServerDocument? ServerOf(OpenDocument document) => service()?.For(document) ?? (IServerDocument?)WebOf(document);

    public WebDocument? WebOf(OpenDocument document) => Web?.Invoke()?.For(document);

    /// <summary>The shown file's web language document, null for a file of none.</summary>
    public WebDocument? WebCurrent => editors.Active is { } active ? WebOf(active) : null;

    /// <summary>The shown file's XAML, null for a file that is not XAML of a project Passenger reads.</summary>
    public XamlDocument? XamlCurrent => editors.Active is { } active ? XamlOf(active) : null;

    public XamlDocument? XamlOf(OpenDocument? document) => document is null ? null : Xaml?.Invoke()?.For(document);

    public MSBuildDocument? MSBuildCurrent => editors.Active is { } active ? MSBuildOf(active) : null;

    public MSBuildDocument? MSBuildOf(OpenDocument? document) => document is null ? null : MSBuild?.Invoke()?.For(document);

    /// <summary>The shown file's syntax as it stands, the one parse of it everything shares; null for a file not C#.</summary>
    public ParsedText? Syntax => editors.Active?.Syntax?.Current;

    /// <summary>Whether the file shown is a Razor file, whose completion's words are markup's as well as C#'s.</summary>
    public bool InRazor => Current?.Lsp?.IsRazor == true;

    /// <summary>The server's document for the file shown: null for a file no server reads, and, said in the status bar for
    /// a command (<paramref name="what"/>), while the server cannot take it.</summary>
    public LspDocument? Ask(string? what = null)
    {
        if (what is not null && Unready(what) is { } unready)
        {
            Say(unready);
        }
        return Current?.Lsp;
    }

    /// <summary>Why a command (<paramref name="what"/>) gets no server's document for the file shown, a server's file it
    /// cannot take yet, in the status bar's words; null where it has one, or no server reads the file.</summary>
    public string? Unready(string what) => Current is not { Lsp: null } current ? null
        : current is WebDocument && Web?.Invoke() is { } web && editors.Active is { } shown ? web.Unready(what, shown)
        : service() is { } csharp ? csharp.Unready(what, editors.Active)
        : null;

    /// <summary>An open file's server document as it is now, for what the person chose from an answer (a fix, a new
    /// name): the server that answered may have been started again while they chose.</summary>
    public LspDocument Now(OpenDocument document) => service()?.For(document)?.Lsp ?? WebOf(document)?.Lsp ?? throw new LanguageServerFailed(
        WebOf(document) is not null ? "its language server is not running"
        : service() is { } csharp ? $"the C# language server is not ready: {csharp.State}" : Refusals.NoCSharp);

    /// <summary>Waits, for a change at an offset of a C# file, until the server holds in their projects the C# files come
    /// under the projects it reaches (<see cref="CSharpService.ReachedAsync"/>), for up to <see cref="Patience.Limit"/>, the
    /// status bar saying which meanwhile. Why not when the time is up, else null; null at once, the caller's offsets still
    /// standing, for a file of another server's or with none come.</summary>
    public async Task<string?> TakenInAsync(OpenDocument document, Func<int> offset) =>
        CSharp is { IsReady: true, Arriving: true } csharp && csharp.For(document) is not null
            ? NotTakenIn(await csharp.ReachedAsync(document, offset, Patience.Limit, SayWaiting, CancellationToken.None))
            : null;

    /// <summary>Waits, for an edit the server worked out, until it holds in their projects the C# files come under the
    /// projects of the files the edit reaches (<see cref="CSharpService.ReachedAsync(System.Text.Json.JsonElement, TimeSpan,
    /// Action{string?}?, CancellationToken)"/>), for up to <see cref="Patience.Limit"/>, the status bar saying which
    /// meanwhile: why not when the time is up, else null; and whether any was waited for, which the edit went without.</summary>
    public async Task<(string? Unloaded, bool Waited)> TakenInAsync(System.Text.Json.JsonElement edit)
    {
        if (CSharp is not { IsReady: true } csharp)
        {
            return (null, false);
        }
        (IReadOnlyList<(string Path, string Project)> left, bool waited) = await csharp.ReachedAsync(edit, Patience.Limit, SayWaiting, CancellationToken.None);
        return (NotTakenIn(left), waited);
    }

    /// <summary>Waits, for a question of another language's file that its project's C# answers (a MAUI page's expression),
    /// until the server holds in their projects the C# files come under the projects the file sees (<see
    /// cref="CSharpService.SeenFrom"/>), for up to <see cref="Patience.Limit"/>, the status bar saying which meanwhile. Why
    /// not when the time is up, else null.</summary>
    public async Task<string?> TakenInAsync(string path) =>
        CSharp is { IsReady: true, Arriving: true } csharp
            ? NotTakenIn(await csharp.UnplacedAsync(Patience.Limit, looking: false, csharp.SeenFrom(path), SayWaiting, CancellationToken.None))
            : null;

    /// <summary>For a question about an offset of a C# file (Find Usages): waits until the server holds the file itself in
    /// its project, if it just came, for up to <see cref="Patience.Limit"/>; then the C# files come under the projects that
    /// can change the answer that it has not taken in yet, looked at once, which the answer says it went without.</summary>
    public async Task<IReadOnlyList<(string Path, string Project)>> UntakenAsync(OpenDocument document, Func<int> offset)
    {
        if (CSharp is not { IsReady: true, Arriving: true } csharp || csharp.For(document) is null)
        {
            return [];
        }
        IReadOnlyList<(string Path, string Project)> itself = await csharp.PlacedAsync(document.Path, Patience.Limit, looking: false, SayWaiting, CancellationToken.None);
        return itself.Count > 0 ? itself : await csharp.UntakenAsync(document, offset(), CancellationToken.None);
    }

    private void SayWaiting(string? waiting) => Say(waiting is null ? string.Empty
        : global::Passenger.Workspace.WorkspaceModel.IsProjectFile(waiting) ? $"Waiting for the C# language server to load {Path.GetFileName(waiting)} again…"
        : $"Waiting for the C# language server to load {Path.GetFileName(waiting)} into its project…");

    /// <summary>The window's actions' work, which a change says its waits through.</summary>
    public ActionWork? Work { get; set; }

    /// <summary>What the Explorer has chosen for Reformat Code, Code Cleanup and Optimize Imports to run over where one is
    /// asked of it (its menu, or its keys while it has them); null where they act on the file shown.</summary>
    public Func<ActionContext, ChosenFiles?> Chosen { get; set; } = _ => null;

    /// <summary>The wait a run over many files takes for the C# language server, giving why a file it holds in no project yet
    /// is left (<see cref="Mcp.PassengerTools.WholeCodeAsync"/>).</summary>
    public Func<CancellationToken, Task<Func<string, string?>>>? WholeCode { get; set; }

    /// <summary>The open folder's Local History, which names a run over many files one change set.</summary>
    public Func<Passenger.App.History.LocalHistory?> History { get; set; } = () => null;

    /// <summary>Settings' C# formatter: what lays C# out for Reformat Code, Code Cleanup and Reformat on save.</summary>
    public Settings.CSharpFormatter CSharpFormatter { get; set; }

    /// <summary>dotnet format over C# files (canonical paths), whitespace alone or everything it fixes, as the window runs it
    /// for the open folder: null once it ran, else why not.</summary>
    public Func<IReadOnlyList<string>, bool, CancellationToken, Task<string?>>? DotnetFormat { get; set; }

    /// <summary>A change the window makes of a server's edit, run in a <see cref="Patience"/>: made again once the server has
    /// read the files not open written a moment before it was asked (by Undo, another change, another program), what it
    /// waits for said as the work of the <paramref name="action"/> that asked for it.</summary>
    public Task<RefactorOutcome> PatientlyAsync(string action, Func<Patience, Task<RefactorOutcome>> attempt) =>
        PatientlyAsync(action, attempt, made => made.Problem is not null);

    /// <summary>As <see cref="PatientlyAsync(string, Func{Patience, Task{RefactorOutcome}})"/>, for an answer
    /// <paramref name="refused"/> tells a refusal of.</summary>
    public Task<T> PatientlyAsync<T>(string action, Func<Patience, Task<T>> attempt, Func<T, bool> refused)
    {
        Action<string?> waits = Work?.Waits(action, Say) ?? (stage =>
        {
            if (stage is not null)
            {
                Say(stage);
            }
        });
        return Patience.RunAsync(Editors.Commands, () => CSharp?.Session, attempt, refused, stage => waits(stage is null ? null : $"{stage}…"), CancellationToken.None);
    }

    private static string? NotTakenIn(IReadOnlyList<(string Path, string Project)> left) =>
        left.Count == 0 ? null
        : CSharpService.IsReload(left[0]) ? Refusals.NotLoadedAgain(Path.GetFileNameWithoutExtension(left[0].Project))
        : Refusals.NotTakenIn(Path.GetFileName(left[0].Path), Path.GetFileNameWithoutExtension(left[0].Project));

    /// <summary>The folder the open file's places are named from: the C# server's, else the web servers'.</summary>
    public string? Root => service()?.Session?.Folder ?? Web?.Invoke()?.Folder;

    /// <summary>Something to say in the status bar ("No usages found").</summary>
    public void Say(string message) => Message?.Invoke(this, message);

    /// <summary>Says in the status bar why a command got nothing from the server.</summary>
    public void Failed(string what, LanguageServerFailed failed) => Say($"{what} failed: {failed.Reason}");

    /// <summary>A place to go: a file and a line and column (from zero).</summary>
    public void Navigate((string Path, TextPosition Position) place) => Navigated?.Invoke(this, place);

    /// <summary>Whether places to choose from (several declarations, implementations, base or derived symbols) are kept in
    /// the Find window rather than chosen in a popup.</summary>
    public Func<bool> KeepsPlaces { get; set; } = () => false;

    /// <summary>Find Usages' answer, for the Find window, or Show Usages' for its popup at an offset: what was searched for,
    /// where it is used, and what the answer went without, when it did.</summary>
    public void ShowUsages(string title, IReadOnlyList<LspLocation> usages, int? popupAt, string? note = null) =>
        UsagesFound?.Invoke(this, new UsagesFound(title, usages, note, popupAt));

    /// <summary>How the window runs a change of files beyond the open ones (<see cref="ChangeFilesAsync"/>).</summary>
    public Func<IReadOnlyCollection<string>, CommandHistory.Recording, Action, Task>? FilesChanging { get; set; }

    /// <summary>Runs a refactoring's change of files beyond the open ones within its command's recording, which writes the
    /// files not open it changes (<see cref="CommandHistory.Recording.WriteAsync"/>); the window follows it once they are
    /// written: what it made, moved or deleted among <paramref name="paths"/>, which the Explorer and Git then show, and the
    /// projects and XAML that read a text it changed. Throws <see cref="WorkspaceEditFailed"/> for a file the disk refused,
    /// once all the command did is taken back.</summary>
    public async Task ChangeFilesAsync(IReadOnlyCollection<string> paths, CommandHistory.Recording recording, Action change)
    {
        if (FilesChanging is { } following)
        {
            await following(paths, recording, change);
            return;
        }
        change();
        await WorkspaceEdits.WrittenAsync(recording, await recording.WriteAsync());
    }

    /// <summary>A page to open in the browser: an ESLint rule's, an image's registry page.</summary>
    public void Browse(Uri page) => Browsed?.Invoke(this, page);

    /// <summary>A link to follow: an address written in a file's comment or string, by what it opens.</summary>
    public void Follow(object target) => Followed?.Invoke(this, target);

    /// <summary>A sound file to play: one a path in the code names, its card's Play pressed.</summary>
    public void Play(string file) => Played?.Invoke(this, file);

    /// <summary>A file's text as the editor has it, else the disk's (<see cref="EditorArea.TextOf"/>); empty when it cannot be read.</summary>
    public string ReadOpenOrDisk(string path) => editors.TextOf(path)?.ToString() ?? string.Empty;

    /// <summary>Whether a character is a C# name's, as Roslyn reads one: a letter, a decimal digit, connector punctuation, a
    /// combining mark or a format character. The names of XAML, MSBuild and a shader are read by it too.</summary>
    public static bool IsIdentifierChar(char c) => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsIdentifierPartCharacter(c);

    /// <summary>Whether a C# name may start with a character, as Roslyn reads one: a letter or '_'.</summary>
    public static bool IsIdentifierStart(char c) => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsIdentifierStartCharacter(c);

    /// <summary>Whether the character just typed before an offset of the editor's text starts a name there: one a name may
    /// start with, after none of a name's.</summary>
    public bool StartsName(char typed, int caret) => IsIdentifierStart(typed) && WordStart(caret) == caret - 1;

    /// <summary>Where the identifier before an offset of the editor's text starts.</summary>
    public int WordStart(int caret)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        int start = caret;
        while (start > 0 && IsIdentifierChar(text[start - 1]))
        {
            start--;
        }
        return start;
    }

    /// <summary>The name at an offset of the editor's text, or empty where there is none.</summary>
    public string NameAt(int offset) => NameAt(Editor.Document.Snapshot, offset);

    /// <summary>The name at an offset of a text, or empty where there is none.</summary>
    public static string NameAt(TextSnapshot text, int offset)
    {
        TextRange word = TextNavigation.WordAt(text, Math.Min(offset, text.Length));
        return word.Length > 0 && WordCharacters.IsWord(text[word.Start]) ? text.GetText(word) : string.Empty;
    }
}
