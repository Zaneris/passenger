using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Passenger.Editor;
using Passenger.Text;
using TextRun = Avalonia.Controls.Documents.Run;

namespace Passenger.App.Editors;

/// <summary>What a page of rendered Markdown opens and draws: release notes (What's New) a web link alone and no image; a
/// file's preview also its relative links and <c>#heading</c>s, and the images of files inside the open folder, which it
/// loads itself (<see cref="Load"/>). Every link a page draws is handed to <see cref="Follow"/>.</summary>
internal sealed class MarkdownPage(Action<Uri> follow)
{
    public Action<Uri> Follow { get; } = follow;

    /// <summary>Whether a link to a file beside the page's (or a heading of it) is one: a file's preview only.</summary>
    public bool LocalLinks { get; init; }

    /// <summary>The file an image's target names when the page may draw it, else null (a web image, one outside the open
    /// folder, a file of no image type or none there); null on a page that draws no image.</summary>
    public Func<string, string?>? ImageFile { get; init; }

    /// <summary>A picture's decode, off the window's thread, null when it could not be read.</summary>
    public Func<string, Task<Bitmap?>>? Load { get; init; }

    /// <summary>What a link's target opens: a web address, or on a page of local links a relative one (a file beside it or
    /// a heading); null for anything else, which is drawn as its words.</summary>
    public Uri? LinkOf(string target)
    {
        if (Markdown.Web(target) is { } web)
        {
            return web;
        }
        string written = target.Trim().Trim('<', '>');
        return LocalLinks && written.Length > 0 && !written.StartsWith("//", StringComparison.Ordinal)
            && !Uri.TryCreate(written, UriKind.Absolute, out _) && Uri.TryCreate(written, UriKind.Relative, out Uri? local)
            ? local
            : null;
    }

    /// <summary>An image of a file the page may draw, its words standing for it until it is read and should it not read.</summary>
    public Control Picture(string file, string alt) => new MarkdownPicture(file, alt, Load);
}

/// <summary>A picture of a file in a page, no wider than the page and never enlarged; its words in its place until its
/// decode lands off the window's thread, and should it fail.</summary>
public sealed class MarkdownPicture : Panel
{
    private readonly Image image;
    private readonly TextBlock alt;

    internal MarkdownPicture(string file, string words, Func<string, Task<Bitmap?>>? load)
    {
        File = file;
        Classes.Add("mdPicture");
        image = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        alt = new TextBlock { Classes = { "muted" }, Text = words.Length > 0 ? words : Path.GetFileName(file), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(this, alt.Text);
        Children.Add(image);
        Children.Add(alt);
        if (load is not null)
        {
            IdeErrors.Observe(ShowAsync(load(file)));
        }
    }

    /// <summary>The file it draws.</summary>
    public string File { get; }

    /// <summary>The picture drawn, null until it is read and when it could not be.</summary>
    public Bitmap? Shown => image.Source as Bitmap;

    private async Task ShowAsync(Task<Bitmap?> loading)
    {
        if (await loading is { } bitmap)
        {
            image.Source = bitmap;
            image.IsVisible = true;
            alt.IsVisible = false;
        }
    }
}

/// <summary>Fenced code on the editor's ground in its font, coloured as the editor colours its language
/// (<see cref="Languages.Highlighters"/>) once it is shown: a highlighter while it is on screen, let go as it
/// leaves.</summary>
public sealed class MarkdownCode : Border
{
    private readonly SelectableTextBlock text;
    private readonly string code;
    private readonly Languages.Language? language;
    private TextDocument? document;
    private IHighlighter? highlighter;

    protected override Type StyleKeyOverride => typeof(Border);

    private MarkdownCode(string code, Languages.Language? language)
    {
        this.code = code;
        this.language = language;
        Classes.Add("code");
        // "=>" as written rather than the font's arrow.
        text = new SelectableTextBlock
        {
            FontFamily = CodeBlock.Font,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontFeatures = [new FontFeature { Tag = "calt", Value = 0 }, new FontFeature { Tag = "liga", Value = 0 }],
            Inlines = [new TextRun(code)],
        };
        Child = text;
    }

    internal static MarkdownCode Of(Markdown.Code code) => new(code.Text, LanguageOf(code.Info));

    /// <summary>The language the editor colours it as, by the name the IDE gives it; null for none.</summary>
    public string? Language => language?.Name;

    /// <summary>The code as drawn, a run a coloured stretch.</summary>
    public IEnumerable<TextRun> Runs => text.Inlines!.OfType<TextRun>();

    /// <summary>The language a fence's word names, by the file a person would keep it in: <c>csharp</c> as a .cs file is.</summary>
    private static Languages.Language? LanguageOf(string info)
    {
        string word = info.Trim('{', '}', '.').ToLowerInvariant();
        string? file = word switch
        {
            "" => null,
            "c#" or "cs" or "csharp" => "code.cs",
            "javascript" or "js" or "jsx" => "code.js",
            "typescript" or "ts" or "tsx" => "code.ts",
            "bash" or "sh" or "shell" or "zsh" => "code.sh",
            "docker" or "dockerfile" => "Dockerfile",
            "markdown" or "md" => "code.md",
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
        document = new TextDocument(code);
        highlighter = Languages.Highlighters.For(language, document);
        if (highlighter is not null)
        {
            highlighter.Changed += OnColoured;
            Colour();
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

    private void OnColoured(object? sender, EventArgs e) => Colour();

    /// <summary>The code's runs again from the highlighter's spans, each in its kind's syntax brush.</summary>
    private void Colour()
    {
        if (highlighter is null || document is null)
        {
            return;
        }
        TextSnapshot snapshot = document.Snapshot;
        IReadOnlyList<SyntaxSpan> spans = highlighter.GetSpans(snapshot, 0, snapshot.Length);
        if (spans.Count == 0)
        {
            return;
        }
        List<Inline> runs = [];
        int at = 0;
        foreach (SyntaxSpan span in spans)
        {
            if (span.Start < at || span.End > code.Length)
            {
                continue;
            }
            if (span.Start > at)
            {
                runs.Add(new TextRun(code[at..span.Start]));
            }
            TextRun coloured = new(code[span.Start..span.End]);
            // A kind with no brush is drawn in the code's own colour; every theme has the same tokens.
            string brush = $"Passenger.Syntax.{span.Kind}";
            if (span.Kind != TokenKind.Text && this.TryFindResource(brush, ActualThemeVariant, out _))
            {
                coloured.Bind(TextElement.ForegroundProperty, this.GetResourceObservable(brush));
            }
            runs.Add(coloured);
            at = span.End;
        }
        if (at < code.Length)
        {
            runs.Add(new TextRun(code[at..]));
        }
        text.Inlines!.Clear();
        text.Inlines.AddRange(runs);
    }
}
