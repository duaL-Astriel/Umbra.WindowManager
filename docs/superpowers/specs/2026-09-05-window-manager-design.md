# Window Manager Plugin for Umbra Design Specification

## Overview
This specification outlines the architecture, data structures, and user experience for a Window Manager plugin for Umbra (Final Fantasy XIV Dalamud plugin). The plugin serves as a comprehensive taskbar and window manager for the entire Dalamud plugin ecosystem, displaying all open and minimized windows directly in the Umbra toolbar. Minimized windows completely disappear from the viewport instead of collapsing into a floating title bar, and grouped (docked) tabs are minimized and restored together as a cohesive unit.

## Context & Constraints
- **Target Platform**: Final Fantasy XIV running with official Dalamud (.NET 10 / net10.0-windows).
- **Umbra Framework**: Umbra 3.1.x using the `Una.Drawing` node rendering library for custom UI widgets and layout.
- **Ecosystem Compatibility**: Must work seamlessly with official Dalamud releases without relying on private branches or custom Dalamud modifications.
- **Non-Intrusive**: Plugins should not need to modify their code to support being managed, minimized, or grouped.

---

## Architectural Design

### System Components

```
┌────────────────────────────────────────────────────────────────────────┐
│                          Umbra Toolbar                                 │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │                    WindowManagerToolbarWidget                    │  │
│  │     - Dynamic Node container (Una.Drawing)                       │  │
│  │     - Hybrid display: Taskbar Buttons / Icon-Only / Dropdown     │  │
│  │     - Visual state: Active, Open, Minimized                      │  │
│  └──────────────────────────────────┬───────────────────────────────┘  │
│                                     │                                  │
│  ┌──────────────────────────────────▼───────────────────────────────┐  │
│  │                       WindowManagerService                       │  │
│  │     - Central window registry and state machine                  │  │
│  │     - Minimized window tracking                                  │  │
│  │     - Docked group tracking & restoration logic                  │  │
│  └───────────────┬─────────────────────────────────┬────────────────┘  │
└──────────────────┼─────────────────────────────────┼───────────────────┘
                   │                                 │
┌──────────────────▼──────────────┐   ┌──────────────▼───────────────────┐
│       DalamudWindowTracker      │   │        ImGuiContextMonitor       │
│ - Reflection on PluginManager   │   │ - Live ImGuiContext inspection   │
│ - WindowSystem discovery        │   │ - DockNode & Tab tree inspection │
│ - IWindow discovery & caching   │   │ - Native collapse interception   │
│ - TitleBarButton injection      │   │                                  │
└─────────────────────────────────┘   └──────────────────────────────────┘
```

### Component Details

#### 1. `DalamudWindowTracker`
- **Purpose**: Discovers and decorates `IWindow` instances from Dalamud and all installed plugins.
- **Discovery Mechanism**:
  - Accesses Dalamud's internal `PluginManager` via reflection:
    `Service<PluginManager>.Get().InstalledPlugins`.
  - Scans loaded plugin instances and their fields/properties for instances of `Dalamud.Interface.Windowing.WindowSystem` and `IWindow`.
  - Also captures Dalamud's core internal `WindowSystem`.
  - Stores all references as `WeakReference` to ensure plugins can unload cleanly.
- **Throttling**: Full discovery runs at startup, on plugin load/unload events, and periodically on a 2-second background timer.
- **Title Bar Decoration**:
  - Inspects `IWindow.TitleBarButtons`.
  - Automatically appends a minimize button with `FontAwesomeIcon.WindowMinimize` and high priority (`int.MaxValue - 1`).
  - Clicking this button executes `WindowManagerService.Minimize(window)`.

#### 2. `ImGuiContextMonitor`
- **Purpose**: Real-time ImGui frame-level observation and guard.
- **Execution Hook**: Runs on Umbra's `[OnDraw]` pipeline.
- **Functions**:
  - **Dock Group Tracking**: Inspects `window.DockNode` on active windows. Discovers when multiple windows share an `ImGuiDockNodePtr` (tab group) and identifies the currently visible tab (`DockNode.VisibleWindow`).
  - **Native Collapse Guard**: Detects if an ImGui window has `Collapsed == true` (from double-clicking the title bar or clicking the native collapse triangle). Immediately resets `window.Collapsed = false` and calls `WindowManagerService.Minimize(window)`, preventing floating collapsed title bars.

#### 3. `WindowManagerService`
- **Purpose**: Central state manager for active, minimized, and grouped windows.
- **Key Methods**:
  - `Minimize(IWindow window)`:
    - If `window` is part of a multi-window dock group, triggers `MinimizeGroup(groupKey)`.
    - Otherwise, records `window` in `_minimizedWindows` and sets `window.IsOpen = false`.
  - `Restore(IWindow window)`:
    - If `window` is part of a minimized dock group, restores all windows in the group.
    - Sets `window.IsOpen = true`, calls `window.BringToFront()`, and sets `window.RequestFocus = true`.
    - Removes from `_minimizedWindows`.
  - `MinimizeGroup(string groupKey)`:
    - Snapshots member windows and the current active tab.
    - Sets `IsOpen = false` on every member window in the dock node, causing the dock node host window to disappear completely.
    - Adds group to `_minimizedGroups`.
  - `RestoreGroup(string groupKey)`:
    - Sets `IsOpen = true` on all member windows.
    - Focuses and brings to front the previously active tab.
    - Removes group from `_minimizedGroups`.
  - `Close(IWindow window)`: Sets `window.IsOpen = false` and removes from minimized state.

#### 4. `WindowManagerToolbarWidget`
- **Purpose**: Umbra toolbar widget rendering managed windows.
- **UI Architecture**: Inherits from `ToolbarWidget`, using `Una.Drawing.Node` for its horizontal layout container.
- **Modes**:
  - **Taskbar Mode**: Buttons with plugin icon + clean window title (`title.Split("##")[0].Trim()`).
  - **Icon-Only Mode**: Compact dock buttons with tooltip titles.
  - **Dropdown Mode**: Single widget icon with window count badge; clicking opens an Umbra `MenuPopup`.
  - **Auto Mode (Default)**: Automatically starts in Taskbar Mode and condenses to Icon-Only or Dropdown when toolbar width is constrained.
- **Visual Classes**:
  - `.active`: Distinct accent color for the currently focused window.
  - `.open`: Standard background for open background windows.
  - `.minimized`: Dimmed opacity (60%) with a badge indicator showing it is minimized into the toolbar.
  - `.dock-group`: Group pill/badge indicating a stacked tab container.

---

## User Interaction Specifications

| Action | Left-Click | Right-Click |
|---|---|---|
| **Minimized Window** | Restores window, focuses, and brings to front | Context menu (Restore, Close) |
| **Open & Unfocused Window** | Brings window to front and focuses | Context menu (Minimize, Close) |
| **Open & Focused Window** | Minimizes window into toolbar | Context menu (Minimize, Close) |
| **Docked Tab Group** | Toggles minimize / restore for all tabs in the group | Context menu (Select Active Tab, Close All Tabs) |

---

## Configuration Options

| Setting | Type | Default | Description |
|---|---|---|---|
| `DisplayMode` | Enum | `Auto` | Hybrid mode: `Auto`, `Taskbar`, `IconOnly`, `Dropdown` |
| `MaxTitleWidth` | Integer | `140` | Maximum pixel width of title text before ellipsis in Taskbar mode |
| `GroupDockedTabs` | Boolean | `true` | When enabled, tabs in the same dock container minimize and restore together |

---

## Error Handling & Safety
- **Memory Leak Protection**: Plugin and window references stored strictly in `WeakReference<T>` collections, cleaned up on disposal.
- **ImGui Ptr Safety**: Pointer null-checks (`!ptr.IsNull`) prior to any access to protect against transient frames or window destructions.
- **Safe Reflection**: Reflection calls wrapped in defensive try-catch guards with logging; fails gracefully without interrupting Umbra or the game loop.
- **Scene State Integrity**: Minimized states survive GPose, duty cutscenes, and zone transitions without accidental re-opening.

---

## Verification Plan

### Automated Tests
1. **`WindowManagerServiceTests`**:
   - Single window tracking, minimize, restore, close.
   - Idempotency of repeated minimize/restore calls.
   - Title formatting utility (stripping `##` and `###` identifiers).
2. **`DockGroupManagerTests`**:
   - Multi-window dock node association.
   - Group-wide minimize (`IsOpen = false` for all siblings).
   - Group-wide restore with active tab focus preserved.

### Manual In-Game Verification
1. Open multiple Dalamud plugin windows. Confirm all appear in the Umbra toolbar.
2. Click minimize title bar button on an undocked window $\rightarrow$ window completely disappears from screen into the toolbar.
3. Click toolbar item $\rightarrow$ window reappears and gains focus.
4. Dock 2+ windows into tabs $\rightarrow$ click minimize $\rightarrow$ entire tab group vanishes into the toolbar.
5. Click group item in toolbar $\rightarrow$ all tabs restore together in their docked configuration.
6. Double-click window title bar $\rightarrow$ window minimizes directly to toolbar rather than collapsing into its title bar.
