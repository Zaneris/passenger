using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Inspector;

/// <summary>The failed bindings an inspector tells Passenger of, kept alike for Avalonia's and WPF's (this file is
/// linked into both, and touches neither framework): each distinct failure is sent the first time and its count again
/// as it repeats, a quarter second apart at most; past the most it lists, failures are only counted, their count said
/// (untold) as repeats are. Non-user code, so a stop below it shows the app's own code.</summary>
[DebuggerNonUserCode]
internal static class BindingFailures
{
    /// <summary>How long repeats of a failure gather before their count is sent.</summary>
    private static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(250);

    /// <summary>Distinct failures past this are not listed, only counted: an app failing without end says nothing new.</summary>
    private const int Most = 1000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Failure> Seen = [];
    private static Connection? connection;
    private static Timer? timer;
    private static bool gathering;

    /// <summary>The failures past the most listed, every time each failed, and as many as were last said.</summary>
    private static int untold;
    private static int untoldSaid;

    /// <summary>Failures are told over <paramref name="to"/> from now.</summary>
    public static void Start(Connection to)
    {
        connection = to;
        timer = ApartThreads.Timer(_ => Flush());
    }

    /// <summary>A failure as it happened, known by <paramref name="key"/>: one seen before counted again; a new one made by
    /// <paramref name="made"/> (given its id, and whether it is listed, which alone looks for the nearest names) and sent.
    /// Past the most listed it is only counted, and made only for a debugged app, whose every failure stops; null when it
    /// is neither listed nor stopped at. Made under the log's lock, from the thread that evaluated the binding.</summary>
    public static BindingFailure? Record(string key, bool debugged, Func<int, bool, BindingFailure> made)
    {
        BindingFailure? send = null;
        BindingFailure failure;
        lock (Gate)
        {
            if (Seen.TryGetValue(key, out Failure? known))
            {
                known.Count++;
                FlushLater();
                failure = known.First with { Count = known.Count };
            }
            else if (Seen.Count >= Most && !debugged)
            {
                untold++;
                FlushLater();
                return null;
            }
            else
            {
                bool listed = Seen.Count < Most;
                failure = made(Seen.Count + 1, listed);
                if (listed)
                {
                    Seen[key] = new Failure(failure) { Count = 1, Sent = 1 };
                    send = failure;
                }
                else
                {
                    untold++;
                    FlushLater();
                }
            }
        }
        if (send is not null)
        {
            connection?.Send(new Reply { Event = Wire.BindingFailed, Binding = send });
        }
        return failure;
    }

    /// <summary>The names nearest a misspelt one, at most three: those it differs from by letter case alone first, then by
    /// the fewest letters added, taken away, changed or swapped, up to a third of its length (one at least).</summary>
    public static IReadOnlyList<string> Nearest(string name, IEnumerable<string> names)
    {
        int limit = Math.Max(1, name.Length / 3);
        string folded = name.ToUpperInvariant();
        return [.. names
            .Where(n => !string.Equals(n, name, StringComparison.Ordinal))
            .Select(n => (Name: n, Apart: Spelling.Distance(folded, n.ToUpperInvariant()), Exactly: Spelling.Distance(name, n)))
            .Where(n => n.Apart <= limit)
            .OrderBy(n => n.Apart).ThenBy(n => n.Exactly).ThenBy(n => n.Name, StringComparer.Ordinal)
            .Take(3)
            .Select(n => n.Name)];
    }

    /// <summary>A flush of the counts a moment from now, unless one is due already; under <see cref="Gate"/>.</summary>
    private static void FlushLater()
    {
        if (!gathering)
        {
            gathering = true;
            timer?.Change(Gather, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Sends the count of each failure that repeated since it was last sent, and of those not listed.</summary>
    private static void Flush()
    {
        List<BindingFailure> repeated = [];
        int? unlisted = null;
        lock (Gate)
        {
            gathering = false;
            foreach (Failure failure in Seen.Values)
            {
                if (failure.Count > failure.Sent)
                {
                    failure.Sent = failure.Count;
                    repeated.Add(failure.First with { Count = failure.Count });
                }
            }
            if (untold > untoldSaid)
            {
                unlisted = untoldSaid = untold;
            }
        }
        foreach (BindingFailure failure in repeated)
        {
            connection?.Send(new Reply { Event = Wire.BindingFailed, Binding = failure });
        }
        if (unlisted is int count)
        {
            connection?.Untold(count);
        }
    }

    private sealed class Failure(BindingFailure first)
    {
        public BindingFailure First { get; } = first;

        public int Count { get; set; }

        public int Sent { get; set; }
    }
}
