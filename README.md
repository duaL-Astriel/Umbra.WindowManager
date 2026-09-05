# Umbra.WindowManager

[![Build and Test](https://img.shields.io/badge/build-passing-brightgreen.svg)](#building--testing)
[![Target](https://img.shields.io/badge/.NET-10.0--windows-blue.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](https://www.gnu.org/licenses/agpl-3.0.html)

**Umbra Window Manager** is a plugin for [Umbra](https://github.com/una-xiv/umbra) (the customizable HUD and toolbar framework for Final Fantasy XIV via [Dalamud](https://goatcorp.github.io/)).

It acts as a comprehensive taskbar and window manager for the entire Dalamud plugin ecosystem:
- Automatically discovers open and registered plugin windows without requiring plugins to change their code.
- Adds an injected minimize button to plugin title bars and intercepts native ImGui collapses.
- Completely hides minimized windows from the viewport instead of leaving floating collapsed title bars.
- Intelligently manages docked tab groups, minimizing and restoring tab sets together while preserving the active tab focus.
- Displays all managed windows in the Umbra toolbar with customizable display modes (`Auto`, `Taskbar`, `IconOnly`, `Dropdown`).

---

## Features

- **Universal Window Discovery**: Scans Dalamud's internal `PluginManager` via safe reflection to discover `WindowSystem` and `IWindow` instances from any installed plugin.
- **True Minimization**: Minimized windows have their visibility toggled off cleanly (`IsOpen = false`), eliminating clutter and viewport obstructions.
- **Collapse Interception Guard**: Intercepts native ImGui window collapses (such as double-clicking title bars or clicking collapse triangles) and automatically converts them into full toolbar minimizes.
- **Dock Group Management**: Detects multi-window ImGui dock nodes (`ImGuiDockNodePtr`). Docked tabs are grouped together, minimized as a collective unit, and restored with their active tab intact.
- **Safe Memory Management**: All external window references are held weakly via `WeakReference<IWindow>`, preventing memory leaks or blocking plugins from unloading.
- **Flexible Toolbar Widget**:
  - Built with `Una.Drawing.Node` layout system.
  - Interactive states: `.active` (accented when focused), `.open` (standard background), `.minimized` (dimmed at 60% opacity), `.dock-group` (pill indicator for grouped tabs).
  - Per-window icons: renders the owning plugin's icon (`images/icon.png`, resolved via reflection) with a text-monogram fallback, so Icon-Only mode always shows something scannable.
  - Configurable display modes, all functional: `Taskbar` (icon + label), `IconOnly` (icon + tooltip), `Dropdown` (single button with a window-count badge opening an Umbra `MenuPopup`), and `Auto` (starts as Taskbar and condenses toward IconOnly/Dropdown as toolbar width tightens).
  - Left-click to focus, bring to front, or toggle minimize/restore; right-click opens a per-state context menu (Minimize/Restore, Close; or Select Active Tab / Close All Tabs for dock groups).

---

## Architecture

Umbra Window Manager uses a dual-layer observation and orchestration architecture:

```text
┌────────────────────────────────────────────────────────────────────────┐
│                          Umbra Toolbar                                 │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │                    WindowManagerWidget                           │  │
│  │     - Dynamic Node container (Una.Drawing)                       │  │
│  │     - Hybrid display: Taskbar / Icon-Only / Dropdown / Auto      │  │
│  │     - Visual classes: .active, .open, .minimized, .dock-group    │  │
│  └──────────────────────────────────┬───────────────────────────────┘  │
│                                     │                                  │
│  ┌──────────────────────────────────▼───────────────────────────────┐  │
│  │                       WindowManagerService                       │  │
│  │     - Central window registry and state machine                  │  │
│  │     - Minimized window tracking                                  │  │
│  │     - Docked group tracking & active tab restoration             │  │
│  └───────────────┬─────────────────────────────────┬────────────────┘  │
└──────────────────┼─────────────────────────────────┼───────────────────┘
                   │                                 │
┌──────────────────▼──────────────┐   ┌──────────────▼───────────────────┐
│       DalamudWindowTracker      │   │        ImGuiContextMonitor       │
│ - Reflection on PluginManager   │   │ - Live ImGuiContext inspection   │
│ - WindowSystem discovery        │   │ - DockNode & Tab tree inspection │
│ - IWindow discovery & caching   │   │ - Native collapse interception   │
│ - TitleBarButton injection      │   │ - List allocation pooling        │
└─────────────────────────────────┘   └──────────────────────────────────┘
```

### Components

1. **`WindowInfoHelper`**: Parses ImGui identifiers (`##` and `###`) from window titles to extract clean human-readable names and stable identifiers.
2. **`TrackedWindow`**: Encapsulates a weak reference to `IWindow`, tracking clean titles, namespaces, dock group memberships, and minimized states.
3. **`DockGroup`**: Aggregates docked sibling tabs sharing an ImGui dock node, orchestrating group-level minimize and restoration of the active tab.
4. **`WindowManagerService`**: Central thread-safe state machine managing tracked windows and dock groups. Dock-group registration is idempotent, so a stable dock group is not re-allocated on every frame.
5. **`DalamudWindowTracker`**: Reflection service that runs every 2 seconds (`[OnTick]`) and on demand. Safely checks service initialization tokens, discovers `WindowSystem`s across loaded plugins, and injects minimize `TitleBarButton`s.
6. **`ImGuiContextMonitor`**: Per-frame draw hook (`[OnDraw]`) that checks native ImGui pointers for collapses and dock memberships using pooled collections to prevent draw-loop allocations.
7. **`WindowManagerWidget`**: Umbra `ToolbarWidget` maintaining a dynamic `Una.Drawing.Node` hierarchy with live style updates, tooltip management, and click handlers.

---

## Repository Structure

```text
├── Umbra.WindowManager/                     # Core plugin project
│   ├── Services/
│   │   └── WindowManager/
│   │       ├── DalamudWindowTracker.cs      # Reflection discovery & button injection
│   │       ├── DockGroup.cs                 # Docked tab group aggregate model
│   │       ├── ImGuiContextMonitor.cs       # Unsafe ImGuiContext observer & collapse guard
│   │       ├── TrackedWindow.cs             # Weak-referenced window model
│   │       ├── WindowInfoHelper.cs          # ImGui title & ID parsing utilities
│   │       └── WindowManagerService.cs      # Core state machine & lifecycle service
│   ├── Widgets/
│   │   └── WindowManagerWidget.cs           # Umbra toolbar widget (Una.Drawing UI)
│   └── Umbra.WindowManager.csproj
├── Umbra.WindowManager.Tests/               # xUnit unit test project
│   ├── DalamudWindowTrackerTests.cs         # Reflection & injection unit tests
│   ├── DockGroupTests.cs                    # DockGroup model & lifecycle tests
│   ├── WindowInfoHelperTests.cs             # Title & ID parser tests
│   ├── WindowManagerServiceTests.cs         # Service state machine & GC tests
│   ├── WindowManagerWidgetTests.cs          # Widget node hierarchy & config tests
│   └── Umbra.WindowManager.Tests.csproj
├── docs/
│   └── superpowers/
│       ├── plans/                           # Step-by-step implementation plans
│       ├── specs/                           # Architectural design specifications
│       └── walkthrough.md                   # Full walkthrough & verification report
└── README.md
```

---

## Prerequisites

- **.NET 10 SDK** (`net10.0-windows`, x64).
- **Final Fantasy XIV** with an active Dalamud installation via XIVLauncher.
- Dalamud assembly dependencies located at `%AppData%\XIVLauncher\addon\Hooks\dev\`:
  - `Dalamud.dll`, `Dalamud.Bindings.ImGui.dll`, `FFXIVClientStructs.dll`, `Lumina.dll`, `Lumina.Excel.dll`
- Umbra plugin binaries located at `%AppData%\XIVLauncher\installedPlugins\Umbra\`:
  - `Umbra.dll`, `Umbra.Common.dll`, `Umbra.Game.dll`, `Una.Drawing.dll`

---

## Building & Testing

### Build the Solution

Run the standard .NET CLI build command:

```powershell
dotnet build
```

The compiled plugin assembly will be output to:
```text
out/Debug/Umbra.WindowManager.dll
```

### Run Unit Tests

Execute the comprehensive xUnit test suite (79 unit tests covering all components):

```powershell
dotnet test
```

All tests should pass with zero failures and zero skips. (The exact count grows as coverage is
added; run `dotnet test` for the current number rather than relying on a hard-coded value here.)

---

## Installing into Umbra

Umbra loads third-party widgets through its built-in **custom plugin** system
(`Umbra.Plugins.PluginManager`), not through a Dalamud manifest. **No `manifest.json` or dedicated
entry-point class is required** — Umbra discovers this assembly's `[ToolbarWidget]` and `[Service]`
types by attribute scanning once the DLL is loaded, exactly as it does for its built-in widgets. The
plugin's display metadata (name, author, version, description) is read from the assembly attributes
that `Umbra.WindowManager.csproj` already sets.

To load the built `Umbra.WindowManager.dll`:

1. Build the project (`dotnet build -c Release`); the assembly is written to `out/Release/Umbra.WindowManager.dll`.
2. In-game, open **Umbra Settings → Plugins** and enable **Custom Plugins** (this is an
   experimental/developer feature; Umbra will warn that custom plugins run unsandboxed).
3. Add this plugin either:
   - **From a local file** — point Umbra at the built `Umbra.WindowManager.dll`
     (`PluginEntry.FromFile`), or
   - **From a GitHub repository** — provide the `owner/repo` of a release that ships the DLL
     (`PluginEntry.FromRepository`).
4. Umbra loads the assembly into an isolated `AssemblyLoadContext` (`PluginLoadContext`) and watches
   the file for changes (hot-reload). Restart Umbra if it reports that a restart is required.
5. Open **Umbra Settings → Widgets**, add the **Window Manager** widget to a toolbar, and configure it.

> **Dependency resolution:** all Umbra/Dalamud references in the `.csproj` use `<Private>false</Private>`,
> so shared assemblies (`Umbra`, `Umbra.Common`, `Una.Drawing`, `Dalamud`, …) are resolved from the
> host at load time rather than copied alongside the plugin. Do not ship those DLLs with the plugin.

> **In-game smoke test:** because loading requires a live FFXIV + Dalamud + Umbra session, the manual
> verification steps in [`docs/superpowers/walkthrough.md`](docs/superpowers/walkthrough.md) still need
> to be run in-game to confirm the end-to-end experience.

---

## Configuration Options

Umbra Window Manager provides the following settings via Umbra's Widget Settings UI:

| Setting | Type | Default | Description |
|---|---|---|---|
| `WindowManager.DisplayMode` | Select | `Auto` | Presentation mode: `Auto` (Taskbar, condensing under width pressure), `Taskbar` (icon + label), `IconOnly` (icon + tooltip), `Dropdown` (single button with a window-count badge that opens a `MenuPopup`). |
| `WindowManager.MaxTitleWidth` | Integer | `140` | Maximum pixel width for window title labels before truncation (range: 60–300 px). |
| `WindowManager.GroupDockedTabs` | Boolean | `true` | Whether docked tabs are visually marked (`.dock-group`) and minimized/restored collectively. |

---

## Controls & Usage

| Interaction | Context | Effect |
|---|---|---|
| **Left-Click** | Minimized window | Restores window, brings to front, and requests focus. |
| **Left-Click** | Open & unfocused window | Brings window to front and focuses it. |
| **Left-Click** | Open & focused window | Minimizes window to the toolbar. |
| **Left-Click** | Docked tab group | Toggles minimize / restore for all tabs in the dock container. |
| **Right-Click** | Open window | Context menu: **Minimize**, **Close**. |
| **Right-Click** | Minimized window | Context menu: **Restore**, **Close**. |
| **Right-Click** | Docked tab group | Context menu: **Select Active Tab**, **Close All Tabs**. |
| **Title Bar Button** | Any window | Clicks the injected minimize button (`FontAwesomeIcon.WindowMinimize`) to minimize. |
| **Double-Click Title Bar** | Native ImGui collapse | Intercepted and routed to full window minimize. |

---

## License

This project is licensed under the GNU Affero General Public License v3.0 or later ([AGPL-3.0-or-later](https://www.gnu.org/licenses/agpl-3.0.html)).
Copyright &copy; 2026 Astriel.
