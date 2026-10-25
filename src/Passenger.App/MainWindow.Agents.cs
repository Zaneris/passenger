using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Agents in the window: its own socket, open to them from its start (<c>--no-mcp</c> aside), on which an
/// agent works once let in: by Enable MCP Server (turning it off shuts them out), by the folder's Always, or by the
/// person's Allow when its first call comes; the person's leave before an agent's first use of a tool that changes
/// something, and before it opens another folder here; and the status bar's word on who is on the window and what they
/// run there.</summary>
public partial class MainWindow
{
    private readonly List<string> agentWork = [];

    /// <summary>The questions agents' calls wait on, as the status bar says them, while the person is asked.</summary>
    private readonly List<string> agentQuestions = [];

    /// <summary>The questions' dialogs, as <see cref="agentQuestions"/> words them, which a click on the status bar's words
    /// brings forward.</summary>
    private readonly List<Window> agentDialogs = [];

    private Mcp.AgentSocket? agentSocket;
    private int agents;

    /// <summary>Whether Enable MCP Server was on when last followed, so turning it off shuts the agents out.</summary>
    private bool agentsLetIn;

    /// <summary>Whether this window may let agents in at all; <c>--no-mcp</c>, <c>--mcp</c>'s own window and a screenshot's
    /// without an agent say never.</summary>
    public bool AgentsAllowed { get; init; } = true;

    /// <summary>How long an agent's call waits for the person to answer a question it asks (whether it may work in the
    /// window, use a tool, open a folder), after which the call says nobody answered and a later one asks again.</summary>
    public TimeSpan AgentAnswerLimit { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The socket agents reach this window through, else null.</summary>
    public string? AgentSocket => agentSocket?.Path;

    /// <summary>Agents let into the window and still connected.</summary>
    public int Agents
    {
        get => agents;
        set
        {
            agents = value;
            ShowAgents();
        }
    }

    /// <summary>What the status bar says of agents on the window, or null while it says nothing.</summary>
    public string? AgentStatus => AgentButton.IsVisible ? AgentText.Text : null;

    /// <summary>Whether nobody is at the window to ask: a screenshot's that runs an agent (<c>--agent</c>), which lets in
    /// its agent unasked and lets it use every tool. Set before its agent starts.</summary>
    public bool AgentsUnattended { get; private set; }

    /// <summary>Whether an agent is let in without asking: Enable MCP Server, the folder's Always, or nobody at the window.</summary>
    public bool LetsAgentsIn => AgentsUnattended || settings.McpServer || AlwaysLetsAgentsIn;

    /// <summary>Whether the open folder lets agents in always (Always for this folder): it is among Settings' folders agents
    /// join unasked.</summary>
    public bool AlwaysLetsAgentsIn => Folder is { } folder && LetsIn(settings, folder);

    private static bool LetsIn(Settings.AppSettings with, string folder) => with.AgentFolders.Any(f => PathNames.Same(f, folder));

    private void AddAgents()
    {
        Opened += (_, _) => ListenForAgents(settings.McpServer);
        Closed += (_, _) =>
        {
            AgentsUnattended = false;
            agentSocket?.Dispose();
            agentSocket = null;
            if (!PassengerApp.Windows.Any(w => w != this && w.AgentSocket is not null))
            {
                Mcp.AppSocket.Close();
            }
        };
    }

    /// <summary>What a screenshot's agent finds this Passenger by: its executable, and its process's id to attach to.</summary>
    [IdentifierHome]
    public const string AgentExecutableVariable = "PASSENGER";
    [IdentifierHome]
    public const string AgentPidVariable = "PASSENGER_PID";

    /// <summary>A screenshot's agent (<c>--agent</c>): the window's socket opened to it, and its command line run in a tab
    /// of the Terminal window, which it joins the window from as any agent does, with <c>$PASSENGER --mcp-attach
    /// $PASSENGER_PID</c>. Throws saying why agents cannot reach the window.</summary>
    internal Task<Run.TerminalRun> RunAgent(IReadOnlyList<string> command)
    {
        AgentsUnattended = true;
        if (AgentsAllowed && agentSocket is null)
        {
            agentSocket = Mcp.AgentSocket.Open(this);
            agentSocket.Describe(Folder);
        }
        System.Diagnostics.ProcessStartInfo start = Core.ChildProcesses.Command(command[0], command.Skip(1), Folder ?? Environment.CurrentDirectory);
        start.Environment[AgentExecutableVariable] = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Passenger.exe" : "Passenger");
        start.Environment[AgentPidVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ShowInTerminal("Agent", start);
    }

    /// <summary>Opens the window's socket to agents once it is shown, with the process's for agents asking it to open a
    /// folder; Enable MCP Server turned off ends the connections of the agents on it, who are asked at their next call.</summary>
    private void ListenForAgents(bool serverOn)
    {
        bool wasOn = agentsLetIn;
        agentsLetIn = serverOn;
        if (AgentsAllowed && agentSocket is null && IsVisible)
        {
            try
            {
                agentSocket = Mcp.AgentSocket.Open(this);
                agentSocket.Describe(Folder);
                Mcp.AppSocket.Listen();
            }
            catch (Exception error) when (error is IOException or SocketException or ArgumentException or UnauthorizedAccessException)
            {
                status.Text = $"Agents cannot reach this window: {error.Message}";
                // Logged too: an agent waiting for the window learns why only from the log (a socket folder another user made first).
                IdeErrors.Write($"Agents: the window on {Folder ?? "no folder"} cannot listen for agents: {error.Message}");
            }
        }
        if (wasOn && !serverOn && !AgentsUnattended)
        {
            agentSocket?.ShutOut();
        }
    }

    /// <summary>The window's folder taken off the folders agents join unasked (Settings' Remove) shuts out the agents on it
    /// while Enable MCP Server is off: each is asked again at its next call.</summary>
    private void FollowAgentFolders(Settings.AppSettings before, Settings.AppSettings next)
    {
        if (Folder is { } folder && !next.McpServer && !AgentsUnattended && LetsIn(before, folder) && !LetsIn(next, folder))
        {
            agentSocket?.ShutOut();
        }
    }

    /// <summary>The window leaves its folder for <paramref name="folder"/> (none when it closes it): the agents let in on the
    /// folder it leaves are shut out, each asked again at its next call, unless the window lets agents in unasked where it
    /// goes; the agent whose own open it is (<paramref name="opener"/>) stays. Those that stay are asked again before their
    /// first use of each tool there.</summary>
    private void AgentsGoTo(string? folder, Mcp.AgentConnection? opener) =>
        agentSocket?.Moving(AgentsUnattended || settings.McpServer || (folder is not null && LetsIn(settings, folder)), opener);

    /// <summary>Asks the person whether an agent may work in the window, its calls waiting: Allow, with Always for this
    /// folder kept with the folder, Don't allow, or no answer within <see cref="AgentAnswerLimit"/> or before
    /// <paramref name="takenAway"/> (no call waits any more, or the agent went), which takes the question away.</summary>
    internal async Task<Admission> AskToLetInAsync(string agent, CancellationToken takenAway)
    {
        UiThread.Check();
        string? folder = Folder;
        AgentAdmissionDialog dialog = new(agent, folder);
        IdeErrors.Write($"Agents: {agent} asks to work in the window on {folder ?? "no folder"}");
        Admission said = await AskForAgentAsync(dialog, Admission.NotAllowed, $"{agent} waits for you: let it in?", takenAway) ?? Admission.Unanswered;
        if (said == Admission.Allowed && dialog.Always && folder is not null && !LetsIn(settings, folder))
        {
            // Settings' list, which every window over the folder follows and the person's settings keep.
            ApplySettings(settings with { AgentFolders = [.. settings.AgentFolders, folder] });
        }
        IdeErrors.Write($"Agents: {agent} {(said switch
        {
            Admission.Allowed => dialog.Always ? "let in always" : "let in",
            Admission.NotAllowed => "not let in",
            _ => "not answered",
        })} on {folder ?? "no folder"}");
        return said;
    }

    /// <summary>Asks the person whether an agent may open another folder or solution in the window, replacing the one
    /// open: no answer within <see cref="AgentAnswerLimit"/>, or the agent giving up on the call, takes the question away.</summary>
    internal async Task<Admission> AllowAgentOpenAsync(string agent, string opening, string replacing, CancellationToken cancel)
    {
        UiThread.Check();
        string name = Path.GetFileName(opening.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ConfirmDialog dialog = new($"Open {name} here?", $"{agent} wants to open {opening} here, replacing {replacing}.", AgentAdmissionDialog.Allow, AgentAdmissionDialog.DontAllow);
        return Answered(await AskForAgentAsync(dialog, false, $"{agent} waits for you: open {name} here?", cancel));
    }

    /// <summary>Asks the person whether an agent may use a tool that changes something, showing what it asked with: no
    /// answer within <see cref="AgentAnswerLimit"/>, or every call waiting giving up, takes the question away.</summary>
    internal async Task<Admission> AllowAgentAsync(string agent, string tool, string title, IReadOnlyList<(string Name, string Value)> arguments, CancellationToken cancel)
    {
        UiThread.Check();
        string what = char.ToLowerInvariant(title[0]) + title[1..];
        ConfirmDialog dialog = new($"Allow {tool}?",
            $"{agent}, connected through Passenger's MCP server, wants to {what} (its {tool} tool). Once allowed, it may use that tool again without asking in this folder until it disconnects.",
            "Allow", "Deny", arguments.Count == 0 ? null : AgentArguments(arguments));
        IdeErrors.Write($"Agents: {agent} asks to use {tool} in the window on {Folder ?? "no folder"}");
        Admission said = Answered(await AskForAgentAsync(dialog, false, $"{agent} waits for you: allow {tool}?", cancel));
        IdeErrors.Write($"Agents: {agent} {(said switch
        {
            Admission.Allowed => "allowed",
            Admission.NotAllowed => "not allowed",
            _ => $"not answered within {AgentConnection.Limit(this)} whether it may use",
        })} {tool} on {Folder ?? "no folder"}");
        return said;
    }

    private static Admission Answered(bool? said) => said switch
    {
        true => Admission.Allowed,
        false => Admission.NotAllowed,
        null => Admission.Unanswered,
    };

    /// <summary>A question an agent's call waits on, put to the person: the window brought forward (the system asked for
    /// their attention where it cannot be) and the status bar saying who waits on what, until they answer, closing the
    /// dialog answers <paramref name="no"/>, and <paramref name="cancel"/> or <see cref="AgentAnswerLimit"/> takes it
    /// away unanswered (null).</summary>
    private async Task<T?> AskForAgentAsync<T>(Window dialog, T no, string waiting, CancellationToken cancel) where T : struct
    {
        UiThread.Check();
        bool takenAway = false;
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(AgentAnswerLimit);
        using CancellationTokenRegistration closing = limit.Token.Register(() => UiThread.Post(() =>
        {
            // An answer that came first stands.
            if (dialog.IsVisible)
            {
                takenAway = true;
                dialog.Close();
            }
        }));
        agentQuestions.Add(waiting);
        agentDialogs.Add(dialog);
        ShowAgents();
        // The agent's call waits on the person, who may be in another window or program.
        PassengerApp.BringForward(this);
        using IDisposable attention = Attention.Ask(this);
        try
        {
            T? said = await dialog.ShowDialog<T?>(this);
            return takenAway ? null : said ?? no;
        }
        finally
        {
            agentQuestions.Remove(waiting);
            agentDialogs.Remove(dialog);
            ShowAgents();
        }
    }

    /// <summary>Why an agent's input through the inspector (app_click, app_key, app_type and app_scroll on app ide) may not
    /// reach one of Passenger's own top levels, else null: a question Passenger asks the person on an agent's behalf, or
    /// Settings, where the person alone says what agents may do; a popup or dialog of either is it. The process's, as the
    /// inspector loaded into it is (<see cref="LiveTree.LiveApp.Own"/>).</summary>
    internal static string? KeptFromAgents(object reached)
    {
        UiThread.Check();
        for (TopLevel? at = reached as TopLevel; at is not null; at = at is Avalonia.Controls.Primitives.PopupRoot { ParentTopLevel: { } parent } ? parent : (at as Window)?.Owner)
        {
            if (at is Settings.SettingsDialog)
            {
                return "Settings is the person's alone: no agent's input reaches it (settings_set changes what an agent may)";
            }
            if (at is Window asking && PassengerApp.Windows.Any(w => w.agentDialogs.Contains(asking)))
            {
                return $"\"{asking.Title}\" is Passenger's question to the person on an agent's behalf: only they answer it, never an agent's input";
            }
        }
        return null;
    }

    internal void AgentWorkStarted(string title)
    {
        agentWork.Add(title);
        ShowAgents();
    }

    internal void AgentWorkEnded(string title)
    {
        agentWork.Remove(title);
        ShowAgents();
    }

    /// <summary>The status bar's word on agents: a question one waits on first, which the person may not have seen, then
    /// what one runs, then how many are on the window.</summary>
    private void ShowAgents()
    {
        bool asking = agentQuestions.Count > 0;
        AgentButton.IsVisible = agents > 0 || agentWork.Count > 0 || asking;
        AgentButton.Classes.Set("asking", asking);
        AgentIcon.IsVisible = asking;
        AgentText.Text = asking ? agentQuestions[^1]
            : agentWork.Count > 0 ? $"Agent: {agentWork[^1]}"
            : agents == 1 ? "Agent connected"
            : $"{agents} agents connected";
        ToolTip.SetTip(AgentButton, asking
            ? "An agent's call waits for your answer to the question this window asks: click to see it. Unanswered, the agent is told so once the time is up"
            : "An agent is using this window through Passenger's MCP server");
    }

    /// <summary>The status bar's words on agents, clicked: the question one waits on comes forward.</summary>
    private void OnAgentClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (agentDialogs.LastOrDefault() is { } dialog)
        {
            dialog.Activate();
        }
    }

    private static Control AgentArguments(IReadOnlyList<(string Name, string Value)> arguments)
    {
        const int Longest = 400;
        Border frame = CodeBlock.Of(string.Join('\n', arguments.Select(a => $"{a.Name}: {(a.Value.Length > Longest ? a.Value[..Longest] + "…" : a.Value)}")));
        frame.MaxHeight = 200;
        Control text = frame.Child!;
        frame.Child = null;
        frame.Child = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        return frame;
    }
}
