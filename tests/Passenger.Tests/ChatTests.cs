using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.Acp;
using Passenger.App;
using Passenger.App.Chat;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The Chat window: shown and hidden from its stripe button and shown by its action, as each tool window is, and
/// Connect starting its agent in the open folder, a message sent and its reply streamed, its tool lines, its questions
/// as cards the person answers, Markdown, and Stop, a stand-in in memory answering as the Claude adapter does.</summary>
public sealed class ChatTests
{
    [AvaloniaFact]
    public void Its_stripe_button_shows_the_chat_window_and_hides_it_again()
    {
        MainWindow window = Open();
        Ui.Click(Button(window));
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Chat));
        Assert.IsType<ChatView>(window.Chat);
        Assert.True(Button(window).IsChecked);

        Ui.Click(Button(window));
        Assert.False(window.DockHost.IsShown(ToolWindowIds.Chat));
        window.Close();
    }

    [AvaloniaFact]
    public void Its_action_shows_the_chat_window_saying_no_agent_is_connected()
    {
        MainWindow window = Open();
        window.Actions.Run(AppActions.ShowChat);
        Ui.Settle();
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Chat));
        Assert.Equal("No agent connected", window.Chat.Said);
        Assert.True(window.Chat.OffersConnect);
        window.Close();
    }

    [AvaloniaFact]
    public void Connect_starts_the_agent_in_the_open_folder_and_says_which_agent_is_connected()
    {
        string folder = TestFolders.Create("chat");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        StandInAgent? agent = null;
        string? startedIn = null;
        window.StartChatAgent = at =>
        {
            startedIn = at;
            agent = new StandInAgent();
            return Task.FromResult(agent.Agent);
        };
        window.ShowToolWindow(ToolWindowIds.Chat);
        Ui.Settle();

        Ui.Click(ConnectButton(window));
        Ui.WaitFor(() => window.Chat.Said == "Connected to Claude Agent 0.107.3", () => $"the agent connected, where the window says \"{window.Chat.Said}\"");

        Assert.Equal(folder, startedIn);
        Assert.Equal(folder, agent!.SessionFolder);
        Assert.Equal("acceptEdits", agent.Mode);
        Assert.False(window.Chat.OffersConnect);
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_sends_the_message_and_the_reply_streams_in_piece_by_piece()
    {
        MainWindow window = Connected(out StandInAgent agent);

        Ui.Click(window.Chat.Input);
        foreach (char c in "Add a README")
        {
            Ui.Type(window, c == ' ' ? "SPACE" : c.ToString(), c.ToString());
        }
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.Chat.Busy && window.Chat.Conversation.Count == 2, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("Add a README", agent.Prompts.Single());
        Assert.Equal([("You", "Add a README"), ("Claude Agent", "I'll add a README.")], window.Chat.Conversation);
        Assert.Equal(string.Empty, window.Chat.Input.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Stop_ends_the_agents_turn_and_says_it_stopped()
    {
        MainWindow window = Connected(out StandInAgent agent);
        window.Chat.Input.Text = "wait for stop";
        Ui.Click(Named(window, "Send"));
        Ui.WaitFor(() => window.Chat.Busy && agent.Prompts.Count == 1, "the agent working");
        Assert.Equal("Thinking…", window.Chat.Doing);

        Ui.Click(Named(window, "Stop"));
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn stopped, where the conversation is {Shown(window)}");

        Assert.True(agent.Cancelled);
        Assert.Equal(("", "Stopped"), window.Chat.Conversation[^1]);
        Assert.Null(window.Chat.Doing);
        window.Close();
    }

    [AvaloniaFact]
    public void A_tools_line_updates_in_place_and_the_words_after_it_come_below_it()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "tool");
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        ToolLine line = Assert.Single(window.Chat.Tools);
        Assert.Equal("Read hello.py", line.Title);
        Assert.Equal("completed", line.Status);
        Assert.Equal([("You", "tool"), ("Claude Agent", "Reading it."), ("Claude Agent", "It prints hello.")], window.Chat.Conversation);
        window.Close();
    }

    [AvaloniaFact]
    public void An_edit_waits_on_a_card_with_its_diff_and_Accept_answers_allow_once()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "write");
        Ui.WaitFor(() => window.Chat.Questions.Any(), () => $"the question asked, where the conversation is {Shown(window)}");

        ApprovalCard card = window.Chat.Questions.Single();
        Assert.Equal("Needs approval", card.State);
        Assert.Equal("Waiting for your answer", window.Chat.Doing);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "+ # Notes");
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "+1");
        Assert.Null(agent.Answered);

        Ui.Click(InCard(card, "Accept"));
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("allow-once", agent.Answered);
        Assert.Equal("Accepted", card.State);
        Assert.Equal(("Claude Agent", "Wrote notes.md."), window.Chat.Conversation[^1]);
        window.Close();
    }

    [AvaloniaFact]
    public void Rejecting_an_edit_tells_the_agent_no()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "write");
        Ui.WaitFor(() => window.Chat.Questions.Any(), "the question asked");

        Ui.Click(InCard(window.Chat.Questions.Single(), "Reject"));
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("reject", agent.Answered);
        Assert.Equal("Rejected", window.Chat.Questions.Single().State);
        window.Close();
    }

    [AvaloniaFact]
    public void Stop_while_asked_answers_the_question_cancelled()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "write");
        Ui.WaitFor(() => window.Chat.Questions.Any(), "the question asked");

        Ui.Click(Named(window, "Stop"));
        Ui.WaitFor(() => agent.Answered is not null, "the agent answered");

        Assert.Equal("cancelled", agent.Answered);
        Assert.Equal("Cancelled", window.Chat.Questions.Single().State);
        window.Close();
    }

    [AvaloniaFact]
    public void The_agents_edits_apply_and_wait_in_the_review_bar_with_their_counts()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "edit");
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("hello\nworld\n", File.ReadAllText(Path.Combine(folder, "hello.txt")));
        Assert.Equal(["hello.txt", "notes.md"], window.Chat.Review.Shown.Select(file => Path.GetFileName(file.Path)));
        Assert.True(window.Chat.Review.IsVisible);
        Assert.Equal(["+1 −0", "+1 −0"], window.Chat.Tools.Select(line => line.Counts));
        Assert.True(window.Chat.Review.Shown[1].IsNew);
        window.Close();
    }

    [AvaloniaFact]
    public void Undo_all_puts_each_file_back_as_it_was_before_the_agent()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "edit");
        Ui.WaitFor(() => !window.Chat.Busy && window.Chat.Review.Shown.Count == 2, "the edits in review");

        Ui.Click(InReview(window, "Undo all"));
        Ui.WaitFor(() => window.Chat.Review.Shown.Count == 0, () => $"the review emptied, where it lists {window.Chat.Review.Shown.Count}");

        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(folder, "hello.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "notes.md")));
        Assert.False(window.Chat.Review.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Undo_leaves_a_file_changed_since_the_agent_and_says_so()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "edit");
        Ui.WaitFor(() => !window.Chat.Busy && window.Chat.Review.Shown.Count == 2, "the edits in review");
        File.WriteAllText(Path.Combine(folder, "hello.txt"), "mine\n");

        Ui.Click(InReview(window, "Undo all"));
        Ui.WaitFor(() => window.Chat.Review.Shown.Count == 1, () => $"one file undone, where the review lists {window.Chat.Review.Shown.Count}");

        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(folder, "hello.txt")));
        Assert.Equal("hello.txt", Path.GetFileName(window.Chat.Review.Shown.Single().Path));
        window.Close();
    }

    [AvaloniaFact]
    public void Keep_all_empties_the_review_and_leaves_the_files_as_the_agent_left_them()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "edit");
        Ui.WaitFor(() => !window.Chat.Busy && window.Chat.Review.Shown.Count == 2, "the edits in review");

        Ui.Click(InReview(window, "Keep all"));

        Assert.Empty(window.Chat.Review.Shown);
        Assert.Equal("hello\nworld\n", File.ReadAllText(Path.Combine(folder, "hello.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "notes.md")));
        window.Close();
    }

    [AvaloniaFact]
    public void The_agents_settings_are_pickers_under_the_box_and_choosing_one_sets_it()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Ui.WaitFor(() => window.Chat.Settings.Count == 3, "the three pickers");

        Assert.Equal(["mode", "model", "effort"], window.Chat.Settings.Keys);
        Assert.Equal("Accept edits", Chosen(window.Chat.Settings["mode"]));
        Assert.Equal("Opus 5.5", Chosen(window.Chat.Settings["model"]));

        window.Chat.Settings["model"].SelectedIndex = 1;
        Ui.WaitFor(() => agent.Model == "sonnet", "the model set");
        Ui.Settle();
        Assert.Equal("Sonnet 5.5", Chosen(window.Chat.Settings["model"]));
        window.Close();
    }

    [AvaloniaFact]
    public void Shift_Tab_in_the_box_moves_to_the_agents_next_mode()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Ui.WaitFor(() => window.Chat.Settings.ContainsKey("mode"), "the mode picker");

        Ui.Click(window.Chat.Input);
        Ui.Press(window, "shift TAB");
        Ui.WaitFor(() => agent.Mode == "plan", () => $"the next mode set, where it is {agent.Mode}");
        Ui.Settle();
        Assert.Equal("Plan", Chosen(window.Chat.Settings["mode"]));
        window.Close();
    }

    [AvaloniaFact]
    public void A_slash_lists_the_agents_commands_and_Enter_writes_the_chosen_one()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Ui.WaitFor(() => window.Chat.Commands.Commands.Count == 3, "the agent's commands");

        Ui.Click(window.Chat.Input);
        window.Chat.Input.Text = "/co";
        window.Chat.Input.CaretIndex = 3;
        Ui.Settle();
        Assert.True(window.Chat.Commands.IsOpen);
        Assert.Equal(["code-review", "compact"], window.Chat.Commands.Shown);

        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Assert.Equal("/compact ", window.Chat.Input.Text);
        Assert.False(window.Chat.Commands.IsOpen);
        Assert.Empty(agent.Prompts);
        window.Close();
    }

    [AvaloniaFact]
    public void The_header_says_the_account_and_the_plans_use_and_the_box_the_context_used()
    {
        MainWindow window = Connected(out _);
        Ui.WaitFor(() => window.Chat.Account.Contains("week", StringComparison.Ordinal), () => $"the plan's use, where the header says {window.Chat.Account}");

        Assert.Equal("Claude Pro · 5h 30% · week 4%", window.Chat.Account);
        Assert.Equal("4% context", window.Chat.ContextUsed);
        window.Close();
    }

    [AvaloniaFact]
    public void A_plan_is_saved_to_the_folders_plans_and_the_agents_first_answer_builds_it()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "plan");
        Ui.WaitFor(() => window.Chat.Questions.Any(), () => $"the plan's card, where the conversation is {Shown(window)}");

        string plan = Path.Combine(folder, ".passenger", "plans", $"{DateTime.Now:yyyy-MM-dd}_add-a-math-class.plan.md");
        Assert.True(File.Exists(plan), $"No plan at {plan}");
        Assert.Equal(StandInAgent.Plan, File.ReadAllText(plan));
        ApprovalCard card = window.Chat.Questions.Single();
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Plan: Add a math class");
        // Shown at once as a page, with no click; Claude Code's own draft is a step, neither reviewed nor a second plan.
        Ui.WaitFor(() => window.EditorArea.Active?.Path?.EndsWith("add-a-math-class.plan.md", StringComparison.Ordinal) == true, () => $"the plan shown, where {window.EditorArea.Active?.Path} is");
        Assert.True(window.EditorArea.ShowsMarkdownPreview);
        Assert.Empty(window.Chat.Review.Shown);
        Assert.Contains(window.Chat.Tools, line => line.Title == "Draft the plan");

        Ui.Click(InCard(card, "Yes, and use auto mode"));
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("exit-plan-auto", agent.PlanAnswer);
        Assert.Equal("Building", card.State);
        Assert.Empty(agent.Steered);
        window.Close();
    }

    [AvaloniaFact]
    public void A_plan_edited_before_it_is_built_is_sent_to_the_agent_as_it_is_now()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "plan");
        Ui.WaitFor(() => window.Chat.Questions.Any(), "the plan's card");
        string plan = Path.Combine(folder, ".passenger", "plans", $"{DateTime.Now:yyyy-MM-dd}_add-a-math-class.plan.md");
        File.AppendAllText(plan, "3. Write the docs\n");

        Ui.Click(InCard(window.Chat.Questions.Single(), "Yes, and use auto mode"));
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        string steered = Assert.Single(agent.Steered);
        Assert.Contains("3. Write the docs", steered, StringComparison.Ordinal);
        Assert.Contains("add-a-math-class.plan.md", steered, StringComparison.Ordinal);
        Assert.Equal(["steer", "answer"], agent.Order);
        Assert.Contains(window.Chat.Conversation, part => part.Text.StartsWith("Sent your edited plan from", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void Open_plan_opens_the_plans_file_as_a_page_alone_its_source_behind_the_preview_toggle()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "plan");
        Ui.WaitFor(() => window.Chat.Questions.Any(), "the plan's card");

        Ui.Click(window.Chat.Questions.Single().GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Open plan")));
        Ui.Settle();

        EditorGroup group = window.EditorArea.ActiveGroup;
        Assert.EndsWith("add-a-math-class.plan.md", window.EditorArea.Active?.Path, StringComparison.Ordinal);
        Assert.True(group.Markdown?.IsEffectivelyVisible == true, "The plan reads as no page");
        Assert.False(group.Editor.IsEffectivelyVisible, "The plan's source shows beside its page, the same words twice");

        Ui.Click(group.PreviewToggle);
        Ui.Settle();
        Assert.True(group.Editor.IsEffectivelyVisible, "The preview's toggle shows no source of the plan to edit");
        Ui.Click(group.PreviewToggle);
        Ui.Settle();
        Assert.False(group.Editor.IsEffectivelyVisible, "The plan's source stays after its page came back");
        Ui.Click(InCard(window.Chat.Questions.Single(), "No, keep planning"));
        Ui.WaitFor(() => !window.Chat.Busy, "the turn ended");
        Assert.Equal("reject", agent.PlanAnswer);
        window.Close();
    }

    [AvaloniaFact]
    public void A_plans_answers_are_the_agents_numbered_and_Revise_keeps_planning_with_what_to_change_sent_first()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out _);
        Send(window, "plan");
        Ui.WaitFor(() => window.Chat.Questions.Any(), "the plan's card");
        ApprovalCard card = window.Chat.Questions.Single();
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "1");
        Assert.DoesNotContain(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Build");
        Button revise = card.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Revise"));
        Assert.False(revise.IsEnabled, "Revise sends nothing to change");

        card.GetVisualDescendants().OfType<TextBox>().Single().Text = "Use SQLite instead of Postgres";
        Ui.Settle();
        Ui.Click(revise);
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("reject", agent.PlanAnswer);
        Assert.Equal("Revising", card.State);
        Assert.Contains("Use SQLite instead of Postgres", Assert.Single(agent.Steered), StringComparison.Ordinal);
        Assert.Equal(["steer", "answer"], agent.Order);
        window.Close();
    }

    [AvaloniaFact]
    public void Edit_plan_shows_the_plans_source_beside_its_page_and_Open_plan_the_page_alone_again()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out _);
        Send(window, "plan");
        Ui.WaitFor(() => window.Chat.Questions.Any(), "the plan's card");
        ApprovalCard card = window.Chat.Questions.Single();

        Ui.Click(card.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Edit plan")));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        Assert.EndsWith("add-a-math-class.plan.md", window.EditorArea.Active?.Path, StringComparison.Ordinal);
        Assert.True(group.Editor.IsEffectivelyVisible, "Edit plan shows no source to edit");
        Assert.True(group.Markdown?.IsEffectivelyVisible == true, "Edit plan hides the plan's page");

        Ui.Click(card.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Open plan")));
        Ui.Settle();
        Assert.False(group.Editor.IsEffectivelyVisible, "Open plan still shows the source beside the page");
        Ui.Click(InCard(card, "No, keep planning"));
        Ui.WaitFor(() => !window.Chat.Busy, "the turn ended");
        window.Close();
    }

    [AvaloniaFact]
    public void A_new_file_asked_for_opens_as_its_copy_all_added_and_its_banners_Accept_answers_it()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "write");
        Ui.WaitFor(() => Proposed(window) is { AgentReview.KeepWord: "Accept" }, () => $"the new file's copy shown, where the conversation is {Shown(window)}");

        OpenDocument copy = Proposed(window)!;
        Assert.Equal("notes.md", Path.GetFileName(copy.Path));
        Assert.True(copy.IsReadOnly, "The copy of a file not yet made can be typed over");
        Assert.Equal("# Notes\n", copy.Text.Snapshot.ToString());
        Assert.Equal(ChangeMarkKind.Added, Assert.Single(copy.Decorations.ReviewMarks).Kind);
        ApprovalCard card = window.Chat.Questions.Single();
        Ui.Click(InCard(card, "Write notes.md"));
        Ui.Settle();
        Assert.Same(copy, window.EditorArea.Active);
        Assert.Empty(window.OwnedWindows.OfType<Passenger.App.Vcs.DiffWindow>());

        Ui.Click(window.EditorArea.ActiveGroup.ReviewLinks.Keep);
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");
        Assert.Equal("allow-once", agent.Answered);
        Ui.WaitFor(() => Proposed(window) is null, "the copy closed once answered");
        window.Close();
    }

    [AvaloniaFact]
    public void An_edits_line_opens_its_file_in_the_editor_with_its_changes_marked()
    {
        MainWindow window = Reviewing(out _, out _);
        Send(window, "edit");
        Ui.WaitFor(() => !window.Chat.Busy && window.Chat.Review.Shown.Count == 2, "both edits in review");

        ToolLine line = window.Chat.Tools.First(tool => tool.Title == "Write hello.txt");
        Ui.Click(line.GetVisualDescendants().OfType<Button>().First(b => b.IsVisible));
        Ui.WaitFor(() => window.EditorArea.Active?.Decorations.ReviewMarks.Count > 0, "hello.txt's changes marked");

        Assert.Equal("hello.txt", Path.GetFileName(window.EditorArea.Active?.Path));
        Assert.Empty(window.OwnedWindows.OfType<Passenger.App.Vcs.DiffWindow>());
        window.Close();
    }

    /// <summary>The copy open of a file the agent asks to make, if one is.</summary>
    private static OpenDocument? Proposed(MainWindow window) =>
        window.EditorArea.Documents.FirstOrDefault(document => ChatProposals.IsCopy(document.Path));

    [AvaloniaFact]
    public void The_agents_to_dos_are_pinned_above_the_box()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out _);
        Send(window, "plan");
        Ui.WaitFor(() => window.Chat.Todos.IsVisible, "the to-dos");

        Assert.Equal("To-dos 1/3", window.Chat.Todos.Count);
        Assert.Equal(["completed", "in_progress", "pending"], window.Chat.Todos.Entries.Select(entry => entry.Status));
        window.Close();
    }

    [AvaloniaFact]
    public void An_edited_file_opens_behind_with_its_added_lines_tinted_and_its_banner()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "edit");
        Ui.WaitFor(() => !window.Chat.Busy && Reviewed(window, "hello.txt")?.AgentReview is not null, () => $"hello.txt in review in its editor, where the conversation is {Shown(window)}");

        OpenDocument hello = Reviewed(window, "hello.txt")!;
        Assert.Equal([new ChangeMark(1, 1, ChangeMarkKind.Added)], hello.Decorations.ReviewMarks);
        Assert.Empty(hello.Decorations.ReviewRemoved);
        Assert.StartsWith("The agent changed this file (hello.txt: +1 −0)", hello.AgentReview!.Said, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_replaced_line_shows_where_it_stood_as_a_row_above_the_line_that_took_its_place()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        window.OpenFile(Path.Combine(folder, "hello.txt"));
        Ui.Settle();
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        double before = editor.PointOf(0).Y;

        Send(window, "swap");
        Ui.WaitFor(() => !window.Chat.Busy && Reviewed(window, "hello.txt")?.Decorations.ReviewRemoved.Count == 1, () => $"the removed line marked, where the conversation is {Shown(window)}");
        Ui.Settle();

        OpenDocument hello = Reviewed(window, "hello.txt")!;
        Assert.Equal("hi\n", hello.Text.Snapshot.ToString());
        Assert.Equal(0, hello.Decorations.ReviewRemoved[0].Line);
        Assert.Equal(["hello"], hello.Decorations.ReviewRemoved[0].Text);
        Assert.Equal([new ChangeMark(0, 1, ChangeMarkKind.Added)], hello.Decorations.ReviewMarks);
        // The removed line's row stands above the first line, which moves down a row.
        Assert.True(editor.PointOf(0).Y >= before + (editor.LineHeight * 0.9), $"The first line stayed at {editor.PointOf(0).Y}, from {before}");
        window.Close();
    }

    [AvaloniaFact]
    public void The_files_banner_Keep_ends_its_review_and_Undo_puts_it_back()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "edit");
        Ui.WaitFor(() => !window.Chat.Busy && Reviewed(window, "hello.txt")?.AgentReview is not null && Reviewed(window, "notes.md")?.AgentReview is not null, "both files in review");

        Reviewed(window, "notes.md")!.AgentReview!.Keep();
        Ui.WaitFor(() => Reviewed(window, "notes.md")?.AgentReview is null, "notes.md kept");
        Assert.Empty(Reviewed(window, "notes.md")!.Decorations.ReviewMarks);
        Assert.Equal(["hello.txt"], window.Chat.Review.Shown.Select(file => Path.GetFileName(file.Path)));

        Reviewed(window, "hello.txt")!.AgentReview!.Undo();
        Ui.WaitFor(() => window.Chat.Review.Shown.Count == 0 && Reviewed(window, "hello.txt")?.AgentReview is null, "hello.txt undone");
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(folder, "hello.txt")));
        Assert.Equal("hello\n", Reviewed(window, "hello.txt")!.Text.Snapshot.ToString());
        window.Close();
    }

    [AvaloniaFact]
    public void An_edit_asked_first_shows_in_its_file_unwritten_and_the_banners_Accept_answers_it()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "ask");
        Ui.WaitFor(() => Reviewed(window, "hello.txt")?.AgentReview is { KeepWord: "Accept" }, () => $"the edit shown in hello.txt, where the conversation is {Shown(window)}");

        OpenDocument hello = Reviewed(window, "hello.txt")!;
        Assert.Equal("hello\n", hello.Text.Snapshot.ToString());
        Passenger.Editor.RemovedLines coming =Assert.Single(hello.Decorations.ReviewRemoved);
        Assert.True(coming.Added, "The line the agent asks to add is drawn as a removed one");
        Assert.Equal(["world"], coming.Text);
        Assert.Equal(1, coming.Line);
        ApprovalCard card = window.Chat.Questions.Single();
        Ui.Click(card.GetVisualDescendants().OfType<Button>().First(b => b.Content is TextBlock { Text: { } text } && text.EndsWith("hello.txt", StringComparison.Ordinal)));
        Ui.Settle();
        Assert.Same(hello, window.EditorArea.Active);

        Ui.Click(window.EditorArea.ActiveGroup.ReviewLinks.Keep);
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("allow-once", agent.Answered);
        Assert.Equal("Accepted", card.State);
        Assert.Equal("hello\nworld\n", File.ReadAllText(Path.Combine(folder, "hello.txt")));
        // Accepted before it was written, it was reviewed then: no Keep or Undo asked of it again.
        Ui.WaitFor(() => hello.AgentReview is null && hello.Decorations.ReviewRemoved.Count == 0, "the edit's marks gone once answered");
        Assert.Empty(window.Chat.Review.Shown);
        window.Close();
    }

    [AvaloniaFact]
    public void An_edit_asked_first_and_rejected_in_its_file_leaves_the_file_as_it_was()
    {
        MainWindow window = Reviewing(out StandInAgent agent, out string folder);
        Send(window, "ask");
        Ui.WaitFor(() => Reviewed(window, "hello.txt")?.AgentReview is { UndoWord: "Reject" }, "the edit shown in hello.txt");

        Ui.Click(window.EditorArea.ActiveGroup.ReviewLinks.Undo);
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal("reject", agent.Answered);
        Assert.Equal("Rejected", window.Chat.Questions.Single().State);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(folder, "hello.txt")));
        Ui.WaitFor(() => Reviewed(window, "hello.txt")?.AgentReview is null, "the edit's banner gone once answered");
        Assert.Empty(window.Chat.Review.Shown);
        window.Close();
    }

    [AvaloniaFact]
    public void A_reply_shows_as_Markdown_once_whole()
    {
        MainWindow window = Connected(out StandInAgent agent);
        Send(window, "bold");
        Ui.WaitFor(() => !window.Chat.Busy, () => $"the turn ended, where the conversation is {Shown(window)}");

        Assert.Equal(("Claude Agent", "Use **this** one."), window.Chat.Conversation[^1]);
        Assert.DoesNotContain(window.Chat.GetVisualDescendants().OfType<TextBlock>(), text => (text.Text ?? string.Empty).Contains("**", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void A_suggestion_of_the_empty_conversation_fills_the_box()
    {
        MainWindow window = Connected(out _);
        Ui.Click(Named(window, "Explain how this project is structured"));
        Assert.Equal("Explain how this project is structured", window.Chat.Input.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void An_agent_that_cannot_start_says_why_and_offers_connect_again()
    {
        MainWindow window = Ui.OpenFolder(TestFolders.Create("chat"), languageServer: false);
        window.StartChatAgent = _ => Task.FromException<ChatAgent>(new Win32Exception("npx was not found"));
        window.ShowToolWindow(ToolWindowIds.Chat);
        Ui.Settle();

        Ui.Click(ConnectButton(window));
        Ui.WaitFor(() => window.Chat.Said.StartsWith("Could not start", StringComparison.Ordinal), () => $"the failure said, where the window says \"{window.Chat.Said}\"");

        Assert.Equal("Could not start the agent: npx was not found", window.Chat.Said);
        Assert.True(window.Chat.OffersConnect);
        window.Close();
    }

    private static MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = Passenger.App.State.StateStore.DefaultRoot };
        window.Show();
        Ui.Settle();
        return window;
    }

    private static ToggleButton Button(MainWindow window) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>().First(b => Equals(b.Tag, ToolWindowIds.Chat));

    private static Button ConnectButton(MainWindow window) => Named(window, "Connect to Claude Code");

    private static Button Named(MainWindow window, string content) =>
        window.Chat.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, content) && b.IsEffectivelyVisible);

    /// <summary>A window on a folder of its own with the Chat window connected to a stand-in agent.</summary>
    private static MainWindow Connected(out StandInAgent agent)
    {
        MainWindow window = Ui.OpenFolder(TestFolders.Create("chat"), languageServer: false);
        StandInAgent started = new();
        window.StartChatAgent = _ => Task.FromResult(started.Agent);
        window.ShowToolWindow(ToolWindowIds.Chat);
        Ui.Settle();
        Ui.Click(ConnectButton(window));
        Ui.WaitFor(() => window.Chat.Said == "Connected to Claude Agent 0.107.3", () => $"the agent connected, where the window says \"{window.Chat.Said}\"");
        agent = started;
        return window;
    }

    /// <summary>A window connected to a stand-in agent in a folder holding hello.txt, the agent writing only once the
    /// window has taken the file for review, as Claude Code names a file before it writes it.</summary>
    private static MainWindow Reviewing(out StandInAgent agent, out string folder)
    {
        string made = TestFolders.Create("chat");
        File.WriteAllText(Path.Combine(made, "hello.txt"), "hello\n");
        MainWindow window = Ui.OpenFolder(made, languageServer: false);
        StandInAgent started = new();
        ConcurrentDictionary<string, TaskCompletionSource> taken = new(StringComparer.Ordinal);
        TaskCompletionSource Taken(string path) => taken.GetOrAdd(Path.GetFileName(path), _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        window.ChatReview.Taken += path => Taken(path).TrySetResult();
        started.BeforeWrite = path => Taken(path).Task;
        window.StartChatAgent = _ => Task.FromResult(started.Agent);
        window.ShowToolWindow(ToolWindowIds.Chat);
        Ui.Settle();
        Ui.Click(ConnectButton(window));
        Ui.WaitFor(() => window.Chat.Said == "Connected to Claude Agent 0.107.3", "the agent connected");
        agent = started;
        folder = made;
        return window;
    }

    /// <summary>The open file of that name, if it is open.</summary>
    private static OpenDocument? Reviewed(MainWindow window, string name) =>
        window.EditorArea.Documents.FirstOrDefault(document => Path.GetFileName(document.Path) == name);

    private static Button InReview(MainWindow window, string content) =>
        window.Chat.Review.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, content));

    private static string? Chosen(ComboBox picker) => (picker.SelectedItem as ComboBoxItem)?.Content as string;

    private static void Send(MainWindow window, string text)
    {
        window.Chat.Input.Text = text;
        Ui.Click(Named(window, "Send"));
    }

    /// <summary>The card's button of those words, a numbered answer's too.</summary>
    private static Button InCard(ApprovalCard card, string content) =>
        card.GetVisualDescendants().OfType<Button>().First(b => b.Content is TextBlock { Text: { } text } ? text == content
            : b.Content is Panel panel && panel.Children.OfType<TextBlock>().Any(words => words.Text == content));

    private static string Shown(MainWindow window) => string.Join(" | ", window.Chat.Conversation.Select(part => $"{part.Who}: {part.Text}"));

    /// <summary>An agent in memory that answers as the Claude adapter did in real runs: initialize, session/new and
    /// set_mode; a prompt with its reply in three pieces, or by its first word: "wait" nothing until session/cancel;
    /// "edit" hello.txt changed and notes.md made, each written once the window has taken it;
    /// "tool" a read's call with words before and after it; "write" a question with a new file's diff, then its words;
    /// "bold" Markdown.</summary>
    private sealed class StandInAgent
    {
        private readonly Pipe toSession = new();
        private readonly Pipe fromSession = new();
        private readonly List<string> prompts = [];

        public StandInAgent()
        {
            Agent = new ChatAgent(AcpSession.Over(toSession.Reader.AsStream(), fromSession.Writer.AsStream()), () => toSession.Writer.Complete());
            // Unheld: it answers for as long as the session lives, which no wait for the IDE to be quiet may wait out.
            Passenger.Core.UnawaitedWork.Observe(Passenger.Core.BackgroundWork.Unheld(AnswerAsync));
        }

        public ChatAgent Agent { get; }

        /// <summary>The folder the session was opened in.</summary>
        public string? SessionFolder { get; private set; }

        /// <summary>The mode the session was set to.</summary>
        public string? Mode { get; private set; }

        /// <summary>The model the session was set to.</summary>
        public string Model { get; private set; } = "opus";

        /// <summary>Whether the turn was cancelled.</summary>
        public bool Cancelled { get; private set; }

        /// <summary>The plan it asks to build, as the Claude adapter sends a plan's Markdown.</summary>
        public const string Plan = "# Plan: Add a math class\n\n1. Write MathOperations.cs\n2. Add tests\n";

        private readonly List<string> steered = [];
        private readonly List<string> order = [];

        /// <summary>The messages steered into its turn.</summary>
        public IReadOnlyList<string> Steered
        {
            get
            {
                lock (steered)
                {
                    return [.. steered];
                }
            }
        }

        /// <summary>What came, in turn, of a steer and the plan's answer.</summary>
        public IReadOnlyList<string> Order
        {
            get
            {
                lock (steered)
                {
                    return [.. order];
                }
            }
        }

        /// <summary>The answer to its plan: an option's id, or "cancelled".</summary>
        public string? PlanAnswer { get; private set; }

        /// <summary>Waited on before it writes a file, by the file's path.</summary>
        public Func<string, Task> BeforeWrite { get; set; } = _ => Task.CompletedTask;

        /// <summary>The answer to its question: an option's id, or "cancelled".</summary>
        public string? Answered { get; private set; }

        public IReadOnlyList<string> Prompts
        {
            get
            {
                lock (prompts)
                {
                    return [.. prompts];
                }
            }
        }

        private async Task AnswerAsync()
        {
            try
            {
                await AnsweringAsync();
            }
            catch (InvalidOperationException)
            {
                // The window ended the agent while it answered, as a closed connection ends a real one.
            }
        }

        private async Task AnsweringAsync()
        {
            UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
            using StreamReader reader = new(fromSession.Reader.AsStream(), utf8);
            await using StreamWriter writer = new(toSession.Writer.AsStream(), utf8) { NewLine = "\n", AutoFlush = true };
            string? waiting = null;
            while (await reader.ReadLineAsync() is string line)
            {
                JsonNode message = JsonNode.Parse(line)!;
                string id = message["id"]?.ToJsonString() ?? "null";
                JsonNode? parameters = message["params"];
                switch (message["method"]?.GetValue<string>())
                {
                    case "initialize":
                        await Answer(writer, id, """{"protocolVersion":1,"agentInfo":{"name":"@agentclientprotocol/claude-agent-acp","title":"Claude Agent","version":"0.107.3"},"authMethods":[]}""");
                        break;
                    case "session/new":
                        SessionFolder = parameters?["cwd"]?.GetValue<string>();
                        await Answer(writer, id, """{"sessionId":"sess_1","modes":{"currentModeId":"auto","availableModes":[{"id":"default","name":"Manual"},{"id":"acceptEdits","name":"Accept edits"},{"id":"plan","name":"Plan"}]},"configOptions":""" + Config() + "}");
                        await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"auth/status_update","params":{"state":"authenticated","accounts":[{"label":"Claude Pro","active":true,"email":"person@example.com"}]}}""");
                        await Update(writer, """{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"code-review","description":"Review the current diff"},{"name":"compact","description":"Clear the conversation but keep a summary"},{"name":"init","description":"Write a CLAUDE.md"}]}""");
                        await Update(writer, """{"sessionUpdate":"usage_update","used":40000,"size":1000000,"_meta":{"_claude/rateLimit":{"unifiedWindows":{"five_hour":{"utilization":0.3,"resetsAt":1794848400},"seven_day":{"utilization":0.04,"resetsAt":1795428000}}}}}""");
                        break;
                    case "session/set_config_option":
                        string setting = parameters?["configId"]?.GetValue<string>() ?? string.Empty;
                        string chosen = parameters?["value"]?.GetValue<string>() ?? string.Empty;
                        if (setting == "mode")
                        {
                            Mode = chosen;
                        }
                        else if (setting == "model")
                        {
                            Model = chosen;
                        }
                        await Answer(writer, id, """{"configOptions":""" + Config() + "}");
                        break;
                    case "session/set_mode":
                        Mode = parameters?["modeId"]?.GetValue<string>();
                        await Answer(writer, id, "{}");
                        break;
                    case "session/prompt":
                        string text = parameters?["prompt"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
                        lock (prompts)
                        {
                            prompts.Add(text);
                        }
                        if (text.StartsWith("wait", StringComparison.Ordinal))
                        {
                            waiting = id;
                            break;
                        }
                        if (text == "tool")
                        {
                            await Say(writer, "Reading it.");
                            await Update(writer, """{"toolCallId":"call_1","sessionUpdate":"tool_call","status":"pending","title":"Read File","kind":"read","content":[]}""");
                            await Update(writer, """{"toolCallId":"call_1","sessionUpdate":"tool_call_update","title":"Read hello.py","locations":[{"path":"/work/hello.py"}]}""");
                            await Update(writer, """{"toolCallId":"call_1","sessionUpdate":"tool_call_update","status":"completed"}""");
                            await Say(writer, "It prints hello.");
                            await Answer(writer, id, """{"stopReason":"end_turn"}""");
                            break;
                        }
                        if (text == "write")
                        {
                            waiting = id;
                            await Update(writer, """{"toolCallId":"call_2","sessionUpdate":"tool_call","status":"pending","title":"Preparing file…","kind":"edit","content":[]}""");
                            await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":"ask_1","method":"session/request_permission","params":{"sessionId":"sess_1","toolCall":{"toolCallId":"call_2","status":"pending","title":"Write notes.md","kind":"edit","content":[{"type":"diff","path":"/work/notes.md","oldText":"","newText":"# Notes\n"}]},"options":[{"optionId":"allow-once","name":"Yes","kind":"allow_once"},{"optionId":"allow-always","name":"Yes, allow all edits during this session","kind":"allow_always"},{"optionId":"reject","name":"No","kind":"reject_once"}]}}""");
                            break;
                        }
                        if (text == "ask" && SessionFolder is { } asking)
                        {
                            // As the Claude adapter asks in Manual mode: the call naming its file, then the question with its diff.
                            waiting = id;
                            string named = JsonValue.Create(Path.Combine(asking, "hello.txt")).ToJsonString();
                            await Update(writer, $$$"""{"toolCallId":"call_7","sessionUpdate":"tool_call","status":"pending","title":"Edit hello.txt","kind":"edit","content":[],"locations":[{"path":{{{named}}}}]}""");
                            await writer.WriteLineAsync($$$"""{"jsonrpc":"2.0","id":"ask_edit","method":"session/request_permission","params":{"sessionId":"sess_1","toolCall":{"toolCallId":"call_7","status":"pending","title":"Edit hello.txt","kind":"edit","content":[{"type":"diff","path":{{{named}}},"oldText":"hello\n","newText":"hello\nworld\n"}]},"options":[{"optionId":"allow-once","name":"Yes","kind":"allow_once"},{"optionId":"allow-always","name":"Yes, allow all edits during this session","kind":"allow_always"},{"optionId":"reject","name":"No","kind":"reject_once"}]}}""");
                            break;
                        }
                        if (text == "swap" && SessionFolder is { } swapped)
                        {
                            await Edit(writer, "call_6", Path.Combine(swapped, "hello.txt"), "hi\n");
                            await Answer(writer, id, """{"stopReason":"end_turn"}""");
                            break;
                        }
                        if (text == "edit" && SessionFolder is { } folder)
                        {
                            await Edit(writer, "call_3", Path.Combine(folder, "hello.txt"), "hello\nworld\n");
                            await Edit(writer, "call_4", Path.Combine(folder, "notes.md"), "# Notes\n");
                            await Say(writer, "Done.");
                            await Answer(writer, id, """{"stopReason":"end_turn"}""");
                            break;
                        }
                        if (text == "plan")
                        {
                            waiting = id;
                            string content = JsonValue.Create(Plan).ToJsonString();
                            // Claude Code drafts its plan in its own folder first; nothing is written there here.
                            string draft = JsonValue.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plans", "stand-in-draft.md")).ToJsonString();
                            await Update(writer, $$$"""{"toolCallId":"call_draft","sessionUpdate":"tool_call","status":"pending","title":"Write","kind":"edit","content":[],"locations":[{"path":{{{draft}}}}]}""");
                            await Update(writer, """{"toolCallId":"call_draft","sessionUpdate":"tool_call_update","status":"completed"}""");
                            await Update(writer, """{"sessionUpdate":"plan","entries":[{"content":"Read the code","priority":"high","status":"completed"},{"content":"Write MathOperations.cs","priority":"high","status":"in_progress"},{"content":"Add tests","priority":"medium","status":"pending"}]}""");
                            await Update(writer, $$$"""{"toolCallId":"call_5","sessionUpdate":"tool_call","status":"pending","title":"Approve Plan","kind":"switch_mode","content":[{"type":"content","content":{"type":"text","text":{{{content}}}}}]}""");
                            await writer.WriteLineAsync($$$"""{"jsonrpc":"2.0","id":"ask_plan","method":"session/request_permission","params":{"sessionId":"sess_1","toolCall":{"toolCallId":"call_5","title":"Ready to code?","kind":"switch_mode","content":[{"type":"content","content":{"type":"text","text":{{{content}}}}}]},"options":[{"optionId":"exit-plan-auto","name":"Yes, and use auto mode","kind":"allow_always"},{"optionId":"exit-plan-default","name":"Yes, manually approve edits","kind":"allow_once"},{"optionId":"reject","name":"No, keep planning","kind":"reject_once"}]}}""");
                            break;
                        }
                        if (text == "bold")
                        {
                            await Say(writer, "Use **this** one.");
                            await Answer(writer, id, """{"stopReason":"end_turn"}""");
                            break;
                        }
                        foreach (string piece in new[] { "I'll ", "add a ", "README." })
                        {
                            await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":""" + JsonValue.Create(piece).ToJsonString() + "}}}}");
                        }
                        await Answer(writer, id, """{"stopReason":"end_turn"}""");
                        break;
                    case "session/cancel" when waiting is not null && Answered is null && !Asked:
                        Cancelled = true;
                        await Answer(writer, waiting, """{"stopReason":"cancelled"}""");
                        waiting = null;
                        break;
                    case "_session/steering":
                        lock (steered)
                        {
                            steered.Add(parameters?["prompt"]?[0]?["text"]?.GetValue<string>() ?? string.Empty);
                            order.Add("steer");
                        }
                        await Answer(writer, id, """{"outcome":"injected"}""");
                        break;
                    case null when message["id"]?.ToJsonString() == "\"ask_plan\"":
                        JsonNode? decided = message["result"]?["outcome"];
                        lock (steered)
                        {
                            order.Add("answer");
                        }
                        PlanAnswer = decided?["outcome"]?.GetValue<string>() == "cancelled" ? "cancelled" : decided?["optionId"]?.GetValue<string>();
                        if (waiting is not null)
                        {
                            await Update(writer, """{"toolCallId":"call_5","sessionUpdate":"tool_call_update","status":"completed","title":"Exited Plan Mode"}""");
                            await Say(writer, PlanAnswer == "reject" ? "I'll keep planning." : "Building.");
                            await Answer(writer, waiting, """{"stopReason":"end_turn"}""");
                            waiting = null;
                        }
                        break;
                    case null when message["id"]?.ToJsonString() == "\"ask_1\"":
                        JsonNode? outcome = message["result"]?["outcome"];
                        Answered = outcome?["outcome"]?.GetValue<string>() == "cancelled" ? "cancelled" : outcome?["optionId"]?.GetValue<string>();
                        if (waiting is not null)
                        {
                            await Update(writer, """{"toolCallId":"call_2","sessionUpdate":"tool_call_update","status":"completed"}""");
                            await Say(writer, "Wrote notes.md.");
                            await Answer(writer, waiting, """{"stopReason":"end_turn"}""");
                            waiting = null;
                        }
                        break;
                    case null when message["id"]?.ToJsonString() == "\"ask_edit\"" && SessionFolder is { } edited:
                        JsonNode? said = message["result"]?["outcome"];
                        Answered = said?["outcome"]?.GetValue<string>() == "cancelled" ? "cancelled" : said?["optionId"]?.GetValue<string>();
                        bool allowed = Answered is "allow-once" or "allow-always";
                        if (allowed)
                        {
                            string hello = Path.Combine(edited, "hello.txt");
                            await BeforeWrite(hello);
                            await File.WriteAllTextAsync(hello, "hello\nworld\n");
                        }
                        if (waiting is not null)
                        {
                            await Update(writer, $$$"""{"toolCallId":"call_7","sessionUpdate":"tool_call_update","status":"{{{(allowed ? "completed" : "failed")}}}"}""");
                            await Say(writer, allowed ? "Added world." : "Left it.");
                            await Answer(writer, waiting, """{"stopReason":"end_turn"}""");
                            waiting = null;
                        }
                        break;
                    case string when message["id"] is not null:
                        await writer.WriteLineAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32601,"message":"not played"}}""");
                        break;
                }
            }
        }

        /// <summary>Whether it has a question open, whose answer ends the turn rather than session/cancel.</summary>
        private bool Asked => Prompts.LastOrDefault() is "write" or "plan" or "ask";

        /// <summary>An edit as the Claude adapter sends one in Accept edits mode: its call, then the file it names, then,
        /// once written, done.</summary>
        private async Task Edit(StreamWriter writer, string call, string path, string text)
        {
            string named = JsonValue.Create(path).ToJsonString();
            await Update(writer, $$$"""{"toolCallId":"{{{call}}}","sessionUpdate":"tool_call","status":"pending","title":"Preparing file…","kind":"edit","content":[]}""");
            await Update(writer, $$$"""{"toolCallId":"{{{call}}}","sessionUpdate":"tool_call_update","title":"Write {{{Path.GetFileName(path)}}}","locations":[{"path":{{{named}}}}]}""");
            await BeforeWrite(path);
            await File.WriteAllTextAsync(path, text);
            await Update(writer, $$$"""{"toolCallId":"{{{call}}}","sessionUpdate":"tool_call_update","status":"completed"}""");
        }

        /// <summary>Its settings as the Claude adapter lists them, cut to a few choices, at their values now.</summary>
        private string Config() =>
            $$$"""[{"id":"mode","name":"Mode","category":"mode","type":"select","currentValue":"{{{Mode ?? "auto"}}}","options":[{"value":"default","name":"Manual"},{"value":"acceptEdits","name":"Accept edits"},{"value":"plan","name":"Plan"}]},{"id":"model","name":"Model","category":"model","type":"select","currentValue":"{{{Model}}}","options":[{"value":"opus","name":"Opus 5.5"},{"value":"sonnet","name":"Sonnet 5.5"}]},{"id":"effort","name":"Effort","category":"thought_level","type":"select","currentValue":"high","options":[{"value":"low","name":"Low"},{"value":"high","name":"High"}]}]""";

        private static Task Say(StreamWriter writer, string text) =>
            Update(writer, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":""" + JsonValue.Create(text).ToJsonString() + "}}");

        private static Task Update(StreamWriter writer, string update) =>
            writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":""" + update + "}}");

        private static Task Answer(StreamWriter writer, string id, string result) =>
            writer.WriteLineAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{{{result}}}}""");
    }
}
