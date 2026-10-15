# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this is

**Passenger** is a Rider-inspired IDE for C# and modern .NET (CoreCLR only: no .NET Framework, Mono or Unity),
written in C# on .NET 11 with an Avalonia 12 UI, for Windows and macOS first. The work still to do is the issues of
the GitHub MVP milestone (https://github.com/Zaneris/passenger/milestone/1), each saying what Rider does and when it
is done. Read the issue before starting anything larger than a fix, and close or tick it when it lands. When a reference for how something should behave is needed, JetBrains
Rider is installed at `~/Applications/Rider.app`.

## Commands

```sh
just build                 # Debug build of the solution (Passenger.slnx); warnings are errors
just run [PATH] [args]     # run the IDE on a folder or file (--file F, --caret L:C, --theme dark|light)
just screenshot X.png ...  # the main window drawn off-screen to a PNG, after the steps given
just publish RID           # self-contained Release build, ReadyToRun (osx-arm64, osx-x64, win-x64, win-arm64)
just package-mac [RID] [--dry-run]  # artifacts/package/Passenger-macos-arm64.dmg (or -x64), checked mounted; signed
                           # with PASSENGER_SIGN_IDENTITY and notarized with PASSENGER_NOTARY_PROFILE when set, else ad hoc;
                           # its window's background is tools/dmg-background.svg, drawn by sips at 1x and 2x into one TIFF
just icons                 # rewrite Theme/Icons.axaml from the list in tools/icons.py (icons from a fixed commit)
just app-icon              # Passenger.icns, Passenger.ico and passenger.png from Assets/passenger.svg, by sips; fails
                           # unless every size's corners are transparent (never write the three files any other way)
just package-linux [RID]   # artifacts/package/Passenger-linux-x64.tar.gz (or -arm64): one passenger/ folder (app/,
                           # the launcher, install.sh, the desktop entry, icon and file types from tools/linux), checked
just package-win [RID]     # artifacts/package/Passenger-windows-x64.exe (or -arm64), Velopack's per-user installer;
                           # signed on Windows by PASSENGER_SIGNTOOL_PARAMS or PASSENGER_ARTIFACT_SIGNING_METADATA
just notices               # fail naming any package the solution, Roslyn's server or a dotnet tool carries that the
                           # notices miss
just test [filter]         # every test, or those whose name matches
just test-times RUN [RUN...]  # rewrite TestTimes.json (each test class's seconds on CI, which deals the shards) from
                           # CI runs' results
just mcp [FOLDER]          # Passenger as an MCP server over a folder (this one by default), from a clone of the last build
```

**Use Passenger on itself.** `.mcp.json` starts Passenger's MCP server over this repository (`tools/mcp.sh`), so its
tools are the quickest way to ask Roslyn about Passenger's code (`diagnostics`, `find_symbol`, `outline`, `usages`),
to debug the IDE's own projects, and to look at the window (`screenshot`). It runs from an APFS clone of the last
Debug build, so `just build` never changes the files under it; reconnect the server to pick a new build up. A tool's
lines and columns count from 1 and its paths are the folder's. Anything it gets wrong is a Passenger bug to fix.

The screenshot is the self-check for anything visible (with nothing named it shows the Welcome screen, its list
empty, since a screenshot keeps no state: unless PASSENGER_SETTINGS names a folder, its state and logs go to a
temporary one removed as it ends, never the person's own log, and its terminal's shell keeps its history and startup
files apart from the person's). It opens what the command line names, takes
its steps in order through the window's real input, waits for background work to land, and captures
the compositor's own frame:

```sh
artifacts/bin/Passenger.App/debug/Passenger . --file src/Passenger.App/App.axaml.cs --caret 12:5 \
    --key enter --type "var x = 1;" --key "shift+meta+left" --click 400,300 \
    --screenshot out.png --size 1400x880 --scale 2 --crop 320,76,900,500 --theme light
```

A step after one that opens a dialog goes to that dialog, as a person's keys and clicks would. `--key` takes
"cmd+shift+L" or Rider's "meta shift L"; `--drag X1,Y1,X2,Y2` drags (`--drag-hold` keeps the button down,
to capture a tool window over its drop place); `--right-click X,Y` opens the menu of what is there; `--crop X,Y,W,H`,
`--click X,Y` and `--hover X,Y` (the pointer left to rest, for a hover card) are in logical units; `--click-name NAME`
clicks the visible control of that x:Name at its centre (a name none has ends it with exit code 2); `--wait MS` gives a
running program time.

Headless, the dispatcher's timers (the editor's hover, a caret's blink) fire inside a running dispatcher frame: a wait
that needs one pumps a frame (`Screenshot.Pump`, `Ui.WaitFor` in tests). One that has come due also fires while
`Ui.Settle` runs the queued jobs, so the caret's 530 ms blink redraws the editor during any settle that long: keep what
a key holds in `BackgroundWork` short (the XAML service waits for typing to pause only in a C# file under a loaded XAML
project).

Builds land under `artifacts/` (`UseArtifactsOutput`), never beside the sources. Package versions live only in
`Directory.Packages.props` (Roslyn's server's too, as `RoslynServerVersion`); shared properties only in
`Directory.Build.props`. Packages come from nuget.org alone, as nuget.org signed them (`nuget.config`), at what each
project's `packages.lock.json` pins: a restore that adds or changes a package rewrites the lock file, committed with
the change, and CI restores in locked mode. Every `just` recipe and CI set `AVALONIA_TELEMETRY_OPTOUT=1`, which
Avalonia's build task reads only from the environment. `Directory.Build.targets` gives every
`ProjectReference` its full path, because the .NET 11 SDK opens a solution reached through a symbolic link (anywhere
under macOS's `/tmp`) by the link while MSBuild resolves references from the real folder, which built each referenced
project twice at once.

**Look at UI changes.** After changing anything visible, run `just screenshot` and view the PNG at `--scale 2`
before calling it done. The capture runs on Avalonia's headless platform with real Skia drawing, so it never opens a
window or takes the focus; it shows everything but the system's own window chrome.

## Layout

- `src/Passenger.Core`: what every part shares and no UI needs. `ChildProcesses` starts every program Passenger runs (an
  input of its own, UTF-8, none of a git hook's variables, a log line when one fails or takes a while) and ends it with
  the IDE, a crash or a kill included (`Lifeline`: the runtime's job object on Windows; on macOS and Linux a process
  group per child, which a watchdog shell kills once Passenger's pipe to it closes). `DotnetCommand` builds without
  MSBuild's server (which Stop could not reach) and in nodes salted to this IDE, which end with it. `Dotnet`,
  `Programs` and `ShellEnvironment` find the tools as the login shell would; `PathNames` makes paths canonical
  (`realpath`, or `GetFinalPathNameByHandle` on Windows) and answers `IsUnder` for every "is this inside that folder".
- `src/Passenger.Text`: no UI. The rope (`Rope.cs`, an immutable B-tree, so every version is a
  thread-safe snapshot), `TextDocument` (undo, change events in LSP order), carets
  (`SelectionSet`) and every editing command as a pure function to an `EditPlan`.
- `src/Passenger.Editor`: the `CodeEditor` control (drawing, input, commands, in partial files),
  the keymap (`Keymap.cs`, chords included; defaults in `EditorActions.cs` follow Rider's Visual
  Studio keymaps) and the highlighting contract (`IHighlighter`), with JetBrains Mono embedded.
  An inlay hint (`CodeEditor.Hints.cs`) is a parameter's name, which goes with the text after it, or a type, which
  goes with the text before it; the hints at an offset are a caret place of their own, as IntelliJ's inlays are.
  `CodeEditor.Usages.cs` is what the caret lights up: the host's usage highlights (an edit takes them away unless
  kept, as Highlight Usages in File's are) and the bracket pair from a highlighter that is an `IBraceMatcher`
  (`CSharpHighlighter`, from its syntax, never the server). Soft wrap (`CodeEditor.Wrapping.cs`) makes every line one or
  more rows: `Rows` maps lines to rows and back, so whatever turns an offset into a place, a click into an offset or a
  line into a height goes through a row's `RowSpan`, never a `LineSpan` alone; an edit wraps again only the lines it
  touched.
- `src/Passenger.Languages`: colour before any language server answers. C# is Roslyn's parser
  in-process, incremental and off the UI thread (`CSharpHighlighter`), classified from syntax
  alone (`CSharpClassifier`); the language server's semantic colours layer over it. `CSharpHighlighter` is also each
  open file's one syntax service (`OpenDocument.Syntax`: the newest tree, carried through the edits since): a feature
  that reads C# syntax (templates, postfix, test marks, selection ranges, inline values) uses it, never a fresh parse.
  `Markup/FoldScan` finds XML's and JSON's folds in one scan of the rope off the UI thread; every fold region has a
  `FoldKind`, which Settings' Code Folding (Rider's Fold by default) and Collapse to Definitions read, and a folder keeps
  each file's folds as it closed (`WorkspaceState.Folds`, through `FoldMemory`). Every other language is coloured by VS
  Code's TextMate grammar for it (`TextMate/Grammars`, read by TextMateSharp with Onigwrap's Oniguruma), its scopes
  mapped to Rider's colours in `TextMate/ScopeColours` (named in full: TextMateSharp never matches parent selectors);
  `TextMateHighlighter` re-reads only from a changed line until the grammar's state is the same, off the UI thread.
  `Highlighters.For(language, document)` is the one place an editor's colours are chosen. A new grammar is its file,
  its scope in `Grammar.Files`, its language in `Grammar.Languages` and `Workspace/Language.ForPath` (with its
  comments), and its entry after THIRD_PARTY_NOTICES.md's marker line. Razor's grammar and C#'s (from dotnet/roslyn's
  Razor tooling) colour Razor files before the server answers, and C#'s colours Markdown's C# blocks. `TypeHints` turns Roslyn's type hints
  into Rider's (after the name, left off where Rider calls the type evident).
  A key costs what it changed, not the file's size: folds (`FoldRegions`) and bracket depths come from the parse
  before through the nodes Roslyn reused, never from a walk of the whole tree, and the server's colours are asked for
  the screen's lines first and the whole file only once typing stops.
- `src/Passenger.Xaml`: Passenger's own XAML core, no UI and no Avalonia, which WPF (#187) shares: `Syntax` (XmlTree and
  MarkupExtension, positions kept, tolerant of half-typed markup), `Metadata` (a TypeSystem over the project's
  assemblies through System.Reflection.Metadata and its C# through Roslyn's parser), `Projects` (ProjectInputs reads
  project.assets.json; XamlProjectBuilder keeps a project's types and XAML under its own lock), `Dialects`
  (AvaloniaDialect, WpfDialect) and `Features` (XamlCompletion, XamlDiagnostics, XamlNavigation, ThemeVariants, Colours). Not
  Avalonia.Ide.CompletionEngine, which is archived, predates Avalonia 12 and went with AvaloniaVS into Accelerate.
  A project's dialect is the framework it references (`XamlProjectBuilder.DialectOf`): Avalonia's package, or WPF's
  shared framework (restore's Microsoft.WindowsDesktop.App[.WPF], else UseWPF); any other's .xaml (MAUI, WinUI) gets no
  XAML service. WPF's types come from the WindowsDesktop reference pack, read from where restore put it
  (EnableWindowsTargeting off Windows) before the SDK's packs. Design-time data-context hints (d:DesignInstance,
  DataContext, an items control's ItemsSource for its templates and columns) are read only where the dialect compiles
  no binding, so Avalonia's compiled bindings read x:DataType alone. MarkupCompile's errors (MC####) mark their lines as
  AVLN's do; a named WPF element's code-behind field is MarkupCompile's `obj/…/File.g.cs` (the server's design-time
  build writes it), found from C# by its `#line`. An `x:Class` no C# declares is a class of its root element's type,
  declared at its `x:Class`, where the dialect's compiler makes it (`XamlDialect.MakesClasses`: WPF's MarkupCompile);
  Avalonia's compiler refuses one (AVLN2000), so there it stays marked. XAML Preview on a WPF file draws it on Windows
  (`WpfPreviewHost`) and elsewhere says WPF previews only on Windows; `XamlPreview` is the pane's model for either
  previewer (`AvaloniaPreviewHost`, `WpfPreviewHost`), its `PreviewElement`s what a click in the frame chooses and the
  caret outlines. `src/Passenger.WpfPreview` is WPF's previewer, Passenger's own (MIT, net8.0-windows, built
  everywhere with EnableWindowsTargeting, carried under `wpfpreview/` only in a Windows build or publish):
  `WpfPreviewHost` copies the project's last build to the state's `previews/<pid>-<guid>` (as Visual Studio's shadow
  cache, so `bin/` is never locked) and runs it with `dotnet exec` on the project's runtimeconfig and deps.json (or its
  own rolling-forward one for a library or a project before .NET 8); JSON lines over stdio (`Protocol.cs`, compiled
  into both), each frame a PNG at 96 DPI with the elements' boxes from `VisualDiagnostics.GetXamlSourceInfo`. The file
  loads as loose XAML (`LooseXaml`: the root of its tag's type, x:Class, events, EventSetters and x:Code blanked);
  App.xaml's resources become the application's; `pack://application:,,,/` is the project's through WPF's private
  resource-assembly fields. A WPF control with no code-behind has no constructor calling `InitializeComponent`, so it
  draws empty where it is used, in the app and its preview alike.
  `Features` also has `XamlFormatter`/`XamlFormatSettings` (Rider's XML formatter, which is XAML's, at Rider's
  defaults, read from .editorconfig by Rider's `xml_*` keys or their `resharper_` forms and `max_line_length`; XAML
  Styler's attribute order and empty-element options as `xaml_enable_attribute_reordering`,
  `xaml_attribute_ordering_rule_groups` and `xaml_remove_ending_tag_of_empty_element`, off by default; otherwise only
  whitespace changes, and a second pass changes nothing), `XamlRefactorings` (Extract Resource, Extract Style, Surround
  With's targets) and `CreateFromUsage` (a binding path's property or command, and a missing converter; the MVVM
  Toolkit known by its attributes' full names). `CSharp/CodeAssist.XamlEditing.cs` is the window's side (dialogs in
  `Xaml/XamlRefactoringDialogs.cs`, surround templates in `LiveTemplates.Xaml`), each change's files applied through
  `ApplyXamlChanges` as one command; Settings' Code Style: XAML page is `AppSettings.XamlFormat`.
  `src/Passenger.App/Xaml`: `XamlService` is the session's (`ProjectSession.Xaml`), one `XamlDocument` per open XAML
  file, projects read off the UI thread, told each build's AVLN problems; `LibrarySources` decompiles a library's
  type read-only; `SourceLink` fetches a theme's XAML through its PDB's Source Link from symbols.nuget.org (whose
  request needs the `SymbolChecksum` header; tests point `PASSENGER_SYMBOLS` at a dead loopback address).
  `XamlPreview`/`XamlPreviewPane` run Avalonia.Designer.HostApp (MIT) from the project's own Avalonia package through
  ChildProcesses over Avalonia.Remote.Protocol, restarted by `BuildRunController.BuildFinished`; frames are bitmaps at
  96 DPI sized by the host's scale, and the host's viewport resize requests go unanswered (answering made it ask again
  in a loop). `dotnet new avalonia.app` adds Accelerate's DevTools (`AvaloniaUI.DiagnosticsSupport`,
  `.WithDeveloperTools()`), which Passenger never adds or relies on.
- `src/Passenger.Lsp`: the Language Server Protocol client (StreamJsonRpc over stdio) and the
  Roslyn language server it runs, downloaded per platform at build and copied beside the app under
  `roslyn/`, less the four Visual Studio assemblies it never loads (`RoslynServerLeftOut`; the build fails if the
  package stops carrying one). `RoslynSession` opens the folder's solution; `LspDocument` keeps one document in step
  and makes requests in the editor's offsets. A server that stops, restarts or refuses a request is one exception,
  `LanguageServerFailed`, which callers catch; StreamJsonRpc's own types never leave the project. `RoslynExtension` is
  Passenger's own extension to the server (`src/Passenger.Roslyn.Extension`, compiled against the server package's own
  assemblies through `RoslynServer.targets`, carried under `roslyn-extension/` with none of them): the server loads it
  with `--extension`, `server/_vs_activateExtension` registers its handlers, and `LspDocument.ExtensionAsync` sends a
  message through `textDocument/_vs_dipatchExtensionMessage`. A handler reads the server's solution and answers with
  edits, never changing its workspace. Razor (.razor, .cshtml) is the server's own, cohosted in it: `LspDocument.IsRazor`
  opens such a file as `aspnetcorerazor`, its colours take the legend Razor registers as it starts (why the client
  capabilities take dynamic registration), its diagnostics are one pull, and `RazorHtml` answers what Razor asks the
  client of a file's HTML, since Passenger has no HTML server. A request nothing waits on, sent as a window closes,
  goes through `LanguageServer.Tell`, never awaited on the closing window's thread. Roslyn reads a column past a line's
  end as the line's end, so no change is sent between a "\r\n"'s halves: `LspDocument.Unsplit` takes in the half
  beside it and puts it back. `LanguageSession`/`ServerStart` run a server other than Roslyn: its `LspDocument`s carry
  a language id and their diagnostics are pushed (`DiagnosticsPushed`) or pulled. `RazorHtml` opens each Razor file's
  HTML in the HTML server (`LanguageServer.HtmlServer`) as `<uri>__virtual.html` and forwards Razor's HTML questions
  there, mapping answers' URIs back; a Razor file's pull adds the HTML server's diagnostics on lines its HTML has as the
  file does. `WebServers.targets` carries the web language servers under `web/`: `WebServers/package.json` pins them
  and `package-lock.json` everything they bring, each with its sha512; `tools/Passenger.NodePackages` puts them in
  place at build with no Node.js (cached in `artifacts/node/cache`), leaving out the HTML server's own TypeScript's
  tsserver, tsc and translations and moving its `lib*.d.ts` to `node_modules/node_modules/typescript/lib`, where the
  HTML server reads them; SchemaStore schemas go to `web/schemas`. To move a pin: edit package.json, run
  `npm install --package-lock-only --ignore-scripts` there, then update THIRD_PARTY_NOTICES.md's "Inside web/" table.
- `src/Passenger.App/Web`: `WebLanguages` is `ProjectSession.Web`, one server per kind (TypeScript over the project's
  own node_modules/typescript, else Passenger's; HTML; CSS, SCSS and Less; JSON), each run on `NodeRuntime` (Settings'
  Node.js page, else `Programs.Node()`) through ChildProcesses and started with the first file of its kind; without
  Node.js, `OpenDocument.Notice` (the editor's banner) and the status bar say why and the file keeps its colours.
  `WebDocument` is an `IServerDocument`, as `CSharpDocument` is, and `CodeAssist` asks it through `Current`.
  `JsonSchemas` answers `vscode/content` from the carried schemas, else downloads while Settings' Remote JSON Schemas
  allows (`PASSENGER_SCHEMAS` points the tests at a dead address). `CodeAssist.Web.cs` gives JSON the Go to Declaration
  (package.json dependencies) and property Rename its server lacks. Agents' MCP tools don't know web files yet.
  `WebLanguages.Eslint` (`EslintService`/`EslintDocument`) runs vscode-langservers-extracted's ESLint server per folder
  on the project's own eslint (`ProjectPackages`: found from the file up, or Settings' Manual package); its fixes are
  commands whose edits come back as `workspace/applyEdit` (`ServerStart.ApplyEdit`, applied through `WorkspaceEdits`),
  and its markers go beside the server's (`WebDocument.ShowLint`). `WebLanguages.Prettier` runs the project's prettier
  through `Web/prettier.cjs` (carried as `web/passenger/prettier.cjs`), one warm Node.js per folder over JSON lines.
  `CodeAssist.Linting.cs` is ESLint's context actions, Fix ESLint Problems, Reformat Code / Reformat with Prettier and
  `BeforeSave`, which `MainWindow.SaveExplicitly` runs on File Save / Save All only. A banner's link is
  `OpenDocument.NoticeAction` (Run 'npm install', through ChildProcesses).
- `src/Passenger.Host`: MSBuild in a process of its own (net10.0 on the machine's dotnet, one per SDK in
  `ProjectHosts`, started through ChildProcesses, carried under `host/`), loading the installed SDK's MSBuild (the one
  the folder's global.json picks) through MSBuildLocator. It answers JSON lines: `evaluate` (the Solution view's
  projects as a build sees them, `Workspace/ProjectModels`, cached in the folder's `projectModels.json`), `build`
  (Roslyn's design-time builds, written as its build host writes ProjectFileInfo, `Workspace/ProjectBuilds`, cached
  Brotli-compressed in `projectBuilds.json`) and `cancel` (a request given up: no more projects started, the build's
  submissions cancelled, so a left folder never holds the host) and `inspect` (one project evaluated for the code help
  in its MSBuild files, `Inspection.cs`: the open files' unsaved text fed to MSBuild in place of the disk's; properties
  with the places that set them, items, targets, imports, the SDK's frameworks and the evaluation's errors (a skipped
  import as MSB4019), places by a file table, answered from its cache while the inputs hold). Off Windows a project
  that references a framework the SDK marks Windows-only and leaves EnableWindowsTargeting unset is loaded again with it
  as a global property (`WindowsTargeting.cs`), in evaluate, build and inspect alike; Passenger's builds follow
  (`BuildOutput.WindowsTargeting`, `-p:EnableWindowsTargeting=true`), and Roslyn's server has it in its environment off
  Windows, since its `dotnet restore` takes no properties; it is never written into a project. Each result carries stamps of what it read
  (`Stamps.cs`), so a reopen redoes only what changed; the caches are read off the UI thread (`StateDocument.ValueAsync`).
  The shown file's project and what it references load first (`ProjectSession.ActiveProjectsAsync`).
  `Passenger.Roslyn.Extension/ProjectLoader.cs` subclasses the server's own `LanguageServerProjectLoader` so the server
  loads the host's builds (`passenger/loadProjects`, `passenger/projectInfo`, `passenger/projectLoader`), compiled
  against the server's publicized assemblies (`src/Publicize.targets`, which the debugger imports too); a server
  without the host builds its projects itself. A file in several projects (a multi-targeted project's) has the
  editor's target framework switcher (`CodeEditor.ContextLabel`, `MainWindow.TargetFramework.cs`), its requests naming
  the chosen context (`LspDocument.Context`, Roslyn's `_vs_projectContext`).
- `src/Passenger.App/CSharp`: the language server in the IDE. `CSharpService` runs Roslyn for the
  folder; `CSharpDocument` gives an open file its semantic colours and diagnostics; `CodeAssist`
  is completion, hover, signature help and the code actions (`app.code.*`), its popups on a layer
  over the editor; `WorkspaceEdits` applies renames and fixes across files. `UsageHighlighting` asks the server's
  `documentHighlight` once a split's caret rests and is Highlight Usages in File. `CodeAssist.Surround.cs` is Surround With
  and Complete Current Statement, from the file's syntax with no server (surround templates are `LiveTemplates` entries
  with a `Surround` scope); `CodeAssist.Refactorings.cs` is Optimize Imports (shared with Code Cleanup), Safe Delete
  (the server's definition and references, the removal from `SafeDeletion`) and the server's Introduce Field,
  Introduce Parameter and Encapsulate Field under Rider's names. `TypeRename` renames a type with the files named after
  it (their name up to the first dot), from Rename, the Solution view's Rename of a file and MCP's `rename`; Rename's
  options are the store's `refactorings.json`. `LiveTemplates` is the built-in live and surround templates and Rider's
  macros; `TemplateSet` is the set in force (the settings' `TemplateChange`s, each a built-in's difference keyed by the
  shortcut it ships with, over the built-ins), which `ApplySettings` hands to `CodeAssist.Templates`. `CodeAssist.Dialogs.cs` is Change Signature, Extract Interface, Move,
  Pull Members Up and Push Members Down: the extension describes what is at the caret, a `RefactoringDialog` asks the
  options, and the answer is applied inside `EditorArea.Commands.Record`, which keeps the files a command creates so
  Undo deletes them. The server's colour and diagnostics refreshes are two
  events, each refreshing only its own. Roslyn sends one toast per project-load batch, so `ProjectLoad.cs` reads its
  project-system log lines (`Warning/Error while loading P: …`, `Successfully completed load of P`) and `CSharpService`
  keeps each project's load problems, said in the status bar as a warning or error and listed in Problems' Toolset,
  Environment tab.
- `src/Passenger.App/Editors`: `EditorArea` is a tree of splits, each an `EditorGroup` (tabs, find bar, editor); its
  `CodeEditor` and `Active` are the active group's, so code that acts on "the editor" keeps working, and wiring for every
  editor's events goes through `ForEachEditor`, since a split makes a new one. An editor window (Open Tab in New Window,
  or a tab dragged out) is an `EditorFrame` of the same `EditorArea`, never a second area, so a file shown in two
  windows is one `OpenDocument`; the folder keeps the windows in `WorkspaceState.EditorWindows`. Pinned tabs come
  first (`EditorTab.IsPinned`, kept as `KeptTab.Pinned`); `EditorArea.CloseTab` is the one close a person's goes
  through, which Reopen Closed Tab remembers. `EditorArea.Commands` is Rider's global undo: a command that changes
  several files or moves one (`Commands.Record`, as renames do) is undone and redone whole, asked first. The tab
  actions are `MainWindow.Tabs.cs`, filling a tab's menu through
  `EditorArea.TabMenuOpening` with `ActionContext.Tab` naming the tab they act on. `MainWindow.CloseSolution` ends the
  folder's session and shows the Welcome screen; `LeaveFolder` keeps or closes editors and `EndSession` ends the
  session, so new per-folder state belongs in `ProjectSession`, made with it and ended in its end.
- `src/Passenger.App/Docking`: the tool windows' frame, in IntelliJ's six places. `DockHost` is the layout and what
  it keeps; `Stripe`, `ToolPane` and `FloatWindow` (a window of its own that knows nothing of tool windows) are its
  parts. A window moves by dragging a stripe button or a header (`DockDrag` says where it lands); `DragGesture` and
  `DropOverlay` are the one drag and drop zone, which editor tabs share. The layout is kept in the state store as a
  `DockLayout`, with the folder open and as the one a folder without its own opens on; `DockHost.RestoreDefault` puts
  each window back where `Dock.Add` put it. A new tool window is a view and
  one `Dock.Add` in `MainWindow.AddToolWindows`.
- `src/Passenger.App/State`: the IDE's own state, in one `StateStore` under `PASSENGER_SETTINGS` or the user's local
  application data, kept only by the person's window, shared by every window of the process that keeps state there
  (`StateStore.Open`), with `layout.json` only the default for a folder with no layout of its own: the app's documents at its top (`settings.json`, `layout.json`,
  `recentSolutions.json`, `updates.json`, `find.json`, `refactorings.json`)
  and each folder's under `workspaces/<name>-<hash>/` (editors and recent files, layout, breakpoints, bookmarks, run
  configurations, Find in Files' scope, `localHistory/`, the profiler's `snapshots/`), with the debugger's decompiled
  symbols in `cache/symbols/` at the root. A Passenger that Run, Debug or
  Profile launch (Passenger.App, or any program named Passenger) keeps its state in the launcher's `sandbox/`
  (`LaunchPlan.SandboxedState`, as IntelliJ's DevKit sandbox), unless its configuration sets `PASSENGER_SETTINGS`. A
  `StateDocument` is a record that is an `IStateDocument`: its `Upgrades` from each older version and its `Clamped` value;
  it is written debounced off the UI thread through a temporary file, merged over the file so values it does not know
  stay (and an entry another window removed stays removed), and a file that does not read is set aside as `.bad` and logged. New state is a document there, never a file of
  its own.
- Logs: one folder, `logs/` under the state root, written by `IdeLog` off a queue with every file rotated by size:
  `passenger.log` (IDE errors, the language server's warnings and errors, each folder's startup phases with times),
  `roslyn/` (the server's own logs), `lsp.log`, `dap.log` and `vstest.log`. Settings | Diagnostics turns on protocol
  traces (`TraceProtocols`) and a line for every program started (`LogEveryProgram`; otherwise only failing or slow
  ones); Help | Show Log opens the folder. What the language server says to show reaches the status bar.
- `src/Passenger.App/Vcs`: Git through the `git` executable (`Git`, porcelain v2), Myers' line diff (`LineDiff`) for
  the gutter's change bars, and `VcsController`, the folder's working tree (its session's; the Git and Commit views bind to it): status, branch, each open file's bars,
  commit, push, update, checkout and rollback (after which open files take their text from disk); `DiffView` is the
  side-by-side diff and `CommitGraph` the log's lanes. `GitView` is the Git window's content: the Log and a `GitLogView`
  tab per Show History, given a `HistoryScope` (a file followed with `--follow`, a folder, or lines with `-L`), read by
  `Git.HistoryAsync`, as `vcs_log` with a path reads it. `MainWindow.History.cs` is Show History, Show History for
  Selection, Compare with Branch or Tag and Compare with Revision. `WordDiff` (Passenger.Text, IntelliJ's ByWord) marks
  changed words inside changed lines for `DiffView` (Highlighting Differences is `AppSettings.DiffHighlighting`),
  `ChangePopup` and `MergeView`. `ToolWindows/BranchesPanel` is the Log's branches tree, its operations in
  `VcsController` and `MainWindow.Branches.cs`; a branch filters the Log through `GitLogView.FilterBranch`, and
  favourites are the folder's `gitBranches.json`. `MergeState` is what a merge, rebase, cherry-pick or
  revert stopped on and Rider's words for its sides (during a rebase yours is git's theirs); `ConflictsDialog` is
  Rider's Conflicts dialog and `MergeWindow`/`MergeView` the three-way merge over `ThreeWayMerge`;
  `MainWindow.Conflicts.cs` wires them with Continue Rebase and the aborts, and `vcs_status`/`vcs_resolve` give an
  agent the same. A file moved through `EditorArea.MoveFile` takes its index entry with it, as git mv does. `MainWindow.Vcs.cs` is the branch widget, the Commit and Git
  windows' places (`ToolWindows/CommitView`, `GitLogView`) and the `app.vcs.*` actions.
- `src/Passenger.App/Packages`: NuGet. `PackageSourceSet` reads the folder's nuget.config files through
  NuGet.Configuration as restore does (feeds, credentials, packageSourceMapping); `FeedSet` searches All Feeds or one,
  over `V3Feed`, `V2Feed` and `FolderFeed` (what the tests use offline); each HTTP feed's requests go through
  `FeedClient`, with its own credentials or else `CredentialProviders` (NuGet's plugin protocol, through
  ChildProcesses), never logged. `NuGetConfigEditor` changes one nuget.config in place, every other character kept;
  `PackageManager` changes projects through `dotnet add`/`remove package`. `ToolWindows/NuGetView` is the window, its
  Packages and Sources tabs. A run's NuGet is its own on macOS and Linux (DOTNET_CLI_HOME under the run's folder).
  `VulnerabilityData` reads the VulnerabilityInfo resource of the folder's auditSources (else its enabled sources), as
  restore's NuGetAudit does; the window marks vulnerable installed versions and offers the lowest that fixes them.
  `StandInFeed` is the tests' v3 feed on loopback (optional credentials, an advisory resource); tests never reach
  nuget.org for a package or an advisory.
- `src/Passenger.App/Discord`: Rich Presence over the Discord app's local IPC, Passenger's own client
  (`DiscordConnection`; `DiscordPresence` on one worker off the UI thread, at most one update every 4 s, a look every
  20 s without Discord). Only the person's window reaches Discord (`ShowActivityInDiscord`, called by App); the test
  run points `PASSENGER_DISCORD_IPC` at a place of its own where nothing listens.
- `src/Passenger.App/Settings`: `AppSettings` (the store's `settings.json`) and Rider's searchable `SettingsDialog`;
  `SettingsStore` holds them once per process: a change reads settings.json again, puts over it only what changed and
  raises `Changed`, which every window follows (`MainWindow.Follow`), the file watched for other Passengers. A new
  setting is a property, an option row in the dialog, a line in `MainWindow.Follow` (the window's side) or
  `SettingsStore.InForce` (the process's), and its range in `Clamped` when it has one. `SettingsDialog.Keymap.cs` is the Keymap page: Rider's
  Edit Shortcuts menu, the Keyboard Shortcut dialog (chords) and Find Actions by Shortcut; `Keymap.Conflicts` is Rider's
  conflict rule, and a conflict is removed, never kept, since a shortcut runs one action. A chord can't be a native
  macOS menu key equivalent; in-window menus show it as text.
- `src/Passenger.App/Updates`: the update check and the update. `Releases` asks GitHub's releases/latest (a GET naming
  Passenger and its version, nothing else) and offers a release newer than `Product.Version`, never a draft or
  pre-release; its files download over HTTPS only (`Downloads.cs`), checked against the SHA-256 the same release gives.
  `Updater` is how this copy installs one, only for a copy its installer or disk image put in place
  (`Updater.OfThisCopy`): `VelopackUpdater` on Windows (the release's `releases.<rid>.json`, full packages only) and
  `DiskImageUpdater` on macOS (the release's disk image checked against SHA256SUMS and its app by codesign, swapped in
  as the process ends). Every other copy (`just run`, tests, `--mcp`, screenshots) only links the download.
  `UpdatesState` (`updates.json`) keeps the version announced and the one being installed. `MainWindow.Updates.cs`
  is the status bar's notice, Check for Updates, Restart to Update and the daily check while Settings' Check for
  updates is on; `Program.Main` installs a ready update last.
- `src/Passenger.App/Templates`: New Solution and Add > New Project. `ProjectTemplates` reads `dotnet new`'s installed
  templates once per SDK from its own cache (`templatecache.json`, refreshed by `dotnet new list`) and each template's
  `ide.host.json` for the options an IDE shows; `NewProjectDialog` is Rider's New Project dialog;
  `MainWindow.NewProject.cs` opens a new solution through `Open` and adds a project through `SolutionEdits`.
  `ProjectTemplates.Ahead.cs` reads the templates ahead once a person's window has started (`MainWindow.PreloadTemplates`;
  screenshots and `--mcp` don't), keeps the newest per SDK (`Ready`, what the dialog opens with), and reads again when a
  watch on dotnet new's `packages.json` (home or `Hive`) or the .NET install's sdk/templates/template-packs folders sees a
  change, raising `Changed`. `TemplateFamilies` groups them as Rider's New Solution does: a template group is a Type
  that joins the first of Rider's project types its classifications name (`Kinds`, Rider's names and order), the rest
  grouped by their short name's family; `NewProjectDialog` lists families under a search, with a Type chooser whose
  choice sets the options below it, values the person set kept where the new Type has them.
  `TemplatePacks` is the template packages New Project offers when dotnet new lacks them (Avalonia's, and Rider's
  Aspire, Azure Functions and AI Extensions), their kinds kept with the version they were taken from; choosing one
  offers Install (`dotnet new install` through a `ProcessRunner` into the dialog's console), then the templates are
  read again (`ProjectTemplates.Forget`). Solution templates are New Solution's only and make the solution themselves.
  dotnet new runs with the template's skip-restore option and `DeveloperTools.TakeOut` removes Avalonia Accelerate's
  DevTools from what it made before Passenger restores; every `dotnet new` goes through `ProjectTemplates.New` or
  `NewCommand`, which add the IDE's `Hive` (`--debug:custom-hive`) when set.
- `src/Passenger.Profiler`: the profiler with no UI. `ProfilingSession` starts a program held at a diagnostic port of
  its own and streams EventPipe to a .nettrace; `Snapshot` reads it (TraceEvent) into call trees and hot spots.
  `App/Profiling` is the window (`ProfilerView`, `FlameGraph`), `ProfilerCommands` the Profile action. Every session
  enables `UiFrames.Provider`: `Snapshot.UiFrames` are an Avalonia app's frames, each with the compositor's first
  drawing after it, recorded by the inspector (below); `App/Profiling/FramesView` is the Frames page (`FrameChart`, the
  layout table by type then control, own time first), a control's row opening its XAML and, for the Live Visual Tree's
  process, choosing it there. `ProfilingSession.AttachAsync` profiles a running program at its own diagnostic port
  (Run > Attach Profiler to Running Program: the debugged one, else Run's); an `Attached` one is never killed.
  `MemorySnapshot` is a heap dump read with ClrMD into `HeapGraph` (roots by dotMemory's kinds, shortest paths, a
  dominator tree for retained bytes); `HeapHistory` follows a run's snapshots' objects through every GC (an EventPipe
  session of the GC's survival and movement events), so `SnapshotComparison` gives new, dead and survived objects;
  `App/Profiling/MemoryView` is the Memory page. ClrMD 3 finds no statics on .NET 9 and later, so
  `HeapGraph.StaticBases` reaches ISOSDacInterface14 through ClrMD's private fields: a ClrMD update keeps
  `MemorySnapshotsTests` passing.
- `src/Passenger.Terminal`: the terminal. `TerminalScreen` is the VT emulator (no UI), `TerminalView` draws it and
  encodes the keys, `TerminalSession` runs the shell in a pseudo-terminal (Porta.Pty); on Windows PowerShell is told
  `HISTFILE` as PSReadLine's `HistorySavePath` before its first prompt, so a test's or screenshot's never reaches the
  person's history. `MainWindow.Terminal.cs` is its
  tool window and the keys it takes ahead of the IDE's.
- `src/Passenger.App/Run` and `ToolWindows`: build and run through the .NET command line
  (`ProcessRunner` streams a process into a document; `BuildOutput` reads MSBuild's error lines),
  and the bottom panel's Build, Run and Problems windows. Every console (Run, Build, Debug, the profiler's) is written
  through `ConsoleSink`: a frame's output at a time, colour codes stripped whole across reads, the head trimmed past
  Rider's 1024 KB, and what is typed into it sent to the program. A run configuration is data (`RunConfiguration`: the
  folder's own in `runConfigurations.json`, the rest made from the executable projects' launch profiles, those that
  cannot launch here saying why: a project whose framework references Microsoft.WindowsDesktop.App runs only on
  Windows (`ProjectModel.WindowsOnly`), so off Windows the Solution view marks it, `RunConfiguration.WhyNotHere` greys
  its configurations and `LaunchPlan.ResolveAsync` refuses it before its build); `LaunchPlan.ResolveAsync` works one out once, in one MSBuild request, into what Run,
  Debug, Profile, Hot Reload and a debugged test host start, and `RunConfigurationsDialog` is Run > Edit
  Configurations. A build says what started it (`BuildCause`): explicit builds open the Build window as they start,
  the build before Run, Debug or Profile only with Settings' implicit-build option, a test run's never, and its end
  brings the Build window forward as Showing build results says (errors only by default). A test project's first Project launch profile gives its variables to its tests. A configuration that
  uses the terminal (Rider's Use external console) runs in a Terminal tab of its name (`TerminalPanel.RunAsync`, a
  `TerminalRun` with its process, exit code and Stop); Debug answers the adapter's runInTerminal the same way
  (`DebugController.RunInTerminal`), and `debug_input`/`debug_output` use that tab. In a program's tab, Run, Debug,
  Rerun, Stop and the debugger's keys stay with the IDE. Console links come from `ToolWindows/ConsoleLinks` (URLs, .NET
  stack frames, compiler messages; in the terminal also paths to any file, hidden until Cmd/Ctrl), drawn by
  `CodeEditor.Links` and `TerminalView.Links`; `MainWindow.Browse` is the only way the IDE opens a browser (recording
  `Browsed` for tests), and Run and Debug open a plan's `BrowserUrl` once its port answers (`Run/BrowserLaunch`).
- `src/Passenger.App/Testing`: unit testing works in Passenger's own `TestItem` (its id the project and full name, plus
  a data row's name) and `TestOutcome`, behind `ITestRunner`; `VsTestRunner` is the only file that knows VSTest's
  object model. `TestFinder` shows a solution's tests from code as it opens, before any build: test projects' files
  parsed off the UI thread, an open file's from its own parse (`TestsAtCaret.Read` is the one reading of a file's
  tests), changed ones through the folder watcher; discovery and runs merge into those rows by id.
  `MainWindow.Tests.cs` wires it. `TestRunners` sends each built assembly to its runner: a Testing Platform application
  (its `Microsoft.Testing.Platform.Application` assembly metadata: xUnit v3, MSTest's runner, TUnit) to
  `TestingPlatformRunner`, which runs it with `--server` through ChildProcesses and speaks the platform's JSON-RPC, one
  request per process; every other to `VsTestRunner`. `TestTree` groups by Rider's Group By levels (`TestGrouping.cs`) and filters by
  outcome and search; code that wants every test asks `TestTree.Methods` (each method once), never `Roots`. The
  grouping and filters are a folder's `unitTests.json`. `TestFrames` gives a run of a project with Avalonia.Headless
  and xunit v3 beside it the startup hook `src/Passenger.TestFrames` (carried under `testframes/`,
  `PASSENGER_TEST_FRAMES=1`): when an exception reaches xunit's runner frames on the headless session thread, it adds
  each shown window's `CaptureRenderedFrame`, popups drawn in, to the test's attachments as a PNG, before Dispose runs.
  `TestOutcome.Attachments` is read from VSTest's `TestResult.Attachments` and the platform's `attachments.N.uri`/
  `display-name` properties; `ToolWindows/TestFramesStrip` shows every picture attachment over the details, and
  `FrameViewer` shows one at its size with zoom.
- `src/Passenger.App/Navigation`: Recent Files and the Go to popups, `NavigationHistory` (Back and Forward, the places
  edited for Last and Next Edit Location, and those Recent Locations lists, carried through edits) and
  `RecentLocationsPopup`; `Navigator` records the places visited and edited. Base and Derived Symbols use the
  server's type hierarchy, references and implementations, since Roslyn has no go-to-base request
  (`CSharp/Declarations.cs`). Find in Files is `Navigation/FindInFilesPopup`; Rider's scopes (Solution, Project, Directory, Scope) and file mask are
  `Workspace/SearchScope.cs`, which `MainWindow.Search.cs` turns into files (`ScopeOf` for Solution view rows).
- `src/Passenger.App/History`: Rider's Local History. `LocalHistory` is a folder's store under
  `workspaces/<name>-<hash>/localHistory/` (content once per hash, Brotli, in `blobs/`; a JSON line per event in
  `changes.log`), fed by `FileText.Wrote` (every IDE write, with the bytes it replaced), the folder watcher (External
  change, or the `VcsController.Activity` name), files opened, `EditorArea.MoveFile` and Explorer deletes, off the UI
  thread; it prunes to Rider's 5 days of activity and a size cap (`HistoryKeeping`). `LocalHistoryView` is the tool
  window, reusing `DiffView` on the file's document. A test that deletes through the IDE sets `Trash.Folder`.
- `Explorer/FileNesting` nests a project's files in the Solution view by Rider's rules (its
  `ProjectViewNestingRulesProvider`s, chained as IntelliJ's `FileNestingBuilder` does), the project's `DependentUpon`,
  and `appsettings.*.json`; File Nesting… in the Explorer's options turns the rules off (`AppSettings.NestFiles`). A
  rename, move or copy takes a file's dependents along, and `ProjectEdits.MoveItems` makes the project's `Include`,
  `DependentUpon` and `LastGenOutput` follow. A tool window's own header ⋮ items are `ToolWindow.Options`.
- `src/Passenger.App/Workspace`: `SolutionModel` reads solutions and projects for the Explorer's Solution view, and
  `SolutionEdits` is every change to a .sln or .slnx (solution folders, the files they list, projects in and out): a
  .sln line by line and a .slnx through XDocument with its whitespace kept, so the rest of the file stays byte for byte
  as it was; never `dotnet sln`, which rewrites a whole .sln. `MainWindow.Solution.cs` is the Solution view's New
  Solution Folder, Existing Project, Existing Item, Rename and Remove. `ProjectEdits` puts a project's
  `ProjectReference`s in and takes them out as text, the rest kept byte for byte (never `dotnet add reference`), for
  Add > Reference (`MainWindow.References.cs`). `MainWindow.FileMoves.cs` is the Explorer's Cut, Copy, Paste and drags,
  every move through `EditorArea.MoveFile` so open files, breakpoints and bookmarks follow.
  `EditorConfig` reads the .editorconfig files over a file (its own parser and globs); `FileIndent` picks a file's
  indent (its .editorconfig, else `Indentation.Detect` on its lines, else its language) and names it in the status bar;
  `OpenDocument.Save` applies the save-time properties; a project edit or a save waits out a reader that shares no
  writes (Windows refuses the write while a project load reads the file) for up to 2 s (`FileText.WhileHeld`). Every
  edit from the language server goes through
  `LineEndings.Fit`, then `Indentation.Fit`. `OpenDocument` keeps how its file is stored, encoding, BOM and
  `LineSeparator` (a CR file's breaks are held as '\n' and written as '\r'); the status bar's widgets and File > File
  Encoding / Line Separators change them (`MainWindow.FileProperties.cs`), each one undoable step, a choice winning
  over .editorconfig's while the file is open. `FileEncoding` lists the encodings; `EncodingChange` weighs Reload
  against Convert as Rider's EncodingUtil does.
- `src/Passenger.App/PassengerApp.cs`: the process root. Its project windows (a folder or solution each), the one in
  front, and the one `OpenProject(path, ThisWindow | NewWindow)` that a start, Open, the recent solutions, the Welcome
  screen, the Window menu, the system's file activation (`App.OnActivated`) and `--mcp` go through; `NewWindow` makes a
  window as the one it is opened from. `MainWindow.Open` asks Rider's Open Solution (`OpenProjectDialog`) unless
  Settings' Open project in answers. Quit keeps every window's solution for the next start; a window closed alone does
  not. Discord's one presence (the front window's) and the one watch for updates live here. A window's state is its
  own fields, and a static says why it belongs to the process.
- `src/Passenger.App/ProjectSession.cs`: one open folder's services, made together and ended together: the folder and
  its solution, the language server, index, watcher, Local History, state, recent files and places, run
  configurations, the build and run runners, terminal and profiled runs, tests, the debugger and breakpoints, Git and
  bookmarks. `LeaveAsync` when the window stays (opening another folder waits for it, within the window's limit),
  `CloseAsync` with the window. The window builds `BuildRunController`, `ProfilerCommands`, `Navigator`, `PopupHost`
  and `StatusBar` over it, each a file of its own given an explicit parts record; a handler hung on a session acts only
  while that session is the window's (`Bind`).
- `src/Passenger.App`: the shell. Explorer, editor tabs, files on disk. Each action is an `IdeAction` (id, title,
  group, menu places, `Run`, `WhyNot`, whether its keys work while the terminal has them) that its feature adds to the
  window's `ActionRegistry`: `MainWindow.Actions.cs` for the window's own, a partial's or controller's `Add…Actions`
  for the rest, and a `ToolWindow`'s `Action` for its show action. `AppActions.cs` holds the ids and each platform's
  keymap. Keys, the main menu (`MainMenu.Build`), Find Action, context menus and MCP's `ide_action` all run actions
  through the registry, so one that cannot run says why; editor actions go to the focused editor.
  `Product` is what the build is (version, commit, runtime, the licence and notices beside it), the one place the
  version is read; `AboutDialog` is About (the application menu on macOS, Help elsewhere).
- `src/Passenger.App/Welcome`: the Welcome screen (`WelcomeView`, in the editor area while nothing is open) and the
  recent folders and solutions (`RecentSolutions`, the store's `recentSolutions.json`), read by the screen, File >
  Recent Solutions and a start that names nothing (Settings' Reopen projects on startup); `MainWindow.Welcome.cs`
  wires them and Clone Repository (`Vcs/CloneDialog`, `Git.CloneAsync`). `MainWindow.Open` is how File > Open (a
  solution or project), Open Folder, the Welcome screen and Recent Solutions open a workspace, refusing anything else
  with why (Avalonia's panel takes files or folders, never both). A .csproj opens on its own, as Rider opens one
  (`WorkspaceModel.ProjectFile`); what was opened is kept and reopened as itself (`WorkspaceModel.OpenedAs`), under
  the name that is also the window's title. `Installation.cs` is what an installed
  Windows copy does on install, update and uninstall (Velopack's hooks): the file types it opens, under the user.
- `src/Passenger.App/Mcp`: Passenger as an MCP server (the official C# SDK). `PassengerTools` is every tool, each
  run on the UI thread as the window's own commands are and ended by its `CancellationToken`, answering with what its
  operation returned (`VcsOutcome`, `CommitResult`, `BuildOutcome`, the debugger's `Stop`), never with what the status
  bar says by then. The tools reach the folder through the window's `ProjectSession` as each call finds it, and the
  window only for editors, the terminal, actions, opening and the picture; `McpHost` hosts them
  three ways: `--mcp [FOLDER]` (a headless IDE on standard input and output), a window's own socket under the state
  folder (`AgentSocket`, open only while the person's Enable MCP Server setting is on), and `--mcp-attach [PID]`
  (standard input and output joined to the window over the agent's folder). On a person's window a tool neither
  `ReadOnly` nor `Destructive = false` asks them the first time an agent uses it (`ConnectedTool`).
- Attach to Process: `MainWindow.Attach.cs` (`Debugging/AttachToProcessDialog`, Reattach, the recently attached);
  `Core/DotnetProcesses` lists the .NET programs a debugger can attach to (the runtime's `clr-debug-pipe-*` in TMPDIR
  on macOS and Linux, checked against the process's start; its diagnostic pipe on Windows). An attached session is
  let go by Detach, by leaving its folder and by the window's close, never ended by them. On macOS the debugger needs no entitlement and no
  get-task-allow on its target: the adapter runs under the machine's dotnet and talks to the runtime through its
  pipes, so the package carries no com.apple.security.cs.debugger.
- Return values and Smart Step Into: a step over sets a breakpoint after each call in its statement
  (`ICorDebugCode3.GetReturnValueLiveOffset`) and keeps each value by a strong handle (a value type boxed by an eval,
  after which the step's stepper is made anew, since an eval mid-step loses it); `$ReturnValue`/`$ReturnValueN` are
  expression-compiler aliases, as `$exception` is, typed by the program's own assemblies. Smart Step Into matches the
  statement's IL calls to its Roslyn syntax by name, in order, and steps over it with a breakpoint at the chosen
  method's first line; the editor's choice mode is `CodeEditor.Choices`.
- `src/Passenger.App/Debugging`: `BreakpointStore` holds line breakpoints by file and Rider's CLR exception
  breakpoints (`ExceptionBreakpoint`; "Any exception" is every folder's and is never removed), kept with the folder;
  `Muted` is Mute Breakpoints, which sends none while on. The engine decides exception breakpoints (`ExceptionRule`)
  when the runtime reports a handler found or an exception unhandled, the stack still at the throw ("user code" is a
  Just My Code frame); they cross the protocol as DAP `exceptionOptions` with Passenger's own scenarios and conditions.
  An evaluation the runtime won't start where the thread is stopped (a pause in optimized or native code) is
  `EvalRefusedException`; a value shown by its ToString then shows its type. Data breakpoints (Variables' Set Data
  Breakpoint, `ManagedDebugger_DataBreakpoints`, `DebugRegisters`): the runtime watches memory only on Windows x64
  (`FEATURE_DATABREAKPOINT`), so elsewhere the item is greyed with the reason; the engine pins the object
  (`ICorDebugHeapValue4`, the runtime's advice over GC notifications) and sets DR0–DR7 on every thread through both the
  runtime's context and Win32. They last one session, as Rider's do, and their window tests run only on Windows x64.
  XAML breakpoints: XamlIl (through Cecil) writes a sequence point per .axaml element and attribute, in documents of
  no language, mid-expression, where the JIT maps few; for such a document `ModuleMetadataReader.ResolveBreakpoint`
  binds at `JitBoundaryAtOrBefore` (after a call, a nop, the method's start), an attribute with no point of its own at
  the one after the attribute before it (`UnpointedAttribute`), a template's root in its builder, and the stop is shown
  at the breakpoint's place. `LineBreakpoint.Attribute` is an attribute's breakpoint (F9 on it, `XamlBreakpoints`),
  kept with the folder, sent with its name's column. `PropertyBreakpoint` is the Live Visual Tree's Break When Value
  Changes, for one session: the inspector watches that element's property (protocol v3 `watch`, `PropertyBreaks.cs`)
  and calls `Breaks.Changed`, on which the IDE sets one function breakpoint per property breakpoint (condition
  `breakpoint == Id && (…)`, the hit count kept in the app). The inspector is never optimized (the debugger reads that
  call's arguments) and its watching code is `[DebuggerNonUserCode]`, shown as external code under Just My Code, so
  the stop selects the code that set the value. `BindingErrorBreakpoint` is XAML Binding Errors ("Any binding
  error", off until turned on, never removed, kept with the folder): under a debugger the inspector's binding sink calls
  `Breaks.BindingFailed(BindingError)` (`Breaks.cs`, with `Breaks.Changed`) on each failure, repeats too, on the thread
  that evaluated it, where the IDE sets one function breakpoint (`Wire.Failed`), sent with the property breakpoints' in
  one `setFunctionBreakpoints`; its condition and log message say `$bindingError` (rewritten to the parameter). The
  stop reads `bindingError.Said` (a `BindingFailure` as JSON) in frame 0, selects the first user frame, says the failure
  in the status bar, annotates the line, lists `$bindingError` in Variables and writes a linked console line. A function
  breakpoint's binding is told apart by its module, not its base address: the inspector is two modules over one image.
  The engine's lock (`EngineLock`) is held by one request or runtime event at a time and is not re-entrant: code under
  it never takes it again (`DrainRuntimeEventQueue` runs under its caller's hold).
- The debugger, Passenger's own, started from SharpDbg and ICorDebugSharp (MIT; `THIRD_PARTY_NOTICES.md` says what came from where):
  `src/Passenger.Debugger.Interop` (ICorDebug, IMetaDataImport and dbgshim for the COM source generator),
  `src/Passenger.Debugger` (the engine), `src/Passenger.Dap` (the Debug Adapter Protocol, both ends) and
  `src/Passenger.DebugAdapter` (the engine behind the protocol, its own process, carried under `debugger/`). Before
  letting a program go (Disconnect without terminating), the engine runs it off any patch or single-step stop and stops
  it again (`ManagedDebugger_LetGo.cs`): under CET a thread resumes from a breakpoint only through the debugger's
  out-of-process SetThreadContext, which mscordbi's Detach waits for only once it has answered one such flare, so a
  first stop after an attach, detached at, would fault (0xC0000409). Symbols for assemblies without their own are
  decompiled into the folder the adapter is given (`--symbolCache`; the IDE's `DebugSession.SymbolCache`, the state
  folder's `cache/symbols`) by `Decompilation/SymbolCache`: `NAME/MVID/NAME.<decompiler>-<Format>.pdb` (raise
  `SymbolCache.Format` when `PortablePdbWriter2`'s output moves), written through a temporary file and renamed, one that
  does not load deleted and made again once, pruned as the adapter starts (30 days unused, 512 MB, never one used in the
  last day). A decompile never runs under `EngineLock`: resolveStackFrame takes `SymbolsWantedAt` under it,
  `DecompileAsync` off it, then the frame under it again; without Just My Code a stop in such an assembly waits as
  `AfterDecompiling`, dropped if the program was let go. A [DebuggerDisplay] fragment of dotted names that doesn't
  evaluate is read as the value's property by that metadata name (Roslyn's `{System.Collections.ICollection.Count}`).
  configurationDone's answer comes before any of the program's events: the adapter launches or attaches under the
  engine's lock and keeps it until the answer is written (its `then`, or `AbortAsync` when the client went), and
  `DebugSession.StartAsync` leaves a stop told before that answer standing, as `Resume` does.
  Hot Reload while debugging (Edit and Continue): `Passenger.Roslyn.Extension/HotReload.cs` drives the server's own
  `EditAndContinueService` (its session starting at the debugging session's first C# edit, given each file's pre-edit
  text, checked against the build PDB's checksums), the runtime's capabilities read from the program's CoreLib; the
  deltas reach the engine as Passenger's `applyChanges` request (`Passenger.Dap`, with `hotReloadState`), and
  `ManagedDebugger_HotReload.cs` applies them (`ICorDebugModule2.ApplyChanges`, which needs `GetMetaDataInterface` asked
  first) and remaps frames (`FunctionRemapOpportunity`/`RemapFunction`, a stepper deactivated before the remap).
  `ModuleMetadataReader_EnC.cs` keeps each update as a generation: read a method's sequence points, locals and IL only
  through `Points`, `DebugOf` and `MethodIL` at the frame's EnC version (`VersionOf`), never the PDB's directly; after
  an update `_peMetadataReader` is mscordbi's saved copy of the module's metadata, `_imageMetadataReader` the image's.
  A Debug launch sets `DOTNET_MODIFIABLE_ASSEMBLIES=debug`. `Debugging/HotReload.cs` and `MainWindow.HotReload.cs` are
  Apply Hot Reload Changes (Alt+F10, toolbar and Debug/Run windows' flame, edits made while paused applied on the next
  resume or step), Problems' Hot Reload tab for rude edits, and the restart (Settings' Hot Reload page: always restart
  for a change it can't apply, on by default, else dotnet watch's question); attached sessions get none.
- `src/Passenger.Inspector`: the Live Visual Tree's helper inside an Avalonia app being debugged (Passenger's own, MIT,
  net8.0, carried under `inspector/`, never put in the app's build): a startup hook `ProjectSession.Inspected` gives only
  a Debug launch whose build has Avalonia.Base.dll beside it (`DOTNET_STARTUP_HOOKS`, which it takes back out of the
  environment). It compiles against Avalonia 12's implementation assemblies (the reference ones leave out [PrivateApi]
  value-store diagnostics) and runs in a load context of its own that takes the app's Avalonia whatever its 12.x; it
  never touches `Dispatcher.UIThread` before the app makes it, and answers on the app's UI thread over a Unix socket in
  the temp folder (macOS allows 104 characters). `Protocol.cs` is compiled into both ends, deeper than System.Text.Json's
  default 64. `App/LiveTree/LiveApp` is Passenger's end (the session's, ended with its debugging); `LiveTreeController`
  and `LiveTreeView` are the window, which asks nothing while the app is paused. Why a style fails is read from its
  selector's text (`Selectors.cs`); whether it applies is always Avalonia's answer. Agents' tools (`app_*`,
  `Mcp/PassengerTools.App.cs`) ask the same inspector: its windows, the tree with each element's box, text and state,
  `capture` (`Pictures.cs`: the window rendered by the app at a scale, its popups drawn over it) and `input`
  (`Driver.cs`: raw input into the window's `ITopLevelImpl.Input` with a mouse device of its own, so the window is never
  activated and the real pointer never moves, waiting for a rendered frame before and after, since hit testing reads
  the last one). `LiveTree/AppQuery` is how an agent names elements (`@id`, type, #name, .class, "text", steps by /,
  [n]); `LiveApp.Own()` loads the inspector into Passenger itself for `app: ide`, once per process. Binding values
  (`BindingValues.cs`, protocol v4 `values`): each element by its XamlSourceInfo place (a root by x:Class), of several
  instances the one nearest the element chosen in the Live Visual Tree, each binding's path walked from Avalonia's
  binding expression nodes. Paused, the IDE evaluates `Wire.ReadValues` on the UI thread (the stop's, else the main
  thread; the startup hook's copy forwards to the inspector's own), the answer an array whose slots the debugger shows
  as Variables does; running, only for the control chosen, read again on `valuesChanged`.
  `App/LiveTree/BindingValuesController` shows them after the lines (`OpenDocument.LiveValues`,
  `LineAnnotationKind.Failed`) and on hover. The runtime evaluates nothing on a UI thread waiting outside .NET, so a
  stop on another thread shows none, saying so. The startup hook turns on Avalonia's
  `Avalonia.Diagnostics.Diagnostic.IsEnabled` switch, so Avalonia makes its `Avalonia.Diagnostic.Source` Measure and
  Arrange activities (tagged `Control`) and `Avalonia.Diagnostic.Meter` histograms; `FrameRecorder` (`Frames.cs`)
  listens only while a session enables Passenger's EventSource `Passenger-Avalonia`, so they land in the .nettrace.
  Protocol v5 `overlays` sets each window's `RendererDiagnostics.DebugOverlays` (the Run and Debug windows' toggle,
  `LiveTree/OverlaysCommands`, `ToolWindows/OverlaysButton`, Run > Avalonia Debug Overlays; the last choice is the next
  launch's `PASSENGER_INSPECTOR_OVERLAYS`). Profile launches get the inspector too (`ProjectSession.InspectedProfile`).
  `src/Passenger.Inspector.Wpf` is the WPF counterpart (MIT, net8.0-windows, built everywhere with EnableWindowsTargeting,
  carried under `inspector/wpf/` only by a Windows build or win-* publish): `LiveApp.For` gives it (`LiveAppKind.Wpf`) to
  a Run or Debug launch on Windows whose runtimeconfig names Microsoft.WindowsDesktop.App and whose assembly references
  PresentationFramework, with `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1`. It links Protocol.cs and Connection.cs (the same
  socket, protocol v5, `Hello.Framework`), runs in the default load context, and retries a main-thread dispatcher WPF is
  still making. Value sources are `DependencyPropertyHelper.GetValueSource`; places `VisualDiagnostics.GetXamlSourceInfo`
  turned into files through the assembly's PDB; binding errors come from `PresentationTraceSources` and
  `BindingDiagnostics.BindingFailed`, and `Breaks.BindingFailed`/`Changed` keep their names so one function breakpoint
  serves both frameworks (XAML Binding Errors; a WPF stop has no user frame, so the editor shows the XAML). Its
  `Driver.cs` clicks through UI Automation patterns in-process and sends WPF's own key, text and wheel events, never
  activating a window. `PASSENGER_INSPECTOR_TRACE=FILE` traces its start and answers. A stop whose ids came in an unanswered setFunctionBreakpoints waits for
  that answer (`DebugSession.TakeStopAfter`), so a property or binding-error stop is known as one.
  The inspector also takes Avalonia's logger before the app's Main (`Bindings.cs`, put back over any sink the app sets,
  which is still fed) and tells of each failed binding (`bindingFailed`: control, property, path, message, XAML place,
  and for a member the source's type lacks its nearest members) once, then its count at most every 250 ms. Run gives an
  Avalonia app the inspector too (`ProjectSession.InspectedRun`, `RunApp`; none under Hot Reload, whose dotnet watch
  would load it); the Live Visual Tree follows only the debugger's `LiveApp`. `App/LiveTree/BindingErrors` is how a
  failed binding shows: a console line with its place first in a compiler message's shape (so `ConsoleLinks` links it),
  Problems' Bindings tab (`BindingProblem`, by file, each once with its count), and fixes through
  `CodeAssist.EditorFixes`, Passenger's own fixes that Show Context Actions lists before the server's.
- `tools/Passenger.Notices`: writes the app's `THIRD-PARTY-NOTICES.txt` (beside `LICENSE.txt` in every build and
  publish, through `WriteNotices` in `Passenger.App.csproj`) from the packages the build copies, the self-contained
  runtime pack, the debugger's and Roslyn's server's `.deps.json`, each package's licence and copyright, and the part
  of THIRD_PARTY_NOTICES.md after its marker line; a licence it cannot write fails the build.
- `src/Passenger.App/MSBuild`: code help in project, .props and .targets files. `MSBuildService` is the session's
  (`ProjectSession.MSBuild`), one `MSBuildDocument` per open MSBuild file, evaluated through its project (itself, the
  workspace project whose evaluation imports it, a Directory.* file's nearest project, or the one Go to Declaration
  came from) once typing pauses, on the text as typed. Completion reads the text as typed (`Passenger.Xaml`'s XmlTree)
  with `MSBuildSchema` (dotnet/msbuild's Core and CommonTypes XSDs, embedded because the SDK lacks them) and the last
  evaluation that succeeded; package ids, versions and advisories come from the window's NuGet (`NuGetView.FeedAsync`,
  `VulnerabilitiesAsync`), and under CPM a new PackageReference takes no version while Directory.Packages.props gets
  its PackageVersion, as one command. Installed MSBuild files (the SDK's, packages') open read-only
  (`MSBuildFiles.IsInstalled`). `CodeAssist.MSBuild.cs` connects it to completion, the hover card and Go to Declaration.
- `MainWindow.TitleBar.cs` and `TitleBars`: the toolbar is the main window's title bar, as Rider's header is: always
  on Windows and macOS, on Linux by Settings' Merge main menu with window title (off by default while it rests on
  Avalonia's experimental X11 decorations; greyed under WSL, tiling window managers or an unnamed desktop). Windows
  learns the bar's parts from Avalonia 12's `WindowDecorationProperties.ElementRole` (TitleBar on the toolbar, User on
  what is on it, Minimize/Maximize/CloseButton on its window buttons), so Windows itself moves, snaps and maximizes the
  window and shows snap layouts: anything new on the bar goes inside `ToolbarStart` or `ToolbarEnd`, or Windows takes a
  press on it as the caption's. Linux and macOS move the window through `OnToolbarPressed` (macOS's double click as
  the system's setting says). The layout follows `TitleBars`, not `IsExtendedIntoWindowDecorations`, so headless
  pictures show the merged bar; Avalonia's own drawn decorations get the empty `TitleBarDecorations` theme. Only the
  main window merges.
- `tools/package-mac.sh` and `tools/dmg-layout.py`: the disk image's Finder window is a .DS_Store written as Finder
  writes one, with no AppleScript: the icons' places and the background (`.background/background.tiff`) named by a
  Carbon alias made on the image's own read-write mount, since it carries the volume's creation date and the picture's
  catalog IDs, which the compressed image keeps; `dmg-layout.py check` reads it all back on the mounted image. The
  picture stays light in either appearance, as Finder draws the names black over a light picture in Dark too.
- `tools/linux`: what the Linux archive carries beside the app: the launcher, install.sh (per user, `--system`,
  `--uninstall`), the desktop entry and the file types it opens. CI runs the suite on Ubuntu 24.04 too, and installs,
  uses and uninstalls the archive. ClrMD's DAC stays loaded once used (`HeapGraph.Runtime`): unloading it crashed the
  IDE on Linux. Linux's file systems tell letter case apart, so a test of another letter case skips there.
- `site/`: passenger-ide.net, plain HTML and CSS, deployed to Cloudflare Pages by `.github/workflows/site.yml` on a push that
  touches it. Its downloads are the latest GitHub release's assets, by fixed names (#56); its share card,
  `images/social.png`, is drawn from `tools/social-card.html` (the command is in its first comment).
- `.github/workflows`: `test.yml` is every test on macOS, Windows and Linux, in four shards on macOS/Linux and five on Windows,
  run at once (jobs named as "macOS (arm64), shard 1 of 4"), then a summary job per system named as the system ("macOS
  (arm64)", the check that stands for it). It is called by `ci.yml` (each push and pull request, with the packages'
  and notices' jobs) and by `release.yml`, which a pushed `vX.Y.Z` tag runs: the tests, then both disk images and both
  Windows installers at the tag's version, `SHA256SUMS` and Velopack's feeds, published as the release under the fixed
  names the site and the updater link to. Its Run workflow with dry run builds it all and publishes nothing.

## Rules

- **Never use Microsoft's debugger (vsdbg) or anything from C# Dev Kit**, including Dev Kit's pieces that ship inside
  VS Code's C# extension (`.debugger`, `.roslynDevKit`, `.testDiscovery`). Their licences limit them to Microsoft's
  own editors. Nothing is copied out of an installed extension's folder; `roslyn-language-server` comes from NuGet.
  Nor Microsoft's `VSCodeDebugProtocol` package, whose terms are not open source: `Passenger.Dap` is the protocol.
- **No Avalonia Accelerate components** (TreeDataGrid and the rest need a paid licence).
- **Every program Passenger starts goes through `ChildProcesses`** (`Command` or `DotnetCommand`, then `Start` or
  `RunAsync`; `Kill` ends it with all it started), so none outlives the IDE. Only the terminal's shell (Porta.Pty's,
  ended by its pseudo-terminal's hangup), vstest.console (VSTest's, which watches the IDE) and, on Windows, Velopack's
  Update.exe (started as Passenger closes with an update ready, since it must outlive Passenger to replace it) start
  elsewhere. macOS's update runs nothing after Passenger: it swaps the app in as the process ends and starts the new
  one through LaunchServices (`open`, through ChildProcesses). A program a run configuration runs in the terminal is
  started by Porta.Pty and then `ChildProcesses.Adopt`ed: its process group goes on Lifeline's watchdog (macOS, Linux),
  and Porta.Pty's job ends it on Windows.
- **Every dependency, icon or asset** has an open-source licence and an entry in `THIRD_PARTY_NOTICES.md`, added in the
  same change that brings it in: a package by its NuGet id in backticks, with its licence and copyright as the package
  states them (`just notices`, and CI, fail on one missing), copied source by its file with the project it came from.
  The one exception, decided by the owner: `Microsoft.VisualStudio.Telemetry` and
  `Microsoft.VisualStudio.Utilities.Internal`, under Visual Studio's terms, stay inside Roslyn's server (MIT on nuget.org)
  because its built-in Razor support needs them; THIRD_PARTY_NOTICES.md says so. Nothing else under such terms ships.
  The second, also the owner's: the TextMate bundle licence ("Permission to copy, use, modify, sell and distribute this
  software is granted", no SPDX identifier), which the HTML grammar carries and the XML, Markdown, CSS and JavaScript
  grammars name as their origin, as VS Code ships them. A new SPDX licence needs its text in
  `tools/Passenger.Notices/Licenses`, a package inside Roslyn's server a row in THIRD_PARTY_NOTICES.md's roslyn/ table,
  and copied source or an asset its entry after that file's marker line, so the app carries them.
- **Colours are tokens.** A control names a brush from `Theme/Tokens.axaml` (`Passenger.*`), never a literal colour,
  and every token exists in both the Dark and Light dictionaries.
- **A malformed flag or value is fatal**, never a silent default (`LaunchOptions.cs`).
- **Work off the UI thread that changes what is drawn goes through `BackgroundWork`**, so a
  screenshot waits for it instead of capturing a half-finished frame: `BackgroundWork.Run` rather than a bare
  `Task.Run` (one that must not hold screenshots says why in a comment), and `Debounce` rather than a
  `DispatcherTimer` for a pause before such work, since a headless timer never fires while a screenshot settles.
- **One threading rule.** Controls, the documents editors show, the window's `ProjectSession` and its parts,
  controllers and views belong to the UI thread
  (controllers call `UiThread.Check()` in their entry points, which fails at once in a Debug build). The projects
  without UI (Text, Lsp, Dap, Debugger, Profiler) never reference Avalonia, and their events and callbacks come on
  any thread; Editor, Languages and Terminal raise theirs on the UI thread. The App crosses from another thread only through `UiThread`: `Post`, or `Marshal` for a handler given to
  a library, holds `BackgroundWork` and reports an exception to `IdeErrors` rather than ending the IDE; `InvokeAsync`
  is for a caller that waits for the answer. An `await` on the UI thread comes back to it; `Dispatcher.UIThread.Post`
  only defers work already there.
- **Paths are canonical** (`PathNames.Canonical`, symlinks resolved), because MSBuild and Roslyn
  report resolved paths; a path compared with another must come from the same function.
- **Shortcuts follow Rider's.** A new action takes the binding Rider's Visual Studio (Windows) or
  Visual Studio OSX (macOS) keymap gives it; Rider's keymap files are in its jars for reference.
- Commit messages go through a file (`git commit -F msg.txt`): backticks in a `-m` string are zsh command
  substitution.

## Tests

Tests drive the IDE whole, headless (`tests/Passenger.Tests`, xunit v3 on Avalonia's headless
platform with real Skia text): a window or an editor, real key presses by Rider's macOS keymap
(`EditorRig`), typing, clicks, files on disk. There are no unit tests of one function or type on its
own; a test says what a person would see or get ("an edit saved keeps the file's line endings",
"typing never loses a word's colour"), and its failure message names the consequence. The long
random editing session checks every key against a plain string model, which is what keeps the rope,
the line index and undo honest; keep it passing rather than narrowing it; it also runs with soft wrap on, moves checked against what a move among rows
must do and rows against a fresh editor's. One test of something a person sees or
gets is worth more than many that restate the code they call: a test that only checks one type or function on its own
is deleted, and what it covered tested through the window instead. `BannedSymbols.txt` makes that a build error
(RS0030) for the internals tests used to drive directly (the terminal's emulator, a Roslyn or profiling session started
by hand, a lone `TextDocument`, `LineDiff`, `CommitGraph`, `Snippet.Parse`); the editor rig in `Rig.cs` is the one
exception.

A run keeps to a folder of its own (`TestRun`, set up by a module initializer before anything starts): the IDE's state
(`PASSENGER_SETTINGS`: its log, the language servers' logs, agents' sockets, profiler snapshots), every test's
folders (`TestFolders.Create`) and the shells' history, with an empty Git configuration in place of the person's
(`GIT_CONFIG_GLOBAL`, `GIT_CONFIG_NOSYSTEM`). Nothing changes those variables afterwards; a test that needs fresh
state gives the window a folder of its own (`MainWindow.StateFolder`). So a run never touches the person's settings,
log, history or Git signing and hooks. No test reaches GitHub: the run points `PASSENGER_RELEASES` at a dead loopback
address, and a test that needs releases gives its window a `Releases` feed of its own (`UpdatesTests`); one that tests
an update gives its window the updater of a copy installed in a test folder, and no other window has one. After every window test, pass or fail, the windows it left open are closed,
what they started is waited out and its folders are removed (`TestsLeaveNothingBehind`), so one failure never
cascades into the next test's waits. A plain `[Fact]` that runs window code through `HeadlessUnitTestSession.Dispatch`
must not end on the session's thread (the next window test would wait on it for good): it hands a failure back as the
run's answer and `await Task.Yield()`s before throwing. After a resume, a debugging test waits for the session to leave
the stop before waiting for Ended or Paused. A Mac that sleeps during a run fails tests with "Background work never finished"
(Settle's limits are wall-clock); `pmset -g log` shows the sleeps, and the lead's full runs go under `caffeinate -is`.

What runs beside what is chosen (`Collections.cs`): window tests run one at a time on Avalonia's session thread and, as
the assembly's one collection, class after class; beside them run only the debug adapter's protocol tests
(`DebugSession.Collection`). A class with a test that holds what it tests to a time (a key's, a frame's, how soon the
window answers, a profile's samples, the debugger's evaluation limit), or that debugs through the window, goes in
`Alone`, which runs after the rest with nothing beside it. A class whose tests share one project derives its fixture
from `SharedProject`: its windows share one warm language server (`MainWindow.WarmServer`), the project is put back as
written before each test, and a file a test adds goes through `project.Write`. A class whose tests each restore a fresh
folder takes it from `RestoredFolders`. CI splits each system's suite into shards, a runner each (#206), so a class
never counts on another having run before it: each shard lists the suite and `tools/test-shards.py` deals the classes
by their seconds on that system in `tests/Passenger.Tests/TestTimes.json` (one it lacks by its budget), the same deal in
every shard, checked before any test runs to put every test in exactly one; inside a shard the collections keep their
order. A shard writes its classes' times into its summary (`tools/test-times.py` over the .trx, budgets in
`tests/Passenger.Tests/TestBudget.json`): a class over its seconds warns, and a shard over its 20 minutes fails (the
whole run was an hour in one job a system). Each system's summary job fails unless every shard succeeded and every test
has exactly one result, and warns when its run's own times would deal a slowest shard two minutes shorter: `just
test-times RUN` writes them to TestTimes.json, for committing. Raise a budget, or add a shard, knowing why. The run
sets its own nice value to 0 when it starts below it (GitHub's macOS
runner starts at −10), since MSBuild keeps no idle node at any other; a test that needs one skips, saying why, when
the run is above 0 and cannot come down (`Nice`). MSBuild itself sometimes keeps no node (a race as a build ends, or
more than 1.5 nodes a processor running machine-wide): a test that needs one waits for the count
(`Processes.MSBuildNodesCounted`), builds again, or skips saying why.

The debugger's tests (`tests/Passenger.Tests/Debugging`) run the adapter from the tests' own `debugger/` folder, as
the IDE does, and launch or attach to the programs in `tests/Debuggees`, whose lines and columns (tabs included) they
stop at: moving a line there means moving the tests that name it. A line breakpoint on a line with a lambda binds inside
the lambda, so a test stopping at such a statement gives its column. `SelfDebuggingTests` opens this repository and
debugs Passenger.App; it finds its breakpoint by the code `status.Text = why;` in MainWindow.Actions.cs, so changing
that line means changing the test. `TerminalConsoleApp` there needs a real terminal;
window tests copy it into a folder of their own (`Debuggees.CopyTo`). The protocol tests run one at a time (`DebugSession.Collection`), beside the window tests; one that holds the engine
to a time runs in `Alone`. `PASSENGER_ENGINE_LOG=FILE` keeps the engine's own log, for the
tests' sessions and the IDE's alike. `LiveTreeTests` debug `tests/Debuggees/LiveTreeApp`, headless, with no project file
of its own (the test writes one on the tests' own Avalonia version); "click X,Y" typed into its Debug console stands in
for the pointer. `BindingErrorsTests` run and debug `tests/Debuggees/BindingErrorsApp` (written the same way), whose
caption binds a misspelt Name; "rebind" typed into its console makes it fail again. `XamlBreakpointsTests` debug
`tests/Debuggees/XamlBreakpointsApp` the same way ("paint", "warn" and "rename" typed into its console change Save's
background and width, the buttons' style class, and the caption's binding). `XamlEditingTests` drive each XAML editing
tool in an Avalonia app and a WPF app of their own (`AvaloniaGallery`, `WpfGallery`), the MVVM Toolkit stood in by its
attributes' full names. `ProjectFileHelpTests` drive the MSBuild code help on an SDK-style project and a
Directory.Packages.props with `StandInFeed`. No test sees the system's window frame: `TitleBarTests` checks the roles
Windows is asked about through Avalonia's internal `IInputRoot.HitTestChromeElement` and counts moves by
`MovesStarted`; a real Windows window is checked by hand in the VM (`~/Documents/passenger-vm/handcheck/bar205`). A
screenshot's `--type` sends its text as one input, so completion opens only when its trigger is typed alone
(`--type "<" --type "Tar"`). A test of what reaches the language server fails if the server ended during it
(`CSharpService.Stopped`), since a server started again is sent the whole file, which hides a drift. `TestFramesTests`
(fixture `HeadlessTestsFolder`) run an xunit v3 headless project on the platform and one on VSTest
(`IsTestingPlatformApplication=false`), restored through `AvaloniaApp.UseFeed`. `WebLanguagesTests` need Node.js on
the PATH and skip, saying why, without it (the TypeScript ones also on a Node.js older than typescript-language-server's
`engines.node`). `EslintPrettierTests` copy the tests' own eslint and prettier (`tests/Passenger.Tests/NodePackages`,
put in `node-packages/` at build by `tools/Passenger.NodePackages`) into their project; the run's npm is offline with
its own cache and config (`TestRun`), so Run 'npm install' fails fast without the registry. The Windows VM has no
Node.js, so the web languages' tests skip there; CI's Windows runner has it. `BindingErrorsTests` also debug BindingErrorsApp with XAML Binding Errors on. Headless, a window
never loses activation, so a menu stays open under a dialog, and when the dialog closes the window puts focus back on
what it remembers, never a menu (a focus scope of its own): `MainWindow.KeepKeys` gives an open menu the keys back.
Read any log the IDE or a server may be writing with sharing (`Ui.Shared`), and a `WaitFor` condition returns false,
never throws, while what it waits for isn't there yet. A test that opens a dialog holds it to `DialogButtonsTests.Check`
(centred words, one height and radius, Rider's order). `HotReloadTests` and `HotReloadEditsTests` (Alone) edit debugged
programs (`KeyTextInput` drops `\n`, so multi-line edits go through `Typing.Replace`); `WpfLiveTreeTests` (Alone,
Windows only) debug `tests/Debuggees/WpfTreeApp` in a real window, and `McpTests.WpfApp.cs` drives it with `--mcp`. `WindowsOnlyProjectsTests` open a dotnet new wpf and winforms app
(`WpfApp.Create(asMade: true)`, `WpfApp.CreateWindowsForms`) never restored: Passenger restores and builds them itself.
`DecompiledSymbolsTests` (Alone) give each adapter a cache folder of their own; PassengerCases' `corelib` case stops in
a callback from List.ForEach, CoreLib's frame below it. `BindingValuesTests` debug `tests/Debuggees/BindingValuesApp`
("rename" typed into its console renames the customer on the UI thread). `AvaloniaFramesTests` (Alone) profile,
debug and run `tests/Debuggees/FramesApp` (its SlowPanel spins 30 ms a measure). `WpfPreviewTests` (Windows only)
preview WpfXamlTests' gallery; the VM's hand check is `~/Documents/passenger-vm/handcheck/wpfprev200/preview.cmd`.
`XamlTests`' `AvaloniaApp` makes `dotnet new avalonia.app` from templates installed into the run's own hive
(`--debug:custom-hive`) and takes Accelerate's DevTools out of it.
The run's IDE keeps dotnet new templates in its own home (DOTNET_CLI_HOME) on macOS and Linux and in
`ProjectTemplates.Hive` (a hive of the run's) on Windows, apart from `AvaloniaApp`'s. `AvaloniaApp.Feed` is the run's
local feed (Avalonia.Templates at `AvaloniaTemplatesVersion`, downloaded at restore by a `PackageDownload` and copied
from the package cache), so installs never reach nuget.org; `UseFeed(folder)` points a folder's nuget.config at it
alone. `NewSolutionTests` installs Avalonia's templates through New Solution and runs the app it made headless.
`WpfXamlTests`' app (`WpfApp`, `dotnet new wpf` at the test project's own target) restores from that feed alone:
Microsoft.WindowsDesktop.App.Ref is a `PackageDownload` at Directory.Packages.props' `WindowsDesktopRefVersion`, which
the tests check against the version the SDK's KnownFrameworkReference gives the target, failing with the version to
pin when an SDK changes it; off Windows the app sets EnableWindowsTargeting and MarkupCompile builds it there too.

The MCP server's tests (`McpTests`) are agents using the SDK's own client: one starts `Passenger --mcp` and debugs a
project through it, others attach to a window with `--mcp-attach` (its sockets under the run's
`PASSENGER_SETTINGS`) and change what the window shows, once it lets them in and the person answers its questions.
An agent that gives up on a call sends the protocol's "cancelled" notification itself (`CallGivingUp`): the C# SDK's
client can drop its own when the call's token is cancelled. `McpTests.AvaloniaApp.cs` drive `tests/Debuggees/AgentApp`
(headless, its popups windows of their own; `AgentAppFolder` writes and restores it once per class from
`AvaloniaApp.HeadlessProject()`, which LiveTreeApp and BindingErrorsApp also use). The Windows on Arm VM runs a test
filter on a commit with `~/Documents/passenger-vm/vm-test.sh NAME FILTER` (66–120 min for the whole suite, by the Mac's
load); the VM can't see dot folders, and a batch file in the share runs as `powershell -Command "& cmd.exe /c '…'"`.
The Ubuntu VM ("Ubuntu 26.04 ARM64", a real GNOME desktop CI lacks) runs one with
`~/Documents/passenger-vm/vm-test-linux.sh NAME FILTER`, headless as CI's are, from `~/src/passenger-NAME`, its .NET in
`~/.dotnet` and Node.js in `~/.local/node` on the login path; a script in the share runs as `bash -l
/media/psf/Home/…`. `TemplateFamiliesTests` installs and uninstalls a test template of its own (`probetest`, from a
folder) in the run's template home while a dialog is open; the watch sees it.

## Code style

- C#: four spaces, file-scoped namespaces, braces on every block, explicit types over `var`, accessibility always
  written. `.editorconfig` enforces most of it in the build.
- Comments say why, or what else depends on this, never what the next lines do. A type's or member's `///` summary
  is at most five lines; an in-body comment is one line.
- XAML: four spaces; one style per selector in `Theme/Shell.axaml`, restyling Fluent's states on its
  `PART_ContentPresenter`, where Fluent paints them. Shell.axaml starts with base styles every Button, TextBox,
  ComboBox, NumericUpDown, CheckBox and MenuItem takes (content centred, `Passenger.ControlHeight` 28, the radius
  resources `ControlRadius` 4, `ToolRadius` 6, `PopupRadius` 8); a class style comes after them and says only how it
  differs. A visual inconsistency is fixed once in a shared place (a token, a base style or resource, or a helper such
  as `DialogFooter.Of`), never per control, following Rider where the design is in question; a control that can't use
  the shared piece says why in one line. A dialog's buttons come from `DialogFooter.Of(default, cancel, more…)` (Rider's
  order per platform) in a `Border.footerBar`; a list or table in a page is `Border.framed`; a rule inside a page is
  `Passenger.Separator` (`Passenger.Divider` is for edges between panels). PopupHost gives each popup room for its
  frame's shadow.
