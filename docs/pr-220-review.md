# PR #220 review

Reviewed head: `4009385ef12764550915ccb142c47644d395c29a` against `Zaneris/passenger:tebeco`, merge base `a5dc9928b7b9e77541025cedff4ed8304628afe5`. Fixes are in the local branch `codex/pr-220-review`.

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

## Scope and validation

Review concentrated on runtime lifecycle, XAML update planning/application, design-time build isolation, build-hook preparation, docking/popup changes, and packaging/test orchestration. This is a large, cross-platform PR; local Windows validation cannot establish native macOS or Linux behavior.

- `dotnet build --no-restore`: passed with zero warnings and zero errors after the fixes. The initial cold build also passed, with seven existing unused-variable warnings in debugger fixtures.
- New regression tests: **3 passed**, none skipped. Negative control using the original PR handler/planner: **3 failed at the expected assertions**; fixes restored afterward.
- CI sharding CLI tests against the discovered test suite: **4 passed**.
- Disk-image ownership tests: **13 skipped** because they require macOS; no native disk-image validation claimed.
- Final neighboring build and XAML regression checks: **49 passed, 0 failed, 0 skipped** in 4 minutes 41 seconds. Includes all `BuildRunTests`, the three new cases, Avalonia/WPF literal property-element updates, original-anchor preservation after additions, WPF Stop/Close during Run preparation, and unsupported structure rejection. Results: `artifacts/pr220-verified/*.trx`; console log: `artifacts/pr220-verified.log`; successful build log: `artifacts/pr220-build-verified.log`.
- Validation setup note: the first broader run was stopped after detecting that restoring negative-control sources preserved older timestamps and reused the negative-control binaries. The affected sources were timestamped again and rebuilt, and the test-output inspector binary was verified newer than the restored source. Only the corrected run in `artifacts/pr220-verified/` counts as final validation.
- The two fixes are committed separately on `codex/pr-220-review`; the review report is maintained in its own documentation commit. Further review and fixes continue on isolated branches before integration here.
- `git diff --check`: passed.
