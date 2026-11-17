using System.Diagnostics;
using Passenger.Acp;
using Passenger.Core;

namespace Passenger.App.Chat;

/// <summary>A coding agent the Chat window talks to, and how it ends. The Claude adapter is started as a child of the
/// IDE's, its own login Claude Code's, never Passenger's; a test gives a stand-in's session in its place.</summary>
public sealed class ChatAgent(AcpSession session, Action end) : IAsyncDisposable
{
    /// <summary>The ACP adapter for Claude Code, run with npx as Zed and JetBrains run it, at a version of its own: npx
    /// given no version looks for a newer one at every start and installs it over the copy another start is running,
    /// which on Windows leaves a half-written claude.exe (spawn EFTYPE). Moved by hand once a newer one is tried.</summary>
    public const string ClaudeAdapter = "@agentclientprotocol/claude-agent-acp@0.107.3";

    /// <summary>The Claude adapter's mode that asks before every change ("Manual"); it starts in one that never asks.</summary>
    public const string ManualMode = "default";

    /// <summary>The Claude adapter's mode that applies edits without asking and asks before commands ("Accept edits"),
    /// Cursor's way: the panel takes each file for review first, to keep or undo.</summary>
    public const string ReviewMode = "acceptEdits";

    /// <summary>The Claude adapter's answer to a plan that builds it with edits applied as it goes, for review at the end,
    /// which it offers only where it has no auto mode.</summary>
    public const string PlanAcceptEdits = "exit-plan-accept-edits";

    /// <summary>The Claude adapter's answer to a plan that builds it asking before each edit (Manual), always offered.</summary>
    public const string PlanManual = "exit-plan-default";

    public AcpSession Session { get; } = session;

    /// <summary>The last line the agent wrote to its errors, for what says why it ended; null while it wrote none.</summary>
    public string? LastError { get; private set; }

    /// <summary>Starts the Claude adapter in <paramref name="folder"/>; what the start throws (no npx found), this throws.</summary>
    public static async Task<ChatAgent> StartClaudeAsync(string folder)
    {
        // npx is a .cmd script on Windows, which only cmd runs.
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? ChildProcesses.Command("cmd.exe", ["/d", "/c", "npx", "-y", "--prefer-offline", ClaudeAdapter], folder)
            : ChildProcesses.Command("npx", ["-y", "--prefer-offline", ClaudeAdapter], folder);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process process = await ChildProcesses.StartAsync(start);
        ChatAgent? made = null;
        UnawaitedWork.Observe(ChildProcesses.Hear(process, errors: line =>
        {
            IdeErrors.Write($"Chat agent: {line}");
            if (made is not null && line.Trim().Length > 0)
            {
                made.LastError = line.Trim();
            }
        }));
        AcpSession session = AcpSession.Over(ChildProcesses.OutputStream(process), ChildProcesses.Input(process).BaseStream);
        made = new ChatAgent(session, () =>
        {
            ChildProcesses.Kill(process);
            process.Dispose();
        });
        return made;
    }

    public async ValueTask DisposeAsync()
    {
        // Ended first, so the session's read finds its stream closed rather than waiting on it.
        end();
        await Session.DisposeAsync();
    }
}
