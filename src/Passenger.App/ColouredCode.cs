using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Text;
using TextRun = Avalonia.Controls.Documents.Run;

namespace Passenger.App;

/// <summary>Code shown to read and copy (a card's, a Markdown page's fenced code, an agent's setup), drawn as the editor
/// draws code: on its ground in its font, each stretch in its kind's colour and style, the theme's
/// <c>Passenger.Syntax.*</c> and <c>Passenger.SyntaxStyle.*</c> as the theme gives them the code's language
/// (<see cref="ThemeLanguages"/>), followed as the look changes. Coloured by the spans a server gives (C#'s,
/// <see cref="Languages.CSharp.ServerCode"/>), else by its language's highlighter while it is on screen, let go as it
/// leaves, else plain; selectable and wrapped rather than cut, a card's signature too long for its width laid out a
/// parameter a line (<see cref="Signature"/>).</summary>
public sealed class ColouredCode : Border
{
    private readonly SelectableWords text;
    private readonly Languages.Language? language;
    private readonly IReadOnlyDictionary<int, string>? declarations;
    private TextDocument? document;
    private IHighlighter? highlighter;

    /// <summary>The spans the code is coloured by, of its one-line text; none while it is plain.</summary>
    private IReadOnlyList<SyntaxSpan>? spans;

    /// <summary>The code laid out a parameter a line, for a card's signature that has parameters; and whether it shows so.</summary>
    private Languages.SignatureLines? signature;
    private bool laidOut;

    protected override Type StyleKeyOverride => typeof(Border);

    private ColouredCode(string code, Languages.Language? language, IReadOnlyList<SyntaxSpan>? spans, Languages.Language? coloursOf = null,
        IReadOnlyDictionary<int, string>? declarations = null, Action<string, string>? openDeclaration = null)
    {
        Code = code;
        this.language = language;
        this.declarations = declarations;
        Classes.Add(StyleClasses.Code);
        text = new SelectableWords
        {
            TextWrapping = TextWrapping.Wrap,
            FontFeatures = CSharp.Fonts.NoLigatures,
            Text = code,
            OpenLink = openDeclaration,
        };
        text.Bind(TextElement.ForegroundProperty, text.GetResourceObservable(TokenKeys.Editor_Text));
        Child = text;
        if ((coloursOf ?? language) is { } colours)
        {
            ThemeLanguages.Follow(this, colours.Id);
        }
        if (spans is not null)
        {
            Colour(spans);
        }
    }

    /// <summary>Code in a language, coloured by its highlighter once shown; plain for none.</summary>
    public static ColouredCode Of(string code, Languages.Language? language) => new(code, language, null);

    /// <summary>Code in the colours a server gave it, each kind's as the theme gives it <paramref name="language"/>; never
    /// coloured again by that language's highlighter, which would read it as a file of its own.</summary>
    public static ColouredCode Of(string code, IReadOnlyList<SyntaxSpan> spans, Languages.Language? language = null,
        IReadOnlyDictionary<int, string>? declarations = null, Action<string, string>? openDeclaration = null) =>
        new(code, null, spans, language, declarations, openDeclaration);

    /// <summary>Code of no language, in the code's own colour.</summary>
    public static ColouredCode Plain(string code) => new(code, null, null);

    /// <summary>The code as given, on one line where a signature is laid out a parameter a line.</summary>
    public string Code { get; }

    /// <summary>The code as it shows now: a signature laid out a parameter a line where its width breaks it.</summary>
    public string Shown => laidOut && signature is not null ? signature.Text : Code;

    /// <summary>Whether a signature on one line wider than the room it is given shows laid out a parameter a line, as a
    /// card's does.</summary>
    public bool Signature
    {
        get => signature is not null;
        set
        {
            signature = value ? Languages.SignatureLines.Of(Code) : null;
            laidOut = false;
            InvalidateMeasure();
        }
    }

    /// <summary>A signature as wide as its one line where that fits the room, else laid out a parameter a line.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (signature is not null && !double.IsPositiveInfinity(availableSize.Width))
        {
            LayOut(false);
            double whole = base.MeasureOverride(new Size(double.PositiveInfinity, availableSize.Height)).Width;
            LayOut(whole > availableSize.Width);
        }
        return base.MeasureOverride(availableSize);
    }

    /// <summary>The signature shown on one line or a parameter a line, in its colours.</summary>
    private void LayOut(bool parameterALine)
    {
        if (laidOut == parameterALine)
        {
            return;
        }
        laidOut = parameterALine;
        if (spans is not null)
        {
            Colour(spans);
        }
        else
        {
            text.Inlines?.Clear();
            text.Text = Shown;
        }
    }

    /// <summary>The language its highlighter colours it as, by the name the IDE gives it; null for none.</summary>
    public string? Language => language?.Name;

    /// <summary>The code as drawn once coloured, a run a coloured stretch; none while it is plain, its text whole.</summary>
    public IEnumerable<TextRun> Runs => text.Inlines?.OfType<TextRun>() ?? [];

    /// <summary>The language a Markdown fence's word names, by the file a person would keep it in: <c>csharp</c> as a .cs
    /// file is; null for none the IDE colours.</summary>
    public static Languages.Language? LanguageOf(string info)
    {
        string word = info.Trim('{', '}', '.').ToLowerInvariant();
        string? file = word switch
        {
            "" => null,
            "c#" or "cs" or "csharp" => "code.cs",
            "javascript" or "js" or "jsx" => "code.js",
            "typescript" or "ts" => "code.ts",
            "bash" or "sh" or "shell" or "zsh" => "code.sh",
            "docker" or "dockerfile" => "Dockerfile",
            "markdown" or "md" => "code.md",
            "python" or "py" => "code.py",
            _ when word.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') => $"code.{word}",
            _ => null,
        };
        return file is not null && Languages.Language.ForPath(file) is var language && language != Languages.Language.PlainText ? language : null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (language is null || highlighter is not null)
        {
            return;
        }
        document = new TextDocument(Code);
        // Code no project compiles, coloured alone: a file in no project's symbols and C# are the best guess.
        highlighter = Languages.Highlighters.For(language, document);
        if (highlighter is not null)
        {
            highlighter.Changed += OnColoured;
            Coloured();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (highlighter is not null)
        {
            highlighter.Changed -= OnColoured;
            (highlighter as IDisposable)?.Dispose();
            highlighter = null;
        }
    }

    private void OnColoured(object? sender, EventArgs e) => Coloured();

    /// <summary>The runs again from the highlighter's spans.</summary>
    private void Coloured()
    {
        if (highlighter is null || document is null)
        {
            return;
        }
        TextSnapshot snapshot = document.Snapshot;
        if (highlighter.GetSpans(snapshot, 0, snapshot.Length) is { Count: > 0 } spans)
        {
            Colour(spans);
        }
    }

    /// <summary>A stretch of the code as given drawn strong and underlined (the parameter a call's argument fills), null for none.</summary>
    public (int Start, int Length)? Marked
    {
        get => marked;
        set
        {
            marked = value;
            Colour(spans ?? []);
        }
    }

    private (int Start, int Length)? marked;

    /// <summary>A run for each span of the code as given and for the plain text between, each span's in its kind's look,
    /// the stretch <see cref="Marked"/> strong and underlined, laid out as the code shows now.</summary>
    private void Colour(IReadOnlyList<SyntaxSpan> given)
    {
        spans = given;
        string shown = Shown;
        IReadOnlyList<SyntaxSpan> placed = laidOut && signature is not null ? signature.Map(given) : given;
        (int Start, int End)? strong = null;
        if (marked is { } mark)
        {
            SyntaxSpan stretch = new(mark.Start, mark.Length, TokenKind.Text);
            stretch = laidOut && signature is not null ? signature.Map([stretch])[0] : stretch;
            strong = (stretch.Start, stretch.End);
        }
        List<Inline> runs = [];
        List<SelectableWords.Link>? links = declarations is { Count: > 0 } ? [] : null;
        int at = 0;
        for (int index = 0; index < placed.Count; index++)
        {
            SyntaxSpan span = placed[index];
            if (span.Start < at || span.End > shown.Length)
            {
                continue;
            }
            Add(at, span.Start, null);
            string? key = declarations?.GetValueOrDefault(given[index].Start);
            Add(span.Start, span.End, span.Kind);
            if (key is not null && links is not null)
            {
                links.Add(new SelectableWords.Link(span.Start, span.End, shown[span.Start..span.End], key));
            }
            at = span.End;
        }
        Add(at, shown.Length, null);
        text.Inlines!.Clear();
        // An inline added beside a text would take the text as a run before it.
        text.Text = null;
        text.Inlines.AddRange(runs);
        text.InlineLinks = links;

        // A run of the stretch, split where the strong stretch begins and ends.
        void Add(int from, int to, TokenKind? kind)
        {
            foreach (int cut in strong is { } edges ? (int[])[edges.Start, edges.End] : [])
            {
                if (cut > from && cut < to)
                {
                    Add(from, cut, kind);
                    from = cut;
                }
            }
            if (to <= from)
            {
                return;
            }
            TextRun run = new(shown[from..to]);
            bool isStrong = strong is { } within && from >= within.Start && to <= within.End;
            if (kind is { } coloured && coloured != TokenKind.Text)
            {
                Look(run, coloured, isStrong);
            }
            if (isStrong)
            {
                run.FontWeight = FontWeight.Bold;
                run.TextDecorations = TextDecorations.Underline;
            }
            runs.Add(run);
        }
    }

    /// <summary>A run in its kind's colour, the code's own where the theme gives the kind none, and in the style the theme
    /// sets the kind in, else the kind's own (a heading bold, emphasis italic), as the editor sets it.</summary>
    private void Look(TextRun run, TokenKind kind, bool strong = false)
    {
        run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable($"Passenger.Syntax.{kind}"));
        string style = $"Passenger.SyntaxStyle.{kind}";
        if (!strong)
        {
            run.Bind(TextElement.FontWeightProperty, this.GetResourceObservable(style, set =>
                (set is string named ? named.Contains("Bold", StringComparison.Ordinal) : kind is TokenKind.Heading or TokenKind.Bold) ? FontWeight.Bold : FontWeight.Normal));
        }
        run.Bind(TextElement.FontStyleProperty, this.GetResourceObservable(style, set =>
            (set is string named ? named.Contains("Italic", StringComparison.Ordinal) : kind is TokenKind.UserInput or TokenKind.Italic) ? FontStyle.Italic : FontStyle.Normal));
    }
}
