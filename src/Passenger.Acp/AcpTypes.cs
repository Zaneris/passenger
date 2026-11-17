namespace Passenger.Acp;

// The agent's updates and questions as plain types, so an ACP change is met in AcpSession alone, never in the UI.

/// <summary>Who the agent is, from its answer to initialize.</summary>
public sealed record AgentInfo(string Name, string? Title, string? Version);

/// <summary>A mode the session can run in, such as Manual ("default"), Accept edits or Plan.</summary>
public sealed record SessionMode(string Id, string Name, string? Description);

/// <summary>A piece of the agent's reply, to add to the end of the current message.</summary>
public sealed record TextChunk(string Text);

/// <summary>The agent's whole plan: each one replaces the last.</summary>
public sealed record PlanUpdated(IReadOnlyList<PlanEntry> Entries);

/// <summary>One step of the plan. Priority is high, medium or low; status is pending, in_progress or completed.</summary>
public sealed record PlanEntry(string Content, string Priority, string Status);

/// <summary>A tool call as it stands now, every update so far merged in. Kind is read, edit, delete, move, search,
/// execute, think, fetch, switch_mode or other; status is pending, in_progress, completed or failed.</summary>
public sealed record ToolCall(
    string Id,
    string Title,
    string Kind,
    string Status,
    IReadOnlyList<FileDiff> Diffs,
    IReadOnlyList<string> Texts,
    IReadOnlyList<string> Locations);

/// <summary>A proposed or made change to one file. OldText is null when the agent sends none: a new file, or (as the
/// Claude adapter's Write does) a whole file replaced, so read the file to show what it was.</summary>
public sealed record FileDiff(string Path, string? OldText, string NewText);

/// <summary>How much of the agent's context the session has used, and of the person's plan where the agent says
/// (Claude Code's five-hour and weekly windows).</summary>
public sealed record UsageUpdated(long Used, long Size, IReadOnlyList<UsageWindow> Windows);

/// <summary>One of the plan's usage windows: its name ("five_hour", "seven_day"), how much is used from 0 to 1, and
/// when it starts afresh.</summary>
public sealed record UsageWindow(string Name, double Used, DateTimeOffset? Resets);

/// <summary>A setting of the session the agent offers (mode, model, effort…), its current value among its choices.</summary>
public sealed record ConfigOption(string Id, string Name, string? Description, string? Category, string Current, IReadOnlyList<ConfigChoice> Choices);

/// <summary>One choice of a setting: the value the agent takes, and its name and description for the person.</summary>
public sealed record ConfigChoice(string Value, string Name, string? Description);

/// <summary>A slash command or skill the agent offers ("code-review"), what it does, and what it takes after its name.</summary>
public sealed record AgentCommand(string Name, string? Description, string? Hint);

/// <summary>Who the agent is signed in as: its state ("authenticated") and its accounts, the active one first.</summary>
public sealed record AccountStatus(string State, IReadOnlyList<AgentAccount> Accounts);

/// <summary>An account the agent can use: its label ("Claude Pro"), email, and whether it is the one in use.</summary>
public sealed record AgentAccount(string Label, string? Email, bool Active);

/// <summary>A past conversation of the agent's in a folder, to resume.</summary>
public sealed record PastSession(string Id, string? Title, DateTimeOffset? Updated);

/// <summary>The agent asks before a risky action. The answer is one option's id, or null for cancelled.</summary>
public sealed record PermissionRequest(ToolCall ToolCall, IReadOnlyList<PermissionOption> Options);

/// <summary>Kind is allow_once, allow_always, reject_once or reject_always.</summary>
public sealed record PermissionOption(string Id, string Name, string Kind);

/// <summary>The agent asks to read a file, which may be unsaved text in the editor. Line is 1-based.</summary>
public sealed record FileReadRequest(string Path, int? Line, int? Limit);

/// <summary>The agent asks to write a file. Nothing is written unless the host writes it.</summary>
public sealed record FileWriteRequest(string Path, string Content);

/// <summary>Why a turn ended: end_turn, max_tokens, max_turn_requests, refusal or cancelled.</summary>
public sealed record TurnEnded(string StopReason);

/// <summary>Questions the agent asks the person before it goes on (Claude Code's AskUserQuestion), as ACP's form
/// elicitation carries them: what it says over them, and each question in turn.</summary>
public sealed record QuestionsRequest(string? ToolCallId, string Message, IReadOnlyList<AgentQuestion> Questions);

/// <summary>One question: the field its answer goes in, its short header, its words, whether several choices may be
/// picked, its choices (none for a question answered in words alone), and the field for an answer in the person's own
/// words, where it offers one.</summary>
public sealed record AgentQuestion(string Id, string? Header, string Text, bool Multiple, IReadOnlyList<QuestionChoice> Choices, string? OwnWordsId);

/// <summary>A choice: its label, which is what the answer records, and what it means.</summary>
public sealed record QuestionChoice(string Label, string? Description);

/// <summary>The person's answers, by each question's field: the labels picked, and an answer in their own words, by
/// its own field.</summary>
public sealed record QuestionsAnswer(IReadOnlyDictionary<string, IReadOnlyList<string>> Picked, IReadOnlyDictionary<string, string> OwnWords);

/// <summary>A way the agent offers to sign in, run in a terminal: its name, what it is, and the command and arguments
/// that run it (null where the agent names none).</summary>
public sealed record AuthMethod(string Id, string Name, string? Description, string? Command, IReadOnlyList<string> Args);