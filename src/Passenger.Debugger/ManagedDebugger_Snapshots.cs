using System.Diagnostics;
using System.Runtime.CompilerServices;
using Passenger.Dap;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;
using Passenger.Debugger.PresentationHintModels;
using Passenger.Languages.CSharp;

namespace Passenger.Debugger;

/// <summary>Snapshot points: at a hit taken, the thread's frames and the values of its user code's frames are read as a stop's
/// are (the same reads, Just My Code's and async frames' names alike), running none of the program's code, and copied into
/// plain rows before the thread goes on, which voids every value read; then the point's own expressions, which run code as a
/// watch's do, within the time any evaluation is given. The reads are bounded (frames, levels below each local, values in
/// all), so a capture holds the program a short while, which it says.</summary>
public partial class ManagedDebugger
{
    /// <summary>A snapshot point's capture, told once the program has gone on from it.</summary>
    public event Action<Snapshot>? OnSnapshot;

    /// <summary>How many of the thread's frames of the user's code a capture reads the values of, from its top.</summary>
    internal const int SnapshotFrames = 10;

    /// <summary>How many levels of members below each local a capture reads.</summary>
    internal const int SnapshotLevels = 2;

    /// <summary>How many values a capture reads in all, its top frame's first and each level before the next.</summary>
    internal const int SnapshotValues = 1_000;

    /// <summary>Why a value a capture would run code to read is not read: a getter, a ToString, a debugger view.</summary>
    internal const string ReadsNoCode = "Not read: a snapshot runs none of the program's code";

    /// <summary>When each breakpoint's event came from the runtime, the program held from then.</summary>
    private readonly ConditionalWeakTable<CorDebugManagedCallbackEventArgs, StrongBox<long>> _hitCame = new();

    /// <summary>The capture the hit being handled took, told once the program has gone on.</summary>
    private Snapshot? _taken;

    /// <summary>Why a snapshot point's own expressions, or the number of captures it asks for, cannot be taken as they are;
    /// null where they can.</summary>
    internal static string? UnreadableCapture(string? captures, int? limit)
    {
        if (limit is int asked && asked is < 1 or > BreakpointHits.MostCaptures)
        {
            return $"A snapshot point takes from 1 to {Core.Numbers.Said(BreakpointHits.MostCaptures)} snapshots, not {Core.Numbers.Said(asked)}.";
        }
        foreach (string expression in Expressions(captures))
        {
            if (CodeBrackets.Expression(expression) is null)
            {
                return $"The expression {expression} {NestedTooDeep}.";
            }
        }
        return null;
    }

    /// <summary>A snapshot point's own expressions, one to a line, blank lines passed over.</summary>
    private static IEnumerable<string> Expressions(string? captures) =>
        (captures ?? string.Empty).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);

    /// <summary>Takes a snapshot point's capture where its thread stands, held since <paramref name="came"/>: its frames, their
    /// values, the point's own expressions, kept to tell once the program goes on (<see cref="TellTaken"/>).</summary>
    private async Task CaptureAsync(ICorDebugThread thread, IHitBreakpoint point, long came)
    {
        int threadId = thread.Id;
        Capture capture = new(ReadsNoCode, $"Not read: a snapshot reads {SnapshotLevels} levels of members below each local", SnapshotValues,
            $"Not read: a snapshot reads its first {Core.Numbers.Said(SnapshotValues)} values");
        List<SnapshotFrame> frames = [];
        List<VariableInfo> asked = [];
        string? wasOff = EvalStatus.ImplicitOff;
        // Implicit evaluation off for the capture: a value its reads would run code for says so in its place.
        EvalStatus.ImplicitOff = ReadsNoCode;
        try
        {
            List<StackFrameInfo> stack = GetStackTrace(threadId);
            Queue<Opening> opening = new();
            int read = 0;
            foreach (StackFrameInfo frame in stack)
            {
                int locals = 0;
                if (frame.IsUserCode && read < SnapshotFrames)
                {
                    read++;
                    locals = await CaptureLocalsAsync(frame.Id, capture, opening);
                }
                else if (frame.IsUserCode)
                {
                    capture.Cut($"the values of frames of the user's code below the top {SnapshotFrames}");
                }
                frames.Add(new SnapshotFrame(frame, locals));
            }
            while (opening.TryDequeue(out Opening open))
            {
                await OpenAsync(open, capture, opening);
            }
            if (stack.Count > 0)
            {
                foreach (string expression in Expressions(point.Captures))
                {
                    // Explicit, as a watch's: its own code runs, bounded as every evaluation is; what it opens to is read as the rest.
                    VariableInfo value = await Evaluate(expression, stack[0].Id);
                    asked.Add(Copied(value, SnapshotLevels, capture, opening));
                }
                while (opening.TryDequeue(out Opening open))
                {
                    await OpenAsync(open, capture, opening);
                }
            }
        }
        catch (Exception error) when (Failures.OfRuntime(error) || error is InvalidOperationException or ArgumentException)
        {
            _logger?.Invoke($"The snapshot of breakpoint {point.Id} was cut short: {error}");
            capture.Cut($"what came after a read that failed ({error.Message})");
        }
        finally
        {
            EvalStatus.ImplicitOff = wasOff;
        }
        // Named as the threads' list names it.
        string? name = _threads.GetValueOrDefault(threadId)?.Name ?? thread.GetThreadNameOrNull() ?? (threadId == _mainThreadId ? "Main Thread" : null);
        _taken = new Snapshot(point.Id, threadId, name, DateTimeOffset.Now, Stopwatch.GetElapsedTime(came), frames, capture.Values, asked, capture.Said);
        _logger?.Invoke($"Snapshot of breakpoint {point.Id} on thread {threadId}: {capture.Count} values in {Core.Durations.Said(_taken.Paused)}");
    }

    /// <summary>Tells the capture the hit just handled took, once the program has gone on from it.</summary>
    private void TellTaken()
    {
        if (Interlocked.Exchange(ref _taken, null) is { } taken)
        {
            OnSnapshot?.Invoke(taken);
        }
    }

    /// <summary>A frame's locals read and copied, those that open put to <paramref name="opening"/>; their reference among the
    /// capture's values, 0 for a frame with none.</summary>
    private async Task<int> CaptureLocalsAsync(int frameId, Capture capture, Queue<Opening> opening)
    {
        List<VariableInfo> locals;
        try
        {
            List<ScopeInfo> scopes = GetScopes(frameId);
            if (scopes.Count == 0)
            {
                return 0;
            }
            locals = await GetVariables(scopes[0].VariablesReference);
        }
        catch (Exception error) when (error is EvalException or ArgumentException or InvalidOperationException || Failures.OfRuntime(error))
        {
            // A frame whose values cannot be read says so in their place; the frames below it are read still.
            locals = [FailedRow("error", error.Message)];
        }
        capture.Count += locals.Count;
        return capture.Add([.. locals.Where(Kept).Select(row => Copied(row, SnapshotLevels, capture, opening))]);
    }

    /// <summary>A row's members read and copied, while the capture has values left; past them, the row opens to why it was
    /// not read.</summary>
    private async Task OpenAsync(Opening open, Capture capture, Queue<Opening> opening)
    {
        if (capture.Count >= capture.Most)
        {
            capture.Cut($"values past its first {Core.Numbers.Said(capture.Most)}");
            open.Copy.VariablesReference = capture.Unread(capture.PastMost);
            return;
        }
        List<VariableInfo> members;
        try
        {
            members = await GetVariables(open.Reference);
        }
        catch (Exception error) when (error is EvalException or ArgumentException or InvalidOperationException || Failures.OfRuntime(error))
        {
            members = [FailedRow("error", error.Message)];
        }
        capture.Count += members.Count;
        open.Copy.VariablesReference = capture.Add([.. members.Where(Kept).Select(row => Copied(row, open.Levels, capture, opening))]);
    }

    /// <summary>A row read for a capture as a plain one, which opens, where it has <paramref name="levels"/> left, once its
    /// members are read in their turn (a group of an array's elements taking none of them); past them, to why it was not.
    /// A row whose opening would run code (a query's SQL, a sequence's results) says so and opens to nothing.</summary>
    private static VariableInfo Copied(VariableInfo row, int levels, Capture capture, Queue<Opening> opening)
    {
        VariableInfo copy = new()
        {
            Name = row.Name,
            EvaluateName = row.EvaluateName,
            DeclaringType = row.DeclaringType,
            Value = row.Value,
            Type = row.Type,
            PresentationHint = row.PresentationHint,
            VariablesReference = 0,
            Mark = row.Mark,
        };
        if (row.VariablesReference <= 0)
        {
            return copy;
        }
        if (row.PresentationHint?.Lazy == true || Grouping(row, DapRows.Results))
        {
            copy.Value = capture.RunsNoCode;
            copy.PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Virtual };
            return copy;
        }
        bool group = row.PresentationHint?.Kind == PresentationHintKind.Virtual;
        if (levels <= 0 && !group)
        {
            copy.VariablesReference = capture.Unread(capture.PastLevels);
            return copy;
        }
        opening.Enqueue(new Opening(copy, row.VariablesReference, group ? levels : levels - 1));
        return copy;
    }

    /// <summary>Whether a row read for a capture is kept: a type's static members, which are the program's state rather than
    /// the frame's, are not.</summary>
    private static bool Kept(VariableInfo row) => !Grouping(row, DapRows.StaticMembers);

    /// <summary>Whether a row is the debugger's own of a name, grouping what it opens to (<see cref="GroupRow"/>), not a member.</summary>
    private static bool Grouping(VariableInfo row, string name) => row.Name == name && row.Type is "" && row.EvaluateName is null;

    /// <summary>A row a capture opens in its turn: its copy, the engine's reference to read its members by, and the levels
    /// left below it.</summary>
    private readonly record struct Opening(VariableInfo Copy, int Reference, int Levels);

    /// <summary>What a capture has read: its rows by the references of its own, how many values (at most
    /// <paramref name="most"/>), what it left out, and the words it says of a value it runs no code for, of members past its
    /// levels and of values past its most; with <paramref name="seed"/>, another's rows under their references, its own after.</summary>
    private sealed class Capture(string runsNoCode, string pastLevels, int most, string pastMost, IReadOnlyDictionary<int, IReadOnlyList<VariableInfo>>? seed = null)
    {
        private readonly Dictionary<int, IReadOnlyList<VariableInfo>> values = seed is null ? [] : new(seed);
        private readonly Dictionary<string, int> unread = new(StringComparer.Ordinal);
        private readonly List<string> cut = [];

        public string RunsNoCode => runsNoCode;

        public string PastLevels => pastLevels;

        public int Most => most;

        public string PastMost => pastMost;

        public IReadOnlyDictionary<int, IReadOnlyList<VariableInfo>> Values => values;

        public int Count { get; set; }

        /// <summary>What it left out, in words, or null for nothing.</summary>
        public string? Said => cut.Count == 0 ? null : $"Not read: {string.Join("; ", cut)}";

        /// <summary>Rows kept under a reference of the capture's own, which it gives.</summary>
        public int Add(IReadOnlyList<VariableInfo> rows)
        {
            int reference = values.Count + 1;
            values[reference] = rows;
            return reference;
        }

        /// <summary>The reference of the one row that says why a value's members were not read, kept once for each why.</summary>
        public int Unread(string why)
        {
            if (!unread.TryGetValue(why, out int reference))
            {
                reference = Add([new VariableInfo { Name = "Not read", Value = why, Type = null, VariablesReference = 0, PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Virtual } }]);
                unread[why] = reference;
            }
            return reference;
        }

        /// <summary>Notes a kind of what it left out, once.</summary>
        public void Cut(string what)
        {
            if (!cut.Contains(what))
            {
                cut.Add(what);
            }
        }
    }
}
