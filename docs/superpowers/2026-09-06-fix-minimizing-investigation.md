# Fix-Minimizing Investigation — Session Summary

**Date:** 2026-09-06
**Branch:** `duaL-Astriel/fix-minimizing`
**Reported bug:** The plugin is supposed to handle minimizing of Dalamud windows, but the
minimize button behaves like the default one (windows are not hidden into the Umbra toolbar).

Method: `superpowers:systematic-debugging` (root-cause-first). Investigation is still in the
evidence-gathering phase — **no fix has been applied yet.** Only temporary diagnostic instrumentation
was added.

---

## What the code is supposed to do

- `DalamudWindowTracker` (`[OnTick 2000ms]` + ctor) discovers plugin `WindowSystem`s via reflection
  over `PluginManager.InstalledPlugins → LocalPlugin.instance`, scanning each plugin instance's own
  (and base-class) `WindowSystem` fields/properties. For each window it calls
  `WindowManagerService.RegisterWindow` and `InjectMinimizeButton`.
- `InjectMinimizeButton` adds a `TitleBarButton` (`FontAwesomeIcon.WindowMinimize`, priority
  `int.MaxValue-1`) whose `Click` calls `service.Minimize(tracked)`. Idempotent per **window instance**
  via a `ConditionalWeakTable`.
- `WindowManagerService.Minimize` → for a dock group calls `group.Minimize()`, otherwise sets
  `tracked.IsMinimized = true; tracked.IsOpen = false`. `TrackedWindow.IsOpen` setter writes through
  the live `IWindow` via a `WeakReference`.
- `ImGuiContextMonitor` (`[OnDraw executionOrder:10]`) walks `ImGui.GetCurrentContext().Windows`,
  intercepts native collapse (`win.Collapsed` → reset + `Minimize`), and tracks dock groups. It only
  acts on windows already present in the tracked map.
- `WindowManagerWidget` renders toolbar entries and toggles state on click. It never writes
  `IsOpen = true` except in user-triggered `Restore`.

---

## What was verified (static analysis + decompiled Dalamud/Umbra)

Decompiler used: `ilspycmd` (installed globally) against the live
`%AppData%\XIVLauncher\addon\Hooks\dev\Dalamud*.dll` and
`%AppData%\XIVLauncher\installedPlugins\Umbra\3.1.18.0\Umbra*.dll`.

1. **Project builds cleanly** against the real Dalamud/Umbra assemblies (Debug).
2. **The injected button renders and fires.** `WindowHost.DrawTitleBarButtons` draws every entry in
   `Window.TitleBarButtons` (when the window has a title bar) and invokes `Click(ImGuiMouseButton.Left)`.
   Unit test `InjectMinimizeButton_AddsButtonOnceAndBindsClick` confirms the wiring
   (`IsMinimized=true`, `IsOpen=false`).
3. **`IsOpen=false` provably hides a window.** `WindowHost.DrawInternal` line ~188:
   `if (!Window.IsOpen) { … return; }` — the window is not drawn. `WindowSystem.Windows` returns the
   exact `IWindow` instances that are drawn, so the tracked instance == the drawn instance in the
   normal single-window path.
4. **Umbra registers `[OnDraw]`/`[OnTick]` for custom plugins.** `PluginManager.LoadCustomPlugins`
   (`[WhenFrameworkCompiling(-2147483647)]`) adds the plugin assembly to `Framework.Assemblies`
   *before* `Scheduler.Start()` scans it; hot-reload re-runs `Framework.Restart()`. So the monitor and
   tracker do run.
5. **Reflection targets exist** in the current Dalamud (`PluginManager`, `Service`1.instanceTcs`,
   `LocalPlugin.instance`, `Manifest.InternalName`, `DllFile`).
6. **The plugin never re-opens windows itself** — `IsOpen=true` only appears in `Restore` /
   `DockGroup.Restore`, both user-triggered.
7. **`Logger.Info` output** goes to `dalamud.log` via Umbra's `DefaultLogTarget`
   (chat output is `[Conditional("DEBUG")]`, and the installed Umbra is Release, so it won't spam chat).

Conclusion of static analysis: for a discovered/tracked window, the minimize path *should* work. The
failure is runtime-only and not reproducible outside the game.

---

## User-reported observations (evolving — record faithfully)

Collected via clarifying questions during the session:

| Round | Observation |
|---|---|
| 1 | Injected minimize button appears **only for docked/grouped windows**; minimize does **nothing at all**; affects **several plugins**. |
| 2 | In the toolbar widget, **all/most open windows appear** as entries; **even windows that have the button do not hide** when clicked. |
| 3 (latest, interrupt) | **"No button visible still even in grouped windows."** |

Note: rounds 1 and 3 conflict about whether the button ever appears on grouped windows. This needs to
be re-confirmed with the diagnostic build. Round 2 established that discovery/tracking works (toolbar
is populated) and that the **minimize mechanism itself is the failing part**.

---

## Remaining hypotheses (to be discriminated by the diagnostic build)

Given tracking works but clicking never hides the window, a *universal* failure must be one of:

1. **Click never reaches `Minimize`** (injected button not invoking our handler in-game).
2. **Instance mismatch** — we set `IsOpen=false` on a different `IWindow` instance than the one Dalamud
   draws.
3. **External re-open** — something outside our plugin sets `IsOpen=true` each frame.

The newest "no button visible at all" report may reopen a fourth possibility: the button injection /
title-bar rendering path is not producing a visible button in-game (e.g., discovery is registering
windows through a path that does not carry a title bar, or injection is not happening for the windows
being tested).

---

## Instrumentation added (TEMPORARY — must be reverted before any real fix/merge)

All marked `[WM-DIAG]`:

- **`DalamudWindowTracker.InjectMinimizeButton`** — the button `Click` now logs
  `button click '<name>' winHash=<hash> dockKey=<key>` before calling `Minimize`.
- **`DalamudWindowTracker.ScanPlugins`** — after the discovery loop, for every tracked window with
  `IsMinimized`, logs `minimized-tracked '<name>' liveIsOpen=<bool> winHash=<hash>` (or `weakref=DEAD`).
- **`WindowManagerService.Minimize`** — after mutating state, logs
  `Minimize '<name>' path=<dock|single> readbackIsOpen=<bool> winHash=<hash>` (or `weakref=DEAD`).

Interpretation guide:
- `button click` present but window stays + `readbackIsOpen=False` → mechanism sets flag correctly.
- `minimized-tracked … liveIsOpen=True` → something re-opens it (hypothesis 3).
- `minimized-tracked … liveIsOpen=False` while still visible → instance mismatch (hypothesis 2).
- no `button click` line → click not reaching handler (hypothesis 1).

---

## Session 2 Update (2026-09-06)

1. **Why `dalamud.log` had no prior logs:**
   - `%AppData%\XIVLauncher\dalamudConfig.json` configured `"LogLevel": 4` (`Error`).
   - Serilog in Dalamud completely drops all `Logger.Info` and `Logger.Warning` messages.
   - Solution: Added [`DiagLogger`](file:///C:/Users/mayer/orca/workspaces/Umbra.WindowManager/fix-minimizing/Umbra.WindowManager/Services/WindowManager/DiagLogger.cs) which writes directly to `%AppData%\XIVLauncher\wm-debug.log` on disk AND logs via `Logger.Error` (which is written to `dalamud.log` even under `LogLevel: 4`).

2. **Verified Live Status in Running Game (`ffxiv_dx11`):**
   - Umbra is actively loading `out/Debug/Umbra.WindowManager.dll` directly via `FileSystemWatcher` hot-reload.
   - `DalamudWindowTracker.ScanPlugins` is actively executing: successfully discovered and is tracking **104 plugin windows**.
   - `InjectMinimizeButton` successfully injects into decorated windows (e.g. `GatherBuddy`, `Market Board`, `FishingTimer`).
   - Added hot-reload deduplication: previous hot-reloads were creating duplicate minimize buttons on surviving `Window` instances because `InjectedButtons` was empty in the new assembly load context. Now duplicates are automatically pruned and rebound.

3. **Live User Click Verified (Phase 1 & 2 Complete):**
   - User clicked minimize on `Artisan 4.0.5.19###Artisan` and `Orchestrion - [45] Sultana Dreaming###Orchestrion`.
   - In `wm-debug.log`:
     - `button click 'Artisan 4.0.5.19###Artisan' winHash=41460756 dockKey=<none> liveIsOpen=True`
     - `Minimize 'Artisan 4.0.5.19###Artisan' path=single readbackIsOpen=False winHash=41460756`
     - `button click 'Orchestrion - [45] Sultana Dreaming###Orchestrion' winHash=40443502 dockKey=<none> liveIsOpen=True`
     - `Minimize 'Orchestrion - [45] Sultana Dreaming###Orchestrion' path=single readbackIsOpen=False winHash=40443502`
   - Both windows successfully minimized (`IsOpen = false`) and stayed minimized across all subsequent frames.

4. **Root Cause for "Where did they disappear to?":**
   - Decompressed and inspected the user's active toolbar configuration (`Toolbar.WidgetData` in `Default.profile.json`).
   - The user has 29 widgets active on the bar (Clock, Currencies, Durability, Volume, etc.), but **the `UmbraWindowManagerWidget` ("Window Manager") widget has not been added to their toolbar**.
   - Because the widget is not present on the bar, there is no UI element displaying the minimized taskbar buttons to click and restore them.
   - Why only "some" windows have the button: Dalamud only renders title bar buttons on decorated windows (windows that have a title bar). Frameless overlays and HUD elements (`NoTitleBar` / `NoDecoration`) do not have title bars.

## Next steps

1. Instruct the user to add the "Window Manager" widget to their Umbra toolbar (`/umbra` -> Edit Bar -> Add Widget -> Window Manager).
2. Once added, verify that minimized windows (`Artisan`, `Orchestrion`) appear on the toolbar and can be restored by clicking them.
3. Clean up diagnostic logs (`DiagLogger`) and prepare branch for merge.

## Key files

- [`DalamudWindowTracker.cs`](file:///C:/Users/mayer/orca/workspaces/Umbra.WindowManager/fix-minimizing/Umbra.WindowManager/Services/WindowManager/DalamudWindowTracker.cs) — discovery + injection (instrumented + deduplicated)
- [`WindowManagerService.cs`](file:///C:/Users/mayer/orca/workspaces/Umbra.WindowManager/fix-minimizing/Umbra.WindowManager/Services/WindowManager/WindowManagerService.cs) — state machine (instrumented)
- [`ImGuiContextMonitor.cs`](file:///C:/Users/mayer/orca/workspaces/Umbra.WindowManager/fix-minimizing/Umbra.WindowManager/Services/WindowManager/ImGuiContextMonitor.cs) — collapse guard + dock tracking (instrumented)
- [`WindowManagerWidget.cs`](file:///C:/Users/mayer/orca/workspaces/Umbra.WindowManager/fix-minimizing/Umbra.WindowManager/Widgets/WindowManagerWidget.cs) — toolbar widget (instrumented)
- [`DiagLogger.cs`](file:///C:/Users/mayer/orca/workspaces/Umbra.WindowManager/fix-minimizing/Umbra.WindowManager/Services/WindowManager/DiagLogger.cs) — dual disk/error diagnostic logger

