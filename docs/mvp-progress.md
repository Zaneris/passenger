# MVP milestone progress

Updated: 2026-10-16 (Indian/Mauritius). This records ongoing local implementation, verification and remaining work for the
[open MVP milestone](https://github.com/Zaneris/passenger/milestone/1). A passing focused test is not a claim that the
whole milestone, platform suite or release has passed. The 2026-10-15 evidence remains identified below; resumed work
adds new evidence without replacing it. Reviewed commits are being pushed to the fork's `origin/main` as requested.
No issue was closed, release published or tag pushed.

The owner's shutdown resume point, preserved unmerged drafts and next verification steps are recorded in
[the handoff](mvp-handoff.md). Its checkpoint does not mark the continuing work complete.

## Resumed CI investigation — 2026-10-16

The final shutdown source `74edf7d` ran all 1,034 expected cases exactly once per system in
[CI `38848023088`](https://github.com/tebeco/passenger/actions/runs/38848023088), finishing in 24m58s. Windows reports
1,023 passed, nine skipped and two failed; Linux 942 passed, 92 skipped and none failed; macOS 951 passed, 81 skipped
and two failed. All 14 shards remain within the 20-minute test budget and all packaging jobs pass. The failures are
the two-theme Windows populated Git History diff-content assertion and two-theme macOS Find Action pointer-row
lookup; these are separate from setup, coverage or timing failures. WPF hot reload passes all 56 Windows cases.

A subsequent fresh restore independently reports NU1902 for StreamJsonRpc 2.25.29's MessagePack 2.5.302 dependency.
The [reviewed advisory](https://github.com/advisories/GHSA-qhrr-8q5h-9q3h) entered GitHub's database on October 15 at
18:35:46 UTC; its [2.5.303 patch](https://github.com/MessagePack-CSharp/MessagePack-CSharp/releases/tag/v2.5.303) was
published September 17 at 19:19:27 UTC. The bounded package repair explicitly references centrally managed 2.5.303
in App/Lsp and updates their four affected lock graphs, preserving normal auditing and warning severity. This fixes
the fresh NuGet graph blocker; Roslyn 5.11.0's separately downloaded payload still carries its own older MessagePack
copy and needs separate upstream/provenance follow-up. It does not explain the earlier four assertion failures.
The isolated patch passes audited restore, forced locked restore, a fresh solution build (zero errors/seven unchanged
fixture warnings, 46.45s), all eight real C# language-server/Testing Platform/MCP integration controls (56.41s), and
the notices check for all 258 packages. Exact graph comparison finds only MessagePack/Annotations version changes
and their owning project-reference metadata. Evidence is retained in the `ci-messagepack-patch` worktree's
`artifacts/messagepack-patch/`; the new App DLL reports 2.5.303 while the independent Roslyn DLL remains 2.5.302.

The two original failure families have separate source-specific repairs and evidence:

- `655a092` adds the trailing `--` when reading a revision blob in `Git.TextAtAsync`. Git for Windows otherwise tries
  to stat the `hash:path` argument, whose combined length can exceed its working-path limit; the failed read had
  become an empty diff. This is a product repair, preserving long-path coverage and normal Git configuration.
  Both-theme real Git Log/Enter/diff controls fail with the old command; the frozen VCS/History batch passes 44/44
  with the fix. Isolated command probes also preserve an error for a genuinely missing blob. Source hashes, logs,
  results and four inspected default/narrow frames are under the shell worktree's `artifacts/ci-git-diff/`.
  Root merged and pushed this repair in `ced3c63`; its integration build has zero errors/seven unchanged fixture
  warnings in 27.85s, and both original History cases plus both new VCS cases pass (4/4, 14s).
- `0f341c7` fixes the Find Action test's query replacement. Avalonia Headless configures its own text-box hotkeys;
  choosing Meta+A from the host OS left no selection and appended `Go to File` to the old query, producing zero rows.
  The test now uses the actual configured Select All gesture and asserts both the selection and replacement text.
  Both-theme wrong-gesture controls fail with that exact concatenated query; the configured-Meta positive and final
  restored controls each pass 2/2. The broader local Action/Navigation/Shortcut/Editor Window batch passes 20/20.
  Root merged and separately pushed the repair in `2ea6f8c`; its fresh integration build has zero errors/seven
  existing fixture warnings in 29.17s and the same 20-case integration batch passes in 36s, with no skips.

Original native metadata, four failed job logs, all 14 result archives and exact coverage analysis remain under
`artifacts/resume-ci-2026-10-16/evidence/`. Action probes and actual captures are under the shipping worktree's
`artifacts/ci-action-query/`. No timeout, coverage or layout assertion was weakened to explain the original run.

The repaired source `2ea6f8c` completes [CI `38940010842`](https://github.com/tebeco/passenger/actions/runs/38940010842)
in **27m12s**. Actual discovery, all 14 filtered discoveries and TRXs agree on **1,036 unique cases exactly once per
system**. Windows reports **1,027 passed/nine skipped/zero failed**; Linux **944 passed/92 skipped/zero failed**;
macOS **954 passed/81 skipped/one failed**. Both original History and Action theme cases and both new VCS cases
pass natively; all packaging jobs pass. Three Mac shards still fail the unchanged 20-minute test budget:
**21.0188, 20.8369 and 20.7928 minutes** (shards 1, 3 and 4). Mac shard 3 also fails the existing MCP input-latency
assertion at `McpTests.cs:970`: a key waits **547ms** against the **250ms** bound while all 10,002 usages are found.
The cause of that delay is not established. The Mac jobs' initial **2m12s–2m27s** runner queue is counted separately;
it does not explain their measured TRX budget overruns. Neither the below-25-minute CI goal nor complete green
native acceptance is met. Full results, failed logs, exact per-case coverage and class timings are retained under
`artifacts/resume-ci-2026-10-16/evidence/final-38940010842/`, with `analysis.json` the machine-readable inventory.

## Issue status

| Issue | Local result | What remains |
| --- | --- | --- |
| [#219 Slow Windows debugger startup](https://github.com/Zaneris/passenger/issues/219) | `93737dc` fixes the reproduced false five-second timeout for a live runtime and startup cancellation. Integration then exposed a cancellation-response/shutdown ordering race, fixed in `04b7d865`: the configuration lock covers writing success/error responses before disconnect can close the transport, preserving success-only continuation behavior. Both deterministic old-order controls fail; the fixed RC1 run passed 34 focused tests plus 10/10 repetitions of the original cancellation case. | The original CI incident's late-versus-lost callback cause still needs a trace under the failing runner conditions. Final combined validation is recorded below. |
| [#218 WPF generated-file contention](https://github.com/Zaneris/passenger/issues/218) | Reproduced the exact MC1000 overlap and identified `Passenger.Host` as the generated-file holder. Selected fix `2991601` persists separate design-time intermediate directories per project/SDK/evaluated properties and owns them across processes, preserving normal Build/Run/watch outputs. Final fixtures passed 9/9; broader checks passed 21 with one expected platform skip. Reverting the four product files makes the final fixture fail MC1000 in 7.5 seconds. Follow-up `3c4a034` repairs Roslyn's reported assembly identity and compiler `/out:` baseline without moving actual generated sources out of isolation; all nine isolation cases and both mixed managed/XAML cases pass. | Native platform/full-suite acceptance remains; CI3 Unix EnC failures are followed by baseline-path repair `cdb4592` and native Linux checks below, with a new complete CI run required. Target-time or local `IntermediateOutputPath` overrides are explicitly unsupported with actionable reasons and recovery. Cache pruning is not implemented. The earlier graph-coordinator experiment was superseded, not shipped. |
| [#217 MAUI template inventory](https://github.com/Zaneris/passenger/issues/217) | `d94d4af` passed the original three family tests with MAUI absent and a temporary MAUI-tagged template positive-branch check. Native Windows CI then exposed truncated SDK tag-column parsing in the test expectation; `8116317` reads complete tag columns. The revised four-case class passed locally, and Windows shard 2 in native CI3 at cc7456a passed the corrected inventory assertion. | Native Windows runner acceptance is now observed for that checkpoint; the complete cross-platform CI result remains separate. |
| [#216 Dialog answer order](https://github.com/Zaneris/passenger/issues/216) | Shared footer ordering puts competing answers before Cancel on Windows/Linux and the default last on macOS. Thirteen focused Windows tests and reviewed dark/light scale-2 captures are followed by native CI3 at `cc7456a`: both `DialogButtonsTests` theme cases and the real Open Solution/Don't ask again boundary pass on all three systems. | The focused implementation now has native Windows/macOS/Linux test evidence. Remaining whole-UI states and native Rider comparison belong to #213. |
| [#215 Cross-platform locked restore](https://github.com/Zaneris/passenger/issues/215) | `7ca9d31` fixes the WPF inspector's shared six-RID list/lock entries; six forced locked restores and full self-contained ReadyToRun App restore graphs passed under RC1. Native CI exposed a separate Roslyn extension host/target cross-publish failure; `bbd39c1`, merged in `04e303f`, fixes the portable extension identity. The subsequent native CI packaging jobs all passed, including cold-cache macOS x64. | Final release dry run and native install/use acceptance remain separate from successful restore/publish/package construction. |
| [#214 Await-step/return-watch race](https://github.com/Zaneris/passenger/issues/214) | Diagnostics added in `3a1ef55`; no race fix claimed. The original await scenario passed 100/100 launches; a return-value boundary probe also passed. Final step/async-step/synthetic-value regressions passed 14/14. | A complete failing engine trace or deterministic other-frame return-watch/completion interleaving is still missing. The 100 clean runs did not exercise that interleaving. |
| [#213 Whole-UI check](https://github.com/Zaneris/passenger/issues/213) | Ongoing. Responsive docking, menus, Settings, shared scrollbars/buttons/focus, Terminal process/tabs, tool headers, populated Profiler Memory/Frames, NuGet Sources and Problems strips have scoped evidence below. `b3b3458` and `82c4ba7` repair the reproduced long editor-tab and status-bar action clipping with actual pointer/keyboard checks at 640/683. The recurring native Profiler header failure is traced to mixed test coordinates during Fluent's press transition; `2d0e231` repairs the test with real held-pointer and genuine-clipping controls. Complete native `aab25be` now passes all six header cases; no header product fix is claimed. `88c97a1` repairs the same mixed-coordinate error in Terminal tab tests, with a real held press and genuine-clipping negative. See the [surface inventory](ui-audit-213.md). | Continue unexamined dialogs/menus/popups/populated tools, remaining Commit/diff states, popup scale/shadows and radio/menu/focus states. `9c805c4`/`d229d3a` add scoped populated Git/Local History checks below. Captured counts are not whole-UI visual passes. Native Rider behavior was not observed; bundled defaults provide the actual local reference. Unfinished implementation is not an external blocker. |
| [#211 Final MCP pass](https://github.com/Zaneris/passenger/issues/211) | Audited the existing 65 tools and tests; concrete gaps listed below. | Remaining implementation and per-feature SDK-client tests. The owner requires this final pass after other MVP work; no completed final pass is claimed. |
| [#208 Editor/server line-break drift](https://github.com/Zaneris/passenger/issues/208) | Existing `LspDocument.Unsplit` fix confirmed; both exact line-break regressions passed locally and in the complete native Windows/Linux/macOS CI3 run at `cc7456a`. | No further source change is indicated by these results. Overall CI failures are separate from this issue's passing regressions. |
| [#206 CI shards](https://github.com/Zaneris/passenger/issues/206) | Six Windows/four Linux/four macOS shards retain the 20-minute test budget. Four macOS tests plus one sequential image job fit five runner slots. Healthy class weights now come from complete `aab25be` CI, retaining older healthy values for failed classes. All 14 actual filters cover frozen `6187168`'s 1031 tests exactly once; repeated deals, actionlint and four CLI regressions pass. | Observe successful CI below 25 minutes and every shard below 20 minutes. The 1015-case `aab25be` run takes 27m43s, with test failures and two macOS budget failures. Redealt estimates are about 14.63/14.49/18.67 minutes for Windows/Linux/macOS, excluding overhead and 16 newer cases; class totals are not scaled. |
| [#57 Linux packaging/install](https://github.com/Zaneris/passenger/issues/57) | Native CI packaging passed at `04e303f`; x64/ARM64 archives passed architecture and actual install/update/uninstall/settings-retention checks. Native RC1 Ubuntu x64 publish `cc7456a` passed build, Roslyn, run, breakpoint/evaluate, CPU profile and PTY via MCP. Completed native Linux ARM run `38827279628` at `47f7a57` passes the full 948-case suite (908 passed/40 skipped), package/install/installed frame/MCP/uninstall; the host and debuggee are both ARM64. | Later-source native acceptance and an interactive Ubuntu desktop pass remain. The release dry run remains sequenced after the feature/MCP/site work. |
| [#56 Release workflow](https://github.com/Zaneris/passenger/issues/56) | Existing workflow covers tests, six RIDs, fixed asset names, version/feed checks, signing hooks, checksums, tag notes and a non-publishing dry run. Packaging/release shell syntax checks passed. | The owner's required order is other MVP work and green three-system CI, then #211, then #55, then the final dry run, then an owner-chosen tag. The final dry run and tag were not performed out of sequence. Native signing/notarization was not exercised. |
| [#55 Website/README refresh](https://github.com/Zaneris/passenger/issues/55) | Deferred as the owner explicitly requires this last, after the feature/MCP passes. Existing site, README and comments were inspected. | Commit a repeatable sample and screenshot command; retake all required views in both themes; revise the whole site/README/share card against the finished feature set. The `www` redirect needs the owner's Cloudflare dashboard because the deploy token cannot write redirect rules. |
| [#33 Hot Reload](https://github.com/Zaneris/passenger/issues/33) | Managed/early literal, run/watch, future-view Avalonia and Host-baseline checkpoints remain attributed below. Complete native `c9509e4` has no failed tests on any system. Later WPF `2ba93fb`/`4ded215`/`c33673c` adds future-view updates, cache/import repair and late-Run cancellation using wire version 9. `8800b89` additionally supports two exact optimized helper IL shapes; its eight-case Windows run passes. | New WPF/UI coverage is not part of the older 951-case native run. Current whole-system acceptance and broader structural/binding/markup/resource/style/template support remain. Unsupported graphs and other IL shapes require a truthful restart; the optimized checkpoint is not blanket Release support. Earlier native literals do not establish later full XAML feature support. |
| [#19 Data breakpoints](https://github.com/Zaneris/passenger/issues/19) | Existing Windows x64 implementation and Variables/Breakpoints UI verified by all four data-breakpoint tests, none skipped. | Current engine/runtime availability is explicitly Windows x64. macOS/ARM support was neither implemented nor verified in this pass. |
| [#1 Windows verification](https://github.com/Zaneris/passenger/issues/1) | CET/data-breakpoint/Hot Reload focused checks passed as recorded below. Complete native Windows x64 CI at `c9509e4` reports 942 passed/nine skipped, exactly once. Completed Windows ARM run `38827279628` at older `47f7a57` reports 932 passed/12 skipped/four failed; package/install/installed ARM64 debugging/draw/uninstall pass independently. | Repair and rerun the four ARM failures listed below. Newer WPF/UI source still needs its full native suite; native interactive build/run/debug/profile acceptance remains distinct from headless and installed-copy checks. |

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
| `fc974f5`, Unix terminal missing executable | Native WSL reproduced the CI missing-shell failure: Porta reported PID 775/running, then child exit 2 and a blank closed tab. Missing explicit Unix paths are now rejected before PTY allocation, with relative paths resolved against the actual working folder. Both failures retain their diagnostic tab without PID/connection; a real relative script in a spaced folder takes keyboard input and exits 2 normally. Windows final run reports 23 passes in 23s (13 older Unix-only bodies return early); clean exact committed Linux x64 source passes all 23 actual terminal lifecycle/input/tab cases in 46s, zero skips/failures. Six Windows/Linux scale-2 missing-path frames inspected in both themes. This is not an exec-handshake, PATH lookup, interpreter/permission or time-of-check race fix. Later native macOS/Linux x64 and Linux ARM results below clear this missing-path case. |
| `4fff99c`, populated profiler rows | Actual CPU completion explains that no heap snapshot was taken; long Call Tree/Hot Spots/allocation rows and a real held-40-object heap snapshot retain accessible names, aligned numbers, virtualization and selected rows through resize. User scrolling away preserves reading offset. Profiler/Memory Snapshots/Tool Header pass 14/14 in 1m24s, zero skips; build 0 warnings/errors. Normal 1200/narrow 640 scale-2 frames inspected in both themes, with 683 also captured. Evidence is shell `tests/Passenger.Tests/TestResults/profiler-and-memory-final.trx`, `artifacts/arm-validation/{profiler-final-build,profiler-and-memory-final}.log` and `artifacts/profiler-populated/captures-final/`. Busy, diff and frame states remain separate. |
| `152062a`, native Linux profiler follow-up | Clean exact committed source rebuilt with native Ubuntu x64 RC1. Profiler seven, Memory Snapshots three and Tool Header six cases pass 16/16, zero skips, in 2m55s; selected-row visibility and scroll-away resize assertions execute there. Evidence is shell `artifacts/profiler-populated/native-profiler.log`, `native-linux/profiler-memory-linux.trx` and `native-linux/captures/`. This does not establish remaining busy/diff/frame states. |
| `1339d80`, long selected feed and Problems summary | Real local-feed selection and missing-baseline debugging check ellipsis, full-value tooltips, actual text layout and clipping ancestors at 1200/683/640, with a 200px NuGet side pane. Four both-theme cases pass in 24s; existing Problems/Package Sources pass seven with one expected Windows credential-provider skip in 22s. Build 0 errors/seven known fixture warnings. Eight final normal/narrow scale-2 frames inspected in `long-labels/`; ordinary Problems message/detail clipping is repaired separately by the later `6b91997` checkpoint below. |
| `6d1752c`, Terminal session overflow/focus | A real long-named terminal run and seven actual shells keep tabs/close actions reachable at 1200/683/640 and in a 200px side pane. Names ellipsize beside close actions with full-name tooltips; wheel/pointer/Tab/Space selection routes commands to the selected shell and closing the background run ends its actual process. Resize reveals the selected session, or an inactive tab while it has keyboard focus, without changing selection. Prior selected-only reveal fails the focused-tab viewport assertion in both themes. Final product passes eight Tool Tab/Lifecycle/shared-header cases in 1m16s, zero skips; the final test-only real-Tab-key focus refinement passes both affected cases in 24s. Builds 0 warnings/errors. Eight geometry frames, four focused-resize frames and the two final visible-keyboard-ring frames inspected separately. Native Unix execution of this overflow addition remains pending. |
| `6d93582`, focused inactive close action | Actual Tab reaches an inactive close child whose old style has opacity zero. The shared focus rule paints its glyph and a rounded non-hit-testing accent ring. Shift-Tab/Tab/Enter closes only that background shell, checking saved PID and retained run selection. Tool Tab/Editor Tabs pass 10/10 in 36s, zero skips; build 0 warnings/errors. All four final 640/1200 two-theme `close-action-final/` frames inspected; root's merged final two Terminal cases also pass in 23s. |
| `ab704c8`, shared keyboard focus | Default outlines use the shared rounded accent treatment; checkbox focus surrounds the actual glyph, following bundled Rider icons. Real Tab/Space/pointer input, checked/unchecked states, disabled focus removal, actual unclipped left-outline paint and a primary button's visible focus are checked. A real Settings label wraps at default font size at 640px and retains glyph alignment/focus through 640/980/683/640 resize. Dialog Buttons/Settings/Tool Tab/Tool Header pass 21/21 in 1m39s, zero skips; build 0 warnings/errors. All fourteen final `checkbox-focus-verified/` scale-2 frames inspected. Existing explicit tab overrides remain. That run predates only `4ec6143`'s separate Profiler test diagnostic refinement; native header acceptance is qualified below. |
| `6b91997`, populated Problems rows | Actual missing-baseline and local-feed vulnerability flows constrain message, link and location labels to the row, retain full tooltips/clipboard values and click the advisory at 640px. Old source reproduces a location beyond the list even at 1200px. Hot Reload/Project Load/Problems pass 8/8 in 40s, zero skips; build 0 warnings/errors. Twelve final `problem-rows-final/` scale-2 frames; all eight normal/narrow whole frames inspected in both themes. The later category-strip checkpoint is attributed separately below. |
| `892e0ef`, Problems category overflow | A real long-named erroneous file, solution analysis and failed compiler build exercise five categories with actual wheel/pointer/Tab/Space input, side200 and bottom640/683/1200. Full-name tooltips and ellipsis retain access; focused inactive categories stay visible through resize without switching the shown page. The actual missing-baseline Hot Reload last category also fits side200. Final Problems/Hot Reload/Project Load pass 10/10 in one minute, zero skips; build 0 warnings/errors. Old source fails both long-file geometry cases in eight seconds and both real Hot Reload side-category checks. Eighty scale-2 frames in `problem-tabs-frozen/`; ten representative frames inspected, including both-theme normal/narrow/focus/Hot Reload states. Long editor-tab/status overflow discovered there remains a separate source checkpoint. |
| `b3b3458`, long editor tabs | Finite filename ellipsis leaves modified/pin/close actions inside each real editor group, including split groups. Pointer/wheel/Tab/Shift-Tab reach inactive close actions without changing selection; focus, editor text and caret survive 1200/900/683/640 resize. Existing Editor Tabs/Active Editor run passes 17/17 in 16s; final test-only keyboard refinement passes both affected cases in 5s, zero skips. Builds 0 warnings/errors. Old source fails both geometry cases even at 1200px. Eight of 26 final scale-2 `editor-long-tab-frozen/` frames inspected in both themes, including single/split groups and inactive focus. |
| `82c4ba7`, bounded status paths | Scrollable breadcrumbs with full-path tooltips reserve readable message and storage-action space; focused crumbs reveal without changing editor selection. A plain root file remains readable on first open/reopen with no outline. Actual wheel/pointer/Tab input reaches folder/file crumbs; the real 640px line-separator menu converts saved LF to CRLF. Final File Properties/outline/Problems/long-editor checks pass 16/16 in 55s, zero skips; build 0 warnings/errors. Old source fails both actual separator clicks at 640px because the path intercepts them. Nine final full frames inspected in both themes, including actual compiler-error status. Evidence: shipping `artifacts/2026-10-16/{build,tests}-213-status-path-frozen.log` and `status-path-frozen/` captures. Optional status-widget combinations remain separate inventory work. |
| `412d956`, Profiler Memory navigation | Actual 40→47 held objects exercise comparison/object/root-path navigation, keyboard action access, breadcrumb wheel/back navigation and finite viewports. Windows Profiler/Memory/Tool Header pass 16/16 in 2m12s; native Ubuntu heap cases pass 2/2 in 27s, zero skips. Shell `artifacts/profiler-memory-navigation/{tests-final.log,native-navigation.log,final/,native/captures/}` retains both-theme normal/narrow frames inspected by the shell agent and old two-case negative controls. |
| `7e1fb80`, populated Profiler Frames | A real long-named slow control exercises aligned metrics, horizontal/vertical wheel input, focus/selection and source jump/reopen. Windows Frames/Profiler/Memory pass 14/14 in 1m47s; native cases pass 2/2 in 39s, zero skips. Both-theme captures and old two-case negative controls are retained under shell `artifacts/profiler-frames/{test-final.log,native-frames.log,final/,native/captures/}`. |
| `10d729e`, large heap metadata | Full timestamp/count/size wrap for actual 200,000 new/one-million surviving-object comparison and object rows at 1200/683/640. Windows Profiler/Memory pass 11/11 in 1m17s; native million-object cases pass 2/2 in 23s, zero skips. Durable geometry does not assume a busy phase lasts through capture; native Light comparison completed during that optional observation. Shell `artifacts/profiler-million/{tests-final.log,native-million.log,final/,native/captures/}` includes inspected both-theme frames and old two-case negatives. |
| `fe54628`, no-frame guidance | Existing two-theme completed CPU-profile cases pass 2/2 in 18s after the old margin clips guidance at 640px in both cases. Shell inspected the padded 1200/683/640 frames under `artifacts/profiler-empty-frames/{tests-before.log,tests-after.log,before/,after/}`. No new native repeat is claimed for this one-line change. |
| `af1720b`, populated NuGet Sources | Names, full configuration paths, source properties and summaries wrap inside the real Sources form while Edit/Delete and enable checks stay reachable. Actual file/config keyboard selection, two-axis wheel input and Edit/cancel run at 1200/683/640 and side200 in both themes. Windows class passes six with one existing credential-provider skip in 17s; native class passes 7/7 in 81s, then exact final two side-pane cases pass in 11s. Builds 0 warnings/errors; old two-case truncation control fails. Shell `artifacts/nuget-sources/{tests-final.log,build-final.log,native-class.log,native-side-final.log,final/,native/captures/}` retains inspected frames and TRX files. |
| `3fee501` / `d702d41`, NuGet headings and package body | The Feed dialog's long heading wraps while real Home/End/edit/cancel input retains its full value. Populated package metadata/project names wrap, identifiers/details remain horizontally readable, and install/update/remove actions stay reachable at 1200/683/640 and side200 in both themes. Explicit version selection survives same-package/feed refresh only while still offered; package/feed/removal reset it, and automatic defaults follow an actual 1.1→1.2→1.1 feed change. Windows Packages/Project Load/Package Sources reports 17 passed/one existing credential-provider skip in 54s; native Linux Packages/Project Load passes 11/11 in 52s. Shell inspected corresponding normal/narrow Windows/Linux frames under `artifacts/nuget-packages/{final/,native/captures/}`. `tests-final.log`, `native-final.log` and `native/packages-linux.trx` retain results. Native ancillary project diagnostics twice report a missing WindowsDesktop reference pack in the fixture feed; passing package workflows are not a clean code-help claim. |
| `faec7f1` / `569c663`, native isolation fixture identity | The custom-output fixture uses the actual runtime platform instead of win-x64 and restores through one canonical project root instead of `/var` and `/private/var` aliases writing the same cache. Each local Windows checkpoint passes four with one expected Unix-symlink skip (21s/28s). Focused `7241544` Windows ARM run `38839282387` reports four passed/one intentional Unix-symlink skip in 62.25s with native ARM64 metadata; macOS `38839286211` passes 5/5 in 72.05s. Root independently verified exact source, five selected/results and unique names in `artifacts/milestone-1/integration/focused-<run>/{Windows ARM64,macOS}/`. The repairs have target-system evidence; the older ARM moved-intermediate RPC loss did not reproduce and remains unexplained, with no speculative product fix claimed. |
| `6f480d2`, detached macOS device reuse | Owned-image cleanup succeeds once the image is absent, even if its old device name has been reused; it never detaches the unrelated replacement. Two new old-source controls fail; final CLI checks pass 12 with one native-macOS-only skip in 27.6s. Replaying the captured `38835542810` ARM plist changes old exit 1 to fixed exit 0 with only information queries and zero detaches to the simulator's reused disk 7. Shell `artifacts/dmg-mount/{device-reuse-before.log,device-reuse-final.log,captured-device-reuse.log}` retains evidence. Later native `7241544` CI passes the actual mount check and both ARM64/x64 images; all six retained plists were parsed as described below. |
| `a6dcaef`, Self Debugging restore/deadline | The repository's App graph restores under the established test-owned NuGet home before models open; imported props/targets/assets must remain byte-identical through the actual IDE Build/Debug. Local pumping honors the already-existing five-minute launch deadline instead of entering `Ui.Settle`'s unrelated 90-second wait. Final Windows case passes in 47s; earlier related language-server/package-reload plus Self checks pass 8/8 in 1m25s. Native Linux on mounted NTFS passes the complete stop/step/evaluate/child-cleanup flow in 4m33s, with a 242s launch and design-time builds unchanged at 32. The same prepared native test with the old nested Await fails at 90s while its real build remains live. No timeout/budget is raised, and this slow filesystem probe is not a CI timing measurement. Shipping `artifacts/2026-10-16/self-debugging/{final-manifest.json,windows-final-results/,native-results/,native-deadline-results/}` retains exact evidence. Complete native `aab25be` subsequently passes the actual macOS case in 101s. |
| `9c805c4`, populated Git body | A real 37-commit repository exercises long branches, paths and messages at 1200/683/640 and side200 in both themes, retaining commit/filter selection and virtualized scrolling. Actual branch Enter, keyboard Refresh and focused file Enter open the corresponding Git diff; a routed-key repair addresses the ListBox consuming the earlier handler. Windows History/Vcs/Branches pass 47/47 in 92s and final routing checks pass 2/2 in 15.8s; native Linux final routing passes 2/2 in 10s. Root integrated checks pass 2/2 in 13s. Shell `artifacts/git-body/{tests-final.log,tests-routed.log,native-routed.log,final/,native/captures/}` retains before failures and final captures. DiffView, Commit and unexamined Git states continue separately. |
| `d229d3a`, populated Local History | Long labels/file rows and the Revert toolbar gain horizontal access in a finite, vertically virtualized body, retaining selected change sets and reading position. Actual file Revert, Undo and Save and folder Revert Selection affect the selected B file while A stays unchanged at 1200/683/640 and side200 in both themes. Exact fresh Windows build has zero errors/seven unchanged fixture warnings; all ten Local History cases pass in 43s. Native Linux final two cases pass in 30s with both exact source hashes recorded. Shell `artifacts/local-history-body/{build-final.log,tests-final.log,native.log,final/,native/captures/,native/local-history-body-linux.trx}` retains evidence. DiffView long filename/version reading and Commit remain separate work. |
| `dac05a0`, added literal XAML property elements | Existing live and future Avalonia/WPF controls accept narrowly supported added literal property elements without shifting their compiled anchors; unsupported data-context, graph, binding/markup and identity changes still request restart. The full Windows controls pass 83/83 (44 WPF, 39 Avalonia) in 11m38s, and old-source controls produce eleven expected failures. Root's combined XAML/Local History checks pass 13/13 in 1m59s after a zero-error build with seven unchanged fixture warnings. These are bounded literal additions, not whole structural/Release/native-platform acceptance. |
| `46fcdc1`, bounded navigation popups | The shared host reserves room for the existing shadow and refits centered/anchored popups on resize, restoring requested width on widening without recreating input. Go to File separates ellipsized filename/folder columns with a real full-path tooltip; an existing query-only key handler moves to the popup tunnel so Enter opens the row selected by pointer. Old-host-only controls fail both themes at 683px; the prior pointer/Enter control likewise fails both themes. Final Windows Navigation, Editor Window and actual conditional/trace breakpoint workflows pass 14/14 in 27s, zero skips, with a zero-warning/error build. Actual 1200/683/640 main/detached windows preserve query selection/focus and anchored condition text; closing a detached owner with an open popup then reopening on main is checked. Thirty-two scale-2 frames are captured, seven final representative frames inspected. Shipping `artifacts/2026-10-16/navigation-popup-{draft-results/,old-host-results/,final-results/}` and `artifacts/ui-audit-213/2026-10-16/navigation-popup-frozen/` retain evidence. Native Linux passes the exact two theme workflows in 12s after a zero-warning/error 20.61s build: clean `d9c7ce5` plus the four committed file hashes, retained in `navigation-popup-native.log` and `navigation-popup-native/{navigation-popup-linux.trx,captures/}`. Two native narrow file/detached frames were inspected. macOS, short-height/other popup bodies and further Action Popup rows/input remain separate checks. |
| `2d0e231`, transformed header test geometry | Fluent transitions a pressed ToggleButton to a smaller size around its center over 75ms. The test combined a transformed origin with its original width: the native failure's actual right edge is 549.3417px inside the unchanged 549.5px limit, while the mixed calculation reports 549.6583px. Whole-rectangle transformation repairs the test, including overlap checks, without changing product styles/reveal or tolerance. Real wheel input first exposes a genuinely clipped tab and requires rejection; then a real held Snapshots press at 683 requires pre-capture containment/selection/focus plus rendered label ink. Windows class 6/6 passes in 1m14s; restoring only the old helper fails both themes in 19s (mixed 549.85px versus painted 548.15px). Restored final cases pass 2/2 in 27s, with both final scale-2 held frames inspected. Shipping `artifacts/2026-10-16/profiler-transform-{evidence.json,results/,negative-results/,final-results/}` retains controls and exact installed-theme source attribution. Complete native `aab25be` subsequently passes all six Tool Header cases, including both held-press controls. |
| `6f07925` / `bb4bd1b`, Find Action rows and native hover | Finite title/group/shortcut columns ellipsize within the actual popup; full tooltips retain all details and unavailable reasons. Pointer-selected Enter executes in its owner. Real 1200/683/640/320 width changes preserve query/selection/focus. Windows whole Actions passes 4/4; final Action/Terminal checks pass 4/4 in 30s. Native Linux initially proves the unpainted row Grid is not the pointer hit target; a transparent row background makes the whole row hoverable without extending the tooltip delay. Exact native theme cases pass 2/2 in 10s with a zero-warning/error build; both final full-tooltip/disabled-reason frames were inspected. Shipping `artifacts/2026-10-16/action-popup-native-hit-area{.log,/action-popup-linux.trx,/captures/}` retains the clean-base/source-hash evidence; earlier failed native hover logs remain separate. Root whole Actions passes 4/4 in 33s. |
| `88c97a1`, Terminal transformed test bounds | The same Fluent press transition exposed a test-only origin/width mismatch in native Terminal overflow coverage. Shared `UiAudit.BoundsIn` transforms the complete rectangle with the existing tolerance. A real held long-run tab, wheel-exposed genuine clipping, shell input/focus/close and child cleanup remain checked before capture. Full Terminal/Profiler controls pass 6/6 in 1m1s; restoring only the old helper fails both themes in 13s; final Action/Terminal checks pass 4/4 in 30s. Both held 683px scale-2 frames were inspected. Shipping `artifacts/2026-10-16/{terminal-transform-results/,terminal-transform-negative-results/,terminal-action-final-results/}` retains evidence. No product reveal/style change or newer native acceptance is claimed. |
| `d3b675e` / `236fbf1`, DiffView names and captions | Actual long filename/version captions remain horizontally readable with Settings pinned right, real Lines→Words input at side200 and both Revert flows. Review found a genuine caption/thumb ink overlap; final local 12px rail spacing fixes it without a shared-style change. Earlier Windows Local History/VCS/Diff Words controls pass 48/48 before that spacing refinement; exact final theme cases pass 2/2 in 39s on Windows and 2/2 in 38s on native Linux. Shell `artifacts/diff-body/` retains final logs, source hashes and both-theme default/narrow/side captures. Root integrates the exact final two cases; remaining diff/Commit states continue. |
| `89f9ba5`, WPF zero-current-instance registration | Wire version 10 reports future view registrations as view definitions, not an instance count. Registration verifies unique loaded x:Class, assembly MVID/associated portable PDB and original source checksum, strict generated population hooks and built-in dependency-property resolution without constructing a view. Actual first construction in Debug/watch, repeated registration, invalid/atomic refusal, state/bindings/constructor behavior and old-wire controls pass 13/13; full WPF/MCP/non-picker checks pass 66/66, with six expected old-runtime negative failures. Unsupported custom/ambiguous/unverifiable cases request restart. This 12-case growth is outside the 1015-case native run; its two earlier native WPF input failures are still under investigation. Debugger `artifacts/wpf-uncreated-ui/` retains captures. |
| `9391530`, bounded Commit body | Long messages retain a finite file list and reachable wrapping footer actions. Actual keyboard checkbox/message/Refresh, resize/visible-file anchor, wheel, Diff and single-file Commit controls pass 2/2 in 7s, then the final Windows VCS class passes 36/36 in 1m16s. Native Linux theme cases pass 2/2 in 6s with a zero-warning/error build; real default/narrow/side200 frames were inspected. Shell `artifacts/commit-body/` retains final logs, TRX and both-theme captures. Parent reviewed and merged the checkpoint; nonrepository Git HEAD/Local unusable-action state and other UI states remain. |
| Avalonia XAML `77f0a72` + `e1d3855` | `XamlHotReloadTests` passed 11/11 on exact RC1; debugger worktree `tests/Passenger.Tests/TestResults/xaml-reload-getters.trx`. |
| WPF XAML `ffe2706` | `WpfXamlHotReloadTests` passed 4/4 on exact RC1; debugger worktree `tests/Passenger.Tests/TestResults/wpf-xaml-first.trx`. Both-framework scale-2 before/after captures are under `artifacts/bin/Passenger.Tests/debug/{xaml-hot-reload,wpf-xaml-hot-reload}/`. |
| WPF design-time isolation `2991601` | Final nine-case fixture passed; broader 21 passed with one expected platform skip. `artifacts/wt-shell-isolated/tests/Passenger.Tests/TestResults/{isolation-final-fixtures,isolation-checkpoint}.trx`. Imported/global paths, selected references, generated navigation, cache reopen and Build/Run/watch overlap are checked. Shell worktree `artifacts/isolation-probe/tests/Passenger.Tests/TestResults/final-fixture-negative.trx` retains MC1000 with the four product files reverted; holder PID 12024 is `Passenger.Host`. |
| Host baseline identity `3c4a034` / XAML `cc7456a`, `b6aad68` | Host's design-time paths stay isolated while reported assembly metadata and `/out:` point Roslyn at the real baseline. Nine isolation plus two previously failing mixed managed/XAML cases pass. Saved-editor run/watch tracking and accepted Avalonia edits in newly created views are merged. The expanded XAML/WPF run passes 32 actual cases; inspector protocol v7 reports runtime application failures. These source checkpoints supersede the earlier Host-identity investigation without implying all XAML editing is supported. |
| Later XAML `feb9254` (2026-10-16) | Literal attribute additions with binding-safe rollback and inspector wire version 8 are merged. The expanded actual Windows XAML/WPF cases pass 41/41, with an observable-guard negative control. This is a later checkpoint than the 32-case row above; its historical native Linux generic EnC failures at `cc7456a` are addressed by the subsequent baseline repair below. |
| Unix EnC baseline `cdb4592` (2026-10-16) | MSBuild full assembly paths replace Windows-only path interpretation for the reported Roslyn baseline. Native Ubuntu exact-source `fullpath-native26.trx` passes 24, with two mixed cases failing before their bodies because of a harness path; corrected `fullpath-native-mixed2.trx` passes both. The actual Linux MCP probe applies a C# edit in 323ms and observes result 3; the old `cc7456a` control reports no managed changes and result 2. Evidence: debugger `artifacts/linux-enc/` TRX files and shell `artifacts/cross-publish-cold/linux-enc-fixed-probe{.log,/summary.json}`. Later complete `c9509e4` has no failed tests on any system; newer WPF additions remain separate coverage. |

Later WPF future-view source `2ba93fb`/`4ded215`/`c33673c` is merged. `2ba93fb` passes whole-WPF 28/28 in 3m55s;
its broader 99-case run has 97 passes, one generated-usages failure and one intentional Windows symlink skip,
so is not green. `4ded215` repairs that assertion and two proven cache edges, passing 15/15 in 2m9s. `c33673c`
repairs four proven late launches after Stop/Close plus a handler-loss negative: 42/42 pass in 5m15s before final
generation-reset cleanup, then final nine cases pass in 1m33s (five cancellation and four managed/future cases).
Debugger `artifacts/test-results/wpf-{future-final-class,future-final-controls,future-repair,run-lifecycle-controls,run-lifecycle-final}.trx`
and matching logs retain attribution. Fixture-selected SDK 11 RC2 executes both net10/net11 projects; repo app/test
builds remain pinned RC1. Wire 9 rejects unsupported graphs/IL shapes with restart reasons. Later `8800b89`
recognizes two exact optimized helper shapes (null-conditional callback and local-callback null branch) while
retaining closed-helper validation. Eight actual Windows cases pass in 1m17s, zero skips, in debugger
`artifacts/test-results/wpf-optimized-verified.trx`; `wpf-optimized-final-manifest.json` records source hashes.
This is narrow optimized support, not acceptance of every Release IL shape. Later merged acceptance is separate
from these source-attributed focused runs.

`ede99e4` adds existing single-line literal property-element bodies in Avalonia/WPF, using the containing object's
original compiled source anchor. It retains conversion/value-source checks, batch preflight/rollback and cumulative
future-view initialization. Nested objects, structural additions/removals, directives, markup/bindings and
whitespace-sensitive bodies remain truthful restart cases. Four actual Debug/watched-Run editor boundaries pass;
the full two XAML classes pass **72/72 in 9m33s**, zero skips, in debugger
`artifacts/test-results/xaml-property-elements-controls.trx`. The old planner directly refuses three final controls;
the fourth negative watched case waits on its unanswered restart prompt and is retained as harness evidence only.
Root's merged four cases pass in 50s. The debugger inspected all four scale-2 property-element frames; source hashes
and details are in `artifacts/xaml-property-elements-{final-manifest.json,evidence.md}`. No general managed EnC
source changed, and this does not establish full XAML/Release/native-system acceptance.

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

The later [complete native run at `47f7a57`](https://github.com/tebeco/passenger/actions/runs/38826362406) completes in
23m56s with **948 results per OS**, no missing/duplicate tests and no runtime-budget failure. Windows reports
939 passed/nine skipped, Linux 908 passed/40 skipped, and macOS 914 passed/four failed/30 skipped. Only the two
Profiler fixture cases and two Profiler header cases fail; Windows/Linux and their coverage gates pass. This
clears the previous Unix missing-shell diagnostic and canonical partial-EnC fixture failures at this checkpoint,
but does not establish successful three-OS CI acceptance. Root retained all result artifacts under
`artifacts/milestone-1/integration/ci-38826362406-results/`. Canonical fixture repair `ea9ef61` and header diagnostics
`77d9f2d` are merged into `cee6db2` with a focused native workflow. A redundant full run
at `73c4c2d` was cancelled; the separately dispatched full ARM result at `47f7a57` is recorded below.

The [focused macOS rerun at `cee6db2`](https://github.com/tebeco/passenger/actions/runs/38829620638) passes all four
actual cases, zero skips, in about 1m15s. This clears the canonical Profiler fixture failure; the header failure
does not reproduce, and no Profiler header source fix is claimed. Eight native scale-2 frames were inspected:
Snapshots at 640/683/1200 in both themes and both 640px running frames. However, `77d9f2d` captures before asserting,
and `CaptureRenderedFrame` triggers a renderer tick. Follow-up `4ec6143` snapshots selection, visibility, focus,
checked pages and exact geometry immediately after settling, then captures and asserts the pre-capture values.
The completed `c9509e4` CI uses the earlier assertion order and cannot alone settle that header regression. Root evidence is
`artifacts/milestone-1/integration/focused-38829620638/macOS/{focused.trx,tests.log,captures/}`.

The later [pre-capture native run at `fdb008b`](https://github.com/tebeco/passenger/actions/runs/38831479112)
passes all four selected/reported cases on macOS ARM64, zero skips, in 81s. Selection and geometry are measured
before capture, excluding the earlier capture-order concern for this passing checkpoint. Snapshots 683 Dark/1200
Light and running 640 Light/1200 Dark final scale-2 frames were personally inspected; their headers/actions fit.
No production header fix or explanation of the original intermittent failure is claimed. Evidence is root
`artifacts/milestone-1/integration/focused-38831479112/macOS/{focused.trx,tests.log,captures/}`.
Complete `c9509e4` CI has 951 passing/skipped results exactly once per system and no failed tests, but still fails
the macOS shard budget and ARM image detach. Its exact counts and refreshed timing limitations are in the CI
scheduling section. Later WPF/UI additions still require a complete native run.

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
`artifacts/milestone-1/integration/`. The `fc974f5` repair has later native macOS/Linux x64 evidence in complete
`c9509e4` and native Linux ARM evidence in the completed run below.

[Native ARM full run `38827279628`](https://github.com/tebeco/passenger/actions/runs/38827279628) at older
`47f7a57` reports exactly **948 selected/results per OS**, with matching unique name sets and no duplicates.
Linux succeeds with **908 passed/40 skipped**. Windows has **932 passed/12 skipped/four failed**: moved-intermediate
design-time JSON-RPC connection loss, custom-intermediate restore requesting a hardcoded win-x64 host pack, a native
WPF picker timeout, and the MCP WPF Blue ComboBoxItem outside its view. These require their own repairs/reruns;
they are not current-source acceptance. Both systems independently pass native package/install/installed screenshot/
installed MCP/uninstall checks. The installed MCP test passes once per OS and proves both host and debuggee are ARM64.
Root inspected both installed Welcome frames and retained raw results under
`artifacts/milestone-1/integration/arm-38827279628-{win,linux}/ArmResults/<rid>/`.

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
| One tool-window tab style | Terminal shares `ToggleButton.panelTab` (`6ef4205`); Profiler tabs scroll with real pointer/keyboard checks (`7b28e51`). Shared headers preserve tab identity/selection, keyboard focus, thumb hit testing and narrow/floating actions (`16e843e`). Terminal (`6d1752c`) and Problems (`892e0ef`) use scrolling strips with long-name ellipsis and focused inactive tabs visible through resize; remaining populated bodies continue. |
| Thin overlay scrollbars without arrows | Shared editor/output/list `bce47d4` and real terminal scrollback/alternate behavior `2a4df3b`; remaining individual surface review continues. |
| Light plain buttons white with a border | Shared token and both-theme rendered pixel checks, `30d3965`. |
| Popup/menu shadows; rounded centered focus, including checkboxes | Rounded tool-tab focus does not cover scrollbar pointer targets (`16e843e`). Shared default and checkbox-glyph focus (`ab704c8`) checks actual keyboard/pointer input, painted outlines and default-font multi-line Settings labels; remaining popup/radio/menu states continue. |
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

The later [complete native run at `c9509e4`](https://github.com/tebeco/passenger/actions/runs/38830612857) takes
**24m47s**, with exactly one result for each of **951 tests in 129 classes per system**. Windows reports
942 passed/nine skipped, Linux 911 passed/40 skipped, and macOS 922 passed/29 skipped. No test fails. macOS shard 3
takes **20.4766 minutes**, exceeding the unchanged 20-minute budget; its failed job correctly keeps the macOS
coverage gate red. The ARM disk image separately fails detach; the x64 image passes. Updating test weights does
not relabel either failure as success.

The previous timing checkpoint refreshed `TestTimes.json` from this run alone: **all 129 classes on each system**, including healthy results
from the budget-failed macOS job. No failed-class time or fabricated measurement is included. Under the old actual
assignment, the collection model predicts macOS shard 3 at 20.1423 minutes versus 20.4766 observed. Redealing the
stored, rounded native weights gives the following projections; build, discovery, queue and packaging overhead
remain excluded:

| System | Shards | Projected test minutes from `c9509e4` weights |
| --- | --- | --- |
| macOS | 4 | 18.03, 18.02, 18.02, 18.02 |
| Windows | 5 | 15.36, 15.35, 15.34, 15.36, 15.35 |
| Linux | 4 | 15.81, 15.82, 15.83, 15.83 |

Exact merged source `b3cf086` (main `a6e364e` plus Problems `892e0ef`) builds with zero errors and seven unchanged
intentional fixture warnings, then discovers **983 tests in the same 129 classes**. All 13 actual VSTest filtered
listings pass exactly-once coverage: Windows 162/187/189/201/244, Linux 256/196/260/271 and macOS 236/200/259/288.
Repeated allocation writes byte-identical filters, and all four shard CLI boundary regressions pass. The historical
result coverage command still passes Windows/Linux at 951 tests and refuses macOS solely for shard 3's failed job.

The 32 additional cases are **unmeasured native coverage**, even though their existing classes already have weights.
The allocator does not scale class totals by test count. Its identical class membership therefore yields the same
projection for the newer suite; this is not measured current-source acceptance:

| Class | Cases at `c9509e4` | Cases at `b3cf086` |
| --- | ---: | ---: |
| AvaloniaFramesTests | 3 | 4 |
| DialogButtonsTests | 2 | 4 |
| MemorySnapshotsTests | 3 | 4 |
| PackageSourcesTests | 6 | 7 |
| ProblemsTests | 2 | 4 |
| WpfXamlHotReloadTests | 10 | 35 |

The five/four/four matrix, four-plus-one macOS slot limit, 20-minute test budget and 45-minute job timeout are
unchanged. Root retains the native artifacts under
`artifacts/milestone-1/integration/ci-38830612857-results/`. Shipping evidence is
`artifacts/2026-10-16/{build-ci-c950-refresh.log,ci-c950-current-suite.txt,ci-c950-validation.log,ci-c950-analysis.json}`;
the per-system `ci-c950-*-{split,check,observed-cover}.log` files retain projections and real filter checks.
Reproduce the timing refresh with
`python -I tools/test-shards.py update artifacts/milestone-1/integration/ci-38830612857-results`, then build/list the
current suite and run `split`, actual `dotnet test --list-tests --settings` for every emitted file, and `check`.
The shipping artifact `artifacts/2026-10-16/validate-ci-c950.ps1` retains the exact commands used. A new complete
native run must validate the refreshed deal and later case growth without changing acceptance budgets.

Exact pushed source `74f481b` adds the two optimized WPF cases, discovering **985 tests in 129 classes**. Read-only
discovery of its built DLL and all 13 actual VSTest filtered listings again pass exactly-once coverage: Windows
162/187/189/203/244, Linux 258/196/260/271 and macOS 236/200/259/290. Shipping retains
`artifacts/2026-10-16/ci-74f481b-{suite.txt,validation.log}` and `validate-ci-74f481b.ps1` with exact commands.
Its 34 added cases versus the measured 951 are still unmeasured by existing class totals; later editor/status
additions are outside this discovery snapshot.

[Complete native CI `38835542810`](https://github.com/tebeco/passenger/actions/runs/38835542810) at `74f481b` takes
**29m21s**, with exactly **985 selected/results per OS**, no missing or duplicate display names. Windows reports
**976 passed/nine skipped**, Linux **918 passed/67 skipped**, and macOS **927 passed/56 skipped/two failed**.
All 37 Windows WPF Hot Reload cases pass. Windows shards 4/5 fail only their unchanged 20-minute budget; the
expanded WPF class alone takes 588.6 seconds versus the earlier ten-case weight. The observed shard minutes are:

| System | Actual test minutes at `74f481b` | Redealt projection from its healthy class weights |
| --- | --- | --- |
| Windows | 15.99, 16.88, 18.83, 24.81, 21.75 | 19.37, 19.40, 19.38, 19.38, 19.37 |
| Linux | 15.75, 18.55, 16.29, 14.37 | 16.04, 16.05, 16.05, 16.06 |
| macOS | 18.16, 19.93, 19.51, 18.66 | 18.84, 18.84, 18.84, 18.84 |

The refreshed `TestTimes.json` uses all **129 healthy classes on Windows/Linux and 127 on macOS**. macOS
`SelfDebuggingTests` and `DesignTimeIsolationTests` keep their prior healthy `c9509e4` weights, 131.5/41.7 seconds;
failed-case durations are excluded. macOS Self Debugging fails at `Ui.Await`/`Ui.Settle` with `Background work never
finished`, while generated NuGet imports change and repository projects reload. Custom-intermediate restore
separately fails because an existing `/private/var/.../project.nuget.cache` target is written via its `/var` alias.
Both require source-attributed repairs/reruns; no timeout increase or original cause is inferred from the timing table.

The macOS image job also fails its final gate with `ARM_OUTCOME=failure`, despite the continue-on-error ARM step's
API conclusion saying success. Nine mount controls and the x64 image pass; ARM detach is followed by
`cannot find IO media` and a guard reporting `/dev/disk7 belongs to another image`. Root retains
`ci-38835542810-macos-{1,images}.log`, `ci-38835542810-disk-images/` and all 13 raw result artifacts under
`artifacts/milestone-1/integration/`. No green CI or #206 acceptance is claimed.

Exact current `d9c7ce5` discovery contains **995 tests in the same 129 classes**. All 13 real VSTest filtered listings
pass exactly-once coverage: Windows 183/202/196/219/195, Linux 265/244/223/263 and macOS 290/224/292/189. Repeated
allocation is byte-identical; all four shard CLI regressions pass. The historical result gate passes Linux and still
refuses Windows shards 4/5 and macOS shards 1/3. The ten newer cases are unmeasured growth: Editor Tabs 6→8,
File Properties 7→9, Packages 6→8, WPF Hot Reload 37→39 and Avalonia XAML Hot Reload 31→33. Existing class totals
are not scaled for these additions; projections exclude build/discovery/queue/package overhead. Five/four/four
shards, the five-slot macOS limit, 20-minute test budget and 45-minute job timeout remain unchanged.
Shipping `artifacts/2026-10-16/{refresh-ci-74f-timings.py,ci-74f-timing-analysis.json,ci-d9c7ce5-validation.log,validate-ci-d9c7ce5.ps1}`
retains exact raw-result analysis, failed-class retention and discovery/filter commands. The earlier `c9509e4`
tables and 983/985 discovery snapshots above remain historical evidence, superseded for the current deal.

[Complete native `7241544` run `38839133992`](https://github.com/tebeco/passenger/actions/runs/38839133992) finishes
in **30m43s**. All **995 tests per OS** have exactly one result: Windows **986 passed/nine skipped**, Linux
**926 passed/69 skipped**, and macOS **935 passed/58 skipped/two failed**. Windows shards 1/4 fail only the 20-minute
budget at 20.0660/20.9433 minutes; WPF Hot Reload's 39 cases cost 607.9s and Code Assist costs 396.4s. macOS shard 1
fails only its 20.6982-minute budget. macOS shard 4 fails the old Self Debugging nested settle and the Dark Profiler
Snapshots-at-683 geometry case, now recurring after the pre-capture diagnostic repair. Its fractional transformed
geometry is being investigated; no product clipping fix or tolerance change is claimed. The later `a6dcaef` local
Self Debugging evidence is separate from this historical failing native result.

All three native image gates **MOUNT_OUTCOME/ARM_OUTCOME/X64_OUTCOME are success**, independently read at lines
494–496 of root `ci-38839133992-macos-images.log`. All six attachment/info plists were parsed: the owned mount-check,
ARM64 and x64 devices/mounts are recorded, with the owned images absent after cleanup and unrelated system/simulator
images retained. Root `ci-38839133992-disk-images/` retains the raw artifact; shipping
`ci-724-images-plists.{py,json,log}` retains the inspection. This establishes actual repaired native mount and both
image paths at `7241544`, not signing, release publication or whole-CI acceptance.

That historical timing refresh takes all **129 healthy classes on Windows/Linux and 127 on macOS** from those 13 raw
artifacts. Failed macOS `SelfDebuggingTests` retains 131.5s from healthy `c9509e4`; `ToolHeaderTests` retains its
healthy `74f481b` 60.6s. Design Time Isolation now contributes its new healthy measurement. No failed-class time
is used. The observed budget failures, class growth and variance motivate **six Windows shards** while retaining
four Linux/macOS shards and the five-slot macOS image/test limit. The summary still reads shard count from actual
metadata; the unchanged CLI checks require every selected/result name exactly once.

| System | Shards | Projected test minutes from healthy `7241544` weights |
| --- | --- | --- |
| Windows | 6 | 14.77, 14.77, 14.76, 14.75, 14.75, 14.76 |
| Linux | 4 | 16.52, 16.53, 16.54, 16.54 |
| macOS | 4 | 19.09, 19.10, 19.09, 19.10 |

Frozen primary binary `95612f6` discovers **1002 tests in the same 129 classes**. All 14 actual VSTest filtered
listings pass exactly-once coverage: Windows 131/200/166/189/150/166, Linux 262/249/280/211 and macOS
280/262/218/242. Repeated filters are byte-identical; workflow actionlint and all four shard CLI boundary regressions
pass. The seven newer cases are unmeasured class growth (History 4→6, MCP 23→27, WPF Live Tree 4→5), not added
timing estimates. TestTimes is not scaled by case count. The test budget stays 20 minutes, job timeout 45 minutes,
and whole-CI target below 25 minutes. Build, discovery, queue and packaging overhead remain excluded from projections.
Shipping `artifacts/2026-10-16/{refresh-ci-724-timings.py,ci-724-timing-analysis.json,ci-six-windows-724-validation.log,validate-ci-six-windows.ps1}`
retains the exact raw analysis and real filter commands. IDE `PopupHost`/`GoToFilePopup` source drafts are outside
this frozen primary binary; the merged target-app WPF popup repair is included.
Root's merged popup/Self seven-case run passes in 1m41s; integrated Git's two cases pass in 13s after a build with
zero errors/seven unchanged fixture warnings. These focused passes do not replace native full-suite acceptance.

### Complete 1015-case measurements and current 1031-case filters

[Native `aab25be` run `38843579821`](https://github.com/tebeco/passenger/actions/runs/38843579821) completes in
**27m43s**. Each OS reports exactly the same **1015 discovered names**, once each, with zero missing, unexpected
or duplicate results. Windows has **1000 passed/nine skipped/six failed**, Linux **934 passed/79 skipped/two failed**,
and macOS **944 passed/68 skipped/three failed**. Windows shard test times are 12.04/11.88/16.24/18.06/13.75/16.24
minutes; Linux 17.06/13.29/15.90/12.32; macOS 21.67/21.62/14.56/17.85. macOS shards 1/2 exceed the unchanged
20-minute budget. All six Profiler header cases pass, including the transformed-rectangle controls; Self Debugging
passes in 101s. The native Terminal tab geometry false positive is repaired separately in `88c97a1`; Git hint,
WPF and other failing-class outcomes remain attributed to that historical run rather than called green.

The refreshed inputs use all **125 healthy Windows, 128 Linux and 127 macOS class totals** from those 14 raw TRXs.
Failed classes retain their prior healthy weights: Windows WPF XAML Hot Reload 607.9s, History 8.2s, Tool Layout 3.0s,
MCP 203.2s; Linux Tool Layout 3.4s; macOS Tool Tab 31.9s and Tool Layout 1.8s. No failed-class duration is used.

| System | Shards | Redealt projected test minutes from healthy `aab25be` weights |
| --- | --- | --- |
| Windows | 6 | 14.64, 14.63, 14.63, 14.63, 14.63, 14.63 |
| Linux | 4 | 14.49, 14.49, 14.48, 14.48 |
| macOS | 4 | 18.67, 18.67, 18.68, 18.67 |

Frozen primary `6187168d8c30e91a36b4c09eec4585a56a4d8b37` discovers **1031 tests in 129 classes**; its test DLL
SHA256 is `36E51A432FC8D80DF2CAFC56D4A4BC3CC63E061BA38D8606F119E5C8F27C32A6`. All **14 actual VSTest filters**
cover that listing exactly once: Windows 178/155/175/159/182/182, Linux 236/189/328/278 and macOS
295/217/261/258. Repeated runsettings are byte-identical; workflow actionlint and all four shard CLI regressions pass.
The **16 added cases are unmeasured**: Actions 2→4, Navigation 6→8 and WPF XAML Hot Reload 44→56. In particular,
the retained healthy Windows WPF total predates this growth; it is not scaled or represented as measured current
acceptance. The 20-minute test budget, 45-minute job timeout, below-25-minute CI goal and five macOS slots remain
unchanged. Projections exclude build, discovery, queue and packaging overhead. A successful complete native run
of the new source is still required.

Root `artifacts/milestone-1/integration/ci-38843579821-{results/,analysis.json}` retains all raw results and the
independent native coverage audit. Shipping `artifacts/2026-10-16/{refresh-ci-aab-timings.py,ci-aab-timing-analysis.json,
validate-ci-aab-current.ps1,ci-aab-current-filter-checks.log,ci-aab-shards-tests.log,ci-aab-actionlint.log}` retains
the measured refresh and actual filter checks. Earlier timing checkpoints remain provenance; the current deal uses
these healthy `aab25be` measurements and the explicitly retained older values for its failed classes.

The completed [CI4 run at `f4d89b9`](https://github.com/tebeco/passenger/actions/runs/38821710686) contains exactly one
result for each of its **943 tests in 129 classes per system** across all 13 shards. Windows reports 934 passed,
nine not executed and no test failures; Linux reports 902 passed, 40 not executed and one failure; macOS reports
911 passed, 30 not executed and two failures. Windows shard 3 took 20.18 minutes of tests and macOS shard 2 took
21.55, exceeding the unchanged 20-minute budget. The complete run took 25m55s, so the below-25-minute acceptance
remains open. Both disk images passed in the single sequential image job; all packaging jobs succeeded.

The preceding timing checkpoint took the measured CI4 seconds of classes with no failed result: **129 Windows, 128 Linux,
127 macOS**. This includes the 31-case XamlHotReloadTests cost that the earlier weights understated: 436.1 seconds
on Windows, 343.7 on Linux and 341.6 on macOS. With the actual CI4 class assignments held fixed, the measured
collection model accounts for all but 7–32 seconds of each shard's VSTest elapsed time. Build, discovery, queue
and packaging costs are separate. No failed run is declared green by updating its successful classes.

The failed classes retained the following prior inputs; their failing elapsed times were recorded, not used as healthy
measurements. These were the retained inputs before the healthy `c9509e4` refresh:

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
