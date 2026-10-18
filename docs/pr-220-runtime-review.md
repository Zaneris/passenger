# PR #220 runtime lifecycle review

Reviewed original PR head `4009385` against merge base `a5dc9928`, with the initial review fixes at `72e08ed` present.

## Fixed: P1 - Start every terminal shutdown before awaiting native teardown

PR #220 makes `TerminalSession.DisposeAsync` asynchronous. `TerminalPanel.CloseAllAsync` still awaited each tab in sequence. Closing a window therefore stopped only its first terminal before returning to the caller. Later terminals depended on another UI continuation, even though the last window's dispatcher may have stopped. The same continuation eventually called `tabs.Clear()`, discarding a replacement terminal opened while the old sessions were closing, leaving that real shell running without its tab.

`CloseAllAsync` now detaches a snapshot of the old tabs, initiates every stop/disposal before its first await, and awaits their combined completion. Old exit callbacks cannot remove replacement tabs, and completion never clears newly opened sessions.

Two real-shell regressions in `TerminalLifecycleTests` demonstrate both boundaries:

- Closing a window with three shells must stop all three without pumping the UI after Close returns. Before the fix this failed because the second shell was still running after five seconds.
- Starting a replacement shell during old-terminal teardown must preserve its tab, active selection, view session and running process. Before the fix this failed because the tabs collection was empty.

Both failed against the original implementation before the fix; their waits and assertions were retained unchanged.

## Coverage and limitations

Reviewed runtime-related changes in `Passenger.Dap/DapConnection.cs`, `Passenger.DebugAdapter/DebugAdapter.cs`, debugger startup/diagnostic helpers and managed-debugger changes, `Passenger.Terminal/TerminalSession.cs` and `TerminalView.cs`, `Passenger.App/Run/BuildRunController.cs`, `Passenger.App/Debugging/DebugController.cs`, plus terminal panel and window/session shutdown callers. No additional substantiated introduced defect was identified in these areas.

Validation on Windows x64:

- `dotnet build tests/Passenger.Tests/Passenger.Tests.csproj --no-restore --nologo`: succeeded, 0 warnings/errors.
- Negative control: both added terminal regressions failed before the production fix, for the consequences documented above.
- `dotnet test tests/Passenger.Tests/Passenger.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~TerminalLifecycleTests|FullyQualifiedName~RuntimeStartupTests|FullyQualifiedName~TerminalScrollBarTests"`: 11 passed, 0 failed, 0 skipped (20 seconds). Includes existing real-terminal immediate-close/startup, descendant-process cleanup, runtime cancellation/handshake and scrollback checks.
- No timeout or existing assertion was weakened.

This review does not claim a complete debugger suite or validation on macOS/Linux.
