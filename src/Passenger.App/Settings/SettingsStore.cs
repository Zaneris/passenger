using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.App.Settings;

/// <summary>The settings as every window that follows them has them: one per state folder in the process (the person's
/// windows all follow theirs, kept in its settings.json), or one of a window's own that keeps nothing. A change reads
/// the file again first, so what another Passenger wrote meanwhile stays, puts over it only the values the change makes
/// differ, is written through the store, and raises <see cref="Changed"/>; settings.json changed on disk by another
/// Passenger is read again as it lands, as its watcher overflows, and as a window comes in front, each once it moved since
/// this process last read or wrote it (<see cref="LookAgain"/>). The theme, the code font, the keymap and the logs' switches
/// are the process's: put in force here, once, before any window hears. UI thread only.</summary>
public sealed class SettingsStore : IDisposable
{
    private readonly StateDocument<AppSettings>? document;

    /// <summary>settings.json watched for other Passengers' changes; null for a window's own settings.</summary>
    private readonly WatchedFile? watched;
    private AppSettings value;
    private readonly List<SettingsPreview> previews = [];

    /// <summary>The settings as last shown, which the process's share of the look and the windows have followed.</summary>
    private AppSettings? effective;
    private bool inForce;
    private bool disposed;

    private SettingsStore(StateDocument<AppSettings>? document, AppSettings? opening = null)
    {
        this.document = document;
        value = document?.Value ?? opening ?? new AppSettings();
        if (document is null)
        {
            return;
        }
        watched = new WatchedFile(document.Path, "Settings", _ =>
        {
            UiThread.Post(ReadAgain);
            return true;
        });
        document.Wrote += Saw;
    }

    /// <summary>Settings kept in a state folder's settings.json.</summary>
    internal static SettingsStore Kept(StateDocument<AppSettings> document) => new(document);

    /// <summary>A window's own settings, kept nowhere: the defaults, or those it opens on, changed only by that window.</summary>
    public static SettingsStore Unkept(AppSettings? opening = null) => new(null, opening);

    /// <summary>The settings kept where PASSENGER_SETTINGS names (<see cref="StateStore.Named"/>), which a process nobody
    /// sees opens on, read as a window keeping them reads them, on any thread: null with no folder named, or none kept there
    /// (no file, or one that did not read).</summary>
    internal static AppSettings? KeptWhereNamed() =>
        StateStore.Named is { } folder ? new StateDocument<AppSettings>(Path.Combine(folder, StateStore.SettingsFile)).KeptNow() : null;

    /// <summary>The logs' switches of the settings a process opens on, put in force before any window takes them, so what
    /// it begins ahead of its window (a language server, an agent's first calls) is traced from its start.</summary>
    internal static void LogsInForce(AppSettings opening) => LogsInForce(null, opening);

    /// <summary>The settings in force.</summary>
    public AppSettings Value => value;

    /// <summary>The settings as shown: those in force with the looks open Settings dialogs changed put over them.</summary>
    public AppSettings Effective => previews.Aggregate(value, (now, preview) => preview.Over(now));

    /// <summary>The settings as shown changed, by a change in force or a Settings dialog's preview; raised once the
    /// process's share of the look is in force.</summary>
    public event EventHandler? PreviewChanged;

    /// <summary>A Settings dialog's preview of the look, shown until it is disposed.</summary>
    public SettingsPreview BeginPreview()
    {
        SettingsPreview preview = new(this);
        previews.Add(preview);
        return preview;
    }

    internal void EndPreview(SettingsPreview preview)
    {
        previews.Remove(preview);
        RefreshPreview();
    }

    /// <summary>The settings as shown followed, where a preview changed them: an edit of behaviour redraws nothing.</summary>
    internal void RefreshPreview()
    {
        AppSettings next = Effective;
        if (next == effective)
        {
            return;
        }
        VisualsInForce(effective ?? value, next);
        effective = next;
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether the settings are kept in a file (the person's), rather than a window's own.</summary>
    public bool IsKept => document is not null;

    /// <summary>The watch on settings.json; null for a window's own settings.</summary>
    public WatchedFile? Watched => watched;

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
        InForce(null, Effective);
        effective = Effective;
    }

    /// <summary>Writes a change still waiting, now.</summary>
    public void Flush() => document?.Flush();

    /// <summary>Looks off the UI thread whether settings.json moved since this process last read or wrote it, as a window
    /// comes in front, and reads it again only if it did: another Passenger's change the watcher never heard, as the
    /// system may drop one without a word, is followed then.</summary>
    public void LookAgain()
    {
        UiThread.Check();
        if (!disposed)
        {
            watched?.LookAgain();
        }
    }

    public void Dispose()
    {
        disposed = true;
        if (document is not null)
        {
            document.Wrote -= Saw;
        }
        watched?.Dispose();
    }

    /// <summary>settings.json as this process just wrote it, on the writing thread: no change for a look to read again.</summary>
    private void Saw(FileStamp stamp) => watched?.Saw(stamp);

    /// <summary>settings.json as it is on disk now, when another Passenger changed it.</summary>
    private void ReadAgain()
    {
        if (disposed || document is null)
        {
            return;
        }
        // Taken before the read, so a write after it moves the stamp past this.
        watched?.Saw(FileStamp.Of(document.Path));
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
        AppSettings next = Effective;
        InForce(effective ?? before, next);
        effective = next;
        Changed?.Invoke(this, new SettingsChange(before, after));
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The process's share of the look, put in force once for every window: the theme, its dark or light and the
    /// code's colours only when their settings change, so one named on the command line stands until then; the code font and
    /// the window's; the carets' width.</summary>
    private static void VisualsInForce(AppSettings? before, AppSettings after)
    {
        if (Avalonia.Application.Current is { } app && (before is null || before.Theme != after.Theme))
        {
            Program.ApplyTheme(app, after.Theme);
        }
        if (Avalonia.Application.Current is { } current && (before is null || before.Look != after.Look || before.ColourScheme != after.ColourScheme))
        {
            Themes.Apply(current, after.Look, after.ColourScheme);
        }
        if (Avalonia.Application.Current is { } contrasted && (before is null || before.FollowSystemContrast != after.FollowSystemContrast))
        {
            Themes.FollowContrast(contrasted, after.FollowSystemContrast);
        }
        if (Avalonia.Application.Current is { } coloured && (before is null || before.StatePalette != after.StatePalette))
        {
            Themes.UseStates(coloured, after.StatePalette);
        }
        FontsInForce(after);
        if (Avalonia.Application.Current is { } careted && (before is null || before.CaretWidth != after.CaretWidth))
        {
            Passenger.Editor.Carets.UseWidth(careted, after.CaretWidth);
        }
        if (Avalonia.Application.Current is { } scaled && (before is null || before.DisplayScale != after.DisplayScale))
        {
            InterfaceScale.Use(scaled, after.DisplayScale);
        }
    }

    /// <summary>The settings whose fonts are in force, which the theme's fonts give way to.</summary>
    private static AppSettings? fontsOf;

    /// <summary>The code's and the window's family in force, as their names: a family Settings names, else the theme's.</summary>
    private static (string? Code, string? Window)? fontsInForce;

    /// <summary>The code font and the window's: the family Settings names wherever the person named one, else the theme's,
    /// else Passenger's own; put in force again as the theme's change.</summary>
    private static void FontsInForce(AppSettings after)
    {
        if (fontsOf is null)
        {
            Themes.FontsChanged += (_, _) => FontsInForce(fontsOf!);
        }
        fontsOf = after;
        (string? code, string? window) = (after.EditorFontFamily ?? Themes.Fonts.CodeFamily, after.WindowFontFamily ?? Themes.Fonts.WindowFamily);
        if (Avalonia.Application.Current is { } shown && (fontsInForce is null || fontsInForce.Value.Code != code))
        {
            Passenger.Editor.CodeFonts.Use(shown, Passenger.Editor.CodeFonts.Named(code) ?? Passenger.Editor.CodeEditor.CodeFont);
        }
        if (Avalonia.Application.Current is { } framed && (fontsInForce is null || fontsInForce.Value.Window != window))
        {
            WindowFonts.Use(framed, WindowFonts.Named(window) ?? WindowFonts.Carried);
        }
        fontsInForce = Avalonia.Application.Current is null ? null : (code, window);
    }

    /// <summary>What belongs to the process, put in force once for every window: its share of the look, the keymap and the
    /// logs' switches.</summary>
    private static void InForce(AppSettings? before, AppSettings after)
    {
        VisualsInForce(before, after);
        if (before is null || before.Keymap != after.Keymap || !SameShortcuts(before.OwnShortcuts(), after.OwnShortcuts()))
        {
            AppActions.Choose(after.Keymap, after.OwnShortcuts());
        }
        LogsInForce(before, after);
    }

    private static void LogsInForce(AppSettings? before, AppSettings after)
    {
        if (before is null || before.TraceProtocols != after.TraceProtocols)
        {
            IdeLog.Traces = after.TraceProtocols;
        }
        IdeLog.Programs = after.LogEveryProgram;
    }

    private static bool SameShortcuts(Dictionary<string, string[]> one, Dictionary<string, string[]> other) =>
        ReferenceEquals(one, other) || (one.Count == other.Count
            && one.All(pair => other.TryGetValue(pair.Key, out string[]? keys) && keys.SequenceEqual(pair.Value, StringComparer.Ordinal)));
}

/// <summary>The settings before a change and after it.</summary>
public sealed record SettingsChange(AppSettings Before, AppSettings After);
