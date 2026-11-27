using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>The system's file events for a folder, on macOS and Windows, where one watcher reads a folder and all below it:
/// read through a watcher the process shares among its folders (<see cref="Source"/>), whose events each go to the folders
/// they reach and are handed over in turn, as background work, as <see cref="FolderEvents"/> reads them, the system's word
/// that it dropped changes among them. On macOS that is an FSEvents stream of Passenger's own whose callback only queues
/// what it is given, so a reader held up never has the system drop the stream's events, as the system allows a process 512
/// and every program together 1,024; on Windows .NET's watcher, each with a handle and a buffer of its own. Linux's inotify
/// watches each folder alone, so there a folder is watched on its own. A <see cref="WatchedTree"/>'s alone.</summary>
internal sealed unsafe partial class SharedWatch : IDisposable
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
    private readonly Action<FolderEvent> heard;
    private readonly ConcurrentQueue<(string Path, uint Flags)> queued = new();

    /// <summary>The watcher the folder's events come by, under <see cref="Source.Gate"/>; null once disposed, or once its
    /// watcher failed and could not be started again.</summary>
    private Source? source;
    private int draining;
    private int disposed;

    /// <summary>Reads the events under <paramref name="folder"/> (canonical) from now on, through a watcher already reading
    /// it or one started for it, each handed to <paramref name="heard"/> in turn, off the system's thread; an <see
    /// cref="IOException"/> where the system would not start one.</summary>
    public SharedWatch(string folder, bool recursive, Action<FolderEvent> heard)
    {
        Folder = folder;
        this.recursive = recursive;
        this.heard = heard;
        Source.Read(this);
    }

    /// <summary>The folder whose events are read.</summary>
    public string Folder { get; }

    /// <summary>The folder each watcher the process holds open reads from.</summary>
    public static IReadOnlyList<string> Watchers => Source.Folders;

    /// <summary>Holds the reading of macOS's stream the folder's events come by, as a reader held up by a loaded system is,
    /// until let go of: what comes meanwhile, for every folder it reads, waits in the system's queue for the stream, which
    /// may drop it and say so. For a test.</summary>
    public IDisposable HoldReading() => Source.Hold(this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Source.Leave(this);
        }
    }

    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>On the watcher's thread: an event that may reach the folder, queued as given, and the queue handed over.</summary>
    private void Take(string path, uint flags)
    {
        if (!IsDisposed)
        {
            queued.Enqueue((path, flags));
            Drain();
        }
    }

    /// <summary>Hands what was queued over in turn, one drain at a time, as background work; an event touching a folder the
    /// IDE moves waits there, with those after it, until the folder has gone (<see cref="MovingFolders"/>).</summary>
    private void Drain()
    {
        if (Interlocked.Exchange(ref draining, 1) == 1)
        {
            return;
        }
        IdeErrors.Observe(BackgroundWork.Run(() =>
        {
            List<FolderEvent> read = [];
            while (true)
            {
                while (queued.TryPeek(out (string Path, uint Flags) next))
                {
                    using IDisposable? reading = MovingFolders.Begin(next.Path, Drain);
                    if (reading is null)
                    {
                        break;
                    }
                    queued.TryDequeue(out _);
                    read.Clear();
                    FolderEvents.Read(next.Path, next.Flags, Folder, recursive, From, There, read);
                    foreach (FolderEvent each in read)
                    {
                        if (IsDisposed)
                        {
                            return;
                        }
                        heard(each);
                    }
                }
                Volatile.Write(ref draining, 0);
                // One waiting for a folder to go is drained again once it has; one let go of since is drained now.
                if (!queued.TryPeek(out (string Path, uint Flags) first) || MovingFolders.Touched(first.Path) || Interlocked.Exchange(ref draining, 1) == 1)
                {
                    return;
                }
            }
        }));
    }

    /// <summary>The folder the watcher the events come by reads from, the folder itself where there is none.</summary>
    private string From => Volatile.Read(ref source)?.Top ?? Folder;

    /// <summary>Whether something stands at a path now: a file where the event says it was one.</summary>
    private static bool There(string path, bool file) => File.Exists(path) || !file && Directory.Exists(path);

    /// <summary>One of the process's watchers of a folder and all below it, and the watched folders it reads for, each event
    /// handed to those it reaches. A folder below a watcher's is read through it; past <see cref="Few"/> watchers, one below
    /// none is read through a watcher over the deepest folder it shares with another's (<see cref="FolderEvents.Shared"/>),
    /// which takes in the watchers below it, so hundreds of folders apart (a language server's packages) take a few. A
    /// watcher ends with the last folder it reads for; one whose watch failed is started again.</summary>
    private abstract class Source(string top)
    {
        /// <summary>How many watchers the process starts before it shares one among folders apart.</summary>
        private const int Few = 16;

        /// <summary>What the watchers, and which watcher each folder's events come by, are held by.</summary>
        public static readonly Lock Gate = new();

        private static readonly List<Source> Open = [];

        private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? PathNames.Canonical(home) : "/";

        /// <summary>How many holds on the reading are let go of not yet, under the gate.</summary>
        private int holds;

        /// <summary>The folder read, with all below it.</summary>
        public string Top { get; } = top;

        /// <summary>The folders read for, which the watcher's thread reads without the gate; changed under it.</summary>
        protected Readers Reading { get; } = new();

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

        /// <summary>Has a folder read from now on: by the watcher reading it already, else by one started for it, or past
        /// <see cref="Few"/> for the folder it shares with another's; a watcher started takes in those below its folder,
        /// each handing over what it heard before it stops.</summary>
        public static void Read(SharedWatch events)
        {
            lock (Gate)
            {
                if (Open.Where(s => PathNames.Within(events.Folder, s.Top)).MaxBy(s => s.Top.Length) is { } reading)
                {
                    reading.Reading.Add(events);
                    Volatile.Write(ref events.source, reading);
                    return;
                }
                string top = Open.Count < Few ? events.Folder
                    : Open.Where(s => s.holds == 0).Select(s => FolderEvents.Shared(events.Folder, s.Top, Home)).OfType<string>().MaxBy(f => f.Length) ?? events.Folder;
                List<Source> taken = [.. Open.Where(s => s.holds == 0 && PathNames.Within(s.Top, top))];
                Start(top, [events, .. taken.SelectMany(s => s.Reading.Every)]);
                foreach (Source old in taken)
                {
                    Open.Remove(old);
                    old.Retire();
                }
            }
        }

        /// <summary>Lets go of a folder, and of its watcher once it reads for no other.</summary>
        public static void Leave(SharedWatch events)
        {
            lock (Gate)
            {
                if (Volatile.Read(ref events.source) is not { } reading)
                {
                    return;
                }
                Volatile.Write(ref events.source, null);
                reading.Reading.Remove(events);
                if (reading.Reading.Count == 0 && Open.Remove(reading))
                {
                    reading.End();
                }
            }
        }

        /// <summary>Holds the reading of a folder's watcher until let go of (<see cref="HoldReading"/>); a watcher held is
        /// never taken into another, whose taking it in would wait on its reading.</summary>
        public static IDisposable Hold(SharedWatch events)
        {
            lock (Gate)
            {
                if (Volatile.Read(ref events.source) is not { } held || held.Suspend() is not { } resumed)
                {
                    return new Resumed(null, null);
                }
                held.holds++;
                return new Resumed(held, resumed);
            }
        }

        /// <summary>A held reading, let go of once.</summary>
        private sealed class Resumed(Source? source, IDisposable? resumed) : IDisposable
        {
            private IDisposable? held = resumed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref held, null) is { } resuming)
                {
                    resuming.Dispose();
                    lock (Gate)
                    {
                        source!.holds--;
                    }
                }
            }
        }

        /// <summary>Starts a watcher over a folder for those given, their events coming by it from then on; under the gate.</summary>
        private static void Start(string top, List<SharedWatch> reading)
        {
            Source started = OperatingSystem.IsMacOS() ? new MacStream(top) : new WindowsWatcher(top);
            reading.ForEach(started.Reading.Add);
            started.Begin();
            Open.Add(started);
            reading.ForEach(e => Volatile.Write(ref e.source, started));
        }

        /// <summary>Starts the system's watch, its events handed over (<see cref="Hand"/>) from when it returns; an <see
        /// cref="IOException"/> where the system would not start it.</summary>
        protected abstract void Begin();

        /// <summary>Stops the system's watch, its events not handed over again once it returns; under the gate.</summary>
        protected abstract void End();

        /// <summary>Stops a watcher another took in, once it has handed over what it heard before that one began; under the gate.</summary>
        protected abstract void Retire();

        /// <summary>Holds the system's reading until the answer is disposed; null where there is none to hold.</summary>
        protected virtual IDisposable? Suspend() => null;

        /// <summary>On the watcher's thread: an event handed to the folders it reaches.</summary>
        protected void Hand(string path, uint flags) => Reading.Hand(path, flags, Top);

        /// <summary>The system's watch failed, which hears nothing more: the folders it read for are read by a watcher started
        /// again where the folder is still there, else by none, and each told of the failure.</summary>
        protected void Failing()
        {
            List<SharedWatch> reading;
            lock (Gate)
            {
                if (!Open.Remove(this))
                {
                    return;
                }
                reading = Reading.Every;
                End();
                reading.ForEach(e => Volatile.Write(ref e.source, null));
                if (Directory.Exists(Top))
                {
                    try
                    {
                        Start(Top, reading);
                    }
                    catch (IOException error)
                    {
                        IdeErrors.Write($"The watch of {Top} failed and could not be started again: {error.Message}");
                    }
                }
            }
            reading.ForEach(e => e.Take(Top, FolderEvents.Failed));
        }
    }

    /// <summary>One of macOS's FSEvents streams, on a dispatch queue of its own.</summary>
    private sealed class MacStream(string top) : Source(top)
    {
        private IntPtr stream;
        private IntPtr queue;

        protected override void Begin()
        {
            GCHandle self = GCHandle.Alloc(this);
            IntPtr path = CFStringCreateWithCString(IntPtr.Zero, Top, Utf8);
            IntPtr paths = CFArrayCreate(IntPtr.Zero, &path, 1, IntPtr.Zero);
            try
            {
                Context context = new() { Info = GCHandle.ToIntPtr(self), Release = &Release };
                stream = FSEventStreamCreate(IntPtr.Zero, &Callback, &context, paths, SinceNow, 0, NoDefer | WatchRoot | FileEvents);
                if (stream == IntPtr.Zero)
                {
                    self.Free();
                    throw new IOException($"macOS made no stream of the file events under {Top}");
                }
                queue = dispatch_queue_create($"Passenger's file events of {Top}", IntPtr.Zero);
                FSEventStreamSetDispatchQueue(stream, queue);
                if (!FSEventStreamStart(stream))
                {
                    End();
                    throw new IOException($"macOS would not start the stream of the file events under {Top}");
                }
            }
            finally
            {
                CFRelease(paths);
                CFRelease(path);
            }
        }

        protected override void End()
        {
            FSEventStreamStop(stream);
            FSEventStreamInvalidate(stream);
            // Its last release lets go of this object's handle (Release).
            FSEventStreamRelease(stream);
            dispatch_release(queue);
            Reading.Clear();
        }

        protected override void Retire()
        {
            FSEventStreamFlushSync(stream);
            End();
        }

        protected override IDisposable? Suspend()
        {
            dispatch_retain(queue);
            dispatch_suspend(queue);
            return new Suspended(queue);
        }

        /// <summary>A queue suspended, resumed once.</summary>
        private sealed class Suspended(IntPtr queue) : IDisposable
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

        /// <summary>On the stream's queue: each event handed to the folders it reaches.</summary>
        [UnmanagedCallersOnly]
        private static void Callback(IntPtr stream, IntPtr info, nuint count, byte** paths, uint* flags, ulong* ids)
        {
            if (GCHandle.FromIntPtr(info).Target is not MacStream read)
            {
                return;
            }
            for (nuint i = 0; i < count; i++)
            {
                read.Hand(Marshal.PtrToStringUTF8((IntPtr)paths[i]) ?? string.Empty, flags[i]);
            }
        }

        [UnmanagedCallersOnly]
        private static void Release(IntPtr info) => GCHandle.FromIntPtr(info).Free();
    }

    /// <summary>One of .NET's watchers on Windows, of the folder and all below it, its events read into FSEvents' flags: a
    /// rename as the old name gone and the new one moved in, its buffer's overflow as a drop, any other error as the watch
    /// failed.</summary>
    private sealed class WindowsWatcher(string top) : Source(top)
    {
        /// <summary>How long a watcher another took in reads on beside it, for what it heard before that one began: .NET's
        /// watcher has nothing to hand it over at once.</summary>
        private static readonly TimeSpan Overlap = TimeSpan.FromSeconds(1);

        private FileSystemWatcher? watcher;

        protected override void Begin()
        {
            FileSystemWatcher made;
            try
            {
                made = new FileSystemWatcher(Top)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
            }
            catch (ArgumentException gone)
            {
                throw new IOException($"{Top} is not there to watch", gone);
            }
            made.Created += (_, e) => Hand(e.FullPath, FolderEvents.ItemCreated);
            made.Deleted += (_, e) => Hand(e.FullPath, FolderEvents.ItemRemoved);
            made.Changed += (_, e) => Hand(e.FullPath, FolderEvents.ItemModified);
            made.Renamed += (_, e) =>
            {
                Hand(e.OldFullPath, FolderEvents.ItemRemoved);
                Hand(e.FullPath, FolderEvents.ItemRenamed);
            };
            made.Error += (_, e) =>
            {
                if (e.GetException() is InternalBufferOverflowException)
                {
                    Hand(Top, FolderEvents.MustScanSubDirs | FolderEvents.UserDropped);
                    return;
                }
                Failing();
            };
            watcher = made;
            try
            {
                made.EnableRaisingEvents = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                made.Dispose();
                throw new IOException($"Windows would not watch {Top}: {error.Message}", error);
            }
        }

        protected override void End()
        {
            watcher?.Dispose();
            Reading.Clear();
        }

        protected override void Retire()
        {
            // Not held: nothing waits on the old watcher going, and it hands over nothing of its own after.
            IdeErrors.Observe(BackgroundWork.Unheld(() => Task.Delay(Overlap).ContinueWith(_ =>
            {
                lock (Gate)
                {
                    End();
                }
            }, TaskScheduler.Default)));
        }
    }

    /// <summary>The folders a watcher reads for, by each folder (named as the system's events name them, case aside), which
    /// the watcher's thread reads without a lock: each folder's readers replaced whole as one comes or goes, under the gate,
    /// so one more costs what its folder's readers do, never all of them again.</summary>
    private sealed class Readers
    {
        private readonly ConcurrentDictionary<string, SharedWatch[]> byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SharedWatch[]>.AlternateLookup<ReadOnlySpan<char>> lookup;

        /// <summary>Every folder read for, under the gate.</summary>
        private readonly HashSet<SharedWatch> every = [];

        /// <summary>The lengths of the shortest and longest folder read for, between which a path's folders are looked up;
        /// widened as folders come, never narrowed until cleared.</summary>
        private int shortest = int.MaxValue;
        private int longest;

        public Readers() => lookup = byName.GetAlternateLookup<ReadOnlySpan<char>>();

        /// <summary>How many folders are read for, under the gate.</summary>
        public int Count => every.Count;

        /// <summary>Every folder read for, under the gate.</summary>
        public List<SharedWatch> Every => [.. every];

        /// <summary>A folder read for from now on, under the gate.</summary>
        public void Add(SharedWatch events)
        {
            every.Add(events);
            byName.AddOrUpdate(events.Folder, static (_, added) => [added], static (_, were, added) => [.. were, added], events);
            Volatile.Write(ref shortest, Math.Min(shortest, events.Folder.Length));
            Volatile.Write(ref longest, Math.Max(longest, events.Folder.Length));
        }

        /// <summary>A folder read for no longer, under the gate.</summary>
        public void Remove(SharedWatch gone)
        {
            every.Remove(gone);
            if (byName.TryGetValue(gone.Folder, out SharedWatch[]? were))
            {
                SharedWatch[] left = [.. were.Where(e => e != gone)];
                if (left.Length == 0)
                {
                    byName.TryRemove(gone.Folder, out _);
                }
                else
                {
                    byName[gone.Folder] = left;
                }
            }
        }

        /// <summary>No folder read for any more, under the gate: what the watcher hands over after reaches none.</summary>
        public void Clear()
        {
            every.Clear();
            byName.Clear();
            Volatile.Write(ref shortest, int.MaxValue);
            Volatile.Write(ref longest, 0);
        }

        /// <summary>Hands an event to each folder above its path up to <paramref name="top"/>, and to each folder it may say
        /// something more of (<see cref="FolderEvents.Reaches"/>): the watcher's own word (a drop, its folder moved, its
        /// failure) to those it reaches alone, a folder renamed or deleted to those it held too.</summary>
        public void Hand(string path, uint flags, string top)
        {
            if (FolderEvents.Says(flags))
            {
                Reaching(path, flags);
                return;
            }
            int least = Math.Max(Path.TrimEndingDirectorySeparator(top.AsSpan()).Length, Volatile.Read(ref shortest));
            int most = Volatile.Read(ref longest);
            foreach (ReadOnlySpan<char> above in PathNames.Above(path))
            {
                if (above.Length < least || above.Length == 0)
                {
                    break;
                }
                if (above.Length <= most && lookup.TryGetValue(above, out SharedWatch[]? readers))
                {
                    foreach (SharedWatch each in readers)
                    {
                        each.Take(path, flags);
                    }
                }
            }
            if ((flags & (FolderEvents.ItemRemoved | FolderEvents.ItemRenamed)) != 0 && (flags & FolderEvents.ItemIsFile) == 0)
            {
                Reaching(path, flags);
            }
        }

        /// <summary>Hands an event to each folder it reaches past those above its path.</summary>
        private void Reaching(string path, uint flags)
        {
            foreach (KeyValuePair<string, SharedWatch[]> folder in byName)
            {
                foreach (SharedWatch each in folder.Value)
                {
                    if (FolderEvents.Reaches(path, flags, each.Folder))
                    {
                        each.Take(path, flags);
                    }
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
