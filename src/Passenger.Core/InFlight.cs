using System.Diagnostics;
using System.Globalization;

namespace Passenger.Core;

/// <summary>Work under way that no frame of the window's waits for and a test's quiet wait does: a language server's
/// request still unanswered, a state file's write still queued. Recorded only while <see cref="Watched"/>, so the IDE
/// itself pays nothing for it. Each piece is named, with when it began on Stopwatch's clock, for a wait that gives up to
/// say what was still busy and for how long. What is busy for as long as it runs, by design (a pull held open until the
/// next change, a program still running), is never entered here.</summary>
public static class InFlight
{
    private static readonly object Gate = new();
    private static readonly HashSet<Piece> Pieces = [];
    private static readonly IDisposable Nothing = new Piece(string.Empty, null, 0);
    private static int count;

    /// <summary>Whether work in flight is recorded: in a test run, from its start.</summary>
    public static bool Watched { get; set; }

    /// <summary>What a piece that is not to be waited for holds: nothing.</summary>
    public static IDisposable None => Nothing;

    /// <summary>How many pieces are in flight now.</summary>
    public static int Count => Volatile.Read(ref count);

    /// <summary>A piece of work in flight until the answer is disposed, once however often; <paramref name="of"/>, when
    /// given, says whose (a server's name), kept apart so neither string is made per piece.</summary>
    public static IDisposable Begin(string what, string? of = null)
    {
        if (!Watched)
        {
            return Nothing;
        }
        Piece piece = new(what, of, Stopwatch.GetTimestamp());
        lock (Gate)
        {
            Pieces.Add(piece);
            count = Pieces.Count;
        }
        return piece;
    }

    /// <summary>What is in flight now, the oldest first, each with when it began (a timestamp of Stopwatch's).</summary>
    public static IReadOnlyList<(string What, long Since)> Now()
    {
        lock (Gate)
        {
            return [.. Pieces.OrderBy(p => p.Since).Select(p => (p.Name, p.Since))];
        }
    }

    /// <summary>Lets go of everything recorded, for a test beginning: what an earlier one left in flight ends with what
    /// it started, and is never this test's to wait for.</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            Pieces.Clear();
            count = 0;
        }
    }

    /// <summary>A line for each piece of busy work, its age at <paramref name="now"/> (timestamps of Stopwatch's) and the
    /// time of day it began, as a wait that gives up says it.</summary>
    public static string Describe(IEnumerable<(string What, long Since)> busy, long now)
    {
        DateTime clock = DateTime.Now;
        return string.Join('\n', busy.Select(b =>
        {
            TimeSpan age = Stopwatch.GetElapsedTime(b.Since, now);
            return $"  {b.What}, for {Durations.Said(age)} (since {(clock - age).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)})";
        }));
    }

    private sealed class Piece(string what, string? of, long since) : IDisposable
    {
        public long Since { get; } = since;

        public string Name => of is null ? what : $"{what} ({of})";

        public void Dispose()
        {
            // The one given while nothing is watched, which no lock need guard.
            if (Since == 0)
            {
                return;
            }
            lock (Gate)
            {
                if (Pieces.Remove(this))
                {
                    count = Pieces.Count;
                }
            }
        }
    }
}
