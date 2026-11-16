using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Passenger.Acp;
using Passenger.App.Editors;

namespace Passenger.App.Chat;

/// <summary>The Chat window: a conversation with a coding agent the person runs on their own account, over the Agent
/// Client Protocol; Passenger holds no key and no login for it. Until an agent is connected it says what it is doing and
/// offers Connect. Once one is, each message shows under who said it, the agent's reply as its parts come, in order:
/// its words (Markdown once a part is whole), a line per tool it runs, and a card per question it asks before it acts.
/// A ring and what the agent is doing show until its turn ends; Enter sends, Shift+Enter starts a new line, Stop ends
/// the turn.</summary>
public sealed class ChatView : DockPanel
{
    /// <summary>What the empty conversation offers to ask, as Stoat's does.</summary>
    private static readonly string[] Suggestions =
    [
        "Explain how this project is structured",
        "Find likely bugs and explain them",
        "Add tests for the main code",
    ];

    private readonly TextBlock said = new()
    {
        Classes = { StyleClasses.EmptyText },
        Text = "No agent connected",
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private readonly Button connect = new()
    {
        Content = "Connect to Claude Code",
        Classes = { StyleClasses.Primary },
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private readonly StackPanel idle;
    private readonly TextBlock agent = new() { Classes = { StyleClasses.Muted }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock account = new() { Classes = { StyleClasses.Muted }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly DockPanel header = new() { Margin = new Thickness(12, 8), IsVisible = false };
    private readonly WrapPanel pickers = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<string, ComboBox> settings = new(StringComparer.Ordinal);
    private readonly TextBlock context = new() { Classes = { StyleClasses.Muted }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly CommandMenu menu;
    private IReadOnlyList<ConfigOption> config = [];
    private bool showingConfig;
    private string? signedIn;
    private string signedInTip = string.Empty;
    private IReadOnlyList<UsageWindow> windows = [];
    private readonly StackPanel messages = new() { Spacing = 14 };
    private readonly StackPanel empty;
    private readonly TextBlock doing = new() { Classes = { StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel working;
    private readonly ScrollViewer conversation;
    private readonly TextBox input = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 160,
        PlaceholderText = "Ask the agent",
    };

    private readonly Button send = new() { Content = "Send", Classes = { StyleClasses.Primary }, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button stop = new() { Content = "Stop", IsVisible = false, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StackPanel composer;
    private readonly List<Part> parts = [];
    private readonly Dictionary<string, ToolLine> tools = [];
    private readonly List<ApprovalCard> asking = [];
    private StackPanel? reply;
    private Part? words;
    private string agentName = "Agent";

    public ChatView()
    {
        connect.Click += (_, _) => ConnectRequested?.Invoke(this, EventArgs.Empty);
        send.Click += (_, _) => Send();
        stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        menu = new CommandMenu(input);
        // Tunnelled, so the list, Enter (send) and Shift+Tab (next mode) come before the box takes the key.
        input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (menu.Press(e))
            {
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Send();
            }
            else if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift && NextMode() is { } next)
            {
                e.Handled = true;
                ConfigChosen?.Invoke(this, ("mode", next));
            }
        }, RoutingStrategies.Tunnel);

        idle = new StackPanel { Spacing = 12, Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center, Children = { said, connect } };
        empty = EmptyState();
        working = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            IsVisible = false,
            Children = { new WorkingRing { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center }, doing },
        };
        conversation = new ScrollViewer
        {
            IsVisible = false,
            Content = new StackPanel { Spacing = 14, Margin = new Thickness(12), Children = { empty, messages, working } },
        };
        DockPanel buttons = new() { Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(send);
        buttons.Children.Add(stop);
        DockPanel writing = new();
        SetDock(buttons, Dock.Right);
        writing.Children.Add(buttons);
        writing.Children.Add(input);
        DockPanel below = new();
        SetDock(context, Dock.Right);
        below.Children.Add(context);
        below.Children.Add(pickers);
        composer = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8, 12, 12), IsVisible = false, Children = { writing, below, menu.Popup } };
        Button plans = ToolButtons.Of(IconKeys.DocumentText, "Plans: the agent's plans in this folder", () => PlansRequested?.Invoke(this, PlansButton!));
        PlansButton = plans;
        SetDock(plans, Dock.Right);
        SetDock(account, Dock.Right);
        header.Children.Add(plans);
        header.Children.Add(account);
        header.Children.Add(agent);
        SetDock(header, Dock.Top);
        SetDock(composer, Dock.Bottom);
        SetDock(Review, Dock.Bottom);
        SetDock(Todos, Dock.Bottom);
        Children.Add(header);
        Children.Add(composer);
        Children.Add(Review);
        Children.Add(Todos);
        Children.Add(new Panel { Children = { idle, conversation } });
    }

    /// <summary>Connect was pressed.</summary>
    public event EventHandler? ConnectRequested;

    /// <summary>A setting was chosen: its id ("mode", "model", "effort") and the value chosen.</summary>
    public event EventHandler<(string Id, string Value)>? ConfigChosen;

    /// <summary>The slash command list over the box.</summary>
    public CommandMenu Commands => menu;

    /// <summary>What the header says of the account and the plan's use: "Claude Pro · 5h 30% · week 4%".</summary>
    public string Account => account.Text ?? string.Empty;

    /// <summary>What it says of the context used: "4% context".</summary>
    public string ContextUsed => context.Text ?? string.Empty;

    /// <summary>The pickers of the agent's settings, by setting id.</summary>
    public IReadOnlyDictionary<string, ComboBox> Settings => settings;

    /// <summary>An edit's diff was asked for from its line, by its tool call's id.</summary>
    public event EventHandler<string>? EditDiffRequested;

    /// <summary>The files the agent changed, waiting for review, above the box.</summary>
    public ReviewBar Review { get; } = new();

    /// <summary>The agent's to-do list, above the review.</summary>
    public TodoBar Todos { get; } = new();

    /// <summary>The header's button that lists the plans.</summary>
    public Button? PlansButton { get; private set; }

    /// <summary>A plan's file was asked to be opened as a page, by its path.</summary>
    public event EventHandler<string>? PlanRequested;

    /// <summary>The list of the folder's plans was asked for, under the button given.</summary>
    public event EventHandler<Button>? PlansRequested;

    /// <summary>The person sent a message, its text given.</summary>
    public event EventHandler<string>? SendRequested;

    /// <summary>Stop was pressed while the agent works.</summary>
    public event EventHandler? StopRequested;

    /// <summary>Opens a link in a reply.</summary>
    public Action<Uri> Browse { get; set; } = _ => { };

    /// <summary>What it says of the agent now: "No agent connected", "Starting Claude Code…", "Connected to Claude Agent 0.107.3".</summary>
    public string Said => said.Text ?? string.Empty;

    /// <summary>Whether Connect is offered: while no agent is connected or starting.</summary>
    public bool OffersConnect => idle.IsVisible && connect.IsVisible;

    /// <summary>Whether the agent is working on a message: Stop shows in Send's place.</summary>
    public bool Busy => stop.IsVisible;

    /// <summary>What the ring's line says while the agent works: "Thinking…", a tool's title, "Waiting for your answer".</summary>
    public string? Doing => working.IsVisible ? doing.Text : null;

    /// <summary>The words of the conversation so far: who said each ("You", the agent, or "" for a note) and what.</summary>
    public IReadOnlyList<(string Who, string Text)> Conversation => [.. parts.Select(part => (part.Who, part.Text.ToString()))];

    /// <summary>The agent's tool lines in order.</summary>
    public IEnumerable<ToolLine> Tools => tools.Values;

    /// <summary>The questions asked so far, each as its card.</summary>
    public IEnumerable<ApprovalCard> Questions => messages.GetLogicalDescendants().OfType<ApprovalCard>();

    /// <summary>The box the person writes in.</summary>
    public TextBox Input => input;

    /// <summary>Says <paramref name="text"/> with no agent connected, as an error where it is one, with Connect offered
    /// while no agent is starting.</summary>
    public void Say(string text, bool error = false, bool offerConnect = false)
    {
        said.Text = text;
        said.Classes.Set(StyleClasses.Error, error);
        connect.IsVisible = offerConnect;
        idle.IsVisible = true;
        header.IsVisible = conversation.IsVisible = composer.IsVisible = false;
    }

    /// <summary>Shows the conversation with the agent <paramref name="name"/> (its version after it, as <paramref name="described"/>).</summary>
    public void Connected(string name, string described)
    {
        agentName = name;
        said.Text = $"Connected to {described}";
        agent.Text = described;
        ToolTip.SetTip(agent, said.Text);
        idle.IsVisible = false;
        header.IsVisible = conversation.IsVisible = composer.IsVisible = true;
        empty.IsVisible = messages.Children.Count == 0;
        input.Focus();
    }

    /// <summary>The person's message, then the agent's reply begun, the ring turning until its turn ends.</summary>
    public void Asked(string text)
    {
        empty.IsVisible = false;
        StackPanel mine = Message("You");
        SelectableTextBlock body = new() { Text = text, TextWrapping = TextWrapping.Wrap };
        mine.Children.Add(new Border { Classes = { StyleClasses.Framed }, Padding = new Thickness(10, 8), Child = body });
        parts.Add(new Part("You", new StringBuilder(text), body, null));
        reply = Message(agentName);
        Working(true, "Thinking…");
        ToEnd();
    }

    /// <summary>A piece of the agent's words, added to the end of its current part.</summary>
    public void Streamed(string text)
    {
        if (reply is null)
        {
            return;
        }
        if (words is null)
        {
            SelectableTextBlock body = new() { TextWrapping = TextWrapping.Wrap };
            StackPanel holder = new() { Children = { body } };
            reply.Children.Add(holder);
            words = new Part(agentName, new StringBuilder(), body, holder);
            parts.Add(words);
        }
        words.Text.Append(text);
        words.Body.Text = words.Text.ToString();
        if (asking.Count == 0)
        {
            doing.Text = "Thinking…";
        }
        ToEnd();
    }

    /// <summary>A tool call's line, added or updated in place; the ring's line says what runs.</summary>
    public void Tool(ToolCall call)
    {
        if (reply is null)
        {
            return;
        }
        if (tools.TryGetValue(call.Id, out ToolLine? line))
        {
            line.Show(call);
        }
        else
        {
            Close();
            line = new ToolLine(call);
            line.DiffRequested += (_, _) => EditDiffRequested?.Invoke(this, call.Id);
            tools[call.Id] = line;
            reply.Children.Add(line);
        }
        if (asking.Count == 0)
        {
            doing.Text = call.Status is "pending" or "in_progress" ? line.Title : "Thinking…";
        }
        ToEnd();
    }

    /// <summary>The agent's settings it lets the person choose, each a picker under the box (mode, model, effort, in
    /// that order where the agent has them), set to its current value; choosing one tells <see cref="ConfigChosen"/>.</summary>
    public void ShowConfig(IReadOnlyList<ConfigOption> options)
    {
        config = options;
        showingConfig = true;
        try
        {
            foreach (string id in new[] { "mode", "model", "effort" })
            {
                if (options.FirstOrDefault(option => option.Id == id) is not { Choices.Count: > 0 } option)
                {
                    continue;
                }
                if (!settings.TryGetValue(id, out ComboBox? picker))
                {
                    picker = new ComboBox { FontSize = 12, Margin = new Thickness(0, 0, 6, 0), MaxWidth = 170 };
                    picker.SelectionChanged += (_, _) =>
                    {
                        if (!showingConfig && picker.SelectedItem is ComboBoxItem { Tag: string value })
                        {
                            ConfigChosen?.Invoke(this, (id, value));
                        }
                    };
                    settings[id] = picker;
                    pickers.Children.Add(picker);
                }
                picker.ItemsSource = option.Choices.Select(choice => new ComboBoxItem
                {
                    Content = choice.Name,
                    Tag = choice.Value,
                    [ToolTip.TipProperty] = choice.Description,
                }).ToList();
                picker.SelectedIndex = option.Choices.ToList().FindIndex(choice => choice.Value == option.Current);
                ToolTip.SetTip(picker, id == "mode" ? $"{option.Name} (Shift+Tab moves to the next)" : option.Name);
            }
        }
        finally
        {
            showingConfig = false;
        }
    }

    /// <summary>The slash commands and skills the agent offers, for the list "/" opens.</summary>
    public void ShowCommands(IReadOnlyList<AgentCommand> commands) => menu.Commands = commands;

    /// <summary>Who the agent is signed in as, in the header: its plan, and its email in the tip with how to switch.</summary>
    public void ShowAccount(AccountStatus status)
    {
        AgentAccount? active = status.Accounts.FirstOrDefault(each => each.Active) ?? status.Accounts.FirstOrDefault();
        signedIn = active is null ? status.State : active.Label;
        signedInTip = active is null
            ? $"The agent says it is {status.State}"
            : $"Signed in as {active.Email ?? active.Label} ({active.Label}), in the agent's own login.\nTo use another account, run claude /login in a terminal, then connect again.";
        ShowPlan();
    }

    /// <summary>How much of the context the session has used, under the box, and of the plan's windows, in the header.</summary>
    public void ShowUsage(UsageUpdated usage)
    {
        if (usage.Size > 0)
        {
            context.Text = $"{Math.Round(100.0 * usage.Used / usage.Size)}% context";
            ToolTip.SetTip(context, $"{usage.Used:N0} of {usage.Size:N0} tokens of the conversation's context");
        }
        windows = usage.Windows;
        ShowPlan();
    }

    /// <summary>The header's account and plan use, its tip who is signed in and when each window starts afresh.</summary>
    private void ShowPlan()
    {
        List<string> said = signedIn is null ? [] : [signedIn];
        said.AddRange(windows.Select(window => $"{Window(window.Name)} {Math.Round(window.Used * 100)}%"));
        account.Text = string.Join(" · ", said);
        string resets = string.Join("\n", windows.Where(window => window.Resets is not null)
            .Select(window => $"{Window(window.Name)}: {Math.Round(window.Used * 100)}% used, starts afresh {window.Resets!.Value.ToLocalTime():ddd HH:mm}"));
        ToolTip.SetTip(account, $"{signedInTip}\n{resets}".Trim());

        static string Window(string name) => name switch
        {
            "five_hour" => "5h",
            "seven_day" => "week",
            _ => name.Replace('_', ' '),
        };
    }

    /// <summary>The agent's mode after the current one, round to the first, as Shift+Tab moves.</summary>
    private string? NextMode()
    {
        if (config.FirstOrDefault(option => option.Id == "mode") is not { Choices.Count: > 1 } mode)
        {
            return null;
        }
        int at = mode.Choices.ToList().FindIndex(choice => choice.Value == mode.Current);
        return mode.Choices[(at + 1) % mode.Choices.Count].Value;
    }

    /// <summary>What an edit's tool call added and removed, on its line.</summary>
    public void Counted(string toolCallId, int added, int removed)
    {
        if (tools.TryGetValue(toolCallId, out ToolLine? line))
        {
            line.Counted(added, removed);
        }
    }

    /// <summary>The agent's question as a card in its reply; the click, or Stop, answers it.</summary>
    public Task<string?> Ask(PermissionRequest request, IReadOnlyList<ChatChange> changes, ChatPlan? plan = null)
    {
        if (reply is null)
        {
            return Task.FromResult<string?>(null);
        }
        Close();
        ApprovalCard card = new(request, changes, Browse, plan);
        card.PlanRequested += (_, path) => PlanRequested?.Invoke(this, path);
        reply.Children.Add(card);
        asking.Add(card);
        doing.Text = "Waiting for your answer";
        ToEnd();
        return AnswerOf(card);
    }

    /// <summary>The agent's turn is over: open questions are cancelled, and <paramref name="note"/>, where there is one,
    /// says how it ended ("Stopped").</summary>
    public void Answered(string? note = null, bool error = false)
    {
        foreach (ApprovalCard card in asking.ToArray())
        {
            card.Cancel();
        }
        Close();
        if (note is not null && reply is not null)
        {
            TextBlock body = new() { Classes = { error ? StyleClasses.Error : StyleClasses.Muted }, Text = note, TextWrapping = TextWrapping.Wrap };
            reply.Children.Add(body);
            parts.Add(new Part(string.Empty, new StringBuilder(note), body, null));
        }
        reply = null;
        Working(false, null);
        ToEnd();
    }

    /// <summary>A note in the agent's reply as it goes ("Sent your edited plan"), muted, or as an error.</summary>
    public void Noted(string text, bool error = false)
    {
        if (reply is null)
        {
            return;
        }
        Close();
        TextBlock body = new() { Classes = { error ? StyleClasses.Error : StyleClasses.Muted }, Text = text, TextWrapping = TextWrapping.Wrap };
        reply.Children.Add(body);
        parts.Add(new Part(string.Empty, new StringBuilder(text), body, null));
        ToEnd();
    }

    /// <summary>Stop was pressed: each open question is answered cancelled at once.</summary>
    public void CancelQuestions()
    {
        foreach (ApprovalCard card in asking.ToArray())
        {
            card.Cancel();
        }
    }

    private async Task<string?> AnswerOf(ApprovalCard card)
    {
        try
        {
            return await card.Answer;
        }
        finally
        {
            asking.Remove(card);
            if (asking.Count == 0 && working.IsVisible)
            {
                doing.Text = "Thinking…";
            }
        }
    }

    /// <summary>Ends the part of the agent's words being written: it is shown as Markdown from here on.</summary>
    private void Close()
    {
        if (words is not { Holder: { } holder } part)
        {
            return;
        }
        words = null;
        holder.Children.Clear();
        Markdown.Render(Markdown.Parse(part.Text.ToString()), holder, Browse);
    }

    private void Working(bool busy, string? what)
    {
        send.IsVisible = !busy;
        stop.IsVisible = busy;
        working.IsVisible = busy;
        doing.Text = what;
    }

    private void Send()
    {
        string text = input.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || Busy || !composer.IsVisible)
        {
            return;
        }
        input.Text = string.Empty;
        SendRequested?.Invoke(this, text);
    }

    /// <summary>A message's column under who said it, added to the conversation.</summary>
    private StackPanel Message(string who)
    {
        StackPanel message = new() { Spacing = 6, Children = { Who(who) } };
        messages.Children.Add(message);
        return message;
    }

    private static TextBlock Who(string who) => new() { Classes = { StyleClasses.Muted }, Text = who, FontWeight = FontWeight.SemiBold, FontSize = 11 };

    private StackPanel EmptyState()
    {
        StackPanel state = new() { Spacing = 8, Margin = new Thickness(0, 24, 0, 0) };
        state.Children.Add(new TextBlock { Text = "Ask about this folder, or have the agent change it.", TextWrapping = TextWrapping.Wrap });
        state.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, Text = "Its edits apply as it goes, for you to keep or undo; it asks before each command.", TextWrapping = TextWrapping.Wrap });
        foreach (string suggestion in Suggestions)
        {
            Button ask = new() { Content = suggestion, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            ask.Click += (_, _) =>
            {
                input.Text = suggestion;
                input.CaretIndex = suggestion.Length;
                input.Focus();
            };
            state.Children.Add(ask);
        }
        return state;
    }

    private void ToEnd() => conversation.ScrollToEnd();

    /// <summary>Words of the conversation: who said them, their text, the control showing them while they stream, and
    /// what holds that control, which a part of the agent's words is rendered into once whole.</summary>
    private sealed record Part(string Who, StringBuilder Text, TextBlock Body, StackPanel? Holder);
}
