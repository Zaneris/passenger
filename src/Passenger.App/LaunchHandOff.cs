using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App;

/// <summary>A person's launch handed to the Passenger already running in its state, through that process's socket
/// (<see cref="Mcp.AppSocket"/>): its window on the folder takes the files and the caret, brought forward, or the running
/// Passenger opens the folder (<see cref="PassengerApp.TakeLaunch"/>); only where none takes it does the launch start a
/// Passenger of its own, the lines each way <see cref="HandedLaunch"/>'s.</summary>
public static partial class LaunchHandOff
{
    /// <summary>How long a running Passenger has to connect, take the launch and answer, as a busy one may take a while.</summary>
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(30);

    /// <summary>Whether a launch is one to hand to a running Passenger: a person's window, never <c>--mcp</c>,
    /// <c>--headless</c> or a screenshot, nor one asking for a run of its own (<c>--no-mcp</c>, a theme, look or scheme),
    /// nor the one an agent's <c>--mcp-attach</c> started after the running Passenger would not open its folder.</summary>
    public static bool HandsOff(LaunchOptions options) =>
        options is { Mcp: McpMode.None, Headless: false, Screenshot: null, ListenForAgents: true, Theme: null, Look: null, Scheme: null }
        && !Mcp.McpProxy.StartedForAgent;

    /// <summary>The launch the options name.</summary>
    public static HandedLaunch Of(LaunchOptions options) => new(options.Folder, options.Workspace, options.Files, options.Caret);

    /// <summary>Hands a launch to the Passengers running in this state, the newest first: true once one took it, false
    /// when none runs or none would, so the launch starts a Passenger of its own.</summary>
    public static async Task<bool> HandOffAsync(HandedLaunch launch, CancellationToken cancel)
    {
        if (Mcp.McpHost.FoundSocketFolder() is not { } sockets)
        {
            return false;
        }
        byte[] asked = Encoding.UTF8.GetBytes(launch.Message() + "\n");
        foreach (string path in Directory.EnumerateFiles(sockets, Mcp.AppSocket.Pattern).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                limit.CancelAfter(Waits.Of(AnswerLimit));
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), limit.Token);
                if (OperatingSystem.IsWindows() && Mcp.McpHost.ProcessOf(path) is int pid)
                {
                    // Started by the program in front (Godot's editor), the launch may pass on its leave to come forward.
                    AllowSetForegroundWindow(pid);
                }
                await using NetworkStream stream = new(socket, ownsSocket: false);
                await stream.WriteAsync(asked, limit.Token);
                using StreamReader reader = new(stream, Encoding.UTF8);
                if (await reader.ReadLineAsync(limit.Token) is not { } line)
                {
                    continue;
                }
                using JsonDocument answer = JsonDocument.Parse(line);
                if (JsonMembers.Text(answer.RootElement, "taken") is { } taken)
                {
                    IdeErrors.Write($"Launch: taken by the Passenger at {path}, {(taken.Length > 0 ? $"in its window on {taken}" : "in its window with no folder")}");
                    if (OperatingSystem.IsMacOS() && Mcp.McpHost.ProcessOf(path) is int running)
                    {
                        await ActivateAsync(running, cancel);
                    }
                    return true;
                }
                IdeErrors.Write($"Launch: the Passenger at {path} would not take it: {JsonMembers.Text(answer.RootElement, "refused") ?? line}");
            }
            catch (Exception error) when (error is SocketException or IOException or JsonException
                || (error is OperationCanceledException && !cancel.IsCancellationRequested))
            {
                // A Passenger that went, or one too busy to answer: the next is asked.
            }
        }
        return false;
    }

    /// <summary>How long <c>open</c> has to bring the running Passenger in front.</summary>
    private static readonly TimeSpan ActivateLimit = TimeSpan.FromSeconds(10);

    /// <summary>On macOS, brings the Passenger that took the launch in front through LaunchServices (<c>open -a</c> its
    /// bundle), asked by this short-lived process, which the program in front started (Godot's editor): macOS's cooperative
    /// activation may refuse the running Passenger's own, and honours one asked for by the front program's child. A
    /// Passenger in no bundle (a build's) is left to its own, and its window to ask for the person where that is refused.</summary>
    private static async Task ActivateAsync(int running, CancellationToken cancel)
    {
        string? executable = Libc.ExecutableOf(running);
        int end = executable?.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal) ?? -1;
        if (executable is null || end < 0)
        {
            IdeErrors.Write($"Launch: the Passenger that took it (process {running}) is in no app bundle, so it comes in front by itself");
            return;
        }
        string bundle = executable[..(end + ".app".Length)];
        try
        {
            ChildResult opened = await ChildProcesses.RunAsync(ChildProcesses.Command("/usr/bin/open", ["-a", bundle]), limit: Waits.Of(ActivateLimit), cancel: cancel);
            IdeErrors.Write(opened.Succeeded ? $"Launch: {bundle} brought in front through LaunchServices"
                : $"Launch: open -a {bundle} ended with exit code {opened.ExitCode}: {opened.Error.Trim()}");
        }
        catch (Exception error) when (error is TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            IdeErrors.Write($"Launch: {bundle} could not be brought in front through LaunchServices: {error.Message}");
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "AllowSetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
