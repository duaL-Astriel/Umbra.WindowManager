// Umbra.WindowManager/Services/WindowManager/ImGuiContextMonitor.cs
using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Umbra.Common;

namespace Umbra.WindowManager.Services.WindowManager;

[Service]
public class ImGuiContextMonitor
{
    private readonly WindowManagerService windowManager;
    private readonly DalamudWindowTracker? windowTracker;
    private readonly Dictionary<string, TrackedWindow> trackedMap = new();
    private readonly Dictionary<uint, List<TrackedWindow>> dockGroups = new();
    private readonly Dictionary<uint, string> dockActiveTab = new();
    private readonly Dictionary<uint, string> dockKeyCache = new();
    private readonly List<List<TrackedWindow>> listPool = new();
    private readonly List<TrackedWindow> trackedBuffer = [];
    private readonly HashSet<string> seenWindows = [];
    private readonly HashSet<string> unmanagedWindowNames = [];
    private int unmanagedClearCounter;

    public ImGuiContextMonitor(WindowManagerService windowManager, DalamudWindowTracker? windowTracker = null)
    {
        this.windowManager = windowManager;
        this.windowTracker = windowTracker;
    }

    public static bool ValidateWindowDimensions(System.Numerics.Vector2 size) =>
        size.X > 0 && size.Y > 0;

    public static bool ValidateWindowContent(System.Numerics.Vector2 contentSize, int drawCmdCount) =>
        contentSize.X > 0 || contentSize.Y > 0 || drawCmdCount > 2;

    /// <summary>
    /// Whether a window is currently bound to a live ImGui dock node this frame. The Dalamud ImGui
    /// binding reports <c>ImGuiWindow.DockNode</c> as null from our <c>OnDraw</c> hook even for docked
    /// windows, and <c>DockIsActive</c> is false for inactive/background tabs, so neither is a usable
    /// signal. Instead we use the persistent <c>DockId</c> (a nonzero backup of the last node id, retained
    /// even after a window floats again) together with <c>DockNodeIsVisible</c> (true only while the
    /// window is actually docked into a visible node). Windows that are docked and share a <c>DockId</c>
    /// form a dock group (issue #25).
    /// </summary>
    public static bool IsWindowDocked(uint dockId, bool dockNodeVisible) =>
        dockId != 0 && dockNodeVisible;

    [OnDraw(executionOrder: 10)]
    public unsafe void OnDraw()
    {
        var ctx = ImGui.GetCurrentContext();
        if (ctx.IsNull) return;

        if (this.unmanagedClearCounter++ % 60 == 0)
        {
            this.unmanagedWindowNames.Clear();
        }

        this.seenWindows.Clear();
        this.trackedMap.Clear();
        this.windowManager.GetTrackedWindows(this.trackedBuffer);
        for (var i = 0; i < this.trackedBuffer.Count; i++)
        {
            var t = this.trackedBuffer[i];
            this.trackedMap[t.WindowName] = t;
        }

        foreach (var list in this.dockGroups.Values)
        {
            list.Clear();
            this.listPool.Add(list);
        }
        this.dockGroups.Clear();
        this.dockActiveTab.Clear();

        for (var i = 0; i < ctx.Windows.Size; i++)
        {
            var win = ctx.Windows[i];
            if (win.IsNull) continue;

            var name = win.Name != null ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)win.Name) : null;
            if (string.IsNullOrEmpty(name))
                continue;

            if (!this.trackedMap.TryGetValue(name, out var tracked))
            {
                if (this.unmanagedWindowNames.Contains(name))
                    continue;

                // Fast-path: check known window systems for dynamic windows opened between scan ticks
                tracked = this.windowTracker?.TryFastTrackWindow(name);
                if (tracked != null)
                {
                    this.trackedMap[name] = tracked;
                }
                else
                {
                    this.unmanagedWindowNames.Add(name);
                    continue;
                }
            }

            this.seenWindows.Add(name);

            // Validate window presence: dimensions, content, and visibility
            var hasValidSize = ValidateWindowDimensions(win.Size);
            var hasContent = win.Appearing || ValidateWindowContent(win.ContentSize, win.DrawList.IsNull ? 0 : win.DrawList.CmdBuffer.Size);
            var isHidden = win.Hidden;

            if (hasValidSize && hasContent && !isHidden)
            {
                tracked.HasConfirmedUi = true;
                tracked.UnseenFrames = 0;

                // Reconcile a stale "minimized" flag. Some plugins (e.g. Glamourer/Penumbra) own their
                // window visibility and can reopen a window we previously minimized, without going through
                // our Restore path. When that happens the window is genuinely drawn AND its own IsOpen flag
                // is true again, yet tracked.IsMinimized is still set -- leaving the taskbar button greyed
                // over a visible window and making the next click toggle the wrong way. Detect that here and
                // clear the flag. We require IsUnderlyingOpen (the window's own IsOpen == true) so the single
                // transitional frame right after *our* minimize (where IsOpen is already false) never trips
                // this, and we never fight our own minimize action.
                if (tracked.IsMinimized && tracked.IsUnderlyingOpen)
                {
                    tracked.IsMinimized = false;
                }
            }
            else if (!tracked.IsMinimized)
            {
                tracked.HasConfirmedUi = false;
            }

            // Whether this window is currently docked into a (visible) node this frame. See IsWindowDocked:
            // the DockNode pointer reads as null here, so we rely on DockId + DockNodeIsVisible (#25).
            var isDocked = IsWindowDocked(win.DockId, win.DockNodeIsVisible);

            // Continuous injection check: keep the minimize button in sync with dock state. The shared
            // InjectMinimizeButton routine suppresses the button whenever DockGroupKey is set (populated
            // below once full node membership is known), so dock-group tabs -- where Dalamud draws the
            // button inside the client area, colliding with plugin controls (issue #25) -- lose it, while
            // floating/standalone windows keep it re-injected in case a plugin cleared buttons.
            if (tracked.TryGetWindow(out var dalamudWindow))
            {
                DalamudWindowTracker.InjectMinimizeButton(dalamudWindow, tracked, this.windowManager);
            }

            // 1. Native collapse guard: if collapsed natively, cancel it and fully minimize
            if (win.Collapsed)
            {
                win.Collapsed = false;
                this.windowManager.Minimize(tracked);
                continue;
            }

            // 1b. Title bar double click: minimize window cleanly to toolbar
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) &&
                (win.Flags & ImGuiWindowFlags.NoTitleBar) == 0 &&
                ctx.HoveredWindow == win)
            {
                var mousePos = ImGui.GetMousePos();
                var titleBarHeight = ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.Y * 2.0f;
                if (mousePos.X >= win.Pos.X && mousePos.X <= win.Pos.X + win.Size.X &&
                    mousePos.Y >= win.Pos.Y && mousePos.Y <= win.Pos.Y + titleBarHeight)
                {
                    this.windowManager.Minimize(tracked);
                    continue;
                }
            }

            // 2. Dock node tracking. Windows are grouped by their shared DockId (the DockNode pointer is
            // null from here). Background tabs -- whose DockIsActive is false but which are still docked
            // and node-visible -- are collected too, so the group keeps all its members (issue #25).
            if (isDocked)
            {
                var dockId = win.DockId;
                if (!this.dockGroups.TryGetValue(dockId, out var groupMembers))
                {
                    groupMembers = this.GetPooledList();
                    this.dockGroups[dockId] = groupMembers;
                }
                groupMembers.Add(tracked);

                if (win.DockTabIsVisible)
                {
                    this.dockActiveTab[dockId] = name;
                }
            }
            else if (tracked.DockGroupKey != null)
            {
                tracked.DockGroupKey = null;
            }
        }

        // Register multi-window dock groups. RegisterDockGroup is idempotent, and the dock key string is
        // cached per dock id, so a stable dock group produces zero draw-loop allocations (see issue #6).
        foreach (var (dockId, members) in this.dockGroups)
        {
            if (members.Count > 1)
            {
                var activeName = this.dockActiveTab.GetValueOrDefault(dockId, members[0].WindowName);
                this.windowManager.RegisterDockGroup(this.GetDockKey(dockId), activeName, members);
            }
            else if (members.Count == 1 && members[0].DockGroupKey != null)
            {
                this.windowManager.RemoveDockGroup(this.GetDockKey(dockId));
                members[0].DockGroupKey = null;
            }
        }

        // For open non-minimized windows not observed in ctx.Windows, count missing frames
        for (var i = 0; i < this.trackedBuffer.Count; i++)
        {
            var t = this.trackedBuffer[i];
            if (this.seenWindows.Contains(t.WindowName))
                continue;

            if (t.IsOpen && !t.IsMinimized)
            {
                t.UnseenFrames++;
                if (t.UnseenFrames > 5)
                {
                    t.HasConfirmedUi = false;
                }
            }
            else
            {
                t.UnseenFrames = 0;
            }
        }
    }


    private string GetDockKey(uint dockId)
    {
        if (!this.dockKeyCache.TryGetValue(dockId, out var key))
        {
            key = $"dock_{dockId}";
            this.dockKeyCache[dockId] = key;
        }

        return key;
    }

    private List<TrackedWindow> GetPooledList()
    {
        if (this.listPool.Count > 0)
        {
            var lastIndex = this.listPool.Count - 1;
            var list = this.listPool[lastIndex];
            this.listPool.RemoveAt(lastIndex);
            return list;
        }

        return new List<TrackedWindow>();
    }
}
