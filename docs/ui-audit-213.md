# Continuing UI audit for #213

Evidence date: 2026-10-16 (Indian/Mauritius). This inventory tracks the
[owner's shared UI requirements](https://github.com/Zaneris/passenger/issues/213#issuecomment-6176439816).
It is a work queue, not a whole-UI pass. Source checkpoints `5ce60d7`, `6ef4205` and `0c4e149` cover shared
Chrome/disabled actions, Terminal tool tabs/lifecycle and wrapping tool hints respectively;
earlier source checkpoints and full test commands are in [MVP progress](mvp-progress.md).

## Evidence and rules

The shipping worktree retains compositor PNGs under `artifacts/ui-audit-213/2026-10-16/`.
`settings-final/` contains the earlier 20-page, normal/narrow/two-theme sweep: all 92 frames were inspected.
`scrollbars-verified/`, `terminal-scrollbar-verified/` and `empty-editor/` contain the later focused checks.

`dialogs-verified/` contains 124 scale-2 captures from real dialog workflows across 22 types. Every capture runs
`DialogButtonsTests.Check` rules for centered button content, common height/radius, plain light-button paint,
footer order, Chrome pages, disabled primary paint and transparent disabled toolbar buttons. Audit mode repeats
these checks in both themes and restores the dialog's requested theme. Captures include repeated states; the number
of PNGs is not the number of independently reviewed surfaces. The table records the 30 frames actually inspected
for this source checkpoint, separately from automated assertions/capture existence.

The ten-class run passed 48, skipped one expected Windows-inapplicable credential-provider case, and failed none in
2m29s. Build: zero warnings/errors. Logs and TRX are in shipping `artifacts/2026-10-16/`:
`build-213-dialog-verified.log`, `tests-213-dialog-verified.log`,
`test-results-213-dialog-verified/ahert_CORSAIR13900_2026-10-16_01_50_27_net11.0.trx`.

## Dialog/window inventory

All captured widths below have both Dark and Light frames. A captured default/minimum width does not establish
all text lengths, all pages, a populated form, keyboard focus or accessibility behavior.

| Surface | Captured widths/state in `dialogs-verified/` | Inspected here / next checkpoint |
| --- | --- | --- |
| AboutDialog | 460 | Captured; visual review pending. |
| AddReferenceDialog | 520, actual project reference workflow | Captured; narrow and visual review pending. |
| AttachToProcessDialog | 900, 560, live process list | Captured; final visual/filter/selection and narrow review pending. |
| AvailabilityDialog | 520, 420, generation template | Inspected 420 both themes; page and footer fit. Keyboard checkbox focus remains. |
| ChangeSignatureDialog | 720, populated parameter table/preview | Inspected both themes; narrow and long names remain. |
| ChoiceDialog | 480, real file/window choices | Captured; complete choice/footer/long-text review pending. |
| ChooseMacroDialog | 460, no-macro selection | Inspected both themes; minimum width and keyboard focus remain. |
| CloneDialog | 560, empty clone form | Captured; narrow, errors and visual review pending. |
| ConfirmDialog | 420, real refactoring/keymap questions | Captured; visual and long-text/footer review pending. |
| ConflictsDialog | 760, real Git conflict | Captured; narrow and visual review pending. |
| ExtractInterfaceDialog | 560, checked members | Inspected both themes; minimum width and long names remain. |
| FeedDialog | 520, new source form | Inspected both themes; narrow and keyboard checkbox focus remain. |
| FileNestingDialog | 720, enabled/disabled rules | Enabled frame inspected both themes; minimum width and scroll ends remain. |
| KeyboardShortcutDialog | 460, empty/assigned/conflicting strokes | Empty disabled-OK frame inspected both themes; other states/long action names remain. |
| MembersMoveDialog | 560, pull-up/push-down | Disabled Next inspected both themes; long names and minimum width remain. |
| MergeWindow | 1300, real three-way merge | Captured; minimum width and visual review pending. Full code surface intentionally uses editor background. |
| MoveTypeDialog | 480, invalid namespace and target file | Both states inspected in both themes; narrow/long error text remains. |
| NewProjectDialog | 920, 720, Console; no-match disables Create, clearing enables it | Enabled/disabled and minimum Console frames inspected both themes. Other families, Type options, shared dropdown widths and install/error states remain. |
| OpenProjectDialog | 560, real open-over-folder question | Captured; October 15 footer-leading evidence also exists. Current whole-frame review remains. |
| RunConfigurationsDialog | 980, 700, empty | Empty minimum inspected both themes; populated environment/profile pages and shared dropdown widths remain. |
| SettingsDialog | 980, Appearance, Keymap, Live Templates in this run | Appearance inspected both themes. Earlier `626f3fe` checks all 20 pages at 980/640/683; current new Chrome paint checked by shared rules. |
| TemplateVariablesDialog | 620, two variables/no macro | Inspected both themes; minimum width/parameter form/focus remain. |
| ExtractResourceDialog, ExtractStyleDialog | Not in this capture sweep | Add existing XAML refactoring flows to the shared rules/capture inventory. |
| ChangesWindow, DiffWindow | Not in this capture sweep | Open actual revision/diff workflows at default/minimum widths. Diff is intentionally a full code surface. |
| FrameViewer | Not in this capture sweep | Open a real captured test frame; inspect zoom controls, navigation and narrow layout. Its canvas intentionally uses editor background. |
| FloatWindow / editor window | Prior docking/editor tests, not this sweep | Explicit floating/windowed tool and detached-editor inventory remains. |

## Popup/menu inventory

The current-value menus have focused source/tests and both-theme evidence (`c0a648b`), but this is not a full
menu style/focus/shadow pass. The following concrete popups remain to capture at default and narrow window widths
in both themes, with actual text/input and dismissal/focus restoration:

| Surface | Existing source/entry point | Remaining shared checks |
| --- | --- | --- |
| GoToFilePopup, GoToLinePopup | Navigation actions | Width/title/row scale, shadow, focus, keyboard navigation. |
| ActionPopup, SymbolPopup | Find Action / symbol navigation | Same shared rules, long labels and shortcuts. |
| RecentLocationsPopup | Recent Locations | Preview has no scrollbar/stripe rail after `bce47d4`; popup layout/focus/shadow still to audit. |
| FindInFilesPopup | Find in Files | Options, widths, scoped fields, narrow layout. |
| CompletionPopup, CSharp.ChoicePopup | Actual completion/refactoring choices | Assist-layer shadows/rows/widths, keyboard selection and editor focus. |
| BreakpointPopup, BreakpointsWindow | Breakpoint properties / View Breakpoints | Checkbox focus, conditions, default/narrow geometry. |
| ErrorsPopup | IDE error display | Long error details, scale/scrolling, dismissal/focus. |
| Vcs.NamePopup, Vcs.ChoicePopup, Vcs.UnstashPopup | Branch/tag/stash actions | Shared popup rules, invalid/disabled actions and narrow layout. |
| Vcs.ChangePopup | Gutter change action | Code preview exception, action rows, shadow/focus. |
| ContextMenu / submenu / dropdown | Main menu, tree/editor/terminal/status contexts | Shared shadow and row scale; checked current-value behavior exists. Encoding CLI named-click discrepancy still needs diagnosis. |
| Tooltips, editor hover/signature cards | Real pointer/keyboard flows | Wrap/width/scale/focus where interactive; no blanket pass claimed. |

## Tool-window inventory

Registered tools are Explorer, Structure, Run, Debug, Unit Tests, Build, Problems, Bookmarks, Live Visual Tree,
Find, Hierarchy, Local History, NuGet, Terminal, Commit, Git and Performance Profiler. Memory is a profiler subview.
All need an explicit default/narrow/two-theme state entry as the sweep proceeds; existing functional tests do not
substitute for that inventory.

`6ef4205` changes Terminal's old editor-style tabs to the existing shared `ToggleButton.panelTab` style. Actual Windows cmd
flows check selected-state stability, active/inactive close bubbling, tab order, focus/keys and kept reading position.
The final Windows combined run reports 25 passes in 25s; 13 older Unix-only bodies return early, so 12 cases execute
here. Real Run-in-Terminal closure, Stop/Dispose/CloseAll, natural exit code 3 and an inherited-output child flood
also check actual PIDs, completion and exactly one exit event. Old immediate disposal fails the immediate-shutdown
regression with a missing event. Native waiting/disposal happens off the UI thread while the reader drains closing
output. The final `tool-tabs-real-output/` folder has 68 scale-2 frames: all 17 registered tools at 1200/683 in both
themes. Four final Terminal frames were inspected; earlier exploratory `tool-tabs-fixed/` frames helped identify
responsive faults but used injected terminal history and are not the final terminal visual evidence.

`0c4e149` adds one shared wrapping style for ten empty tool hints. `tool-hints-verified/` has 80 frames from
1200→683→640→1200 in both themes, with requested side-pane widths of 200px. The actual hint TextLayout and each
ancestor viewport up to its tool are asserted. Tool Layout/Problems/Local History classes pass 12/12 in 31s;
RC1 build has zero warnings/errors. Twenty-four representative whole frames were inspected: all ten migrated
hints in both themes, with normal and narrow widths represented. This does not claim all 80 frames were viewed.

`7b28e51` fits the Profiler's tabs/actions/status at 640/683/1200. Its real profiled program exercises all eight tabs
with wheel/click and Tab/Shift-Tab/Space, checking one selected page, focus and action bounds through resize.
The combined Tool Header/Profiler/Tool Layout run passes 8/8 in 43s, zero skips; build 0 warnings/errors.
`profiler-header-final/` contains 54 profiler frames (alongside 80 hint frames). Eight final running/Snapshots frames
at normal/narrow widths were inspected in both themes, plus 14 earlier same-source page frames. Populated-page
inspection found stale empty-Memory guidance after completion and inaccessible long method/type rows; these are
continuing source work, not a whole-profiler pass.

`2952092` wraps NuGet's existing search/feed/options/refresh/status controls. A real local package is searched before
an absent query checks long status text; values, caret and focus survive resize. New cases pass 2/2, and the
combined Package Sources/new run passes seven with one expected Windows credential-provider skip in 15s. Build
0 warnings/errors. All 14 `nuget-header-status/` scale-2 frames were inspected at 1200/683/640 and in a 200px side
pane, both themes. The body toolbar fits there; the shared header's Sources tab and abrupt long feed-label clipping
remain visible, actionable follow-ups.

| Tool surfaces | Current captured/tested state | Continuing source or capture work |
| --- | --- | --- |
| Hierarchy, Git, Unit Tests | Empty guidance fits its own 640px-window pane; both themes inspected. | Populated hierarchy/history/test views and responsive controls. |
| Profiler | Responsive header, actual running program and all eight snapshot pages at 640/683/1200, both themes; pointer/keyboard tab access and resize state checked. | Empty Memory guidance, long populated rows and further Memory/Frames state inventory. |
| Find, Structure, Bookmarks, Local History, Problems, Debug | Shared empty hint geometry at normal/narrow widths; both themes inspected across representative frames. Existing Problems/Local History workflows also pass. | Populated tool rows, long tab names, narrow action bars, keyboard focus and selection through resize. |
| Terminal | Final real-shell normal/narrow frames both themes inspected; lifecycle and command input exercise real Windows processes. | Unix execution of the new cases, long/many-session tab overflow and complete focus inventory. |
| Explorer, Run, Build, Commit, Live Visual Tree | Captured at 1200/683 both themes in the 17-tool sweep. Earlier focused docking, console/editor scrollbar and empty-editor evidence remains. | Complete populated-state visual/input inventory; capture existence alone is not a pass. |
| NuGet | Real local-feed search/refresh toolbar at 1200/683/640 and 200px side pane; both themes inspected, full count/trimmed status and saved input state checked. | Shared header's Sources tab and long feed-label clipping; Sources/populated package/error/detail states remain. |
| Profiler Memory/Frames subviews | Empty subviews captured during the actual sampling-snapshot sweep; stale Memory guidance identified. | Actual heap/frame snapshots, subtab overflow, empty/busy/result states, toolbars and viewport checks. |

Shared tool-header/session overflow, popup/focus rules and populated forms remain the next source checkpoints. The capture
inventory is updated as those checks run; it is not a stopping condition.

## Reference and execution limits

Installed Rider 2026.2.3.1's `lib/intellij.platform.ide.impl.jar` contains
`themes/expUI/expUI_{dark,light}.theme.json`. Its actual button/control defaults supply the shared radius/height
and new disabled tokens: dark Gray6/Gray2/Gray4, light Gray8/Gray14/Gray11. This is a bundled source reference;
native Rider interactions were not observed. The desktop is now locked, so unlocked-native comparison remains
unavailable; headless compositor input/capture and source fixes continue. No locally feasible implementation is
classified as an external blocker.
