using System.Diagnostics;
using Passenger.Dap;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;

namespace Passenger.Debugger;

/// <summary>A recording's moments that are no call: an exception thrown in the user's code (or reaching it from code that
/// is not) and one caught there, the exception read as a stop's $exception is; and a hit of one of the person's
/// breakpoints that went on (its condition false, its hit count not reached, a tracepoint's message logged, a snapshot
/// taken), its frame's values read as a snapshot reads them. Each is placed in the recorded call its frame is.</summary>
public partial class ManagedDebugger
{
    /// <summary>The threads whose exception now passing through has been kept as thrown, until it is caught or ends them.</summary>
    private readonly HashSet<int> _throwsRecorded = [];

    /// <summary>Keeps an exception's moment: thrown where it is first seen in the user's code, caught where the user's code
    /// catches it, calls it unwound let go; none for one that ends its thread, which stops.</summary>
    private async Task RecordExceptionAsync(ICorDebugThread thread, Exception2CorDebugManagedCallbackEventArgs seen, long came)
    {
        MomentKind kind;
        switch (seen.DwEventType)
        {
            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE when IsUserCodeFrame(seen.Frame):
            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_USER_FIRST_CHANCE:
                if (!_throwsRecorded.Add(thread.Id))
                {
                    return;
                }
                kind = MomentKind.Thrown;
                break;
            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_CATCH_HANDLER_FOUND:
                _throwsRecorded.Remove(thread.Id);
                if (!IsUserCodeFrame(seen.Frame))
                {
                    return;
                }
                kind = MomentKind.Caught;
                break;
            default:
                _throwsRecorded.Remove(thread.Id);
                return;
        }
        if (seen.Frame is not ICorDebugILFrame frame || RangeOf(frame) is not { } range)
        {
            return;
        }
        List<OpenCall> open = OpenOn(thread.Id);
        if (kind == MomentKind.Caught)
        {
            // What lay below the frame that catches it was unwound.
            open.RemoveAll(call => call.Frame.Start.Value < range.Start.Value);
        }
        string type = GetCurrentExceptionType(thread);
        string? message = thread.TryGetCurrentException(out ICorDebugValue? exception) is Cor.S_OK && exception is not null ? ExceptionMessage(exception) : null;
        string said = kind == MomentKind.Caught ? $"{type} caught" : message is null ? $"{type} thrown" : $"{type} thrown: {Cut(message)}";
        await KeepInFrameAsync(thread, frame, range, kind, said, came, rows => AddCurrentException(rows, new ThreadId(thread.Id), new FrameStackDepth(0)));
    }

    /// <summary>An exception's message as it stores it, null where it holds none or is no exception.</summary>
    private static string? ExceptionMessage(ICorDebugValue exception)
    {
        if (exception.UnwrapDebugValue() is not ICorDebugObjectValue thrown)
        {
            return null;
        }
        ICorDebugType? type = thrown.ExactType;
        for (int level = 0; level < 16 && type is not null; level++)
        {
            ICorDebugClass declaring = type.Class;
            mdFieldDef field = declaring.Module.GetMetaDataInterface<IMetaDataImport>().EnumFieldsWithName(declaring.Token, "_message").FirstOrDefault();
            if (!field.IsNil)
            {
                return thrown.GetFieldValue(declaring, field)?.UnwrapDebugValue() is ICorDebugStringValue text ? text.String : null;
            }
            type = type.TryGetBase(out ICorDebugType? next) is Cor.S_OK ? next : null;
        }
        return null;
    }

    /// <summary>Keeps a hit of the person's breakpoint that went on, and why, with its frame's values.</summary>
    private Task RecordPassedAsync(ICorDebugThread thread, string named, string why, long came)
    {
        if (TopFrame(thread) is not ICorDebugILFrame frame || RangeOf(frame) is not { } range || FindModule(frame.Function.Module) is not { } module)
        {
            return Task.CompletedTask;
        }
        string said = $"{char.ToUpperInvariant(named[0])}{named[1..]} went on: {why}";
        return KeepInFrameAsync(thread, frame, range, MomentKind.Passed, said, came, async rows =>
        {
            ThreadId threadId = new(thread.Id);
            ICorDebugValue? hoisted = await AddArguments(module, frame.Function, rows, threadId, new FrameStackDepth(0));
            await AddLocalVariables(module, frame.Function, rows, threadId, new FrameStackDepth(0), hoisted);
        });
    }

    /// <summary>Keeps a moment that stands in a frame, not a call: placed where the frame stands, in the recorded call the
    /// frame is (its caller's place and the calls above), its rows those <paramref name="read"/> adds.</summary>
    private async Task KeepInFrameAsync(ICorDebugThread thread, ICorDebugILFrame frame, StackRange range, MomentKind kind, string said, long came,
        Func<List<VariableInfo>, Task> read)
    {
        List<OpenCall> open = OpenOn(thread.Id);
        OpenCall? inside = open.FindLast(call => call.Frame == range);
        int flow = inside?.Flow ?? AsyncCallRunIn(frame)?.Flow ?? thread.Id;
        StackFrameInfo place = DescribeFrame(0, frame);
        Capture capture = NewRecordingCapture(null);
        List<VariableInfo> rows = [];
        int topReference = await ReadRecordedAsync(capture, rows, () => read(rows));
        Keep(new KeptMoment(new RecordedMoment(_momentsRecorded + 1, kind, thread.Id, DateTimeOffset.Now, place, open.Count, said), ThreadNameOf(thread), inside?.Caller,
            inside?.Parent, capture.Values, topReference, capture.Values[topReference], Stopwatch.GetElapsedTime(came), BytesOf(capture.Values, 0), capture.Said, flow));
    }
}
