# PR #220 review

Reviewed head: `4009385ef12764550915ccb142c47644d395c29a` against `Zaneris/passenger:tebeco`, merge base `a5dc9928b7b9e77541025cedff4ed8304628afe5`. Fixes are integrated into `codex/pr-220-review`, published to `tebeco/passenger` (the workspace's `origin`).

## Findings

### 1. [P2] Build pane Stop does not cancel pending preparation

- Location: `src/Passenger.App/Run/BuildRunController.cs`, `BuildNextAsync`; `src/Passenger.App/MainWindow.axaml.cs`, Build Stop handler.
- Build now becomes active before awaiting WPF population preparation. During that await no builder process exists. The pane's Stop handler calls `session.Builder.Stop()` directly, which cannot cancel preparation. After the user presses Stop, the continuation still starts the build.
- Fix: expose the controller's build-only stop operation and route the pane's button through it. Keep the identity check that prevents an old preparation from starting a canceled build.
- Regression: hold real project-host evaluation behind a named pipe, click the Build pane's Stop button, release evaluation, and verify the build remains stopped and a subsequent build succeeds.
- Status: fixed. The real-window regression passes with the fix and fails against the original handler with "The Build pane's Stop ignored preparation before process startup".

### 2. [P2] Significant XAML text whitespace is acknowledged without being applied

- Location: `src/Passenger.Inspector/XamlUpdatePlan.cs`, direct text-content comparison (shared by Avalonia and WPF).
- The planner trims direct text before comparing it and discards whitespace-only nodes. Changing `Before` to ` Before ` under `xml:space="preserve"` therefore passes as an empty update. The source baseline advances while live and future views retain the old compiled text. In a mixed batch, other properties can apply even though the unsupported text edit was silently skipped.
- Fix: preserve parsed whitespace and compare meaningful direct text exactly; under inherited `xml:space="preserve"`, include whitespace-only text too. Continue ignoring ordinary formatting-only indentation.
- Regression: edit preserved text and a supported literal property together through a real debugged Avalonia app; require restart, leave both live values unchanged, and retain the pending edit.
- Status: fixed. Both regression cases (spaces around nonempty text and whitespace-only content) pass with the fix and fail against the original planner: expected `RestartNeeded`, actual `Applied`.

### 3. [P1] Terminal shutdown leaves later shells running or removes a replacement tab

- Location: `src/Passenger.App/ToolWindows/TerminalPanel.cs`, `CloseAllAsync`.
- Sequentially awaiting the newly asynchronous session disposal means only the first shell is stopped before the last window closes and its dispatcher can stop. The eventual `tabs.Clear()` can also discard a new session opened during old-session teardown, leaving its shell running without a tab.
- Fix: detach the existing tabs, initiate all their disposals before yielding, then await their combined completion. New sessions are outside that snapshot.
- Status: fixed in `dcec3f9`. Both real-shell regressions failed against the original implementation; all 11 focused terminal/startup/scrollbar tests passed after the fix. See [runtime review](pr-220-runtime-review.md).

### 4. [P2] Mixed XAML content reordering is acknowledged without being applied

- Location: `src/Passenger.Inspector/XamlUpdatePlan.cs`, `Content` comparison shared by Avalonia and WPF.
- Moving `Before` from before an unchanged `Run` element to after it leaves the separate text and element sequences unchanged. The planner accepted the unsupported reorder, advanced its baseline and could apply other edits in the same batch while the visible text retained its compiled order.
- Fix: compare each meaningful text node's position among non-property child elements as well as its value. Literal property leaves remain excluded so supported property edits keep their existing behavior.
- Regression: verify the compiled inline order in a real debugged Avalonia app, reorder the mixed content alongside a button edit, and require restart with the button and control identity unchanged and the edit still pending.
- Status: fixed in `95d0adb`. The added regression failed against the prior planner with expected `RestartNeeded`, actual `Applied`; all 20 focused XAML cases passed afterward. Details are in the [XAML review](pr-220-xaml-review.md).

## Scope and validation

Review concentrated on runtime lifecycle, XAML update planning/application, design-time build isolation, build-hook preparation, docking/popup changes, and packaging/test orchestration. This is a large, cross-platform PR; local Windows validation cannot establish native macOS or Linux behavior.

- `dotnet build --no-restore`: passed with zero warnings and zero errors after the fixes. The initial cold build also passed, with seven existing unused-variable warnings in debugger fixtures.
- New regression tests: **3 passed**, none skipped. Negative control using the original PR handler/planner: **3 failed at the expected assertions**; fixes restored afterward.
- CI sharding CLI tests against the discovered test suite: **4 passed**.
- Disk-image ownership tests: **13 skipped** because they require macOS; no native disk-image validation claimed.
- Final neighboring build and XAML regression checks: **49 passed, 0 failed, 0 skipped** in 4 minutes 41 seconds. Includes all `BuildRunTests`, the three new cases, Avalonia/WPF literal property-element updates, original-anchor preservation after additions, WPF Stop/Close during Run preparation, and unsupported structure rejection. Results: `artifacts/pr220-verified/*.trx`; console log: `artifacts/pr220-verified.log`; successful build log: `artifacts/pr220-build-verified.log`.
- Validation setup note: the first broader run was stopped after detecting that restoring negative-control sources preserved older timestamps and reused the negative-control binaries. The affected sources were timestamped again and rebuilt, and the test-output inspector binary was verified newer than the restored source. Only the corrected run in `artifacts/pr220-verified/` counts as final validation.
- Fixes and review reports have separate commits. Follow-up review used isolated runtime, XAML, UI and packaging branches, merged into `codex/pr-220-review` after review and validation.
- [UI review](pr-220-ui-review.md): no additional substantiated finding; 46 tests passed and one macOS-only test skipped.
- [Packaging and CI review](pr-220-packaging-review.md): no additional substantiated finding; sharding regressions, six-shard generation and script syntax checks passed.
- Integrated build: passed, zero errors and seven existing unused-variable debugger-fixture warnings. All **6 new regression cases passed together**, zero failures/skips (23 seconds), after all branches were merged. Evidence: `artifacts/pr220-integrated-build.log`, `artifacts/pr220-integrated-final.log`, `artifacts/pr220-integrated-final/*.trx`.
- Integration caught a redundant `Kill()` in the new terminal test's cleanup racing with released native handles. `eadc307` removes those cleanup calls and uses idempotent disposal; production behavior and assertions are unchanged. The corrected run above is the acceptance result.
- `git diff --check`: passed.
