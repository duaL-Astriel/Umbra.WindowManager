using System.Collections.Generic;
using System.Threading;
using Dalamud.Interface.Windowing;
using Umbra.Widgets;
using Umbra.WindowManager.Services.WindowManager;
using Umbra.WindowManager.Widgets;
using Xunit;

namespace Umbra.WindowManager.Tests;

/// <summary>
/// Per-frame window state cache and the window snapshot (issue #64). "Frames" are driven by hand through
/// <see cref="WindowManagerService.BeginFrame"/>; without it the cache is deliberately inactive.
/// </summary>
public class FrameStateCacheTests
{
    private class CountingWindow : Window
    {
        public int DrawConditionCalls;

        public CountingWindow(string name) : base(name)
        {
            this.IsOpen = true;
        }

        public override bool DrawConditions()
        {
            this.DrawConditionCalls++;
            return true;
        }

        public override void Draw() { }
    }

    private static MinimizeAllWindowsWidget CreateMinimizeAllWidget(WindowManagerService service)
    {
        var info = new WidgetInfo("UmbraMinimizeAllWindowsWidget", "Minimize All Windows", "Minimizes all windows");
        return new MinimizeAllWindowsWidget(info, null, null, service);
    }

    private static WindowManagerWidget CreateWindowManagerWidget(WindowManagerService service)
    {
        var info = new WidgetInfo("WindowManagerWidget", "Window Manager", "Window manager widget");
        return new WindowManagerWidget(info, null, null, service);
    }

    [Fact]
    public void IsOpen_WithinOneFrame_EvaluatesDrawConditionsOnce()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Cached##Cached");
        var tw = service.RegisterWindow(win);

        service.BeginFrame();
        for (var i = 0; i < 5; i++)
            Assert.True(tw.IsOpen);

        Assert.Equal(1, win.DrawConditionCalls);

        service.BeginFrame();
        Assert.True(tw.IsOpen);
        Assert.Equal(2, win.DrawConditionCalls);
    }

    [Fact]
    public void IsOpen_WithoutFrameClockStarted_IsNotCached()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Uncached##Uncached");
        var tw = service.RegisterWindow(win);

        Assert.True(tw.IsOpen);
        Assert.True(tw.IsOpen);

        Assert.Equal(2, win.DrawConditionCalls);
    }

    [Fact]
    public void IsOpen_WhenFramesStopAdvancing_FallsBackToLiveReads()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Stale##Stale");
        var tw = service.RegisterWindow(win);

        service.BeginFrame();
        Assert.True(tw.IsOpen);
        Thread.Sleep((int)WindowFrameClock.MaxFrameAgeMs + 100);

        // The monitor stopped ticking: a frozen frame number must not freeze the window state with it.
        win.IsOpen = false;
        Assert.False(tw.IsOpen);
    }

    [Fact]
    public void IsOpen_ChangedByWindowItself_IsPickedUpOnNextFrame()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Plugin##Plugin");
        var tw = service.RegisterWindow(win);

        service.BeginFrame();
        Assert.True(tw.IsOpen);

        win.IsOpen = false;
        Assert.True(tw.IsOpen);   // same frame: cached

        service.BeginFrame();
        Assert.False(tw.IsOpen);
    }

    [Fact]
    public void Minimize_WithinFrame_IsVisibleToLaterReadsThatFrame()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Min##Min");
        var tw = service.RegisterWindow(win);

        service.BeginFrame();
        Assert.True(tw.IsOpen);
        Assert.True(tw.IsManageable);

        service.Minimize(tw);

        Assert.False(tw.IsOpen);
        Assert.True(tw.IsMinimized);
        Assert.True(tw.IsManageable);
        Assert.DoesNotContain(service.GetVisibleAndMinimizedWindows(), w => !w.IsMinimized);
        Assert.False(service.AreAnyWindowsOpen);
    }

    [Fact]
    public void Restore_WithinFrame_IsVisibleToLaterReadsThatFrame()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Res##Res");
        var tw = service.RegisterWindow(win);

        service.BeginFrame();
        service.Minimize(tw);
        Assert.False(tw.IsOpen);
        Assert.False(service.AreAnyWindowsOpen);

        service.Restore(tw);

        Assert.True(tw.IsOpen);
        Assert.False(tw.IsMinimized);
        Assert.True(service.AreAnyWindowsOpen);
    }

    [Fact]
    public void MinimizeAllThenRestoreAll_WithinFrame_SeeNewStateImmediately()
    {
        var service = new WindowManagerService();
        var a = new CountingWindow("A##A");
        var b = new CountingWindow("B##B");
        var twA = service.RegisterWindow(a);
        var twB = service.RegisterWindow(b);

        service.BeginFrame();
        Assert.True(service.AreAnyWindowsOpen);

        service.MinimizeAll();
        Assert.False(twA.IsOpen);
        Assert.False(twB.IsOpen);
        Assert.False(service.AreAnyWindowsOpen);
        Assert.True(service.CanRestoreBulkMinimized);

        service.RestoreAll();
        Assert.True(twA.IsOpen);
        Assert.True(twB.IsOpen);
        Assert.True(service.AreAnyWindowsOpen);
    }

    [Fact]
    public void Close_AndIsOpenSetter_WithinFrame_InvalidateCache()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Close##Close");
        var tw = service.RegisterWindow(win);

        service.BeginFrame();
        Assert.True(tw.IsOpen);

        service.Close(tw);
        Assert.False(tw.IsOpen);

        tw.IsOpen = true;
        Assert.True(tw.IsOpen);
    }

    [Fact]
    public void IsFocused_IsCachedPerFrame_AndBringToFrontInvalidatesIt()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Focus##Focus") { IsFocused = false };
        var tw = service.RegisterWindow(win);

        service.BeginFrame();
        Assert.False(tw.IsFocused);

        win.IsFocused = true;
        Assert.False(tw.IsFocused);   // cached for this frame

        tw.BringToFront();
        Assert.True(tw.IsFocused);
    }

    [Fact]
    public void IsManageable_FollowsHasConfirmedUiChangesWithinFrame()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Ui##Ui");
        var tw = service.RegisterWindow(win);

        service.BeginFrame();
        Assert.True(tw.IsManageable);

        tw.HasConfirmedUi = false;
        Assert.False(tw.IsManageable);

        tw.HasConfirmedUi = true;
        Assert.True(tw.IsManageable);
    }

    [Fact]
    public void MonitorAndBothWidgets_EvaluateDrawConditionsOncePerFrame()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Shared##Shared");
        service.RegisterWindow(win);
        var monitor = new ImGuiContextMonitor(service);
        var windowManagerWidget = CreateWindowManagerWidget(service);
        var minimizeAllWidget = CreateMinimizeAllWidget(service);
        win.DrawConditionCalls = 0;   // the widget constructors already refreshed once, outside any frame

        service.BeginFrame();
        monitor.PopulateTrackedMap();
        monitor.UpdateUnseenFrames();          // not drawn this frame, so it asks IsOpen
        windowManagerWidget.UpdateButtons();
        minimizeAllWidget.UpdateButtonState();
        windowManagerWidget.UpdateButtons();
        minimizeAllWidget.UpdateButtonState();

        Assert.Equal(1, win.DrawConditionCalls);

        service.BeginFrame();
        monitor.PopulateTrackedMap();
        monitor.UpdateUnseenFrames();
        windowManagerWidget.UpdateButtons();
        minimizeAllWidget.UpdateButtonState();

        Assert.Equal(2, win.DrawConditionCalls);
    }

    [Fact]
    public void UpdateButtonState_WithBulkSnapshot_EvaluatesAreAnyWindowsOpenOnce()
    {
        var service = new WindowManagerService();
        var first = new CountingWindow("First##First");
        service.RegisterWindow(first);
        service.MinimizeAll();   // leaves a bulk-minimized snapshot, so CanRestoreBulkMinimized reads AreAnyWindowsOpen

        var other = new CountingWindow("Other##Other");
        service.RegisterWindow(other);
        var widget = CreateMinimizeAllWidget(service);
        other.DrawConditionCalls = 0;   // the constructor already refreshed once

        widget.UpdateButtonState();

        // No frame clock here, so nothing is cached: this counts the scans themselves, which used to be two.
        Assert.Equal(1, other.DrawConditionCalls);
    }

    [Fact]
    public void GetBulkMinimizeState_MatchesSeparateProperties()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Bulk##Bulk");
        service.RegisterWindow(win);

        service.GetBulkMinimizeState(out var anyOpen, out var canRestore);
        Assert.True(anyOpen);
        Assert.False(canRestore);

        service.MinimizeAll();
        service.GetBulkMinimizeState(out anyOpen, out canRestore);
        Assert.False(anyOpen);
        Assert.True(canRestore);
        Assert.Equal(service.AreAnyWindowsOpen, anyOpen);
        Assert.Equal(service.CanRestoreBulkMinimized, canRestore);
    }

    [Fact]
    public void Snapshot_PicksUpAddsAndRemoves()
    {
        var service = new WindowManagerService();
        var a = new CountingWindow("A##A");
        var b = new CountingWindow("B##B");
        var buffer = new List<TrackedWindow>();

        service.GetTrackedWindows(buffer);
        Assert.Empty(buffer);

        var twA = service.RegisterWindow(a);
        service.GetTrackedWindows(buffer);
        Assert.Equal([twA], buffer);

        var twB = service.RegisterWindow(b);
        service.GetTrackedWindows(buffer);
        Assert.Equal(2, buffer.Count);
        Assert.Contains(twB, buffer);

        service.UnregisterWindow(a);
        service.GetTrackedWindows(buffer);
        Assert.Equal([twB], buffer);
    }

    [Fact]
    public void Snapshot_PicksUpReplacementOfSameNameWithNewInstance()
    {
        var service = new WindowManagerService();
        var first = new CountingWindow("Same##Same");
        var second = new CountingWindow("Same##Same");
        var buffer = new List<TrackedWindow>();

        var tw1 = service.RegisterWindow(first);
        service.GetTrackedWindows(buffer);
        Assert.Same(tw1, Assert.Single(buffer));

        var tw2 = service.RegisterWindow(second);
        service.GetTrackedWindows(buffer);
        Assert.NotSame(tw1, tw2);
        Assert.Same(tw2, Assert.Single(buffer));
    }

    [Fact]
    public void Snapshot_PicksUpUnregisterForPlugin()
    {
        var service = new WindowManagerService();
        var tw = service.RegisterWindow(new CountingWindow("P##P"));
        tw.PluginInternalName = "SomePlugin";
        var buffer = new List<TrackedWindow>();

        service.GetTrackedWindows(buffer);
        Assert.Single(buffer);

        service.UnregisterWindowsForPlugin("SomePlugin");
        service.GetTrackedWindows(buffer);
        Assert.Empty(buffer);
    }

    [Fact]
    public void Snapshot_PicksUpRawImGuiRegistration()
    {
        var service = new WindowManagerService();
        var buffer = new List<TrackedWindow>();

        service.GetTrackedWindows(buffer);
        Assert.Empty(buffer);

        var raw = service.RegisterImGuiWindow("Raw Window");
        service.GetTrackedWindows(buffer);
        Assert.Same(raw, Assert.Single(buffer));
    }

    [Fact]
    public void Snapshot_DropsDeadWindowsAndRemovesThemFromTheDictionary()
    {
        var service = new WindowManagerService();
        var raw = (ImGuiTrackedWindow)service.RegisterImGuiWindow("Dying Raw");
        var buffer = new List<TrackedWindow>();

        service.GetTrackedWindows(buffer);
        Assert.Single(buffer);

        raw.UnseenFrames = ImGuiTrackedWindow.MaxUnseenRawFrames + 1;
        var generation = service.WindowSetGeneration;

        service.GetTrackedWindows(buffer);
        Assert.Empty(buffer);
        Assert.NotEqual(generation, service.WindowSetGeneration);

        // Already gone: a second pass must not need another rebuild.
        generation = service.WindowSetGeneration;
        service.GetTrackedWindows(buffer);
        Assert.Equal(generation, service.WindowSetGeneration);
    }

    [Fact]
    public void WindowSetGeneration_IsStableWhenNothingChanges()
    {
        var service = new WindowManagerService();
        var win = new CountingWindow("Stable##Stable");
        service.RegisterWindow(win);

        var generation = service.WindowSetGeneration;
        service.RegisterWindow(win);   // same instance: no change
        service.GetTrackedWindows(new List<TrackedWindow>());

        Assert.Equal(generation, service.WindowSetGeneration);
    }

    [Fact]
    public void RawImGuiWindows_AreNeverFrameCached()
    {
        var service = new WindowManagerService();
        var raw = (ImGuiTrackedWindow)service.RegisterImGuiWindow("Raw");
        raw.ObservedSize = new System.Numerics.Vector2(100, 100);

        service.BeginFrame();
        Assert.True(raw.IsManageable);
        Assert.True(raw.IsOpen);

        // The monitor rewrites these mid-frame; later reads in the same frame must see them.
        raw.ObservedSize = default;
        raw.HasConfirmedUi = false;
        Assert.False(raw.IsManageable);
        Assert.False(raw.IsOpen);

        raw.UnseenFrames = ImGuiTrackedWindow.MaxUnseenRawFrames + 1;
        Assert.False(raw.IsAlive);
    }
}
