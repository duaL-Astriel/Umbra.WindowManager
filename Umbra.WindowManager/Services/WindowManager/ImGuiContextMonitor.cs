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
    private readonly Dictionary<string, TrackedWindow> trackedMap = new();
    private readonly Dictionary<uint, List<TrackedWindow>> dockGroups = new();
    private readonly Dictionary<uint, string> dockActiveTab = new();
    private readonly Dictionary<uint, string> dockKeyCache = new();
    private readonly List<List<TrackedWindow>> listPool = new();
    private readonly List<TrackedWindow> trackedBuffer = [];
    private readonly HashSet<string> seenWindows = [];

    public ImGuiContextMonitor(WindowManagerService windowManager)
    {
        this.windowManager = windowManager;
    }

    public static bool ValidateWindowDimensions(System.Numerics.Vector2 size) =>
        size.X > 0 && size.Y > 0;

    public static bool ValidateWindowContent(System.Numerics.Vector2 contentSize, int drawCmdCount) =>
        contentSize.X > 0 || contentSize.Y > 0 || drawCmdCount > 2;

    [OnDraw(executionOrder: 10)]
    public unsafe void OnDraw()
    {
        var ctx = ImGui.GetCurrentContext();
        if (ctx.IsNull) return;

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
            if (string.IsNullOrEmpty(name) || !this.trackedMap.TryGetValue(name, out var tracked))
                continue;

            this.seenWindows.Add(name);

            // Validate window presence: dimensions, content, and visibility
            var hasValidSize = ValidateWindowDimensions(win.Size);
            var hasContent = win.Appearing || ValidateWindowContent(win.ContentSize, win.DrawList.IsNull ? 0 : win.DrawList.CmdBuffer.Size);
            var isHidden = win.Hidden;

            if (hasValidSize && hasContent && !isHidden)
            {
                tracked.HasConfirmedUi = true;
                tracked.UnseenFrames = 0;
            }
            else if (!tracked.IsMinimized)
            {
                tracked.HasConfirmedUi = false;
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

            // 2. Dock node tracking
            if (win.DockIsActive && !win.DockNode.IsNull)
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
