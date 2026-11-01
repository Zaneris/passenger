using System.Net.Sockets;
using System.Text;
using Passenger.Core;
using Passenger.UpdateHandlers;

/// <summary>Run before the program's Main, as DOTNET_STARTUP_HOOKS names this assembly, only in a program Passenger debugs
/// with Hot Reload: it takes itself and its variable out of what the program's children inherit, and connects to Passenger's
/// socket on a thread of its own. It never waits on Passenger in the program's way: the thread is a background one, and
/// a socket that cannot be reached leaves the program as it would run without the hook.</summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        string? socket = Environment.GetEnvironmentVariable(Wire.SocketVariable);
        OwnEnvironment.Set(Wire.SocketVariable, null);
        StartupHooks.TakeOut(typeof(StartupHook).Assembly.Location);
        if (socket is not { Length: > 0 })
        {
            return;
        }
        Thread listening = new(() => Serve(socket)) { IsBackground = true, Name = "Passenger update handlers" };
        listening.Start();
    }

    /// <summary>Calls the handlers after each update Passenger says was applied, answering what they came to, until the
    /// socket closes.</summary>
    private static void Serve(string path)
    {
        try
        {
            using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            using NetworkStream stream = new(socket, ownsSocket: false);
            using StreamReader reader = new(stream, new UTF8Encoding(false));
            using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            while (reader.ReadLine() is { } line)
            {
                if (line != Wire.Updated)
                {
                    continue;
                }
                foreach (string failure in Handlers.Run())
                {
                    writer.WriteLine(Wire.Failed + failure.ReplaceLineEndings(" "));
                }
                writer.WriteLine(Wire.Done);
            }
        }
        catch (Exception error) when (error is SocketException or IOException or ObjectDisposedException)
        {
            // Passenger gone or never reached: the program runs on as it would without the hook.
        }
    }
}
