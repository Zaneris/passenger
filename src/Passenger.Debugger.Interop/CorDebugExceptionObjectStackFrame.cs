#nullable enable

namespace Passenger.Debugger.Interop;

/// <summary>One frame of the stack an exception object stores, as the runtime recorded it: the module and method of the
/// code, the native address in it, and whether it is the last frame of a stack trace copied from an earlier throw. The
/// module is a COM reference the caller releases.</summary>
public struct CorDebugExceptionObjectStackFrame
{
    public nint pModule;

    public CORDB_ADDRESS ip;

    public uint methodDef;

    public int isLastForeignExceptionFrame;
}
