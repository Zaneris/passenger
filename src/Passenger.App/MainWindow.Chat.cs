using System.Collections.Concurrent;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Passenger.Acp;
using Passenger.App.Chat;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using VcsDiff = Passenger.Vcs.FileDiff;

namespace Passenger.App;

/// <summary>The window's Chat: its tool window, the coding agent it connects to in the open folder, ended with the
/// window, and the review of the files the agent changed. The agent's updates come on its connection's thread and land
/// on the window's, each held as background work.</summary>
public partial class MainWindow
{
    private readonly ChatReview chatReview = new();
    private readonly ConcurrentDictionary<string, string[]> chatEdits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Path, string Text)> chatPlans = new(StringComparer.Ordinal);

    /// <summary>The edits the person answered before they were written, by call: reviewed already as they land.</summary>
    private readonly ConcurrentDictionary<string, bool> answeredEdits = new(StringComparer.Ordinal);

    /// <summary>The edit waiting for its answer in each file, by its canonical path: shown in the file's own editor, its
    /// banner's Accept and Reject answering its card. On the window's thread.</summary>
    private readonly Dictionary<string, (ApprovalCard Card, ChatChange Change)> chatProposals = new(StringComparer.Ordinal);

    /// <summary>The copy shown of each file the agent asks to make (<see cref="ChatProposals"/>), by the file's canonical
    /// path, and the file each copy stands for. On the window's thread.</summary>
    private readonly Dictionary<string, string> proposalCopies = new(StringComparer.Ordinal);

    /// <summary>The cards asked, by their call, for what their answer carries (a plan's revision). Any thread.</summary>
    private readonly ConcurrentDictionary<string, ApprovalCard> askedCards = new(StringComparer.Ordinal);

    /// <summary>The plans the person edits, shown as their source beside their page rather than as a page alone.</summary>
    private readonly HashSet<string> editedPlans = new(StringComparer.Ordinal);
    private ChatAgent? chatAgent;
    private CancellationTokenSource? chatTurn;

    /// <summary>The files the agent changed that wait for the person to keep or undo them.</summary>
    public ChatReview ChatReview => chatReview;

    /// <summary>The Chat tool window, built as it is first shown or asked for.</summary>
    public ChatView Chat => (ChatView)Dock[ToolWindowIds.Chat].Content;

    /// <summary>Starts the Chat window's agent in a folder: the Claude adapter, or a test's stand-in.</summary>
    public Func<string, Task<ChatAgent>> StartChatAgent { get; set; } = ChatAgent.StartClaudeAsync;

    private void AddChat()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Chat, "Chat", IconKeys.Chat, () =>
        {
            ChatView view = new() { Browse = Browse };
            view.Say("No agent connected", offerConnect: true);
            view.ConnectRequested += (_, _) =>
            {
                Task connecting = ConnectChatAsync(view);
                BackgroundWork.Track(connecting);
                IdeErrors.Observe(connecting);
            };
            // A turn is not held: it lasts as long as the agent works, which no wait for the IDE to be quiet may wait out.
            view.SendRequested += (_, text) => IdeErrors.Observe(SendChatAsync(view, text));
            view.StopRequested += (_, _) =>
            {
                view.CancelQuestions();
                chatTurn?.Cancel();
            };
            view.PlanRequested += (_, path) => OpenChatPlan(path);
            view.PlanEditRequested += (_, path) => EditChatPlan(path);
            view.Asking += (_, card) =>
            {
                askedCards[card.CallId] = card;
                Propose(card);
            };
            view.ChangeRequested += (_, change) => ShowProposal(change);
            view.PlansRequested += (_, button) => ShowChatPlans(button);
            view.ConfigChosen += (_, chosen) =>
            {
                Task setting = SetChatConfigAsync(view, chosen.Id, chosen.Value);
                BackgroundWork.Track(setting);
                IdeErrors.Observe(setting);
            };
            view.Review.KeepRequested += (_, files) =>
            {
                foreach (ReviewedFile file in files)
                {
                    chatReview.Done(file.Path);
                }
                ShowReview(view);
            };
            view.Review.UndoRequested += (_, files) =>
            {
                Task undoing = UndoChatAsync(view, files);
                BackgroundWork.Track(undoing);
                IdeErrors.Observe(undoing);
            };
            view.Review.DiffRequested += (_, file) => OpenChangedFile(file.Path);
            view.EditDiffRequested += (_, id) =>
            {
                if (chatEdits.TryGetValue(id, out string[]? named) && named.Length > 0)
                {
                    OpenChangedFile(named[0]);
                }
            };
            return view;
        }, Docking.ToolAnchor.RightTop)
        {
            Action = AppActions.ShowChat,
            Menus = [new MenuPlace("Tools", 0, 10)],
        });
        Closed += (_, _) => EndChatAgent();
        reviewMarking = new Debounce(TimeSpan.FromMilliseconds(250), MarkReviewAsync);
        // A file opened while the agent's changes to it wait, and every edit of one, is marked again once typing pauses.
        Editors.Opened += (_, document) =>
        {
            document.Text.Changed += (_, _) =>
            {
                if (document.AgentReview is not null)
                {
                    reviewMarking.Restart();
                }
            };
            reviewMarking.Restart();
        };
    }

    private Debounce? reviewMarking;

    /// <summary>The review as it is now: the files in the bar above the box, and in each open file's own editor its lines
    /// tinted and its banner's Keep and Undo.</summary>
    private void ShowReview(ChatView view)
    {
        view.Review.Show(chatReview.Files);
        reviewMarking?.Restart();
    }

    /// <summary>Opens the files the agent just changed, each in a tab behind the file the person has shown (an agent's
    /// file comes behind, <see cref="AgentAsks"/>), so its changes are there to see in its own editor.</summary>
    private void OpenBehind(IEnumerable<ReviewedFile> files)
    {
        foreach (ReviewedFile file in files)
        {
            if (!file.Deleted && Editors.Find(file.Path) is null && File.Exists(file.Path))
            {
                AgentAsks.Act(() => Editors.Open(file.Path, focus: false));
            }
        }
    }

    /// <summary>Marks the agent's changes in review in every open file they are in, as Cursor shows them in the file: its
    /// lines tinted from the file's text before the agent against its text now (red rules where lines went), and its
    /// banner saying so with Keep and Undo; a file with none waiting has neither.</summary>
    private async Task MarkReviewAsync()
    {
        IReadOnlyList<ReviewedFile> files = chatReview.Files;
        foreach (Workspace.OpenDocument document in Editors.Documents.ToArray())
        {
            string path = PathNames.Canonical(document.Path);
            if (ChatProposals.IsCopy(path))
            {
                // A file the agent asks to make: every line of its copy is what it would add.
                if (proposalCopies.TryGetValue(path, out string? original) && chatProposals.TryGetValue(original, out (ApprovalCard Card, ChatChange Change) made))
                {
                    int lines = ChatDiff.Count(made.Change).Added;
                    document.Decorations.SetReviewMarks(lines > 0 ? [new ChangeMark(0, lines, ChangeMarkKind.Added)] : []);
                    document.AgentReview = new Workspace.AgentReview(
                        $"The agent asks to make this file ({Path.GetFileName(original)}: +{lines}), not written until you accept it",
                        () => made.Card.Choose(allow: true),
                        () => made.Card.Choose(allow: false),
                        "Accept",
                        "Reject");
                }
                else
                {
                    document.Decorations.SetReviewMarks([]);
                    document.AgentReview = null;
                }
                continue;
            }
            if (document.HoldsText && chatProposals.TryGetValue(path, out (ApprovalCard Card, ChatChange Change) asked))
            {
                TextSnapshot now = document.Text.Snapshot;
                ProposalMarks? marks = await BackgroundWork.Run(() => MarksOf(now.ToString(), asked.Change));
                if (!ReferenceEquals(document.Text.Snapshot, now))
                {
                    continue;
                }
                document.Decorations.SetReviewMarks(marks?.Gone ?? [], marks?.Coming);
                document.AgentReview = new Workspace.AgentReview(
                    $"The agent asks to change this file ({Path.GetFileName(path)}: {(marks is { } m ? $"+{m.Added} −{m.Removed}" : "its diff in the chat")})",
                    () => asked.Card.Choose(allow: true),
                    () => asked.Card.Choose(allow: false),
                    "Accept",
                    "Reject");
                continue;
            }
            if (files.FirstOrDefault(file => file.Path == path) is not { } file || !document.HoldsText)
            {
                document.Decorations.SetReviewMarks([]);
                document.AgentReview = null;
                continue;
            }
            TextSnapshot text = document.Text.Snapshot;
            (ChangeMark[] added, RemovedLines[] removed) = await BackgroundWork.Run(() => ReviewMarks(file.BeforeText, text.ToString()));
            if (!ReferenceEquals(document.Text.Snapshot, text))
            {
                // Typed over meanwhile: the debounce that typing restarted marks it again.
                continue;
            }
            document.Decorations.SetReviewMarks(added, removed);
            document.AgentReview = new Workspace.AgentReview(
                $"The agent changed this file ({Path.GetFileName(path)}: +{file.Added} −{file.Removed}), to keep or undo",
                () => KeepChatFile(file),
                () => UndoChatFile(file));
        }
    }

    /// <summary>A file's changes as Cursor shows them in the file: the lines added (in place of others or not) tinted as
    /// added, and the lines removed shown where they stood, above what took their place.</summary>
    private static (ChangeMark[] Added, RemovedLines[] Removed) ReviewMarks(string before, string now)
    {
        string[] then = LineDiff.Lines(before);
        IReadOnlyList<DiffHunk> hunks = LineDiff.Hunks(then, LineDiff.Lines(now));
        ChangeMark[] added = [.. hunks.Where(h => h.NewCount > 0).Select(h => new ChangeMark(h.NewStart, h.NewCount, ChangeMarkKind.Added))];
        RemovedLines[] removed = [.. hunks.Where(h => h.OldCount > 0).Select(h => new RemovedLines(h.NewStart, then[h.OldStart..(h.OldStart + h.OldCount)]))];
        return (added, removed);
    }

    /// <summary>An edit waiting for its answer as Cursor shows it in the file: the lines it would replace tinted as deleted,
    /// and the lines it would write drawn below them as rows of their own on the added colour, none of them in the text
    /// yet. Null where the file no longer holds what the edit replaces.</summary>
    private static ProposalMarks? MarksOf(string now, ChatChange change)
    {
        if (change.Applied(now) is not { } after)
        {
            return null;
        }
        string[] lines = LineDiff.Lines(after);
        IReadOnlyList<DiffHunk> hunks = LineDiff.Hunks(LineDiff.Lines(now), lines);
        return new ProposalMarks(
            [.. hunks.Where(h => h.OldCount > 0).Select(h => new ChangeMark(h.OldStart, h.OldCount, ChangeMarkKind.Deleted))],
            [.. hunks.Where(h => h.NewCount > 0).Select(h => new RemovedLines(h.OldStart + h.OldCount, lines[h.NewStart..(h.NewStart + h.NewCount)], Added: true))],
            hunks.Sum(h => h.NewCount),
            hunks.Sum(h => h.OldCount));
    }

    private sealed record ProposalMarks(ChangeMark[] Gone, RemovedLines[] Coming, int Added, int Removed);

    /// <summary>An edit's question was shown: each file it changes opens in a tab behind the one the person has shown, the
    /// change marked in it until the question is answered.</summary>
    private void Propose(ApprovalCard card)
    {
        if (card.Changes.Count == 0)
        {
            return;
        }
        foreach (ChatChange change in card.Changes)
        {
            string path = PathNames.Canonical(change.Path);
            chatProposals[path] = (card, change);
            if (Shown(change) is { } shown && Editors.Find(shown) is null)
            {
                AgentAsks.Act(() => Editors.Open(shown, focus: false));
            }
        }
        reviewMarking?.Restart();
        IdeErrors.Observe(UnproposeAsync(card));
    }

    /// <summary>Once a question is answered or cancelled, its edit leaves the files' editors, and a new file's copy closes;
    /// one accepted opens in its place once the agent wrote it.</summary>
    private async Task UnproposeAsync(ApprovalCard card)
    {
        await card.Answer;
        OnWindowThread(() =>
        {
            foreach (string path in chatProposals.Where(entry => entry.Value.Card == card).Select(entry => entry.Key).ToArray())
            {
                chatProposals.Remove(path);
            }
            foreach ((string copy, string original) in proposalCopies.Where(entry => !chatProposals.ContainsKey(entry.Value)).ToArray())
            {
                proposalCopies.Remove(copy);
                if (Editors.Find(copy) is { } open)
                {
                    Editors.CloseEverywhere(open);
                    if (card.State == "Accepted")
                    {
                        acceptedCopies.Add(original);
                    }
                }
            }
            reviewMarking?.Restart();
        });
    }

    /// <summary>The new files whose copy was shown and whose making was accepted, to open once the agent wrote them.</summary>
    private readonly HashSet<string> acceptedCopies = new(StringComparer.Ordinal);

    /// <summary>What shows a waiting edit in the editor: its file, or for a file the agent asks to make, the copy of what
    /// it would hold, written now; null where neither can be had.</summary>
    private string? Shown(ChatChange change)
    {
        string path = PathNames.Canonical(change.Path);
        if (!change.IsNew && File.Exists(path))
        {
            return path;
        }
        try
        {
            string copy = ChatProposals.Write(path, change.After);
            proposalCopies[copy] = path;
            if (Editors.Find(copy) is { } open && open.ChangedOnDisk())
            {
                open.Reload(overUnsaved: true);
            }
            return copy;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            status.Text = $"Could not show {Path.GetFileName(path)}: {error.Message}";
            return null;
        }
    }

    /// <summary>A waiting edit's file, clicked in its card: brought to the front in the main editor with the change marked
    /// in it, a new file as the copy of what it would hold.</summary>
    private void ShowProposal(ChatChange change)
    {
        if (Shown(change) is not { } shown)
        {
            return;
        }
        if (Editors.Open(shown, focus: true) is { } problem)
        {
            status.Text = problem;
        }
        reviewMarking?.Restart();
    }

    /// <summary>A file the agent changed, clicked in the chat: opened in the main editor, its changes in review marked in
    /// it; one the agent deleted as Passenger's diff of it, in a tab.</summary>
    private void OpenChangedFile(string path)
    {
        string full = PathNames.Canonical(path);
        if (File.Exists(full))
        {
            if (Editors.Open(full, focus: true) is { } problem)
            {
                status.Text = problem;
            }
            reviewMarking?.Restart();
        }
        else if (chatReview.Files.FirstOrDefault(file => file.Path == full) is { } file)
        {
            IdeErrors.Observe(ShowChatDiffAsync(file));
        }
        else
        {
            status.Text = $"{Path.GetFileName(full)} is not there";
        }
    }

    /// <summary>Keeps the agent's changes to a file, from its banner.</summary>
    private void KeepChatFile(ReviewedFile file)
    {
        chatReview.Done(file.Path);
        ShowReview(Chat);
    }

    /// <summary>Undoes the agent's changes to a file, from its banner.</summary>
    private void UndoChatFile(ReviewedFile file)
    {
        Task undoing = UndoChatAsync(Chat, [file]);
        BackgroundWork.Track(undoing);
        IdeErrors.Observe(undoing);
    }

    /// <summary>Starts the agent in the open folder and opens its session in the agent's Accept edits mode, as Cursor
    /// works: its edits apply as it goes, each taken for review first, and it asks before each command. An agent without
    /// that mode runs in Manual, asking before each edit too. Says each step and, failing, why.</summary>
    private async Task ConnectChatAsync(ChatView view)
    {
        if (Folder is not { } folder)
        {
            view.Say("Open a folder to chat with an agent in it", offerConnect: true);
            return;
        }
        EndChatAgent();
        view.Say("Starting Claude Code…");
        try
        {
            ChatAgent agent = await StartChatAgent(folder);
            chatAgent = agent;
            Follow(agent, view);
            AgentInfo info = await agent.Session.InitializeAsync();
            await agent.Session.NewSessionAsync(folder);
            if (agent.Session.Config.Any(option => option.Id == "mode" && option.Choices.Any(choice => choice.Value == ChatAgent.ReviewMode)))
            {
                await agent.Session.SetConfigAsync("mode", ChatAgent.ReviewMode);
            }
            else if (agent.Session.Modes.Any(mode => mode.Id == ChatAgent.ReviewMode))
            {
                await agent.Session.SetModeAsync(ChatAgent.ReviewMode);
            }
            else if (agent.Session.Modes.Any(mode => mode.Id == ChatAgent.ManualMode))
            {
                await agent.Session.SetModeAsync(ChatAgent.ManualMode);
            }
            string name = info.Title ?? info.Name;
            view.Connected(name, $"{name}{(info.Version is { } version ? " " + version : "")}");
            view.ShowConfig(agent.Session.Config);
        }
        catch (Exception error) when (error is AcpError or Win32Exception or IOException or InvalidOperationException)
        {
            EndChatAgent();
            view.Say($"Could not start the agent: {error.Message}", error: true, offerConnect: true);
        }
    }

    /// <summary>Sets one of the agent's settings the person chose (mode, model, effort); the pickers show what the agent
    /// answers it now is, which puts a refused choice back.</summary>
    private async Task SetChatConfigAsync(ChatView view, string id, string value)
    {
        if (chatAgent is not { } agent)
        {
            return;
        }
        try
        {
            await agent.Session.SetConfigAsync(id, value);
        }
        catch (AcpError error)
        {
            status.Text = $"The agent did not take that {id}: {error.Message}";
        }
        view.ShowConfig(agent.Session.Config);
    }

    /// <summary>Sends the person's message and shows the reply as it streams, until the agent ends its turn.</summary>
    private async Task SendChatAsync(ChatView view, string text)
    {
        if (chatAgent is not { } agent)
        {
            return;
        }
        using CancellationTokenSource turn = new();
        chatTurn = turn;
        view.Asked(text);
        try
        {
            TurnEnded ended = await agent.Session.PromptAsync(text, turn.Token);
            // The turn's answer comes after its updates, but its continuation outruns their posts: they land first.
            await Dispatcher.UIThread.InvokeAsync(static () => { });
            view.Answered(ended.StopReason switch
            {
                "end_turn" => null,
                "cancelled" => "Stopped",
                "max_tokens" => "Stopped: the reply reached its length limit",
                "refusal" => "The agent declined to answer",
                _ => $"Stopped: {ended.StopReason}",
            });
        }
        catch (AcpError error)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { });
            view.Answered($"The agent failed: {error.Message}", error: true);
        }
        finally
        {
            chatTurn = null;
        }
    }

    /// <summary>Shows what the agent says and does on the window's thread, and puts each question it asks before it acts
    /// to the person as a card, its answer the one clicked: an edit with its diff, the file read for its text before
    /// where the agent sent none.</summary>
    private void Follow(ChatAgent agent, ChatView view)
    {
        agent.Session.TextReceived += chunk => OnWindowThread(() => view.Streamed(chunk.Text));
        agent.Session.ConfigChanged += config => OnWindowThread(() => view.ShowConfig(config));
        agent.Session.CommandsChanged += commands => OnWindowThread(() => view.ShowCommands(commands));
        agent.Session.AccountChanged += account => OnWindowThread(() => view.ShowAccount(account));
        agent.Session.UsageUpdated += usage => OnWindowThread(() => view.ShowUsage(usage));
        agent.Session.PlanUpdated += plan => OnWindowThread(() => view.Todos.Show(plan.Entries));
        agent.Session.ToolCallChanged += call =>
        {
            Planned(call);
            (string[] paths, bool done) = Reviewed(call);
            // Claude Code's draft of its plan is its own: the plan shown is the folder's copy, so the draft is a step, not a file.
            ToolCall shown = call.Kind == "edit" && call.Locations.Count > 0 && call.Locations.All(ChatPlans.IsAgentDraft)
                ? call with { Title = "Draft the plan" }
                : call;
            ReviewedFile[] these = done ? [.. chatReview.Files.Where(file => paths.Contains(file.Path, StringComparer.Ordinal))] : [];
            // Answered before it was written, the edit was reviewed then: it asks for no Keep or Undo again.
            bool answered = done && answeredEdits.TryRemove(call.Id, out _);
            if (answered)
            {
                foreach (string path in paths)
                {
                    chatReview.Answered(path);
                }
            }
            OnWindowThread(() =>
            {
                view.Tool(shown);
                if (done)
                {
                    view.Counted(call.Id, these.Sum(file => file.Added), these.Sum(file => file.Removed));
                    ShowReview(view);
                    if (!answered)
                    {
                        OpenBehind(these);
                    }
                    // A new file the person accepted from its copy opens where its copy stood.
                    foreach (string path in paths.Where(path => acceptedCopies.Remove(path) && File.Exists(path)))
                    {
                        Editors.Open(path, focus: false);
                    }
                }
            });
        };
        agent.Session.AskPermission = async (request, _) =>
        {
            Reviewed(request.ToolCall);
            ChatPlan? plan = Planned(request.ToolCall);
            ChatChange[] changes = [.. request.ToolCall.Diffs.Select(diff => ChatChange.Of(diff.Path, diff.OldText, diff.NewText))];
            TaskCompletionSource<Task<string?>> shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            OnWindowThread(() => shown.SetResult(view.Ask(request, changes, plan)));
            string? answer = await await shown.Task;
            askedCards.TryRemove(request.ToolCall.Id, out ApprovalCard? asked);
            if (plan is null && changes.Length > 0)
            {
                answeredEdits[request.ToolCall.Id] = true;
            }
            if (plan is not null && asked?.Revision is { } revision)
            {
                await ReviseChatPlanAsync(agent, view, plan, revision);
            }
            if (plan is not null && answer is not null && request.Options.Any(option => option.Id == answer && option.Kind.StartsWith("allow", StringComparison.Ordinal)))
            {
                await BuildEditedPlanAsync(agent, view, request.ToolCall.Id, plan);
            }
            return answer;
        };
        IdeErrors.Observe(agent.Session.Connection.Completion.ContinueWith(_ => OnWindowThread(() =>
        {
            if (chatAgent == agent)
            {
                chatAgent = null;
                view.Say("The agent ended", error: true, offerConnect: true);
            }
        }), TaskScheduler.Default));
    }

    /// <summary>Writes a plan the agent asks to build to the folder's plans (<see cref="ChatPlans"/>), once a call, on the
    /// agent's connection's thread: the call that leaves plan mode carries the plan's Markdown. The plan's card's file.</summary>
    private ChatPlan? Planned(ToolCall call)
    {
        if (call.Kind != "switch_mode" || Folder is not { } root)
        {
            return null;
        }
        if (!chatPlans.TryGetValue(call.Id, out (string Path, string Text) saved))
        {
            if (call.Texts.FirstOrDefault(text => text.Trim().Length > 0) is not { } plan)
            {
                return null;
            }
            try
            {
                saved = (ChatPlans.Save(root, plan, DateTime.Now), plan);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                IdeErrors.Write($"Chat plan: {error.Message}");
                return null;
            }
            chatPlans[call.Id] = saved;
            // Shown at once in the main editor as a page, as Cursor shows its plan; the keys stay in the chat.
            string page = saved.Path;
            OnWindowThread(() => OpenChatPlan(page, focus: false));
        }
        return new ChatPlan(saved.Path, PathNames.Shown(saved.Path, root), ChatPlans.TitleOf(saved.Text));
    }

    /// <summary>Where the person changed a plan's file before Build, tells the agent to build their version: steered into
    /// the turn, so it follows the answer that leaves plan mode, and said in the chat.</summary>
    private async Task BuildEditedPlanAsync(ChatAgent agent, ChatView view, string callId, ChatPlan plan)
    {
        string now;
        try
        {
            now = await File.ReadAllTextAsync(plan.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (!chatPlans.TryGetValue(callId, out (string Path, string Text) saved) || Normalized(now) == Normalized(saved.Text))
        {
            return;
        }
        try
        {
            await agent.Session.SteerAsync($"I edited the plan before approving it. Build it as it is now in {plan.Shown}:\n\n{now}");
            OnWindowThread(() => view.Noted($"Sent your edited plan from {plan.Shown}"));
        }
        catch (AcpError error)
        {
            OnWindowThread(() => view.Noted($"Could not send your edited plan: {error.Message}", error: true));
        }

        static string Normalized(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    /// <summary>Asked to revise the plan, as Claude Code's "tell Claude what to change": the person's words steered into
    /// the turn, so they come before the answer that keeps planning, and said in the chat.</summary>
    private static async Task ReviseChatPlanAsync(ChatAgent agent, ChatView view, ChatPlan plan, string revision)
    {
        try
        {
            await agent.Session.SteerAsync($"Keep planning: revise the plan in {plan.Shown} as I ask, then ask me again.\n\n{revision}");
            OnWindowThread(() => view.Noted($"Asked to revise the plan: {revision}"));
        }
        catch (AcpError error)
        {
            OnWindowThread(() => view.Noted($"Could not send what to revise: {error.Message}", error: true));
        }
    }

    /// <summary>A plan's file as a page alone, its diagrams drawn; with <paramref name="focus"/> it takes the keys, as a click
    /// on Open plan asks.</summary>
    private void OpenChatPlan(string path, bool focus = true)
    {
        editedPlans.Remove(PathNames.Canonical(path));
        ShowChatPlan(path, focus);
    }

    /// <summary>A plan's file to edit before answering: its source in the editor, its page beside it.</summary>
    private void EditChatPlan(string path)
    {
        editedPlans.Add(PathNames.Canonical(path));
        ShowChatPlan(path, focus: true);
    }

    private void ShowChatPlan(string path, bool focus)
    {
        if (Editors.Open(path, focus) is { } problem)
        {
            status.Text = problem;
            return;
        }
        if (!Editors.ShowsMarkdownPreview)
        {
            ToggleMarkdownPreview();
        }
        Editors.FollowPreviews();
        if (focus)
        {
            Editors.ActiveGroup.FocusShown();
        }
    }

    /// <summary>The folder's plans under the Plans button, the newest first, each opening as a page.</summary>
    private void ShowChatPlans(Button under)
    {
        MenuFlyout menu = new();
        IReadOnlyList<string> plans = Folder is { } root ? ChatPlans.List(root) : [];
        foreach (string path in plans)
        {
            string title;
            try
            {
                title = ChatPlans.TitleOf(File.ReadAllText(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                title = Path.GetFileName(path);
            }
            MenuItem item = new() { Header = $"{title}  ·  {File.GetLastWriteTime(path):d MMM HH:mm}" };
            ToolTip.SetTip(item, PathNames.Shown(path, Folder));
            item.Click += (_, _) => OpenChatPlan(path);
            menu.Items.Add(item);
        }
        if (plans.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No plans yet: ask in the agent's Plan mode", IsEnabled = false });
        }
        menu.ShowAt(under);
    }

    /// <summary>Takes the files an edit names for review before the agent writes them (it names them first), and their
    /// text once the edit is done; on the agent's connection's thread. Which files, and whether the edit is done.</summary>
    private (string[] Paths, bool Done) Reviewed(ToolCall call)
    {
        if (call.Kind is not ("edit" or "delete" or "move"))
        {
            return ([], false);
        }
        string[] named = [.. call.Locations.Concat(call.Diffs.Select(diff => diff.Path)).Select(PathNames.Canonical).Distinct(StringComparer.Ordinal).Where(path => !ChatPlans.IsAgentDraft(path))];
        if (named.Length > 0 && chatEdits.TryAdd(call.Id, named))
        {
            foreach (string path in named)
            {
                Reading(() => chatReview.Before(path));
            }
        }
        bool done = call.Status is "completed" or "failed";
        string[] paths = chatEdits.TryGetValue(call.Id, out string[]? known) ? known : [];
        if (done)
        {
            foreach (string path in paths)
            {
                Reading(() => chatReview.After(path));
            }
        }
        return (paths, done && paths.Length > 0);

        static void Reading(Action read)
        {
            try
            {
                read();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Unreadable, the file is left out of the review rather than undone wrongly.
                IdeErrors.Write($"Chat review: {error.Message}");
            }
        }
    }

    /// <summary>What the agent changed in a file, as Passenger shows a diff: its text before the agent's first change
    /// beside its text now, read-only.</summary>
    private async Task ShowChatDiffAsync(ReviewedFile file)
    {
        string now = File.Exists(file.Path) ? await File.ReadAllTextAsync(file.Path) : string.Empty;
        TextDocument mine = new(now);
        TextSnapshot text = mine.Snapshot;
        VcsDiff diff = await BackgroundWork.Run(() => VcsDiff.Compare(file.BeforeText, text));
        string name = Path.GetFileName(file.Path);
        ShowDiff(new DiffContent($"{name}: before the agent and now", $"chat\n{file.Path}", file.Path, null,
            () => Diffs.ForFile(name, diff, mine, null, (file.IsNew ? "Before the agent (no file)" : "Before the agent", "Now"), file.Path, file.Path)), inTab: true);
    }

    /// <summary>Puts files back as they were before the agent changed them, as Local History reverts: an open file's text
    /// as one undoable step, saved; a closed one's bytes written as they were; one the agent made, to the Trash. A file
    /// changed since the agent's edit is left as it is, so nobody's later work is lost; the status bar says which.</summary>
    private async Task UndoChatAsync(ChatView view, IReadOnlyList<ReviewedFile> files)
    {
        List<string> refused = [];
        using IDisposable? activity = session.LocalHistory is { } history ? history.During("Undo the agent's changes") : null;
        foreach (ReviewedFile file in files)
        {
            string name = Path.GetFileName(file.Path);
            string? now = File.Exists(file.Path) ? await File.ReadAllTextAsync(file.Path) : null;
            if ((now ?? string.Empty) != file.AfterText || (now is null) != file.Deleted)
            {
                refused.Add($"{name} (changed since)");
                continue;
            }
            try
            {
                if (file.Before is not { } before)
                {
                    if (now is not null && !await DeleteAsync(file.Path, asked: true))
                    {
                        refused.Add(name);
                        continue;
                    }
                }
                else if (Editors.Find(file.Path) is { HoldsText: true } open)
                {
                    open.Revert(before);
                    if (open.IsModified && !Editors.Save(open))
                    {
                        refused.Add(name);
                        continue;
                    }
                }
                else
                {
                    await BackgroundWork.Run(() => FileText.Restore(file.Path, before));
                    if (Editors.Find(file.Path) is { } tab)
                    {
                        FollowDisk(tab);
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                refused.Add($"{name} ({error.Message})");
                continue;
            }
            chatReview.Done(file.Path);
        }
        ShowReview(view);
        status.Text = refused.Count == 0
            ? files.Count == 1 ? $"Undid the agent's changes to {Path.GetFileName(files[0].Path)}" : $"Undid the agent's changes to {files.Count} files"
            : $"Not undone: {string.Join(", ", refused)}";
    }

    /// <summary>Runs on the window's thread, held as background work until it has, so a test or a screenshot waits for it.</summary>
    private static void OnWindowThread(Action show)
    {
        IDisposable held = BackgroundWork.Scope();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                show();
            }
            finally
            {
                held.Dispose();
            }
        });
    }

    private void EndChatAgent()
    {
        chatTurn?.Cancel();
        if (chatAgent is { } agent)
        {
            chatAgent = null;
            IdeErrors.Observe(agent.DisposeAsync().AsTask());
        }
    }
}
