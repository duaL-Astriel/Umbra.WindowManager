using System.Linq;
using Dalamud.Bindings.ImGui;
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

    [Fact]
    public void InjectMinimizeButton_InjectsEvenWhenPluginHasOwnMinimizeButton()
    {
        // Issue #8.1: a plugin shipping its own WindowMinimize button must not suppress ours; we match
        // by window instance, not by icon, so our minimize action is always wired.
        var win = new DummyWindow("HasOwnMinimize");
        win.TitleBarButtons.Add(new TitleBarButton { Icon = FontAwesomeIcon.WindowMinimize });
        var service = new WindowManagerService();
        var tw = service.RegisterWindow(win);

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);

        Assert.Equal(2, win.TitleBarButtons.Count);

        // Our button (added last) minimizes the window.
        win.TitleBarButtons.Last().Click?.Invoke(Dalamud.Bindings.ImGui.ImGuiMouseButton.Left);
        Assert.True(tw.IsMinimized);
        Assert.False(win.IsOpen);
    }

    [Fact]
    public void InjectMinimizeButton_SkipsWindowsWithNoTitleBarOrNoDecoration()
    {
        var service = new WindowManagerService();
        var winNoTitleBar = new DummyWindow("Overlay1") { Flags = ImGuiWindowFlags.NoTitleBar };
        var winNoDecoration = new DummyWindow("Overlay2") { Flags = ImGuiWindowFlags.NoDecoration };
        var tw1 = service.RegisterWindow(winNoTitleBar);
        var tw2 = service.RegisterWindow(winNoDecoration);

        DalamudWindowTracker.InjectMinimizeButton(winNoTitleBar, tw1, service);
        DalamudWindowTracker.InjectMinimizeButton(winNoDecoration, tw2, service);

        Assert.Empty(winNoTitleBar.TitleBarButtons);
        Assert.Empty(winNoDecoration.TitleBarButtons);
    }

    [Fact]
    public void TrackWindowSystem_WithPluginContext_SetsPluginNameAndIcon()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("IconSys");
        var win = new DummyWindow("IconWindow");
        ws.AddWindow(win);

        var iconBytes = new byte[] { 1, 2, 3 };
        tracker.TrackWindowSystem(ws, "MyPlugin", iconBytes);

        var tw = service.GetTrackedWindows().Single(t => t.WindowName == "IconWindow");
        Assert.Equal("MyPlugin", tw.PluginInternalName);
        Assert.Same(iconBytes, tw.IconBytes);
    }

    [Fact]
    public void InjectMinimizeButton_NullTitleBarButtons_DoesNotThrow()
    {
        var win = new DummyWindow("NullButtons");
        win.TitleBarButtons = null!;
        var service = new WindowManagerService();
        var tw = service.RegisterWindow(win);

        var ex = Record.Exception(() => DalamudWindowTracker.InjectMinimizeButton(win, tw, service));
        Assert.Null(ex);
    }

    [Fact]
    public void TrackWindowSystem_RegistersWindowsAndInjectsButtons()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("TestSystem");
        var win1 = new DummyWindow("PluginWindow1");
        var win2 = new DummyWindow("PluginWindow2");
        ws.AddWindow(win1);
        ws.AddWindow(win2);

        tracker.TrackWindowSystem(ws);

        var tracked = service.GetTrackedWindows();
        Assert.Equal(2, tracked.Count);
        Assert.Contains(tracked, t => t.WindowName == "PluginWindow1");
        Assert.Contains(tracked, t => t.WindowName == "PluginWindow2");

        Assert.Single(win1.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, win1.TitleBarButtons.First().Icon);
        Assert.Single(win2.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, win2.TitleBarButtons.First().Icon);

        // Idempotency: re-tracking shouldn't duplicate buttons
        tracker.TrackWindowSystem(ws);
        Assert.Single(win1.TitleBarButtons);
        Assert.Single(win2.TitleBarButtons);
    }

    [Fact]
    public void TrackWindowSystem_RecreatedWindowWithSameName_ReceivesMinimizeButton()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws1 = new WindowSystem("TestSystem1");
        var win1 = new DummyWindow("RecreatedWindow");
        ws1.AddWindow(win1);
        tracker.TrackWindowSystem(ws1);

        Assert.Single(win1.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, win1.TitleBarButtons.First().Icon);

        // A new window instance with the same name is created (e.g. after plugin reloads or re-instantiates window)
        var ws2 = new WindowSystem("TestSystem2");
        var win2 = new DummyWindow("RecreatedWindow");
        ws2.AddWindow(win2);
        Assert.Empty(win2.TitleBarButtons);

        tracker.TrackWindowSystem(ws2);

        Assert.Single(win2.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, win2.TitleBarButtons.First().Icon);
    }

    [Fact]
    public void TrackWindowSystem_SkipsEmptyOrWhitespaceWindowNames()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("EmptySystem");
        var emptyWin = new DummyWindow("");
        var wsWin = new DummyWindow("   ");
        ws.AddWindow(emptyWin);
        ws.AddWindow(wsWin);

        tracker.TrackWindowSystem(ws);

        var tracked = service.GetTrackedWindows();
        Assert.Empty(tracked);
    }

    [Fact]
    public void ScanPlugins_DoesNotThrow_WhenPluginManagerNotAvailable()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ex = Record.Exception((Action)(() => tracker.ScanPlugins()));
        Assert.Null(ex);
    }

    private class PluginWithWindowSystems
    {
        public WindowSystem SysProp { get; set; }
        public WindowSystem SysField;

        public PluginWithWindowSystems(WindowSystem p, WindowSystem f)
        {
            SysProp = p;
            SysField = f;
        }
    }

    [Fact]
    public void ScanObjectForWindowSystems_DiscoversWindowSystemsInPropertiesAndFields()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws1 = new WindowSystem("SysProp");
        var win1 = new DummyWindow("PropWindow");
        ws1.AddWindow(win1);

        var ws2 = new WindowSystem("SysField");
        var win2 = new DummyWindow("FieldWindow");
        ws2.AddWindow(win2);

        var plugin = new PluginWithWindowSystems(ws1, ws2);

        var scanMethod = typeof(DalamudWindowTracker).GetMethod("ScanObjectForWindowSystems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);
        scanMethod.Invoke(tracker, new object[] { plugin });

        var tracked = service.GetTrackedWindows();
        Assert.Equal(2, tracked.Count);
        Assert.Contains(tracked, t => t.WindowName == "PropWindow");
        Assert.Contains(tracked, t => t.WindowName == "FieldWindow");
    }

    private class BasePluginWithWindowSystem
    {
        private readonly WindowSystem basePrivateWs;

        public BasePluginWithWindowSystem(WindowSystem ws)
        {
            this.basePrivateWs = ws;
        }
    }

    private class DerivedPluginWithWindowSystem : BasePluginWithWindowSystem
    {
        private readonly WindowSystem derivedPrivateWs;

        public DerivedPluginWithWindowSystem(WindowSystem baseWs, WindowSystem derivedWs) : base(baseWs)
        {
            this.derivedPrivateWs = derivedWs;
        }
    }

    [Fact]
    public void ScanObjectForWindowSystems_TraversesBaseClassHierarchy()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var baseWs = new WindowSystem("BaseSys");
        baseWs.AddWindow(new DummyWindow("BaseClassWindow"));

        var derivedWs = new WindowSystem("DerivedSys");
        derivedWs.AddWindow(new DummyWindow("DerivedClassWindow"));

        var derivedPlugin = new DerivedPluginWithWindowSystem(baseWs, derivedWs);

        var scanMethod = typeof(DalamudWindowTracker).GetMethod("ScanObjectForWindowSystems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);
        scanMethod.Invoke(tracker, new object[] { derivedPlugin });

        var tracked = service.GetTrackedWindows();
        Assert.Equal(2, tracked.Count);
        Assert.Contains(tracked, t => t.WindowName == "BaseClassWindow");
        Assert.Contains(tracked, t => t.WindowName == "DerivedClassWindow");
    }

    [Fact]
    public void InjectMinimizeButton_SetsNoCollapseFlagOnManagedWindow()
    {
        var win = new DummyWindow("ManagedWindow");
        var service = new WindowManagerService();
        var tw = service.RegisterWindow(win);

        Assert.Equal(Dalamud.Bindings.ImGui.ImGuiWindowFlags.None, win.Flags & Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoCollapse);

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);

        Assert.NotEqual(Dalamud.Bindings.ImGui.ImGuiWindowFlags.None, win.Flags & Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoCollapse);
    }

    [Fact]
    public void InjectMinimizeButton_DoesNotInjectOnUnmanageableWindow()
    {
        var win = new DummyWindow("Overlay") { Flags = Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoTitleBar };
        var service = new WindowManagerService();
        var tw = service.RegisterWindow(win);

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);

        Assert.Empty(win.TitleBarButtons);
    }

    [Fact]
    public void InjectMinimizeButton_HooksExistingPluginMinimizeButton()
    {
        var win = new DummyWindow("CustomMinimizeWin");
        var originalCalled = false;
        var customButton = new TitleBarButton
        {
            Icon = FontAwesomeIcon.WindowMinimize,
            Priority = 0,
            Click = _ => { originalCalled = true; }
        };
        win.TitleBarButtons.Add(customButton);

        var service = new WindowManagerService();
        var tw = service.RegisterWindow(win);

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);

        // Clicking the plugin's own button should invoke both the plugin handler and service.Minimize
        customButton.Click?.Invoke(Dalamud.Bindings.ImGui.ImGuiMouseButton.Left);
        Assert.True(originalCalled);
        Assert.True(tw.IsMinimized);
    }

    [Fact]
    public void LoadPluginIcon_LoadsFromDiskCacheIfExists()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var fakePluginName = "TestPluginCache_" + System.Guid.NewGuid().ToString("N");
        var cachedPath = DalamudWindowTracker.GetCachedIconPath(fakePluginName);
        var dir = System.IO.Path.GetDirectoryName(cachedPath)!;
        System.IO.Directory.CreateDirectory(dir);

        var fakeBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        System.IO.File.WriteAllBytes(cachedPath, fakeBytes);

        try
        {
            // Object with empty dummy properties for reflection
            var dummyPlugin = new object();
            var loaded = tracker.LoadPluginIcon(dummyPlugin, fakePluginName, null);
            Assert.NotNull(loaded);
            Assert.Equal(fakeBytes, loaded);
        }
        finally
        {
            if (System.IO.File.Exists(cachedPath))
                System.IO.File.Delete(cachedPath);
        }
    }

    private class PluginWithNestedUi
    {
        public UiCoordinator Ui { get; }

        public PluginWithNestedUi(WindowSystem ws)
        {
            this.Ui = new UiCoordinator(ws);
        }
    }

    private class UiCoordinator
    {
        public WindowSystem SubWindowSystem { get; }

        public UiCoordinator(WindowSystem ws)
        {
            this.SubWindowSystem = ws;
        }
    }

    private class CyclicPlugin
    {
        public CyclicNode NodeA { get; } = new();

        public CyclicPlugin(WindowSystem ws)
        {
            NodeA.NodeB = new CyclicNode { NodeB = NodeA, WindowSystem = ws };
        }
    }

    private class CyclicNode
    {
        public CyclicNode? NodeB { get; set; }
        public WindowSystem? WindowSystem { get; set; }
    }

    private class PluginWithServiceDictionary
    {
        public System.Collections.Generic.Dictionary<string, object> Services { get; } = new();

        public PluginWithServiceDictionary(WindowSystem ws)
        {
            Services["WindowSystem"] = ws;
        }
    }

    private class PluginWithWindowSystemList
    {
        public System.Collections.Generic.List<WindowSystem> Systems { get; } = new();

        public PluginWithWindowSystemList(WindowSystem ws)
        {
            Systems.Add(ws);
        }
    }

    [Fact]
    public void ScanObjectForWindowSystems_DiscoversWindowSystemsInNestedSubObjects()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("NestedSys");
        var win = new DummyWindow("NestedWindow");
        ws.AddWindow(win);

        var plugin = new PluginWithNestedUi(ws);

        var scanMethod = typeof(DalamudWindowTracker).GetMethod("ScanObjectForWindowSystems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);
        scanMethod.Invoke(tracker, new object[] { plugin });

        var tracked = service.GetTrackedWindows();
        Assert.Contains(tracked, t => t.WindowName == "NestedWindow");
        Assert.Single(win.TitleBarButtons);
    }

    [Fact]
    public void ScanObjectForWindowSystems_DiscoversWindowSystemsInDictionaryAndCollections()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var wsDict = new WindowSystem("DictSys");
        var winDict = new DummyWindow("DictWindow");
        wsDict.AddWindow(winDict);

        var wsList = new WindowSystem("ListSys");
        var winList = new DummyWindow("ListWindow");
        wsList.AddWindow(winList);

        var pluginDict = new PluginWithServiceDictionary(wsDict);
        var pluginList = new PluginWithWindowSystemList(wsList);

        var scanMethod = typeof(DalamudWindowTracker).GetMethod("ScanObjectForWindowSystems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);
        scanMethod.Invoke(tracker, new object[] { pluginDict });
        scanMethod.Invoke(tracker, new object[] { pluginList });

        var tracked = service.GetTrackedWindows();
        Assert.Contains(tracked, t => t.WindowName == "DictWindow");
        Assert.Contains(tracked, t => t.WindowName == "ListWindow");
    }

    [Fact]
    public void ScanObjectForWindowSystems_HandlesCyclicReferencesGracefully()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("CyclicSys");
        var win = new DummyWindow("CyclicWindow");
        ws.AddWindow(win);

        var cyclicPlugin = new CyclicPlugin(ws);

        var scanMethod = typeof(DalamudWindowTracker).GetMethod("ScanObjectForWindowSystems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);

        // Must not throw StackOverflowException or hang
        scanMethod.Invoke(tracker, new object[] { cyclicPlugin });

        var tracked = service.GetTrackedWindows();
        Assert.Contains(tracked, t => t.WindowName == "CyclicWindow");
    }

    [Fact]
    public void InjectMinimizeButton_InjectsOnWindowWithZeroSizeOrUnconfirmedUi()
    {
        var service = new WindowManagerService();
        var winZeroSize = new DummyWindow("ZeroSizeWindow")
        {
            Size = System.Numerics.Vector2.Zero
        };
        var tw = service.RegisterWindow(winZeroSize);
        tw.HasConfirmedUi = false;

        // tw.IsManageable is false for toolbar purposes, but winZeroSize has a titlebar and should receive a minimize button
        Assert.False(tw.IsManageable);

        DalamudWindowTracker.InjectMinimizeButton(winZeroSize, tw, service);

        Assert.Single(winZeroSize.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, winZeroSize.TitleBarButtons.First().Icon);
    }

    [Fact]
    public void InjectMinimizeButton_ReinjectsIfTitleBarButtonsCleared()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("DynamicButtonsWindow");
        var tw = service.RegisterWindow(win);

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);
        Assert.Single(win.TitleBarButtons);

        // Simulate plugin clearing buttons on tab switch or dynamic redraw
        win.TitleBarButtons.Clear();
        Assert.Empty(win.TitleBarButtons);

        // Next pass re-injects the minimize button
        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);
        Assert.Single(win.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, win.TitleBarButtons.First().Icon);
    }

    [Fact]
    public void RemoveMinimizeButton_RemovesPreviouslyInjectedButton()
    {
        // Docked tabs must drop the injected button: Dalamud draws it inside the client area where it
        // collides with and hides beneath plugin controls (issue #25).
        var service = new WindowManagerService();
        var win = new DummyWindow("DockedTab");
        var tw = service.RegisterWindow(win);

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);
        Assert.Single(win.TitleBarButtons);

        DalamudWindowTracker.RemoveMinimizeButton(win);
        Assert.Empty(win.TitleBarButtons);
    }

    [Fact]
    public void RemoveMinimizeButton_AllowsReinjectionAfterUndock()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("RedockableTab");
        var tw = service.RegisterWindow(win);

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);
        DalamudWindowTracker.RemoveMinimizeButton(win);
        Assert.Empty(win.TitleBarButtons);

        // Once the window undocks, its minimize control must come back.
        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);
        Assert.Single(win.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, win.TitleBarButtons.First().Icon);
    }

    [Fact]
    public void InjectMinimizeButton_WhenWindowInDockGroup_DoesNotInject()
    {
        // A dock-group member (e.g. Glamourer docked with Penumbra) must never receive the raw button:
        // it would render inside the client area beneath plugin controls (issue #25).
        var service = new WindowManagerService();
        var win = new DummyWindow("GlamourerDocked");
        var tw = service.RegisterWindow(win);
        tw.DockGroupKey = "dock_glam_penumbra";

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);

        Assert.Empty(win.TitleBarButtons);
    }

    [Fact]
    public void InjectMinimizeButton_WhenWindowJoinsDockGroup_RemovesExistingButton()
    {
        // Reproduces the re-injection race: a floating window gets the button, then docks. Every later
        // injection path (draw loop AND the 250ms discovery tick) must strip the button, not re-add it.
        var service = new WindowManagerService();
        var win = new DummyWindow("PenumbraTab");
        var tw = service.RegisterWindow(win);

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);
        Assert.Single(win.TitleBarButtons);

        // Window becomes a dock-group member.
        tw.DockGroupKey = "dock_glam_penumbra";
        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);

        Assert.Empty(win.TitleBarButtons);
    }

    [Fact]
    public void InjectMinimizeButton_WhenWindowLeavesDockGroup_ReinjectsButton()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("UndockedTab");
        var tw = service.RegisterWindow(win);
        tw.DockGroupKey = "dock_glam_penumbra";

        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);
        Assert.Empty(win.TitleBarButtons);

        // Window undocks -> its standalone minimize control must come back.
        tw.DockGroupKey = null;
        DalamudWindowTracker.InjectMinimizeButton(win, tw, service);

        Assert.Single(win.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, win.TitleBarButtons.First().Icon);
    }

    [Fact]
    public void RemoveMinimizeButton_WhenNoButtonInjected_IsNoOp()
    {
        var win = new DummyWindow("NoButtonWindow");
        Assert.Empty(win.TitleBarButtons);

        var ex = Record.Exception(() => DalamudWindowTracker.RemoveMinimizeButton(win));
        Assert.Null(ex);
        Assert.Empty(win.TitleBarButtons);
    }

    [Fact]
    public void RemoveMinimizeButton_NullTitleBarButtons_DoesNotThrow()
    {
        var win = new DummyWindow("NullButtonsRemove");
        win.TitleBarButtons = null!;

        var ex = Record.Exception(() => DalamudWindowTracker.RemoveMinimizeButton(win));
        Assert.Null(ex);
    }

    [Fact]
    public void TryFastTrackWindow_RegistersAndInjectsNewlyAddedWindowFromKnownWindowSystem()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("FastTrackSys");
        var initialWin = new DummyWindow("InitialWindow");
        ws.AddWindow(initialWin);

        tracker.TrackWindowSystem(ws, "FastTrackPlugin", null);
        Assert.Contains(service.GetTrackedWindows(), t => t.WindowName == "InitialWindow");

        // Dynamically instantiate and add a new window to the known window system
        var dynamicWin = new DummyWindow("DynamicModalWindow");
        ws.AddWindow(dynamicWin);

        // Before fast track, service doesn't have it
        Assert.DoesNotContain(service.GetTrackedWindows(), t => t.WindowName == "DynamicModalWindow");

        // Fast track resolves it immediately without waiting for 2000ms scan
        var tw = tracker.TryFastTrackWindow("DynamicModalWindow");
        Assert.NotNull(tw);
        Assert.Equal("DynamicModalWindow", tw.WindowName);
        Assert.Equal("FastTrackPlugin", tw.PluginInternalName);
        Assert.Single(dynamicWin.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, dynamicWin.TitleBarButtons.First().Icon);
    }

    [Fact]
    public void ScanKnownWindowSystems_DiscoversDynamicallyAddedWindows()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("PeriodicSys");
        var win1 = new DummyWindow("Window1");
        ws.AddWindow(win1);

        tracker.TrackWindowSystem(ws, "PeriodicPlugin", null);

        var win2 = new DummyWindow("Window2");
        ws.AddWindow(win2);

        tracker.ScanKnownWindowSystems();

        var tracked = service.GetTrackedWindows();
        Assert.Contains(tracked, t => t.WindowName == "Window2");
        Assert.Single(win2.TitleBarButtons);
    }

    private class PluginWithThrowingUiProperty
    {
        public UiCoordinator ThrowingUi => throw new System.InvalidOperationException("Context missing");
        public WindowSystem SafeSystem { get; }

        public PluginWithThrowingUiProperty(WindowSystem ws)
        {
            SafeSystem = ws;
        }
    }

    [Fact]
    public void ScanObjectForWindowSystems_ContinuesScanning_WhenPropertyGetterThrows()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("SafeSys");
        ws.AddWindow(new DummyWindow("SafeWindow"));

        var plugin = new PluginWithThrowingUiProperty(ws);

        var scanMethod = typeof(DalamudWindowTracker).GetMethod("ScanObjectForWindowSystems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);

        // Must not throw even if a property getter throws
        var ex = Record.Exception(() => scanMethod.Invoke(tracker, new object[] { plugin }));
        Assert.Null(ex);

        var tracked = service.GetTrackedWindows();
        Assert.Contains(tracked, t => t.WindowName == "SafeWindow");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NonExistentWindow")]
    public void TryFastTrackWindow_ReturnsNull_ForUnknownOrEmptyWindowNames(string? windowName)
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("TestSys");
        ws.AddWindow(new DummyWindow("ExistingWindow"));
        tracker.TrackWindowSystem(ws);

        var tw = tracker.TryFastTrackWindow(windowName!);
        Assert.Null(tw);
    }

    #region DI & Complex Architecture Mocks

    private class MockEngineScope
    {
        public Dictionary<string, object> _resolvedServices { get; } = new();
    }

    private class MockServiceProvider : System.IServiceProvider
    {
        public MockEngineScope _root { get; }

        public MockServiceProvider(MockEngineScope root)
        {
            this._root = root;
        }

        public object? GetService(System.Type serviceType) => null;
    }

    private class MockLunaServiceManager
    {
        public MockServiceProvider Provider { get; }
        public System.Collections.Generic.HashSet<object> _ownedObjects { get; } = new();

        public MockLunaServiceManager(MockServiceProvider provider)
        {
            this.Provider = provider;
        }
    }

    private class MockGlamourerWindowSystem
    {
        private readonly WindowSystem _windowSystem;

        public MockGlamourerWindowSystem(WindowSystem ws)
        {
            this._windowSystem = ws;
        }
    }

    private class MockGlamourerPlugin
    {
        private readonly MockLunaServiceManager _services;

        public MockGlamourerPlugin(MockLunaServiceManager services)
        {
            this._services = services;
        }
    }

    private class MockDirectWindowPlugin
    {
        public DummyWindow DirectPropWindow { get; }
        private readonly DummyWindow directFieldWindow;

        public MockDirectWindowPlugin(DummyWindow propWin, DummyWindow fieldWin)
        {
            this.DirectPropWindow = propWin;
            this.directFieldWindow = fieldWin;
        }
    }

    private class DeepNode
    {
        public object? Next { get; set; }
    }

    #endregion

    [Fact]
    public void ScanObjectForWindowSystems_DiscoversGlamourerLunaArchitecture_ViaMicrosoftDi()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("GlamourerSystem");
        var win = new DummyWindow("Glamourer.Gui.MainWindow");
        ws.AddWindow(win);

        var gws = new MockGlamourerWindowSystem(ws);

        var scope = new MockEngineScope();
        scope._resolvedServices["Glamourer.Gui.GlamourerWindowSystem"] = gws;

        var provider = new MockServiceProvider(scope);
        var lunaSm = new MockLunaServiceManager(provider);
        var plugin = new MockGlamourerPlugin(lunaSm);

        var scanMethod = typeof(DalamudWindowTracker).GetMethod(
            "ScanObjectForWindowSystems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);

        scanMethod.Invoke(tracker, new object[] { plugin });

        var tracked = service.GetTrackedWindows();
        Assert.Contains(tracked, t => t.WindowName == "Glamourer.Gui.MainWindow");

        Assert.Single(win.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, win.TitleBarButtons.First().Icon);
    }

    [Fact]
    public void ScanObjectForWindowSystems_DiscoversLunaArchitecture_ViaOwnedObjects()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("PenumbraSystem");
        var win = new DummyWindow("Penumbra.Gui.MainWindow");
        ws.AddWindow(win);

        var gws = new MockGlamourerWindowSystem(ws);

        var provider = new MockServiceProvider(new MockEngineScope());
        var lunaSm = new MockLunaServiceManager(provider);
        lunaSm._ownedObjects.Add(gws);
        var plugin = new MockGlamourerPlugin(lunaSm);

        var scanMethod = typeof(DalamudWindowTracker).GetMethod(
            "ScanObjectForWindowSystems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);

        scanMethod.Invoke(tracker, new object[] { plugin });

        var tracked = service.GetTrackedWindows();
        Assert.Contains(tracked, t => t.WindowName == "Penumbra.Gui.MainWindow");

        Assert.Single(win.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, win.TitleBarButtons.First().Icon);
    }

    [Fact]
    public void ScanObjectForWindowSystems_DiscoversDirectIWindows_OnFieldsAndProperties()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var winProp = new DummyWindow("StandAlonePropWindow");
        var winField = new DummyWindow("StandAloneFieldWindow");
        var plugin = new MockDirectWindowPlugin(winProp, winField);

        var scanMethod = typeof(DalamudWindowTracker).GetMethod(
            "ScanObjectForWindowSystems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);

        scanMethod.Invoke(tracker, new object[] { plugin });

        var tracked = service.GetTrackedWindows();
        Assert.Contains(tracked, t => t.WindowName == "StandAlonePropWindow");
        Assert.Contains(tracked, t => t.WindowName == "StandAloneFieldWindow");

        Assert.Single(winProp.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, winProp.TitleBarButtons.First().Icon);
        Assert.Single(winField.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, winField.TitleBarButtons.First().Icon);
    }

    [Fact]
    public void ScanObjectForWindowSystems_TraversesDeepHierarchyUpTo6Levels()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("DeepSys");
        var win = new DummyWindow("DeepNestedWindow");
        ws.AddWindow(win);

        // 5 levels of nesting: root -> n1 -> n2 -> n3 -> n4 -> ws
        var n4 = new DeepNode { Next = ws };
        var n3 = new DeepNode { Next = n4 };
        var n2 = new DeepNode { Next = n3 };
        var n1 = new DeepNode { Next = n2 };
        var root = new DeepNode { Next = n1 };

        var scanMethod = typeof(DalamudWindowTracker).GetMethod(
            "ScanObjectForWindowSystems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);

        scanMethod.Invoke(tracker, new object[] { root });

        var tracked = service.GetTrackedWindows();
        Assert.Contains(tracked, t => t.WindowName == "DeepNestedWindow");
        Assert.Single(win.TitleBarButtons);
    }

    private class HeavyNonUiService
    {
        public System.Collections.Generic.List<string> LargeDataCollection
        {
            get => throw new System.InvalidOperationException("Non-UI service property must not be accessed!");
        }
    }

    [Fact]
    public void ScanObjectForWindowSystems_IgnoresHeavyNonUiServices_AndDoesNotTraverseLargeDataCollections()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("PenumbraSys");
        var win = new DummyWindow("Penumbra.Gui.MainWindow");
        ws.AddWindow(win);
        var gws = new MockGlamourerWindowSystem(ws);

        var scope = new MockEngineScope();
        scope._resolvedServices["Penumbra.Gui.MainWindowSystem"] = gws;
        // Non-UI service with throwing property
        scope._resolvedServices["Penumbra.Services.ActorManager"] = new HeavyNonUiService();

        var provider = new MockServiceProvider(scope);
        var lunaSm = new MockLunaServiceManager(provider);
        var plugin = new MockGlamourerPlugin(lunaSm);

        var scanMethod = typeof(DalamudWindowTracker).GetMethod(
            "ScanObjectForWindowSystems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(scanMethod);

        // Must succeed without invoking HeavyNonUiService's throwing property
        var ex = Record.Exception(() => scanMethod.Invoke(tracker, new object[] { plugin }));
        Assert.Null(ex);

        var tracked = service.GetTrackedWindows();
        Assert.Contains(tracked, t => t.WindowName == "Penumbra.Gui.MainWindow");
    }
}
