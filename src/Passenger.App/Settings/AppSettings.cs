using System.Text.Json.Nodes;
using Passenger.App.State;
using Passenger.Vcs;

namespace Passenger.App.Settings;

/// <summary>What a person has set in Settings: the theme, the editor's and the terminal's font, the shell. Kept in the
/// store's <c>settings.json</c> (only by the window a person uses: a headless IDE starts on the defaults and keeps
/// nothing); a value the file lacks takes its default, and one out of range is brought into it.</summary>
public sealed record AppSettings : IStateDocument<AppSettings>
{
    /// <summary>The smallest font Settings offers; a size read from the file is brought between it and the largest.</summary>
    public const double MinFontSize = 6;

    public const double MaxFontSize = 48;

    /// <summary>The names that soft-wrap at first.</summary>
    public const string DefaultSoftWrapFiles = "*.md; *.txt; *.rst; *.adoc";

    /// <summary>Dark or light, or as the system is.</summary>
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>The theme, Passenger's own or another of <see cref="Look"/>'s, each in the dark or light <see cref="Theme"/> says.</summary>
    public Look Look { get; init; } = Look.Passenger;

    /// <summary>The code's colours: the theme's own scheme, or Rider's colours in its Dark or Light as the theme is
    /// (over the theme's ground, so the areas stay told apart).</summary>
    public ColourScheme ColourScheme { get; init; } = ColourScheme.Theme;

    /// <summary>"Merge main menu with window title", which Linux alone offers: the toolbar is the window's title bar
    /// (its window buttons at its right) rather than a strip under the system's. On at first, since the frame Passenger
    /// draws moves, resizes and maximizes as the window manager's own does on GNOME.</summary>
    public bool MergeMainMenuWithWindowTitle { get; init; } = true;

    /// <summary>The main menu as a bar of its menus on the toolbar (File, Edit, View…) rather than behind its button,
    /// where the system has no menu bar of its own; those the toolbar has no room for behind the button after
    /// them.</summary>
    public bool MainMenuBar { get; init; }

    public double EditorFontSize { get; init; } = 13;
    public double EditorLineSpacing { get; init; } = 1.2;
    public bool Ligatures { get; init; }
    public bool ShowLineNumbers { get; init; } = true;

    /// <summary>Rainbow braces: brackets coloured by how deeply they nest.</summary>
    public bool RainbowBraces { get; init; } = true;

    /// <summary>Parameter name hints: an argument's parameter shown before it where the call does not say it.</summary>
    public bool ParameterHints { get; init; } = true;

    /// <summary>Type name hints: the type of a <c>var</c>, a lambda's parameter or a target-typed <c>new</c> shown
    /// after it where the code does not say it.</summary>
    public bool TypeHints { get; init; } = true;

    /// <summary>"Soft-wrap these files": a file whose name matches <see cref="SoftWrapFiles"/> opens with its lines
    /// wrapped at the editor's width (View, Active Editor, Soft-Wrap turns it on and off for one file).</summary>
    public bool SoftWrap { get; init; } = true;

    /// <summary>The names that soft-wrap, as masks: <c>*</c> and <c>?</c> as wildcards, <c>;</c> between
    /// them.</summary>
    public string SoftWrapFiles { get; init; } = DefaultSoftWrapFiles;

    /// <summary>"Use the original line's indent for wrapped fragments": a wrapped line's rows after its first start
    /// where its text does.</summary>
    public bool SoftWrapIndent { get; init; } = true;

    /// <summary>Show whitespaces: spaces drawn as dots and tabs as arrows (View, Active Editor, Show Whitespaces turns
    /// it on and off for one file).</summary>
    public bool ShowWhitespaces { get; init; }

    /// <summary>"Change font size with Ctrl+Mouse Wheel" (Command on macOS): off at first, so the wheel only
    /// scrolls.</summary>
    public bool WheelZoom { get; init; }
    public double TerminalFontSize { get; init; } = 13;
    public string? TerminalFontFamily { get; init; }
    public bool TerminalCopyOnSelection { get; init; }
    public bool TerminalCursorBlinking { get; init; }
    public Passenger.Terminal.CursorShape TerminalCursorStyle { get; init; } = Passenger.Terminal.CursorShape.Block;

    /// <summary>Code Folding, Fold by default: the comments a file begins with fold as it opens (a file keeps the folds
    /// it closed with instead, once it has some).</summary>
    public bool FoldFileHeader { get; init; } = true;

    /// <summary>Fold by default: the usings.</summary>
    public bool FoldImports { get; init; } = true;

    /// <summary>Fold by default: documentation comments.</summary>
    public bool FoldDocComments { get; init; }

    /// <summary>Fold by default: the members' bodies.</summary>
    public bool FoldMethodBodies { get; init; }

    /// <summary>Fold by default: #region to #endregion (Preprocessor regions).</summary>
    public bool FoldPreprocessorRegions { get; init; } = true;

    /// <summary>Fold by default: block comments over several lines.</summary>
    public bool FoldComments { get; init; }

    /// <summary>Fold by default: XML elements, in the project files too.</summary>
    public bool FoldXmlTags { get; init; }

    /// <summary>Whether Run runs under dotnet watch, so a save applies its edits to the running program (Hot Reload),
    /// and an edit that cannot be applied restarts it.</summary>
    public bool HotReload { get; init; }

    /// <summary>Hot Reload when debugging (Edit and Continue): Debug launches the program so its code can change as it
    /// runs, Apply Hot Reload Changes applies the edits, and edits made while it is paused are applied as it goes
    /// on.</summary>
    public bool HotReloadWhileDebugging { get; init; } = true;

    /// <summary>Hot Reload on file save: a C# file saved while the program is debugged applies its edits.</summary>
    public bool HotReloadOnSave { get; init; }

    /// <summary>An edit Hot Reload cannot apply (a rude edit) restarts the debugged program, built again with the same
    /// configuration and breakpoints, without asking; off, the restart is asked first.</summary>
    public bool RestartOnRudeEdit { get; init; } = true;

    /// <summary>"Tool Window: when build is explicitly invoked, open the build progress view in a tool window": the
    /// Build menu's builds, rebuilds and cleans open the Build window as they start.</summary>
    public bool OpenBuildWindowOnBuild { get; init; } = true;

    /// <summary>"Tool Window: open the build progress view even when the build is invoked implicitly (i.e. Run/Debug)":
    /// the build before Run, Debug or Profile opens it too, while the setting above is on. Off at first, so a run's
    /// build leaves the bottom panel as it was. A test run's build never opens it.</summary>
    public bool OpenBuildWindowOnImplicitBuild { get; init; }

    /// <summary>"Showing build results after build completed": which builds bring the Build window forward as they end,
    /// whatever started them.</summary>
    public BuildResultsMode ShowBuildResults { get; init; } = BuildResultsMode.ForErrors;

    /// <summary>Solution-wide analysis: every file's errors in Problems as code changes, without a build. Off at first,
    /// since it analyses the whole solution; the status bar turns it on and off.</summary>
    public bool SolutionWideAnalysis { get; init; }

    /// <summary>Autosave on frame deactivation: every changed file saved when the focus goes to another program or to
    /// the terminal.</summary>
    public bool SaveOnDeactivation { get; init; } = true;

    /// <summary>"Save files if the IDE is idle": every changed file saved once no key or click has come for <see
    /// cref="IdleSeconds"/>.</summary>
    public bool SaveWhenIdle { get; init; }

    public int IdleSeconds { get; init; } = 15;

    /// <summary>"Reopen projects on startup": started with nothing named, the window opens the folder or solution
    /// opened last, rather than the Welcome screen.</summary>
    public bool ReopenProjectsOnStartup { get; init; } = true;

    /// <summary>"Open project in": where a solution or folder opened from a window that has one goes, a window of its
    /// own, that window's place, or asked each time (Open Solution's This Window and New Window).</summary>
    public OpenProjectIn OpenProjectIn { get; init; } = OpenProjectIn.Ask;

    /// <summary>Check IDE updates: the person's window asks GitHub for Passenger's latest release as it starts and once
    /// a day, and an installed Passenger downloads a newer one to install as it restarts. Off, nothing is asked unless
    /// Help, Check for Updates is chosen, and nothing is downloaded at all.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>How much of a Run, Build or Debug console's text is kept, in KB, the oldest lines dropped past
    /// it.</summary>
    public int ConsoleBufferKb { get; init; } = ToolWindows.OutputView.DefaultLimitKb;

    /// <summary>The keymap the person's keys follow, Settings' Keymap: Visual Studio's at first, so a settings.json
    /// without one keeps the keys it had.</summary>
    public Passenger.Editor.KeymapPreset Keymap { get; init; } = Passenger.Editor.KeymapPreset.VisualStudio;

    /// <summary>The person's shortcuts over the Visual Studio keymap's, by action, in keymap notation ("meta shift K"); an
    /// empty list unbinds the action.</summary>
    public Dictionary<string, string[]> Shortcuts { get; init; } = [];

    /// <summary>The person's shortcuts over the VS Code keymap's, kept apart from Visual Studio's, so a shortcut set over
    /// one keymap is never put over the other.</summary>
    public Dictionary<string, string[]> VSCodeShortcuts { get; init; } = [];

    /// <summary>The person's shortcuts over the keymap they chose (a method, so the settings agents list leave it out).</summary>
    public Dictionary<string, string[]> OwnShortcuts() => Keymap == Passenger.Editor.KeymapPreset.VSCode ? VSCodeShortcuts : Shortcuts;

    /// <summary>These settings with these as the person's shortcuts over the keymap they chose.</summary>
    public AppSettings WithOwnShortcuts(Dictionary<string, string[]> shortcuts) =>
        Keymap == Passenger.Editor.KeymapPreset.VSCode ? this with { VSCodeShortcuts = shortcuts } : this with { Shortcuts = shortcuts };

    /// <summary>The live templates as the person left them, by template: a built-in one's changes (its key the shortcut
    /// it ships with), and the whole of each they added (<see cref="TemplateChange"/>).</summary>
    public Dictionary<string, TemplateChange> LiveTemplates { get; init; } = [];

    /// <summary>Postfix Completion: the postfix templates turned off (false), by name; one not named is on.</summary>
    public Dictionary<string, bool> PostfixTemplates { get; init; } = [];

    /// <summary>Enable postfix completion: off, no postfix template is offered or expanded.</summary>
    public bool PostfixCompletion { get; init; } = true;

    /// <summary>Show postfix templates in completion autopopup: the list after a dot offers them, which Tab after one's
    /// name expands either way.</summary>
    public bool PostfixInCompletion { get; init; } = true;

    /// <summary>The terminal's shell, or null for the user's own login shell.</summary>
    public string? TerminalShell { get; init; }

    /// <summary>The Node.js the JavaScript, TypeScript, HTML, CSS and JSON language servers run on, or null for the
    /// first on the PATH as the login shell finds it.</summary>
    public string? NodeInterpreter { get; init; }

    /// <summary>ESLint's "Run for files" at first.</summary>
    public const string DefaultEslintFiles = "**/*.{js,ts,jsx,tsx,cjs,cts,mjs,mts,html,vue}";

    /// <summary>Prettier's "Run for files" at first.</summary>
    public const string DefaultPrettierFiles = "**/*.{js,ts,jsx,tsx,cjs,cts,mjs,mts,json,vue,astro}";

    /// <summary>ESLint: Automatic (the project's own eslint package, found from the file's folder up, and its
    /// configuration), Manual (<see cref="EslintPackage"/>) or Disabled. Its problems are shown as a file is
    /// typed.</summary>
    public ToolConfiguration Eslint { get; init; } = ToolConfiguration.Automatic;

    /// <summary>Manual ESLint configuration's package: the eslint package's folder (…/node_modules/eslint).</summary>
    public string? EslintPackage { get; init; }

    /// <summary>ESLint's "Run for files": the files it checks, a glob of their paths from the folder open.</summary>
    public string EslintFiles { get; init; } = DefaultEslintFiles;

    /// <summary>"Run eslint --fix on save": a save (File, Save All) first fixes what ESLint can fix in each file it checks.</summary>
    public bool EslintFixOnSave { get; init; }

    /// <summary>Prettier: Automatic (the project's own prettier package, found from the file's folder up), Manual (<see
    /// cref="PrettierPackage"/>) or Disabled.</summary>
    public ToolConfiguration Prettier { get; init; } = ToolConfiguration.Automatic;

    /// <summary>Manual Prettier configuration's package: the prettier package's folder (…/node_modules/prettier).</summary>
    public string? PrettierPackage { get; init; }

    /// <summary>Prettier's "Run for files": the files Reformat Code and a save format with it, a glob of their paths from the
    /// folder open.</summary>
    public string PrettierFiles { get; init; } = DefaultPrettierFiles;

    /// <summary>"Run on 'Reformat Code' action": Reformat Code formats the files <see cref="PrettierFiles"/> covers with
    /// Prettier, others with their language server.</summary>
    public bool PrettierOnReformat { get; init; } = true;

    /// <summary>"Run on save": a save (File, Save All) formats each file <see cref="PrettierFiles"/> covers with Prettier first.</summary>
    public bool PrettierOnSave { get; init; }

    /// <summary>Remote JSON Schemas, "Allow downloading JSON Schemas from remote sources": a JSON file's own
    /// <c>$schema</c> on the web is downloaded to check and complete it. Those Passenger carries never are.</summary>
    public bool DownloadJsonSchemas { get; init; } = true;

    /// <summary>"Complete image tags from Docker Hub": the Dockerfile server asks Docker Hub for an image's tags, naming the
    /// image, as <c>FROM image:</c> is completed. Off, it asks Docker Hub nothing and offers no tags.</summary>
    public bool DockerHubTags { get; init; } = true;

    /// <summary>"Check Dockerfiles with BuildKit": as a Dockerfile opens and is saved, the engine's BuildKit runs its
    /// build checks on it with its own Dockerfile frontend (never a <c># syntax=</c> line's), reading each base image's
    /// metadata from its registry. Off, none runs (a compose file's hints and errors stay). On unless
    /// <c>PASSENGER_BUILDKIT_CHECKS</c> is off (<see cref="Containers.BuildKitChecks"/>).</summary>
    public bool BuildKitChecks { get; init; } = Containers.BuildKitChecks.ByDefault;

    /// <summary>Enable MCP Server: agents reaching the window through its socket (<c>passenger --mcp-attach</c>) are
    /// let in without asking. Off until the person turns it on, since any program running as them could then act with
    /// the IDE's permissions; off, the window asks the person before each agent's first call goes on.</summary>
    public bool McpServer { get; init; }

    /// <summary>The folders whose window lets agents in without asking (the question's Always for this folder), whatever
    /// <see cref="McpServer"/> says; Settings' MCP Server page lists them, each taken back by Remove. The person's alone:
    /// settings_set refuses it, as it refuses <see cref="McpServer"/>.</summary>
    public IReadOnlyList<string> AgentFolders { get; init; } = [];

    /// <summary>Show activity in Discord: the Discord app on this computer shows "Playing Passenger" with Passenger's
    /// icon and what <see cref="DiscordShows"/> names to whoever can see the person's activity. On at first, naming
    /// nothing of the work; off, Discord is told nothing at all.</summary>
    public bool ShowInDiscord { get; init; } = true;

    /// <summary>What the activity names beyond Passenger: nothing at first (Passenger's own words and how long the solution
    /// has been open); the solution, or the solution and the file, only as the person chooses.</summary>
    public DiscordShown DiscordShows { get; init; } = DiscordShown.PassengerOnly;

    /// <summary>Whether the activity names only Passenger once no key, click or scroll has come for <see
    /// cref="DiscordIdleMinutes"/>.</summary>
    public bool DiscordIdle { get; init; } = true;

    /// <summary>How long without a key, click or scroll counts as idle: 5 minutes at first, up to a day.</summary>
    public int DiscordIdleMinutes { get; init; } = 5;

    /// <summary>A client's verbose protocol trace: every message to and from the C# language servers, the debugger and
    /// VSTest written to lsp.log, dap.log and vstest.log in the logs folder (<see cref="IdeLog.Traces"/>).</summary>
    public bool TraceProtocols { get; init; }

    /// <summary>Every program the IDE runs logged in passenger.log with its exit code and time, not only those that
    /// fail or take a while (<see cref="IdeLog.Programs"/>).</summary>
    public bool LogEveryProgram { get; init; }

    /// <summary>The diff's Highlighting Differences: the words that changed inside changed lines marked, at first; the
    /// diff's Settings changes it for every diff after.</summary>
    public DiffHighlighting DiffHighlighting { get; init; } = DiffHighlighting.Words;

    /// <summary>Highlight Words in a change bar's popup: the words that changed marked in what the lines were and in
    /// the file while the popup is open; the popup's toggle changes it.</summary>
    public bool ChangePopupWords { get; init; } = true;

    /// <summary>"Show files with the same names as nested": the Solution view nests files by their nesting rules
    /// (Counter.razor.cs under Counter.razor), on until the Explorer's File Nesting... turns it off.</summary>
    public bool NestFiles { get; init; } = true;

    /// <summary>The code style for XAML (the XML formatter's options, with an attribute order and empty-element style),
    /// which Reformat Code lays out Avalonia's and WPF's XAML by, a file's .editorconfig over it.</summary>
    public Passenger.Xaml.Features.XamlFormatSettings XamlFormat { get; init; } = new();

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned, DiscordOnNamingNothing];

    /// <summary><see cref="AgentFolders"/> is a set: a folder let in always from one window while another's Settings takes
    /// back a different one stays let in.</summary>
    public static IReadOnlyList<string> Sets { get; } = [nameof(AgentFolders)];

    /// <summary>Version 1 to 2, Discord's activity on at first and naming nothing of the work: a file whose activity was not
    /// turned on holds the old defaults (off, the solution and the file), written with whatever else was changed, so it
    /// comes up on, naming only Passenger; one the person had turned on keeps what they chose.</summary>
    private static void DiscordOnNamingNothing(JsonObject settings)
    {
        if (settings["showInDiscord"] is JsonValue shown && shown.TryGetValue(out bool on) && on)
        {
            return;
        }
        settings["showInDiscord"] = true;
        settings["discordShows"] = nameof(DiscordShown.PassengerOnly);
    }

    /// <summary>A hand-edited or later build's value this build cannot use is brought into range, or back to its default
    /// (each setting of one value as <see cref="Declared"/> says); a shortcut that does not parse is the keymap's to pass over.</summary>
    public AppSettings Clamped()
    {
        AppSettings defaults = new();
        AppSettings clamped = this with
        {
            TerminalFontFamily = string.IsNullOrWhiteSpace(TerminalFontFamily) ? null : TerminalFontFamily.Trim(),
            Shortcuts = Readable(Shortcuts),
            VSCodeShortcuts = Readable(VSCodeShortcuts),
            LiveTemplates = (LiveTemplates ?? []).Where(t => t.Value is not null).ToDictionary(t => t.Key, t => t.Value.Clamped()),
            PostfixTemplates = PostfixTemplates ?? [],
            TerminalShell = string.IsNullOrWhiteSpace(TerminalShell) ? null : TerminalShell,
            NodeInterpreter = string.IsNullOrWhiteSpace(NodeInterpreter) ? null : NodeInterpreter.Trim(),
            EslintPackage = string.IsNullOrWhiteSpace(EslintPackage) ? null : EslintPackage.Trim(),
            EslintFiles = string.IsNullOrWhiteSpace(EslintFiles) ? defaults.EslintFiles : EslintFiles.Trim(),
            PrettierPackage = string.IsNullOrWhiteSpace(PrettierPackage) ? null : PrettierPackage.Trim(),
            PrettierFiles = string.IsNullOrWhiteSpace(PrettierFiles) ? defaults.PrettierFiles : PrettierFiles.Trim(),
            SoftWrapFiles = SoftWrapFiles ?? defaults.SoftWrapFiles,
            XamlFormat = (XamlFormat ?? defaults.XamlFormat).Clamped(),
            AgentFolders = [.. (AgentFolders ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(Passenger.Core.PathNames.Comparer)],
        };
        foreach (Setting setting in Declared.All)
        {
            clamped = setting.Clamp(clamped);
        }
        return clamped;

        // A shortcut kept under an action's former id is the action's now (one kept under its id now winning).
        static Dictionary<string, string[]> Readable(Dictionary<string, string[]>? shortcuts) =>
            (shortcuts ?? []).Where(s => s.Value is not null).OrderBy(s => AppActions.Formerly.ContainsKey(s.Key) ? 0 : 1)
                .GroupBy(s => AppActions.Now(s.Key), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value.Where(k => k is not null).ToArray());
    }

    /// <summary>Whether a region of a kind folds as its file opens, as Fold by default says.</summary>
    public bool FoldsByDefault(Passenger.Editor.FoldKind kind) => kind switch
    {
        Passenger.Editor.FoldKind.FileHeader => FoldFileHeader,
        Passenger.Editor.FoldKind.Imports => FoldImports,
        Passenger.Editor.FoldKind.DocComment => FoldDocComments,
        Passenger.Editor.FoldKind.Body => FoldMethodBodies,
        Passenger.Editor.FoldKind.Region => FoldPreprocessorRegions,
        Passenger.Editor.FoldKind.Comment => FoldComments,
        Passenger.Editor.FoldKind.Tag => FoldXmlTags,
        _ => false,
    };

    /// <summary>Whether a file opens soft-wrapped: its name matches one of <see cref="SoftWrapFiles"/>, ignoring case, while
    /// <see cref="SoftWrap"/> is on.</summary>
    public bool WrapsFile(string path)
    {
        if (!SoftWrap)
        {
            return false;
        }
        string name = Path.GetFileName(path);
        foreach (string mask in SoftWrapFiles.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Matches(mask, name))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether a name matches a mask whose <c>*</c> stands for any run of characters and <c>?</c> for one.</summary>
    private static bool Matches(ReadOnlySpan<char> mask, ReadOnlySpan<char> name)
    {
        while (mask.Length > 0)
        {
            if (mask[0] == '*')
            {
                for (int skip = 0; skip <= name.Length; skip++)
                {
                    if (Matches(mask[1..], name[skip..]))
                    {
                        return true;
                    }
                }
                return false;
            }
            if (name.Length == 0 || (mask[0] != '?' && char.ToUpperInvariant(mask[0]) != char.ToUpperInvariant(name[0])))
            {
                return false;
            }
            mask = mask[1..];
            name = name[1..];
        }
        return name.Length == 0;
    }
}

/// <summary>The choices for "Showing build results after build completed", in the order Settings lists them.</summary>
public enum BuildResultsMode
{
    Always,
    ForWarningsOrErrors,
    ForErrors,
    Never,
}

/// <summary>How ESLint and Prettier are configured, in the order Settings lists them.</summary>
public enum ToolConfiguration
{
    /// <summary>Not run.</summary>
    Disabled,

    /// <summary>The project's own package, found from each file's folder up, and its own configuration files.</summary>
    Automatic,

    /// <summary>The package Settings names, for every file the glob covers.</summary>
    Manual,
}

/// <summary>The choices for "Open project in", in the order Settings lists them.</summary>
public enum OpenProjectIn
{
    NewWindow,
    CurrentWindow,
    Ask,
}

/// <summary>What Discord's activity names beyond Passenger, in the order Settings lists them.</summary>
public enum DiscordShown
{
    PassengerOnly,
    Solution,
    SolutionAndFile,
}
