#nullable enable

using System;
using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Passenger.Debugger.Interop;

/// <summary>dbgshim's exports and the startup callback it calls take the platform's default convention: stdcall on 32-bit
/// Windows, where dbgshim is built with it by default, and the one convention of every 64-bit system.</summary>
public static partial class DbgShim
{
    [LibraryImport("dbgshim", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CreateProcessForLaunch(string lpCommandLine, [MarshalAs(UnmanagedType.Bool)] bool bSuspendProcess, nint lpEnvironment, string? lpCurrentDirectory, out uint pProcessId, out nint pResumeHandle);

    [LibraryImport("dbgshim")]
    public static partial int ResumeProcess(nint hResumeHandle);

    [LibraryImport("dbgshim")]
    public static partial int CloseResumeHandle(nint hResumeHandle);

    [LibraryImport("dbgshim")]
    public unsafe static partial int RegisterForRuntimeStartup(uint dwProcessId, delegate* unmanaged<void*, void*, int, void> pfnCallback, nint parameter, out nint pUnregisterToken);

    [LibraryImport("dbgshim")]
    public static partial int UnregisterForRuntimeStartup(nint pUnregisterToken);
}
