using System;
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

    [Fact]
    public void TrackedWindow_Properties_InitializedCorrectly()
    {
        var w = new DummyWindow("My Tool##UniqueId") { Namespace = "TestPlugin", IsOpen = true };
        var tw = new TrackedWindow(w);

        Assert.Equal("My Tool##UniqueId", tw.WindowName);
        Assert.Equal("My Tool", tw.CleanTitle);
        Assert.Equal("UniqueId", tw.Id);
        Assert.Equal("TestPlugin", tw.Namespace);
        Assert.True(tw.IsOpen);
        Assert.False(tw.IsMinimized);
        Assert.Null(tw.DockGroupKey);
        Assert.True(tw.TryGetWindow(out var retrieved));
        Assert.Same(w, retrieved);
    }

    [Fact]
    public void TrackedWindow_NullNamespace_DefaultsToEmptyString()
    {
        var w = new DummyWindow("NoNamespace");
        w.Namespace = null!;
        var tw = new TrackedWindow(w);

        Assert.Equal(string.Empty, tw.Namespace);
    }

    [Fact]
    public void TrackedWindow_IsOpen_UpdatesUnderlyingWindow()
    {
        var w = new DummyWindow("Window 1") { IsOpen = false };
        var tw = new TrackedWindow(w);

        tw.IsOpen = true;
        Assert.True(w.IsOpen);

        tw.IsOpen = false;
        Assert.False(w.IsOpen);
    }

    [Fact]
    public void TrackedWindow_BringToFront_SetsRequestFocus()
    {
        var w = new DummyWindow("Window 1") { RequestFocus = false };
        var tw = new TrackedWindow(w);

        tw.BringToFront();
        Assert.True(w.RequestFocus);
    }

    [Fact]
    public void TrackedWindow_WhenWindowCollected_TryGetWindowReturnsFalse()
    {
        TrackedWindow tw;
        Func<TrackedWindow> factory = () =>
        {
            var win = new DummyWindow("ShortLived");
            return new TrackedWindow(win);
        };

        tw = factory();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(tw.TryGetWindow(out _));
        Assert.False(tw.IsOpen);
        Assert.False(tw.IsFocused);
        // Setting IsOpen or BringToFront should not throw when collected
        tw.IsOpen = true;
        tw.BringToFront();
    }

    [Fact]
    public void DockGroup_Constructor_SetsDockGroupKeyOnMembers()
    {
        var w1 = new DummyWindow("Tab 1");
        var tw1 = new TrackedWindow(w1);
        var group = new DockGroup("dock_abc", "Tab 1", new[] { tw1 });

        Assert.Equal("dock_abc", group.GroupKey);
        Assert.Equal("Tab 1", group.ActiveWindowName);
        Assert.Equal("dock_abc", tw1.DockGroupKey);
        Assert.Single(group.Members);
    }

    [Fact]
    public void DockGroup_Restore_FallbackToFirstMemberIfActiveNotFound()
    {
        var w1 = new DummyWindow("Tab 1") { IsOpen = false, RequestFocus = false };
        var tw1 = new TrackedWindow(w1);
        var group = new DockGroup("dock_abc", "NonExistentTab", new[] { tw1 });

        group.Restore();

        Assert.True(w1.IsOpen);
        Assert.True(w1.RequestFocus);
    }

    [Fact]
    public void DockGroup_EmptyMembers_MinimizeAndRestoreDoNotThrow()
    {
        var group = new DockGroup("dock_empty", "None", Array.Empty<TrackedWindow>());

        group.Minimize();
        Assert.True(group.IsMinimized);

        group.Restore();
        Assert.False(group.IsMinimized);
    }
}
