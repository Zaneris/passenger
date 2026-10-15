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
| [#218 WPF generated-file contention](https://github.com/Zaneris/passenger/issues/218) | Reproduced the exact MC1000 overlap and identified `Passenger.Host` as the generated-file holder. Selected fix `2991601` persists separate design-time intermediate directories per project/SDK/evaluated properties and owns them across processes, preserving normal Build/Run/watch outputs. Final fixtures passed 9/9; broader checks passed 21 with one expected platform skip. Reverting the four product files makes the exact final fixture fail MC1000 in 7.5 seconds. | Native platform/full-suite acceptance remains. Target-time or local `IntermediateOutputPath` overrides are explicitly unsupported with actionable reasons and recovery. Cache pruning is not implemented. The earlier graph-coordinator experiment was superseded, not shipped. |
| [#217 MAUI template inventory](https://github.com/Zaneris/passenger/issues/217) | `d94d4af` passed the original three family tests with MAUI absent and a temporary MAUI-tagged template positive-branch check. Native Windows CI then exposed truncated SDK tag-column parsing in the test expectation; `8116317` reads complete tag columns. The revised four-case class passed locally. | Recheck the corrected inventory assertion on the actual native runner; local absent/present branches do not establish its installed inventory. |
| [#216 Dialog answer order](https://github.com/Zaneris/passenger/issues/216) | Shared footer ordering now puts competing answers before Cancel on Windows/Linux and the default last on macOS. Thirteen focused Windows tests passed; dark/light scale-2 screenshots were reviewed. | Execute on macOS/Linux; the local Windows pass does not establish their native behavior. |
| [#215 Cross-platform locked restore](https://github.com/Zaneris/passenger/issues/215) | `7ca9d31` fixes the WPF inspector's shared six-RID list/lock entries; six forced locked restores and full self-contained ReadyToRun App restore graphs passed under RC1. Native CI exposed a separate Roslyn extension host/target cross-publish failure; `bbd39c1`, merged in `04e303f`, fixes the portable extension identity. The subsequent native CI packaging jobs all passed, including cold-cache macOS x64. | Final release dry run and native install/use acceptance remain separate from successful restore/publish/package construction. |
| [#214 Await-step/return-watch race](https://github.com/Zaneris/passenger/issues/214) | Diagnostics added in `3a1ef55`; no race fix claimed. The original await scenario passed 100/100 launches; a return-value boundary probe also passed. Final step/async-step/synthetic-value regressions passed 14/14. | A complete failing engine trace or deterministic other-frame return-watch/completion interleaving is still missing. The 100 clean runs did not exercise that interleaving. |
| [#213 Whole-UI check](https://github.com/Zaneris/passenger/issues/213) | Ongoing. The first pass fixed footers, plain buttons, minimum width/toolbar and Welcome. Resumed work covers responsive docking/current menus/active tab close, all 20 Settings pages, shared editor/output/list scrollbars, terminal scrollback/alternate-screen behavior (`2a4df3b`), readable empty-editor headings (`2ba036b`), and shared Chrome pages/disabled action styling (`5ce60d7`). See the precise evidence below and [surface inventory](ui-audit-213.md). | Continue unexamined window/dialog/menu/popup/tool states, consistent tool tabs, popup scale/shadows and centered rounded focus. The shared-dialog audit captures 22 types in both themes; this is not a whole-UI visual pass. Native Rider behavior was not observed; bundled defaults were inspected. Unfinished source work is not an external blocker. |
| [#211 Final MCP pass](https://github.com/Zaneris/passenger/issues/211) | Audited the existing 65 tools and tests; concrete gaps listed below. | Remaining implementation and per-feature SDK-client tests. The owner requires this final pass after other MVP work; no completed final pass is claimed. |
| [#208 Editor/server line-break drift](https://github.com/Zaneris/passenger/issues/208) | Existing `LspDocument.Unsplit` fix confirmed; two line-break regressions passed. No further change needed from the local evidence. | Full Windows/Linux/macOS CI runs must establish that the intermittent full-run drift is gone. |
| [#206 CI shards](https://github.com/Zaneris/passenger/issues/206) | `8048b8e` uses three macOS test shards beside two disk-image jobs, fitting the known five-runner limit. Windows/Linux retain four. Static matrix expansion yields 11 test jobs; all 98 measured classes per system are dealt exactly once. Existing real-suite discovery/result coverage checks still use each system's shard count. | Observe a successful CI run below 25 minutes; class-time estimates are not observed job/wall times. |
| [#57 Linux packaging/install](https://github.com/Zaneris/passenger/issues/57) | Existing scripts, launcher, install/uninstall, instructions, smoke jobs and release architectures were inspected. Native CI's Linux archive job passed at `04e303f`, and the earlier complete Linux test suite was green at `33ddf0d`. Existing Ubuntu WSL/Docker environments also support local checks. | Finish local install checks, validate both architectures and Ubuntu desktop build/run/debug/profile/terminal acceptance. Archive construction and WSL/container results must be distinguished from native desktop acceptance. |
| [#56 Release workflow](https://github.com/Zaneris/passenger/issues/56) | Existing workflow covers tests, six RIDs, fixed asset names, version/feed checks, signing hooks, checksums, tag notes and a non-publishing dry run. Packaging/release shell syntax checks passed. | The owner's required order is other MVP work and green three-system CI, then #211, then #55, then the final dry run, then an owner-chosen tag. The final dry run and tag were not performed out of sequence. Native signing/notarization was not exercised. |
| [#55 Website/README refresh](https://github.com/Zaneris/passenger/issues/55) | Deferred as the owner explicitly requires this last, after the feature/MCP passes. Existing site, README and comments were inspected. | Commit a repeatable sample and screenshot command; retake all required views in both themes; revise the whole site/README/share card against the finished feature set. The `www` redirect needs the owner's Cloudflare dashboard because the deploy token cannot write redirect rules. |
| [#33 Hot Reload](https://github.com/Zaneris/passenger/issues/33) | Managed-code tests passed 8/8 and edit tests 15/15. Avalonia literal edits (`77f0a72`, getter follow-up `e1d3855`) passed 11/11 locally; native macOS/Linux CI at `33ddf0d` also passed that checkpoint's XAML tests. WPF literal edits (`ffe2706`) passed 4/4 on Windows x64. Run/watch inspector injection/reconnection (`0078c8f`, evaluated-framework follow-up `0c82e14`) is now merged. | Saved-editor-XAML tracking and broader structural/binding/resource/template support remain active software work. The selected design-time isolation's PassengerEnC identity regression has a fix under validation; no whole-feature clean result is claimed. Native evidence for the literal checkpoint does not cover later run/watch or host changes. |
| [#19 Data breakpoints](https://github.com/Zaneris/passenger/issues/19) | Existing Windows x64 implementation and Variables/Breakpoints UI verified by all four data-breakpoint tests, none skipped. | Current engine/runtime availability is explicitly Windows x64. macOS/ARM support was neither implemented nor verified in this pass. |
| [#1 Windows verification](https://github.com/Zaneris/passenger/issues/1) | Existing CET teardown protection runs a paused thread off exception/single-step dispatch before detach; its attach/detach regression passed. The combined data-breakpoint/Hot Reload/CET audit passed 28/28 on Windows x64. | Full Windows x64/ARM64 suite and native interactive build/run/debug/profile acceptance are not established by these focused headless tests. No Windows ARM64 hardware was available. |

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
| Avalonia XAML `77f0a72` + `e1d3855` | `XamlHotReloadTests` passed 11/11 on exact RC1; debugger worktree `tests/Passenger.Tests/TestResults/xaml-reload-getters.trx`. |
| WPF XAML `ffe2706` | `WpfXamlHotReloadTests` passed 4/4 on exact RC1; debugger worktree `tests/Passenger.Tests/TestResults/wpf-xaml-first.trx`. Both-framework scale-2 before/after captures are under `artifacts/bin/Passenger.Tests/debug/{xaml-hot-reload,wpf-xaml-hot-reload}/`. |
| WPF design-time isolation `2991601` | Final nine-case fixture passed; broader 21 passed with one expected platform skip. `artifacts/wt-shell-isolated/tests/Passenger.Tests/TestResults/{isolation-final-fixtures,isolation-checkpoint}.trx`. Imported/global paths, selected references, generated navigation, cache reopen and Build/Run/watch overlap are checked. Shell worktree `artifacts/isolation-probe/tests/Passenger.Tests/TestResults/final-fixture-negative.trx` retains MC1000 with the four product files reverted; holder PID 12024 is `Passenger.Host`. |

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

### Current shared-UI audit coverage

| Owner requirement | Current evidence / continuing work |
| --- | --- |
| Centered content, shared radius/height and footer order | Shared tokens/base styles and `DialogButtonsTests.Check`; opened-dialog coverage exists, complete surface inventory continues. |
| Checked current menu values | Branch/configuration/encoding/separator helper and boundary tests/captures, `c0a648b`. |
| One tool-window tab style | Active implementation gap: panel tabs and terminal/editor-style tabs still differ. |
| Thin overlay scrollbars without arrows | Shared editor/output/list `bce47d4` and real terminal scrollback/alternate behavior `2a4df3b`; remaining individual surface review continues. |
| Light plain buttons white with a border | Shared token and both-theme rendered pixel checks, `30d3965`. |
| Popup/menu shadows; rounded centered focus, including checkboxes | Shared-style and complete capture checks remain. |
| Popup width/title/row scale and one dropdown width per column | Settings dropdown width/column alignment are centralized; remaining popup/dropdown inventory continues. |
| Disabled default button distinct from Cancel | Shared Rider-default tokens/base and primary styles; painted-state assertions plus real disabled Enter/hover and re-enable workflow, `5ce60d7`. |
| Every dialog page uses Chrome | Base Window and shared dialogPage style migrate all 7 explicit form-page wrappers found; 22-type boundary sweep checks actual paint. Full-code diff/merge surfaces have documented exceptions. Remaining surface inventory continues. |
| Footer-left Don't ask again | Shared leading-footer helper and Open Solution dark/light captures, `331540a`; complete dialog inventory continues. |
| Active editor tab close button | Modified/unmodified/pinned/hover behavior and captures, `c7a8794`. |
| Half-screen layout and preserved user layout | Toolbar/Welcome, simultaneous-side collapse and all Settings pages at 640/683 are covered by the targeted tests above; remaining tool-window/dialog states continue. |

Rider 2026.2.3.1 is installed locally. Its bundled new-UI theme defaults were inspected: control arc 8 (radius 4),
button minimum height 28 and tab height 40, among others. This is an actual source reference, not an observation of
native Rider interactions. Native comparison remains a limitation while local source faults continue to be fixed.

The resumed shipping captures live under `artifacts/ui-audit-213/2026-10-16/`: `dock-live-tree-*`, `menu-*`,
`active-modified-tab-*`, `settings-before/`, `settings-final/`, `scrollbars-verified/`, `terminal-scrollbar-verified/`,
`empty-editor/` and `dialogs-verified/`. The [surface inventory](ui-audit-213.md) identifies captured versus unexamined states. The earlier exploratory
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

`tools/test-shards.py` still chooses classes by the recorded system times, not by hand, checks the selected lists
against the real full suite before running, and checks exactly one result per test after running. The matrix change
only changes macOS's number of hands; summary coverage reads the count from each shard's metadata.

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
| Hot Reload | An IDE action can request Apply Changes. | A structured tool returning applied edits, rude edits with file/line and restart requirements; XAML support also depends on #33. |
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
