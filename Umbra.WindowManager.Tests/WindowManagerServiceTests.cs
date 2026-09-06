using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
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
        var winNoTitleBar = new DummyWindow("Overlay NoTitleBar") { IsOpen = true, Flags = ImGuiWindowFlags.NoTitleBar };
        var winNoDecoration = new DummyWindow("Overlay NoDecoration") { IsOpen = true, Flags = ImGuiWindowFlags.NoDecoration };
        var winNoInputs = new DummyWindow("Overlay NoInputs") { IsOpen = true, Flags = ImGuiWindowFlags.NoInputs };
        var winClickthrough = new DummyWindow("Overlay Clickthrough") { IsOpen = true, IsClickthrough = true };

        var twOpen = service.RegisterWindow(winOpen);
        var twMinimized = service.RegisterWindow(winMinimized);
        var twClosed = service.RegisterWindow(winClosed);
        var twEmptyTitle = service.RegisterWindow(winEmptyTitle);
        var twNoTitleBar = service.RegisterWindow(winNoTitleBar);
        var twNoDecoration = service.RegisterWindow(winNoDecoration);
        var twNoInputs = service.RegisterWindow(winNoInputs);
        var twClickthrough = service.RegisterWindow(winClickthrough);

        service.Minimize(twMinimized);

        var visibleAndMinimized = service.GetVisibleAndMinimizedWindows();
        var activeAndMinimized = service.GetActiveAndMinimizedWindows();

        Assert.Contains(twOpen, visibleAndMinimized);
        Assert.Contains(twMinimized, visibleAndMinimized);
        Assert.DoesNotContain(twClosed, visibleAndMinimized);
        Assert.DoesNotContain(twEmptyTitle, visibleAndMinimized);
        Assert.DoesNotContain(twNoTitleBar, visibleAndMinimized);
        Assert.DoesNotContain(twNoDecoration, visibleAndMinimized);
        Assert.DoesNotContain(twNoInputs, visibleAndMinimized);
        Assert.DoesNotContain(twClickthrough, visibleAndMinimized);

        Assert.Equal(visibleAndMinimized, activeAndMinimized);
    }

    [Fact]
    public void WindowManagerService_GetVisibleAndMinimizedWindows_MinimizedWindowWithOnlyId_IsRetained()
    {
        var service = new WindowManagerService();
        var winOnlyId = new DummyWindow("###orchestrion_miniplayer") { IsOpen = true };
        var tw = service.RegisterWindow(winOnlyId);

        // Open window with no clean title is ignored (overlay behavior)
        var visible = service.GetVisibleAndMinimizedWindows();
        Assert.DoesNotContain(tw, visible);

        // But once minimized, it must be retained so the user can restore it
        service.Minimize(tw);
        visible = service.GetVisibleAndMinimizedWindows();
        Assert.Contains(tw, visible);
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
    public void WindowManagerService_CloseDockGroup_ClosesAllMembers()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Tab 1") { IsOpen = true };
        var win2 = new DummyWindow("Tab 2") { IsOpen = true };
        var tw1 = service.RegisterWindow(win1);
        var tw2 = service.RegisterWindow(win2);

        service.RegisterDockGroup("dock_close_all", "Tab 1", new[] { tw1, tw2 });

        service.CloseDockGroup("dock_close_all");

        Assert.False(win1.IsOpen);
        Assert.False(win2.IsOpen);
        Assert.False(tw1.IsMinimized);
        Assert.False(tw2.IsMinimized);
    }

    [Fact]
    public void WindowManagerService_RegisterDockGroup_ReusesGroupWhenUnchanged()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Tab 1") { IsOpen = true };
        var win2 = new DummyWindow("Tab 2") { IsOpen = true };
        var tw1 = service.RegisterWindow(win1);
        var tw2 = service.RegisterWindow(win2);

        service.RegisterDockGroup("dock_reuse", "Tab 1", new[] { tw1, tw2 });
        var first = service.PeekDockGroup("dock_reuse");
        Assert.NotNull(first);

        // Same members + active tab must not allocate a new DockGroup every frame (issue #6).
        service.RegisterDockGroup("dock_reuse", "Tab 1", new[] { tw1, tw2 });
        Assert.Same(first, service.PeekDockGroup("dock_reuse"));

        // A changed active tab replaces the group.
        service.RegisterDockGroup("dock_reuse", "Tab 2", new[] { tw1, tw2 });
        var second = service.PeekDockGroup("dock_reuse");
        Assert.NotSame(first, second);

        // A changed member set replaces the group.
        service.RegisterDockGroup("dock_reuse", "Tab 2", new[] { tw1 });
        Assert.NotSame(second, service.PeekDockGroup("dock_reuse"));
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

    [Fact]
    public void WindowManagerService_ZeroAllocOverloads_PopulateProvidedLists()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Window 1") { IsOpen = true };
        var win2 = new DummyWindow("Window 2") { IsOpen = false };
        var win3 = new DummyWindow("Window 3") { IsOpen = false };

        var tw1 = service.RegisterWindow(win1);
        var tw2 = service.RegisterWindow(win2);
        var tw3 = service.RegisterWindow(win3);
        service.Minimize(tw2);

        var trackedBuffer = new List<TrackedWindow> { tw1 };
        service.GetTrackedWindows(trackedBuffer);
        Assert.Equal(3, trackedBuffer.Count);

        var visibleBuffer = new List<TrackedWindow>();
        service.GetVisibleAndMinimizedWindows(visibleBuffer);
        Assert.Equal(2, visibleBuffer.Count);
        Assert.Contains(visibleBuffer, w => w.WindowName == "Window 1");
        Assert.Contains(visibleBuffer, w => w.WindowName == "Window 2");
        Assert.DoesNotContain(visibleBuffer, w => w.WindowName == "Window 3");

        var activeBuffer = new List<TrackedWindow>();
        service.GetActiveAndMinimizedWindows(activeBuffer);
        Assert.Equal(2, activeBuffer.Count);
    }

    [Fact]
    public void WindowManagerService_GetVisibleAndMinimizedWindows_FiltersOutOverlaysAndNonInteractiveWindows()
    {
        var service = new WindowManagerService();
        var normalWin = new DummyWindow("Normal Window") { IsOpen = true };
        var noTitleBarWin = new DummyWindow("WaitOverlay") { IsOpen = true, Flags = Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoTitleBar };
        var noDecorationWin = new DummyWindow("OverlayBox") { IsOpen = true, Flags = Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoDecoration };
        var noInputsWin = new DummyWindow("SpearfishingHelper") { IsOpen = true, Flags = Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoInputs };
        var clickthroughWin = new DummyWindow("ClickthroughHUD") { IsOpen = true, IsClickthrough = true };

        service.RegisterWindow(normalWin);
        service.RegisterWindow(noTitleBarWin);
        service.RegisterWindow(noDecorationWin);
        service.RegisterWindow(noInputsWin);
        service.RegisterWindow(clickthroughWin);

        var visible = new List<TrackedWindow>();
        service.GetVisibleAndMinimizedWindows(visible);

        Assert.Single(visible);
        Assert.Equal("Normal Window", visible[0].WindowName);
    }

    [Fact]
    public void WindowManagerService_GetVisibleAndMinimizedWindows_RetainsWindowsWithNoCollapseNoResizeOrNoScrollbar()
    {
        var service = new WindowManagerService();
        var noCollapseWin = new DummyWindow("NoCollapse Window")
        {
            IsOpen = true,
            Flags = Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoCollapse
        };
        var noResizeWin = new DummyWindow("NoResize Window")
        {
            IsOpen = true,
            Flags = Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoResize
        };
        var noScrollbarWin = new DummyWindow("NoScrollbar Window")
        {
            IsOpen = true,
            Flags = Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoScrollbar
        };

        var twCollapse = service.RegisterWindow(noCollapseWin);
        var twResize = service.RegisterWindow(noResizeWin);
        var twScrollbar = service.RegisterWindow(noScrollbarWin);

        var visible = new List<TrackedWindow>();
        service.GetVisibleAndMinimizedWindows(visible);

        Assert.Equal(3, visible.Count);
        Assert.Contains(twCollapse, visible);
        Assert.Contains(twResize, visible);
        Assert.Contains(twScrollbar, visible);

        // When a window with NoCollapse is minimized, it must remain visible in GetVisibleAndMinimizedWindows
        service.Minimize(twCollapse);
        Assert.True(twCollapse.IsMinimized);
        Assert.False(twCollapse.IsOpen);

        visible.Clear();
        service.GetVisibleAndMinimizedWindows(visible);

        Assert.Equal(3, visible.Count);
        Assert.Contains(twCollapse, visible);
    }

    [Fact]
    public void WindowManagerService_PruneDeadWindows_RemovesGarbageCollectedWindowsFromDictionary()
    {
        var service = new WindowManagerService();

        Func<TrackedWindow> registerWindow = () =>
        {
            var win = new DummyWindow("Temporary Window to Prune");
            return service.RegisterWindow(win);
        };

        var tw = registerWindow();
        var buffer = new List<TrackedWindow>();
        service.GetTrackedWindows(buffer);
        Assert.Single(buffer);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(tw.TryGetWindow(out _));

        service.PruneDeadWindows();

        service.GetTrackedWindows(buffer);
        Assert.Empty(buffer);
    }

    [Fact]
    public void WindowManagerService_RegisterWindow_WhenInstanceUnchanged_ReturnsSameTrackedWindow()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Stable Window") { IsOpen = true };

        var tw1 = service.RegisterWindow(win);

        // Re-registering the exact same instance returns the same TrackedWindow.
        var tw2 = service.RegisterWindow(win);
        Assert.Same(tw1, tw2);

        // Registering a new instance with the same name replaces the tracked window.
        var winReplacement = new DummyWindow("Stable Window") { IsOpen = true };
        var tw3 = service.RegisterWindow(winReplacement);
        Assert.NotSame(tw1, tw3);
        Assert.True(tw3.TryGetWindow(out var retrieved));
        Assert.Same(winReplacement, retrieved);
    }

    private class DummyConditionalWindow : Window
    {
        public bool ConditionResult { get; set; } = true;

        public DummyConditionalWindow(string name) : base(name) { }

        public override bool DrawConditions() => this.ConditionResult;

        public override void Draw() { }
    }

    [Fact]
    public void WindowManagerService_GetVisibleAndMinimizedWindows_ExcludesWindowsFailingDrawConditions()
    {
        var service = new WindowManagerService();
        var win = new DummyConditionalWindow("Conditional Window")
        {
            IsOpen = true,
            ConditionResult = false
        };

        var tw = service.RegisterWindow(win);

        Assert.False(tw.PassesDrawConditions);
        Assert.False(tw.IsOpen);

        var visible = service.GetVisibleAndMinimizedWindows();
        Assert.DoesNotContain(tw, visible);

        // When condition becomes true, window becomes open and visible
        win.ConditionResult = true;
        Assert.True(tw.PassesDrawConditions);
        Assert.True(tw.IsOpen);

        visible = service.GetVisibleAndMinimizedWindows();
        Assert.Contains(tw, visible);
    }

    [Fact]
    public void WindowManagerService_GetVisibleAndMinimizedWindows_ExcludesZeroSizeWindows()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("ZeroSize Window")
        {
            IsOpen = true,
            Size = System.Numerics.Vector2.Zero
        };

        var tw = service.RegisterWindow(win);

        Assert.False(tw.IsManageable);

        var visible = service.GetVisibleAndMinimizedWindows();
        Assert.DoesNotContain(tw, visible);
    }

    [Fact]
    public void WindowManagerService_GetVisibleAndMinimizedWindows_ExcludesZeroMaxConstraintsWindows()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("ZeroConstraints Window")
        {
            IsOpen = true,
            SizeConstraints = new WindowSizeConstraints
            {
                MaximumSize = System.Numerics.Vector2.Zero
            }
        };

        var tw = service.RegisterWindow(win);

        Assert.False(tw.IsManageable);

        var visible = service.GetVisibleAndMinimizedWindows();
        Assert.DoesNotContain(tw, visible);
    }

    [Fact]
    public void WindowManagerService_GetVisibleAndMinimizedWindows_ExcludesUnconfirmedUiWindows()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Unconfirmed Window") { IsOpen = true };

        var tw = service.RegisterWindow(win);
        Assert.True(tw.HasConfirmedUi);
        Assert.True(tw.IsManageable);

        tw.HasConfirmedUi = false;
        Assert.False(tw.IsManageable);

        var visible = service.GetVisibleAndMinimizedWindows();
        Assert.DoesNotContain(tw, visible);
    }

    [Fact]
    public void WindowManagerService_GetVisibleAndMinimizedWindows_MinimizedWindowRetainsManageableEvenIfUiUnconfirmed()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Test Minimized Window") { IsOpen = true };

        var tw = service.RegisterWindow(win);
        Assert.True(tw.IsManageable);

        // Minimize window
        service.Minimize(tw);
        Assert.True(tw.IsMinimized);
        Assert.False(win.IsOpen);

        // Simulate ImGuiContextMonitor observing no active UI content during/after minimization fade-out
        tw.HasConfirmedUi = false;

        // Even with HasConfirmedUi == false, a minimized window must remain manageable and in visible/minimized list
        Assert.True(tw.IsManageable);

        var visible = service.GetVisibleAndMinimizedWindows();
        Assert.Contains(tw, visible);
    }

    [Fact]
    public void WindowManagerService_Restore_MaintainsManageableAndConfirmedUi()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Test Restore Window") { IsOpen = true };

        var tw = service.RegisterWindow(win);
        Assert.True(tw.IsManageable);

        service.Minimize(tw);
        Assert.True(tw.IsMinimized);

        service.Restore(tw);
        Assert.False(tw.IsMinimized);
        Assert.True(tw.IsOpen);
        Assert.True(tw.IsManageable);
        Assert.Contains(tw, service.GetVisibleAndMinimizedWindows());
    }
}

