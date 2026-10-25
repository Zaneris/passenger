using Avalonia.Controls;
using Passenger.App.Docking;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Where what the status bar says has more to see, and the way there: a click on the words goes, while they are
/// said. <see cref="Name"/> is what the click does, as the tooltip words it after "Click to".</summary>
public sealed record StatusPlace(string Name, Action Go)
{
    /// <summary>A tool window, shown in its place.</summary>
    public static StatusPlace Window(DockHost dock, string id) => new($"open the {dock[id].Title} window", () => dock.Show(id));

    /// <summary>Problems on one of its tabs, the first problem <paramref name="which"/> picks chosen.</summary>
    public static StatusPlace Problems(DockHost dock, ProblemsModel problems, ProblemsTab tab, Func<Problem, bool>? which = null) =>
        new($"see it in Problems, {ProblemsModel.TitleOf(tab)}", () =>
        {
            dock.Show(ToolWindowIds.Problems);
            problems.Reveal(tab, which);
        });

}

/// <summary>Says something in the window's status bar, with where a click on it shows more (none when null).</summary>
public delegate void StatusSay(string said, StatusPlace? more = null);

/// <summary>The window's status bar: what the last action came to (an error's or a warning's marked with its icon until
/// something else is said, and a link to where it has more to see while it has a place, its whole text on hover), and
/// the file shown's place (its path while nothing is said), caret, line separator, encoding, indent and language with
/// the language server's state. It says what it is told; what an operation came to is the operation's own answer, never
/// read back from here. A UI-thread object of the window.</summary>
public sealed class StatusBar
{
    private readonly Button message;
    private readonly TextBlock text;
    private readonly PathIcon icon;
    private readonly WorkingRing working;
    private readonly TextBlock caret;
    private readonly TextBlock indent;
    private readonly TextBlock language;
    private readonly Button lineSeparator;
    private readonly TextBlock lineEnding;
    private readonly Button encoding;
    private readonly TextBlock encodingText;

    /// <summary>What was said through <see cref="Say(string, ProblemSeverity?, StatusPlace?)"/>, with its mark and place;
    /// anything else said takes the icon and the place away.</summary>
    private (string Text, StatusPlace? Place)? standing;

    public StatusBar(Button message, TextBlock text, PathIcon icon, WorkingRing working, TextBlock caret, TextBlock indent, TextBlock language, Button lineSeparator,
        TextBlock lineEnding, Button encoding, TextBlock encodingText)
    {
        this.message = message;
        this.text = text;
        this.icon = icon;
        this.working = working;
        this.caret = caret;
        this.indent = indent;
        this.language = language;
        this.lineSeparator = lineSeparator;
        this.lineEnding = lineEnding;
        this.encoding = encoding;
        this.encodingText = encodingText;
        text.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBlock.TextProperty)
            {
                return;
            }
            if (text.Text != standing?.Text)
            {
                standing = null;
                icon.IsVisible = false;
            }
            Placed();
        };
        message.Click += (_, _) => Place?.Go();
    }

    /// <summary>What the status bar says now, for the person: told by whatever finished last.</summary>
    public string? Text
    {
        get => text.Text;
        set => text.Text = value;
    }

    /// <summary>Whether what it says is an error, a warning or neither, as its icon shows it.</summary>
    public ProblemSeverity? Severity => icon.IsVisible ? (ProblemSeverity?)icon.Tag : null;

    /// <summary>Where a click on what it says goes, while it says it; null when it goes nowhere.</summary>
    public StatusPlace? Place => standing?.Place;

    /// <summary>Says something, an error or a warning with its icon, and where a click on it shows more.</summary>
    public void Say(string said, ProblemSeverity? severity, StatusPlace? more = null)
    {
        standing = (said, more);
        text.Text = said;
        Placed();
        if (severity is not { } kind)
        {
            icon.IsVisible = false;
            return;
        }
        bool error = kind == ProblemSeverity.Error;
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(error ? IconKeys.Error : IconKeys.Problems));
        icon.Bind(PathIcon.ForegroundProperty, icon.GetResourceObservable(error ? TokenKeys.Error : TokenKeys.Warning));
        icon.Tag = kind;
        icon.IsVisible = true;
    }

    /// <summary>What it says now, with its mark and place, for what covers it a while and puts it back (a chord's prompt).</summary>
    public (string? Text, ProblemSeverity? Severity, StatusPlace? Place) Said => (Text, Severity, Place);

    /// <summary>Puts back what <see cref="Said"/> gave.</summary>
    public void SayAgain((string? Text, ProblemSeverity? Severity, StatusPlace? Place) said)
    {
        if (said.Text is not { } words)
        {
            Text = null;
            return;
        }
        Say(words, said.Severity, said.Place);
    }

    /// <summary>The words a link while they have a place (the hand, the link's look under the pointer, a key's reach), and
    /// their whole on hover, which a narrow bar cuts.</summary>
    private void Placed()
    {
        StatusPlace? place = Place;
        message.Classes.Set("place", place is not null);
        string? said = text.Text;
        ToolTip.SetTip(message, string.IsNullOrEmpty(said) ? null : place is null ? said : $"{said}\n\nClick to {place.Name}");
    }

    /// <summary>An action's work begun, at a new stage, or ended: its stage said beside the turning ring while it is under
    /// way, the ring there while any work is, and once none is, whatever was said last left standing.</summary>
    public void ShowWork(WorkChange change, Working? latest)
    {
        working.IsVisible = latest is not null;
        if (!change.Ended)
        {
            Say(change.Work.Stage, null);
        }
    }

    /// <summary>The file shown, or none: with none the message line names the folder (or says there is no solution); with
    /// one, its path names it, so the message line is left for messages.</summary>
    public void ShowFile(OpenDocument? active, string? folder, CodeEditor editor, string languageLabel)
    {
        string where = folder is null ? "No solution" : Path.GetFileName(folder);
        text.Text = active is null ? where : string.Empty;
        ShowStorage(active);
        OpenDocument? textual = Textual(active);
        indent.Text = textual?.Indentation.Shown ?? "";
        ToolTip.SetTip(indent, textual?.Indentation.Tip(textual.Language.Name));
        ShowLanguage(languageLabel);
        ShowCaret(textual is null ? null : editor);
    }

    /// <summary>The file shown while it is text: a workbook's or an asset preview's tab holds none, so it has no breaks,
    /// encoding, indent or caret.</summary>
    private static OpenDocument? Textual(OpenDocument? active) => active is { HoldsText: true } ? active : null;

    /// <summary>The file's line separator and encoding, which a click there offers others of.</summary>
    public void ShowStorage(OpenDocument? shown)
    {
        OpenDocument? active = Textual(shown);
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
