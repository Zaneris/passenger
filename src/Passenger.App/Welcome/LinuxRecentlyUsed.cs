using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using Passenger.App.State;
using Passenger.App.Updates;
using Passenger.Core;

namespace Passenger.App.Welcome;

/// <summary>freedesktop's recently used list (<see cref="RecentlyUsed"/>) kept in step with the recent folders and
/// solutions: each opened since the list last followed them is a use of Passenger's, and each let go of loses Passenger's
/// use, so KDE lists them under Passenger's icon and file managers as Recent. Only a copy install.sh installed writes it,
/// naming its desktop entry, whose Exec KDE knows the icon by; adding follows the desktop's own switch and size, read at
/// each write. The writes go in turn, off the window's thread.</summary>
public static class LinuxRecentlyUsed
{
    /// <summary>A list Passenger follows, the program it names, and how it is held (null: as the desktop holds it).</summary>
    public sealed record Place(string File, RecentlyUsed.Program Program, RecentlyUsed.Keeping? Keeping);

    /// <summary>The list a test follows in place of the person's, as an installed Passenger a person sees follows it.</summary>
    public static Place? StandIn { get; set; }

    /// <summary>Each recent list as the recently used list last followed it.</summary>
    private static readonly ConditionalWeakTable<StateDocument<RecentSolutionsState>, RecentSolutionsState> Followed = new();

    /// <summary>One write at a time, in the order asked.</summary>
    private static readonly SemaphoreSlim Turn = new(1, 1);

    /// <summary>Passenger as install.sh installed it, found once; null where it isn't.</summary>
    private static Task<Place?>? installed;

    /// <summary>Following <paramref name="recent"/> starts from it as it stands, where it has not begun: what a start
    /// reopens comes after.</summary>
    public static void Begin(StateDocument<RecentSolutionsState> recent)
    {
        UiThread.Check();
        Followed.TryAdd(recent, recent.Value);
    }

    /// <summary>What changed in <paramref name="recent"/> since it was last followed goes into the list.</summary>
    public static void Follow(StateDocument<RecentSolutionsState> recent)
    {
        UiThread.Check();
        RecentSolutionsState now = recent.Value;
        RecentSolutionsState before = Followed.TryGetValue(recent, out RecentSolutionsState? was) ? was : now;
        Followed.AddOrUpdate(recent, now);
        (IReadOnlyList<(string Path, DateTimeOffset When)> opened, IReadOnlyList<string> gone) = RecentlyUsed.Between(Stamps(before), Stamps(now));
        if (opened.Count == 0 && gone.Count == 0)
        {
            return;
        }
        Task<Place?> place = StandIn is { } standIn ? Task.FromResult<Place?>(standIn) : installed ??= BackgroundWork.Run(Installed);
        IdeErrors.Observe(BackgroundWork.Run(() => WriteAsync(place, opened, gone)));
    }

    private static Dictionary<string, DateTimeOffset> Stamps(RecentSolutionsState state) =>
        state.Solutions.ToDictionary(s => s.Key, s => s.Value.Opened, StringComparer.Ordinal);

    private static async Task WriteAsync(Task<Place?> finding, IReadOnlyList<(string Path, DateTimeOffset When)> opened, IReadOnlyList<string> gone)
    {
        if (await finding is not { } place)
        {
            return;
        }
        RecentlyUsed.Keeping keeping = place.Keeping ?? await DesktopKeepingAsync();
        await Turn.WaitAsync();
        try
        {
            Write(place, keeping, opened, gone);
        }
        finally
        {
            Turn.Release();
        }
    }

    private static void Write(Place place, RecentlyUsed.Keeping keeping, IReadOnlyList<(string Path, DateTimeOffset When)> opened, IReadOnlyList<string> gone)
    {
        XDocument? was;
        try
        {
            using FileStream stream = new(place.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            was = RecentlyUsed.Read(stream);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            was = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
            IdeErrors.Write($"Recently used: {place.File} could not be read, so it is left as it is: {error.Message}");
            return;
        }
        XDocument? list = was;
        if (keeping.Adds)
        {
            foreach ((string path, DateTimeOffset when) in opened)
            {
                list = RecentlyUsed.Used(list, path, RecentlyUsed.MimeTypeOf(path, Directory.Exists(path)), place.Program, when);
            }
        }
        foreach (string path in gone)
        {
            list = list is null ? null : RecentlyUsed.Forgotten(list, path, place.Program.Name);
        }
        if (list is null || ReferenceEquals(list, was))
        {
            return;
        }
        list = RecentlyUsed.Kept(list, keeping, DateTimeOffset.UtcNow);
        try
        {
            string target = AtomicFile.Target(place.File);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            AtomicFile.Write(target, RecentlyUsed.Bytes(list), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Recently used: {place.File} not written: {error.Message}");
        }
    }

    /// <summary>Passenger as install.sh installed it (its app folder beside its launcher, its desktop entry in the data home
    /// or the system's running that launcher), with the list in the data home; null for a build or an archive unpacked.</summary>
    private static Place? Installed()
    {
        if (!OperatingSystem.IsLinux() || RecentlyUsed.FileIn(Environment.GetEnvironmentVariable) is not { } file
            || Path.GetDirectoryName(Environment.ProcessPath) is not { } app || Path.GetFileName(app) != "app" || Path.GetDirectoryName(app) is not { } install)
        {
            return null;
        }
        string launcher = PathNames.Canonical(Path.Combine(install, "passenger"));
        foreach (string folder in (string[])[Path.GetDirectoryName(file)!, "/usr/local/share", "/usr/share"])
        {
            string entry = Path.Combine(folder, "applications", LinuxUpdater.DesktopEntry + ".desktop");
            try
            {
                if (File.Exists(entry) && File.ReadLines(entry).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal)) is { } line
                    && line["Exec=".Length..] is var exec && RecentlyUsed.CommandOf(exec) is var command && Path.IsPathRooted(command)
                    && PathNames.Same(PathNames.Canonical(command), launcher))
                {
                    return new Place(file, new RecentlyUsed.Program(LinuxUpdater.DesktopEntry, exec), null);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                IdeErrors.Write($"Recently used: {entry} could not be read: {error.Message}");
            }
        }
        IdeErrors.Write($"Recently used: no desktop entry runs {launcher}, so this Passenger, not one install.sh installed, lists nothing as recently used");
        return null;
    }

    /// <summary>How the desktop holds the list: KDE's settings in a KDE session, else GNOME's where gsettings has them,
    /// else GTK's settings files.</summary>
    private static async Task<RecentlyUsed.Keeping> DesktopKeepingAsync()
    {
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } set && Path.IsPathRooted(set) ? set
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        if ((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase))
        {
            return RecentlyUsed.Keeping.OfKde([Text(Path.Combine(config, "kdeglobals")), Text("/etc/xdg/kdeglobals")]);
        }
        if (Programs.OnPath("gsettings") is { } gsettings)
        {
            try
            {
                ChildResult listed = await ChildProcesses.RunAsync(ChildProcesses.Command(gsettings, ["list-recursively", "org.gnome.desktop.privacy"]),
                    limit: Waits.Of(TimeSpan.FromSeconds(10)));
                if (listed.Succeeded && RecentlyUsed.Keeping.OfGnome(listed.Output) is { } gnome)
                {
                    return gnome;
                }
            }
            catch (Exception error) when (ChildFailures.Of(error))
            {
                IdeErrors.Write($"Recently used: GNOME's privacy settings could not be read, so GTK's are taken: {error.Message}");
            }
        }
        string[] folders = [Path.Combine(config, "gtk-4.0"), Path.Combine(config, "gtk-3.0"), "/etc/xdg/gtk-4.0", "/etc/xdg/gtk-3.0", "/etc/gtk-3.0"];
        return RecentlyUsed.Keeping.OfGtk(folders.Select(folder => Text(Path.Combine(folder, "settings.ini"))));
    }

    /// <summary>A settings file's text, or null where there is none to read.</summary>
    private static string? Text(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
