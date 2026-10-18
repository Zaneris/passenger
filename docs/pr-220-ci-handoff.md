# PR #220: CI analysis and resumable handoff

## Status and exact revisions

**Completed: CI failed. Analysis only; no CI fixes started.** The run finished on 2026-10-18 at approximately 18:54 UTC (22:54 Mauritius). The four PR-review fixes were merged and pushed before this run. The remaining work below is deliberately left for a later resumed session.

- Reviewed upstream PR: <https://github.com/Zaneris/passenger/pull/220>.
- Review branch: `codex/pr-220-review`, pushed at `fb00dbb955207d0bffe5f4d8a7901c51a3f95625`.
- Integrated `main`: `97c9b2c1685702c964d8d9293c6e882a44aacda0`, pushed to `tebeco/passenger`.
- Run under analysis: [CI 39154154084](https://github.com/tebeco/passenger/actions/runs/39154154084).
- Prior main comparison: [CI 38947955385](https://github.com/tebeco/passenger/actions/runs/38947955385), commit `4009385ef12764550915ccb142c47644d395c29a`.
- All four review findings are fixed. See [review and local validation](pr-220-review.md); six new regression cases passed together after integration.
- This handoff is a subsequent documentation-only commit; it does not change the tested source. Its commit skips duplicate CI, and the exact code-validation run is the one linked above.

## Final outcome

**21 jobs: 16 successful, 5 failed.** There are three underlying failed shards and two summary jobs propagating their failures. No job was cancelled.

| Area | Outcome | Test results / reason |
|---|---|---|
| Linux, all four shards and summary | Passed | 949 passed, 93 skipped; no failed tests |
| Windows, six shards | One shard failed | 1,032 passed, 9 skipped, 1 failed; watched WPF timeout in shard 1 |
| macOS, four shards | Two shards failed | 960 passed, 82 skipped; no failed tests; shards 1 and 4 exceeded 20 minutes |
| macOS and Windows summary jobs | Failed downstream | Explicitly report failed shards, not missing-test or duplicate-test errors |
| Linux archive, Windows installer, macOS ARM64/x64 disk images, package audit | Passed | All packaging/audit jobs succeeded |

All 14 test artifacts were collected: **1,042 results per platform**. Across platforms this is 2,941 passed, 184 skipped and one failed. The six new review regression cases produced **16 passes and two expected non-Windows WPF skips**; none failed. This does not make the overall CI run green.

| Platform | Shard durations in minutes, in shard order | 20-minute budget |
|---|---|---|
| Linux | 16.264, 18.869, 14.398, 18.045 | All passed |
| Windows | 16.985, 16.861, 16.554, 12.626, 16.601, 16.336 | All passed; shard 1 failed its test |
| macOS | 23.296, 19.407, 18.815, 21.612 | Shards 1 and 4 failed |

## Current-run failure analysis

### F1 — Watched WPF edit loses its running app and never reports an outcome

Evidence: [Windows shard 1](https://github.com/tebeco/passenger/actions/runs/39154154084/job/117753776150), local log `117753776150.log`.

- Failed test: `WpfXamlHotReloadTests.Literal_property_elements_update_their_parent_and_future_views_without_replacing_state(watched: True)`.
- Exception: `System.TimeoutException: Waited 90 s for the saved WPF edit to apply`.
- The stack points to `WpfXamlHotReloadTests.cs:932`, through `ApplyEdit` at line 866 and `FutureApplied` at line 1645. Lines 930–931 deliberately add `Text="Duplicate"` alongside `<TextBlock.Text>` to test refusal without replacing or terminating the live view.
- Log lines 799–804 show watch detecting `RepeatedView.xaml.passenger-save`, reloading projects, announcing restart and exiting the app. Lines 808–819 then show MC3024 (duplicate Text property), followed by watch waiting for a correction. The duplicate-property inspector outcome is not logged before exit.
- This is the same restart pattern as the previous main's failure, but the previous run reached the later malformed-element case (current line 947) and MC3074. Both compiler failures arise from intentionally invalid test inputs; neither proves a partial disk write.
- **Strong hypothesis, not yet a proven root cause:** discovery of the atomic-save sidecar triggers watch to restart the target. The disconnected inspector cannot publish the expected update outcome. `RunXamlHotReload.cs:192,205` requires a connected app; the helper waits for a new `LastApplied`. Buffered logs establish event order, but do not establish the precise watch classification/restart cause.
- This shard's budget passed: **16.985/20 minutes**. Do not conflate this failure with the historical Windows overrun.

Next investigation: capture watch input classification/restart reasons together with ordered save, process-exit, inspector-connection and apply-result events. Start with `src/Passenger.App/Workspace/FileText.cs:127,167` and `src/Passenger.App/Run/BuildRunController.cs:593`, plus `RunXamlHotReload`. Keep atomic-write safety, real-app coverage and existing timeout/assertions. The acceptance condition is that the watched test reports the expected refusal while the original target stays alive, including the duplicate-property and malformed-element cases, followed by a successful Windows shard.

**No CI fix has been attempted.** The user's final instruction is to finish this analysis and stop; the tasks below are for a later explicitly resumed session.

### F2 — macOS shard 1 exceeds the aggregate budget

Evidence: [macOS shard 1](https://github.com/tebeco/passenger/actions/runs/39154154084/job/117753776121), log lines 1091–1098 and its TRX. **229 passed, 72 skipped, zero failed; 23.296 minutes.** The failure is the aggregate 20-minute limit. Class warnings are separate diagnostics, not additional failed tests.

The largest class is `XamlHotReloadTests` at **611.2s**, followed by `PackageSourcesTests` 83.5s, `ToolHeaderTests` 74.5s, `SolutionViewTests` 70.7s, `SolutionFoldersTests` 68.7s and `ProfilerTests` 64.1s. The baseline already had shard 1 at 22.629 minutes and XAML at 606.3s. This run demonstrates a recurring capacity/performance problem, not proof that a particular review change caused it.

Next investigation: measure XAML fixture/setup, restore/build, target startup, update application and teardown separately on native macOS. Then profile the other contributors if needed. Preserve full real-app coverage and `Alone` isolation; do not replace it with planner-only tests or raise budgets to make CI green.

### F3 — macOS shard 4 exceeds the aggregate budget

Evidence: [macOS shard 4](https://github.com/tebeco/passenger/actions/runs/39154154084/job/117753776181), log lines 883–885 and its TRX. **261 passed, one skipped, zero failed; 21.612 minutes.** Every class stayed within its individual budget, while their aggregate exceeded the shard limit.

| Class | Actual seconds | Allocator estimate seconds |
|---|---:|---:|
| BuildRunTests | 258.0 | 206.1 |
| HotReloadTests | 135.4 | 103.7 |
| HotReloadEditsTests | 133.2 | 165.1 |
| XamlTests | 79.1 | 62.5 |
| UpdatesTests | 75.0 | 83.8 |
| VcsTests | 71.5 | 55.5 |

Next investigation: profile the dispersed build/run startup costs and hot-reload setup/update costs independently of F1. `BuildRunTests` spans 33 cases; its slowest case is browser-launch readiness at about 25 seconds, so there is no single failed/hung test explaining the overrun.

**Scheduling constraint:** macOS shard durations total 83.13 minutes. Shards 2 and 3 have only 1.778 minutes combined spare capacity, versus 4.908 minutes excess across shards 1 and 4. A simple duration transfer cannot fit these observed totals under four 20-minute slots. This is not a mathematical lower bound for a different allocation: collection overlap and overhead change with placement. Model complete class timings before claiming redistribution alone will solve it, and leave room for variation.

### Downstream summary failures

[macOS summary](https://github.com/tebeco/passenger/actions/runs/39154154084/job/117760117747) log lines 354–356 reports shards 1 and 4 ended in failure. [Windows summary](https://github.com/tebeco/passenger/actions/runs/39154154084/job/117760117760) lines 366–367 reports shard 1 ended in failure. The step named “Every test ran in exactly one shard” also checks upstream job status; its red result is not evidence of a separate suite-distribution defect. Do not open two additional fixes for these propagated results.

## Prior-run comparison (not the new run's result)

The previous main already failed CI. Its macOS shard 1 took 22.6291 minutes and Linux shard 4 took 20.9306 minutes against the unchanged 20-minute limit; all executed tests in both shards passed. Windows shard 1 took 21.0410 minutes and also had a watched WPF property-element test timeout. These observations must not be attributed to the review fixes.

The Windows baseline's `Literal_property_elements_update_their_parent_and_future_views_without_replacing_state(watched: True)` intentionally saves malformed `<TextBlock.>` syntax to require restart without terminating the live app. Before the 90-second timeout, watch reports `RepeatedView.xaml.passenger-save` as added, reloads projects, restarts and exits the app; its rebuild then fails with MC3074. The compiler error is therefore not evidence of an incomplete save. A temporary-save-file-triggered restart is a strong hypothesis, but the precise watch input/event cause still needs verification. The same watched case passed in local focused validation, so it is not established as resolved.

Baseline class timing also drifted from estimates: macOS XAML 606.3s versus 497.4s, Windows WPF 948.1s versus 607.9s, and Linux slowdown across BuildRun, UnitTests, MCP, HotReloadEdits and PackageSources. Removing the Windows timeout would not explain all class-time drift, even if it could eliminate that shard's immediate overrun.

## Follow-up dependency map

Evidence triage above is complete; **implementation has not started**. A, B1, B2 and C are ready for deeper parallel investigation from the completed run. D and E must wait for their dependencies. Starting implementation requires the later resume instruction; this session stops at analysis.

| ID | Work | Can start in parallel with | Depends on | Completion evidence |
|---|---|---|---|---|
| A | Diagnose and fix F1 on Windows: watch/save/inspector lifecycle | B1, B2, C | None; failure logs and source references above are available | Verified restart cause; watched refusal cases retain original app and report an outcome; unchanged test passes |
| B1 | Diagnose/optimize F2: macOS XAML class setup and execution | A, B2, C | None; complete native TRX available | Measured phase costs; native regression coverage and isolation preserved |
| B2 | Diagnose/optimize F3: macOS build/run and hot-reload costs | A, B1, C | None; complete native TRX available | Measured costs and successful native neighboring regressions |
| C | Model macOS whole-class allocation and overhead | A, B1, B2 | None; all four shard artifacts and suite listings available | Predicted schedule accounting for collections and variation; exact suite coverage |
| D | Finalize macOS timing weights/allocation if needed | A, provided shared timing files have one owner | B1/B2 changes finished or ruled out; C model | Fresh timings describing final code, unchanged budgets, sharding CLI checks |
| E | Integrate and validate in full CI | Read-only documentation | A and required B1/B2/D changes reviewed and merged | Exact pushed SHA; green full CI; no missing/duplicated tests; updated handoff |

Use separate branches/worktrees for A, B1 and B2, starting from current `origin/main` after checking for newer work. Suggested branch names are `codex/ci220-watch-save`, `codex/ci220-macos-xaml` and `codex/ci220-macos-build-run`. C can analyze immediately but should have one owner for final `TestTimes.json`, `tools/test-shards.py` and workflow changes. Budgets remain unchanged. A's source area is `Workspace/FileText.cs`, `Run/BuildRunController.cs`, `Run/RunXamlHotReload.cs` and watcher/inspector lifecycle. B1/B2 may share those production paths or common test fixtures: profiling is parallel-safe, but coordinate ownership before overlapping edits. D does not depend on the Windows-only fix unless it changes shared timings; E depends on both tracks.

Keep the 20-minute shard budget, class budgets, assertions and timeouts unchanged. Tests in `Alone` must retain isolation. The allocator models `Alone + max(window, debugger)` and moves whole classes; macOS has four test runners plus one packaging job within five slots. Investigate runner capacity before proposing extra shards. Final timing regeneration follows correctness/performance changes, so the weights describe the code that will actually run.

## Evidence and pickup instructions

Local raw evidence is ignored under `artifacts/pr220-ci-39154154084/`: `run.json`, `jobs.json`, `artifacts.json`, five failed-job logs, all 14 `test-results-*/` directories containing TRX/suite/shard metadata, and the derived `analysis.json`. Historical logs are `artifacts/pr220-baseline-*.log`. GitHub run/job links above remain the shared evidence source; download `test-results-*` from the exact run if local artifacts are unavailable. Do not rely on a branch name alone to identify the tested code.

When resuming, confirm the remote `main` SHA and whether a later CI run supersedes this report. Start only tasks whose dependencies are satisfied; preserve completed review fixes and use new branches for CI work. Distinguish a failing assertion from a job budget failure and from a summary job propagating an upstream failure.

## How to resume later

Open this repository in a new Codex chat and paste:

> Resume from docs/pr-220-ci-handoff.md. Check current main and the latest CI results, then fix the remaining issues using separate branches and subagents where the handoff allows parallel work. Respect its dependencies, preserve test assertions and time budgets, merge validated fixes into main, push, wait for CI, and update the handoff.

Before editing, the next agent should report which evidence is still current and which task IDs it will start. If newer code or CI results supersede a finding, update its status rather than repeating completed work. Commit every fix separately from unrelated changes, and record the resulting hashes here so another pickup can continue without reconstructing this conversation.
