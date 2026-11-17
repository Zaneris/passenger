using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Acp;
using Passenger.App.Editors;

namespace Passenger.App.Chat;

/// <summary>A question the agent asks before it acts, as a card in its reply: an edit with its diff, a command with its
/// text, or a plan to start building, and one button per answer the agent offers, in its own words. The agent waits for
/// the click; nothing is answered for the person, no button is the default on Enter, and the card takes no keys. Once
/// answered, or cancelled by Stop, the buttons give way to what was decided. A plan's card is Cursor's: the plan's title,
/// a link that opens its file as a page, and Build, the agent's first way to build it, its words in the tip.</summary>
public sealed class ApprovalCard : Border
{
    private readonly TaskCompletionSource<string?> answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock chip = new() { Text = "Needs approval", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel choices = new() { Spacing = 6 };

    private readonly ChatPlan? plan;

    private readonly PermissionRequest request;

    public ApprovalCard(PermissionRequest request, IReadOnlyList<ChatChange> changes, Action<Uri> browse, ChatPlan? plan = null)
    {
        this.plan = plan;
        this.request = request;
        Changes = plan is null ? changes : [];
        Classes.Add(StyleClasses.Framed);
        Padding = new Thickness(10, 8);
        chip.Bind(TextBlock.ForegroundProperty, chip.GetResourceObservable(TokenKeys.Warning));
        StackPanel body = new() { Spacing = 8 };
        body.Children.Add(Heading(request.ToolCall, changes));
        foreach (Control part in plan is null ? Shown(request.ToolCall, changes, browse, change => FileRequested?.Invoke(this, change)) : PlanShown(plan))
        {
            body.Children.Add(part);
        }

        // One under another, each its width: an agent's answers are sentences ("Yes, allow all edits during this session").
        // An edit's are Cursor's, Accept and Reject in a row, the agent's own words in their tips.
        bool edit = Changes.Count > 0;
        Panel buttons = edit ? new WrapPanel { ItemSpacing = 6, LineSpacing = 6 } : new StackPanel { Spacing = 6 };
        PermissionOption? build = plan is null ? null : request.Options.FirstOrDefault(option => option.Kind.StartsWith("allow", StringComparison.Ordinal));
        foreach (PermissionOption option in request.Options)
        {
            Button button = new()
            {
                Content = new TextBlock { Text = option == build ? "Build" : edit ? EditWord(option) : option.Name, TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = option.Id,
            };
            if (option == build || (edit && EditWord(option) != option.Name))
            {
                ToolTip.SetTip(button, option.Name);
            }
            if (build is null ? option.Kind == "allow_once" : option == build)
            {
                button.Classes.Add(StyleClasses.Primary);
            }
            button.Click += (_, _) => Decide(option);
            buttons.Children.Add(button);
        }
        choices.Children.Add(buttons);
        if (plan is null && request.Options.Any(option => option.Kind is "allow_always" or "reject_always"))
        {
            choices.Children.Add(new TextBlock
            {
                Classes = { StyleClasses.Muted },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Text = "An “always” answer is kept by the agent beyond this chat.",
            });
        }
        body.Children.Add(choices);
        Child = body;
    }

    /// <summary>Framed as Shell.axaml frames a Border, which styles a Border's subclass only through its key.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The plan's file was asked to be opened as a page.</summary>
    public event EventHandler<string>? PlanRequested;

    /// <summary>A changed file's link was clicked, to see the change in the file's own editor.</summary>
    public event EventHandler<ChatChange>? FileRequested;

    /// <summary>The edits the question asks to make, none for a command or a plan.</summary>
    public IReadOnlyList<ChatChange> Changes { get; }

    /// <summary>Answers as Accept or Reject would, from the file's own banner: the agent's once-only answer of that kind,
    /// else its first of that kind; whether there was one to give.</summary>
    public bool Choose(bool allow)
    {
        string once = allow ? "allow_once" : "reject_once";
        string kind = allow ? "allow" : "reject";
        if (Answer.IsCompleted
            || (request.Options.FirstOrDefault(option => option.Kind == once)
                ?? request.Options.FirstOrDefault(option => option.Kind.StartsWith(kind, StringComparison.Ordinal))) is not { } chosen)
        {
            return false;
        }
        Decide(chosen);
        return true;
    }

    /// <summary>An edit's answer in Cursor's words: its once-only ones Accept and Reject, any other as the agent says it.</summary>
    private static string EditWord(PermissionOption option) => option.Kind switch
    {
        "allow_once" => "Accept",
        "reject_once" => "Reject",
        _ => option.Name,
    };

    /// <summary>The id of the option clicked, or null once cancelled.</summary>
    public Task<string?> Answer => answered.Task;

    /// <summary>What the card says of its question now: "Needs approval", "Accepted", "Rejected", "Cancelled".</summary>
    public string State => chip.Text ?? string.Empty;

    /// <summary>The turn was stopped before an answer: the agent is told cancelled.</summary>
    public void Cancel()
    {
        if (answered.TrySetResult(null))
        {
            Settle("Cancelled", TokenKeys.TextMuted);
        }
    }

    private void Decide(PermissionOption option)
    {
        if (answered.TrySetResult(option.Id))
        {
            bool allowed = option.Kind.StartsWith("allow", StringComparison.Ordinal);
            Settle(plan is null ? allowed ? "Accepted" : "Rejected" : allowed ? "Building" : "Planning on",
                allowed ? TokenKeys.Vcs_Added : TokenKeys.TextMuted);
        }
    }

    private void Settle(string state, string colour)
    {
        chip.Text = state;
        chip.Bind(TextBlock.ForegroundProperty, chip.GetResourceObservable(colour));
        choices.IsVisible = false;
    }

    /// <summary>The question's line: its title, what it adds and removes where it edits, and its state.</summary>
    private DockPanel Heading(ToolCall call, IReadOnlyList<ChatChange> changes)
    {
        // Docked, the title last, so a long one ends in an ellipsis before the counts and the state rather than under them.
        DockPanel heading = new();
        chip.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(chip, Dock.Right);
        heading.Children.Add(chip);
        StackPanel left = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(left, Dock.Right);
        TextBlock title = new()
        {
            Text = plan is not null ? $"Plan: {plan.Title}" : call.Title.Length > 0 ? call.Title : "The agent asks",
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(title, title.Text);
        if (changes.Count > 0)
        {
            int added = 0;
            int removed = 0;
            foreach (ChatChange change in changes)
            {
                (int a, int r) = ChatDiff.Count(change);
                added += a;
                removed += r;
            }
            left.Children.Add(Counted($"+{added}", TokenKeys.Vcs_Added));
            left.Children.Add(Counted($"−{removed}", TokenKeys.Vcs_Deleted));
        }
        heading.Children.Add(left);
        heading.Children.Add(title);
        return heading;
    }

    /// <summary>What the question is about: each change's path and diff, a command's text, or the agent's words (a plan).</summary>
    private static IEnumerable<Control> Shown(ToolCall call, IReadOnlyList<ChatChange> changes, Action<Uri> browse, Action<ChatChange> open)
    {
        foreach (ChatChange change in changes)
        {
            // The file's path is its link: the change shown in the file's own editor, to accept or reject there.
            Button link = new()
            {
                Classes = { StyleClasses.Link },
                Content = new TextBlock { Text = change.IsNew ? $"{change.Path} (new)" : change.Path, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            ToolTip.SetTip(link, "Open the file with the change shown in it");
            link.Click += (_, _) => open(change);
            yield return link;
            yield return ChatDiff.View(change);
        }
        if (call.Kind == "execute")
        {
            yield return CodeBlock.Of(call.Title);
        }
        foreach (string text in call.Texts)
        {
            StackPanel words = new() { Spacing = 6 };
            Markdown.Render(Markdown.Parse(text), words, browse);
            yield return words;
        }
    }

    /// <summary>What a plan's card shows of it: a link to its file and where that is, and that an edit is built.</summary>
    private IEnumerable<Control> PlanShown(ChatPlan shown)
    {
        Button open = new() { Classes = { StyleClasses.Link }, Content = "Open plan", HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => PlanRequested?.Invoke(this, shown.Path);
        yield return open;
        yield return new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, Text = shown.Shown, TextTrimming = TextTrimming.CharacterEllipsis };
        yield return new TextBlock
        {
            Classes = { StyleClasses.Muted },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Text = "Change the file before Build and the agent builds your version.",
        };
    }

    private static TextBlock Counted(string text, string colour)
    {
        TextBlock counted = new() { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        counted.Bind(TextBlock.ForegroundProperty, counted.GetResourceObservable(colour));
        return counted;
    }
}

/// <summary>A plan the agent asks to build, as its file: where it is, how the card says where, and its title.</summary>
public sealed record ChatPlan(string Path, string Shown, string Title);
