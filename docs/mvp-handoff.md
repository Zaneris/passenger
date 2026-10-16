# MVP shutdown handoff — 2026-10-16

Work is paused for the owner's shutdown. This is a resume point, not whole-MVP, whole-UI, release or final MCP
acceptance. Retain the worktrees and ignored artifacts; do not archive or clean them before resuming.

## Resume investigation — 2026-10-16

Final shutdown source `74edf7d` is confirmed by [CI `38848023088`](https://github.com/tebeco/passenger/actions/runs/38848023088).
Every system reports all 1,034 expected cases exactly once: Windows 1,023 passed/nine skipped/two failed, Linux
942 passed/92 skipped/zero failed, macOS 951 passed/81 skipped/two failed. The two failure families are Windows
populated Git History's empty diff content and macOS Find Action's missing pointer-selected row, both themes.
All 14 shards fit their 20-minute budget; all packaging jobs pass; the run takes 24m58s. Resume with these actual
failures rather than the earlier pending-run assumptions below. Raw evidence is retained under
`artifacts/resume-ci-2026-10-16/evidence/` in the main checkout.

Fresh restore subsequently detects [GHSA-qhrr-8q5h-9q3h](https://github.com/advisories/GHSA-qhrr-8q5h-9q3h), added to
GitHub's advisory database October 15 at 18:35:46 UTC. MessagePack 2.5.303, published September 17 at 19:19:27 UTC,
patches StreamJsonRpc's older transitive minimum. The explicit App/Lsp package references and four lock updates
retain strict auditing; this independent restore blocker is not the cause of those four native test assertions.
Roslyn 5.11.0's separately downloaded payload still contains MessagePack 2.5.302 and remains an upstream/provenance
follow-up; do not claim the whole bundled payload remediated by the project graph repair.

## Reviewed source and verification

Before the final shutdown push, the confirmed remote checkpoint is `e43dd47`, containing the healthy native timing
refresh and scoped UI evidence. Reviewed main additionally merges WPF diagnostics `c1ee283` and Commit `9391530`.
Verify `git status`, the final main/origin heads and CI outcomes when resuming.
At handoff, [CI `38847330773`](https://github.com/tebeco/passenger/actions/runs/38847330773) is in progress for
older pushed source `e43dd4745bb6a4089752b66107c42449e44ceecd` (created 03:48:15 UTC). A final source push starts
a newer run; resume with the run whose head matches final `origin/main`. The older run is not final-source evidence
and may be superseded by the parent. Avoid a cycle of documentation-only pushes to record each new run ID.

Root's `6187168` combined solution build has zero errors and seven unchanged intentional debugger-fixture warnings
in 55.84s. Its final 25 focused WPF/framework MCP/Git/empty-hint/Action/Terminal checks pass in 2m58s, no skips.
The exact DiffView integration also passes both cases in 28.9s. Root retains logs under
`artifacts/milestone-1/integration/`. These focused checks do not replace the pending complete native run.
Final root Commit integration passes **2/2 in 7.43s** after a zero-error/seven-unchanged-fixture-warning **26.72s**
build. Final actual discovery contains **1034 tests**, recorded in `shutdown-final-discovery.log` there. All workers'
owned build/test sessions are complete; native source/output and worktrees are retained.

The earlier frozen `6187168` test DLL discovers **1031 tests in 129 classes**. All 14 actual VSTest filters cover that listing
exactly once, repeated filters are identical, four shard CLI regressions and workflow actionlint pass. The 16 cases
added since native `aab25be` are unmeasured: Actions 2→4, Navigation 6→8, WPF XAML Hot Reload 44→56. Healthy
native class measurements are retained without scaling; failed-class prior weights, projections and exact DLL hash
are in [MVP progress](mvp-progress.md#complete-1015-case-measurements-and-current-1031-case-filters).
Six Windows/four Linux/four macOS shards, the 20-minute test budget and below-25-minute CI goal remain unchanged.
The final **1034-case** source adds **one MCP and two VCS cases** after those actual filter checks. The 14 filtered
discoveries and complete native suite were **not repeated** for those three additions; resume with current discovery,
exact filter/result coverage and the final-source cloud run rather than treating the 1031-case checks as current.
The latest completed native run `38843579821` is not green: 27m43s, two macOS budget failures and assigned test
failures, with exact 1015-name coverage on every OS. It does establish the six repaired Profiler header cases and
Self Debugging on native macOS. Collect the next run's actual result artifacts before refreshing timing again.

Git follow-up `710f129` repairs the native empty-hint regression with Windows four-case and native Linux four-case
controls; the native run passes in 17s with a zero-warning/error 21.05s build. The selected-row oracle remains strict.
Its source and actual captures were reviewed before merge. Shell's `artifacts/git-ci-regressions/` retains the detailed
evidence; Windows four-case controls pass in 23s. The old strict controls fail both themes at the same 560px overflow.

WPF `c1ee283` adds opt-in target input-phase/shutdown tracing and complete refusal/watch output. Its eight focused
checks pass in 1m51s. The animated default popup truthfully refuses input after 39 moving samples and a background
timeout; the strict one-frame-per-second probe records 61 moving samples. No input is queued. No-late-click,
recovery and fresh-retry controls remain. This is diagnostics and refusal evidence, not a behavioral repair for the
unexplained native watched property cancellation. The original cancellation has not been reproduced locally.
Root's exact merged six-case validation passes in 1m6s, zero skips, after a zero-error/seven-fixture-warning 34.46s
build. It includes strict Busy/Moving/no-queue/recovery and WPF property Debug/watch checks.

Commit `9391530` is reviewed and merged. The final whole Windows VCS class passes **36/36**, zero skips, in 1m16s;
exact native Linux Commit cases pass **2/2** in 6s after a zero-warning/error 30.29s build. Real checkbox Space,
typed long messages, resize/visible-file anchor, Refresh/caret, footer wheel, both actions, Diff and actual single-file
Commit remain checked. Parent and Shell inspected actual default/narrow/side200 frames in both themes. Shell retains
`artifacts/commit-body/{tests-vcs-final.log,native.log,final/,native/captures/,native/commit-body-linux.trx}` and
`tests/Passenger.Tests/TestResults/commit-body-vcs-final.trx`. This completes the bounded source checkpoint, not
whole Commit/Git/UI acceptance. The nonrepository Git HEAD/Local unusable-action state is an unstarted next item.

## Preserved unfinished work

| Owner/worktree | Preserved draft | Resume action |
| --- | --- | --- |
| Shipping: `artifacts/worktrees/shipping`; local branch `codex/mvp-shipping` | **Unmerged WIP `b22fea36bc30de868be9a2537ba811b0caf0f789`**, only `src/Passenger.App/Navigation/SymbolPopup.cs` and `tests/Passenger.Tests/CodeAssistTests.cs`. The worktree currently shows clean `codex/mvp-shipping-handoff`; switch back explicitly to resume the WIP. | Original real Roslyn-backed long-symbol rows fail both themes from overlapping container text even at 1200px. The finite row/tooltip draft builds with zero warnings/errors and passes layout/resize/actual hover checks, then deliberately fails both cases at pointer-selected Enter because routing is still query-only. Finish popup-level routing, cover the shared StructureView consumer, inspect both-theme final scale-2 captures, run focused regressions/native controls, then submit a separate reviewed source commit. Do not merge the WIP as a passing fix. |
| Debugger: `artifacts/worktrees/debugger`, `codex/mvp-debugger` | **Unvalidated WIP `5ac88e45a7143b0e7d950dea23015e0fc20b18e0`**, unmerged/unpushed. Five files: new `src/Passenger.Inspector/{CompiledXaml,UncreatedXaml}.cs`, `src/Passenger.Inspector/XamlHotReload.cs`, `src/Passenger.Inspector.Wpf/UncreatedXaml.cs` and its project file. No fixture/build/test acceptance. | Read `artifacts/shutdown-debugger-handoff.md` and `avalonia-uncreated-wip-manifest.json` there before editing. Avalonia `GetRegistered(Type)` runs initializers; refuse user/base initializers before static-field reads and tie the population method to PDB source. Reconcile with merged wire version 10; prove actual first construction/negative controls and rerun affected WPF checks before review. |
| Shell: `artifacts/wt-shell-isolated` | Commit `9391530` is reviewed/merged with the actual Windows/native evidence above; no unfinished Commit draft remains. Worktree and artifacts are retained. | Resume the unstarted nonrepository Git HEAD/Local unusable-action state and other remaining UI states; do not repeat the completed source work as unfinished. |

Shipping's symbol evidence remains in `artifacts/2026-10-16/{symbol-before-build.log,symbol-before.log,
symbol-before-results/,symbol-layout-build.log,symbol-layout.log,symbol-layout-results/}` and
`artifacts/ui-audit-213/2026-10-16/{symbol-before/,symbol-layout/}`. The layout-only run is **two failures**, not
acceptance; both stop at the actual pointer-Enter navigation boundary. No owned shipping build/test execution remains
active. Do not kill unrelated agents' processes when inspecting retained outputs.

## Order of work on resume

1. Read [MVP progress](mvp-progress.md) and the [UI surface inventory](ui-audit-213.md), check each worktree's branch,
   cleanliness and logs, then fetch the final pushed CI run/source and raw results. Preserve source attribution and
   separate actual native acceptance from newer focused tests. Reproduce assigned failures rather than weakening
   geometry, coverage, input or timeout assertions.
2. Continue #213 locally actionable work: finish preserved Symbol/Structure changes, then remaining popup/dialog/menu,
   populated Commit/diff/tool states and shared focus/scale checks. Real input and inspected scale-2 Dark/Light frames
   at default/narrow widths are required; capture existence alone is not a visual pass. Coordinate files/native leases.
3. Continue unresolved #33/runtime and native cancellation investigation using the new diagnostics. Broader structural,
   binding/markup/resource/style/template graphs remain unsupported; truthful restart guards do not implement them.
4. After the remaining feature/UI work and green native CI, implement the concrete #211 MCP/provider/test gaps listed
   in MVP progress. The audited 65-tool inventory is not a completed final pass. Then do #55 site/README/screenshots,
   the final non-publishing #56 dry run, and only then the owner's chosen tag/publication action.

External/manual limits remain explicit: the desktop was locked, so unlocked native desktop/Rider interaction was
deferred; headless whole-window implementation and tests remain feasible. Rider's bundled defaults are actual source
reference, not observed native behavior. The website `www` redirect needs the owner's Cloudflare dashboard permission.
Signing/notarization, broader unsupported runtime/platform features and final release acceptance are not claimed.
Do not reclassify unfinished source work as an external blocker. No issue has been closed or release/tag published.
