using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Passenger.TestHits;

/// <summary>Coverlet's counts in the test program, read for Passenger between tests. Each instrumented module's tracker
/// adds itself, as its first count is made, to the list a collector in the program keeps under Coverlet's key, which this
/// makes as the program starts (<see cref="Register"/>); what each tracker counted since the last take is one line naming,
/// for each tracker by the file its counts would be written to, the places counted (its array's indices, in order), whose
/// counts it starts again (<see cref="Take"/>): answered to each <see cref="Variables.Take"/> Passenger asks
/// (<see cref="Serve"/>), or told as each test ends after the test's full name (<see cref="Push"/>). Nothing is written as
/// the program ends: what it counted is Passenger's already.</summary>
internal static class Hits
{
    /// <summary>The key Coverlet's trackers find a collector's list of them under.</summary>
    private const string Registry = "Coverlet_RegisteredModuleTrackers";

    /// <summary>The list the trackers add themselves to, which <see cref="Take"/> reads: a collector's of Coverlet's where it
    /// made one first, else this one's own.</summary>
    private static List<EventHandler> trackers = [];

    /// <summary>The list each tracker adds itself to, made before any counts: Coverlet's own where a collector of its made it.</summary>
    public static void Register()
    {
        if (AppDomain.CurrentDomain.GetData(Registry) is List<EventHandler> made)
        {
            trackers = made;
        }
        else
        {
            AppDomain.CurrentDomain.SetData(Registry, trackers);
        }
    }

    /// <summary>Connects to Passenger and answers it on a thread of its own, for as long as the connection stands; a
    /// connection refused leaves the program to run as it would.</summary>
    public static void Serve(int port)
    {
        Register();
        if (Connected(port) is { } client)
        {
            Thread answering = new(() => Answer(client)) { IsBackground = true, Name = "Passenger test hits" };
            answering.Start();
        }
    }

    /// <summary>A connection to Passenger that each test's end is told on, as its full name and its counts; null where
    /// Passenger is not there.</summary>
    public static Pushing? Push(int port)
    {
        Register();
        return Connected(port) is { } client ? new Pushing(client) : null;
    }

    private static TcpClient? Connected(int port)
    {
        TcpClient client = new() { NoDelay = true };
        try
        {
            client.Connect(IPAddress.Loopback, port);
            return client;
        }
        catch (SocketException)
        {
            client.Dispose();
            return null;
        }
    }

    /// <summary>The connection each test's end is told on.</summary>
    public sealed class Pushing(TcpClient client) : IDisposable
    {
        private readonly StreamWriter writer = new(client.GetStream(), new UTF8Encoding(false)) { NewLine = "\n" };

        /// <summary>A test ended: its full name, then what it counted, each a line; a connection gone passes it over.</summary>
        public void Ended(string test)
        {
            try
            {
                writer.WriteLine(Quoted(new StringBuilder(), test).ToString());
                writer.WriteLine(Take());
                writer.Flush();
            }
            catch (IOException)
            {
                // Passenger went: the tests run on uncounted.
            }
        }

        public void Dispose()
        {
            try
            {
                writer.Dispose();
            }
            catch (IOException)
            {
                // Gone already.
            }
            client.Dispose();
        }
    }

    private static void Answer(TcpClient client)
    {
        using (client)
        using (StreamReader reader = new(client.GetStream(), new UTF8Encoding(false)))
        using (StreamWriter writer = new(client.GetStream(), new UTF8Encoding(false)) { NewLine = "\n" })
        {
            try
            {
                while (reader.ReadLine() is { } asked)
                {
                    if (asked == Variables.Take)
                    {
                        writer.WriteLine(Take());
                        writer.Flush();
                    }
                }
            }
            catch (IOException)
            {
                // Passenger went: the program runs on uncounted.
            }
        }
    }

    /// <summary>Each tracker's places counted since the last take, as JSON: {"file": [index, …], …}.</summary>
    public static string Take()
    {
        List<EventHandler> registered = trackers;
        EventHandler[] each;
        lock (registered)
        {
            each = [.. registered];
        }
        StringBuilder line = new("{");
        foreach (Type tracker in each.Select(t => t.Method.DeclaringType).OfType<Type>().Distinct())
        {
            if (tracker.GetField("HitsArray", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not int[] counts
                || tracker.GetField("HitsFilePath", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not string file)
            {
                continue;
            }
            tracker.GetField("FlushHitFile", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, false);
            if (line.Length > 1)
            {
                line.Append(',');
            }
            Quoted(line, file).Append(":[");
            bool first = true;
            for (int at = 0; at < counts.Length; at++)
            {
                if (Volatile.Read(ref counts[at]) != 0 && Interlocked.Exchange(ref counts[at], 0) != 0)
                {
                    line.Append(first ? string.Empty : ",").Append(at);
                    first = false;
                }
            }
            line.Append(']');
        }
        return line.Append('}').ToString();
    }

    private static StringBuilder Quoted(StringBuilder line, string text)
    {
        line.Append('"');
        foreach (char c in text)
        {
            if (c is '"' or '\\')
            {
                line.Append('\\').Append(c);
            }
            else if (c < ' ')
            {
                line.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                line.Append(c);
            }
        }
        return line.Append('"');
    }
}
