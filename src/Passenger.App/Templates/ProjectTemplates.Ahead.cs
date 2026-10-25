using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Templates;

/// <summary>The templates read ahead of New Solution and New Project, so the dialog opens with them listed: once the IDE
/// has started, in the background, and again when what they come from changes (dotnet new's installed packages, the
/// .NET install's SDKs and templates). What has been read is kept per SDK for a dialog to show at once. The process's,
/// as dotnet new's home is.</summary>
public static partial class ProjectTemplates
{
    /// <summary>How long the watch waits for the writes an install makes to end before reading again.</summary>
    private static readonly TimeSpan Settling = TimeSpan.FromMilliseconds(500);

    /// <summary>The newest catalog read of each SDK and hive, kept while it is read again, for a dialog to show at once.</summary>
    private static readonly ConcurrentDictionary<(string Sdk, string? Hive), TemplateCatalog> Newest = new();

    /// <summary>The SDK each folder's dotnet said last.</summary>
    private static readonly ConcurrentDictionary<string, string> Sdks = new(PathNames.Comparer);

    /// <summary>The folders the templates were last asked for, newest first: read again when what they come from changes.</summary>
    private static readonly List<string> Asked = [];

    /// <summary>The watches on what the templates come from, kept for the process's life.</summary>
    private static readonly List<FileSystemWatcher> Watches = [];

    private static volatile string? lastSdk;
    private static int preloaded;
    private static int watching;
    private static long changes;

    /// <summary>The templates were read again after an install or an SDK changed them; raised on the UI thread.</summary>
    public static event Action? Changed;

    /// <summary>The templates as last read for a folder's SDK, else for the SDK read last, at once: what a dialog opens
    /// with; null when none have been read.</summary>
    public static TemplateCatalog? Ready(string folder)
    {
        foreach (string? sdk in (string?[])[Sdks.GetValueOrDefault(folder), lastSdk])
        {
            if (sdk is not null && Newest.TryGetValue((sdk, Hive), out TemplateCatalog? catalog))
            {
                return catalog;
            }
        }
        return null;
    }

    /// <summary>The templates as dotnet new last listed them for a folder's SDK, from its cache without listing them
    /// again: what a dialog opened before any read is done shows meanwhile; null when dotnet new keeps none for it.</summary>
    public static async Task<TemplateCatalog?> LastListedAsync(string folder)
    {
        string sdk = await SdkAsync(folder);
        if (Newest.TryGetValue((sdk, Hive), out TemplateCatalog? read))
        {
            return read;
        }
        return TemplateRoot(Hive) is { } root && CacheFile(root, sdk) is { } cache ? await ParseAsync(cache, sdk) : null;
    }

    /// <summary>Reads the templates of the SDK a folder builds with ahead of the dialog, once the window is up and below
    /// its input and drawing, once a process: after that, the watch keeps them fresh.</summary>
    public static void Preload(string folder)
    {
        if (Interlocked.Exchange(ref preloaded, 1) == 1)
        {
            return;
        }
        Dispatcher.UIThread.Post(() => IdeErrors.Observe(PreloadAsync(folder)), DispatcherPriority.Background);
    }

    private static async Task PreloadAsync(string folder)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            // Not background work: nothing is drawn from it, and holding a settle would outlast a chord's second key.
            TemplateCatalog catalog = await Task.Run(() => LoadAsync(folder));
            IdeErrors.Write($"New Solution's templates read ahead in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:0.00} s: "
                + $"{catalog.Templates.Count} templates in {catalog.Families.Count} families, SDK {catalog.Sdk}");
        }
        catch (Exception error) when (Unreadable(error))
        {
            // The dialog says why when it is opened; until then, a line in the log.
            Interlocked.Exchange(ref preloaded, 0);
            IdeErrors.Write($"The project templates could not be read ahead of New Solution: {error.Message}");
        }
    }

    /// <summary>What a read of the templates fails with when dotnet, its cache or a template's files cannot be read.</summary>
    public static bool Unreadable(Exception error) =>
        error is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or TimeoutException
            or KeyNotFoundException or System.ComponentModel.Win32Exception;

    /// <summary>A folder the templates were asked for, read again on a change; the last four are kept.</summary>
    private static void Remember(string folder)
    {
        lock (Asked)
        {
            Asked.Remove(folder);
            Asked.Insert(0, folder);
            if (Asked.Count > 4)
            {
                Asked.RemoveAt(Asked.Count - 1);
            }
        }
    }

    /// <summary>Watches, once there is something read: dotnet new's packages.json in its home (or the hive), which an
    /// install or uninstall rewrites, and the .NET install's sdk, templates and template-packs folders, which an SDK or a
    /// workload changes. dotnet new list itself writes none of them.</summary>
    private static void Watch()
    {
        if (Interlocked.Exchange(ref watching, 1) == 1)
        {
            return;
        }
        List<(string Folder, string Filter)> places = [];
        if (TemplateRoot(Hive) is { } root)
        {
            places.Add((root, "packages.json"));
        }
        try
        {
            string dotnet = Path.GetDirectoryName(PathNames.Canonical(Dotnet.Host()))!;
            places.AddRange(((string[])["sdk", "templates", "template-packs"]).Select(f => (Path.Combine(dotnet, f), "*")));
        }
        catch (FileNotFoundException)
        {
            // No .NET to watch: the read says so.
        }
        lock (Watches)
        {
            foreach ((string folder, string filter) in places.Where(p => Directory.Exists(p.Folder)))
            {
                try
                {
                    FileSystemWatcher watch = new(folder, filter)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    watch.Changed += Seen;
                    watch.Created += Seen;
                    watch.Deleted += Seen;
                    watch.Renamed += Seen;
                    watch.EnableRaisingEvents = true;
                    Watches.Add(watch);
                }
                catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    IdeErrors.Write($"New Solution's templates are not watched in {folder}: {error.Message}");
                }
            }
        }
    }

    /// <summary>Something the templates come from changed: once its writes have settled, what was read is let go and the
    /// folders asked for are read again, then the dialogs are told.</summary>
    private static void Seen(object? sender, FileSystemEventArgs e)
    {
        long change = Interlocked.Increment(ref changes);
        // Not background work, as a read ahead isn't: a template installed while a window waits for a key holds nothing.
        IdeErrors.Observe(Task.Run(async () =>
        {
            await Task.Delay(Settling);
            if (Interlocked.Read(ref changes) != change)
            {
                return;
            }
            Forget();
            Sdks.Clear();
            Dotnet.ForgetSdks();
            string[] folders;
            lock (Asked)
            {
                folders = [.. Asked];
            }
            foreach (string folder in folders)
            {
                if (!Directory.Exists(folder))
                {
                    lock (Asked)
                    {
                        Asked.Remove(folder);
                    }
                    continue;
                }
                try
                {
                    await LoadAsync(folder);
                }
                catch (Exception error) when (Unreadable(error))
                {
                    IdeErrors.Write($"The project templates could not be read again after a change: {error.Message}");
                }
            }
            UiThread.Post(() => Changed?.Invoke());
        }));
    }
}
