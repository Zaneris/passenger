using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>The language server's colours over the parser's: what only the compiler knows (that a name is a struct, a
/// field, a local, an extension method) replaces the syntax's guess wherever the server says something. Tokens are
/// asked for once edits settle and whenever the server says the code moved: the lines on screen first (a screen takes
/// the server milliseconds, a long file about a second), then the whole file once typing has stopped
/// (<see cref="Idle"/>). Each answer is decoded off the UI thread against the snapshot it was asked for and carried
/// through the edits made since, so colours never lag a keystroke behind the text nor jump when an answer lands.</summary>
public sealed class SemanticHighlighter : IHighlighter, IBraceMatcher, IDisposable
{
    /// <summary>How long typing must pause before new tokens are asked for.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <summary>How much longer the text must stay as it is, the screen's tokens come, before the whole file's are asked
    /// for: a long file's take the server a second and their answer megabytes to read, which the next key would throw
    /// away, and meanwhile the whole file's last are carried through the edits.</summary>
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);

    /// <summary>The most changes the whole file's tokens are carried through before they are asked for again at the next
    /// pause, idle or not: every frame drawn beyond the screen's carries them through each.</summary>
    private const int MostCarried = 64;

    private readonly IHighlighter syntax;
    private readonly LspDocument document;
    private readonly TokenKind?[] kinds;

    /// <summary>Each change since the oldest tokens still shown, or since the snapshot a request still out was asked
    /// for, with the version of the text it made.</summary>
    private readonly List<(int Version, IReadOnlyList<Passenger.Text.TextEdit> Edits)> history = [];

    /// <summary>The whole file's tokens, and the screen's while they are newer.</summary>
    private Tokens? whole;
    private Tokens? screen;

    /// <summary>What the editors asked colours for in the text as it stands: the lines on screen.</summary>
    private int shownStart;
    private int shownEnd;
    private int shownVersion = -1;

    private CancellationTokenSource? pending;
    private int asked;
    private int answered;
    private int? askedFor;
    private bool disposed;

    /// <summary>The latest fetch's last request, placed among the server's words that its colours moved as it reached the
    /// server; null before it sends one.</summary>
    private SentRequest? outAfter;

    public SemanticHighlighter(IHighlighter syntax, LspDocument document, SemanticTokensLegend legend)
    {
        this.syntax = syntax;
        this.document = document;
        kinds = legend.TokenTypes.Select(Kind).ToArray();
        syntax.Changed += OnSyntaxChanged;
        document.Text.Changed += OnTextChanged;
        Refresh();
    }

    public event EventHandler? Changed;

    /// <summary>Asks for fresh tokens now: the server's view moved (projects loaded, another project chosen).</summary>
    public void Refresh() => Request(TimeSpan.Zero);

    /// <summary>The server's word that its colours moved, handled: tokens are asked for again, unless the latest request
    /// reached the server only after the word was read and so answers with what it told of.</summary>
    public void Refreshed()
    {
        if (outAfter is { AfterEveryWord: true })
        {
            return;
        }
        Request(TimeSpan.Zero);
    }

    public IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot snapshot, int start, int length)
    {
        IReadOnlyList<SyntaxSpan> parsed = syntax.GetSpans(snapshot, start, length);
        Shown(start, length);
        List<SyntaxSpan> semantic = Visible(start, start + length);
        if (semantic.Count == 0)
        {
            return parsed;
        }
        List<SyntaxSpan> merged = new(parsed.Count + semantic.Count);
        int s = 0;
        foreach (SyntaxSpan span in parsed)
        {
            while (s < semantic.Count && semantic[s].End <= span.Start)
            {
                s++;
            }
            bool covered = s < semantic.Count && semantic[s].Start < span.End;
            if (!covered)
            {
                merged.Add(span);
            }
        }
        merged.AddRange(semantic);
        merged.Sort((a, b) => a.Start.CompareTo(b.Start));
        return merged;
    }

    /// <summary>The parser's colours this layers over, which stand alone again once the server is gone.</summary>
    public IHighlighter Syntax => syntax;

    /// <summary>The syntax's pair: brackets are the parser's to tell, never the server's.</summary>
    public BracePair? PairAt(TextSnapshot snapshot, int offset, bool far = false) => (syntax as IBraceMatcher)?.PairAt(snapshot, offset, far);

    /// <summary>The syntax's tag pair, a Razor file's markup's.</summary>
    public BracePair? TagPairAt(TextSnapshot snapshot, int caret, bool far = false) => (syntax as IBraceMatcher)?.TagPairAt(snapshot, caret, far);

    public void Dispose()
    {
        disposed = true;
        pending?.Cancel();
        syntax.Changed -= OnSyntaxChanged;
        document.Text.Changed -= OnTextChanged;
    }

    private void OnSyntaxChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>How many changes the colours shown are carried through to the text as it stands, each range drawn going
    /// through each: a run of typing in one place is one, however long the server takes to answer.</summary>
    public int Carrying => history.Count;

    /// <summary>Whether the latest ask for colours, the pause before it included, has yet to be answered or refused.</summary>
    public bool Asking => answered != asked;

    private void OnTextChanged(object? sender, DocumentChangedEventArgs e)
    {
        // The change before is joined only when no tokens shown, nor the request out, are of the text it made.
        if (history.Count == 0 || whole?.Version == history[^1].Version || screen?.Version == history[^1].Version || askedFor == history[^1].Version
            || !CarriedSpans.Join(history, e.Version, e.Edits))
        {
            history.Add((e.Version, e.Edits));
        }
        Request(Settle);
        Forget();
    }

    /// <summary>Notes a range an editor drew, which the next request asks for first.</summary>
    private void Shown(int start, int length)
    {
        int version = document.Text.Version;
        if (shownVersion != version)
        {
            shownVersion = version;
            shownStart = start;
            shownEnd = start + length;
        }
        shownStart = Math.Min(shownStart, start);
        shownEnd = Math.Max(shownEnd, start + length);
    }

    /// <summary>The whole lines drawn since the text last changed, when they are a part of it worth asking for alone.</summary>
    private TextRange? Screen(TextSnapshot snapshot)
    {
        if (shownVersion != document.Text.Version || shownStart >= shownEnd || (shownEnd - shownStart) * 2L > snapshot.Length)
        {
            return null;
        }
        int first = snapshot.GetLineFromOffset(Math.Min(shownStart, snapshot.Length));
        int last = snapshot.GetLineFromOffset(Math.Min(shownEnd, snapshot.Length));
        int end = last + 1 < snapshot.LineCount ? snapshot.GetLineStart(last + 1) : snapshot.Length;
        return TextRange.FromBounds(snapshot.GetLineStart(first), end);
    }

    private void Request(TimeSpan delay)
    {
        if (disposed)
        {
            return;
        }
        pending?.Cancel();
        CancellationTokenSource cancel = new();
        pending = cancel;
        int request = ++asked;
        askedFor = null;
        outAfter = null;
        BackgroundWork.Begin();
        UnawaitedWork.Observe(Fetch(delay, request, cancel.Token));
    }

    private async Task Fetch(TimeSpan delay, int request, CancellationToken cancel)
    {
        bool held = true;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancel);
            }
            TextSnapshot snapshot = document.Text.Snapshot;
            int version = document.Text.Version;
            askedFor = version;
            if (Screen(snapshot) is { } range)
            {
                (SemanticTokens? near, _) = await Out(request, sent => document.SemanticTokensAsync(range, cancel, sent));
                if (near is not null && !await Land(near, snapshot, version, range, request, cancel))
                {
                    return;
                }
                if (whole is not null && history.Count - After(whole.Version) <= MostCarried)
                {
                    // The rest of the file once typing stops: nothing on screen waits for it, so no screenshot does.
                    BackgroundWork.End();
                    held = false;
                    await Task.Delay(Idle, cancel);
                    BackgroundWork.Begin();
                    held = true;
                }
            }
            (SemanticTokens? all, _) = await Out(request, sent => document.SemanticTokensAsync(cancel, sent));
            if (all is not null)
            {
                await Land(all, snapshot, version, null, request, cancel);
            }
        }
        catch (OperationCanceledException)
        {
            // Asked again, or let go.
        }
        catch (LanguageServerFailed)
        {
            // The parser's colours stand until the server answers, or a server started again does.
        }
        finally
        {
            if (request == asked)
            {
                askedFor = null;
                answered = request;
                Forget();
            }
            if (held)
            {
                BackgroundWork.End();
            }
        }
    }

    /// <summary>A request of the latest fetch: where it reached the server among the server's words is followed, kept while
    /// its answer lands and the fetch goes on, until a new fetch begins or the request fails.</summary>
    private async Task<(SemanticTokens? Tokens, int Version)> Out(int request, Func<SentRequest, Task<(SemanticTokens? Tokens, int Version)>> send)
    {
        SentRequest sent = new();
        if (request == asked)
        {
            outAfter = sent;
        }
        try
        {
            return await send(sent);
        }
        catch (LanguageServerFailed) when (request == asked)
        {
            outAfter = null;
            throw;
        }
    }

    /// <summary>Decodes an answer off the UI thread and, back on it, shows it, unless a newer request has been made
    /// since: false then, and the request goes no further.</summary>
    private async Task<bool> Land(SemanticTokens answer, TextSnapshot snapshot, int version, TextRange? covered, int request, CancellationToken cancel)
    {
        TokenKind?[] types = kinds;
        SyntaxSpan[] spans = await BackgroundWork.Run(() => Decode(answer.Data, snapshot, types), cancel);
        if (request != asked || disposed)
        {
            return false;
        }
        Tokens tokens = new(spans, version, covered);
        if (covered is null)
        {
            whole = tokens;
            if (screen?.Version <= version)
            {
                screen = null;
            }
        }
        else
        {
            screen = tokens;
        }
        Forget();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Lets go of the changes that no tokens shown, nor a request still out, have to be carried through.</summary>
    private void Forget()
    {
        int floor = Math.Min(Math.Min(whole?.Version ?? int.MaxValue, screen?.Version ?? int.MaxValue), askedFor ?? int.MaxValue);
        int done = 0;
        while (done < history.Count && history[done].Version <= floor)
        {
            done++;
        }
        history.RemoveRange(0, done);
    }

    /// <summary>The protocol's relative encoding (each token as line and column deltas, length, type, modifiers) turned
    /// into offsets in the snapshot it was asked for, in order. Off the UI thread: a long file has hundreds of thousands
    /// of tokens, read once to count those with a colour and once to place them, each line found once.</summary>
    private static SyntaxSpan[] Decode(int[] data, TextSnapshot snapshot, TokenKind?[] kinds)
    {
        ServerLines lines = ServerLines.Of(snapshot);
        int count = 0;
        int line = 0;
        for (int i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            if (line < lines.LineCount && data[i + 3] < kinds.Length && kinds[data[i + 3]] is not null)
            {
                count++;
            }
        }
        SyntaxSpan[] spans = new SyntaxSpan[count];
        int placed = 0;
        int found = -1;
        int lineStart = 0;
        int lineLength = 0;
        int column = 0;
        line = 0;
        for (int i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            column = data[i] == 0 ? column + data[i + 1] : data[i + 1];
            int type = data[i + 3];
            if (line >= lines.LineCount || type >= kinds.Length || kinds[type] is not { } kind)
            {
                continue;
            }
            if (line != found)
            {
                found = line;
                lineStart = lines.OffsetOf(line, 0);
                lineLength = lines.OffsetOf(line, int.MaxValue) - lineStart;
            }
            spans[placed++] = new SyntaxSpan(lineStart + Math.Min(column, lineLength), data[i + 2], kind);
        }
        return spans;
    }

    /// <summary>The server's colours over a range of the current text: the screen's where they reach, the whole
    /// file's elsewhere.</summary>
    private List<SyntaxSpan> Visible(int start, int end)
    {
        if (screen is not { Covered: { } covered } near)
        {
            return whole is null ? [] : Carried(whole, start, end);
        }
        for (int i = After(near.Version); i < history.Count; i++)
        {
            covered = TextEditing.MapRange(covered, history[i].Edits);
        }
        TextRange reach = covered;
        List<SyntaxSpan> spans = Carried(near, start, end);
        spans.RemoveAll(s => s.Start < reach.Start || s.End > reach.End);
        if (whole is not null && (start < reach.Start || end > reach.End))
        {
            List<SyntaxSpan> around = Carried(whole, start, end);
            around.RemoveAll(s => s.End > reach.Start && s.Start < reach.End);
            spans.AddRange(around);
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        return spans;
    }

    /// <summary>One answer's tokens near a range of the current text: found in the answer's own text by halving, then
    /// carried through every edit since.</summary>
    private List<SyntaxSpan> Carried(Tokens tokens, int start, int end)
    {
        int first = After(tokens.Version);
        int from = start;
        int to = end;
        for (int i = history.Count - 1; i >= first; i--)
        {
            from = CarriedSpans.Back(history[i].Edits, from, toEnd: false);
            to = CarriedSpans.Back(history[i].Edits, to, toEnd: true) + 1;
        }
        SyntaxSpan[] all = tokens.Spans;
        int low = 0;
        int high = all.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (all[middle].End <= from - 1)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        int last = low;
        while (last < all.Length && all[last].Start <= to)
        {
            last++;
        }
        List<SyntaxSpan> spans = new(last - low);
        for (int i = low; i < last; i++)
        {
            spans.Add(all[i]);
        }
        for (int i = first; i < history.Count; i++)
        {
            CarriedSpans.Forward(spans, history[i].Edits, Word);
        }
        return spans;
    }

    /// <summary>Where the changes made after a version begin in the history.</summary>
    private int After(int version)
    {
        int first = 0;
        while (first < history.Count && history[first].Version <= version)
        {
            first++;
        }
        return first;
    }

    /// <summary>Whether text typed inside a token keeps it one: a name's letters and digits, never what would split it.</summary>
    private static bool Word(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Roslyn's token types, standard and its own, and Razor's for a Razor file's markup, as the editor's kinds:
    /// a component's tag in a type's colour and its parameters in a property's, HTML's in the HTML grammar's kinds,
    /// Razor's directives and transitions as keywords. Operators, punctuation and text are left to
    /// the syntax.</summary>
    private static TokenKind? Kind(string type) => type switch
    {
        "razorTransition" or "razorDirective" or "razorDirectiveColon" or "razorDirectiveAttribute" => TokenKind.Keyword,
        "razorComponentElement" or "razorTagHelperElement" => TokenKind.Class,
        "razorComponentAttribute" or "razorTagHelperAttribute" => TokenKind.Property,
        "razorComment" or "razorCommentStar" or "razorCommentTransition" or "markupComment" or "markupCommentPunctuation" => TokenKind.Comment,
        "markupElement" or "markupTagDelimiter" or "markupOperator" => TokenKind.Tag,
        "markupAttribute" => TokenKind.AttributeName,
        "markupAttributeValue" or "markupAttributeQuote" => TokenKind.AttributeValue,
        "namespace" => TokenKind.Namespace,
        "class" or "recordClass" or "recordClassName" or "className" or "record" => TokenKind.Class,
        "struct" or "recordStruct" or "recordStructName" or "structName" => TokenKind.Struct,
        "interface" or "interfaceName" => TokenKind.Interface,
        "enum" or "enumName" => TokenKind.Enum,
        "delegate" or "delegateName" => TokenKind.Delegate,
        "typeParameter" or "typeParameterName" => TokenKind.TypeParameter,
        "method" or "methodName" or "extensionMethod" or "extensionMethodName" or "function" => TokenKind.Method,
        "property" or "propertyName" => TokenKind.Property,
        "event" or "eventName" => TokenKind.Event,
        "field" or "fieldName" => TokenKind.Field,
        "constant" or "constantName" => TokenKind.Constant,
        "enumMember" or "enumMemberName" => TokenKind.EnumMember,
        "variable" or "local" or "localName" => TokenKind.Local,
        "parameter" or "parameterName" => TokenKind.Parameter,
        "label" or "labelName" => TokenKind.Label,
        "keyword" => TokenKind.Keyword,
        "controlKeyword" => TokenKind.ControlKeyword,
        "string" or "stringVerbatim" => TokenKind.String,
        "stringEscapeCharacter" => TokenKind.StringEscape,
        "number" => TokenKind.Number,
        "comment" => TokenKind.Comment,
        "excludedCode" => TokenKind.Excluded,
        "preprocessorKeyword" or "preprocessorText" => TokenKind.Preprocessor,
        _ when type.StartsWith("xmlDocComment", StringComparison.Ordinal) => TokenKind.DocComment,
        _ => null,
    };

    /// <summary>One answer: its tokens in order of where they start, the version of the text they are for, and the
    /// range they cover in it, or none for the whole file.</summary>
    private sealed record Tokens(SyntaxSpan[] Spans, int Version, TextRange? Covered);
}
