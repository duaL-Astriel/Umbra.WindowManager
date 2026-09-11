# Raw-ImGui / non-WindowSystem window tracking — design

Tracks issue [#38](https://github.com/duaL-Astriel/Umbra.WindowManager/issues/38).
Split from #36 (part 2).

## Problem

Some plugins (notably Sonar, `SonarPlugin.GUI.SonarMainWindow`) do not subclass
`Dalamud.Interface.Windowing.Window` / implement `IWindow`. They draw via raw
`ImGui.Begin(...)` / `ImGui.End()` loops inside their own `IDisposable` /
`IHostedService` classes. Because the entire tracking pipeline is coupled to
`IWindow`, these windows are never registered, minimized, restored, focused, or
given a minimize button.

### Why this is inherently limited (not merely unimplemented)

- **Minimize/restore is best-effort only.** A raw-ImGui plugin owns its own draw
  loop; we cannot stop it drawing. The only lever is to move/collapse the window
  each frame, which a plugin can fight. Restore cannot force a non-drawing window
  to reappear.
- **No title-bar injection path.** `InjectMinimizeButton` needs
  `IWindow.TitleBarButtons`; raw windows have none.
- **`IWindow` coupling is load-bearing.** It keeps the taskbar limited to the
  curated top-level set. `ctx.Windows` contains *everything* — child windows,
  popups, tooltips, `Debug##Default`, transient overlays. The strict filtering is
  the actual hard part.

A prior spike confirmed this is **not** better solved by a standalone Dalamud
plugin: both an Umbra module and a standalone plugin share the same process-wide
`ImGui.GetCurrentContext().Windows` and the same absence of a foreign `IWindow`.
Dalamud exposes no cross-plugin window registry. Standalone would only sacrifice
the Umbra toolbar integration that is the point of this plugin, for zero gain on
the raw-ImGui problem.

## Product decisions (agreed)

1. **Opt-in, default OFF.** A global cvar gates the raw path. Anyone who does not
   enable it gets byte-for-byte the current `IWindow` behavior.
2. **Soft-hide = move off-screen** each frame while minimized.
3. **Drop stale entries after N unseen frames.** A raw window has no
   `IWindow.IsOpen`, so we cannot tell "we hid it" from "the plugin closed it".
   A minimized window keeps drawing (off-screen) so it stays seen and persists;
   once the plugin actually stops drawing it, it ages out and is pruned.

## Type model (agreed): subclass + virtual behavior

Introduce `ImGuiTrackedWindow : TrackedWindow`. The storage type stays
`TrackedWindow`, so `WindowManagerService`'s
`ConcurrentDictionary<string, TrackedWindow>`, the widget, and `DockGroup` are
untouched at the type level. Raw behavior is isolated in one class and is
unit-testable; the existing `IWindow` path is literally unchanged.

Raw windows have **no** `IWindow`, so they inherently never touch the
`IWindow`-coupled paths (`InjectMinimizeButton`, `TitleBarButtons`, dock groups) —
those are already guarded behind `TryGetWindow`, which returns `false` for a raw
window.

### `TrackedWindow` changes

- Add a `protected TrackedWindow(string windowName)` constructor for the
  no-`IWindow` case (sets `WindowName` / `CleanTitle` / `Id`; leaves the weak
  reference empty).
- Introduce `public virtual bool IsAlive => this.TryGetWindow(out _);` — the
  single "is this entry still real" signal the service prunes on.
- Make virtual (raw overrides each): `IsManageable`, `IsOpen` (getter+setter),
  `IsFocused`, `PassesDrawConditions`, `BringToFront()`.
- Add `public virtual bool IsRawImGui => false;` for best-effort UI labeling.
- Existing member bodies are unchanged; they simply become `virtual`.

### `ImGuiTrackedWindow : TrackedWindow`

Holds per-frame **observed** state (updated by the monitor) and **intent** flags:

```
ObservedPos, ObservedSize : Vector2
ObservedFocused           : bool
UnseenFrames              : int         // inherited; drives IsAlive
SavedRestorePos           : Vector2?    // captured on first minimize frame
PendingFocus              : bool        // set by BringToFront()
WasMinimizedLastFrame     : bool        // monitor uses to detect restore edge
```

Overrides:

- `IsAlive => UnseenFrames <= MaxUnseenRawFrames` (e.g. ~30 frames / ~0.5 s).
- `IsOpen` getter `=> !IsMinimized && HasConfirmedUi`; setter is a no-op.
- `IsFocused => ObservedFocused`.
- `PassesDrawConditions => true` (raw windows have no `DrawConditions`).
- `IsManageable` — derived from observed state: positive `ObservedSize`, has a
  title bar, not clickthrough/child/popup/tooltip. (The monitor only registers
  windows that already pass the classifier, so this is mostly a re-assertion.)
- `IsRawImGui => true`.
- `BringToFront()` sets `PendingFocus = true` (the monitor performs the actual
  `SetWindowFocus`).

`WindowManagerService.Minimize`/`Restore` need **no raw-specific branch**: they
already flip `IsMinimized`, set `IsOpen` (no-op on raw), and call
`BringToFront()`. The monitor detects the `IsMinimized` true→false edge via
`WasMinimizedLastFrame` and repositions + focuses on restore.

## Mechanism

### Classifier (pure, unit-tested)

New `ImGuiWindowClassifier.ShouldTrack(ImGuiWindowFlags flags, Vector2 size,
string name, string cleanTitle, bool hasContent) : bool`:

- Reject `ChildWindow`, `Popup`, `Tooltip`, `Modal`, `NoTitleBar`,
  `NoDecoration`, `NoInputs`, `NoMouseInputs`.
- Require positive size and `hasContent`.
- Require non-empty `cleanTitle` (drops `###id`-only overlays — consistent with
  #36 part 3 being intentionally dropped).
- Reject internal name prefixes (`Debug##`, and a small hardcoded list).

Keeping this pure mirrors the existing `ImGuiContextMonitorValidationTests` /
`WindowInfoHelperTests` style and makes the hard part testable without a live
ImGui context.

### Discovery — `ImGuiContextMonitor.OnDraw` (gated on the cvar)

At the top of `OnDraw`, if the cvar is off, skip all raw logic (and, once, restore
any raw window still parked off-screen — see teardown).

In the `ctx.Windows` loop, when a name is neither in `trackedMap` nor
fast-trackable via a known `WindowSystem` (i.e. genuinely raw): run the
classifier. On pass, `windowManager.RegisterImGuiWindow(name)` and insert into
`trackedMap` so the same loop iteration processes it. On fail, fall through to the
existing `unmanagedWindowNames` cache (its 60-frame clear lets a window that later
gains a title be re-evaluated).

For a raw tracked window in the loop:

- Update `ObservedPos` / `ObservedSize` / `ObservedFocused`; set `HasConfirmedUi`
  via the existing size/content checks; reset `UnseenFrames`.
- **Skip** `InjectMinimizeButton` and all dock-node logic (guard: only run those
  when `tracked.TryGetWindow(out _)`).
- **Skip** the stale-`IsMinimized` reconcile block (that path is `IWindow`
  specific and would fight our intentional off-screen hold).
- Soft-hide enforcement:
  - `IsMinimized`: if `SavedRestorePos == null` capture `ObservedPos`; then
    `ImGui.SetWindowPos(name, OffScreen)` each frame.
  - restore edge (`WasMinimizedLastFrame && !IsMinimized`):
    `ImGui.SetWindowPos(name, SavedRestorePos ?? clampToViewport)` once +
    `ImGui.SetWindowFocus(name)`; clear `SavedRestorePos`.
  - else if `PendingFocus`: `ImGui.SetWindowFocus(name)`; clear it.
  - set `WasMinimizedLastFrame = IsMinimized`.
- Raw windows are routed out of the draw-loop's per-window handling (an early `continue`) and so never reach the shared `IWindow` native-collapse guard / title-bar double-click block. Instead, the raw block wires the same two affordances itself via the pure predicate `ImGuiContextMonitor.ShouldMinimizeRawWindowFromTitleBar(...)` (operating on the raw `win`, not `IWindow`): a native collapse or a title-bar double-click on a raw window is intercepted and routed to a clean toolbar minimize (the collapse is undone with `win.Collapsed = false`). Applied before `ComputeFrameAction` so the soft-hide takes effect the same frame. There is still no *injected* title-bar button (that needs `IWindow.TitleBarButtons`); the toolbar button and context menu remain available too.

`OffScreen` is a fixed far-negative position (e.g. `(-32000, -32000)`). Plugins
that call `SetNextWindowPos` every frame will win the fight; that is the
documented best-effort limitation.

### Lifecycle / stale drop

The trailing "not observed this frame" loop increments `UnseenFrames` for raw
windows regardless of open/minimized state (a minimized raw window still draws
off-screen, so it stays seen and never ages out). When the plugin stops drawing,
`UnseenFrames` passes the threshold, `IsAlive` goes false, and the service's
existing prune removes the entry.

### `WindowManagerService` changes

- Add `TrackedWindow RegisterImGuiWindow(string windowName)`: returns the existing
  entry if it is already an `ImGuiTrackedWindow`, else creates and stores one in
  the same dictionary.
- Swap the three aliveness checks (`GetTrackedWindows`,
  `GetVisibleAndMinimizedWindows`, `PruneDeadWindows`) from `TryGetWindow(out _)`
  to `IsAlive`. (The widget's dock-group helpers keep `TryGetWindow` — raw windows
  never join dock groups.)
- `Minimize` / `Restore` / `Toggle` / `Close` unchanged.

### Config

The opt-in lives as a **widget config variable** in the Window Manager widget's
own settings panel (alongside Display Mode / Blacklist), so it is where users
already configure this widget and is guaranteed to render. The monitor is a
singleton `[Service]` and cannot read per-widget config directly, so the widget
**pushes** the value to the service each frame:

- Widget: add `[ConfigVariable("WindowManager.TrackRawImGuiWindows", ...)]`
  boolean property + a `BooleanWidgetConfigVariable` entry in
  `GetConfigVariables()` (default `false`), mirroring the existing `Decorate` /
  `GroupDockedTabs` settings.
- Service: add a simple `bool RawTrackingEnabled { get; set; }` (plain
  auto-property; written on the UI/update thread, read in the draw loop — both run
  on the framework thread, so no synchronization is needed).
- `WindowManagerWidget.UpdateButtons()` (already called every frame via
  `OnUpdate`) sets `this.windowManager.RawTrackingEnabled = this.TrackRawImGui;`.
- Monitor: reads `windowManager.RawTrackingEnabled` at the top of `OnDraw`; when
  false, the raw path is skipped entirely.

Because the setting sits in the widget panel, no cross-reference note is needed.

**Teardown when disabled:** if the toggle flips off while a raw window is parked
off-screen, the monitor repositions it to `SavedRestorePos` once before it ages
out of tracking, so disabling never strands a window off-screen.

### Documentation of the limitation

- Raw entries append a "Best-effort (raw ImGui window)" line to the widget
  tooltip (`BuildTabTooltip` gains an `isRawImGui` argument; the widget passes
  `window.IsRawImGui`).
- README gains a short "Raw-ImGui windows (experimental)" section describing the
  opt-in and the soft-hide/focus-only-restore limitation.

## Isolation seam for tests

The only un-unit-testable pieces are the live ImGui calls (`SetWindowPos`,
`SetWindowFocus`, reading `ctx.Windows`). Put the write calls behind a tiny
delegate/interface seam so the monitor's decision logic (what to do this frame) is
testable and only the thin adapter that calls ImGui is left uncovered.

## Testing

- **`ImGuiWindowClassifierTests`** — table-driven: a Sonar-like window passes;
  `ChildWindow` / `Popup` / `Tooltip` / `Modal` / `NoTitleBar` / `Debug##` /
  `###id`-only / empty-title / zero-size all fail.
- **`ImGuiTrackedWindowTests`** — `IsAlive` threshold; `IsOpen` / `IsFocused` /
  `IsManageable` from observed state; minimize→restore flag transitions;
  `SavedRestorePos` capture-and-clear; `PendingFocus` set by `BringToFront`.
- **`WindowManagerServiceTests`** (additions) — `RegisterImGuiWindow` stores in
  the dict and returns the same instance on re-register; prune drops a raw window
  once `IsAlive` is false; `Minimize`/`Restore` flip flags; **regression:**
  `IWindow`-backed windows behave exactly as before.
- **Monitor decision logic** — via the seam: minimized ⇒ off-screen write;
  restore edge ⇒ reposition + focus; pending focus ⇒ focus; unseen ⇒
  `UnseenFrames` climbs.
- **`ShouldMinimizeRawWindowFromTitleBar`** (in `ImGuiContextMonitorValidationTests`)
  — table-driven: native collapse ⇒ minimize; double-click inside the title-bar
  band while hovered ⇒ minimize; double-click below the band / outside the window /
  while occluded / single-click ⇒ no minimize; already minimized or `NoTitleBar` ⇒
  no minimize.

## Acceptance (maps to the issue)

- Sonar's main window (and similar raw windows) appear in the toolbar/taskbar —
  classifier + `RegisterImGuiWindow`.
- No child windows / popups / tooltips / transient overlays leak — classifier
  rejects them.
- Minimize soft-hides best-effort (off-screen); restore focuses + repositions
  where the plugin cooperates; the limitation is documented (tooltip + README).
- Existing `IWindow`-based behavior is untouched — raw behavior lives only in
  overrides; the default path and its tests are unchanged; the feature is opt-in.

## Out of scope

- Title-bar minimize-button *injection* for raw windows (no `IWindow`; the toolbar button, context menu, title-bar double-click, and native-collapse interception cover minimize). Drawing a custom (non-injected) title-bar button, like the dock-group button, remains a possible follow-up.
- Dock-group participation for raw windows.
- Fighting plugins that hard-pin their own position every frame.
