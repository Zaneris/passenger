using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Mcp;

/// <summary>Passenger as a Model Context Protocol server, three ways. <c>passenger --mcp [FOLDER]</c> is an IDE with no
/// window, its tools on standard input and output, for an agent to start as its own. Each window of the person's listens
/// on a socket of its own (<see cref="AgentSocket"/>), and <c>passenger --mcp-attach [PID] [FOLDER]</c> is the agent's
/// server for its whole session (<see cref="McpProxy"/>), joining the window over the folder it works in at its first call,
/// so the agent drives the IDE the person is looking at, once Enable MCP Server or the person's Allow lets it in. Each
/// connection is a server of its own over the same tools.</summary>
public static partial class McpHost
{
    /// <summary>The IDE has no window, only the headless platform's (whose frames a screenshot takes). The process's, as
    /// the platform is: <c>--mcp</c> runs on the headless one alone.</summary>
    public static bool IsHeadless { get; private set; }

    /// <summary>The variable that, set to <c>off</c>, has an <c>--mcp</c> Passenger start no C# language server
    /// (<see cref="MainWindow.StartsLanguageServer"/>), for an agent that asks nothing of C#.</summary>
    [IdentifierHome]
    public const string LanguageServerVariable = "PASSENGER_MCP_LANGUAGE_SERVER";

    /// <summary>Whether <c>--mcp</c>'s IDE starts the C# language server: always, unless <see cref="LanguageServerVariable"/>
    /// says <c>off</c>; a value other than <c>off</c> or <c>on</c> is fatal as Passenger starts, as a malformed flag is.</summary>
    public static bool StartsLanguageServer => Environment.GetEnvironmentVariable(LanguageServerVariable) switch
    {
        null or "" or "on" => true,
        "off" => false,
        string other => throw new FormatException($"{LanguageServerVariable} takes off or on, not '{other}'"),
    };

    /// <summary>Where windows listen, one socket each, beside the process's <c>PID.open</c>: the state folder's socket folder
    /// (<see cref="SocketFolders"/>), where Passenger's other sockets are too.</summary>
    public static string SocketFolder => SocketFolders.For(StateStore.DefaultRoot);

    /// <summary>A state folder's agent sockets' folder (<see cref="SocketFolders.For"/>).</summary>
    public static string SocketFolderFor(string state) => SocketFolders.For(state);

    /// <summary>The socket folder made the user's alone, or why not (<see cref="SocketFolders.Own"/>).</summary>
    internal static string OwnSocketFolder() => SocketFolders.Own(StateStore.DefaultRoot);

    /// <summary>The socket folder when it is there and the user's own, else null: the windows an agent may join.</summary>
    internal static string? FoundSocketFolder() => SocketFolders.Found(StateStore.DefaultRoot);

    /// <summary>What an agent is told as it connects: how to begin and find its way, and what a person's window asks.</summary>
    internal const string Instructions =
        "Passenger is a C# IDE (with XAML, MSBuild files and the web languages). These tools act on its open workspace as its user would. " +
        "Lines and columns count from 1; relative paths are the open folder's. Parameters several tools share: configuration, a run " +
        "configuration (the chosen one by default); arguments, a command line in place of its own; waitSeconds, how long a call " +
        "waits for the program to stop, end or show its UI; session, a debugged program as debug_state's sessions name it (by " +
        "default the one paused, else the only one); resource, an Aspire resource (name or AppHostPath::name) or a compose " +
        "service of the run, as workspace lists them; symbol, in place of path, line and column, a C# symbol of the solution's " +
        "own code by name: Name, Type.Member or Namespace.Type, an overload as Member(string, int), or its documentation id " +
        "(M:Namespace.Type.Member(System.String)).\n\n" +
        "Begin with workspace: the folder and solution open, their projects, run configurations and open files (open opens another folder, " +
        "solution or file). Find your way by meaning rather than by text: find_symbol finds a type or member by name, outline lists a file's " +
        "types and members with their lines, and symbol_info, definition, usages and hierarchy follow the symbol at a line " +
        "and column (in C#, else the one the line declares) or named by symbol; find_in_files searches text. editor_text reads a file with its lines numbered; edit changes one as the editor does " +
        "(an open file the editor has not changed also takes what is written on disk); diagnostics checks it. A file written a moment " +
        "before is waited for (up to 10 s) until the language server has read it, so rename, refactor and code actions work on what was " +
        "written: there is no need to wait or try again.\n\n" +
        "Change code across the solution as the IDE's refactorings do rather than file by file: rename a symbol everywhere, and refactor for " +
        "Change Signature (every call updated), Move, Safe Delete, Extract Interface, Pull Members Up, Push Members Down, Introduce Field or " +
        "Parameter, Encapsulate Field, Optimize Imports and Code Cleanup; refactor with describe true answers what its dialog offers and the " +
        "options to send back, changing nothing. Each change is one step of the IDE's Undo.\n\n" +
        "build, run, tests_list and tests_run build (and publish), run and test. To debug: breakpoint_set, then debug_start (or debug_attach), debug_step, " +
        "debug_evaluate, debug_state and debug_continue, each waiting for the program to stop and saying where, with the top frame's " +
        "locals. An Avalonia or WPF app that run or debug_start starts is looked at and used as a person tests it: app_windows, app_tree, " +
        "app_screenshot, app_click, app_type, app_key, app_scroll, app_binding_errors. Also version control (vcs_status, " +
        "vcs_commit), NuGet (nuget_search, nuget_install), Problems and Local History (diagnostics with no path, local_history), the settings (settings, " +
        "settings_set), the container engine (containers, container_exec, container_action) and Passenger's own " +
        "window (the app_ tools with app ide, ide_action).\n\n" +
        "Attached to a person's Passenger (--mcp-attach), the first call joins the Passenger window open on the agent's folder (the folder " +
        "after --mcp-attach, else the agent's working folder). With none open, it has the running Passenger open that folder, or starts " +
        "Passenger on it, in a window the person sees, and waits for it. Unless the person has turned on Enable MCP Server, or let agents " +
        "into that folder always, the window asks them to let the agent in. The person is also asked before the agent first uses each tool " +
        "that changes something (edit, rename, build, run, debug_start, breakpoint_set, debug_evaluate, vcs_commit and the like; reading, " +
        "save_all, stop and stepping never ask), and before it opens another folder in their window. A call waits for each answer 2 " +
        "minutes at most, telling its progress meanwhile when asked to; one refused or left unanswered says so, and a later call asks " +
        "again. Passenger --mcp FOLDER is an agent's own IDE with no window, which asks nothing.";

    /// <summary>The server as an agent sees it, whichever way it is reached.</summary>
    internal static Implementation ServerInfo => new()
    {
        Name = "passenger",
        Title = "Passenger",
        Version = Product.Version,
    };

    /// <summary>The build's tools, PassengerTools' methods that are one: what every connection and <c>--mcp-attach</c>
    /// list.</summary>
    internal static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(PassengerTools).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    /// <summary>How a tool's answer is written as JSON: its text as written, quotes, angle brackets and the rest left as
    /// they are rather than as \u0022s, so code in an answer reads as code and costs an agent no more than it should.</summary>
    private static readonly JsonSerializerOptions AnswerJson = new(McpJsonUtilities.DefaultOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>How every connection and <c>--mcp-attach</c> create a tool of the build's: its answer as <see cref="AnswerJson"/>
    /// writes it, its schema the same in both.</summary>
    internal static McpServerToolCreateOptions ToolCreateOptions() => new() { SerializerOptions = AnswerJson };

    /// <summary>A tool as every connection and <c>--mcp-attach</c> list it: its schema without what says nothing (an
    /// optional parameter's null default and null type), and its title once, where the SDK writes it again in its
    /// annotations.</summary>
    internal static McpServerTool Lean(McpServerTool tool)
    {
        Tool shown = tool.ProtocolTool;
        if (shown.Annotations is { } annotations && annotations.Title == shown.Title)
        {
            annotations.Title = null;
        }
        if (JsonNode.Parse(shown.InputSchema.GetRawText()) is JsonObject schema)
        {
            Trim(schema);
            using JsonDocument lean = JsonDocument.Parse(schema.ToJsonString());
            shown.InputSchema = lean.RootElement.Clone();
        }
        return tool;

        static void Trim(JsonNode? node)
        {
            if (node is JsonArray items)
            {
                foreach (JsonNode? item in items)
                {
                    Trim(item);
                }
                return;
            }
            if (node is not JsonObject part)
            {
                return;
            }
            if (part.TryGetPropertyValue("default", out JsonNode? given) && given is null)
            {
                part.Remove("default");
            }
            if (part["type"] is JsonArray { Count: 2 } types && types.Select(t => t?.GetValue<string>()).ToList() is { } named
                && named.Contains("null") && named.Single(t => t != "null") is { } type)
            {
                part["type"] = type;
            }
            foreach (JsonNode? child in part.Select(p => p.Value).ToList())
            {
                Trim(child);
            }
        }
    }

    /// <summary>The <see cref="PassengerTools"/> a call reaches: its connection's, which <see cref="ConnectedTool"/> gives the
    /// call it serves.</summary>
    internal static readonly AsyncLocal<PassengerTools?> Serving = new();

    /// <summary>The build's tools, each made once a process (its schema read by reflection and trimmed, <see cref="Lean"/>),
    /// on whichever thread first asks, never the UI thread: every connection shares them, each call reaching its own
    /// connection's tools through <see cref="Serving"/>, and <c>--mcp-attach</c> lists them.</summary>
    internal static IReadOnlyList<McpServerTool> Tools => made.Value;

    private static readonly Lazy<McpServerTool[]> made = new(() => [.. ToolMethods().Select(method =>
        Lean(McpServerTool.Create(method, _ => Serving.Value ?? throw new InvalidOperationException("A Passenger tool ran outside an agent's call"), ToolCreateOptions())))]);

    /// <summary>The tools as one connection reaches them (<see cref="ConnectedTool"/>), made of <see cref="Tools"/>: call off
    /// the UI thread, which the first connection's would hold while every tool's schema is made.</summary>
    public static McpServerOptions Options(MainWindow window, AgentConnection connection) =>
        Options(Task.FromResult(new PassengerTools(window, connection)), window, connection);

    /// <summary>--mcp's server, its tools' list answered at once and each call waiting for the IDE it acts on.</summary>
    private static McpServerOptions Options(Task<PassengerTools> ready, AgentConnection connection) => Options(ready, null, connection);

    private static McpServerOptions Options(Task<PassengerTools> tools, MainWindow? window, AgentConnection connection)
    {
        McpServerPrimitiveCollection<McpServerTool> collection = [];
        foreach (McpServerTool tool in Tools)
        {
            collection.Add(new ConnectedTool(tool, window, connection, tools));
        }
        return new McpServerOptions
        {
            ServerInfo = ServerInfo,
            ServerInstructions = Instructions,
            ToolCollection = collection,
        };
    }

    /// <summary>How long an MCP mode the system asked to end takes to end as the end of its input ends it, before it goes
    /// however far it got.</summary>
    internal static readonly TimeSpan EndLimit = TimeSpan.FromSeconds(3);

    /// <summary>Has the system's request to end the process (SIGTERM, which a client may send just after closing the
    /// input) end it as the end of its input does, <paramref name="end"/>, so its state is written and what it started
    /// ends, the process going after <see cref="EndLimit"/> at the latest. Null where the system has no such
    /// signal.</summary>
    internal static System.Runtime.InteropServices.PosixSignalRegistration? EndOnSignal(Action end)
    {
        try
        {
            return System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                end();
                IdeErrors.Observe(Task.Delay(EndLimit).ContinueWith(_ => Environment.Exit(143), TaskScheduler.Default));
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary><c>--mcp</c>: the IDE with no window, its tools on standard input and output until the agent closes them
    /// or the system asks it to end. Its server answers before the IDE is up (initialize and the tools' list need none of
    /// it), so an agent asking as it connects finds the tools at once; each call waits for the IDE.</summary>
    public static int RunStdio(LaunchOptions launch)
    {
        IsHeadless = true;
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        // Standard output is the protocol's alone, and standard error the agent may never read: the console goes to the log.
        IdeLog.TakeConsole();
        StartHostAhead(launch);

        using CancellationTokenSource done = new();
        TaskCompletionSource<PassengerTools> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The agent's own IDE: nobody else is at it to ask.
        AgentConnection connection = new(done.Token, attached: false);
        // Not held, and made on a worker while the window is: it serves as long as the agent stays, each tool holding what it starts.
        IdeErrors.Observe(Task.Run(async () =>
        {
            try
            {
                McpServer server = McpServer.Create(new PeerTransport(new StreamServerTransport(input, output, "passenger")), Options(ready.Task, connection));
                await server.RunAsync(done.Token);
            }
            finally
            {
                ended.TrySetResult();
            }
        }));
        using System.Runtime.InteropServices.PosixSignalRegistration? terminated = EndOnSignal(() => ended.TrySetResult());

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterSetup(builder => Program.ApplyTheme(builder.Instance!, launch))
            .SetupWithoutStarting();

        // The agent's own IDE, which no other agent joins.
        MainWindow window = new() { Width = 1600, Height = 1000, AgentsAllowed = false, AgentsOwn = true, StartsLanguageServer = StartsLanguageServer };
        window.SetRenderScaling(2);
        window.Show();
        window.Start(launch);
        ready.SetResult(new PassengerTools(window, connection));
        // Posted once the dispatcher is this thread's: the agent may have gone before it was.
        IdeErrors.Observe(ended.Task.ContinueWith(_ => UiThread.Post(done.Cancel), TaskScheduler.Default));
        Dispatcher.UIThread.MainLoop(done.Token);
        // What the agent left running (a program, a debugger) ends with the windows, as it does with the person's.
        PassengerApp.Quit();
        PassengerApp.End();
        return 0;
    }

    /// <summary>Starts the project host for the folder the command line names, on a worker, while the window is made, which
    /// the folder's first evaluation would wait for; one that cannot start says why when the folder asks for it.</summary>
    private static void StartHostAhead(LaunchOptions launch)
    {
        if ((launch.Workspace ?? launch.Folder) is not { } named)
        {
            return;
        }
        string full = PathNames.Canonical(named);
        string folder = File.Exists(full) && WorkspaceModel.Opens(full) ? Path.GetDirectoryName(full)! : full;
        if (Directory.Exists(folder))
        {
            IdeErrors.Observe(Task.Run(async () =>
            {
                try
                {
                    await ProjectHosts.ForAsync(folder);
                }
                catch (ProjectHostFailed)
                {
                    // Said when the folder asks for its host.
                }
            }));
        }
    }

    /// <summary><c>--headless [PATH]</c>: a person's window on the headless platform, which <c>--mcp-attach</c> starts
    /// in place of the installed app under <see cref="McpProxy.StartVariable"/>: it listens for agents and asks before
    /// letting them in, as any window does, until its input ends.</summary>
    public static int RunHeadless(LaunchOptions launch)
    {
        IsHeadless = true;
        Stream input = Console.OpenStandardInput();
        // Its starter reads nothing of its output or error: the console goes to the log.
        IdeLog.TakeConsole();
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterSetup(builder => Program.ApplyTheme(builder.Instance!, launch))
            .SetupWithoutStarting();

        MainWindow window = new() { Width = 1600, Height = 1000 };
        window.SetRenderScaling(2);
        window.Show();
        window.Start(launch);

        using CancellationTokenSource done = new();
        using System.Runtime.InteropServices.PosixSignalRegistration? terminated = EndOnSignal(() => UiThread.Post(done.Cancel));
        // Not held: it waits for the end of the input, which its starter's own end closes.
        IdeErrors.Observe(Task.Run(() =>
        {
            try
            {
                input.CopyTo(Stream.Null);
            }
            finally
            {
                UiThread.Post(done.Cancel);
            }
        }));
        Dispatcher.UIThread.MainLoop(done.Token);
        PassengerApp.Quit();
        PassengerApp.End();
        return 0;
    }

    /// <summary><c>--mcp-attach [PID] [FOLDER]</c>: the agent's server, joining the window over its folder at its first
    /// call (<see cref="McpProxy"/>).</summary>
    public static int Attach(int? processId, string? folder) => McpProxy.Run(processId, folder);

    /// <summary>The file beside a window's socket that names the folder open in it.</summary>
    internal static string FolderFile(string socket) => Path.ChangeExtension(socket, ".folder");

    /// <summary>Takes away the sockets (and their folder files) of IDEs that are no longer running, passing over a folder
    /// file a window is writing (<see cref="AtomicFile"/>'s temporary file, named for no process).</summary>
    internal static void RemoveStale()
    {
        if (FoundSocketFolder() is not { } sockets)
        {
            return;
        }
        foreach (string path in Directory.EnumerateFiles(sockets))
        {
            if (!path.EndsWith(AtomicFile.Temporary, StringComparison.Ordinal) && ProcessOf(path) is int pid && !Running(pid))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        static bool Running(int pid)
        {
            try
            {
                using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>The process a socket is named for: <c>PID-WINDOW.sock</c>, or an older build's
    /// <c>PID.sock</c>.</summary>
    internal static int? ProcessOf(string path) =>
        int.TryParse(Path.GetFileNameWithoutExtension(path).Split('-')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid)
            ? pid
            : null;

    internal static string? WindowFolder(string socket)
    {
        try
        {
            return File.ReadAllText(FolderFile(socket)) is { Length: > 0 } folder ? folder : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>How closely a window's folder holds a place: its length when it does, so the innermost wins, else -1.</summary>
    internal static int Holding(string? folder, string here)
    {
        if (folder is null)
        {
            return -1;
        }
        return PathNames.Within(here, folder) ? folder.Length : -1;
    }
}
