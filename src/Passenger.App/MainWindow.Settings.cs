using Avalonia.Interactivity;
using Passenger.App.Settings;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>The window's settings: those of the process's store it follows (<see cref="SettingsStore"/>), shared with every
/// window keeping its state in the same folder, or its own while it keeps none; each change, here or in another window,
/// is followed the moment it lands (every editor, the terminal, the consoles, the menus' shortcuts, agents, updates,
/// Discord). The theme, the keymap and the logs' switches the store puts in force once for the process.</summary>
public partial class MainWindow
{
    private SettingsStore settingsStore = SettingsStore.Unkept();
    private AppSettings settings = new();
    private bool keepsSettings;

    /// <summary>The settings in force.</summary>
    public AppSettings Settings => settings;

    /// <summary>Whether the settings are the person's, kept in settings.json, rather than this window's own defaults.</summary>
    public bool SettingsKept => settingsStore.IsKept;

    private void AddSettings()
    {
        Editors.ForEachEditor(editor => ApplyTo(editor, settings));
        settingsStore.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            settingsStore.Changed -= OnSettingsChanged;
            if (!settingsStore.IsKept)
            {
                settingsStore.Dispose();
            }
        };
        // macOS has Settings in the application menu.
        actions!.Add(new IdeAction(AppActions.ShowSettings, "Settings…", "IDE", _ => ShowSettings())
        {
            Menus = OperatingSystem.IsMacOS() ? [] : [new MenuPlace("File", 3, 0)],
        });
    }

    /// <summary>The window a person uses follows the settings kept in its <see cref="StateFolder"/>, which every other
    /// window keeping its state there follows too, and opens on them; headless ones keep the defaults.</summary>
    public void KeepSettings()
    {
        SettingsStore kept = Store().Settings;
        if (kept == settingsStore)
        {
            return;
        }
        settingsStore.Changed -= OnSettingsChanged;
        settingsStore.Dispose();
        settingsStore = kept;
        kept.Changed += OnSettingsChanged;
        keepsSettings = true;
        kept.PutInForce();
        Follow(kept.Value);
    }

    /// <summary>Puts settings in force: what they change from this window's is put over the settings every window
    /// following them has, and kept when they are kept.</summary>
    public void ApplySettings(AppSettings next) => settingsStore.Apply(settings, next);

    private void OnSettingsChanged(object? sender, SettingsChange change) => Follow(change.After);

    /// <summary>The window's share of settings come into force: each editor's, the terminal's, the consoles', the menus'
    /// shortcuts and the rest that is this window's.</summary>
    private void Follow(AppSettings next)
    {
        AppSettings before = settings;
        settings = next;
        Keymap keymap = keys.Keymap;
        keys.Keymap = AppActions.Keymap();
        keys.Reset();
        if (!ReferenceEquals(keymap, keys.Keymap))
        {
            Dock.FollowKeys();
            foreach (Avalonia.Visual within in (Avalonia.Visual[])[this, .. OwnedWindows])
            {
                ActionTips.Follow(within);
            }
        }
        if (menuBar is not null)
        {
            BuildMenuBar();
        }
        foreach (Editors.EditorGroup group in Editors.Groups)
        {
            ApplyTo(group.Editor, next);
        }
        FollowViewSettings(before, next);
        FollowTitleBar(next);
        FollowMainMenu(next);
        Explorer.NestFiles = next.NestFiles;
        assist.Templates = CSharp.TemplateSet.Of(next);
        assist.XamlFormat = next.XamlFormat;
        if (terminal is { } panel)
        {
            FollowTerminalSettings(panel, next, before);
        }
        foreach (ToolWindows.OutputView console in (ToolWindows.OutputView[])[Bottom.Run, Bottom.Build, .. Bottom.Debug.Consoles])
        {
            console.Limit = next.ConsoleBufferKb * 1024;
        }
        ApplyAnalysis();
        session.Web?.UseNode(next.NodeInterpreter);
        session.Web?.UseTools();
        session.Web?.UseDockerHub(next.DockerHubTags);
        session.Web?.Checks.UseBuildKit(next.BuildKitChecks);
        ListenForAgents(next.McpServer);
        FollowAgentFolders(before, next);
        WatchUpdates(next.CheckForUpdates);
        FollowInDiscord();
    }

    /// <summary>Settings (Ctrl+Alt+S, Cmd+, on the Mac), over this window until OK or Cancel.</summary>
    public SettingsDialog ShowSettings()
    {
        SettingsDialog dialog = new(settings, actions!, (from, to) => settingsStore.Apply(from, to), Mcp.AgentSetup.Here(Folder));
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    private static void ApplyTo(CodeEditor editor, AppSettings with)
    {
        editor.FontSize = with.EditorFontSize;
        editor.LineSpacing = with.EditorLineSpacing;
        editor.Ligatures = with.Ligatures;
        editor.ShowLineNumbers = with.ShowLineNumbers;
        editor.ShowParameterHints = with.ParameterHints;
        editor.ShowTypeHints = with.TypeHints;
        editor.RainbowBraces = with.RainbowBraces;
        editor.WrapIndent = with.SoftWrapIndent;
        editor.ZoomsWithWheel = with.WheelZoom;
        editor.InvalidateVisual();
    }
}
