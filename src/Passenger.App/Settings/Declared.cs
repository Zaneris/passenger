using Passenger.Vcs;
using Passenger.Workspace.GDScript;
using Passenger.Xaml.Features;
using D = Passenger.App.Settings.SettingsDialog;

namespace Passenger.App.Settings;

/// <summary>Every setting of one value, each declared once: its own row in Settings (none where the dialog builds the row
/// round it), its range or its choices. <see cref="AppSettings.Clamped"/> brings each into what it allows, and the
/// dialog makes each row from here.</summary>
public static class Declared
{
    /// <summary>Every declaration, each adding itself as it is made, so this comes before them.</summary>
    internal static readonly List<Setting> All = [];

    public static readonly ChoiceSetting<Look> Look = new(s => s.Look, (s, v) => s with { Look = v }, D.LookChoices,
        new(D.Appearance, "Theme", "colour color theme look palette " + string.Join(" ", D.LookChoices.Select(c => c.Name.ToLowerInvariant()))));

    public static readonly ChoiceSetting<ThemeChoice> Theme = new(s => s.Theme, (s, v) => s with { Theme = v },
        [(ThemeChoice.System, "System"), (ThemeChoice.Dark, "Dark"), (ThemeChoice.Light, "Light")],
        new(D.Appearance, "Dark or light", "colour color theme dark light system sync os appearance mode"));

    public static readonly FontSetting WindowFontFamily = new(nameof(AppSettings.WindowFontFamily), s => s.WindowFontFamily, (s, v) => s with { WindowFontFamily = v }, null,
        FontChoices.Window, new(D.Appearance, "Font family", "font family typeface ui interface window words system segoe san francisco inter",
            "The font of the window's words: menus, tool windows, tabs, dialogs and popups. The system font is Segoe UI on Windows, "
            + "San Francisco on macOS and the desktop's sans-serif on Linux. Code keeps the editor's font."));

    public static readonly NumberSetting DisplayScale = new(s => s.DisplayScale, (s, v) => s with { DisplayScale = double.IsFinite(v) ? v : 1 }, 1, 3, 0.1,
        new(D.Appearance, "Display scale", "display scale zoom font size bigger larger interface ui accessibility dpi hidpi 4k monitor",
            "Scales the entire interface, including menus, tool windows, tabs, dialogs, popups, editors and icons. "
            + "1.0 is the normal size; try 1.2 or 1.5 on a high-resolution screen. Applies on top of your system's display scaling."), unit: "×");

    public static readonly ToggleSetting MainMenuBar = new(s => s.MainMenuBar, (s, v) => s with { MainMenuBar = v },
        new(D.Appearance, "Show main menu as a menu bar", "main menu bar hamburger file edit view toolbar ribbon menus",
            "File, Edit, View and the rest side by side on the toolbar rather than behind its button. Those the toolbar has no room for go behind the button after them."));

    public static readonly ToggleSetting ReopenProjectsOnStartup = new(s => s.ReopenProjectsOnStartup, (s, v) => s with { ReopenProjectsOnStartup = v },
        new(D.SystemPage, "Reopen projects on startup", "reopen last solution project folder startup start launch welcome screen recent"));

    public static readonly ChoiceSetting<OpenProjectIn> OpenProjectIn = new(s => s.OpenProjectIn, (s, v) => s with { OpenProjectIn = v }, D.OpenProjectChoices);

    public static readonly ToggleSetting SaveOnDeactivation = new(s => s.SaveOnDeactivation, (s, v) => s with { SaveOnDeactivation = v },
        new(D.SystemPage, "Save when leaving the IDE", "autosave files switching to a different application or the built-in terminal focus frame deactivation"));

    public static readonly NumberSetting IdleSeconds = new(s => s.IdleSeconds, (s, v) => s with { IdleSeconds = (int)v }, 1, 3600, 1);

    public static readonly ToggleSetting CheckForUpdates = new(s => s.CheckForUpdates, (s, v) => s with { CheckForUpdates = v },
        new(D.UpdatesPage, "Check for updates", "updates check new version release newer download github automatic install restart",
            "Passenger asks GitHub for its latest release when it starts and once a day. When a newer one is out, an installed "
            + "Passenger downloads it, checks it against the SHA-256 the release gives, and installs it when it restarts; a "
            + "copy built from source says in the status bar that it is out. Nothing is sent but the requests, which name "
            + "Passenger and its version."));

    public static readonly ToggleSetting AlwaysSelectOpenedFile = new(s => s.AlwaysSelectOpenedFile, (s, v) => s with { AlwaysSelectOpenedFile = v },
        new(D.ExplorerPage, "Always select opened file", "explorer solution view tree follow track sync locate active file tab autoscroll from source",
            "The Explorer chooses the file shown as a tab is switched to or a file opened. Off, it keeps the row chosen until "
            + "Select Opened File asks for the file shown. Its options menu turns this on and off too."));

    public static readonly ToggleSetting OpenFilesWithSingleClick = new(s => s.OpenFilesWithSingleClick, (s, v) => s with { OpenFilesWithSingleClick = v },
        new(D.ExplorerPage, "Open files with single click", "explorer solution view tree one click preview",
            "A plain click on a file's row opens it, the keys staying in the Explorer. Off, a double-click or Enter opens it. "
            + "Its options menu turns this on and off too."));

    public static readonly ToggleSetting NestFiles = new(s => s.NestFiles, (s, v) => s with { NestFiles = v },
        new(D.ExplorerPage, "Show files with the same names as nested", "explorer solution view file nesting nested dependent code-behind group appsettings razor",
            "A file whose name carries another's, as Counter.razor.cs carries Counter.razor, shows under it in the Solution view. "
            + "File Nesting… in the Explorer's options lists the rules."));

    public static readonly ChoiceSetting<ColourScheme> ColourScheme = new(s => s.ColourScheme, (s, v) => s with { ColourScheme = v }, D.ColourSchemeChoices,
        new(D.EditorPage, "Colour scheme", "colour color scheme syntax highlighting code colours rider keywords strings comments",
            "The colours the code is drawn in: the theme's own, or Rider's, Dark or Light as the theme is. The editor keeps the theme's background."));

    public static readonly FontSetting EditorFontFamily = new(nameof(AppSettings.EditorFontFamily), s => s.EditorFontFamily, (s, v) => s with { EditorFontFamily = v }, null,
        FontChoices.Code, new(D.EditorPage, "Font family", "font family typeface monospace monospaced code",
            "The font code is shown in: the editor's, the terminal's unless it has its own, and code's in popups and tool windows."));

    public static readonly NumberSetting EditorFontSize = new(s => s.EditorFontSize, (s, v) => s with { EditorFontSize = v },
        AppSettings.MinFontSize, AppSettings.MaxFontSize, 1, new(D.EditorPage, "Font size", "font size text"));

    public static readonly NumberSetting EditorLineSpacing = new(s => s.EditorLineSpacing, (s, v) => s with { EditorLineSpacing = v }, 0.8, 3, 0.1,
        new(D.EditorPage, "Line spacing", "font line height"));

    public static readonly ToggleSetting Ligatures = new(s => s.Ligatures, (s, v) => s with { Ligatures = v }, new(D.EditorPage, "Enable ligatures", "font arrows"));

    public static readonly ToggleSetting ShowLineNumbers = new(s => s.ShowLineNumbers, (s, v) => s with { ShowLineNumbers = v },
        new(D.EditorPage, "Show line numbers", "gutter appearance"));

    public static readonly ToggleSetting RainbowBraces = new(s => s.RainbowBraces, (s, v) => s with { RainbowBraces = v },
        new(D.EditorPage, "Rainbow braces", "brackets parentheses colour color nesting"));

    public static readonly ToggleSetting CloseTags = new(s => s.CloseTags, (s, v) => s with { CloseTags = v },
        new(D.EditorPage, "Close tags on '>' and '</'", "auto close closing end tags markup xml xaml html razor blazor msbuild elements"));

    public static readonly ToggleSetting XamlWorkspace = new(s => s.XamlWorkspace, (s, v) => s with { XamlWorkspace = v },
        new(D.EditorPage, "Open XAML files in the XAML workspace",
            "xaml workspace axaml preview designer live visual tree selection properties avalonia wpf layout side by side",
            "An Avalonia or WPF file opens beside its preview and, while its app runs, the Live Visual Tree, an element chosen in "
            + "any of them chosen in the others."));

    public static readonly ToggleSetting SolutionWideAnalysis = new(s => s.SolutionWideAnalysis, (s, v) => s with { SolutionWideAnalysis = v },
        new(D.EditorPage, "Solution-wide analysis", "errors problems whole solution analysis inspections without build"));

    public static readonly ToggleSetting ParameterHints = new(s => s.ParameterHints, (s, v) => s with { ParameterHints = v },
        new(D.EditorPage, "Show parameter name hints", "inlay hints parameter names arguments"));

    public static readonly ToggleSetting TypeHints = new(s => s.TypeHints, (s, v) => s with { TypeHints = v },
        new(D.EditorPage, "Show type name hints", "inlay hints type names var lambda parameters new inferred"));

    public static readonly ToggleSetting SoftWrapIndent = new(s => s.SoftWrapIndent, (s, v) => s with { SoftWrapIndent = v },
        new(D.EditorPage, "Use the original line's indent for wrapped fragments", "soft wrap wrapped indent indentation fragments"));

    public static readonly ToggleSetting ShowWhitespaces = new(s => s.ShowWhitespaces, (s, v) => s with { ShowWhitespaces = v },
        new(D.EditorPage, "Show whitespaces", "whitespace spaces tabs dots arrows invisible characters appearance"));

    public static readonly ToggleSetting WheelZoom = new(s => s.WheelZoom, (s, v) => s with { WheelZoom = v },
        new(D.EditorPage, OperatingSystem.IsMacOS() ? "Change font size with Command+Mouse Wheel" : "Change font size with Ctrl+Mouse Wheel",
            "zoom font size mouse wheel scroll ctrl control command cmd"));

    public static readonly ToggleSetting FoldFileHeader = new(s => s.FoldFileHeader, (s, v) => s with { FoldFileHeader = v },
        new(D.CodeFoldingPage, "File header", $"{D.FoldingWords} comments licence license copyright top",
            "Fold by default: what folds as a file opens. A file keeps the folds it was closed with, after a restart too."));

    public static readonly ToggleSetting FoldImports = new(s => s.FoldImports, (s, v) => s with { FoldImports = v },
        new(D.CodeFoldingPage, "Imports", $"{D.FoldingWords} usings using directives"));

    public static readonly ToggleSetting FoldDocComments = new(s => s.FoldDocComments, (s, v) => s with { FoldDocComments = v },
        new(D.CodeFoldingPage, "Documentation comments", $"{D.FoldingWords} doc xml summary comments"));

    public static readonly ToggleSetting FoldMethodBodies = new(s => s.FoldMethodBodies, (s, v) => s with { FoldMethodBodies = v },
        new(D.CodeFoldingPage, "Method bodies", $"{D.FoldingWords} members bodies accessors properties functions"));

    public static readonly ToggleSetting FoldPreprocessorRegions = new(s => s.FoldPreprocessorRegions, (s, v) => s with { FoldPreprocessorRegions = v },
        new(D.CodeFoldingPage, "Preprocessor regions", $"{D.FoldingWords} #region endregion directives .net"));

    public static readonly ToggleSetting FoldComments = new(s => s.FoldComments, (s, v) => s with { FoldComments = v },
        new(D.CodeFoldingPage, "Line and block comments", $"{D.FoldingWords} block comments .net"));

    public static readonly ToggleSetting FoldXmlTags = new(s => s.FoldXmlTags, (s, v) => s with { FoldXmlTags = v },
        new(D.CodeFoldingPage, "XML tags", $"{D.FoldingWords} xml elements tags project csproj props axaml xaml"));

    public static readonly ChoiceSetting<XamlAttributeStyle> AttributeStyle = new(s => s.XamlFormat.AttributeStyle, (s, v) => s with { XamlFormat = s.XamlFormat with { AttributeStyle = v } },
        Named<XamlAttributeStyle>(D.AttributeStyles),
        new(D.XamlCodeStylePage, "Attributes format", $"{D.XamlWords} attributes format separate lines single line first attribute do not touch",
            "Reformat Code lays out .axaml and .xaml by these, Rider's own. A file's .editorconfig overrides each by Rider's key: "
            + "xml_attribute_style, xml_attribute_indent, xml_indent_child_elements, max_line_length and the rest, with "
            + "xaml_enable_attribute_reordering, xaml_attribute_ordering_rule_groups and xaml_remove_ending_tag_of_empty_element."));

    public static readonly ChoiceSetting<XamlAttributeIndent> AttributeIndent = new(s => s.XamlFormat.AttributeIndent, (s, v) => s with { XamlFormat = s.XamlFormat with { AttributeIndent = v } },
        Named<XamlAttributeIndent>(D.AttributeIndents),
        new(D.XamlCodeStylePage, "Attributes indenting", $"{D.XamlWords} attributes indenting indent align first attribute single double"));

    public static readonly ToggleSetting SpacesAroundEquals = new(s => s.XamlFormat.SpacesAroundEquals, (s, v) => s with { XamlFormat = s.XamlFormat with { SpacesAroundEquals = v } },
        new(D.XamlCodeStylePage, "Spaces around '=' in attribute", $"{D.XamlWords} spaces equals"));

    public static readonly ToggleSetting SpaceAfterLastAttribute = new(s => s.XamlFormat.SpaceAfterLastAttribute,
        (s, v) => s with { XamlFormat = s.XamlFormat with { SpaceAfterLastAttribute = v } },
        new(D.XamlCodeStylePage, "Space after last attribute", $"{D.XamlWords} space tag header"));

    public static readonly ToggleSetting SpaceBeforeSelfClosing = new(s => s.XamlFormat.SpaceBeforeSelfClosing,
        (s, v) => s with { XamlFormat = s.XamlFormat with { SpaceBeforeSelfClosing = v } },
        new(D.XamlCodeStylePage, "Space before '/>'", $"{D.XamlWords} space self closing empty"));

    public static readonly ToggleSetting KeepLineBreaks = new(s => s.XamlFormat.KeepLineBreaks, (s, v) => s with { XamlFormat = s.XamlFormat with { KeepLineBreaks = v } },
        new(D.XamlCodeStylePage, "Keep existing line breaks", $"{D.XamlWords} line breaks keep user"));

    public static readonly NumberSetting MaxBlankLines = new(s => s.XamlFormat.MaxBlankLines, (s, v) => s with { XamlFormat = s.XamlFormat with { MaxBlankLines = (int)v } },
        0, XamlFormatSettings.MostBlankLines, 1, new(D.XamlCodeStylePage, "Maximum blank lines between tags", $"{D.XamlWords} blank lines empty"));

    public static readonly ToggleSetting MultilineElementsOnNewLine = new(s => s.XamlFormat.MultilineElementsOnNewLine,
        (s, v) => s with { XamlFormat = s.XamlFormat with { MultilineElementsOnNewLine = v } },
        new(D.XamlCodeStylePage, "Place multiline elements on a new line", $"{D.XamlWords} tags line breaks multiline"));

    public static readonly ToggleSetting SinglelineElementsOnNewLine = new(s => s.XamlFormat.SinglelineElementsOnNewLine,
        (s, v) => s with { XamlFormat = s.XamlFormat with { SinglelineElementsOnNewLine = v } },
        new(D.XamlCodeStylePage, "Place single-line elements on a new line", $"{D.XamlWords} tags line breaks single line"));

    public static readonly ToggleSetting BreaksInsideElementsWithChildren = new(s => s.XamlFormat.BreaksInsideElementsWithChildren,
        (s, v) => s with { XamlFormat = s.XamlFormat with { BreaksInsideElementsWithChildren = v } },
        new(D.XamlCodeStylePage, "Line breaks after start-tag and before end-tag in elements that contain other elements and no text",
            $"{D.XamlWords} tag content line breaks children"));

    public static readonly ToggleSetting BreaksInsideMultilineElements = new(s => s.XamlFormat.BreaksInsideMultilineElements,
        (s, v) => s with { XamlFormat = s.XamlFormat with { BreaksInsideMultilineElements = v } },
        new(D.XamlCodeStylePage, "Line breaks after start-tag and before end-tag in multiline elements", $"{D.XamlWords} tag content line breaks multiline"));

    public static readonly ChoiceSetting<XamlContentIndent> ChildIndent = new(s => s.XamlFormat.ChildIndent, (s, v) => s with { XamlFormat = s.XamlFormat with { ChildIndent = v } },
        Named<XamlContentIndent>(D.ContentIndents),
        new(D.XamlCodeStylePage, "Indent inside element that does not contain text", $"{D.XamlWords} indent children elements tag content"));

    public static readonly ChoiceSetting<XamlContentIndent> TextIndent = new(s => s.XamlFormat.TextIndent, (s, v) => s with { XamlFormat = s.XamlFormat with { TextIndent = v } },
        Named<XamlContentIndent>(D.ContentIndents),
        new(D.XamlCodeStylePage, "Indent inside element that contains text", $"{D.XamlWords} indent text tag content"));

    public static readonly ToggleSetting BlankLineAfterProcessingInstructions = new(s => s.XamlFormat.BlankLineAfterProcessingInstructions,
        (s, v) => s with { XamlFormat = s.XamlFormat with { BlankLineAfterProcessingInstructions = v } },
        new(D.XamlCodeStylePage, "Blank line after processing instructions", $"{D.XamlWords} processing instructions xml declaration blank line"));

    public static readonly ToggleSetting WrapTags = new(s => s.XamlFormat.WrapTags, (s, v) => s with { XamlFormat = s.XamlFormat with { WrapTags = v } },
        new(D.XamlCodeStylePage, "Wrap tags and processing instructions", $"{D.XamlWords} wrap long lines tags margin"));

    public static readonly ToggleSetting WrapAroundElements = new(s => s.XamlFormat.WrapAroundElements, (s, v) => s with { XamlFormat = s.XamlFormat with { WrapAroundElements = v } },
        new(D.XamlCodeStylePage, "Wrap before and after elements", $"{D.XamlWords} wrap long lines elements margin"));

    public static readonly NumberSetting RightMargin = new(s => s.XamlFormat.RightMargin, (s, v) => s with { XamlFormat = s.XamlFormat with { RightMargin = (int)v } },
        0, XamlFormatSettings.WidestMargin, 1,
        new(D.XamlCodeStylePage, "Right margin (columns)", $"{D.XamlWords} right margin columns wrap hard max line length")
        {
            When = new(s => s.XamlFormat.WrapTags || s.XamlFormat.WrapAroundElements, "Wrap tags and processing instructions and Wrap before and after elements are both off"),
        });

    public static readonly ToggleSetting CollapseEmptyElements = new(s => s.XamlFormat.CollapseEmptyElements,
        (s, v) => s with { XamlFormat = s.XamlFormat with { CollapseEmptyElements = v } },
        new(D.XamlCodeStylePage, "Remove ending tag of empty element", $"{D.XamlWords} empty element end tag self closing collapse xaml styler"));

    public static readonly ToggleSetting GDScriptWrapLongLines = new(s => s.GDScriptFormat.WrapLongLines,
        (s, v) => s with { GDScriptFormat = s.GDScriptFormat with { WrapLongLines = v } },
        new(D.GDScriptCodeStylePage, "Wrap long lines inside brackets", $"{D.GDScriptWords} wrap long lines line length brackets arguments arrays multiline",
            "Reformat Code lays a .gd script out by Godot's GDScript style guide, indented by its .editorconfig, else with "
            + "Godot's tabs. A line past the line length breaks inside its brackets in the guide's multiline forms. A file's "
            + ".editorconfig says over this by max_line_length: off leaves long lines as they are."));

    public static readonly NumberSetting GDScriptLineLength = new(s => s.GDScriptFormat.LineLength,
        (s, v) => s with { GDScriptFormat = s.GDScriptFormat with { LineLength = (int)v } },
        GDScriptFormatSettings.ShortestLine, GDScriptFormatSettings.LongestLine, 1,
        new(D.GDScriptCodeStylePage, "Line length (columns)", $"{D.GDScriptWords} line length columns max right margin hard wrap 100 80",
            "The style guide's is 100. A file's .editorconfig says over this by max_line_length.")
        {
            When = new(s => s.GDScriptFormat.WrapLongLines, "Wrap long lines inside brackets is off, so no line is wrapped"),
        });

    public static readonly NumberSetting GDScriptBlankLines = new(s => s.GDScriptFormat.BlankLinesAroundFunctions,
        (s, v) => s with { GDScriptFormat = s.GDScriptFormat with { BlankLinesAroundFunctions = (int)v } }, 1, 2, 1,
        new(D.GDScriptCodeStylePage, "Blank lines around functions and classes", $"{D.GDScriptWords} blank lines empty functions classes methods",
            "The style guide's is 2. A file's .editorconfig says over this by gdscript_blank_lines_around_functions."));

    public static readonly ToggleSetting GDScriptTrailingCommas = new(s => s.GDScriptFormat.TrailingCommas,
        (s, v) => s with { GDScriptFormat = s.GDScriptFormat with { TrailingCommas = v } },
        new(D.GDScriptCodeStylePage, "Trailing comma in multi-line arrays, dictionaries and enums", $"{D.GDScriptWords} trailing comma arrays dictionaries enums",
            "A file's .editorconfig says over this by gdscript_trailing_comma. A comma already written stays."));

    public static readonly ToggleSetting GDScriptFormatOnSave = new(s => s.GDScriptFormatOnSave, (s, v) => s with { GDScriptFormatOnSave = v },
        new(D.GDScriptCodeStylePage, "Reformat on save", $"{D.GDScriptWords} save actions on save format on save",
            "File, Save and Save All reformat each changed script first, as Reformat Code does, as one undoable edit. Autosave never does."));

    public static readonly ChoiceSetting<BuildResultsMode> ShowBuildResults = new(s => s.ShowBuildResults, (s, v) => s with { ShowBuildResults = v },
        Named<BuildResultsMode>(D.BuildResultsChoices),
        new(D.BuildPage, "Showing build results after build completed",
            "build tool window results after completed finished failed errors warnings always never toolset presentation"));

    public static readonly ToggleSetting HotReloadWhileDebugging = new(s => s.HotReloadWhileDebugging, (s, v) => s with { HotReloadWhileDebugging = v },
        new(D.HotReloadPage, "Hot Reload when debugging (Edit and Continue)", "hot reload edit and continue enc debug apply changes paused running alt f10"));

    public static readonly ToggleSetting HotReload = new(s => s.HotReload, (s, v) => s with { HotReload = v },
        new(D.HotReloadPage, "Hot Reload when running (dotnet watch)", "hot reload dotnet watch run apply changes on save running without debugging"));

    public static readonly ToggleSetting HotReloadOnSave = new(s => s.HotReloadOnSave, (s, v) => s with { HotReloadOnSave = v },
        new(D.HotReloadPage, "Apply changes when a file is saved", "hot reload save apply changes debug edit and continue"));

    public static readonly ToggleSetting XamlHotReloadOnSave = new(s => s.XamlHotReloadOnSave, (s, v) => s with { XamlHotReloadOnSave = v },
        new(D.HotReloadPage, "Apply XAML changes to the running app when a XAML file is saved",
            "hot reload xaml axaml save apply changes running app avalonia wpf run debug"));

    public static readonly ToggleSetting RestartOnRudeEdit = new(s => s.RestartOnRudeEdit, (s, v) => s with { RestartOnRudeEdit = v },
        new(D.HotReloadPage, "Always restart for a change it cannot apply", "hot reload rude edit restart ask always never dotnet watch"));

    public static readonly NumberSetting ConsoleBufferKb = new(s => s.ConsoleBufferKb, (s, v) => s with { ConsoleBufferKb = (int)v }, 16, 102_400, 64,
        new(D.RunPage, "Console buffer size", "console cycle buffer size output limit lines run debug build kb"), "KB");

    public static readonly ChoiceSetting<ToolConfiguration> Eslint = new(s => s.Eslint, (s, v) => s with { Eslint = v });

    public static readonly ToggleSetting EslintFixOnSave = new(s => s.EslintFixOnSave, (s, v) => s with { EslintFixOnSave = v },
        new(D.EslintPage, "Run eslint --fix on save", $"{D.EslintWords} fix on save actions on save autofix",
            "File, Save and Save All fix what ESLint can fix in each file it checks first."));

    public static readonly ChoiceSetting<ToolConfiguration> Prettier = new(s => s.Prettier, (s, v) => s with { Prettier = v });

    public static readonly ToggleSetting PrettierOnReformat = new(s => s.PrettierOnReformat, (s, v) => s with { PrettierOnReformat = v },
        new(D.PrettierPage, "Run on 'Reformat Code' action", $"{D.PrettierWords} run on reformat code action"));

    public static readonly ToggleSetting PrettierOnSave = new(s => s.PrettierOnSave, (s, v) => s with { PrettierOnSave = v },
        new(D.PrettierPage, "Run on save", $"{D.PrettierWords} run on save actions on save",
            "File, Save and Save All format each file the glob covers with Prettier first."));

    public static readonly ToggleSetting DownloadJsonSchemas = new(s => s.DownloadJsonSchemas, (s, v) => s with { DownloadJsonSchemas = v },
        new(D.SchemasPage, "Allow downloading JSON Schemas from remote sources",
            "json schema schemas remote download web http https schemastore $schema validation completion",
            "A JSON file that names a schema on the web ($schema) has it downloaded to check and complete it. package.json, "
            + "tsconfig.json, jsconfig.json, appsettings.json, launchSettings.json and global.json use the schemas Passenger "
            + "carries from SchemaStore, which are never downloaded."));

    public static readonly ToggleSetting DownloadStylesheets = new(s => s.DownloadStylesheets, (s, v) => s with { DownloadStylesheets = v },
        new(D.StylesheetsPage, "Allow downloading the stylesheets a page links from the web",
            "css stylesheet stylesheets class classes remote download web http https cdn bootstrap link completion html razor blazor",
            "A page's <link rel=\"stylesheet\"> to an address on the web (a CDN's Bootstrap) has the stylesheet downloaded once, kept in "
            + "Passenger's cache, to complete, describe and check the classes it defines; one whose link gives an integrity hash is "
            + "used only while it matches. The app's own stylesheets and its packages' are read from disk, never downloaded."));

    public static readonly ToggleSetting DockerHubTags = new(s => s.DockerHubTags, (s, v) => s with { DockerHubTags = v },
        new(D.DockerPage, "Complete image tags from Docker Hub",
            "docker hub dockerfile containerfile image images tag tags from completion registry remote download web network",
            "In a Dockerfile, completion after FROM image: lists the image's tags, which the Dockerfile language server asks "
            + "Docker Hub for, naming the image. Off, it asks Docker Hub nothing and offers no tags."));

    public static readonly ToggleSetting BuildKitChecks = new(s => s.BuildKitChecks, (s, v) => s with { BuildKitChecks = v },
        new(D.DockerPage, "Check Dockerfiles with BuildKit",
            "buildkit buildx build check checks lint rules warnings dockerfile containerfile base image images metadata registry registries network engine",
            "As a Dockerfile opens and is saved, the container engine's BuildKit checks it by its build rules with the engine's own "
            + "Dockerfile frontend, never one a # syntax= line names, which is neither pulled nor run. BuildKit reads each base "
            + "image's metadata from its registry. Off, no check runs and no registry is asked; a compose file's values and errors, "
            + "which Compose works out on this computer, stay."));

    public static readonly NumberSetting TerminalFontSize = new(s => s.TerminalFontSize, (s, v) => s with { TerminalFontSize = v },
        AppSettings.MinFontSize, AppSettings.MaxFontSize, 1, new(D.TerminalPage, "Font size", "font size shell"));

    public static readonly FontSetting TerminalFontFamily = new(nameof(AppSettings.TerminalFontFamily), s => s.TerminalFontFamily, (s, v) => s with { TerminalFontFamily = v },
        "Same as the editor", FontChoices.Code, new(D.TerminalPage, "Font family", "font family typeface monospace monospaced shell"));

    public static readonly ProgramSetting TerminalShell = new(s => s.TerminalShell, (s, v) => s with { TerminalShell = v },
        () => Passenger.Terminal.TerminalSession.DefaultShell().App, new(D.TerminalPage, "Shell path", "shell zsh bash powershell program"));

    public static readonly ProgramSetting DebugBrowser = new(s => s.DebugBrowser, (s, v) => s with { DebugBrowser = v },
        () => Core.ChromiumBrowsers.Find(null) is { } found ? found.Program : "Not found",
        new(D.RunPage, Debugging.BrowserDebugging.BrowserRow, "browser chrome edge chromium brave blazor webassembly wasm debug debugger page",
            "The Chromium browser Debug opens a Blazor app's page in, to debug its WebAssembly components, on a profile of Passenger's own, with any arguments "
            + "to give each start after it. Empty, it is the first of Chrome, Edge, Chromium and Brave found where they install, else on the PATH."));

    public static readonly ProgramSetting GodotExecutable = new(s => s.GodotExecutable, (s, v) => s with { GodotExecutable = v },
        () => Run.GodotLaunch.Found(null) is { Exists: true } found ? found.Executable : "Not found",
        new(D.GodotPage, "Godot executable", "godot godot4 engine game editor executable path program dotnet mono csharp c# scene environment variable",
            "The Godot a Godot project's game and its editor run on: its .NET build, which runs C#, with any arguments to give each "
            + $"start after it. Empty, it is the one {Core.GodotExecutables.Variable} names, else {Core.GodotExecutables.Variable4}, else the first on the PATH, "
            + "else on macOS the newest in Applications."),
        Run.GodotLaunch.DescribedAsync);

    public static readonly ToggleSetting GodotLiveValues = new(s => s.GodotLiveValues, (s, v) => s with { GodotLiveValues = v },
        new(D.GodotPage, "Show a running game's values in the code", "godot live values members fields properties variables hints inline running game sparkline",
            "While a Godot game runs, each member of a script's class shows its value at the end of its line, read from the game once a "
            + "second for the lines shown, from the node chosen in the Live Visual Tree where several run the script. Hovering a value "
            + "shows its last minute."));

    public static readonly ToggleSetting GodotTestsWindowed = new(s => s.GodotTestsWindowed, (s, v) => s with { GodotTestsWindowed = v },
        new(D.GodotPage, "Show the game's window as GUT and GoDotTest run its tests", "godot tests gut godottest window headless visible run unit",
            "Off, Godot runs a game's GUT and GoDotTest tests headless, with no window; on, it shows the game's window as they run. "
            + "GdUnit4 runs Godot as its run settings' parameters say. A Passenger nobody sees runs every Godot headless."));

    public static readonly ToggleSetting GodotDebugPlays = new(s => s.GodotDebugPlays, (s, v) => s with { GodotDebugPlays = v },
        new(D.GodotPage, "Debug what Godot's Play starts", "godot editor play addon debug attach breakpoint held start",
            "With Passenger's addon in a project, each game Play starts in Godot's editor waits at its start for this Passenger's "
            + "debugger, which attaches to it, so a breakpoint set here stops it. Off, Play still builds here, and the game runs undebugged."));

    public static readonly ToggleSetting GodotSelectionFollows = new(s => s.GodotSelectionFollows, (s, v) => s with { GodotSelectionFollows = v },
        new(D.GodotPage, "Show here the node selected in Godot's editor", "godot editor selection scene dock node outline follow addon",
            "With Passenger's addon in a project, a node selected in the scene dock of Godot's editor shows here: its scene at the node, "
            + "which the outline follows, and its script, behind, at its class. The keys stay where they are."));

    public static readonly ToggleSetting TerminalCopyOnSelection = new(s => s.TerminalCopyOnSelection, (s, v) => s with { TerminalCopyOnSelection = v },
        new(D.TerminalPage, "Copy on selection", "copy selection clipboard mouse drag word"));

    public static readonly ToggleSetting TerminalCursorBlinking = new(s => s.TerminalCursorBlinking, (s, v) => s with { TerminalCursorBlinking = v },
        new(D.TerminalPage, "Cursor blinking", "cursor caret blink blinking", "Programs can override this setting until the terminal is reset."));

    public static readonly ChoiceSetting<Passenger.Terminal.CursorShape> TerminalCursorStyle = new(s => s.TerminalCursorStyle, (s, v) => s with { TerminalCursorStyle = v },
        [(Passenger.Terminal.CursorShape.Block, "Block"), (Passenger.Terminal.CursorShape.Bar, "Line"), (Passenger.Terminal.CursorShape.Underline, "Underline")],
        new(D.TerminalPage, "Cursor style", "cursor caret type style shape block line bar underline", "Programs can override this setting until the terminal is reset."));

    public static readonly ToggleSetting McpServer = new(s => s.McpServer, (s, v) => s with { McpServer = v },
        new(D.McpPage, "Enable MCP Server", D.McpWords,
            "Agents started with passenger --mcp-attach can use this window without asking to: read and change files, build, run and "
            + "debug, use Git, NuGet and the terminal. Off, the window asks before letting each agent in. Either way, an agent asks "
            + "before it first uses each tool that changes anything beyond saving, stopping and stepping."));

    public static readonly ChoiceSetting<DiscordShown> DiscordShows = new(s => s.DiscordShows, (s, v) => s with { DiscordShows = v },
        Named<DiscordShown>(D.DiscordChoices));

    public static readonly NumberSetting DiscordIdleMinutes = new(s => s.DiscordIdleMinutes, (s, v) => s with { DiscordIdleMinutes = (int)v }, 1, 24 * 60, 1);

    public static readonly ToggleSetting TraceProtocols = new(s => s.TraceProtocols, (s, v) => s with { TraceProtocols = v },
        new(D.DiagnosticsPage, "Trace protocol messages",
            "trace log lsp dap json-rpc language server debugger adapter vstest tests messages verbose mcp agent calls",
            "Every message to and from the C# language server, the debugger and the test runner is written to lsp.log, dap.log "
            + "and vstest.log in the logs folder (Help, Show Log), and each agent's MCP call, with what it waited on and for how "
            + "long, to mcp.log."));

    public static readonly ToggleSetting LogEveryProgram = new(s => s.LogEveryProgram, (s, v) => s with { LogEveryProgram = v },
        new(D.DiagnosticsPage, "Log every program started", "debug log processes programs commands git dotnet exit code time",
            "Each program the IDE runs (git, dotnet, the servers) is written to passenger.log with its exit code and time. "
            + "Otherwise only those that fail or take a second or more are."));

    public static readonly ChoiceSetting<Passenger.Editor.KeymapPreset> Keymap = new(s => s.Keymap, (s, v) => s with { Keymap = v });

    public static readonly ChoiceSetting<DiffPlace> OpenDiffsIn = new(s => s.OpenDiffsIn, (s, v) => s with { OpenDiffsIn = v },
        [(DiffPlace.Window, "A separate window"), (DiffPlace.Tab, "An editor tab")],
        new(D.VersionControlPage, "Open diffs in", "diff diffs show diff compare with revision branch commit log files window tab editor separate git vcs",
            "Where Show Diff, Compare With, a commit's file in the Git log and Compare Files show the two versions. Changed, the "
            + "diffs open now move there."));

    public static readonly ChoiceSetting<DiffHighlighting> DiffHighlighting = new(s => s.DiffHighlighting, (s, v) => s with { DiffHighlighting = v },
        Vcs.DiffView.HighlightingWays,
        new(D.VersionControlPage, "Highlighting differences", "diff highlight highlighting differences words characters lines inline changed git vcs",
            "What a diff marks inside the lines that changed. A diff's own Settings changes it too."));

    /// <summary>An enum's values with the names Settings gives them, in the enum's order.</summary>
    private static (T Value, string Name)[] Named<T>(string[] names) where T : struct, Enum => [.. Enum.GetValues<T>().Zip(names)];
}
