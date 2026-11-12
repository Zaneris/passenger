# Flake ledger

Each test failure on CI (or a VM) once its cause is known: the test, what it said, and which of two things it was, so a
pattern shows. A **race in the IDE** is fixed in the IDE; waiting longer or trying again would only have hidden it. A
**test waited wrong** is fixed in the test, by waiting for what it checks (`Ui.WaitFor`, `Ui.PumpUntil`,
`Waiting.Until`), never by a longer fixed time. A failure that was neither says what it was.

To add one: a line under its date, newest first, naming the test, the run, the cause in a sentence and the commit
that fixed it, in one of the forms below. A failure not yet understood goes in only once it is. Since 2026-10-28 only
the gate (`[Gate]` and the unit tests) holds a run or a release back: a gate test that failed for a test's reason leaves
the gate until it is rewritten, and a report-lane failure is an issue (`ci-report`) written here once understood.

**Where they come from** (the 111 entries of 2026-10-19 to 27, against the 1,556 tests CI ran on 2026-10-27):
- **By the kind of test:**
  - UI end-to-end, the whole IDE in a headless window (1,204 tests): 80 failures, about 7 per 100 tests.
  - Agent end-to-end over MCP (125): 12, the highest rate, about 10 per 100.
  - Debugger over DAP (122): 7.
  - Other windowless tests (105): 6.
  - No test named (a shard, a job): 6.
- **By what failed:**
  - A wrong result: 66.
  - A wait that gave up: 20.
  - A time over its bound: 15, from the few dozen tests held to a time, the highest rate of any kind of check.
  - An allocation measure over its bound: 10.
- **By cause:**
  - The IDE: 53, a bug or race a person could meet.
  - The test itself: 44, waiting for less than it checked, or reading a state that moved.
  - Outside the code: 7 (the runner, GitHub, xunit, Rosetta).
  - Unlabelled: 7.

## 2026-11-11

- `AspireExecutionServerTests`' `Malformed_or_ambiguous_wire_input_never_reaches_the_launch_callback(kind:
  "duplicate-content-type")`, CI's Windows x64 report lane at fd37313ff (run 43162817058): "One or more Aspire project
  sessions could not be stopped. (Object reference not set to an instance of an object.)" from
  `AspireExecutionConnection.CloseAsync`. **A race in the IDE**: the server's dispose cancels `shutdown`, then aborts each
  connection, disposing its `TcpClient`; a handler past `CloseAsync`'s check before the cancel then read `client.Client`,
  null once the client is disposed, a null the handler never took for the connection's end as it does an
  `ObjectDisposedException`. `CloseAsync` now reads the socket once and ends where there is none (#1509).
- `AspireStandInTests`' `A_confirmation_cancelled_and_a_form_the_AppHost_updates_send_what_the_dialog_shows`, CI's
  Windows x64 report lane at 053101b37 (run 43132648654): "The IDE caught 1 error … NullReferenceException" at
  `Process.GetProcessHandlePairs` < `Process.Kill(entireProcessTree: true)` < `ChildProcesses.KillTree` <
  `ContainerEngine.Unfollow`. **A race in the IDE**: the last follower's going killed `docker events` outside the lock,
  and its reader, hearing the output end as the kill ended the program, disposed the same `Process` while .NET's tree
  walk still read it at each process on the machine. `Close` nulls the handle before clearing the flag that says it
  has one, so a read between the two threw this; a read after it threw "No process is associated with this object",
  which `KillTree` took for an end and dropped, leaving what the child started running. On the Windows on Arm VM, a
  `cmd` with two `ping`s killed while a thread disposed it on its exit cut the walk short in 217 of 300 rounds, 434
  pings left running; with no dispose, beside four threads starting short-lived programs, 30 of 30 ended whole.
  `ChildProcesses.Kill` on Windows now holds the child's handle before anything ends and walks the tree itself
  (`ProcessTree`): each process held by a handle of its own until the walk is done, taken only where it names its
  parent's id and began no earlier, each level waited out (2 s at most) before the next is listed, and one Passenger may
  read but not end walked through, so nothing done to the `Process` meanwhile cuts it short;
  `A_child_disposed_on_another_thread_as_it_is_killed_has_what_it_started_ended_too` fails on the old code on the VM
  (#1504).
- `TestingPlatformBytesTests`' `A_runs_results_cost_at_most_twice_their_JSON_to_read`, CI's macOS report lane at
  053101b37 (run 43132648654): "Reading a run's 2,000 results of 1,179,955 bytes of JSON allocated 2,370,080 bytes (2.0×
  its JSON)", 2.01 times, where CI's macOS runs had measured 1.44 to 1.46 (Linux 1.61 to 1.97, Windows 1.49 to 1.63).
  **The IDE**: what a run cost hung on the shared pool. Its request of 2,000 tests (about 261 KB) went out through
  StreamJsonRpc's header handler, which asks its writer for one span of a message's whole content, so the pipe
  Nerdbank's UsePipeWriter made rented a 256 KB array from the shared pool for each run and gave it back on the thread
  that copied it to the socket; a run whose writing thread found none in its own slot or the pool's stacks (one it never
  gave one to, or after a gen-2 collection at a memory load past the pool's high mark, which clears every thread's slot)
  allocated one anew, as the builder of the message's 4 to 32 KB pieces did its own. On this Mac main measured 1.42 to
  1.77 times over 68 runs, 3 of the 4 high ones of 40 sampled taking a fresh 256 KB array, and with 3 processors and the
  GC's high-memory mark at 16 %, as a 7 GB runner deep in a shard, 1.43 to 1.84 times, 9 of 30 taking one; CI's least of
  three carried about 655 KB more than its usual run, as two such arrays and more of the pieces would (the run kept no
  allocation trace). Beside that, every result read its uid only to look its test up, and its display name and traits
  anew, 0.5 times the JSON in all. A conversation now writes through one buffer it keeps (ContiguousWriter), a run's
  nodes read their uids and names as their tests' own strings and share their traits, and its tests are taken without a
  grouping's arrays: 0.84 to 0.96 times on this Mac (0.85 to 0.97 so held, with no large array), 0.86 to 0.90 on the
  Windows on Arm VM and 0.91 to 0.97 on the Ubuntu VM, where main measured 1.45 and 1.53 (53a971bf6, #1506).
- `StylesheetClassesTests`' `A_cdns_class_completes_in_a_component_only_once_settings_allow_its_download_beside_a_packages_and_the_components_own`,
  CI's macOS report lane at 053101b37 (run 43132648654): "Expected: 1 Actual: 2", the stand-in CDN asked twice for
  bootstrap.min.css once Settings allowed downloads. **A race in the IDE**: an ask of a stylesheet that stopped waiting
  took the download it waited on out of those under way, though it ran on for the others. The completion began the
  download and the diagnostics pull the settings change made waited on it; a later pull cancelled that one, and, finding
  neither the copy, not yet written, nor a download under way, began a second; two asks at once could also each begin
  one through `GetOrAdd`'s factory. On the Ubuntu VM, the stand-in answering after a second and a later pull begun as it
  waited, the old code failed so: pull #4, cancelled 1 s in, removed the download it waited on, and pull #5 began
  another 76 ms later. A link's fetch is now one task for the folder that every ask takes and none cancels, made again
  only once it ended without a copy, and a download under way one task for every window, let go only once it ended;
  there the cancelled pull left the fetch to the next, which took it, and the CDN was asked once (a7144a0a5, #1505).
- `DebuggerTests`' Continue pressed while a stop's values are read, CI's Linux x64 report lane at a79ff0869 (run
  43083723699): "slow.Getter never showed 1500 as a watch: the watches were slow.Getter = error: An evaluation that
  could not be stopped is held unfinished on this thread…". **A race in the IDE**: Passenger's Hot Reload hook, in every
  program it debugs, started its thread before Main and returned, the thread then compiling its loop, loading
  System.Net.Sockets and making .NET's Socket while Main ran; on macOS and Linux that Socket's first use runs
  SocketAsyncEngine's class constructor, which starts .NET's ".NET Sockets" thread. The program's first stop caught both
  starting (the hook's thread in EmptyArray<SocketAsyncContext>'s class constructor under SocketAsyncEngine's, the new
  thread at its StartCallback), and the stop's first evaluation, which runs its thread alone, waited outside managed
  code, before the method it ran, past its limit: #599's symptom, then from a thread the test's own program had just
  started. On this Mac the old hook's engine log shows it loading 8 assemblies (System.Net.Sockets to
  System.Diagnostics.DiagnosticSource) and starting that thread after a stop on Main's first line, and a stop there
  finds its thread still at its start, its lambda under StartCallback. Not met again in 15 runs of the test and 100 of
  its program at stops aimed across the hook's setup on the Ubuntu VM beside 4 burners, nor on this Mac with a program's
  own thread held at each assembly load of its Socket's setup, so which lock the evaluation waited on is not pinned. The
  hook now connects with the system's own socket calls (no .NET Socket, so no thread of .NET's) and makes each of its
  own calls of reading and answering before Main goes on, which it does once the hook's thread is about to wait in recv
  or after 2 s of the clock, the time a debugger holds the program included; finding, reading and calling the program's
  handlers is still first done at the first update.
  `A_program_stopped_at_once_finds_the_hooks_thread_waiting_in_its_read_with_no_thread_of_its_beside_it` finds the
  thread in its call of the system's receive at a stop on Main's second line and fails at once on the old hook; on the
  VM beside 4 burners it and the failing test each passed 10 of 10 (d66095e9b, #1495).
- `SolutionViewTests`' `New_file_new_folder_and_paste_make_again_only_the_rows_of_what_they_make_or_move`, the gate on
  the Windows 11 on Arm VM at a79ff0869, b5fef9c55 and 981a2042e (6 of 6 runs): "Cut and Paste made again rows other
  than those of what it changed: [Archive] are new". **A race in the IDE**: the Solution view showed a folder the
  project holds nothing in only while it was empty on disk, so Archive, just made by New Folder, stood by that alone.
  Line.cs pasted into it, Windows' watcher named Archive and Lines too (a folder's entries changing writes the folder),
  and its batch had the Explorer read again the folder above them, Models, by the project's evaluation from before the
  move, which left Archive out as neither empty nor the project's; the evaluation landing made its row anew. On the VM
  the batch came 218 ms into the step and the evaluation some 80 ms later; with the watcher's gather made 1 s, the old
  code passed there, which is how CI's faster x64 runners pass it, their evaluation landing within the 150 ms gather. A
  folder pasted into a folder just made failed so on this Mac too, the paste itself reading Models again. A folder shown
  empty now shows until the project is evaluated again, and the watcher has the Explorer read again only the folders
  holding what came or went; `A_folder_cut_and_pasted_into_a_folder_just_made_keeps_that_folders_row` fails on the old
  code on this Mac (36be742c2, #1501).
- `PauseTests`' `A_condition_that_calls_a_method_stops_a_parallel_loop_on_its_matching_iteration`, CI's Linux gate at
  b5fef9c55 (run 43112936183): "Sequence contains no elements" at `DebugSession.LocalsAsync`, the stop a breakpoint's
  and its thread's top frame of no scope. **The debugger let a thread run beside an evaluation**: a condition's
  evaluation holds every thread but its own, but one the runtime told of only as the evaluation let the program go (the
  loop's next worker, starting as the breakpoint stopped it) was never held. It reached the breakpoint, whose event waits
  for the evaluation's end, and ran on past it, so the event was tried where the thread then stood, outside the lambda:
  `i` was not found there, and the breakpoint stopped saying the condition could not be evaluated, in code with no
  readable local. On the Ubuntu VM beside two busy loops, 30 runs of the loop with each thread's start logged met it
  once, as CI did: thread 1898295 made during an evaluation, put off at the breakpoint 3 ms later, tried 57 ms later in
  `Environment.get_TickCount64` < `Parallel.CheckTimeoutReached` < `ForWorker`, "The name 'i' does not exist", top frame
  of no scope; six threads made during an evaluation reached the breakpoint unheld in those runs, four of them twice. A
  thread made during an evaluation is now held until it ends, unless every thread was let go: 30 runs there since held
  53 such threads and stopped right every time, and a condition that starts a thread past a breakpoint, which stopped it
  1,188 turns past its first pass before, stops it on that pass (678bc540e, #1500).
- `GodotUnkeptTests`' `Each_ended_games_unkept_values_are_held_until_they_are_kept_or_discarded`, CI's macOS report
  lane at 38761732e (run 43121748961): "The IDE caught 1 error … InvalidOperationException: Not listening" at
  `TcpListener.AcceptTcpClientAsync` in `GodotGame.ServeAsync`. **A race in the IDE**: `Listen` starts the listener and
  hands `ServeAsync` to the thread pool, and a game that ended first was disposed, `Dispose` cancelling `ending` and
  stopping the listener before the accept was reached, which then threw what the loop never took for the game's end.
  `ServeAsync` now takes it so once `ending` is cancelled, which `Dispose` does before it stops the listener (#1502).
- `McpRefactorTests`' `An_agents_code_action_renaming_a_class_takes_its_file_along_and_one_renaming_a_file_in_letter_case_renames_it`,
  CI's Windows x64 report lane at b5fef9c55 (run 43112936183): "The language server still holds Crate.cs by its old
  name, offering: Rename file to Crate.cs [refactor]; ...". **A race in the IDE**: the extension's loader marked a project
  once a load left out a file renamed in letter case alone (Shelf.cs), and the project's next load, whatever brought it,
  took the mark and loaded every file as given. On the loaded runner the agent's Rename file to Crate.cs moved crate.cs
  while the Fix name violation's loads were under way, so the load after the mark found Crate.cs beside the crate.cs
  the project held, which the server refused ("has already been added to this project"), and no later load came. On
  this Mac a file moved 0.6 s after the fix failed that load the same way. Each load now judges such files from what
  the project holds as it runs, and the failure gives the IDE's and the server's logs (0496a3cfc, #1494).
- `NuGetTests`' `Update_all_stops_when_a_provider_check_handles_an_update_that_fails`, CI's macOS report lane at
  7f6f7db47 (run 43037078657): "Assert.Contains() Failure: Sub-string not found / String: "Updated 0 package references;
  App: /private/var/fo"··· / Not found: "failed"". **The IDE left dotnet's last lines out of a change's words**:
  `PackageManager` raised each line it heard through a post to the window at Default priority and returned once the
  reading had ended, while its await of dotnet's exit came back at its own context's priority (Send in a window test).
  With the window behind as dotnet ended, the exit's continuation ran before the queued lines, found the reading over,
  and the change stopped hearing `Line` before they ran: App's "Failed to restore ..." reached the log alone, and Update
  All's status gave the restore's NU1101 line as dotnet's last words. On this Mac a probe of the run showed the lines
  run at Default and the exit's continuation at Send, dotnet restore ending some 30 ms after its last line; with the
  window stalled at the NU1101 line, "Failed to restore" came during the stall and both runs failed with CI's message,
  the status "...; App: .../App.csproj : error NU1101: Unable to find package Passenger.Missing. ...; App.csproj put
  back as before". The lines heard now go through a queue the posts drain, and the run drains what is left before it
  returns; `A_failed_changes_words_are_dotnets_last_line_even_where_the_window_fell_behind_as_dotnet_ended` stalls the
  window in a job of its own at that line and fails on the old code with it as the change's words (020acfd6a, #1492).
- `PauseTests`' `Pause_stops_a_looping_program_in_its_loop_and_a_second_pause_after_continuing_does_too`, CI's macOS
  gate at c599ba911 (run 43015461601): "Assert.EndsWith() Failure: String end does not match / String: null", the first
  pause's top frame with no source. **The test waited wrong**: it paused once LoopingConsoleApp said "looping", which the
  looping thread wrote itself just before its loop, so a pause sent as the line arrived could find that thread still in
  the write. On this Mac, of 427 launches on .NET 10.0.14 paused as the line arrived, under several loads, one (40
  lanes of fresh 30 ms busy processes) found it in `Interop.Sys.Write` < ... < `Console.WriteLine` < `<Main>$` at line
  123, 3 ms after the line reached the adapter; 3,900 pause-and-continue rounds of the loop itself all stopped on its
  lines, and 343 runs of the test on .NET 11 under load passed. With the write held 300 ms after its bytes went (a
  `write$NOCANCEL` interposed into the debuggee alone), 10 of 10 launches stopped there and the test failed with CI's
  message, as did the sleep tests' steps back to the loop; the IDE and MCP show such a stop at the first frame with
  source, Main's. Another thread says "looping" once the loop has turned, seeing a field the loop sets (the loop still
  calls nothing); with the write held, every test waiting for the line passes. The Debug console's pause test said it
  the same way and says it so now, and `AssertInLoop` names the frames a failure stood in (7463124f0, #1490).
- `NewServiceSetupTests`' `New_Project_of_an_MVC_app_keeps_its_folder_for_Undo_without_its_bytes`, CI's macOS runner at
  09b6aa8de (run 43005926642): "New Project's Undo held 2253880 bytes of the history for a folder of 9798159 bytes in 78
  files". **The test measured the process, not the command**: it read `GC.GetTotalMemory` before and after clearing the
  Undo history, so what anything else let go of between the two readings counted as the command's; `ListedNamesTests`,
  run beside it, parsed every source file of src for 14 s around it, and `Debugging.ReckoningTests` ended a test 0.4 s
  before. The command holds the solution's 160 characters before and after and the folder as its 78 files' lengths and
  times: 15,240 bytes walked, 13,936, 6,656 and 4,952 by the old measure on this Mac, and 2,254,632 by it with
  2,253,880 bytes let go of elsewhere between its readings. Not met beside those classes on this Mac, where
  `ListedNamesTests` ends before the readings. The test walks the command (`HeldBytes`), which fails at 9,790,288 bytes
  on a command made to keep the folder's bytes, and `GC.GetTotalMemory` is banned in the window tests (ef07a7feb, #1489).
- `AspireOrchestrationTests`' `A_hosting_package_is_added_beside_one_the_AppHost_has_commented_out_…` and
  `Orchestration_added_under_an_open_solution_filter_…`, CI's Windows runner at 09b6aa8de (run 43005926642): "The process
  cannot access the file '…\Shop.ServiceDefaults' because it is being used by another process" in the class's constructor,
  right after `Adding_orchestration_creates_the_missing_projects_and_one_global_undo_and_redo_…` passed; the next test's
  reset, 40 ms later, removed it. **The test removed too soon**: the language server a closed window leaves for the class's
  next test (`WarmServer`, the tests' alone) restores the solution once Redo brings the projects back, and its `dotnet
  restore Shop.slnx` works in each project's folder as it reads it, while the reset removed what the test added before it
  ended that server. On the Windows VM, the folders sampled through that test showed the restore from half a second before
  the window closed to two seconds after, in Shop.ServiceDefaults at its end. The reset now ends the server first where a
  test added files or folders, and waits out what it had started as they go;
  `The_next_tests_reset_removes_the_projects_orchestration_made_while_…` holds the restore in ServiceDefaults and fails on
  the old reset with CI's words (030d74ca8, #1488).
- `LongFileAssistTests`' `Typing_with_pauses_in_a_file_of_twenty_thousand_lines_asks_for_the_screens_colours_and_the_…`,
  CI's Windows runner at 4499c3ecc (run 42854165103): "The whole file's colours were asked for 1 times while typing went
  on with pauses", its one `semanticTokens/full` 11 ms in, before the first statement's range at 322 ms. **The test
  counted the opening's ask**: it waited for the server's colours on the file's last local through the highlighter, which
  takes every read for lines drawn, so the text as opened looked drawn from its top to its end, and a word that the
  server's view moved, read as the test's settle ended and handled at the first key's turn, asked for the whole file at
  once (an ask after the idle, which no settle waits for, would look the same). Met every time on this Mac with that
  word posted just before the first key ("9 ms --> full, 332 ms --> range", CI's trace); after a key drawn, the same word
  asked for the screen alone. Typing never asks for the whole file: a word between a key and its frame asked for the
  key's lines (6 to 8). The test types a key and takes it back, then waits until the colours ask nothing more, before
  counting (5c4d87b11, #1463).
- `TerminalResponsivenessTests`' `A_paste_into_a_program_that_reads_nothing_leaves_the_window_taking_keys`, CI's macOS
  runner at 52ee34043 (run 42904209048): "Waited 20 s for the start of the paste echoed", the loop let go before the
  wait. **The test waited wrong**, but that does not explain it all: it pasted once the loop's command stood above the
  cursor, and bash's line editor writes the line's end before it gives the terminal its echo back, so a paste between
  the two lands unechoed, the pseudo-terminal holding it (its write waiting in the kernel, the command's 135 bytes alone
  out) until a program reads it. On this Mac under 16 busy loops, 52 of 530 rounds of the test's steps had no echo while
  the loop ran, 8 of 100 for a full 10 s, as `A_terminal_whose_program_read_none_of_a_paste_…` waits with its loop
  running; none of 150 with the wait for the shell's own word that the loop runs. But every round met echoed once its
  loop was let go (and 150 of 150 with the loop let go at once, as this test does, and 10 runs of the class), so CI's
  20 s after the loop's end are not reproduced; one round of 60 waited 90 s for a new shell's prompt, not met again in
  340. Both paste tests wait for that word, and their wait for the echo now says what the screen shows (1f9ba893a,
  #1471).
- `ProjectEvaluationTests`' `The_project_of_the_file_shown_and_those_it_references_load_in_the_language_server_before_…`,
  CI's Windows runner at 9431c8f49 (run 42986237634): "Sequence contains more than one element" in its `ServerLog`.
  **The test found its log by name**: it looked in the run's shared log folder for `server-*-{process id}.log`, and
  Windows had reused an earlier window's server's id, so two logs answered. Every test now reads the server's own
  `LogPath` (9dd051f15, #1485).
- `NuGetTests`' `Update_all_stopped_by_an_edit_to_a_central_file_names_it_and_leaves_ef_cores_set_whole`, CI's Linux
  runner at 9431c8f49 (run 42986237634): the status said "Updated 5 package references", not that
  examples/Directory.Packages.props changed. **The test edited at the wrong moment**: Update All takes projects in the
  order of their paths by `PathNames.Comparer`, ordinal on Linux, where Lib sorts before examples/One; the hook edited
  at the fourth save, so there One's, before One wrote the file, and the run rightly took the edit as the file it
  started from, keeping it and Undo keeping it too. Met every time on the Mac with the folder named to sort after Lib
  (the run's log: App, Lib, One, Two); the hook edits once the file's text shows the run wrote it, and the folder is
  Examples, so every system takes Lib last, which the test checks (8aff4b180, #1486).
- `GDScriptRefactorCommandsTests`' `Each_refactoring_changes_every_script_…`, CI's Windows runner at 61d99a589 (run
  42966878050): main.tscn "being used by another process" to a read sharing everything, in the wait after Undo. **A gap in
  the IDE, and the test reading during the writes**: ReplaceFile refuses even such a read for a moment while it puts a
  file in place, and a save took it for files with nothing a move loses, as `WindowsFileMetadata.MoveKeeps` read a list
  of unmarked entries (what a folder whose list is not marked auto-inherited gives a new file, as the VM's temporary
  folder does) as one of their own. On the Windows VM every read refused while polling through the test's writes began
  inside ReplaceFile (16 and 19 a run; none once such a file moves, 121,526 opened), and a move refused none while
  44,242 opened; no holder was named, the Restart Manager finding none by the time it looked, and CI's folder was not
  seen. A list a new file there is given counts as the folder's (be1efd499), and the test reads once Undo says it is
  done (e630387ec, #1481; #1434 met the same hold, read then as its own read's sharing).
- `DebuggerTests`' `After_a_watch_runs_with_every_thread_let_go_threads_and_call_stack_show_where_the_threads_are_now`, CI's
  Linux runner at 61d99a589 (run 42966878050): "Waited 180 s for Holder's frame in the lock in Call Stack", Holder's
  frames ending in `ManualResetEventSlim.Set` at line 176. **The test's debuggee raced**: Holder's `Set` woke the main
  thread before it returned, and the main thread reached its breakpoint while Holder was still in `Set`, a line short of
  the sleep the test looks for, where the stopped program held it; Call Stack showed where it was. The main thread now
  goes on only once Holder is on its sleep's line, and the test passed on the Windows VM with Holder made to wait 700 ms
  before that line (50b591863, #1480).
- `FolderWatchTests`' `A_saves_temporary_file_there_as_the_folder_is_read_is_never_offered_by_go_to_file_find_in_files_…`,
  CI's macOS runner at 76cfe6054 (run 42957469387): "Find in Files finds class Shop in [], a save's temporary file among
  them". **The test waited wrong**: it set the search box's text and searched at once, but the box tells of a new text
  after the frame, which put that search off until a pause and dropped its matches; the test passed on the search the
  pause brought, which its wait settled on, except where its own search had ended on the thread pool before the
  window's thread awaited it, so the wait ended at once and its closing draw ran the box's word. Read from the code (the
  run kept no log, nor the popup's status) and met every time with the search made to end before the window's thread
  looks, the popup saying "Searching…"; the test tells the box first and waits for the matches shown, through
  `Ui.Search`, which the other tests' copies share now (943044337, #1476).

## 2026-11-10

- `ProblemsTests`' `A_pull_of_thousands_of_warnings_decodes_each_at_a_small_cost_and_never_copies_a_files_text`, CI's macOS
  runner at 970d75b30 (run 42948909557): "allocated 10,267 KB for 2001 warnings, 2,754 KB for 201 in the same files: 4,274
  bytes for each warning more". **The test counted other tests' allocations**: it counts every thread's, and its dense
  pulls ran beside DebuggerRulesTests and AgreeingWordsTests reading the repository's sources. Alone on the Mac a warning
  cost 2,750 to 2,877 bytes over seven runs; in one process with those classes it failed 2 of 2 at 4,545 and 5,012. It
  runs alone now, in `SolutionAnalysisBytesTests`, its bounds as they were (3d5867b18, #1474).
- `McpIdeTests`' `An_agent_finds_text_in_the_windows_scopes_reverts_a_file_through_local_history_…`, CI's macOS runner at
  eafb1315c (run 42934072614): "Helper.cs's history after two saves": Change, External change, External change, Change.
  **A race in the IDE**: the watcher's word of the first save, read on Local History's queue as the second save wrote,
  found that save's bytes before its record came and kept them as an external change, and the save's record, finding the
  history knowing what it wrote, kept an external change back to what it wrote over beside it. Read from the sets'
  numbers and order (the run kept no log) and met every time with the read held until the next save is written; a save
  the history holds already as an external change of its very bytes now takes its place (570069a9e, held by
  `LocalHistoryTests`, #1472).
- `XamlTests`' `An_elements_field_renamed_from_the_code_behind_a_moment_after_its_xaml_was_written_…`, CI's Linux runner at
  c1cbb2f26 (run 42884859818) and 65208647c (run 42914645058): "did not say it waits … (the XAML's change was held unsent
  to the server): the status says Renamed in 2 files". **The test held too little**: Roslyn's server takes a closed
  file's change as a text it reads only when next asked, and the warm server the test took up had been told of
  MainWindow.axaml (saved by the test before, put back by this one's reset) after its last read, so it read the test's
  write unheld and knew the new field; the rename rightly renamed at once. Read from the code (the runs kept no log) and
  met on the Mac by a word of the XAML told just before the write; the test and the three others holding the XAML's word
  start on a server of their own that holds it as on disk and was told of no change of it (fdc728b10, #1468).
- `NoDotnetTests`' `Install_dotnet_SDK_asks_then_installs_the_SDK_from_the_feed_…`, CI's Windows runner at 52ee34043 (run
  42904209048): "Access to the path '…\user-dotnet\.passenger-install-…' is denied", move 9.0 s. **A race in the IDE**: a
  reader of the unpacked SDK (a scanner, or the IDE's own walks, the install folder lying inside the open one) refused a
  folder's rename, which Directory.Move throws as an IOException, and `FolderMoves.Renamed` took that for a move between
  volumes, so the folder was copied beside its place and the copy's own rename was refused in turn, with nothing to wait
  it out; only another volume's refusal is carried now, the rest coming in entry by entry (a09975fa2, held by
  `FolderMovesTests`, #1470).
- `WelcomeTests`' `Clone_Repository_asks_for_the_login_…` (run 42835557169) and `VcsTests`' `Push_to_a_remote_that_asks_for_a_login_…`
  (run 42884859818), CI's Windows runner: "The IDE was not quiet within 225 s", git's askpass helper still running with its
  password prompt. **A gap in the IDE**: the IDE read a question to the helper's close of its side of the Unix socket,
  and Windows can leave that close unheard (the socket readable to select, no byte left, its pending read never ending),
  while the helper waited for the answer; a loop of clones on the Windows VM stopped so at the 20th, 49th and 94th. Each
  end now reads the other's words to an end byte (8a490c35d, held by `AskPassHelperTests`, #1455).
- `McpRefactorTests`' `An_agent_refactoring_right_after_git_changed_and_deleted_files_…`, CI's Windows runner at 2b79cb29e
  (run 42767140366): "Rename failed: Circle.cs could not be written: Could not find file". **A gap in the IDE**, read from
  the code (the run kept no log): a deleted file was found only by its folder written within 2 s of the request or the
  watcher's word, and a loaded machine can give neither; an edit to a closed file not on disk now waits for the server to
  let go of it (8e5507804, held by `WorkspaceEditsTests`, #1444).
- `AspireCodeHelpTests`' `Renaming_a_resource_…`, every system at 2b79cb29e (run 42767140366): IndexOutOfRangeException in
  `ServerDiagnostics.Drop`. **A bug in the IDE**, not a flake: its loop ran to a reused list's length, past the
  diagnostics kept (bc60b9e07, #1443).
- `ContainerRunProfilingTests`' `A_container_runs_dotnet_service_has_its_request_duration_charted_…`, CI's Linux runner at
  d69f16701 (run 42615378243) and 2b79cb29e (run 42767140366): nothing charted, the lookup finding no .NET service of the
  folder's run. **The test chose wrong**: ContainersTests' listing test left its project, which has a web service, up for
  the rest of the run, and the test chose the first web row; that test takes its project down and the container tests
  choose their own project's rows (2873f0e90, #1436).
- `MetricsPaneTests`' `A_runtime_that_takes_no_session_…`, CI's Windows runner at ddee64a65 (run 42712131206): "it asked
  again", a third connection. **The test counted another process's**: Windows' pipe names are machine-wide, so it counts
  only its own process's connections (1ff23f34c, f1fc653f6, #1442); the pane's start waiting at a silent runtime is given
  up once let go of (f2402e6c5).
- `FlingTests`' `A_fling_through_lines_of_many_colours_…`, CI's macOS runner at d69f16701 (run 42615378243): "Waited 180 s
  for the runtime's word of the finalizers it ran". **The test's listener dropped it**: an in-process EventListener's 10 MB
  session drops what overflows unsaid; it opens a sized, filtered EventPipe session and fails on a lost event (d017b5ed7,
  f8d3a2699, #1435).
- `GDScriptRefactorCommandsTests`' `Each_refactoring_changes_every_script_…`, CI's Windows runner at d69f16701 (run
  42615378243): main.tscn "being used by another process". **The test held the file**: its `File.ReadAllText` poll met
  the IDE's write; it reads as the IDE does (`Ui.Shared`, 9e93fbdd5, #1434).
- `ExplorerFilesTests`' tests of a loaded project's build output, every system at d69f16701 (run 42615378243). **Roslyn's
  server 5.13** folds a nested project's watches into its folder's (d6f3ff891); then on Windows a move during the server's
  start met watches made after it let go, refused by Windows; the folders let go of hold for every server's watches made
  meanwhile (41363a3fa, #1433).

## 2026-11-09

- `VcsTests`' `An_edit_shows_a_change_bar_and_committing_it_from_the_commit_window_clears_it`, every system at 42e825d99
  (run 42571668028): "the window still lacked the commit message to have the keys". **A bug in the IDE**, not a flake:
  #1369's scroller placed the message in the tree only at its first layout, after Commit asked for the keys; they are
  asked for once laid out (e0de905b5).

## 2026-11-07

- `DebuggerTests`' `A_watch_waiting_on_a_lock_another_thread_holds_runs_again_…`, CI's Linux runner at 4004896c7 (run
  42400907211): "Locked.LetGo never showed true as a watch". **A race in the test's program**: it set `LetGo` after
  leaving the lock, and the watch's end, the lock taken, can stop it before that line; it is set inside the lock now
  (dc8b33690, #1366).
- `ListedNamesTests`, every system at 4004896c7 (run 42400907211): Install .NET SDK's holders joined with " and " of
  their own. **The rule catching new code**, not a flake: listed through `Plural.Listed` (dc8b33690, #1364).
- `McpAspireStandInTests`' `An_agent_reads_a_services_request_duration_through_telemetry_metrics`, CI's Windows runner
  at 7150c471f (run 42391194839): "its runtime took no session in 25 s (process 2584 runs, … the pipe … is there but
  answered no other request either)", web never at `app.Run()`. **A hang of .NET 10's**, met by the IDE: a metrics session
  begun as a program starts deadlocks its EventSource lock against the program's first `ArrayPool` rent
  (dotnet/runtime#119014, fixed in .NET 11), proven by dumps in the Windows VM on 10.0.14; a live session now waits out a
  pre-11 program's start (`ProgramStarts`, 1f368d4ef, #1340).
- `NoDotnetTests`' `Install_dotnet_SDK_asks_then_installs_…`, CI's Windows runner at 7150c471f (run 42391194839): held 3
  minutes moving `shared`. Windows refuses a folder's rename while any file under it is open; the runner's Defender is
  off, and the IDE's own Local History reads what arrives in the open folder, where the test installs. **A gap in the
  IDE**: a folder refused whole comes in file by file, what makes it count to the host last (9fdbc8d99, #1364). The entry
  of 00200fd83 below was the same, its raised limit no fix.
- `UpdatesTests`' `A_newer_release_is_announced_once_…`, CI's Windows runner at 8252640fb (run 42361075365), a gate test:
  "the status bar's notice is ''" was a hidden notice, null quoted as `''`, not an empty one. The window's first check
  came to nothing, most likely past the test's 1 s feed limit, unscaled, as the process's first window test with a
  debugging test beside it. **The test**: the limit is `ReleaseFeed.ShortTimeout.Scaled()`, and a failure names the notice,
  the status and the log's checks (cbbe3fc2b, #1357).
- `JsonTextTests`' `Values_as_deep_as_are_read_read_whole`, CI's Windows runner at 8252640fb, a gate test: "nested too
  deeply to read at 2395". A Debug build on Windows x64 runs a 512 KB stack out at 479 levels of `{"a":`, so the stack
  check refused short of the 512 cap. **The IDE's cap**: JSON reads 256 deep (5220006a1).
- `XmlTreeTests`' `Elements_as_deep_as_a_tree_holds_…`, CI's Windows runner at 9d9a8a504 (run 42354875977), a gate test:
  "Stack overflow." ended the unit tests' process. The XAML formatter took about five frames a level, and 256 levels
  fit in 512 KB only on macOS's and Linux's frame sizes. **A gap in the IDE**: every walk checks its stack at each level
  (`StackLeft`, d05addcc5).

## 2026-11-04

The one-off report-lane failures of the 0.3.0 PR's CI run 41847036147 (#1268):
- `WelcomeTests`' `File_Recent_Solutions_lists_the_others_and_opens_the_one_chosen_with_its_recent_files` and
  `Open_takes_the_solution_chosen_of_a_folders_several_and_Recent_Solutions_keeps_and_reopens_that_one`, CI macOS:
  "Collections differ … hn37q6pj7sj_9dmqqc… / hn37q6pj7sj__9dmqqc…". A native menu's words go through
  `MainWindow.NativeWords`, which doubles underscores so the menu shows them as written (#1147); the tests compared the
  items' headers with the paths as written, which only a run folder with an underscore in its path, as that runner's
  TMPDIR had, tells apart. **The test**: it expects the menu's words (with TMPDIR under a folder named "_" it failed so
  on this Mac before, and passes after).
- `TemplateFamiliesTests`' `New_Solution_opens_with_every_family_listed_at_once_the_templates_read_ahead_and_the_search_finds_one_by_any_of_its_Types`,
  CI macOS: "Collections differ at index 1: Godot 4 Game / Probe Host App". xunit ran it right after the class's
  ide.host.json test, whose own template's family was still in the list a dialog shows at once (the last read, which
  `ProjectTemplates.Forget` keeps). Not the entry of 2026-10-31. **The test**: the ide.host.json test reads the templates
  again once it has uninstalled its own (the two ordered so on this Mac, it failed every time before and passes after).
- `McpNavigationTests`' `Go_to_declaration_into_a_project_a_solution_filter_leaves_out_opens_its_source_at_the_declaration`,
  CI Windows: "Expected ("Lib/Greeter.cs", 7), Actual ("Lib\Greeter.cs", 7)". MCP answers a path relative to the folder
  in the system's separators. **The test**, written on the Mac: it expects `Path.Combine`'s (passes on the Windows VM).
- `McpContainerDebuggingTests`' `An_agents_debug_start_debugs_each_service_of_a_compose_configuration_its_container_action_goes_through_the_run_and_debug_attach_reaches_a_program_in_a_container`,
  CI Linux (the one runner with a container engine): telemetry of a run collecting nothing "answered: … (Edit
  Configurations' Collect logs and traces)". Since 5d0ae849d the refusal named the box by words no box reads. **The
  IDE**: it names the box as the dialog writes it, one constant for both (`RunConfigurationsDialog.TelemetryWords`; passes on the Linux VM).
- `CommitSigningTests`' `An_x509_keys_passphrase_is_asked_in_passengers_dialog_through_gpgsm_and_signs_the_commit`, CI
  Windows: "gpgsm made no certificate … keyblock resource '/d/a/…/debug/C:\Users\…\gnupg/pubring.kbx'". The test's
  `TestX509Keys` gave Git for Windows' MSYS gpgsm, the runner's, GNUPGHOME in Windows' form, which it takes for a folder
  under its own. **The test**: its gpgsm goes through `TestGpgKeys.Of` (`MsysGpg.Fit`), as its gpg does (passes on the
  Windows VM with Git's usr\bin on the PATH, as on the runner; skipped there without it).
- `McpLanguagesTests`' `An_agent_reads_navigates_renames_and_refactors_avalonia_xaml_through_passengers_own_xaml_and_a_library_type_decompiled_once`,
  CI Windows: "Rename was not applied: MainWindow.axaml changed on disk a moment before the language server was asked,
  which had not read it after 10 s". The server, 15 s into its load, had not read the XAML the rename before wrote, and
  `Patience.Limit` stayed 10 s where every other wait of the IDE's on what it started takes `PASSENGER_WAIT_SCALE`
  (2.5 on that runner). Not the entry of 2026-10-31, whose wait is what ran out here. **The IDE**: the limit goes
  through `Waits.Of` (passes on the Windows VM).
- `McpAspireStandInTests`' `An_agent_reads_a_services_request_duration_through_telemetry_metrics`, CI Windows: "web runs no
  .NET program on this machine to read metrics from". The stand-in pushes web starting, then running with its process
  id; the test asked at once and took the first answer, given before the push landed, for good. **The test waited
  wrong**: it waits for workspace to list web running (passes on the Windows VM).
- `ProfilerTests`' `A_profile_of_ten_thousand_stacks_reads_drags_and_draws_in_what_its_stacks_and_bars_keep`, CI macOS:
  "The flame graph drew 565 bars whole and 74 zoomed into a leaf, of 26 samples". Its program's leaves spun about a
  fifth of a second, which the runner's sampler caught 26 times (191 on this Mac), as the 2026-10-26 entry met with a
  shorter spin. **The test**: the leaves spin ten times as long (2,346 samples on this Mac).

## 2026-10-31

- `BuildRunTests`' `With_hot_reload_edits_reach_the_running_program_a_signature_change_restarts_it_and_it_ends_with_the_window`,
  CI Linux x64, release run 41228013314 (d85c9858), gate (#1062): "Waited 180 s for the status bar to say the changes
  applied; the status bar says \"Hot Reload: no managed code changes to apply\"". A save writes its bytes to a temporary
  file beside its file, flushes them to disk and moves it over the file. dotnet watch gathers what changes for 200 ms
  before it acts, and takes a file added in a project's folder for a new item, loading the projects again; on the loaded
  runner the save kept its temporary file there past that, so dotnet watch heard it added, loaded the projects (which
  read the edit, applied), then heard the file's change and the temporary file's deletion and said "No managed code
  changes to apply", which the status bar followed. A script saving as Passenger does, its temporary file kept 0.4 s,
  had dotnet watch say just that on the Linux VM and this Mac; a name of a hidden file did not help, and a hidden folder
  for it was heard deleted on Linux. **A race in the IDE**: a save's temporary file, and the old file a replace sets
  aside on Windows, end in `.user`, which every .NET project's items leave out (the SDK's DefaultItemExcludes), so dotnet
  watch passes them over and hears a save as its file's change alone (in this commit, with a case saving four edits in a
  row, each save's temporary file kept a second, which on main had dotnet watch load the projects again for each, on
  the Linux VM and this Mac). After it, the test and the case pass 10 of 10 on the Linux VM and 10 of 10 on this Mac.

- `ProjectLoadTests`' `A_package_with_a_known_vulnerability_is_a_warning_in_the_status_bar_and_problems_with_its_advisory_and_nuget_offers_its_fix`,
  CI Linux x64, run 41208501263 (c1e3b8c6), gate (#1060): "Clicking the advisory's link opened []". Roslyn's server
  logged the project's load warning again after Problems listed it, and each report made every row of the tab shown
  anew (`ProblemsModel.Refresh`), resetting the list and unchoosing its row: landing between the press on the link and
  its release (each runs the window's queued work first), the reset moved the pressed link's container to the group's
  row, so the release opened nothing. On the Linux VM it failed 2 of 3 runs so, the warning unchosen before the click
  or its link hidden as the click aimed, the report caught in the act by a trace of the rows' changes. **A race in the
  IDE**: a refresh of the tab shown keeps the rows it makes alike, a group taking its new count, and the row chosen
  among them, so a report saying what Problems lists tells the list nothing (in this commit, with a case pressing the
  link as the load is reported again, which on main opened nothing). After it, 10 of 10 on the Linux VM idle and 10
  of 10 with two CPU burners on its two cores, 10 of 10 on this Mac.

- `AspireStandInTests`' `An_agent_is_told_the_failed_bindings_of_an_Avalonia_app_the_AppHost_runs_as_a_service`, then
  the class's next tests, the Windows VM (Windows 11 on Arm), not CI (#1059): "The process cannot access the file
  …\Bindings because it is being used by another process" from each next test's `SharedProject.Reset`. Listing every
  process's working folder and handles on the folder named the holder: `dotnet --version`, which the IDE runs in a
  project's folder for the SDK its global.json picks (`Dotnet.SdkAsync`), here as the AppHost started the Bindings app.
  It answers in about 40 ms, then sends the command line's telemetry for about 2 s more, its working folder held all
  the while, and Windows lets no one delete a folder a process works in; `dotnet run-api`, asked in a file-based
  program's folder, lingers the same. CI and `just test` turn the command line's telemetry off for the whole run, so
  only the VM's run, which leaves it on, showed it. **The IDE**: its own questions of the .NET command line are asked
  with its telemetry off (`ChildProcesses.DotnetQuestion`), so each ends with its answer (in this commit). On main the
  class failed so on both runs on the VM (7 and 17 tests); after it, it passed whole 2 of 2 there (39 passed, the 8
  needing a container engine or a shell skipped, as on main) and 2 of 2 on this Mac.

- `AspireStandInTests`' `An_AppHosts_console_and_its_containers_merged_log_cost_a_small_constant_for_each_line_however_many_come`,
  CI Linux x64, run 41154974796 (b628b1f3), report lane (#1057): "The AppHost's console allocated 238 bytes off the
  window's thread for each line of 10,000 more than of 1,000", against 220. About 140 bytes of a line are its text
  waiting for the console; the rest came from the reads of the AppHost's pipe that waited for it, each of which .NET's
  reader of a child's output allocates for (its read's state and its pipe stream's, about 430 bytes), so the more often
  the reads caught a program writing a line at a time up, as on CI's runner, the more a line cost. **The IDE**: a
  child's output on macOS and Linux is read through Passenger's own reader over a socket on its pipe's descriptor, whose
  reads that wait allocate nothing (in this commit). With the AppHost pausing after each line, so that each comes in a
  read of its own, a line cost 462 to 475 bytes off the window's thread on this Mac and 332 to 370 on the Linux VM
  before it, 45 to 48 and 33 to 44 after it. As the test is, its reads seldom waiting on either machine, 134 to 146
  after it on this Mac (139 to 158 before) and 123 to 165 on the Linux VM (136 to 148 before), its lines' text.

- `AspireStandInTests`' `Profile_of_an_AppHost_gives_up_on_a_service_past_its_time_saying_so`, CI macOS, run 41154974796
  (b628b1f3), report lane (#1057): "Profile's time limit was said as " with nothing after it, as in release run
  40127831310 below. Since 50ce6ce4 the limit fell due on a timer of its own from DCP's ask, and only there: a start
  that landed before the timer ran was taken for one in time, so where the timer ran a second late (the runner too busy
  to run it, as far as can be told) the API, built by the tests before, was profiled. **A race in the IDE**: a start
  that lands or fails reads the clock against the limit too, whichever comes first deciding (in this commit, with a
  case holding the limit's timer past Profile's answer, which fails so without the clock's reading). On main 15 of 15 on
  this Mac, 5 of them with one processor seen; after it, both cases 10 of 10 on this Mac and 20 of 20 on the Linux VM.

- `ProfilerTests`' `A_trace_of_allocations_and_collections_alone_lists_them`, CI Linux x64, run 41154974796 (b628b1f3),
  report lane (#1056): "The byte arrays allocated are listed with 108,768 bytes, not the 8 MB allocated". The trace held
  one small object's allocation tick for the byte arrays and none of the 80 arrays of 100 KB the test allocated: on
  Linux the runtime leaves most of a large object's ticks out of an EventPipe session once its process has allocated
  large objects before (a plain program on the Linux VM lost them in 21 of 30 sessions after its first, and never a
  small array's; never on this Mac), and TraceEvent's own parser finds none in the file either. Neither race nor the
  IDE's: **the runtime**, and **the test**, which records 80,000 arrays of 100 bytes now (in this commit). Before it, 4
  of 20 runs on the Linux VM after AspireStandInTests' merged-log test failed so, none of 20 alone; after it, 20 of 20
  there after that test, and 10 of 10 on this Mac.

- `FileBasedProgramsTests`' `A_file_of_top_level_statements_alone_with_no_directive_is_a_program_that_completes_runs_and_debugs`,
  CI Linux x64, run 41154974796 (b628b1f3), report lane (#1046): "dotnet restore …/héllo app.cs exited with code 1: …
  NU1101: Unable to find package Microsoft.AspNetCore.App.Runtime.linux-x64". Not the accent or the space: a program's
  Native AOT, on by default, has the SDK infer a self-contained publish for this platform before the test's
  Directory.Build.targets turns AOT off, so the restore asks for this platform's .NET and ASP.NET Core runtime packs,
  which the test's empty feed lacks; it passed only where the package cache held both, and CI's held the .NET pack the
  repository's own restore brings but not ASP.NET Core's. Neither race: **the test**, whose Directory.Build.props now
  leaves the publish's platform unset (in this commit). It failed so on the Linux VM and on this Mac with a cache
  lacking ASP.NET Core's pack; after it, 10 of 10 on the VM so, and it passes on this Mac so.

- `FindInFilesScopeTests`' `Changing_the_scope_cannot_replace_files_from_the_previous_search_before_the_next_search_finishes`,
  CI Linux x64, run 41154974796 (b628b1f3), report lane (#1055): "Assert.Equal() Failure: Values differ, Expected: 0,
  Actual: 1". The test asserted that Replace All, pressed as the scope changed to the current file, had asked nothing by
  the time the call returned; but Replace All searches again first, and that search of one open file runs on the thread
  pool and can end before the window's thread reaches its await, so the replace went on at once and asked, rightly, of
  the current file's two matches alone. Neither race in the IDE: **the test** read a state that moved; it now checks
  what Replace All asked, the current file's matches, whichever ends first (in this commit). It failed so on the Linux VM
  before; after it, 10 of 10 there, and it passes on this Mac.

- `SmallestWindowTests`' `At_the_smallest_window_Find_in_Files_stands_inside_the_window_its_results_and_preview_giving_way`
  and `At_the_smallest_width_a_refusal_stands_whole_in_front_of_a_deep_files_path`, CI Linux and Windows x64, run
  41154974796 (b628b1f3), report lane (#1054): "Waited 180 s for Find in Files" and "Waited 180 s for Build's refusal in
  the status bar" (225 s on Windows). Each pressed Rider's macOS keys, meta shift F and meta shift B, which the keymaps
  of Linux and Windows bind to nothing, so neither action ran there; both passed on macOS and failed so on the Linux VM.
  Neither race: **the test**, which presses the platform's first shortcut for each action now (in this commit). After
  it, 10 of 10 on the Linux VM and on the Windows VM, and it passes on this Mac.

- `FileColoursTests`' `A_line_too_long_to_colour_that_opens_a_comment_leaves_the_next_line_in_the_comment`, CI Windows
  x64, run 41154974796 (b628b1f3), report lane (#1053): "The line after a 30,023-character line opening a comment is
  coloured Text: the long line's comment never reached it". A line over 20,000 characters was read once a pass within a
  tenth of a line's time, 100 ms, ending in the state it began in past it (#949); this one takes 20 to 30 ms of it warm on
  this Mac and over 30 cold (with a 30 ms budget its first read failed 4 of 4, a second read of it in the same window
  passed), so on the slower Windows runner, loaded by the MCP tests' Passengers beside it, the one read ran out, as it did
  once in three on this Mac under load. **The IDE**, on a busy machine: a long line whose time runs out is read once more,
  its rules' scanners made by then and the machine's pause past, before it ends in the state it began in (with a 30 ms
  budget, 3 of 3 after it); and **the test** held the IDE to a developer's machine's time on a machine the run counts 2.5
  times slower, which it now scales as every limit is (in this commit). It passed 6 of 6 alone on the idle Windows VM on
  main; after it, 10 of 10 there and on this Mac.

- `McpLanguagesTests`' `An_agent_reads_navigates_renames_and_refactors_avalonia_xaml_through_passengers_own_xaml_and_a_library_type_decompiled_once`,
  CI Windows x64, run 41154974796 (b628b1f3), report lane (#1052): "Expected: "Renamed in 2 files: MainWindow.axaml,
  MainWindow.a"··· Actual: "This can't be renamed"". The agent renamed the button Go to Start from the XAML, which wrote
  MainWindow.axaml, then at once renamed the field Start from the code-behind. Roslyn's server reads a XAML file from
  disk once the folder's watcher tells it of a change and makes the element's field from it, so asked before it had read
  the new XAML it knew no Start and its rename answered nothing, which the IDE took as a name that can't be renamed; the
  wait for files the server had yet to read looked only at what an edit changed, and passed over open files, though the
  server reads an open XAML file from disk as any other. **A race in the IDE**: a rename the server answers with nothing
  a moment after a file of its solution it reads from disk was written (an open one too, but C# and Razor) waits for the
  server to read it and asks again, as an edit's files are waited for (in this commit, with a window test holding the
  server's word of the XAML's change, failing so on main every time on this Mac). It passed 6 of 6 on the idle Windows
  VM on main; after it, it and the new test passed 10 of 10 there and on this Mac.

- `XamlTests`' `Previewers_run_for_the_preview_shown_and_the_one_hidden_last_and_a_build_starts_only_the_shown_one_again`,
  CI Windows x64, run 41154974796 (b628b1f3), report lane (#1050): "A build started 0 previewers again, where only the
  shown view's should start again". An Avalonia previewer loads the project's assembly where the build put it, and
  Windows lets no build write a file a running program has loaded, so the build after the edit failed after ten tries to
  copy Gallery.dll into bin/ ("MSB3027 … The file is locked by: .NET Host"), and a failed build starts no previewer; a
  person with XAML Preview on met the same failed build. **A bug in the IDE**: on Windows a build's first step waits for
  the Avalonia previewers to end, their frames kept, and the shown view's starts again as the build ends (in this
  commit). It failed so on the Windows VM 2 of 2 on main; after it, 10 of 10 there and on this Mac, and XamlTests 52 of
  52 there.

- `BuildRunTests`' `A_program_under_dotnet_watch_whose_build_fails_is_said_to_wait_for_a_change_naming_the_error_and_a_change_that_builds_runs_it`,
  CI Windows x64, run 41154974796 (b628b1f3), report lane (#1051): "Waited 150 s for dotnet watch to build and start the
  program again once a change that builds was saved", the Run window ending "dotnet watch : mended again" and "Waiting for
  changes" on two lines. dotnet watch writes its prefix and its words to standard error in two writes, and the program it
  started wrote its first line to standard output between them; the Run window's runner told one line of both streams
  together, so "dotnet watch : Waiting for changes" never came whole and the window kept saying the change waited to be
  applied. **A race in the IDE**: each of a program's streams has its lines told whole now, however the other's text
  comes between (in this commit, with a test whose program writes into each stream's line from the other, failing so on
  main every time on this Mac). It passed 6 of 6 on the idle Windows VM on main; after it, it and the new test passed 10
  of 10 there and on this Mac.

- `TemplateFamiliesTests`' `New_Solution_opens_with_every_family_listed_at_once_the_templates_read_ahead_and_the_search_finds_one_by_any_of_its_Types`,
  CI Windows x64, run 41154974796 (b628b1f3), report lane (#1049): "Waited 300 s for the templates to be read ahead of
  New Solution". The templates were read ahead once a process but kept by template home (`ProjectTemplates.Hive`); the
  class's Aspire test sets a home of its own and puts the run's back, and in CI's Windows checkout xunit ran it first in
  its process, so the one read ahead was of the Aspire test's home, none ever came for the run's, and the next test
  waited for it (on Linux and macOS a test between them read the run's home through its dialog). Not #1036's watch:
  nothing changed for it to hear. **A bug in the IDE** only a change of template home meets, which the tests alone make:
  the templates are read ahead once a process for each home (in this commit). With the Aspire test ordered first (by a
  test orderer used for the check alone) it failed on this Mac every time before; after it, the class passed 10 of 10
  there and 10 of 10 on the Windows VM.

- `IdeErrorsTests`' `A_rename_whose_write_to_a_file_never_lands_leaves_that_file_out_of_its_Undo`, CI Windows x64, run
  41154974796 (b628b1f3), report lane (#1048): "Assert.Contains() Failure: Sub-string not found ... Not found:
  "Helper.Double(2)"". Since #1033 a rename refused partway takes back all it did before it says so, the open file's
  change too, and leaves no command for Undo; the test's Windows branch still asked for the open file to keep the rename
  after the refusal, as before #1033, which no run could give, and off Windows it asked nothing then, so it passed there.
  Not the take-back racing the held write: the write gives up after its wait and never lands. **The test asked for the
  old behaviour**: it holds every system to the take-back (the call back as it was, nothing unsaved, no command for
  Undo), renamed `A_rename_whose_write_to_a_file_never_lands_takes_back_the_open_files_change_and_leaves_nothing_to_Undo`
  (in this commit). It failed on the Windows VM every time before; after it, 10 of 10 there and 10 of 10 on this Mac.

- `McpTests`' `An_agents_package_install_under_central_versions_kept_where_the_build_names_names_that_file_among_those_it_changed`,
  CI Windows x64, run 41154974796 (b628b1f3), report lane (#1047): "nuget_install failed: ... dotnet.exe add ...
  package Passenger.Sample --version 1.0.0", dotnet's last line "error: Object reference not set to an instance of an
  object.". dotnet add package finds the central versions file among the project's imports by comparing each import's
  full path with DirectoryPackagesPropsPath as written (NUGET_SHOW_STACK: `MSBuildAPIUtility.AddPackageVersionIntoItemGroupCPM`),
  and refuses a package the file already holds for the same mismatch ("defined in incorrect location");
  `$(MSBuildThisFileDirectory)eng/Versions.props` is `...\eng/Versions.props` on Windows, never the import's
  `...\eng\Versions.props`, and a path through `..` mismatches so on every system. **A bug in NuGet the IDE met** every
  time: where the evaluation spells the file otherwise than its full path, Passenger writes the reference and the version
  as dotnet add package would and restores (in this commit, with a test naming the file through `Lib/..`). It failed on
  the Windows VM every time before; after it, both tests 10 of 10 there and 10 of 10 on this Mac.

- `PauseTests`' `An_interpolation_paused_in_a_sleep_writes_numbers_in_the_programs_culture_and_refuses_a_value_only_its_ToString_writes`,
  CI Windows x64, run 41154974796 (b628b1f3), report lane (#1045): "Paused in its sleep, $"{count}" answered "-1234" and
  $"{ratio:N1}" "1 234,5", where the program wrote asleep -1234 1�234,5 dreaming". The debugger's answer was right, its
  group separator U+202F as the program's fr-FR culture holds it (not the separators the issue guessed at); the program's
  own line was wrong as it reached the Debug Console: on Windows a debugged program had a console of its own, shown in
  no window, whose code page is the system's (437 on an English Windows), so .NET wrote its redirected output in it, the
  narrow no-break space as a byte that is no UTF-8, and the adapter, reading UTF-8, made it U+FFFD. **A bug in the IDE**,
  met by every character outside that code page a program debugged on Windows writes: the program shares the adapter's
  own windowless console, set to UTF-8 first (in this commit). It failed on the Windows VM every time before; after it,
  10 of 10 there and 10 of 10 on this Mac.

## 2026-10-30

- `UiRulesTests`' `A_window_opens_inside_a_small_screens_working_area_centred_and_one_placed_past_its_edge_is_brought_onto_it`,
  CI Linux, run 41154974796 (b628b1f3), gate (#1044): "A new window opened at 84, 0, 2800, 1580, not centred on the
  screen's working area 0, 0, 3000, 1660". Since #737 a window is fitted before it shows with room kept for the most a
  system's frame adds (16 by 40) wherever its content is not the title bar, and that guessed frame is what it centred; on
  Linux with no desktop named (CI's runners, the VM's runs) the main window keeps the system's title bar, so it centred a
  frame the headless platform never draws and stood 8 by 20 units off the centre, as any window did whose real frame
  differs from the guess (a dialog's on macOS, a window manager's on Linux). macOS and Windows merge the main window's
  title bar, so it guessed no frame there and passed. Neither race: **a bug in the IDE**, met every time: a window
  fitted before it shows is fitted again once it shows, with the frame the platform reports (none where it reports
  none), and again as X11's window manager first reports it; the test also opens a window in the system's frame, which
  failed so on macOS before it (in this commit). It failed on the Linux VM every time before; after it, 10 of 10 there
  and 10 of 10 on this Mac.

- `ConflictsTests`' `An_unstash_that_conflicts_opens_the_conflicts_dialog_on_the_stash_and_accept_theirs_takes_the_stashed_change`,
  CI Windows x64, run 41154974796 (b628b1f3), gate (#1043): "Expected: "Conflicts during unstashing stash@{0}", Actual:
  "The following files have conflicts:"". Git keeps no record of an unstash stopped for conflicts, so the window names
  its stash itself until a read of the status finds none; a read begun before the pop's git ended (one the folder
  watcher started as git wrote the files and the index) took the status before the conflicts were in it and landed after
  the stash was named, taking the name away, and the dialog the unstash then opened gave the words of no operation. It
  was not the watcher opening the dialog, as the issue guessed: only the unstash's end opens it, and it follows every
  read. **A race in the IDE**: only a read begun after the unstash's git ended takes the stash's name away (in this
  commit, with a test holding a read taken before the unstash until the unstash is under way, which fails so on main
  every time, on this Mac and the Windows VM). The test alone passed 11 of 11 on main on the idle Windows VM; after it,
  it and the new test passed 10 of 10 on the Windows VM and on this Mac.

- `HotReloadEditsTests`' `Each_edit_to_a_running_program_applies_as_the_runtime_allows` (chain "bodies of methods, lambdas,
  async methods, records and generics"), this Mac, one of four runs while landing #1020, report lane (#1039): "Waited 90 s
  for the program to print 'lambda captured' once a lambda capturing a new variable applied", its output ending at "pet
  Rex". The update landed while `Tick` was part way through a statement after the lambda's, and the debugger moved the
  frame into the new code at its next statement, as Roslyn mapped it; but the new code makes the closure for the newly
  captured `i` at the method's start, which the frame had passed, so `Echo`'s line read `i` through a null closure and
  stopped the program on a NullReferenceException (the engine's log: the remap, then the exception). A local newly
  captured beside `i`, a field the update gave the closure the frame held, read as never set. **A race in the IDE**, met
  every time a call of the method is under way: a frame moves to the new code only where it holds each closure the new
  code holds there, in the same slot, of the same type and given no field since, and otherwise finishes in its old code
  (in this commit, with tests paused at `Tick`'s `Describe` line and in `Describe`, and in `Describe` as a local is
  captured beside `i`, each failing so on main every time). The chain alone passed 12 of 12 on main beside two CPU
  burners and two other agents' builds and tests (an update landed in a `Tick` once, at another edit); after it, the
  class, its new tests among them, passed 20 of 20 beside the same.

## 2026-10-29

- `McpAttachTimingTests`' `A_call_that_ends_while_its_word_to_a_slow_agent_is_still_written_comes_back_after_that_word`,
  CI macOS, run 40921064516 (7358d411), report lane: "The agent never heard what its call waited on", only
  --mcp-attach's own beats heard. The window writes its held word and then its answer back to back, and the SDK hands
  each message it reads to a handler of its own: in --mcp-attach the answer could end the call first, and the word was
  dropped, its call's token forgotten or its telling cancelled with the call's beats; in the test's client the call's
  progress handler goes once the answer is in. **A race in the IDE**: --mcp-attach hears the window's progress as it
  reads it, in the window's order, and the answer waits for each word heard to be told on; and **the test**, which read
  what the call was told the moment it was answered, now waits for the word as the client hands it over (in this
  commit). 12 runs beside 12 CPU burners never failed before it; after it, 20 of 20 beside 16 and another test process,
  and 18 of 18 on the Linux VM.

- `HotReloadEditsTests`' `Each_edit_to_a_running_program_applies_as_the_runtime_allows` (chain "return types changed with
  their callers and without"), CI macOS, run 40921064516 (7358d411), report lane: "Waited 180 s for the program to print
  'as object' once a return type changed with its callers applied", its output ending at "async 16". The update landed
  while `Tick` was part way through its `FetchAsync` line, a frame the runtime offers the new code only at a statement's
  start, and the debugger moved it only to a statement the edit had left as it was up to its end; the next line, the
  edited call of `Describe`, ran in the old code, called the `Describe` the update deleted and stopped the program on
  `HotReloadException`. **A race in the IDE**, met every time by a program paused in a call when the line after it is
  edited: a frame part way through an active statement goes on, at the statement after it, at the one after what Roslyn
  made of it (in this commit, with a test paused in `FetchAsync` that fails so on main every time).

- `AspireContainerRecoveryTests`' `Opening_a_window_removes_only_an_exact_record_of_a_dead_owners_container`, every scenario,
  CI Linux and macOS, run 40921064516 (7358d411), report lane: "Item: info --format json … not found". Since #636 the engine
  asks `docker info` once it knows the server's version, and the test's stand-in docker listed the commands it allows
  without it. Neither race: **the test**, which now allows it.
- `BuildRunTests`' `With_hot_reload_edits_reach_the_running_program_a_signature_change_restarts_it_and_it_ends_with_the_window`,
  CI Linux, run 40921064516 (7358d411), gate: "dotnet watch did not start the program again, for the signature changed
  while the window was held". Its last step changed the signature of `Say`, which the program's loop calls every 200 ms;
  the program called the deleted body before dotnet watch's restart and ended on the unhandled `HotReloadException`, and
  dotnet watch waited for a file to change rather than restarting. Neither race in the IDE: **the test** relied on dotnet
  watch's own timing; it left the gate and was rewritten to change a method the program calls only once dotnet watch has
  hooked the deleted body's exception (189eee22, #701), and is back in the gate, the release run its first CI record
  (the owner's choice).

## 2026-10-28

- `TerminalResponsivenessTests`' `A_paste_into_a_program_that_reads_nothing_leaves_the_window_taking_keys`, this Mac
  (#653): on main 1 pass and 2 hangs until the 10-minute blame dump, with #644 2 passes, 3 hangs and a failure after
  90 s with "sh: child setpgid (N to N): Operation not permitted" on the screen. Two causes. The hangs: the test closes the
  window while the shell is still taking the 30 KB paste, and closing a terminal stopped reading its output, killed the
  shell and closed the pseudo-terminal on the window's thread. A shell leading its session waits as it exits for its
  terminal to take what it wrote, which nothing read any more, and the close waited for the paste's write still in the
  kernel, which only the shell's going ends: the window's thread waited in `close` for good (its stack: Window.Close,
  TerminalPanel.CloseAllAsync, TerminalSession.DisposeAsync, Porta.Pty's Dispose, `pty_close`; the shell `?Es`). A
  person closing the window or the solution while a program read none of a paste and wrote on met it every time.
  **The IDE**: a closed terminal reads its output to the end, and kills the shell and closes it off the window's
  thread once the shell has gone (in this commit, with a test that closes the solution while `yes` writes on and has
  read none of a paste, which hangs on main every time). The failure: macOS's bash 3.2 with job control at times says a
  child's own `setpgid` was refused, the child running all the same (Python's `forkpty` running `/bin/sh` complained in
  4 of 30 rounds of 20 `sleep`s, and `bash -c 'set -m; …'` with no terminal in 3 of 40), and the test's wait for the
  loop to start took only the line just above the cursor for the loop's command, so a complaint there held it to its
  90 s. **The test** looks for the command in every line above the cursor.

- `ServerWatchTests`' `A_thousand_files_deleted_side_by_side_in_obj_each_reach_the_server_as_gone`, the Mac in three
  processes beside FolderWatchTests, ProblemsTests and LanguageServerProjectsTests under 20 burners, 1 round in 10
  (#617): "Of the 1000 sources deleted side by side in obj, the language server was told 827 went". A watcher's record
  took in what came or went in a batch as work queued as the batch was handed over, reading each path from the disk as
  that work ran. The test deletes the thousand once the server is told they came; on a loaded thread pool that work ran
  after the deletions began, so it dropped from the record sources the server had just been told were there, and for
  those whose deletions FSEvents dropped, the listing after the burst had nothing left to find gone. (The log's "823
  paths heard … 178 changes not heard found" was the thousand's making; a listing's count leaves out a path heard or
  found in the last moments, so the deletions' "0 found" said nothing.) A script deleting what a build had just written,
  on a busy Mac, met the same, and so did the folder's own watcher, Go to File keeping the file. **The IDE**: a batch's
  paths are looked at on the thread pool as it is let go of, before anything it goes to looks, and the record takes each
  as it was then, so a change after that differs from the record and the listing finds it (#617, in this commit, with
  tests that hold the record's work while a file the server or Go to File was told came is deleted unheard, which fail
  on main every time).

- `ProblemsTests`' `Lines_typed_above_a_files_problems_move_their_rows_without_making_the_list_again`, macOS report lane
  of run 40704117907 (#615): "The IDE was not quiet within 180 s. Still busy: background work begun at
  CSharpService.cs:711", the start of a window taking up the class's kept server to load the file the test wrote. With
  no window holding it, the server was told of the project's files written since it started and loaded the project
  again, asking the closed window for its design-time builds; those, let go of with the folder, queued the question
  and never answered it, and the next window's load waited behind that reload for good. A project file touched
  between two windows, then a file written for the next, hung the start every time. Only a test's kept server
  (`WarmServer`) outlives its window. **The IDE**: builds asked of a folder let go of are refused at once, and a kept
  server's questions wait for the window that takes it up, whose builds answer them (#615, in this commit, with a test
  that touches the project file between windows).

- `LanguageServerProjectsTests`' project whose packages arrive during its first design-time build, the Mac in three
  processes beside ServerWatchTests, FolderWatchTests and ProblemsTests (#613): "Waited 180 s for the first window's
  language server to resolve the package … Page.cs has CS0246, CS0103, CS0103, IDE0005, after 1 design-time build(s)",
  under 20 burners, where under 14 and 16 it passed, and alone too. The test's build copies the restored assets into
  obj, and the copy keeps their time, the restore's: written at 15:48:53.5, they arrived during a build begun at
  15:48:59.4, and the server's watches of obj, started as its load ended, told what was written since 2 s before the
  server started (15:48:58.4). Load put more than 2 s between the restore and that start, so the server was never told
  of assets its build had not read, though the build's own stamps said project.assets.json was missing as it read the
  project and there as it landed; no window of time from the build's start would have told them either. A person opening
  a folder on a busy machine as its restore lands, or as its obj is unpacked or copied in with times kept, met the same.
  **The IDE**: a build given the server as it asks comes with the files it read that changed since, told the server as
  changed and kept for the registrations its load sends, whatever their times; and the watches tell what was written
  since the earliest of the builds the server was given as it started began reading (a kept build's since its stamps
  were looked at), where that came before the server's start (#613, in this commit). The test now dates the arriving
  assets a minute back, before any server's start, so on main it fails alone too.

- `IdeErrorsTests`' rename that cannot write a file it changes, Windows x64 report lane of run 40704117907 (#614):
  "String: "Renamed in 2 files", Expected start: "Rename failed: Helper.cs could not be written"". The test holds
  Helper.cs open sharing only reads. Since #577 a save Windows refuses while another program holds the file lands in the
  background, so the rename's write to that file, not open, returned at once: the status bar said it renamed in both
  files while the write waited, and when the write gave up two seconds later nothing said so, Helper.cs keeping Twice
  while Program.cs called Double. A person renaming into a file a build or a scanner held met the same. **The IDE**: a
  change of several files (a rename, a refactoring, a code action, Replace in Files, and an agent's calls of each)
  waits for its writes to land, the status bar saying "Waiting for Helper.cs to be saved…", then says it is done, or
  that it failed and why (in this commit, with a test of a held file let go in time).

- `CodeAssistTests`' type and parameter name hints, the Mac under 14 burners beside FolderWatchTests and ProblemsTests'
  thousand rewrites (#610): "Waited 135 s for the hint's hover card". As the window opened, its design-time build
  rewrote the project's generated .editorconfig; told of it, Roslyn read every document again, the text the same but its
  version new, after it gave the file's hints and before the refresh it sent brought the next, and refused to describe a
  hint given for the earlier version (ContentModified: "Request resolve version … does not match current version …"),
  so no card came for a pointer that never moved: 2 and 7 of 20 windows opened in a loop under load met it. A person
  resting on a hint as a window opens on a busy machine, or just after an .editorconfig changes, met the same. **The
  IDE**: a hint the server refuses as outdated is asked of the hints it gives now, and the one at that place with those
  words described (#610, in this commit, with a test that holds the hints after an .editorconfig's change).
- `ProblemsTests`' thousand files rewritten on disk, the Mac under 14 burners beside CodeAssistTests before #604 landed:
  the server told of 980 of the 1,000. FSEvents dropped the rest and said so (`MustScanSubDirs|UserDropped` on the
  watched folder), but .NET 11's macOS watcher skips any event on the watched folder before it looks for that flag, so
  no `Error` came; 1,000 deleted side by side lost about half every time. **The IDE**: the folder's watcher takes a
  burst at risk (256 changes within a second, or an overflow) as a reason to list again the folders it touched against
  its record, and once more after a quiet second (#607, in this commit).
- `LanguageServerProjectsTests`' project whose packages arrive during its first design-time build, the Mac on main
  (#608): "The first window's build resolved the package (CS0103): the language server was given a build made after its
  assets arrived", in every run alone (passing on 9befe118's parent, failing from 9befe118 on) and in some of its class.
  Since #604 the server's watches of a project's obj begin with what was written there since it started, so the server,
  given the build made as the assets arrived, is told of project.assets.json as its load ends and loads the project
  again 0.4 s later from a build made after them: the package resolves in the window it arrived in, where before #604 it
  stayed unresolved until the folder was opened again (the rewritten test there: still CS0246 after 3 minutes). The test
  checked that the first window kept the build without the package, which held only when it read the errors and closed
  the window within those 0.4 s. Neither a race nor a wrong wait: the IDE got better. **The test**: it checks the first
  window resolves the package from a second build, the first not standing once its assets came, and the folder opened
  again loads that one; it still fails with the assets stamped after the build (in this commit).
- `DebuggerTests`' Continue pressed while a stop's values are read, Linux x64 report lane of run 40623345416:
  "slow.Getter never showed 1500 as a watch: the watches were slow.Getter = error: An evaluation that could not be
  stopped is still running on this thread…". The program started its ticking thread just before its first stop, which
  caught that thread in its first moments holding what the stop's first evaluation (Variables' ToString of `slow`)
  needed; an evaluation runs its thread alone, so it waited, outside managed code, on a thread it held still, past its
  3 s and its abort's window, and was left to run on, and the watch after it was refused on that thread. 5a880bf2 gave
  both threads their first use of `Interlocked` and a file write there. The Ubuntu VM beside 4 burners failed so in 1
  of 3 runs, the engine's log saying "past its time and not aborted: it runs outside managed code"; a program whose
  other thread the stop catches loading a type the watch needs does so every time. Not the IDE's: it gives up and says
  so, as for any evaluation waiting on a thread it holds still. **The test**: its program stops only once that thread
  has gone round its loop; the VM then passed 10 of 10 beside the burners, the Mac 4 of 4 beside 14 (in this commit).

## 2026-10-27

- `AspireStandInTests`, Windows shard 2 of Release run 40574842088: the eight tests after the one whose AppHost runs an
  Avalonia app failed in 0.2 s at `SharedProject.Reset`, "Access to the path 'Avalonia.Base.dll' is denied"; the ninth,
  0.2 s later, passed. The app was killed as its window closed, but TestsLeaveNothingBehind waited out only windows a
  test left open, so the next reset deleted the folder while the dying app still mapped its DLL. **The test rig**: it
  waits for the end of every IDE window a test closed itself. Its new test found **a bug in the IDE** too: a language
  server that answers nothing ran on after its window closed, its shutdown's limit cancelling a request that waits for
  the server's answer; the shutdown is given up at its limit (35fadc92).
- `LiveRequestsTests`' requests window over the running shop, Windows shard 3 of Release run 40574842088: "A value of the
  Password column shows in the clear, or not as dots: GET /orders/{id:int}: lambda (Shop: http)". A Requests row view
  holds a request's line and a command's and shows one; the list handed a sign-up command the row of the first request
  scrolled off its foot, and the test read the first tooltip of any part of the row, the hidden request line's among
  them. A person's pointer never reaches a hidden line. **The test**: it reads the tooltip the pointer on the row's words
  finds, the row found by its command, and a new test makes the hand-over happen on any machine (796f1e63).
- `ProgramListsTests`' profile of a thousand methods, macOS shard 4 of Release run 40562976386: "The profiler's call
  tree listed 493 of the 1000 methods the program made", under the half the list's measure needs. Each method spun some
  4 ms, and the runtime's sampler on a loaded 3-processor runner woke late enough to miss over half of them; this Mac
  at background QoS beside 10 CPU burners listed 704. Not the IDE's: the runtime writes the trace itself, so no reader
  dropped samples. **The test**: each method spins three times as long, and every list is whole there again (1000 of
  1000); the test says what each list held (in this commit).
- `IdeErrorsTests`' `A_language_server_that_ends_just_as_it_starts_is_started_again`, the Mac in three processes beside
  AspireStandInTests and TestingPlatformTests: "Waited 135 s for the server started again to be ended as its start
  came back". The test's feed held the window's thread from the projects' answer and read the restarted server's
  process only then; since aa666d77 asks for the projects as the server starts, that answer came 10 to 15 ms after
  the process began (565 to 603 ms before), under load before its start was told, so the hold waited 180 s for the
  start of process 0. **The test**: it waits for the process and then its start, and fails at once when it gives up (86ad35c5).
- `FilesFollowed.Check` on Windows in CI run 40465410317 (LocalHistoryTests' revert of A.cs, RefactoringDialogsTests'
  Change Signature, TypeRenameTests' rename of Order.cs, RunConfigurationsTests' Undo and Redo of launchSettings.json):
  not load. ReplaceFile with no backup name sets the old file aside as `name~RF<hex>.TMP` and deletes it as the save
  ends; the watcher took that for a file come and gone, so the projects were evaluated again, and its '~' made Go to
  File's index walk the folder whole as for an 8.3 alias. **A bug in the IDE**: the save names what it sets aside as
  its own, and the index walks whole only for a name an 8.3 alias can be (a7aefd5c).
- `RuntimeAsyncStepTests`' step over WaitForExitAsync's await in the decompiled library, Linux and Windows x64: "stopped
  for step at …Process.cs:1908" from line 1928 (1841 from 1861 on Windows). Optimized code keeps nothing of an await's
  statement after it, so the run went on inside the next statement and the step stepped over that whole statement; x64's
  code for it runs into code shared with an identical line 20 lines earlier, and arm64 skipped the line unseen. **A bug
  in the IDE**: a step whose run goes on past the await's statement ends there (e7853b0f).
- `RuntimeAsyncStepTests`' step out before the method suspends, Windows x64: "The session ended while a stopped event
  was awaited". The step told the awaiting caller's frame by its stack place alone, a single address on x64, which
  another method's frame took once `<Main>$` suspended, so the step took its own run for another's and the program ran
  to its end. **A bug in the IDE**: the step's frame is told by its place and its method (e7853b0f).
- `ProjectEvaluationTests`' file shown's project loaded first, Linux x64: "The language server took up another project
  before the file shown's, and the one it references". Since aa666d77 asks for the design-time builds as the server
  starts, the window could ask for the file shown's projects before the folder's first evaluation round began, find
  the models idle and take App alone, unevaluated. **A race in the IDE**: the wait goes on until the models follow the
  workspace (b3457547).
- `FileBasedProgramsTests`' program beside a solution, Linux x64: "Waited 120 s for tools/tool.cs, written beside the
  solution, to be a program of its own". Linux watches each folder on its own, and a new folder's watch began after
  the file written with it, so the file was never heard of. **A race in the IDE**: the files already in a folder just
  made are taken as made with it (3f1f6fd6).
- `TestingPlatformBytesTests`, Linux and macOS: 2.07 and 2.13 times the results' JSON. Each test-host message arrived
  in 4 KB pieces and was copied whole into a pooled array that, on few processors, nearly always missed the pool (2.04
  to 2.37 times in a 4-processor container). **The IDE's own allocation**: the peer reads each message into one kept
  buffer, 1.50 to 1.61 times after (f4cdb7a7).
- `StepTests`' race of a step's end against another thread's exception breakpoint, x64 .NET under Rosetta on the Mac (7
  of 8): "No stopped event came in 60 s", the engine's log ending at a Continue. The runtime was suspending for the racing
  thread's exception and waited for the stepping thread, whose activation signal had gone out during a single step 37
  turns before and was never handled; the runtime sends a thread no other while one is unanswered. Rosetta loses
  SIGUSR1 to a thread whose traps a Mach exception server answers (a C program: 20,000 of 20,000 handled on arm64, none
  after the 24th under Rosetta). **Neither**: Rosetta's alone; the test skips under it, saying why (07cb27f6).
- `LongTextTests`' key in a line of a million characters, Linux x64 shard 2: "allocated 369,496 bytes … against 345,736
  … the line's measure was made again", reading 6,428 characters against 7,196. Avalonia's text formatter leaves the
  pooled arrays of runs it shapes together and splits to finalizers, so between collections its pool runs dry and a key
  costs about 9 KB more; the test took the short line's keys before the long line's, and the shard's long-lived process
  ran dry between them. **The test**: the lines' keys are taken in turns, each after a collection (147139ee).
- `WorkingActionsTests`' second click on Run, Linux x64 run 40524535928 shard 3: "A second click on Run should be told
  it is under way, but the status bar said: Build succeeded | … | Hello exited with code 0 | Working out how to run
  Hello…". A second click at one place waits out twice the double-click time (1 s), and nothing held the first Run
  under way meanwhile: straight after another class's build, Hello built (660 to 770 ms in a 4-processor container), ran
  and ended within it. A fast machine, not a loaded one. **The test**: its build waits at its compile until the second
  click is answered (`HoldNextBuild`), Debug's double click too, and the ring is read from one frame (9804ed6e).
- `XamlTimingTests`' keys while Passenger.App's types are read, Linux x64 run 40524535928 shard 2: a key waited 369 ms
  against the 250 ms bound, the read taking 2509 ms (about 2.5 times its usual). The read began as Shell.axaml opened,
  so its PLINQ threads on every processor and the collections of its 280 MB landed on the file's opening and first
  frame; on its own it held keys only for single pauses of 5 to 25 ms. **A bug in the IDE**: the read begins once the
  window has drawn (fd15830b); worst wait on Linux x64 with 4 processors 172 to 261 ms before, 158 to 198 after. This
  answers the 39324137868 entry's "look at the read's work on the UI thread".
- `PauseTests`' evaluation waiting on another thread, Windows x64 run 40544196991: "The evaluation took longer than 3 s
  and cou…", not "…was stopped: it may wait on another thread". The abort was asked at the first look, but had not
  taken within its 1.2 s on the loaded runner (it takes 15 to 60 ms after the runtime's 0.5 s hold on arm64, 80 to 360
  ms under x64), so the evaluation was left to run on. **A bug in the IDE**: its wait on the program for the abort did
  not scale with the machine; it scales by `PASSENGER_WAIT_SCALE`, and the test quotes the whole answer and the engine's
  log (03fa8ed6).
- `DebuggerTests`' debugger that dies with an exception, macOS shard 2 of run 40544196991: "Not found:
  UnauthorizedAccessException" in the IDE's log, the console holding the stack trace. The session listened for its
  debugger's exit only once the window's thread had the started process back; a process raises Exited once, so a
  debugger that died within those milliseconds (0.04 s, its engine log refused) was never heard to end: no log entry,
  its session never ended. Beside 18 CPU burners the test then waited 90 s, 5 rounds in 7; on CI its wait matched an
  earlier test's "Passenger's debugger (process" in the same process. **A race in the IDE**: the session takes an exit
  before it listened too, as both previewers now do; **and the test waited wrong**: it waits for its own debugger's
  entry by process id (2d45e290).
- Linux shard 2 in run 40544196991: 32.3 minutes of tests, over a shard's 32, every test passing; its job took 36
  minutes where the other Linux shards' took 24 to 27. **The deal**: dealt from older times it took more than its share;
  dealt again from the two newest runs (1bfb9da6).

## 2026-10-26

- `AspireStandInTests` on the Windows VM beside another agent's run: two run sessions had no answer from the IDE's DCP
  endpoint in the stand-in's 100 s, then a Run held 675 s (BuildRunController.cs:724) and each later settle 225 s at
  ProjectModels.cs:265, the project host idle. `ProjectHosts.ForAsync`, asked on a window's thread, took the gate every
  window shares there and asked `dotnet --version` (`Dotnet.SdkAsync`, whose first ask every later caller shares), its
  ends coming back to that thread; a headless test's thread never runs again once its test is over, so one ending
  mid-ask left the gate held. **A race in the IDE**: the host and the SDK are found on the thread pool, whoever asks;
  the stand-in's request times out after 2 minutes scaled (d8fbd889).
- `AspireTests`' `Gutter_startup_profile_uses_its_AppHost_not_the_toolbar_and_records_the_first_hot_turn`, the Mac with
  real AppHosts beside three agents' runs: "Expected \"Show Aspire Resources\", Actual \"Discover Aspire AppHosts\"",
  Find Action's first row with nothing typed. A real AppHost sends its "No trusted development certificate"
  notification whenever its start reaches it, shown as a window that activated and took the keys (headless, cleared
  them); under load it came after Find Action opened, so the action's name went nowhere. **A race in the IDE**, met by
  a person typing as an AppHost starts too: a notification shows without taking the keys (5f419625).
- Windows in CI run 40422179664, shard 3: "[FATAL ERROR] Foreground threads were left running, forcing process exit",
  every test passed. No thread was left: xunit exits a process still running 10 s after its run, and the run's end
  waited up to 75 s for a held folder (05de9d64's MSBuild nodes) in the fixture and again in each ProcessExit handler.
  **The test rig**: only the fixture's end waits; the process's exit tries each folder once (fdb93364).
- `TerminalTests`' key read: "key? qkey q", a key typed between the prompt and ReadKey echoed by the tty. **The test**:
  it takes the key once the program says it read it (411bd003).
- Compose Run, Debug and Profile on a busy machine: "ports are not available". **A race in the IDE**: a chosen host port
  is outside the system's ephemeral range and free at every address, and is chosen again if taken before `up` (806cf9d5).
- `MigrationsTests`' main test and `VcsTests`' diff test on the Windows VM, seeming to hang: nothing hung, the VM ran at
  time scale 1. **Neither**: the VM scripts run at CI's scales.
- `McpAttachTimingTests`' `An_agents_call_nobody_answers_comes_back_at_the_windows_limit_and_hears_what_it_waits_on_meanwhile`,
  Linux in CI run 40422179664: "The workspace call had no answer in 120 s", though mcp.log has the window answering at
  10 s. The call's one-second progress beat fell due as its admission question closed, and the call's end cancelled
  the beat's write between the SDK's JSON and its newline (each write takes the sender's token), so the answer joined
  that line and the agent dropped it as JSON it could not read. **A race in the IDE**: every MCP connection writes a
  message whole once begun, a cancellation stopping it only while it waits its turn (`PeerTransport`, 10a6cc6a).
- `AspireOrchestrationTests`' fixture, Windows in CI run 40422179664: "The process cannot access the file
  '…\\aspire-orchestration-df8cfeae' because it is being used by another process", until the run's end 13 minutes
  later, with only idle MSBuild nodes and the project host running. An MSBuild node starts in its launcher's folder,
  the language server's restore runs in the solution's, and on Windows the server's nodes outlived it by up to
  MSBuild's 15 idle minutes, one never taken up keeping the folder (the VM did not repeat it). **A race in the IDE**,
  most likely: the language server runs in a Windows job of its own, closed with every process it started (05de9d64).
- `FileColoursResponsivenessTests`' `A_union_typed_has_its_name_in_a_structs_colour_where_it_is_declared`, every system in
  CI run 40422179664: "The union's name typed is coloured as several kinds, not as a struct's". Not a flake: the test
  read the colour of `Pet(`, the name and its parenthesis, two colours, so it could never pass; it landed with its file's
  other class run but not its own. **The test**: it reads `Pet` alone, which is a struct's colour (e236127d).
- `StuckEvaluationTests`' `An_evaluation_spinning_in_a_finally_block_runs_to_its_end_once_let_out`, Windows (x64) in CI
  run 40398875778: "The evaluation took longer than 3 s and cou…" without "as it runs a catch or finally block". On x64 the
  getter's spin loop makes a call, and a look that stops the thread inside it finds it held at the call's return at an
  unsafe point (about one look in eight); the engine read the thread's state before its frames, so that look said "it
  runs outside managed code", and the reason told is the last look's. **A race in the IDE**, a wrong reason only (no
  abort was asked): the evaluation's frames are read first, as the runtime puts an abort off for a finally on any of
  them whatever the thread does; one sleeping in its finally, told "it waits" every time, is the test's second case (b5badad0).
- `ProfilerTests`' `A_profile_of_ten_thousand_stacks_reads_drags_and_draws_in_what_its_stacks_and_bars_keep`, macOS in
  CI run 40398875778: "The flame graph drew 544 bars whole and 74 zoomed into a leaf" (and 406 bytes a bar against
  400). The test's program, about 200 ms long, left the sampler few samples on the runner, so the flame graph's bars were
  few, wide and nearly all labelled; a labelled bar cost 330–500 bytes a frame, its label drawn under its bar's clip
  (Skia's rounded clip allocates its radii each time) and, trimmed, as two glyph runs. **A cost in the IDE**: a label is
  one glyph run with no clip, about 130–230 bytes a labelled bar; the program spins in each leaf so its profile draws
  thousands of bars anywhere, and the test holds a frame of labelled bars alone to under 400 bytes a bar (fe3694bc).
- `RunConfigurationsTests`' `Save_as_launch_profile_adds_a_configuration_made_here_to_launchSettings_json_which_dotnet_run_runs_and_Undo_takes_it_back`,
  the Mac's whole run at 39a3600b under load (#532): "The Explorer does not show Hello" at the row click after Undo. The
  folder watcher took `launchSettings.json` for a project file, so the Undo's write had the solution read again and every
  Explorer row made anew; that landed after Undo's settle, inside `Ui.Row`'s, so the row just found was gone. The rebuild
  also took the Explorer's keys. **A race in the IDE**, and more work than the change needed: a launch profile changed
  makes only the run configurations again (`FolderChanges.LaunchProfiles`), and a rebuild of the Explorer gives the keys
  back to the chosen row (73991f77).
- `ProblemsTests`' `Keys_typed_in_a_burst_have_solution_wide_analysis_ask_the_server_again_once_typing_pauses`, the Mac
  beside DebugSessionsTests (#532): "asked the server again while 10 keys were typed in a burst … HotReloadDiagnostics 1
  then 0". Roslyn's `workspace/diagnostic/refresh`, sent again about 2 s after solution-wide analysis is turned on, came
  mid-burst: the open file's pull went at once instead of waiting out typing's pause, ended between two keys, and every
  analysis source was asked again. The analysis's step from a file pull's end to its next ask also went through the
  thread pool unheld, so `Ui.Settle` could end before it. **A race in the IDE**: a pull for now leaves the work to a
  pull waiting out the pause, and that step is held and looks again. The test's warm-up `Press("SPACE")` typed nothing,
  so its BACK_SPACE broke the caller before the burst; it now types a real space (22e3551d).
- `AspireStandInTests`' `Rerun_runs_an_AppHost_whose_own_code_changed_again_once_stopped_and_leaves_one_unchanged_running`,
  CI Windows, run 40312082824 (c33d9f4d): "Waited 450 s for Rerun to run the changed AppHost again", the IDE quiet. The
  stand-in resource service read the key the stand-in AppHost renames into place sharing reads alone, which Windows
  refuses while the rename still holds the file with delete access, so it answered the IDE's first call 500 and the
  start failed, leaving nothing running (once in four class runs on the Windows VM; a reader polling against a rename
  met 228 to 370 sharing violations in 20 s, none sharing delete). **The test's stand-in** reads the key sharing delete,
  and the wait names the status, AppHosts, consoles, Build window and programs started (a94088da).
- `BuildRunTests`' `A_build_goes_on_through_the_language_server_starting_again_while_it_runs`, CI Linux, runs
  40204484945 and 40312082824: "the language server's restore left no MSBuild node", its server's load failing with
  "the project host ended". The WPF design-time hold test before it deleted its folder as its window closed while the
  project host every window shares was building it there; MSBuild in the host, its working folder gone, failed as its
  build session ended (MSB0001, exit 134), and the next test's round waiting in that host failed, its projects left
  unloaded. **A race in the IDE**, which a person deleting a folder under its build meets too: a round whose host ended
  is asked once of a host started anew, and the host's end says its exit code and what it wrote (e8c58f00).
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
- `XamlTests`' `An_elements_rename_in_its_xaml_a_moment_after_the_xaml_was_written_…`, CI's macOS runner at d274f75f8: the
  rename said "Renamed", in the XAML alone. It waited only on signs of a recent write, its time within 2 s or the
  watcher's word, and a write by another program can have neither on a loaded machine. **A gap in the IDE**, read from the
  code (the run kept no log) and held by a test that gives the same words: the rename asks what the server holds of the
  XAML itself (5ac081ec7).
- `UpdatesTests`' `An_app_on_another_volume_…`, CI's macOS runner at d274f75f8, in its `finally`: hdiutil's last exit code 1,
  no words (`-quiet`), nothing open. Read as a forced detach taking the volume after saying busy. **The test**: what counts
  is whether the volume is mounted, and hdiutil's words are kept (`TestVolumes.Detach`, 2aaee55ca).
- `NoDotnetTests`' `Install_dotnet_SDK_asks_then_installs_…`, CI's Windows runner at 00200fd83: the stages end at "Waiting
  for Windows to let go of the .NET SDK's sdk", the move giving up on what held it after 30 s. **The IDE's limit**: a
  scanner reads every file just unpacked, and the same install gave the SDK's first run 3 minutes for that reason. The
  move now waits as long.
