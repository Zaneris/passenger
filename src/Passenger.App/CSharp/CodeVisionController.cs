using System.Diagnostics;
using Passenger.App.Editors;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.CSharp;

/// <summary>What the window gives Code Vision to read and to do: the editors, the language server, Git's blame of a text, an
/// action run by its id, a place to go to and a commit to show.</summary>
internal sealed record CodeVisionParts(EditorArea Editors, Func<CSharpService?> CSharp, Func<OpenDocument, TextSnapshot, Task<IReadOnlyList<BlameLine>?>> Blame,
    Action<string> Perform, Action<LspLocation> Arrive, Func<BlameLine, Task> ShowCommit);

/// <summary>What a Code Vision hint says.</summary>
internal enum VisionKind
{
    Usages,
    Inheritors,
    Authors,
}

/// <summary>What a hint is known by (the data of its annotation): its declaration's name and column on the line it was made
/// for, and what pressing it needs: the one place a declaration is used, or the newest commit of its authors with the words
/// of its hover card.</summary>
internal sealed record VisionHint(VisionKind Kind, string Name, int Column, LspLocation? Only = null, BlameLine? Commit = null, string? Words = null);

/// <summary>Code Vision: after the line of each C# type and member shown, how many places use it, what implements or overrides
/// it and who changed it most (Git blame), each a hint pressed to see them. Read only for the lines on screen and a few beyond,
/// after typing and scrolling pause, one request of the server at a time; what was read stays through edits until it is
/// read again (once the file is saved, or its numbers are a while old), so typing never takes a hint away. A UI-thread object.</summary>
internal sealed class CodeVisionController
{
    /// <summary>How long typing and scrolling pause before the lines shown are read.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(800);

    /// <summary>How long before a server that is not ready, or a file it does not have yet, is asked again.</summary>
    private static readonly TimeSpan NotReady = TimeSpan.FromSeconds(3);

    /// <summary>How old the numbers of an edited file are let to grow before they are read again, a save reading them at once.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    /// <summary>How many lines beyond those on screen are read, so a short scroll shows hints at once.</summary>
    private const int Beyond = 10;

    private readonly CodeVisionParts parts;
    private readonly Debounce soon;
    private readonly Debounce later;
    private readonly Dictionary<OpenDocument, Memory> memories = [];
    private AppSettings settings;
    private CancellationTokenSource? running;

    public CodeVisionController(CodeVisionParts parts, AppSettings settings)
    {
        this.parts = parts;
        this.settings = settings;
        soon = new Debounce(Pause, RefreshAsync);
        later = new Debounce(NotReady, () =>
        {
            Changed();
            return Task.CompletedTask;
        });
        parts.Editors.ActiveChanged += (_, _) => Changed();
        parts.Editors.Opened += (_, document) => Track(document);
        parts.Editors.Closed += (_, document) => Forget(document);
        parts.Editors.ForEachEditor(editor =>
        {
            editor.ScrollChanged += (_, _) => Changed();
            editor.AnnotationPressed += (_, note) => Pressed(editor, note);
        });
        foreach (OpenDocument document in parts.Editors.Documents)
        {
            Track(document);
        }
    }

    /// <summary>What was read of one file: each declaration's numbers by its key, and the file's blame with the version of
    /// the text it was made for.</summary>
    private sealed class Memory(EventHandler<DocumentChangedEventArgs> onChanged, EventHandler onSaved)
    {
        public EventHandler<DocumentChangedEventArgs> OnChanged { get; } = onChanged;

        public EventHandler OnSaved { get; } = onSaved;

        public Dictionary<string, Counted> Counts { get; } = [];

        public IReadOnlyList<BlameLine>? Blame { get; set; }

        public int BlameVersion { get; set; } = -1;

        /// <summary>Whether the file was saved since its numbers were last read: they are all read again, however new.</summary>
        public bool Saved { get; set; }
    }

    /// <summary>What was read of a declaration: how many places use it (and the one, where it is one), how many implement or
    /// override it, for the version of the text and when (<see cref="Stopwatch.GetTimestamp"/>).</summary>
    private sealed record Counted(int? Usages, LspLocation? Only, int? Inheritors, int Version, long At);

    /// <summary>Takes in the settings as they stand: reads again where Code Vision's changed.</summary>
    public void Follow(AppSettings next)
    {
        UiThread.Check();
        bool same = settings.CodeVision == next.CodeVision && settings.CodeVisionUsages == next.CodeVisionUsages
            && settings.CodeVisionInheritors == next.CodeVisionInheritors && settings.CodeVisionAuthors == next.CodeVisionAuthors;
        settings = next;
        if (same)
        {
            return;
        }
        if (IsOn)
        {
            Changed();
            return;
        }
        // Turned off: nothing waits, and every hint goes.
        running?.Cancel();
        running = null;
        soon.Stop();
        later.Stop();
        Clear();
    }

    /// <summary>Whether Code Vision is on with some kind of hint to show; off, nothing here waits for or reads anything.</summary>
    private bool IsOn => settings.CodeVision && (settings.CodeVisionUsages || settings.CodeVisionInheritors || settings.CodeVisionAuthors);

    private void Track(OpenDocument document)
    {
        if (memories.ContainsKey(document))
        {
            return;
        }
        Memory memory = new((_, _) => Changed(), (_, _) =>
        {
            if (memories.TryGetValue(document, out Memory? saved))
            {
                saved.Saved = true;
            }
            Changed();
        });
        memories[document] = memory;
        document.Text.Changed += memory.OnChanged;
        document.Saved += memory.OnSaved;
    }

    private void Forget(OpenDocument document)
    {
        if (memories.Remove(document, out Memory? memory))
        {
            document.Text.Changed -= memory.OnChanged;
            document.Saved -= memory.OnSaved;
            document.CodeVision = [];
        }
    }

    /// <summary>What was read is out of date, or what is shown moved: whatever is being read stops, and the lines shown are read
    /// once typing and scrolling pause.</summary>
    private void Changed()
    {
        running?.Cancel();
        running = null;
        if (IsOn)
        {
            soon.Restart();
        }
    }

    /// <summary>The hints of every file taken away.</summary>
    private void Clear()
    {
        foreach (OpenDocument document in memories.Keys)
        {
            if (document.CodeVision.Count > 0)
            {
                document.CodeVision = [];
            }
        }
    }

    private async Task RefreshAsync()
    {
        UiThread.Check();
        AppSettings now = settings;
        if (!IsOn)
        {
            Clear();
            return;
        }
        if (parts.Editors.Active is not { } document || document.Language != Passenger.Languages.Language.CSharp || document.Syntax is not { } syntax
            || !memories.TryGetValue(document, out Memory? memory) || parts.Editors.CodeEditor is not { } editor || editor.Document != document.Text)
        {
            return;
        }
        CSharpService? csharp = parts.CSharp();
        if (csharp is null)
        {
            return;
        }
        if (!csharp.IsReady || csharp.For(document)?.Lsp is not { } lsp)
        {
            // The server loads its projects first, and a file it has yet to take in has no hints to show.
            later.Restart();
            return;
        }
        CancellationTokenSource mine = running = new CancellationTokenSource();
        // Held, so a screenshot or a test settles on the hints rather than on the window before they came.
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            await ReadAsync(document, syntax, memory, lsp, editor.LinesShown, now, mine.Token);
        }
        catch (OperationCanceledException)
        {
            // Typed or scrolled since, or the file closed: the pause after it reads again.
        }
        catch (LanguageServerFailed)
        {
            // What is shown stands; the server's restart or the next change asks afresh.
            later.Restart();
        }
        finally
        {
            if (running == mine)
            {
                running = null;
            }
        }
    }

    /// <summary>Reads the file's declarations and blame as its text stands, then the numbers of those near the lines shown,
    /// showing the hints after each, until the text changes (which has the next pause read it again).</summary>
    private async Task ReadAsync(OpenDocument document, CSharpHighlighter syntax, Memory memory, LspDocument lsp, (int First, int Last) shown,
        AppSettings now, CancellationToken cancel)
    {
        TextSnapshot text = document.Text.Snapshot;
        int version = document.Text.Version;
        ParsedText parsed = await syntax.CurrentAsync();
        if (!ReferenceEquals(parsed.Text, text) || !ReferenceEquals(document.Text.Snapshot, text))
        {
            // The parse of the text as it stands is on its way.
            later.Restart();
            return;
        }
        IReadOnlyList<VisionDeclaration> all = await BackgroundWork.Run(() => VisionDeclarations.Of(parsed), cancel);
        if (now.CodeVisionAuthors && memory.BlameVersion != version)
        {
            memory.Blame = await parts.Blame(document, text);
            memory.BlameVersion = version;
        }
        cancel.ThrowIfCancellationRequested();
        if (!ReferenceEquals(document.Text.Snapshot, text))
        {
            return;
        }
        Publish(document, memory, all, text, now);
        foreach (VisionDeclaration declaration in all)
        {
            if (declaration.Line < shown.First - Beyond || declaration.Line > shown.Last + Beyond)
            {
                continue;
            }
            Counted? had = memory.Counts.GetValueOrDefault(declaration.Key);
            if (!Needs(had, declaration, memory, version, now))
            {
                continue;
            }
            cancel.ThrowIfCancellationRequested();
            if (!ReferenceEquals(document.Text.Snapshot, text))
            {
                return;
            }
            memory.Counts[declaration.Key] = await CountAsync(lsp, declaration, had, (text, version), now, cancel);
            if (!ReferenceEquals(document.Text.Snapshot, text))
            {
                return;
            }
            Publish(document, memory, all, text, now);
        }
        memory.Saved = false;
    }

    /// <summary>Whether a declaration's numbers are to be read: none yet, a kind shown that was not read, or edited text with
    /// them a while old or the file saved.</summary>
    private static bool Needs(Counted? had, VisionDeclaration declaration, Memory memory, int version, AppSettings now)
    {
        if (had is null || now.CodeVisionUsages && had.Usages is null
            || now.CodeVisionInheritors && declaration.Heritage != Heritage.None && had.Inheritors is null)
        {
            return true;
        }
        return had.Version != version && (memory.Saved || Stopwatch.GetElapsedTime(had.At) > StaleAfter);
    }

    private static async Task<Counted> CountAsync(LspDocument lsp, VisionDeclaration declaration, Counted? had, (TextSnapshot Text, int Version) read,
        AppSettings now, CancellationToken cancel)
    {
        int? usages = had?.Usages;
        LspLocation? only = had?.Only;
        int? inheritors = had?.Inheritors;
        LspPosition at = ServerLines.Of(read.Text).PositionOf(declaration.NameOffset);
        if (now.CodeVisionUsages)
        {
            // The declaration is among the places the server names, which is no use of it.
            List<LspLocation> used = [.. (await lsp.ReferencesAsync(declaration.NameOffset, cancel)).Where(l => l.Range.Start != at || !string.Equals(l.Uri, lsp.Identifier.Uri, StringComparison.OrdinalIgnoreCase))];
            usages = used.Count;
            only = used.Count == 1 ? used[0] : null;
        }
        if (now.CodeVisionInheritors && declaration.Heritage != Heritage.None)
        {
            inheritors = (await lsp.ImplementationAsync(declaration.NameOffset, cancel)).Count;
        }
        return new Counted(usages, only, inheritors, read.Version, Stopwatch.GetTimestamp());
    }

    /// <summary>Shows the hints of every declaration there is a number or an author for, after its line.</summary>
    private static void Publish(OpenDocument document, Memory memory, IReadOnlyList<VisionDeclaration> all, TextSnapshot text, AppSettings now)
    {
        List<LineAnnotation> notes = [];
        foreach (VisionDeclaration declaration in all)
        {
            int column = declaration.NameOffset - text.GetLineStart(declaration.Line);
            Counted? counted = memory.Counts.GetValueOrDefault(declaration.Key);
            if (now.CodeVisionUsages && counted?.Usages is { } usages)
            {
                notes.Add(new LineAnnotation(declaration.Line, usages == 0 ? "no usages" : Plural.Of(usages, "usage"), LineAnnotationLook.Vision,
                    new VisionHint(VisionKind.Usages, declaration.Name, column, Only: counted.Only)));
            }
            if (now.CodeVisionInheritors && declaration.Heritage != Heritage.None && counted?.Inheritors is { } inheritors)
            {
                notes.Add(new LineAnnotation(declaration.Line, Inheriting(declaration.Heritage, inheritors), LineAnnotationLook.Vision,
                    new VisionHint(VisionKind.Inheritors, declaration.Name, column)));
            }
            if (now.CodeVisionAuthors && memory.Blame is { } blame && BlameAuthors.Of(blame, declaration.FirstLine, declaration.LastLine) is { } authors)
            {
                notes.Add(new LineAnnotation(declaration.Line, authors.Label, LineAnnotationLook.Vision,
                    new VisionHint(VisionKind.Authors, declaration.Name, column, Commit: authors.IsCommitted ? authors.Latest : null, Words: Words(authors))));
            }
        }
        document.CodeVision = notes;
    }

    /// <summary>What a declaration's implementations or overrides are called, by what it is.</summary>
    private static string Inheriting(Heritage heritage, int count)
    {
        string noun = heritage switch
        {
            Heritage.Overrides => "override",
            Heritage.Inheritors => "inheritor",
            _ => "implementation",
        };
        return count == 0 ? $"no {noun}s" : Plural.Of(count, noun);
    }

    /// <summary>The commit an author hint's card names: who made it and when, and its subject.</summary>
    private static string Words(StretchAuthors authors)
    {
        if (!authors.IsCommitted)
        {
            return "Not committed yet";
        }
        BlameLine commit = authors.Latest;
        return $"{commit.Short} {commit.Summary}\n{commit.Author}, {commit.Time.ToLocalTime():f}\nPress to show it in the Git window";
    }

    /// <summary>The card for a hint the pointer rests on: what pressing it does, and for the authors the newest commit.</summary>
    public bool Card(OpenDocument document, LineAnnotation note, IHoverCard card)
    {
        if (note.Data is not VisionHint hint || !memories.ContainsKey(document))
        {
            return false;
        }
        card.AddText(hint.Kind switch
        {
            VisionKind.Usages => hint.Only is not null ? "Go to the one place it is used" : "Show where it is used",
            VisionKind.Inheritors => "Go to what implements or overrides it",
            _ => hint.Words ?? string.Empty,
        });
        return true;
    }

    /// <summary>A hint pressed: usages go to their one place or are listed in a popup at the declaration, implementations as
    /// Go to Implementation does, the authors' commit in the Git window.</summary>
    private void Pressed(CodeEditor editor, LineAnnotation note)
    {
        if (note.Data is not VisionHint hint || parts.Editors.Active is not { } document || editor.Document != document.Text)
        {
            return;
        }
        if (hint.Kind == VisionKind.Authors)
        {
            if (hint.Commit is { } commit)
            {
                IdeErrors.Observe(parts.ShowCommit(commit));
            }
            return;
        }
        if (hint.Kind == VisionKind.Usages && hint.Only is { } only)
        {
            parts.Arrive(only);
            return;
        }
        if (NameOffset(document.Text.Snapshot, note.Line, hint) is not { } at)
        {
            return;
        }
        editor.Selections = SelectionSet.Single(at);
        parts.Perform(hint.Kind == VisionKind.Usages ? AppActions.ShowUsages : AppActions.GoToImplementation);
    }

    /// <summary>Where a hint's declaration is named on its line now: its column when the name is still there, else the first
    /// whole word that is it; none when the line no longer names it.</summary>
    private static int? NameOffset(TextSnapshot text, int line, VisionHint hint)
    {
        if (line >= text.LineCount)
        {
            return null;
        }
        int start = text.GetLineStart(line);
        string code = text.GetLineText(line);
        if (hint.Column >= 0 && hint.Column + hint.Name.Length <= code.Length && string.CompareOrdinal(code, hint.Column, hint.Name, 0, hint.Name.Length) == 0)
        {
            return start + hint.Column;
        }
        for (int at = code.IndexOf(hint.Name, StringComparison.Ordinal); at >= 0; at = code.IndexOf(hint.Name, at + 1, StringComparison.Ordinal))
        {
            int end = at + hint.Name.Length;
            if ((at == 0 || !AssistContext.IsIdentifierChar(code[at - 1])) && (end >= code.Length || !AssistContext.IsIdentifierChar(code[end])))
            {
                return start + at;
            }
        }
        return null;
    }
}
