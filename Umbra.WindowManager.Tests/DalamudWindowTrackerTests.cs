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
}
