using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Text;

namespace Passenger.App.Vcs;

/// <summary>What a click on a change bar shows: the lines as the last commit had them, the words that changed marked in
/// them while Highlight Words is on, and the change's actions (previous and next change, Rollback, Show Diff, Highlight
/// Words).</summary>
public sealed class ChangePopup : UserControl
{
    private readonly TextBlock? old;
    private bool highlightWords;

    /// <param name="highlightWordsChanged">Highlight Words turned on or off, for the file's side of the change.</param>
    public ChangePopup(string? before, bool highlightWords, Action previous, Action next, Action rollback, Action diff,
        Action<bool> highlightWordsChanged)
    {
        Before = before;
        this.highlightWords = highlightWords;
        ToggleButton highlight = new() { Classes = { "option" }, IsChecked = highlightWords, Content = new PathIcon(), Focusable = false, Margin = new Thickness(4, 0, 0, 0) };
        ((PathIcon)highlight.Content).Bind(PathIcon.DataProperty, highlight.GetResourceObservable(IconKeys.Highlight));
        ToolTip.SetTip(highlight, "Highlight Words");
        highlight.IsCheckedChanged += (_, _) =>
        {
            this.highlightWords = highlight.IsChecked == true;
            ShowText();
            highlightWordsChanged(this.highlightWords);
        };
        HighlightToggle = highlight;
        StackPanel tools = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                ToolButtons.Of(IconKeys.ArrowUp, "Previous Change", previous),
                ToolButtons.Of(IconKeys.ArrowDown, "Next Change", next),
                ToolButtons.Of(IconKeys.Rollback, "Rollback", rollback),
                ToolButtons.Of(IconKeys.Diff, "Show Diff", diff),
                highlight,
            },
        };
        StackPanel content = new() { Spacing = 6, Children = { tools } };
        if (before is { Length: > 0 })
        {
            old = new TextBlock { FontFamily = CSharp.Fonts.Mono, FontSize = 12, Padding = new Thickness(8, 6) };
            ShowText();
            Border text = new() { Classes = { "box" }, Child = new ScrollViewer { MaxHeight = 260, MaxWidth = 640, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = old } };
            text.Bind(Border.BackgroundProperty, text.GetResourceObservable(TokenKeys.Diff_Modified));
            content.Children.Add(text);
        }
        Content = new Border { Classes = { "popup" }, Padding = new Thickness(6), Child = content };
    }

    /// <summary>The lines the last commit had there, or null for lines that are new.</summary>
    public string? Before { get; }

    /// <summary>The words of <see cref="Before"/> that changed, once worked out.</summary>
    public IReadOnlyList<InlineChange> Words { get; private set; } = [];

    /// <summary>The words of <see cref="Before"/> that changed, worked out after the popup opened.</summary>
    public void ShowWords(IReadOnlyList<InlineChange> words)
    {
        Words = words;
        ShowText();
    }

    /// <summary>The words marked in what the lines were: none while Highlight Words is off.</summary>
    public IReadOnlyList<InlineChange> WordsShown => highlightWords ? Words : [];

    /// <summary>The Highlight Words toggle.</summary>
    public ToggleButton HighlightToggle { get; }

    /// <summary>What the lines were, the changed words on a background of their own while Highlight Words is on.</summary>
    private void ShowText()
    {
        if (old is null || Before is null)
        {
            return;
        }
        InlineCollection inlines = [];
        int at = 0;
        foreach (InlineChange word in WordsShown)
        {
            if (word.Range.Start > at)
            {
                inlines.Add(new Avalonia.Controls.Documents.Run(Before[at..word.Range.Start]));
            }
            Avalonia.Controls.Documents.Run changed = new(Before.Substring(word.Range.Start, word.Range.Length));
            changed.Bind(TextElement.BackgroundProperty, old.GetResourceObservable(word.Kind == ChangeMarkKind.Deleted ? TokenKeys.Diff_DeletedWord : TokenKeys.Diff_ModifiedWord));
            inlines.Add(changed);
            at = word.Range.End;
        }
        if (at < Before.Length)
        {
            inlines.Add(new Avalonia.Controls.Documents.Run(Before[at..]));
        }
        old.Inlines = inlines;
    }
}
