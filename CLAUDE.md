# CLAUDE.md

Guidance for Claude Code in this repository: the rules, the commands, where things are and the traps the code doesn't
show. For how something works, read the code; Passenger's own MCP tools (below) are the quickest way. Every session loads
this file, so keep it short: a change adds a rule, a trap or a pointer where one is missing, in a line, never a
description of what the code does.

## What this is

**Passenger** is an IDE for C# and modern .NET (CoreCLR only: no .NET Framework, Mono or Unity), written in C# on .NET 11
with an Avalonia 12 UI, for Windows, macOS and Linux. v0.2.0 shipped; the work now is bug fixes and code cleanup only, no
new features, on main toward 0.2.1 (https://github.com/Zaneris/passenger/milestone/4), features waiting in the Backlog
(https://github.com/Zaneris/passenger/milestone/2). Each issue says **Now:**, **The aim:** and **Done when:**;
read it before anything larger than a fix, and close or tick it when it lands. A new feature is the best Passenger can
make it, never a copy of another IDE's; only its keys follow Rider's keymaps (their files are in the jars of
`~/Applications/Rider.app`).

## Commands

```sh
just build                 # Debug build of Passenger.slnx; warnings are errors
just run [PATH] [args]     # the IDE on a folder or file (--file F, --caret L:C, --theme dark|light)
just screenshot X.png ...  # the main window drawn off-screen to a PNG, after the steps given
just test [filter] [N]     # tests whose name matches, in N processes, then the Alone classes (tools/test-run.py);
                           # results and console logs in artifacts/test-results/local; --plan shows the deal
just test-times RUN...     # rewrite tests/Passenger.Tests/TestTimes.json from CI runs (it deals CI's shards)
just publish RID           # self-contained Release build: osx-arm64, win-x64, win-arm64, linux-x64, linux-arm64
just package-mac [RID]     # the .dmg (Apple Silicon), signed by PASSENGER_SIGN_IDENTITY and notarized by
                           # PASSENGER_NOTARY_PROFILE when set, else ad hoc (--dry-run needs an identity)
just package-win [RID]     # Velopack's per-user installer; just package-linux [RID]: the .tar.gz with install.sh
just container-tools [RID] [--archive]  # the in-container tools (linux[-musl]-x64/arm64) into artifacts/container-tools
just notices               # fail naming any carried package the notices miss
just mcp [FOLDER]          # Passenger as an MCP server over a folder, from a clone of the last build
just mcp-call FOLDER TOOL [JSON]  # one MCP tool over a folder (a worktree) from the shell, its server kept
just site-shots [SCENE...] # site/images/<scene>-<dark|light>.webp retaken (macOS, cwebp)
just icons | file-icons | themes | unicode-widths | aspire-protocol | app-icon   # regenerate checked-in files
```

The app icons, generated themes, icon sets, terminal widths and Aspire's protocol code come only from their recipes.

**Use Passenger on itself.** `.mcp.json` starts Passenger's MCP server over this repository (`tools/mcp.sh`, an APFS
clone of the last Debug build: reconnect to pick up a new one). Ask Roslyn with `diagnostics`, `find_symbol`, `outline`
and `usages`, debug the IDE's projects, and look at the window with `app_screenshot` (`app` ide). Lines and columns count
from 1. Anything it gets wrong is a Passenger bug.

**Look at UI changes** with a screenshot at `--scale 2` before calling them done. It runs on the headless platform (real
Skia, never a window or the focus), opens what the command line names, takes its steps through real input, waits for
background work and captures the compositor's frame:

```sh
artifacts/bin/Passenger.App/debug/Passenger . --file src/Passenger.App/App.axaml.cs --caret 12:5 \
    --key enter --type "var x = 1;" --key "shift+meta+left" --click 400,300 \
    --screenshot out.png --size 1400x880 --scale 2 --crop 320,76,900,500 --theme light
```

Steps: `--key` ("cmd+shift+L" or Rider's "meta shift L"), `--type` (one input, so completion opens only on a trigger
typed alone), `--click`, `--right-click`, `--hover X,Y` (logical units), `--click-name NAME` (exit 2 if none),
`--drag[-hold] X1,Y1,X2,Y2`, `--wait MS`, `--window-buttons` (macOS's traffic lights, drawn by Passenger), `--agent
COMMAND` (an agent in the Terminal's Agent tab, joined by `$PASSENGER --mcp-attach $PASSENGER_PID`, every tool allowed).
A step after one that opens a dialog goes to the dialog. A screenshot keeps no state unless PASSENGER_SETTINGS names a
folder (short on macOS: a socket path holds at most 103 bytes). Headless, dispatcher timers fire only inside a running
frame (`Screenshot.Pump`, `Ui.WaitFor`); `Ui.Settle` fires those due, so the caret's 530 ms blink redraws during a long
settle.

**Build facts.** Everything builds under `artifacts/` (`UseArtifactsOutput`). Package versions live only in
`Directory.Packages.props` (Roslyn's server's as `RoslynServerVersion`, each kept pin saying why), shared properties only
in `Directory.Build.props` (the version too). Packages come from nuget.org alone, as it signed them, at what each project's
`packages.lock.json` pins: CI restores locked, so commit a rewritten lock file with its change. Every lock file holds all
eight `RuntimeIdentifiers` (osx-x64 and the linux-musl pair too), even a Windows-only project's, since a publish's RID
reaches every referenced project's restore. Every recipe and CI set `AVALONIA_TELEMETRY_OPTOUT=1`. `Directory.Build.targets`
gives every ProjectReference its full path (the SDK opens a solution reached through a symlink by the link and built
referenced projects twice).

## Rules

- **Never Microsoft's debugger (vsdbg) or anything of C# Dev Kit**, including its pieces inside VS Code's C# extension
  (`.debugger`, `.roslynDevKit`, `.testDiscovery`), nor `VSCodeDebugProtocol`; nothing is copied out of an installed
  extension. Roslyn's server comes from NuGet; `Passenger.Dap` is the protocol.
- **No Avalonia Accelerate** (TreeDataGrid, DevTools: `DeveloperTools.TakeOut` removes what `dotnet new avalonia.app` adds).
- **Every dependency, icon or asset has an open-source licence and an entry in `THIRD_PARTY_NOTICES.md`**, in the same
  change: a package by NuGet id with its licence and copyright as stated (`just notices` and CI fail otherwise), copied
  source by file and origin, assets after the marker line, a new SPDX licence's text in `tools/Passenger.Notices/Licenses`,
  a package inside Roslyn's server in its table. The owner's two exceptions: Visual Studio Telemetry and
  Utilities.Internal inside Roslyn's server (its Razor support needs them), and the TextMate bundle licence. MIT first.
- **Every program Passenger starts goes through `ChildProcesses`** (`Command`/`DotnetCommand`, then `Start`,
  `StartAsync` or `RunAsync`; `Kill` ends it with all it started), so none outlives the IDE (`Lifeline`: a job object on
  Windows, a watchdog over process groups elsewhere). Started elsewhere: the terminal's shell (Porta.Pty), vstest.console,
  Velopack's Update.exe, and the person's Passenger an agent's `--mcp-attach` starts (`StartApart`); a program run in the
  terminal is `Adopt`ed. Docker Desktop and the file manager are the person's (`open -a`, `open -R`, Explorer, through
  ChildProcesses).
- **No secret on a command line** (any process can read it): give it through the environment, or don't make it (the dev
  certificate goes to containers as PEM with no password). Command lines are logged and echoed through
  `ChildProcesses.Shown`, and secrets everywhere (Details, Inspect's copies, logs, consoles, hints, agents' answers) are
  masked by one rule, `ContainerSecrets` (`Secret`, `InValue`, `InSaid`, `CommandLine`).
- **Containers are ended by label**, since no child holds them: everything an engine makes for Passenger carries
  `ContainerLabels` (state, machine, process, folder). A folder's end removes what it started (only containers and
  networks carrying this process's labels; never `down` or `--remove-orphans`, which take the person's own), the
  watchdog's last command removes the process's own however it ends, and the next start removes those of a dead process
  of this state and machine. What an engine makes unlabelled (Aspire's DCP containers and session network) is recorded in
  `AspireContainerOwnership` and ended by the same rules. Persistent containers and the person's own are never removed.
- **Colours are tokens** (`Passenger.*` in `Theme/Tokens.axaml`), given in every theme (dark and light, `Harbour.axaml`
  and each look `just themes` writes); Fluent's keys are mapped once in `Theme/FluentKeys.axaml`. UiRulesTests fails on a
  colour or non-zero CornerRadius written outside `Theme/` (but the content's own) and on an unmapped Fluent key.
- **An identifier is named from its home** (`[IdentifierHome]`: action and tool window ids, DAP and LSP names, `McpTools`,
  each `PASSENGER_` variable's const, the theme's keys as `TokenKeys` and `IconKeys`, generated from their XAML): PSG0001
  (`src/Passenger.Analyzers`, every rule's id and words in `Rules.cs`) fails its text written anywhere else, a plain word
  only where a parameter or member marked `[IdentifierOf]` its home takes it.
- **A malformed flag or environment value is fatal**, never a silent default (`LaunchOptions.cs`).
- **A warning is fixed at its cause.** Turning it off (NoWarn, a pragma, a lowered severity, a suppression) or moving
  the line it checks (a raised budget or limit) does not count as fixing it; neither does an analyzer's baseline.
- **Work off the UI thread that changes what is drawn goes through `BackgroundWork`** (`Run`, and `Debounce` rather than
  a `DispatcherTimer`), so screenshots and tests wait for it; one that mustn't hold them says why in a comment.
- **Work nothing awaits goes to `IdeErrors.Observe`** (`Core.UnawaitedWork.Observe` below the App), never a bare `_ =`
  or a call left as a statement, so its fault is said as it happens: IdeErrorsTests reads the built IL (`DroppedTasks`).
- **One threading rule.** Controls, documents, the window's `ProjectSession` and its parts, controllers and views belong
  to the UI thread (`UiThread.Check()` in entry points). Text, Vcs, Workspace, Packages, Lsp, Dap, Debugger and Profiler
  never reference Avalonia and raise events on any thread; Editor, Languages and Terminal on the UI thread. The App
  crosses from another thread only through `UiThread` (`Post`, `Marshal` for a library's handler, `InvokeAsync` to wait
  for an answer).
- **Paths are canonical** (`PathNames.Canonical`, symlinks resolved, as MSBuild and Roslyn report them); compare with
  `PathNames.Comparer`/`Same`/`IsUnder`, walk a project's folders with `FoldersUnder` and the open folder's files with
  `FilesUnder` (`IsSystemFile` passed over). Show one to a person with `PathNames.Shown`.
- **Lines differ by reader.** Roslyn's server and compilers end a line at a lone '\r', U+0085, U+2028 and U+2029, an XML
  reader (XAML's compilers, BAML, XamlIl) at a lone '\r' only. Every place crossing to a server, compiler, PDB or running
  app goes through `ServerLines`/`ServerPlaces` (`Compiled`, `CountedByRoslyn`, `PositionOfCompiled`), never `new
  TextPosition(line, character)`; the debugger's lines through `Debugging/BuiltLines`. MCP's places are the editor's.
- **Writes go one way.** A workspace file: `FileText.Save` (atomic, through symlinks, permissions, Windows attributes,
  ACL and streams and macOS metadata kept, a read-only file refused, Local History given the old bytes). Any other whole
  file: `AtomicFile.Write`. Never `File.WriteAll*`. Passenger saves an open project, solution, nuget.config or
  Directory.Packages.props before writing it and loads what it wrote after (`SaveBeforeWriting`, `FollowWritten`). A
  change of several files, or one that moves a file, is one command of global undo (`EditorArea.Commands.Record`).
  Server edits pass `LineEndings.Fit` then `Indentation.Fit`.
- **State is a `StateDocument`** in the one `StateStore` (versioned `Upgrades`, `Clamped`, written debounced and merged,
  a bad file set aside as `.bad`), read off the UI thread; never a file of its own. Per-folder state lives under
  `workspaces/<name>-<hash>/` and is made in `ProjectSession`. A new setting: an `AppSettings` property, its row and
  range declared in `Declared` and `Add`ed in its place in `SettingsDialog`, a line in `MainWindow.Follow` or
  `SettingsStore.InForce`.
- **Every action is an `IdeAction`** in the window's `ActionRegistry` (ids and keymaps in `AppActions.cs`): keys, menus,
  Find Action, context menus and MCP's `ide_action` all run it, `WhyNot` greys it with why, titles come from the registry
  (`TitleWhere`), a title that asks more ends in "…", and what's missing is said in `Refusals`' words. Anything that
  takes a while holds a `Working` (`ActionWork.Begin`) from its first moment to its hand-over, saying each stage and
  honouring `Stopping`; toolbar buttons bind with `ActionButtons.Bind`.
- **One way out to the system:** a browser only through `MainWindow.Browse`, the file manager only through
  `ShowInFileManager` (both recorded for tests).
- **Shortcuts follow Rider's**: its Visual Studio (Windows) or Visual Studio OSX (macOS) keymap, and its VS Code keymaps
  for that preset, resolved as IntelliJ does (own list, then plugins' descriptors, then the parent); a keypad key Rider
  binds beside a main key is bound too. A key IntelliJ gives two actions by place goes to one, said where it's bound;
  the place takes it through `AppActions.KeysIn`.
- **Workflows run only by hand** (the owner's choice): never `gh workflow run` unless the owner says. Commit messages go
  through a file (`git commit -F msg.txt`): backticks in a `-m` string are zsh command substitution.

## Where things are

Projects in `src/`:
- `Passenger.Core`: what everything shares, no UI. `ChildProcesses` (`RunAsync`'s limit, a 2 s `OutputGrace` after exit,
  `mostOutput`; `StartAsync` forks off the UI thread, since a fork costs a large process tens of ms, #233),
  `Dotnet`/`Programs`/`ShellEnvironment` (tools found as the login shell would), `PathNames`, `AtomicFile`,
  `SocketFolders` (every Unix socket, short paths), `DotnetProcesses` (attachable programs), `InFlight`,
  `BackgroundWork`, `Product` (the one place the version is read), `PassengerLog` (passenger.log for code below the App,
  never `IdeLog`), `WebAddress`, `UnawaitedWork`.
- `Passenger.Text`: the rope (an immutable B-tree, so every version is a thread-safe snapshot), `TextDocument` (undo,
  LSP-ordered changes; carets and run lists carry offsets with `TextEditing.Map*` over the change's `SortedEdits` as it
  came), carets, every editing command as a pure function to an `EditPlan`, `MarkupIndent`, `WordDiff`, `FuzzyMatch`
  (allocates nothing), `FileText` (encoding, BOM, `.editorconfig`, line separator), `ChangeMark`, anchors
  (`TextDocument.AnchorAt`/`RangeAt`, moved before `Changed`; each handler isolated, its failure to
  `PassengerLog.Report`; `AnchoredList` for a list of them, read once a change): hold a place in the text with one,
  never your own `Map*`.
- `Passenger.Editor`: the `CodeEditor` control (partials), `Keymap` and `EditorActions` (presets), `IHighlighter`. With
  soft wrap, whatever turns an offset into a place goes through `Rows`/`RowSpan`. Wraps (`WrapMap`), rows (`RowMap`),
  folds (`FoldList`), hints (`HintList`) and built lines (`LineRuns`) are runs an edit moves only where it reaches, so a
  key costs what it changed, not the file's size: keep it so.
- `Passenger.Languages`: C# coloured by Roslyn's parser in-process (`CSharpHighlighter`, also each open file's one
  syntax service, `OpenDocument.Syntax`: use it, never a fresh parse), the rest by VS Code's TextMate grammars
  (`TextMateHighlighter`, scopes mapped in `ScopeColours`); `Highlighters.For` chooses. A new language: one `Language`
  entry (patterns, comments, grammar, folds, service), its grammar file and a notices entry; compare entries, never
  names. A finder never tells its answer inside a document's `Changed` (the open file moves its regions in a later
  handler).
- `Passenger.Xaml`: Passenger's own XAML core (no Avalonia; WPF shares it): tolerant syntax, a TypeSystem over the
  project's assemblies and its C#, the Avalonia and WPF dialects (`XamlProjectBuilder.DialectOf`), completion,
  diagnostics, navigation, `XamlFormatter`, refactorings, `CreateFromUsage`. Not Avalonia.Ide.CompletionEngine.
- `Passenger.Lsp`: the LSP client (StreamJsonRpc, whose types never leave the project; a server that fails is
  `LanguageServerFailed`), Roslyn's server downloaded per platform (`RoslynServer.targets`, four VS assemblies left out),
  `LspDocument` (requests in editor offsets; answers that grow with the solution decoded off the UI thread; never a
  change between a "\r\n"'s halves), `RazorHtml`, `LanguageSession` for other servers, and `WebServers.targets` (the web
  servers under `web/`, pinned in `WebServers/package.json` and its lock; to move a pin: edit it, `npm install
  --package-lock-only --ignore-scripts`, update the notices' "Inside web/" table).
- `Passenger.Roslyn.Extension`: Passenger's handlers inside Roslyn's server (`--extension`), compiled against its
  publicized assemblies (`src/Publicize.targets`): the host's project loading (`ProjectLoader`), Hot Reload, refactoring
  descriptions, Aspire's code help, routes, configuration reads. A handler reads the solution, never changes it.
- `Passenger.Host`: MSBuild in a process of its own per SDK (`ProjectHosts`), over JSON lines: `evaluate`, `build`
  (design-time, as Roslyn's build host), `inspect` (MSBuild files' code help), `properties`, `inputs` (what a build
  reads, stamped, for `UpToDate`), `hold` (no generating design-time build while Passenger builds: Windows lets one
  writer have a file), `cancel`. Results are stamped with what they read, so a reopen redoes only what changed; a
  Windows-only framework loads with EnableWindowsTargeting off Windows.
- `Passenger.Vcs`, `Passenger.Workspace`, `Passenger.Packages`: the App's Git, folder model and NuGet code that
  needs no UI, referencing no Avalonia; what belongs to the UI thread stays in the App's folder of the same name.
- `Passenger.Dap`, `Passenger.Debugger` (+`.Interop`), `Passenger.DebugAdapter`: Passenger's own debugger (begun from
  SharpDbg and ICorDebugSharp, MIT) behind its own DAP, in a process of its own under `debugger/`.
  `Passenger.ContainerTools` is `passenger-tools`, what runs inside a Linux container (`dap`, `processes`, `trace`,
  `heap`, `sources`, `version`): self-contained, carried only in the release's archives.
- `Passenger.Profiler`: EventPipe sampling (`ProfilingSession`, `Snapshot`), heap snapshots with ClrMD (`MemorySnapshot`,
  `HeapGraph`, `HeapHistory`; ClrMD's Azure packages stay carried, and its DAC stays loaded once used: unloading it crashed
  on Linux), live requests (`LiveRequests`), and recording inside a container (`InContainer`, `HeapWire`, nothing in a
  frame trusted).
- `Passenger.Inspector` (+`.Wpf`): the Live Visual Tree's helper, loaded into a run or debugged app by a startup hook,
  answering over a Unix socket (`Protocol.cs` compiled into both ends). `Passenger.WpfPreview`: WPF's previewer (Windows
  only, design mode). `Passenger.TestFrames`: pictures of a failed headless test. `Passenger.Terminal`: the VT emulator
  and Porta.Pty sessions.
- `tools/`: `Passenger.Notices` (the notices files), `Passenger.NodePackages` (the web servers without Node.js), the test
  runner and shard scripts, packaging scripts (`tools/linux`, the disk image's layout), site shots.

`src/Passenger.App`, by area:
- **Shell.** `PassengerApp` (the process root: every open goes through `OpenProject`), `MainWindow` and its partials,
  `ProjectSession` (one folder's services made and ended together; a handler acts only while its session is bound,
  `Bind`), `StatusBar` (`StatusPlace` makes its words a link),
  `PopupHost`, `IdeKeys` (the one keymap in every window but a dialog; a part with keys of its own is an `IOwnKeys`).
  The toolbar is the main window's title bar: anything new on it goes inside `ToolbarStart` or `ToolbarEnd`, or Windows
  takes a press on it for the caption's. Logs are `logs/` under the state root (`IdeLog`: passenger.log, roslyn/,
  lsp.log, dap.log, vstest.log, mcp.log; Settings › Diagnostics turns on protocol traces).
- **Editors.** `EditorArea` (splits of `EditorGroup`s; editor windows are `EditorFrame`s of the same area, so a file in
  two windows is one `OpenDocument`; wire every editor through `ForEachEditor`; `CloseTab` is the one close), and
  `OpenDocument` (encoding, BOM and line separator kept; images, fonts, workbooks and release notes are previews that hold
  no text, `HoldsText` false). A Markdown file's preview is its group's `MarkdownPreview`, drawn by What's New's `Markdown`.
- **Docking.** `DockHost` and `ToolWindowIds` (a kept layout records windows by them, so a value never changes; add a
  window with one `Dock.Add`). A window builds its view when first shown; what the IDE writes while it's hidden lives in a
  model (`ProblemsModel`, `DebugModel`, `AspireModel`, `RequestLog`, `MigrationsController`), and code reaches a view
  only through a property that builds it for a person's action.
- **C#.** `CSharpService`, `CSharpDocument`, `ServerDiagnostics` (one pull at a time after a pause), `CodeAssist` (the
  window's wiring) over `CodeCompletion` (narrowed off the UI thread, rows kept), `QuickInfo`, `ParameterInfo`,
  `CodeNavigation`, `CodeActions` (Passenger's own `EditorFixes` first) and `PopupPlacement`; each language's answers a
  `LanguageAssist`, asked in `LanguageAssists`' one order; `WorkspaceEdits` (a server's edit to a file not open is
  refused if it was written in the last 2 s; an agent's call waits instead, `Patience`), `TypeRename`, `LiveTemplates`.
- **Web languages.** `WebLanguages` (one Node.js server per kind: TypeScript, HTML, CSS, JSON, Dockerfile, YAML; ESLint
  and Prettier per folder; every per-kind switch names each kind), `JsonSchemas` (carried schemas; downloads only while
  Settings allows), compose help (`ComposeDeclarations`, `ComposeNames`, `ComposeRename`, `ComposeText`). The servers are
  carried under `web/`, in a macOS bundle under `Contents/Resources/web` (`WebLanguages.Carried`).
- **XAML and MSBuild files.** `XamlService`/`XamlDocument`, the previewers (`AvaloniaPreviewHost`, `WpfPreviewHost`),
  `LibrarySources`, `SourceLink`; `MSBuildService`/`MSBuildDocument` (evaluated on the typed text).
- **Build and run.** `BuildRunController` (partials), `ProcessRunner`, `ConsoleSink` (every console, through one
  `PumpAsync`), `BuildOutput`, `RunConfiguration` (data: the folder's own, or generated from launch profiles, compose
  files and Dockerfiles and edited in place as `ConfigurationEdit`s), `LaunchPlan` (one resolution for Run, Debug,
  Profile, Hot Reload and a debugged test host; a Passenger it launches keeps its state in the launcher's `sandbox/`),
  `UpToDate` (a launch's build skipped while what its last build read and wrote stands; anything it can't account for
  builds), `TogetherBuild` (a start's projects side by side in one dotnet build), Publish (`PublishProfiles`, `PubXml`,
  a profile's `.pubxml` as the SDK reads it).
- **Runs of services.** `ServiceRun` is the one model of a run of several .NET services; `ContainerRun` and
  `AspireAppRun` are its kinds. Debug, Attach, Run without Debugger, Restart and Profile of a service are written once
  (`BuildRunController.Services.cs`); a kind supplies only its restarts, holds, program places and refusals. One start at
  a time (`TakeStartAsync`); Stop ends every session and recording before the programs go (`EndSessionsAsync`).
- **Containers.** Through the person's `docker` CLI and context only, never the Engine API or a Docker package
  (`ContainerEngine`, `ContainerSnapshot`, `ContainerCommands`; a service's name always after `--`). Compose's own model
  is the plan (`ComposeModel`, `ContainerPlans`); fast mode by default (the host's build mounted into the image's runtime
  stage); Passenger's override written to the state; services held at their start for Debug, Hot Reload and Profile;
  the tools downloaded from the release and SHA256-checked (`ContainerTools`). `EngineChecks` runs BuildKit's and
  Compose's checks on open files. Podman works both ways it's set up (`EngineState.IsPodman`).
- **Aspire.** `AspireController` (Aspire's IDE execution protocol on loopback, bearer tokens checked first),
  `AspireInteractions` (the resource service's gRPC; the protocol's C# is checked in), `AspireTelemetry`, `AspireClient`
  (the CLI, for an AppHost Passenger didn't start), `AspireOrchestration`. A run's logs and traces: `Telemetry/`.
- **Web apps' data.** Routes from code (`WebRoutes`, `RouteCatalog`), the Requests window (`RequestLog`, only while
  someone looks), Migrations (`MigrationsController`, `EfGaps`, `EfProviders`, `EfRun`; connection strings by environment
  only), configuration (`ConfigurationFiles`, `ConfigurationKey`), Endpoints (`HttpFile`, `EndpointClient`: nothing goes
  over the network but on Test or Send, and a send taking the person's environment asks first).
- **Localization.** The extension's `LocalizedReads` (`Localization.cs`) reads a C# or Razor file's localizer keys and
  generated-resource properties (Razor through its generated C#'s `#line`), `Passenger.Xaml`'s `XamlLocalization` XAML's
  `{x:Static}`; `ResourceFiles` is the one reader of `.resx` and `xlf/*.xlf`, and the card, warning and fix are shared.
- **Debugging.** `DebugController` (a `DebugTab` per session, several at once; `Session` is the chosen tab's),
  `DebugSession`, `BreakpointStore`, `VariablesTree`/`VariablesList` (Variables and the hover card are one list), Hot
  Reload (`Debugging/HotReload.cs`), Attach to Process (`MainWindow.Attach.cs`).
- **Profiling and the live app.** `ProfilerCommands`, `ProfilerView`, `FramesView`, `MemoryView`; `LiveApp`,
  `LiveTreeController`, `BindingErrors`, `BindingValuesController`.
- **Tests window.** `TestFinder` (tests from code before any build), `TestRunners` (a Testing Platform app to
  `TestingPlatformRunner`, the rest to `VsTestRunner`, the only file that knows VSTest's object model), `TestTree`.
- **The rest,** each folder named for its job: `Vcs` (`VcsController`; `Git` over porcelain v2 and `ThreeWayMerge`
  in Passenger.Vcs), `Packages` (`NuGetView`, `FolderPackages`, every package change's one path; feeds, `FeedSet` and
  nuget.config in Passenger.Packages, credentials only to their feed's host), `History` (Local History), `Navigation`,
  `Workspace` (the UI's part; `SolutionModel`, `ProjectHosts` and `SolutionEdits`/`ProjectEdits`, which change files as
  text, never `dotnet sln`/`add reference`, in Passenger.Workspace), `Welcome`, `Templates` (every `dotnet new`
  through `ProjectTemplates.New`/`NewCommand`), `Updates` (`Releases`, `Updater`; only an installed copy updates itself;
  What's New from `docs/release-notes/X.Y.Z.md`), `Settings`, `State`, `Discord`.
- **MCP.** `PassengerTools` (every tool, run on the UI thread, answering with what its operation returned), `McpHost`
  (`--mcp FOLDER`, headless; a window's socket; `--mcp-attach` through `McpProxy`), `AgentConnection` (admission and
  per-tool questions).

Releases and the site: `ci.yml` and `release.yml` (from a `vX.Y.Z` tag; its body is `docs/release-notes/X.Y.Z.md`, which
fails a publish while it is the stand-in) call `test.yml`, every system's suite in four shards. `site/` is
passenger-ide.net, plain HTML and CSS, deployed by hand (`gh workflow run Site --ref main`, the owner's); its share card
is named by its content, so a changed one takes a new name. Each page (home, `/containers`, `/agents`, `/linux`) has its
own title, description, canonical and Open Graph tags and a `sitemap.xml` entry, and loads only the site's files (CSP
'self'); `just site-shots themes` makes every `--look`'s capture a frame of one animated WebP (img2webp), and the
aspire and requests scenes run `tools/site-shots/services`, an AppHost that needs no container engine.

## Traps

- **Roslyn's server** stays at the latest *stable* build, 5.11.0 (owner's rule): later builds start Visual Studio
  Telemetry's session even with telemetry off (dotnet/roslyn#86122, fixed by #86130 in no package yet; #234). After
  lowering it, delete `artifacts/bin/*/debug/roslyn*`, or the copy leaves a mixed server that won't start.
- **Roslyn's server makes a C# file opened before its project loads a Miscellaneous File** and restores a project of its
  own for it, so `CSharpService` opens a loading project's files only once the server says that project loaded.
- **Roslyn's server writes its MEF caches beside its DLLs** (`roslyn/cache/`, no option for elsewhere), so where that
  folder is in a macOS bundle (a file added breaks the seal: "damaged", #450) or read-only, it runs from a copy in the
  state's `cache/roslyn/` (`RoslynServerFolder`); nothing a Passenger runs may write inside the app.
- **The debugger's engine.** `EngineLock` is not re-entrant. A read of the stop waits behind continue, steps, pause and
  disconnect. Never leave `_stepper` set but inactive (async stepping reads it as a queued StepComplete). An evaluation
  past its time is aborted only where the abort takes (never rude: one left pending fires later inside a lock, #238).
  Before letting a program go the engine runs it off patch and step stops (CET would fault it) and waits out abandoned
  evaluations. A decompile never runs under the lock. After a Hot Reload update, read a method's points, locals and IL
  only at the frame's version (`Points`, `DebugOf`, `MethodIL`).
- **Hot Reload** drives the server's `EditAndContinueService` from the extension; a session takes edits only for the
  projects of modules its program has loaded (others frozen), and `StaleCallers` blocks an update that leaves loaded code
  calling a deleted body.
- **The inspector** never touches `Dispatcher.UIThread` before the app makes it, never waits on Passenger, and readies its
  wire's types before the app's Main (System.Text.Json's lock against a thread the debugger stopped).
- **MCP's tool list** is paid for by every agent session (45.6 KB, held under 62 KB by McpDebuggingTests): say each thing
  once (shared parameters in `McpHost.Instructions`), and add no tool another covers. `save_all` stays a tool of its own:
  never asked about on a person's window (#278). `debug_evaluate`, `debug_input` and `breakpoint_set` are `Destructive`
  and ask the person the first time; every question closes unanswered after 2 minutes; calls send progress every 10 s
  (OpenCode gives up after 60 s of silence).
- **Agents never open windows on the person's screen**: `--mcp FOLDER`, or `--mcp-attach` with
  `PASSENGER_START=headless` and a PASSENGER_SETTINGS of its own; never a bare `--mcp-attach`, never `just run`.
- **Aspire's CLI and AppHosts** keep running AppHosts in `$HOME/.aspire/cli/bch` whatever ASPIRE_HOME says; on Windows in
  the profile whatever the environment says (those tests skip there).
- **Windows**: a write waits out a reader that shares no writes for up to 2 s (`FileText.WhileHeld`); closing a socket
  with unread input resets it, losing what was written (servers shut their side and drain first); the diagnostics library
  leaves a profiler pipe open after its listener goes (`ProfilingSession.CloseLeftListener` closes it, by reflection).
- **macOS**: a Unix socket path holds 104 bytes; `codesign` takes a dotted folder under `Contents/MacOS` for a bundle (so
  the web servers live in Resources); TMPDIR is reached through the /var link, so a build or restore of a test's project
  goes by its real path, and two spellings of one project restored at once collide.
- **macOS's "damaged" dialog** comes from a Passenger.app whose seal no longer holds being read or started: a mounted disk image
  whose file was replaced under it (`just package-mac` ejects one first) or a bundle written into (#450). Tests never start a
  program inside an unsigned bundle, and the run's folder ends in `.noindex`, so Spotlight reads none of what a run makes.
- **Linux**: file systems tell letter case apart, so a test of another letter case skips there.
- **MSBuild**: a build through another spelling of a project's path deletes what the first wrote (IncrementalClean;
  the terminal's shell is given its folder as PWD, #413). It keeps no idle node unless the process's nice value is 0.
- **EventPipe from a program's start**: .NET 10's LoggingEventSource misreads the `FilterSpecs` it is made with, so a
  category takes no `:Level` there or none of its logs come (`LiveRequests.LogsSpec`).

## Tests

Tests drive the IDE whole, headless (`tests/Passenger.Tests`, xunit v3 on Avalonia's headless platform with real Skia):
a window or an editor, real keys by Rider's macOS keymap (`EditorRig`), typing, clicks, files on disk. No unit tests of
one type or function: a test says what a person sees or gets, and its failure message names the consequence.
`BannedSymbols.txt` (RS0030) bans driving internals directly (the terminal's emulator, a hand-started Roslyn or profiling
session, a lone `TextDocument`, `LineDiff`, `CommitGraph`, `Snippet.Parse`; `Rig.cs` excepted), raising Click events
(`Ui.Click` instead) and fixed sleeps (`Thread.Sleep`, `Task.Delay`, `Screenshot.Pump`) outside `Waiting` and `Ui`. The
long random editing session checks every key against a string model, with soft wrap too: keep it passing, never narrow
it. Performance is held by what the UI thread allocates in a small case against a large one, not by a time; to see where
the UI thread's time goes, attach Passenger's profiler to the test process (`ProfilingSession.AttachAsync(Environment.ProcessId, …)`).
A headless frame is GC pressure, so tests that press thousands of keys draw a small window.

**Waits (#237).** A test waits for what it checks, however slow the machine. `Ui.Settle` (and `Press`, `Landed`,
`WaitFor` on it) waits until the IDE is quiet: `BackgroundWork` landed, nothing in `InFlight` (but Roslyn's held
workspace pull), due timers fired, the last layout drawn; a give-up lists each hold by file and line. Not waited for: a
running program, the state store's 0.5 s write delay (flush to read a state file), templates read ahead, the system's
delay before the watcher hears a change (wait for its `Changed`). Words describing the window as a wait gave up are a
`Func<string>`. Wait with `Ui.WaitFor`, `Ui.PumpUntil`/`Pumped` (the loop run, not settled: a held start, an app that
keeps the window busy) or `Waiting.Until`/`UntilAsync`/`Within` off the UI thread; time that must pass is `Ui.Hold(time,
why)`, `Ui.Stall` or `Waiting.For`. Every limit is written as a developer machine's time and scaled by
`TestRun.TimeScale` (`PASSENGER_TEST_TIME_SCALE`; CI 2 on macOS, 2.5 on Windows, 2 on Linux); the IDE's own waits on what
it started scale by `PASSENGER_WAIT_SCALE`. `FLAKES.md` records each CI failure's cause.

**Isolation.** A run keeps to its own folder (`TestRun`): the IDE's state, logs and sockets, every test folder
(`TestFolders.Create`), shells' history, an empty Git configuration, NuGet's home on macOS and Linux, MSBuild's node salt,
container labels, user secrets (`TestRun.UserSecrets`), dotnet new's templates, Aspire's HOME and CLI. It never reaches
nuget.org (local feeds: `AvaloniaApp.Feed`, `StandInFeed`, and the restore-only `tests/EfGraphs` and `tests/AspireGraphs`
that fill the package cache), GitHub (`PASSENGER_RELEASES` dead), registries (`PASSENGER_BUILDKIT_CHECKS=off`), Docker Hub,
symbol servers, Discord, or the person's Passenger (`PASSENGER_START=headless`). It shortens `PASSENGER_EVAL_TIMEOUT_MS`
(3 s), `PASSENGER_WATCH_LATER_MS` (3 s) and `PASSENGER_MCP_PROGRESS_MS` (1 s); a test holding a server to not answering
sets that server's limits, never the run's. After each window test its windows close and what they started is waited out
(`TestsLeaveNothingBehind`). A plain `[Fact]` using `HeadlessUnitTestSession.Dispatch` must not end on the session's
thread (`await Task.Yield()` before throwing). A sleeping Mac fails tests: full runs go under `caffeinate -is`.

**Collections and shared projects** (`Collections.cs`). Window tests run one at a time on the session's thread. Beside
them run only the debug adapter's protocol tests (`DebugSession.Collection`) and `Beside` classes (no window, nothing the
process shares changed, nothing held to a time). A class with a test held to a time, or that debugs through the window,
is `[Collection(Alone.Name)]`, run last with nothing beside it. A class sharing one project derives from `SharedProject`:
one warm language server, the files put back before each test (with times that keep MSBuild honest), a new file through
`project.Write` (which ends the server), `Prepare(restore: false)` where nothing reads a restore, `Prepare(byRealPath:
true)` where projects name each other by real path. Fresh restored folders come from `RestoredFolders`. A test asking
nothing of C# opens without the language server (`Ui.OpenFolder(folder, languageServer: false)`, `McpRig.Start(…,
languageServer: false)`).

**Runs.** `just test` deals the classes outside Alone to N processes by `TestTimes.json` (each with its own run folder;
the container classes in one lane), then the Alone classes in one. CI deals each system's suite to four shards the same
way; a shard over `TestBudget.json`'s 32 minutes fails (macOS's runners vary ±10%; a fifth macOS shard would queue behind the disk image), a class over its seconds warns, and `just test-times RUN` rewrites
the times. The run sets its nice value to 0; a test needing an MSBuild node waits for `Processes.MSBuildNodesCounted` or
skips saying why. The VMs run a filter on a commit: Windows on Arm, `~/Documents/passenger-vm/vm-test.sh NAME FILTER` (no
Node.js; it can't see dot folders), and Ubuntu 26.04 ARM64, `vm-test-linux.sh NAME FILTER`.

**Helpers.** Clicks: `Ui.Click` (a real press on a control shown, enabled and hit, shutting first the tooltips its
pointer leaves, which open in the window headless as the window redraws under a pointer that never moved; `RightClick`,
`DoubleClick`, `ClickButton`, `ClickRow`, `ClickLink`); menus: `Ui.Choose`, `ChooseFromMenu`, `ChooseFromRowMenu`,
`ChooseFromEditorMenu`. A chord in one `Ui.Press(window, "control M , control S")` (its first key is forgotten after 2
s). Consoles: `Ui.TypeLine` (`TypeToRun`, `TypeToDebugged`); the caret: `Ui.PlaceAfter`; a run: `Ui.Launched` and its
status `Ui.Said`, both naming a failed build's errors. Projects: `TestProjects.Console`, `Library`, `Web` (`.With`) on
`TestProjects.Framework`; files: `TestFolders.Write`; a loopback HTTP server: `StandInHttp`. Multi-line edits:
`Typing.Replace` (`KeyTextInput` drops `\n`). Read a log the IDE may be writing with `Ui.Shared`. A dialog is held to
`DialogButtonsTests.Check`, the window to `UiRulesTests`. A stand-in leaves files a target writes as it found them (a
new one makes MSBuild skip the target). Containers: `Docker.Stopped`/`TakenDown`; MCP: `McpRig.Text`, `Gone`; an agent
that gives up sends "cancelled" itself (`CallGivingUp`). A headless click runs the window's queued work and draws frames
after itself, so what a status said during a click is read from what it showed throughout, not after; a count of a key's
frames sets aside the caret's blinks (`CodeEditor.Blinks`).

**What a test needs, or skips saying why without:** a Linux container engine (`docker info`'s OSType; tests label
everything with `Docker.RunLabel`, remove dead runs' leftovers, never prune or start or stop Docker Desktop, and pull
nothing large: a test needing an image the engine lacks names it and skips), Node.js (web languages), Postgres's image
(MigrationsPostgresTests), `PassengerAspireRuntime=true` (real AppHosts: Aspire's dashboard and orchestrator, about
900 MB), Windows (WPF's preview and inspector, data breakpoints on x64). The WPF reference pack's version is pinned
(`WindowsDesktopRefVersion`): a new SDK fails WpfXamlTests naming the version to pin.

**Debuggees** (`tests/Debuggees`): tests find the lines they stop at by their code (`Debuggees.Line`, `MyMethodEnd`), so
changing a line's code means changing the tests that name it (a breakpoint on a line with a lambda binds inside it: give
the column). `SelfDebuggingTests` finds its
breakpoint by the code `status.Text = why;` in MainWindow.Actions.cs. `Debugging/Helpers/StartHold` holds a launched
program at its start (`DOTNET_DiagnosticPorts=…,connect,suspend`). `PASSENGER_ENGINE_LOG=FILE` keeps the engine's log.

**MCP tests** are agents on the SDK's own client over `McpRig`: `McpTests` and per-area classes on `--mcp`,
`McpWindowTests` and `McpAttachTests` on a window through `--mcp-attach`, `McpDebuggingTests` holding the tool list's
size. Each checks a tool's answer, leaving the window's steps to the window's tests.

## Code style

- C#: four spaces, file-scoped namespaces, braces on every block, explicit types over `var`, accessibility always
  written; the build fails on each (IDE0161, IDE0011, IDE0008, IDE0040).
- A comment speaks only of what it sits on, never how something else works, and says why, never what the next lines do.
  A file's or type's comment is at most 10 lines, a member's 5, and one inside a body 1 line, there only for a reason the
  code can't show (`just comments` holds the lengths).
- XAML: four spaces; one style per selector in `Theme/Shell.axaml`, restyling Fluent's states on its
  `PART_ContentPresenter`. Shell.axaml's base styles give every Button, TextBox, ComboBox, NumericUpDown, CheckBox and
  MenuItem its look (`Passenger.ControlHeight` 28; radii `ControlRadius` 4, `ToolRadius` 6, `PopupRadius` 8); a class style
  says only how it differs. A style on a template part Fluent sets under a property trigger (`^[AllowAutoHide=True]`)
  needs a trigger of its own, or it ranks below Fluent's.
- A visual inconsistency is fixed once, in a shared place (a token, a base style, a helper), never per control: Passenger's
  own consistency settles a design question. The shared pieces: dialogs `DialogFooter` (`Of`, `Listed`, `Answers`, `Bar`,
  `Page`) in a `Border.footerBar`; lists in a page `Border.framed`; rules `Passenger.Separator` (`Passenger.Divider`
  between panels); menus' value in force `MenuChoice`; tool window tabs `ToolTabs.Tab`; popups' widths `PopupFrame`,
  titles `popupTitle`; every row `ListRow.Height` (24); empty states `TextBlock.emptyText`; toolbar icon buttons
  `ToolButtons.Of`/`Toggle` (never focusable); muted words `MutedText`; list keys `ListKeys.Move`; counts `Plural.Of`;
  rounded borders `Border.kindBadge`, `tag` or `box`, never a literal radius; lists of what runs `RunRows`, Details tabs
  `DetailsTables`, merged consoles `MergedLog`; a toolbar down a side `ToolColumn`; scrollbars `ControlThemes.axaml`; the
  focus ring `FocusRing`. A row a template makes hears its item only while shown (`Observable.FollowWhileShown`).
- `MainWindow.SmallestWidth` is 640: the layout works at half a 1366-wide screen, and new windows fit the screen's
  working area (`FitToScreen`).
