using System;
using System.Threading;
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
    private readonly WindowFrameClock frameClock = new();
    private WindowSnapshot snapshot = new(-1, []);
    private int windowSetGeneration;

    private sealed record WindowSnapshot(int Generation, KeyValuePair<string, TrackedWindow>[] Entries);

    /// <summary>
    /// Whether raw-ImGui window tracking is enabled. Written by the widget (which owns the user-facing
    /// toggle) on the framework update thread and read by <see cref="ImGuiContextMonitor"/> in the draw
    /// loop; both run on the framework thread, so no synchronization is required. Defaults off (issue #38).
    /// </summary>
    public bool RawTrackingEnabled { get; set; }

    internal IReadOnlySet<string> LastBulkMinimizedKeys => this.lastBulkMinimizedKeys;

    public bool AreAnyWindowsOpen
    {
        get
        {
            foreach (var (_, w) in this.GetWindowSnapshot())
            {
                if (w.IsAlive && w.IsManageable && w.IsOpen && !w.IsMinimized && !string.IsNullOrWhiteSpace(w.CleanTitle))
                    return true;
            }

            return false;
        }
    }

    public bool CanRestoreBulkMinimized => this.lastBulkMinimizedKeys.Count > 0 && !this.AreAnyWindowsOpen;

    /// <summary>
    /// Reads <see cref="AreAnyWindowsOpen"/> and <see cref="CanRestoreBulkMinimized"/> together, scanning the
    /// windows once. Callers that need both (the Minimize All widget) would otherwise scan twice (issue #64).
    /// </summary>
    public void GetBulkMinimizeState(out bool anyWindowsOpen, out bool canRestoreBulkMinimized)
    {
        anyWindowsOpen = this.AreAnyWindowsOpen;
        canRestoreBulkMinimized = this.lastBulkMinimizedKeys.Count > 0 && !anyWindowsOpen;
    }

    /// <summary>
    /// Marks the start of a draw frame. Per-window state (<see cref="TrackedWindow.IsOpen"/> and friends) is
    /// cached until the next call, so each window's plugin <c>DrawConditions()</c> runs at most once per frame.
    /// Called by <see cref="ImGuiContextMonitor"/> at the top of its draw hook.
    /// </summary>
    internal void BeginFrame() => this.frameClock.Advance();

    public void GetTrackedWindows(List<TrackedWindow> destination)
    {
        destination.Clear();
        foreach (var entry in this.GetWindowSnapshot())
        {
            if (entry.Value.IsAlive)
                destination.Add(entry.Value);
            else
                this.TryRemoveEntry(entry);
        }
    }

    public void GetVisibleAndMinimizedWindows(List<TrackedWindow> destination)
    {
        destination.Clear();
        foreach (var entry in this.GetWindowSnapshot())
        {
            var w = entry.Value;
            if (w.IsAlive)
            {
                if (!w.IsManageable)
                    continue;

                var hasTitle = !string.IsNullOrWhiteSpace(w.CleanTitle) || !string.IsNullOrWhiteSpace(w.Id);
                if ((w.IsOpen && !string.IsNullOrWhiteSpace(w.CleanTitle)) || (w.IsMinimized && hasTitle))
                    destination.Add(w);
            }
            else
            {
                this.TryRemoveEntry(entry);
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
        foreach (var entry in this.GetWindowSnapshot())
        {
            if (!entry.Value.IsAlive)
                this.TryRemoveEntry(entry);
        }
    }

    /// <summary>
    /// Bumped on every change to the set of tracked windows: an addition, a replacement, or any removal.
    /// Unlike <see cref="WindowRegistrationGeneration"/> (additions only), this is what invalidates the
    /// window snapshot, so the draw loop and widgets iterate a plain array instead of the
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> and the array is rebuilt only when this moves (issue #64).
    /// </summary>
    internal int WindowSetGeneration => Volatile.Read(ref this.windowSetGeneration);

    /// <summary>
    /// The current windows as an immutable array, rebuilt only when <see cref="WindowSetGeneration"/> has
    /// changed. May include dead windows until a caller prunes them; check <see cref="TrackedWindow.IsAlive"/>.
    /// </summary>
    private KeyValuePair<string, TrackedWindow>[] GetWindowSnapshot()
    {
        var current = Volatile.Read(ref this.snapshot);
        var generation = this.WindowSetGeneration;
        if (current.Generation != generation)
        {
            current = new WindowSnapshot(generation, this.windows.ToArray());
            Volatile.Write(ref this.snapshot, current);
        }

        return current.Entries;
    }

    private TrackedWindow Adopt(TrackedWindow tracked)
    {
        tracked.FrameClock = this.frameClock;
        Interlocked.Increment(ref this.windowSetGeneration);
        return tracked;
    }

    // Removes only if the key still maps to this exact instance, so a window re-registered under the same
    // name since the snapshot was taken is left alone.
    private bool TryRemoveEntry(KeyValuePair<string, TrackedWindow> entry)
    {
        if (!this.windows.TryRemove(entry))
            return false;

        Interlocked.Increment(ref this.windowSetGeneration);
        return true;
    }

    private bool TryRemoveKey(string key)
    {
        if (!this.windows.TryRemove(key, out _))
            return false;

        Interlocked.Increment(ref this.windowSetGeneration);
        return true;
    }

    private int windowRegistrationGeneration;

    /// <summary>
    /// Bumped every time a window is registered under a name that was not already tracked (or is re-registered
    /// with a different <see cref="IWindow"/> instance). Observers use it to tell "the set of manageable windows
    /// actually changed" from "nothing happened", so caches keyed on manageability can be invalidated on the
    /// change itself instead of being flushed on a timer (issue #51). Additions only: dropping a window can
    /// never turn a previously unmanageable window name into a manageable one.
    /// </summary>
    public int WindowRegistrationGeneration => Volatile.Read(ref this.windowRegistrationGeneration);

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
            _ =>
            {
                Interlocked.Increment(ref this.windowRegistrationGeneration);
                return this.Adopt(new TrackedWindow(window));
            },
            (_, existing) =>
            {
                if (existing.TryGetWindow(out var alive) && ReferenceEquals(alive, window))
                    return existing;

                Interlocked.Increment(ref this.windowRegistrationGeneration);
                return this.Adopt(new TrackedWindow(window));
            });
    }

    /// <summary>
    /// Registers (or returns the existing) tracked entry for a raw-ImGui window identified only by name.
    /// Stored in the same dictionary as <see cref="IWindow"/>-backed windows; if the name is currently held
    /// by a non-raw entry (e.g. a window that later gained an <see cref="IWindow"/>), it is replaced.
    /// </summary>
    public TrackedWindow RegisterImGuiWindow(string windowName)
    {
        return this.windows.AddOrUpdate(
            windowName,
            _ => this.Adopt(new ImGuiTrackedWindow(windowName)),
            (_, existing) => existing is ImGuiTrackedWindow ? existing : this.Adopt(new ImGuiTrackedWindow(windowName)));
    }

    public void UnregisterWindow(IWindow window)
    {
        this.TryRemoveKey(window.WindowName);
    }

    public void UnregisterWindowsForPlugin(string pluginInternalName)
    {
        if (string.IsNullOrWhiteSpace(pluginInternalName))
            return;

        var affectedDockKeys = new HashSet<string>();
        foreach (var (key, tw) in this.windows)
        {
            if (string.Equals(tw.PluginInternalName, pluginInternalName, StringComparison.OrdinalIgnoreCase))
            {
                if (tw.DockGroupKey != null)
                    affectedDockKeys.Add(tw.DockGroupKey);

                this.TryRemoveKey(key);
            }
        }

        foreach (var dockKey in affectedDockKeys)
        {
            if (this.dockGroups.TryGetValue(dockKey, out var group))
            {
                var hasLiveMembers = false;
                foreach (var member in group.Members)
                {
                    if (this.windows.ContainsKey(member.WindowName))
                    {
                        hasLiveMembers = true;
                        break;
                    }
                }

                if (!hasLiveMembers)
                {
                    this.RemoveDockGroup(dockKey);
                }
            }
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
