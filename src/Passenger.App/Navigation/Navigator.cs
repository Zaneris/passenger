using Avalonia.Threading;
using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>What a window lends its navigation: its editors, its session (whose <see cref="NavigationHistory"/>,
/// index, recent files and language server it goes by), its popups and status bar, opening a file in it, and a file's
/// text as the editor or the disk has it.</summary>
public sealed record NavigatorParts(
    EditorArea Editors,
    Func<ProjectSession> Session,
    PopupHost Popups,
    StatusBar Status,
    Func<string, string?> OpenFile,
    Func<string, TextSnapshot?> TextOf);

/// <summary>Getting about the code: going to a file and position remembered for Back and Forward, Last and Next Edit
/// Location, Recent Locations with their lines of code, and the Go to popups (File, Symbol, Everything, Line:Column,
/// File Structure). The places are the open folder's (its session's navigation history), visited as a file is left for
/// another and edited as one is typed in. A UI-thread object of the window.</summary>
public sealed class Navigator
{
    /// <summary>How many places Recent Locations lists.</summary>
    private const int RecentLocationsShown = 25;

    /// <summary>The lines Recent Locations shows either side of a place.</summary>
    private const int LinesAround = 2;

    private const string NoEditBefore = "No earlier edit location";
    private const string NoEditAfter = "No later edit location";

    private readonly NavigatorParts parts;
    private OpenDocument? shownLast;
    private bool editNoted;

    /// <summary>Whether Recent Locations last showed only the places edited, which it opens on again.</summary>
    private bool recentEditedOnly;

    /// <summary>The lines of the files Go to Symbol's rows have named, read once while its popup is open, kept only where
    /// the server's are not the editor's (<see cref="ServerPlaces.Differing"/>); any thread.</summary>
    private System.Collections.Concurrent.ConcurrentDictionary<string, Lsp.ServerLines?> symbolFiles = new(StringComparer.Ordinal);

    public Navigator(NavigatorParts parts)
    {
        this.parts = parts;
        Editors.ActiveChanged += (_, _) =>
        {
            if (shownLast is { } left && left != Editors.Active)
            {
                TextSnapshot text = left.Text.Snapshot;
                int caret = Math.Min(left.Selections.Primary.Caret, text.Length);
                Places.Visited(new HistoryPlace(left.Path, caret, text.GetLineFromOffset(caret)));
            }
            shownLast = Editors.Active;
        };
        Editors.Opened += (_, document) => document.Text.Changed += (_, _) => NoteEdit(document);
    }

    private EditorArea Editors => parts.Editors;

    private ProjectSession Session => parts.Session();

    /// <summary>The open folder's places: those left, those edited, and where Back and Forward are.</summary>
    private NavigationHistory Places => Session.Navigation;

    /// <summary>Goes to a file and position (go to definition, a usage), remembering where it came from for Back.</summary>
    public void NavigateTo(string path, TextPosition position)
    {
        HistoryPlace? from = Here();
        parts.OpenFile(path);
        // Opened files are known by their canonical path: the one given may run through a symlink.
        if (Editors.Active is { } active && active.Path == PathNames.Canonical(path))
        {
            Editors.CodeEditor.GoTo(position);
            if (from is not null && Here() is { } to && (to.Path != from.Path || to.Line != from.Line))
            {
                Places.Left(from);
            }
        }
    }

    /// <summary>The caret's place in the file shown, as the navigation history keeps it.</summary>
    private HistoryPlace? Here()
    {
        if (Editors.Active is not { } active)
        {
            return null;
        }
        int caret = Editors.CodeEditor.Selections.Primary.Caret;
        return new HistoryPlace(active.Path, caret, active.Text.Snapshot.GetLineFromOffset(caret));
    }

    private bool Reachable(string path) => Editors.Documents.Any(d => d.Path == path) || File.Exists(path);

    /// <summary>Back (a step of -1) or Forward (1): the caret goes to the place the history gives.</summary>
    public void StepHistory(int step)
    {
        HistoryPlace? here = Here();
        HistoryPlace? target = step < 0 ? Places.Back(here, Reachable) : Places.Forward(here, Reachable);
        if (target is null)
        {
            return;
        }
        parts.OpenFile(target.Path);
        if (Editors.Active is { } active && active.Path == target.Path)
        {
            Editors.CodeEditor.GoTo(active.Text.Snapshot.GetPosition(Math.Min(target.Offset, active.Text.Snapshot.Length)));
        }
    }

    /// <summary>Why Last Edit Location (a step of -1) or Next Edit Location (1) cannot go now: no edit that way.</summary>
    public string? WhyNotStepEdits(int step) => Places.CanStepEdits(step) ? null : step < 0 ? NoEditBefore : NoEditAfter;

    /// <summary>An edit to the file shown keeps the caret's place once the edit has put it there. A reload from disk,
    /// or what a save does to the text, leaves the file saved and keeps none.</summary>
    private void NoteEdit(OpenDocument document)
    {
        if (editNoted || Editors.Active != document)
        {
            return;
        }
        editNoted = true;
        Dispatcher.UIThread.Post(() =>
        {
            editNoted = false;
            if (Editors.Active == document && document.IsModified && Here() is { } here)
            {
                Places.Edited(here);
            }
        });
    }

    /// <summary>Last Edit Location (a step of -1) or Next Edit Location (1): the caret goes to the place edited before or
    /// after the one it went to last, Back coming back from there.</summary>
    public void StepEdits(int step)
    {
        if (Places.StepEdits(step, Here(), Reachable) is not { } place)
        {
            parts.Status.Text = step < 0 ? NoEditBefore : NoEditAfter;
            return;
        }
        GoToPlace(place);
    }

    /// <summary>Goes to a place the history kept, by its offset in the file as it is now, remembered for Back.</summary>
    private void GoToPlace(HistoryPlace place)
    {
        if (parts.TextOf(place.Path) is { } text)
        {
            NavigateTo(place.Path, text.GetPosition(Math.Min(place.Offset, text.Length)));
        }
    }

    /// <summary>Recent Locations: the places left and edited, each with its lines of code. Its own keys again while it
    /// is open turn Show edited only.</summary>
    public RecentLocationsPopup ShowRecentLocations()
    {
        if (parts.Popups.Open is RecentLocationsPopup open)
        {
            open.EditedOnly = !open.EditedOnly;
            return open;
        }
        Dictionary<string, (TextSnapshot Text, IHighlighter? Colours)> files = [];
        List<IDisposable> made = [];
        HistoryPlace? here = Here();
        NavigationHistory places = Places;
        RecentLocationsPopup popup = new(editedOnly => [.. places.Recent(editedOnly, here).Select(place => Location(place, files, made)).OfType<RecentLocation>().Take(RecentLocationsShown)],
            recentEditedOnly);
        popup.Forgot += (_, place) => places.Forget(place);
        popup.DetachedFromVisualTree += (_, _) =>
        {
            recentEditedOnly = popup.EditedOnly;
            foreach (IDisposable colours in made)
            {
                colours.Dispose();
            }
            made.Clear();
        };
        parts.Popups.Show(popup, row =>
        {
            if (row is not null)
            {
                GoToPlace(row.Place);
            }
        });
        return popup;
    }

    /// <summary>A place as Recent Locations shows it: its line and those either side, coloured as the file is (an open
    /// file's own colours, or a parse of a closed one's text, made once for each file), and what it is in; null for a
    /// file that can no longer be read.</summary>
    private RecentLocation? Location(HistoryPlace place, Dictionary<string, (TextSnapshot Text, IHighlighter? Colours)> files, List<IDisposable> made)
    {
        OpenDocument? open = Editors.Find(place.Path);
        if (!files.TryGetValue(place.Path, out (TextSnapshot Text, IHighlighter? Colours) file))
        {
            if (open is not null)
            {
                file = (open.Text.Snapshot, open.Highlighter);
            }
            else if (parts.TextOf(place.Path) is { } read)
            {
                TextDocument closed = new(read.ToString());
                IHighlighter? colours = Languages.Highlighters.For(Languages.Language.ForPath(place.Path), closed);
                if (colours is IDisposable disposable)
                {
                    made.Add(disposable);
                }
                file = (closed.Snapshot, colours);
            }
            else
            {
                return null;
            }
            files[place.Path] = file;
        }
        TextSnapshot text = file.Text;
        int offset = Math.Min(place.Offset, text.Length);
        int line = text.GetLineFromOffset(offset);
        int first = Math.Max(0, line - LinesAround);
        int last = Math.Min(text.LineCount - 1, line + LinesAround);
        int start = text.GetLineStart(first);
        int end = text.GetLine(last).Start + text.GetLine(last).Length;
        string? member = open?.Syntax is { } syntax ? Declarations.PathAt(syntax.Current, offset) : null;
        return new RecentLocation(place with { Offset = offset, Line = line }, Path.GetFileName(place.Path), member, first, text.GetText(start, end - start),
            file.Colours is { } whole ? new FragmentColours(whole, text, start) : null);
    }

    /// <summary>Go to File: the folder's files by name, the recent ones before anything is typed.</summary>
    public void ShowGoToFile()
    {
        ProjectSession session = Session;
        if (session.Folder is null)
        {
            return;
        }
        GoToFilePopup popup = new(session.Index, session.RecentFiles.Skip(Editors.Active is null ? 0 : 1).ToList());
        parts.Popups.Show(popup, hit =>
        {
            if (hit is not null && popup.At is { } at)
            {
                NavigateTo(hit.FullPath, at);
            }
            else if (hit is not null)
            {
                parts.OpenFile(hit.FullPath);
            }
        });
    }

    /// <summary>Go to Symbol: the solution's types and members by name or CamelHumps, as the language server finds them.</summary>
    public void ShowGoToSymbol() => ShowSymbols("Go to Symbol", "A type or member, by name or CamelHumps", SearchSymbols);

    /// <summary>Search Everywhere: types first, then files, then members.</summary>
    public void ShowGoToEverything() => ShowSymbols("Go to Everything", "A type, member or file", SearchEverything);

    private void ShowSymbols(string title, string placeholder, Func<string, CancellationToken, Task<IReadOnlyList<SymbolRow>>> search)
    {
        if (Session.Folder is null)
        {
            return;
        }
        symbolFiles = new(StringComparer.Ordinal);
        SymbolPopup popup = new(title, placeholder, search, SelectedWord());
        parts.Popups.Show(popup, row =>
        {
            if (row is not null)
            {
                NavigateTo(row.Path, row.ServerPosition is { } at ? ServerPlaces.PositionOf(Editors, row.Path, at) : row.Position);
            }
        });
    }

    /// <summary>What a search starts from: the editor's selection, if it is one word.</summary>
    public string? SelectedWord()
    {
        if (Editors.Active is null)
        {
            return null;
        }
        Selection primary = Editors.CodeEditor.Selections.Primary;
        string selected = primary.IsEmpty ? string.Empty : Editors.CodeEditor.Document.Snapshot.GetText(primary.Start, primary.Length);
        return selected.Length is > 0 and < 80 && selected.All(c => char.IsLetterOrDigit(c) || c == '_') ? selected : null;
    }

    private async Task<IReadOnlyList<SymbolRow>> SearchSymbols(string query, CancellationToken cancel)
    {
        if (query.Trim().Length == 0)
        {
            return [];
        }
        CSharpService? csharp = Session.CSharp;
        if (csharp?.Session is not { IsLoaded: true } server)
        {
            throw new Lsp.LanguageServerFailed(csharp is null ? Refusals.NoCSharp : $"the C# language server is not ready: {csharp.State}");
        }
        IReadOnlyList<Lsp.SymbolInformation> found = await server.SymbolsAsync(query.Trim(), cancel);
        string? root = Session.Folder;
        Dictionary<string, TextSnapshot> open = new(StringComparer.Ordinal);
        foreach (OpenDocument document in Editors.Documents)
        {
            open.TryAdd(document.Path, document.Text.Snapshot);
        }
        System.Collections.Concurrent.ConcurrentDictionary<string, Lsp.ServerLines?> files = symbolFiles;
        return await BackgroundWork.Run(() => (IReadOnlyList<SymbolRow>)[.. found.Where(s => Lsp.DocumentUris.GeneratedName(s.Location.Uri) is null).Take(200).Select(symbol =>
        {
            string path = Lsp.DocumentUris.ToPath(symbol.Location.Uri);
            Lsp.ServerLines? lines = open.TryGetValue(path, out TextSnapshot? text)
                ? ServerPlaces.Differing(text)
                : files.GetOrAdd(path, at => ServerPlaces.Differing(ServerPlaces.Read(at)));
            return SymbolRowOf(symbol, path, root, ServerPlaces.PositionOf(lines, symbol.Location.Range.Start));
        })], cancel);
    }

    /// <summary>Go to Everything: types first, then files, then members; the recent files before anything is
    /// typed.</summary>
    private async Task<IReadOnlyList<SymbolRow>> SearchEverything(string query, CancellationToken cancel)
    {
        ProjectSession session = Session;
        string root = session.Index.Root ?? session.Folder ?? string.Empty;
        if (query.Trim().Length == 0)
        {
            return [.. session.RecentFiles.Skip(Editors.Active is null ? 0 : 1).Select(path => FileRow(PathNames.Shown(path, root)))];
        }
        (string named, TextPosition? at) = GoToFilePopup.PlaceIn(query);
        IReadOnlyList<SymbolRow> symbols = [];
        try
        {
            symbols = session.CSharp?.Session is { IsLoaded: true } ? await SearchSymbols(named, cancel) : [];
        }
        catch (Lsp.LanguageServerFailed)
        {
            // The files still match without the server.
        }
        IEnumerable<SymbolRow> files = session.Index.Search(named).Take(20).Select(relative => FileRow(relative, at));
        static bool IsType(Lsp.SymbolKind kind) => kind is Lsp.SymbolKind.Class or Lsp.SymbolKind.Enum or Lsp.SymbolKind.Interface or Lsp.SymbolKind.Struct;
        return [.. symbols.Where(r => IsType(r.Kind)), .. files, .. symbols.Where(r => !IsType(r.Kind))];

        SymbolRow FileRow(string relative, TextPosition? place = null) =>
            new(Path.GetFileName(relative), null, Path.GetDirectoryName(relative), SymbolRow.FileKind, Path.Combine(root, relative), place ?? new TextPosition(0, 0));
    }

    /// <summary>A symbol's row, at <paramref name="at"/>: where the server put it, as the editor counts its file's lines.</summary>
    private static SymbolRow SymbolRowOf(Lsp.SymbolInformation symbol, string path, string? root, TextPosition at)
    {
        string where = PathNames.Shown(path, root);
        return new SymbolRow(symbol.Name, Container(symbol.ContainerName), $"{where}:{at.Line + 1}", symbol.Kind, path, at)
        {
            ServerPosition = symbol.Location.Range.Start,
        };
    }

    /// <summary>A server's container ("in Greeter (project Greetings (net10.0))") as a row names it: the type, or the
    /// project for a type.</summary>
    private static string? Container(string? container)
    {
        if (string.IsNullOrEmpty(container))
        {
            return null;
        }
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(container, @"^(in )?(?<owner>.*?)\s*\(?project (?<project>[^()]+?)\s*(\([^)]*\))?\)?$");
        if (!match.Success)
        {
            return container;
        }
        string owner = match.Groups["owner"].Value.Trim();
        return owner.Length > 0 ? owner : match.Groups["project"].Value.Trim();
    }

    /// <summary>File Structure: the active file's types and members, indented, filtered as typed with the containers of
    /// what matches kept.</summary>
    public void ShowFileStructure()
    {
        if (Editors.Active is not { } active || Session.CSharp?.For(active)?.Lsp is null)
        {
            parts.Status.Text = "File Structure needs a C# file the language server has open";
            return;
        }
        IReadOnlyList<SymbolRow>? outline = null;
        int caretLine = Editors.CodeEditor.CaretPosition.Line;
        SymbolPopup popup = new($"File Structure: {active.Name}", "Filter by name, CamelHumps too", async (query, cancel) =>
        {
            // The server's document now: one started again while the popup is open has a new one.
            Lsp.LspDocument lsp = Session.CSharp?.For(active)?.Lsp ?? throw new Lsp.LanguageServerFailed("the C# language server is not running");
            outline ??= ToolWindows.StructureView.Flatten(await lsp.OutlineAsync(cancel), 0, active.Path, Lsp.ServerLines.Of(lsp.Text.Snapshot));
            return FilterOutline(outline, query.Trim());
        })
        {
            // The innermost member the caret is in.
            Initial = rows => rows.Select((row, i) => (row, i)).LastOrDefault(p => p.row.Spans(caretLine), (null!, 0)).Item2,
        };
        parts.Popups.Show(popup, row =>
        {
            if (row is not null)
            {
                NavigateTo(row.Path, row.Position);
            }
        });
    }

    private static IReadOnlyList<SymbolRow> FilterOutline(IReadOnlyList<SymbolRow> outline, string query)
    {
        if (query.Length == 0)
        {
            return outline;
        }
        List<SymbolRow> shown = [];
        List<SymbolRow> path = [];
        foreach (SymbolRow row in outline)
        {
            while (path.Count > 0 && path[^1].Depth >= row.Depth)
            {
                path.RemoveAt(path.Count - 1);
            }
            bool matches = row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || FuzzyMatch.Score(query, row.Name) is not null;
            if (matches)
            {
                // The containers of a match stay, so it reads in its place.
                foreach (SymbolRow container in path.Where(c => !shown.Contains(c)))
                {
                    shown.Add(container);
                }
                shown.Add(row);
            }
            path.Add(row);
        }
        return shown;
    }

    /// <summary>Go to Line:Column in the file shown.</summary>
    public void ShowGoToLine()
    {
        if (Editors.Active is null)
        {
            return;
        }
        CodeEditor editor = Editors.CodeEditor;
        GoToLinePopup popup = new(editor.CaretPosition, editor.Document.Snapshot.LineCount);
        parts.Popups.Show(popup, position =>
        {
            if (position is { } at)
            {
                editor.GoTo(at);
            }
        });
    }

    /// <summary>A file's colours over a stretch of it, shifted to the stretch's own offsets, for a place's lines drawn
    /// apart from the file.</summary>
    private sealed class FragmentColours(IHighlighter whole, TextSnapshot text, int start) : IHighlighter
    {
        public event EventHandler? Changed
        {
            add => whole.Changed += value;
            remove => whole.Changed -= value;
        }

        public IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot fragment, int from, int length)
        {
            List<SyntaxSpan> spans = [];
            foreach (SyntaxSpan span in whole.GetSpans(text, start + from, Math.Max(0, Math.Min(length, text.Length - start - from))))
            {
                int spanStart = Math.Max(span.Start - start, 0);
                int spanEnd = Math.Min(span.End - start, fragment.Length);
                if (spanEnd > spanStart)
                {
                    spans.Add(new SyntaxSpan(spanStart, spanEnd - spanStart, span.Kind));
                }
            }
            return spans;
        }
    }
}
