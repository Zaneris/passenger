using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>macOS's file events for a folder, read through an FSEvents stream of Passenger's own whose callback only queues
/// what it is given, so a reader held up never has the system drop the stream's events: what reaches the folder is handed
/// over in turn, as background work, each event as <see cref="MacFileEventFlags"/> reads it, the stream's word that it
/// dropped changes among them. The process's streams are shared among its folders (<see cref="Stream"/>), as the system
/// allows a process 512 and every program together 1,024. A <see cref="WatchedTree"/>'s alone.</summary>
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

    /// <summary>The stream the folder's events come by, under <see cref="Stream.Gate"/>; null once disposed.</summary>
    private Stream? stream;
    private int draining;
    private int disposed;

    /// <summary>Reads the events under <paramref name="folder"/> (canonical) from now on, through a stream already reading
    /// it or one started for it, each handed to <paramref name="heard"/> in turn, off the system's thread; an <see
    /// cref="IOException"/> where the system would not start one.</summary>
    public MacFileEvents(string folder, bool recursive, Action<MacFileEvent> heard)
    {
        Folder = folder;
        this.recursive = recursive;
        this.heard = heard;
        Stream.Read(this);
    }

    /// <summary>The folder whose events are read.</summary>
    public string Folder { get; }

    /// <summary>The folder each stream the process holds open reads from.</summary>
    public static IReadOnlyList<string> Streams => Stream.Folders;

    /// <summary>Holds the reading of the stream the folder's events come by, as a reader held up by a loaded system is,
    /// until let go of: what comes meanwhile, for every folder it reads, waits in the system's queue for the stream, which
    /// may drop it and say so. For a test.</summary>
    public IDisposable HoldReading() => Stream.Hold(this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Stream.Leave(this);
        }
    }

    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>On the stream's queue: an event that may reach the folder, queued as given, and the queue handed over.</summary>
    private void Take(string path, uint flags)
    {
        if (!IsDisposed)
        {
            queued.Enqueue((path, flags));
            Drain();
        }
    }

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
                    MacFileEventFlags.Read(next.Path, next.Flags, Folder, recursive, From, There, read);
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

    /// <summary>The folder the stream the events come by reads from, the folder itself once disposed.</summary>
    private string From => Volatile.Read(ref stream)?.Top ?? Folder;

    /// <summary>Whether something stands at a path now: a file where the event says it was one.</summary>
    private static bool There(string path, bool file) => File.Exists(path) || !file && Directory.Exists(path);

    /// <summary>One of the process's FSEvents streams, reading a folder and all below it on a dispatch queue of its own, and
    /// the watched folders it reads for, each event handed to those it reaches. A folder below a stream's is read through
    /// it; past <see cref="Few"/> streams, one below none is read through a stream over the deepest folder it shares with
    /// another's (<see cref="MacFileEventFlags.Shared"/>), which takes in the streams below it, so hundreds of folders apart
    /// (a language server's packages) take a few. A stream ends with the last folder it reads for.</summary>
    private sealed class Stream
    {
        /// <summary>How many streams the process starts before it shares one among folders apart.</summary>
        private const int Few = 16;

        /// <summary>What the streams, and which stream each folder's events come by, are held by.</summary>
        public static readonly Lock Gate = new();

        private static readonly List<Stream> Open = [];

        private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? PathNames.Canonical(home) : "/";

        private IntPtr stream;
        private IntPtr queue;

        /// <summary>The folders read for, as one snapshot the stream's queue reads without the gate.</summary>
        private Readers readers = Readers.None;

        /// <summary>The folders read for; changed under the gate.</summary>
        private Readers Reading
        {
            get => Volatile.Read(ref readers);
            set => Volatile.Write(ref readers, value);
        }

        /// <summary>How many holds on the reading are let go of not yet, under the gate.</summary>
        private int holds;

        private Stream(string top) => Top = top;

        /// <summary>The folder read, with all below it.</summary>
        public string Top { get; }

        public static IReadOnlyList<string> Folders
        {
            get
            {
                lock (Gate)
                {
                    return [.. Open.Select(s => s.Top)];
                }
            }
        }

        /// <summary>Has a folder read from now on: by the stream reading it already, else by one started for it, or past
        /// <see cref="Few"/> for the folder it shares with another's; a stream started takes in those below its folder,
        /// each handing over what it heard before it stops.</summary>
        public static void Read(MacFileEvents events)
        {
            lock (Gate)
            {
                if (Open.Where(s => PathNames.Within(events.Folder, s.Top)).MaxBy(s => s.Top.Length) is { } reading)
                {
                    reading.Reading = reading.Reading.With([events]);
                    Volatile.Write(ref events.stream, reading);
                    return;
                }
                string top = Open.Count < Few ? events.Folder
                    : Open.Where(s => s.holds == 0).Select(s => MacFileEventFlags.Shared(events.Folder, s.Top, Home)).OfType<string>().MaxBy(f => f.Length) ?? events.Folder;
                List<Stream> taken = [.. Open.Where(s => s.holds == 0 && PathNames.Within(s.Top, top))];
                Start(top, [events, .. taken.SelectMany(s => s.Reading.Every)]);
                foreach (Stream old in taken)
                {
                    FSEventStreamFlushSync(old.stream);
                    Open.Remove(old);
                    old.End();
                }
            }
        }

        /// <summary>Lets go of a folder, and of its stream once it reads for no other.</summary>
        public static void Leave(MacFileEvents events)
        {
            lock (Gate)
            {
                if (Volatile.Read(ref events.stream) is not { } reading)
                {
                    return;
                }
                Volatile.Write(ref events.stream, null);
                reading.Reading = reading.Reading.Without(events);
                if (reading.Reading.Every.Count == 0)
                {
                    Open.Remove(reading);
                    reading.End();
                }
            }
        }

        /// <summary>Holds the reading of a folder's stream until let go of (<see cref="HoldReading"/>); a stream held is
        /// never taken into another, whose taking it in would wait on its reading.</summary>
        public static IDisposable Hold(MacFileEvents events)
        {
            Stream? held;
            lock (Gate)
            {
                held = Volatile.Read(ref events.stream);
                if (held is null)
                {
                    return new Resumed(null, IntPtr.Zero);
                }
                held.holds++;
                dispatch_retain(held.queue);
                dispatch_suspend(held.queue);
                return new Resumed(held, held.queue);
            }
        }

        /// <summary>A held reading, let go of once.</summary>
        private sealed class Resumed(Stream? stream, IntPtr queue) : IDisposable
        {
            private IntPtr held = queue;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref held, IntPtr.Zero) is var queue && queue != IntPtr.Zero)
                {
                    dispatch_resume(queue);
                    dispatch_release(queue);
                    lock (Gate)
                    {
                        stream!.holds--;
                    }
                }
            }
        }

        /// <summary>Starts a stream over a folder for those given, their events coming by it from then on; under the gate.</summary>
        private static void Start(string top, List<MacFileEvents> reading)
        {
            Stream started = new(top) { Reading = Readers.None.With(reading) };
            GCHandle self = GCHandle.Alloc(started);
            IntPtr path = CFStringCreateWithCString(IntPtr.Zero, top, Utf8);
            IntPtr paths = CFArrayCreate(IntPtr.Zero, &path, 1, IntPtr.Zero);
            try
            {
                Context context = new() { Info = GCHandle.ToIntPtr(self), Release = &Release };
                started.stream = FSEventStreamCreate(IntPtr.Zero, &Callback, &context, paths, SinceNow, 0, NoDefer | WatchRoot | FileEvents);
                if (started.stream == IntPtr.Zero)
                {
                    self.Free();
                    throw new IOException($"macOS made no stream of the file events under {top}");
                }
                started.queue = dispatch_queue_create($"Passenger's file events of {top}", IntPtr.Zero);
                FSEventStreamSetDispatchQueue(started.stream, started.queue);
                if (!FSEventStreamStart(started.stream))
                {
                    started.End();
                    throw new IOException($"macOS would not start the stream of the file events under {top}");
                }
            }
            finally
            {
                CFRelease(paths);
                CFRelease(path);
            }
            Open.Add(started);
            reading.ForEach(e => Volatile.Write(ref e.stream, started));
        }

        /// <summary>Stops the stream, its callback not called again once it returns; under the gate.</summary>
        private void End()
        {
            FSEventStreamStop(stream);
            FSEventStreamInvalidate(stream);
            // Its last release lets go of this object's handle (Release).
            FSEventStreamRelease(stream);
            dispatch_release(queue);
            Reading = Readers.None;
        }

        /// <summary>On the stream's queue: each event handed to the folders it reaches, by those above its path, and where it
        /// may say more (a drop, a folder holding one moved), each folder it says it of.</summary>
        [UnmanagedCallersOnly]
        private static void Callback(IntPtr stream, IntPtr info, nuint count, byte** paths, uint* flags, ulong* ids)
        {
            if (GCHandle.FromIntPtr(info).Target is not Stream read)
            {
                return;
            }
            Readers now = read.Reading;
            for (nuint i = 0; i < count; i++)
            {
                string path = Marshal.PtrToStringUTF8((IntPtr)paths[i]) ?? string.Empty;
                now.Hand(path, flags[i], read.Top);
            }
        }

        [UnmanagedCallersOnly]
        private static void Release(IntPtr info) => GCHandle.FromIntPtr(info).Free();
    }

    /// <summary>The folders a stream reads for, by each folder (named as macOS's events name them, case aside), made anew
    /// for each change so the stream's queue reads it without a lock.</summary>
    private sealed class Readers
    {
        public static readonly Readers None = new([]);

        private readonly Dictionary<string, MacFileEvents[]>.AlternateLookup<ReadOnlySpan<char>> byName;

        /// <summary>The lengths of the shortest and longest folder read for, between which a path's folders are looked up.</summary>
        private readonly int shortest;
        private readonly int longest;

        private Readers(List<MacFileEvents> every)
        {
            Every = every;
            byName = every.GroupBy(e => e.Folder, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase)
                .GetAlternateLookup<ReadOnlySpan<char>>();
            (shortest, longest) = every.Count == 0 ? (int.MaxValue, 0) : (every.Min(e => e.Folder.Length), every.Max(e => e.Folder.Length));
        }

        public List<MacFileEvents> Every { get; }

        public Readers With(List<MacFileEvents> more) => new([.. Every, .. more]);

        public Readers Without(MacFileEvents gone) => new([.. Every.Where(e => e != gone)]);

        /// <summary>Hands an event to each folder above its path up to <paramref name="top"/>, and to each folder it may say
        /// something more of (<see cref="MacFileEventFlags.Reaches"/>): a drop or the stream's folder moved to those it
        /// reaches alone, a folder renamed or deleted to those it held too.</summary>
        public void Hand(string path, uint flags, string top)
        {
            if (MacFileEventFlags.Drops(flags) || (flags & MacFileEventFlags.RootChanged) != 0)
            {
                Reaching(path, flags);
                return;
            }
            ReadOnlySpan<char> at = Path.TrimEndingDirectorySeparator(path.AsSpan());
            int least = Math.Max(Path.TrimEndingDirectorySeparator(top.AsSpan()).Length, shortest);
            for (int end = at.LastIndexOf('/'); end >= least && end > 0; end = at[..end].LastIndexOf('/'))
            {
                if (end <= longest && byName.TryGetValue(at[..end], out MacFileEvents[]? above))
                {
                    foreach (MacFileEvents each in above)
                    {
                        each.Take(path, flags);
                    }
                }
            }
            if ((flags & (MacFileEventFlags.ItemRemoved | MacFileEventFlags.ItemRenamed)) != 0 && (flags & MacFileEventFlags.ItemIsFile) == 0)
            {
                Reaching(path, flags);
            }
        }

        /// <summary>Hands an event to each folder it reaches past those above its path.</summary>
        private void Reaching(string path, uint flags)
        {
            foreach (MacFileEvents each in Every)
            {
                if (MacFileEventFlags.Reaches(path, flags, each.Folder))
                {
                    each.Take(path, flags);
                }
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
    private static partial void FSEventStreamFlushSync(IntPtr stream);

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
