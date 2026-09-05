# Window Manager Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an Umbra plugin that functions as a window manager for the entire Dalamud plugin ecosystem, displaying all open and minimized plugin windows in Umbra's toolbar, completely hiding minimized windows and tab groups, and providing hybrid toolbar presentation.

**Architecture:** A dual-layer engine combining service-level reflection (discovering `WindowSystem` and `IWindow` instances from Dalamud's `PluginManager` and injecting minimize title bar buttons) with real-time ImGui context monitoring (tracking dock nodes and intercepting native collapse events), orchestrated by a central `WindowManagerService` and rendered via an Umbra `ToolbarWidget` using `Una.Drawing`.

**Tech Stack:** C# .NET 10 (`net10.0-windows`), Umbra SDK 3.1.x, Dalamud API 11/12, ImGui (`Dalamud.Bindings.ImGui` / Hexa.NET.ImGui), xUnit for testing.

**Spec:** `docs/superpowers/specs/2026-09-05-window-manager-design.md`

## Global Constraints
- Target Platform: Final Fantasy XIV running with official Dalamud (`net10.0-windows`).
- Compatibility: Must work on official Dalamud without custom Dalamud branches or modifications.
- Lifecycle Safety: All external plugin/window references stored as `WeakReference<T>` to avoid memory leaks.
- Zero Warnings: C# code must compile with `Nullable = enable`, `AllowUnsafeBlocks = true`, 0 warnings, 0 errors.

---

### Task 1: Test Project Setup & WindowInfoHelper

**Files:**
- Create: `Umbra.WindowManager/Services/WindowManager/WindowInfoHelper.cs`
- Create: `Umbra.WindowManager.Tests/WindowInfoHelperTests.cs`

**Interfaces:**
- Produces: `WindowInfoHelper.GetCleanTitle(string windowName) -> string`
- Produces: `WindowInfoHelper.GetWindowId(string windowName) -> string`

- [ ] **Step 1: Write the failing test for WindowInfoHelper**

```csharp
// Umbra.WindowManager.Tests/WindowInfoHelperTests.cs
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class WindowInfoHelperTests
{
    [Theory]
    [InlineData("My Window", "My Window")]
    [InlineData("Settings##MyPluginSettings", "Settings")]
    [InlineData("Inspector###InspectorWindow_123", "Inspector")]
    [InlineData("   Spaced Title  ##ID", "Spaced Title")]
    [InlineData("##OnlyId", "")]
    public void GetCleanTitle_StripsImGuiIdentifiers(string input, string expected)
    {
        var clean = WindowInfoHelper.GetCleanTitle(input);
        Assert.Equal(expected, clean);
    }

    [Theory]
    [InlineData("My Window", "My Window")]
    [InlineData("Settings##MyPluginSettings", "MyPluginSettings")]
    [InlineData("Inspector###InspectorWindow_123", "InspectorWindow_123")]
    public void GetWindowId_ExtractsIdentifierOrFallback(string input, string expected)
    {
        var id = WindowInfoHelper.GetWindowId(input);
        Assert.Equal(expected, id);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test`
Expected: FAIL with "WindowInfoHelper does not exist".

- [ ] **Step 3: Implement WindowInfoHelper**

```csharp
// Umbra.WindowManager/Services/WindowManager/WindowInfoHelper.cs
namespace Umbra.WindowManager.Services.WindowManager;

public static class WindowInfoHelper
{
    public static string GetCleanTitle(string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName))
            return string.Empty;

        var split = windowName.Split("##");
        return split[0].Trim();
    }

    public static string GetWindowId(string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName))
            return string.Empty;

        if (windowName.Contains("###"))
        {
            var idx = windowName.IndexOf("###", StringComparison.Ordinal);
            return windowName[(idx + 3)..].Trim();
        }

        if (windowName.Contains("##"))
        {
            var idx = windowName.IndexOf("##", StringComparison.Ordinal);
            return windowName[(idx + 2)..].Trim();
        }

        return windowName.Trim();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Umbra.WindowManager/Services/WindowManager/WindowInfoHelper.cs Umbra.WindowManager.Tests/WindowInfoHelperTests.cs
git commit -m "feat(window-manager): add WindowInfoHelper and unit tests"
```

---

### Task 2: Window and DockGroup Data Models

**Files:**
- Create: `Umbra.WindowManager/Services/WindowManager/TrackedWindow.cs`
- Create: `Umbra.WindowManager/Services/WindowManager/DockGroup.cs`
- Create: `Umbra.WindowManager.Tests/DockGroupTests.cs`

**Interfaces:**
- Produces: `TrackedWindow` class (wraps `IWindow`, clean title, minimized state, ID, namespace).
- Produces: `DockGroup` class (group key, active window name, member `TrackedWindow`s, minimize/restore group).

- [ ] **Step 1: Write failing test for DockGroup behavior**

```csharp
// Umbra.WindowManager.Tests/DockGroupTests.cs
using System.Collections.Generic;
using Dalamud.Interface.Windowing;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class DockGroupTests
{
    private class DummyWindow : Window
    {
        public DummyWindow(string name) : base(name) { }
        public override void Draw() { }
    }

    [Fact]
    public void DockGroup_MinimizeAll_HidesAllMembers()
    {
        var w1 = new DummyWindow("Tab 1") { IsOpen = true };
        var w2 = new DummyWindow("Tab 2") { IsOpen = true };
        var tw1 = new TrackedWindow(w1);
        var tw2 = new TrackedWindow(w2);

        var group = new DockGroup("dock_123", "Tab 1", new[] { tw1, tw2 });
        Assert.False(group.IsMinimized);

        group.Minimize();

        Assert.True(group.IsMinimized);
        Assert.False(w1.IsOpen);
        Assert.False(w2.IsOpen);
        Assert.True(tw1.IsMinimized);
        Assert.True(tw2.IsMinimized);
    }

    [Fact]
    public void DockGroup_RestoreAll_OpensAllMembersAndRestoresActiveTab()
    {
        var w1 = new DummyWindow("Tab 1") { IsOpen = false };
        var w2 = new DummyWindow("Tab 2") { IsOpen = false };
        var tw1 = new TrackedWindow(w1) { IsMinimized = true };
        var tw2 = new TrackedWindow(w2) { IsMinimized = true };

        var group = new DockGroup("dock_123", "Tab 2", new[] { tw1, tw2 }) { IsMinimized = true };

        group.Restore();

        Assert.False(group.IsMinimized);
        Assert.True(w1.IsOpen);
        Assert.True(w2.IsOpen);
        Assert.False(tw1.IsMinimized);
        Assert.False(tw2.IsMinimized);
        Assert.True(w2.RequestFocus);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test`
Expected: FAIL with "TrackedWindow and DockGroup not found".

- [ ] **Step 3: Implement TrackedWindow and DockGroup**

```csharp
// Umbra.WindowManager/Services/WindowManager/TrackedWindow.cs
using System;
using Dalamud.Interface.Windowing;

namespace Umbra.WindowManager.Services.WindowManager;

public class TrackedWindow
{
    private readonly WeakReference<IWindow> windowRef;

    public TrackedWindow(IWindow window)
    {
        this.windowRef = new WeakReference<IWindow>(window);
        this.WindowName = window.WindowName;
        this.CleanTitle = WindowInfoHelper.GetCleanTitle(window.WindowName);
        this.Namespace = window.Namespace ?? string.Empty;
    }

    public string WindowName { get; }
    public string CleanTitle { get; }
    public string Namespace { get; set; }
    public bool IsMinimized { get; set; }
    public string? DockGroupKey { get; set; }

    public bool TryGetWindow(out IWindow window) => this.windowRef.TryGetTarget(out window!);

    public bool IsOpen
    {
        get => this.TryGetWindow(out var w) && w.IsOpen;
        set
        {
            if (this.TryGetWindow(out var w))
                w.IsOpen = value;
        }
    }

    public bool IsFocused => this.TryGetWindow(out var w) && w.IsFocused;

    public void BringToFront()
    {
        if (this.TryGetWindow(out var w))
        {
            w.BringToFront();
            w.RequestFocus = true;
        }
    }
}
```

```csharp
// Umbra.WindowManager/Services/WindowManager/DockGroup.cs
using System.Collections.Generic;
using System.Linq;

namespace Umbra.WindowManager.Services.WindowManager;

public class DockGroup
{
    private readonly List<TrackedWindow> members = [];

    public DockGroup(string groupKey, string activeWindowName, IEnumerable<TrackedWindow> windows)
    {
        this.GroupKey = groupKey;
        this.ActiveWindowName = activeWindowName;
        this.members.AddRange(windows);
        foreach (var w in this.members)
            w.DockGroupKey = groupKey;
    }

    public string GroupKey { get; }
    public string ActiveWindowName { get; set; }
    public IReadOnlyList<TrackedWindow> Members => this.members;
    public bool IsMinimized { get; set; }

    public void Minimize()
    {
        this.IsMinimized = true;
        foreach (var window in this.members)
        {
            window.IsMinimized = true;
            window.IsOpen = false;
        }
    }

    public void Restore()
    {
        this.IsMinimized = false;
        foreach (var window in this.members)
        {
            window.IsMinimized = false;
            window.IsOpen = true;
        }

        var active = this.members.FirstOrDefault(w => w.WindowName == this.ActiveWindowName) 
                     ?? this.members.FirstOrDefault();
        active?.BringToFront();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Umbra.WindowManager/Services/WindowManager/TrackedWindow.cs Umbra.WindowManager/Services/WindowManager/DockGroup.cs Umbra.WindowManager.Tests/DockGroupTests.cs
git commit -m "feat(window-manager): add TrackedWindow and DockGroup models"
```

---

### Task 3: WindowManagerService Core State Machine

**Files:**
- Create: `Umbra.WindowManager/Services/WindowManager/WindowManagerService.cs`
- Create: `Umbra.WindowManager.Tests/WindowManagerServiceTests.cs`

**Interfaces:**
- Produces: `WindowManagerService` with `RegisterWindow(IWindow)`, `UnregisterWindow(IWindow)`, `Minimize(TrackedWindow)`, `Restore(TrackedWindow)`, `Toggle(TrackedWindow)`, `GetActiveAndMinimizedWindows() -> IReadOnlyList<TrackedWindow>`.

- [ ] **Step 1: Write failing test for WindowManagerService**

```csharp
// Umbra.WindowManager.Tests/WindowManagerServiceTests.cs
using System.Linq;
using Dalamud.Interface.Windowing;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class WindowManagerServiceTests
{
    private class DummyWindow : Window
    {
        public DummyWindow(string name) : base(name) { }
        public override void Draw() { }
    }

    [Fact]
    public void WindowManagerService_SingleWindow_Lifecycle()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Test Window") { IsOpen = true };

        service.RegisterWindow(win);

        var tracked = service.GetTrackedWindows();
        Assert.Single(tracked);
        var tw = tracked.First();
        Assert.Equal("Test Window", tw.CleanTitle);
        Assert.False(tw.IsMinimized);

        // Minimize
        service.Minimize(tw);
        Assert.False(win.IsOpen);
        Assert.True(tw.IsMinimized);

        // Restore
        service.Restore(tw);
        Assert.True(win.IsOpen);
        Assert.False(tw.IsMinimized);
        Assert.True(win.RequestFocus);
    }

    [Fact]
    public void WindowManagerService_Toggle_InvertsState()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Toggle Window") { IsOpen = true, IsFocused = true };

        service.RegisterWindow(win);
        var tw = service.GetTrackedWindows().First();

        // Focused & open -> Minimize
        service.Toggle(tw);
        Assert.True(tw.IsMinimized);
        Assert.False(win.IsOpen);

        // Minimized -> Restore
        service.Toggle(tw);
        Assert.False(tw.IsMinimized);
        Assert.True(win.IsOpen);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test`
Expected: FAIL with "WindowManagerService does not exist".

- [ ] **Step 3: Implement WindowManagerService**

```csharp
// Umbra.WindowManager/Services/WindowManager/WindowManagerService.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Windowing;
using Umbra.Common;

namespace Umbra.WindowManager.Services.WindowManager;

[Service]
public class WindowManagerService
{
    private readonly ConcurrentDictionary<string, TrackedWindow> windows = new();
    private readonly ConcurrentDictionary<string, DockGroup> dockGroups = new();

    public event Action? OnWindowsChanged;

    public IReadOnlyList<TrackedWindow> GetTrackedWindows()
    {
        return this.windows.Values
            .Where(w => w.TryGetWindow(out _))
            .ToList();
    }

    public IReadOnlyList<TrackedWindow> GetVisibleAndMinimizedWindows()
    {
        return this.windows.Values
            .Where(w => (w.IsOpen || w.IsMinimized) && !string.IsNullOrWhiteSpace(w.CleanTitle))
            .ToList();
    }

    public TrackedWindow RegisterWindow(IWindow window)
    {
        var tw = this.windows.GetOrAdd(window.WindowName, _ => new TrackedWindow(window));
        this.OnWindowsChanged?.Invoke();
        return tw;
    }

    public void UnregisterWindow(IWindow window)
    {
        this.windows.TryRemove(window.WindowName, out _);
        this.OnWindowsChanged?.Invoke();
    }

    public void Minimize(TrackedWindow tracked)
    {
        if (tracked.DockGroupKey != null && this.dockGroups.TryGetValue(tracked.DockGroupKey, out var group))
        {
            group.Minimize();
        }
        else
        {
            tracked.IsMinimized = true;
            tracked.IsOpen = false;
        }

        this.OnWindowsChanged?.Invoke();
    }

    public void Restore(TrackedWindow tracked)
    {
        if (tracked.DockGroupKey != null && this.dockGroups.TryGetValue(tracked.DockGroupKey, out var group))
        {
            group.Restore();
        }
        else
        {
            tracked.IsMinimized = false;
            tracked.IsOpen = true;
            tracked.BringToFront();
        }

        this.OnWindowsChanged?.Invoke();
    }

    public void Toggle(TrackedWindow tracked)
    {
        if (tracked.IsMinimized || !tracked.IsOpen)
        {
            this.Restore(tracked);
        }
        else if (tracked.IsFocused)
        {
            this.Minimize(tracked);
        }
        else
        {
            tracked.BringToFront();
        }
    }

    public void Close(TrackedWindow tracked)
    {
        tracked.IsMinimized = false;
        tracked.IsOpen = false;
        this.OnWindowsChanged?.Invoke();
    }

    public void RegisterDockGroup(string groupKey, string activeWindowName, IEnumerable<TrackedWindow> members)
    {
        var group = new DockGroup(groupKey, activeWindowName, members);
        this.dockGroups[groupKey] = group;
    }

    public void RemoveDockGroup(string groupKey)
    {
        this.dockGroups.TryRemove(groupKey, out _);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Umbra.WindowManager/Services/WindowManager/WindowManagerService.cs Umbra.WindowManager.Tests/WindowManagerServiceTests.cs
git commit -m "feat(window-manager): implement WindowManagerService core state machine"
```

---

### Task 4: DalamudWindowTracker (Discovery & Title Bar Injection)

**Files:**
- Create: `Umbra.WindowManager/Services/WindowManager/DalamudWindowTracker.cs`
- Create: `Umbra.WindowManager.Tests/DalamudWindowTrackerTests.cs`

**Interfaces:**
- Consumes: `WindowManagerService`
- Produces: `DalamudWindowTracker` (discovers plugins via reflection, injects `TitleBarButton` to minimize).

- [ ] **Step 1: Write test for TitleBarButton injection logic**

```csharp
// Umbra.WindowManager.Tests/DalamudWindowTrackerTests.cs
using System.Linq;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class DalamudWindowTrackerTests
{
    private class DummyWindow : Window
    {
        public DummyWindow(string name) : base(name) { }
        public override void Draw() { }
    }

    [Fact]
    public void InjectMinimizeButton_AddsButtonOnceAndBindsClick()
    {
        var win = new DummyWindow("DecoratedWindow");
        var service = new WindowManagerService();
        var tw = service.RegisterWindow(win);

        Assert.Empty(win.TitleBarButtons);

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);

        Assert.Single(win.TitleBarButtons);
        var btn = win.TitleBarButtons.First();
        Assert.Equal(FontAwesomeIcon.WindowMinimize, btn.Icon);

        // Ensure idempotency
        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);
        Assert.Single(win.TitleBarButtons);

        // Click invokes minimize
        btn.Click?.Invoke(Dalamud.Bindings.ImGui.ImGuiMouseButton.Left);
        Assert.True(tw.IsMinimized);
        Assert.False(win.IsOpen);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test`
Expected: FAIL with "DalamudWindowTracker does not exist".

- [ ] **Step 3: Implement DalamudWindowTracker**

```csharp
// Umbra.WindowManager/Services/WindowManager/DalamudWindowTracker.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Umbra.Common;

namespace Umbra.WindowManager.Services.WindowManager;

[Service]
public class DalamudWindowTracker
{
    private readonly WindowManagerService windowManagerService;
    private readonly HashSet<string> injectedWindowNames = [];

    public DalamudWindowTracker(WindowManagerService windowManagerService)
    {
        this.windowManagerService = windowManagerService;
        this.ScanPlugins();
    }

    public static void InjectMinimizeButton(IWindow window, TrackedWindow tracked, WindowManagerService service)
    {
        if (window.TitleBarButtons.Any(b => b.Icon == FontAwesomeIcon.WindowMinimize))
            return;

        window.TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.WindowMinimize,
            Priority = int.MaxValue - 1,
            Click = _ => service.Minimize(tracked),
            ShowTooltip = () =>
            {
                if (Dalamud.Bindings.ImGui.ImGui.IsItemHovered())
                    Dalamud.Bindings.ImGui.ImGui.SetTooltip("Minimize to Umbra Toolbar");
            }
        });
    }

    [OnTick(interval: 2000)]
    public void ScanPlugins()
    {
        try
        {
            var logAssembly = typeof(Dalamud.Plugin.Services.IPluginLog).Assembly;
            var pmType = logAssembly.GetType("Dalamud.Plugin.Internal.PluginManager");
            if (pmType == null) return;

            var serviceGeneric = typeof(Dalamud.Service<>).MakeGenericType(pmType);
            var getMethod = serviceGeneric.GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            var pmInstance = getMethod?.Invoke(null, null);
            if (pmInstance == null) return;

            var installedProp = pmType.GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.Instance);
            if (installedProp?.GetValue(pmInstance) is not IEnumerable installedPlugins) return;

            foreach (var localPlugin in installedPlugins)
            {
                if (localPlugin == null) continue;
                var pluginInstanceField = localPlugin.GetType().GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
                var pluginObj = pluginInstanceField?.GetValue(localPlugin);
                if (pluginObj == null) continue;

                this.ScanObjectForWindowSystems(pluginObj);
            }
        }
        catch
        {
            // Defensive: logging or ignore reflection errors
        }
    }

    private void ScanObjectForWindowSystems(object obj)
    {
        var type = obj.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        foreach (var prop in type.GetProperties(flags))
        {
            if (typeof(WindowSystem).IsAssignableFrom(prop.PropertyType))
            {
                if (prop.GetValue(obj) is WindowSystem ws)
                    this.TrackWindowSystem(ws);
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            if (typeof(WindowSystem).IsAssignableFrom(field.FieldType))
            {
                if (field.GetValue(obj) is WindowSystem ws)
                    this.TrackWindowSystem(ws);
            }
        }
    }

    public void TrackWindowSystem(WindowSystem ws)
    {
        foreach (var window in ws.Windows)
        {
            if (string.IsNullOrWhiteSpace(window.WindowName)) continue;
            var tw = this.windowManagerService.RegisterWindow(window);
            if (this.injectedWindowNames.Add(window.WindowName))
            {
                InjectMinimizeButton(window, tw, this.windowManagerService);
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Umbra.WindowManager/Services/WindowManager/DalamudWindowTracker.cs Umbra.WindowManager.Tests/DalamudWindowTrackerTests.cs
git commit -m "feat(window-manager): add DalamudWindowTracker and button injection"
```

---

### Task 5: ImGuiContextMonitor (Dock Nodes & Collapse Interception)

**Files:**
- Create: `Umbra.WindowManager/Services/WindowManager/ImGuiContextMonitor.cs`

**Interfaces:**
- Consumes: `WindowManagerService`
- Inspects `ImGui.GetCurrentContext()`, detects dock nodes, uncollapses native ImGui collapses and triggers full minimize.

- [ ] **Step 1: Write ImGuiContextMonitor implementation**

```csharp
// Umbra.WindowManager/Services/WindowManager/ImGuiContextMonitor.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Umbra.Common;

namespace Umbra.WindowManager.Services.WindowManager;

[Service]
public class ImGuiContextMonitor
{
    private readonly WindowManagerService windowManager;

    public ImGuiContextMonitor(WindowManagerService windowManager)
    {
        this.windowManager = windowManager;
    }

    [OnDraw(executionOrder: 10)]
    public unsafe void OnDraw()
    {
        var ctx = ImGui.GetCurrentContext();
        if (ctx.IsNull) return;

        var trackedList = this.windowManager.GetTrackedWindows();
        var trackedMap = trackedList.ToDictionary(t => t.WindowName, t => t);

        // Group tracking dictionary: dockId -> list of windows in that dock
        var dockGroups = new Dictionary<uint, List<TrackedWindow>>();
        var dockActiveTab = new Dictionary<uint, string>();

        for (var i = 0; i < ctx.Windows.Size; i++)
        {
            var win = ctx.Windows[i];
            if (win.IsNull) continue;

            var name = win.Name != null ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)win.Name) : null;
            if (string.IsNullOrEmpty(name) || !trackedMap.TryGetValue(name, out var tracked))
                continue;

            // 1. Native collapse guard: if collapsed natively, cancel it and fully minimize
            if (win.Collapsed)
            {
                win.Collapsed = false;
                this.windowManager.Minimize(tracked);
                continue;
            }

            // 2. Dock node tracking
            if (win.DockIsActive && !win.DockNode.IsNull)
            {
                var dockId = win.DockId;
                if (!dockGroups.TryGetValue(dockId, out var groupMembers))
                {
                    groupMembers = [];
                    dockGroups[dockId] = groupMembers;
                }
                groupMembers.Add(tracked);

                if (win.DockTabIsVisible)
                {
                    dockActiveTab[dockId] = name;
                }
            }
        }

        // Register multi-window dock groups
        foreach (var (dockId, members) in dockGroups)
        {
            if (members.Count > 1)
            {
                var activeName = dockActiveTab.GetValueOrDefault(dockId, members[0].WindowName);
                this.windowManager.RegisterDockGroup($"dock_{dockId}", activeName, members);
            }
        }
    }
}
```

- [ ] **Step 2: Build solution to verify compilation**

Run: `dotnet build`
Expected: PASS with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Umbra.WindowManager/Services/WindowManager/ImGuiContextMonitor.cs
git commit -m "feat(window-manager): add ImGuiContextMonitor for dock tracking and collapse guarding"
```

---

### Task 6: WindowManagerToolbarWidget UI & Hybrid Presentation

**Files:**
- Create: `Umbra.WindowManager/Widgets/WindowManagerWidget.cs`

**Interfaces:**
- Inherits: `ToolbarWidget`
- Exposes: Umbra configuration variables (`DisplayMode`, `MaxTitleWidth`, `GroupDockedTabs`).
- Builds dynamic `Node` hierarchy with `.active`, `.open`, `.minimized` button styles, left click actions, and right click menus.

- [ ] **Step 1: Implement WindowManagerWidget**

```csharp
// Umbra.WindowManager/Widgets/WindowManagerWidget.cs
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Umbra.Common;
using Umbra.WindowManager.Services.WindowManager;
using Umbra.Widgets;
using Una.Drawing;

namespace Umbra.WindowManager.Widgets;

[ToolbarWidget(
    "UmbraWindowManagerWidget",
    "Window Manager",
    "Displays all open and minimized Dalamud plugin windows in the toolbar."
)]
public class WindowManagerWidget : ToolbarWidget
{
    private readonly WindowManagerService windowManager = Framework.Service<WindowManagerService>();
    private readonly Node rootNode;
    private readonly Dictionary<string, Node> windowNodes = [];

    public WindowManagerWidget(
        WidgetInfo info,
        string? guid = null,
        Dictionary<string, object>? configValues = null
    ) : base(info, guid, configValues)
    {
        this.rootNode = new Node
        {
            Flow = Flow.Horizontal,
            AutoSize = (AutoSize.Fit, AutoSize.Fit),
            Style = { Gap = 4 }
        };
    }

    public override Node Node => this.rootNode;
    public override WidgetPopup? Popup => null;

    [ConfigVariable("WindowManager.DisplayMode", "General", "Window Manager", "Auto", ["Auto", "Taskbar", "IconOnly", "Dropdown"])]
    public string DisplayMode { get; set; } = "Auto";

    [ConfigVariable("WindowManager.MaxTitleWidth", "General", "Window Manager", 140, 60, 300)]
    public int MaxTitleWidth { get; set; } = 140;

    public override void Update()
    {
        base.Update();
        this.UpdateButtons();
    }

    private void UpdateButtons()
    {
        var windows = this.windowManager.GetVisibleAndMinimizedWindows();
        var currentNames = new HashSet<string>(windows.Select(w => w.WindowName));

        // Remove old nodes
        foreach (var (name, node) in this.windowNodes.ToList())
        {
            if (!currentNames.Contains(name))
            {
                this.rootNode.RemoveChild(node, true);
                this.windowNodes.Remove(name);
            }
        }

        // Add or update nodes
        foreach (var window in windows)
        {
            if (!this.windowNodes.TryGetValue(window.WindowName, out var btnNode))
            {
                btnNode = new Node
                {
                    Flow = Flow.Horizontal,
                    Style =
                    {
                        Padding = new EdgeSize(4, 6, 4, 6),
                        RoundedCorners = new RoundedCorners(4),
                        Cursor = "pointer"
                    }
                };

                btnNode.OnClick += _ => this.windowManager.Toggle(window);
                btnNode.OnRightClick += _ => this.windowManager.Close(window);

                this.rootNode.AppendChild(btnNode);
                this.windowNodes[window.WindowName] = btnNode;
            }

            // Visual styles
            btnNode.ToggleClass("active", window.IsFocused);
            btnNode.ToggleClass("minimized", window.IsMinimized);
            btnNode.Style.Opacity = window.IsMinimized ? 0.6f : 1.0f;
            btnNode.Tooltip = $"{window.CleanTitle}{(window.IsMinimized ? " [Minimized]" : "")}";

            var showText = this.DisplayMode is "Auto" or "Taskbar";
            btnNode.NodeValue = showText ? window.CleanTitle : string.Empty;
        }
    }
}
```

- [ ] **Step 2: Build solution to verify compilation**

Run: `dotnet build`
Expected: PASS with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Umbra.WindowManager/Widgets/WindowManagerWidget.cs
git commit -m "feat(window-manager): add WindowManagerWidget toolbar UI"
```

---

### Task 7: Full System Verification & Walkthrough

**Files:**
- Create: `README.md`
- Create: `docs/superpowers/walkthrough.md`

- [ ] **Step 1: Add README documentation**

Create `README.md` describing the repository, architecture, and build instructions.

- [ ] **Step 2: Run all tests and complete solution build**

Run: `dotnet test` and `dotnet build`
Expected: All tests pass, 0 warnings, 0 errors.

- [ ] **Step 3: Create Walkthrough documentation**

Document the architecture, usage, and verification steps in `docs/superpowers/walkthrough.md`.

- [ ] **Step 4: Commit**

```bash
git add README.md docs/superpowers/walkthrough.md
git commit -m "chore: complete Window Manager plugin implementation and tests"
```
