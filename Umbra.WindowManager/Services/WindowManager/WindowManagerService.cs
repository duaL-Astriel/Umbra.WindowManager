using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Windowing;
using Umbra.Common;

namespace Umbra.WindowManager.Services.WindowManager;

[Service]
public class WindowManagerService
{
    private readonly ConcurrentDictionary<string, TrackedWindow> windows = new();
    private readonly ConcurrentDictionary<string, DockGroup> dockGroups = new();

    public event Action? OnWindowsChanged;

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
                if ((w.IsOpen || w.IsMinimized) && !string.IsNullOrWhiteSpace(w.CleanTitle))
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
        this.PruneDeadWindows();

        var changed = false;
        var tw = this.windows.AddOrUpdate(
            window.WindowName,
            _ =>
            {
                changed = true;
                return new TrackedWindow(window);
            },
            (_, existing) =>
            {
                if (existing.TryGetWindow(out var alive) && ReferenceEquals(alive, window))
                {
                    return existing;
                }

                changed = true;
                return new TrackedWindow(window);
            });

        if (changed)
        {
            this.OnWindowsChanged?.Invoke();
        }

        return tw;
    }

    public void UnregisterWindow(IWindow window)
    {
        if (this.windows.TryRemove(window.WindowName, out _))
        {
            this.OnWindowsChanged?.Invoke();
        }
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

        this.OnWindowsChanged?.Invoke();
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

        this.OnWindowsChanged?.Invoke();
    }

    public void Toggle(TrackedWindow tracked)
    {
        if (tracked.IsMinimized || !tracked.IsOpen)
        {
            this.Restore(tracked);
        }
        else if (tracked.IsFocused)
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
        this.OnWindowsChanged?.Invoke();
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
