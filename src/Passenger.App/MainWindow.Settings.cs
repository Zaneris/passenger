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
        Editors.ForEachEditor(editor => ApplyTo(editor, settingsStore.Effective));
        settingsStore.Changed += OnSettingsChanged;
        settingsStore.PreviewChanged += OnPreviewChanged;
        Activated += (_, _) => settingsStore.LookAgain();
        Themes.SaidChanged += OnThemesSaid;
        Themes.FontsChanged += OnThemeFonts;
        OnThemesSaid(null, EventArgs.Empty);
        Closed += (_, _) =>
        {
            Themes.SaidChanged -= OnThemesSaid;
            Themes.FontsChanged -= OnThemeFonts;
            settingsStore.Changed -= OnSettingsChanged;
            settingsStore.PreviewChanged -= OnPreviewChanged;
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
    /// window keeping its state there follows too, and opens on them; headless ones keep their own (<see cref="OpenOnSettings"/>).</summary>
    public void KeepSettings()
    {
        SettingsStore kept = Store().Settings;
        if (kept == settingsStore)
        {
            return;
        }
        keepsSettings = true;
        Take(kept);
    }

    /// <summary>A window nobody sees opens on the settings given (those kept where PASSENGER_SETTINGS names), its own from
    /// then on: kept nowhere, so its changes stay its own and no other Passenger's reach it.</summary>
    public void OpenOnSettings(AppSettings opening)
    {
        keepsSettings = false;
        Take(SettingsStore.Unkept(opening));
    }

    /// <summary>The window of a process nobody sees (an agent's <c>--mcp</c>, a screenshot) opens on the settings kept where
    /// PASSENGER_SETTINGS names, as a person's window opens on theirs, keeping nothing; with no folder named, or no settings
    /// kept there, on the defaults, so the person's own reach no Passenger they don't see. The command line's theme stands.</summary>
    public void OpenOnNamedSettings(LaunchOptions launch)
    {
        if (State.StateStore.Named is { } folder && SettingsStore.KeptIn(folder) is { } kept)
        {
            OpenOnSettings(kept);
            Program.ApplyTheme(Avalonia.Application.Current!, launch);
        }
    }

    /// <summary>The settings the window follows from now on, the process's share of them put in force; a store of the
    /// window's own it followed before is let go.</summary>
    private void Take(SettingsStore next)
    {
        settingsStore.Changed -= OnSettingsChanged;
        settingsStore.PreviewChanged -= OnPreviewChanged;
        if (!settingsStore.IsKept)
        {
            settingsStore.Dispose();
        }
        settingsStore = next;
        next.Changed += OnSettingsChanged;
        next.PreviewChanged += OnPreviewChanged;
        next.PutInForce();
        Follow(next.Value);
        OnPreviewChanged(this, EventArgs.Empty);
    }

    /// <summary>Puts settings in force: what they change from this window's is put over the settings every window
    /// following them has, and kept when they are kept.</summary>
    public void ApplySettings(AppSettings next) => settingsStore.Apply(settings, next);

    /// <summary>The settings as this window last showed them, a Settings dialog's preview too.</summary>
    private AppSettings visualSettings = new();

    /// <summary>The editors take the theme's code size where the person set none.</summary>
    private void OnThemeFonts(object? sender, EventArgs args) => Editors.ForEachEditor(editor => ApplyTo(editor, settingsStore.Effective));

    /// <summary>The window's share of the look follows the settings shown: each editor's, the title bar's, the menu's, the
    /// terminal's.</summary>
    private void OnPreviewChanged(object? sender, EventArgs args)
    {
        AppSettings next = settingsStore.Effective;
        foreach (Editors.EditorGroup group in Editors.Groups)
        {
            ApplyTo(group.Editor, next);
        }
        FollowViewSettings(visualSettings, next);
        FollowArrangement(next);
        FollowTitleBar(next);
        // The layout's smallest at the scale shown: SmallestWidth units of content, however large they are drawn.
        HoldAtLeast(new Avalonia.Size(SmallestWidth * next.DisplayScale, SmallestHeight * next.DisplayScale));
        FollowMainMenu(next);
        if (terminal is { } panel)
        {
            FollowTerminalLook(panel, next, visualSettings);
        }
        session.CSharp?.Hints = (next.ParameterHints, next.TypeHints);
        visualSettings = next;
    }

    private void OnSettingsChanged(object? sender, SettingsChange change) => Follow(change.After);

    /// <summary>The status bar says a chosen theme being fetched, or why it could not be, a click on it opening Appearance;
    /// words said for the themes before go once the theme came.</summary>
    private void OnThemesSaid(object? sender, EventArgs args)
    {
        if (Themes.Said is { } said)
        {
            themesSaid = said;
            status.Say(said, Themes.SaidProblem ? Run.ProblemSeverity.Warning : null,
                new StatusPlace($"choose a theme in {SettingsDialog.PlaceOfPage(SettingsDialog.Appearance)}", () => ShowSettings().Page = SettingsDialog.Appearance));
        }
        else if (themesSaid is not null && status.Text == themesSaid)
        {
            status.Text = null;
            themesSaid = null;
        }
    }

    /// <summary>What the status bar last said of the themes.</summary>
    private string? themesSaid;

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
        if (next.OpenDiffsIn != before.OpenDiffsIn)
        {
            MoveDiffs(next.OpenDiffsIn);
        }
        Explorer.NestFiles = next.NestFiles;
        Explorer.OpensOnClick = next.OpenFilesWithSingleClick;
        if (next.AlwaysSelectOpenedFile && !before.AlwaysSelectOpenedFile && Editors.Active?.Path is { } shown)
        {
            Explorer.Reveal(shown);
        }
        FollowXamlWorkspace(before);
        FollowVimMappings(next.VimMode);
        assist.Templates = CSharp.TemplateSet.Of(next);
        assist.XamlFormat = next.XamlFormat;
        assist.GDScriptFormat = (next.GDScriptFormat, next.GDScriptFormatOnSave);
        if (terminal is { } panel)
        {
            FollowTerminalSettings(panel, next, before);
        }
        foreach (ToolWindows.OutputView console in (ToolWindows.OutputView[])[Bottom.Run, Bottom.Build, .. Bottom.Debug.Consoles])
        {
            console.Limit = next.ConsoleBufferKb * 1024;
        }
        ApplyAnalysis();
        godotValues?.On = next.GodotLiveValues;
        session.Web?.UseNode(next.NodeInterpreter);
        session.Web?.UseTools();
        session.Web?.UseDockerHub(next.DockerHubTags);
        if (next.DownloadStylesheets != before.DownloadStylesheets)
        {
            session.Stylesheets.DownloadsChanged();
        }
        session.Web?.Checks.UseBuildKit(next.BuildKitChecks);
        ListenForAgents(next.McpServer);
        FollowAgentFolders(before, next);
        WatchUpdates(next.CheckForUpdates);
        FollowInDiscord();
    }

    /// <summary>Settings (Ctrl+Alt+S, Cmd+, on the Mac), over this window until OK or Cancel.</summary>
    public SettingsDialog ShowSettings()
    {
        SettingsDialog dialog = new(settings, actions!, (from, to) => settingsStore.Apply(from, to), Mcp.AgentSetup.Here(Folder, settings.TerminalShell), settingsStore.BeginPreview());
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    private static void ApplyTo(CodeEditor editor, AppSettings with)
    {
        editor.FontSize = with.CodeSize();
        editor.LineSpacing = with.EditorLineSpacing;
        editor.Ligatures = with.Ligatures;
        editor.ShowLineNumbers = with.ShowLineNumbers;
        editor.ShowParameterHints = with.ParameterHints;
        editor.ShowTypeHints = with.TypeHints;
        editor.RainbowBraces = with.RainbowBraces;
        editor.UsesVim = with.VimMode;
        editor.VimLeavesKeys = Declared.VimKeysToKeymap.Given(with);
        editor.ClosesTags = with.CloseTags;
        editor.WrapIndent = with.SoftWrapIndent;
        editor.ZoomsWithWheel = with.WheelZoom;
        editor.InvalidateVisual();
    }
}
