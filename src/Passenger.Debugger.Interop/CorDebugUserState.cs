#nullable enable

namespace Passenger.Debugger.Interop;

/// <summary>
/// <para>Indicates the user state of a thread.</para>
/// </summary>
/// <remarks>
/// <para>The user state of a thread is the state that the thread has when the debugger examines it. A thread may have a combination of user states.</para>
/// <para>Use the <see cref="M:Passenger.Debugger.Interop.ICorDebugThread.TryGetUserState(Passenger.Debugger.Interop.CorDebugUserState@)">ICorDebugThread::GetUserState</see> method to retrieve a thread's user state.</para>
/// <para>Documentation adapted from <see href="https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/icordebug/cordebuguserstate-enumeration">Microsoft Learn</see>.</para>
/// </remarks>
[Flags]
public enum CorDebugUserState
{
    /// <summary>
    /// <para>A termination of the thread has been requested.</para>
    /// </summary>
    USER_STOP_REQUESTED = 1,
    /// <summary>
    /// <para>A suspension of the thread has been requested.</para>
    /// </summary>
    USER_SUSPEND_REQUESTED = 2,
    /// <summary>
    /// <para>The thread is running in the background.</para>
    /// </summary>
    USER_BACKGROUND = 4,
    /// <summary>
    /// <para>The thread has not started executing.</para>
    /// </summary>
    USER_UNSTARTED = 8,
    /// <summary>
    /// <para>The thread has been terminated.</para>
    /// </summary>
    USER_STOPPED = 0x10,
    /// <summary>
    /// <para>The thread is waiting for another thread to complete a task.</para>
    /// </summary>
    USER_WAIT_SLEEP_JOIN = 0x20,
    /// <summary>
    /// <para>The thread has been suspended.</para>
    /// </summary>
    USER_SUSPENDED = 0x40,
    /// <summary>
    /// <para>The thread is at an unsafe point. That is, the thread is at a point in execution where it may block garbage collection.<br /><br /> Debug events may be dispatched from unsafe points, but suspending a thread at an unsafe point  will very likely cause a deadlock until the thread is resumed. The safe and unsafe points are determined by the just-in-time (JIT) and garbage collection implementation.</para>
    /// </summary>
    USER_UNSAFE_POINT = 0x80,
    /// <summary>
    /// <para>The thread is from the thread pool.</para>
    /// </summary>
    USER_THREADPOOL = 0x100
}
