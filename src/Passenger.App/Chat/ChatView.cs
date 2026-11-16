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
    private readonly TextBlock agent = new() { Classes = { StyleClasses.Muted }, Margin = new Thickness(12, 8), TextTrimming = TextTrimming.CharacterEllipsis };
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
    private readonly DockPanel composer;
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
        // Tunnelled, so Enter sends before the box takes it as a new line; Shift+Enter is left to the box.
        input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Send();
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
        composer = new DockPanel { Margin = new Thickness(12, 8, 12, 12), IsVisible = false };
        SetDock(buttons, Dock.Right);
        composer.Children.Add(buttons);
        composer.Children.Add(input);
        agent.IsVisible = false;
        SetDock(agent, Dock.Top);
        SetDock(composer, Dock.Bottom);
        Children.Add(agent);
        Children.Add(composer);
        Children.Add(new Panel { Children = { idle, conversation } });
    }

    /// <summary>Connect was pressed.</summary>
    public event EventHandler? ConnectRequested;

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
        agent.IsVisible = conversation.IsVisible = composer.IsVisible = false;
    }

    /// <summary>Shows the conversation with the agent <paramref name="name"/> (its version after it, as <paramref name="described"/>).</summary>
    public void Connected(string name, string described)
    {
        agentName = name;
        said.Text = $"Connected to {described}";
        agent.Text = said.Text;
        idle.IsVisible = false;
        agent.IsVisible = conversation.IsVisible = composer.IsVisible = true;
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
            tools[call.Id] = line;
            reply.Children.Add(line);
        }
        if (asking.Count == 0)
        {
            doing.Text = call.Status is "pending" or "in_progress" ? line.Title : "Thinking…";
        }
        ToEnd();
    }

    /// <summary>The agent's question as a card in its reply; the click, or Stop, answers it.</summary>
    public Task<string?> Ask(PermissionRequest request, IReadOnlyList<ChatChange> changes)
    {
        if (reply is null)
        {
            return Task.FromResult<string?>(null);
        }
        Close();
        ApprovalCard card = new(request, changes, Browse);
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
        state.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, Text = "It asks before each edit and command.", TextWrapping = TextWrapping.Wrap });
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
