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
/// <c>Passenger.Syntax.*</c> and <c>Passenger.SyntaxStyle.*</c>, followed as the look changes. Coloured by the spans a
/// server gives (C#'s, <see cref="Languages.CSharp.ServerCode"/>), else by its language's highlighter while it is on screen,
/// let go as it leaves, else plain; selectable and wrapped rather than cut.</summary>
public sealed class ColouredCode : Border
{
    private readonly SelectableTextBlock text;
    private readonly Languages.Language? language;
    private TextDocument? document;
    private IHighlighter? highlighter;

    protected override Type StyleKeyOverride => typeof(Border);

    private ColouredCode(string code, Languages.Language? language, IReadOnlyList<SyntaxSpan>? spans)
    {
        Code = code;
        this.language = language;
        Classes.Add(StyleClasses.Code);
        text = new SelectableTextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontFeatures = CSharp.Fonts.NoLigatures,
            Text = code,
        }.InCodeFont();
        text.Bind(TextElement.ForegroundProperty, text.GetResourceObservable(TokenKeys.Editor_Text));
        Child = text;
        if (spans is not null)
        {
            Colour(spans);
        }
    }

    /// <summary>Code in a language, coloured by its highlighter once shown; plain for none.</summary>
    public static ColouredCode Of(string code, Languages.Language? language) => new(code, language, null);

    /// <summary>Code in the colours a server gave it.</summary>
    public static ColouredCode Of(string code, IReadOnlyList<SyntaxSpan> spans) => new(code, null, spans);

    /// <summary>Code of no language, in the code's own colour.</summary>
    public static ColouredCode Plain(string code) => new(code, null, null);

    /// <summary>The code as shown.</summary>
    public string Code { get; }

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

    /// <summary>A run for each span and for the plain text between, each span's in its kind's look.</summary>
    private void Colour(IReadOnlyList<SyntaxSpan> spans)
    {
        List<Inline> runs = [];
        int at = 0;
        foreach (SyntaxSpan span in spans)
        {
            if (span.Start < at || span.End > Code.Length)
            {
                continue;
            }
            if (span.Start > at)
            {
                runs.Add(new TextRun(Code[at..span.Start]));
            }
            TextRun coloured = new(Code[span.Start..span.End]);
            if (span.Kind != TokenKind.Text)
            {
                Look(coloured, span.Kind);
            }
            runs.Add(coloured);
            at = span.End;
        }
        if (at < Code.Length)
        {
            runs.Add(new TextRun(Code[at..]));
        }
        text.Inlines!.Clear();
        // An inline added beside a text would take the text as a run before it.
        text.Text = null;
        text.Inlines.AddRange(runs);
    }

    /// <summary>A run in its kind's colour, the code's own where the theme gives the kind none, and in the style the theme
    /// sets the kind in, else the kind's own (a heading bold, emphasis italic), as the editor sets it.</summary>
    private void Look(TextRun run, TokenKind kind)
    {
        run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable($"Passenger.Syntax.{kind}"));
        string style = $"Passenger.SyntaxStyle.{kind}";
        run.Bind(TextElement.FontWeightProperty, this.GetResourceObservable(style, set =>
            (set is string named ? named.Contains("Bold", StringComparison.Ordinal) : kind is TokenKind.Heading or TokenKind.Bold) ? FontWeight.Bold : FontWeight.Normal));
        run.Bind(TextElement.FontStyleProperty, this.GetResourceObservable(style, set =>
            (set is string named ? named.Contains("Italic", StringComparison.Ordinal) : kind is TokenKind.UserInput or TokenKind.Italic) ? FontStyle.Italic : FontStyle.Normal));
    }
}
