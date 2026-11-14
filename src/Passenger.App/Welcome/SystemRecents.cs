using System.Runtime.InteropServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Passenger.App.State;
using Passenger.App.Updates;
using Passenger.Core;

namespace Passenger.App.Welcome;

/// <summary>The recent folders and solutions where the system lists them beside Passenger's icon: Windows' taskbar jump
/// list, macOS's Dock menu, and on Linux freedesktop's recently used list (<see cref="RecentlyUsed"/>), which KDE's task
/// manager shows as the icon's Recent Files. Only the person's Passenger follows them (<see cref="Start"/>), so a test,
/// an agent's or a screenshot's leaves the person's lists alone; on Windows and Linux only an installed one, whose
/// executable the lists' entries name.</summary>
internal static class SystemRecents
{
    private static StateDocument<RecentSolutionsState>? recent;

    /// <summary>Each showing's number: one that ends after a later one began shows nothing, so the newest list stands.</summary>
    private static int shows;

    /// <summary>Passenger as install.sh installed it, found once as following starts; null where it isn't.</summary>
    private static Task<RecentlyUsed.Program?>? installed;

    /// <summary>The recently used list's changes waiting, made in turn by one drain at a time.</summary>
    private static readonly Queue<Func<RecentlyUsed.Program, XDocument?, XDocument?>> Changes = new();

    private static bool draining;

    /// <summary>Follows the recent list from now on, and shows it as it stands.</summary>
    public static void Start(StateDocument<RecentSolutionsState> kept)
    {
        UiThread.Check();
        recent = kept;
        installed = OperatingSystem.IsLinux() ? BackgroundWork.Run(InstalledProgram) : null;
        Show();
    }

    /// <summary>A folder or solution just put at the top of the recent list.</summary>
    public static void Opened(string path)
    {
        UiThread.Check();
        if (recent is null)
        {
            return;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ChangeRecentlyUsed((program, list) => RecentlyUsed.Used(list, path, MimeTypeOf(path), program, now));
        Show();
    }

    /// <summary>A folder or solution taken off the recent list for good.</summary>
    public static void Forgot(string path)
    {
        UiThread.Check();
        if (recent is null)
        {
            return;
        }
        ChangeRecentlyUsed((program, list) => list is null ? null : RecentlyUsed.Forgotten(list, path, program.Name));
        Show();
    }

    private static IReadOnlyList<(string Path, string Title)> Listed(RecentSolutionsState state) =>
        RecentLists.Titled(state.Newest().Select(s => (s.Path, s.Entry.Name)), path => Directory.Exists(path) || File.Exists(path), RecentRow.Home);

    private static void Show()
    {
        if (OperatingSystem.IsWindows() && Installation.Installed)
        {
            IdeErrors.Observe(WriteJumpListAsync(recent!.Value, ++shows));
        }
        else if (OperatingSystem.IsMacOS())
        {
            IdeErrors.Observe(ShowInDockAsync(recent!.Value, ++shows));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WriteJumpListAsync(RecentSolutionsState now, int showing)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Passenger.exe has no path");
        IReadOnlyList<(string Path, string Title)> listed = await BackgroundWork.Run(() => Listed(now));
        UiThread.Check();
        if (showing != shows)
        {
            return;
        }
        IReadOnlyList<string> removed;
        try
        {
            removed = JumpList.Write(exe, listed);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            IdeErrors.Write($"Jump list: not written: {error.Message} (0x{error.HResult:X8})");
            return;
        }
        // What the person removed from the jump list they no longer want listed as recent anywhere.
        IReadOnlyList<string> forgotten = RecentLists.RemovedOf(recent!.Value.Newest().Select(s => s.Path), removed);
        if (forgotten.Count == 0)
        {
            return;
        }
        foreach (string path in forgotten)
        {
            IdeErrors.Write($"Jump list: {path} was removed from it, so it is forgotten as recent");
            recent.Set(recent.Value.Without(path));
        }
        foreach (MainWindow window in PassengerApp.Windows)
        {
            window.ShowRecent();
        }
        Show();
    }

    private static async Task ShowInDockAsync(RecentSolutionsState now, int showing)
    {
        IReadOnlyList<(string Path, string Title)> listed = await BackgroundWork.Run(() => Listed(now));
        if (showing != shows || Application.Current is not { } app)
        {
            return;
        }
        NativeMenu menu = new();
        foreach ((string path, string title) in listed)
        {
            NativeMenuItem item = new(title);
            item.Click += (_, _) => PassengerApp.OpenFromSystem([path]);
            menu.Add(item);
        }
        NativeDock.SetMenu(app, menu);
    }

    /// <summary>A change of the recently used list, made after those asked before it; none where Passenger isn't installed.</summary>
    private static void ChangeRecentlyUsed(Func<RecentlyUsed.Program, XDocument?, XDocument?> change)
    {
        if (installed is null)
        {
            return;
        }
        lock (Changes)
        {
            Changes.Enqueue(change);
            if (draining)
            {
                return;
            }
            draining = true;
        }
        IdeErrors.Observe(BackgroundWork.Run(DrainAsync));
    }

    private static async Task DrainAsync()
    {
        bool drained = false;
        try
        {
            RecentlyUsed.Program? program = await installed!;
            string? file = RecentlyUsed.FileIn(Environment.GetEnvironmentVariable);
            while (true)
            {
                Func<RecentlyUsed.Program, XDocument?, XDocument?>? change;
                lock (Changes)
                {
                    if (!Changes.TryDequeue(out change))
                    {
                        draining = false;
                        drained = true;
                        return;
                    }
                }
                if (program is not null && file is not null)
                {
                    Change(file, program, change);
                }
            }
        }
        finally
        {
            if (!drained)
            {
                lock (Changes)
                {
                    draining = false;
                }
            }
        }
    }

    private static void Change(string file, RecentlyUsed.Program program, Func<RecentlyUsed.Program, XDocument?, XDocument?> change)
    {
        if (!RecentlyUsed.TryRead(file, out XDocument? was))
        {
            IdeErrors.Write($"Recently used: {file} could not be read, so it is left as it is");
            return;
        }
        if (change(program, was) is not { } next || ReferenceEquals(next, was))
        {
            return;
        }
        try
        {
            RecentlyUsed.Write(file, next);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Recently used: {file} not written: {error.Message}");
        }
    }

    /// <summary>The mime type freedesktop's list gives a recent entry: a folder's, or a solution's or project's as
    /// Passenger's shared-mime-info file names them.</summary>
    private static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        _ when Directory.Exists(path) => RecentlyUsed.Folder,
        ".sln" => "application/x-ms-sln",
        ".slnx" => "application/x-ms-slnx",
        ".slnf" => "application/x-ms-slnf",
        ".csproj" => "application/x-ms-csproj",
        _ => "application/xml",
    };

    /// <summary>Passenger as install.sh installed it (its app folder beside its launcher, its desktop entry in the
    /// applications menu running that launcher): the program the recently used list names, by the entry's own Exec,
    /// which KDE matches as written; null for a build or an archive only unpacked.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static RecentlyUsed.Program? InstalledProgram()
    {
        if (Path.GetDirectoryName(Environment.ProcessPath) is not { } app || Path.GetFileName(app) != "app"
            || Path.GetDirectoryName(app) is not { } install)
        {
            return null;
        }
        List<string> data = [];
        if (RecentlyUsed.FileIn(Environment.GetEnvironmentVariable) is { } file)
        {
            data.Add(Path.GetDirectoryName(file)!);
        }
        data.AddRange(["/usr/local/share", "/usr/share"]);
        string launcher = PathNames.Canonical(Path.Combine(install, "passenger"));
        foreach (string folder in data)
        {
            string entry = Path.Combine(folder, "applications", LinuxUpdater.DesktopEntry + ".desktop");
            try
            {
                if (File.Exists(entry) && File.ReadLines(entry).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal)) is { } line
                    && line["Exec=".Length..] is var exec && RecentlyUsed.CommandOf(exec) is var command && Path.IsPathRooted(command)
                    && PathNames.Same(PathNames.Canonical(command), launcher))
                {
                    return new RecentlyUsed.Program(LinuxUpdater.DesktopEntry, exec);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                IdeErrors.Write($"Recently used: {entry} could not be read: {error.Message}");
            }
        }
        return null;
    }
}
