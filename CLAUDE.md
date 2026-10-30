# CLAUDE.md

Guidance for Claude Code in this repository: the rules, the commands, where things are and the traps the code doesn't
show. For how something works, read the code; Passenger's own MCP tools (below) are the quickest way. Every session loads
this file, so keep it short: a change adds a rule, a trap or a pointer where one is missing, in a line, never a
description of what the code does.

## What this is

**Passenger** is an IDE for C# and modern .NET (CoreCLR only: no .NET Framework, Mono or Unity; the one exception,
Blazor's WebAssembly half debugged through the runtime's browser debug proxy, #586), written in C# on .NET 11 with an
Avalonia 12 UI, for Windows, macOS and Linux. v0.2.2 shipped; the work now is bug fixes and code cleanup only, no new
features, on main toward 0.2.3 (https://github.com/Zaneris/passenger/milestone/5), features waiting in the Backlog
(https://github.com/Zaneris/passenger/milestone/2). Each issue says **Now:**, **The aim:** and **Done when:**; read it
before anything larger than a fix, and close or tick it when it lands. A new feature is the best Passenger can make it,
never a copy of another IDE's; only its keys follow Rider's keymaps (their files are in the jars of
`~/Applications/Rider.app`).

## Commands

```sh
just build                 # Debug build of Passenger.slnx; warnings are errors
just run [PATH] [args]     # the IDE on a folder or file (--file F, --caret L:C, --theme dark|light)
just screenshot X.png ...  # the main window drawn off-screen to a PNG, after the steps given
just test [filter] [N] [--gate|--report]  # tests whose name matches (one lane, or both), in N processes, then the
                           # Alone classes (tools/test-run.py); results and logs in artifacts/test-results/local;
                           # --plan shows the deal
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

**Look at UI changes** with a screenshot at `--scale 2` before calling them done: headless (real Skia, never a window or
the focus), it opens what the command line names, takes its steps through real input and waits for background work:

```sh
artifacts/bin/Passenger.App/debug/Passenger . --file src/Passenger.App/App.axaml.cs --caret 12:5 \
    --key enter --type "var x = 1;" --key "shift+meta+left" --click 400,300 \
    --screenshot out.png --size 1400x880 --scale 2 --crop 320,76,900,500 --theme light
```

Steps: `--key`, `--type` (one input, so completion opens only on a trigger typed alone), `--click`, `--right-click`,
`--hover X,Y` (logical units), `--click-name NAME` (exit 2 if none), `--drag[-hold]`, `--wait MS`, `--window-buttons`,
`--agent COMMAND` (an agent in the Terminal's Agent tab, every tool allowed); a step after one that opens a dialog goes to
the dialog. State is kept only where PASSENGER_SETTINGS names a folder (short on macOS: a socket path holds 103 bytes).
Headless, dispatcher timers fire only inside a running frame (`Screenshot.Pump`, `Ui.WaitFor`).

**Build facts.** Everything builds under `artifacts/`. Package versions live only in `Directory.Packages.props` (each
kept pin saying why), shared properties only in `Directory.Build.props` (the version too). Packages come from nuget.org
alone, as it signed them, at what each project's `packages.lock.json` pins: CI restores locked, so commit a rewritten lock
file with its change. Every lock file holds all eight `RuntimeIdentifiers`, even a Windows-only project's (a publish's
RID reaches every referenced project's restore). Every recipe and CI set `AVALONIA_TELEMETRY_OPTOUT=1`.

## Rules

- **Never Microsoft's debugger (vsdbg) or anything of C# Dev Kit**, including its pieces inside VS Code's C# extension
  (`.debugger`, `.roslynDevKit`, `.testDiscovery`), nor `VSCodeDebugProtocol`; nothing is copied out of an installed
  extension. Roslyn's server comes from NuGet; `Passenger.Dap` is the protocol.
- **No Avalonia Accelerate** (TreeDataGrid, DevTools: `DeveloperTools.TakeOut` removes what `dotnet new avalonia.app` adds).
- **Every dependency, icon or asset has an open-source licence and an entry in `THIRD_PARTY_NOTICES.md`** in the same
  change (`just notices` and CI fail on a package missed): a package by NuGet id with its licence and copyright as
  stated, copied source by file and origin, assets after the marker line, a new SPDX text in
  `tools/Passenger.Notices/Licenses`, a package inside Roslyn's server in its table. The owner's exceptions: Visual Studio
  Telemetry and Utilities.Internal inside Roslyn's server (its Razor support needs them), and the TextMate bundle
  licence. MIT first.
- **Every program Passenger starts goes through `ChildProcesses`** (`Command`/`DotnetCommand`, then `Start`,
  `StartAsync`, `RunAsync`, or `AnswerAsync` for a question one line answers; `Kill` ends it with all it started), so
  none outlives the IDE (`Lifeline`), each given its folder as the disk names it, PWD too; a child's lines are heard
  through `ChildProcesses.Hear`, never Process's `Begin*ReadLine` (banned: its disposal faults a read under it). Started
  elsewhere: the terminal's shell (Porta.Pty), vstest.console, Velopack's Update.exe and an agent's `--mcp-attach`
  Passenger (`StartApart`); a program run in the terminal is `Adopt`ed. Passenger's own standard input is taken at its
  start (`StandardInput`, fd 0 then the null device), so nothing it starts reads it.
- **No secret on a command line** (any process can read it): give it through the environment, or don't make it. Secrets
  everywhere (Details, logs, consoles, hints, agents' answers, `ChildProcesses.Shown`'s echoes) are masked by one rule,
  `ContainerSecrets`.
- **Containers are ended by label** (`ContainerLabels`: state, machine, process, folder), never by `down` or
  `--remove-orphans`, which take the person's own: a folder's end removes what this process started there, the
  watchdog's last command all it started, the next start what a dead process of this state and machine left. Aspire's
  unlabelled DCP containers and network are recorded in `AspireContainerOwnership` and ended the same way; persistent
  containers and the person's own never are.
- **Colours are tokens** (`Passenger.*` in `Theme/Tokens.axaml`), given in every theme (dark and light, `Harbour.axaml`
  and each look `just themes` writes); Fluent's keys are mapped once in `Theme/FluentKeys.axaml`. UiRulesTests fails on a
  colour or non-zero CornerRadius written outside `Theme/` (but the content's own) and on an unmapped Fluent key.
- **An identifier is named from its home** (`[IdentifierHome]`: action and tool window ids, DAP and LSP names, `McpTools`,
  each `PASSENGER_` variable's const, the theme's keys as `TokenKeys` and `IconKeys`, generated from their XAML): PSG0001
  (`src/Passenger.Analyzers`, every rule's id and words in `Rules.cs`) fails its text written anywhere else, a plain word
  only where a parameter or member marked `[IdentifierOf]` its home takes it.
- **Async is awaited**: no async void (PSG0002), no async lambda given to a void delegate (PSG0003), no blocking wait on
  a task (PSG0004; a read where the task has certainly ended passes); a caller that must stay synchronous (Main's end, a
  library's synchronous callback) goes through `Synchronously.Run`, saying why.
- **Work for the thread pool goes through `BackgroundWork`** (`Run`, and `Debounce` rather than a `DispatcherTimer`), so
  screenshots and tests wait for it; `Unheld`/`UnheldThread` for work that mustn't hold them, saying why (PSG0006; a
  test's own through `Waiting.Apart`). **A catch-all is a boundary** that passes on what it caught (PSG0005); a catch
  around the runtime's interop takes what it can throw, `Failures.OfRuntime` (`OfReading` for files and metadata).
- **A malformed flag or environment value is fatal**, never a silent default (`LaunchOptions.cs`).
- **A warning is fixed at its cause.** Turning it off (NoWarn, a pragma, a lowered severity, a suppression) or moving
  the line it checks (a raised budget or limit) does not count as fixing it; neither does an analyzer's baseline.
- **Work nothing awaits goes to `IdeErrors.Observe`** (`Core.UnawaitedWork.Observe` below the App), a fault no one is to
  hear to `UnawaitedWork.Dismiss`, a wait however it ended through `task.Ended()`, never a bare `_ =` or `ContinueWith`
  or a call left as a statement, so its fault is said as it happens: IdeErrorsTests reads the built IL (`DroppedTasks`).
- **One threading rule.** Controls, documents, the window's `ProjectSession` and its parts, controllers and views belong
  to the UI thread (`UiThread.Check()` in entry points). Text, Vcs, Workspace, Packages, Lsp, Dap, Debugger and Profiler
  never reference Avalonia and raise events on any thread; Editor, Languages and Terminal on the UI thread. The App
  crosses from another thread only through `UiThread` (`Post`, `Marshal` for a library's handler, `InvokeAsync` to wait
  for an answer).
- **Paths are canonical** (`PathNames.Canonical`, symlinks resolved, as MSBuild and Roslyn report them); compare with
  `PathNames.Comparer`/`Same`/`IsUnder` (a folder asked whether it tells case apart only for names alike but for case),
  names in one folder with `NamesIn(folder)` (`CaseIgnoredIn`), never `Comparison` for a path, walk a project's folders
  with `FoldersUnder` and the open folder's files with `FilesUnder` (`IsSystemFile` passed over). Show one to a person
  with `PathNames.Shown`.
- **Lines differ by reader.** Roslyn's server and compilers end a line at a lone '\r', U+0085, U+2028 and U+2029, an XML
  reader (XAML's compilers, BAML, XamlIl) at a lone '\r' only. Every place crossing to a server, compiler, PDB or running
  app goes through `ServerLines`/`ServerPlaces` (`Compiled`, `CountedByRoslyn`, `PositionOfCompiled`), never `new
  TextPosition(line, character)`; the debugger's lines through `Debugging/BuiltLines`. MCP's places are the editor's.
- **Writes go one way.** A workspace file: `FileText.Save` (atomic, through symlinks, permissions, Windows attributes,
  ACL and streams, macOS metadata, and Linux's extended attributes, ACL, owner and group where it may kept
  (`UnixFileMetadata`, `AtomicFile.Write`'s too), a file another hard link names written in place, a read-only file
  refused, Local History given the old bytes). A file the IDE makes: `FileText.SaveNew` (its .editorconfig's end_of_line
  and charset), one written again `FileText.SaveOver`, a workspace file the window changes itself
  `MainWindow.ChangeFileAsync`. Any other whole file: `AtomicFile.Write`. Never `File.WriteAll*`. A state file only the
  person may read goes through `OwnFiles` (`Folder`, `Write`, `Create`). Passenger saves an open project, solution,
  solution filter, nuget.config or Directory.Packages.props before writing it and loads what it wrote after
  (`SaveBeforeWriting`, `FollowWritten`). A change of several files, or one that moves a file, is one command of global
  undo (`EditorArea.Commands.Record`), and the window follows only what a change of files made, moved or deleted
  (`Following`, `CameOrWent`, through `ProjectSession.CameOrWent`, which lets go of a failed evaluation where its file
  comes back), never a whole re-read of the folder. Server edits pass `LineEndings.Fit` then `Indentation.Fit`. A save
  Windows refuses while another program holds the file lands in the background, in order (`FileText.Landed`;
  `FileText.Read` gives its bytes meanwhile), and what hands the files to another program (a build, Git, a restore, an
  agent) waits for it (`SaveAllLandedAsync`); a change that writes a file not open (several files, a fix, Undo and Redo)
  says it is done only once its own writes have landed (`WorkspaceEdits.LandedAsync`, `EditorArea.LandedAsync(paths,
  …)`), every command waiting inside its recording (`Recording.LandedAsync`, `WorkspaceEdits.LandedAsync(recording, …)`:
  the open files' changes taken first, a file never written left out; `TypeRename.Apply` and `ApplyAsCommand` take the
  caller's), the window's words through `UnlandedAsync`; a command that waits between its writes records with
  `Commands.RecordWrites`, so only what it wrote is its own; its moves go through `Recording.Move`, never a bare
  `EditorArea.MoveFile`, which no recording follows; every write of closed files goes in turn through
  `CommandHistory.InTurnAsync`; a closed file changes through `Recording.Change` (or `Wrote` for texts it holds),
  written off the window's thread by `Recording.WriteAsync`, the window following once they are written
  (`AssistContext.ChangeFilesAsync`, `MainWindow.FollowingAsync`); Undo and Redo put closed files back off it
  (`CommandHistory.BlockedAsync`, `PutAsync`), a command's `Part` (its put a task: a long `First` put runs off the
  window's thread while the rest is checked again, `ChangedSinceFirstAsync`) saying why it could not be put and one the
  disk may refuse going `First`; one refused partway takes back what it did (`CommandHistory.TakenBackAsync`; a
  recording's `Recording.TakenBackAsync`, which `WorkspaceEdits.LandedAsync` and `WrittenAsync` call), and a file a
  command deletes is kept for Undo (`Recording.Deleted`). A file shown read-only refuses every change to its text
  (`TextDocument.IsReadOnly`, set only by `OpenDocument`), and an action on it greys with `Refusals.ReadOnly`.
- **State is a `StateDocument`** in the one `StateStore` (versioned `Upgrades`, `Clamped`, written debounced and merged,
  a bad file set aside as `.bad`), read off the UI thread; never a file of its own. Per-folder state lives under
  `workspaces/<name>-<hash>/`, held by each window with the folder open (`StateStore.Hold`/`LetGo`); its writes hold a
  lock file every process honours (`StateFile.Hold(path, limit)`, Local History's too); a failed write is tried again,
  and a key a later build renamed moves in `IStateDocument.Renamed`; a value of the wrong kind is passed over alone
  (`StateFile.Read`). A new setting: an `AppSettings` property, its row and range declared in `Declared` (a value it
  refuses as `Refused`, which Settings and `settings_set` both ask) and `Add`ed in its place in `SettingsDialog`, a line
  in `MainWindow.Follow` or `SettingsStore.InForce`.
- **Every action is an `IdeAction`** in the window's `ActionRegistry` (ids and keymaps in `AppActions.cs`): keys, menus,
  Find Action, context menus and MCP's `ide_action` all run it, `WhyNot` greys it with why, titles come from the
  registry (`TitleWhere`), a title that asks more ends in "…", and what's missing is said in `Refusals`' words
  (RefusalsTests fails a copy). Anything that takes a while holds a `Working` (`ActionWork.Begin`) from its first moment
  to its hand-over, saying each stage and honouring `Stopping`, and tells what it came to (`Working.Tell`,
  `ActionWork.Tell`), which `ide_action` answers; toolbar buttons bind with `ActionButtons.Bind`. An agent's run
  (`ActionRegistry.RunForAgent`, `ActionContext.ByAgent`) never takes the person's keys or brings a window forward
  (`DockHost.ShowBehind`); an attached agent's flow is marked by `AgentAsks`, whose `Install` cancels its focus moves
  and refuses its popups and modal dialogs (`Refusals.AsksThePerson`; a popup host asks `AgentAsks.Refused`/`Popup`),
  and a file it opens comes in a tab behind (`EditorArea.Activate`).
- **One way out to the system:** a browser only through `MainWindow.Browse`, the file manager only through
  `ShowInFileManager` (both recorded for tests).
- **Shortcuts follow Rider's**: its Visual Studio (Windows) or Visual Studio OSX (macOS) keymap, and its VS Code keymaps
  for that preset, resolved as IntelliJ does (own list, then plugins' descriptors, then the parent); a keypad key Rider
  binds beside a main key is bound too. A key IntelliJ gives two actions by place goes to one, said where it's bound;
  the place takes it through `AppActions.KeysIn`. Mouse gestures too: `Keymap.Bind` takes Rider's mouse notation (`meta
  shift button1`), an editor asks its window's keymap (`CodeEditor.GesturesProperty`) and names no IDE action, and the
  side buttons run window-wide (`IdeKeys`).
- **Workflows run only by hand** (the owner's choice): never `gh workflow run` unless the owner says. Commit messages go
  through a file (`git commit -F msg.txt`): backticks in a `-m` string are zsh command substitution.

## Where things are

Projects in `src/`; `find_symbol` finds the rest:
- `Passenger.Core`: what everything shares, no UI: `ChildProcesses` (`StartAsync` forks off the UI thread: a fork costs
  a large process tens of ms), `Dotnet`/`Programs`/`ShellEnvironment` (tools found as the login shell would,
  `ShellEnvironment.LoginShell` the environment's and the terminal's, every PATH search `Programs.OnPath`/`AllOnPath`;
  the installation's folder is `Dotnet.Root`, from the host's real path, never the folder of `Dotnet.Host()`),
  `Dotnet.CliHome` (the CLI's home, after the login shell), `OutboundHttp` (every request Passenger makes of a host),
  `PathNames`, `AtomicFile`, `FolderMoves` (a folder moved whole, across volumes too), `SocketFolders` (every Unix
  socket), `Libc` (a C library call more than one place makes, declared once; the App's `MacObjC` and `Xlib` likewise),
  `InFlight`, `BackgroundWork`, `DotnetProcesses.Runs` (whether a process runs: never `HasExited`, which on Windows
  reads one this user may not open as ended), `EnvironmentValues.Milliseconds` (every PASSENGER_*_MS), `ExitCodes` (an
  exit code said with its signal), `WindowsCommandLine` (the one maker of a Windows command line), `Product` (the one
  place the version is read), `PassengerLog` (below the App, never `IdeLog`; a line quoting a program's words through
  `WriteMasked`), `UnawaitedWork`, `Utf8Lines` and `PooledBytes` (a peer's lines read and messages written as pooled
  bytes), `SharedNames` (a name read from bytes, made once), `ContentLengthHeader` (every LSP and DAP message's header),
  `RazorFiles` and `MSBuildNames` (compiled into the extension and the host), `PrimitiveTypes` (the one table of
  primitives; the debugger's element types through `MetadataTypeNames.ElementTypeOf`, a generic name closed through
  `CSharpTypeNames.Closed`).
- `Passenger.Text`: the rope (every version a thread-safe snapshot), `TextDocument`, every editing command as a pure
  function to an `EditPlan`, `FuzzyMatch` (allocates nothing), `Outline` (a tree's rows in reading order, kept where
  unchanged), `FileText`, and anchors (`AnchorAt`/`RangeAt`/`MarkAt`, `AnchoredList`; moved before `Changed`, each
  handler isolated): hold a place in the text with one, never your own `TextEditing.Map*`.
- `Passenger.Editor`: the `CodeEditor` control (partials), `Keymap` and `EditorActions` (presets). The gutter's columns
  but numbers and folds are the App's (`IGutterColumn`), the editor naming no debugger, test or VCS concept
  (EditorRulesTests); what a file's editors draw is its one `DocumentDecorations`, never fed per split. With soft wrap,
  an offset becomes a place through `Rows`/`RowSpan` (a place on a line through `WrapOf`), the lines off screen wrapped
  on the pool (`WrapRest`). Wraps, rows, folds, hints and built lines are runs an edit moves only where it reaches, so a
  key costs what it changed, not the file's size: keep it so. Row views are carried so too, a highlighter carrying the
  colours of lines an edit didn't reach (`IHighlighter.Showing`, `KeptColours`).
- `Passenger.Languages`: C# coloured by Roslyn's parser in-process (`CSharpHighlighter`, also each open file's one
  syntax service, `OpenDocument.Syntax`: never a fresh parse; every in-process parse takes its options from
  `CSharpParsing`, a folder's file's through `CompilationSymbols`, `EditorArea.ParseOptions`, the XAML core's through
  `ProjectEvaluation.Parsing`, the debug adapter's sent with its request, never its own; symbols and LangVersion from
  the project's evaluation), the rest by VS Code's TextMate grammars (Tint stops a line past its time budget in the
  state it reached, so `TextMateHighlighter.Read` reads it again, then ends it in the state it began in; a line over
  20,000 characters is read once a pass with a tenth of the time, by the grammar's long-line tokenizer behind its own
  lock, `EndOfLong`). A new language: one `Language` entry, its grammar file and a notices entry; compare entries, never
  names. A finder never tells its answer inside a document's `Changed` (the open file moves its regions in a later
  handler).
- `Passenger.Xaml`: Passenger's own XAML core (no Avalonia; WPF shares it; not Avalonia.Ide.CompletionEngine); a binding's
  path is read once (`BindingPath`, `XamlFile.Walk`) for the check, Go to Declaration, uses and completion.
- `Passenger.Lsp`: the LSP client (StreamJsonRpc, whose types never leave the project; `JsonRpcPeer` for any other
  JSON-RPC peer, the Testing Platform's test hosts; both cancel through `ToldCancellation`), `LanguageIds` (C#'s and
  Razor's), Roslyn's server downloaded per platform (`RoslynServer.targets`), `LspDocument` (answers that grow with the
  solution decoded off the UI thread; never a change between a "\r\n"'s halves), and the web servers under `web/`,
  pinned in `WebServers/package.json` and its lock (to move a pin: edit it, `npm install --package-lock-only
  --ignore-scripts`, update the notices' "Inside web/" table). `typescript` stays on 6.x: 7's package is native and
  carries no `tsserver`, which typescript-language-server runs.
- `Passenger.Roslyn.Extension`: Passenger's handlers inside Roslyn's server, compiled against its publicized assemblies
  (`src/Publicize.targets`). A handler reads the solution, never changes it.
- `Passenger.Host`: MSBuild in a process of its own per SDK and global.json (`ProjectHosts`), over JSON lines, its
  results stamped with what they read; `hold` keeps generating design-time builds off while Passenger builds (Windows
  lets one writer have a file). A file-based program is a project named by its C# file (`ProgramFiles`), the SDK's
  project of it (`dotnet run-api`, `ProgramProjects`) evaluated in memory (`VirtualProjects`).
- `Passenger.Vcs`, `Passenger.Workspace`, `Passenger.Packages`: the App's Git, folder model and NuGet code that needs no
  UI; what belongs to the UI thread stays in the App's folder of the same name. A revision's text is `Git.BytesAtAsync`
  (through the file's filters) read in the open file's encoding (`FileVersion`, which keeps its stored encoding and
  BOM), a binary conflict probed by its first bytes (`BlobProbe`), a revision's file read as it comes so a binary one is
  only counted (`Git.StoredAtAsync`, `StoredRead`), never `git show` decoded as UTF-8; every git call but a clone runs
  through `Git.RunAsync<T>` (over `ChildProcesses.RunAsync<T>`, a failed read ending the child); paths reach git on its
  input (`Git.OnFilesAsync`), never its command line; git's questions (a login, a passphrase) reach the window only
  through `GitAskPass` (`VcsController.AskerOf`).
- `Passenger.Dap`, `Passenger.Debugger` (+`.Interop`), `Passenger.DebugAdapter`: Passenger's own debugger behind its own
  DAP, in a process of its own under `debugger/`. `Passenger.ContainerTools` (`passenger-tools`) runs inside a Linux
  container, self-contained, carried only in the release's archives.
- `Passenger.Profiler`: EventPipe sampling and ClrMD heap snapshots (its Azure packages stay carried; its DAC stays
  loaded once used: unloading it crashed on Linux); recording inside a container trusts nothing in a frame. A session's
  stop goes through `EventPipeStops.StopAsync`, a diagnostic port's failures through `DiagnosticsFailures.Of`, a
  recording in a container begins through `ContainerTrace.StartAsync`, passenger-tools' words are `ContainerToolsWords`.
  A run keeps its newest three memory snapshots' objects (`KeptSnapshots`). A heap dump is Passenger's own (`DumpTool`,
  deleted only once its writer and reader end, `DeleteAfterAsync`; leftovers swept, `Sweep`): the runtime's createdump
  writes into the program's stdout, and Windows' dumps only its parent.
- `Passenger.Inspector` (+`.Wpf`): the Live Visual Tree's helper, loaded into the app by a startup hook, answering over a
  Unix socket (`Protocol.cs` compiled into both ends). `Passenger.WpfPreview` (Windows only), `Passenger.TestFrames`
  (pictures of a failed headless test), `Passenger.Terminal` (the VT emulator, Porta.Pty).
- `Passenger.Analyzers`: the PSG rules (`Rules.cs`); a rule goes on only where nothing it flags is left.
- `tools/`: the notices, the web servers without Node.js, the test runner and shard scripts, packaging, site shots.

`src/Passenger.App`, by area:
- **Shell.** `PassengerApp` (every open goes through `OpenProject`; a `.slnf` is `SolutionFilter`, the projects it leaves
  out unloaded but never saved as the person's own), `ProjectSession` (one folder's services made and ended together; a
  handler acts only while its session is bound, `Bind`), `StatusBar` (`StatusPlace` makes its words a
  link; a message naming a Settings page takes it from `SettingsDialog.PlaceOfPage`), `IdeKeys` (the one keymap in every
  window but a dialog; a part with keys of its own is an `IOwnKeys`). The
  toolbar is the title bar: anything new on it goes inside `ToolbarStart` or `ToolbarEnd`, or Windows takes a press on
  it for the caption's. Logs are `logs/` under the state root (`IdeLog`; Settings › Diagnostics turns on protocol traces).
- **Editors.** `EditorArea` (a file in two windows is one `OpenDocument`; wire every editor through `ForEachEditor`;
  `CloseTab` is the one close); a preview holds no text (`HoldsText` false). The Markdown preview draws only the blocks
  near its sight (`MarkdownBlocks`), a page kept per tab, a scroll it sets going through `Jump`.
- **Docking.** A kept layout records windows by `ToolWindowIds`, so a value never changes; add a window with one
  `Dock.Add`; its floats close with their window (`DockHost.CloseFloats`); its action gives it the keys
  (`DockHost.Activate`, `ToolWindow.TakeKeys`), and Escape in it unhandled is Focus Editor (`ToolPane`). A view is built
  when first shown: what the IDE writes while it's hidden lives in a model, and code reaches a view only through a
  property that builds it for a person's action.
- **C#.** `CSharpService`, `ServerDiagnostics` (one pull at a time after a pause), `CodeAssist` (the window's wiring)
  over its parts; each language's answers a `LanguageAssist`, asked in `LanguageAssists`' one order. `WorkspaceEdits`: a
  server's edit to a file not open written in the 2 s before it was asked waits for the server to read it (`Patience`,
  the window's and an agent's alike, said as the action's work), and any other file of the solution written or deleted a
  moment before (`DocumentsWritten`); one written as the server worked is refused. `ServerWatches` watches for the
  server what it registers (`WatchedFiles`), beside the folder's watcher. A place the server names in a source
  generator's document reaches the file it was written in through `RoslynSession.WrittenAsync` (Razor's), else a
  read-only copy under the state's cache (`GeneratedCode`; an agent's tools on one answer from its text,
  `PassengerTools.NotGenerated`), never opened in the server, which would freeze its text into the compilation. A C#
  symbol's uses in XAML come from one finder, `XamlService.UsesAsync` over `XamlNavigation.Uses` (Go to Declaration's
  resolver) and `CodeSymbol`. A server's own command (TypeScript's refactorings, ESLint's fixes) runs through
  `ServerCommands`, the edits it sends back (`workspace/applyEdit`) one command of Undo.
- **Web languages.** `WebLanguages` (one Node.js server per kind; every per-kind switch names each kind), `JsonSchemas`
  (downloads only while Settings allows); in a macOS bundle the servers live under `Contents/Resources/web`. TypeScript
  runs one tsserver (`useSyntaxServer` never): a syntax server answers rename and usages from the open files alone.
  typescript-language-server keeps a file's first `formattingOptions` answer while it runs, so each open file tells
  tsserver how it indents itself (`WebDocument`'s `configure`).
- **Build and run.** A folder with no usable .NET SDK is `ProjectModels.MissingSdk`, said through `Refusals.NoSdk`.
  `BuildRunController` (partials), `ConsoleSink` (every console through one `PumpAsync`), `RunConfiguration` (data,
  edited in place as `ConfigurationEdit`s), `LaunchPlan` (one resolution for Run, Debug, Profile, Hot Reload and a
  debugged test host; a RunCommand naming another program run as `dotnet run` runs it, `OtherProgram`; a Blazor Web
  App's client no program of its own, `ProjectModel.IsBrowserClient`; a Passenger it launches keeps its state in the
  launcher's `sandbox/`), `UpToDate` (a launch's build skipped while what its last build read and wrote stands, a build
  under way waited out first), `TogetherBuild`.
- **Runs of services.** `ServiceRun` is the one model (`ContainerRun`, `AspireAppRun` its kinds): Debug, Attach,
  Restart and Profile of a service are written once (`BuildRunController.Services.cs`). One start at a time
  (`TakeStartAsync`); Stop ends every session and recording before the programs go.
- **Containers.** An app's variables are named in Core's `AppVariables` (its environment
  `AspNetCoreVariables.EnvironmentOf`, HTTPS `ServesHttps`), the engine's in `EngineVariables`; a compose file is
  `ComposeFiles.IsCompose`. The person's `docker` CLI and context only, never the Engine API or a Docker package; a
  service's name always after `--`. Compose's own model is the plan, Passenger's override goes to the state, and the
  tools downloaded from the release are SHA256-checked. Podman works both ways it's set up (`EngineState.IsPodman`).
- **Aspire.** `AspireController` (the IDE execution protocol on loopback, bearer tokens checked first),
  `AspireInteractions` (gRPC, its C# checked in), `AspireClient` (the CLI, for an AppHost Passenger didn't start).
- **Web apps' data.** The Requests window reads only while someone looks; Migrations takes connection strings by
  environment only; Endpoints sends nothing but on Test or Send, and a send taking the person's environment asks first.
  `ResourceFiles` is the one reader of `.resx` and `xlf/*.xlf`.
- **Debugging.** `DebugController` (a `DebugTab` per session, several at once), `DebugSession`, `BreakpointStore`;
  Variables and the hover card are one list; a row's expression is the debugger's `evaluateName` (`RowExpressions`),
  never built in the window, a member row's cast from the declared type read from metadata (`DeclaredType`). A session's
  built lines and Hot Reload begin at its build's save (`DebugController.Building`, `BuiltEdits`). The debugger's own
  rows are named in `DapRows`, break modes in `DapBreakModes`; an engine row is built by `ValueRow` (`FailedRow`,
  `GroupRow`), CoreLib found by `ManagedDebugger.CoreLib`. Every kind of breakpoint is refused, matched and hit through
  one pipeline (`IHitBreakpoint`: `Unreadable`, `Reconcile`, `HitStopsAsync`). A C# breakpoint inside a lambda on its
  statement's line keeps the lambda's column (`LambdaBreakpoints`).
- **Tests window.** `TestFinder` (tests from code before any build), `TestRunners`; `VsTestRunner` is the only file that
  knows VSTest's object model. A run from the editor, a filter or the Explorer chooses its tests once its build has
  ended (`TestController.RunMatchingAsync`); the gutter marks by name or by the runner's file and line
  (`DeclaredPlace`).
- **The rest.** `Packages` (every package change's one path, its files the project, its lock file and
  Directory.Packages.props, one command of Undo, put back when it fails; credentials only to their feed's host),
  `Workspace` (`SolutionEdits`/`ProjectEdits` change files as text, never `dotnet sln`/`add reference`; a version
  written where packages are managed centrally goes through `CentralPackages` (the file, `PropsOf`)),
  `ShellWords.Quoted` a word for a shell by kind (a dropped path, Settings' agent commands), `Templates` (every `dotnet
  new` through `ProjectTemplates`, what it writes above the new folder part of its command, `FilesAbove`; a new
  service's container or Aspire set-up after Create through Add's own actions, `MainWindow.ServiceSetup`), `Updates`
  (only an installed copy updates itself; What's New from `docs/release-notes/X.Y.Z.md`).
- **MCP.** `PassengerTools` (every tool, run on the UI thread, answering with what its operation returned), `McpHost`
  (`--mcp FOLDER`, a window's socket, `--mcp-attach` through `McpProxy`), `HeadStart` (an `--mcp` folder's opening
  begun before its window, which its first session takes up), `AgentConnection` (admission and questions). A relay
  records itself as `PID.relay` in the socket folder (`McpProxy.RelayAt`), which an update has follow it
  (`Updater.FollowRelays`: it execs itself from the updated app as the same session, `McpProxy.Follow`) or, on Windows,
  ends (`EndRelays`), signalling the relay alone, never its agent's process group.

Releases and the site: `ci.yml` and `release.yml` (from a `vX.Y.Z` tag, its body `docs/release-notes/X.Y.Z.md`, refused
while it is the stand-in) call `test.yml`: the gate on every system in two shards, and for CI the report lane too. `site/` is passenger-ide.net, plain HTML
and CSS loading only its own files (CSP 'self'), deployed by the owner by hand: each page has its own title,
description, canonical, Open Graph tags and `sitemap.xml` entry, a changed share card takes a new name, and `just
site-shots` retakes its images.

## Traps

- **Roslyn's server** stays at 5.11.0 until a published build carries dotnet/roslyn#86130 (merged 2026-10-20): the
  builds since start Visual Studio Telemetry's session even with telemetry off (dotnet/roslyn#86122; #234). After
  lowering it, delete `artifacts/bin/*/debug/roslyn*`, or the copy leaves a mixed server that won't start.
- **Roslyn's server makes a C# file opened before its project loads a Miscellaneous File** and restores a project of its
  own for it, so `CSharpService` opens a loading project's files only once the server says that project loaded, and a
  file come since only once a load of its project that read it has ended (`CSharpService.Arrivals`); a change waits only
  for such files in the projects it reaches, and Find Usages answers at once naming any not taken in (`UnplacedAsync`'s
  filter, `ReachOfAsync`); references come from the session's evaluated projects (`CSharpService.ProjectsNow`), never
  the service's start-time `workspace`. A reload sent while it loads is one more load to wait for (`RoslynSession`): the
  server's word that a load ended names none. A server that ends during its start is started again within
  `ServerRestarts`; StreamJsonRpc then drops a faulted answer, which `IdeErrors` passes over
  (`LanguageServer.IsDroppedAnswer`).
- **Roslyn's server takes file watching all or nothing**: offered it, it watches nothing itself; all it registers is
  ours. A file deleted reaches it only once the projects holding it loaded again without it (`ProjectMethods.Deleted`):
  told first, it reads the missing file for its document, a second each, before any answer needing the compilation
  (dotnet/roslyn#86236).
- **Roslyn's server passes over an option answered empty** when told its configuration changed, so an option goes back
  to its default only as that default's value (`CSharpService.OwnScope`), never null (a server's option values can't
  be); `LanguageServer.TakesOptions` names the one thing a running server still can't take, an option left out.
- **Roslyn's server writes its MEF caches beside its DLLs** (`roslyn/cache/`, no option for elsewhere), so where that
  folder is in a macOS bundle (a file added breaks the seal: "damaged", #450) or read-only, it runs from a copy in the
  state's `cache/roslyn/` (`RoslynServerFolder`); nothing a Passenger runs may write inside the app.
- **The debugger's engine.** `EngineLock` is not re-entrant. A read of the stop waits behind continue, steps, pause and
  disconnect. Never leave `_stepper` set but inactive (async stepping reads it as a queued StepComplete). An evaluation
  past its time is aborted only where the abort takes (never rude: one left pending fires later inside a lock, #238).
  Before letting a program go the engine runs it off patch and step stops (CET would fault it) and waits out abandoned
  evaluations. A decompile never runs under the lock. Returned partway through a statement, a frame's IL offset reads as
  the statement's start, so the call it returned from is sought across the whole statement. One assembly in two load
  contexts is two modules at one base address: modules are kept by their `ICorDebugModule`, and a breakpoint's bindings
  (`BreakpointManager.BreakpointBinding`) name their `ModuleInfo`, never an address. After a Hot Reload update, read a
  method's points, locals and IL only at the frame's version (`Points`, `DebugOf`, `MethodIL`). A runtime-async await
  (.NET 11) steps by a breakpoint where its method goes on (`AsyncStepper_RuntimeAsync`), which a Just My Code stepper
  on the thread passes by, so a step over one turns it off. A func-eval passes a by-ref value with no home of its own as
  a copy gone once the call returns: a callee that may keep the reference gets a home of the evaluation's
  (`PinnedArrayOfOneAsync`), or the evaluation refuses; a struct the evaluation stores is its own copy (`OwnAsync`),
  never the frame's.
- **Hot Reload** drives the server's `EditAndContinueService` from the extension; CoreCLR lets no debugger go once Edit
  and Continue changed a program, so the debugger never lets go of a program Hot Reload changed
  (`DebugController.LetsNoDebuggerGo`; Detach refused, such a service restarted, leaving the folder asks, then stops it:
  `MainWindow.HeldByLeaving`), and a build's edits are kept for each program it starts later (`Kept`/`Copied`); a
  session takes edits only for the projects of modules its program has loaded (others frozen), and `StaleCallers` blocks
  an update that leaves loaded code calling a deleted body. The runtime offers a frame left in old code a remap at every
  statement, unstoppable: a running program is held through an update so Roslyn maps its frames, which move only at a
  statement's start (`RemapTarget`).
- **dotnet watch takes Ctrl+R only from a console's keys**, never from redirected input: the Run window's restart under it
  is Rerun, and when it waits after the program ends it says so (`BuildRunController.WatchedEnd`).
- **The inspector** never touches `Dispatcher.UIThread` before the app makes it, never waits on Passenger, and readies its
  wire's types before the app's Main (System.Text.Json's lock against a thread the debugger stopped).
- **MCP's tool list** is paid for by every agent session (45.6 KB, held under 62 KB by McpDebuggingTests): say each thing
  once (shared parameters in `McpHost.Instructions`), and add no tool another covers; an answer that can run long goes
  through `AgentAnswers` and says what it left out. `save_all` stays a tool of its own:
  never asked about on a person's window (#278). `debug_evaluate`, `debug_input` and `breakpoint_set` are `Destructive`
  and ask the person the first time; every question closes unanswered after 2 minutes; calls send progress every 10 s
  (OpenCode gives up after 60 s of silence).
- **Work every window shares** (a cache of tasks, a process-wide gate) runs on the thread pool, never on the asking
  thread: a headless window test's thread never runs again once its test is over, so what waits on it waits for good.
- **A window the IDE opens of its own accord** (a notification, an agent's question, not a person's action) shows with
  `ShowActivated` false, or it takes the keys from what the person types (headless, it clears them); an agent's question
  is not waited on as a dialog and has no default button, so a stray Enter never answers it.
- **Agents never open windows on the person's screen**: `--mcp FOLDER`, or a headless screenshot run's `--agent`
  step to try attached behaviour (a headless `--mcp-attach` window keeps no settings, so it asks a person who isn't
  there); never a bare `--mcp-attach`, never `just run`.
- **Aspire's CLI and AppHosts** keep running AppHosts in `$HOME/.aspire/cli/bch` whatever ASPIRE_HOME says; on Windows
  in the profile whatever the environment says (those tests skip there). An Aspire service's replicas share
  `OTEL_SERVICE_NAME` and its display name: a start's own resource is found by `service.instance.id`
  (`AspireResourceRun.ResourceIn`), never by name alone; a service action takes the start chosen, and a start's hold,
  attach and recording are kept by `ServiceRun.KeyOf`. DCP names each replica with a random suffix every AppHost run,
  and its index never reaches the IDE: a replica's identity lasts one run, so a choice for one replica is every
  replica's next run.
- **Windows**: a folder a process works in can't be deleted, and MSBuild nodes start in their launcher's folder and
  outlive it, so a child whose descendants must end with it is held with `ChildProcesses.HoldFamily`; a write waits out a
  reader that shares no writes for up to 2 s, off the window's thread (`FileText.WhileHeldAsync`); closing a socket
  with unread input resets it, losing what was written (servers shut their side and drain first); the diagnostics library
  leaves a profiler pipe open after its listener goes (`ProfilingSession.CloseLeftListener` closes it, by reflection).
- **macOS**: a variadic libc call through P/Invoke (`fcntl`, `ioctl` with an argument) loses its variadic arguments on
  arm64 (`Descriptors.CloseOnExec`), and `poll` never says a named pipe's writer has gone (`select`); a Unix socket path
  holds 104 bytes; `codesign` takes a dotted folder under `Contents/MacOS` for a bundle (so the web servers live in
  Resources); TMPDIR is reached through the /var link, so a build or restore of a test's project goes by its real path,
  and two spellings of one project restored at once collide.
- **.NET's watcher on macOS passes over FSEvents' word that it dropped events** (`MustScanSubDirs|UserDropped` arrive on
  the watched folder itself, which it skips; dotnet/runtime#136577), so a folder is watched only through `WatchedTree`
  (the folder's watcher, each of `ServerWatches`' and Git's folders outside it, `GitWatcher`), never a bare
  `FileSystemWatcher`: it takes 256 changes within a second, or an overflow, as a burst at risk and lists again the
  folders it touched against its record (#607, #609). The one file watched alone, settings.json, is read again on its
  watcher's error and as a window comes in front (`SettingsStore.LookAgain`).
- **macOS's "damaged" dialog** comes from a Passenger.app whose seal no longer holds being read or started: a mounted disk image
  whose file was replaced under it (`just package-mac` ejects one first) or a bundle written into (#450). Tests never start a
  program inside an unsigned bundle, and the run's folder ends in `.noindex`, so Spotlight reads none of what a run makes.
- **Linux**: file systems tell letter case apart, so a test of another letter case skips there.
- **MSBuild**: a build through another spelling of a project's path deletes what the first wrote (IncrementalClean; a
  shell or MSBuild takes an inherited PWD for its folder's path, #413, #537), and a reused node carries a project's
  earlier spelling and NuGet home into the next build. It keeps no idle node unless the process's nice value is 0.
- **Local History's `changes.log` is shared by every Passenger on the same state**: a store takes in the others' lines
  under `Hold` before it keeps anything, and a change logged twice (named by its maker, heard by another) lists once.
- **A variable taken out of a process's own environment goes through `OwnEnvironment.Set`**: on macOS and Linux .NET's
  `SetEnvironmentVariable` never changes the process's own, from which Porta.Pty forks the terminal's shell; Porta.Pty also
  drops an empty value, so a terminal run's removed variable, and an outer tmux's or screen's
  (`TerminalSession.WithoutOuterTerminal`), is unset through `env -u` (`TerminalSession.Unsetting`), which on macOS loses `DYLD_*`
  (System Integrity Protection), so they go under `TerminalSession.KeptPrefix` for sh to set again, never as env operands.
- **The carried schemas hold Passenger's patches** (`WebServers/schemas`, MSBuild's xsd), listed in THIRD_PARTY_NOTICES.md
  and each file's `$comment`: a fresh upstream copy loses them (CarriedSchemasTests checks defaults).
- **The terminal reuses a line** once it leaves the screen and a full scrollback (`TerminalLine.Reuse`): nothing keeps a
  `TerminalLine` past its line number. A line's cells are written only through `TerminalLine.Writing()`: the view draws
  a line as it kept it while its `Writes` stand. A pseudo-terminal's close on macOS waits for a write still in flight,
  and an exiting shell that leads its session waits for its output to be read: `TerminalSession` reads to the end and
  closes off the window's thread. macOS's bash 3.2 with job control sometimes prints "child setpgid (N to N): Operation
  not permitted", so a test reading a shell's lines allows others between them.
- **Avalonia 12's lists** re-attach, and so re-style, every recycled container and never reuse a `DataTemplate`'s
  content: a list's, combo box's or tab strip's rows go through `ReusedRows`, made once with their containers (UiRulesTests).
- **Avalonia 12's ListBox re-points its keys' row by the chosen row's new index before its panel moves rows**, so a
  focused row loses the keys when rows come above it, and moves a Shift range's anchor only with its first chosen row:
  every tree goes through `TreeKeys.Attach`, which keeps both.
- **`FormattedText` lays its lines out again at every draw**: text drawn frame after frame is kept as a `TextLayout` or
  `TextLine` until what it says or its width changes.
- **EventPipe from a program's start**: .NET 10's LoggingEventSource misreads the `FilterSpecs` it is made with, so a
  category takes no `:Level` there or none of its logs come (`LiveRequests.LogsSpec`). Its sampler takes a thread only
  where it can stop it, so a test program's method that must own its samples spins in a loop that calls nothing. A
  program the debugger holds answers no session's start or stop and gives no heap, so the profiler refuses it
  (`Refusals.PausedInDebugger`, `DebugController.Debugging`).

## Tests

Tests drive the IDE whole, headless (`tests/Passenger.Tests`, xunit v3 on Avalonia's headless platform with real Skia):
a window or an editor, real keys by Rider's macOS keymap (`EditorRig`), typing, clicks, files on disk. Pure logic (no
window, process, network or clock; a temporary folder only where it is about paths) is unit tested in
`tests/Passenger.UnitTests` instead, each test milliseconds and the same on every run, seeded where random; an end-to-end
test that checks only such logic gives way to unit tests. Either way a test's name says what holds and its failure message
names the consequence.

**The gate** (`[Gate]`, and every unit test) is what blocks CI and a release: a test enters it only with a clean record
on CI on all three systems, no measured time or allocation bound and no container, real-Aspire or Node.js need, guarding
a core flow; one that fails there for a test's reason leaves it until rewritten. Every other test runs in the report lane,
whose failures never fail a run: each becomes an issue (`ci-report`) fixed in the normal flow.
`BannedSymbols.txt` (RS0030) bans driving internals directly (the terminal's emulator, a hand-started Roslyn or profiling
session, a lone `TextDocument`, `LineDiff`, `CommitGraph`, `Snippet.Parse`; `Rig.cs` excepted), raising Click events
(`Ui.Click` instead) and fixed sleeps (`Thread.Sleep`, `Task.Delay`, `Screenshot.Pump`) outside `Waiting` and `Ui`. The
long random editing session checks every key against a string model, with soft wrap too: keep it passing, never narrow
it. Performance is held by what the UI thread allocates in a small case against a large one, not by a time; to see where
the UI thread's time goes, attach Passenger's profiler to the test process (`ProfilingSession.AttachAsync(Environment.ProcessId, …)`).
A headless frame is GC pressure, so tests that press thousands of keys draw a small window.
Avalonia's text formatter gives the pooled arrays of runs it shapes together back only when finalized, so a measure of a
key's bytes collects first (LongTextTests).

**Waits (#237).** A test waits for what it checks, however slow the machine. `Ui.Settle` (and `Press`, `Landed`,
`WaitFor` on it) waits until the IDE is quiet: `BackgroundWork` landed, nothing in `InFlight` (but Roslyn's held
workspace pull), due timers fired, the last layout drawn; a give-up lists each hold by file and line. Not waited for: a
running program, the state store's 0.5 s write delay (flush to read a state file), templates read ahead, the system's
delay before the watcher hears a change (wait for its `Changed`). Words describing the window as a wait gave up are a
`Func<string>`. Wait with `Ui.WaitFor`, `Ui.PumpUntil`/`Pumped` (the loop run, not settled: a held start, an app that
keeps the window busy) or `Waiting.Until`/`UntilAsync`/`Within` off the UI thread; read a task's answer once it ended
(`Ui.Await`, `AwaitPumped`, `Landed(task, what)`; `Ui.Idle` only where the window must run nothing, as a measure of
its allocations), and start a test's own work through `Waiting.Apart`. Time that must
pass is `Ui.Hold(time, why)`, `Ui.Stall` or `Waiting.For`. Every limit is written as a developer machine's time and scaled by
`TestRun.TimeScale` (`PASSENGER_TEST_TIME_SCALE`; CI 2 on macOS, 2.5 on Windows, 2 on Linux); the IDE's own waits on what
it started scale by `PASSENGER_WAIT_SCALE`. `FLAKES.md` records each CI failure's cause.

**Isolation.** A run keeps to its own folder (`TestRun`): the IDE's state, logs and sockets, every test folder
(`TestFolders.Create`), shells' history, an empty Git configuration, NuGet's home on macOS and Linux, MSBuild's node
salt, container labels, user secrets (`TestRun.UserSecrets`), dotnet new's templates, Aspire's HOME and CLI. It never
reaches nuget.org (local feeds: `AvaloniaApp.Feed`, `StandInFeed`, and the restore-only `tests/EfGraphs`,
`tests/AspireGraphs` and `tests/BlazorGraphs` that fill the package cache), GitHub (`PASSENGER_RELEASES` dead),
registries (`PASSENGER_BUILDKIT_CHECKS=off`), Docker Hub, symbol servers, Discord, or the person's Passenger
(`PASSENGER_START=headless`). It shortens `PASSENGER_EVAL_TIMEOUT_MS` (3 s), `PASSENGER_WATCH_LATER_MS` (3 s),
`PASSENGER_MCP_PROGRESS_MS` (1 s) and `PASSENGER_REGEX_TIMEOUT_MS` (1 s); a test holding a server to not answering sets
that server's limits, never the run's. After each window test its windows close (those it closed itself too) and what
they started is waited out (`TestsLeaveNothingBehind`). A plain `[Fact]` using `HeadlessUnitTestSession.Dispatch` must
not end on the session's thread (`await Task.Yield()` before throwing). A sleeping Mac fails tests: full runs go under
`caffeinate -is`.

**Collections and shared projects** (`Collections.cs`). Window tests run one at a time on the session's thread. Beside
them run only the debug adapter's protocol tests (`DebugSession.Collection`) and `Beside` classes (no window, nothing the
process shares changed, nothing held to a time). A class with a test held to a time, or that debugs through the window,
is `[Collection(Alone.Name)]`, run last with nothing beside it. A class sharing one project derives from `SharedProject`:
one warm language server, the files put back before each test (with times that keep MSBuild honest), a new file through
`project.Write` (which ends the server, unless the class passes `loadsAgain`), `InitializeAsync` overridden with
`PrepareAsync(restore: false)` where nothing
reads a restore, `PrepareAsync(byRealPath: true)` where projects name each other by real path. A class fixture is made
off the UI thread, a window test's class on it, and xunit v3 calls only `DisposeAsync` on a class with both. Fresh restored folders come from `RestoredFolders`. A test asking
nothing of C# opens without the language server (`Ui.OpenFolder(folder, languageServer: false)`, `McpRig.Start(…,
languageServer: false)`).

**Runs.** `just test` deals the classes outside Alone to N processes by `TestTimes.json` (each with its own run folder;
the container classes in one process), then the Alone classes in one. CI deals each system's gate to two shards and its
report lane to four the same way, and `just test-times RUN` rewrites the times. Tests hold to no time budget (the
owner's choice): each job's summary shows a class's seconds, and one taking far longer than its work needs is flagged as
a bug, never held to a limit. The run sets its nice value to 0; a test needing an MSBuild node waits for
`Processes.MSBuildNodesCounted` (`UntilMSBuildKeepsNodesAsync`) or skips saying why. The VMs run a filter on a commit:
Windows on Arm, `~/Documents/passenger-vm/vm-test.sh NAME FILTER` (no Node.js; it can't see dot folders), and Ubuntu
26.04 ARM64, `vm-test-linux.sh NAME FILTER`.

**Helpers.** Clicks: `Ui.Click` (a real press on a control shown, enabled and hit, shutting first the tooltips its
pointer leaves, which open in the window headless as the window redraws under a pointer that never moved; `RightClick`,
`DoubleClick`, `MiddleClick`, `SideClick`, `ClickButton`, `ClickRow`, `ClickLink`; a drag with any button and modifiers
`Ui.Drag`); menus: `Ui.Choose`, `ChooseFromMenu`, `ChooseFromRowMenu`, `ChooseFromEditorMenu`. A chord in one
`Ui.Press(window, "control M , control S")` (its first key is forgotten after 2 s). Consoles: `Ui.TypeLine`
(`TypeToRun`, `TypeToDebugged`); the caret: `Ui.PlaceAfter`; a run: `Ui.Launched` and its status `Ui.Said`, both naming
a failed build's errors. Projects: `TestProjects.Console`, `Library`, `Web` (`.With`) on `TestProjects.Framework`;
files: `TestFolders.Write`; a loopback HTTP server: `StandInHttp`. Multi-line edits: `Typing.Replace` (`KeyTextInput`
drops `\n`). A server's request held unsent: `LanguageServer.HoldNext`; its answer held: `HoldNextAnswer`; a container
run's `up`, its ports chosen, or the next restart of its services after their build: `BuildRunController.HoldNextUp`; a
watcher's record read: `WatchedTree.RecordSize` (the server's own watcher of a folder, `ServerWatches.Of`). A helper
that runs a program (`TestGit`, `Restore`, `RestoredFolders`, the app makers, `SharedProject.Reset`) waits on the UI
thread with its loop running and refuses any other; a fixture or a windowless test awaits its `…Async` form. Read a log
the IDE may be writing with `Ui.Shared`. A dialog is held to `DialogButtonsTests.Check`, the window to `UiRulesTests`. A
stand-in leaves files a target writes as it found them (a new one makes MSBuild skip the target). Containers:
`Docker.Stopped`/`TakenDown`; MCP: `McpRig.Text`, `Gone`; an agent that gives up sends "cancelled" itself
(`CallGivingUp`). A headless click runs the window's queued work and draws frames after itself, so what a status said
during a click is read from what it showed throughout, not after; a count of a key's frames sets aside the caret's
blinks (`CodeEditor.Blinks`). A window closed as the application's Quit closes it: `Ui.CloseAsQuit`; a headless window
made inactive: `Ui.Leave`.

**What a test needs, or skips saying why without:** a Linux container engine (`docker info`'s OSType; tests label
everything with `Docker.RunLabel`, remove dead runs' leftovers, never prune or start or stop Docker Desktop, and pull
nothing large: a test needing an image the engine lacks names it and skips), Node.js (web languages), Postgres's image
(MigrationsPostgresTests), `PassengerAspireRuntime=true` (real AppHosts: Aspire's dashboard and orchestrator, about
900 MB), Windows (WPF's preview and inspector, data breakpoints on x64). The WPF reference pack's version is pinned
(`WindowsDesktopRefVersion`): a new SDK fails WpfXamlTests naming the version to pin.

**Debuggees** (`tests/Debuggees`): tests find the lines they stop at by their code (`Debuggees.Line`, `MyMethodEnd`), so
changing a line's code means changing the tests that name it (a breakpoint on a line binds the statement that starts
first on it, and inside a lambda only where the line starts in one: give the lambda's column to stop inside it).
`SelfDebuggingTests` finds its breakpoint by the code `status.Text = why;` in MainWindow.Actions.cs.
`Debugging/Helpers/StartHold` holds a launched program at its start. `PASSENGER_ENGINE_LOG=FILE` keeps the engine's log.
A thread a debuggee starts just before a stop can hold that stop's evaluations (it is loading what they need, and they
run with it held still): stop once it has run a round (#599).

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
- A visual inconsistency is fixed once, in a shared place (a token, a base style, a helper), never per control:
  Passenger's own consistency settles a design question. The shared pieces: dialogs `DialogFooter` (`Of`, `Listed`,
  `Answers`, `Bar`, `Page`) in a `Border.footerBar`; lists in a page `Border.framed`; rules `Passenger.Separator`
  (`Passenger.Divider` between panels); menus' value in force `MenuChoice` (`Holding` makes its submenu as it opens); a
  menu the menu key opens `ListMenus.PlaceForKeys`; tool window tabs `ToolTabs.Tab`; popups' widths `PopupFrame`, titles
  `popupTitle`; every row `ListRow.Height` (24); empty states `TextBlock.emptyText` (with an action's link `EmptyState`); toolbar icon buttons
  `ToolButtons.Of`/`Toggle` (never focusable); muted words `MutedText`; list keys `ListKeys.Move`; counts `Plural.Of`, lists `Plural.Listed`;
  rounded borders `Border.kindBadge`, `tag` or `box`, never a literal radius; lists of what runs `RunRows`, Details tabs
  `DetailsTables`, merged consoles `MergedLog` (a console's Find `OutputView.Find`); a tree window's Expand and Collapse
  All `ToolWindow.ExpandAll`/`CollapseAll`; a toolbar down a side `ToolColumn`; scrollbars `ControlThemes.axaml`; the
  focus ring `FocusRing`; a window's icon `AppIcon`; a list's rows `ReusedRows` (`Use`, `Showing`), their icons
  `RowIcon` (a tree row's chevron and icon `RowGlyphs`), drawn as `PathIcon` draws for a fraction of its cost, never
  `PathIcon` (UiRulesTests). A row hears its item only while shown (`Observable.FollowWhileShown`).
- `MainWindow.SmallestWidth` is 640: the layout works at half a 1366-wide screen, and every window is an `IdeWindow`,
  which fits the working area of its owner's screen as it shows (`WindowFit`; one shown with no owner through
  `FitToScreen`).
