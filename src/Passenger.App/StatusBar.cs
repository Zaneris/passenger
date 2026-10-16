using Avalonia.Controls;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The window's status bar, as Rider's: what the last action came to (an error's or a warning's marked with its
/// icon, as Rider's status bar shows a notification's, until something else is said), and the file shown's place (its
/// path while nothing is said), caret, line separator, encoding, indent and language with the language server's state. It
/// says what it is told; what an operation came to is the operation's own answer, never read back from here. A UI-thread
/// object of the window.</summary>
public sealed class StatusBar
{
    private readonly TextBlock text;
    private readonly PathIcon icon;
    private readonly TextBlock caret;
    private readonly TextBlock indent;
    private readonly TextBlock language;
    private readonly Button lineSeparator;
    private readonly TextBlock lineEnding;
    private readonly Button encoding;
    private readonly TextBlock encodingText;

    /// <summary>What was said with an error's or a warning's icon; anything else said takes the icon away.</summary>
    private string? marked;

    public StatusBar(TextBlock text, PathIcon icon, TextBlock caret, TextBlock indent, TextBlock language, Button lineSeparator, TextBlock lineEnding,
        Button encoding, TextBlock encodingText)
    {
        this.text = text;
        this.icon = icon;
        this.caret = caret;
        this.indent = indent;
        this.language = language;
        this.lineSeparator = lineSeparator;
        this.lineEnding = lineEnding;
        this.encoding = encoding;
        this.encodingText = encodingText;
        text.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
            {
                ToolTip.SetTip(text, string.IsNullOrEmpty(text.Text) ? null : text.Text);
            }
            if (e.Property == TextBlock.TextProperty && text.Text != marked)
            {
                marked = null;
                icon.IsVisible = false;
            }
        };
    }

    /// <summary>What the status bar says now, for the person: told by whatever finished last.</summary>
    public string? Text
    {
        get => text.Text;
        set => text.Text = value;
    }

    /// <summary>Whether what it says is an error, a warning or neither, as its icon shows it.</summary>
    public ProblemSeverity? Severity => icon.IsVisible ? (ProblemSeverity?)icon.Tag : null;

    /// <summary>Says something, an error or a warning with its icon.</summary>
    public void Say(string said, ProblemSeverity? severity)
    {
        marked = severity is null ? null : said;
        text.Text = said;
        if (severity is not { } kind)
        {
            icon.IsVisible = false;
            return;
        }
        bool error = kind == ProblemSeverity.Error;
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(error ? "Icon.Error" : "Icon.Problems"));
        icon.Bind(PathIcon.ForegroundProperty, icon.GetResourceObservable(error ? "Passenger.Editor.Error" : "Passenger.Editor.Warning"));
        icon.Tag = kind;
        icon.IsVisible = true;
    }

    /// <summary>The file shown, or none: with none the message line names the folder (or says there is no solution); with
    /// one, its path names it, so the message line is left for messages.</summary>
    public void ShowFile(OpenDocument? active, string? folder, CodeEditor editor, string languageLabel)
    {
        string where = folder is null ? "No solution" : Path.GetFileName(folder);
        text.Text = active is null ? where : string.Empty;
        ShowStorage(active);
        indent.Text = active?.Indentation.Shown ?? "";
        ToolTip.SetTip(indent, active?.Indentation.Tip(active.Language.Name));
        ShowLanguage(languageLabel);
        ShowCaret(active is null ? null : editor);
    }

    /// <summary>The file's line separator and encoding, which a click there offers others of.</summary>
    public void ShowStorage(OpenDocument? active)
    {
        lineSeparator.IsVisible = active is not null;
        encoding.IsVisible = active is not null;
        lineEnding.Text = active?.Separator.ToString() ?? string.Empty;
        encodingText.Text = active?.EncodingName ?? string.Empty;
        ToolTip.SetTip(lineSeparator, active?.SeparatorTip);
        ToolTip.SetTip(encoding, active?.EncodingTip);
    }

    /// <summary>The file's language, with the language server's state or work beside it as the window words it.</summary>
    public void ShowLanguage(string label) => language.Text = label;

    /// <summary>The caret's line and column, the carets there are, or what is selected; nothing with no file shown.</summary>
    public void ShowCaret(CodeEditor? editor)
    {
        if (editor is null)
        {
            caret.Text = "";
            return;
        }
        TextPosition at = editor.CaretPosition;
        int carets = editor.Selections.Count;
        int selected = editor.Selections.Items.Sum(s => s.Length);
        string position = $"{at.Line + 1}:{at.Column + 1}";
        caret.Text = carets > 1 ? $"{carets} carets" : selected > 0 ? $"{position} ({selected} chars)" : position;
    }
}
