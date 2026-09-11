using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dalamud.Interface.Windowing;
using Umbra.Common;

namespace Umbra.WindowManager.Services.WindowManager;

[Service]
public class WindowManagerService
{
    private readonly ConcurrentDictionary<string, TrackedWindow> windows = new();
    private readonly ConcurrentDictionary<string, DockGroup> dockGroups = new();
    private readonly HashSet<string> lastBulkMinimizedKeys = [];
    private DateTime lastPruneTime = DateTime.MinValue;

    internal IReadOnlySet<string> LastBulkMinimizedKeys => this.lastBulkMinimizedKeys;

    public bool AreAnyWindowsOpen
    {
        get
        {
            foreach (var (_, w) in this.windows)
            {
                if (w.TryGetWindow(out _) && w.IsManageable && w.IsOpen && !w.IsMinimized && !string.IsNullOrWhiteSpace(w.CleanTitle))
                    return true;
            }

            return false;
        }
    }

    public bool CanRestoreBulkMinimized => this.lastBulkMinimizedKeys.Count > 0 && !this.AreAnyWindowsOpen;

    public void GetTrackedWindows(List<TrackedWindow> destination)
    {
        destination.Clear();
        foreach (var (key, w) in this.windows)
        {
            if (w.TryGetWindow(out _))
                destination.Add(w);
            else
                this.windows.TryRemove(key, out _);
        }
    }

    public void GetVisibleAndMinimizedWindows(List<TrackedWindow> destination)
    {
        destination.Clear();
        foreach (var (key, w) in this.windows)
        {
            if (w.TryGetWindow(out _))
            {
                if (!w.IsManageable)
                    continue;

                var hasTitle = !string.IsNullOrWhiteSpace(w.CleanTitle) || !string.IsNullOrWhiteSpace(w.Id);
                if ((w.IsOpen && !string.IsNullOrWhiteSpace(w.CleanTitle)) || (w.IsMinimized && hasTitle))
                    destination.Add(w);
            }
            else
            {
                this.windows.TryRemove(key, out _);
            }
        }
    }

    public IReadOnlyList<TrackedWindow> GetTrackedWindows()
    {
        var destination = new List<TrackedWindow>();
        this.GetTrackedWindows(destination);
        return destination;
    }

    public IReadOnlyList<TrackedWindow> GetVisibleAndMinimizedWindows()
    {
        var destination = new List<TrackedWindow>();
        this.GetVisibleAndMinimizedWindows(destination);
        return destination;
    }

    public IReadOnlyList<TrackedWindow> GetActiveAndMinimizedWindows() => this.GetVisibleAndMinimizedWindows();
    public void GetActiveAndMinimizedWindows(List<TrackedWindow> destination) => this.GetVisibleAndMinimizedWindows(destination);

    public void PruneDeadWindows()
    {
        foreach (var (key, tw) in this.windows)
        {
            if (!tw.TryGetWindow(out _))
                this.windows.TryRemove(key, out _);
        }
    }

    public TrackedWindow RegisterWindow(IWindow window)
    {
        var now = DateTime.UtcNow;
        if (now - this.lastPruneTime > TimeSpan.FromSeconds(5))
        {
            this.lastPruneTime = now;
            this.PruneDeadWindows();
        }

        return this.windows.AddOrUpdate(
            window.WindowName,
            _ => new TrackedWindow(window),
            (_, existing) =>
                existing.TryGetWindow(out var alive) && ReferenceEquals(alive, window)
                    ? existing
                    : new TrackedWindow(window));
    }

    public void UnregisterWindow(IWindow window)
    {
        this.windows.TryRemove(window.WindowName, out _);
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
    }

    public void Toggle(TrackedWindow tracked, bool? wasFocused = null)
    {
        var isFocused = wasFocused ?? tracked.IsFocused;
        if (tracked.IsMinimized || !tracked.IsOpen)
        {
            this.Restore(tracked);
        }
        else if (isFocused)
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
    }

    /// <summary>
    /// Minimizes all currently open and manageable plugin windows at once.
    /// Retains a snapshot of windows minimized by this action to allow toggling restore.
    /// </summary>
    public void MinimizeAll()
    {
        this.lastBulkMinimizedKeys.Clear();
        var handledDockGroups = new HashSet<string>();

        var openWindows = new List<TrackedWindow>();
        foreach (var (_, w) in this.windows)
        {
            if (w.TryGetWindow(out _) && w.IsManageable && w.IsOpen && !w.IsMinimized && !string.IsNullOrWhiteSpace(w.CleanTitle))
            {
                openWindows.Add(w);
            }
        }

        foreach (var w in openWindows)
        {
            if (w.DockGroupKey != null && this.dockGroups.TryGetValue(w.DockGroupKey, out var group))
            {
                if (handledDockGroups.Add(w.DockGroupKey))
                {
                    group.Minimize();
                    foreach (var member in group.Members)
                    {
                        this.lastBulkMinimizedKeys.Add(member.WindowName);
                    }
                }
            }
            else
            {
                this.Minimize(w);
                this.lastBulkMinimizedKeys.Add(w.WindowName);
            }
        }
    }

    /// <summary>
    /// Restores previously bulk-minimized windows. If no bulk snapshot exists, restores all
    /// minimized manageable windows.
    /// </summary>
    public void RestoreAll()
    {
        var handledDockGroups = new HashSet<string>();

        if (this.lastBulkMinimizedKeys.Count > 0)
        {
            foreach (var key in this.lastBulkMinimizedKeys)
            {
                if (this.windows.TryGetValue(key, out var w) && w.TryGetWindow(out _))
                {
                    if (w.DockGroupKey != null && this.dockGroups.TryGetValue(w.DockGroupKey, out var group))
                    {
                        if (handledDockGroups.Add(w.DockGroupKey))
                        {
                            group.Restore();
                        }
                    }
                    else
                    {
                        this.Restore(w);
                    }
                }
            }

            this.lastBulkMinimizedKeys.Clear();
        }
        else
        {
            foreach (var (_, w) in this.windows)
            {
                if (w.TryGetWindow(out _) && w.IsManageable && w.IsMinimized)
                {
                    if (w.DockGroupKey != null && this.dockGroups.TryGetValue(w.DockGroupKey, out var group))
                    {
                        if (handledDockGroups.Add(w.DockGroupKey))
                        {
                            group.Restore();
                        }
                    }
                    else
                    {
                        this.Restore(w);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Closes every window belonging to the given dock group. Used by the toolbar's
    /// "Close All Tabs" context-menu action.
    /// </summary>
    public void CloseDockGroup(string groupKey)
    {
        if (!this.dockGroups.TryGetValue(groupKey, out var group)) return;

        foreach (var member in group.Members)
        {
            member.IsMinimized = false;
            member.IsOpen = false;
        }
    }

    /// <summary>
    /// Registers (or refreshes) a dock group. This is called from the per-frame draw loop, so it is
    /// idempotent: if a group with the same key, active tab, and exact member set already exists, no new
    /// <see cref="DockGroup"/> is allocated. This keeps the draw loop allocation-free while a dock group
    /// is on screen (see issue #6).
    /// </summary>
    public void RegisterDockGroup(string groupKey, string activeWindowName, IReadOnlyList<TrackedWindow> members)
    {
        if (this.dockGroups.TryGetValue(groupKey, out var existing)
            && existing.ActiveWindowName == activeWindowName
            && MembersEqual(existing.Members, members))
        {
            return;
        }

        this.dockGroups[groupKey] = new DockGroup(groupKey, activeWindowName, members);
    }

    /// <summary>
    /// Returns the currently registered dock group for the given key, or <c>null</c> if none is
    /// registered. Used by the toolbar to collapse a docked tab set into a single grouped button.
    /// </summary>
    public DockGroup? GetDockGroup(string groupKey) =>
        this.dockGroups.TryGetValue(groupKey, out var group) ? group : null;

    /// <summary>Test/diagnostic accessor for the currently registered dock group, if any.</summary>
    internal DockGroup? PeekDockGroup(string groupKey) =>
        this.dockGroups.TryGetValue(groupKey, out var group) ? group : null;

    public void RemoveDockGroup(string groupKey)
    {
        if (this.dockGroups.TryRemove(groupKey, out var group))
        {
            foreach (var member in group.Members)
            {
                if (member.DockGroupKey == groupKey)
                    member.DockGroupKey = null;
            }
        }
    }

    private static bool MembersEqual(IReadOnlyList<TrackedWindow> a, IReadOnlyList<TrackedWindow> b)
    {
        if (a.Count != b.Count) return false;

        for (var i = 0; i < a.Count; i++)
        {
            // Reference equality is intentional: the monitor emits members in a stable order, and a
            // re-instantiated window produces a fresh TrackedWindow instance that must force a refresh.
            if (!ReferenceEquals(a[i], b[i])) return false;
        }

        return true;
    }
}
