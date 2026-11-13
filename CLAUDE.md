# CLAUDE.md

Guidance for Claude Code in this repository: the rules, the commands, where things are and the traps the code doesn't
show. For how something works, read the code; Passenger's own MCP tools (below) are the quickest way. Every session
loads this file, so keep it short: a change adds a rule, a trap or a pointer where one is missing, in a line, never a
description of what the code does.

## What this is

**Passenger** is an IDE for C# and modern .NET (CoreCLR only: no .NET Framework, Mono or Unity; the one exception,
Blazor's WebAssembly half debugged through the runtime's browser debug proxy, #586), written in C# on .NET 11 with an
Avalonia 12 UI, for Windows, macOS and Linux. v0.3.0 shipped; the Backlog
(https://github.com/Zaneris/passenger/milestone/2) holds what's next. Each issue says **Now:**, **The aim:** and **Done when:**; read it before anything larger than a fix,
and close or tick it when it lands. A new feature is the best Passenger can make it, never a copy of another IDE's; only
its keys follow Rider's keymaps (their files are in the jars of `~/Applications/Rider.app`).

## Commands

```sh
just build                 # Debug build of Passenger.slnx; warnings are errors
just run [PATH] [args]     # the IDE on a folder or file (--file F, --caret L:C, --theme T, --run|--debug [NAME])
just screenshot X.png ...  # the main window drawn off-screen to a PNG, after the steps given
just test [filter] [N] [--gate|--report]  # tests whose name matches (one lane, or both), in N processes, then the
                           # Alone classes (tools/test-run.py); results and logs in artifacts/test-results/local;
                           # --plan shows the deal
just test-times RUN...     # rewrite tests/Passenger.Tests/TestTimes.json from CI runs (it deals CI's shards)
just publish RID           # self-contained Release build: osx-arm64, osx-x64, win-x64, win-arm64, linux-x64, linux-arm64
just package-mac [RID]     # the .dmg (osx-arm64, or osx-x64 checked under Rosetta with DOTNET_ROOT_X64's Intel SDK),
                           # signed by PASSENGER_SIGN_IDENTITY and notarized by PASSENGER_NOTARY_PROFILE when set,
                           # else ad hoc (--dry-run needs an identity)
just package-win [RID]     # Velopack's per-user installer; just package-linux [RID]: the .tar.gz with install.sh
just first-start-shots     # the first start's pictures of each carried theme, retaken in a version bump's commit
                           # (FirstStartPicturesTests fails until then)
just debug-relay [android] [apple]  # the debugger's relay into artifacts/debug-relay/<rid>/ (macOS: clang, the Android
                           # workload's linker, Xcode's SDKs); the app carries Android's under debugger/relay/<rid>/
just container-tools [RID] [--archive]  # the in-container tools (linux[-musl]-x64/arm64) into artifacts/container-tools
just notices               # fail naming any carried package the notices miss
just mcp [FOLDER]          # Passenger as an MCP server over a folder, from a clone of the last build
just mcp-call FOLDER TOOL [JSON]  # one MCP tool over a folder (a worktree) from the shell, its server kept
just site-shots [SCENE...] # site/images/<scene>-<dark|light>.webp retaken (macOS, cwebp)
just godot-sweep [NAME...] # tools/godot-projects.json's Godot projects swept by a headless Passenger (report.md),
                           # cloned outside any repository, whose Directory.*.props MSBuild would take in
just icons | file-icons | themes | unicode-widths | aspire-protocol | app-icon  # regenerate
just godot-shader-types | godot-theme                                          # regenerate
```

The app icons, generated themes, icon sets, terminal widths, Aspire's protocol code, and Godot's shader table and
default theme come only from their recipes.

**Use Passenger on itself.** `.mcp.json` starts Passenger's MCP server over this repository (`tools/mcp.sh`, an APFS
clone of the last Debug build: reconnect to pick up a new one). Ask Roslyn with `diagnostics`, `find_symbol`, `outline`
and `usages`, debug the IDE's projects, and look at the window with `app_screenshot` (`app` ide). Lines and columns
count from 1. Anything it gets wrong is a Passenger bug.

**Look at UI changes** with a headless screenshot at `--scale 2` before calling them done (real Skia and real input,
never a window or the focus; it waits for background work):

```sh
artifacts/bin/Passenger.App/debug/Passenger . --file src/Passenger.App/App.axaml.cs --caret 12:5 \
    --key enter --type "var x = 1;" --key "shift+meta+left" --click 400,300 \
    --screenshot out.png --size 1400x880 --scale 2 --crop 320,76,900,500 --theme light
```

Steps: `--key` (as a US keyboard types it: `shift+4` is `$`), `--type` (one input, so completion opens only on a trigger typed alone), `--click`, `--right-click`,
`--hover X,Y` (logical units), `--click-name NAME` (exit 2 if none), `--drag[-hold]`, `--wait MS`, `--window-buttons`,
`--agent COMMAND` (an agent in the Terminal's Agent tab, every tool allowed); a step after one that opens a dialog goes
to the dialog. State is kept only where PASSENGER_SETTINGS names a folder, whose settings.json a screenshot or `--mcp`
Passenger opens on, keeping no change (short on macOS: a socket path holds 103 bytes). Headless, dispatcher timers fire only inside a running frame (`Screenshot.Pump`, `Ui.WaitFor`).

**Build facts.** Everything builds under `artifacts/`. Package versions live only in `Directory.Packages.props` (each
kept pin saying why), shared properties only in `Directory.Build.props` (the version too). Packages come from nuget.org
alone, as it signed them, at what each `packages.lock.json` pins: CI restores locked, so commit a rewritten lock file
with its change; every lock file holds all eight `RuntimeIdentifiers`, even a Windows-only project's (a publish's RID
reaches every referenced project's restore). Every recipe and CI set `AVALONIA_TELEMETRY_OPTOUT=1`. The App and the
tests run with a 4 MB `System.GC.Gen0MaxBudget`, so a young collection fits a 144 Hz frame.

## Rules

- **Never Microsoft's debugger (vsdbg) or anything of C# Dev Kit**, including its pieces inside VS Code's C# extension
  (`.debugger`, `.roslynDevKit`, `.testDiscovery`), nor `VSCodeDebugProtocol`; nothing is copied out of an installed
  extension. Roslyn's server comes from NuGet; `Passenger.Dap` is the protocol.
- **No Avalonia Accelerate** (TreeDataGrid, DevTools: `DeveloperTools.TakeOut` removes what `dotnet new avalonia.app`
  adds); Avalonia.Controls.WebView, MIT since it left Accelerate, is allowed (#1078).
- **Every dependency, icon or asset has an open-source licence and an entry in `THIRD_PARTY_NOTICES.md`** in the same
  change (`just notices` and CI fail on a package missed): a package by NuGet id with its licence and copyright as
  stated, copied source by file and origin, assets after the marker line, a new SPDX text in
  `tools/Passenger.Notices/Licenses`, a package inside Roslyn's server in its table. MIT first. The owner's exceptions:
  Visual Studio Telemetry and Utilities.Internal inside Roslyn's server (Razor needs them), and the TextMate bundle
  licence.
- **Every program Passenger starts goes through `ChildProcesses`**, so none outlives the IDE (`Lifeline`): `Command`,
  `DotnetCommand`, `HelperCommand` (Passenger's helpers on Passenger's runtime, never the machine's), `ConservingMemory`
  for Roslyn's server and the project host but never the debug adapter, whose program would inherit it, `DotnetQuestion`
  for the .NET CLI's questions (no telemetry, so it lets go of its folder), `Kill` for it and all it started. Give each
  its folder as the disk names it, PWD too. On Windows every child shares the IDE's UTF-8 console (`SharedConsole`).
  Read a child through `ChildProcesses.Output`/`Errors`/`Hear`, never `Begin*ReadLine` (banned: its disposal faults a
  read under it). Started elsewhere: the terminal's shell (Porta.Pty), vstest.console, Velopack's Update.exe, an agent's
  `--mcp-attach` Passenger (`StartApart`), the system web view's helpers; a program run in the terminal is `Adopt`ed.
  Passenger's own standard input is taken at its start (`StandardInput`), so nothing it starts reads it.
- **No secret on a command line** (any process can read it): give it through the environment, or don't make it. Secrets
  are masked everywhere (Details, logs, consoles, hints, agents' answers, `ChildProcesses.Shown`) by one rule,
  `ContainerSecrets`.
- **Containers are ended by label** (`ContainerLabels`), never by `down` or `--remove-orphans`, which take the person's
  own; Aspire's unlabelled DCP containers and network through `AspireContainerOwnership`; persistent containers and the
  person's own never.
- **Colours are tokens** (`Passenger.*` in `Theme/Tokens.axaml`, the default theme every other is laid over), given in
  every theme (dark and light; the others live in Zaneris/passenger-themes, the tests' copy in
  `tests/Passenger.Tests/Themes`, Harbour's and Reef by hand, the rest by `just themes FOLDER`; Cubicle, carried in `Theme/BuiltIn`, by
  `tools/themes.py`'s `BUILT_IN`, `ThemeSource.Carried` naming what is carried); a theme file is read
  only through `ThemeFiles` (format 1, `docs/themes.md`) and laid through `ThemeResources`, so a token added to
  Tokens.axaml is one a theme may give. A theme is a `SourcedTheme`; a source is read only through `ThemeSourceReads` and
  `ThemeListing`, never at a start whose themes are kept (`KeptThemes`); `PASSENGER_THEMES` names the repository's place; a theme's picture is drawn only through `ThemeBackdrop`
  (`ThemeBackdrops`), its colours by language reach an editor only through `ThemeLanguages.Follow`; the code's size in force
  is `AppSettings.CodeSize()`, never `EditorFontSize`, the person's own and null until set;
  Fluent's keys are mapped once in `Theme/FluentKeys.axaml`. The window's typeface
  is the resource `WindowFonts.Key` (macOS tracking `WindowTracking`), the code's `CodeFonts.Key`; a control drawing its
  own words takes `TextElement.GetFontFamily`, never a named family. UiRulesTests fails on a colour or non-zero
  CornerRadius written outside `Theme/` (but the content's own) and on an unmapped Fluent key. No access keys:
  `Shell.axaml`'s ContentPresenter style shows words as written, a native menu item's through `MainWindow.NativeWords`.
- **An identifier is named from its home** (`[IdentifierHome]`: action and tool window ids, DAP and LSP names,
  `McpTools`, each `PASSENGER_` variable, `TokenKeys`, `IconKeys`, `StyleClasses`): PSG0001 (`Rules.cs`) fails its text
  written anywhere else, a plain word only where a member marked `[IdentifierOf]` its home takes it.
- **Async is awaited**: no async void (PSG0002), no async lambda given to a void delegate (PSG0003), no blocking wait on
  a task (PSG0004; a read where the task has certainly ended passes); a caller that must stay synchronous goes through
  `Synchronously.Run`, saying why.
- **Thread-pool work goes through `BackgroundWork`** (`Run`; `Debounce`, not a `DispatcherTimer`), so screenshots and
  tests wait for it; `Unheld`/`UnheldThread` for work that mustn't hold them, saying why (PSG0006; a test's own through
  `Waiting.Apart`).
- **A catch-all is a boundary** that passes on what it caught (PSG0005); a catch around the runtime's interop takes what
  it can throw, `Failures.OfRuntime` (`OfReading` for files and metadata).
- **A malformed flag or environment value is fatal**, never a silent default (`LaunchOptions.cs`).
- **A warning is fixed at its cause.** NoWarn, a pragma, a lowered severity, a suppression, an analyzer's baseline, or a
  raised budget or limit does not count as fixing it.
- **Work nothing awaits goes to `IdeErrors.Observe`** (`Core.UnawaitedWork.Observe` below the App), a fault no one is to
  hear to `UnawaitedWork.Dismiss`, a wait however it ended through `task.Ended()`; never a bare `_ =`, `ContinueWith` or
  a call left as a statement (IdeErrorsTests reads the built IL).
- **One threading rule.** Controls, documents, the window's `ProjectSession` and its parts, controllers and views belong
  to the UI thread (`UiThread.Check()` in entry points). Text, Vcs, Workspace, Packages, Lsp, Dap, Debugger and Profiler
  never reference Avalonia and raise events on any thread; Editor, Languages and Terminal on the UI thread. The App
  crosses from another thread only through `UiThread` (`Post`, `Marshal`, `InvokeAsync`).
- **Paths are canonical** (`PathNames.Canonical`, symlinks resolved, as MSBuild and Roslyn report them); compare with
  `PathNames.Comparer`/`Same`/`IsUnder`, names in one folder with `NamesIn(folder)`, never `Comparison` for a path; walk
  a project's folders with `FoldersUnder`, the open folder's files with `FilesUnder`; show one with `PathNames.Shown`.
- **Lines differ by reader.** Roslyn ends a line at a lone '\r', U+0085, U+2028 and U+2029, an XML reader (XAML's
  compilers, BAML, XamlIl) at a lone '\r' only. Every place crossing to a server, compiler, PDB or running app goes
  through `ServerLines`/`ServerPlaces`, never `new TextPosition(line, character)`; the debugger's lines through
  `Debugging/BuiltLines`. MCP's places are the editor's.
- **Writes go one way.** Every write first passes `EditorArea.ClearAsync`, the gate for what the person's Godot editor
  holds unsaved (an agent refused, `Refusals.HeldInGodot`); `TrySave` refuses a held file unless explicit or let
  through; a command writing outside its recording asks first (`Recording.ClearAsync`). A workspace file:
  `FileText.Save` (atomic, metadata kept, a hard-linked file in place, a read-only one refused, Local History given the
  old bytes); a file the IDE makes `SaveNew` (its .editorconfig's end_of_line and charset), again `SaveOver`; one the
  window changes itself `MainWindow.ChangeFileAsync`; a file landed byte for byte (a publisher's output) `LandedFiles`;
  any other whole file `AtomicFile.Write`, a file that may be a link (a shell's startup file) written to the file it
  names (`AtomicFile.Target`); a state file only the person may read `OwnFiles`; never `File.WriteAll*`. An open
  project, solution, filter, nuget.config or Directory.Packages.props is saved before Passenger writes it and loaded
  after (`SaveBeforeWriting`). Server edits pass `LineEndings.Fit` then `Indentation.Fit`.
- **A change of several files, or a move, is one command of global undo** (`EditorArea.Commands.Record`; `RecordWrites`
  for one that waits between writes; moves through `Recording.Move`, never a bare `EditorArea.MoveFile`; deletes kept).
  A closed file changes through `Recording.Change`, written off the window's thread, every write of closed files in turn
  (`CommandHistory.InTurnAsync`); Undo and Redo put them back off it, a `Part` saying why it could not be put, one the
  disk may refuse going `First`, one refused partway taking back what it did. The window follows only what a change
  made, moved or deleted (`ProjectSession.CameOrWent`), never a whole re-read of the folder; one that may make or delete
  a file runs in `AssistContext.ChangeFilesAsync` (`MainWindow.Following`): the watcher hears the IDE's own writes as
  saves (`FileText.Writing`), and Roslyn's server takes a file heard changed into no project.
- **Saves land in order.** A save Windows refuses while another program holds the file lands in the background
  (`FileText.Landed`); what hands files to another program (a build, Git, a restore, an agent) waits for it
  (`SaveAllLandedAsync`); a change writing closed files is done only once its own writes landed
  (`WorkspaceEdits.LandedAsync`, `Recording.LandedAsync`).
- **A file shown read-only refuses every change** (`TextDocument.IsReadOnly`, set only by `OpenDocument`; every file in
  a package folder an evaluation names, `PackageFolders`); an action on it greys with `Refusals.ReadOnly`.
- **State is a `StateDocument`** in the one `StateStore` (versioned, `Clamped`, debounced, merged, a bad file set aside
  as `.bad`), read off the UI thread, never a file of its own; per-folder state under `workspaces/<name>-<hash>/`, held
  by each window with it open; writes hold a lock every process honours (`StateFile.Hold`); a renamed key moves in
  `IStateDocument.Renamed`; a value of the wrong kind is passed over alone. A new setting: an `AppSettings` property,
  its row and range in `Declared` (refused values `Refused`, which Settings and `settings_set` ask), `Add`ed in
  `SettingsDialog`, a line in `MainWindow.Follow` or `SettingsStore.InForce`, a look's in `SettingsPreview.Over` and
  `MainWindow.OnPreviewChanged` (`VisualsInForce`) instead, so Settings shows it before Apply; a row another setting
  voids says so (`SettingRow.When`).
- **Every action is an `IdeAction`** in the window's `ActionRegistry` (ids and keymaps in `AppActions.cs`): keys, menus,
  Find Action, context menus and MCP's `ide_action` all run it; `WhyNot` greys it with why; titles come from the
  registry (`TitleWhere`), one that asks more ending in "…"; what's missing is said in `Refusals`' words (RefusalsTests
  fails a copy). Work that takes a while holds a `Working` (`ActionWork.Begin`) from its first moment to its hand-over,
  saying each stage, honouring `Stopping`, and tells what it came to (`Working.Tell`), which `ide_action` answers;
  toolbar buttons bind with `ActionButtons.Bind`.
- **An agent never takes the person's keys or brings a window forward** (`ActionRegistry.RunForAgent`,
  `DockHost.ShowBehind`); `AgentAsks` cancels an attached agent's focus moves and refuses its popups and modal dialogs
  (`Refusals.AsksThePerson`; a popup host asks `AgentAsks.Refused`); a file it opens comes in a tab behind.
- **One way out to the system:** a browser only through `MainWindow.Browse`, the file manager only through
  `ShowInFileManager` (both recorded for tests).
- **Shortcuts follow Rider's** Visual Studio (Windows) or Visual Studio OSX (macOS) keymap, and its VS Code and IntelliJ
  (`$default`, `Mac OS X 10.5+`) keymaps for those presets, resolved as IntelliJ does (own list, plugins' descriptors,
  the parent); a keypad key Rider binds beside a main key is bound too; a modifier tapped twice is a shortcut. A key
  IntelliJ gives two actions by place goes to one, said where it's bound (`AppActions.KeysIn`). Mouse gestures:
  `Keymap.Bind` takes Rider's notation (`meta shift button1`); an editor asks its window's keymap and names no IDE
  action; the side buttons run window-wide; an action's `shortcuts` list gives exactly its keys and gestures; a gesture
  gives one click (run on release where pressed) and one drag (`EditorActions.IsDrag`).
- **Workflows run only by hand** (the owner's choice): never `gh workflow run` unless the owner says. Commit messages go
  through a file (`git commit -F msg.txt`): backticks in a `-m` string are zsh command substitution.

## Where things are

Projects in `src/`; `find_symbol` finds the rest from the names here. A pointer's "never" is a rule.
- `Passenger.Core` (no UI), where each of these is the one way: a PATH search `Programs.OnPath`; .NET's folder
  `Dotnet.Root`, never `Dotnet.Host()`'s folder; a request of a host `OutboundHttp`; a Unix socket `SocketFolders`; a C
  call made in more than one place `Libc`; whether a process runs `DotnetProcesses.Runs`, a `Process` held too, never
  `HasExited` (PSG0007): ended, one Passenger started has an `ExitCode` to read, one found by id none; a PASSENGER_*_MS
  `EnvironmentValues.Milliseconds`; a Windows command line `WindowsCommandLine`; the version `Product`; a log below the
  App `PassengerLog`, never `IdeLog`; an MSBuild name Passenger writes `MSBuildNames`; another program's or a person's
  JSON `JsonMembers` (`Value` for a member of any kind), never a bare `TryGetProperty`/`GetProperty` (PSG0008); a search
  by halving `Sorted`, never a loop of your own; a primitive `PrimitiveTypes`; an SDK into an installation
  `DotnetMerge`; a path in a project file `ItemPaths`; what a Mac program links `MachOFile`; whether this process runs translated `Dotnet.Translated`, a program's processors
  `ProgramProcessors`; work any thread asks for by
  key (a download), begun once however many ask, an ask that stops waiting never letting it go, `SharedWork`.
- `Passenger.Text`: the rope, `TextDocument`, editing commands as pure functions to an `EditPlan`, `FileText`; an XML
  value through `XmlEntities`, never `SecurityElement.Escape` or a hand Replace; a place in the text held by an anchor
  (`AnchorAt`), never your own `TextEditing.Map*`; an end tag typed over by hand `EndTagTyping`, never a caret jump.
- `Passenger.Editor`: `CodeEditor`, `Keymap`, `EditorActions`; the code font is `CodeFonts.Key`, never a `FontFamily`
  written elsewhere; a family code or the window's words are drawn in is named through `FallbackFonts` (`Carry`, `Of`), an emoji's fallback asked in
  `und-Zsye` (`FallbackFonts.LanguageOf`), never the culture's, or fontconfig names a text face,
  so what it lacks takes the platform's own fallback; the editor names no debugger, test or VCS concept (`IGutterColumn`, EditorRulesTests); a file's
  editors draw its one `DocumentDecorations`, never fed per split. Vim mode: `Vim` decides a key's meaning purely over the editor's
  `IVimView` (`Vim.Commands`, `Vim.Operators`, `VimMotions`, `VimTextObjects`) and `CodeEditor.PressVim` takes it before
  any keymap; an insert session is one `EditKind.Joined` step, `.`'s edits one too (`FollowVim`); normal-mode carets
  settle in `SetSelections`; registers are each window's own (`CodeEditor.VimRegisters`), `+`/`*` the clipboard; visual mode's stretches are the editor's selections, a selection made
  otherwise entering it (`Vim.FollowSelections`); macros are pressed again by `CodeEditor.ReplayVim`; search and the ex command line are Vim's own (`Vim.Search`, `Vim.Ex`,
  `VimPatterns`) over `TextSearch`, and what only the window can do comes as a `VimRequest` (`MainWindow.Vim.cs`);
  `VimTests`' expected texts are the system Vim 9.1's. Wide characters are counted by `WideCharacters` (Passenger.Text).
- **A key costs what it changed, not the file's size**: keep it so. Wraps, rows, folds, hints and built lines move only
  where an edit reaches; a highlighter keeps colours an edit didn't reach (`KeptColours`, `ChangedWithin`); a row is a
  retained visual (`CodeEditor.Rows.cs`) drawn again only where its key changed, the caret's blink only the overlay;
  shapes are made at the origin and drawn through a translation (FlingTests, ScrollingTests, RetainedRowsTests).
- `Passenger.Languages`: C# by Roslyn's parser in-process, each open file's one syntax (`OpenDocument.Syntax`), never a
  fresh parse, every parse's options from `CSharpParsing`, never its own; the rest by TextMate grammars through Tint
  (`TextMateHighlighter`, `ScopeColours`). A new language: one `Language` entry, its grammar and a notices entry;
  compare entries, never names. A finder never tells its answer inside a document's `Changed`. A recursive reader of a
  person's text goes through `DeepestNesting` and checks the stack at each level (`StackLeft`), as a level's cost is the
  platform's: no text ends the IDE. Roslyn's parser recurses unchecked,
  so every in-process parse goes through `CSharpParsing.Parse`/`Changed` (`DeepText`), never `ParseText`; its depth is
  read as Roslyn's lexer reads strings and comments (`Brackets.ReadCode`), a key's around what it changed
  (`TypedMayGoDeep`), and a `?[` is read ahead to its expression's end, so 11 of them at once are too deep.
- `Passenger.Xaml`: Passenger's own XAML core (no Avalonia; WPF shares it; not Avalonia.Ide.CompletionEngine); a
  binding's path is read once (`BindingPath`) for the check, Go to Declaration, uses and completion. MAUI's XAML is a
  dialect of it (`MauiDialect`, its implied namespaces in `XamlFile`); a `{…}` a dialect reads as a C# expression is the
  compiler's to check (`XamlDialect.IsExpression`).
- `Passenger.Lsp`: the LSP client (StreamJsonRpc, whose types never leave the project), `LspDocument` (never a change
  between a "\r\n"'s halves), Roslyn's server (`RoslynServer.targets`), the web servers under `web/` (to move a pin:
  edit `WebServers/package.json`, `npm install --package-lock-only --ignore-scripts`, the notices' "Inside web/").
- `Passenger.Roslyn.Extension`: Passenger's handlers inside Roslyn's server (`src/Publicize.targets`); a handler reads
  the solution, never changes it.
- `Passenger.Host`: MSBuild in a process per SDK and global.json (`ProjectHosts`); `hold` keeps design-time builds off
  while Passenger builds (Windows lets one writer have a file). File-based programs `ProgramFiles`; `dotnet run-api`
  decodes a directive's text once and writes it unescaped: `#:ref`'s metadata is the real file, a Compile's Include is
  read again by MSBuild.
- `Passenger.Vcs`, `Passenger.Workspace`, `Passenger.Packages`: UI-free Git, folder and NuGet code. A revision's text
  `Git.BytesAtAsync` in the open file's encoding, never `git show` decoded as UTF-8; every git call but a clone through
  `Git.RunAsync<T>`; paths on git's input, never its command line; git's questions only through `GitAskPass`, GPG's
  through Passenger's own gpg (`GpgLoopback`; gpgsm's for X.509; Git for Windows' MSYS gpg given its home as MSYS reads
  one, `MsysGpg`, its agent inheriting none of Passenger's handles, `StandardHandles.NotInherited`; every gpg found by
  `GpgPrograms`); signatures read for the rows shown, never by `%G?` in a read; a git that may ask ends with its folder
  (`GitQuestions`); a partial commit's own index starts from `read-tree -m --index-output` (the real index's stats), never
  a plain `read-tree`, which reads every file again.
- `Passenger.Dap`, `Passenger.Debugger`, `Passenger.DebugAdapter`: Passenger's own debugger behind its own DAP. On a
  device it runs through `Passenger.DebugRelay` (relay.c), loaded by the Debug app as its profiler, which knows only the
  callbacks' argument kinds: `Passenger.Debugger/Relay` plans each interface from the interop declarations, keeps answers
  in `RelayAnswers` keyed by the interface declaring the method (ICorDebug's interfaces reuse slot numbers), and answers
  or refuses a raw pointer no plan can size on the host.
  `Passenger.ContainerTools` runs inside a Linux container, carried only in the release's archives.
- `Passenger.Profiler`: EventPipe and ClrMD (its Azure packages stay carried; its DAC stays loaded once used: unloading
  it crashed on Linux); recording in a container trusts nothing in a frame. A heap dump is Passenger's own (`DumpTool`):
  the runtime's createdump writes into the program's stdout, and Windows' dumps only its parent.
- `Passenger.UpdateHandlers`, `Passenger.Inspector` (+`.Wpf`; `Protocol.cs` in both ends), `Passenger.WpfPreview`,
  `Passenger.TestFrames`, `Passenger.Terminal` (Porta.Pty); `Passenger.Analyzers` (`Rules.cs`; a rule goes on only where
  nothing it flags is left); `tools/` (notices, the test runner, packaging, site shots).

`src/Passenger.App`, by area:
- **Shell.** A new state's first start asks the theme and dark or light (`MainWindow.ShowFirstStartOnce`,
  `Welcome/FirstStartView`), never in a window nobody sees; a carried theme needs its first-start pictures.
  `KeepWorkspace` writes `WorkspaceState` whole, so a field added to it is added there too. Every open through
  `PassengerApp.OpenProject`; a `.slnf`'s left-out projects are never saved as the
  person's; a handler acts only while its session is bound (`ProjectSession.Bind`). A Passenger nobody sees
  (`ShowsNoWindow`) runs Godot and browsers headless, a desktop app where nobody sees it (`UnseenApps`; what a start
  runs read by `DesktopPrograms`, never `Built` alone), and plays no sound; a Linux display is taken by `NoDisplay`,
  never by unsetting `WAYLAND_DISPLAY`: a client named no socket connects to the session's `wayland-0`. Anything new on
  the toolbar goes inside `ToolbarStart` or `ToolbarEnd`, or Windows takes a press for the caption's. The display scale is
  one `InterfaceScale` transform per window, which its popups take from it.
- **Editors.** `EditorArea` (a file in two windows is one `OpenDocument`; every editor through `ForEachEditor`; the one
  close `CloseTab`). A diff is an `OpenDocument.OfDiff` of no file, never in `Documents`, every one through
  `MainWindow.ShowDiff`. An SVG preview is a drawing (`ZoomedPicture`), never a bitmap; an HTML preview is never made in
  a window nobody sees (WebView2 refuses a non-STA thread), on Windows is served (`ServedPage`, `WebView2Pages`),
  `NavigateToString` only where WebView2 refuses, and its white is the web view's own (`DrawGround`), never CSS: a
  background on html stops a body's reaching the canvas. Convert Indentation has one refusal
  (`OpenDocument.WhyNotConverted`), a language's literals beyond its strings go in `Language.Literals`, Python's blocks
  through `Indentation.FirstBlockAtRisk`, `Language.BlockWords` and `Language.Continued`.
- **Docking.** A theme's default layout is the dock's default (`DockHost.UseDefault`/`Default`, `Themes.Layout`): what a
  window with no kept layout opens on and what Restore Default Layout goes to, never laid over a kept layout. A kept
  layout records windows by `ToolWindowIds`, so a value never changes; one `Dock.Add` per window. A
  view is built when first shown: what the IDE writes while it's hidden lives in a model, reached only through a
  property that builds the view for a person's action. A view letting go on `DetachedFromVisualTree` shows itself again
  on attach (a split rebuilds the frame).
- **C#.** `CSharpService`, `CodeAssist`; completion ranks through `CompletionRanking` (its order held equal to scoring
  afresh by `CompletionRankingTests`) and reads an answer once (`CompletionAnswer`, list defaults by `WithDefaults`); each language's answers a `LanguageAssist` in `LanguageAssists`' one order. A
  server's edit to a file written moments before waits for the server to read it (`WorkspaceEdits`); one written as it
  worked is refused. A generated document is never opened in the server (it would freeze its text into the compilation):
  its file (`RoslynSession.WrittenAsync`) or a read-only copy (`GeneratedCode`), asked about through
  `RoslynSession.Unopened`. Whether a place is in generated code is Roslyn's own check, asked through the extension
  (`GeneratedCode.SetApartAsync`), never a file-name or comment rule of Passenger's. The server finds a partial member's callers only at its declaring half and callees only at
  its body half (`PartialHalf`). Its Go to Declaration leaves out a type's generated parts once the person wrote one
  (`GeneratedParts`); an extension's document handler refuses a file that's no C# document, a `.razor`, so a handler
  asked of any file is a workspace one. C# in XAML has one finder (`XamlService.UsesAsync`). A multi-targeted project's
  framework is one choice (`TargetFrameworkChoices`), never a framework's output taken by its newest write; the
  solution-wide analysis pulls in it through the extension (`ChosenFrameworksHandler`), its reports kept by project:
  Roslyn's own `workspace/diagnostic`, pulled only without the extension, analyses every framework and names no
  project. A C# file no tab shows is pulled when shown or asked, never at a refresh. Edits added to a server's edit join
  that file's entry (`TypeRename.With`): `documentChanges` apply in order. A server's own command runs through
  `ServerCommands`, its `workspace/applyEdit` one command of Undo; a code action's edit through
  `CodeActions.ApplyEditAsync`, so a type it renames takes its files along as Rename's (`TypeRename.RenamedByAsync`).
- **Godot code.** `GodotScenes`, `GodotSceneIndex` (never a walk of every file on the window's thread; an autoload by
  uid, never project.godot's text), the extension's `GodotChecks` and `GodotKinds` (the projects' strings only through
  `GodotKinds.WritesAsync`). GDScript is Godot's parser and analyzer, ported (`GDScriptTree`, `GDScriptAnalyzer`); a
  newer Godot's warning or error is gated on `GodotEngineApi.Version`; an error is said only where the class is read
  whole and both types are sure (`GDScriptAnalyzer.Knows`, `Sure`); a GDScript session starts no Godot editor, its
  headless one only when asked (`WebLanguages.AskGodot`; a test of Godot's own server opens with
  `MainWindow.GodotHeadlessAtOpen`); a function's argument count only `GodotScriptDeclared.Takes`; what stands before a dot only
  `GDScriptAnalysis.ValueBefore`, never `NodeAt(dot - 1)`; `GDScriptFormatter`
  refuses unless `Differs` finds the same tokens, comments and tree, and Reformat Code never runs `OrderParts`. Godot's
  generator assigns every field in generated code, so a field's values come from the code's own documents. Edits placed
  in text Passenger read carry that text (`PlacedEdits`), checked before anything is written. A GDScript refactoring
  moving code asks `GDScriptEffects`: a call's arguments run before what it is called on, and an object's script
  variable with no getter is read only as what takes it runs. Text typed for a scene is checked by `GodotVariantText`
  and `GodotVariantBounds`, never `GodotFile`, which keeps what it doesn't know; values kept together go through
  `GodotKeeping`. Shader marks are Passenger's port of Godot's front end (`GodotShaderChecker`). The person's Godot
  editor is asked through IDE messaging (`GodotIdeLink`), never Passenger's headless editors.
- **Web languages.** `WebLanguages`: one Node.js server per kind but GDScript's (Godot's editor over TCP, one per Godot
  project: `ServerPlace`), every per-kind switch naming each kind; `JsonSchemas` and `PageStylesheets` reach the web
  only while Settings allows; a page's `<base href>` says nothing of the app's mount, so a link is the browser's address
  first, `LinkedSheet.Mounted` second; in a macOS bundle the servers live under `Contents/Resources/web`. One tsserver,
  never `useSyntaxServer` (it answers rename and usages from the open files alone); `typescript` stays on 6.x (7 carries
  no `tsserver`); typescript-language-server keeps a file's first `formattingOptions`, so each open file tells tsserver
  its indent. what an HTML server holds of Razor's files changes only in a send's turn (`RazorHtmlCopies`); the SDK
  writes no scoped CSS bundle once a project has no sheet left. Razor forwards `_vs_onAutoInsert` to the client's HTML
  only for `=`: its `>` is Razor's own answer, its `</` closed over its HTML.
- **Build and run.** The build configuration and platform are one choice (`BuildConfigurationChoices`, `WorkspaceModel.Build`):
  command lines take it through `WorkspaceModel.BuildArgumentsFor`, evaluations through `ProjectModels.Properties`/`Each`;
  never a `-c` written anywhere else. Godot runs only a game's Debug build (`GodotLaunch.RunsConfiguration`).
  `BuildRunController`, every console through `ConsoleSink.PumpAsync`, `LaunchPlan` (one resolution
  for Run, Debug, Profile, Hot Reload and a debugged test host), `UpToDate`; a Passenger it launches keeps its state in
  `sandbox/`; `PASSENGER_DOTNET_RELEASES` is dead in tests. Godot: `GodotLaunch`, `GodotGame`; the Game window is an
  owned window, never a child; a GDScript-only game is never in `Projects`; export templates only from a `.tpz` the
  person picks. On Windows a build first lets go of the Avalonia previewers holding its output. A solution's build gives
  each project the configuration and platform it maps it to (bin/x64/…), a project's own build neither: what reads the
  solution build's output is evaluated with them (the host's `SolutionBuild`, `WorkspaceModel.BuiltSolution`). A
  solution's multi-project launch profiles live only in Visual Studio's `.slnLaunch`/`.slnLaunch.user` beside it
  (`SolutionLaunch`, written as text where each stands), each project started as its own configuration
  (`RunConfigurations.ProjectOf`). What a mobile project needs is asked of its own workloads (`MobileQuestions`, said by `MobileBanner`), and a workload
  installed reloads every project (`ProjectHosts.EndUnderAsync`, `ProjectModels.EvaluateAll`). An app on a device is a `LaunchPlan.Device` (`DeviceLaunches`), its target chosen per project;
  Android's only through the workload's `ComputeAvailableDevices`, `DeployToDevice` and `ComputeRunArguments`, adb through
  `ChildProcesses`.
- **Runs of services.** `ServiceRun` is the one model: Debug, Attach, Restart and Profile written once; one start at a
  time; Stop ends every session and recording before the programs go.
- **Containers.** A published port takes connections from the container's start, before its program listens, so a
  container's page opens on an HTTP answer (`BrowserLaunch.AnsweringAsync`), never a connection. A .dcproj is MSBuild's
  file in the editor but never evaluated (its SDK is Visual Studio's); its properties are read as text (`ComposeProjectFile`). Its Solution-view files come from that text
  (`ContainerConfigurations.ItemsOf`), never its folder. A compose service's project is the person's choice, else Visual
  Studio's project-name label, else what its Dockerfile builds with the build's arguments filled in
  (`DockerfileStages.Expanded`).
  The person's `docker` CLI and context only, never the Engine API or a Docker package; a service's name
  always after `--`; Compose's own model is the plan, Passenger's override in the state; downloaded tools
  SHA256-checked; Podman both ways. A container publish (`-t:PublishContainer`; several platforms need
  `RuntimeIdentifiers` too, NETSDK1047 otherwise, and containerd's image store) is judged by the SDK's "Pushed image"
  line: an unreachable base image is only a warning while dotnet exits 0, and its registry client tries HTTPS first even
  on localhost; a push takes the logins the SDK reads (`RegistryLogins`, server names only), refused only on the
  registry's own 401 (`AsksForLoginAsync`). Vulnerabilities only when
  asked: without the plugin docker answers `scout … --format` with "unknown flag", so find it by `docker scout version`.
  docker exec's end ends nothing it started, only closing its standard input; a container's first process takes no
  SIGKILL from inside it, so Stop lets one attached to go (`ManagedDebugger.Unending`).
- **Aspire.** `AspireController` checks bearer tokens first; deploy logins are read from each tool's files, never by
  running a cloud CLI; metrics only while shown. MetricsEventSource's CollectionStart and Stop reach TraceEvent as
  `Collection/Start` and `Collection/Stop`, and a histogram's count is the interval's.
- **Web apps' data.** The Requests window reads only while someone looks; Migrations takes connection strings by
  environment only; Endpoints sends nothing but on Test or Send, and a send taking the person's environment asks first.
  An app's OpenAPI document is asked only at its origin (`OpenApiAddresses`), and what's made from one with no app asked
  comes from `AppDocuments`, never a process-wide cache. `ResourceFiles` is the one reader and writer of `.resx` and
  `xlf/*.xlf`, a table's change one edit of the open text. A configuration read is checked against the programs running
  its code (`ProjectSession.ConfiguredCodeOf`, `ProjectModel.ProgramsRunning`), never its own library's folder.
- **Debugging.** `DebugController`, `DebugSession`; Variables and the hover card are one list, a hover evaluating only
  inside the stopped frame's method (`MethodBounds`). A row's expression is the debugger's `evaluateName`, never built
  in the window. A library's types are shown as the App tells the debugger at launch (`DapTypePresentation`), never by
  engine code naming a library. An exception is read from what it stores, a getter or `ToString` only where that can't
  be. An HTTPS page's dev certificate is only read, never made or trusted. Every breakpoint through one pipeline
  (`IHitBreakpoint`). A program attached to in a container maps its paths by its Compose labels' Dockerfile
  (`ContainerReach.PathsAsync`), else back by the engine's own match (`SourcePaths.FileOf`).
- **Tests window.** `TestFinder`; tests are keyed by themselves (`TestItem.ById`), never a key string per test;
  `VsTestRunner` is the only file that knows VSTest's object model (VSTest starts a debugged .NET test host itself and
  asks to attach: `LaunchTestHost` is never called for one); the person's `GODOT_BIN` wins; GUT's tests are chosen by
  `GutRuns.Hook`, never `-gunit_test_name` or `-ginner_class`, which take substrings; what a read depends on is taken
  inside `TestFinder.ReadFile`'s parse, never captured by a listing. A case named in place of its method is a row from
  its source (`TestItem.NamedCase`), never by `NUnit.ClassName`, the declaring type; the Testing Platform's VSTest
  bridge says no file or line unless run settings ask (`TestingPlatformRunner.SourcesAsked`); run settings reach a
  Testing Platform host through `PlatformRunSettings`, never `--settings`. VSTest ends a discovery at the first assembly
  whose test host cannot start, so each it did not look through whole is asked again alone; an assembly a runner could
  not look through is an `UnfoundTests`, said wherever its tests are missed.
- **The rest.** `Packages` is every package change's one path (one command of Undo, put back when it fails; credentials
  only to their feed's host); under central package management Passenger writes each version where the evaluation keeps
  it (`CentralWrites`), never dotnet add package, which drops a props file's blank lines and turns a property to a literal;
  EF Core's own packages move as one set, another maker's provider only when asked (`MigrationsController.UpdateAsync`);
  `SolutionEdits`/`ProjectEdits` change files as text, never `dotnet sln`/`add reference`;
  templates drive what is offered, Passenger's config only layered on (`ProjectTemplates`); only an installed copy
  updates itself, a package manager's left to it; What's New from `docs/release-notes/X.Y.Z.md`.
- **MCP.** `PassengerTools` (every tool on the UI thread), `McpHost`, `McpProxy`, `AgentConnection`. A relay follows an
  update or, on Windows, ends, signalling the relay alone, never its agent's process group; `godot_editor` makes each
  change one `EditorUndoRedoManager` action.

Releases and the site: `ci.yml` and `release.yml` (from a `vX.Y.Z` tag, its body `docs/release-notes/X.Y.Z.md`, refused
while it is the stand-in) call `test.yml`: the gate on every system in two shards, and for CI the report lane too.
`site/` is passenger-ide.net, plain HTML and CSS loading only its own files (CSP 'self'), deployed by the owner by hand:
each page has its own title, description, canonical, Open Graph tags and `sitemap.xml` entry, a changed share card takes
a new name, and `just site-shots` retakes its images.

## Traps

- **Roslyn's server** is carried patched (`tools/Passenger.RoslynPatch`, `PatchRoslynServer`): until a carried build holds
  dotnet/roslyn#86130, every build since 5.12 starts Visual Studio Telemetry's session even with telemetry off, so its
  `CreateSession` returns null, the assembly is written IL-only, and the build fails on any other session start (#234);
  drop the patch once a carried build has the fix. After moving its version, delete `artifacts/bin/*/debug/roslyn*`, or
  the copy leaves a mixed server that won't start.
- **Roslyn's server 5.13** gives one half of a partial member's references (`RoslynSession.ReferencesAsync` adds both),
  writes a Razor component as `X_razor.g.cs` and `X_razor.decl.g.cs` (`@code`; read both, `SymbolNames.HintsOf`),
  restores after its load batch ends (a failed restore is said as heard), takes a project's assets as seen at load (a
  given build reading what changed since loads it again, `ProjectGiven.Stale`), streams a pull's results as named
  `$/progress`, and registers its watches as one brace of `**/` choices (`FileWatch` matches each alone), every watch
  under a folder it watches with subfolders folded into that folder's one (`ServerWatches` walks it for each project's
  build output).
- **Roslyn's server makes a C# file opened before its project loads a Miscellaneous File** (with no SDK, of no
  references), so a loading project's files open only once it loaded, a file come since once a load that read it ended
  (`CSharpService.Arrivals`), and an agent's code tool waits for every one its file's project and references hold
  (`SeenFrom`), never its own alone; references come from `CSharpService.ProjectsNow`, never the start-time `workspace`.
  A reload sent while it loads is one more load to wait for: its word that a load ended names none. A server ending
  during its start is started again, and StreamJsonRpc drops a faulted answer (`LanguageServer.IsDroppedAnswer`).
- **Roslyn's server takes file watching all or nothing**: offered it, it watches nothing itself. A deleted file reaches
  it only once its projects loaded again without it (`ProjectMethods.Deleted`): told first, it reads the missing file, a
  second each, before any answer needing the compilation (dotnet/roslyn#86236). A reload adds new files before it drops
  those gone, and the solution finds a path's documents ignoring case: a file renamed in letter case alone loads first
  without it (`Recased`), and a document found by path is checked with `PathNames.Same`.
- **Roslyn's server reruns every project's generators** a second after its projects load and for each 5 s batch of
  `.dll` changes under .NET's packs folders, which moves every project's version: solution-wide analysis drops a pull
  under way at the server's diagnostic refresh (`CSharpService.SolutionMoved`), and its first round ends at the
  extension's word that it analysed everything, never at the answer. A change under such a `**/*.dll` watch reaches the server only for a
  file a project references (`WatchedFiles.ReferencedAsync`, the extension's `ReferencedFiles`).
- **Roslyn's server keeps what it first read of a reference**, a failed read too, and waits 5 s after a change:
  references go through `ReferencesOnDisk` (the extension), a build's end through `CSharpService.Built`, which moves
  `LanguageServer.Changes` itself (`AnswersMoved`).
- **Roslyn's server cuts a completion list at 1,000 items and marks it incomplete** (`dotnet_lsp_max_completion_list_size`,
  never asked through `workspace/configuration`): Passenger's extension sets it uncut (`WholeCompletionLists`), so a name
  asks once and each key narrows here; without the extension every key asks again.
- **Roslyn's server passes over an option answered empty**, so an option goes back to its default only as the default's
  value (`CSharpService.OwnScope`), never null; a running server can't take an option left out.
- **Roslyn's server writes its MEF caches beside its DLLs**, so in a macOS bundle (a file added breaks the seal, #450)
  or a read-only folder it runs from a copy in `cache/roslyn/` (`RoslynServerFolder`); nothing a Passenger runs may
  write inside the app.
- **The debugger's engine.** `EngineLock` is not re-entrant; a read of the stop waits behind continue, steps, pause and
  disconnect. Never leave `_stepper` set but inactive (async stepping reads it as a queued StepComplete). Abort an
  evaluation past its time only where the abort takes, never rudely (one left pending fires later inside a lock, #238).
  Before letting a program go, run it off patch and step stops (CET would fault it) and wait out abandoned evaluations.
  Never decompile under the lock. Returned partway through a statement, a frame's IL offset reads as its start: seek the
  call across the whole statement. One assembly in two load contexts is two modules at one base address: key them by
  `ICorDebugModule`, bindings by `ModuleInfo`, never an address. After a Hot Reload update read a method's points,
  locals and IL at the frame's version. A runtime-async await (.NET 11) steps by a breakpoint a Just My Code stepper
  passes by, so a step over one turns it off. A func-eval's by-ref value with no home is a copy gone when the call
  returns: give a callee that may keep it a home (`PinnedArrayOfOneAsync`) or refuse; a struct the evaluation stores is
  its own copy (`OwnAsync`). `Task.NotifyDebuggerOfWaitCompletion`'s breakpoint is no GC-safe point: nothing runs there,
  and a value read there is boxed at its stop. A thread paused in a library method's P/Invoke (`Thread.Sleep`) shows that frame on top: a step from it ends only at
  the thread's next pass through that code, never once tiering replaced it, so it steps out to the user's code (#1359).
  Letting threads run voids every value read before, so an evaluation left
  running returns first (`LetAbandonedReturnAsync`), holding the program at the stop shown: never Continue there. One left
  is held with the rest (`process.Stop(0)`) until the person goes on, so "still running N s later" says nothing of how
  long it ran. ICorDebug walks code marked [DebuggerHidden] with [StackTraceHidden] (compiled code's helpers, a program's
  own; from .NET 11 the exception dispatch and entry-point call too), which the stack leaves out on every runtime: a
  stop's frames and top come from `EnumerateFramesForThread` and `TopFrame`, never a chain's own, and
  `thread.ActiveFrame` may be hidden code (#1503). Without Just My Code the runtime's stepper loses a frame's return into
  native code (a static constructor, .NET 11's CallClassConstructor): its step goes out of the hidden frame above the
  next shown one, which guards its other steps (`AboveShownCaller`, #1511).
- **Hot Reload**: the server's `EditAndContinueService` from the extension; update handlers through Passenger's hook
  (`UpdateHandlerHook`), never the runtime's agent, which refuses under a debugger; the hook connects by the system's
  socket calls, never .NET's Socket (whose first use starts a thread of .NET's on macOS and Linux), and makes its own
  calls of reading and answering before Main (the program's handlers are first found and called at the first update),
  so a stop between updates finds its thread blocked in recv: a thread caught partway through a first use may hold a
  stop's evaluations (#1495). CoreCLR lets no debugger go once
  Edit and Continue changed a program, so neither does Passenger (`DebugController.LetsNoDebuggerGo`: Detach refused, a
  service restarted, leaving the folder asks). A build's edits are kept for each program it starts later; a session
  takes edits only for its loaded modules' projects; `StaleCallers` blocks an update leaving loaded code calling a
  deleted body. A frame in old code is offered a remap at every statement, unstoppable: a running program is held
  through an update, its frames moving only at a statement's start where they hold the new closures (`RemapTarget`). A
  XAML edit reaches a running app through its inspector after C#'s (`XamlHotReload`), what the text before wrote of its
  Styles, Resources and root handlers cleared first (`XamlRoot`; filling adds to them, a duplicate key throws, and the
  app's own stay); C#'s bookkeeping follows `HotReloadOutcome.Code`; a file of an app's source places is told by
  `XamlPlaces.FileOf`, never by name; WPF's `x:Name` fields are found through `IComponentConnector` (its
  `SuppressMessage` is compiled out); an app keeps its build's XAML, so a closed file written on disk reaches it. dotnet
  watch replaces its app's `DOTNET_STARTUP_HOOKS`, so an Avalonia or WPF app's Run with Hot Reload is attached quietly.
- **dotnet watch takes Ctrl+R only from a console's keys**, never redirected input: the Run window's restart under it is
  Rerun, and a wait after the program ends is said (`BuildRunController.WatchedEnd`). A save's temporary file ends in
  `.user` (`FileText.SaveSuffix`), which `DefaultItemExcludes` leaves out, or dotnet watch hears it as added, reloads
  and loses the next edit.
- **The inspector** never touches `Dispatcher.UIThread` before the app makes it, never waits on Passenger, and readies
  its wire's types before the app's Main (System.Text.Json's lock against a thread the debugger stopped). An app the
  previewer loads needs `BuildAvaloniaApp()`, or HostApp aborts.
- **MCP's tool list** is paid for by every agent session (45.6 KB, held under 62 KB by McpDebuggingTests): say each
  thing once (shared parameters in `McpHost.Instructions`), add no tool another covers; an answer that can run long goes
  through `AgentAnswers` and says what it left out. `save_all` stays a tool of its own, never asked about on a person's
  window (#278). `debug_evaluate`, `debug_input` and `breakpoint_set` are `Destructive` and ask the person the first
  time; every question closes unanswered after 2 minutes; calls send progress every 10 s (OpenCode gives up after 60 s).
- **An await of a task the window's thread completes continues on the thread pool**, even with ConfigureAwait(false):
  what must happen where the answer lands is an awaited `ContinueWith(…, ExecuteSynchronously)`; an await of work that
  may have ended continues inline, so a view filling on its own turn awaits with `ConfigureAwaitOptions.ForceYielding`.
- **Work every window shares** (a cache of tasks, a process-wide gate) runs on the thread pool, never the asking thread:
  a headless window test's thread never runs again once its test is over.
- **A window the IDE opens of its own accord** (a notification, an agent's question) shows with `ShowActivated` false,
  or it takes the keys from what the person types; an agent's question is not waited on as a dialog and has no default
  button, so a stray Enter never answers it. The File Cache Conflict question is answered only by its buttons (Enter and
  Escape are Cancel), and a screenshot run says each file it wrote.
- **Agents never open windows on the person's screen**: `--mcp FOLDER`, or a headless screenshot run's `--agent` step
  for attached behaviour; never a bare `--mcp-attach` (headless, it keeps no settings and asks a person who isn't
  there), never `just run`.
- **Chrome and .NET's browser proxy**: Chrome runs a new-document script only once `Page.enable` came, so a page's
  conversation enables Page first, or nothing binds; an app-hosted `/_framework/debug/ws-proxy` binds once per app
  process to its first browser's port; with a fixed `--remote-debugging-port` Chrome writes no DevToolsActivePort; a
  getter the proxy reads only by itself is called through `Runtime.callFunctionOn` on its object.
- **Aspire's CLI and AppHosts** list pipeline steps only over the CLI's backchannel
  (`--operation inspect --list-steps true`, 13.6+); a real AppHost's `--operation publish` without it runs every step,
  deploy included: never in a probe or a test. They keep running AppHosts in `$HOME/.aspire/cli/bch` whatever
  ASPIRE_HOME says (on Windows in the profile; those tests skip there). Replicas share `OTEL_SERVICE_NAME` and display
  name: find a start's resource by `service.instance.id`, never by name, and key its hold, attach and recording by
  `ServiceRun.KeyOf`. DCP renames replicas every run and never tells their index, so a choice for one replica is every
  replica's next run.
- **Godot, running.** A headless Godot goes without SDL's HID driver (`GodotExecutables.HidGamepads`, given by
  `ChildProcesses.Command`): its search runs the main run loop as Godot ends, after the CameraServer is gone, whose
  observer any `CameraTexture` starts, doc generation included. It starts .NET through hostfxr with no startup hook, so
  Hot Reload, under Run too, goes through the debugger, never dotnet watch; its assembly comes from a stream (no
  `Location`). It writes logs and settings under the user's folders (on macOS whatever XDG says): a test gives it
  HOME/APPDATA of its own and always `--headless`. Windows' Godot .exe writes to no console, so every terminal run goes
  through its wrapper (`GodotConsole`), the debugger taking the game's own process. Its .NET host rolls forward only to
  released runtimes. A pre-release says 4.8 while its Godot.NET.Sdk is 4.8.0-beta.2: compare versions as numbers. A
  launch profile running Godot on the project (`%GODOT%`) is the game's configuration, its `--path` the launch's. A
  macOS export of an arm64 or universal game needs `textures/vram_compression/import_etc2_astc`. A headless game's
  window is 64×64 (the helper sets `display/window/size`); it has no picture, picks no 3D mesh without a collision
  shape, and never pauses again after Next Frame (the helper's `step` ends it).
- **Godot's headless editor** writes project.godot (a plugin's `ProjectSettings.save()` writes all it holds),
  `editor_layout.cfg`, `script_editor_cache.cfg` and `ide_messaging_meta.txt` into the project, imports and exports too,
  which `GodotScripts` puts back (a Passenger save meanwhile stays the person's); from 4.5 it runs `--recovery-mode`,
  but with a GDExtension its writes are put back only through its start, never where another program wrote
  (`GodotScripts.FolderChanged`; Godot's safe-save temp is `<file>-XXXXXX`, `<file><digits>.tmp` on Windows, and a
  change to project.godot counts as Godot's only where that temp was heard, `GodotSafeSave`). Run imports a project
  lacking `.import` files first. `script_templates/` is C# the build compiles unless removed; without a .sln Godot
  rewrites the csproj as it makes a C# script. On Windows a test's editor is two processes, both `--editor`.
- **Godot's wire.** `scene:inspect_objects`, `Object.get` and `in` all run a script's getters and `_get`, so the helper
  reads through `ClassDB.class_get_property` and `inst_to_dict`. A scan of Godot's flags stops at its separator
  (`EngineArguments`); a condition's truth is the game's (`GodotScriptBreakpoints.Truth`). Its debug peer takes only
  `[name, thread id 1, data]`; packed strings end with a NUL; `scene:inspect_objects` answers a few objects (inspect one
  at a time); a warning comes as an error with its flag set; its JSON reads every number as a float; from 4.7 an
  embedded game's state is 32 bytes and its pointer in points. Its language server takes whole texts, reads
  `Content-Length` at a fixed offset, needs `rootPath`, has no shutdown, and names a warning by `(NAME): ` to 4.7, by
  `code` from 4.8. Its script debugger: a message to a stopped thread carries its id; an `evaluate` in a frame with no
  instance, or a `break` while stopped, lets the game go on; an evaluation's errors come after its answer
  (`get_stack_dump` after each); `reload_scripts` keeps members by name, an added one null. IDE messaging: `Play` plays
  only the main scene, `DebugPlay`'s debugger fields are dead in Godot 4, `ReloadScripts` reloads the running games'
  scripts (GodotTools' watcher reloads the editor's own assemblies every 0.5 s); its completion kinds lack font sizes,
  so send the engine's numbers; an editor importing as it opens answers no handshake until done (`GodotIdeLink` retries
  after 10 s).
- **Godot's game.** Stop ends a game before its debug session: wait for the session too; a session is ended once its
  adapter says so or its process is gone after Exited without Terminated (`ProgramGone`). An agent's call on a game has
  one limit (`GodotGame.Within`); app_ tools wait for the first scene, no evaluation. The frame counter runs on while
  paused: count the helper's frames. Node selection, once set up, takes the game's clicks until turned off; 4.8 renamed
  `runtime_node_select_set_type` to `set_node_type` and a game passes over one it lacks (`GodotPicks.TypeMessages`); the
  Live tree's follow keeps its own count and never drops a refresh's answer. Godot reads the debugger's messages between
  a script's lines, so the helper comes in only once its live-edit path was sent (`GodotGame.AskAsync`), every call
  waits while its members are made (`asked`, `_made`), and a tree that lacks it brings it in again. A typed member
  refuses an untyped Array or Dictionary: send back the game's element types; a loaded resource comes by path, its id
  from the helper's `held`. A node instancing a scene no text says keeps the names below it unknown; a signal's
  parameters are known only where written and its emits agree; a connection's flags may bind the sender. Godot compiles
  a Shader only at `get_rid()`, with no warnings, stopping at its first error, passing over a failed preprocess: only
  the shader port's comparison test reads it. Godot finds a C# error's `at:` place and backtrace by counting frames,
  which shift once tier 1 inlines GodotSharp's `PushError`: the console keys an entry by `GodotConsoleFold.KeyOf`.
- **Godot's packages.** Godot.NET.Sdk reads `GodotProjectDir` in its props: only a full path set before `<Import …
  Sdk.props>` reaches its generators and output (`GodotProject.WhyNotBuiltFor`). Its own GodotSharp packages differ from
  nuget.org's of the same version, so `tests/GodotGraphs` never restores GodotSharp at a version a Godot ships (NU1403):
  GoDotTest's and GdUnit4's are `PackageDownload`s. 4.8's C# generators can't build a script class whose base an older
  Godot's built (godotengine/godot#124939), so GdUnit4 5.1.0-rc6's runner doesn't build on 4.8 (`GdUnit4Log`).
  `--doctool` writes no descriptions, so engine words come from `--dump-extension-api-with-docs`
  (`GodotClassReference`).
- **Godot's editor and the addon.** GodotTools' `_Build` runs before any addon's, so the addon sets
  `SkipBuildBeforePlaying` while Passenger builds. A game the editor's Play starts inherits its environment, names it by
  `--editor-pid`, is headless only through `editor/run/main_run_args`, and on Windows writes nothing to its stdout. An
  `EditorDebuggerPlugin` never captures Godot's own messages (`debug_data` does); `set_pid` comes before `started`;
  `var_to_bytes` writes an editor-held object as its holder's id (send `{"passenger:object": id}`); a script shows no
  progress while `_build` blocks. Match a session to its `ScriptEditorDebugger` by its tab (`add_session_tab`), never
  its place in the tree; relayed data stays a `List<object?>`; live-edit ids are negative, the editor's count from one.
  `get_unsaved_scenes` exists only from 4.7 (an older Godot's open scenes may be unsaved), and an inspected resource
  counts as held; reload only a scene Godot has open and saved. A static function named after an Object virtual (`_set`)
  fails the whole script; `remove_child` clears owners outside the branch, so a move or undone remove sets them again.
  `PASSENGER_ADDON_TRACE` prints each scene act.
- **Android**: .NET for Android turns an app's OutputType Exe into Library (`AndroidApplication` true, which runnability
  reads); its Debug host reads `files/.__override__/<abi>/environment`, where a Debug start puts the relay's variables
  through run-as and puts the build's link back after; `am set-debug-app` (kept only in the activity manager: clear only
  your own) keeps a long stop of the main thread from ending the app; the relay calls the DBI's `DllMain` itself, or
  `ICorDebug::Initialize` hangs; without the workload's platform (`platforms;android-37.0`) its device listing fails with
  XA5207. AndroidDeviceTests needs that platform, a JDK in `JAVA_HOME`, an AVD (booted `-no-window`)
  and the relays a build carries only after `just debug-relay android`.
- **Windows**: a folder a process works in can't be deleted, and MSBuild nodes start in their launcher's folder and
  outlive it, so hold a child whose descendants must end with it with `ChildProcesses.HoldFamily`; a write waits out a
  reader that shares no writes for up to 2 s, off the window's thread (`FileText.WhileHeldAsync`); closing a socket with
  unread input resets it, losing what was written (servers shut their side and drain first); the diagnostics library
  leaves a profiler pipe open after its listener goes (`ProfilingSession.CloseLeftListener`); a folder can't be renamed
  while any file under it is open, however it is shared, where a file opened sharing deletion can be (`DotnetMerge`
  moves what others may read file by file); what holds a file is `FileHolders`.
- **macOS**: an x64 Passenger under Rosetta loads MSBuild only from an x64 SDK (`DOTNET_ROOT_X64`,
  `/usr/local/share/dotnet/x64`), and MSBuildLocator finds its SDK by DOTNET_ROOT, so the host is given the chosen root;
  a zombie gives no process info (`DotnetProcesses.Runs` counts it gone); `SystemFonts` lists the hidden
  `.AppleSystemUIFont` only once loaded and `TryGetGlyphTypeface` answers an unknown name with Helvetica (`WindowFonts`
  checks the face); Chrome waits for good on a keychain where HOME has none (`--use-mock-keychain`); a hardened-runtime
  program without `get-task-allow` (Godot's release build) can't be dumped, so its snapshot comes from its GC heap dump;
  a variadic libc call through P/Invoke (`fcntl`, `ioctl` with an argument) loses its variadic arguments on arm64;
  `poll` never says a named pipe's writer has gone (`select`); a Unix socket path holds 104 bytes; `Screen.Scaling` is 1
  on Retina, so a pixel size goes with `RenderScaling`, and Avalonia's `PointToScreen` is in points whatever its
  `PixelPoint` says; `codesign` takes a dotted folder under `Contents/MacOS` for a bundle (so the web servers live in
  Resources); TMPDIR goes through the /var link, so build a test's project by its real path (two spellings restored at
  once collide).
- **.NET's watcher on macOS passes over FSEvents' word that it dropped events** (dotnet/runtime#136577), so a folder is
  watched only through `WatchedTree`, never a bare `FileSystemWatcher` (#607, #609); settings.json, watched alone, is
  read again on its watcher's error and as a window comes in front (`SettingsStore.LookAgain`).
- **macOS's "damaged" dialog** comes from reading or starting a Passenger.app whose seal no longer holds: a mounted disk
  image whose file was replaced (`just package-mac` ejects one first) or a bundle written into (#450). Tests never start
  a program inside an unsigned bundle, and a run's folder ends in `.noindex`, so Spotlight reads none of it.
- **Linux**: file systems tell letter case apart, so a test of another letter case skips there.
- **MSBuild**: a Remove glob (by its source an Include's or Update's too) is unescaped twice, an Exclude's or Import's
  once, and one escaped wildcard makes a whole glob a plain name fitting no file (`ItemPaths`); a build through another
  spelling of a project's path deletes what the first wrote (IncrementalClean; a shell or MSBuild takes an inherited PWD
  for its folder, #413, #537), and a reused node carries a project's earlier spelling and NuGet home into the next
  build. It keeps no idle node unless the process's nice value is 0.
- **Local History's `changes.log` is shared by every Passenger on the same state**: a store takes in the others' lines
  under `Hold` before it keeps anything, and a change logged twice lists once.
- **A variable taken out of a process's own environment goes through `OwnEnvironment.Set`**: on macOS and Linux
  `SetEnvironmentVariable` never changes the process's own, from which Porta.Pty forks the shell. Porta.Pty drops an
  empty value, so a removed variable (an outer tmux's or screen's too) is unset through `env -u`
  (`TerminalSession.Unsetting`), which on macOS loses `DYLD_*` (SIP), so those go under `TerminalSession.KeptPrefix` for
  sh to set again, never as env operands.
- **The carried schemas hold Passenger's patches** (`WebServers/schemas`, MSBuild's xsd), listed in the notices and each
  file's `$comment`: a fresh upstream copy loses them (CarriedSchemasTests checks defaults).
- **The terminal reuses a line** once it leaves the screen and a full scrollback: nothing keeps a `TerminalLine` past
  its line number; cells are written only through `TerminalLine.Writing()`. A pseudo-terminal's close on macOS waits for
  a write in flight, and an exiting session leader waits for its output to be read: `TerminalSession` reads to the end
  and closes off the window's thread. macOS's bash 3.2 sometimes prints "child setpgid (N to N): Operation not
  permitted", so a test reading a shell's lines allows others between them.
- **ConPTY** (Porta.Pty's OpenConsole and Windows' own) passes a program's kitty keyboard requests out and CSI u keys in
  as written, but gives a VT-input program a win32-input-mode key (`?9001h`) as xterm's bytes, Shift+Enter a bare `\r`:
  a key's modifiers reach a program by the kitty protocol (`TerminalKeys`).
- **Avalonia 12's lists** re-attach, and so re-style, every recycled container and never reuse a `DataTemplate`'s
  content: rows go through `ReusedRows`, made once with their containers (UiRulesTests).
- **Avalonia 12.1 counts a run of clicks per button**: a press of another button starts a new run (`ClickCount` 1); a
  control with no background is no pointer target.
- **Avalonia 12's ListBox re-points its keys' row by the chosen row's new index before moving rows**, so a focused row
  loses the keys when rows come above it, and moves a Shift range's anchor only with its first chosen row: every tree
  goes through `TreeKeys.Attach`.
- **`GC.GetAllocatedBytesForCurrentThread` counts the unused rest of a thread's allocation context** when a background
  collection's mark ends, up to 8 KB never allocated, so the unit tests run every collection blocking (#1355).
- **`ArrayPool.Shared` loses a thread's arrays at a Gen 2 once load reaches 81% of memory**, so a `Rent` on a path held
  to 0 bytes can allocate: such a path keeps a per-thread array (`FuzzyMatch`), a conversation its own message buffers
  (`ContiguousReader`).
- **`FormattedText` lays its lines out again at every draw**: text drawn frame after frame is kept as a `TextLayout` or
  `TextLine` until what it says or its width changes. Avalonia's text formatter returns the pooled arrays of runs it
  shapes together only when finalized, so runs of one line are taken from two copies of its text in turn.
- **Avalonia's font fallback sweeps every loaded font in name order before asking the platform** (AvaloniaUI#22464), and
  never calls a `FontCollectionBase`'s own `TryMatchCharacter`: `FallbackFonts` is an `IFontCollection`. Apple's fonts
  shape by `morx`, which `CanShapeScript` doesn't count.
- **Svg.Skia stays at 5.1** while Avalonia's Skia is SkiaSharp 3 (5.2 needs SkiaSharp 4); its `SKSvg` disposes its
  picture, so a picture is never let go while a frame may still draw it.
- **Avalonia renders a visual again when its Bounds, ZIndex or RenderTransform change**, and on `InvalidateArrange`:
  retained content moves through its parent layer. Two visuals invalidated in one frame draw again all that lies between
  them: invalidate only what changed (`CodeEditor.RefreshStripe`).
- **EventPipe from a program's start**: .NET 10's LoggingEventSource misreads its `FilterSpecs`, so a category takes no
  `:Level` there (`LiveRequests.LogsSpec`). Its sampler takes a thread only where it can stop it, so a test program's
  method that must own its samples spins in a loop that calls nothing. A program the debugger holds answers no session's
  start or stop and gives no heap, so the profiler refuses it (`Refusals.PausedInDebugger`). A session enabling a managed
  EventSource as a program before .NET 11 RC starts hangs it for good (dotnet/runtime#119014): every live session starts
  through `SessionStarts`, which waits out `ProgramStarts`.
- **A runtime's events heard in-process**: an `EventListener`'s session holds 10 MB and drops what overflows unsaid, and
  the GC keyword at Verbose bursts at every collection, so a test that must hear every event opens a session of its own,
  sized and filtered by event id, and fails on TraceEvent's `EventsLost` (`FlingTests`). TraceEvent holds a thread's last
  events of a flush until a later one the runtime marks oldest comes: a wait on the last event makes one on a new thread.

## Tests

Tests drive the IDE whole, headless (`tests/Passenger.Tests`, xunit v3 on Avalonia's headless platform with real Skia):
a window or an editor, real keys by Rider's macOS keymap (`EditorRig`), typing, clicks, files on disk. Pure logic (no
window, process, network or clock; a temporary folder only where it is about paths) is unit tested in
`tests/Passenger.UnitTests`, each test milliseconds and the same every run, seeded where random; an end-to-end test that
checks only such logic gives way to unit tests. A test's name says what holds; its failure message names the
consequence.

**The gate** (`[Gate]`, and every unit test) blocks CI and a release: a test enters it only with a clean record on CI on
all three systems, no measured time or allocation bound and no container, real-Aspire or Node.js need, guarding a core
flow; one that fails there for a test's reason leaves it until rewritten. Every other test is in the report lane, whose
failures never fail a run but each become a `ci-report` issue. `BannedSymbols.txt` bans driving internals directly (the
terminal's emulator, a hand-started Roslyn or profiling session, a lone `TextDocument`, `LineDiff`, `CommitGraph`,
`Snippet.Parse`; `Rig.cs` excepted), raising Click events (`Ui.Click` instead), fixed sleeps (`Thread.Sleep`,
`Task.Delay`, `Screenshot.Pump`) outside `Waiting` and `Ui`, and the process's heap total (`GC.GetTotalMemory`, which
counts what classes beside let go of: what an object keeps is `HeldBytes.Of` it), and `Process.Kill` (a process the test
started ends through `ChildProcesses.Kill`, one found by its id through `OwnProcess`, taken while it is known to be the
test's own: a dead program's id may be another's). The long random editing session checks
every key against a string model, soft wrap too: keep it passing, never narrow it. Performance is held by what the UI
thread allocates in a small case against a large one, never by a time (attach the profiler to the test process to see
its time, `ProfilingSession.AttachAsync(Environment.ProcessId, …)`). Tests that press thousands of keys draw a small
window (a headless frame is GC pressure); a measure of a key's bytes collects first (LongTextTests).

**Waits (#237).** A test waits for what it checks, however slow the machine. `Ui.Settle` (and `Press`, `Landed`,
`WaitFor` on it) waits until the IDE is quiet: `BackgroundWork` landed, nothing in `InFlight` but Roslyn's held
workspace pull, due timers fired, the last layout drawn. Not waited for: a running program, the state store's 0.5 s
write delay (flush to read a state file), templates read ahead, the watcher's delay (wait for its `Changed`). Words
describing the window as a wait gave up are a `Func<string>`. Wait with `Ui.WaitFor`, `Ui.PumpUntil` (the loop run, not
settled), or `Waiting.Until`/`Within` off the UI thread; read a task's answer once it ended (`Ui.Await`, `Landed(task,
what)`; `Ui.Idle` only where the window must run nothing); start a test's own work through `Waiting.Apart`; time that
must pass is `Ui.Hold(time, why)`, `Ui.Stall` or `Waiting.For`. Every limit is a `Deadline` (its clock stops while the
machine sleeps), never the wall clock, of a developer machine's time scaled by `TestRun.TimeScale`
(`PASSENGER_TEST_TIME_SCALE`; CI 2 on macOS, 2.5 on Windows, 2 on Linux); the IDE's own waits scale by
`PASSENGER_WAIT_SCALE`. `FLAKES.md` records each CI failure's cause.

**Isolation.** A run keeps to its own folder (`TestRun`): state, logs, sockets, test folders, shells' history, an empty
Git configuration (no ssh-agent; its own GnuPG home and keys), NuGet's home on macOS and Linux, MSBuild's node salt,
container labels, user secrets, dotnet new's templates, Aspire's HOME and CLI, Godot's user folders
(`PASSENGER_GODOT_HOME`); `PersonsGodot` fails a run that writes the person's Godot folders. Its themes come from the tests' copy (`PASSENGER_THEMES`). It never reaches nuget.org
(local feeds, the run folder's feedless nuget.config `TestRun.Feedless`, the restore-only `tests/*Graphs`, which name
every package their test apps resolve, at its version: a CI cache restored by key prefix hides one missing), GitHub
(`PASSENGER_RELEASES` dead), registries (`PASSENGER_BUILDKIT_CHECKS=off`), Docker Hub, symbol servers, Discord, the
person's Passenger (`PASSENGER_START=headless`), or a host a browser asks of its own accord (`PASSENGER_BROWSER_PROXY`).
It shortens `PASSENGER_EVAL_TIMEOUT_MS`, `PASSENGER_WATCH_LATER_MS` (3 s each), `PASSENGER_MCP_PROGRESS_MS` and
`PASSENGER_REGEX_TIMEOUT_MS` (1 s each); a test holding a server to not answering sets that server's limits, never the
run's. After each window test its windows close and what they started is waited out (`TestsLeaveNothingBehind`); each
takes its own dispatcher (`UiThread.Own`). A plain `[Fact]` using `HeadlessUnitTestSession.Dispatch` must not end on the
session's thread (`await Task.Yield()` before throwing). `just test` keeps the Mac awake for its run.

**Collections and shared projects** (`Collections.cs`). Window tests run one at a time on the session's thread; beside
them only `DebugSession.Collection` and `Beside` classes (no window, nothing shared changed, nothing held to a time). A
class with a test held to a time, or that debugs through the window, is `[Collection(Alone.Name)]`, run last alone. A
class sharing one project derives from `SharedProject` (one warm server; a new file through `project.Write`, which ends
the server unless `loadsAgain`; `PrepareAsync(restore: false)` where nothing reads a restore, `byRealPath: true` where
projects name each other by real path). xunit v3 calls only `DisposeAsync` on a class with both. `RestoredFolders` hands
a later test the folder the first taker's `write` made: every test of a class writes the same. A test asking nothing of
C# opens without the language server (`languageServer: false`).

**Runs.** `just test` deals the classes outside Alone to N processes by `TestTimes.json`, then the Alone classes; CI
deals each system's gate to two shards and its report lane to four. Tests hold to no time budget (the owner's choice): a
class taking far longer than its work needs is a bug, never held to a limit. A test needing an MSBuild node waits for
`Processes.MSBuildNodesCounted` or skips saying why. The VMs: Windows on Arm
`~/Documents/passenger-vm/vm-test.sh NAME FILTER` (no Node.js; it can't see dot folders), Ubuntu 26.04 ARM64
`vm-test-linux.sh NAME FILTER`.

**Helpers** (`Ui`, `Waiting`, `TestProjects`, `TestFolders`, `McpRig`, `Docker`): a click is `Ui.Click` (a real press on
a control shown, enabled and hit); a chord one `Ui.Press(window, "control M , control S")` (its first key forgotten
after 2 s); multi-line edits `Typing.Replace` (`KeyTextInput` drops `\n`); a server's request or answer held
`LanguageServer.HoldNext`/`HoldNextAnswer`, a container run's `up` `BuildRunController.HoldNextUp`; a log the IDE may be
writing `Ui.Shared`. A dialog is held to `DialogButtonsTests.Check`, the window to UiRulesTests. A helper that runs a
program waits on the UI thread with its loop running; a fixture or windowless test awaits its `…Async` form. A stand-in
leaves files a target writes as it found them (a new one makes MSBuild skip the target). A headless click runs queued
work and draws after itself, so a status during a click is read from what it showed throughout; a count of a key's
frames sets aside the caret's blinks.

**What a test needs, or skips saying why without:** a Linux container engine (label everything with `Docker.RunLabel`,
remove dead runs' leftovers, never prune or start or stop Docker Desktop, pull nothing large: a test needing an image
the engine lacks names it and skips), Node.js (web languages), Postgres's image, `PassengerAspireRuntime=true` (real
AppHosts, about 900 MB), Windows (WPF's preview and inspector, data breakpoints on x64). A new SDK fails WpfXamlTests
naming the `WindowsDesktopRefVersion` to pin.

**Debuggees** (`tests/Debuggees`): tests find their lines by code (`Debuggees.Line`), so changing a line's code means
changing the tests that name it (a breakpoint binds the statement that starts first on its line, inside a lambda only
where the line starts in one: give the lambda's column). `SelfDebuggingTests` finds its breakpoint by the code `Say(why,
RefusalPlace(action));` in MainWindow.Actions.cs. `StartHold` holds a launched program at its start;
`PASSENGER_ENGINE_LOG=FILE` keeps the engine's log. A thread a debuggee starts just before a stop can hold that stop's
evaluations: stop once it has run a round (#599). An evaluation past its time logs `WhereThreadsStand`, and a test of
one passes `mayStand`.

**MCP tests** are agents on the SDK's own client over `McpRig` (`McpTests` and per-area classes on `--mcp`,
`McpWindowTests` and `McpAttachTests` through `--mcp-attach`); each checks a tool's answer, leaving the window's steps
to the window's tests.

## Code style

- C#: four spaces, file-scoped namespaces, braces on every block, explicit types over `var`, accessibility always
  written; the build fails on each (IDE0161, IDE0011, IDE0008, IDE0040).
- A comment speaks only of what it sits on, never how something else works, and says why, never what the next lines do.
  A file's or type's comment is at most 10 lines, a member's 5, and one inside a body 1 line, there only for a reason
  the code can't show (`just comments`).
- XAML: four spaces; one style per selector in `Theme/Shell.axaml`, restyling Fluent's states on its
  `PART_ContentPresenter`. Shell.axaml's base styles give every Button, TextBox, ComboBox, NumericUpDown, CheckBox and
  MenuItem its look (`Passenger.ControlHeight` 28; radii `ControlRadius` 4, `ToolRadius` 6, `PopupRadius` 8); a class
  style says only how it differs, and code names a class from `StyleClasses`, never as text or as a marker no style
  takes (UiRulesTests). A style on a template part Fluent sets under a property trigger (`^[AllowAutoHide=True]`) needs
  a trigger of its own, or it ranks below Fluent's.
- A visual inconsistency is fixed once, in a shared place (a token, a base style, a helper), never per control:
  Passenger's own consistency settles a design question. The shared pieces: `DialogFooter` (`Of`, `Listed`, `Answers`,
  `Bar`, `Page`, `Default`, `Cancel`, `OkCancel`, `Headed`, `Popup`) in a `Border.footerBar`; `Border.framed`;
  `Passenger.Separator`, `Passenger.Divider`; `MenuChoice` (`Holding`); `ListMenus.PlaceForKeys`; `ToolTabs.Tab`;
  `PopupFrame` (`Framed`), `popupTitle`; `ListRow.Height` (24); `TextBlock.emptyText`, `EmptyState`;
  `ToolButtons.Of`/`Toggle` (never focusable), `Tip` (an icon's tip and its accessible name), `Grey` (greyed, its tip saying why); `ColumnTitles.Of`/`Row`; `ToolBars.OverEditor`; `Banner`; `CheckRows`;
  `MutedText`; `ListKeys.Move`; `Plural.Of`/`Listed`; `Border.kindBadge`/`tag`/`box` (never a literal radius);
  `RunRows`; `DetailsTables`; `MergedLog` (`OutputView.Find`); `ZoomedPicture`; `ToolWindow.ExpandAll`/`CollapseAll`;
  `ToolWindows.FilterBox`; `ToolFlow`; `PaneSplit`; `ToolColumn`; `ControlThemes.axaml`; `FocusRing`; `AppIcon`;
  `ReusedRows` (`Use`, `Showing`); `RowIcon` and `RowGlyphs`, drawn as `PathIcon` draws for a fraction of its cost,
  never `PathIcon` (UiRulesTests). A row hears its item only while shown (`Observable.FollowWhileShown`).
- `MainWindow.SmallestWidth` is 640: the layout works at half a 1366-wide screen, its least size that times the display
  scale (`IdeWindow.HoldAtLeast`); every window is an `IdeWindow`, which fits its owner's screen as it shows and as it is
  rescaled (`IdeWindow.Rescaled`).
