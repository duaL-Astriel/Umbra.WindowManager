using System;
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

    [Fact]
    public void WindowManagerService_Toggle_OpenAndNotFocused_BringsToFront()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Unfocused Window") { IsOpen = true, IsFocused = false, RequestFocus = false };

        var tw = service.RegisterWindow(win);

        // Open & not focused -> BringsToFront
        service.Toggle(tw);
        Assert.False(tw.IsMinimized);
        Assert.True(win.IsOpen);
        Assert.True(win.RequestFocus);
    }

    [Fact]
    public void WindowManagerService_Toggle_ClosedNotMinimized_Restores()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Closed Window") { IsOpen = false, RequestFocus = false };

        var tw = service.RegisterWindow(win);
        Assert.False(tw.IsMinimized);
        Assert.False(tw.IsOpen);

        // Closed and not minimized -> Restore
        service.Toggle(tw);
        Assert.False(tw.IsMinimized);
        Assert.True(win.IsOpen);
        Assert.True(win.RequestFocus);
    }

    [Fact]
    public void WindowManagerService_UnregisterWindow_RemovesWindow()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Unregister Window") { IsOpen = true };

        service.RegisterWindow(win);
        Assert.Single(service.GetTrackedWindows());

        service.UnregisterWindow(win);
        Assert.Empty(service.GetTrackedWindows());
    }

    [Fact]
    public void WindowManagerService_Close_ResetsMinimizedAndClosesWindow()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Closing Window") { IsOpen = true };

        var tw = service.RegisterWindow(win);
        service.Minimize(tw);
        Assert.True(tw.IsMinimized);

        service.Close(tw);
        Assert.False(tw.IsMinimized);
        Assert.False(win.IsOpen);
        Assert.False(tw.IsOpen);
    }

    [Fact]
    public void WindowManagerService_GetVisibleAndMinimizedWindows_FiltersCorrectly()
    {
        var service = new WindowManagerService();
        var winOpen = new DummyWindow("Open Window") { IsOpen = true };
        var winMinimized = new DummyWindow("Minimized Window") { IsOpen = true };
        var winClosed = new DummyWindow("Closed Window") { IsOpen = false };
        var winEmptyTitle = new DummyWindow("###Hidden") { IsOpen = true };

        var twOpen = service.RegisterWindow(winOpen);
        var twMinimized = service.RegisterWindow(winMinimized);
        var twClosed = service.RegisterWindow(winClosed);
        var twEmptyTitle = service.RegisterWindow(winEmptyTitle);

        service.Minimize(twMinimized);

        var visibleAndMinimized = service.GetVisibleAndMinimizedWindows();
        var activeAndMinimized = service.GetActiveAndMinimizedWindows();

        Assert.Contains(twOpen, visibleAndMinimized);
        Assert.Contains(twMinimized, visibleAndMinimized);
        Assert.DoesNotContain(twClosed, visibleAndMinimized);
        Assert.DoesNotContain(twEmptyTitle, visibleAndMinimized);

        Assert.Equal(visibleAndMinimized, activeAndMinimized);
    }

    [Fact]
    public void WindowManagerService_DockGroup_MinimizeAndRestore_AffectsAllGroupMembers()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Tab 1") { IsOpen = true };
        var win2 = new DummyWindow("Tab 2") { IsOpen = true };

        var tw1 = service.RegisterWindow(win1);
        var tw2 = service.RegisterWindow(win2);

        service.RegisterDockGroup("dock_test_group", "Tab 2", new[] { tw1, tw2 });

        // Minimizing tw1 should minimize the dock group
        service.Minimize(tw1);
        Assert.False(win1.IsOpen);
        Assert.False(win2.IsOpen);
        Assert.True(tw1.IsMinimized);
        Assert.True(tw2.IsMinimized);

        // Restoring tw1 should restore all and focus Tab 2
        win2.RequestFocus = false;
        service.Restore(tw1);
        Assert.True(win1.IsOpen);
        Assert.True(win2.IsOpen);
        Assert.False(tw1.IsMinimized);
        Assert.False(tw2.IsMinimized);
        Assert.True(win2.RequestFocus);
    }

    [Fact]
    public void WindowManagerService_DockGroup_RemoveDockGroup_RestoresIndividualBehavior()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Tab 1") { IsOpen = true };
        var win2 = new DummyWindow("Tab 2") { IsOpen = true };

        var tw1 = service.RegisterWindow(win1);
        var tw2 = service.RegisterWindow(win2);

        service.RegisterDockGroup("dock_test_group", "Tab 1", new[] { tw1, tw2 });
        service.RemoveDockGroup("dock_test_group");

        // Now tw1 has DockGroupKey set, but service no longer contains dock_test_group
        service.Minimize(tw1);
        Assert.False(win1.IsOpen);
        Assert.True(tw1.IsMinimized);
        Assert.True(win2.IsOpen);
        Assert.False(tw2.IsMinimized);
    }

    [Fact]
    public void WindowManagerService_OnWindowsChanged_FiresOnMutations()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Event Window") { IsOpen = true };

        int fireCount = 0;
        service.OnWindowsChanged += () => fireCount++;

        var tw = service.RegisterWindow(win);
        Assert.Equal(1, fireCount);

        service.Minimize(tw);
        Assert.Equal(2, fireCount);

        service.Restore(tw);
        Assert.Equal(3, fireCount);

        service.Close(tw);
        Assert.Equal(4, fireCount);

        service.UnregisterWindow(win);
        Assert.Equal(5, fireCount);
    }

    [Fact]
    public void WindowManagerService_GetTrackedWindows_ExcludesGarbageCollectedWindows()
    {
        var service = new WindowManagerService();

        Func<TrackedWindow> registerWindow = () =>
        {
            var win = new DummyWindow("Temporary Window");
            return service.RegisterWindow(win);
        };

        var tw = registerWindow();
        Assert.Single(service.GetTrackedWindows());

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(tw.TryGetWindow(out _));
        Assert.Empty(service.GetTrackedWindows());
    }

    [Fact]
    public void WindowManagerService_GetVisibleAndMinimizedWindows_ExcludesGarbageCollectedMinimizedWindows()
    {
        var service = new WindowManagerService();

        TrackedWindow RegisterAndMinimize()
        {
            var win = new DummyWindow("Dead Minimized Window") { IsOpen = true };
            var tw = service.RegisterWindow(win);
            service.Minimize(tw);
            return tw;
        }

        var tracked = RegisterAndMinimize();
        Assert.True(tracked.IsMinimized);
        Assert.Single(service.GetVisibleAndMinimizedWindows());

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(tracked.TryGetWindow(out _));
        Assert.Empty(service.GetVisibleAndMinimizedWindows());
        Assert.Empty(service.GetActiveAndMinimizedWindows());
    }

    [Fact]
    public void WindowManagerService_RegisterWindow_WhenWindowReinstantiated_ReplacesStaleTrackedWindow()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("SameTitle##Id") { IsOpen = true };
        var tw1 = service.RegisterWindow(win1);

        // Same window instance returns same TrackedWindow
        var tw1Again = service.RegisterWindow(win1);
        Assert.Same(tw1, tw1Again);

        // New window instance with same WindowName replaces stale entry
        var win2 = new DummyWindow("SameTitle##Id") { IsOpen = true };
        var tw2 = service.RegisterWindow(win2);

        Assert.NotSame(tw1, tw2);
        Assert.True(tw2.TryGetWindow(out var retrieved));
        Assert.Same(win2, retrieved);
    }

    [Fact]
    public void WindowManagerService_RemoveDockGroup_ClearsDockGroupKeyOnMembers()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Tab 1");
        var win2 = new DummyWindow("Tab 2");
        var tw1 = service.RegisterWindow(win1);
        var tw2 = service.RegisterWindow(win2);

        service.RegisterDockGroup("dock_group_to_remove", "Tab 1", new[] { tw1, tw2 });
        Assert.Equal("dock_group_to_remove", tw1.DockGroupKey);
        Assert.Equal("dock_group_to_remove", tw2.DockGroupKey);

        service.RemoveDockGroup("dock_group_to_remove");

        Assert.Null(tw1.DockGroupKey);
        Assert.Null(tw2.DockGroupKey);
    }
}
