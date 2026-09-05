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
}
