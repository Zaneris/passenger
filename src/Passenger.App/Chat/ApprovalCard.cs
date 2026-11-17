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
/// answered, or cancelled by Stop, the buttons give way to what was decided. A plan's card is Claude Code's: the plan's
/// title, links that open its file as a page or to edit, the agent's answers numbered, and a box to say what to revise.</summary>
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
        foreach (Control part in plan is null ? Shown(request.ToolCall, changes, browse) : PlanShown(plan))
        {
            body.Children.Add(part);
        }

        // One under another, each its width: an agent's answers are sentences ("Yes, allow all edits during this session").
        // An edit's are Cursor's, Accept and Reject in a row, the agent's own words in their tips. A plan's are Claude
        // Code's, numbered as its terminal lists them, the first the one it suggests, and a box to say what to revise.
        bool edit = Changes.Count > 0;
        Panel buttons = edit ? new WrapPanel { ItemSpacing = 6, LineSpacing = 6 } : new StackPanel { Spacing = 6 };
        int number = 0;
        foreach (PermissionOption option in request.Options)
        {
            Button button = new()
            {
                Content = plan is null
                    ? new TextBlock { Text = edit ? EditWord(option) : option.Name, TextWrapping = TextWrapping.Wrap }
                    : Numbered(++number, option.Name),
                HorizontalAlignment = plan is null ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Tag = option.Id,
            };
            if (edit && EditWord(option) != option.Name)
            {
                ToolTip.SetTip(button, option.Name);
            }
            if (plan is null ? option.Kind == "allow_once" : number == 1)
            {
                button.Classes.Add(StyleClasses.Primary);
            }
            button.Click += (_, _) => Decide(option);
            buttons.Children.Add(button);
        }
        choices.Children.Add(buttons);
        if (plan is not null && request.Options.FirstOrDefault(option => option.Kind.StartsWith("reject", StringComparison.Ordinal)) is { } keepPlanning)
        {
            choices.Children.Add(Revising(keepPlanning));
        }
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

    /// <summary>The plan's file was asked to be opened for editing, its source beside its page.</summary>
    public event EventHandler<string>? PlanEditRequested;

    /// <summary>A changed file was clicked, by its title, to see the change in the file's own editor.</summary>
    public event EventHandler<ChatChange>? FileRequested;

    /// <summary>What the person asked the agent to change in its plan, sent with the answer to keep planning; null for none.</summary>
    public string? Revision { get; private set; }

    /// <summary>An answer as Claude Code's terminal lists it: its number, muted, then its words.</summary>
    private static DockPanel Numbered(int number, string words)
    {
        TextBlock place = new() { Classes = { StyleClasses.Muted }, Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 18 };
        DockPanel.SetDock(place, Dock.Left);
        return new DockPanel { Children = { place, new TextBlock { Text = words, TextWrapping = TextWrapping.Wrap } } };
    }

    /// <summary>Claude Code's "tell Claude what to change": a box for what to revise and Revise, which keeps planning with it.</summary>
    private DockPanel Revising(PermissionOption keepPlanning)
    {
        TextBox words = new() { PlaceholderText = "Tell the agent what to change in the plan", TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 120 };
        Button revise = new() { Content = "Revise", IsEnabled = false, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        words.TextChanged += (_, _) => revise.IsEnabled = !string.IsNullOrWhiteSpace(words.Text);
        revise.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(words.Text) && !Answer.IsCompleted)
            {
                Revision = words.Text.Trim();
                Decide(keepPlanning);
            }
        };
        DockPanel.SetDock(revise, Dock.Right);
        return new DockPanel { Children = { revise, words } };
    }

    /// <summary>The id of the agent's call the question asks about.</summary>
    public string CallId => request.ToolCall.Id;

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
            Settle(plan is null ? allowed ? "Accepted" : "Rejected" : allowed ? "Building" : Revision is not null ? "Revising" : "Planning on",
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
        Control named = title;
        if (changes.Count > 0)
        {
            // An edit's title opens its file, as Cursor's does: the change shown in the file's own editor.
            Button open = new() { Classes = { StyleClasses.TextLink }, Content = title, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(open, $"Open {(changes[0].IsNew ? $"{changes[0].Path} (new)" : changes[0].Path)} with the change shown in it");
            open.Click += (_, _) => FileRequested?.Invoke(this, changes[0]);
            named = open;
        }
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
        heading.Children.Add(named);
        return heading;
    }

    /// <summary>What the question is about: each change's path and diff, a command's text, or the agent's words (a plan).</summary>
    private static IEnumerable<Control> Shown(ToolCall call, IReadOnlyList<ChatChange> changes, Action<Uri> browse)
    {
        foreach (ChatChange change in changes)
        {
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
        Button open = new() { Classes = { StyleClasses.Link }, Content = "Open plan" };
        open.Click += (_, _) => PlanRequested?.Invoke(this, shown.Path);
        Button edit = new() { Classes = { StyleClasses.Link }, Content = "Edit plan" };
        ToolTip.SetTip(edit, "Edit the plan's file, its page beside it");
        edit.Click += (_, _) => PlanEditRequested?.Invoke(this, shown.Path);
        yield return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { open, edit } };
        yield return new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, Text = shown.Shown, TextTrimming = TextTrimming.CharacterEllipsis };
        yield return new TextBlock
        {
            Classes = { StyleClasses.Muted },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Text = "Edit the plan before you answer and the agent builds your version.",
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
