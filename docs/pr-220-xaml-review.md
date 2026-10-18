# PR #220: XAML and build follow-up review

Compared original PR head `4009385` with merge base `a5dc9928`, starting fixes after the initial review commits at `72e08ed`.

## Finding: mixed-content reordering is silently acknowledged (P2)

In `src/Passenger.Inspector/XamlUpdatePlan.cs`, structure validation compares child element names, while `Content` separately compares text values. Moving the same text across an unchanged inline child preserves both sequences. For example, `Before<Run Text=" child" />` becomes `<Run Text=" child" />Before`, but the live view retains its original ordering and the batch can still apply unrelated property changes. The accepted source then no longer describes the running view.

The shared planner serves both Avalonia and WPF. Text comparisons must retain their positions relative to object children, while still ignoring ordinary indentation and literal property elements that can be inserted independently.

A live Avalonia regression drives the real debugger/inspector, checks the compiled inline order in application output, edits the mixed content alongside a button label, and requires a restart with the button unchanged and the edit still pending.

Fixed by comparing each significant text node together with its position among non-property child elements. Literal property leaves do not shift those positions.

Validation:

- New live regression against the unchanged planner: failed as expected, `Expected: RestartNeeded`, `Actual: Applied`, after verifying compiled inline text order.
- Fixed build and focused tests: **20 passed, 0 failed, 0 skipped** (2 minutes 57 seconds). Included the new regression, preserved whitespace, literal edits with source-line movement, and Avalonia/WPF property-element insertion, future-view, rollback, and binding cases.
- Test filter: `Moving_text_across_an_inline_requires_restart_without_applying_the_batch|Preserved_text_whitespace_changes|Literal_xaml_edits_change|property_element`.
- `git diff --check` passed. Parent agent independently reviewed the patch without change requests.

The mixed-content regression itself exercises Avalonia; WPF uses the same planner source and its adjacent property-element tests passed. No full repository suite or cross-platform run was performed.

## Other review coverage

Inspected the new/changed XAML paths in `Passenger.Host/DesignTimeBuilds.cs`, `WpfBuildPreparation.cs`, and `Stamps.cs`; `Passenger.App/Run/WpfPopulationBuild.cs`, `WpfPopulation.targets`, and `RunXamlHotReload.cs`; `Passenger.App/LiveTree/LiveApp.cs`, `ProjectSession.cs`, and `Debugging/HotReload.cs`; the Avalonia planner, hot reload and population paths; WPF hot reload, population, compiled-loader recognition and uncreated-view validation; and `Passenger.Roslyn.Extension/HotReload.cs`.

No additional substantiated defect was recorded in those paths. This is focused source review and regression coverage, not a claim that every build graph, runtime version, or XAML shape has been executed. The initial Stop-during-preparation and significant-whitespace findings remain documented in `pr-220-review.md`.
