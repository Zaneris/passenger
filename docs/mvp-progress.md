# MVP milestone progress

Updated: 2026-10-16 (Indian/Mauritius). This records ongoing local implementation, verification and remaining work for the
[open MVP milestone](https://github.com/Zaneris/passenger/milestone/1). A passing focused test is not a claim that the
whole milestone, platform suite or release has passed. The 2026-10-15 evidence remains identified below; resumed work
adds new evidence without replacing it. Reviewed commits are being pushed to the fork's `origin/main` as requested.
No issue was closed, release published or tag pushed.

## Issue status

| Issue | Local result | What remains |
| --- | --- | --- |
| [#219 Slow Windows debugger startup](https://github.com/Zaneris/passenger/issues/219) | `93737dc` fixes the reproduced false five-second timeout for a live runtime and startup cancellation. Integration then exposed a cancellation-response/shutdown ordering race, fixed in `04b7d865`: the configuration lock covers writing success/error responses before disconnect can close the transport, preserving success-only continuation behavior. Both deterministic old-order controls fail; the fixed RC1 run passed 34 focused tests plus 10/10 repetitions of the original cancellation case. | The original CI incident's late-versus-lost callback cause still needs a trace under the failing runner conditions. Final combined validation is recorded below. |
| [#218 WPF generated-file contention](https://github.com/Zaneris/passenger/issues/218) | Reproduced the exact MC1000 overlap and identified `Passenger.Host` as the generated-file holder. Selected fix `2991601` persists separate design-time intermediate directories per project/SDK/evaluated properties and owns them across processes, preserving normal Build/Run/watch outputs. Final fixtures passed 9/9; broader checks passed 21 with one expected platform skip. Reverting the four product files makes the final fixture fail MC1000 in 7.5 seconds. Follow-up `3c4a034` repairs Roslyn's reported assembly identity and compiler `/out:` baseline without moving actual generated sources out of isolation; all nine isolation cases and both mixed managed/XAML cases pass. | Native platform/full-suite acceptance remains; CI3 Unix EnC failures are followed by baseline-path repair `cdb4592` and native Linux checks below, with a new complete CI run required. Target-time or local `IntermediateOutputPath` overrides are explicitly unsupported with actionable reasons and recovery. Cache pruning is not implemented. The earlier graph-coordinator experiment was superseded, not shipped. |
| [#217 MAUI template inventory](https://github.com/Zaneris/passenger/issues/217) | `d94d4af` passed the original three family tests with MAUI absent and a temporary MAUI-tagged template positive-branch check. Native Windows CI then exposed truncated SDK tag-column parsing in the test expectation; `8116317` reads complete tag columns. The revised four-case class passed locally, and Windows shard 2 in native CI3 at cc7456a passed the corrected inventory assertion. | Native Windows runner acceptance is now observed for that checkpoint; the complete cross-platform CI result remains separate. |
| [#216 Dialog answer order](https://github.com/Zaneris/passenger/issues/216) | Shared footer ordering puts competing answers before Cancel on Windows/Linux and the default last on macOS. Thirteen focused Windows tests and reviewed dark/light scale-2 captures are followed by native CI3 at `cc7456a`: both `DialogButtonsTests` theme cases and the real Open Solution/Don't ask again boundary pass on all three systems. | The focused implementation now has native Windows/macOS/Linux test evidence. Remaining whole-UI states and native Rider comparison belong to #213. |
| [#215 Cross-platform locked restore](https://github.com/Zaneris/passenger/issues/215) | `7ca9d31` fixes the WPF inspector's shared six-RID list/lock entries; six forced locked restores and full self-contained ReadyToRun App restore graphs passed under RC1. Native CI exposed a separate Roslyn extension host/target cross-publish failure; `bbd39c1`, merged in `04e303f`, fixes the portable extension identity. The subsequent native CI packaging jobs all passed, including cold-cache macOS x64. | Final release dry run and native install/use acceptance remain separate from successful restore/publish/package construction. |
| [#214 Await-step/return-watch race](https://github.com/Zaneris/passenger/issues/214) | Diagnostics added in `3a1ef55`; no race fix claimed. The original await scenario passed 100/100 launches; a return-value boundary probe also passed. Final step/async-step/synthetic-value regressions passed 14/14. | A complete failing engine trace or deterministic other-frame return-watch/completion interleaving is still missing. The 100 clean runs did not exercise that interleaving. |
| [#213 Whole-UI check](https://github.com/Zaneris/passenger/issues/213) | Ongoing. Resumed work covers responsive docking/current menus/active tab close, all 20 Settings pages, shared editor/output/list scrollbars, terminal scrollback/alternate screen, readable empty-editor headings, Chrome pages/disabled actions, actual Terminal process teardown, ten wrapping hints, responsive Profiler/NuGet headers, shared narrow/floating headers and focus (`16e843e`), populated profiler rows (`4fff99c`) and long feed/Problems summary labels (`1339d80`). See the precise evidence below and [surface inventory](ui-audit-213.md). | Continue unexamined dialog/menu/popup/populated-tool states, Terminal session-tab overflow, remaining populated Profiler/NuGet/History/Git/Problems bodies, popup scale/shadows and checkbox focus. The dialog audit captures 22 types and the tool sweep captures all 17 registered surfaces in both themes; these counts are not whole-UI visual passes. Native Rider behavior was not observed; bundled defaults were inspected. Unfinished source work is not an external blocker. |
| [#211 Final MCP pass](https://github.com/Zaneris/passenger/issues/211) | Audited the existing 65 tools and tests; concrete gaps listed below. | Remaining implementation and per-feature SDK-client tests. The owner requires this final pass after other MVP work; no completed final pass is claimed. |
| [#208 Editor/server line-break drift](https://github.com/Zaneris/passenger/issues/208) | Existing `LspDocument.Unsplit` fix confirmed; both exact line-break regressions passed locally and in the complete native Windows/Linux/macOS CI3 run at `cc7456a`. | No further source change is indicated by these results. Overall CI failures are separate from this issue's passing regressions. |
| [#206 CI shards](https://github.com/Zaneris/passenger/issues/206) | `0ac31f0` models collection finish time; `fd80a32` fits four macOS test shards beside one sequential two-image job into five runner slots. `70efe3c` refreshes successful native class measurements and gives Windows five shards; Linux/macOS retain four. Real-suite exactly-once discovery/result checks remain. | Observe a successful CI run below 25 minutes; measured-weight estimates are source/listing-specific and exclude build/discovery/queue/package overhead. CI3 used the original allocation and Windows shard 3 failed its 23.7-minute budget despite no test failures. The scheduling evidence below records retained weights/fallback limits, with later native measurements under review. |
| [#57 Linux packaging/install](https://github.com/Zaneris/passenger/issues/57) | Native CI packaging passed at `04e303f`; x64/ARM64 archives passed architecture and actual install/update/uninstall/settings-retention checks. Native RC1 Ubuntu x64 publish `cc7456a` passed build, Roslyn, run, breakpoint/evaluate, CPU profile and PTY via MCP. Later native Linux ARM package/install/installed frame/MCP/uninstall checks passed independently of its missing-shell smoke failure, now repaired locally by `fc974f5`. | ARM smoke rerun after the terminal repair and an interactive Ubuntu desktop pass remain. The release dry run remains sequenced after the feature/MCP/site work. |
| [#56 Release workflow](https://github.com/Zaneris/passenger/issues/56) | Existing workflow covers tests, six RIDs, fixed asset names, version/feed checks, signing hooks, checksums, tag notes and a non-publishing dry run. Packaging/release shell syntax checks passed. | The owner's required order is other MVP work and green three-system CI, then #211, then #55, then the final dry run, then an owner-chosen tag. The final dry run and tag were not performed out of sequence. Native signing/notarization was not exercised. |
| [#55 Website/README refresh](https://github.com/Zaneris/passenger/issues/55) | Deferred as the owner explicitly requires this last, after the feature/MCP passes. Existing site, README and comments were inspected. | Commit a repeatable sample and screenshot command; retake all required views in both themes; revise the whole site/README/share card against the finished feature set. The `www` redirect needs the owner's Cloudflare dashboard because the deploy token cannot write redirect rules. |
| [#33 Hot Reload](https://github.com/Zaneris/passenger/issues/33) | Managed-code tests passed 8/8 and edit tests 15/15. Earlier Avalonia literals passed 11/11 locally and native macOS/Linux CI at `33ddf0d`; WPF literals passed 4/4 on Windows x64. Saved-editor XAML tracking for run/watch is merged at `cc7456a`, after Host baseline repair `3c4a034`. Avalonia future-view initialization `b6aad68` and inspector protocol-v7 runtime failure events are also merged; the expanded actual XAML/WPF cases passed 32/32 on Windows x64. | CI3 at `cc7456a` failed Unix managed/mixed/remap cases. Later `cdb4592` passes native Linux 24 managed/remap cases plus two corrected-harness mixed cases and a real MCP applied-edit/result check; new full CI and macOS acceptance remain. Broader structural/attribute-set/binding/markup/resource/style/template support remains software work. Future-view initialization covers accepted Avalonia edits, not arbitrary reconstruction. Native evidence for the earlier literal checkpoint does not cover later run/watch, future-view or Host changes. |
| [#19 Data breakpoints](https://github.com/Zaneris/passenger/issues/19) | Existing Windows x64 implementation and Variables/Breakpoints UI verified by all four data-breakpoint tests, none skipped. | Current engine/runtime availability is explicitly Windows x64. macOS/ARM support was neither implemented nor verified in this pass. |
| [#1 Windows verification](https://github.com/Zaneris/passenger/issues/1) | Existing CET teardown protection runs a paused thread off exception/single-step dispatch before detach; its attach/detach regression passed. The combined data-breakpoint/Hot Reload/CET audit passed 28/28 on Windows x64. Native ARM workflow execution has since begun, separately from these x64 checks. | Full Windows x64/ARM64 suite and native interactive build/run/debug/profile acceptance remain distinct from focused headless tests and installed-copy smoke checks. |

## Resumed implementation evidence: 2026-10-16

This work continues through the remaining source gaps; the following checkpoints do not finish #213 or #33.
All counts are attributed to the source tested, rather than added together as one suite result.

| Source checkpoint | Verification and retained evidence |
| --- | --- |
| `7602f8a`, responsive docking | 14 Docking tests and six Live Tree cases passed. Presentation-only collapse keeps the requested open panels, sizes and selections in persisted layout; effective visibility, stripe toggles, focus transfer and explicit Hide are checked. Six scale-2 dark/light captures at 640/683/1200 were inspected. |
| `c0a648b`, current-value menus | 51 File Properties/Menu/VCS/Run Configuration cases passed. Choosing the checked current encoding/separator neither edits unsaved text nor adds an undo step. Eight dark/light encoding, separator, branch and configuration menu captures were inspected. Unsupported-platform configuration muting keeps its explicit reason. |
| `c7a8794`, active editor tabs | 15 Editor Tabs/Active Editor cases passed. Active tabs show their close button without hovering, with unsaved dot and pinned pin in separate slots. Four scale-2 dark/light captures at 683/1200 were inspected. |
| `626f3fe`, responsive Settings | 16 Settings/Keymap/Live Templates cases passed; exact RC1 build had zero warnings/errors. Every one of the 20 pages was checked at 980→640→683→980 against the page viewport, including untrimmed text layouts. Typed values, text selection/focus and the chosen template survive resizing; wide editor/note columns share a left edge. All 92 dark/light normal, narrow and scrolled-end frames were inspected. |
| `bce47d4`, shared scrollbars | Exact RC1 build had zero warnings/errors; 39 editor/stripe/folding/Settings cases and five Go To navigation cases passed, zero skips. Six-pixel thumbs have twelve-pixel pointer targets, no arrows or opaque track, with wheel, page-click and two-axis drag behavior retained. Short files/consoles lose their bar; diagnostic marks remain clickable. Stable wrapping/reveal/preview rails do not depend on thumb visibility; pictures disabling bars and stripe reserve no rail. The previous wrap expression fails the calibrated resize regression with 64 rows instead of 32; restored source passes. Twelve final dark/light editor, console and list captures, including 683-pixel views, were inspected. |
| `2a4df3b`, terminal overlay | Exact RC1 build 0 warnings/errors. Terminal classes report 21 passes in 18 seconds; 13 existing Unix-only test bodies return early on Windows, so 8 cases execute here. Real shell input, dragging, wheel, selection, output while reading, 640/683/1200 resizing and bounded 10,000-line eviction are covered. Capture review caught normal history leaking into the alternate screen; strengthened checks and the fix require the actual full-screen text, ignore history paging there and restore normal history without typing. All 10 verified dark/light frames were inspected. |
| `2ba036b`, empty-editor heading | Long folder title and next step fit actual editor bounds beside Explorer/Build at 1200→640→683→1200 and after closing the last file. New both-theme cases plus Welcome minimum regression passed 3/3. All 6 scale-2 frames inspected. Build 0 errors, 7 unchanged intentional fixture warnings. |
| `5ce60d7`, dialog shared rules | Exact RC1 build 0 warnings/errors; ten boundary classes passed 48, one expected Windows credential-provider skip, zero failures in 2m29s. Actual painted Chrome/disabled-primary/transparent disabled-tool rules apply through every existing `DialogButtonsTests.Check` caller. Optional audit restores each requested theme after checking/capturing both. 124 scale-2 frames cover 22 dialog types; 30 representative frames, including every migrated page wrapper in both themes, were inspected. Full inventory continues. |
| `6ef4205`, Terminal tool tabs/lifecycle | Exact RC1 build 0 warnings/errors; four terminal/tab classes report 25 passes in 25 seconds (12 actual Windows cases; 13 older Unix-only bodies return early). Actual cmd history/input checks active/inactive close, selected state, focus, tab order and kept reading position. Real run configurations check natural code 3, Stop/disposal/CloseAll, an inherited-output child flood and exactly one actual exit. Immediate shell shutdown/failure paths go through MainWindow. Old immediate disposal fails the shutdown regression in 931ms with a missing exit notification. All 17 tool surfaces have 1200/683 two-theme captures; four final Terminal frames inspected. Root's merged `1e19199` solution build has 0 errors/7 intentional fixture warnings, and three existing debugger-terminal/MCP integration cases pass in 28 seconds. |
| `0c4e149`, wrapping tool hints | Shared hint style fits Hierarchy, Git, Unit Tests, Profiler, Find, Structure, Bookmarks, Local History, Problems and Debug. Exact RC1 build 0 warnings/errors; Tool Layout/Problems/Local History 12/12 pass in 31 seconds. Actual TextLayout plus every ancestor viewport is checked at 1200→683→640→1200 with requested 200px side panes. 80 scale-2 frames captured; 24 representative normal/narrow whole frames inspected across all ten hints and both themes. Profiler-tab and NuGet-toolbar clipping remain separate active source fixes. |
| `7b28e51`, responsive profiler header | Shared scrollable tab strip reserves Open Snapshot; runtime actions wrap in their own row and status has a full-value tooltip. A real profiled program drives all eight pages by wheel/click and Tab/Shift-Tab/Space. Exactly one selected page, focus and fitted actions survive 640/683/1200 resizing. Tool Header/Profiler/Tool Layout pass 8/8, zero skips, in 43s; RC1 build 0 warnings/errors. Eight final normal/narrow running/Snapshots frames inspected in both themes, plus 14 earlier same-source page frames. Empty Memory guidance and long populated rows remain actionable follow-ups. |
| `2952092`, responsive NuGet tools | Actual local nupkg search, feed choice, prerelease and refresh keep query/caret/focus/selection through 640/683/1200 resize and 200px side placement. All toolbar actions fit without overlap; status text fits or trims with complete tooltip. New both-theme cases pass 2/2; combined Package Sources/new cases pass seven with one expected Windows credential-provider skip in 15s. RC1 build 0 warnings/errors; all 14 scale-2 frames inspected. Shared narrow tool-header tab overflow and abrupt long feed-label clipping remain separate fixes. |
| `16e843e`, shared tool headers/focus | All five header-tool consumers fit 200px side panes and bottom/floating placement, retaining fixed Options/Hide actions and a separate scrollable tab row when needed. Real pointer/wheel/keyboard selection, active/inactive close, thumb hit/drag without pane drag, repeated 640/683/1200 resizing, dynamic titles and focus through reparenting are checked. Tab controls retain identity; the rounded keyboard ring cannot intercept the scrollbar. Floating tools follow live owner theme changes and unsubscribe on close/reopen. Exact RC1 build 0 warnings/errors; 72 total cases, 71 pass/one expected Windows credential-provider skip, zero failures in 2m39s. Thirty-two final scale-2 header frames captured; twelve inspected, including all five genuinely Light floating panes, both focus rings and representative bottom/side frames. Populated Sources/History/Git body clipping remains separate source work. |
| `fc974f5`, Unix terminal missing executable | Native WSL reproduced the CI missing-shell failure: Porta reported PID 775/running, then child exit 2 and a blank closed tab. Missing explicit Unix paths are now rejected before PTY allocation, with relative paths resolved against the actual working folder. Both failures retain their diagnostic tab without PID/connection; a real relative script in a spaced folder takes keyboard input and exits 2 normally. Windows final run reports 23 passes in 23s (13 older Unix-only bodies return early); clean exact committed Linux x64 source passes all 23 actual terminal lifecycle/input/tab cases in 46s, zero skips/failures. Six Windows/Linux scale-2 missing-path frames inspected in both themes. This is not an exec-handshake, PATH lookup, interpreter/permission or time-of-check race fix. macOS/ARM reruns remain. |
| `4fff99c`, populated profiler rows | Actual CPU completion explains that no heap snapshot was taken; long Call Tree/Hot Spots/allocation rows and a real held-40-object heap snapshot retain accessible names, aligned numbers, virtualization and selected rows through resize. User scrolling away preserves reading offset. Profiler/Memory Snapshots/Tool Header pass 14/14 in 1m24s, zero skips; build 0 warnings/errors. Normal 1200/narrow 640 scale-2 frames inspected in both themes, with 683 also captured. Evidence is shell `tests/Passenger.Tests/TestResults/profiler-and-memory-final.trx`, `artifacts/arm-validation/{profiler-final-build,profiler-and-memory-final}.log` and `artifacts/profiler-populated/captures-final/`. Busy, diff and frame states remain separate. |
| `152062a`, native Linux profiler follow-up | Clean exact committed source rebuilt with native Ubuntu x64 RC1. Profiler seven, Memory Snapshots three and Tool Header six cases pass 16/16, zero skips, in 2m55s; selected-row visibility and scroll-away resize assertions execute there. Evidence is shell `artifacts/profiler-populated/native-profiler.log`, `native-linux/profiler-memory-linux.trx` and `native-linux/captures/`. This does not establish remaining busy/diff/frame states. |
| `1339d80`, long selected feed and Problems summary | Real local-feed selection and missing-baseline debugging check ellipsis, full-value tooltips, actual text layout and clipping ancestors at 1200/683/640, with a 200px NuGet side pane. Four both-theme cases pass in 24s; existing Problems/Package Sources pass seven with one expected Windows credential-provider skip in 22s. Build 0 errors/seven known fixture warnings. Eight final normal/narrow scale-2 frames inspected in `long-labels/`; ordinary Problems message/detail rows still clip narrowly and remain source work. |
| `6d1752c`, Terminal session overflow/focus | A real long-named terminal run and seven actual shells keep tabs/close actions reachable at 1200/683/640 and in a 200px side pane. Names ellipsize beside close actions with full-name tooltips; wheel/pointer/Tab/Space selection routes commands to the selected shell and closing the background run ends its actual process. Resize reveals the selected session, or an inactive tab while it has keyboard focus, without changing selection. Prior selected-only reveal fails the focused-tab viewport assertion in both themes. Final product passes eight Tool Tab/Lifecycle/shared-header cases in 1m16s, zero skips; the final test-only real-Tab-key focus refinement passes both affected cases in 24s. Builds 0 warnings/errors. Eight geometry frames, four focused-resize frames and the two final visible-keyboard-ring frames inspected separately. Native Unix execution of this overflow addition remains pending. |
| Avalonia XAML `77f0a72` + `e1d3855` | `XamlHotReloadTests` passed 11/11 on exact RC1; debugger worktree `tests/Passenger.Tests/TestResults/xaml-reload-getters.trx`. |
| WPF XAML `ffe2706` | `WpfXamlHotReloadTests` passed 4/4 on exact RC1; debugger worktree `tests/Passenger.Tests/TestResults/wpf-xaml-first.trx`. Both-framework scale-2 before/after captures are under `artifacts/bin/Passenger.Tests/debug/{xaml-hot-reload,wpf-xaml-hot-reload}/`. |
| WPF design-time isolation `2991601` | Final nine-case fixture passed; broader 21 passed with one expected platform skip. `artifacts/wt-shell-isolated/tests/Passenger.Tests/TestResults/{isolation-final-fixtures,isolation-checkpoint}.trx`. Imported/global paths, selected references, generated navigation, cache reopen and Build/Run/watch overlap are checked. Shell worktree `artifacts/isolation-probe/tests/Passenger.Tests/TestResults/final-fixture-negative.trx` retains MC1000 with the four product files reverted; holder PID 12024 is `Passenger.Host`. |
| Host baseline identity `3c4a034` / XAML `cc7456a`, `b6aad68` | Host's design-time paths stay isolated while reported assembly metadata and `/out:` point Roslyn at the real baseline. Nine isolation plus two previously failing mixed managed/XAML cases pass. Saved-editor run/watch tracking and accepted Avalonia edits in newly created views are merged. The expanded XAML/WPF run passes 32 actual cases; inspector protocol v7 reports runtime application failures. These source checkpoints supersede the earlier Host-identity investigation without implying all XAML editing is supported. |
| Later XAML `feb9254` (2026-10-16) | Literal attribute additions with binding-safe rollback and inspector wire version 8 are merged. The expanded actual Windows XAML/WPF cases pass 41/41, with an observable-guard negative control. This is a later checkpoint than the 32-case row above; the native Linux generic EnC failures at `cc7456a` remain under investigation. |
| Unix EnC baseline `cdb4592` (2026-10-16) | MSBuild full assembly paths replace Windows-only path interpretation for the reported Roslyn baseline. Native Ubuntu exact-source `fullpath-native26.trx` passes 24, with two mixed cases failing before their bodies because of a harness path; corrected `fullpath-native-mixed2.trx` passes both. The actual Linux MCP probe applies a C# edit in 323ms and observes result 3; the old `cc7456a` control reports no managed changes and result 2. Evidence: debugger `artifacts/linux-enc/` TRX files and shell `artifacts/cross-publish-cold/linux-enc-fixed-probe{.log,/summary.json}`. Current whole-system CI acceptance still requires a new run. |

The shell agent's completed exploratory full Windows run used the superseded graph-coordinator snapshot `d2f5633`:
868 total, 857 passed, two failed, nine skipped in 59.99 minutes. Its About assertion used binaries stamped
`3b41974` before the `d2` commit. The other failure was an MCP deliberate-design-time-sleep load/run timeout on that
obsolete coordinator. On selected-isolation source `bbd39c1`, the exact MCP case passed in 27 seconds and both About
checks passed. These reruns do not turn the historical full run into a green full suite for current main. A separate WPF native-picker probe
remains timing-sensitive; no picker fix is claimed.

Root's combined solution build at `33ddf0d` passed with zero errors and seven unchanged intentional fixture warnings,
in `artifacts/milestone-1/integration/resumed-build.log`. Later source checkpoints require their own combined checks;
focused counts above do not establish full Windows/macOS/Linux acceptance.

The [native CI run at pushed `33ddf0d`](https://github.com/tebeco/passenger/actions/runs/38807870015) completed all four Linux
test shards green. Windows exposed the #217 parser and #218 overlap. The only macOS test failures were the two new
RuntimeStartup protocol fixtures' overlong Unix pipe paths; another macOS shard failed only its runtime budget.
Avalonia literal-XAML tests at this checkpoint therefore have native macOS/Linux evidence. The run's
[macOS x64 packaging job](https://github.com/tebeco/passenger/actions/runs/38807870015/job/116673341029)
exposed the Roslyn server host/target failure. The [packaging rerun at `04e303f`](https://github.com/tebeco/passenger/actions/runs/38810456744)
passed every packaging job: macOS x64 cold-cache, macOS arm64, Linux archive, Windows installer and package checks.
This does not establish native desktop install/use acceptance. macOS startup tests exposed the new protocol fixture's
overlong Unix pipe path; `3a83de1` shortens it, with native rerun still required. Observed macOS shards took about 21/22.7 minutes;
the complete below-25-minute acceptance remains unestablished. SDK pin `a99e88b`, merged after the earlier
pushed checkpoint, changes `global.json` to disable roll-forward: ordinary `dotnet` now selects RC1. Its focused
About/Windows-only-project checks passed three with one expected skip. Earlier direct-RC1 commands and temporary
worktree-only pin evidence below remain historical; they are not still required by current main.

The completed [CI3 run at pushed `cc7456a`](https://github.com/tebeco/passenger/actions/runs/38815446318) uses the original
allocation; collection-aware `0ac31f0` landed afterward. Coverage checked all **909 results per system**, without
missing/duplicate errors. Windows reports 901 passed, eight not executed and no test failures; shard 3 failed only
its 23.7-minute budget. Linux reports 847 passed, 26 failed and 36 not executed; macOS reports 856 passed, 28 failed
and 25 not executed. Both Unix systems failed 23 managed EnC, two mixed and one remap case; macOS also failed the
known WPF navigation and Self Debugging cases. All five packaging jobs passed. These are historical failures before
the later Unix baseline repair, not a green current suite. Root retained all 11 result artifacts under
`artifacts/milestone-1/integration/ci-38815446318-results/`. Both exact #208 line-break regressions passed on all three
systems in that full run; `ci-38815446318-line-break-evidence.json` records all six results.
The current Windows terminal integration evidence is root
`artifacts/milestone-1/integration/terminal-debug-mcp.log` and
`terminal-debug-mcp-results/terminal-debug-mcp.trx`: three existing actual debugger/MCP terminal cases pass after
merging `6ef4205`, including a breakpoint/restart/input flow and Stop with child cleanup.
Root's later merged `2dd7af7` build also passed; Tool Layout plus an existing WPF declaration/navigation regression
passed 3/3 in eight seconds after merging canonical-root navigation repair `d9ce446` and wrapping hints `0c4e149`.

Linux checks progressed beyond archive inspection. Earlier cross-published x64/ARM64 archives are approximately
199/189MB and each contains 40 ELF files checked for its target architecture. Actual Ubuntu offscreen Welcome,
per-user install/update/uninstall under a spaced HOME with retained settings, and disposable-container system
install/update/0644 desktop-entry permissions/uninstall/settings retention passed. These archive checks were
cross-publish followed by Linux execution, not native SDK publishing.
Source `cc7456a4046016b6a4b2971a6041ed11d66d9a5e` was then published with a real temporary, checksum-verified RC1 SDK
inside Ubuntu 24.04 x64 WSL. MCP drove a real project build (one deliberate unused-variable warning), Roslyn symbol
search/diagnostics, Run, a breakpoint and `Helper.Twice(21)` evaluation returning `42`, CPU profiling and the native
PTY shell. Evidence under `artifacts/wt-shell-isolated/artifacts/cross-publish-cold/` includes
`linux-current-publish.log`, `linux-current-probe.log`, `linux-current-probe/{summary.json,mcp-events.jsonl}`,
`wsl-package-{x64,arm64}.log`, `wsl-install-user.log` and `docker-install-system.log`.
This establishes native Linux x64 binaries and headless/project workflows in WSL. Later ARM execution is recorded
separately below; interactive desktop acceptance is not inferred from these headless checks.

Native CI4 `38821710686` exposed the same missing-shell diagnostic failure on macOS shard 3 and Linux x64 shard 1;
Linux ARM smoke `38822928529` also failed only that case (eight passed, one failed). The native Linux ARM package,
install, scale-two installed frame, installed MCP case and uninstall checks passed independently. Retained root logs
are `ci-38821710686-{macos-3,linux-1}.log` and `arm-38822928529-linux.log` under
`artifacts/milestone-1/integration/`. The failure is repaired locally by `fc974f5`; successful x64 WSL execution is
not a claim that its macOS/ARM reruns have completed. Native ARM full run `38827279628` is now active at pushed
`47f7a57`, without a completed result yet.

The earlier allegation that Windows/Linux ARM Welcome buttons painted differently was a visual-review error,
including the initial root observation. Pixel comparison of the original frames found identical button fill
RGB 57/59/64, borders 78/81/87 and focus 61/123/245: 1,064 button border/padding pixels and 7,700 focus-border pixels
have zero differences. The exact native `4a2804d` Linux package under emulation also reproduces its original PNG
byte-for-byte. Shell `artifacts/welcome-arm/paint-pixels.json` retains the evidence; no product fault or fix is claimed.

The final header run is `tests-213-shared-header-final.log` with
`test-results-213-shared-header-final/ahert_CORSAIR13900_2026-10-16_03_32_07_net11.0.trx` in shipping
`artifacts/2026-10-16/`. Terminal evidence there includes `terminal-native-before.log`,
`terminal-native-probe.log`/`terminal-native/missing-shell-probe.trx`, `tests-terminal-path-final.log` and
`terminal-native-exact.log`/`terminal-native/terminal-path-exact.trx`. The last native run rebuilt clean exact source
`fc974f5c8aab8648c06391033672b590c542279f`; earlier native patch runs are explicitly separate.

### Current shared-UI audit coverage

| Owner requirement | Current evidence / continuing work |
| --- | --- |
| Centered content, shared radius/height and footer order | Shared tokens/base styles and `DialogButtonsTests.Check`; opened-dialog coverage exists, complete surface inventory continues. |
| Checked current menu values | Branch/configuration/encoding/separator helper and boundary tests/captures, `c0a648b`. |
| One tool-window tab style | Terminal shares `ToggleButton.panelTab` (`6ef4205`); Profiler tabs scroll with real pointer/keyboard checks (`7b28e51`). Shared tool headers preserve tab identity/selection, keyboard focus, thumb hit testing and narrow/floating actions (`16e843e`). Terminal's many-session/long-name strip and inactive keyboard focus now fit (`6d1752c`); other populated bodies remain. |
| Thin overlay scrollbars without arrows | Shared editor/output/list `bce47d4` and real terminal scrollback/alternate behavior `2a4df3b`; remaining individual surface review continues. |
| Light plain buttons white with a border | Shared token and both-theme rendered pixel checks, `30d3965`. |
| Popup/menu shadows; rounded centered focus, including checkboxes | Rounded tool-tab focus ring is visible without covering real scrollbar pointer targets (`16e843e`); popup and checkbox surface checks remain. |
| Popup width/title/row scale and one dropdown width per column | Settings dropdown width/column alignment are centralized; remaining popup/dropdown inventory continues. |
| Disabled default button distinct from Cancel | Shared Rider-default tokens/base and primary styles; painted-state assertions plus real disabled Enter/hover and re-enable workflow, `5ce60d7`. |
| Every dialog page uses Chrome | Base Window and shared dialogPage style migrate all 7 explicit form-page wrappers found; 22-type boundary sweep checks actual paint. Full-code diff/merge surfaces have documented exceptions. Remaining surface inventory continues. |
| Footer-left Don't ask again | Shared leading-footer helper and Open Solution dark/light captures, `331540a`; real Open Solution boundary and both footer-order themes also pass on all three native CI3 systems. Complete dialog inventory continues. |
| Active editor tab close button | Modified/unmodified/pinned/hover behavior and captures, `c7a8794`. |
| Half-screen layout and preserved user layout | Toolbar/Welcome, simultaneous-side collapse and all Settings pages at 640/683 are covered by the targeted tests above; remaining tool-window/dialog states continue. |

Rider 2026.2.3.1 is installed locally. Its bundled new-UI theme defaults were inspected: control arc 8 (radius 4),
button minimum height 28 and tab height 40, among others. This is an actual source reference, not an observation of
native Rider interactions. Native comparison remains a limitation while local source faults continue to be fixed.

The resumed shipping captures live under `artifacts/ui-audit-213/2026-10-16/`: `dock-live-tree-*`, `menu-*`,
`active-modified-tab-*`, `settings-before/`, `settings-final/`, `scrollbars-verified/`, `terminal-scrollbar-verified/`,
`empty-editor/`, `dialogs-verified/`, `tool-tabs-real-output/`, `tool-hints-verified/`, `profiler-header-final/` and `nuget-header-status/`. The [surface inventory](ui-audit-213.md) identifies captured versus unexamined states. The earlier exploratory
scrollbar folders retain the too-thin Fluent transform reproduction. Settings final filenames identify every page,
640/980 width, theme and scrolled end; no full-dialog or full-tool-window visual pass is inferred from those files.
Build/test logs and TRX are in the shipping worktree's `artifacts/2026-10-16/`, including
`build-213-settings-final.log`, `tests-213-settings-final.log`, `build-213-scrollbars-verified.log`,
`tests-213-scrollbars-verified.log`, `tests-213-scrollbars-navigation.log` and `tests-213-scrollbars-negative.log`.

To regenerate the targeted whole-window capture inventory using the exact RC1 invocation defined below:

```powershell
$env:PASSENGER_UI_AUDIT = Join-Path (Get-Location) 'artifacts/ui-audit-213/2026-10-16/reproduced'
& $dotnetExe exec $rc1Cli test tests/Passenger.Tests --no-build --no-restore --filter 'FullyQualifiedName~SettingsTests|FullyQualifiedName~ScrollBarTests'
Remove-Item Env:PASSENGER_UI_AUDIT
```

## CI scheduling evidence

`tools/test-shards.py` chooses classes by recorded system times, checks the selected lists against the real full suite
before running, and checks exactly one result per test after running. `0ac31f0` refreshes native CI weights and models
the suite's assembly/Debugger/Alone collections when estimating each shard's finish time; unknown conventions fail
explicitly. Summary coverage still reads each system's count from shard metadata. New classes use their configured
budget until native timings are collected.

`fd80a32` changes macOS to four test jobs and one image job that checks both disk images sequentially, even if the
first fails, retaining the x64 cold-cache/host-package guard. Four test jobs plus one package job fit the known
five-slot limit. Its earlier 923-test listing projected 16.4 minutes per macOS shard; the later 934-test listing
projected 16.7. These are source-specific estimates, not observed below-25-minute acceptance.

The completed [CI4 run at `f4d89b9`](https://github.com/tebeco/passenger/actions/runs/38821710686) contains exactly one
result for each of its **943 tests in 129 classes per system** across all 13 shards. Windows reports 934 passed,
nine not executed and no test failures; Linux reports 902 passed, 40 not executed and one failure; macOS reports
911 passed, 30 not executed and two failures. Windows shard 3 took 20.18 minutes of tests and macOS shard 2 took
21.55, exceeding the unchanged 20-minute budget. The complete run took 25m55s, so the below-25-minute acceptance
remains open. Both disk images passed in the single sequential image job; all packaging jobs succeeded.

`TestTimes.json` now takes the measured CI4 seconds of classes with no failed result: **129 Windows, 128 Linux,
127 macOS**. This includes the 31-case XamlHotReloadTests cost that the earlier weights understated: 436.1 seconds
on Windows, 343.7 on Linux and 341.6 on macOS. With the actual CI4 class assignments held fixed, the measured
collection model accounts for all but 7–32 seconds of each shard's VSTest elapsed time. Build, discovery, queue
and packaging costs are separate. No failed run is declared green by updating its successful classes.

The failed classes retain the following prior inputs; their failing elapsed times are recorded, not used as healthy
measurements. These explicit limits remain until the next native run:

| Class/system | Weight used | Observed CI4 coverage and elapsed |
| --- | --- | --- |
| HotReloadTests, macOS | 100.0 seconds from the eight-case `33ddf0d` observation | 12 cases, 11 passed and one failed, 175.3 seconds; the retained weight understates added coverage |
| TerminalLifecycleTests, Linux | Existing default budget, 60 seconds; no earlier measured class | Two cases, one passed and one failed, 10.4 seconds |
| TerminalLifecycleTests, macOS | Existing default budget, 60 seconds; no earlier measured class | Two cases, one passed and one failed, 11.1 seconds |

| System | Shards | Projected test minutes from exact `152062a` discovery (948 tests, 129 classes) |
| --- | --- | --- |
| macOS | 4 | 16.77, 16.75, 16.75, 16.75 |
| Windows | 5 | 15.82, 15.82, 15.82, 15.82, 15.82 |
| Linux | 4 | 16.30, 16.31, 16.31, 16.30 |

The matrix remains five Windows shards and four each on Linux/macOS, with the same budgets. The projections
exclude runner/build overhead and unmeasured new coverage: ProfilerTests has seven cases at `152062a` versus
four in CI4, and ToolHeaderTests has six versus four. Further WPF coverage being developed after this checkpoint
also requires native measurement. The earlier CI3-weight table for exact `8f5534f` discovery (943 tests) projected
17.19–17.20 minutes on macOS, 16.75–16.77 on Windows and 15.72–15.73 on Linux; those estimates predated the
additional XAML cases' measured costs and used Python 3.11 float summation.

`7a03b7d` makes allocation comparisons use decimal seconds, preserving the reported float totals. Python's
[3.12 float-summation change](https://docs.python.org/3.12/library/functions.html#sum) made equal decimal finish
times break differently: the same frozen 943-test input and timing-file hash produced Linux counts
217/291/216/219 on actual Python 3.11.9 and 256/266/203/218 on actual Python 3.12.14, matching the CI discrepancy.
The corrected CLI passes four boundary regressions on both versions; all 13 generated filters and projected
outputs are byte-identical across them. Actual selected-test discovery checks the refreshed `152062a` filters:
all 948 tests appear exactly once per system across its five/four/four shards. Each later merged suite still needs
those coverage checks, followed by native timing and whole-CI acceptance.

The manual-only `.github/workflows/arm-validation.yml` prepares native Windows ARM64 and Ubuntu ARM64 acceptance on
GitHub's [documented hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
Its default smoke selection drives existing build/run, design-time baseline, launch/step, CPU profiling, managed Hot
Reload, real terminal lifecycle and MCP debugger tests. Every requested case prefix must still appear in discovery.
The optional full selection runs the existing whole suite unchanged, with a finite 90-minute test step/120-minute
job and the existing ten-minute blame timeout; it changes no regular CI budget. Both modes attempt native packaging,
per-user installation, a scale-two off-screen frame and debugging through the installed copy, then check uninstallation
keeps the person's state. Smoke failure does not suppress independent package/install checks.

ARM preflight records the actual OS/process and .NET host architectures, validates Windows apphost/native-library PE
headers or Linux packaging's existing exhaustive ELF check, and evaluates the paused debuggee's process architecture
through MCP against the test host. Results, engine logs and timing tables stay under `ArmResults/<rid>` in ARM-named
artifacts, with no ordinary shard metadata or x64 timing update. Cached ARM64 dbgshim images and Linux's ARM64 PTY
library are present; data breakpoints remain an explicit Windows x64 runtime capability. Local x64 validation checks
the workflow/PowerShell syntax, all eight required smoke prefixes (nine discovered tests), PE positive/x64-negative
controls, native-host rejection on x64 and the real MCP architecture assertion (one pass). These preparation checks
are separate from the later observed native ARM results above. Smoke/package runs leave the full Windows ARM64
suite and interactive desktop acceptance for their own verification.

The following is the historical `8048b8e` estimate, before the collection-aware timing refresh; it is retained as the
evidence for choosing three macOS shards, not a current runtime estimate:

| System | Shards | Estimated test minutes per shard from `TestTimes.json` |
| --- | --- | --- |
| macOS | 3 | 17.75, 17.74, 17.75 |
| Windows | 4 | 15.03, 15.03, 15.03, 15.03 |
| Linux | 4 | 13.03, 13.03, 13.03, 13.03 |

The previous four-shard macOS deal estimated 13.31 minutes each, but one shard waited 13 minutes while CI also
requested two disk-image runners. Three shards plus two image jobs avoid requesting a sixth macOS slot. Build,
discovery, package and queue times are excluded from the table; the issue's observed-under-25-minute acceptance stays open.

## Initial partial UI audit and captures: 2026-10-15

The shared footer now keeps Don't ask again at the left of its buttons on the same row, with enough dialog width for
both. Plain buttons read their fill from one theme token and their border from the shared field border; the light
fill is white. The existing dialog inventory runs in both themes and checks rendered content centering, matching
button geometry, answer order, the footer option's position and plain light-button fill/border.

The main window's minimum is now 640 logical pixels. Below 900 pixels the toolbar keeps project/branch dropdowns as
icons, shortens the configuration label, and returns the Build/Profile icons' space to Run/Debug/Stop/Apply Changes
and the window buttons. Build/Profile retain their menu/actions. Above that width a bounded budget shares label
space; widening restores the labels/icons. Configuration identity remains in its tooltip/dropdown and resizing does
not change the selection. A real `dotnet watch` run with long solution/branch/configuration names checks every visible
toolbar button inside the toolbar, with no overlap, at 640, 683, 800, 900, 1024, 1200 and 1400 pixels and back again.
The Welcome screen's project dropdown is exercised at 640 pixels with no solution open; its heading wraps, and the
regression checks every visible Welcome text layout and action button fits its space.

These are the captures inspected at scale 2, under the shipping worktree's ignored `artifacts/ui-audit-213/` folder:

| Capture files | Checked/found |
| --- | --- |
| `open-solution-dark.png`, `open-solution-light.png` | Footer option moved onto the buttons' row; the light capture exposed the remaining grey plain-button fill. |
| `open-solution-narrow-dark.png`, `open-solution-narrow-light.png` | Same footer at the previous 800-pixel main-window minimum; no clipping in either theme. |
| `open-solution-buttons-dark.png`, `open-solution-buttons-light.png` | Shared button token/border fix: plain light buttons are white and bordered; primary/plain geometry and footer baseline remain alike. |
| `half-tile-exploratory-dark.png` | Temporary lower-minimum probe before toolbar fixes: long names clipped toolbar actions and native window controls, establishing why lowering the minimum alone was insufficient. |
| `half-tile-run-dark.png`, `half-tile-run-light.png` | Corrected 683-pixel window during a live run: compact toolbar/native controls fit, editor and Run remain usable. |
| `welcome-minimum-dark.png`, `welcome-minimum-light.png` | Welcome at the new 640-pixel minimum after wrapping its previously clipped heading: title, subtitle, search, action buttons and empty-list text fit in both complete frames. This is a targeted minimum-width check, not the complete UI inventory. |

To reproduce the corrected targeted views after building, from the repository root:

```sh
mkdir -p artifacts/dialog-before artifacts/dialog-after artifacts/ui-audit-213
for theme in dark light; do
    dotnet artifacts/bin/Passenger.App/debug/Passenger.dll artifacts/dialog-before --open artifacts/dialog-after \
        --screenshot artifacts/ui-audit-213/open-solution-buttons-$theme.png --size 800x600 --scale 2 --theme "$theme"
    dotnet artifacts/bin/Passenger.App/debug/Passenger.dll tests/Debuggees/LoopingConsoleApp --file Program.cs \
        --click-name RunButton --wait 2000 --hover 200,600 \
        --screenshot artifacts/ui-audit-213/half-tile-run-$theme.png --size 683x768 --scale 2 --theme "$theme"
    dotnet artifacts/bin/Passenger.App/debug/Passenger.dll \
        --screenshot artifacts/ui-audit-213/welcome-minimum-$theme.png --size 640x768 --scale 2 --theme "$theme"
done
```

At this first checkpoint, `DockHost.Fit` shrank open side panes proportionally and reserved only 25% for the editor,
leaving roughly 150 pixels with both sides open at half-screen width. The resumed `7602f8a` checkpoint above implements
presentation-only pane collapse/restoration and checks editor usability without overwriting saved layout intent.
Native Rider comparison remains unverified.

## MCP audit: concrete remaining implementation

The current source has 65 `[McpServerTool(...)]` declarations under `src/Passenger.App/Mcp`, counted again on this
snapshot rather than taken from the issue's earlier count. Existing SDK-client tests cover C# code/debugging,
agent connection/approval/cancellation, Git/terminal operations,
and input/screenshots/failed bindings for Avalonia/WPF apps. Existing coverage and missing coverage should stay distinct:

| Area | Existing | Remaining |
| --- | --- | --- |
| Language tools | `PassengerTools.Code` routes to `Session.CSharp`; `format` has a separate XAML implementation. | Route diagnostics/navigation/usages/rename/symbols/actions/format through XAML, web/ESLint/Prettier and MSBuild providers with truthful unsupported-operation results and SDK-client tests. |
| App inspection | `app_tree` gives values; both frameworks have app input, screenshots and failed-binding tools. | Return property value sources and applicable/inapplicable style reasons. |
| Breakpoints/debug state | Existing line, XAML-attribute, exception and binding-error set/remove; data/property breakpoint enumeration. | Property-breakpoint set/remove parameters are absent. `DebugSnapshot` has locals/frames/exception, but no live XAML binding values. |
| Hot Reload | An IDE action can request Apply Changes; #33 now includes saved XAML run/watch tracking and protocol-v7 runtime failure events. | A structured MCP tool returning applied edits, rude edits with file/line and restart requirements, with per-provider SDK-client coverage. Runtime event support alone does not supply that tool. |
| Profiling | The `profile` result already includes Avalonia frame/layout costs when present. | Final SDK-client coverage for these costs and all remaining release actions/tool windows. Do not reimplement the existing result fields. |
| Preview/action coverage | `ide_actions` is backed by the action registry and general screenshots can see IDE panes. | Complete the requested per-action, per-tool-window, per-language coverage inventory with a tested MCP path or an explicit reason one is unnecessary, including preview behavior. |

## Reproducing focused verification

The first-pass shipping restore/UI checks used the installed .NET `11.0.100-rc.1.26425.128`, selected by a scratch
`global.json` with `rollForward: disable`. Some first-pass debugger audits used RC2 selected by the then-current
`latestFeature` setting. The resumed SDK pin `a99e88b` now selects locked RC1 for ordinary `dotnet`; the explicit
invocation below remains reproducible. No unrelated package-lock changes were needed for those shipping checks.

For each of `osx-arm64`, `osx-x64`, `linux-x64`, the successful full App restore used the publishing graph's properties:

```sh
dotnet msbuild src/Passenger.App/Passenger.App.csproj -t:Restore -p:RestoreLockedMode=true \
    -p:RuntimeIdentifier=osx-arm64 -p:SelfContained=true -p:PublishReadyToRun=true -nodeReuse:false -maxCpuCount:1
```

The inspector-only forced check covered all six RIDs using the same `RuntimeIdentifier` property, with runtime/host
pack downloads suppressed because the inspector is framework-dependent. Its offline-only run disabled NuGet audit;
the full App restore above used approved NuGet access without that suppression.

On this Windows host, this exact invocation selects RC1 directly (`--version` was checked to return
`11.0.100-rc.1.26425.128`). It also reproduced the historical checks when repository roll-forward selected RC2:

```powershell
$dotnetExe = 'C:\Program Files\dotnet\dotnet.exe'
$rc1Cli = 'C:\Program Files\dotnet\sdk\11.0.100-rc.1.26425.128\dotnet.dll'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnetExe exec $rc1Cli --version
& $dotnetExe exec $rc1Cli build tests/Passenger.Tests --disable-build-servers -p:UseSharedCompilation=false
& $dotnetExe exec $rc1Cli test tests/Passenger.Tests --no-build --filter 'FullyQualifiedName~DialogButtonsTests|FullyQualifiedName~Open_asks_This_Window_or_New_Window_and_Dont_ask_again_makes_the_answer_the_setting_every_window_follows'
& $dotnetExe exec $rc1Cli test tests/Passenger.Tests --no-build --filter 'FullyQualifiedName~TitleBarTests|FullyQualifiedName~CommandLineTests'
```

Set `AVALONIA_TELEMETRY_OPTOUT=1` as every `just` recipe does. A NuGet cache relocated under the repository caused
the repository's namespace style to apply to third-party `Krafs.Publicizer` generated source; using the normal cache
outside the repository resolved it without suppressing source checks. Sandbox registry/network/MSYS-object restrictions
needed scoped elevated execution. Existing debuggee unused-local warnings are fixtures, not new production warnings.
Do not force `NUGET_PACKAGES` when running package-feed fixtures: it overrides their `nuget.config` fixture-local
`globalPackagesFolder`. The shared-dialog sweep's initial forced-cache run failed that assertion after a successful
authenticated install; removing the override yielded the 48-pass/one-skip result above without a product change.

The initial combined run on source `155a6dd` ran 54 tests: 52 passed, one failed and one macOS-only test skipped on
Windows. That run caught the real #219 response/shutdown ordering race: disconnect succeeded, but a pending
configuration cancellation could be lost as `the connection closed`. The subsequent product fix `04b7d865` keeps the
configuration lock through both success/error response writes and full-path cleanup, with the existing success-only
continuation semantics preserved. Deterministic stalled-write tests fail under the old ordering and pass when fixed;
34 focused tests and 10/10 repetitions of the original cancellation case passed under direct RC1.

The 2026-10-15 integrated source `e7370c87ef622f99294b5ed0acfaf9903c92b84f` passed the direct-RC1 locked restore and full solution
build. The build had zero errors and seven expected CS0219 warnings in unchanged intentional debugger fixtures, with
no product/test-source warnings. The post-fix combined run completed in 63 seconds: **56 total, 55 passed, one
macOS-only test skipped on Windows, zero failures**.

The local integration evidence is under `artifacts/milestone-1/integration/`: `post-fix-restore.log`,
`post-fix-build.log`, `post-fix-tests.log` and `results/post-fix-integration.trx`.

Full three-system CI,
native platform acceptance, release dry run, final MCP pass, site refresh and the owner's release/tag decision remain
separate checkpoints.
