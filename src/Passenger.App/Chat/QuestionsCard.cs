using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Acp;

namespace Passenger.App.Chat;

/// <summary>The agent's questions before it goes on, as Cursor pins them above the box: "Questions", "1 of 2" with the
/// arrows between them, the question numbered, its choices lettered A, B, C (several may be picked where it asks), and
/// last "Other…", which opens a box for an answer in the person's own words. Skip answers none of them; Continue goes to
/// the next question, and on the last sends the answers. Nothing is answered for the person, and it takes no keys.</summary>
public sealed class QuestionsCard : Border
{
    private readonly TaskCompletionSource<QuestionsAnswer?> answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly QuestionsRequest request;
    private readonly Dictionary<string, List<string>> picked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBox> ownWords = new(StringComparer.Ordinal);
    private readonly ContentControl shown = new();
    private readonly TextBlock place = new() { Classes = { StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
    private readonly Button back;
    private readonly Button next;
    private readonly Button go = new() { Classes = { StyleClasses.Primary }, Content = "Continue" };
    private int at;

    public QuestionsCard(QuestionsRequest request)
    {
        this.request = request;
        Classes.Add(StyleClasses.Framed);
        Padding = new Thickness(10, 8);
        Margin = new Thickness(12, 0, 12, 6);
        RowIcon icon = new() { DataKey = IconKeys.Info, ForegroundKey = TokenKeys.TextMuted, Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        TextBlock title = new() { Text = "Questions", VerticalAlignment = VerticalAlignment.Center };
        back = ToolButtons.Of(IconKeys.ArrowUp, "The question before", () => Show(at - 1));
        next = ToolButtons.Of(IconKeys.ArrowDown, "The next question", () => Show(at + 1));
        StackPanel pager = new() { Orientation = Orientation.Horizontal, Children = { back, place, next } };
        DockPanel.SetDock(pager, Dock.Right);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel heading = new() { Children = { pager, icon, title } };
        Button skip = new() { Classes = { StyleClasses.TextLink }, Content = "Skip", VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(skip, "Answer none of the questions: the agent goes on without them");
        skip.Click += (_, _) => Finish(null);
        go.Click += (_, _) =>
        {
            if (at < request.Questions.Count - 1)
            {
                Show(at + 1);
            }
            else
            {
                Finish(Answers());
            }
        };
        StackPanel footer = new() { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Right, Children = { skip, go } };
        Child = new StackPanel { Spacing = 8, Children = { heading, new ScrollViewer { MaxHeight = 320, Content = shown }, footer } };
        Show(0);
    }

    /// <summary>Framed as Shell.axaml frames a Border, which styles a Border's subclass only through its key.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The answers once sent, null once skipped or cancelled.</summary>
    public Task<QuestionsAnswer?> Answer => answered.Task;

    /// <summary>The question shown, from zero.</summary>
    public int At => at;

    /// <summary>The turn was stopped before the answers: they are given as none.</summary>
    public void Cancel() => Finish(null);

    /// <summary>Picks a choice of the question shown, as a click on it does: a question of one answer takes it alone.</summary>
    public void Pick(string label)
    {
        AgentQuestion question = request.Questions[at];
        List<string> chosen = picked.TryGetValue(question.Id, out List<string>? known) ? known : picked[question.Id] = [];
        if (question.Multiple)
        {
            if (!chosen.Remove(label))
            {
                chosen.Add(label);
            }
        }
        else
        {
            chosen.Clear();
            chosen.Add(label);
        }
        Show(at);
    }

    private void Finish(QuestionsAnswer? answer)
    {
        if (answered.TrySetResult(answer))
        {
            IsVisible = false;
        }
    }

    private QuestionsAnswer Answers()
    {
        Dictionary<string, IReadOnlyList<string>> chosen = picked.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)[.. entry.Value], StringComparer.Ordinal);
        Dictionary<string, string> words = ownWords
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value.Text))
            .ToDictionary(entry => entry.Key, entry => entry.Value.Text!.Trim(), StringComparer.Ordinal);
        return new QuestionsAnswer(chosen, words);
    }

    /// <summary>Shows one question: its words numbered, then its choices lettered, then Other.</summary>
    private void Show(int index)
    {
        if (index < 0 || index >= request.Questions.Count)
        {
            return;
        }
        at = index;
        AgentQuestion question = request.Questions[at];
        place.Text = $"{at + 1} of {request.Questions.Count}";
        back.IsEnabled = at > 0;
        next.IsEnabled = at < request.Questions.Count - 1;
        go.Content = at < request.Questions.Count - 1 ? "Continue" : "Send";
        StackPanel body = new() { Spacing = 4 };
        body.Children.Add(new TextBlock
        {
            Text = $"{at + 1}. {question.Text}",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        if (question.Multiple)
        {
            body.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, Text = "Pick any that apply." });
        }
        IReadOnlyList<string> chosen = picked.TryGetValue(question.Id, out List<string>? known) ? known : [];
        int letter = 0;
        foreach (QuestionChoice choice in question.Choices)
        {
            body.Children.Add(Choice(Letter(letter++), choice.Label, choice.Description, chosen.Contains(choice.Label), () => Pick(choice.Label)));
        }
        string ownId = question.OwnWordsId ?? question.Id;
        if (question.OwnWordsId is not null || question.Choices.Count == 0)
        {
            TextBox words = ownWords.TryGetValue(ownId, out TextBox? kept) ? kept : ownWords[ownId] = new TextBox
            {
                PlaceholderText = question.Choices.Count == 0 ? "Your answer" : "Your own answer, or a note to the choice above",
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MaxHeight = 100,
            };
            (words.Parent as Panel)?.Children.Remove(words);
            if (question.Choices.Count > 0)
            {
                bool writing = words.IsVisible && !string.IsNullOrEmpty(words.Text);
                words.IsVisible = writing;
                body.Children.Add(Choice(Letter(letter), "Other…", null, writing, () =>
                {
                    words.IsVisible = true;
                    words.Focus();
                }));
            }
            body.Children.Add(words);
        }
        shown.Content = body;
    }

    private static string Letter(int index) => ((char)('A' + Math.Min(index, 25))).ToString();

    /// <summary>A choice's row: its letter in a box, its label, and what it means, muted, under it; a chosen one marked.</summary>
    private static Button Choice(string letter, string label, string? description, bool chosen, Action pick)
    {
        Border box = new()
        {
            Classes = { StyleClasses.Box },
            Child = new TextBlock { Text = letter, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
            Width = 20,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        DockPanel.SetDock(box, Dock.Left);
        StackPanel words = new() { Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap } } };
        if (description is { Length: > 0 })
        {
            words.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, Text = description, TextWrapping = TextWrapping.Wrap });
        }
        Button row = new()
        {
            Classes = { chosen ? StyleClasses.Primary : StyleClasses.TextLink },
            Content = new DockPanel { Children = { box, words } },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4),
        };
        row.Click += (_, _) => pick();
        return row;
    }
}
