using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Passenger.TestHits;

/// <summary>Coverlet's counts in the test program, read for Passenger between tests. Each instrumented module's tracker
/// adds itself, as its first count is made, to the list a collector in the program keeps under Coverlet's key, which this
/// makes as the program starts; each <see cref="Variables.Take"/> is answered with one line naming, for each tracker by the
/// file its counts would be written to, the places counted since the last (its array's indices, in order), whose counts it
/// starts again. Nothing is written as the program ends: what it counted is Passenger's already.</summary>
internal static class Hits
{
    /// <summary>The key Coverlet's trackers find a collector's list of them under.</summary>
    private const string Registry = "Coverlet_RegisteredModuleTrackers";

    private static readonly List<EventHandler> Trackers = [];

    /// <summary>Connects to Passenger and answers it on a thread of its own, for as long as the connection stands; a
    /// connection refused leaves the program to run as it would.</summary>
    public static void Serve(int port)
    {
        AppDomain.CurrentDomain.SetData(Registry, Trackers);
        TcpClient client = new() { NoDelay = true };
        try
        {
            client.Connect(IPAddress.Loopback, port);
        }
        catch (SocketException)
        {
            client.Dispose();
            return;
        }
        Thread answering = new(() => Answer(client)) { IsBackground = true, Name = "Passenger test hits" };
        answering.Start();
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
    private static string Take()
    {
        EventHandler[] trackers;
        lock (Trackers)
        {
            trackers = [.. Trackers];
        }
        StringBuilder line = new("{");
        foreach (Type tracker in trackers.Select(t => t.Method.DeclaringType).OfType<Type>().Distinct())
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
