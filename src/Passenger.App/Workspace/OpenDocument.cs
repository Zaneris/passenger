using System.Text;
using Avalonia;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages;
using Passenger.Languages.CSharp;
using Passenger.Languages.Markup;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>A file open in a tab: its text and storage, or a read-only asset, with the view left so switching tabs
/// returns to the same carets and scroll. An asset is shared by the file's tabs and released with its last view or hold.</summary>
public sealed class OpenDocument : Observable
{
    private IHighlighter? parser;
    private IHighlighter? highlighter;
    private IReadOnlyList<Marker> markers = [];
    private IReadOnlyList<string> projectContexts = [];
    private string? projectContext;
    private IReadOnlyList<LineMark> lineMarks = [];
    private IReadOnlyList<LineAnnotation> lineAnnotations = [];
    private IReadOnlyList<LineAnnotation> liveValues = [];

    /// <summary>The line of each of <see cref="lineAnnotations"/> and of <see cref="liveValues"/>, carried by the text.</summary>
    private AnchoredLine[] annotationLines = [];
    private AnchoredLine[] liveLines = [];
    private IReadOnlyList<InlayHint> inlayHints = [];
    private IReadOnlyList<TestMark> testMarks = [];
    private IReadOnlyList<ColourMark> colourMarks = [];
    private IReadOnlyList<ChangeMark> changeMarks = [];
    private IReadOnlyList<string?> gutterAnnotations = [];
    private IReadOnlyList<int> bookmarks = [];
    private IReadOnlyList<FoldRegion> foldRegions = [];
    private IFoldFinder? finder;
    private bool softWrap;
    private bool showWhitespaces;

    /// <summary>The indentation the file's own lines show, found when its text came from disk; null when they do not tell.</summary>
    private IndentOptions? detected;

    /// <summary>A CR file: each break, held as '\n', is written as '\r'.</summary>
    private bool crBreaks;

    /// <summary>The person chose the line separator, which a save keeps over the .editorconfig's <c>end_of_line</c>.</summary>
    private bool separatorChosen;

    /// <summary>The person chose the encoding, which a save keeps over the .editorconfig's <c>charset</c> and a read from
    /// disk tries first.</summary>
    private bool encodingChosen;

    /// <summary>The image or font owned by this document, null for text: a stand-in (<see cref="AssetPreview.IsPending"/>)
    /// until its first decode lands, then the decoded one, replaced only after a successful reload.</summary>
    public AssetPreview? Asset { get; private set; }

    /// <summary>The asset's decode under way off the UI thread, until what it came to has landed.</summary>
    private Task? decoding;

    /// <summary>A reload asked for while a decode was under way: one more follows it rather than running beside it.</summary>
    private bool decodeAgain;

    /// <summary>Closed, so a decode that lands after lets go of what it decoded.</summary>
    private bool closed;

    /// <summary>Why the asset's first decode failed, said once: its tabs close, as a file that could not be opened.</summary>
    public event EventHandler<string>? PreviewFailed;

    /// <summary>Why the asset's first decode failed, once it has; null while it shows or loads, and for text.</summary>
    public string? PreviewFailure { get; private set; }

    /// <summary>Whether the asset is being decoded off the UI thread: its first time (the tab says it is loading) or again.</summary>
    public bool IsDecoding => decoding is { IsCompleted: false };

    /// <summary>Waits until no decode of the asset is under way, a reload's that followed another's too.</summary>
    public async Task DecodedAsync()
    {
        while (decoding is { IsCompleted: false } under)
        {
            await under;
        }
    }

    /// <summary>Opens a supported image or font as a read-only asset with an empty text placeholder, otherwise as text.
    /// An asset's tab shows it loading until its decode lands off the UI thread; a missing, empty or oversized one is
    /// refused at once. Other binary content opens as its bytes' text, read-only and never written
    /// (<see cref="FileText.Bytes"/>), or, unless <paramref name="asBytes"/> (for what would edit it), is refused with an
    /// <see cref="IOException"/>, as is one past <see cref="LargestText"/>.</summary>
    public static OpenDocument Load(string path, bool asBytes = true)
    {
        if (AssetPreview.KindOf(path) is { } kind)
        {
            OpenDocument asset = new(path, new FileText(string.Empty, new UTF8Encoding(false), false))
            {
                Asset = AssetPreview.Pending(path),
                Language = kind == AssetKind.Font ? Language.Font : Language.Image,
            };
            asset.Decode();
            return asset;
        }
        // A workbook's tab holds no text, since its table is read from the file itself.
        if (Tables.Workbook.IsWorkbook(path))
        {
            return new OpenDocument(path, new FileText(string.Empty, new UTF8Encoding(false), false));
        }
        string name = System.IO.Path.GetFileName(path);
        DiskStamp stamp = DiskStamp.Of(new FileInfo(path));
        long length = stamp.Length;
        if (length > LargestText)
        {
            throw new IOException($"{name} is {length / (1024.0 * 1024):0.#} MB: the editor opens a file of up to {LargestText / (1024 * 1024)} MB as text");
        }
        FileText file = FileText.Load(path) ?? (asBytes ? FileText.Bytes(path) : throw new IOException($"{name} looks like a binary file."));
        OpenDocument document = new(path, file);
        document.ReadAt(stamp);
        return document;
    }

    /// <summary>The file's length and last write time as found just before the editor last read all of it, with the version of
    /// the text it then held: while both hold, the editor holds what the disk does (<see cref="TakeDiskTextAsync"/>).</summary>
    private (DiskStamp Stamp, int Version)? readAt;

    private void ReadAt(DiskStamp? stamp) => readAt = stamp is { } found ? (found, Text.Version) : null;

    /// <summary>The largest file opened as text or as its bytes: the editor holds about six and a half times a file's
    /// size, and .NET holds no string past a billion characters.</summary>
    public const long LargestText = 64 * 1024 * 1024;

    /// <summary>Decodes the asset off the UI thread, the one shown (the stand-in at first) kept until the new one lands;
    /// while one decode is under way, one more follows it, so a file written in several steps is read once more, never
    /// twice at once.</summary>
    private void Decode()
    {
        if (IsDecoding)
        {
            decodeAgain = true;
            return;
        }
        decoding = DecodeAsync();
    }

    /// <summary>One decode and what it came to: the new asset in place of the one shown, or why not, said in the banner
    /// over a picture already shown, or as <see cref="PreviewFailed"/> over the stand-in.</summary>
    private async Task DecodeAsync()
    {
        string path = Path;
        AssetPreview? decoded = null;
        string? failed = null;
        try
        {
            decoded = await BackgroundWork.Run(() => AssetPreview.Load(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failed = error.Message;
        }
        catch (Exception error)
        {
            // A fault of the IDE's rather than the file's: reported, and the tab never left loading for good.
            IdeErrors.Report(error);
            failed = $"Passenger failed: {error.GetType().Name}: {error.Message}";
        }
        // Moved meanwhile, it is read again where it is now.
        if (closed || decodeAgain || path != Path)
        {
            decoded?.Dispose();
            if (!closed)
            {
                decodeAgain = false;
                decoding = DecodeAsync();
            }
            return;
        }
        AssetPreview shown = Asset!;
        if (decoded is not null)
        {
            Asset = decoded;
            Raise(nameof(Asset));
            shown.Dispose();
            Notice = null;
        }
        else if (!shown.IsPending)
        {
            Notice = $"Preview reload failed; showing the previous version: {failed}";
        }
        else
        {
            PreviewFailure = failed;
            PreviewFailed?.Invoke(this, failed!);
        }
    }

    /// <summary>Opens text with the encoding and line endings read from disk, and the conventions its path supplies.</summary>
    public OpenDocument(string path, FileText file)
    {
        Path = path;
        Text = new TextDocument(file.Text);
        Encoding = file.Encoding;
        HasBom = file.HasBom;
        EncodingName = file.EncodingName;
        crBreaks = file.CrBreaks;
        IsBinary = file.IsBinary;
        Language = IsBinary ? Language.PlainText : Language.ForPath(path);
        detected = Language.Detect(file.Text);
        Conventions = EditorConfig.For(path);
        Indentation = Choose();
        FollowLineEnding(file.Text.Contains('\n', StringComparison.Ordinal));
        parser = Parser();
        highlighter = parser;
        finder = Finder();
        Text.Changed += (_, change) =>
        {
            // Ahead of what moves with the change (the caret, then the breadcrumbs), which found afresh would read the file.
            ServerLines.After(change);
            Selections = TextEditing.MapThrough(Selections, change.Edits).Clamp(change.After.Length);
            if (foldRegions.Count > 0)
            {
                // Moved where the edit reaches them, the runs of regions past it shifted whole.
                FoldRegions = FoldList.Of(foldRegions).Through(change.Edits);
            }
            if (inlayHints.Count > 0)
            {
                // The same way: each editor has carried its own already, which this draws alike, so none wraps a line again.
                InlayHints = HintList.Of(inlayHints).Through(change.Edits);
            }
            if (lineAnnotations.Count > 0)
            {
                Set(ref lineAnnotations, Carried(lineAnnotations, annotationLines), nameof(LineAnnotations));
            }
            if (liveValues.Count > 0)
            {
                Set(ref liveValues, Carried(liveValues, liveLines), nameof(LiveValues));
            }
            FollowHeld(change.Edits);
            Raise(nameof(IsModified));
        };
    }

    private string? encodingWarning;

    /// <summary>What the editor's banner says once the text holds a character the file's encoding cannot (windows-1252
    /// has no '→'): that the next save writes the file as UTF-8, said before that save changes every other non-ASCII
    /// byte of it. Null while the encoding holds the whole text.</summary>
    public string? EncodingWarning
    {
        get => encodingWarning;
        private set => Set(ref encodingWarning, value);
    }

    /// <summary>Follows whether the encoding holds the text: only what an edit put in while it does, the whole text while
    /// it does not (so taking the character out again takes the warning away) or when <paramref name="edits"/> is null.</summary>
    private void FollowHeld(IReadOnlyList<TextEdit>? edits)
    {
        if (Encoding is UTF8Encoding or UnicodeEncoding or UTF32Encoding)
        {
            EncodingWarning = null;
            return;
        }
        string? unheld = null;
        if (encodingWarning is null && edits is not null)
        {
            foreach (TextEdit edit in edits)
            {
                if (edit.Text.Length > 0 && FileText.Unheld(Encoding, edit.Text) is { } first)
                {
                    unheld = first;
                    break;
                }
            }
        }
        else
        {
            unheld = FileText.Unheld(Encoding, Text.Snapshot.ToString());
        }
        EncodingWarning = unheld is null ? null : $"{EncodingName} cannot hold '{unheld}': the next save writes {Name} as UTF-8";
    }

    /// <summary>Raised after a save, so the language server hears of it.</summary>
    public event EventHandler? Saved;

    /// <summary>A parse of the C# text as it stands landed (<see cref="CSharpHighlighter.Parsed"/>), for work on its
    /// syntax off the UI thread.</summary>
    public event EventHandler<ParsedText>? Parsed;

    /// <summary>The file's C# syntax, parsed as it is typed, which everything else reading it shares rather than parse
    /// the file again; null for a file that is not C#.</summary>
    public CSharpHighlighter? Syntax => parser as CSharpHighlighter;

    /// <summary>Raised when a save wrote the file as UTF-8 because its own encoding could not hold the text, with what
    /// the status bar says of it.</summary>
    public event EventHandler<string>? Converted;

    /// <summary>Raised when an undo or redo of a line separator or encoding change could not write the file, with what the
    /// status bar says; the file is left modified, for the next save to try again.</summary>
    public event EventHandler<string>? WriteFailed;

    /// <summary>Colours from the in-process parser, replaced by the language server's layered over it
    /// once that is running; null for a language with neither.</summary>
    public IHighlighter? Highlighter
    {
        get => highlighter;
        set => Set(ref highlighter, value);
    }

    /// <summary>The language service's markers (diagnostics), in this text's offsets.</summary>
    public IReadOnlyList<Marker> Markers
    {
        get => markers;
        set => Set(ref markers, value);
    }

    private int checks;

    /// <summary>Whether the file's own checks are under way, or waiting for typing to pause or for their server to start
    /// (XAML's, an MSBuild file's, a web language server's, ESLint's), which an agent's diagnostics waits out rather than
    /// all of the IDE's work.</summary>
    public bool IsChecking => checks > 0;

    /// <summary>The file's own checks are all done again (<see cref="IsChecking"/> false), on the UI thread: what an agent's
    /// diagnostics waits on.</summary>
    public event EventHandler? Checked;

    /// <summary>Holds <see cref="IsChecking"/> until the scope ends, on the UI thread.</summary>
    public IDisposable Checking()
    {
        UiThread.Check();
        checks++;
        return new CheckHeld(this);
    }

    private sealed class CheckHeld(OpenDocument document) : IDisposable
    {
        private bool released;

        public void Dispose()
        {
            if (!released)
            {
                released = true;
                if (--document.checks == 0)
                {
                    document.Checked?.Invoke(document, EventArgs.Empty);
                }
            }
        }
    }

    private string? notice;

    /// <summary>What the editor's banner says of the file beyond where it comes from: why its language server cannot
    /// run for it (no Node.js); null for nothing.</summary>
    public string? Notice
    {
        get => notice;
        set => Set(ref notice, value);
    }

    private NoticeAction? noticeAction;

    /// <summary>What the banner offers to do about its notice, as a link after it (Run 'npm install'); null for nothing.</summary>
    public NoticeAction? NoticeAction
    {
        get => noticeAction;
        set => Set(ref noticeAction, value);
    }

    /// <summary>The projects the language server compiles the file in (a target framework of a multi-targeted project
    /// each), by the server's names for them; none while it has not said, or for a file it does not know.</summary>
    public IReadOnlyList<string> ProjectContexts
    {
        get => projectContexts;
        set => Set(ref projectContexts, value);
    }

    /// <summary>The one of <see cref="ProjectContexts"/> the server answers for the file in: its colours, diagnostics and
    /// everything else asked of it.</summary>
    public string? ProjectContext
    {
        get => projectContext;
        set => Set(ref projectContext, value);
    }

    /// <summary>The debugger's marks in the gutter: breakpoints and the execution point.</summary>
    public IReadOnlyList<LineMark> LineMarks
    {
        get => lineMarks;
        set => Set(ref lineMarks, value);
    }

    /// <summary>The tests' run icons beside the test methods and classes it declares.</summary>
    public IReadOnlyList<TestMark> TestMarks
    {
        get => testMarks;
        set => Set(ref testMarks, value);
    }

    /// <summary>The run icons beside what a run configuration runs from it: a compose file's services, a Dockerfile's image.</summary>
    public IReadOnlyList<RunMark> RunMarks
    {
        get => runMarks;
        set => Set(ref runMarks, value);
    }

    private IReadOnlyList<RunMark> runMarks = [];

    /// <summary>The icons beside the web routes it declares, each sending its route's request; drawn as the run icons are.</summary>
    public IReadOnlyList<RunMark> RouteMarks
    {
        get => routeMarks;
        set => Set(ref routeMarks, value);
    }

    private IReadOnlyList<RunMark> routeMarks = [];

    /// <summary>Every run icon its gutter draws: a run configuration's and a route's.</summary>
    public IReadOnlyList<RunMark> GutterRunMarks => routeMarks.Count == 0 ? runMarks : [.. runMarks, .. routeMarks];

    /// <summary>The colours its lines write or name (XAML's, C#'s), each a swatch in the gutter.</summary>
    public IReadOnlyList<ColourMark> ColourMarks
    {
        get => colourMarks;
        set => Set(ref colourMarks, value);
    }

    /// <summary>The change bars: the lines that differ from the last commit.</summary>
    public IReadOnlyList<ChangeMark> ChangeMarks
    {
        get => changeMarks;
        set => Set(ref changeMarks, value);
    }

    /// <summary>What folds in the text and which of it is folded, in this text's offsets.</summary>
    public IReadOnlyList<FoldRegion> FoldRegions
    {
        get => foldRegions;
        set => Set(ref foldRegions, value);
    }

    /// <summary>Which of the regions first found in the file fold as it opens (the window's: those folded as it last
    /// closed, else those Settings folds by default); none while unset. A region that holds a caret stays open.</summary>
    public Func<FoldRegion, bool>? FoldsAtFirst { get; set; }

    /// <summary>Whether the file's regions have been found, so which of them are folded is the person's to keep.</summary>
    public bool FoldsFound { get; private set; }

    /// <summary>What finds the file's regions: its C# parse (<see cref="Syntax"/>), or XML's or JSON's scan.</summary>
    public IFoldFinder? FoldFinder => finder;

    /// <summary>The bookmarked lines.</summary>
    public IReadOnlyList<int> Bookmarks
    {
        get => bookmarks;
        set => Set(ref bookmarks, value);
    }

    /// <summary>Text per line at the gutter's left (Git blame's), empty for none.</summary>
    public IReadOnlyList<string?> GutterAnnotations
    {
        get => gutterAnnotations;
        set => Set(ref gutterAnnotations, value);
    }

    /// <summary>The language service's words inside lines (parameter names before arguments), in this text's offsets: a
    /// <see cref="HintList"/>, which the text carries through each edit, as its editors carry theirs.</summary>
    public IReadOnlyList<InlayHint> InlayHints
    {
        get => inlayHints;
        set => Set(ref inlayHints, HintList.Of(value));
    }

    /// <summary>The debugger's text after lines: inline values, the exception it stopped on. Each goes with its line as
    /// the text is edited.</summary>
    public IReadOnlyList<LineAnnotation> LineAnnotations
    {
        get => lineAnnotations;
        set
        {
            annotationLines = Anchored(value, annotationLines);
            Set(ref lineAnnotations, value);
        }
    }

    /// <summary>A debugged Avalonia app's values after an .axaml file's lines: each binding's value on the control it is read
    /// for (LiveTree/BindingValues), shown after <see cref="LineAnnotations"/>.</summary>
    public IReadOnlyList<LineAnnotation> LiveValues
    {
        get => liveValues;
        set
        {
            liveLines = Anchored(value, liveLines);
            Set(ref liveValues, value);
        }
    }

    /// <summary>The lines of annotations, anchored in the text, those of the ones they replace let go.</summary>
    private AnchoredLine[] Anchored(IReadOnlyList<LineAnnotation> annotations, AnchoredLine[] replaced)
    {
        foreach (AnchoredLine line in replaced)
        {
            line.Detach();
        }
        AnchoredLine[] lines = new AnchoredLine[annotations.Count];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = new AnchoredLine(annotations[i].Line);
            lines[i].Attach(Text);
        }
        return lines;
    }

    /// <summary>Annotations with the lines their anchors carried them to; the same list when none moved.</summary>
    private static IReadOnlyList<LineAnnotation> Carried(IReadOnlyList<LineAnnotation> annotations, AnchoredLine[] lines)
    {
        LineAnnotation[]? moved = null;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Follow())
            {
                moved ??= [.. annotations];
                moved[i] = moved[i] with { Line = lines[i].Line };
            }
        }
        return moved ?? annotations;
    }

    /// <summary>What its editors show after its lines: the debugger's annotations, then the app's live values.</summary>
    public IReadOnlyList<LineAnnotation> ShownAnnotations => liveValues.Count == 0 ? lineAnnotations : [.. lineAnnotations, .. liveValues];

    /// <summary>Whether the editors showing the file wrap its lines at their width: Settings' soft wrap masks decide as it
    /// opens, View, Active Editor, Soft-Wrap after.</summary>
    public bool SoftWrap
    {
        get => softWrap;
        set => Set(ref softWrap, value);
    }

    /// <summary>Whether the editors showing the file draw its spaces and tabs: Settings' Show whitespaces as it opens, View,
    /// Active Editor, Show Whitespaces after.</summary>
    public bool ShowWhitespaces
    {
        get => showWhitespaces;
        set => Set(ref showWhitespaces, value);
    }

    /// <summary>Lets go of the file's asset (one still decoding as it lands), colours and folds after its last view or hold
    /// closes.</summary>
    public void Close()
    {
        closed = true;
        Asset?.Dispose();
        (Highlighter as IDisposable)?.Dispose();
        (finder as FoldScan)?.Dispose();
    }

    public string Path { get; private set; }
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>What its tab is called: its name, or for release notes the app carries what they are ("What's New in 0.2.0").</summary>
    public string Title => Updates.ReleaseNotes.VersionOf(Path) is { } version ? Updates.ReleaseNotes.TitleOf(version) : Name;

    /// <summary>Whether this is release notes the app carries, which its tab shows rendered (What's New), read-only.</summary>
    public bool IsReleaseNotes => Updates.ReleaseNotes.VersionOf(Path) is not null;

    /// <summary>Takes the file's new path once it moved on disk, keeping its text, undo, carets and folds; a new name
    /// of another language takes that language's parser. An asset keeps its preview, and a binary file its bytes' plain
    /// text, rather than becoming another language's.</summary>
    public void MoveTo(string path)
    {
        Path = path;
        Language language = Asset is null && !IsBinary ? Language.ForPath(path) : Language;
        if (language != Language)
        {
            Language = language;
            (parser as IDisposable)?.Dispose();
            (finder as FoldScan)?.Dispose();
            parser = Parser();
            FoldRegions = [];
            FoldsFound = false;
            finder = Finder();
            TestMarks = [];
            Highlighter = parser;
        }
        Raise(nameof(Path));
        Raise(nameof(Name));
        ReadConventions();
    }

    /// <summary>What the .editorconfig files over the file say of it, as last read.</summary>
    public EditorConfig Conventions { get; private set; }

    /// <summary>How Enter, Tab, a paste, a template and the language server's edits indent the file, and why.</summary>
    public FileIndent Indentation { get; private set; }

    /// <summary>Reads the .editorconfig files over the file again (one changed on disk, the file moved) and follows them:
    /// its indentation, and the line break a file with none yet starts with.</summary>
    public void ReadConventions()
    {
        Conventions = EditorConfig.For(Path);
        Raise(nameof(Conventions));
        FollowLineEnding(Text.Snapshot.LineCount > 1);
        Choose(raise: true);
    }

    /// <summary>The indentation from the .editorconfig, else the file's own lines, else its language; raised as changed.</summary>
    private FileIndent Choose(bool raise = false)
    {
        FileIndent chosen = FileIndent.Choose(Conventions, detected, Language.Indent);
        if (raise && chosen != Indentation)
        {
            Indentation = chosen;
            Raise(nameof(Indentation));
        }
        return chosen;
    }

    /// <summary>A file with no line break yet takes the .editorconfig's <c>end_of_line</c> for the first one, unless the
    /// person chose one; one with breaks keeps its own until it is saved.</summary>
    private void FollowLineEnding(bool hasBreak)
    {
        if (!hasBreak && !separatorChosen)
        {
            SetSeparator(Conventions.EndOfLine ?? LineSeparator.LF);
        }
    }

    /// <summary>What Enter inserts: the file's own line break, or the one its .editorconfig gives it ('\n' in a CR file,
    /// whose breaks are held so).</summary>
    public string LineEnding => Text.LineEnding;

    /// <summary>How the file's lines end on disk, as the status bar's line separator widget shows it.</summary>
    public LineSeparator Separator => crBreaks ? LineSeparator.CR : Text.LineEnding == "\r\n" ? LineSeparator.CRLF : LineSeparator.LF;

    /// <summary>The line separator a file read from disk has, by its first break.</summary>
    private static LineSeparator SeparatorOf(FileText file) =>
        file.CrBreaks ? LineSeparator.CR : TextDocument.DetectLineEnding(file.Text) == "\r\n" ? LineSeparator.CRLF : LineSeparator.LF;

    private void SetSeparator(LineSeparator separator)
    {
        if (Separator != separator)
        {
            crBreaks = separator == LineSeparator.CR;
            Text.LineEnding = separator.Held();
            Raise(nameof(LineEnding));
        }
    }

    /// <summary>The line separator widget's tip, with what the .editorconfig says of it.</summary>
    public string SeparatorTip
    {
        get
        {
            string tip = $"Line Separator: {Separator.Escaped()}";
            if (Conventions.EndOfLine is not { } asked)
            {
                return tip;
            }
            string said = Conventions.Said("end_of_line")!;
            return asked == Separator ? $"{tip}\nAs the .editorconfig asks: {said}"
                : separatorChosen ? $"{tip}\nChosen over the .editorconfig's {said}"
                : $"{tip}\nThe next save makes it {asked}, as the .editorconfig asks: {said}";
        }
    }

    /// <summary>The encoding widget's tip, with what the .editorconfig says of it.</summary>
    public string EncodingTip
    {
        get
        {
            string tip = $"File Encoding: {EncodingName}";
            if (Conventions.Charset is not { } asked)
            {
                return tip;
            }
            string said = Conventions.Said("charset")!;
            return asked.Name == EncodingName ? $"{tip}\nAs the .editorconfig asks: {said}"
                : encodingChosen ? $"{tip}\nChosen over the .editorconfig's {said}"
                : $"{tip}\nThe next save makes it {asked.Name}, as the .editorconfig asks: {said}";
        }
    }

    /// <summary>Line Separators: every break made <paramref name="to"/>'s and the file written so, as one undoable step
    /// whose undo and redo write it too, and which a save keeps over the .editorconfig's <c>end_of_line</c>. An asset
    /// preview is left unchanged.</summary>
    public void ConvertLineSeparators(LineSeparator to)
    {
        if (Asset is not null)
        {
            return;
        }
        IReadOnlyList<TextEdit> edits = TextEditing.Saving(Text.Snapshot.ToString(), Selections, false, false, to.Held(), Text.LineEnding)?.Edits ?? [];
        Change(edits, Current with { Separator = to, SeparatorChosen = true }, write: true);
    }

    /// <summary>What another encoding would do to the file, read again or written in it. An asset has no text encoding
    /// and is refused with an <see cref="InvalidOperationException"/>.</summary>
    public EncodingChange Weigh(FileEncoding to)
    {
        if (Asset is not null)
        {
            throw new InvalidOperationException("Asset previews do not have a text encoding.");
        }
        byte[]? bytes;
        try
        {
            bytes = File.Exists(Path) ? FileText.Read(Path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            bytes = null;
        }
        return EncodingChange.Weigh(bytes, Text.Snapshot.ToString(), Encoding, crBreaks, to);
    }

    /// <summary>Reload: the file's bytes read again in the encoding replace the text, as one undoable step; the file is
    /// left as it is. An asset preview is left unchanged.</summary>
    public void ReloadIn(FileEncoding to)
    {
        if (Asset is not null)
        {
            return;
        }
        FileText read = FileText.Decode(FileText.Read(Path), to.Encoding);
        LineSeparator separator = read.Text.Contains('\n', StringComparison.Ordinal) ? SeparatorOf(read) : Separator;
        // UTF-16 is written with its BOM, the only way it is read back as UTF-16.
        bool bom = read.HasBom || to.CodePage is 1200 or 1201;
        Change(LineDiff.Edits(Text.Snapshot.ToString(), read.Text), Current with { Encoding = to.Encoding, Bom = bom, Separator = separator, EncodingChosen = true }, write: false);
    }

    /// <summary>Convert: the text written in the encoding, as one undoable step whose undo and redo write it too. A
    /// character it cannot hold, converted anyway, becomes '?' in the text as on disk. Asset previews are
    /// unchanged.</summary>
    public void ConvertTo(FileEncoding to) =>
        Change(EncodingChange.Lost(to.Encoding, Text.Snapshot.ToString()), Current with { Encoding = to.Encoding, Bom = to.Bom, EncodingChosen = true }, write: true);

    /// <summary>Local History's revert: a version's bytes read as that version was written (UTF-8 before the encoding the
    /// file has now, which a Convert since may have changed), its text and how it was stored taken as one undoable step
    /// and written, as Convert writes, its undo and redo writing too. Nothing changes when the version is what it holds.</summary>
    public void Revert(byte[] bytes)
    {
        FileEncoding now = FileEncoding.Of(Encoding, HasBom);
        FileText version = FileText.FromBytes(bytes, null, () => now.IsUnicode ? Conventions.Charset : now) ?? new FileText(string.Empty, Encoding, HasBom);
        string text = Text.Snapshot.ToString();
        LineSeparator separator = version.Text.Contains('\n', StringComparison.Ordinal) ? SeparatorOf(version) : Separator;
        // Bytes the encoding now writes as the version had them (ASCII in UTF-8 or Latin-1) keep it.
        bool stored = version.IsStoredAs(Encoding, HasBom);
        if (version.Text == text && stored && separator == Separator)
        {
            return;
        }
        Change(LineDiff.Edits(text, version.Text), stored ? Current with { Separator = separator } : Current with { Encoding = version.Encoding, Bom = version.HasBom, Separator = separator }, write: true);
    }

    /// <summary>An encoding taken as it is: one that reads the file's bytes as the text it has and writes the text as
    /// the bytes it has (an ASCII file) is taken with nothing read or written, as one undoable step. Asset previews are
    /// left unchanged.</summary>
    public void TakeEncoding(FileEncoding to) =>
        Change([], Current with { Encoding = to.Encoding, Bom = to.Bom, EncodingChosen = true }, write: false);

    /// <summary>How the file is stored, which an undoable change of it puts back.</summary>
    private sealed record Stored(Encoding Encoding, bool Bom, LineSeparator Separator, bool SeparatorChosen, bool EncodingChosen);

    private Stored Current => new(Encoding, HasBom, Separator, separatorChosen, encodingChosen);

    /// <summary>One undoable step that changes how the file is stored, and its text with it: undone or redone, the file
    /// goes back to how it was or on again, written when the step wrote it, else saved as far as the file, read in the
    /// encoding taken, is the text.</summary>
    private void Change(IReadOnlyList<TextEdit> edits, Stored after, bool write)
    {
        // A workbook's empty stand-in or a binary file's signs, written, would replace the file.
        if (!HoldsText)
        {
            return;
        }
        Stored before = Current;
        SelectionSet carets = Selections;
        TextEdit[] sorted = [.. edits.OrderBy(e => e.Start)];
        string changed = Text.Snapshot.Apply(sorted).ToString();
        // Written before anything changes, so a write that fails leaves the file and the editor as they were.
        (FileText, string?)? written = write ? FileText.Save(Path, changed, after.Encoding, after.Bom, after.Separator == LineSeparator.CR, makeFolder: false) : null;
        Text.Perform(sorted, carets, TextEditing.MapThrough(carets, sorted).Clamp(changed.Length),
            () => Undoable(() => Settle(before, write)),
            () => Undoable(() => Settle(after, write)));
        Restore(after);
        if (written is { } done)
        {
            Wrote(done);
        }
        else
        {
            MarkSavedIfOnDisk();
        }
    }

    /// <summary>The file as an undo or redo leaves it: stored so, written when the step wrote it.</summary>
    private void Settle(Stored stored, bool write)
    {
        Restore(stored);
        if (write)
        {
            Write(Encoding, HasBom);
        }
        else
        {
            MarkSavedIfOnDisk();
        }
    }

    private void Restore(Stored stored)
    {
        separatorChosen = stored.SeparatorChosen;
        encodingChosen = stored.EncodingChosen;
        SetSeparator(stored.Separator);
        Encoding = stored.Encoding;
        HasBom = stored.Bom;
        EncodingName = FileEncoding.Of(Encoding, HasBom).Name;
        // The widgets' tips name the choices as well.
        Raise(nameof(LineEnding));
        Raise(nameof(EncodingName));
        FollowHeld(null);
    }

    private void MarkSavedIfOnDisk()
    {
        if (OnDisk())
        {
            Text.MarkSaved();
            Raise(nameof(IsModified));
        }
    }

    /// <summary>Whether the file's bytes, read in the encoding taken, are the text as it stands.</summary>
    private bool OnDisk()
    {
        try
        {
            return File.Exists(Path) && FileText.Decode(FileText.Read(Path), Encoding).Text == Text.Snapshot.ToString();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>An undo or redo's write, which says why when it fails rather than end the key press that asked for it.</summary>
    private void Undoable(Action settle)
    {
        try
        {
            settle();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WriteFailed?.Invoke(this, $"{Name} could not be saved: {error.Message}");
        }
    }

    /// <summary>The in-process parser's colours and folds for the file's language (C#'s parse, another language's TextMate
    /// grammar); null for a language with neither.</summary>
    private IHighlighter? Parser()
    {
        IHighlighter? colours = Highlighters.For(Language, Text);
        if (colours is CSharpHighlighter csharp)
        {
            csharp.Parsed += (_, parsed) => Parsed?.Invoke(this, parsed);
        }
        else if (colours is Languages.TextMate.TextMateHighlighter grammar)
        {
            grammar.Failed += (_, error) => IdeErrors.Report(error);
        }
        return colours;
    }

    /// <summary>What finds the file's folds: C#'s parse, XML's and JSON's scan; none for another language.</summary>
    private IFoldFinder? Finder()
    {
        IFoldFinder? found = Highlighters.FoldsFor(Language, Text, parser);
        if (found is not null)
        {
            found.FoldsChanged += (_, _) =>
            {
                if (ReferenceEquals(found, finder))
                {
                    TakeFolds(found.Folds);
                }
            };
        }
        return found;
    }

    /// <summary>Regions found for the text as it stands. The first fold as <see cref="FoldsAtFirst"/> says; after, they keep
    /// the folds of the ones they replace, matched by where they start. The regions carried through the keys are usually
    /// the finder's already, and stay; with none folded the finder's own are taken.</summary>
    private void TakeFolds(IReadOnlyList<FoldRegion> found)
    {
        if (!FoldsFound)
        {
            FoldsFound = true;
            FoldRegions = Opening(found);
            return;
        }
        if (Same(foldRegions, found))
        {
            return;
        }
        HashSet<int>? folded = null;
        for (int i = 0; i < foldRegions.Count; i++)
        {
            if (foldRegions[i].Folded)
            {
                (folded ??= []).Add(foldRegions[i].Start);
            }
        }
        if (folded is null)
        {
            FoldRegions = found;
        }
        else
        {
            FoldRegion[] kept = new FoldRegion[found.Count];
            for (int i = 0; i < kept.Length; i++)
            {
                kept[i] = found[i] with { Folded = folded.Contains(found[i].Start) };
            }
            FoldRegions = kept;
        }
    }

    /// <summary>The first regions, folded as the file opens: as <see cref="FoldsAtFirst"/> says, but for one hiding a caret.</summary>
    private IReadOnlyList<FoldRegion> Opening(IReadOnlyList<FoldRegion> found)
    {
        if (FoldsAtFirst is not { } folds || found.Count == 0)
        {
            return found;
        }
        TextSnapshot text = Text.Snapshot;
        int[] carets = [.. Selections.Items.Select(s => text.GetLineFromOffset(Math.Min(s.Caret, text.Length)))];
        FoldRegion[] opened = new FoldRegion[found.Count];
        for (int i = 0; i < opened.Length; i++)
        {
            FoldRegion region = found[i];
            bool hides = region.End <= text.Length && carets.Any(line => line > text.GetLineFromOffset(region.Start) && line <= text.GetLineFromOffset(region.End));
            opened[i] = region with { Folded = !hides && folds(region) };
        }
        return opened;
    }

    /// <summary>Whether two lists of regions fold the same text, whatever is folded.</summary>
    private static bool Same(IReadOnlyList<FoldRegion> kept, IReadOnlyList<FoldRegion> found)
    {
        if (kept.Count != found.Count)
        {
            return false;
        }
        for (int i = 0; i < kept.Count; i++)
        {
            if (kept[i].Start != found[i].Start || kept[i].End != found[i].End || kept[i].Placeholder != found[i].Placeholder || kept[i].Kind != found[i].Kind)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether the language server made this file from an assembly's metadata (Go to Declaration on a
    /// library's symbol), where nothing typed would reach the library.</summary>
    public bool IsDecompiled => Path.Contains($"{System.IO.Path.DirectorySeparatorChar}MetadataAsSource{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>Whether the editor shows it read-only, under a bar that says why: decompiled, the licence, notices or release
    /// notes the app carries, a workbook, an asset preview, a binary file shown as its bytes, or an MSBuild file of the .NET
    /// SDK or a package that Go to Declaration went into, or a copy of a container's file or of what the engine says of one.</summary>
    public bool IsReadOnly => Asset is not null || IsDecompiled || IsWorkbook || IsBinary || Product.Carries(Path) || IsReleaseNotes
        || MSBuild.MSBuildFiles.IsInstalled(Path) || Containers.ContainerFilesPane.IsCopy(Path) || Endpoints.RequestLog.IsCommandCopy(Path);

    /// <summary>Whether this is an Excel workbook, shown as its sheets' cells: its tab holds no text, and nothing is ever
    /// written over it.</summary>
    public bool IsWorkbook => Tables.Workbook.IsWorkbook(Path);

    /// <summary>Whether this is a binary file with no view of its own, shown as its bytes' text (<see cref="FileText.Bytes"/>),
    /// which written back would no longer be the file.</summary>
    public bool IsBinary { get; }

    /// <summary>Whether its tab holds the file's text: an asset preview's and a workbook's hold an empty stand-in, a
    /// binary file's its bytes drawn as signs and release notes' their Markdown shown rendered, so nothing searches, reads,
    /// edits or writes it as the file's (<see cref="ShownAs"/> says what the file is instead).</summary>
    public bool HoldsText => Asset is null && !IsWorkbook && !IsBinary && !IsReleaseNotes;

    /// <summary>What a file whose tab holds no text is shown as, for what refuses its text to say; null for text.</summary>
    public string? ShownAs => Asset is { Kind: AssetKind.Image } ? "an image, shown as a read-only preview"
        : Asset is not null ? "a font, shown as a read-only preview"
        : IsWorkbook ? "an Excel workbook, shown as its sheets' cells"
        : IsBinary ? "a binary file, shown as its bytes' text"
        : IsReleaseNotes ? "Passenger's release notes, shown rendered" : null;

    /// <summary>What the bar over a read-only file says it is and where it came from.</summary>
    public string Origin => Asset is { Kind: AssetKind.Image } ? "An image, shown as its pixels, read-only"
        : Asset is not null ? "A font, shown in its own glyphs, read-only"
        : IsWorkbook ? "An Excel workbook, shown as its sheets' cells, read-only"
        : IsBinary ? "A binary file, shown as its bytes' text, read-only"
        : IsReleaseNotes ? "Passenger's release notes, as the app carries them, read-only"
        : Product.Carries(Path) ? "Passenger's own file, as the app carries it, read-only"
        : MSBuild.MSBuildFiles.IsInstalled(Path) ? "An MSBuild file of the .NET SDK or a package, as installed, read-only"
        : Containers.ContainerFilesPane.IsCopy(Path) ? "A copy of what a container holds, or of what the engine says of it, read-only"
        : Endpoints.RequestLog.IsCommandCopy(Path) ? "SQL a running app issued, or an EF Core query's the debugger read, copied to read, read-only"
        : Path.Contains("SourceLink", StringComparison.OrdinalIgnoreCase) || Path.Contains("Embedded", StringComparison.OrdinalIgnoreCase)
        ? "Source from the library's Source Link or PDB, read-only"
        : "Decompiled code: the language server made this from the assembly, so it is read-only";
    /// <summary>The editable text, or an empty placeholder for an asset that must never be serialized over its file.</summary>
    public TextDocument Text { get; }

    /// <summary>How the file is stored, which a save keeps: as it was read, as it is on disk since (a BOM a formatter
    /// added), or UTF-8 once a save needed it.</summary>
    public Encoding Encoding { get; private set; }
    public bool HasBom { get; private set; }
    public string EncodingName { get; private set; }
    public Language Language { get; private set; }

    /// <summary>Whether text has unsaved changes; a read-only asset is never considered modified.</summary>
    public bool IsModified => Asset is null && Text.IsModified;
    public Avalonia.Media.Geometry Icon => Explorer.FileIcons.ForFile(Path);
    public Avalonia.Media.IBrush IconBrush => Explorer.FileIcons.BrushForFile(Path);

    /// <summary>The carets: the editor's while the file is shown there, as left otherwise, moved by every change to the text.</summary>
    public SelectionSet Selections { get; set; } = SelectionSet.Single(0);
    public Vector Scroll { get; set; }

    /// <summary>Whether something other than this IDE changed the file's text on disk since it was last loaded or saved.
    /// A change only to how it is stored there (a BOM a formatter added, a conversion to UTF-8) is taken at once, over
    /// unsaved edits too, since they were made over the same text: the next save keeps it, and no question is asked.
    /// An asset answers false with nothing read: it has no text to conflict, and the folder watcher's word that its file
    /// changed is what decodes it again (<see cref="Reload"/>).</summary>
    public bool ChangedOnDisk()
    {
        if (Asset is not null)
        {
            return false;
        }
        FileText? file;
        try
        {
            file = File.Exists(Path) ? FromDisk() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (file is null)
        {
            return false;
        }
        if (file.Text != Text.Saved.ToString())
        {
            return true;
        }
        StoreAs(file);
        return false;
    }

    /// <summary>Whether a file holds the text this one was last loaded or saved with, read as this one is read (an asset's
    /// bytes): the same file under another name, once its folder was moved.</summary>
    public bool IsSavedAt(string path)
    {
        try
        {
            return Asset is { } asset ? asset.IsOf(path) : File.Exists(path) && FromDisk(path)?.Text == Text.Saved.ToString();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The encoding the person chose for the file, which a read from disk tries before guessing.</summary>
    private Encoding? Chosen => encodingChosen ? Encoding : null;

    /// <summary>A file's text on disk (this one's, unless another is named) read as this one was when opened: a binary
    /// file's as its bytes.</summary>
    private FileText? FromDisk(string? path = null) => IsBinary ? FileText.Bytes(path ?? Path) : FileText.Load(path ?? Path, Chosen);

    /// <summary>Takes the file's text and encoding from disk again when something else changed them there (a checkout, a
    /// rollback), as one undoable step over only the lines that differ, so what is anchored elsewhere stays put; over unsaved
    /// changes only when asked (File Cache Conflict's Load File System Changes). An asset is decoded again off the UI
    /// thread and replaced once that succeeds, the previous preview kept and <see cref="Notice"/> set on failure. Whether
    /// the text changed; false for an asset, whose change lands later.</summary>
    public bool Reload(bool overUnsaved = false)
    {
        if (Asset is not null)
        {
            Decode();
            return false;
        }
        FileText? file;
        DiskStamp? stamp = null;
        try
        {
            file = (IsModified && !overUnsaved) || (stamp = DiskStamp.Of(Path)) is null ? null : FromDisk();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (file is null)
        {
            return false;
        }
        bool taken = Take(file, same: Text.Snapshot.ToString() == file.Text);
        ReadAt(stamp);
        return taken;
    }

    /// <summary>Takes the file's text from disk when another program wrote it since the editor last read it (an agent's own
    /// file tools, which write and then ask of it at once), never over unsaved changes, as <see cref="Reload"/> would: its
    /// length and time looked at, and only once they changed the file read and compared, off the UI thread, so an agent's
    /// many questions of one file between its writes cost the window nothing of the disk. A preview has no text to take.</summary>
    public async Task TakeDiskTextAsync(CancellationToken cancel)
    {
        if (IsModified || !HoldsText)
        {
            return;
        }
        (DiskStamp Stamp, int Version)? known = readAt is { } at && at.Version == Text.Version ? at : null;
        int version = Text.Version;
        TextSnapshot held = Text.Snapshot;
        string path = Path;
        bool binary = IsBinary;
        Encoding? chosen = Chosen;
        (DiskStamp? stamp, FileText? file, bool same) = await BackgroundWork.Run(() =>
        {
            try
            {
                DiskStamp? now = DiskStamp.Of(path);
                if (now is not { } found || known is { } kept && kept.Stamp.Holds(found))
                {
                    return ((DiskStamp?)null, (FileText?)null, true);
                }
                FileText? read = binary ? FileText.Bytes(path) : FileText.Load(path, chosen);
                return (now, read, read is null || held.ToString() == read.Text);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return (null, null, true);
            }
        }, cancel);
        // Typed into, or taken from disk by the watcher, while the disk was read: what the editor holds now stands.
        if (file is null || IsModified || Text.Version != version)
        {
            return;
        }
        Take(file, same);
        ReadAt(stamp);
    }

    /// <summary>The disk's text taken, as one undoable step over only the lines that differ, unless it is the
    /// <paramref name="same"/> as the editor's; how it is stored taken either way. Whether the text changed.</summary>
    private bool Take(FileText file, bool same)
    {
        StoreAs(file);
        if (same)
        {
            return false;
        }
        Text.SetText(file.Text, Selections);
        Text.MarkSaved();
        if (file.Text.Contains('\n', StringComparison.Ordinal))
        {
            // Enter goes on writing the breaks the file has now.
            SetSeparator(SeparatorOf(file));
        }
        // Text written elsewhere may indent otherwise than what it replaced.
        detected = Language.Detect(file.Text);
        Choose(raise: true);
        Raise(nameof(IsModified));
        return true;
    }

    /// <summary>Writes the file, first doing to its text what its .editorconfig asks of a save (trailing whitespace off
    /// every line without one of <paramref name="carets"/>, a final line break, every break its <c>end_of_line</c>, as
    /// one undoable step) and in its <c>charset</c>; a line separator or encoding the person chose stays. Its folder, once
    /// another program moved or deleted it, is made again only when <paramref name="makeFolder"/> (the person's Save). An
    /// asset preview is never written, even when Save is called directly.</summary>
    public void Save(SelectionSet? carets = null, bool makeFolder = false)
    {
        if (Asset is not null)
        {
            return;
        }
        if (IsWorkbook)
        {
            // Its tab's text is no workbook's: written, it would empty the file.
            throw new IOException($"{Name} is a workbook, shown read-only.");
        }
        if (IsBinary)
        {
            // Its control characters are shown as signs: written, they would replace the bytes.
            throw new IOException($"{Name} is a binary file, shown read-only.");
        }
        ReadConventions();
        string text = Text.Snapshot.ToString();
        SelectionSet kept = carets ?? Selections;
        LineSeparator? asked = separatorChosen ? null : Conventions.EndOfLine;
        if (TextEditing.Saving(text, kept, Conventions.TrimTrailingWhitespace, Conventions.InsertFinalNewline, asked?.Held(), Text.LineEnding) is { } plan)
        {
            Text.BreakUndoGroup();
            Text.Edit(plan.Edits, kept, plan.Selections);
            Text.BreakUndoGroup();
        }
        if (asked is { } separator)
        {
            SetSeparator(separator);
        }
        FileEncoding? charset = encodingChosen ? null : Conventions.Charset;
        Write(charset?.Encoding ?? Encoding, charset?.Bom ?? HasBom, makeFolder);
    }

    /// <summary>Writes the text as it stands, in an encoding and the file's line separator, and takes that as how the
    /// file is stored.</summary>
    private void Write(Encoding encoding, bool bom, bool makeFolder = false) =>
        Wrote(FileText.Save(Path, Text.Snapshot.ToString(), encoding, bom, crBreaks, makeFolder));

    /// <summary>Takes how a write stored the file, which now holds the text.</summary>
    private void Wrote((FileText Written, string? Converted) write)
    {
        (FileText written, string? converted) = write;
        StoreAs(written);
        // Written, the text is in the encoding it was written in.
        EncodingWarning = null;
        Text.MarkSaved();
        Raise(nameof(IsModified));
        Saved?.Invoke(this, EventArgs.Empty);
        if (converted is not null)
        {
            Converted?.Invoke(this, converted);
        }
    }

    /// <summary>Takes how the file is stored now, unless that is what it has already in all but name (ASCII reads as
    /// UTF-8 but is the same bytes in Latin-1), so the next save writes it the same way.</summary>
    private void StoreAs(FileText file)
    {
        if (file.CrBreaks != crBreaks && file.Text.Contains('\n', StringComparison.Ordinal))
        {
            SetSeparator(SeparatorOf(file));
        }
        if (file.IsStoredAs(Encoding, HasBom))
        {
            return;
        }
        Encoding = file.Encoding;
        HasBom = file.HasBom;
        EncodingName = file.EncodingName;
        Raise(nameof(EncodingName));
        // The file may be stored otherwise under unsaved edits (a conversion on disk).
        FollowHeld(null);
    }
}

/// <summary>A link the editor's banner offers after its notice: its words and what it does.</summary>
public sealed record NoticeAction(string Text, Action Run);

/// <summary>A file's length and last write time on disk, and when they were found.</summary>
public readonly record struct DiskStamp(long Length, DateTime Written, DateTime Found)
{
    /// <summary>How long after its last write a file's time is trusted to show a second write: a coarse clock's tick (FAT's two
    /// seconds) could hide one made in it.</summary>
    private static readonly TimeSpan Racy = TimeSpan.FromSeconds(2);

    /// <summary>A file's stamp now, or null when there is none there.</summary>
    public static DiskStamp? Of(string path)
    {
        FileInfo info = new(path);
        return info.Exists ? Of(info) : null;
    }

    public static DiskStamp Of(FileInfo info) => new(info.Length, info.LastWriteTimeUtc, DateTime.UtcNow);

    /// <summary>Whether a file found as <paramref name="now"/> still holds what was read once this was found: its length and
    /// time the same, that time far enough before the read for no second write to hide in its tick.</summary>
    public bool Holds(DiskStamp now) => now.Length == Length && now.Written == Written && Found - Written > Racy;
}
