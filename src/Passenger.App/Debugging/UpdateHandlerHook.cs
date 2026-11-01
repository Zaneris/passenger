using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Passenger.App.State;
using Passenger.Core;
using Wire = Passenger.UpdateHandlers.Wire;

namespace Passenger.App.Debugging;

/// <summary>Passenger's end of its hook in a program it debugs with Hot Reload (Passenger.UpdateHandlers): a socket of the
/// launch's own the hook connects to as the program starts, told after each update the debugger applied, so the program's
/// MetadataUpdateHandlers run as the runtime's own Hot Reload agent runs them and a Blazor component renders the edit at
/// once. A handler that failed is said where the program's output is. Never waited on: the program may be paused, its
/// hook's thread with it, and runs the handlers as it goes on. Ended with its session.</summary>
public sealed class UpdateHandlerHook : IDisposable
{
    private static int made;
    private readonly Socket listener;
    private readonly CancellationTokenSource closing = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private NetworkStream? stream;

    private UpdateHandlerHook(string socket, Socket listener)
    {
        this.listener = listener;
        SocketPath = socket;
        // Not held: it waits as long as the program runs, which nothing drawn waits for.
        IdeErrors.Observe(BackgroundWork.Unheld(ServeAsync));
    }

    /// <summary>The hook beside the app, as its build carries it.</summary>
    public static string HookPath => Path.Combine(AppContext.BaseDirectory, "updatehandlers", "Passenger.UpdateHandlers.dll");

    /// <summary>Where it listens for the hook.</summary>
    public string SocketPath { get; }

    /// <summary>Says a line where the program's output is (a handler's failure); set by the session's tab.</summary>
    public Action<string, bool>? Says { get; set; }

    /// <summary>Whether the hook connected: the program started with it and runs its handlers when told.</summary>
    public bool Connected => stream is not null;

    /// <summary>The hook for a plan's program and the plan that loads it, or null where it cannot be loaded: its program on
    /// a .NET older than the hook's (8), its build without the hook, or no socket of its own to be made.</summary>
    public static (UpdateHandlerHook Hook, Run.LaunchPlan Plan)? For(Run.LaunchPlan plan)
    {
        if (plan.RunsOn is not { } framework || MajorOf(framework) < 8 || !File.Exists(HookPath))
        {
            return null;
        }
        string socket = $"{Environment.ProcessId}-{Interlocked.Increment(ref made)}.handlers";
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket = Path.Combine(SocketFolders.Own(StateStore.DefaultRoot), socket);
            // Only a dead Passenger that had this process's number leaves one by this name.
            File.Delete(socket);
            listener.Bind(new UnixDomainSocketEndPoint(socket));
            listener.Listen(1);
        }
        // ArgumentException: a path past what a Unix socket's address holds (104 bytes on macOS).
        catch (Exception error) when (error is SocketException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            listener.Dispose();
            IdeErrors.Write($"Hot Reload could not listen for {plan.Name}'s update handlers at {socket}, so they are not called: {error.Message}");
            return null;
        }
        Dictionary<string, string> environment = new(plan.Environment)
        {
            [Wire.SocketVariable] = socket,
            [RuntimeVariables.StartupHooks] = StartupHooks.First(plan.Environment, HookPath),
        };
        return (new UpdateHandlerHook(socket, listener), plan with { Environment = environment });
    }

    /// <summary>The major version of a .NET target framework ("net10.0", "net8.0-windows"), zero for any other.</summary>
    internal static int MajorOf(string framework) =>
        framework.StartsWith("net", StringComparison.OrdinalIgnoreCase) && framework.Length > 3 && char.IsAsciiDigit(framework[3])
        && int.TryParse(framework[3..].Split('.', '-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) && major >= 5 ? major : 0;

    /// <summary>Tells the hook the debugger applied an update, so the program runs its handlers as it next runs; nothing when
    /// the hook never connected (the program is on an older .NET than it said, or started without it).</summary>
    public void Updated()
    {
        UiThread.Check();
        if (stream is { } open)
        {
            IdeErrors.Observe(WriteAsync(open));
        }
    }

    private async Task WriteAsync(NetworkStream open)
    {
        await writing.WaitAsync(closing.Token);
        try
        {
            await open.WriteAsync(Encoding.UTF8.GetBytes(Wire.Updated + "\n"), closing.Token);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // The program ended as it was told: nothing is left to update.
        }
        finally
        {
            writing.Release();
        }
    }

    /// <summary>Takes the hook's one connection, then says each handler's failure it answers until the program goes.</summary>
    private async Task ServeAsync()
    {
        Socket client;
        try
        {
            client = await listener.AcceptAsync(closing.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            return;
        }
        finally
        {
            // The one program it was made for has come (or will not): nothing else may connect.
            listener.Dispose();
            Forget(SocketPath);
        }
        NetworkStream network = new(client, ownsSocket: true);
        stream = network;
        try
        {
            using StreamReader reader = new(network, new UTF8Encoding(false));
            while (await reader.ReadLineAsync(closing.Token) is { } line)
            {
                if (line.StartsWith(Wire.Failed, StringComparison.Ordinal))
                {
                    string said = $"Hot Reload: the program's update handler {line[Wire.Failed.Length..]}";
                    UiThread.Post(() => Says?.Invoke(said, true));
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // The program ended, or the session did.
        }
    }

    public void Dispose()
    {
        closing.Cancel();
        listener.Dispose();
        stream?.Dispose();
        Forget(SocketPath);
    }

    private static void Forget(string socket)
    {
        try
        {
            File.Delete(socket);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
