using Avalonia.Threading;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>An open file's share of a language server, C#'s (<see cref="CSharpDocument"/>) or a web language's
/// (<see cref="Web.WebDocument"/>): its document at the server, null until the server has it, and the diagnostics last
/// reported, on the server's lines of the text as it stands. What the editor's commands ask (<see cref="CodeAssist"/>).</summary>
public interface IServerDocument
{
    LspDocument? Lsp { get; }

    IReadOnlyList<Diagnostic> Diagnostics { get; }
}

/// <summary>How a server's diagnostics become the editor's markers, and are carried with the text between reports.</summary>
internal static class DiagnosticMarks
{
    /// <summary>What a diagnostic draws as: errors, warnings and information squiggled; code the
    /// compiler calls unnecessary faded; the server's quieter hints left off the page.</summary>
    public static Marker? Marker(Diagnostic diagnostic, TextRange range)
    {
        bool unnecessary = diagnostic.Tags?.Contains(DiagnosticTag.Unnecessary) == true;
        MarkerKind? kind = Kind(diagnostic.Severity) is MarkerKind.Error || !unnecessary ? Kind(diagnostic.Severity) : MarkerKind.Unnecessary;
        if (kind is not { } k)
        {
            return null;
        }
        return Editor.Marker.Coded(range, k, diagnostic.Code, diagnostic.Message);
    }

    /// <summary>What a severity draws as: errors, warnings and information squiggled, the server's quieter hints left off
    /// the page.</summary>
    public static MarkerKind? Kind(DiagnosticSeverity? severity) => severity switch
    {
        DiagnosticSeverity.Error => MarkerKind.Error,
        DiagnosticSeverity.Warning => MarkerKind.Warning,
        DiagnosticSeverity.Information => MarkerKind.Info,
        _ => null,
    };

    /// <summary>A range of the server's as offsets in the text it was given for, never ending before it starts.</summary>
    public static TextRange Offsets(ServerLines lines, LspRange range)
    {
        int start = lines.OffsetOf(range.Start);
        return TextRange.FromBounds(start, Math.Max(start, lines.OffsetOf(range.End)));
    }

    /// <summary>Each diagnostic's range in the text, as <see cref="Offsets(ServerLines, LspRange)"/> gives it, all placed in
    /// one pass over their lines however many there are.</summary>
    public static TextRange[] Offsets(ServerLines lines, IReadOnlyList<Diagnostic> diagnostics)
    {
        LspPosition[] ends = new LspPosition[diagnostics.Count * 2];
        for (int i = 0; i < diagnostics.Count; i++)
        {
            ends[2 * i] = diagnostics[i].Range.Start;
            ends[(2 * i) + 1] = diagnostics[i].Range.End;
        }
        int[] offsets = lines.OffsetsOf(ends);
        TextRange[] ranges = new TextRange[diagnostics.Count];
        for (int i = 0; i < ranges.Length; i++)
        {
            int start = offsets[2 * i];
            ranges[i] = TextRange.FromBounds(start, Math.Max(start, offsets[(2 * i) + 1]));
        }
        return ranges;
    }

    /// <summary>The diagnostics reported, each on the server's lines where its range has been carried to.</summary>
    public static Diagnostic[] Placed(TextSnapshot text, IReadOnlyList<Diagnostic> reported, IReadOnlyList<TextRange> ranges)
    {
        ServerLines lines = ServerLines.Of(text);
        int length = lines.Text.Length;
        Diagnostic[] moved = new Diagnostic[reported.Count];
        for (int i = 0; i < reported.Count; i++)
        {
            Diagnostic diagnostic = reported[i];
            TextRange now = ranges[i];
            moved[i] = new Diagnostic
            {
                Range = new LspRange(lines.PositionOf(Math.Min(now.Start, length)), lines.PositionOf(Math.Min(now.End, length))),
                Severity = diagnostic.Severity,
                Code = diagnostic.Code,
                CodeIsNumber = diagnostic.CodeIsNumber,
                CodeDescription = diagnostic.CodeDescription,
                Source = diagnostic.Source,
                Message = diagnostic.Message,
                Tags = diagnostic.Tags,
                Data = diagnostic.Data,
            };
        }
        return moved;
    }
}

/// <summary>An open file's diagnostics from one server (C#'s, a web language's, ESLint's): pulled after a pause, each
/// pull ending the one before it and held as background work, or taken as pushed, and carried with the text between
/// reports by anchored ranges, so a squiggle never drifts off its code. What each asks of its server is its own; a pull
/// holds the file's checks for <c>checker</c>, as an agent is told of it, unless that is null.</summary>
internal sealed class ServerDiagnostics(OpenDocument document, string? checker)
{
    private CancellationTokenSource? pulling;

    /// <summary>The ask under way while it waits out its delay; null once it asks, or when it had none.</summary>
    private CancellationTokenSource? pausing;

    /// <summary>Whether the ask under way still waits out its delay (typing's pause), after which it asks for the text as it
    /// stands then.</summary>
    public bool Pausing => pausing is { IsCancellationRequested: false } && pausing == pulling;

    /// <summary>The diagnostics as reported, and each one's range in the text as it stands, which the text carries.</summary>
    private Diagnostic[] found = [];
    private AnchoredRange[] ranges = [];

    /// <summary>How many of the diagnostics, at the end of <see cref="found"/>, a project-wide pull reported
    /// (<see cref="DiagnosticPull.ProjectWide"/>), which answers after the file's own.</summary>
    private int late;

    /// <summary>The markers last given, in the order of their starts, as they were given (the array handed out, never changed
    /// since), and the range that carries each through every edit.</summary>
    private Marker[] shown = [];
    private AnchoredRange[] shownRanges = [];

    /// <summary>Where in <see cref="shown"/> each of <see cref="found"/> drew its marker (-1 for one drawing none), and what
    /// made them.</summary>
    private int[] drawnAt = [];
    private Func<Diagnostic, TextRange, Marker?>? drawnBy;

    /// <summary>The markers a report draws as it is taken, before they are put in order: kept from one report to the next,
    /// emptied after each.</summary>
    private Marker?[] making = [];

    /// <summary>The diagnostics placed on the server's lines of the text as it stands, made when first asked for after an
    /// edit, so a key costs a move of each range rather than a line table of the file.</summary>
    private IReadOnlyList<Diagnostic>? placed = [];

    /// <summary>The diagnostics last reported, moved with the text since, so their lines are the text's as it is now.</summary>
    public IReadOnlyList<Diagnostic> Placed => placed ??= DiagnosticMarks.Placed(document.Text.Snapshot, found, [.. ranges.Select(r => r.Range)]);

    /// <summary>What the last project-wide pull reported, placed so: carried with the file's own until the next one answers.</summary>
    public IReadOnlyList<Diagnostic> Late => late == 0 ? [] : [.. Placed.Skip(found.Length - late)];

    /// <summary>The diagnostics placed so, each with its range in the text as it stands.</summary>
    public IEnumerable<(Diagnostic Diagnostic, TextRange Range)> PlacedRanges => Placed.Zip(ranges.Select(r => r.Range));

    /// <summary>The markers last given, where the text has carried them since: read to show them with another source's.</summary>
    public IReadOnlyList<Marker> Markers
    {
        get
        {
            Marker[] markers = new Marker[shown.Length];
            for (int i = 0; i < markers.Length; i++)
            {
                markers[i] = shown[i] with { Range = shownRanges[i].Range };
            }
            return markers;
        }
    }

    /// <summary>Whether a file's markers are those last given, marker for marker, where the text has carried them: what a
    /// report <see cref="Take"/> found unchanged leaves the file showing, read without a list made for it.</summary>
    public bool Carries(IReadOnlyList<Marker> markers)
    {
        if (markers.Count != shown.Length)
        {
            return false;
        }
        for (int i = 0; i < shown.Length; i++)
        {
            if (markers[i] != shown[i] with { Range = shownRanges[i].Range })
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The text changed: the diagnostics' ranges moved with it, and are placed on the server's lines again when
    /// next asked for.</summary>
    public void Moved()
    {
        if (ranges.Length > 0)
        {
            placed = null;
        }
    }

    /// <summary>Carries markers the file shows already (another server's, until this one reports) as it carries its own.</summary>
    public void Adopt(IReadOnlyList<Marker> markers)
    {
        Release();
        shown = [.. markers.OrderBy(m => m.Range.Start)];
        shownRanges = [.. shown.Select(m => Anchored(m.Range))];
    }

    /// <summary>Takes diagnostics reported for the text as it stands, the last <paramref name="late"/> of them a project-wide
    /// pull's (<see cref="Late"/>), and gives the markers they draw as; null when those are the markers it gave last, where
    /// the text has carried them (<see cref="Markers"/>), each diagnostic keeping its range and marker.</summary>
    public IReadOnlyList<Marker>? Take(IReadOnlyList<Diagnostic> diagnostics, Func<Diagnostic, TextRange, Marker?> marker, int late = 0)
    {
        ServerLines lines = ServerLines.Of(document.Text.Snapshot);
        Diagnostic[] taken = [.. diagnostics];
        TextRange[] reported = DiagnosticMarks.Offsets(lines, taken);
        bool sameMarks = marker.Equals(drawnBy);
        // Markers adopted from another server are carried by none of the diagnostics taken.
        bool own = found.Length > 0 || shown.Length == 0;
        if (sameMarks && own && taken.Length == found.Length && SameInStep(taken, reported))
        {
            found = taken;
            this.late = late;
            placed = diagnostics;
            return null;
        }
        int[] was = Kept(taken, reported);
        if (sameMarks && own && taken.Length == found.Length && Array.IndexOf(was, -1) < 0)
        {
            // The same diagnostics in another order (an analyzer's come in any): each keeps its range and marker.
            AnchoredRange[] same = new AnchoredRange[taken.Length];
            int[] at = new int[taken.Length];
            for (int i = 0; i < taken.Length; i++)
            {
                same[i] = ranges[was[i]];
                at[i] = drawnAt[was[i]];
            }
            found = taken;
            ranges = same;
            drawnAt = at;
            this.late = late;
            placed = diagnostics;
            return null;
        }
        AnchoredRange[] carried = new AnchoredRange[taken.Length];
        Marker?[] made = Making(taken.Length);
        bool[] reused = new bool[ranges.Length];
        for (int i = 0; i < taken.Length; i++)
        {
            if (was[i] >= 0)
            {
                carried[i] = ranges[was[i]];
                reused[was[i]] = true;
                made[i] = !sameMarks ? marker(taken[i], carried[i].Range) : drawnAt[was[i]] is >= 0 and int at ? shown[at] with { Range = carried[i].Range } : null;
            }
            else
            {
                carried[i] = Anchored(reported[i]);
                made[i] = marker(taken[i], carried[i].Range);
            }
        }
        // Adopted markers' ranges are their own, carried by no diagnostic.
        if (found.Length == 0)
        {
            foreach (AnchoredRange range in shownRanges)
            {
                range.Remove();
            }
        }
        for (int i = 0; i < ranges.Length; i++)
        {
            if (!reused[i])
            {
                ranges[i].Remove();
            }
        }
        found = taken;
        ranges = carried;
        this.late = late;
        placed = diagnostics;
        return Show(made, marker);
    }

    /// <summary>For each diagnostic taken, the one of those taken before it carries on: the same words, codes and tags on
    /// the same range of the text as it stands, where the text carried the one before; -1 for one that is new. Read in step
    /// from both ends first, as a pull mostly gives the same diagnostics in the same order, and only the rest by place.</summary>
    private int[] Kept(Diagnostic[] taken, TextRange[] reported)
    {
        int[] was = new int[taken.Length];
        Array.Fill(was, -1);
        int head = 0;
        while (head < taken.Length && head < found.Length && SameAt(head, head))
        {
            was[head] = head;
            head++;
        }
        int tail = 0;
        while (tail < taken.Length - head && tail < found.Length - head && SameAt(taken.Length - 1 - tail, found.Length - 1 - tail))
        {
            was[taken.Length - 1 - tail] = found.Length - 1 - tail;
            tail++;
        }
        int news = taken.Length - head - tail;
        int olds = found.Length - head - tail;
        if (news == 0 || olds == 0)
        {
            return was;
        }
        // The rest in order of place, those on one range paired by their words.
        long[] newPlaces = new long[news];
        int[] newOrder = new int[news];
        for (int k = 0; k < news; k++)
        {
            newPlaces[k] = Place(reported[head + k]);
            newOrder[k] = head + k;
        }
        long[] oldPlaces = new long[olds];
        int[] oldOrder = new int[olds];
        for (int k = 0; k < olds; k++)
        {
            oldPlaces[k] = Place(ranges[head + k].Range);
            oldOrder[k] = head + k;
        }
        Array.Sort(newPlaces, newOrder);
        Array.Sort(oldPlaces, oldOrder);
        int a = 0;
        for (int b = 0; b < news; b++)
        {
            while (a < olds && oldPlaces[a] < newPlaces[b])
            {
                a++;
            }
            for (int c = a; c < olds && oldPlaces[c] == newPlaces[b]; c++)
            {
                if (oldOrder[c] >= 0 && Same(found[oldOrder[c]], taken[newOrder[b]]))
                {
                    was[newOrder[b]] = oldOrder[c];
                    oldOrder[c] = -1;
                    break;
                }
            }
        }
        return was;

        bool SameAt(int now, int before) => ranges[before].Range == reported[now] && Same(found[before], taken[now]);
    }

    /// <summary>Whether the diagnostics taken are those taken before, one for one in the same order, where the text carried them.</summary>
    private bool SameInStep(Diagnostic[] taken, TextRange[] reported)
    {
        for (int i = 0; i < taken.Length; i++)
        {
            if (ranges[i].Range != reported[i] || !Same(found[i], taken[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A range as one number, ordered by its start, then its end.</summary>
    private static long Place(TextRange range) => ((long)range.Start << 32) | (uint)range.End;

    /// <summary>Whether two diagnostics say the same: what draws a marker reads of them, all but their range.</summary>
    private static bool Same(Diagnostic one, Diagnostic other) =>
        one.Severity == other.Severity && one.Code == other.Code && one.CodeIsNumber == other.CodeIsNumber && one.Source == other.Source
        && one.Message == other.Message && one.CodeDescription == other.CodeDescription && SameTags(one.Tags, other.Tags);

    private static bool SameTags(List<DiagnosticTag>? one, List<DiagnosticTag>? other)
    {
        if ((one?.Count ?? 0) != (other?.Count ?? 0))
        {
            return false;
        }
        for (int i = 0; i < (one?.Count ?? 0); i++)
        {
            if (one![i] != other![i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Leaves out the diagnostics <paramref name="which"/> names, giving the markers the rest draw as, or null when
    /// it named none.</summary>
    public IReadOnlyList<Marker>? Drop(Func<Diagnostic, bool> which, Func<Diagnostic, TextRange, Marker?> marker)
    {
        if (!found.Any(which))
        {
            return null;
        }
        int[] kept = [.. Enumerable.Range(0, found.Length).Where(i => !which(found[i]))];
        late = kept.Count(i => i >= found.Length - late);
        foreach (AnchoredRange dropped in ranges.Except(kept.Select(i => ranges[i])).Concat(shownRanges.Except(ranges)))
        {
            dropped.Remove();
        }
        ranges = [.. kept.Select(i => ranges[i])];
        found = [.. kept.Select(i => found[i])];
        placed = null;
        Marker?[] made = Making(found.Length);
        for (int i = 0; i < made.Length; i++)
        {
            made[i] = marker(found[i], ranges[i].Range);
        }
        return Show(made, marker);
    }

    /// <summary>The list <see cref="making"/> for a report of <paramref name="count"/> diagnostics.</summary>
    private Marker?[] Making(int count)
    {
        if (making.Length < count)
        {
            making = new Marker?[Math.Max(count, making.Length * 2)];
        }
        return making;
    }

    /// <summary>The markers the diagnostics draw as (<paramref name="made"/>, each of <see cref="found"/>'s or null), on their
    /// ranges as they stand, put in order once here (the server's sources and Passenger's own each give theirs apart) so a
    /// key carries them in order, two starting together in the order given.</summary>
    private Marker[] Show(Marker?[] made, Func<Diagnostic, TextRange, Marker?> marker)
    {
        drawnBy = marker;
        drawnAt = new int[found.Length];
        int count = 0;
        bool inOrder = true;
        int last = int.MinValue;
        for (int i = 0; i < found.Length; i++)
        {
            if (made[i] is { } m)
            {
                drawnAt[i] = count++;
                inOrder &= m.Range.Start >= last;
                last = m.Range.Start;
            }
            else
            {
                drawnAt[i] = -1;
            }
        }
        shown = new Marker[count];
        shownRanges = new AnchoredRange[count];
        for (int i = 0; i < found.Length; i++)
        {
            if (made[i] is { } m)
            {
                shown[drawnAt[i]] = m;
                shownRanges[drawnAt[i]] = ranges[i];
            }
        }
        Array.Clear(made, 0, found.Length);
        if (!inOrder)
        {
            // Put in order by start, those starting together as they came, each diagnostic told where its marker went.
            int[] order = [.. Enumerable.Range(0, count).OrderBy(k => shown[k].Range.Start)];
            int[] placedAt = new int[count];
            for (int k = 0; k < count; k++)
            {
                placedAt[order[k]] = k;
            }
            shown = [.. order.Select(k => shown[k])];
            shownRanges = [.. order.Select(k => shownRanges[k])];
            for (int i = 0; i < drawnAt.Length; i++)
            {
                drawnAt[i] = drawnAt[i] >= 0 ? placedAt[drawnAt[i]] : -1;
            }
        }
        return shown;
    }

    /// <summary>A range of the text, carried as a squiggle is: what is typed at its end goes inside it while it holds text.</summary>
    private AnchoredRange Anchored(TextRange range) => document.Text.RangeAt(range, AnchorGravity.Before, AnchorGravity.After, empty: AnchorGravity.Before);

    /// <summary>Lets every range go, the text no longer carrying them, and the diagnostics they carried.</summary>
    private void Release()
    {
        foreach (AnchoredRange range in ranges.Concat(shownRanges))
        {
            range.Remove();
        }
        found = [];
        ranges = [];
        placed = [];
        shown = [];
        shownRanges = [];
        drawnAt = [];
        drawnBy = null;
        late = 0;
    }

    /// <summary>Asks the server again after <paramref name="delay"/>, ending the ask under way: <paramref name="ask"/> pulls
    /// (<see cref="PulledAsync"/>) and shows what answers, a server that fails or a later ask leaving the markers as they
    /// stand; the file's checks are held for its checker until the ask ends, as is <paramref name="held"/>.</summary>
    public void Pull(TimeSpan delay, Func<CancellationToken, Task> ask, IDisposable? held = null)
    {
        pulling?.Cancel();
        CancellationTokenSource cancel = new();
        pulling = cancel;
        pausing = delay > TimeSpan.Zero ? cancel : null;
        BackgroundWork.Begin();
        IdeErrors.Observe(PullAsync(delay, ask, cancel, checker is null ? null : document.Checking(checker), held));
    }

    /// <summary>The server's diagnostics of the file from the sources <paramref name="part"/> names, when they answer the
    /// text as it stands and no later ask has begun.</summary>
    public async Task<IReadOnlyList<Diagnostic>?> PulledAsync(LspDocument lsp, CancellationToken cancel, DiagnosticPull part = DiagnosticPull.All)
    {
        (IReadOnlyList<Diagnostic> diagnostics, int version) = await lsp.DiagnosticsAsync(part, cancel);
        return !cancel.IsCancellationRequested && version == document.Text.Version ? diagnostics : null;
    }

    /// <summary>Ends the ask under way and lets the diagnostics' ranges go: the file was let go.</summary>
    public void Stop()
    {
        pulling?.Cancel();
        Release();
    }

    private async Task PullAsync(TimeSpan delay, Func<CancellationToken, Task> ask, CancellationTokenSource cancel, IDisposable? checking, IDisposable? held)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                // Typing went on, the wait ends quietly: an exception thrown at every key costs its thread more than the wait.
                await Task.Delay(delay, cancel.Token).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                if (cancel.IsCancellationRequested)
                {
                    return;
                }
                if (pausing == cancel)
                {
                    pausing = null;
                }
            }
            await ask(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // Typing went on, or the file was let go.
        }
        catch (LanguageServerFailed)
        {
            // The markers stand until the server answers again, or a server started again does.
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                checking?.Dispose();
                held?.Dispose();
                BackgroundWork.End();
            });
        }
    }
}
