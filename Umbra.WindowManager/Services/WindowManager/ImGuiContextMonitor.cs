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
    private readonly List<List<TrackedWindow>> listPool = new();

    public ImGuiContextMonitor(WindowManagerService windowManager)
    {
        this.windowManager = windowManager;
    }

    [OnDraw(executionOrder: 10)]
    public unsafe void OnDraw()
    {
        var ctx = ImGui.GetCurrentContext();
        if (ctx.IsNull) return;

        this.trackedMap.Clear();
        var trackedList = this.windowManager.GetTrackedWindows();
        foreach (var t in trackedList)
        {
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

            // 1. Native collapse guard: if collapsed natively, cancel it and fully minimize
            if (win.Collapsed)
            {
                win.Collapsed = false;
                this.windowManager.Minimize(tracked);
                continue;
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

        // Register multi-window dock groups
        foreach (var (dockId, members) in this.dockGroups)
        {
            if (members.Count > 1)
            {
                var activeName = this.dockActiveTab.GetValueOrDefault(dockId, members[0].WindowName);
                this.windowManager.RegisterDockGroup($"dock_{dockId}", activeName, members);
            }
        }
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
