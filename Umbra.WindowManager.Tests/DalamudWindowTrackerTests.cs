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

    [Fact]
    public void ScanPlugins_ResetsIsScanning_AfterCompletion()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var field = typeof(DalamudWindowTracker).GetField("isScanning", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var value = (int)field.GetValue(tracker)!;
        Assert.Equal(0, value);
    }

    // Mirrors Dalamud.Interface.Internal.DalamudInterface, which owns the core window system
    // (Plugin Installer, Settings, Console, ...) in a private WindowSystem field rather than
    // exposing it through PluginManager.InstalledPlugins (issue #36 part 1).
    private class MockDalamudInterface
    {
        private readonly WindowSystem windowSystem;

        public MockDalamudInterface(WindowSystem ws)
        {
            this.windowSystem = ws;
        }
    }

    [Fact]
    public void ScanDalamudCoreWindows_RegistersCoreWindowsUnderDalamudContext()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var coreWs = new WindowSystem("DalamudCore");
        var installer = new DummyWindow("Plugin Installer###XlPluginInstaller");
        var console = new DummyWindow("Dalamud Console###XlLog");
        coreWs.AddWindow(installer);
        coreWs.AddWindow(console);

        var dalamudInterface = new MockDalamudInterface(coreWs);

        tracker.ScanDalamudCoreWindows(dalamudInterface);

        var tracked = service.GetTrackedWindows();
        var installerTw = tracked.Single(t => t.WindowName == "Plugin Installer###XlPluginInstaller");
        var consoleTw = tracked.Single(t => t.WindowName == "Dalamud Console###XlLog");

        Assert.Equal("Dalamud", installerTw.PluginInternalName);
        Assert.Equal("Dalamud", consoleTw.PluginInternalName);
        Assert.Single(installer.TitleBarButtons);
        Assert.Equal(FontAwesomeIcon.WindowMinimize, installer.TitleBarButtons.First().Icon);
    }

    [Fact]
    public void ScanDalamudCoreWindows_AppliesDalamudLogoIconToCoreWindows()
    {
        // Dalamud's own core windows (Plugin Installer, Settings, ...) carry no owning LocalPlugin and thus
        // no plugin icon, so they must be tagged with Dalamud's own logo instead of falling back to a
        // text monogram in the taskbar.
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var coreWs = new WindowSystem("DalamudCore");
        var installer = new DummyWindow("Plugin Installer###XlPluginInstaller");
        var settings = new DummyWindow("Dalamud Settings###XlSettings2");
        coreWs.AddWindow(installer);
        coreWs.AddWindow(settings);

        var dalamudInterface = new MockDalamudInterface(coreWs);
        var logoBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // stand-in for UIRes/logo.png

        tracker.ScanDalamudCoreWindows(dalamudInterface, logoBytes);

        var tracked = service.GetTrackedWindows();
        var installerTw = tracked.Single(t => t.WindowName == "Plugin Installer###XlPluginInstaller");
        var settingsTw = tracked.Single(t => t.WindowName == "Dalamud Settings###XlSettings2");

        Assert.Equal("Dalamud", installerTw.PluginInternalName);
        Assert.Same(logoBytes, installerTw.IconBytes);
        Assert.Same(logoBytes, settingsTw.IconBytes);
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

    private class FakeManifest
    {
        public string? InternalName { get; set; }
        public string? IconUrl { get; set; }
        public string? Dip17Channel { get; set; }
    }

    private class FakeLocalPlugin
    {
        public FakeManifest? Manifest { get; set; }
        public System.IO.FileInfo? DllFile { get; set; }
    }

    [Fact]
    public void ResolveIconUrl_PrefersManifestIconUrl()
    {
        var url = DalamudWindowTracker.ResolveIconUrl("https://example.com/a.png", "https://other/b.png", "stable", "MyPlugin");
        Assert.Equal("https://example.com/a.png", url);
    }

    [Fact]
    public void ResolveIconUrl_FallsBackToAvailableIconUrl_WhenManifestEmpty()
    {
        var url = DalamudWindowTracker.ResolveIconUrl(null, "https://other/b.png", "stable", "MyPlugin");
        Assert.Equal("https://other/b.png", url);
    }

    [Fact]
    public void ResolveIconUrl_SynthesizesDip17Url_ForMainRepoPluginsWithoutStaticIcon()
    {
        // Issue #37: main-repo (DIP17) plugins carry no static IconUrl; Dalamud itself derives the icon
        // URL from the plugin's Dip17Channel. Mirror that so those plugins show real icons, not monograms.
        var url = DalamudWindowTracker.ResolveIconUrl(null, null, "stable", "SamplePlugin");
        Assert.Equal("https://raw.githubusercontent.com/goatcorp/PluginDistD17/main/stable/SamplePlugin/images/icon.png", url);
    }

    [Fact]
    public void ResolveIconUrl_ReturnsNull_WhenNoStaticUrlAndNoChannel()
    {
        Assert.Null(DalamudWindowTracker.ResolveIconUrl(null, null, null, "SamplePlugin"));
    }

    [Fact]
    public void ResolvePluginContext_DoesNotNegativeCache_WhenIconUnresolvedAtScanTime()
    {
        // Issue #37: when the icon can't be resolved yet (plugin metadata/network not ready at the first
        // scan tick), the null result must NOT be cached permanently. A later tick, once the icon is
        // available on disk, must be able to pick it up instead of being blocked by a stale null entry.
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var name = "LateIconPlugin_" + System.Guid.NewGuid().ToString("N");
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
        System.IO.Directory.CreateDirectory(tempDir);

        var fakePlugin = new FakeLocalPlugin
        {
            Manifest = new FakeManifest { InternalName = name, IconUrl = null, Dip17Channel = null },
            DllFile = new System.IO.FileInfo(System.IO.Path.Combine(tempDir, "plugin.dll")),
        };

        var cachedPath = DalamudWindowTracker.GetCachedIconPath(name);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cachedPath)!);

        try
        {
            // First scan: nothing on disk, no URL to synthesize -> icon unresolved.
            var first = tracker.ResolvePluginContext(fakePlugin);
            Assert.Null(first.IconBytes);

            // Icon becomes available on disk (e.g. Dalamud finished downloading/caching it).
            var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
            System.IO.File.WriteAllBytes(cachedPath, bytes);

            // Second scan must re-resolve rather than return a permanently cached null.
            var second = tracker.ResolvePluginContext(fakePlugin);
            Assert.Equal(bytes, second.IconBytes);
        }
        finally
        {
            if (System.IO.File.Exists(cachedPath)) System.IO.File.Delete(cachedPath);
            try { System.IO.Directory.Delete(tempDir, true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public void ApplyDownloadedIcon_PropagatesToKnownWindowSystem_SoLaterWindowsGetIcon()
    {
        // Issue #37 (race): once an icon is downloaded in the background, a window opened afterwards must
        // still receive it. That requires refreshing the cached plugin context on known window systems,
        // not just the windows tracked at download time.
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws = new WindowSystem("IconRaceSys");
        var initialWin = new DummyWindow("InitialIconWin");
        ws.AddWindow(initialWin);
        tracker.TrackWindowSystem(ws, "RacePlugin", null); // icon not downloaded yet

        var iconBytes = new byte[] { 9, 8, 7 };
        tracker.ApplyDownloadedIcon("RacePlugin", iconBytes);

        // The window tracked at download time receives the icon.
        var initTw = service.GetTrackedWindows().Single(t => t.WindowName == "InitialIconWin");
        Assert.Same(iconBytes, initTw.IconBytes);

        // A window added AFTER the download must also receive it via the fast-path scan.
        var lateWin = new DummyWindow("LateIconWin");
        ws.AddWindow(lateWin);
        tracker.ScanKnownWindowSystems();

        var lateTw = service.GetTrackedWindows().Single(t => t.WindowName == "LateIconWin");
        Assert.Same(iconBytes, lateTw.IconBytes);
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

    [Fact]
    public void UntrackPlugin_RemovesKnownWindowSystems_UnregistersWindows_AndClearsIconCache()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var wsA = new WindowSystem("SysA");
        var winA = new DummyWindow("WindowA");
        wsA.AddWindow(winA);

        var wsB = new WindowSystem("SysB");
        var winB = new DummyWindow("WindowB");
        wsB.AddWindow(winB);

        var iconA = new byte[] { 1, 2, 3 };
        var iconB = new byte[] { 4, 5, 6 };

        tracker.TrackWindowSystem(wsA, "PluginA", iconA);
        tracker.TrackWindowSystem(wsB, "PluginB", iconB);

        Assert.Equal(2, service.GetTrackedWindows().Count);
        Assert.NotNull(tracker.TryFastTrackWindow("WindowA"));
        Assert.NotNull(tracker.TryFastTrackWindow("WindowB"));

        // Untrack PluginA
        tracker.UntrackPlugin("PluginA");

        // WindowA should no longer be tracked in WindowManagerService
        var tracked = service.GetTrackedWindows();
        Assert.Single(tracked);
        Assert.Equal("WindowB", tracked[0].WindowName);

        // WindowA should no longer be fast-trackable from known window systems
        Assert.Null(tracker.TryFastTrackWindow("WindowA"));
        Assert.NotNull(tracker.TryFastTrackWindow("WindowB"));

        // ScanKnownWindowSystems should not re-add WindowA
        tracker.ScanKnownWindowSystems();
        Assert.Single(service.GetTrackedWindows());
    }

    private class FakeLocalPluginWithInstance
    {
        public FakeManifest? Manifest { get; set; }
        public System.IO.FileInfo? DllFile { get; set; }
        internal object? instance;
    }

    private class PluginHost
    {
        public WindowSystem Sys { get; set; }
        public PluginHost(WindowSystem ws) => Sys = ws;
    }

    [Fact]
    public void ScanInstalledPlugins_WhenPluginReloads_UntracksOldStateAndRegistersNewState()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ws1 = new WindowSystem("Sys1");
        var win1 = new DummyWindow("ReloadableWin");
        ws1.AddWindow(win1);
        var host1 = new PluginHost(ws1);

        var plugin = new FakeLocalPluginWithInstance
        {
            Manifest = new FakeManifest { InternalName = "HotReloadPlugin" },
            instance = host1
        };

        tracker.ScanInstalledPlugins(new[] { plugin }, null);

        var tracked1 = service.GetTrackedWindows().Single(t => t.WindowName == "ReloadableWin");
        Assert.True(tracked1.TryGetWindow(out var alive1) && ReferenceEquals(alive1, win1));
        Assert.Single(win1.TitleBarButtons);

        // Simulate plugin reload: new instance host2 with new window system and new window instance
        var ws2 = new WindowSystem("Sys2");
        var win2 = new DummyWindow("ReloadableWin");
        ws2.AddWindow(win2);
        var host2 = new PluginHost(ws2);
        plugin.instance = host2;

        tracker.ScanInstalledPlugins(new[] { plugin }, null);

        // WindowManager should now track win2 instead of win1
        var tracked2 = service.GetTrackedWindows().Single(t => t.WindowName == "ReloadableWin");
        Assert.True(tracked2.TryGetWindow(out var alive2) && ReferenceEquals(alive2, win2));
        Assert.Single(win2.TitleBarButtons);

        // Fast-tracking should resolve win2, not win1
        var fastTracked = tracker.TryFastTrackWindow("ReloadableWin");
        Assert.NotNull(fastTracked);
        Assert.True(fastTracked.TryGetWindow(out var fastAlive) && ReferenceEquals(fastAlive, win2));
    }

    [Fact]
    public void ScanInstalledPlugins_WhenPluginUninstalled_UntracksPluginWindows()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var wsA = new WindowSystem("SysA");
        var winA = new DummyWindow("WinA");
        wsA.AddWindow(winA);
        var hostA = new PluginHost(wsA);

        var wsB = new WindowSystem("SysB");
        var winB = new DummyWindow("WinB");
        wsB.AddWindow(winB);
        var hostB = new PluginHost(wsB);

        var pluginA = new FakeLocalPluginWithInstance
        {
            Manifest = new FakeManifest { InternalName = "PluginA" },
            instance = hostA
        };
        var pluginB = new FakeLocalPluginWithInstance
        {
            Manifest = new FakeManifest { InternalName = "PluginB" },
            instance = hostB
        };

        // First scan: both plugins present
        tracker.ScanInstalledPlugins(new[] { pluginA, pluginB }, null);
        Assert.Equal(2, service.GetTrackedWindows().Count);

        // Second scan: PluginA uninstalled (only pluginB present)
        tracker.ScanInstalledPlugins(new[] { pluginB }, null);

        var remaining = service.GetTrackedWindows();
        Assert.Single(remaining);
        Assert.Equal("WinB", remaining[0].WindowName);
        Assert.Null(tracker.TryFastTrackWindow("WinA"));
    }

    private class MockPluginManagerWithEvent
    {
        public event System.Action? OnInstalledPluginsChanged;
        public void FireInstalledPluginsChanged() => OnInstalledPluginsChanged?.Invoke();
    }

    [Fact]
    public void HookLifecycleEvents_SubscribesToOnInstalledPluginsChanged_AndUnsubscribesOnDispose()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var mockPm = new MockPluginManagerWithEvent();

        var hookMethod = typeof(DalamudWindowTracker).GetMethod(
            "TryHookPluginManagerEvents",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(hookMethod);

        hookMethod.Invoke(tracker, new object[] { mockPm });

        var pmField = typeof(DalamudWindowTracker).GetField(
            "hookedPluginManager",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(pmField);
        Assert.Same(mockPm, pmField.GetValue(tracker));

        tracker.Dispose();
        Assert.Null(pmField.GetValue(tracker));
    }

    private class MockPluginInterfaceWithEvent
    {
        public event Dalamud.Plugin.IDalamudPluginInterface.ActivePluginsChangedDelegate? ActivePluginsChanged;
        public void FireActivePluginsChanged(Dalamud.Plugin.IActivePluginsChangedEventArgs args) => ActivePluginsChanged?.Invoke(args);
    }

    [Fact]
    public void HookLifecycleEvents_SubscribesToActivePluginsChanged_AndUnsubscribesOnDispose()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var mockPi = new MockPluginInterfaceWithEvent();

        var hookMethod = typeof(DalamudWindowTracker).GetMethod(
            "TryHookPluginInterfaceEvents",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(hookMethod);

        hookMethod.Invoke(tracker, new object[] { mockPi });

        var piField = typeof(DalamudWindowTracker).GetField(
            "hookedPluginInterface",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(piField);
        Assert.Same(mockPi, piField.GetValue(tracker));

        tracker.Dispose();
        Assert.Null(piField.GetValue(tracker));
    }

    [Fact]
    public void HookLifecycleEvents_PreferredInterface_ReplacesExistingHook()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var mockPi1 = new MockPluginInterfaceWithEvent();
        var mockPi2Preferred = new MockPluginInterfaceWithEvent();

        tracker.TryHookPluginInterfaceEvents(mockPi1);

        var piField = typeof(DalamudWindowTracker).GetField(
            "hookedPluginInterface",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(piField);
        Assert.Same(mockPi1, piField.GetValue(tracker));

        // Preferred interface replaces the earlier one
        tracker.TryHookPreferredPluginInterfaceEvents(mockPi2Preferred);
        Assert.Same(mockPi2Preferred, piField.GetValue(tracker));

        tracker.Dispose();
        Assert.Null(piField.GetValue(tracker));
    }

    [Fact]
    public void OnActivePluginsChanged_TriggersScanPlugins_AndLeavesIsScanningZero()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var mockPi = new MockPluginInterfaceWithEvent();
        tracker.TryHookPluginInterfaceEvents(mockPi);

        mockPi.FireActivePluginsChanged(null!);

        var isScanningField = typeof(DalamudWindowTracker).GetField(
            "isScanning",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(isScanningField);
        Assert.Equal(0, (int)isScanningField.GetValue(tracker)!);
    }

    [Fact]
    public void OnInstalledPluginsChanged_TriggersScanPlugins_AndLeavesIsScanningZero()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var mockPm = new MockPluginManagerWithEvent();
        tracker.TryHookPluginManagerEvents(mockPm);

        mockPm.FireInstalledPluginsChanged();

        var isScanningField = typeof(DalamudWindowTracker).GetField(
            "isScanning",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(isScanningField);
        Assert.Equal(0, (int)isScanningField.GetValue(tracker)!);
    }

    [Fact]
    public void ScanInstalledPlugins_WhenPluginReloads_InvalidatesIconCache()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var iconPath = DalamudWindowTracker.GetCachedIconPath("ReloadIconPlugin");
        var dir = System.IO.Path.GetDirectoryName(iconPath)!;
        System.IO.Directory.CreateDirectory(dir);

        var oldIcon = new byte[] { 1, 1, 1 };
        var newIcon = new byte[] { 2, 2, 2 };

        System.IO.File.WriteAllBytes(iconPath, oldIcon);

        try
        {
            var ws1 = new WindowSystem("Sys1");
            ws1.AddWindow(new DummyWindow("W"));
            var host1 = new PluginHost(ws1);
            var plugin = new FakeLocalPluginWithInstance
            {
                Manifest = new FakeManifest { InternalName = "ReloadIconPlugin" },
                instance = host1
            };

            tracker.ScanInstalledPlugins(new[] { plugin }, null);

            var tracked1 = service.GetTrackedWindows().Single(t => t.WindowName == "W");
            Assert.Equal(oldIcon, tracked1.IconBytes);

            // Now update the icon on disk and reload plugin
            System.IO.File.WriteAllBytes(iconPath, newIcon);

            var ws2 = new WindowSystem("Sys2");
            ws2.AddWindow(new DummyWindow("W"));
            var host2 = new PluginHost(ws2);
            plugin.instance = host2;

            tracker.ScanInstalledPlugins(new[] { plugin }, null);

            var tracked2 = service.GetTrackedWindows().Single(t => t.WindowName == "W");
            Assert.Equal(newIcon, tracked2.IconBytes);
        }
        finally
        {
            if (System.IO.File.Exists(iconPath))
                System.IO.File.Delete(iconPath);
        }
    }

    [Fact]
    public void ScanInstalledPlugins_WhenPluginReAddedAfterUnload_FiresPluginReloaded()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var reloadedFired = 0;
        tracker.PluginReloaded += () => reloadedFired++;

        var ws1 = new WindowSystem("Sys1");
        var win1 = new DummyWindow("Win1");
        ws1.AddWindow(win1);
        var host1 = new PluginHost(ws1);

        var plugin = new FakeLocalPluginWithInstance
        {
            Manifest = new FakeManifest { InternalName = "ReloadPlugin" },
            instance = host1
        };

        // First scan - plugin is newly loaded
        tracker.ScanInstalledPlugins(new[] { plugin }, null);
        Assert.Equal(1, reloadedFired);

        // Second scan - plugin unloaded (instance = null)
        plugin.instance = null;
        tracker.ScanInstalledPlugins(new[] { plugin }, null);
        Assert.Equal(2, reloadedFired);

        // Third scan - plugin loaded again with new host
        var ws2 = new WindowSystem("Sys2");
        var win2 = new DummyWindow("Win1");
        ws2.AddWindow(win2);
        plugin.instance = new PluginHost(ws2);
        tracker.ScanInstalledPlugins(new[] { plugin }, null);
        Assert.Equal(3, reloadedFired);
    }

    private class MockUmbraWindow : Umbra.Windows.IWindow
    {
        public System.Numerics.Vector2 Position { get; set; } = new(100, 150);
        public System.Numerics.Vector2 Size { get; set; } = new(400, 300);
        public bool IsClosed { get; set; }
        public bool IsMinimized { get; set; }
        public bool IsFocused { get; set; }
        public bool IsHovered { get; set; }
        public bool CloseCalled { get; private set; }
        public int RenderCallCount { get; private set; }

        public event Action? RequestClose;

        public void Close()
        {
            this.CloseCalled = true;
            this.IsClosed = true;
            this.RequestClose?.Invoke();
        }

        public void Render(string instanceId)
        {
            this.RenderCallCount++;
        }

        public void Dispose() { }
    }

    private class FakeUmbraWindowManager
    {
        public Dictionary<string, Umbra.Windows.IWindow> _instances { get; } = new();
        public event Action<Umbra.Windows.IWindow>? OnWindowOpened;
        public event Action<Umbra.Windows.IWindow>? OnWindowClosed;

        public void Open(string instanceId, Umbra.Windows.IWindow window)
        {
            this._instances[instanceId] = window;
            this.OnWindowOpened?.Invoke(window);
        }

        public void Close(string instanceId)
        {
            if (this._instances.Remove(instanceId, out var window))
            {
                this.OnWindowClosed?.Invoke(window);
            }
        }
    }

    [Fact]
    public void ScanUmbra_TryLoadUmbraCoreIcon_LoadsEmbeddedLogoBytes()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var icon = tracker.TryLoadUmbraCoreIcon();

        Assert.NotNull(icon);
        Assert.NotEmpty(icon);
        var iconCached = tracker.TryLoadUmbraCoreIcon();
        Assert.Same(icon, iconCached);
    }

    [Fact]
    public void ScanUmbra_ScanUmbraCoreWindows_RegistersWindowsUnderUmbraWithLogo()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        var mockWindow = new MockUmbraWindow();
        fakeWm._instances["UmbraSettings"] = mockWindow;
        var logoBytes = new byte[] { 1, 2, 3 };

        tracker.ScanUmbraCoreWindows(fakeWm, logoBytes);

        var tracked = service.GetTrackedWindows();
        var tw = Assert.Single(tracked);
        Assert.Equal("UmbraSettings", tw.Id);
        Assert.Equal("Umbra", tw.PluginInternalName);
        Assert.Same(logoBytes, tw.IconBytes);
        Assert.True(tw.TryGetWindow(out var win));
        Assert.IsType<UmbraWindowAdapter>(win);
        var adapter = (UmbraWindowAdapter)win;
        Assert.Same(mockWindow, adapter.UnderlyingWindow);
    }

    [Fact]
    public void ScanUmbra_ScanUmbraCoreWindows_WiresIsBeingMinimizedToTrackedWindow()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        var mockWindow = new MockUmbraWindow();
        fakeWm._instances["UmbraSettings"] = mockWindow;

        tracker.ScanUmbraCoreWindows(fakeWm, new byte[] { 1 });

        var tw = service.GetTrackedWindows().Single();
        Assert.True(tw.TryGetWindow(out var win));
        var adapter = (UmbraWindowAdapter)win;

        // When tw.IsMinimized is true, setting adapter.IsOpen = false should minimize rather than close
        tw.IsMinimized = true;
        adapter.IsOpen = false;

        Assert.True(mockWindow.IsMinimized);
        Assert.False(mockWindow.CloseCalled);
        Assert.NotNull(adapter.Proxy);
        Assert.True(adapter.Proxy.IsHidden);
        Assert.False(adapter.IsOpen);
    }

    [Fact]
    public void ScanUmbra_TryFastTrackWindow_ResolvesUmbraWindow()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        var mockWindow = new MockUmbraWindow();
        fakeWm._instances["UmbraSettings"] = mockWindow;

        tracker.ScanUmbraCoreWindows(fakeWm, new byte[] { 4, 5 });

        var tw = tracker.TryFastTrackWindow("UmbraSettings");
        Assert.NotNull(tw);
        Assert.Equal("Umbra", tw.PluginInternalName);
        Assert.Equal("UmbraSettings", tw.Id);
        Assert.NotNull(tw.IconBytes);
    }

    [Fact]
    public void ScanUmbra_UntrackPlugin_Umbra_ClearsWindowsAndState()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        fakeWm._instances["UmbraSettings"] = new MockUmbraWindow();

        tracker.ScanUmbraCoreWindows(fakeWm, new byte[] { 1 });
        Assert.Single(service.GetTrackedWindows());

        tracker.UntrackPlugin("Umbra");

        Assert.Empty(service.GetTrackedWindows());

        // Further events on old fakeWm should not register
        fakeWm.Open("NewWindow", new MockUmbraWindow());
        Assert.Empty(service.GetTrackedWindows());
    }

    [Fact]
    public void ScanUmbra_Events_DynamicallyRegisterAndUnregisterWindows()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        var logoBytes = new byte[] { 9, 9 };

        tracker.ScanUmbraCoreWindows(fakeWm, logoBytes);
        Assert.Empty(service.GetTrackedWindows());

        var win1 = new MockUmbraWindow();
        fakeWm.Open("WidgetBrowser", win1);

        var tracked = service.GetTrackedWindows();
        var tw = Assert.Single(tracked);
        Assert.Equal("WidgetBrowser", tw.Id);
        Assert.Equal("Umbra", tw.PluginInternalName);

        fakeWm.Close("WidgetBrowser");
        Assert.Empty(service.GetTrackedWindows());
    }

    [Fact]
    public void ScanUmbra_ScanUmbraWindows_OutsideGame_DoesNotThrow()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var ex = Record.Exception(() => tracker.ScanUmbraWindows());
        Assert.Null(ex);
    }

    [Fact]
    public void ScanUmbra_ScanUmbraCoreWindows_InstallsProxyInInstancesDictionary()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        var mockWindow = new MockUmbraWindow();
        fakeWm._instances["UmbraSettings"] = mockWindow;

        tracker.ScanUmbraCoreWindows(fakeWm, new byte[] { 1 });

        Assert.True(fakeWm._instances.ContainsKey("UmbraSettings"));
        Assert.IsType<UmbraWindowProxy>(fakeWm._instances["UmbraSettings"]);
        var proxy = (UmbraWindowProxy)fakeWm._instances["UmbraSettings"];
        Assert.Same(mockWindow, proxy.UnderlyingWindow);
    }

    [Fact]
    public void ScanUmbra_MinimizingAndRestoring_SuppressesAndResumesRenderingViaProxy()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        var mockWindow = new MockUmbraWindow();
        fakeWm._instances["UmbraSettings"] = mockWindow;

        tracker.ScanUmbraCoreWindows(fakeWm, new byte[] { 1 });

        var tw = service.GetTrackedWindows().Single();
        Assert.True(tw.TryGetWindow(out var win));
        var adapter = (UmbraWindowAdapter)win;

        // When minimized:
        service.Minimize(tw);
        Assert.True(tw.IsMinimized);
        Assert.False(adapter.IsOpen);

        var proxy = Assert.IsType<UmbraWindowProxy>(fakeWm._instances["UmbraSettings"]);
        Assert.True(proxy.IsHidden);

        // Umbra's OnDraw calls Render on the instance in _instances
        fakeWm._instances["UmbraSettings"].Render("UmbraSettings");
        Assert.Equal(0, mockWindow.RenderCallCount);

        // When restored:
        service.Restore(tw);
        Assert.False(tw.IsMinimized);
        Assert.True(adapter.IsOpen);
        Assert.False(proxy.IsHidden);

        // Umbra's OnDraw calls Render on the instance in _instances again
        fakeWm._instances["UmbraSettings"].Render("UmbraSettings");
        Assert.Equal(1, mockWindow.RenderCallCount);
    }

    [Fact]
    public void ScanUmbra_UntrackPlugin_Umbra_RestoresRawWindowInInstancesDictionary()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        var mockWindow = new MockUmbraWindow();
        fakeWm._instances["UmbraSettings"] = mockWindow;

        tracker.ScanUmbraCoreWindows(fakeWm, new byte[] { 1 });
        Assert.IsType<UmbraWindowProxy>(fakeWm._instances["UmbraSettings"]);

        tracker.UntrackPlugin("Umbra");

        // Proxy must be unwrapped back to raw window
        Assert.Same(mockWindow, fakeWm._instances["UmbraSettings"]);
    }

    [Fact]
    public void ScanUmbra_ScanUmbraCoreWindows_WhenWindowIsMinimized_MinimizesTrackedWindowAndHidesProxy()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        var mockWindow = new MockUmbraWindow { IsMinimized = true };
        fakeWm._instances["UmbraSettings"] = mockWindow;

        tracker.ScanUmbraCoreWindows(fakeWm, new byte[] { 1 });

        var tw = service.GetTrackedWindows().Single();
        Assert.True(tw.IsMinimized);
        Assert.False(tw.IsOpen);

        var proxy = Assert.IsType<UmbraWindowProxy>(fakeWm._instances["UmbraSettings"]);
        Assert.True(proxy.IsHidden);

        fakeWm._instances["UmbraSettings"].Render("UmbraSettings");
        Assert.Equal(0, mockWindow.RenderCallCount);
    }

    [Fact]
    public void ScanUmbra_ScanUmbraCoreWindows_ExistingWindowBecomesMinimized_MinimizesTrackedWindow()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        var mockWindow = new MockUmbraWindow { IsMinimized = false };
        fakeWm._instances["UmbraSettings"] = mockWindow;

        // Initial scan: window is open and unminimized
        tracker.ScanUmbraCoreWindows(fakeWm, new byte[] { 1 });
        var tw = service.GetTrackedWindows().Single();
        Assert.False(tw.IsMinimized);

        // User clicks title bar minimize button on Umbra window
        mockWindow.IsMinimized = true;

        // Next scan tick reconciles native minimize
        tracker.ScanUmbraCoreWindows(fakeWm, new byte[] { 1 });

        Assert.True(tw.IsMinimized);
        Assert.False(tw.IsOpen);

        var proxy = Assert.IsType<UmbraWindowProxy>(fakeWm._instances["UmbraSettings"]);
        Assert.True(proxy.IsHidden);

        fakeWm._instances["UmbraSettings"].Render("UmbraSettings");
        Assert.Equal(0, mockWindow.RenderCallCount);
    }

    [Fact]
    public void ScanUmbra_TryFastTrackWindow_WhenWindowIsMinimized_MinimizesTrackedWindow()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var fakeWm = new FakeUmbraWindowManager();
        var mockWindow = new MockUmbraWindow { IsMinimized = false };
        fakeWm._instances["UmbraSettings"] = mockWindow;

        tracker.ScanUmbraCoreWindows(fakeWm, new byte[] { 1 });

        // Umbra title bar minimizes window
        mockWindow.IsMinimized = true;

        var tw = tracker.TryFastTrackWindow("UmbraSettings");
        Assert.NotNull(tw);
        Assert.True(tw.IsMinimized);
        Assert.False(tw.IsOpen);
    }
}





