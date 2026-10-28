using Passenger.App.State;

namespace Passenger.App.Settings;

/// <summary>The settings as every window that follows them has them: one per state folder in the process (the person's
/// windows all follow theirs, kept in its settings.json), or one of a window's own that keeps nothing. A change reads
/// the file again first, so what another Passenger wrote meanwhile stays, puts over it only the values the change makes
/// differ, is written through the store, and raises <see cref="Changed"/>; settings.json changed on disk by another
/// Passenger is read again as it lands, as its watcher overflows, and as a window comes in front once it moved (<see
/// cref="LookAgain"/>). The theme, the keymap and the logs' switches are the process's: put in force here, once, before
/// any window hears. UI thread only.</summary>
public sealed class SettingsStore : IDisposable
{
    private readonly StateDocument<AppSettings>? document;
    private readonly FileSystemWatcher? watcher;
    private AppSettings value;
    private bool inForce;
    private bool disposed;

    /// <summary>settings.json's last write and length as it was last read again; none before the first time.</summary>
    private (DateTime Written, long Length) read;

    private SettingsStore(StateDocument<AppSettings>? document)
    {
        this.document = document;
        value = document?.Value ?? new AppSettings();
        if (document is null || Path.GetDirectoryName(document.Path) is not { } folder)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(folder);
            watcher = new FileSystemWatcher(folder, Path.GetFileName(document.Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            // Written by a temporary file moved over it, so a rename onto the name counts as well as a write.
            watcher.Changed += (_, _) => UiThread.Post(ReadAgain);
            watcher.Created += (_, _) => UiThread.Post(ReadAgain);
            watcher.Renamed += (_, e) =>
            {
                if (string.Equals(e.Name, watcher.Filter, StringComparison.OrdinalIgnoreCase))
                {
                    UiThread.Post(ReadAgain);
                }
            };
            // Overflowed, or failed: a change it lost is in the file.
            watcher.Error += (_, _) => UiThread.Post(ReadAgain);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            // Unwatched, another Passenger's change is still kept, and read the next time this one changes something.
            IdeErrors.Write($"Settings: {document.Path} is not watched for other Passengers' changes ({error.Message})");
            watcher?.Dispose();
            watcher = null;
        }
    }

    /// <summary>Settings kept in a state folder's settings.json.</summary>
    internal static SettingsStore Kept(StateDocument<AppSettings> document) => new(document);

    /// <summary>A window's own settings, kept nowhere: the defaults, changed only by that window.</summary>
    public static SettingsStore Unkept() => new(null);

    /// <summary>The settings in force.</summary>
    public AppSettings Value => value;

    /// <summary>Whether the settings are kept in a file (the person's), rather than a window's own.</summary>
    public bool IsKept => document is not null;

    /// <summary>The system's watcher of settings.json; null for a window's own settings, or a file that could not be watched.</summary>
    public FileSystemWatcher? Watcher => watcher;

    /// <summary>The settings changed, by a window of this process or by another Passenger; raised once the process's
    /// share (theme, keymap, logs) is in force.</summary>
    public event EventHandler<SettingsChange>? Changed;

    /// <summary>Changes the settings, from what they are now as just read again.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        UiThread.Check();
        if (disposed)
        {
            return;
        }
        ReadAgain();
        AppSettings before = value;
        AppSettings after = change(before).Clamped();
        value = after;
        document?.Set(after);
        Raise(before, after);
    }

    /// <summary>What a window or its Settings dialog changed from the settings it had: only the values that differ
    /// between the two put over the settings as they are now, so another window's change meanwhile stays.</summary>
    public void Apply(AppSettings from, AppSettings to) => Update(now => ReferenceEquals(now, from) ? to : StateFile.Merged(now, from, to));

    /// <summary>The process's share of the settings as first read put in force, the first time a window keeps them: the
    /// theme (over which a theme named on the command line is put after), the person's shortcuts and the logs' switches.
    /// A change puts its own in force as it comes.</summary>
    public void PutInForce()
    {
        if (inForce)
        {
            return;
        }
        inForce = true;
        InForce(null, value);
    }

    /// <summary>Writes a change still waiting, now.</summary>
    public void Flush() => document?.Flush();

    /// <summary>Reads settings.json again if it moved since it was last read, as a window comes in front: another
    /// Passenger's change the watcher never heard, as the system may drop one without a word, is followed then.</summary>
    public void LookAgain()
    {
        UiThread.Check();
        if (document is not null && Stamp(document.Path) != read)
        {
            ReadAgain();
        }
    }

    public void Dispose()
    {
        disposed = true;
        watcher?.Dispose();
    }

    /// <summary>settings.json as it is on disk now, when another Passenger changed it.</summary>
    private void ReadAgain()
    {
        if (disposed || document is null)
        {
            return;
        }
        // Taken before the read, so a write after it moves the stamp past this.
        read = Stamp(document.Path);
        if (!document.Reread())
        {
            return;
        }
        AppSettings before = value;
        value = document.Value;
        IdeErrors.Write("Settings: settings.json was changed by another Passenger, and is read again");
        Raise(before, value);
    }

    private void Raise(AppSettings before, AppSettings after)
    {
        InForce(before, after);
        Changed?.Invoke(this, new SettingsChange(before, after));
    }

    /// <summary>What belongs to the process, put in force once for every window: the theme, its dark or light and the
    /// code's colours only when their settings change, so one named on the command line stands until then.</summary>
    private static void InForce(AppSettings? before, AppSettings after)
    {
        if (Avalonia.Application.Current is { } app && (before is null || before.Theme != after.Theme))
        {
            Program.ApplyTheme(app, after.Theme);
        }
        if (Avalonia.Application.Current is { } current && (before is null || before.Look != after.Look || before.ColourScheme != after.ColourScheme))
        {
            Themes.Apply(current, after.Look, after.ColourScheme);
        }
        if (before is null || before.Keymap != after.Keymap || !SameShortcuts(before.OwnShortcuts(), after.OwnShortcuts()))
        {
            AppActions.Choose(after.Keymap, after.OwnShortcuts());
        }
        if (before is null || before.TraceProtocols != after.TraceProtocols)
        {
            IdeLog.Traces = after.TraceProtocols;
        }
        IdeLog.Programs = after.LogEveryProgram;
    }

    /// <summary>A file's last write and length, or none while it is not there.</summary>
    private static (DateTime Written, long Length) Stamp(string path)
    {
        FileInfo file = new(path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
    }

    private static bool SameShortcuts(Dictionary<string, string[]> one, Dictionary<string, string[]> other) =>
        ReferenceEquals(one, other) || (one.Count == other.Count
            && one.All(pair => other.TryGetValue(pair.Key, out string[]? keys) && keys.SequenceEqual(pair.Value, StringComparer.Ordinal)));
}

/// <summary>The settings before a change and after it.</summary>
public sealed record SettingsChange(AppSettings Before, AppSettings After);
