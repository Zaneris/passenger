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

    /// <summary>The languages' own answers, in the one order each command asks them.</summary>
    public LanguageAssists Languages { get; set; } = null!;

    public event EventHandler<(string Path, TextPosition Position)>? Navigated;

    public event EventHandler<(string Title, IReadOnlyList<LspLocation> Usages)>? UsagesFound;

    public event EventHandler<string>? Message;

    public event EventHandler? FilesChanged;

    public event EventHandler<Uri>? Browsed;

    public CodeEditor Editor => editors.CodeEditor;

    public int Caret => Editor.Selections.Primary.Caret;

    /// <summary>The shown file's share of its language server: C#'s or Razor's, else a web language's.</summary>
    public IServerDocument? Current => editors.Active is { } active ? service()?.For(active) ?? (IServerDocument?)WebOf(active) : null;

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
        if (Current is not { } current)
        {
            return null;
        }
        if (current.Lsp is null && what is not null)
        {
            if (current is WebDocument && Web?.Invoke() is { } web && editors.Active is { } shown)
            {
                Say(web.Unready(what, shown));
            }
            else if (service() is { } csharp)
            {
                Say(csharp.Unready(what));
            }
        }
        return current.Lsp;
    }

    /// <summary>An open file's server document as it is now, for what the person chose from an answer (a fix, a new
    /// name): the server that answered may have been started again while they chose.</summary>
    public LspDocument Now(OpenDocument document) => service()?.For(document)?.Lsp ?? WebOf(document)?.Lsp ?? throw new LanguageServerFailed(
        WebOf(document) is not null ? "its language server is not running"
        : service() is { } csharp ? $"the C# language server is not ready: {csharp.State}" : Refusals.NoCSharp);

    /// <summary>The folder the open file's places are named from: the C# server's, else the web servers'.</summary>
    public string? Root => service()?.Session?.Folder ?? Web?.Invoke()?.Folder;

    /// <summary>Something to say in the status bar ("No usages found").</summary>
    public void Say(string message) => Message?.Invoke(this, message);

    /// <summary>Says in the status bar why a command got nothing from the server.</summary>
    public void Failed(string what, LanguageServerFailed failed) => Say($"{what} failed: {failed.Reason}");

    /// <summary>A place to go: a file and a line and column (from zero).</summary>
    public void Navigate((string Path, TextPosition Position) place) => Navigated?.Invoke(this, place);

    /// <summary>Find Usages' answer, for the Find window: what was searched for and where it is used.</summary>
    public void ShowUsages((string Title, IReadOnlyList<LspLocation> Usages) found) => UsagesFound?.Invoke(this, found);

    /// <summary>A refactoring changed files beyond the open ones, or made new ones, which the Explorer and Git then show.</summary>
    public void ChangedFiles() => FilesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>A page to open in the browser: an ESLint rule's, an image's registry page.</summary>
    public void Browse(Uri page) => Browsed?.Invoke(this, page);

    /// <summary>A file's text as the editor has it, else the disk's (<see cref="EditorArea.TextOf"/>); empty when it cannot be read.</summary>
    public string ReadOpenOrDisk(string path) => editors.TextOf(path)?.ToString() ?? string.Empty;

    public static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

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

    /// <summary>The name at an offset, or empty where there is none.</summary>
    public string NameAt(int offset)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        TextRange word = TextNavigation.WordAt(text, Math.Min(offset, text.Length));
        return word.Length > 0 && TextNavigation.IsWordChar(text[word.Start]) ? text.GetText(word) : string.Empty;
    }
}
