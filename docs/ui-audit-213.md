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
| FeedDialog | 520, new source form; `3fee501` adds long-heading wrap with real edit/cancel input | Inspected both themes; complete narrow/checkbox-state inventory remains. |
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
| FloatWindow / editor window | `16e843e`: five real tool consumers in 240px floating windows and docked 200px/bottom panes, both themes | Owner theme changes, close/unsubscribe/reopen and tab focus/state through reparenting checked; genuinely Light final floating frames inspected for all five. Detached-editor/windowed and full body inventory remains. |

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
pane, both themes. The body toolbar fits there; the shared header's Sources tab was subsequently repaired by
`16e843e`, and `1339d80` ellipsizes the selected feed with its existing complete tooltip.

`16e843e` checks all five header-tool consumers (NuGet, Local History, Git, Terminal, Explorer) through repeated
640/683/1200 resizing, 200px side placement, bottom placement and 240px floating windows. Fixed Options/Hide actions
remain usable while tabs move to a second scrolling row. Real wheel/pointer/keyboard selection, active/inactive
tab close, selected identity and focus through moving/floating are checked. The rounded keyboard ring cannot
intercept the actual scrollbar thumb; pointer hit testing and dragging are asserted. Floating tools follow the
owner's actual light/dark theme and remove the subscription when closed. The 72-case regression run passes 71,
with one expected Windows credential-provider skip, zero failures in 2m39s; build 0 warnings/errors.
`shared-header-final/` contains 32 scale-2 header frames; twelve were inspected, including all five Light floating
tools, both focus rings and representative bottom/side frames. Sources/History/Git body overflow is separate.

`fc974f5` follows a native Linux reproduction of the CI missing-shell diagnostic failure, retaining a failed tab
without allocating a PTY for a missing explicit executable path. Clean exact committed Linux x64 source passes
all 23 actual terminal lifecycle/input/tab cases in 46s. Windows reports 23 passes in 23s, including 13 old Unix-only
bodies that return early there. All six two-theme Windows/Linux missing-path frames were inspected; native evidence
is shipping `artifacts/2026-10-16/terminal-native/`, separate from the Windows `terminal-path-final/` capture folder.
The valid relative script in a spaced folder still receives real keys and exits 2 once. Later complete `c9509e4`
and Linux ARM `47f7a57` runs clear that missing-path case; newer terminal-overflow coverage is separate.

`4fff99c` follows the profiler findings with real completed CPU data and a real held-40-object heap snapshot.
The shell worktree's `artifacts/profiler-populated/captures-final/` covers readable empty-Memory guidance and long
Call Tree/Hot Spots/allocation/type rows at 1200/640 in both themes (683 also captured). Numeric alignment,
virtualization, selected visibility on resize and reading offset after manual scrolling are checked. Profiler,
Memory Snapshots and Tool Header cases pass 14/14 in 1m24s, zero skips; build 0 warnings/errors. Profiler busy/diff/frame
states remain unexamined here.

Native Ubuntu x64 exact `152062a` subsequently passes all 16 actual Profiler/Memory Snapshots/Tool Header cases in
2m55s, zero skips. Shell `artifacts/profiler-populated/native-profiler.log` and
`native-linux/{profiler-memory-linux.trx,captures/}` retain the result. The apparent ARM Welcome button-paint
discrepancy was a visual-review error: original Windows/Linux border/fill/focus pixels match, and the exact native
Linux package reproduces its original PNG byte-for-byte. `artifacts/welcome-arm/paint-pixels.json` is the evidence;
this alleged fault is removed from the work queue, without a product change.

`1339d80` fits Problems' actual missing-baseline Hot Reload summary/group labels with ellipsis and full-value
tooltips. The existing real debug/restart flow checks actual text and clipping ancestors across 1200/683/640/1200;
the real local-feed test checks the selected feed. Four cases pass in 24s, and existing Problems/Package Sources
pass seven with one expected Windows credential-provider skip in 22s. Build 0 errors/seven known fixture warnings.
Eight `long-labels/` normal/narrow two-theme frames were inspected. Ordinary problem message/detail clipping was
then reproduced and repaired separately by `6b91997`, below.

`6d1752c` makes Terminal's own many-session strip scroll using the shared tool-tab style. A long-named actual
Run-in-Terminal program and seven real shells exercise pointer/wheel/Tab/Space selection, actual command input,
close-button visibility and background-run process exit at 1200/683/640 and in a 200px side pane. A selected tab
is revealed on resize; while another tab has keyboard focus, that focus remains visible without selecting it.
The earlier selected-only reveal fails the focused-inactive-tab viewport assertion in both themes. Final product
passes eight relevant Tool Tab/Lifecycle/shared-header cases in 1m16s, zero skips; final test-only actual-Tab-key
focus setup passes both affected cases in 24s. Builds have zero warnings/errors. Eight geometry frames in
`terminal-overflow-final/`, four focused-resize frames in `terminal-overflow-focus-final/` and two actual-keyboard
640px focus-ring frames in `terminal-keyboard-final/` were inspected separately. The latter folders contain more
captures than those inspected counts. Native Unix execution of this overflow addition remains pending.

`6d93582` follows actual Tab input into an inactive tab's close child: the old style leaves its focused glyph at
opacity zero. The shared rule now paints that action and a rounded accent ring without intercepting pointer hits.
Actual Shift-Tab/Tab/Enter closes only that background shell, preserves the selected run and checks its saved PID.
Tool Tab/Editor Tabs pass 10/10 in 36s, zero skips; build 0 warnings/errors. All four final 640/1200 scale-2 frames in
`close-action-final/` were inspected in both themes. Root's merged final two Terminal cases also pass in 23s.

`ab704c8` replaces the square two-tone default adorner with a rounded accent outline that cannot intercept input.
Checkbox focus follows its actual glyph instead of surrounding the label, following the installed Rider focused
checkbox icon reference. Real Tab/Space and pointer input check checked/unchecked states, disabled focus removal,
the primary button's visible ring and the unclipped left outline's actual painted pixel. A real Settings option
wraps at its default font size at 640px; glyph alignment and keyboard focus survive 640/980/683/640 resizing and
the change between inline/separate labels. Dialog Buttons/Settings/Tool Tab/Tool Header pass 21/21 in 1m39s, zero
skips; build 0 warnings/errors. All fourteen final `checkbox-focus-verified/` scale-2 frames were inspected in both
themes. Existing tab-specific adorners retain their overrides. The 21-case binary predates only the separate
`4ec6143` Profiler diagnostic test refinement; exact native pre-capture header validation remains separate.
Test-only `d09935c` then verifies the focused primary label's rendered ink: both themes retain 663 measured
foreground pixels before/after keyboard focus, while a deliberately opaque adornment control hides all of them.
Restoring the original outline restores the ink; no actual label-loss product fault or background fix is claimed.
The final two real dialog cases pass in 1s, zero skips, with build 0 warnings/errors. Both final primary-focus frames
in `checkbox-focus-original-paint/` were inspected; this does not replace the fourteen original focus-state frames.

`6b91997` constrains actual Problems messages, advisory links and location/code labels to the available row.
Full messages/locations remain on hover, and Copy Message retains the untrimmed text. The old layout reproduces
an out-of-list location even at 1200px. Actual missing-baseline Hot Reload and local-feed vulnerability flows now
check 1200/683/640 resizing, every label's bounds/text layout and a real advisory-link click at 640px. All eight
Hot Reload/Project Load/Problems cases pass in 40s, zero skips; build 0 warnings/errors. Twelve final scale-2 frames
are in `problem-rows-final/`; all eight 640/1200 whole frames were inspected in both themes. This is message-row
coverage, not a completed Problems tab-strip/populated-state audit. The later category checkpoint follows below.

`892e0ef` uses the shared scrolling tool-tab presentation for Problems' own categories, caps long filename labels
to the actual strip viewport and retains full-name tooltips. A real long-named erroneous file, whole-solution
diagnostics and compiler errors exercise every visible File/Solution/Toolset/Build/Bindings category with actual
wheel/pointer/Tab/Space input in side200 and bottom640/683/1200 layouts. Keyboard-focused inactive categories
remain visible through resize without switching the selected page. The real missing-baseline Hot Reload flow
also checks its dynamically shown last category at side200. Final ten cases pass in one minute, zero skips;
build 0 warnings/errors. The old source fails both corrected long-file geometry cases in eight seconds and both
actual Hot Reload side-category checks. `problem-tabs-frozen/` holds eighty scale-2 frames; ten were personally
inspected across normal/narrow/focus/Hot Reload states in both themes. These exposed separate long editor-tab
close clipping and status-bar overlap at 640/683, repaired by the following scoped checkpoints.

`b3b3458` caps editor-tab filenames to the actual group viewport while keeping modified/pin/close actions usable.
Real long modified/pinned files, split/unsplit groups and inactive close focus survive 1200/900/683/640 resize
without losing editor text, caret or selection. Existing Editor Tabs/Active Editor checks pass 17/17 in 16s;
the final keyboard refinement passes both affected cases in 5s, zero skips. Old source fails both geometry checks
even at 1200px. Eight of 26 final scale-2 `editor-long-tab-frozen/` frames were inspected in both themes.

`82c4ba7` gives status breadcrumbs a bounded scrolling viewport and full-path tooltips, reserving message and
storage-action space. First/reopened plain root files with no outline stay readable, including the initially
hidden viewport. Actual pointer/wheel/Tab input reaches folder/file crumbs without switching the editor; the
640px separator menu changes saved LF to CRLF. Combined File Properties/outline/Problems/long-editor checks pass
16/16 in 55s, zero skips; build 0 warnings/errors. Old source fails both real separator clicks because the path
intercepts them. Nine final `status-path-frozen/` full frames were inspected in both themes, including actual
compiler-error status. Optional status widgets and remaining popup states are not covered by that count.

Later Profiler checkpoints add real populated subviews, with both-theme normal/narrow captures inspected by the
shell agent. `412d956` covers actual 40→47 held-object comparison/object/root-path navigation, keyboard actions,
breadcrumb wheel/back navigation and finite viewports: Windows 16/16 in 2m12s, native heap 2/2 in 27s. `7e1fb80`
covers long-named slow-control Frames, aligned metrics, two-axis scrolling, focus/selection and source jump/reopen:
Windows 14/14 in 1m47s, native 2/2 in 39s. `10d729e` fits full metadata and actual 200,000 new/one-million surviving
objects at 1200/683/640: Windows 11/11 in 1m17s, native 2/2 in 23s. Busy captures are observations, not fixed-duration
acceptance conditions; native Light comparison completed during that optional capture. `fe54628` fits no-frame
guidance at 640 after two old-source failures; both Windows cases pass in 18s, with no separate native repeat for
the one-line margin. Shell artifact folders `profiler-memory-navigation/`, `profiler-frames/`, `profiler-million/`
and `profiler-empty-frames/` retain positive/negative logs and final/native captures. These are scoped flows, not
all Profiler or Rider behavior.

`af1720b` fits the populated NuGet Sources form: actual names, full configuration paths, properties and summary
wrap, with Edit/Delete/enable controls still reachable. Both-theme 1200/683/640 and side200 flows use real keyboard
configuration selection, two-axis wheel input and Edit/cancel. Windows passes six with one existing credential-
provider skip in 17s; native class passes 7/7 in 81s, then exact final side-pane cases pass 2/2 in 11s. Builds have
zero warnings/errors; the old source fails both truncation controls. Shell `artifacts/nuget-sources/` retains
inspected normal/narrow frames, `tests-final.log`, `native-class.log`, `native-side-final.log` and native TRX files.

The complete native CI run at `47f7a57` retains 948 results per OS with no missing or duplicate tests. Windows and
Linux pass; macOS fails two Profiler fixture cases and both Profiler header cases. The latter fail the combined
Snapshots selection/visibility check on the 683px resize; `77d9f2d` separates selection from exact viewport/offset geometry in a focused native
rerun. Earlier local/Linux header passes are historical evidence, not proof that this macOS regression is fixed.
The focused native macOS run `38829620638` at `cee6db2` passes all four actual Header/Profiler fixture cases,
zero skips, in about 1m15s. All six normal/narrow Snapshots frames and both 640px running frames were inspected
in both themes. The canonical fixture failure is cleared, but no header source fix is claimed. Because audit
capture triggers rendering before the assertions in `77d9f2d`, `4ec6143` now records the exact selection,
visibility, checked pages, focus and geometry before capture. Complete `c9509e4` uses the earlier assertion order.
Evidence is root
`artifacts/milestone-1/integration/focused-38829620638/macOS/`.

The exact pre-capture native run `38831479112` at `fdb008b` passes all four selected/reported cases on macOS ARM64,
zero skips, in 81s. Snapshots 683 Dark/1200 Light and running 640 Light/1200 Dark final frames were inspected;
headers/actions fit. This excludes the capture-order concern for that passing checkpoint without claiming a
production header fix or explaining the original intermittent failure. Root evidence is
`artifacts/milestone-1/integration/focused-38831479112/macOS/`. Complete `c9509e4` then reports 951 passing/skipped
results exactly once per OS and zero failed tests, with a budget-only macOS shard failure and separate ARM image
detach failure. Newer source still requires native CI; see the timing and case-growth evidence in MVP progress.

| Tool surfaces | Current captured/tested state | Continuing source or capture work |
| --- | --- | --- |
| Hierarchy, Git, Unit Tests | Empty guidance fits its own 640px-window pane; both themes inspected. | Populated hierarchy/history/test views and responsive controls. |
| Profiler | Responsive header plus actual CPU/heap data, long rows and guidance; Memory navigation/comparison and populated Frames now have actual Windows/Linux input/capture checks. macOS pre-capture Header/Profiler rerun passes four cases. | Remaining unexamined snapshot/error/busy interactions and full state inventory; no blanket Profiler/Rider pass or original intermittent header cause claimed. |
| Find, Structure, Bookmarks, Local History, Problems, Debug | Shared empty hints fit normal/narrow viewports. Problems adds actual compiler/solution/Hot Reload rows and reachable categories in side200/bottom panes, both themes. | Other populated tool rows, narrow action bars, keyboard focus and selection through resize. |
| Terminal | Real-shell normal/narrow frames both themes inspected; `fc974f5` executes 23 actual terminal cases on Linux x64, with later macOS/Linux ARM missing-path acceptance. `6d1752c` adds many-session/long-name overflow and keyboard-focus resizing on Windows, with actual command routing and run exit. | Native execution of the newer overflow coverage and complete remaining focus inventory. |
| Explorer, Run, Build, Commit, Live Visual Tree | Captured at 1200/683 both themes in the 17-tool sweep. Earlier focused docking, console/editor scrollbar and empty-editor evidence remains. | Complete populated-state visual/input inventory; capture existence alone is not a pass. |
| NuGet | Real local-feed toolbar/header/feed ellipsis, populated Sources configuration paths and `d702d41` package metadata/install/update/remove fit at 1200/683/640 and side200 with actual Windows/Linux input and both-theme captures. Explicit version choices survive only same-identity refresh while offered; automatic feed defaults remain live. | Remaining error/detail/source-credential states. Native package tests report an ancillary missing WindowsDesktop reference pack; no clean code-help claim. |
| Profiler Memory/Frames subviews | Actual held-object comparison/object/root-path flows, million-object metadata and long Frames metrics/scrolling/source navigation have scoped Windows/Linux tests and both-theme captures. | Remaining error/cancellation/busy transitions and unexamined snapshot states; scoped evidence is listed above. |

Long editor tabs and status paths now have scoped fixes above. Populated History/Git forms, optional status widgets
and remaining popup/focus rules continue; the current native macOS Self Debugging failure takes priority. The capture
inventory is updated as those checks run; it is not a stopping condition.

## Reference and execution limits

Installed Rider 2026.2.3.1's `lib/intellij.platform.ide.impl.jar` contains
`themes/expUI/expUI_{dark,light}.theme.json`. Its actual button/control defaults supply the shared radius/height
and new disabled tokens: dark Gray6/Gray2/Gray4, light Gray8/Gray14/Gray11. This is a bundled source reference;
native Rider interactions were not observed. The desktop is now locked, so unlocked-native comparison remains
unavailable; headless compositor input/capture and source fixes continue. No locally feasible implementation is
classified as an external blocker.
