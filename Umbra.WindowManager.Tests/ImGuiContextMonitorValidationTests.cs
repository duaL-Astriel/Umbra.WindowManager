using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Interface.Windowing;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class ImGuiContextMonitorValidationTests
{
    private class DummyWindow : Window
    {
        public DummyWindow(string name) : base(name) { }
        public override void Draw() { }
    }
    [Theory]
    [InlineData(100f, 100f, true)]
    [InlineData(1f, 1f, true)]
    [InlineData(0f, 100f, false)]
    [InlineData(100f, 0f, false)]
    [InlineData(0f, 0f, false)]
    [InlineData(-10f, 50f, false)]
    [InlineData(50f, -10f, false)]
    public void ValidateWindowDimensions_EvaluatesDimensionsCorrectly(float x, float y, bool expected)
    {
        var size = new Vector2(x, y);
        Assert.Equal(expected, ImGuiContextMonitor.ValidateWindowDimensions(size));
    }

    [Theory]
    [InlineData(10f, 10f, 0, true)]
    [InlineData(10f, 0f, 0, true)]
    [InlineData(0f, 10f, 0, true)]
    [InlineData(0f, 0f, 3, true)]
    [InlineData(0f, 0f, 2, false)]
    [InlineData(0f, 0f, 1, false)]
    [InlineData(0f, 0f, 0, false)]
    [InlineData(-1f, -1f, 0, false)]
    public void ValidateWindowContent_EvaluatesContentAndDrawCmdsCorrectly(float cx, float cy, int drawCmds, bool expected)
    {
        var contentSize = new Vector2(cx, cy);
        Assert.Equal(expected, ImGuiContextMonitor.ValidateWindowContent(contentSize, drawCmds));
    }

    [Fact]
    public void ImGuiContextMonitor_ConstructsWithTracker()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var monitor = new ImGuiContextMonitor(service, tracker);
        Assert.NotNull(monitor);
    }

    [Theory]
    // The Dalamud ImGui binding reports ImGuiWindow.DockNode as null from our OnDraw hook even for
    // docked windows, so membership is derived from the persistent DockId plus DockNodeIsVisible instead
    // (issue #25). A window currently bound to a visible dock node has a nonzero DockId and is node-visible.
    [InlineData(5u, true, true)]
    // A previously-docked, now-floating window retains its old DockId as a backup but is no longer
    // node-visible, so it must not be grouped with the windows still in that node.
    [InlineData(5u, false, false)]
    // A window that was never docked has DockId 0.
    [InlineData(0u, true, false)]
    [InlineData(0u, false, false)]
    public void IsWindowDocked_EvaluatesFromDockIdAndNodeVisibility(uint dockId, bool dockNodeVisible, bool expected)
    {
        Assert.Equal(expected, ImGuiContextMonitor.IsWindowDocked(dockId, dockNodeVisible));
    }

    [Fact]
    public void WithWindowMenuButtonSuppressed_SetsInternalNoWindowMenuButtonFlag()
    {
        // The down-arrow window-menu button on a docked tab group is hidden via the internal
        // NoWindowMenuButton dock-node flag (issue #25).
        var result = ImGuiContextMonitor.WithWindowMenuButtonSuppressed(Dalamud.Bindings.ImGui.ImGuiDockNodeFlags.None);

        Assert.True(((long)result & (long)Dalamud.Bindings.ImGui.ImGuiDockNodeFlagsPrivate.NoWindowMenuButton) != 0);
    }

    [Fact]
    public void WithWindowMenuButtonSuppressed_PreservesExistingFlagsAndIsIdempotent()
    {
        var withExisting = Dalamud.Bindings.ImGui.ImGuiDockNodeFlags.NoResize;
        var once = ImGuiContextMonitor.WithWindowMenuButtonSuppressed(withExisting);
        var twice = ImGuiContextMonitor.WithWindowMenuButtonSuppressed(once);

        // Existing flags survive, and re-applying does not change the result.
        Assert.True((once & Dalamud.Bindings.ImGui.ImGuiDockNodeFlags.NoResize) != 0);
        Assert.Equal(once, twice);
    }

    // Raw-window title-bar minimize affordances (issue #38). Raw windows get no injected title-bar button
    // (no IWindow.TitleBarButtons), but the native collapse arrow and a title-bar double-click are
    // intercepted on the raw window and routed to a clean minimize-to-toolbar. Window rect is
    // pos (100,100), size (200,150); the title bar is the top 20px band -> x in [100,300], y in [100,120].
    [Theory]
    // Native collapse arrow -> minimize, regardless of mouse position.
    [InlineData(false, true, true, false, false, 0f, 0f, true)]
    // Double-click inside the title-bar band while hovered -> minimize.
    [InlineData(false, true, false, true, true, 150f, 110f, true)]
    // Double-click below the title bar (in the client area) -> no minimize.
    [InlineData(false, true, false, true, true, 150f, 130f, false)]
    // Double-click horizontally outside the window -> no minimize.
    [InlineData(false, true, false, true, true, 350f, 110f, false)]
    // Double-click in the band but another window is hovered (occluded) -> no minimize.
    [InlineData(false, true, false, true, false, 150f, 110f, false)]
    // A single click (not a double-click) in the band -> no minimize.
    [InlineData(false, true, false, false, true, 150f, 110f, false)]
    // Already soft-hidden (minimized): neither collapse nor double-click re-triggers.
    [InlineData(true, true, true, true, true, 150f, 110f, false)]
    // No title bar (NoTitleBar flag): no title-bar affordance at all.
    [InlineData(false, false, true, true, true, 150f, 110f, false)]
    public void ShouldMinimizeRawWindowFromTitleBar_EvaluatesCollapseAndDoubleClick(
        bool isMinimized, bool hasTitleBar, bool collapsed, bool doubleClicked, bool hovered,
        float mouseX, float mouseY, bool expected)
    {
        var result = ImGuiContextMonitor.ShouldMinimizeRawWindowFromTitleBar(
            isMinimized, hasTitleBar, collapsed, doubleClicked, hovered,
            new Vector2(mouseX, mouseY),
            windowPos: new Vector2(100f, 100f),
            windowSize: new Vector2(200f, 150f),
            titleBarHeight: 20f);

        Assert.Equal(expected, result);
    }

    [Theory]
    // Active this frame (drawn before OnDraw) -> active.
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    // Active previous frame (drawn after OnDraw, or transitional frame) -> active.
    [InlineData(false, true, true)]
    // Inactive in both frames (window is closed or not submitted) -> inactive.
    [InlineData(false, false, false)]
    public void IsWindowActive_EvaluatesActiveAndWasActive(bool active, bool wasActive, bool expected)
    {
        Assert.Equal(expected, ImGuiContextMonitor.IsWindowActive(active, wasActive));
    }
    [Fact]
    public void ClearUnmanagedCache_EmptiesUnmanagedWindowCache()
    {
        var service = new WindowManagerService();
        var monitor = new ImGuiContextMonitor(service);

        var unmanagedField = typeof(ImGuiContextMonitor).GetField(
            "unmanagedWindowNames",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(unmanagedField);

        var unmanagedSet = (System.Collections.Generic.HashSet<string>)unmanagedField.GetValue(monitor)!;
        unmanagedSet.Add("StaleUnmanagedWindow");
        Assert.Single(unmanagedSet);

        monitor.ClearUnmanagedCache();
        Assert.Empty(unmanagedSet);
    }

    [Fact]
    public void ImGuiContextMonitor_SubscribesToPluginReloaded_AndClearsUnmanagedCache()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var monitor = new ImGuiContextMonitor(service, tracker);

        var unmanagedField = typeof(ImGuiContextMonitor).GetField(
            "unmanagedWindowNames",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(unmanagedField);

        var unmanagedSet = (System.Collections.Generic.HashSet<string>)unmanagedField.GetValue(monitor)!;
        unmanagedSet.Add("StaleUnmanagedWindow");
        Assert.Single(unmanagedSet);

        var reloadedEvent = typeof(DalamudWindowTracker).GetField(
            "PluginReloaded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var action = (System.Action?)reloadedEvent?.GetValue(tracker);
        Assert.NotNull(action);
        action();

        Assert.Empty(unmanagedSet);
    }

    [Fact]
    public void PopulateTrackedMap_IndexesBothWindowNameAndId()
    {
        var service = new WindowManagerService();
        var monitor = new ImGuiContextMonitor(service);
        var windowWithId = new DummyWindow("Settings###UmbraSettings") { IsOpen = true };
        service.RegisterWindow(windowWithId);

        monitor.PopulateTrackedMap();

        var trackedMapField = typeof(ImGuiContextMonitor).GetField(
            "trackedMap",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(trackedMapField);

        var trackedMap = (Dictionary<string, TrackedWindow>)trackedMapField.GetValue(monitor)!;

        // WindowName key must be present
        Assert.True(trackedMap.ContainsKey("Settings###UmbraSettings"));
        // Id key must also be present (allows ImGui raw name "UmbraSettings" to match)
        Assert.True(trackedMap.ContainsKey("UmbraSettings"));
        // Both keys must point to the same TrackedWindow instance
        Assert.Same(trackedMap["Settings###UmbraSettings"], trackedMap["UmbraSettings"]);
        Assert.Equal("UmbraSettings", trackedMap["UmbraSettings"].Id);

        // Direct method lookup via TryGetTrackedWindow
        Assert.True(monitor.TryGetTrackedWindow("Settings###UmbraSettings", out var trackedByName));
        Assert.True(monitor.TryGetTrackedWindow("UmbraSettings", out var trackedById));
        Assert.Same(trackedByName, trackedById);
    }

    [Fact]
    public void PopulateTrackedMap_WindowWithoutDistinctId_IndexesWindowName()
    {
        var service = new WindowManagerService();
        var monitor = new ImGuiContextMonitor(service);
        var normalWindow = new DummyWindow("StandardWindow") { IsOpen = true };
        service.RegisterWindow(normalWindow);

        monitor.PopulateTrackedMap();

        Assert.True(monitor.TryGetTrackedWindow("StandardWindow", out var tracked));
        Assert.NotNull(tracked);
        Assert.Equal("StandardWindow", tracked.WindowName);
    }

    [Fact]
    public void UpdateUnseenFrames_WhenIdMatchedWindowIsObserved_UnseenFramesRemainsZero()
    {
        var service = new WindowManagerService();
        var monitor = new ImGuiContextMonitor(service);
        var windowWithId = new DummyWindow("Settings###UmbraSettings") { IsOpen = true };
        var tw = service.RegisterWindow(windowWithId);

        monitor.PopulateTrackedMap();

        var seenWindowsField = typeof(ImGuiContextMonitor).GetField(
            "seenWindows",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(seenWindowsField);

        var seenWindows = (HashSet<string>)seenWindowsField.GetValue(monitor)!;

        // Simulate observing the window by its bare ID (e.g. "UmbraSettings")
        seenWindows.Add("UmbraSettings");

        Assert.Equal(0, tw.UnseenFrames);

        monitor.UpdateUnseenFrames();

        // tw.UnseenFrames should remain 0 because bare ID matched in seenWindows
        Assert.Equal(0, tw.UnseenFrames);
    }

    [Fact]
    public void UpdateUnseenFrames_WhenUnobserved_IncrementsUnseenFrames()
    {
        var service = new WindowManagerService();
        var monitor = new ImGuiContextMonitor(service);
        var windowWithId = new DummyWindow("Settings###UmbraSettings") { IsOpen = true };
        var tw = service.RegisterWindow(windowWithId);

        monitor.PopulateTrackedMap();

        monitor.UpdateUnseenFrames();

        Assert.Equal(1, tw.UnseenFrames);
    }
}


