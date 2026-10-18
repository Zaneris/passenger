# PR #220: UI review

Reviewed the original PR delta from `a5dc9928` to `4009385`, with checks run on review base `72e08ed` in the isolated `codex/pr220-ui-fixes` branch.

## Findings

No additional actionable UI defect was substantiated in this pass. No production code or test assertions were changed.

## Coverage

- Docking: temporary side collapse versus persisted layout, stripe toggles, explicit hiding, focus restoration when moving or floating panes, header overflow.
- Editors and navigation: tab widths, modified and close indicators, focused tab actions, popup placement and resizing, keyboard selection, breadcrumbs.
- Settings and dialogs: responsive option rows and template panes, retained input and focus, footer choice ordering, shared backgrounds.
- Tool windows: history tab reuse and close focus, Git and local-history scrolling, package version preservation and feed identity, problem links and row sizing.
- Profiling and memory: header wrapping, page reveal, placeholder transitions, horizontal table scrolling, retained selection when resizing.
- Shared presentation: toolbar compaction, theme propagation to floating windows, scrollbar styling, editor diagnostic rail and wrapping geometry.

## Limits

This is a focused review rather than proof that every UI path is defect-free. Validation runs on Windows; native macOS title-bar behavior requires macOS. Runtime/build lifecycle and XAML hot reload are reviewed separately. No visible UI changes were made, so this branch does not introduce a new screenshot baseline.

## Validation

Fresh build and focused existing integration regressions:

```text
dotnet test tests/Passenger.Tests --filter 'FullyQualifiedName~DockingTests|FullyQualifiedName~EditorTabsTests|FullyQualifiedName~NavigationTests|FullyQualifiedName~SettingsTests|FullyQualifiedName~ToolLayoutTests|FullyQualifiedName~TitleBarTests'
Passed: 46; failed: 0; skipped: 1; total: 47; test duration: 43 seconds.
```

The skipped case checks native macOS traffic-light positioning. Build output contained preview SDK notices and existing unused-local warnings in the PassengerCases debuggee; compilation and the test command completed successfully. The selected tests exercise actual headless windows, resize and keyboard/pointer behavior, retained editor state, and settings persistence; assertions and timing budgets were left unchanged.
