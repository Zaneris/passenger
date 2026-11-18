using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>macOS's file events for a folder, read through an FSEvents stream of Passenger's own on a dispatch queue of its
/// own whose callback only queues what it is given, so a reader held up never has the system drop the stream's events:
/// the queue is handed over in turn, as background work, each event as <see cref="MacFileEventFlags"/> reads it, the
/// stream's word that it dropped changes among them. A <see cref="WatchedTree"/>'s alone.</summary>
internal sealed unsafe partial class MacFileEvents : IDisposable
{
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    private const uint Utf8 = 0x08000100;
    private const ulong SinceNow = ulong.MaxValue;

    /// <summary>The stream's events at once, the folder itself watched for a move or deletion, each file's own.</summary>
    private const uint NoDefer = 0x2;
    private const uint WatchRoot = 0x4;
    private const uint FileEvents = 0x10;

    private readonly bool recursive;
    private readonly Action<MacFileEvent> heard;
    private readonly ConcurrentQueue<(string Path, uint Flags)> queued = new();
    private readonly Lock gate = new();
    private IntPtr stream;
    private IntPtr queue;
    private int draining;
    private bool disposed;

    /// <summary>Starts the stream over <paramref name="folder"/> (canonical), each event handed to <paramref name="heard"/> in
    /// turn, off the system's thread; an <see cref="IOException"/> where the system would not start it.</summary>
    public MacFileEvents(string folder, bool recursive, Action<MacFileEvent> heard)
    {
        Folder = folder;
        this.recursive = recursive;
        this.heard = heard;
        GCHandle self = GCHandle.Alloc(this);
        IntPtr path = CFStringCreateWithCString(IntPtr.Zero, folder, Utf8);
        IntPtr paths = CFArrayCreate(IntPtr.Zero, &path, 1, IntPtr.Zero);
        try
        {
            Context context = new() { Info = GCHandle.ToIntPtr(self), Release = &Release };
            stream = FSEventStreamCreate(IntPtr.Zero, &Callback, &context, paths, SinceNow, 0, NoDefer | WatchRoot | FileEvents);
            if (stream == IntPtr.Zero)
            {
                self.Free();
                throw new IOException($"macOS made no stream of the file events under {folder}");
            }
            queue = dispatch_queue_create($"Passenger's file events of {folder}", IntPtr.Zero);
            FSEventStreamSetDispatchQueue(stream, queue);
            if (!FSEventStreamStart(stream))
            {
                Dispose();
                throw new IOException($"macOS would not start the stream of the file events under {folder}");
            }
        }
        finally
        {
            CFRelease(paths);
            CFRelease(path);
        }
    }

    /// <summary>The folder whose events are read.</summary>
    public string Folder { get; }

    /// <summary>Holds the stream's reading, as a reader held up by a loaded system is, until let go of: what comes meanwhile
    /// waits in the system's queue for the stream, which may drop it and say so. For a test.</summary>
    public IDisposable HoldReading()
    {
        IntPtr held;
        lock (gate)
        {
            held = disposed ? IntPtr.Zero : queue;
        }
        if (held == IntPtr.Zero)
        {
            return new Resumed(IntPtr.Zero);
        }
        dispatch_retain(held);
        dispatch_suspend(held);
        return new Resumed(held);
    }

    /// <summary>A held reading, let go of once.</summary>
    private sealed class Resumed(IntPtr queue) : IDisposable
    {
        private IntPtr held = queue;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref held, IntPtr.Zero) is var queue && queue != IntPtr.Zero)
            {
                dispatch_resume(queue);
                dispatch_release(queue);
            }
        }
    }

    public void Dispose()
    {
        IntPtr ending;
        IntPtr ended;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            (ending, ended) = (stream, queue);
            (stream, queue) = (IntPtr.Zero, IntPtr.Zero);
        }
        if (ending != IntPtr.Zero)
        {
            FSEventStreamStop(ending);
            FSEventStreamInvalidate(ending);
            // Its last release lets go of this object's handle (Release).
            FSEventStreamRelease(ending);
        }
        if (ended != IntPtr.Zero)
        {
            dispatch_release(ended);
        }
    }

    /// <summary>On the stream's queue: each event queued as given, its path made a string, and the queue handed over.</summary>
    [UnmanagedCallersOnly]
    private static void Callback(IntPtr stream, IntPtr info, nuint count, byte** paths, uint* flags, ulong* ids)
    {
        if (GCHandle.FromIntPtr(info).Target is not MacFileEvents events)
        {
            return;
        }
        for (nuint i = 0; i < count; i++)
        {
            events.queued.Enqueue((Marshal.PtrToStringUTF8((IntPtr)paths[i]) ?? string.Empty, flags[i]));
        }
        events.Drain();
    }

    [UnmanagedCallersOnly]
    private static void Release(IntPtr info) => GCHandle.FromIntPtr(info).Free();

    /// <summary>Hands what was queued over in turn, one drain at a time, as background work.</summary>
    private void Drain()
    {
        if (Interlocked.Exchange(ref draining, 1) == 1)
        {
            return;
        }
        IdeErrors.Observe(BackgroundWork.Run(() =>
        {
            List<MacFileEvent> read = [];
            while (true)
            {
                while (queued.TryDequeue(out (string Path, uint Flags) next))
                {
                    read.Clear();
                    MacFileEventFlags.Read(next.Path, next.Flags, Folder, recursive, There, read);
                    foreach (MacFileEvent each in read)
                    {
                        if (IsDisposed)
                        {
                            return;
                        }
                        heard(each);
                    }
                }
                Volatile.Write(ref draining, 0);
                if (queued.IsEmpty || Interlocked.Exchange(ref draining, 1) == 1)
                {
                    return;
                }
            }
        }));
    }

    /// <summary>Whether something stands at a path now: a file where the event says it was one.</summary>
    private static bool There(string path, bool file) => File.Exists(path) || !file && Directory.Exists(path);

    private bool IsDisposed
    {
        get
        {
            lock (gate)
            {
                return disposed;
            }
        }
    }

    /// <summary>FSEventStreamContext.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Context
    {
        public nint Version;
        public IntPtr Info;
        public IntPtr Retain;
        public delegate* unmanaged<IntPtr, void> Release;
        public IntPtr CopyDescription;
    }

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFArrayCreate(IntPtr allocator, IntPtr* values, nint count, IntPtr callbacks);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr value);

    [LibraryImport(CoreServices)]
    private static partial IntPtr FSEventStreamCreate(IntPtr allocator, delegate* unmanaged<IntPtr, IntPtr, nuint, byte**, uint*, ulong*, void> callback,
        Context* context, IntPtr paths, ulong since, double latency, uint flags);

    [LibraryImport(CoreServices)]
    private static partial void FSEventStreamSetDispatchQueue(IntPtr stream, IntPtr queue);

    [LibraryImport(CoreServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool FSEventStreamStart(IntPtr stream);

    [LibraryImport(CoreServices)]
    private static partial void FSEventStreamStop(IntPtr stream);

    [LibraryImport(CoreServices)]
    private static partial void FSEventStreamInvalidate(IntPtr stream);

    [LibraryImport(CoreServices)]
    private static partial void FSEventStreamRelease(IntPtr stream);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr dispatch_queue_create(string label, IntPtr attributes);

    [LibraryImport(LibSystem)]
    private static partial void dispatch_retain(IntPtr queue);

    [LibraryImport(LibSystem)]
    private static partial void dispatch_release(IntPtr queue);

    [LibraryImport(LibSystem)]
    private static partial void dispatch_suspend(IntPtr queue);

    [LibraryImport(LibSystem)]
    private static partial void dispatch_resume(IntPtr queue);
}
