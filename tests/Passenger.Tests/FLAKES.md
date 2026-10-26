# Flake ledger

Each test failure on CI (or a VM) once its cause is known: the test, what it said, and which of two things it was, so a
pattern shows. A **race in the IDE** is fixed in the IDE; waiting longer or trying again would only have hidden it. A
**test waited wrong** is fixed in the test, by waiting for what it checks (`Ui.WaitFor`, `Ui.PumpUntil`,
`Waiting.Until`), never by a longer fixed time. A failure that was neither says what it was.

To add one: a line under its date, newest first, naming the test, the run, the cause in a sentence and the commit
that fixed it, in one of the forms below. A failure not yet understood goes in only once it is.

## 2026-10-26

- `ProgramListsTests`' `A_scroll_step_down_a_profiles_thousand_methods_and_types_hands_the_rows_others_never_making_one_again`,
  CI Linux, run 40312082824 (c33d9f4d): "a wheel step down the profiler's call tree allocated 227 KB on the UI thread
  over 1000 methods, against 143 KB over 100". The short list's step was the odd one, a row's restyle below every other
  system's. Its profile had sampled the finalizer thread too, so the timeline above the tree grew a row and the tree
  opened shorter than first laid out. The list kept a row past its new foot realized until it scrolled, so the first
  step brought in one row where every later step brings two. Neither race: **the test** takes one step before the five
  it measures (04f883f6).

## 2026-10-25

- `ProblemsTests`' `A_typing_pause_lists_again_only_the_open_files_problems_however_many_the_solution_has`, the Mac under 15
  CPU burners (#532): "9 bytes a problem" (208 KB against 170 KB, 101 and 10 blinks in 8 pauses). Nothing the window did
  grew with the problems: at equal pause lengths both cases allocated the same. The 4,000-warning solution's pauses were 2
  to 12 s against 1 to 1.5 s, and a pause's time costs the window more than the blinks the test took out at their cost at
  rest, which piled up over a long pause. Two causes. **The test** now also takes out each millisecond of the wait at what
  the window costs at rest, measured in each case (`Ui.RestBytesPerMs`), and still fails at 10 bytes a problem with 8
  injected (11137ee6). **A wait in the IDE**: the open file's pull asked Roslyn's sources in turn and showed none until all
  had answered, and with solution-wide analysis its non-local source analyses the whole project (1.1 to 2.6 s a key with
  4,000 warnings against 75 ms with 22, traced); the file's own are now shown as they answer, the project-wide after.
- `ProblemsTests`' `Keys_typed_above_thousands_of_problems_list_them_again_once_typing_pauses`, the Mac beside two other
  test processes (#532): "listed again 6 times for 8 keys". The File tab lists a file's problems again once its markers
  have not moved for 250 ms (`ProblemsPause`), a delay on the clock, so keys typed that far apart, as a loaded machine's
  are, end a pause between them; not seen again under 15 or 40 burners (keys 10 to 180 ms apart). Neither race: **the
  test** counts only keys that came within the pause of the one before, typing on until eight did (04c7ae79).
- `FindInFilesTests`' `The_options_narrow_the_matches_and_an_open_files_unsaved_text_is_searched` and
  `Replace_in_files_fills_a_regexs_groups_…`, under load (#539): `c\w+t\(` found no match where it finds 1, and `cat|dog`
  with Words none where 2. Every regex had 250 ms a match by the wall clock, and a search of microseconds at rest took up
  to 309 ms beside 30 CPU burners here (2 of 6.5 million of these searches stopped short); Find in Files dropped the stop
  without a word. **A limit in the IDE** a busy machine outran: plain text has none (a literal never backtracks), a regex
  5 s off the UI thread, where the UI thread hands one it gives up on (52efb6f1); a search that stops now names the file.

- `McpAvaloniaAppTests`' `An_agent_debugging_an_avalonia_app_…`, the Mac with several agents' runs at once: "paused on a
  binding error the debugger could not describe" (#532). An agent's answer at a stop waited 5 s for the Debug window to
  read it, and a binding error's stop is read by three of the adapter's answers (its frame, `$bindingError`, its words),
  which a loaded machine gives later, so the answer came without them. Beside 20 CPU burners the same test failed sooner,
  its click into a breakpoint said in 9.7 s and 24.6 s against 8 s (the answer read the stop's locals it never says) and
  its first debug_start in 116 s with the app's first build in it. **A race in the IDE**: the answer waits for the stop's
  read, woken as it lands, and the click's reads only its place; **the test** builds before timing the start (dd8589bb).
- `AttachTests`' `A_program_held_at_its_start_runs_on_when_its_attach_ends_before_taking_hold_of_it` and
  `…whose_runtime_starts_after_its_attach_ended_runs_on`, the Mac with several agents' runs at once: "Strings differ",
  `The runtime has been configured to pause during st…` where "child ran" was expected (#532). Never a program left held:
  it was let go and ended, but its attach, slow to start under load, held it past the 5 s after which its runtime writes
  that notice to its standard output; 24 burners with the tests at nice 15 failed both so. **The test** read the runtime's
  words as the program's; it reads the program's own, and a let-go that gives up is now told in the debug console
  (6822d614).
- `MigrationsTests`' `A_missing_tool_and_Design_package_are_found_before_the_command_fixed_in_place_and_the_command_made`,
  the Mac under load, twice alone and once in four loaded runs here (#532): "Undo asked 0 questions". The test clicked
  the project's row, undid, and clicked the same row again for the second undo; when the first undo came back within the
  500 ms of a double click (0.43 s here), the second press was the row's double click, which opened Shop.csproj in an
  editor that took the keys, so Undo went to the editor. Neither race in the IDE: **the test**'s two clicks at one place
  were a person's double click; `Ui.Click` now waits out the double-click time after a click at the same place (33d096bd).
- `AspireStandInTests`, a different test each run, beside other classes with Docker Desktop down (#532): the status read
  "Docker isn't running (context desktop-linux): …" where the test expected its own words. An AppHost's start says the
  engine's state, then follows its events; when they ended (at once for an engine that is down, later under load, as the
  docker CLI is slow to fail) the window read the engine again and said the same words over what the run or an action
  had said since, a window joining an events worker still retrying hearing no end, so which test it hit varied. A stand-in
  engine held down until the run said it runs fails so every time. **A race in the IDE**: an event's read says the engine
  only when its state changed (de915bfc).
- `LongFileAssistTests`' `Typing_with_pauses_…`, CI macOS, release run 40125454882 (1bd14b59): the whole file's colours
  were asked for twice. CountedMessages noted where the server's refresh stood among Passenger's requests only once
  StreamJsonRpc had parsed it, which can come well after its last byte is read, so a whole-file request sent in that gap
  counted as sent before the refresh and was asked again. **A race in the IDE**: each message's place is now taken as its
  last byte is read (fad3e3c3, #479), and the test waits for no request outstanding rather than a fixed 3 s.

## 2026-10-24

- `XamlTests`' `A_project_that_takes_avalonia_while_its_xaml_is_open_gets_its_xaml_help_once_the_restore_lands`, CI Linux,
  release run 40138434633 (4265c9fd): "Waited 180 s for Avalonia's controls offered after a tag's '<' once the project
  references it". A project file's own PackageReference makes its XAML Avalonia's before a restore, so Linux, hearing the
  project file at once, read the project within the test's settle with none of Avalonia's types, and the wait for the
  restore's read passed on that read; the restore's assets were heard inside the watcher's 150 ms gather as dotnet restore
  exited, so the '<' typed next took its list from it (x: types alone), and the restore's read, landing after, left the
  list so. With the project file's read awaited before the restore and the restore's change held 1 s, the Mac failed so 3
  times in 3. **A race in the IDE**: a XAML list made from the project as it was is asked again once the project is read
  anew (bafbd202).
- `ProblemsTests`' `Solution_wide_analysis_goes_on_following_edits_after_the_server_refuses_a_pull`, CI macOS, release
  run 40127831310 (07a817ff): "Waited 120 s for the broken caller's error". Roslyn works out a workspace pull's reports,
  then holds it open until its source's next change, and the IDE took no partial results: the edit came while the pull
  before it was still working out the old state, so that pull's hold took the change and closed with nothing new, and the
  next pull, which found the caller's error, was held with it until another edit. With 150 more files in the project, so
  that pull takes 3 s, the Mac failed so 4 times in 4. **A race in the IDE**: each report is listed as Roslyn streams it
  (69795db4).
- `AspireStandInTests`' `Profile_of_an_AppHost_gives_up_on_a_service_past_its_time_saying_so`, CI macOS, release run
  40127831310 (07a817ff): "Profile's time limit was said as " with nothing after it, Profile having answered that it was
  profiling apiservice. The limit was measured by the wall clock when Profile's start looked again after DCP's ask, so a
  look that came after the service had reached the profiler (a second after the ask here) took it for one in time; why the
  look came that late on CI is not traced (0.3 to 9 ms in 27 runs on the Mac, under load too), and a look held back 1.5 s
  fails so every time. **A race in the IDE**: the limit falls due on a timer of its own from the ask (50ce6ce4).
- `WpfXamlTests`' fixture, CI Linux, release run 40127831310 (07a817ff): "The file '…/feed/packages/microsoft.windowsdesktop.app.ref…nupkg'
  already exists", every test of the class failing in its fixture. Its gallery and XamlEditingTests' (new with #476's XAML
  localization) made their WPF apps at once, each finding the pack missing from the run's feed before copying it. Neither
  race in the IDE: **the test**'s check-then-copy, now under a lock (9fdcdcca).
- `BuildRunTests`' `With_hot_reload_edits_reach_the_running_program_a_signature_change_restarts_it_and_it_ends_with_the_window`,
  CI Linux, release run 40127831310 (07a817ff): the same "the status bar says \"Hot Reload: dotnet watch applied the
  changes\"" as release run 40088071944 below, with the 3 s the run follows dotnet watch's words doubled (0f450eb7). That
  entry misread the cause: dotnet watch said the restart within a call of the program's after "changes applied", and a
  window a frame behind took both in one batch of the Run window's text, which it read by the first of dotnet watch's
  words the batch held, however long it followed them; a window held from drawing fails so on the Mac (and in the Ubuntu
  VM dotnet watch once told the apply after the restart). **A race in the IDE**: dotnet watch's words are read a line at
  a time in the order it said them, a restart standing, and followed until the next save or the run's end rather than for
  a fixed time (36dfa8d7).
- `StuckEvaluationTests`' `An_evaluation_spinning_in_a_finally_block_runs_to_its_end_once_let_out`, the Mac with 24 busy
  processes on 12 cores, once in 17 runs: "Expected: 0, Actual: null" (#472). The platform layer inside the debugger's own
  libraries reaps a launched program with waitpid as it sees it end, so the adapter's own waitpid, run late, found no child
  and the end was told with no exit code; a wait held a second after the end fails so every time. **A race in the IDE**: on
  macOS the launch watches the program's exit with a kqueue, whose status the system keeps whoever reaps it (532ae553).
- `AttachTests`' `A_program_held_at_its_start_runs_on_when_its_attach_ends_before_taking_hold_of_it`, the Mac with three
  test processes at once: "The program held at its start was left held when its attach ended before it took hold" (#471).
  The session's end asked the runtime to let go once, before a runtime started late had opened its diagnostic port ("it has
  no diagnostic port open", or a stale socket of the same pid refusing), and a program a shell execs a second late fails so
  every time. **A race in the IDE**: the release waits for the port while the program is starting (7ffb94f1).
- `LiveTreeTests`' `A_debugged_avalonia_app_shows_its_controls_where_each_value_and_style_comes_from_and_the_control_clicked_in_it`,
  CI Windows x64, release run 40096586191 (eed64d22): Go to Source's click opened no file ("Expected: …\MainWindow.axaml,
  Actual: null"). The app's inspector took the adorner layer its window makes for the first outline (Save's) as its
  controls changing, so 300 ms on the window asked for the tree and Delete's details again, and details shown again
  rebuilt the Go to Source link: the press went to the old link and the release to its copy. Traced on the Mac, that layer
  was the only change told; with the inspector's 300 ms stretched to 1.5 s and the release held 2.5 s, the Mac and the
  Windows VM failed as CI did. **A race in the IDE**: the same details again leave the window as it is, and the inspector
  tells no change for its own adorner layer (d5c649ff).
- `ProblemsTests`' `A_typing_pause_lists_again_only_the_open_files_problems_however_many_the_solution_has`, CI Linux,
  release run 40088071944 (7bb6516d), both attempts: "A typing pause allocated 192 KB on the UI thread with 4002 problems
  in the solution, 157 KB with 22: 8 bytes for each problem" (then 210 against 178), as once under load on the Mac (#431).
  The window lists nothing again: the larger solution's server answers later, so its pauses outlast more 530 ms blinks of
  the caret, each a frame of the editor (20 to 25 KB), the whole gap. Neither race: **the test** counted the blinks; a
  pause's cost is now taken less what its blinks drew, as a blink costs at rest (`Ui.BlinkBytes`, f4051d8b).
- `BuildRunTests`' `With_hot_reload_edits_reach_the_running_program_a_signature_change_restarts_it_and_it_ends_with_the_window`,
  CI Linux, release run 40088071944 (7bb6516d): "Waited 120 s for the status bar to say dotnet watch restarts the program
  … the status bar says \"Hot Reload: dotnet watch applied the changes\"". dotnet watch did restart it, but its words came
  past the 3 s the run follows them after its first (a person's window, 10 s). Neither race: **the test** run's shortened
  wait, unscaled; it is 3 s times the time scale (0f450eb7).
- `DebuggerTests`' `Debugging_a_program_that_prints_100000_coloured_lines_leaves_the_console_free_of_colour_codes_and_the_window_responsive`,
  the same run: "A key waited up to 512 ms while the program printed, against 2 ms at rest". **The test**'s 500 ms limit,
  unscaled on a runner whose time scale is 2; it is 500 ms times the scale (0f450eb7).
- `TablesTests`' `A_workbook_shows_each_sheet_with_its_numbers_formatted_as_Excel_shows_them_and_is_never_written` and
  `A_sheets_read_that_lands_after_a_later_sheet_was_chosen_never_replaces_the_sheet_chosen_last`, CI on every system
  40061362941 (ea3902a7): "Unable to cast object of type 'Avalonia.Controls.PathIcon' to type 'System.String'". The tests
  found a sheet's tab among the editor group's toggles by casting each one's content to text, and #451 gave every group
  the Markdown preview's toggle (hidden but for a Markdown file), whose content is its icon. No race: **the test**, which
  reads a tab's words as text only where they are (0c03034e).

## 2026-10-23

- `LiveTreeTests`' `A_debugged_avalonia_app_shows_its_controls_where_each_value_and_style_comes_from_and_the_control_clicked_in_it`,
  CI Linux 40051713618 (038a0f45): "A click on Button 'Go to …/MainWindow.axaml:6' lands on Border PART_LayoutRoot
  instead", on the Styles page. The cover was the tooltip of the Go to Source link clicked just before, in the window's
  own layer as headless popups are: a test's pointer jumps to each click without moving, so Avalonia hears it rests on what
  it pressed only as the window redraws under it and opens that link's tooltip 400 ms on (a person's move there is heard,
  and the press shuts the tip while the pointer stays). The runner's wait after that click outlasted it, and the run's
  short /tmp path (pid 4411) made the tip narrow enough, 252 px, to stand right of the pointer over the style's link; the
  Mac's TMPDIR path and the Ubuntu VM's 7-digit pid make it wide enough to flip left of the pointer, clear of it. Held
  600 ms after that click with CI's words in the tip, the Mac fails as CI did. Neither race nor #449's layout: **the
  test**'s click, which now shuts the tooltips its pointer leaves, as a person's move does (aec466ce).
- `FoldingTests`' `Typing_above_two_thousand_regions_moves_them_on_the_UI_thread_without_copying_each_again_and_again`,
  CI macOS 40051713618 (038a0f45): "A key allocated 88 KB on the UI thread above 2003 regions, 78 KB above 3: 5 bytes for
  each region". The window allocates nothing for a region (the Mac's means, five tries before #448 and #449 and five at
  0.2.0's head: 79.6 to 81.5 KB over either, 0 bytes a region). A key's wait is about 400 ms and the caret's blink draws the
  editor again (28 KB) in any wait past its 530 ms, as a busy runner's doc-comment keys are: seven of twenty held 600 ms
  on the Mac give the mean CI's 5 bytes. Neither race: **the test**, which averaged the keys; it takes the least of the
  twenty (a9a80b10), which those slowed keys leave at 0 bytes, and a copy of every region each key still fails at 25.
- `ProfilerTests`' `A_profiled_program_goes_on_after_its_snapshot_until_stop_ends_it`, CI Windows 39992190279 (e25b06d0),
  the test's first Windows run, and the Windows VM each time: the profiler's pipe still listed after Get Snapshot, 90 s on
  in the VM. The diagnostics library's Windows listener makes its next pipe instance as each connection comes in, after
  its own end too when the two cross, and never closes that one; the runtime connects again as each request is answered,
  the session's stop among them, just as the run lets go of its port, so the program sat connected to a pipe Passenger
  held. **A race in the IDE** (its library's): the run closes the pipe left (bcb20d4b), gone within 6 ms in the VM where
  it stayed 20 s without, and the test waits for the program's end of it to go, which Windows lists until it does.
- `AspireExecutionServerTests`' request line past the limit, the same run on Windows: "An existing connection was
  forcibly closed by the remote host" for the 414. The server closed each connection straight after answering, with the
  rest of the request unread, which resets it, and Windows throws away the answer the client has not read yet. **A race in
  the IDE**: it shuts its side once it has answered and drops what is left until the client closes (613114c1).
- `NavigationTests`' Go to File over 20,000 files, the same run on Linux: "The first letter … allocated 3550 KB on the UI
  thread, against 2937 KB over 2,000". The search allocates nothing for a file or a match (the Ubuntu VM's tries: 2,434 to
  2,481 KB over either), so the 613 KB were the window's thread doing something else while that one look was taken.
  Neither race: **the test**, which took one look; it takes the least of three (3207713e), and every match kept, as
  before #414, still fails it.
- `WorkingActionsDebuggingTests`' double click on Debug, the same run on Linux: "Building Hello.csproj…" where it looked
  for "Already under way: Working out how to debug Hello". A headless click runs the window's jobs and draws up to ten
  frames after it while the ring turns, time enough for the launch plan to land and the build to begin between the clicks.
  **The test waited wrong**, reading the status bar after the stage had moved on; it reads what was said through the
  clicks (43407c54), Run's two checks too.
- `VcsTests`' `Commit_and_stash_are_off_while_one_runs…`, the same run on Linux: "the window still lacked the commit (No
  files are chosen to commit)". The click's own frames gave Linux's git time to add, commit and read the status, so the
  second press came after the commit and was told there was nothing to commit, over "1 file committed". **The test waited
  wrong**: a pre-commit hook holds the commit until the second press is refused (7da8036d).
- `EditingBehaviourTests`' `A_key_draws_the_editor_once…`, the same run on macOS: "Key 3, a digit in a number, drew the
  editor 2 times". Each key's settle waits out the path's 400 ms outline pause, 400 to 425 ms on the Mac against the
  caret's 530 ms blink, which a slower runner's settle outlasted. **The test**, which held each settle under the blink:
  the editor counts its blinks and the test sets them aside (37fcafd7).
- `DiscordResponsivenessTests`' Discord that never answers, the same run on macOS: "Ten file switches took 5.1 s". Each
  switch's settle waits out the same 400 ms pause, ten taking 4.05 s on the Mac against an unscaled 5 s. **The test**,
  which held the IDE's own pauses to a time: it holds how long a key waits for the window's thread (0bbb464f), 11 ms on
  the Mac, a window held 1 s failing it.
- `ProfilerTests`' Attach Profiler that cannot write its snapshot, the Ubuntu VM at e2c80fa4 with the three fixes above
  (it passed in the run there before, and in the Windows VM): "IOException: The file '…/snapshots/Waits-20261023-142354.nettrace' already
  exists", from the test's own blocking of every name a snapshot could take for the next two minutes, the second before
  included. The class's tests share the Waits project's snapshot folder, and a trace the test before it began in that
  second already held the name, a file where the test made a folder. **The test**: where a trace has a name, it blocks the
  one the profiler takes next (`-2`, as `ProfilingSession.NewTrace` names it).
- `McpAvaloniaAppTests`' agent's run, drive and profile of AgentApp, CI run 39992190279 at e25b06d0, Linux and Windows (it
  passed on the Mac and in the Ubuntu VM): "profile failed: … Run's program uses the Run console: stop it first, or Attach
  Profiler to profile it". The agent's `stop` answered once it had killed Run's program, not once its end had landed in
  the Run console (`ProcessRunner` holds the console until its output has ended and its exit code is said), and the test's
  wait for the app's tools to say it ended heard the inspector's socket close first, so `profile` came while the console
  still had it. **A race in the IDE**: Stop All, an agent's `stop`, now answers once Run's program has let go of the
  console; and Profile of the configuration Run runs now ends that run and profiles it, as Debug's start does.
- `LiveRequestsTests`' requests window over the running shop, CI run 39992190279 at e25b06d0, Linux and Windows, and every
  time in the Ubuntu VM (it passed on the Mac): "Waited 180 s for the command's parameters, from EF Core's log of it". No
  log of a command came at all. The window follows a program from its runtime's start, so its session can begin before the
  app makes its LoggingEventSource, which is then told the session's FilterSpecs as it is made, in its static constructor;
  .NET 10's reads them there before its separator fields are set, splitting at white space alone, so
  "Microsoft.EntityFrameworkCore.Database.Command:Information" was one category of that name, no logger's, and every log
  was left out (the app's own rules said so, read through it). On the Mac the session began after the source was made.
  **A race in the IDE**, with the app's start: the spec names the category alone, at the session's level, which reads the
  same either way.
- `PublishTests`' Publish greyed for a library and a test project (`Publish_is_greyed_with_why…`), CI run 39992190279 at
  e25b06d0, Linux and Windows (it passed on the Mac and in the Ubuntu VM): "The Explorer does not show Tools", from the
  wait for Tools to be offered once its project asks to be published. Writing Tools.csproj makes the window read the
  solution again as the watcher tells of it (`OnFolderChanged`), which makes every row of the Explorer anew; the test found
  the row shown, then settled before its right click, and the rows made meanwhile left that row unshown, which threw inside
  the wait. **The test waited wrong**: a right click whose rows were made anew before its menu opened on them is found and
  made again.
- `DebugSessionsTests`' Hot Reload tests (`An_attach_to_a_program_started_held_for_it…` and `A_program_resumed_with_an_edit…`),
  the lead's whole-class runs at bff67d61 and 8dfc252a, whichever ran after the class's rude edit, since a4ba62e3 (each
  passed alone): "The edit was not applied as the program resumed: No managed code changes to apply", Roslyn's warning
  behind it ENC1008, "Changing source file Counter.cs in a stale project 'First' has no effect until the project is
  rebuilt". The rude edit's restart saved its signature and built First; the fixture's reset then put Counter.cs back with
  the time it was written at, before that build, so the next test's build took the build of the rude edit as up to date and
  ran it, whose PDB no longer matched the file. At a4ba62e3^ the same order passes and at a4ba62e3 both fail; #402's
  widening of Hot Reload's scope (80e93fd9, in both) was not it. Neither race: **the fixture**, whose reset puts a file
  back a moment after any build since its time, so the next build compiles it again.
- `McpProcessesTests`' agent's Local History outlasting the person's window's pruning, the lead's landing run at 76647182
  (its third process), straight after the class's killed-Passenger test failed there (#443, below; it passed alone): "The IDE
  caught 1 error during the test: IOException: Failed to send message", from the agent's client's `PipeStream.WriteAsync`.
  That test's finally killed Passenger before giving up its build call, and the MCP SDK's client, which drops its own
  cancellation when its wait ends first but sends it unawaited when it cannot resume there at once (its canceller on a
  thread with a context of its own), wrote it to the dead Passenger's input; the failed task, never observed, was caught
  by the IDE's catcher when the collector found it, in the next window test. A call given up from such a thread after its
  Passenger was killed, its output held open, showed the error every time, and none with the fix. **The test**: its
  finally gives up before the kill, as its body does, and the rig's client drops what it tells a Passenger that has gone
  (`PeerTransport`), as the IDE's own MCP connections now do, since the SDK's server also sends a failed answer's error
  unawaited, which an agent going at that moment made an internal error (not seen in a run).
- `CodeAssistTests`' right-click rename (`A_right_click_on_a_call…renames_it…`) straight after its line typed while a
  rename is out (#430), every time in that order (found in #414's first part; it passed alone): "Sub-string not found:
  Helper.Double(", the status saying "Rename was not applied: Helper.cs changed on disk as the language server worked this
  out". The test before renamed Helper.cs, a file not open, on disk, and it was written back (by that test, else by the
  fixture's reset) about 1.5 s before this test's rename, which the IDE refuses for a file not open written within 2 s of
  its request (`WorkspaceEdits.Catching`), though the reset had seen the warm server hold it as written. Neither race:
  **the fixture**, whose reset now puts a file back with the time it was written at, as the project was written.

- `McpProcessesTests`' Passenger killed while it loads, runs, profiles, debugs and builds (#443), every run on 0.2.0 since
  cd9ac656: "Assert.Subset() Failure", the program the agent's `run` started no longer among Passenger's processes when
  the test listed them. `run`'s answer had not changed: its program was running after it and after the profiler's attach,
  and was gone once `debug_start` had answered, since #408 (cd9ac656) makes Debug of the configuration Run runs end that
  run first and the test debugged the configuration it had run. Neither race: **the test**, which now runs one launch
  profile's configuration and debugs another's, so both programs run until Passenger is killed.

## 2026-10-22

- `AspireTests`' real AppHost command (`A_real_AppHost_command_observes_confirmation_cancel_and_server_validated_input_from_the_window`),
  the Ubuntu VM's run of the Aspire classes in #400's rounds three and four (47148ed7 … 20ea86de; it passed alone and in
  its class): "Waited 90 s for the AppHost's Restart API? prompt" after the test clicked the command's button. The Aspire
  window drew a resource's commands as buttons in a bar it emptied and filled again (`ShowResource`) at every update the
  AppHost streamed, which a real AppHost's health checks and logs bring several times a second; the headless platform runs
  the window's posted work between a press and its release, as a busy machine's does, so an update landing between them
  took the pressed button out and put it back, its press lost, and the click never fired: no command went to the AppHost,
  which so asked nothing. **Race in the IDE**, fixed in 2d298100 (the commands are a menu of the toolbar's one button,
  made as it opens and left alone by updates) with 74bd9ca0 (rows and links kept through updates, the same lost press on
  an address). At 20ea86de a press on a stand-in AppHost's command button with an update landing before its release ran
  nothing, where a plain click ran it; the real AppHost's test passed six times of six there and six of six since under
  four busy loops on the VM, so the straddle is rare. AspireStandInTests now press the commands button and its menu's item
  each as an update comes, the stand-in resource service recording the command.
- `AspireCodeHelpTests`, every test, five of some twenty class starts on the Mac while fixing the three below, loaded or
  alone: "Class fixture type … threw in its constructor … The file '…/Worker/obj/Worker.csproj.nuget.g.props' already
  exists". Its AppHosts' references name Api and Worker by their real paths (/private/var/folders), the solution, restored
  through the temporary folder's link, by /var/folders, so dotnet restore restored each twice at once. Neither: **the
  test's restore**, fixed in 8dad2ce9 (restored by the folder's real path). AspireApp's starter, an AppHost too, is still
  restored through the link and has not yet failed so.
- `AspireCodeHelpTests`' connection warning's fix, the Mac's full run through `just test` at 1dfdfb92 (first phase, three
  processes side by side), as once before in a loaded batch: "Waited 135 s for the native fix to add the missing AppHost
  edge". The window refused the chosen fix ("source changed; request the fix again") whenever the Aspire graph's version
  had moved since it was offered, and every `workspace/diagnostic/refresh` moves it, which the server sends for any change
  in the solution; under load one landed while the fix was offered. Another file changed while it is offered failed it
  every time. **Race in the IDE**, fixed in 84d6688a (the server works the edge out again as it is chosen, so only the
  service's own text changing refuses it; the test now changes another file while the fix is offered).
- `TestingPlatformTests`' Stop as a debugged run's test host starts, the same run (its Alone phase): "Tests passed: 0,
  failed: 0 of 0 in 1.2 s, stopped" where it expected "The tests could not run: Debugging Tests was stopped as it
  started". No race: every run since 2eb347b4 failed it, which gave the debugger's start of a test host the run's own
  cancellation, so Stop, cancelling the run before it stops the debugger, ends the run as stopped, as at any other moment
  of a run; the debugger's own word then landed after the run's in the status bar. **The test** (its words from before)
  and **a race in the IDE** (the order of the two words), fixed in 96f975ef (a start given up ends before its caller
  hears so).
- `ExplorerFilesTests`' delete the Trash refuses, the same run (first phase): "Waited 135 s for Notes.txt deleted and its
  tab closed". The test clicked Notes.txt's row again to give the Explorer the keys back after typing; when the steps
  between took less than Avalonia's double-click time, as they did on the loaded machine (one run in two under twelve
  busy loops; it passed alone), the click was a double click (its ClickCount 2), which opened the file and gave the editor
  the keys, so Backspace deleted a character there. **The test**, fixed in 5442629c (Cmd+1, Focus Explorer, gives the keys
  back).
- `ContainerRunDebuggingTests`' Hot Reload of a paused service, the Mac's 24-minute run of the container, MCP, profiler
  and debug-session classes: "The file should have '"web 1"' to replace" with Program.cs's text, the editor gone to web's
  stop just after the test had opened Message.cs. The window shows a stop once the debugger has answered its threads and
  then its frames, which was no background work: the test's wait (the session paused, and its own frames, asked after the
  window's threads and so answered first) and the settle after it opened Message.cs both ended before the window's frames
  came. Holding the window's frames back until Message.cs opened failed it the same way, and with the fix it passed.
  **Race in the IDE**, fixed in 4d1e4017 (a stop is background work until the editor is at it, so every wait for a
  quiet IDE waits for it; the test settles after the stop before it acts).
- `McpTests`' attached diagnostic shut out (`revokeConnection: True`), the same run: "An error occurred invoking
  'diagnostics'." where the agent should hear that the connection to the window ended. The proxy's MCP client fails a
  call still waiting as the connection's reader stops, and says the session is complete a moment later on another of the
  pool's threads (the SDK's code read); the proxy looked at that word as the failure came, so when a busy machine ran the
  failure first, the IOException went to the SDK, which answers any such with that line. Not reproduced: 30 runs alone,
  and 3,300 of the SDK's client hung up on mid-call over a Unix socket, idle and with the pool busy, all heard the end
  first. **Race in the IDE**, fixed in a45a1abf (a call's failure waits up to 5 s for the connection's end to be told).
- `ProtocolTests`' header past the adapter's limit (`sent: "header"`), the Windows VM: "The pipe has been ended". The
  adapter stops reading at 64 KB and ends, as it should, and Windows' pipe ends under the rest of the test's 80 KB write,
  where macOS's and Linux's larger buffers take the rest first. Neither: **the test took the adapter's end for a failure**, fixed
  in 65dded10 (a write cut short is what that header expects; the adapter's end and its log's reason are still checked).

## 2026-10-21

- `ContainersTests`' no docker or engine not running, the Mac beside two agents' Docker test runs: "The IDE was not
  quiet within 90 s", background work begun at ProcessRunner.cs:237, at its first settle. The test before it had closed
  a window streaming a container's log, and the stream's end was heard only once this test had begun: the console's
  word of it went to the earlier test's dispatcher, shut down by then, which dropped it, so the hold it was to let go of
  stayed (an end heard late, put in by hand, did it every time). **Race in the IDE**, fixed in fe11c0e7 (work the window
  will never run lets go of its hold; and a program whose output a job it left still holds is ended with that job after
  ChildProcesses' grace, as `RunAsync`'s is, rather than holding the console for good).

## 2026-10-20

- `TypeRenameTests`' form and partial class renames, the Mac, three times in four class runs once: "Form1.cs changed on
  disk as the language server worked this out", the test writing those files about 2 s before the rename it asked for,
  which refuses a file not open written within 2 s (`WorkspaceEdits.StaleAsync`). **Test waited wrong**, fixed in
  8933a612 (the files are the project's from the start).
- `TablesTests`' CSV Text, TSV copy and sheet choice, Ubuntu VM: "Sequence contains no matching element" for a segment,
  or a click a row below its cell, the table read but its bar and grid not yet laid out when the wait returned: a small
  file's read can end before its await, so `Ui.WaitFor` found it there at once and ran none of the window's jobs.
  **Test waited wrong**, fixed in 00ba1dae (Open waited for both to be arranged), then for every test in e835e3e2 (every
  wait ends with the window laid out and drawn, and Open no longer waits itself).

## 2026-10-19

- `EvalTests`' LINQ watch, CI Linux 39301973028: "The expression took too long to evaluate", a
  program's first LINQ evaluation slower than the 1 s the run gave the debugger. **Test waited wrong**, fixed in
  52bdcaf7 (the run gives 3 s).
- `CodeAssistTests`' long-file colours, CI macOS 39278571192: the whole file's colours asked twice, the server's word
  that they moved taken as newer than a request it had already read. **Race in the IDE**, fixed in b45ecf0b (where the
  request crossed to the server, not where the UI thread sent it), after e3802ba4 (the word counted as read).
- `CommandLineTests`' screenshot agent step, CI macOS 39278571192: still running after two minutes, a Terminal tab
  missing its program's end told before it listened. **Race in the IDE**, fixed in e69d3c10.
- `McpTests`' Change Signature after a write: refused as "changed on disk as the language server worked this out".
  **Race in the IDE** (the agent's call refused what the server had not yet read), fixed in e030d520.
- `McpTests`' run with Hot Reload, CI on all three systems: "its working folder .../bin/Debug/net10.0 does not exist",
  the run checked before dotnet watch's own first build made it. **Race in the IDE**, fixed in 8e487216.
- `BuildRunTests`' Avalonia hold, CI Windows 39265497664: AVLN9999, the test's stand-in leaving an empty resources
  file. Neither: **the test's stand-in was wrong**, fixed in 0f56f8c5.
- `LogsTests`' servers' end, CI Windows: a line of another program whose process id Windows had reused. **Test waited
  wrong** (it read the whole run's log, not its window's), fixed in 43b621c6.
- `UpdatesTests`' download dropped, CI macOS: DirectoryNotFoundException from the test's own look while the updater
  removed the folder. **Test waited wrong** (its look threw where it should read false), fixed in 01786a9f.
- `NewSolutionTests`' Avalonia app, CI Windows: MSB4018 on `obj/…/Avalonia/resources`, a design-time build holding it.
  **Race in the IDE**, fixed in b16f9c6e.
- `DebuggerTests`, `MemorySnapshotsTests`, macOS: "Build failed: 1 error", a design-time build making and signing the
  apphost beside the build. **Race in the IDE**, likely fixed in 070beaac (not proven; 6684f240 makes the next
  failure say the build's errors).
- `ProblemsTests`' solution-wide analysis, CI Windows: "Waited 60 s for the broken caller's error", the analysis loop
  ended for good by one refused pull. **Race in the IDE**, likely fixed in c6818c74.
- `ExplorerFilesTests`' delete of a project, CI macOS: the folder sent to the Trash came back, a design-time build
  still writing it. **Race in the IDE**, fixed in ac35de99.
- `IndentationTests`' Enter in a large XAML file, CI Linux 39245798584: the count took in the jump's frame as well as
  the Enter. **Test waited wrong** (it measured from before the jump had drawn), fixed in 48871ad7.
- `CodeAssistTests`' completion read off the UI thread, CI Linux 39245798584: "0 KB for 80 items", the list already
  open when the count began. **Test waited wrong**, fixed in 60c1e386.
- `BuildRunTests`' Hot Reload signature change, CI macOS: "Waited 60 s for the status bar to say dotnet watch restarts
  the program", its message quoting the window as the wait began. Not reproduced since; 90eecddd makes the message say
  what the window said when the wait ran out.
- CI Windows shard 2, e258c203: no test listed, xunit v3 3.2.2's runner writing after its JSON. Neither: **xunit's
  runner**, worked around in 6001a6f7.
- `XamlTimingTests`' keys while Passenger.App's types are read, CI Linux 39324137868: a key waited 255 ms against the
  250 ms bound once, on a shared runner; it passed on re-run, and the bound measures responsiveness, so the time scale
  rightly leaves it alone. Neither: **the runner's load**, not changed; again on Linux means look at the read's work on
  the UI thread.
- The v0.1.0 release run 39337400528: a shard whose tests had all passed failed uploading its results (CreateArtifact,
  ETIMEDOUT), and the publish met an HTTP 500 from GitHub's releases API (`gh release create` removed its draft).
  Neither: **GitHub**; re-running the failed jobs published the release.
- `TerminalTests`' flood of output, the Ubuntu VM's full run at bc93b815 (beside the Windows VM's full run and three
  agents' builds): a key waited 268 ms against the 250 ms bound; it passed three re-runs at 6–7 s each. Neither: **the
  machine's load**, not changed; the bound measures responsiveness, which the time scale rightly leaves alone.
- `AssetPreviewTests`' large image off the UI thread, the Mac under load: "small.png was decoded before the open
  returned". A small picture's decode on the pool can finish before the open reaches its await, which then goes on at
  once, so the open returns with the picture in place. **The test**, which held every open to still be loading: only the
  large picture's is now; the small one's off-thread decode is shown by the allocations compared.
- The Windows VM's final full run at d77d3222, run unscaled beside the Mac's and Ubuntu's: `DebuggerTests`' Continue
  test saw the program run on 110 ms after Continue against its 100 ms bound (CI's Windows scale makes that 250), and
  `BuildRunTests`' build beside an Avalonia design-time build could not delete its folder while a `dotnet.exe` still
  held it. Both passed run again on their own. Neither: **the machine's load**, not changed.
- `TitleBarTests`' menu bar titles, the same run: "The bar shows 3 of the main menu's titles at 1200 wide". The test was
  first run only on macOS, where the bar holds no window buttons; at 1200 wide Windows fits three titles beside its own,
  the rest behind the button as designed. **The test**, now opened 1600 wide.
- The Mac's full run at 509e5dfc, beside three agents' builds: `BuildRunTests`' launch profile run with arguments of its
  own lacked them. The short program had ended before `RunToEnd` looked, which then pressed Run again, running the
  profile's own arguments. **The test waited wrong**; it now waits for the run it started (`Ended`).
- The same run: `ContainersTests`' attach with tools of another version, its second attach refused "Already under way:
  Downloading Passenger's container tools for linux-arm64". The first attach's download said its stage from a worker,
  posted to the window, and it landed after the attach had ended: `ActionWork.Said` put the ended work back under way, so
  Attach to Process was refused for good. **A race in the IDE**: a stage said after its work ended now leaves it ended.
- `LongTextTests`' thousand carets, the Mac's full run at 58b8dc3f beside three agents' builds and test runs: a key took
  17.6 ms in 8,000 methods against 6.1 ms in 1,000 (the bound 17.2). It passed three runs alone straight after, with
  the load average near 10. Neither: **the machine's load**, not changed; the bound measures the UI thread's work, which
  the time scale rightly leaves alone.
- `McpAttachTests`' call nobody answers, the Mac's full run on lead-373 (d5ca7904) with the load average near 35 from
  two agents' builds and tests: the first `workspace` call through `--mcp-attach` had no answer in 60 s, where the
  window's limit closes its admission question at 5 s; the question had shown. It passed three runs alone straight
  after, the load still 25–37. Not explained: the message tailed only the test's own state, which an attached window
  never writes; it now tails the run's passenger.log and mcp.log too (the test turns protocol traces on), so a repeat says
  where the call waited.
- `McpAspireTests`' windowless Passenger's end, the Mac's full run at 6c42dff1: the agent's `run` of `apiservice`, straight
  after its `stop` and the API's process gone, was refused "apiservice … advertises no start command". The AppHost's
  resource stream still had it stopping, with no start command yet, which a loaded machine's stream says a while after
  the program has gone; two runs alone passed. **A race in the IDE**: an agent's run or debug of a resource that is no
  service Passenger runs and is not yet finished now waits, on the resource stream, up to 30 s for its start command
  (`RunAspireToolResourceAsync`), a finished one with none still refused at once.

- `McpMigrationsTests`' `An_agent_fixes_what_dotnet_ef_lacks_through_code_actions_then_adds_and_applies_a_migration`, the Mac
  under load: "The ide_action call had no answer in 90 s". A Migrations command builds the project and its startup project
  and then runs dotnet ef, which on a loaded machine outlasted the minute McpRig gave a tool that builds nothing. Neither
  race: **the test**'s limit for an action that does a build's work, now the 5 minutes build and run have (3027cd92).
- `SelfDebuggingTests`, the Mac under load (the n18/n19 landing run): "The IDE was not quiet while waiting 300 s for Passenger
  to build and start under its debugger". The window's build restored all 21 of Passenger's restoring projects again, its
  NuGet home being the run's, rewriting the checkout's restore (which the person's next build wrote back); and it compiled
  everything because that run's own build had restored six of main's projects through /Users/zaneris/documents/dev/passenger,
  where the window builds through the disk's spelling. Neither race: **the test** builds with the person's NuGet settings in
  nodes of its own, checks the restore's files unchanged, and fails first, saying why, on a checkout restored through
  another spelling (8c4fb07e).
- `CodeAssistTests`' `The_path_a_hover_card_file_structure_and_go_to_symbol_name_the_code_and_go_to_it`, the Mac under load
  (#532): "Waited 135 s for the member the caret is in", the IDE quiet. The path and File Structure asked for the
  outline as the file was shown and as the C# service's state changed; one asked before the server had the file found
  nothing. On the class's warm server the service is ready at once, before the folder's open files are opened in it, so
  a file shown while its folder was still being read (its 400 ms pause over first, as under load) was opened in the
  server with no state change after it, and the path named no member until the next edit. Opening Program.cs alone and
  then its folder fails so every time. **A race in the IDE**: the service tells when it opens a file in the server
  (`Connected`), and both ask then (5af1fc50).
