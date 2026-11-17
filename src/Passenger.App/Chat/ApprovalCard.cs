using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Acp;
using Passenger.App.Editors;
using Passenger.Editor;

namespace Passenger.App.Chat;

/// <summary>A question the agent asks before it acts, as a card in its reply: an edit with its diff, a command with its
/// text, or a plan to start building, and one button per answer the agent offers, in its own words. The agent waits for
/// the click; nothing is answered for the person, no button is the default on Enter, and the card takes no keys. Once
/// answered, or cancelled by Stop, the buttons give way to what was decided. A plan's card is Cursor's
/// (<see cref="PlanCard"/>).</summary>
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
        if (plan is not null)
        {
            Child = PlanCard(plan);
            return;
        }
        if (request.ToolCall.Kind == "execute" && changes.Count == 0)
        {
            Child = CommandCard(request.ToolCall);
            return;
        }
        StackPanel body = new() { Spacing = 8 };
        body.Children.Add(Heading(request.ToolCall, changes));
        foreach (Control part in Shown(request.ToolCall, changes, browse))
        {
            body.Children.Add(part);
        }

        // One under another, each its width: an agent's answers are sentences ("Yes, allow all edits during this session").
        // An edit's are Cursor's, Accept and Reject in a row, the agent's own words in their tips.
        bool edit = Changes.Count > 0;
        Panel buttons = edit ? new WrapPanel { ItemSpacing = 6, LineSpacing = 6 } : new StackPanel { Spacing = 6 };
        foreach (PermissionOption option in request.Options)
        {
            Button button = new()
            {
                Content = new TextBlock { Text = edit ? EditWord(option) : option.Name, TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = option.Id,
            };
            if (edit && EditWord(option) != option.Name)
            {
                ToolTip.SetTip(button, option.Name);
            }
            if (option.Kind == "allow_once")
            {
                button.Classes.Add(StyleClasses.Primary);
            }
            button.Click += (_, _) => Decide(option);
            buttons.Children.Add(button);
        }
        choices.Children.Add(buttons);
        if (request.Options.Any(option => option.Kind is "allow_always" or "reject_always"))
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

    /// <summary>Whether it asks to build a plan.</summary>
    public bool IsPlan => plan is not null;

    /// <summary>The plan's file, for a plan's question.</summary>
    public string? PlanPath => plan?.Path;

    /// <summary>A plan's card as Cursor's: "Created Plan", its title and what it says, then View Plan, Edit Plan and Build,
    /// the agent's first way to build it, its other answers under Build's arrow. What the person writes in the box while
    /// it waits revises the plan (<see cref="Revise"/>).</summary>
    private StackPanel PlanCard(ChatPlan shown)
    {
        TextBlock label = new() { Classes = { StyleClasses.Muted }, FontSize = 11, Text = "Created Plan", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(chip, Dock.Right);
        DockPanel top = new() { Children = { chip, label } };
        TextBlock title = new() { Text = shown.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        StackPanel body = new() { Spacing = 6, Children = { top, title } };
        if (shown.Summary.Length > 0)
        {
            body.Children.Add(new TextBlock { Text = shown.Summary, TextWrapping = TextWrapping.Wrap });
        }
        Button view = new() { Classes = { StyleClasses.TextLink }, Content = "View Plan", VerticalAlignment = VerticalAlignment.Center };
        view.Click += (_, _) => PlanRequested?.Invoke(this, shown.Path);
        ToolTip.SetTip(view, $"Open {shown.Shown} as a page");
        Button edit = new() { Classes = { StyleClasses.TextLink }, Content = "Edit Plan", VerticalAlignment = VerticalAlignment.Center };
        edit.Click += (_, _) => PlanEditRequested?.Invoke(this, shown.Path);
        ToolTip.SetTip(edit, "Edit the plan's file, its page beside it; the agent builds your version");
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Right, Children = { view, edit } };
        if (BuildAnswer(request.Options) is { } build)
        {
            Button go = new() { Classes = { StyleClasses.Primary }, Content = new TextBlock { Text = "Build" } };
            ToolTip.SetTip(go, "Build it as Cursor does: the agent's edits apply as it goes, for you to keep or undo at the end; it still asks before each command");
            go.Click += (_, _) =>
            {
                Built = true;
                Decide(build);
            };
            MenuFlyout others = new();
            foreach (PermissionOption option in request.Options)
            {
                MenuItem item = new() { Header = option.Name };
                item.Click += (_, _) => Decide(option);
                others.Items.Add(item);
            }
            Button more = new() { Classes = { StyleClasses.Primary }, Content = new RowIcon { DataKey = IconKeys.ChevronDown, Width = 12, Height = 12 } };
            more.Click += (_, _) => others.ShowAt(more);
            ToolTip.SetTip(more, "The agent's other answers");
            actions.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Children = { go, more } });
        }
        choices.Children.Add(actions);
        choices.Children.Add(new TextBlock
        {
            Classes = { StyleClasses.Muted },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Text = "Write in the box below to revise the plan.",
        });
        body.Children.Add(choices);
        return body;
    }

    /// <summary>The answer Build gives, Cursor's way of building: the agent's own "auto-accept edits" where it offers
    /// it, else its answer that asks before each edit (the window then turns the session to Accept edits), else its
    /// first way to build.</summary>
    public static PermissionOption? BuildAnswer(IReadOnlyList<PermissionOption> options) =>
        options.FirstOrDefault(option => option.Id == ChatAgent.PlanAcceptEdits)
        ?? options.FirstOrDefault(option => option.Id == ChatAgent.PlanManual)
        ?? options.FirstOrDefault(option => option.Kind.StartsWith("allow", StringComparison.Ordinal));

    /// <summary>A command's question as Cursor's and Stoat's: the command on one line in the editor's font beside what
    /// it waits for, what it is for under it, then Run and Reject; the agent's other answers ("don't ask again") under
    /// the arrow beside them.</summary>
    private StackPanel CommandCard(ToolCall call)
    {
        RowIcon icon = new() { DataKey = IconKeys.Terminal, ForegroundKey = TokenKeys.TextMuted, Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        // A script of many lines shows its first three, the whole of it in the tip.
        TextBlock command = new TextBlock { Text = call.Title.Trim(), TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }.InCodeFont();
        ToolTip.SetTip(command, call.Title);
        chip.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(chip, Dock.Right);
        DockPanel.SetDock(icon, Dock.Left);
        StackPanel body = new() { Spacing = 6, Children = { new DockPanel { Children = { chip, icon, command } } } };
        if (call.Texts.FirstOrDefault(text => text.Trim().Length > 0) is { } purpose)
        {
            body.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, Text = purpose.Trim(), TextWrapping = TextWrapping.Wrap });
        }
        PermissionOption? run = request.Options.FirstOrDefault(option => option.Kind == "allow_once") ?? request.Options.FirstOrDefault(option => option.Kind.StartsWith("allow", StringComparison.Ordinal));
        PermissionOption? reject = request.Options.FirstOrDefault(option => option.Kind == "reject_once") ?? request.Options.FirstOrDefault(option => option.Kind.StartsWith("reject", StringComparison.Ordinal));
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (run is not null)
        {
            Button go = new() { Classes = { StyleClasses.Primary }, Content = new TextBlock { Text = "Run" } };
            ToolTip.SetTip(go, run.Name);
            go.Click += (_, _) => Decide(run);
            buttons.Children.Add(go);
        }
        if (reject is not null)
        {
            Button no = new() { Content = new TextBlock { Text = "Reject" } };
            ToolTip.SetTip(no, reject.Name);
            no.Click += (_, _) => Decide(reject);
            buttons.Children.Add(no);
        }
        PermissionOption[] others = [.. request.Options.Where(option => option != run && option != reject)];
        if (others.Length > 0)
        {
            MenuFlyout menu = new();
            foreach (PermissionOption option in others)
            {
                MenuItem item = new() { Header = option.Name };
                if (option.Kind.EndsWith("_always", StringComparison.Ordinal))
                {
                    ToolTip.SetTip(item, "Kept by the agent beyond this chat");
                }
                item.Click += (_, _) => Decide(option);
                menu.Items.Add(item);
            }
            Button more = ToolButtons.Of(IconKeys.ChevronDown, "The agent's other answers", () => { });
            more.Click += (_, _) => menu.ShowAt(more);
            buttons.Children.Add(more);
        }
        choices.Children.Add(buttons);
        body.Children.Add(choices);
        return body;
    }

    /// <summary>Whether the plan was answered by Build, Cursor's way, rather than one of the agent's own answers.</summary>
    public bool Built { get; private set; }

    /// <summary>The person wrote what to change in the plan while it waited: the agent keeps planning with it.</summary>
    public bool Revise(string words)
    {
        if (plan is null || Answer.IsCompleted || request.Options.FirstOrDefault(option => option.Kind.StartsWith("reject", StringComparison.Ordinal)) is not { } keepPlanning)
        {
            return false;
        }
        Revision = words;
        Decide(keepPlanning);
        return true;
    }

    /// <summary>Builds the plan as its Build does, from its page's banner: the agent's first way to build it.</summary>
    public bool Build()
    {
        if (plan is null || Answer.IsCompleted || BuildAnswer(request.Options) is not { } build)
        {
            return false;
        }
        Built = true;
        Decide(build);
        return true;
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
            Text = call.Title.Length > 0 ? call.Title : "The agent asks",
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

    private static TextBlock Counted(string text, string colour)
    {
        TextBlock counted = new() { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        counted.Bind(TextBlock.ForegroundProperty, counted.GetResourceObservable(colour));
        return counted;
    }
}

/// <summary>A plan the agent asks to build, as its file: where it is, how the card says where, its title and what the card
/// says of it.</summary>
public sealed record ChatPlan(string Path, string Shown, string Title, string Summary = "");
