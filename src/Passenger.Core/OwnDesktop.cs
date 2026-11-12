using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>A program started on Windows on a desktop of its own, made for it, never switched to, and closed once it has
/// ended: what it shows draws where no one sees it and takes no focus or key of the person's. .NET's start names no desktop
/// (STARTUPINFO's lpDesktop), so the program is started here, suspended until its job holds it (<see cref="Lifeline"/>), its
/// standard streams on pipes of its own that it alone inherits.</summary>
[SupportedOSPlatform("windows")]
public static partial class OwnDesktop
{
    private const uint GenericAll = 0x10000000;
    private const int UseStandardHandles = 0x100;
    private const uint Suspended = 0x4;
    private const uint UnicodeEnvironment = 0x400;
    private const uint ExtendedStartupInfo = 0x80000;
    private const uint NoWindow = 0x08000000;
    private static readonly nint HandleList = 0x20002;
    private static int made;

    /// <summary>Starts a program as <paramref name="start"/> says on a desktop of its own, held in a job that ends it with
    /// Passenger; suspended until <see cref="OwnDesktopStart.Resume"/> where asked. What CreateProcess refuses, this throws.</summary>
    public static OwnDesktopStart Start(ProcessStartInfo start, bool suspended)
    {
        string name = $"passenger-{Environment.ProcessId}-{Interlocked.Increment(ref made)}";
        nint desktop = CreateDesktopW(name, 0, 0, 0, GenericAll, 0);
        if (desktop == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"No desktop of its own could be made for {Path.GetFileName(start.FileName)}");
        }
        List<nint> theirs = [];
        List<nint> ours = [];
        nint desktopName = Marshal.StringToHGlobalUni(name);
        nint commandLine = Marshal.StringToHGlobalUni(CommandLineOf(start));
        nint environment = Marshal.StringToHGlobalUni(EnvironmentOf(start));
        nint attributes = 0;
        try
        {
            (nint childIn, nint input) = Pipe(childReads: true, theirs, ours);
            (nint output, nint childOut) = Pipe(childReads: false, theirs, ours);
            (nint error, nint childErr) = Pipe(childReads: false, theirs, ours);
            nint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            nint handles = Marshal.AllocHGlobal(IntPtr.Size * theirs.Count);
            bool listed = false;
            try
            {
                Marshal.Copy(theirs.ToArray(), 0, handles, theirs.Count);
                listed = InitializeProcThreadAttributeList(attributes, 1, 0, ref size);
                if (!listed || !UpdateProcThreadAttribute(attributes, 0, HandleList, handles, IntPtr.Size * theirs.Count, 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The handles a program inherits could not be named");
                }
                StartupInfoEx info = new()
                {
                    StartupInfo = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>(),
                        Desktop = desktopName,
                        Flags = UseStandardHandles,
                        StandardInput = childIn,
                        StandardOutput = childOut,
                        StandardError = childErr,
                    },
                    AttributeList = attributes,
                };
                uint flags = Suspended | UnicodeEnvironment | ExtendedStartupInfo | (start.CreateNoWindow ? NoWindow : 0);
                string? folder = string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory;
                if (!CreateProcessW(null, commandLine, 0, 0, true, flags, environment, folder, ref info, out ProcessInformation started))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                foreach (nint handle in theirs)
                {
                    CloseHandle(handle);
                }
                theirs.Clear();
                return Taken(start, started, desktop, suspended, input, output, error);
            }
            finally
            {
                if (listed)
                {
                    DeleteProcThreadAttributeList(attributes);
                }
                Marshal.FreeHGlobal(handles);
            }
        }
        catch
        {
            foreach (nint handle in ours)
            {
                CloseHandle(handle);
            }
            CloseDesktop(desktop);
            throw;
        }
        finally
        {
            foreach (nint handle in theirs)
            {
                CloseHandle(handle);
            }
            Marshal.FreeHGlobal(desktopName);
            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(environment);
            if (attributes != 0)
            {
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    /// <summary>A program just started suspended, taken on: its job, its desktop closed as it ends, its streams, and let
    /// run unless it is to stay held.</summary>
    private static OwnDesktopStart Taken(ProcessStartInfo start, ProcessInformation started, nint desktop, bool suspended, nint input, nint output, nint error)
    {
        Process process = Process.GetProcessById(started.ProcessId);
        // Held by its handle, as a child .NET starts is, so its id stays its own and its kill finds it after it has ended.
        _ = process.SafeHandle;
        Lifeline.HoldFamily(started.ProcessId, new SafeProcessHandle(started.Process, ownsHandle: false));
        // The desktop goes once the program has: closed before its first thread took it, the desktop would go under it.
        ProcessEnd ended = new(started.Process);
        _ = ThreadPool.RegisterWaitForSingleObject(ended, (_, _) =>
        {
            CloseDesktop(desktop);
            ended.Dispose();
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        nint thread = started.Thread;
        Action resume = () =>
        {
            if (Interlocked.Exchange(ref thread, 0) is var held and not 0)
            {
                _ = ResumeThread(held);
                CloseHandle(held);
            }
        };
        if (!suspended)
        {
            resume();
        }
        Encoding encoding = new UTF8Encoding(false);
        StreamWriter writer = new(new FileStream(new SafeFileHandle(input, ownsHandle: true), FileAccess.Write, 1), start.StandardInputEncoding ?? encoding) { AutoFlush = true };
        StreamReader? read = Reader(output, start.RedirectStandardOutput, start.StandardOutputEncoding ?? encoding);
        StreamReader? errors = Reader(error, start.RedirectStandardError, start.StandardErrorEncoding ?? encoding);
        return new OwnDesktopStart(process, writer, read, errors, resume);
    }

    /// <summary>A stream the program writes, read: its reader where its start redirected it, else read to its end and dropped.</summary>
    private static StreamReader? Reader(nint handle, bool redirected, Encoding encoding)
    {
        FileStream stream = new(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Read, 4096);
        if (redirected)
        {
            return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false);
        }
        // Unheld: it lasts as long as the program, which no wait for the IDE to be quiet waits out.
        UnawaitedWork.Dismiss(BackgroundWork.Unheld(async () =>
        {
            await using (stream)
            {
                await stream.CopyToAsync(Stream.Null);
            }
        }));
        return null;
    }

    /// <summary>A pipe, its child's end inheritable and named in <paramref name="theirs"/>, its own end in
    /// <paramref name="ours"/>: (child's, ours) where the child reads, (ours, child's) where it writes.</summary>
    private static (nint First, nint Second) Pipe(bool childReads, List<nint> theirs, List<nint> ours)
    {
        SecurityAttributes inherited = new() { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        if (!CreatePipe(out nint read, out nint write, ref inherited, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "No pipe could be made for a program's stream");
        }
        (nint child, nint own) = childReads ? (read, write) : (write, read);
        _ = StandardHandles.NotInherited(own);
        theirs.Add(child);
        ours.Add(own);
        return childReads ? (child, own) : (own, child);
    }

    /// <summary>The program's command line, as Windows splits it back into its arguments.</summary>
    private static string CommandLineOf(ProcessStartInfo start) => start.ArgumentList.Count > 0 || string.IsNullOrEmpty(start.Arguments)
        ? WindowsCommandLine.Of([start.FileName, .. start.ArgumentList])
        : $"{WindowsCommandLine.Of([start.FileName])} {start.Arguments}";

    /// <summary>The program's environment as Windows takes a block of it: sorted by name, each variable ended by a NUL, the
    /// block by another.</summary>
    private static string EnvironmentOf(ProcessStartInfo start)
    {
        StringBuilder block = new();
        foreach ((string name, string? value) in start.Environment.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }
        return block.Append('\0').ToString();
    }

    /// <summary>The program's own process handle, signalled as it ends.</summary>
    private sealed class ProcessEnd : WaitHandle
    {
        public ProcessEnd(nint process)
        {
            SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int Columns;
        public int Rows;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public nint ReservedBytes;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int InheritHandle;
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateDesktopW(string name, nint device, nint mode, int flags, uint access, nint security);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out nint read, out nint write, ref SecurityAttributes security, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, int flags, nint attribute, nint value, nint size, nint previous, nint returned);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string? application, nint commandLine, nint processSecurity, nint threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string? folder, ref StartupInfoEx startup, out ProcessInformation started);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

/// <summary>A program started on a desktop of its own (<see cref="OwnDesktop"/>): its process, its streams (null for one its
/// start did not redirect), and, for one started suspended, what lets it run.</summary>
public sealed class OwnDesktopStart(Process process, StreamWriter input, StreamReader? output, StreamReader? error, Action resume)
{
    public Process Process { get; } = process;
    public StreamWriter Input { get; } = input;
    public StreamReader? Output { get; } = output;
    public StreamReader? Error { get; } = error;

    /// <summary>Lets a program started suspended run its first instruction; nothing for one already running.</summary>
    public void Resume() => resume();
}
