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

    public IReadOnlyList<TrackedWindow> GetTrackedWindows()
    {
        return this.windows.Values
            .Where(w => w.TryGetWindow(out _))
            .ToList();
    }

    public IReadOnlyList<TrackedWindow> GetVisibleAndMinimizedWindows()
    {
        return this.windows.Values
            .Where(w => w.TryGetWindow(out _) && (w.IsOpen || w.IsMinimized) && !string.IsNullOrWhiteSpace(w.CleanTitle))
            .ToList();
    }

    public IReadOnlyList<TrackedWindow> GetActiveAndMinimizedWindows() => this.GetVisibleAndMinimizedWindows();

    public TrackedWindow RegisterWindow(IWindow window)
    {
        var tw = this.windows.AddOrUpdate(
            window.WindowName,
            _ => new TrackedWindow(window),
            (_, existing) => existing.TryGetWindow(out var alive) && ReferenceEquals(alive, window)
                ? existing
                : new TrackedWindow(window));
        this.OnWindowsChanged?.Invoke();
        return tw;
    }

    public void UnregisterWindow(IWindow window)
    {
        this.windows.TryRemove(window.WindowName, out _);
        this.OnWindowsChanged?.Invoke();
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

    public void RegisterDockGroup(string groupKey, string activeWindowName, IEnumerable<TrackedWindow> members)
    {
        var group = new DockGroup(groupKey, activeWindowName, members);
        this.dockGroups[groupKey] = group;
    }

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
}
